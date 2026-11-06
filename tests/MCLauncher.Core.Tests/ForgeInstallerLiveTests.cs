using MCLauncher.Core.Models;
using MCLauncher.Core.Services;
using MCLauncher.Core.Tests.Support;

namespace MCLauncher.Core.Tests;

/// <summary>
/// Forge 处理器流程的真实验证：下载官方安装器、执行处理器、直装自洽实例。
/// 会访问外网并下载数十 MB，日常按名称过滤时跳过。
/// </summary>
[Trait("Category", "Live")]
public sealed class ForgeInstallerLiveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task 直装Forge只生成一个实例目录()
    {
        using var temp = new TempDirectory();
        using var stack = LiveTestStack.Create(Path.Combine(temp.Path, ".cache"), preferMirror: true);
        using var cts = new CancellationTokenSource(Timeout);

        const string gameVersion = "1.20.1";
        const string loaderVersion = "47.2.0";
        const string instanceId = "Forge47.2.0";

        var baseVersion = await stack.FetchVersionAsync(gameVersion, cts.Token);

        var loader = await stack.ModLoaders.GetForgeLikeVersionAsync(
            ModLoaderKind.Forge,
            temp.Path,
            gameVersion,
            loaderVersion,
            baseVersion,
            VersionRepository.VersionJarPath(temp.Path, instanceId),
            // 资源对象有 600 MB，本用例只验证处理器与磁盘布局，资源下载由 GameInstallerLiveTests 覆盖。
            new InstallOptions { DownloadAssets = false },
            progress: null,
            cts.Token);

        Assert.Equal($"1.20.1-forge-{loaderVersion}", loader.Id);
        Assert.Equal(gameVersion, loader.InheritsFrom);

        var report = await stack.Installer.InstallInstanceAsync(
            temp.Path,
            instanceId,
            baseVersion,
            loader,
            new InstallOptions { DownloadAssets = false },
            progress: null,
            cts.Token);

        Assert.True(report.Success, GameInstallerLiveTests.Describe(report));

        // ---- 磁盘布局：只有一个实例目录，且不再生成 .mclauncher 元数据目录 ----
        var versions = Directory.GetDirectories(VersionRepository.VersionsDirectory(temp.Path));
        Assert.Single(versions);
        Assert.Equal(instanceId, Path.GetFileName(versions[0]));
        Assert.False(Directory.Exists(Path.Combine(versions[0], ".mclauncher")));

        Assert.True(File.Exists(VersionRepository.VersionJsonPath(temp.Path, instanceId)));
        Assert.True(File.Exists(VersionRepository.VersionJarPath(temp.Path, instanceId)));

        // ---- 处理器产物 ----
        var patched = GameInstaller.LibraryFilePath(
            temp.Path, $"net/minecraftforge/forge/1.20.1-{loaderVersion}/forge-1.20.1-{loaderVersion}-client.jar");
        Assert.True(File.Exists(patched), "补丁后的客户端 jar 缺失");

        var srg = Directory.GetFiles(
            GameInstaller.LibrariesDirectory(temp.Path), "*-srg.jar", SearchOption.AllDirectories);
        Assert.NotEmpty(srg);
    }
}
