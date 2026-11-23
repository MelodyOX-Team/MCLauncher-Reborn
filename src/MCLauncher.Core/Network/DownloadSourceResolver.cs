using MCLauncher.Core.Abstractions;

namespace MCLauncher.Core.Network;

/// <summary>下载源偏好。</summary>
public enum DownloadSourcePreference
{
    /// <summary>自动：探测镜像是否可用，可用时镜像优先，否则官方优先。</summary>
    Auto,

    /// <summary>尽量使用官方源，失败后回退镜像。</summary>
    Official,

    /// <summary>尽量使用镜像源，失败后回退官方。</summary>
    Mirror,
}

/// <summary>
/// 把一个官方下载地址展开成「官方 + 镜像」的候选列表。
/// <para>
/// 核心规则：**镜像映射按 URL 类别分别处理**，不能一刀切换掉域名。
/// 资源对象要落到 <c>…/assets</c>，库要落到 <c>…/maven</c>，
/// 元数据与客户端 jar 落到镜像根 —— 混用会大面积 404。
/// </para>
/// </summary>
public sealed class DownloadSourceResolver
{
    /// <summary>内置默认镜像根（BMCLAPI）。</summary>
    public const string DefaultMirrorBase = "https://bmclapi2.bangbang93.com";

    /// <summary>默认额外参与竞速的 BMCLAPI 兼容镜像。</summary>
    public static readonly IReadOnlyList<string> DefaultAdditionalMirrorBases = new[]
    {
        "https://mirror.minguncle.cn",
        "https://mirror.hkmc.online",
    };

    /// <summary>国内 Mod 平台镜像根。</summary>
    public const string DefaultModMirrorBase = "https://mod.mcimirror.top";

    private static readonly string[] OfficialHosts =
    {
        "launchermeta.mojang.com",
        "launcher.mojang.com",
        "piston-meta.mojang.com",
        "piston-data.mojang.com",
        "resources.download.minecraft.net",
        "libraries.minecraft.net",
        "maven.minecraftforge.net",
        "maven.neoforged.net",
    };

    private readonly ILogger _log;

    public DownloadSourceResolver(ILogger log)
    {
        _log = log;
    }

    /// <summary>镜像根地址，可在设置中覆盖。</summary>
    public string MirrorBase { get; set; } = DefaultMirrorBase;

    /// <summary>额外镜像根（BMCLAPI 兼容路径），默认启用 MCMirror 与 MC光速。</summary>
    public IReadOnlyList<string> AdditionalMirrorBases { get; set; } = DefaultAdditionalMirrorBases;

    /// <summary>Mod 平台镜像根地址。</summary>
    public string ModMirrorBase { get; set; } = DefaultModMirrorBase;

    /// <summary>版本清单、版本 JSON 这类元数据使用的源。</summary>
    public DownloadSourcePreference VersionListSource { get; set; } = DownloadSourcePreference.Auto;

    /// <summary>客户端 jar、依赖库、资源对象等游戏文件使用的源。</summary>
    public DownloadSourcePreference FileSource { get; set; } = DownloadSourcePreference.Auto;

    private int _mirrorProbeState; // 0 = 未探测，1 = 可用，2 = 不可用

    /// <summary>Auto 模式下的镜像探测结果；<c>null</c> 表示尚未探测。</summary>
    public bool? MirrorReachable => Volatile.Read(ref _mirrorProbeState) switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    /// <summary>记录 Auto 模式下的镜像探测结果。</summary>
    public void SetMirrorReachable(bool reachable) =>
        Volatile.Write(ref _mirrorProbeState, reachable ? 1 : 2);

    /// <summary>
    /// 展开候选源。<paramref name="versionId"/> 仅在 <see cref="DownloadUrlKind.Client"/> 时使用，
    /// 用于额外生成镜像的 <c>versions/&lt;id&gt;/&lt;id&gt;.jar</c> 候选。
    /// </summary>
    public IReadOnlyList<DownloadCandidate> Resolve(string url, DownloadUrlKind kind, string? versionId = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("下载地址为空", nameof(url));
        }

        var officialUrl = NormalizeOfficial(url);
        var official = new DownloadCandidate(officialUrl, "官方");

        if (kind == DownloadUrlKind.Custom)
        {
            return new[] { official };
        }

