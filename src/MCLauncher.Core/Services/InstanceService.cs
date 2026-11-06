using System.Text.Json;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// 实例（版本）管理：版本隔离、游戏目录、实例的创建/重命名/删除。
/// <para>
/// 与 HMCL 一样不依赖任何按实例的额外文件：版本 JSON 与 jar 是唯一的事实来源，
/// 实例级覆盖项集中保存在启动器全局设置（<see cref="AppSettings.InstanceOverrides"/>），
/// 原版 JSON 缺失时按需从下载源获取，继承合并只在内存中进行。
/// </para>
/// </summary>
public sealed class InstanceService
{
    private readonly VersionRepository _repository;
    private readonly ConfigService _config;
    private readonly VersionManifestService _manifests;
    private readonly ILogger _log;

    public InstanceService(
        VersionRepository repository,
        ConfigService config,
        VersionManifestService manifests,
        ILogger log)
    {
        _repository = repository;
        _config = config;
        _manifests = manifests;
        _log = log;
    }

    private Dictionary<string, InstanceConfig> Overrides => _config.Settings.InstanceOverrides;

    private string? FindKey(string versionId) =>
        Overrides.Keys.FirstOrDefault(key =>
            string.Equals(key, versionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>读取实例覆盖项；没有条目时返回一个默认值（不落盘）。</summary>
    public InstanceConfig Load(string gameDirectory, string versionId)
    {
        var key = FindKey(versionId);
        return key is null ? new InstanceConfig() : Overrides[key];
    }

    /// <summary>保存实例覆盖项；没有任何覆盖项时从全局设置里移除该条目。</summary>
    public void Save(string gameDirectory, string versionId, InstanceConfig config)
    {
        var key = FindKey(versionId);

        if (config.IsEmpty)
        {
            if (key is not null)
            {
                Overrides.Remove(key);
                _config.Save();
            }

            return;
        }

        Overrides[key ?? versionId] = config;
        _config.Save();
    }

    /// <summary>该实例是否启用隔离；未显式设置时跟随全局默认。</summary>
    public bool IsIsolated(string gameDirectory, string versionId, bool globalDefault) =>
        Load(gameDirectory, versionId).Isolated ?? globalDefault;

    public void SetIsolation(string gameDirectory, string versionId, bool? isolated)
    {
        var config = Load(gameDirectory, versionId);
        config.Isolated = isolated;
        Save(gameDirectory, versionId, config);
        _log.Info($"实例 {versionId} 的版本隔离设为：{(isolated is null ? "跟随全局" : isolated.Value ? "开" : "关")}");
    }

    /// <summary>实例的游戏目录：隔离时为 <c>versions/&lt;id&gt;</c>，否则为游戏根目录。</summary>
    public string GetGameDirectory(string gameDirectory, string versionId, bool globalDefault)
    {
        var directory = IsIsolated(gameDirectory, versionId, globalDefault)
            ? VersionRepository.VersionDirectory(gameDirectory, versionId)
            : gameDirectory;

        PathUtil.EnsureDirectory(directory);
        return directory;
    }

    /// <summary>删除实例（含隔离目录与全局设置里的覆盖项）。</summary>
    public bool Delete(string gameDirectory, string versionId)
    {
        var key = FindKey(versionId);
        if (key is not null)
        {
            Overrides.Remove(key);
            _config.Save();
        }

        return _repository.Delete(gameDirectory, versionId);
    }

    /// <summary>
    /// 从客户端 jar 内嵌的 <c>version.json</c> 读取它对应的游戏版本号。
    /// 从别处导入的实例没有 <c>inheritsFrom</c>，只能靠客户端自带的这份元数据判断原版版本。
    /// </summary>
    public string? ReadJarGameVersion(string gameDirectory, string jarId)
    {
        var jarPath = VersionRepository.VersionJarPath(gameDirectory, jarId);
        if (!File.Exists(jarPath))
        {
            return null;
        }

        try
        {
            var text = ArchiveUtil.ReadTextEntry(jarPath, "version.json");
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            using var document = JsonDocument.Parse(text);
            var id = document.RootElement.TryGetProperty("id", out var value) ? value.GetString() : null;
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
        catch (Exception ex)
        {
            _log.Warn($"读取客户端 jar 的游戏版本失败：{jarPath}", ex);
            return null;
        }
    }

    /// <summary>
    /// 列出界面上应当展示为实例的版本 id。
    /// 只被其他版本继承、自己不携带完整版本 JSON 的原版基底不算独立实例：
    /// 例如「26.3 + Fabric」只会显示 Fabric 实例，而不会再多出一条 26.3。
    /// </summary>
    public IReadOnlyList<string> ListVisibleInstanceIds(string gameDirectory)
    {
        var ids = _repository.ListInstalledIds(gameDirectory);
        if (ids.Count == 0)
        {
            return ids;
        }

        var inherited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var version = _repository.TryLoad(gameDirectory, id);
            if (!string.IsNullOrWhiteSpace(version?.InheritsFrom))
            {
                inherited.Add(version!.InheritsFrom!);
            }
        }

        var result = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            if (inherited.Contains(id))
            {
                continue;
            }

            result.Add(id);
        }

        return result;
    }

    // ------------------------------------------------------------------ 创建实例

    /// <summary>
    /// 把用户输入的实例名规整成合法且不冲突的目录名：替换非法字符，冲突时追加 (2)、(3)。
    /// </summary>
    public string CreateUniqueInstanceId(string gameDirectory, string? desiredName, string fallbackName)
    {
        var name = SanitizeInstanceName(desiredName, fallbackName);

        var candidate = name;
        var suffix = 2;
        while (Directory.Exists(VersionRepository.VersionDirectory(gameDirectory, candidate)))
        {
            candidate = $"{name} ({suffix++})";
        }

        return candidate;
    }

    /// <summary>
    /// 重命名实例：同步重命名 versions 下的目录、版本 JSON 与客户端 jar，
    /// 并修正自洽实例的 jar 字段、迁移全局设置里的覆盖项。
    /// 返回最终使用的实例 id（非法字符会被替换、重名会追加序号）。
    /// </summary>
    public string RenameInstance(string gameDirectory, string instanceId, string desiredName)
    {
        if (string.IsNullOrWhiteSpace(desiredName))
        {
            return instanceId;
        }

        var name = SanitizeInstanceName(desiredName, instanceId);
        if (string.Equals(name, instanceId, StringComparison.OrdinalIgnoreCase))
        {
            return instanceId;
        }

        var newId = name;
        var suffix = 2;
        while (Directory.Exists(VersionRepository.VersionDirectory(gameDirectory, newId)))
        {
            newId = $"{name} ({suffix++})";
        }

        _repository.Rename(gameDirectory, instanceId, newId);

        var version = _repository.TryLoad(gameDirectory, newId)
                      ?? throw new InvalidOperationException($"重命名后的版本 {newId} 不存在");

        // 自洽实例的 jar 字段指向旧 id，需要跟着改名。
        if (string.Equals(version.Jar, instanceId, StringComparison.OrdinalIgnoreCase))
        {
            version.Jar = newId;
            _repository.Save(gameDirectory, version);
        }

        var oldKey = FindKey(instanceId);
        if (oldKey is not null)
        {
            var overrides = Overrides[oldKey];
            Overrides.Remove(oldKey);
            Overrides[newId] = overrides;
            _config.Save();
        }

        _log.Info($"实例已重命名：{instanceId} → {newId}");
        return newId;
    }

    private static string SanitizeInstanceName(string? desiredName, string fallbackName)
    {
        var name = string.IsNullOrWhiteSpace(desiredName) ? fallbackName : desiredName.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);

        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        name = builder.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            name = fallbackName;
        }

