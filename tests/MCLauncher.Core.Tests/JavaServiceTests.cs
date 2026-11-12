using MCLauncher.Core.Models;
using MCLauncher.Core.Services;
using MCLauncher.Core.Tests.Support;

namespace MCLauncher.Core.Tests;

public sealed class JavaServiceTests
{
    /// <summary>
    /// 注意 <c>1.8.0_292</c> 的归一结果：旧式写法里的 "1." 前缀会被剥掉，
    /// 后面的 <c>292</c>（update 号）落在补丁位上 —— 这样按 SortKey 排序时 8u292 会排在 8u202 之前，
    /// 正是"同主版本优先选更新版本"想要的效果。
    /// </summary>
    [Theory]
    [InlineData("1.8.0_292", 8, 0, 292, 0)]
    [InlineData("17.0.1", 17, 0, 1, 0)]
    [InlineData("11.0.20", 11, 0, 20, 0)]
    [InlineData("9", 9, 0, 0, 0)]
    [InlineData("21.0.2", 21, 0, 2, 0)]
    [InlineData("25.0.4.1", 25, 0, 4, 1)]
    [InlineData("1.7.0_80", 7, 0, 80, 0)]
    public void 版本号归一为四段(string version, int major, int minor, int patch, int build)
    {
        var (m, n, p, b) = JavaService.NormalizeVersion(version);
        Assert.Equal((major, minor, patch, build), (m, n, p, b));
    }

    [Fact]
    public void 空版本串不崩溃()
    {
        Assert.Equal((0, 0, 0, 0), JavaService.NormalizeVersion(null));
        Assert.Equal((0, 0, 0, 0), JavaService.NormalizeVersion(""));
        Assert.Equal((0, 0, 0, 0), JavaService.NormalizeVersion("不是数字"));
    }

    [Fact]
    public void 探测真实Java()
    {
        var java = FindRealJava();
        if (java is null)
        {
            // 机器上没有 Java 时跳过。项目用的 xunit 2.9.3 没有 Assert.Skip
            // （动态跳过是 v3 的能力），也没有引入跳过相关的扩展包，只能直接返回。
            return;
        }

        var service = new JavaService(new TestLog());
        var info = service.Probe(java);

        Assert.NotNull(info);
        Assert.True(info!.Is64Bit, "本机 Java 应为 64 位");
        Assert.True(info.MajorVersion >= 8, $"版本号解析异常：{info.RawVersion}");
        Assert.Equal(java, info.Path);
    }

    [Fact]
    public void 探测不存在的路径返回空()
    {
        using var temp = new TempDirectory();
        var service = new JavaService(new TestLog());
        Assert.Null(service.Probe(temp.File("not-a-java.exe")));
    }

    [Fact]
    public void 探测一个非Java的可执行文件不会挂()
    {
        using var temp = new TempDirectory();
        var fake = temp.File("fake-java.exe");
        File.WriteAllText(fake, "这不是可执行程序");

        var service = new JavaService(new TestLog());
        Assert.Null(service.Probe(fake));
    }

    [Fact]
    public void 枚举候选路径不会抛异常且包含JAVA_HOME()
    {
        var service = new JavaService(new TestLog());
        var candidates = service.EnumerateCandidates();

        Assert.NotNull(candidates);

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var expected = Path.Combine(javaHome!, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            Assert.Contains(candidates, c => string.Equals(c, expected, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void 扫描出的运行时按版本降序且跳过32位()
    {
        var service = new JavaService(new TestLog());
        var detected = service.Detect();

        foreach (var java in detected)
        {
            Assert.True(java.Is64Bit);
        }

        for (var i = 1; i < detected.Count; i++)
        {
            Assert.True(detected[i - 1].SortKey.CompareTo(detected[i].SortKey) >= 0, "结果应按版本降序");
        }

        // 二次调用命中缓存，返回同一个实例。
        Assert.Same(detected, service.Detect());
    }

    [Fact]
    public void 按目标主版本挑最合适的Java()
    {
        var service = new JavaService(new TestLog());
        var pool = new[]
        {
            Fake("8", 8, 0, 292),
            Fake("11", 11, 0, 20),
            Fake("17", 17, 0, 9),
            Fake("17.0.1", 17, 0, 1),
            Fake("21", 21, 0, 2),
        };

        // 有完全匹配 → 同主版本里取补丁最高的
        Assert.Equal("17", service.SelectBest(17, pool)!.RawVersion);

        // 没有完全匹配 → 取不小于要求的最小主版本
        Assert.Equal("11", service.SelectBest(9, pool)!.RawVersion);

        // 要求高于所有可用 → 返回 null，由上层下载对应版本
        Assert.Null(service.SelectBest(25, pool));

        // 要求低于所有可用 → 取最小的可用版本
        Assert.Equal("8", service.SelectBest(7, pool)!.RawVersion);

        Assert.Null(service.SelectBest(17, Array.Empty<JavaInfo>()));
    }

    [Fact]
    public void Java8只接受主版本完全匹配()
    {
        var service = new JavaService(new TestLog());

        // 只有 11/17/21 时不拿新版去跑旧版 Forge，交给上层下载 Java 8
        var newerOnly = new[]
        {
            Fake("11", 11, 0, 20),
            Fake("17", 17, 0, 9),
            Fake("21", 21, 0, 2),
        };
        Assert.Null(service.SelectBest(8, newerOnly));

        // 有 8 时取补丁最高的
        var pool = new[]
        {
            Fake("8.0.202", 8, 0, 202),
            Fake("8.0.292", 8, 0, 292),
            Fake("11", 11, 0, 20),
        };
        Assert.Equal("8.0.292", service.SelectBest(8, pool)!.RawVersion);
    }

    [Fact]
    public void 能否运行的判定()
    {
        var java = Fake("17", 17, 0, 0);
        Assert.True(java.CanRun(8));
        Assert.True(java.CanRun(17));
        Assert.False(java.CanRun(21));
    }

    private static JavaInfo Fake(string raw, int major, int minor, int patch) => new()
    {
        Path = $"C:/fake/{raw}/bin/java.exe",
        RawVersion = raw,
        MajorVersion = major,
        MinorVersion = minor,
        PatchVersion = patch,
        Is64Bit = true,
    };

    /// <summary>从候选里取第一个真实存在且能探测成功的 Java。</summary>
    internal static string? FindRealJava()
    {
        var service = new JavaService(new TestLog());
        foreach (var candidate in service.EnumerateCandidates())
        {
            if (service.Probe(candidate) is not null)
            {
                return candidate;
            }
        }

        return null;
    }
}
