using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services;
using MCLauncher.Core.Services.Auth;

namespace MCLauncher.Core.Tests.Support;

/// <summary>
/// 真实网络的完整服务栈，供需要访问外网的用例共用。
/// 默认把下载源设为官方：本机访问 BMCLAPI 会超时，
/// 否则每个文件都要先撞一次镜像，装几百个资源会拖很久。
/// </summary>
internal sealed class LiveTestStack : IDisposable
{
    public required TestLog Log { get; init; }

    public required HttpDownloader Downloader { get; init; }

    public required DownloadSourceResolver Resolver { get; init; }

    public required DownloadBatch Batch { get; init; }

    public required VersionRepository Repository { get; init; }

    public required GameInstaller Installer { get; init; }

    public required VersionManifestService Manifests { get; init; }

    public required JavaService Java { get; init; }

    public required ZuluJavaService ZuluJava { get; init; }

    public required LaunchArgumentBuilder ArgumentBuilder { get; init; }

    public required GameLauncher Launcher { get; init; }

    public required GameLaunchService LaunchService { get; init; }

    public required ModLoaderService ModLoaders { get; init; }

    public required InstanceService Instances { get; init; }

    public static LiveTestStack Create(string cacheDirectory, bool preferMirror = false)
    {
        var log = new TestLog();
        var downloader = new HttpDownloader(log);
        var preference = preferMirror
            ? DownloadSourcePreference.Mirror
            : DownloadSourcePreference.Official;

        var resolver = new DownloadSourceResolver(log)
        {
            VersionListSource = preference,
            FileSource = preference,
        };
        var batch = new DownloadBatch(downloader, log);
        var repository = new VersionRepository(new VersionResolver(log), log);
        var installer = new GameInstaller(downloader, resolver, batch, repository, log);
        var javaRoot = Path.Combine(cacheDirectory, "java");
        var java = new JavaService(log, javaRoot);
        var zuluJava = new ZuluJavaService(downloader, batch, java, log, javaRoot);
        var forgeInstaller = new ForgeInstallerService(downloader, resolver, installer, java, log);
        var argumentBuilder = new LaunchArgumentBuilder(log);
        var launcher = new GameLauncher(log);
        var authlibAuth = new AuthlibAuthService(downloader, log);
        var authlibInjector = new AuthlibInjectorService(downloader, log);
        var manifests = new VersionManifestService(downloader, resolver, log, cacheDirectory);

        var config = new ConfigService(
            new JsonConfigStore(Path.Combine(cacheDirectory, "config"), log),
            log,
            includeBuiltInSecrets: false);
        config.Load();

        return new LiveTestStack
        {
            Log = log,
            Downloader = downloader,
            Resolver = resolver,
            Batch = batch,
            Repository = repository,
            Installer = installer,
            Manifests = manifests,
            Java = java,
            ZuluJava = zuluJava,
            ArgumentBuilder = argumentBuilder,
            Launcher = launcher,
            LaunchService = new GameLaunchService(
                repository, installer, java, argumentBuilder, launcher, authlibAuth, authlibInjector, log),
            ModLoaders = new ModLoaderService(downloader, repository, installer, forgeInstaller, log),
            Instances = new InstanceService(repository, config, manifests, log),
        };
    }

    public async Task<GameVersionJson> FetchVersionAsync(string id, CancellationToken ct)
    {
        var entry = await Manifests.FindAsync(id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"版本清单里找不到 {id}");

        return await Manifests.GetVersionJsonAsync(entry, ct).ConfigureAwait(false);
    }

    /// <summary>构造一个已安装版本的启动上下文。</summary>
    public LaunchContext CreateLaunchContext(
        string gameRoot,
        string versionId,
        Account account,
        LaunchOptions options,
        string javaPath)
    {
        var version = Repository.Resolve(gameRoot, versionId);
        var jarId = VersionRepository.ResolveJarId(version);
        var indexInfo = version.AssetIndex;
        var index = indexInfo is null ? null : Installer.TryLoadAssetIndex(gameRoot, indexInfo);

        return new LaunchContext
        {
            Version = version,
            Account = account,
            Options = options,
            JavaPath = javaPath,
            VersionId = versionId,
            GameDirectory = gameRoot,
            AssetsRoot = GameInstaller.ResolveAssetsRoot(gameRoot, indexInfo, index),
            NativesDirectory = VersionRepository.NativesDirectory(gameRoot, versionId),
            LibrariesDirectory = GameInstaller.LibrariesDirectory(gameRoot),
            ClientJarPath = VersionRepository.VersionJarPath(gameRoot, jarId),
            AssetsIndexName = indexInfo?.Id,
        };
    }

    public void Dispose() => Downloader.Dispose();
}