        var mirrors = BuildMirrors(officialUrl, kind, versionId);

        // Mod 平台镜像不稳定，官方永远排第一。
        var officialFirst = kind == DownloadUrlKind.ModPlatform || !ShouldPreferMirror(kind);

        var result = new List<DownloadCandidate>(mirrors.Count + 1);
        if (officialFirst)
        {
            result.Add(official);
        }

        foreach (var mirror in mirrors)
        {
            if (!result.Any(c => string.Equals(c.Url, mirror.Url, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(mirror);
            }
        }

        if (!officialFirst)
        {
            result.Add(official);
        }

        return result;
    }

    /// <summary>
    /// 该类别是否优先镜像。元数据跟随「版本列表源」，其余游戏文件跟随「下载源」；
    /// Auto 尚未探测出结果时按镜像可用处理，探测失败后才改为官方优先。
    /// </summary>
    private bool ShouldPreferMirror(DownloadUrlKind kind)
    {
        var preference = kind is DownloadUrlKind.Metadata or DownloadUrlKind.ModLoaderMetadata
            ? VersionListSource
            : FileSource;
        return preference switch
        {
            DownloadSourcePreference.Official => false,
            DownloadSourcePreference.Mirror => true,
            _ => MirrorReachable != false,
        };
    }

    /// <summary>版本列表类元数据是否应优先镜像。</summary>
    public bool PreferMirrorForVersionList => ShouldPreferMirror(DownloadUrlKind.ModLoaderMetadata);

    /// <summary>当前参与映射的镜像根地址，主镜像在前。</summary>
    public IReadOnlyList<(string Url, string Name)> GetMirrorBases() => EnumerateMirrorBases();

    private List<DownloadCandidate> BuildMirrors(string url, DownloadUrlKind kind, string? versionId)
    {
        var mirrors = new List<DownloadCandidate>();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return mirrors;
        }

        if (kind == DownloadUrlKind.ModPlatform)
        {
            AddModPlatformMirrors(mirrors, url);
            return mirrors;
        }

        if (kind == DownloadUrlKind.ModLoaderMetadata)
        {
            AddModLoaderMetadataMirrors(mirrors, uri);
            return mirrors;
        }

        var path = uri.AbsolutePath;
        var bases = EnumerateMirrorBases();

        for (var index = 0; index < bases.Count; index++)
        {
            var (baseUrl, name) = bases[index];
            var isPrimary = index == 0;

            switch (kind)
            {
                case DownloadUrlKind.Asset:
                    AddMirror(mirrors, baseUrl + "/assets" + path, name);
                    break;

                case DownloadUrlKind.Library:
                case DownloadUrlKind.ModLoaderLibrary:
                case DownloadUrlKind.ModLoaderInstaller:
                    AddMirror(mirrors, baseUrl + "/maven" + path, name + "(Maven)");

                    // 只有主镜像（BMCLAPI / 用户自定义）提供 /libraries 别名。
                    if (isPrimary)
                    {
                        AddMirror(mirrors, baseUrl + "/libraries" + path, name + "(Libraries)");
                    }

                    break;

                case DownloadUrlKind.Client:
                    // 只有主镜像提供 versions/<id>/<id>.jar 快捷路径；第三方镜像走原始路径。
                    if (isPrimary && !string.IsNullOrWhiteSpace(versionId))
                    {
                        var safeId = Uri.EscapeDataString(versionId);
                        AddMirror(mirrors, $"{baseUrl}/versions/{safeId}/{safeId}.jar", name + "(版本)");
                    }

                    AddMirror(mirrors, baseUrl + path, name);
                    break;

                case DownloadUrlKind.Metadata:
                case DownloadUrlKind.AssetIndex:
                    AddMirror(mirrors, baseUrl + path, name);
                    break;
            }
        }

        return mirrors;
    }

    /// <summary>主镜像在前，额外镜像按配置顺序追加，并按 URL 去重。</summary>
    private List<(string Url, string Name)> EnumerateMirrorBases()
    {
        var bases = new List<(string Url, string Name)>();
        AddBase(bases, MirrorBase, "BMCLAPI");

        foreach (var extra in AdditionalMirrorBases)
        {
            AddBase(bases, extra, null);
        }

        return bases;
    }