        return name;
    }

    /// <summary>
    /// 卸载实例上的加载器：基底还在时还原为继承原版；基底目录已被清理时，
    /// 从下载源获取原版版本 JSON 后写成自包含原版实例。mods / 存档 / 配置保持不变。
    /// </summary>
    public async Task<bool> RemoveLoaderAsync(
        string gameDirectory,
        string versionId,
        string? baseVersionId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseVersionId)
            || string.Equals(baseVersionId, versionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var version = _repository.TryLoad(gameDirectory, versionId);
        if (version is null)
        {
            return false;
        }

        var baseVersion = _repository.TryLoad(gameDirectory, baseVersionId);
        if (baseVersion is not null)
        {
            _repository.Save(gameDirectory, new GameVersionJson
            {
                Id = versionId,
                InheritsFrom = baseVersionId,
                Jar = baseVersionId,
                Type = version.Type ?? "release",
            });
        }
        else
        {
            // 基底目录没有保留：和安装实例一样从版本清单取原版 JSON，只在内存里使用，不再落盘快照。
            progress?.Report(new InstallProgress
            {
                Stage = $"正在获取原版 {baseVersionId} 的版本信息",
                Completed = 0,
                Total = 1,
            });

            var entry = await _manifests.FindAsync(baseVersionId, ct).ConfigureAwait(false);
            if (entry is null)
            {
                _log.Warn($"版本清单里找不到原版 {baseVersionId}，无法还原实例 {versionId}");
                return false;
            }

            var vanilla = await _manifests.GetVersionJsonAsync(entry, ct).ConfigureAwait(false);
            vanilla.Id = versionId;
            vanilla.InheritsFrom = null;
            vanilla.Jar = versionId;
            _repository.Save(gameDirectory, vanilla);
        }

        _log.Info($"已卸载实例 {versionId} 的加载器，恢复为原版 {baseVersionId}");
        return true;
    }
}
