using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// 原版版本的下载与补全。
/// <para>安装产出的磁盘布局：</para>
/// <code>
/// &lt;游戏目录&gt;/versions/&lt;id&gt;/&lt;id&gt;.json        版本 JSON
/// &lt;游戏目录&gt;/versions/&lt;id&gt;/&lt;id&gt;.jar         客户端 jar
/// &lt;游戏目录&gt;/versions/&lt;id&gt;/natives-&lt;os&gt;/    解压后的 natives
/// &lt;游戏目录&gt;/libraries/...                  依赖库（跨版本共享）
/// &lt;游戏目录&gt;/assets/indexes/&lt;id&gt;.json       资源索引
/// &lt;游戏目录&gt;/assets/objects/&lt;h2&gt;/&lt;hash&gt;     资源对象（跨版本共享）
/// &lt;游戏目录&gt;/assets/virtual/&lt;id&gt;/...        virtual 索引展开（1.7 及更早）
/// &lt;游戏目录&gt;/resources/...                   map_to_resources 展开（远古版本）
/// &lt;游戏目录&gt;/launcher_profiles.json         Forge 安装器必需
/// </code>
/// </summary>
public sealed class GameInstaller
{
    private const string AssetsDirectoryName = "assets";
    private const string LibrariesDirectoryName = "libraries";
    private const string ManagedDirectoryName = ".mclauncher";

