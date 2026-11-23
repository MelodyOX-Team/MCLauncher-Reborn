using System.Text.Json;
using System.Text.RegularExpressions;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>支持的加载器种类。</summary>
public enum ModLoaderKind
{
    Fabric,
    Quilt,
    Forge,
    NeoForge,
    OptiFine,
}

/// <summary>加载器版本选项。</summary>
public sealed class LoaderVersionOption
{
    public required string Version { get; init; }

    public bool IsRecommended { get; init; }

    public bool IsLatest { get; init; }

    public string Display => IsRecommended
        ? $"{Version}（推荐）"
        : IsLatest ? $"{Version}（最新）" : Version;

    public override string ToString() => Display;
}

/// <summary>加载器安装请求。</summary>
public sealed class LoaderInstallRequest
{
    public required string GameRoot { get; init; }

    /// <summary>基底原版版本 id（必须已经安装）。</summary>
    public required string GameVersion { get; init; }

    public required ModLoaderKind Kind { get; init; }

    public required string LoaderVersion { get; init; }

    public InstallOptions InstallOptions { get; init; } = new();
}

/// <summary>加载器安装结果。</summary>
public sealed class LoaderInstallResult
{
    public bool Success { get; init; }

    /// <summary>安装后产生的版本 id（用于界面自动选中）。</summary>
    public string? VersionId { get; init; }

    public string? Error { get; init; }

    public static LoaderInstallResult Ok(string versionId) => new() { Success = true, VersionId = versionId };

    public static LoaderInstallResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// 加载器自动安装。
/// <list type="bullet">
/// <item><b>Fabric / Quilt</b>：拿 <c>profile/json</c> 直接落一个 <c>inheritsFrom</c> 的版本 JSON，**全程不需要 Java**。</item>
/// <item><b>Forge / NeoForge</b>：下载 installer jar 后由 <see cref="ForgeInstallerService"/> 解析
/// <c>install_profile.json</c> 并直接执行其中的处理器，不运行 <c>--installClient</c>。</item>
/// <item><b>OptiFine</b>：第一版只做"把 jar 放进 mods"，不做版本 JSON 注入。</item>
/// </list>
/// </summary>
public sealed partial class ModLoaderService
{
    private const string FabricMetaBase = "https://meta.fabricmc.net/v2";
    private const string QuiltMetaBase = "https://meta.quiltmc.org/v3";

    private const string ForgeMavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string ForgeMetadataUrl = ForgeMavenBase + "/maven-metadata.xml";
    private const string ForgePromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    private const string NeoForgeBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    private const string NeoForgeMetadataUrl = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";

    private readonly HttpDownloader _downloader;
    private readonly DownloadSourceResolver _resolver;
    private readonly VersionRepository _repository;
    private readonly GameInstaller _installer;
    private readonly ForgeInstallerService _forgeInstaller;
    private readonly ILogger _log;

    public ModLoaderService(
        HttpDownloader downloader,
        DownloadSourceResolver resolver,
        VersionRepository repository,
        GameInstaller installer,
        ForgeInstallerService forgeInstaller,
        ILogger log)
    {
        _downloader = downloader;
        _resolver = resolver;
        _repository = repository;
        _installer = installer;
        _forgeInstaller = forgeInstaller;
        _log = log;
    }

    [GeneratedRegex(@"<version>([^<]+)</version>")]
    private static partial Regex MavenVersionPattern();

    public static string DisplayName(ModLoaderKind kind) => kind switch
    {
        ModLoaderKind.Fabric => "Fabric",
        ModLoaderKind.Quilt => "Quilt",
        ModLoaderKind.Forge => "Forge",
        ModLoaderKind.NeoForge => "NeoForge",
        _ => "OptiFine",
    };

    /// <summary>
    /// 解析整合包里的加载器描述，兼容 <c>Fabric 0.15.3</c> 与 CurseForge 的 <c>fabric-0.15.3</c>。
    /// </summary>
    public static (ModLoaderKind Kind, string? Version)? ParseLoaderDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var text = description.Trim();
        var lower = text.ToLowerInvariant();

