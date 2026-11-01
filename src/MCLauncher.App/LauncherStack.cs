using System.IO;
using MCLauncher.Core.Connect;
using MCLauncher.Core.Logging;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services;
using MCLauncher.Core.Services.Auth;

namespace MCLauncher.App;

/// <summary>已安装实例的摘要信息。</summary>
public sealed class InstalledVersionInfo
{
    public required string Id { get; init; }

    /// <summary>实例展示名；为空时用版本 id。</summary>
    public string? DisplayName { get; init; }

    /// <summary>继承的原版基底（加载器/实例版本才有）。</summary>
    public string? BaseVersionId { get; init; }

    public string? Type { get; init; }

    /// <summary>加载器名称（原版 / Forge / Fabric / Quilt / NeoForge / OptiFine）。</summary>
    public string LoaderName { get; init; } = "原版";

    /// <summary>加载器版本号（有则显示）。</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>完整加载器描述，如 <c>Fabric 0.20.1</c>。</summary>
    public string? LoaderText { get; init; }

    /// <summary>实例所属的游戏目录。</summary>
    public string GameDirectory { get; init; } = string.Empty;

    /// <summary>界面展示用的标题（优先用实例名）。</summary>
    public string Display => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName!.Trim();

    /// <summary>基底游戏版本号。</summary>
    public string VersionText => BaseVersionId ?? Id;

    /// <summary>副标题：<c>26.3，Fabric 0.20.1</c>。</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(LoaderText)
        ? VersionText
        : $"{VersionText}，{LoaderText}";

    public bool HasLoader => !string.IsNullOrWhiteSpace(LoaderText);

    public override string ToString() => Display;
}

/// <summary>游戏异常退出（已带崩溃分析结果）。</summary>
public sealed class GameCrashedEventArgs : EventArgs
{
    public required CrashReport Report { get; init; }

    public required string InstanceId { get; init; }

    public required string InstanceName { get; init; }

    public string InstanceDirectory { get; init; } = string.Empty;
}

/// <summary>
/// 启动器运行时装配：把 Core 的全部服务组装好，并持有当前会话状态
/// （游戏根目录、账号、启动日志缓冲）。界面层只跟它打交道。
/// </summary>
public sealed class LauncherStack : IDisposable
{
    public required Core.Abstractions.ILogger Log { get; init; }

    public required ConfigService Config { get; init; }

    public required AccountService Accounts { get; init; }

    public required HttpDownloader Downloader { get; init; }

    public required DownloadSourceResolver Resolver { get; init; }

    public required DownloadBatch Batch { get; init; }

    public required VersionRepository Repository { get; init; }

    public required VersionManifestService Manifests { get; init; }

    public required GameInstaller Installer { get; init; }

    public required JavaService Java { get; init; }

    /// <summary>缺少合适 Java 时从 Azul 下载 Zulu JDK。</summary>
    public required ZuluJavaService ZuluJava { get; init; }

    public required LaunchArgumentBuilder ArgumentBuilder { get; init; }

    public required GameLauncher Launcher { get; init; }

    public required GameLaunchService LaunchService { get; init; }

    public required InstanceService Instances { get; init; }

    public required ModLoaderService ModLoaders { get; init; }

    public required MicrosoftAuthService MicrosoftAuth { get; init; }

    /// <summary>账号可用性检查与启动前令牌准备。</summary>
    public required AccountHealthService AccountHealth { get; init; }

    public required AuthlibAuthService AuthlibAuth { get; init; }

    public required AuthlibInjectorService AuthlibInjector { get; init; }

    public required MinecraftProfileService Profiles { get; init; }

    public required ModpackImporter Modpacks { get; init; }

    public required ResourceRepository Resources { get; init; }

    public required ServerListService ServerList { get; init; }

    /// <summary>CurseForge 检索、文件查询与指纹匹配。</summary>
    public required CurseForgeService CurseForge { get; init; }

    /// <summary>Modrinth 检索与文件挑选（免 API Key）。</summary>
    public required ModrinthService Modrinth { get; init; }

    public required ResourceDownloadService ResourceDownloads { get; init; }

    public required ResourceUpdateService ResourceUpdates { get; init; }

    /// <summary>「更新游戏」的检查与升级（本体、加载器、模组）。</summary>
    public required GameUpdateService GameUpdates { get; init; }