    /// <summary>
    /// 远古版本的版本 JSON 里没有 <c>assetIndex</c> 对象，只有 <c>assets</c> 名。
    /// 这些索引的 SHA-1 是稳定的公开数据，缺失时用它兜底拼出索引地址。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyAssetIndexHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1.8"] = "f6ad102bcaa53b1a58358f16e376d548d44933ec",
        ["14w31a"] = "10a2a0e75b03cfb5a7196abbdf43b54f7fa61deb",
        ["14w25a"] = "32ff354a3be1c4dd83027111e6d79ee4d701d2c0",
        ["1.7.4"] = "545510a60f526b9aa8a38f9c0bc7a74235d21675",
        ["1.7.10"] = "1863782e33ce7b584fc45b037325a1964e095d3e",
        ["1.7.3"] = "f6cf726f4747128d13887010c2cbc44ba83504d9",
        ["pre-1.6"] = "3d8e55480977e32acd9844e545177e69a52f594b",
        ["legacy"] = "770572e819335b6c0a053f8378ad88eda189fc14",
    };

    private const string LegacyIndexBaseUrl = "https://launchermeta.mojang.com/v1/packages/";

    /// <summary>Forge / NeoForge 的安装器只认这两个文件名存在。</summary>
    private const string MinimalLauncherProfiles = """
    {
      "profiles": {},
      "settings": {
        "enableSnapshots": false,
        "enableAdvanced": false,
        "keepLauncherOpen": false
      },
      "version": 3
    }
    """;

    private readonly HttpDownloader _downloader;
    private readonly DownloadSourceResolver _resolver;
    private readonly DownloadBatch _batch;
    private readonly VersionRepository _repository;
    private readonly ILogger _log;

    public GameInstaller(
        HttpDownloader downloader,
        DownloadSourceResolver resolver,
        DownloadBatch batch,
        VersionRepository repository,
        ILogger log)
    {
        _downloader = downloader;
        _resolver = resolver;
        _batch = batch;
        _repository = repository;
        _log = log;
    }

    // ------------------------------------------------------------------ 路径约定

    public static string AssetsDirectory(string gameDirectory) =>
        Path.Combine(gameDirectory, AssetsDirectoryName);

    public static string LibrariesDirectory(string gameDirectory) =>
        Path.Combine(gameDirectory, LibrariesDirectoryName);

    public static string AssetsIndexPath(string gameDirectory, string indexId) =>
        Path.Combine(AssetsDirectory(gameDirectory), "indexes", indexId + ".json");

    public static string AssetsObjectsDirectory(string gameDirectory) =>
        Path.Combine(AssetsDirectory(gameDirectory), "objects");

    public static string AssetsObjectPath(string gameDirectory, string hash) =>
        Path.Combine(AssetsObjectsDirectory(gameDirectory), hash.Length >= 2 ? hash[..2] : hash, hash);

    public static string VirtualAssetsDirectory(string gameDirectory, string indexId) =>
        Path.Combine(AssetsDirectory(gameDirectory), "virtual", indexId);

    public static string ResourcesDirectory(string gameDirectory) =>
        Path.Combine(gameDirectory, "resources");

    public static string LogConfigDirectory(string gameDirectory) =>
        Path.Combine(AssetsDirectory(gameDirectory), "log_configs");

    /// <summary>
    /// 依赖库在磁盘上的落位。制品路径来自版本元数据（可能是远端内容），
    /// 必须保证解析结果仍位于 libraries 目录内，否则一律拒绝。
    /// </summary>
    public static string LibraryFilePath(string gameDirectory, string artifactPath)
    {
        var libraries = Path.GetFullPath(LibrariesDirectory(gameDirectory));

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(libraries, artifactPath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"库路径不合法：{artifactPath}", ex);
        }

        if (!PathUtil.IsParentOf(libraries, full))
        {
            throw new InvalidDataException($"库路径越界：{artifactPath}");
        }

        return full;
    }

    /// <summary>
    /// 远端 JSON 里的路径段（资源索引 id、对象 hash、日志文件名等）不可信，
    /// 落盘前必须确认最终路径仍位于 <paramref name="root"/> 之内。
    /// </summary>
    private static string EnsureInside(string root, string fullPath)
    {
        var normalizedRoot = Path.GetFullPath(root);

        string normalized;
        try
        {
            normalized = Path.GetFullPath(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"路径不合法：{fullPath}", ex);
        }

        // 前缀比较由 PathUtil.IsParentOf 负责：它会去掉末尾分隔符并忽略大小写。
        if (!PathUtil.IsParentOf(normalizedRoot, normalized))
        {
            throw new InvalidDataException($"路径越界：{fullPath}");
        }

        return normalized;
    }

    private static string SafeAssetsIndexPath(string gameDirectory, string indexId) =>
        EnsureInside(
            Path.Combine(AssetsDirectory(gameDirectory), "indexes"),
            AssetsIndexPath(gameDirectory, indexId));

    private static string SafeAssetsObjectPath(string gameDirectory, string hash) =>
        EnsureInside(AssetsObjectsDirectory(gameDirectory), AssetsObjectPath(gameDirectory, hash));

    private static string SafeVirtualAssetsDirectory(string gameDirectory, string indexId) =>
        EnsureInside(
            Path.Combine(AssetsDirectory(gameDirectory), "virtual"),
            VirtualAssetsDirectory(gameDirectory, indexId));

    /// <summary>
    /// 清理旧版本写入的受管清单；目录为空时一并删除。
    /// 安装过程不需要持久化文件清单，相关数据只在内存中使用。
    /// </summary>
    public static void CleanupManagedDirectory(string gameDirectory)
    {
        var directory = Path.Combine(gameDirectory, ManagedDirectoryName);
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var manifest = Path.Combine(directory, "manifest.json");
            if (File.Exists(manifest))
            {
                File.Delete(manifest);
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响安装结果，最多留下一个空目录。
        }
    }

    // ------------------------------------------------------------------ 入口

    /// <summary>把版本 JSON 落盘并补全全部文件。</summary>
    public async Task<InstallReport> InstallVersionAsync(
        string gameDirectory,
        GameVersionJson version,
        InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new InstallOptions();

        if (string.IsNullOrWhiteSpace(version.Id))
        {
            throw new InvalidOperationException("版本 JSON 缺少 id");
        }

        _repository.Save(gameDirectory, version);
        return await EnsureCompleteAsync(gameDirectory, version.Id!, options, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 直接把原版（可再叠加一个加载器）安装成一个自洽实例：
    /// 在内存中合并版本 JSON，客户端 jar 落到实例目录，依赖库与资源一次性并发下载。
    /// 不会在 <c>versions</c> 下生成额外的原版基底目录。
    /// </summary>
    public async Task<InstallReport> InstallInstanceAsync(
        string gameDirectory,
        string instanceId,
        GameVersionJson baseVersion,
        GameVersionJson? loaderVersion = null,
        InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new InvalidOperationException("实例 id 不能为空");
        }

        // 没有加载器时也走一次 Merge：得到一个副本，避免改动调用方手里的原版 JSON。
        var merged = VersionResolver.Merge(baseVersion, loaderVersion ?? new GameVersionJson());
        merged.Id = instanceId;
        merged.InheritsFrom = null;
        merged.Jar = instanceId;

        _log.Info(loaderVersion is null
            ? $"直接安装原版实例 {instanceId}（{baseVersion.Id}）"
            : $"直接安装实例 {instanceId}（{baseVersion.Id} + {loaderVersion.Id}）");

        return await InstallVersionAsync(gameDirectory, merged, options, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 只补全一批依赖库（不落版本 JSON、不下载资源）。
    /// 供 Forge / NeoForge 的处理器安装流程下载安装器自带依赖。
    /// </summary>
    public async Task<InstallReport> EnsureLibrariesAsync(
        string gameDirectory,
        IReadOnlyList<Library> libraries,
        InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new InstallOptions();

        var requests = new List<DownloadRequest>(libraries.Count);
        foreach (var library in libraries)
        {
            if (!library.Applies())
            {
                continue;
            }

            var request = BuildLibraryRequest(gameDirectory, library, out _);
            if (request is not null)
            {
                requests.Add(request);
            }
        }

        if (requests.Count == 0)
        {
            return InstallReport.Ok(0, 0, 0);
        }

        Report(progress, "下载安装器依赖", 0, requests.Count, 0, 0, null);
        var result = await _batch
            .RunAsync(requests, options.MaxParallel, Wrap(progress, "下载安装器依赖", requests.Count), ct)
            .ConfigureAwait(false);

        return InstallReport.FromBatch(result);
    }

    /// <summary>补全（或校验并修复）一个已落盘版本的全部文件。</summary>
    public async Task<InstallReport> EnsureCompleteAsync(
        string gameDirectory,
        string versionId,
        InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new InstallOptions();

        // 启动前的校验绝大多数情况没有缺失文件，文案用「检查」避免被当成重新下载。
        var indexStage = options.ReportAsCheck ? "检查资源索引" : "下载资源索引";
        var filesStage = options.ReportAsCheck ? "检查游戏文件" : "下载游戏文件";

        // 合并继承链后拿到的才是可直接安装的自洽版本。
        var version = _repository.Resolve(gameDirectory, versionId);

        // ---- 阶段 1：资源索引与日志配置（后面展开资源对象要靠它）----
        var indexInfo = ResolveAssetIndexInfo(version);
        var preRequests = new List<DownloadRequest>();

        if (indexInfo is not null)
        {
            try
            {
                preRequests.Add(new DownloadRequest
                {
                    Name = $"assets/indexes/{indexInfo.Id}.json",
                    Candidates = _resolver.Resolve(indexInfo.Url!, DownloadUrlKind.AssetIndex),
                    TargetPath = SafeAssetsIndexPath(gameDirectory, indexInfo.Id!),
                    Checker = MakeChecker(indexInfo, options.VerifyExisting),
                });
            }
            catch (InvalidDataException ex)
            {
                _log.Warn($"资源索引 id 不合法，已跳过资源下载：{indexInfo.Id}", ex);
                indexInfo = null;
            }
        }

        var logRequest = BuildLogConfigRequest(gameDirectory, version, options);
        if (logRequest is not null)
        {
            preRequests.Add(logRequest);
        }

        var downloaded = 0;
        var skipped = 0;
        long totalBytes = 0;

        if (preRequests.Count > 0)
        {
            Report(progress, indexStage, 0, preRequests.Count, 0, 0, null);
            var preResult = await _batch
                .RunAsync(preRequests, options.MaxParallel, Wrap(progress, indexStage, preRequests.Count), ct)
                .ConfigureAwait(false);

            if (!preResult.Success)
            {
                _log.Error($"安装 {versionId} 失败：资源索引或日志配置下载失败");
                return InstallReport.FromBatch(preResult);
            }

            downloaded += preResult.Completed;
            skipped += preResult.Skipped;
            totalBytes += preRequests.Where(r => r.Checker.Size > 0).Sum(r => r.Checker.Size);
        }

        // ---- 阶段 2：客户端 jar + 依赖库 + 资源对象，一次性并发下载 ----
        var requests = new List<DownloadRequest>();
        var nativesPlans = new List<NativePlan>();

        var jarRequest = BuildClientJarRequest(gameDirectory, version);
        if (jarRequest is not null)
        {
            requests.Add(jarRequest);
        }

        foreach (var library in version.Libraries ?? new List<Library>())
        {
            if (!library.Applies())
            {
                continue;
            }

            var request = BuildLibraryRequest(gameDirectory, library, out var classifier);
            if (request is null)
            {
                continue;
            }

            requests.Add(request);

            if (options.ExtractNatives && library.IsNativeLibrary && classifier is not null)
            {
                nativesPlans.Add(new NativePlan
                {
                    Library = library,
                    Classifier = classifier,
                    JarPath = request.TargetPath,
                });
            }
        }

        if (indexInfo is not null && options.DownloadAssets)
        {
            requests.AddRange(BuildAssetRequests(gameDirectory, indexInfo, options));
        }

        Report(progress, filesStage, 0, requests.Count, 0, 0, null);
        var mainResult = await _batch
            .RunAsync(requests, options.MaxParallel, Wrap(progress, filesStage, requests.Count), ct)
            .ConfigureAwait(false);

        downloaded += mainResult.Completed;
        skipped += mainResult.Skipped;
        totalBytes += requests.Where(r => r.Checker.Size > 0).Sum(r => r.Checker.Size);

        if (!mainResult.Success)
        {
            _log.Error($"安装 {versionId} 失败：关键文件下载未成功");
            return new InstallReport
            {
                Success = false,
                Downloaded = downloaded,
                Skipped = skipped,
                Failed = mainResult.Failed,
                TotalBytes = totalBytes,
                Failures = mainResult.Failures,
            };
        }

        // ---- 阶段 3：解压 natives ----
        if (options.ExtractNatives && nativesPlans.Count > 0)
        {
            Report(progress, "解压 natives", 0, nativesPlans.Count, 0, 0, null);
            ExtractNatives(gameDirectory, versionId, nativesPlans);
            Report(progress, "解压 natives", nativesPlans.Count, nativesPlans.Count, 0, 0, null);
        }

        // ---- 阶段 4：virtual / map_to_resources 展开 ----
        if (indexInfo is not null && options.DownloadAssets)
        {
            var index = TryLoadIndex(gameDirectory, indexInfo);
            if (index is not null && (index.Virtual || index.MapToResources))
            {
                Report(progress, "展开遗留资源", 0, index.Objects.Count, 0, 0, null);
                MaterializeLegacyAssets(gameDirectory, indexInfo, index);
                Report(progress, "展开遗留资源", index.Objects.Count, index.Objects.Count, 0, 0, null);
            }
        }

        // ---- 阶段 5：收尾 ----
        if (options.CreateLauncherProfiles)
        {
            CreateLauncherProfiles(gameDirectory);
        }

        // 安装清单不再落盘；顺手清掉旧版本留下的 manifest.json 与空目录。
        CleanupManagedDirectory(gameDirectory);

        Report(progress, "完成", downloaded, downloaded, totalBytes, totalBytes, null);
        _log.Info($"版本 {versionId} 安装完成：新下载 {downloaded} 个文件，已存在 {skipped} 个");

        return InstallReport.Ok(downloaded, skipped, totalBytes);
    }

    /// <summary>校验已安装版本；返回缺失/被改动的相对路径列表。</summary>
    public async Task<InstallReport> VerifyAsync(
        string gameDirectory,
        string versionId,
        bool deep = false,
        CancellationToken ct = default)
    {
        var options = new InstallOptions { VerifyExisting = deep };
        var version = _repository.Resolve(gameDirectory, versionId);
        var missing = new List<string>();

        var checks = new List<(string Path, FileChecker Checker)>();

        var jarRequest = BuildClientJarRequest(gameDirectory, version);
        if (jarRequest is not null)
        {
            checks.Add((jarRequest.TargetPath, jarRequest.Checker));
        }

        foreach (var library in version.Libraries ?? new List<Library>())
        {
            if (!library.Applies())
            {
                continue;
            }

            var request = BuildLibraryRequest(gameDirectory, library, out _);
            if (request is not null)
            {
                checks.Add((request.TargetPath, request.Checker));
            }
        }

        var indexInfo = ResolveAssetIndexInfo(version);
        if (indexInfo is not null)
        {
            try
            {
                checks.Add((SafeAssetsIndexPath(gameDirectory, indexInfo.Id!), MakeChecker(indexInfo, deep)));
            }
            catch (InvalidDataException ex)
            {
                _log.Warn($"资源索引 id 不合法：{indexInfo.Id}", ex);
                missing.Add(indexInfo.Id!);
            }
        }

        foreach (var (path, checker) in checks)
        {
            ct.ThrowIfCancellationRequested();
            if (!checker.Check(path))
            {
                missing.Add(PathUtil.GetRelative(gameDirectory, path));
            }
        }

        // 资源对象按存在性快速检查（deep 时才算哈希）。
        if (indexInfo is not null)
        {
            var index = TryLoadIndex(gameDirectory, indexInfo);
            if (index is not null)
            {
                foreach (var (name, obj) in index.Objects)
                {
                    string objectPath;
                    try
                    {
                        objectPath = SafeAssetsObjectPath(gameDirectory, obj.Hash);
                    }
                    catch (InvalidDataException ex)
                    {
                        _log.Warn($"资源对象 hash 不合法，已跳过：{name}", ex);
                        missing.Add(name);
                        continue;
                    }

                    var checker = deep ? FileChecker.Of(obj.Hash, obj.Size) : FileChecker.OfSize(obj.Size);
                    if (!checker.Check(objectPath))
                    {
                        missing.Add(name);
                    }
                }
            }
        }

        if (missing.Count == 0)
        {
            _log.Info($"版本 {versionId} 校验通过");
            return InstallReport.Ok(0, checks.Count, 0);
        }

        _log.Warn($"版本 {versionId} 校验发现 {missing.Count} 个问题文件");
        return new InstallReport { Success = false, Missing = missing };
    }

    /// <summary>创建 Forge / NeoForge 安装器需要的 <c>launcher_profiles.json</c>。</summary>
    public void CreateLauncherProfiles(string gameDirectory)
    {
        foreach (var fileName in new[] { "launcher_profiles.json", "launcher_profiles_microsoft_store.json" })
        {
            var path = Path.Combine(gameDirectory, fileName);
            if (File.Exists(path))
            {
                continue;
            }

            try
            {
                PathUtil.EnsureDirectory(gameDirectory);
                File.WriteAllText(path, MinimalLauncherProfiles, new System.Text.UTF8Encoding(false));
                _log.Trace($"已创建 {fileName}");
            }
            catch (Exception ex)
            {
                _log.Warn($"创建 {fileName} 失败", ex);
            }
        }
    }

    /// <summary>
    /// 该版本启动时应传给 <c>assets_root</c> 的目录。
    /// virtual / map_to_resources 索引必须指向展开目录，否则远古版本贴图全丢；
    /// 但只有大部分对象都展开成功时才切过去，否则留在 assets 根更安全。
    /// </summary>
    public static string ResolveAssetsRoot(string gameDirectory, AssetIndexInfo? indexInfo, AssetIndex? index)
    {
        if (indexInfo?.Id is null || index is null || index.Objects.Count == 0)
        {
            return AssetsDirectory(gameDirectory);
        }

        if (!(index.Virtual || index.MapToResources))
        {
            return AssetsDirectory(gameDirectory);
        }

        string virtualDirectory;
        try
        {
            virtualDirectory = SafeVirtualAssetsDirectory(gameDirectory, indexInfo.Id);
        }
        catch (InvalidDataException)
        {
            // 索引 id 不合法时退回 assets 根，避免把越界目录当成资源根。
            return AssetsDirectory(gameDirectory);
        }

        if (!Directory.Exists(virtualDirectory))
        {
            return AssetsDirectory(gameDirectory);
        }

        // 虚拟目录里的文件名是"原始资源名"，不是哈希。
        var present = index.Objects.Keys.Count(name =>
            File.Exists(Path.Combine(virtualDirectory, name.Replace('/', Path.DirectorySeparatorChar))));

        return present * 10 >= index.Objects.Count ? virtualDirectory : AssetsDirectory(gameDirectory);
    }

    // ------------------------------------------------------------------ 计划构建

    private DownloadRequest? BuildClientJarRequest(string gameDirectory, GameVersionJson version)
    {
        var jarId = VersionRepository.ResolveJarId(version);
        if (string.IsNullOrWhiteSpace(jarId))
        {
            return null;
        }

        // jar 字段同样来自远端 JSON，必须确认落盘位置没有越出 versions 目录。
        string target;
        try
        {
            target = EnsureInside(
                VersionRepository.VersionsDirectory(gameDirectory),
                VersionRepository.VersionJarPath(gameDirectory, jarId));
        }
        catch (InvalidDataException ex)
        {
            _log.Warn($"客户端 jar 的版本 id 不合法，已跳过下载：{jarId}", ex);
            return null;
        }

        if (version.Downloads?.Client is { Url.Length: > 0 } client)
        {
            return new DownloadRequest
            {
                Name = $"versions/{jarId}/{jarId}.jar",
                Candidates = _resolver.Resolve(client.Url!, DownloadUrlKind.Client, jarId),
                TargetPath = target,
                Checker = FileChecker.Of(client.Sha1, client.Size),
            };
        }

        // 极少数版本没有 downloads.client：只有镜像提供 versions/<id>/<id>.jar 这种短路径。
        _log.Warn($"版本 {version.Id} 缺少 downloads.client，改走镜像短路径");
        return new DownloadRequest
        {
            Name = $"versions/{jarId}/{jarId}.jar",
            Candidates = new[]
            {
                new DownloadCandidate(
                    $"{_resolver.MirrorBase}/versions/{Uri.EscapeDataString(jarId)}/{Uri.EscapeDataString(jarId)}.jar",
                    "BMCLAPI(版本)"),
            },
            TargetPath = target,
            Checker = FileChecker.ExistenceOnly,
        };
    }

    private DownloadRequest? BuildLibraryRequest(string gameDirectory, Library library, out string? classifier)
    {
        classifier = library.IsNativeLibrary ? library.ResolveNativeClassifier() : null;

        if (library.IsNativeLibrary && classifier is null)
        {
            // 该库在当前平台没有对应分类器（例如某平台的专属库），跳过。
            _log.Trace($"库 {library.Name} 在当前平台没有可用的 natives 分类器，已跳过");
            return null;
        }

        var info = library.GetDownloadInfo(classifier);
        var artifactPath = string.IsNullOrWhiteSpace(info?.Path)
            ? library.ArtifactPath(classifier)
            : info!.Path!;

        if (artifactPath is null)
        {
            _log.Warn($"库 {library.Name} 无法解析下载地址，已跳过");
            return null;
        }

        // 有些制品只存在于本地：Forge 安装器内嵌的 universal jar、处理器生成的补丁 jar。
        // 它们没有远端 URL（或 URL 为空），已经落盘时不应该再拼一个必然 404 的地址。
        if (string.IsNullOrWhiteSpace(info?.Url))
        {
            var existing = LibraryFilePath(gameDirectory, artifactPath);
            if (File.Exists(existing))
            {
                _log.Trace($"库 {library.Name} 已在本地，跳过下载");
                return null;
            }
        }

        var url = library.ResolveDownloadUrl(classifier);
        if (url is null)
        {
            _log.Warn($"库 {library.Name} 无法解析下载地址，已跳过");
            return null;
        }

        var kind = IsModLoaderLibrary(library) ? DownloadUrlKind.ModLoaderLibrary : DownloadUrlKind.Library;

        return new DownloadRequest
        {
            Name = artifactPath,
            Candidates = _resolver.Resolve(url, kind),
            TargetPath = LibraryFilePath(gameDirectory, artifactPath),
            Checker = MakeChecker(info?.Sha1, info?.Size ?? -1, true),
        };
    }

    private DownloadRequest? BuildLogConfigRequest(string gameDirectory, GameVersionJson version, InstallOptions options)
    {
        var file = version.Logging?.Client?.File;
        if (file?.Url is null || string.IsNullOrWhiteSpace(file.Id))
        {
            return null;
        }

        string target;
        try
        {
            target = EnsureInside(
                LogConfigDirectory(gameDirectory),
                Path.Combine(LogConfigDirectory(gameDirectory), file.Id!));
        }
        catch (InvalidDataException ex)
        {
            _log.Warn($"日志配置文件名不合法，已跳过：{file.Id}", ex);
            return null;
        }

        return new DownloadRequest
        {
            Name = $"assets/log_configs/{file.Id}",
            Candidates = _resolver.Resolve(file.Url, DownloadUrlKind.Metadata),
            TargetPath = target,
            Checker = MakeChecker(file.Sha1, file.Size, options.VerifyExisting),
            Optional = true,
        };
    }

    private List<DownloadRequest> BuildAssetRequests(string gameDirectory, AssetIndexInfo indexInfo, InstallOptions options)
    {
        var index = TryLoadIndex(gameDirectory, indexInfo);
        if (index is null)
        {
            _log.Warn($"无法读取资源索引，跳过资源下载：{indexInfo.Id}");
            return new List<DownloadRequest>();
        }

        var requests = new List<DownloadRequest>(index.Objects.Count);
        foreach (var (name, obj) in index.Objects)
        {
            string target;
            try
            {
                target = SafeAssetsObjectPath(gameDirectory, obj.Hash);
            }
            catch (InvalidDataException ex)
            {
                _log.Warn($"资源对象 hash 不合法，已跳过：{name}", ex);
                continue;
            }

            requests.Add(new DownloadRequest
            {
                Name = name,
                Candidates = _resolver.Resolve(
                    $"https://resources.download.minecraft.net/{obj.RelativePath}",
                    DownloadUrlKind.Asset),
                TargetPath = target,
                Checker = MakeChecker(obj.Hash, obj.Size, options.VerifyExisting),
            });
        }

        return requests;
    }

    // ------------------------------------------------------------------ 其它

    /// <summary>把 index 的 <c>assets</c> 名 + 已知哈希兜底成可下载的索引信息。</summary>
    private AssetIndexInfo? ResolveAssetIndexInfo(GameVersionJson version)
    {
        var assetIndex = version.AssetIndex;
        if (assetIndex?.Url is { Length: > 0 })
        {
            assetIndex.Id ??= version.Assets ?? "legacy";
            return assetIndex;
        }

        var assetsId = version.Assets;
        if (string.IsNullOrWhiteSpace(assetsId))
        {
            return null;
        }

        if (!LegacyAssetIndexHashes.TryGetValue(assetsId!, out var hash))
        {
            _log.Warn($"版本 {version.Id} 的资源索引名 {assetsId} 未知，且没有 assetIndex 字段");
            return null;
        }

        return new AssetIndexInfo
        {
            Id = assetsId,
            Url = $"{LegacyIndexBaseUrl}{hash}/{assetsId}.json",
        };
    }

    /// <summary>读取已下载的资源索引文件。</summary>
    public AssetIndex? TryLoadAssetIndex(string gameDirectory, AssetIndexInfo indexInfo) => TryLoadIndex(gameDirectory, indexInfo);

    private AssetIndex? TryLoadIndex(string gameDirectory, AssetIndexInfo indexInfo)
    {
        if (indexInfo.Id is null)
        {
            return null;
        }

        string path;
        try
        {
            path = SafeAssetsIndexPath(gameDirectory, indexInfo.Id);
        }
        catch (InvalidDataException ex)
        {
            _log.Warn($"资源索引 id 不合法，无法读取索引：{indexInfo.Id}", ex);
            return null;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonUtil.DeserializeFromFile<AssetIndex>(path);
        }
        catch (Exception ex)
        {
            _log.Warn($"资源索引解析失败：{path}", ex);
            return null;
        }
    }

    private void ExtractNatives(string gameDirectory, string versionId, IReadOnlyList<NativePlan> plans)
    {
        var nativesDirectory = VersionRepository.NativesDirectory(gameDirectory, versionId);
        PathUtil.EnsureDirectory(nativesDirectory);

        foreach (var plan in plans)
        {
            if (!File.Exists(plan.JarPath))
            {
                _log.Warn($"natives 源文件缺失，跳过解压：{plan.JarPath}");
                continue;
            }

            try
            {
                ArchiveUtil.ExtractZip(plan.JarPath, nativesDirectory, plan.Library.ExtractExclusions, overwrite: true);
                _log.Trace($"已解压 natives：{plan.Library.Name}");
            }
            catch (Exception ex)
            {
                _log.Warn($"natives 解压失败：{plan.Library.Name}", ex);
            }
        }
    }

    /// <summary>
    /// 把 objects 展开成"原始文件名"目录（virtual / map_to_resources）。
    /// 返回实际复制的文件数。
    /// </summary>
    public int MaterializeLegacyAssets(string gameDirectory, AssetIndexInfo indexInfo, AssetIndex index)
    {
        // 与官方启动器一致：virtual 与 map_to_resources 都会在 assets/virtual/<id> 下再铺一份；
        // 两者都为假时不得凭空创建目录。
        string? virtualDirectory = null;
        if ((index.Virtual || index.MapToResources) && indexInfo.Id is not null)
        {
            try
            {
                virtualDirectory = SafeVirtualAssetsDirectory(gameDirectory, indexInfo.Id);
            }
            catch (InvalidDataException ex)
            {
                _log.Warn($"资源索引 id 不合法，已跳过 virtual 展开：{indexInfo.Id}", ex);
            }
        }

        var resourcesDirectory = index.MapToResources ? ResourcesDirectory(gameDirectory) : null;

        var copied = 0;

        foreach (var (name, obj) in index.Objects)
        {
            string source;
            try
            {
                source = SafeAssetsObjectPath(gameDirectory, obj.Hash);
            }
            catch (InvalidDataException ex)
            {
                _log.Warn($"资源对象 hash 不合法，已跳过：{name}", ex);
                continue;
            }

            if (!File.Exists(source))
            {
                continue;
            }

            string? virtualTarget = null;
            string? resourcesTarget = null;
            try
            {
                if (virtualDirectory is not null)
                {
                    virtualTarget = CombineUnder(virtualDirectory, name);
                }

                if (resourcesDirectory is not null)
                {
                    resourcesTarget = CombineUnder(resourcesDirectory, name);
                }
            }
            catch (InvalidDataException ex)
            {
                _log.Warn($"资源名不合法，已跳过：{name}", ex);
                continue;
            }

            if (virtualTarget is not null && CopyIfMissing(source, virtualTarget))
            {
                copied++;
            }

            if (resourcesTarget is not null && CopyIfMissing(source, resourcesTarget))
            {
                copied++;
            }
        }

        _log.Info($"已展开遗留资源：{index.Objects.Count} 个对象，复制 {copied} 个文件");
        return copied;
    }

    private static string CombineUnder(string root, string assetName) =>
        EnsureInside(root, Path.Combine(root, assetName.Replace('/', Path.DirectorySeparatorChar)));

    private bool CopyIfMissing(string source, string target)
    {
        try
        {
            if (File.Exists(target))
            {
                return false;
            }

            PathUtil.EnsureParentDirectory(target);
            File.Copy(source, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"遗留资源复制失败：{target}", ex);
            return false;
        }
    }

    private static FileChecker MakeChecker(string? sha1, long size, bool verifyHashes = true) =>
        verifyHashes ? FileChecker.Of(sha1, size) : FileChecker.OfSize(size);

    private static FileChecker MakeChecker(DownloadInfo info, bool verifyHashes) =>
        MakeChecker(info.Sha1, info.Size, verifyHashes);

    private static bool IsModLoaderLibrary(Library library)
    {
        var url = library.Url ?? library.GetDownloadInfo()?.Url ?? string.Empty;
        var name = library.Name ?? string.Empty;

        return url.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase)
               || url.Contains("neoforged", StringComparison.OrdinalIgnoreCase)
               || url.Contains("fabricmc", StringComparison.OrdinalIgnoreCase)
               || url.Contains("quiltmc", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase);
    }

    private static void Report(
        IProgress<InstallProgress>? progress,
        string stage,
        int completed,
        int total,
        long downloadedBytes,
        long totalBytes,
        string? current)
    {
        progress?.Report(new InstallProgress
        {
            Stage = stage,
            Completed = completed,
            Total = total,
            DownloadedBytes = downloadedBytes,
            TotalBytes = totalBytes,
            Current = current,
        });
    }

    private IProgress<DownloadBatchProgress>? Wrap(
        IProgress<InstallProgress>? progress,
        string stage,
        int total)
    {
        if (progress is null)
        {
            return null;
        }

        return new InlineProgress<DownloadBatchProgress>(batch => progress.Report(new InstallProgress
        {
            Stage = stage,
            Completed = batch.Completed + batch.Skipped,
            Total = total,
            DownloadedBytes = batch.DownloadedBytes,
            TotalBytes = batch.TotalBytes,
            Current = batch.Current,
            ActiveFiles = batch.ActiveFiles,
            BytesPerSecond = batch.BytesPerSecond,
        }));
    }

    private sealed class NativePlan
    {
        public required Library Library { get; init; }

        public required string Classifier { get; init; }

        public required string JarPath { get; init; }
    }
}
