using System.Diagnostics;
using MCLauncher.Core.Models;
using MCLauncher.Core.Services;
using MCLauncher.Core.Tests.Support;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Tests;

/// <summary>
/// M5 的硬验证：拿真实版本 JSON 生成的 JVM 参数交给真实 java 执行。
/// 做法是构建出完整命令行后，把主类换成 <c>-version</c> —— Java 会解析完所有选项再打印版本退出，
/// 任何非法选项都会让退出码非 0，因此这能真实验证参数拼装与路径解析是否正确，
/// 又不必真的把游戏窗口拉起来（本环境没有 GPU）。
/// </summary>
[Trait("Category", "Live")]
public sealed class GameLaunchLiveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(12);

    [Fact]
    public async Task 真实版本的JVM参数能被java接受且classpath全部存在()
    {
        using var temp = new TempDirectory();
        using var stack = LiveTestStack.Create(Path.Combine(temp.Path, ".cache"));
        using var cts = new CancellationTokenSource(Timeout);

        var java = stack.Java.Detect(ct: cts.Token).FirstOrDefault(j => j.MajorVersion >= 8);
        if (java is null)
        {
            // 机器上没有可用的 64 位 Java 时跳过。项目用的 xunit 2.9.3 没有 Assert.Skip
            // （动态跳过是 v3 的能力），也没有引入跳过相关的扩展包，只能直接返回。
            return;
        }

        const string versionId = "1.13.2";
        var version = await stack.FetchVersionAsync(versionId, cts.Token);
        var report = await stack.Installer.InstallVersionAsync(
            temp.Path,
            version,
            new InstallOptions { DownloadAssets = false, MaxParallel = 16 },
            null,
            cts.Token);

        Assert.True(report.Success, GameInstallerLiveTests.Describe(report));

        var account = Account.CreateOffline("Tester");
        var command = stack.ArgumentBuilder.Build(stack.CreateLaunchContext(
            temp.Path,
            versionId,
            account,
            new LaunchOptions { MaxMemoryMb = 1024, MinMemoryMb = 256 },
            java.Path));

        // ---- 参数里不能再有未替换的占位符 ----
        foreach (var argument in command.ToArgumentList())
        {
            Assert.DoesNotContain("${", argument);
        }

        // ---- classpath 每一项都必须真实存在 ----
        var classpathEntries = command.Classpath.Split(Path.PathSeparator);
        Assert.True(classpathEntries.Length > 5, $"classpath 项太少：{classpathEntries.Length}");
        foreach (var entry in classpathEntries)
        {
            Assert.True(File.Exists(entry), $"classpath 项不存在：{entry}");
        }

        // ---- 把真实 JVM 参数交给 java：合法性由真实 JVM 判定 ----
        var startInfo = new ProcessStartInfo(java.Path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in command.JvmArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("-version");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdout = await process!.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        var combined = stdout + Environment.NewLine + stderr;

        Assert.DoesNotContain("Unrecognized option", combined);
        Assert.DoesNotContain("Could not create the Java Virtual Machine", combined);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("version", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 真实版本的游戏参数含全部必需项且资产目录解析正确()
    {
        using var temp = new TempDirectory();
        using var stack = LiveTestStack.Create(Path.Combine(temp.Path, ".cache"));
        using var cts = new CancellationTokenSource(Timeout);

        const string versionId = "1.13.2";
        var version = await stack.FetchVersionAsync(versionId, cts.Token);
        await stack.Installer.InstallVersionAsync(
            temp.Path,
            version,
            new InstallOptions { DownloadAssets = false, MaxParallel = 16 },
            null,
            cts.Token);

        var options = new LaunchOptions { Width = 1280, Height = 720 };
        var context = stack.CreateLaunchContext(
            temp.Path,
            versionId,
            Account.CreateOffline("Tester"),
            options,
            "java");

        var command = stack.ArgumentBuilder.Build(context);
        var game = command.GameArguments;

        // 结构化参数版本必须把账号信息、目录、资源索引全带上。
        Assert.Contains("--username", game);
        Assert.Contains("--accessToken", game);
        Assert.Contains("--uuid", game);
        Assert.Contains("--assetIndex", game);
        Assert.Equal("1280", game[IndexOf(game, "--width") + 1]);
        Assert.Equal("720", game[IndexOf(game, "--height") + 1]);

        Assert.Equal("net.minecraft.client.main.Main", command.MainClass);
        Assert.Equal(GameInstaller.AssetsDirectory(temp.Path), context.AssetsRoot);
        Assert.Equal(GameInstaller.LibrariesDirectory(temp.Path), context.LibrariesDirectory);
        Assert.True(context.NativesDirectory.Contains("natives-", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(VersionRepository.VersionJsonPath(temp.Path, "不存在")));
    }

    [Fact]
    public async Task 未安装的版本启动时给出明确错误()
    {
        using var temp = new TempDirectory();
        using var stack = LiveTestStack.Create(Path.Combine(temp.Path, ".cache"));

        var request = new GameLaunchRequest
        {
            GameRoot = temp.Path,
            VersionId = "未安装的版本",
            Account = Account.CreateOffline("Tester"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stack.LaunchService.LaunchAsync(request, new MCLauncher.Core.Logging.LaunchLogBuffer()));
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
