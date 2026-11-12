using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// 版本清单的获取与缓存。
/// 清单地址**必须**用 <c>piston-meta…/version_manifest_v2.json</c>，
/// 每个版本的 JSON 地址**必须**取自清单条目的 <c>url</c>，不能自己拼接。
/// </summary>
public sealed class VersionManifestService
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    private readonly HttpDownloader _downloader;
    private readonly DownloadSourceResolver _resolver;
    private readonly ILogger _log;
    private readonly string _cacheFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VersionManifest? _manifest;

    public VersionManifestService(
        HttpDownloader downloader,
        DownloadSourceResolver resolver,
        ILogger log,
        string cacheDirectory)
    {
        _downloader = downloader;
        _resolver = resolver;
        _log = log;
        _cacheFile = Path.Combine(cacheDirectory, "version_manifest_v2.json");
    }

    /// <summary>内存中已有的清单（可能为 null）。</summary>
    public VersionManifest? Cached => _manifest;

    /// <summary>取清单：内存 → 磁盘缓存（30 分钟内有效）→ 网络。</summary>
    public async Task<VersionManifest> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _manifest is not null)
            {
                return _manifest;
            }

            if (!forceRefresh && TryReadDiskCache(allowStale: false, out var cached))
            {
                _manifest = cached;
                return cached;
            }

            try
            {
                var text = await _downloader
                    .GetStringAsync(_resolver.Resolve(ManifestUrl, DownloadUrlKind.Metadata), ct)
                    .ConfigureAwait(false);

                var manifest = JsonUtil.Deserialize<VersionManifest>(text)
                               ?? throw new InvalidDataException("版本清单反序列化结果为空");

                if (manifest.Versions.Count == 0)
                {
                    throw new InvalidDataException("版本清单里没有任何版本条目");
                }

                _manifest = manifest;
                WriteDiskCache(text);
                _log.Info($"已获取版本清单：共 {manifest.Versions.Count} 个版本，最新正式版 {manifest.Latest.Release}");
                return manifest;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 网络挂了也要能用：退回过期缓存，总比报错什么都做不了强。
                if (TryReadDiskCache(allowStale: true, out var stale))
                {
                    _log.Warn("版本清单更新失败，已退回本地缓存", ex);
                    _manifest = stale;
                    return stale;
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按 id 查找条目；清单里没有时强制刷新一次再找。</summary>
    public async Task<VersionManifestEntry?> FindAsync(string id, CancellationToken ct = default)
    {
        var manifest = await GetAsync(ct: ct).ConfigureAwait(false);
        var entry = Find(manifest, id);
        if (entry is not null)
        {
            return entry;
        }

        _log.Info($"清单里没有 {id}，强制刷新后重试");
        manifest = await GetAsync(forceRefresh: true, ct).ConfigureAwait(false);
        return Find(manifest, id);
    }

    /// <summary>下载该版本的 JSON 文本（校验 SHA-1）。</summary>
    public async Task<string> GetVersionJsonTextAsync(VersionManifestEntry entry, CancellationToken ct = default)
    {
        if (entry.Url is null)
        {
            throw new InvalidOperationException($"版本 {entry.Id} 在清单里没有 url");
        }

        var text = await _downloader
            .GetStringAsync(_resolver.Resolve(entry.Url, DownloadUrlKind.Metadata), ct)
            .ConfigureAwait(false);

        // 清单条目自带该 JSON 的 SHA-1；不一致说明内容被镜像或中间人改过，不能拿去安装。
        if (string.IsNullOrWhiteSpace(entry.Sha1))
        {
            throw new InvalidDataException($"版本 {entry.Id} 在清单里没有 SHA-1，无法校验 JSON");
        }

        var actual = HashUtil.Sha1OfString(text);
        if (!string.Equals(actual, entry.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"版本 {entry.Id} 的 JSON 校验失败（期望 {entry.Sha1}，实际 {actual}）");
        }

        return text;
    }

    /// <summary>下载并解析该版本的 JSON。</summary>
    public async Task<GameVersionJson> GetVersionJsonAsync(VersionManifestEntry entry, CancellationToken ct = default)
    {
        var text = await GetVersionJsonTextAsync(entry, ct).ConfigureAwait(false);
        var json = JsonUtil.Deserialize<GameVersionJson>(text)
                   ?? throw new InvalidDataException($"版本 {entry.Id} 的 JSON 解析失败");

        json.Id ??= entry.Id;
        return json;
    }

    public void Invalidate() => _manifest = null;

    private static VersionManifestEntry? Find(VersionManifest manifest, string id) =>
        manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <param name="allowStale">true 表示允许使用过期缓存（仅在网络失败时的兜底路径上使用）。</param>
    private bool TryReadDiskCache(bool allowStale, out VersionManifest manifest)
    {
        manifest = new VersionManifest();

        try
        {
            if (!File.Exists(_cacheFile))
            {
                return false;
            }

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFile);
            if (!allowStale && age > CacheLifetime)
            {
                _log.Trace($"版本清单缓存已过期（{age.TotalMinutes:0} 分钟），将重新获取");
                return false;
            }

            var cached = JsonUtil.DeserializeFromFile<VersionManifest>(_cacheFile);
            if (cached is null || cached.Versions.Count == 0)
            {
                return false;
            }

            manifest = cached;
            _log.Trace($"使用本地缓存的版本清单（{age.TotalMinutes:0} 分钟前）：{_cacheFile}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"版本清单缓存不可用：{_cacheFile}", ex);
            return false;
        }
    }

    private void WriteDiskCache(string text)
    {
        try
        {
            PathUtil.EnsureParentDirectory(_cacheFile);
            File.WriteAllText(_cacheFile, text, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _log.Warn($"版本清单缓存写入失败：{_cacheFile}", ex);
        }
    }
}
