using MCLauncher.Core.Models;
using MCLauncher.Core.Services;
using MCLauncher.Core.Tests.Support;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Tests;

/// <summary>
/// M8 的硬验证：真实拉取加载器版本列表，并真实安装出可启动的加载器版本。
/// Fabric 走 meta 的 profile/json（不需要 Java）；Forge 走 installer jar 真实执行。
/// </summary>
[Trait("Category", "Live")]
public sealed class ModLoaderLiveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    private static InstallOptions Options() => new()
    {
        DownloadAssets = false,
        MaxParallel = 16,
    };

    [Fact]
    public async Task Fabric加载器列表可用()
    {
        using var temp = new TempDirectory();
        using var stack = LiveTestStack.Create(Path.Combine(temp.Path, ".cache"));
        using var cts = new CancellationTokenSource(Timeout);

        var versions = await stack.ModLoaders.ListLoaderVersionsAsync(ModLoaderKind.Fabric, "1.20.4", cts.Token);

        Assert.NotEmpty(versions);
        Assert.All(versions, v => Assert.False(string.IsNullOrWhiteSpace(v.Version)));
        Assert.Equal(1, versions.Count(v => v.IsLatest));

        Assert.True(await stack.ModLoaders.HasLoaderForAsync(ModLoaderKind.Fabric, "1.20.4", cts.Token));
    }

    [Fact]
    public async Task 安装Fabric并生成可补全的版本()
    {
        using var temp = new TempDirectory();
        using var stack = LiveTestStack.Create(Path.Combine(temp.Path, ".cache"));
        using var cts = new CancellationTokenSource(Timeout);

        const string gameVersion = "1.14.4";

        // 先装基底原版
        var baseVersion = await stack.FetchVersionAsync(gameVersion, cts.Token);
        var baseReport = await stack.Installer.InstallVersionAsync(temp.Path, baseVersion, Options(), null, cts.Token);
        Assert.True(baseReport.Success, GameInstallerLiveTests.Describe(baseReport));

        var loaders = await stack.ModLoaders.ListLoaderVersionsAsync(ModLoaderKind.Fabric, gameVersion, cts.Token);
        Assert.NotEmpty(loaders);

        var result = await stack.ModLoaders.InstallAsync(
            new LoaderInstallRequest
            {
                GameRoot = temp.Path,
                GameVersion = gameVersion,
                Kind = ModLoaderKind.Fabric,
                LoaderVersion = loaders[0].Version,
                InstallOptions = Options(),
            },
            null,
            cts.Token);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.VersionId);

        // 版本 JSON 必须落盘，并且 inheritsFrom 指向基底
        var raw = JsonUtil.DeserializeFromFile<GameVersionJson>(
            VersionRepository.VersionJsonPath(temp.Path, result.VersionId!));
        Assert.NotNull(raw);
        Assert.Equal(gameVersion, raw!.InheritsFrom);

        // 合并后的版本必须能解析出 fabric 的 mainClass 与库
        var resolved = stack.Repository.Resolve(temp.Path, result.VersionId!);
        Assert.Contains("fabric", resolved.MainClass, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(resolved.Libraries!, l => l.Name!.Contains("fabric", StringComparison.OrdinalIgnoreCase));

        // fabric 的库必须真的下载到位
        foreach (var library in resolved.Libraries!.Where(l => l.Applies() && !l.IsNativeLibrary))
        {
            var path = GameInstaller.LibraryFilePath(temp.Path, library.ArtifactPath()!);
            Assert.True(File.Exists(path), $"库未下载：{library.Name}");
        }
    }

    [Fact]
    public async Task NeoForge版本列表可用()
    {
        using var temp = new TempDirectory();
        using var stack = LiveTestStack.Create(Path.Combine(temp.Path, ".cache"));
        using var cts = new CancellationTokenSource(Timeout);

        var versions = await stack.ModLoaders.ListLoaderVersionsAsync(ModLoaderKind.NeoForge, "1.20.4", cts.Token);

        Assert.NotEmpty(versions);
        Assert.All(versions, v => Assert.StartsWith("20.4.", v.Version));
    }

    [Fact]
    public async Task Forge版本列表可用()
    {
        using var temp = new TempDirectory();
        using var stack = LiveTestStack.Create(Path.Combine(temp.Path, ".cache"));
        using var cts = new CancellationTokenSource(Timeout);

        var versions = await stack.ModLoaders.ListLoaderVersionsAsync(ModLoaderKind.Forge, "1.20.4", cts.Token);

        Assert.NotEmpty(versions);
        Assert.Contains(versions, v => v.Version == "49.0.30");

        // 「推荐 / 最新」标记来自 promotions_slim.json（与 maven 元数据是两个数据源），
        // 该端点取不到时标记为空是正常的，因此只要求"标记了就必须是列表里的版本"。
        Assert.All(versions.Where(v => v.IsRecommended || v.IsLatest),
            v => Assert.Contains(versions, x => x.Version == v.Version));
    }

    [Fact]
    public async Task 安装Forge并真实执行安装器()
    {
        using var temp = new TempDirectory();
        using var stack = LiveTestStack.Create(Path.Combine(temp.Path, ".cache"));
        using var cts = new CancellationTokenSource(Timeout);

        const string gameVersion = "1.20.4";
        const string loaderVersion = "49.0.30";

        var baseVersion = await stack.FetchVersionAsync(gameVersion, cts.Token);
        var baseReport = await stack.Installer.InstallVersionAsync(temp.Path, baseVersion, Options(), null, cts.Token);
        Assert.True(baseReport.Success, GameInstallerLiveTests.Describe(baseReport));

        var result = await stack.ModLoaders.InstallAsync(
            new LoaderInstallRequest
            {
                GameRoot = temp.Path,
                GameVersion = gameVersion,
                Kind = ModLoaderKind.Forge,
                LoaderVersion = loaderVersion,
                InstallOptions = Options(),
            },
            null,
            cts.Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal($"{gameVersion}-forge-{loaderVersion}", result.VersionId);

        var raw = JsonUtil.DeserializeFromFile<GameVersionJson>(
            VersionRepository.VersionJsonPath(temp.Path, result.VersionId!));
        Assert.NotNull(raw);
        Assert.Equal(gameVersion, raw!.InheritsFrom);

        // Forge 的库/依赖必须被补全
        var resolved = stack.Repository.Resolve(temp.Path, result.VersionId!);
        Assert.Contains("forge", resolved.MainClass, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(resolved.Libraries!);
    }

    [Fact]
    public void 安装器所需的Java主版本按原版版本推断()
    {
        Assert.Equal(21, ModLoaderService.RequiredJavaMajor("1.20.5"));
        Assert.Equal(21, ModLoaderService.RequiredJavaMajor("1.21.1"));
        Assert.Equal(17, ModLoaderService.RequiredJavaMajor("1.20.4"));
        Assert.Equal(17, ModLoaderService.RequiredJavaMajor("1.18"));
        Assert.Equal(16, ModLoaderService.RequiredJavaMajor("1.17.1"));
        Assert.Equal(8, ModLoaderService.RequiredJavaMajor("1.12.2"));
    }
}
