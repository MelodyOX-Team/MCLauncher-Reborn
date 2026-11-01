using System.Net;
using System.Text;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services;
using MCLauncher.Core.Services.Auth;
using MCLauncher.Core.Tests.Support;

namespace MCLauncher.Core.Tests;

public sealed class MicrosoftAuthTests
{
    private static MicrosoftAuthService CreateService(out TestLog log, out ConfigService config, out HttpDownloader downloader, string? clientId = null)
    {
        log = new TestLog();
        var temp = new TempDirectory();
        var store = new JsonConfigStore(temp.Path, log);
        // 单元测试不依赖开发者本机 LocalSecrets 里的真实凭据。
        config = new ConfigService(store, log, includeBuiltInSecrets: false);
        config.Load();

        if (clientId is not null)
        {
            config.Settings.MsClientId = clientId;
        }

        downloader = new HttpDownloader(log, new FakeHttpHandler("{}"));
        return new MicrosoftAuthService(downloader, config, log);
    }

    [Fact]
    public void 未配置客户端ID时给出引导而不是崩溃()
    {
        var service = CreateService(out _, out _, out var downloader);
        using (downloader)
        {
            Assert.False(service.IsConfigured);

            var exception = Assert.Throws<MsAuthException>(() =>
                service.LoginWithDeviceCodeAsync(null).GetAwaiter().GetResult());

            Assert.Equal(MsAuthFailure.NotConfigured, exception.Failure);
            Assert.Contains("客户端 ID", exception.ToUserMessage());
        }
    }

    [Fact]
    public void 登录说明只描述设备码流程不要求用户配置()
    {
        var service = CreateService(out _, out _, out var downloader, clientId: "cid");
        using (downloader)
        {
            var text = service.DescribeSetup();
            Assert.Contains("浏览器", text);
            Assert.Contains("一次性验证码", text);
            Assert.DoesNotContain("Azure", text);
            Assert.DoesNotContain("客户端 ID", text);
        }
    }

    [Fact]
    public void 错误码映射成人话()
    {
        Assert.Contains("封禁", new MsAuthException(MsAuthFailure.AccountBanned, "").ToUserMessage());
        Assert.Contains("xbox.com", new MsAuthException(MsAuthFailure.NoXboxAccount, "").ToUserMessage());
        Assert.Contains("地区", new MsAuthException(MsAuthFailure.RegionBlocked, "").ToUserMessage());
        Assert.Contains("家庭组", new MsAuthException(MsAuthFailure.ChildAccount, "").ToUserMessage());
        Assert.Contains("没有购买", new MsAuthException(MsAuthFailure.NoGameOwnership, "").ToUserMessage());
        Assert.Contains("重新登录", new MsAuthException(MsAuthFailure.MustSignInAgain, "").ToUserMessage());
        Assert.Contains("稍后", new MsAuthException(MsAuthFailure.RateLimited, "").ToUserMessage());
    }