    private static void AddBase(List<(string Url, string Name)> bases, string? url, string? name)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var normalized = url.Trim().TrimEnd('/');
        if (normalized.Length == 0
            || bases.Any(item => string.Equals(item.Url, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        bases.Add((normalized, name ?? MirrorDisplayName(normalized)));
    }

    private static void AddMirror(List<DownloadCandidate> mirrors, string url, string name)
    {
        if (!mirrors.Any(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            mirrors.Add(new DownloadCandidate(url, name));
        }
    }

    /// <summary>给额外镜像取一个短名字，日志和候选来源里能区分。</summary>
    private static string MirrorDisplayName(string baseUrl)
    {
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;

        if (host.Contains("minguncle", StringComparison.OrdinalIgnoreCase))
        {
            return "MCMirror";
        }

        if (host.Contains("hkmc", StringComparison.OrdinalIgnoreCase))
        {
            return "MC光速";
        }

        return host;
    }

    private void AddModLoaderMetadataMirrors(List<DownloadCandidate> mirrors, Uri uri)
    {
        var host = uri.Host;
        var path = uri.AbsolutePath;

        foreach (var (baseUrl, name) in EnumerateMirrorBases())
        {
            if (host.Equals("meta.fabricmc.net", StringComparison.OrdinalIgnoreCase))
            {
                AddMirror(mirrors, baseUrl + "/fabric-meta" + path, name + "(Fabric Meta)");
                continue;
            }

            if (host.Equals("meta.quiltmc.org", StringComparison.OrdinalIgnoreCase))
            {
                // BMCLAPI 只镜像 Quilt 的版本列表；profile/json 仍必须回官方。
                if (path.Equals("/v3/versions/loader", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/v3/versions/game", StringComparison.OrdinalIgnoreCase))
                {
                    AddMirror(mirrors, baseUrl + "/quilt-meta" + path, name + "(Quilt Meta)");
                }

                continue;
            }

            if (host.Equals("files.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/promotions_slim.json", StringComparison.OrdinalIgnoreCase))
            {
                AddMirror(mirrors, baseUrl + "/forge/promos", name + "(Forge Promos)");
                continue;
            }

            if (host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase)
                && path.StartsWith("/api/maven/versions/releases/", StringComparison.OrdinalIgnoreCase))
            {
                // 镜像的 NeoForge 元数据走 details 接口，返回 files 数组而不是 versions 数组。
                AddMirror(
                    mirrors,
                    baseUrl + "/neoforge/meta/api/maven/details/releases/" +
                    path["/api/maven/versions/releases/".Length..],
                    name + "(NeoForge Meta)");
            }
        }
    }

    private void AddModPlatformMirrors(List<DownloadCandidate> mirrors, string url)
    {
        // Modrinth / CurseForge 的国内镜像：接口与 CDN 路径不同，必须分别重写。
        var replacements = new (string From, string To)[]
        {
            ("https://api.modrinth.com/", DefaultModMirrorBase + "/modrinth/"),
            ("https://staging-api.modrinth.com/", DefaultModMirrorBase + "/modrinth/"),
            ("https://cdn.modrinth.com/", DefaultModMirrorBase + "/"),
            ("https://api.curseforge.com/", DefaultModMirrorBase + "/curseforge/"),
            ("https://edge.forgecdn.net/", DefaultModMirrorBase + "/"),
            ("https://mediafilez.forgecdn.net/", DefaultModMirrorBase + "/"),
            ("https://media.forgecdn.net/", DefaultModMirrorBase + "/"),
        };

        foreach (var (from, to) in replacements)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                mirrors.Add(new DownloadCandidate(to + url[from.Length..], "MCIMirror"));
            }
        }
    }

    /// <summary>把已知官方地址的 http 升级为 https（旧版本 JSON 里存在明文 http）。</summary>
    private string NormalizeOfficial(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsOfficialHost(uri.Host))
        {
            return url;
        }

        var upgraded = "https://" + url["http://".Length..];
        _log.Trace($"下载地址已升级为 https：{upgraded}");
        return upgraded;
    }

    /// <summary>该地址是否属于已知的官方源（用于判断是否值得生成镜像候选）。</summary>
    public static bool IsOfficialHost(string host) =>
        OfficialHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase)
                               || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
}