        var kind = lower switch
        {
            _ when lower.StartsWith("fabric", StringComparison.Ordinal) => ModLoaderKind.Fabric,
            _ when lower.StartsWith("quilt", StringComparison.Ordinal) => ModLoaderKind.Quilt,
            _ when lower.StartsWith("neoforge", StringComparison.Ordinal) => ModLoaderKind.NeoForge,
            _ when lower.StartsWith("forge", StringComparison.Ordinal) => ModLoaderKind.Forge,
            _ => (ModLoaderKind?)null,
        };

        if (kind is null)
        {
            return null;
        }

        var separator = text.IndexOfAny(new[] { ' ', '-', ':' });
        var version = separator >= 0 ? text[(separator + 1)..].Trim() : null;
        return (kind.Value, string.IsNullOrWhiteSpace(version) ? null : version);
    }

    // ------------------------------------------------------------------ 版本列表

    /// <summary>列出该加载器针对某个原版版本的可用加载器版本。</summary>
    public async Task<IReadOnlyList<LoaderVersionOption>> ListLoaderVersionsAsync(
        ModLoaderKind kind,
        string gameVersion,
        CancellationToken ct = default)
    {
        return kind switch
        {
            ModLoaderKind.Fabric => await ListFabricLikeAsync(FabricMetaBase, ct).ConfigureAwait(false),
            ModLoaderKind.Quilt => await ListFabricLikeAsync(QuiltMetaBase, ct).ConfigureAwait(false),
            ModLoaderKind.Forge => await ListForgeAsync(gameVersion, ct).ConfigureAwait(false),
            ModLoaderKind.NeoForge => await ListNeoForgeAsync(gameVersion, ct).ConfigureAwait(false),
            _ => Array.Empty<LoaderVersionOption>(),
        };
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> ListFabricLikeAsync(string metaBase, CancellationToken ct)
    {
        var candidates = _resolver.Resolve(metaBase + "/versions/loader", DownloadUrlKind.ModLoaderMetadata);
        var json = await GetMetadataAsync(candidates, ct).ConfigureAwait(false);

        var items = JsonUtil.Deserialize<List<FabricLoaderEntry>>(json) ?? new List<FabricLoaderEntry>();

        return items
            .Where(i => !string.IsNullOrWhiteSpace(i.Version))
            .Select(i => new LoaderVersionOption
            {
                Version = i.Version!,
                IsLatest = items.FirstOrDefault()?.Version == i.Version,
            })
            .Take(80)
            .ToList();
    }

    /// <summary>该原版版本是否有对应的 Fabric/Quilt 加载器（meta 里能查到 profile 即可）。</summary>
    public async Task<bool> HasLoaderForAsync(ModLoaderKind kind, string gameVersion, CancellationToken ct = default)
    {
        var baseUrl = kind == ModLoaderKind.Quilt ? QuiltMetaBase : FabricMetaBase;
        try
        {
            var candidates = _resolver.Resolve(baseUrl + "/versions/game", DownloadUrlKind.ModLoaderMetadata);
            var json = await GetMetadataAsync(candidates, ct).ConfigureAwait(false);

            var games = JsonUtil.Deserialize<List<FabricGameEntry>>(json) ?? new List<FabricGameEntry>();
            return games.Any(g => string.Equals(g.Version, gameVersion, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _log.Warn($"查询 {DisplayName(kind)} 支持的原版版本失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 取加载器元数据：镜像优先时并行竞速，官方优先时按候选顺序回退。
    /// </summary>
    private Task<string> GetMetadataAsync(IReadOnlyList<DownloadCandidate> candidates, CancellationToken ct) =>
        _resolver.PreferMirrorForVersionList
            ? _downloader.GetStringRaceAsync(candidates, ct)
            : _downloader.GetStringAsync(candidates, ct);

    private async Task<IReadOnlyList<LoaderVersionOption>> ListForgeAsync(string gameVersion, CancellationToken ct)
    {
        // 版本列表与推荐版本互不依赖，并行取；推荐版本失败不影响列表。
        var versionsTask = FetchForgeVersionsAsync(gameVersion, ct);
        var promosTask = FetchForgePromotionsAsync(ct);
        await Task.WhenAll(versionsTask, promosTask).ConfigureAwait(false);

        var versions = await versionsTask.ConfigureAwait(false);
        var promos = await promosTask.ConfigureAwait(false);
        var recommended = promos.GetValueOrDefault($"{gameVersion}-recommended") ?? string.Empty;
        var latest = promos.GetValueOrDefault($"{gameVersion}-latest") ?? string.Empty;

        var result = new List<LoaderVersionOption>(versions.Count);

        foreach (var version in versions)
        {
            // maven 里同一个 Forge 构建可能带 "-<原版版本>" 后缀（只有这种制品才含 universal jar），
            // 和 promotions 的推荐号比较时要把后缀去掉，否则 1.7.10 这种永远匹配不到推荐版本。
            var comparable = StripGameVersionSuffix(version, gameVersion);
            result.Add(new LoaderVersionOption
            {
                Version = version,
                IsRecommended = comparable == recommended,
                IsLatest = comparable == latest,
            });
        }

        // maven-metadata 的顺序并不可靠（1.7.10 是旧构建在前，1.12.2 反而新构建在后），
        // 自己按构建号倒序，避免对话框默认选中一个多年前的旧构建。
        return result
            .OrderByDescending(option => BuildSortKey(option.Version), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 获取 Forge 版本列表。镜像有专用的 <c>/forge/minecraft/&lt;原版&gt;</c> 接口，
    /// 返回的 JSON 与官方 maven-metadata.xml 结构不同，这里按响应内容选择解析方式。
    /// </summary>
    private async Task<List<string>> FetchForgeVersionsAsync(string gameVersion, CancellationToken ct)
    {
        var candidates = new List<DownloadCandidate>();
        foreach (var (baseUrl, name) in _resolver.GetMirrorBases())
        {
            candidates.Add(new DownloadCandidate(
                $"{baseUrl}/forge/minecraft/{Uri.EscapeDataString(gameVersion)}", $"{name}(Forge)"));
        }

        candidates.Add(new DownloadCandidate(ForgeMetadataUrl, "Forge 官方源"));
        if (!_resolver.PreferMirrorForVersionList)
        {
            MoveOfficialFirst(candidates);
        }

        var text = await GetMetadataAsync(candidates, ct).ConfigureAwait(false);
        return ParseForgeVersions(text, gameVersion);
    }

    private async Task<Dictionary<string, string>> FetchForgePromotionsAsync(CancellationToken ct)
    {
        try
        {
            var candidates = new List<DownloadCandidate>();
            foreach (var (baseUrl, name) in _resolver.GetMirrorBases())
            {
                candidates.Add(new DownloadCandidate(baseUrl + "/forge/promos", $"{name}(Forge Promos)"));
            }

            candidates.Add(new DownloadCandidate(ForgePromotionsUrl, "Forge 官方源"));
            if (!_resolver.PreferMirrorForVersionList)
            {
                MoveOfficialFirst(candidates);
            }

            var json = await GetMetadataAsync(candidates, ct).ConfigureAwait(false);
            return ParseForgePromotions(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("获取 Forge 推荐版本失败（不影响列表）", ex);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static List<string> ParseForgeVersions(string text, string gameVersion)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            return ParseForgeMavenVersions(text, gameVersion);
        }

        var entries = JsonUtil.Deserialize<List<ForgeVersionEntry>>(text) ?? new List<ForgeVersionEntry>();
        var prefix = gameVersion + "-";
        var result = new List<string>();

        foreach (var entry in entries)
        {
            var version = entry.Version?.Trim();
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            if (version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                version = version[prefix.Length..];
            }

            result.Add(version);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ParseForgeMavenVersions(string xml, string gameVersion)
    {
        var prefix = gameVersion + "-";
        var result = new List<string>();

        foreach (Match match in MavenVersionPattern().Matches(xml))
        {
            var full = match.Groups[1].Value.Trim();
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(full[prefix.Length..]);
            }
        }

        return result;
    }

    /// <summary>
    /// 解析 Forge 推荐版本。官方 promotions_slim.json 是 <c>{ "promos": { ... } }</c>，
    /// 镜像 <c>/forge/promos</c> 是 <c>[{ name, build: { version } }]</c>，两种都要认。
    /// </summary>
    private static Dictionary<string, string> ParseForgePromotions(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("name", out var nameElement)
                    || !item.TryGetProperty("build", out var buildElement)
                    || buildElement.ValueKind != JsonValueKind.Object
                    || !buildElement.TryGetProperty("version", out var versionElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                var version = versionElement.GetString();
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version))
                {
                    result[name] = version;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object
                 && root.TryGetProperty("promos", out var promos)
                 && promos.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in promos.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var version = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(version))
                {
                    result[property.Name] = version;
                }
            }
        }

        return result;
    }

    private static void MoveOfficialFirst(List<DownloadCandidate> candidates)
    {
        var index = candidates.FindIndex(c => c.SourceName.Contains("官方", StringComparison.Ordinal));
        if (index <= 0)
        {
            return;
        }

        var official = candidates[index];
        candidates.RemoveAt(index);
        candidates.Insert(0, official);
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> ListNeoForgeAsync(string gameVersion, CancellationToken ct)
    {
        var candidates = _resolver.Resolve(NeoForgeMetadataUrl, DownloadUrlKind.ModLoaderMetadata);
        var json = await GetMetadataAsync(candidates, ct).ConfigureAwait(false);

        var versions = ParseNeoForgeVersions(json);
        var latest = versions.LastOrDefault();
        var prefix = McVersionUtil.GetBaseVersion(gameVersion) switch
        {
            var v when v.StartsWith("1.", StringComparison.Ordinal) => v[2..] + ".",
            var v => v + ".",
        };

        var result = versions
            .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(v => new LoaderVersionOption { Version = v, IsLatest = v == latest })
            .ToList();

        result.Reverse();
        return result;
    }

    /// <summary>
    /// 解析 NeoForge 版本列表。官方接口返回 <c>{ "versions": [...] }</c>，
    /// 镜像 details 接口返回 <c>{ "files": [{ "name": "...", "type": "DIRECTORY" }] }</c>。
    /// </summary>
    private static List<string> ParseNeoForgeVersions(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var result = new List<string>();

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("versions", out var versions)
            && versions.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in versions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var version = item.GetString();
                if (!string.IsNullOrWhiteSpace(version))
                {
                    result.Add(version);
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object
                 && root.TryGetProperty("files", out var files)
                 && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in files.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("type", out var type)
                    && (type.ValueKind != JsonValueKind.String
                        || !string.Equals(type.GetString(), "DIRECTORY", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var version = name.GetString();
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        result.Add(version);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>去掉 Forge maven 版本号里重复的原版后缀：<c>10.13.4.1614-1.7.10</c> → <c>10.13.4.1614</c>。</summary>
    private static string StripGameVersionSuffix(string version, string gameVersion)
    {
        var suffix = "-" + gameVersion;
        return version.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? version[..^suffix.Length]
            : version;
    }

    /// <summary>把 <c>10.13.4.1614</c> 这类构建号补齐成可直接按字符串比较的排序键。</summary>
    private static string BuildSortKey(string version)
    {
        var parts = version.Split('-')[0].Split('.');
        var builder = new System.Text.StringBuilder();

        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var number))
            {
                return version;
            }

            builder.Append(number.ToString("D5")).Append('.');
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------ 安装

    public async Task<LoaderInstallResult> InstallAsync(
        LoaderInstallRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!_repository.IsInstalled(request.GameRoot, request.GameVersion))
        {
            return LoaderInstallResult.Fail($"基底版本 {request.GameVersion} 还没有安装，请先安装它");
        }

        try
        {
            return request.Kind switch
            {
                ModLoaderKind.Fabric or ModLoaderKind.Quilt => await InstallFabricLikeAsync(request, progress, ct).ConfigureAwait(false),
                ModLoaderKind.Forge or ModLoaderKind.NeoForge => await InstallViaInstallerJarAsync(request, progress, ct).ConfigureAwait(false),
                _ => LoaderInstallResult.Fail("OptiFine 请使用「作为 Mod 安装」的方式，不要走加载器安装"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"安装 {DisplayName(request.Kind)} {request.LoaderVersion} 失败", ex);
            return LoaderInstallResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 只获取 Fabric / Quilt 的 profile JSON，不落盘、不下载依赖。
    /// 供「原版 + 加载器合并成一个自洽实例」的直装流程使用。
    /// </summary>
    public async Task<GameVersionJson> GetFabricLikeProfileAsync(
        ModLoaderKind kind,
        string gameVersion,
        string loaderVersion,
        CancellationToken ct = default)
    {
        var metaBase = kind == ModLoaderKind.Quilt ? QuiltMetaBase : FabricMetaBase;
        var url = $"{metaBase}/versions/loader/{Uri.EscapeDataString(gameVersion)}/"
                  + $"{Uri.EscapeDataString(loaderVersion)}/profile/json";

        var candidates = _resolver.Resolve(url, DownloadUrlKind.ModLoaderMetadata);
        var json = await GetMetadataAsync(candidates, ct).ConfigureAwait(false);

        var version = JsonUtil.Deserialize<GameVersionJson>(json)
                      ?? throw new InvalidDataException($"{DisplayName(kind)} 没有返回可用的版本 JSON");

        if (string.IsNullOrWhiteSpace(version.Id))
        {
            throw new InvalidDataException("profile JSON 缺少 id");
        }

        if (!string.Equals(version.InheritsFrom, gameVersion, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"profile JSON 的 inheritsFrom（{version.InheritsFrom}）与请求的基底（{gameVersion}）不一致");
        }

        return version;
    }

    private async Task<LoaderInstallResult> InstallFabricLikeAsync(
        LoaderInstallRequest request,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new InstallProgress
        {
            Stage = $"获取 {DisplayName(request.Kind)} 配置",
            Completed = 0,
            Total = 1,
        });

        var version = await GetFabricLikeProfileAsync(
                request.Kind, request.GameVersion, request.LoaderVersion, ct)
            .ConfigureAwait(false);

        _repository.Save(request.GameRoot, version);
        _log.Info($"已写入 {DisplayName(request.Kind)} 版本 JSON：{version.Id}");

        var report = await _installer
            .EnsureCompleteAsync(request.GameRoot, version.Id!, request.InstallOptions, progress, ct)
            .ConfigureAwait(false);

        return report.Success
            ? LoaderInstallResult.Ok(version.Id!)
            : LoaderInstallResult.Fail($"加载器已写入，但依赖文件下载失败（{report.Failed} 个）");
    }

    private async Task<LoaderInstallResult> InstallViaInstallerJarAsync(
        LoaderInstallRequest request,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var kind = request.Kind;
        var installerUrl = BuildInstallerUrl(kind, request.GameVersion, request.LoaderVersion);

        progress?.Report(new InstallProgress
        {
            Stage = $"下载 {DisplayName(kind)} 安装器",
            Completed = 0,
            Total = 1,
        });

        var installerPath = await _forgeInstaller
            .DownloadInstallerAsync(request.GameRoot, kind, request.LoaderVersion, installerUrl, ct)
            .ConfigureAwait(false);

        var baseVersion = _repository.Resolve(request.GameRoot, request.GameVersion);
        var version = await _forgeInstaller
            .PrepareAsync(
                kind, request.GameRoot, request.GameVersion, request.LoaderVersion,
                baseVersion, installerPath, clientJarPath: null,
                request.InstallOptions, progress, ct)
            .ConfigureAwait(false);

        var versionId = version.Id!;

        // 安装器里的版本 JSON 自带 inheritsFrom；先按原样落盘，补全后再交给实例/引擎流程合并。
        _repository.Save(request.GameRoot, version);

        var report = await _installer
            .EnsureCompleteAsync(request.GameRoot, versionId, request.InstallOptions, progress, ct)
            .ConfigureAwait(false);

        return report.Success
            ? LoaderInstallResult.Ok(versionId)
            : LoaderInstallResult.Fail($"加载器已安装，但补全依赖失败（{report.Failed} 个）");
    }

    /// <summary>
    /// 只解析 Forge / NeoForge 安装器并执行处理器，返回加载器版本 JSON。
    /// 供「原版 + 加载器合并成一个自洽实例」的直装流程使用，不需要先安装原版基底目录。
    /// </summary>
    public async Task<GameVersionJson> GetForgeLikeVersionAsync(
        ModLoaderKind kind,
        string gameRoot,
        string gameVersion,
        string loaderVersion,
        GameVersionJson baseVersion,
        string? clientJarPath,
        InstallOptions options,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (kind is not (ModLoaderKind.Forge or ModLoaderKind.NeoForge))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "只支持 Forge / NeoForge");
        }

        var installerUrl = BuildInstallerUrl(kind, gameVersion, loaderVersion);

        progress?.Report(new InstallProgress
        {
            Stage = $"下载 {DisplayName(kind)} 安装器",
            Completed = 0,
            Total = 1,
        });

        var installerPath = await _forgeInstaller
            .DownloadInstallerAsync(gameRoot, kind, loaderVersion, installerUrl, ct)
            .ConfigureAwait(false);

        return await _forgeInstaller
            .PrepareAsync(
                kind, gameRoot, gameVersion, loaderVersion,
                baseVersion, installerPath, clientJarPath, options, progress, ct)
            .ConfigureAwait(false);
    }

    private static string BuildInstallerUrl(ModLoaderKind kind, string gameVersion, string loaderVersion) =>
        kind == ModLoaderKind.Forge
            ? $"{ForgeMavenBase}/{Uri.EscapeDataString($"{gameVersion}-{loaderVersion}")}/"
              + $"forge-{Uri.EscapeDataString($"{gameVersion}-{loaderVersion}")}-installer.jar"
            : $"{NeoForgeBase}/{Uri.EscapeDataString(loaderVersion)}/"
              + $"neoforge-{Uri.EscapeDataString(loaderVersion)}-installer.jar";

    /// <summary>安装器需要与目标版本匹配的 Java，这里按原版版本号推断主版本。</summary>
    public static int RequiredJavaMajor(string gameVersion)
    {
        if (McVersionUtil.IsAtLeast(gameVersion, 1, 20, 5))
        {
            return 21;
        }

        if (McVersionUtil.IsAtLeast(gameVersion, 1, 18, 0))
        {
            return 17;
        }

        if (McVersionUtil.IsAtLeast(gameVersion, 1, 17, 0))
        {
            return 16;
        }

        return 8;
    }

    // ------------------------------------------------------------------ OptiFine

    /// <summary>
    /// 把用户提供的 OptiFine jar 作为 Mod 放进 <c>mods/</c>。
    /// 第一版不做版本 JSON 注入：只适用于"OptiFine 以 Mod 形式加载"的版本。
    /// </summary>
    public void InstallOptifineAsMod(string gameRoot, string modsDirectory, string optifineJarPath)
    {
        if (!File.Exists(optifineJarPath))
        {
            throw new FileNotFoundException("没有找到 OptiFine jar", optifineJarPath);
        }

        PathUtil.EnsureDirectory(modsDirectory);
        var target = Path.Combine(modsDirectory, Path.GetFileName(optifineJarPath));
        File.Copy(optifineJarPath, target, overwrite: true);
        _log.Info($"已把 OptiFine 作为 Mod 安装：{target}");
    }

    // ------------------------------------------------------------------ 响应模型

    private sealed class FabricLoaderEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    private sealed class FabricGameEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    private sealed class ForgeVersionEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}