    /// <summary>
    /// 设备码流程的关键中间态：令牌端点在用户还没授权时返回 HTTP 400 + authorization_pending，
    /// 这**不是失败**，必须继续轮询。这里用一个按顺序应答的假处理器把整条链走通。
    /// </summary>
    [Fact]
    public async Task 设备码流程在授权前会持续轮询并最终登录成功()
    {
        var log = new TestLog();
        var temp = new TempDirectory();
        var store = new JsonConfigStore(temp.Path, log);
        var config = new ConfigService(store, log);
        config.Load();
        config.Settings.MsClientId = "client-123";

        var handler = new SequencedHttpHandler(
            // 1. 申请设备码
            (HttpStatusCode.OK, """
            {"device_code":"dev-code","user_code":"ABCD-EFGH",
             "verification_uri":"https://www.microsoft.com/link","expires_in":60,"interval":1,
             "message":"请在浏览器中输入代码"}
            """),
            // 2. 第一次轮询：用户还没授权
            (HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            // 3. 第二次轮询：拿到 MSA 令牌
            (HttpStatusCode.OK, """{"access_token":"msa-token","refresh_token":"refresh-1","expires_in":3600}"""),
            // 4. Xbox Live
            (HttpStatusCode.OK, """{"Token":"xbl-token","DisplayClaims":{"xui":[{"uhs":"user-hash"}]}}"""),
            // 5. XSTS（Minecraft 依赖方）
            (HttpStatusCode.OK, """{"Token":"xsts-token","DisplayClaims":{"xui":[{"uhs":"user-hash"}]}}"""),
            // 6. XSTS（Xbox Live 依赖方，用来取玩家代号 gtg）
            (HttpStatusCode.OK, """{"Token":"xsts-token","DisplayClaims":{"xui":[{"uhs":"user-hash","gtg":"SteveGamer"}]}}"""),
            // 7. Minecraft 登录
            (HttpStatusCode.OK, """{"access_token":"mc-token","expires_in":86400}"""),
            // 8. 游戏所有权
            (HttpStatusCode.OK, """{"items":[{"name":"product_minecraft"}]}"""),
            // 9. 玩家档案
            (HttpStatusCode.OK, """{"id":"5627dd98e6be3c21b8a8e92344183641","name":"Steve"}"""));

        using var downloader = new HttpDownloader(log, handler);
        var service = new MicrosoftAuthService(downloader, config, log);

        MsDeviceCodePrompt? shown = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        var account = await service.LoginWithDeviceCodeAsync(
            new InlineProgress<MsDeviceCodePrompt>(p => shown = p),
            null,
            cts.Token);

        // 短码必须被展示出来；链接只用服务端给的验证页，不能附带 otc 参数
        Assert.NotNull(shown);
        Assert.Equal("ABCD-EFGH", shown!.UserCode);
        Assert.Equal("https://www.microsoft.com/link", shown.VerificationUri);
        Assert.Equal("https://www.microsoft.com/link", shown.LinkUri);
        Assert.DoesNotContain("otc=", shown.LinkUri);

        Assert.Equal(AccountKind.Microsoft, account.Kind);
        Assert.Equal("Steve", account.Name);
        Assert.Equal("5627dd98e6be3c21b8a8e92344183641", account.Uuid);
        Assert.Equal("user-hash", account.Xuid);
        // 玩家代号来自第二次 XSTS（http://xboxlive.com 依赖方）
        Assert.Equal("SteveGamer", account.XboxName);
        Assert.Equal("refresh-1", account.RefreshToken);

        // 第二次轮询必须带上 device_code 授权类型
        var pollBody = handler.Bodies.FirstOrDefault(b => b.Contains("device_code", StringComparison.Ordinal));
        Assert.NotNull(pollBody);
        Assert.Contains("urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", pollBody);

        // 玩家代号必须通过 http://xboxlive.com 依赖方的 XSTS 请求获取
        Assert.Contains(handler.Bodies, b => b.Contains("http://xboxlive.com", StringComparison.Ordinal));
    }

    /// <summary>
    /// RFC 8628 允许服务端直接给出带验证码的完整地址；给了就必须用它，不能自己再拼。
    /// </summary>
    [Fact]
    public async Task 服务端给出完整验证地址时优先使用()
    {
        const string deviceJson = """
        {"device_code":"d","user_code":"ABC",
         "verification_uri":"https://microsoft.com/devicelogin",
         "verification_uri_complete":"https://microsoft.com/devicelogin?code=ABC",
         "expires_in":30,"interval":1}
        """;

        var (service, downloader, _) = CreateWithHandler(
            (HttpStatusCode.OK, deviceJson),
            (HttpStatusCode.BadRequest, """{"error":"expired_token"}"""));

        using (downloader)
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
        {
            MsDeviceCodePrompt? shown = null;

            await Assert.ThrowsAsync<MsAuthException>(() => service.LoginWithDeviceCodeAsync(
                new InlineProgress<MsDeviceCodePrompt>(p => shown = p),
                null,
                cts.Token));

            Assert.NotNull(shown);
            Assert.Equal("https://microsoft.com/devicelogin?code=ABC", shown!.LinkUri);
        }
    }

    private const string DeviceCodeJson = """
    {"device_code":"dev","user_code":"CODE-1","verification_uri":"https://microsoft.com/devicelogin",
     "expires_in":120,"interval":1}
    """;

    private const string MsaTokenJson = """{"access_token":"msa","refresh_token":"rt","expires_in":3600,"scope":"XboxLive.signin offline_access"}""";

    /// <summary>
    /// 如果微软只授予了别的 scope（表现为授权页没勾 Xbox 权限），报错必须直接点出来，
    /// 而不是让用户去怀疑"客户端 ID 没进白名单"这种不存在的前提。
    /// </summary>
    [Fact]
    public async Task 未授予XboxLive签名权限时直接点明原因()
    {
        var (service, downloader, _) = CreateWithHandler(
            (HttpStatusCode.OK, DeviceCodeJson),
            (HttpStatusCode.OK, """{"access_token":"msa","expires_in":3600,"scope":"openid profile offline_access"}"""));

        using (downloader)
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
        {
            var ex = await Assert.ThrowsAsync<MsAuthException>(() =>
                service.LoginWithDeviceCodeAsync(null, null, cts.Token));

            Assert.Contains("XboxLive.signin", ex.Message);
            Assert.Contains("API 权限", ex.Message);
        }
    }

    /// <summary>
    /// 真实踩到的坑：Mojang 对没进允许清单的新应用注册回 403 +
    /// "Invalid app registration, see https://aka.ms/AppRegInfo"。
    /// 这条必须被单独识别 —— 它既不是账号问题也不是配置写错，而是要提交白名单审核。
    /// </summary>
    [Fact]
    public async Task Mojang拒绝未获许可的应用注册时给出白名单审核指引()
    {
        const string XboxTokenJson = """{"Token":"xbl","DisplayClaims":{"xui":[{"uhs":"user-hash"}]}}""";

        var (service, downloader, _) = CreateWithHandler(
            (HttpStatusCode.OK, DeviceCodeJson),
            (HttpStatusCode.OK, MsaTokenJson),
            (HttpStatusCode.OK, XboxTokenJson),
            (HttpStatusCode.OK, XboxTokenJson),
            (HttpStatusCode.Forbidden,
                """{"path":"/authentication/login_with_xbox","errorMessage":"Invalid app registration, see https://aka.ms/AppRegInfo for more information"}"""));

        using (downloader)
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
        {
            var ex = await Assert.ThrowsAsync<MsAuthException>(() =>
                service.LoginWithDeviceCodeAsync(null, null, cts.Token));

            Assert.Equal(MsAuthFailure.AppNotApproved, ex.Failure);

            var message = ex.ToUserMessage();
            Assert.Contains("mce-reviewappid", message);
            Assert.Contains("允许清单", message);
            // 必须明确告诉用户：既有启动器免申请不代表新应用免申请
            Assert.Contains("历史保留", message);
        }
    }

    private static (MicrosoftAuthService Service, HttpDownloader Downloader, TestLog Log) CreateWithHandler(params (HttpStatusCode, string)[] responses)
    {
        var log = new TestLog();
        var temp = new TempDirectory();
        var store = new JsonConfigStore(temp.Path, log);
        var config = new ConfigService(store, log);
        config.Load();
        config.Settings.MsClientId = "client-123";

        var downloader = new HttpDownloader(log, new SequencedHttpHandler(responses));
        return (new MicrosoftAuthService(downloader, config, log), downloader, log);
    }

    /// <summary>
    /// Xbox Live 这一步同样会回 XErr —— 以前我把它当通用 HTTP 失败处理，
    /// 结果"没有 Xbox 档案"被糊成"微软拒绝了这次请求"，完全没法排查。这里锁死映射。
    /// </summary>
    [Fact]
    public async Task XboxLive的XErr被映射成具体原因()
    {
        var (service, downloader, _) = CreateWithHandler(
            (HttpStatusCode.OK, DeviceCodeJson),
            (HttpStatusCode.OK, MsaTokenJson),
            (HttpStatusCode.Unauthorized, """{"Identity":"0","XErr":2148916233,"Message":"无 Xbox 档案"}"""),
            (HttpStatusCode.Unauthorized, """{"Identity":"0","XErr":2148916233,"Message":"无 Xbox 档案"}"""));

        using (downloader)
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
        {
            var ex = await Assert.ThrowsAsync<MsAuthException>(() =>
                service.LoginWithDeviceCodeAsync(null, null, cts.Token));

            Assert.Equal(MsAuthFailure.NoXboxAccount, ex.Failure);
            Assert.Contains("xbox.com", ex.ToUserMessage());
        }
    }

    /// <summary>无法归类的 401/403 必须把"哪个接口 + 服务端原话"带出来，否则无从排查。</summary>
    [Fact]
    public async Task 通用401的报错带上接口与服务端原话()
    {
        var (service, downloader, _) = CreateWithHandler(
            (HttpStatusCode.OK, DeviceCodeJson),
            (HttpStatusCode.OK, MsaTokenJson),
            (HttpStatusCode.Unauthorized, """{"error":"invalid_token","error_description":"ticket rejected"}"""));

        using (downloader)
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
        {
            var ex = await Assert.ThrowsAsync<MsAuthException>(() =>
                service.LoginWithDeviceCodeAsync(null, null, cts.Token));

            Assert.Contains("user.auth.xboxlive.com", ex.Message);
            Assert.Contains("invalid_token", ex.Message);
            Assert.Contains("401", ex.Message);
        }
    }

    private sealed class SequencedHttpHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public SequencedHttpHandler(params (HttpStatusCode Status, string Body)[] responses) =>
            _responses = new Queue<(HttpStatusCode, string)>(responses);

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            // 最后一条会被反复复用，方便模拟"一直 pending"这类场景。
            var response = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();

            return new HttpResponseMessage(response.Status)
            {
                RequestMessage = request,
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public void 微软账号的类型与协议字段()
    {
        var account = new Account
        {
            Kind = AccountKind.Microsoft,
            Name = "Steve",
            Uuid = "5627dd98e6be3c21b8a8e92344183641",
            Xuid = "123",
            ClientId = "cid",
            ExpiresAt = DateTimeOffset.Now.AddHours(1),
        };

        Assert.Equal("msa", account.UserType);
        Assert.False(account.IsExpired(TimeSpan.FromMinutes(5)));
    }
}
