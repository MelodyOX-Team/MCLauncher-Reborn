using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Network;

namespace MCLauncher.Core.Services;

/// <summary>启动器公告。</summary>
public sealed class LauncherNotice
{
    /// <summary>稳定标识，用于记住「已关闭」。</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Message { get; init; }

    public string? Url { get; init; }
}

/// <summary>Release 里的一个附件。</summary>
public sealed class LauncherUpdateAsset
{
    public required string Name { get; init; }

    public required string Url { get; init; }

    /// <summary>字节数；未知时为 -1。</summary>
    public long Size { get; init; } = -1;

    /// <summary>GitHub 给出的 SHA-256 摘要（十六进制）；没有时为 null。</summary>
    public string? Sha256 { get; init; }
}

/// <summary>GitHub Releases 里的最新版本。</summary>
public sealed class LauncherUpdate
{
    public required string Version { get; init; }

    public string? Title { get; init; }

    public string? Notes { get; init; }

    public string? Url { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public IReadOnlyList<LauncherUpdateAsset> Assets { get; init; } = Array.Empty<LauncherUpdateAsset>();

    /// <summary>
    /// 可以原地替换启动器的 exe 附件：优先名字里带 MCLauncher 的，否则取第一个 exe。
    /// 没有 exe 附件时返回 null（只能让用户去 Release 页面手动下载）。
    /// </summary>
    public LauncherUpdateAsset? ExecutableAsset
    {
        get
        {
            var executables = Assets
                .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return executables.FirstOrDefault(a => a.Name.Contains("MCLauncher", StringComparison.OrdinalIgnoreCase))
                   ?? executables.FirstOrDefault();
        }
    }
}

/// <summary>
/// 启动器自身的公告与版本检查：公告读网站仓库（MelodyOX/MelodyOX.github.io）的 notice.json，
/// 官方 raw + jsDelivr 镜像并行竞速；自动更新读新仓库 MelodyOX-Team/MCLauncher-Reborn 的 Releases。
/// 所有失败都只记日志并返回 null，启动流程不能因为这两个可选项而中断。
/// </summary>
public sealed class LauncherInfoService
{
    /// <summary>自动更新（Releases）所在仓库。</summary>
    private const string Repository = "MelodyOX-Team/MCLauncher-Reborn";

    /// <summary>公告与网站内容所在仓库（GitHub Pages 站点）。</summary>
    private const string NoticeRepository = "MelodyOX/MelodyOX.github.io";

    private const string NoticeUrl =
        "https://raw.githubusercontent.com/" + NoticeRepository + "/main/notice.json";

    private const string NoticeMirrorUrl =
        "https://cdn.jsdelivr.net/gh/" + NoticeRepository + "@main/notice.json";

    private const string LatestReleaseUrl =
        "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>REST API 被限流（未登录请求按 IP 60 次/小时）时的备选：Atom 源不走 REST 配额。</summary>
    private const string ReleaseAtomUrl =
        "https://github.com/" + Repository + "/releases.atom";

    private readonly HttpDownloader _downloader;
    private readonly ILogger _log;

    public LauncherInfoService(HttpDownloader downloader, ILogger log)
    {
        _downloader = downloader;
        _log = log;
    }