    /// <summary>好友直连房间。</summary>
    public required DirectConnectService DirectConnect { get; init; }

    /// <summary>樱花穿透 frpc。</summary>
    public required FrpService Frp { get; init; }

    /// <summary>陶瓦联机官方工具。</summary>
    public required TerracottaService Terracotta { get; init; }

    /// <summary>启动日志缓冲（界面绑定）。</summary>
    public LaunchLogBuffer LaunchLog { get; } = new();

    /// <summary>版本列表 / 账号 / 设置发生变化时触发，供各页面刷新。</summary>
    public event EventHandler? Changed;

    /// <summary>游戏异常退出并完成崩溃分析后触发，由外壳弹出崩溃界面。</summary>
    public event EventHandler<GameCrashedEventArgs>? GameCrashed;

    /// <summary>当前游戏根目录（<c>.minecraft</c>）。</summary>
    public string GameRoot =>
        Config.Settings.SelectedGameFolder
        ?? Config.EnsureDefaultGameFolder(AppContext.BaseDirectory);

    public static LauncherStack Create(ConfigService config, Core.Abstractions.ILogger log)
    {
        var downloader = new HttpDownloader(log);
        var resolver = new DownloadSourceResolver(log)
        {
            VersionListSource = config.Settings.VersionListSource,
            FileSource = config.Settings.FileSource,
            MirrorBase = string.IsNullOrWhiteSpace(config.Settings.MirrorBase)
                ? DownloadSourceResolver.DefaultMirrorBase
                : config.Settings.MirrorBase!,
        };

        // 「自动选择下载源」要依据镜像连通性决定候选顺序：后台探测，不阻塞启动。
        if (config.Settings.VersionListSource == DownloadSourcePreference.Auto
            || config.Settings.FileSource == DownloadSourcePreference.Auto)
        {
            _ = ProbeMirrorAsync(downloader, resolver, log);
        }

        var batch = new DownloadBatch(downloader, log);
        var repository = new VersionRepository(new VersionResolver(log), log);
        var manifests = new VersionManifestService(downloader, resolver, log, Path.Combine(config.ConfigDirectory, "cache"));
        var installer = new GameInstaller(downloader, resolver, batch, repository, log);
        var javaRoot = Path.Combine(config.ConfigDirectory, "java");
        var java = new JavaService(log, javaRoot);
        var zuluJava = new ZuluJavaService(downloader, batch, java, log, javaRoot);
        var forgeInstaller = new ForgeInstallerService(downloader, resolver, installer, java, log, zuluJava);
        var argumentBuilder = new LaunchArgumentBuilder(log);
        var launcher = new GameLauncher(log);
        var authlibAuth = new AuthlibAuthService(downloader, log);
        var authlibInjector = new AuthlibInjectorService(downloader, log);
        var accounts = new AccountService(config, log);
        var msAuth = new MicrosoftAuthService(downloader, config, log);
        var profiles = new MinecraftProfileService(downloader, log);
        var resources = new ResourceRepository(log);
        var modpacks = new ModpackImporter(downloader, resolver, resources, log);
        var curseForge = new CurseForgeService(
            downloader, resolver, config.ResolveCurseForgeApiKey(), log);
        var modrinth = new ModrinthService(downloader, resolver, log);
        var instances = new InstanceService(repository, log);
        var modLoaders = new ModLoaderService(downloader, repository, installer, forgeInstaller, log);
        var resourceDownloads = new ResourceDownloadService(downloader, resolver, log);
        var resourceUpdates = new ResourceUpdateService(curseForge, modrinth, log);
        var gameUpdates = new GameUpdateService(
            installer, modLoaders, instances, repository, manifests, resources,
            resourceUpdates, resourceDownloads, log);

        return new LauncherStack
        {
            Log = log,
            Config = config,
            Accounts = accounts,
            Downloader = downloader,
            Resolver = resolver,
            Batch = batch,
            Repository = repository,
            Manifests = manifests,
            Installer = installer,
            Java = java,
            ZuluJava = zuluJava,
            ArgumentBuilder = argumentBuilder,
            Launcher = launcher,
            LaunchService = new GameLaunchService(
                repository, installer, java, argumentBuilder, launcher, authlibAuth, authlibInjector, log),
            Instances = instances,
            ModLoaders = modLoaders,
            MicrosoftAuth = msAuth,
            AccountHealth = new AccountHealthService(accounts, msAuth, authlibAuth, config, log),
            AuthlibAuth = authlibAuth,
            AuthlibInjector = authlibInjector,
            Profiles = profiles,
            Modpacks = modpacks,
            Resources = resources,
            ServerList = new ServerListService(log),
            CurseForge = curseForge,
            Modrinth = modrinth,
            ResourceDownloads = resourceDownloads,
            ResourceUpdates = resourceUpdates,
            GameUpdates = gameUpdates,
            DirectConnect = new DirectConnectService(log),
            Frp = new FrpService(log, config.ConfigDirectory, downloader),
            Terracotta = new TerracottaService(log, config.ConfigDirectory),
        };
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void NotifyGameCrashed(GameCrashedEventArgs args) => GameCrashed?.Invoke(this, args);

    // ---------------------------------------------------------------- 游戏目录

    /// <summary>官方启动器默认目录（<c>%APPDATA%\.minecraft</c>）。</summary>
    public static string OfficialGameFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    public IReadOnlyList<string> GameFolders => Config.Settings.GameFolders;

    public static string NormalizeGameFolder(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return path.Trim();
        }
    }

