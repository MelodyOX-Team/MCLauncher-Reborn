using System.Text.Json;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services;
using MCLauncher.Core.Services.Auth;
using MCLauncher.Core.Tests.Support;

namespace MCLauncher.Core.Tests;

/// <summary>
/// 凭据落盘、认证服务器地址与版本元数据路径的安全约束。
/// 这些行为一旦回退，用户令牌就会重新以明文出现在磁盘上。
/// </summary>
public sealed class SecurityHardeningTests
{
    [Fact]
    public void 账号令牌落盘会加密且可还原()
    {
        if (!OperatingSystem.IsWindows())
        {
            // DPAPI 只在 Windows 上可用，非 Windows 时跳过。
            // 项目用的 xunit 2.9.3 没有 Assert.Skip（动态跳过是 v3 的能力），
            // 也没有引入跳过相关的扩展包，只能直接返回。
            return;
        }

        var account = new Account
        {
            Kind = AccountKind.Microsoft,
            Name = "Steve",
            Uuid = "u",
            AccessToken = "plain-access-token",
            RefreshToken = "plain-refresh-token",
            ClientToken = "plain-client-token",
        };

        var json = JsonSerializer.Serialize(account);

        Assert.DoesNotContain("plain-access-token", json);
        Assert.DoesNotContain("plain-refresh-token", json);
        Assert.DoesNotContain("plain-client-token", json);
        Assert.Contains("enc:v1:", json);

        var round = JsonSerializer.Deserialize<Account>(json)!;
        Assert.Equal("plain-access-token", round.AccessToken);
        Assert.Equal("plain-refresh-token", round.RefreshToken);
        Assert.Equal("plain-client-token", round.ClientToken);
    }

    [Fact]
    public void 旧版明文令牌仍可读取()
    {
        const string json = """
        {"Kind":0,"Name":"P","Uuid":"u","AccessToken":"legacy-token"}
        """;

        var account = JsonSerializer.Deserialize<Account>(json)!;

        Assert.Equal("legacy-token", account.AccessToken);
    }

    [Theory]
    [InlineData("https://example.com/api", true)]
    [InlineData("http://localhost:8080/api", true)]
    [InlineData("http://127.0.0.1/api", true)]
    [InlineData("http://example.com/api", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("not a url", false)]
    public void 认证服务器地址必须安全(string url, bool expected)
    {
        Assert.Equal(expected, AuthlibAuthService.IsSecureServerUrl(url, out _));
    }

    [Fact]
    public void 依赖库路径越界会被拒绝()
    {
        var root = Path.Combine(Path.GetTempPath(), "mclr-libs-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<InvalidDataException>(() => GameInstaller.LibraryFilePath(root, "../../evil.jar"));
        Assert.Throws<InvalidDataException>(() => GameInstaller.LibraryFilePath(root, "com/../../../evil.jar"));

        var normal = GameInstaller.LibraryFilePath(root, "com/example/a.jar");
        Assert.StartsWith(
            Path.GetFullPath(Path.Combine(root, "libraries")),
            normal,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 非本机的HTTP认证服务器会被拒绝()
    {
        var log = new TestLog();
        using var downloader = new HttpDownloader(log, new FakeHttpHandler("{}"));
        var service = new AuthlibAuthService(downloader, log);

        await Assert.ThrowsAsync<AuthlibAuthException>(
            () => service.FetchMetadataAsync("http://example.com/api"));
    }
}