    /// <summary>获取公告；没有公告或获取失败时返回 null。</summary>
    public async Task<LauncherNotice?> FetchNoticeAsync(CancellationToken ct = default)
    {
        var candidates = new[]
        {
            new DownloadCandidate(NoticeUrl, "GitHub"),
            new DownloadCandidate(NoticeMirrorUrl, "jsDelivr"),
        };

        try
        {
            var json = await _downloader.GetStringRaceAsync(candidates, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            {
                return null;
            }

            var title = GetString(root, "title");
            var id = GetString(root, "id") ?? title;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return new LauncherNotice
            {
                Id = id.Trim(),
                Title = title.Trim(),
                Message = GetString(root, "message"),
                Url = GetString(root, "url"),
            };
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            _log.Info("仓库暂无公告（notice.json 不存在或尚未发布）");
            return null;
        }
        catch (Exception ex)
        {
            _log.Warn("获取启动器公告失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 检查最新版本；没有发布或接口失败时返回 null。
    /// 优先用 REST API（能拿到附件，可原地自更新）；被限流时退回 Atom 源，只保证版本信息。
    /// </summary>
    public async Task<LauncherUpdate?> CheckUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            return await FetchFromApiAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            _log.Info("仓库暂无已发布版本，跳过更新检查");
            return null;
        }
        catch (Exception ex)
        {
            _log.Info($"GitHub Releases API 不可用（{ex.Message}），改读 releases.atom");
            return await FetchFromAtomAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<LauncherUpdate?> FetchFromApiAsync(CancellationToken ct)
    {
        var json = await _downloader
            .GetStringAsync(new[] { new DownloadCandidate(LatestReleaseUrl, "GitHub") }, ct)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var version = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        DateTimeOffset? publishedAt = null;
        if (GetString(root, "published_at") is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            publishedAt = parsed;
        }

        return new LauncherUpdate
        {
            Version = version.Trim(),
            Title = GetString(root, "name"),
            Notes = GetString(root, "body"),
            Url = GetString(root, "html_url"),
            PublishedAt = publishedAt,
            Assets = ParseAssets(root),
        };
    }

    /// <summary>
    /// 从 releases.atom 取最新一条：只解析版本、标题、说明和链接，没有附件信息。
    /// 这条路径用于 REST API 被限流或临时不可用的情况。
    /// </summary>
    private async Task<LauncherUpdate?> FetchFromAtomAsync(CancellationToken ct)
    {
        try
        {
            var xml = await _downloader
                .GetStringAsync(new[] { new DownloadCandidate(ReleaseAtomUrl, "GitHub Atom") }, ct)
                .ConfigureAwait(false);

            var root = XDocument.Parse(xml).Root;
            var ns = root?.Name.Namespace ?? XNamespace.None;
            var entry = root?.Element(ns + "entry");
            if (entry is null)
            {
                _log.Info("releases.atom 暂无发布记录");
                return null;
            }

            var href = entry.Elements(ns + "link")
                .FirstOrDefault(link => link.Attribute("rel")?.Value == "alternate")
                ?.Attribute("href")?.Value;

            var version = href is null ? null : Uri.UnescapeDataString(href.TrimEnd('/').Split('/')[^1]);
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            DateTimeOffset? publishedAt = null;
            if (entry.Element(ns + "updated")?.Value is { } updated
                && DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                publishedAt = parsed;
            }

            return new LauncherUpdate
            {
                Version = version,
                Title = entry.Element(ns + "title")?.Value,
                Notes = PlainText(entry.Element(ns + "content")?.Value),
                Url = href,
                PublishedAt = publishedAt,
            };
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            _log.Info("仓库暂无已发布版本，跳过更新检查");
            return null;
        }
        catch (Exception ex)
        {
            _log.Warn("检查启动器更新失败", ex);
            return null;
        }
    }

    /// <summary>Atom 的 content 是一段转义后的 HTML，横幅只留纯文本。</summary>
    private static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = WebUtility.HtmlDecode(html);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// 比较版本号（允许前缀 <c>v</c> 与 <c>-beta</c> 之类后缀）。只在候选确实更高时返回 true。
    /// </summary>
    public static bool IsNewerVersion(string? candidate, string current)
    {
        var left = ParseVersion(candidate);
        var right = ParseVersion(current);
        if (left is null || right is null)
        {
            return false;
        }

        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a != b)
            {
                return a > b;
            }
        }

        return false;
    }

    private static int[]? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var parts = text.Split('.');
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers.Length == 0 ? null : numbers;
    }

    /// <summary>解析 Release 的 assets 数组；字段不全的条目直接跳过。</summary>
    private static IReadOnlyList<LauncherUpdateAsset> ParseAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<LauncherUpdateAsset>();
        }

        var assets = new List<LauncherUpdateAsset>();
        foreach (var item in list.EnumerateArray())
        {
            var name = GetString(item, "name");
            var url = GetString(item, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            long size = -1;
            if (item.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number)
            {
                size = sizeElement.TryGetInt64(out var value) ? value : -1;
            }

            assets.Add(new LauncherUpdateAsset
            {
                Name = name.Trim(),
                Url = url.Trim(),
                Size = size,
                Sha256 = ParseDigest(GetString(item, "digest")),
            });
        }

        return assets;
    }

    /// <summary>GitHub 的 digest 形如 <c>sha256:xxxx</c>；只认 SHA-256。</summary>
    private static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[prefix.Length..].Trim();
        return hex.Length == 0 ? null : hex;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>GitHub 的 404 表示「还没有这个文件/版本」，属于正常状态而不是错误。</summary>
    private static bool IsNotFound(Exception exception) =>
        exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
        || exception.InnerException is HttpRequestException { StatusCode: HttpStatusCode.NotFound };
}