    public static bool IsSameGameFolder(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(NormalizeGameFolder(a!), NormalizeGameFolder(b!), StringComparison.OrdinalIgnoreCase);

    /// <summary>切换当前游戏目录并保存。</summary>
    public bool SelectGameFolder(string path)
    {
        var normalized = NormalizeGameFolder(path);
        if (!Directory.Exists(normalized))
        {
            return false;
        }

        if (!Config.Settings.GameFolders.Any(folder => IsSameGameFolder(folder, normalized)))
        {
            Config.Settings.GameFolders.Add(normalized);
        }

        Config.Settings.SelectedGameFolder = normalized;
        Config.Save();
        RaiseChanged();
        return true;
    }

    /// <summary>把目录加入列表（不切换）。</summary>
    public bool AddGameFolder(string path)
    {
        var normalized = NormalizeGameFolder(path);
        if (!Directory.Exists(normalized))
        {
            return false;
        }

        if (Config.Settings.GameFolders.Any(folder => IsSameGameFolder(folder, normalized)))
        {
            return false;
        }

        Config.Settings.GameFolders.Add(normalized);
        Config.Save();
        RaiseChanged();
        return true;
    }

    /// <summary>从列表移除目录；当前目录被移除时自动切到第一个。至少保留一个。</summary>
    public bool RemoveGameFolder(string path)
    {
        if (Config.Settings.GameFolders.Count <= 1)
        {
            return false;
        }

        var existing = Config.Settings.GameFolders
            .FirstOrDefault(folder => IsSameGameFolder(folder, path));

        if (existing is null)
        {
            return false;
        }

        Config.Settings.GameFolders.Remove(existing);

        if (IsSameGameFolder(Config.Settings.SelectedGameFolder, existing))
        {
            Config.Settings.SelectedGameFolder = Config.Settings.GameFolders[0];
        }

        Config.Save();
        RaiseChanged();
        return true;
    }

    /// <summary>扫描当前游戏目录里已安装的实例。</summary>
    public IReadOnlyList<InstalledVersionInfo> GetInstalledVersions()
    {
        var result = new List<InstalledVersionInfo>();
        var gameRoot = GameRoot;

        foreach (var id in Instances.ListVisibleInstanceIds(gameRoot))
        {
            var json = Repository.TryLoad(gameRoot, id);
            if (json is null)
            {
                continue;
            }

            var config = Instances.Load(gameRoot, id);
            var (loaderName, loaderVersion, loaderText) = DetectLoaderDetails(json, id, config);

            result.Add(new InstalledVersionInfo
            {
                Id = id,
                DisplayName = config.DisplayName,
                BaseVersionId = ResolveBaseVersionId(gameRoot, json, config, id),
                Type = json.Type,
                LoaderName = loaderName,
                LoaderVersion = loaderVersion,
                LoaderText = loaderText,
                GameDirectory = gameRoot,
            });
        }

        // 加载器版本排在自己的基底后面，纯原版按版本号倒序。
        result.Sort((a, b) =>
        {
            var byBase = string.Compare(a.BaseVersionId ?? a.Id, b.BaseVersionId ?? b.Id, StringComparison.OrdinalIgnoreCase);
            if (byBase != 0)
            {
                return byBase;
            }

            return string.Compare(b.Id, a.Id, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    /// <summary>
    /// 解析实例对应的原版版本：继承字段 → 实例配置 → 原版快照 → 客户端 jar 里的 version.json。
    /// 都没有时返回 null（极老的客户端 jar 不带这份元数据），不要退回实例 id。
    /// </summary>
    private string? ResolveBaseVersionId(
        string gameRoot, GameVersionJson json, InstanceConfig config, string id)
    {
        if (!string.IsNullOrWhiteSpace(json.InheritsFrom))
        {
            return json.InheritsFrom.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.GameVersion))
        {
            return config.GameVersion.Trim();
        }

        var vanilla = Instances.ReadVanillaVersionId(gameRoot, id);
        return !string.IsNullOrWhiteSpace(vanilla)
            ? vanilla
            : Instances.ReadJarGameVersion(gameRoot, json.Jar ?? id);
    }

    /// <summary>从版本 id 推断加载器类型（仅用于界面展示，不影响安装/启动逻辑）。</summary>
    public static string DetectLoader(string versionId)
    {
        var id = versionId.ToLowerInvariant();

        // 顺序不能反：neoforge-21.1.0 里也含 "forge-"。
        if (id.Contains("neoforge"))
        {
            return "NeoForge";
        }

        if (id.Contains("-forge") || id.Contains("forge-"))
        {
            return "Forge";
        }

        if (id.Contains("quilt"))
        {
            return "Quilt";
        }

        if (id.Contains("fabric"))
        {
            return "Fabric";
        }

        if (id.Contains("optifine"))
        {
            return "OptiFine";
        }

        if (id.Contains("liteloader"))
        {
            return "LiteLoader";
        }

        return "原版";
    }

    /// <summary>加载器展示信息：优先用实例配置里的记录，其次从库坐标推断，最后按版本 id 猜。</summary>
    private static (string Name, string? Version, string? Text) DetectLoaderDetails(
        GameVersionJson json, string versionId, InstanceConfig config)
    {
        var saved = config.Loaders.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(saved))
        {
            var parsed = ModLoaderService.ParseLoaderDescription(saved);
            return parsed is { } p
                ? (ModLoaderService.DisplayName(p.Kind), p.Version, saved)
                : (saved!, null, saved);
        }

        foreach (var library in json.Libraries ?? new List<Library>())
        {
            var coordinates = library.Name ?? string.Empty;
            var lower = coordinates.ToLowerInvariant();
            var version = coordinates.Split(':').ElementAtOrDefault(2);
            version = StripGamePrefix(version, json.InheritsFrom);

            if (lower.Contains("fabric-loader"))
            {
                return ("Fabric", version, Describe("Fabric", version));
            }

            if (lower.Contains("quilt-loader"))
            {
                return ("Quilt", version, Describe("Quilt", version));
            }

            if (lower.Contains("neoforge"))
            {
                return ("NeoForge", version, Describe("NeoForge", version));
            }

            if (lower.Contains("minecraftforge:forge") || lower.Contains("minecraftforge:fmlloader"))
            {
                return ("Forge", version, Describe("Forge", version));
            }
        }

        var kind = DetectLoader(versionId);
        if (kind == "原版")
        {
            return ("原版", null, null);
        }

        var guessed = GuessLoaderVersionFromId(versionId, kind);
        return (kind, guessed, Describe(kind, guessed));
    }

    private static string Describe(string kind, string? version) =>
        string.IsNullOrWhiteSpace(version) ? kind : $"{kind} {version}";

    private static string? StripGamePrefix(string? version, string? baseVersion)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(baseVersion))
        {
            return version;
        }

        var prefix = baseVersion + "-";
        return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? version[prefix.Length..]
            : version;
    }

    private static string? GuessLoaderVersionFromId(string versionId, string kind)
    {
        var id = versionId;
        return kind switch
        {
            "Fabric" => SliceBetween(id, "fabric-loader-", "-"),
            "Quilt" => SliceBetween(id, "quilt-loader-", "-"),
            "NeoForge" => SliceAfter(id, "neoforge-"),
            "Forge" => SliceAfter(id, "-forge-"),
            "OptiFine" => SliceAfter(id, "optifine-"),
            _ => null,
        };
    }

    private static string? SliceAfter(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : text[(index + marker.Length)..];
    }

    private static string? SliceBetween(string text, string marker, string terminator)
    {
        var after = SliceAfter(text, marker);
        if (after is null)
        {
            return null;
        }

        var end = after.IndexOf(terminator, StringComparison.Ordinal);
        return end < 0 ? after : after[..end];
    }

    /// <summary>实例目录：开启隔离时为 <c>versions/&lt;id&gt;</c>，否则为游戏根目录。</summary>
    public string GetInstanceDirectory(string versionId, bool isolated) =>
        isolated ? VersionRepository.VersionDirectory(GameRoot, versionId) : GameRoot;

    /// <summary>该版本当前是否启用隔离（实例级设置优先，未设置则跟随全局默认）。</summary>
    public bool IsIsolated(string versionId) =>
        Instances.IsIsolated(GameRoot, versionId, Config.Settings.DefaultIsolation);

    /// <summary>从清单下载并补全一个版本（"启动即安装"也走它）。</summary>
    public async Task<InstallReport> InstallVersionAsync(
        string versionId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var entry = await Manifests.FindAsync(versionId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"版本清单里找不到 {versionId}");

        var version = await Manifests.GetVersionJsonAsync(entry, ct).ConfigureAwait(false);
        version.Id = entry.Id;

        var report = await Installer
            .InstallVersionAsync(GameRoot, version, BuildInstallOptions(), progress, ct)
            .ConfigureAwait(false);

        RaiseChanged();
        return report;
    }

    /// <summary>补全（校验并修复）一个已安装版本。</summary>
    public async Task<InstallReport> CompleteVersionAsync(
        string versionId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var report = await Installer
            .EnsureCompleteAsync(GameRoot, versionId, BuildInstallOptions(), progress, ct)
            .ConfigureAwait(false);

        RaiseChanged();
        return report;
    }

    /// <summary>
    /// 确保版本的继承链完整：缺少的基底原版会自动从版本清单补装。
    /// 基底被删除后启动加载器实例时走这里自愈，而不是直接报「找不到被继承的版本」。
    /// 返回本次补装的基底版本 id。
    /// </summary>
    public async Task<IReadOnlyList<string>> EnsureInheritanceChainAsync(
        string versionId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var missing = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inheritedFrom = Repository.TryLoad(GameRoot, versionId)?.InheritsFrom;

        while (!string.IsNullOrWhiteSpace(inheritedFrom) && visited.Add(inheritedFrom))
        {
            var parent = Repository.TryLoad(GameRoot, inheritedFrom);
            if (parent is null)
            {
                missing.Add(inheritedFrom);
                break;
            }

            inheritedFrom = parent.InheritsFrom;
        }

        // 从最外层基底开始装，保证补装后的链是完整的。
        for (var i = missing.Count - 1; i >= 0; i--)
        {
            var baseId = missing[i];
            var report = await InstallVersionAsync(baseId, progress, ct).ConfigureAwait(false);
            if (!report.Success)
            {
                throw new InvalidOperationException(
                    $"基底版本 {baseId} 补装失败：{report.Failed} 个文件未能下载");
            }
        }

        return missing;
    }

    public InstallOptions BuildInstallOptions() => new()
    {
        DownloadAssets = true,
        ExtractNatives = true,
        CreateLauncherProfiles = true,
        MaxParallel = Config.Settings.EffectiveDownloadThreads,
        VerifyExisting = true,
    };

    /// <summary>
    /// 探测 BMCLAPI 是否可达，供「自动选择下载源」决定候选顺序。
    /// 只读取版本清单（很小），探测失败只影响排序，不阻塞任何下载。
    /// </summary>
    private static async Task ProbeMirrorAsync(
        HttpDownloader downloader,
        DownloadSourceResolver resolver,
        Core.Abstractions.ILogger log)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var candidates = new[]
            {
                new DownloadCandidate(
                    resolver.MirrorBase + "/mc/game/version_manifest_v2.json", "BMCLAPI"),
            };

            await downloader.GetStringAsync(candidates, cts.Token).ConfigureAwait(false);
            resolver.SetMirrorReachable(true);
            log.Info("镜像源探测成功，自动模式优先使用镜像");
        }
        catch (Exception ex)
        {
            resolver.SetMirrorReachable(false);
            log.Warn("镜像源探测失败，自动模式改用官方源", ex);
        }
    }

    public void Dispose()
    {
        Terracotta.Dispose();
        Frp.Dispose();
        DirectConnect.Dispose();
        Downloader.Dispose();
    }
}
