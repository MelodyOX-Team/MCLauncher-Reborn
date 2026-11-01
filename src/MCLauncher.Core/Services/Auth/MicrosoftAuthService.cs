using System.Net;
using System.Text.Json;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services.Auth;

/// <summary>
/// 微软正版登录（设备码流程）。
/// <para>链路（缺一不可，顺序也不能乱）：</para>
/// <list type="number">
/// <item>申请设备码 → 用户在浏览器确认授权 → 轮询拿到 MSA 令牌（无需回调地址）</item>
/// <item>MSA 令牌 → Xbox Live 令牌（user.auth.xboxlive.com）</item>
/// <item>Xbox Live → XSTS 令牌（xsts.auth.xboxlive.com，拿 uhs）</item>
/// <item>XSTS → Minecraft 访问令牌（login_with_xbox）</item>
/// <item>查游戏所有权（entitlements）</item>
/// <item>取玩家档案（profile：id + name）</item>
/// </list>
/// <para>
/// 错误码语义必须映射成人话，否则用户只会看到一串数字：
/// XErr <c>2148916227/33/35/38</c> 分别是封禁 / 无 Xbox 档案 / 地区不支持 / 未成年，
/// 以及 <c>ACCOUNT_SUSPENDED</c>、404 无游戏、<c>must sign in again</c>、429/403/503。
/// </para>
/// </summary>
public sealed class MicrosoftAuthService
{
    private const long XErrBanned = 2148916227;
    private const long XErrNoXboxAccount = 2148916233;
    private const long XErrRegionBlocked = 2148916235;
    private const long XErrChildAccount = 2148916238;

    private readonly HttpDownloader _downloader;
    private readonly ConfigService _config;
    private readonly ILogger _log;

    public MicrosoftAuthService(HttpDownloader downloader, ConfigService config, ILogger log)
    {
        _downloader = downloader;
        _config = config;
        _log = log;
    }

    /// <summary>当前配置的客户端 ID（可能为 null）。</summary>
    public string? ClientId => _config.ResolveMsClientId();

    /// <summary>没有客户端 ID 时登录不可用，界面应引导而不是崩溃。</summary>
    public bool IsConfigured => ClientId is not null;

    /// <summary>用刷新令牌重新走一遍换链（令牌过期时调用，用户无感）。</summary>
    public async Task<Account> RefreshAsync(
        Account account,
        IProgress<MsAuthProgress>? progress = null,
        CancellationToken ct = default)
    {
        var clientId = account.ClientId ?? ClientId
                       ?? throw new MsAuthException(MsAuthFailure.NotConfigured, "尚未配置微软客户端 ID");

        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            throw new MsAuthException(MsAuthFailure.MustSignInAgain, "没有刷新令牌，需要重新登录");
        }

        Report(progress, "刷新令牌", "正在用刷新令牌续期");

        var token = await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["refresh_token"] = account.RefreshToken!,
                ["grant_type"] = "refresh_token",
                ["scope"] = MsAuthEndpoints.Scope,
            },
            ct).ConfigureAwait(false);

        // 有些刷新响应不回传新的 refresh_token，这时必须保留旧的。
        token.RefreshToken ??= account.RefreshToken;

        return await WalkChainAsync(token, clientId, progress, ct).ConfigureAwait(false);
    }

    /// <summary>令牌将在 <paramref name="margin"/> 内过期时自动刷新，否则原样返回。</summary>
    public async Task<Account> EnsureFreshAsync(
        Account account,
        TimeSpan margin,
        IProgress<MsAuthProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!account.IsMicrosoft || !account.IsExpired(margin))
        {
            return account;
        }

        _log.Info("微软令牌即将过期，自动刷新");
        return await RefreshAsync(account, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 给界面用的登录流程说明：设备码流程，用户不需要配置任何东西。
    /// </summary>
    public string DescribeSetup()
        => "启动器会打开浏览器并显示一次性验证码，确认授权后自动完成登录。";

    /// <summary>
    /// 设备码登录：**完全不需要注册任何回调地址**。
    /// <para>
    /// 启动器申请一个短码，并打开微软返回的验证页（<c>https://www.microsoft.com/link</c>）。
    /// 该短码由界面展示、可一键复制，用户在页面上粘贴后登录并确认授权。
    /// 注意不要拼接 <c>?otc=</c>：微软会把这条路径当作第一方授权处理，普通应用会被拒绝。
    /// </para>
    /// <para>
    /// 启动器按微软给的间隔轮询令牌端点，用户授权完就自动拿到令牌。
    /// </para>
    /// </summary>
    public async Task<Account> LoginWithDeviceCodeAsync(
        IProgress<MsDeviceCodePrompt>? prompt,
        IProgress<MsAuthProgress>? progress = null,
        CancellationToken ct = default)
    {
        var clientId = ClientId
                       ?? throw new MsAuthException(MsAuthFailure.NotConfigured, "尚未配置微软客户端 ID");

        Report(progress, "申请设备码", "正在向微软申请一次性登录码");

        string json;
        try
        {
            json = await _downloader.PostFormAsync(
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["scope"] = MsAuthEndpoints.Scope,
                },
                MsAuthEndpoints.DeviceCodeUrl,
                ct).ConfigureAwait(false);
        }
        catch (HttpApiException ex)
        {
            throw MapApiException(ex);
        }

        var device = JsonUtil.Deserialize<MsDeviceCodeResponse>(json)
                     ?? throw new MsAuthException(MsAuthFailure.Unknown, "设备码响应无法解析");

        if (string.IsNullOrWhiteSpace(device.DeviceCode) || string.IsNullOrWhiteSpace(device.UserCode))
        {
            throw new MsAuthException(MsAuthFailure.Unknown, "微软没有返回可用的设备码");
        }

        var verificationUri = string.IsNullOrWhiteSpace(device.VerificationUri)
            ? MsAuthEndpoints.VerificationPage
            : device.VerificationUri!;

        // 有完整地址就用完整地址；否则用服务端给的验证页根地址。
        // 不能自己拼 ?otc=，那条路径会被微软按第一方应用处理并拒绝普通应用。
        var linkUri = !string.IsNullOrWhiteSpace(device.VerificationUriComplete)
            ? device.VerificationUriComplete!
            : verificationUri;

        prompt?.Report(new MsDeviceCodePrompt
        {
            UserCode = device.UserCode!,
            VerificationUri = verificationUri,
            LinkUri = linkUri,
            Message = device.Message,
            ExpiresInSeconds = device.ExpiresIn,
        });

        _log.Info($"设备码登录已发起，验证地址 {linkUri}");
        Report(progress, "等待授权", "浏览器已打开登录页，请粘贴验证码并确认授权");

        var interval = TimeSpan.FromSeconds(Math.Max(3, device.Interval));
        var deadline = DateTimeOffset.Now.AddSeconds(device.ExpiresIn > 0 ? device.ExpiresIn : 900);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTimeOffset.Now > deadline)
            {
                throw new MsAuthException(MsAuthFailure.Timeout, "设备码已过期，请重新发起登录。");
            }

            // 必须遵守微软给的最小间隔，否则会被判定为 slow_down 并延长间隔。
            await Task.Delay(interval, ct).ConfigureAwait(false);

            try
            {
                var tokenJson = await _downloader.PostFormAsync(
                    new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["device_code"] = device.DeviceCode!,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    },
                    MsAuthEndpoints.TokenUrl,
                    ct).ConfigureAwait(false);

                var token = JsonUtil.Deserialize<MsTokenResponse>(tokenJson)
                            ?? throw new MsAuthException(MsAuthFailure.Unknown, "令牌响应无法解析");

                return await WalkChainAsync(token, clientId, progress, ct).ConfigureAwait(false);
            }
            catch (HttpApiException ex)
            {
                // 未授权时令牌端点的响应是 HTTP 400 + {"error":"authorization_pending"}，
                // 这是正常中间态，绝不能当成失败。
                switch (ParseOAuthError(ex.ResponseBody))
                {
                    case "authorization_pending":
                        continue;

                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        continue;

                    case "expired_token":
                        throw new MsAuthException(MsAuthFailure.Timeout, "设备码已过期，请重新发起登录。", ex);

                    case "authorization_declined":
                    case "access_denied":
                        throw new MsAuthException(MsAuthFailure.UserCancelled, "你已拒绝授权。", ex);

                    default:
                        throw MapApiException(ex);
                }
            }
        }
    }

    /// <summary>从令牌端点的 400 响应体里取出 OAuth 错误码。</summary>
    private static string? ParseOAuthError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonUtil.Deserialize<MsTokenResponse>(body)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ 换链

    private async Task<Account> WalkChainAsync(
        MsTokenResponse token,
        string clientId,
        IProgress<MsAuthProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new MsAuthException(MsAuthFailure.Unknown, "微软没有返回访问令牌");
        }

        // 先把实际授予的 scope 记下来：Xbox 换链失败时，这条日志能立刻区分
        // "权限没被同意" 与 "令牌前缀/账号问题"，避免在 Azure 配置上瞎猜。
        _log.Info($"MSA 令牌已获取，实际授予的 scope：{token.Scope ?? "(未返回 scope 字段)"}");

        if (token.Scope is { Length: > 0 } granted
            && !granted.Contains("XboxLive.signin", StringComparison.OrdinalIgnoreCase))
        {
            throw new MsAuthException(
                MsAuthFailure.Unknown,
                $"微软只授予了 scope「{granted}」，其中没有 XboxLive.signin，"
                + "因此无法换取 Xbox 令牌。请在 Azure 应用注册的「API 权限」里确认可以请求 Xbox Live 权限，"
                + "并在「支持的账户类型」里包含个人 Microsoft 账户。");
        }

        Report(progress, "验证 Xbox Live", "正在用微软令牌换取 Xbox Live 令牌");
        var xblToken = await AuthenticateXboxLiveAsync(token.AccessToken!, ct).ConfigureAwait(false);

        Report(progress, "获取 XSTS 令牌", "正在向 Xbox 安全令牌服务申请令牌");
        var (xstsToken, uhs, gamertag) = await AuthorizeXstsAsync(xblToken, ct).ConfigureAwait(false);

        // Minecraft 的 XSTS 响应通常只有 uhs，玩家代号需要再查一次 Xbox Live 依赖方。
        gamertag ??= await FetchGamertagAsync(xblToken, ct).ConfigureAwait(false);

        Report(progress, "登录 Minecraft", "正在用 Xbox 令牌换取 Minecraft 访问令牌");
        var mcToken = await LoginWithXboxAsync(uhs, xstsToken, ct).ConfigureAwait(false);

        Report(progress, "检查游戏所有权", "正在确认该账号是否拥有 Minecraft");
        await EnsureGameOwnershipAsync(mcToken, ct).ConfigureAwait(false);

        Report(progress, "读取玩家档案", "正在获取玩家名与 UUID");
        var profile = await GetProfileAsync(mcToken, ct).ConfigureAwait(false);

        var account = new Account
        {
            Kind = AccountKind.Microsoft,
            Name = profile.Name ?? "Player",
            Uuid = profile.Id ?? string.Empty,
            AccessToken = mcToken,
            Xuid = uhs,
            XboxName = gamertag,
            RefreshToken = token.RefreshToken,
            ExpiresAt = DateTimeOffset.Now.AddSeconds(Math.Max(60, token.ExpiresIn)),
            ClientId = clientId,
        };

        _log.Info($"微软登录成功：{account.Name}（{account.Uuid}）");
        Report(progress, "完成", $"已登录 {account.Name}");
        return account;
    }

    private async Task<MsTokenResponse> RequestTokenAsync(
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        string json;
        try
        {
            json = await _downloader.PostFormAsync(form, MsAuthEndpoints.TokenUrl, ct).ConfigureAwait(false);
        }
        catch (HttpApiException ex)
        {
            throw MapApiException(ex);
        }

        var token = JsonUtil.Deserialize<MsTokenResponse>(json)
                    ?? throw new MsAuthException(MsAuthFailure.Unknown, "令牌响应无法解析");

        if (!string.IsNullOrWhiteSpace(token.Error))
        {
            _log.Warn($"令牌端点返回错误：{token.Error} {token.ErrorDescription}");
            throw new MsAuthException(
                token.Error switch
                {
                    "invalid_grant" or "interaction_required" => MsAuthFailure.MustSignInAgain,
                    "authorization_pending" => MsAuthFailure.Timeout,
                    _ => MsAuthFailure.Unknown,
                },
                token.ErrorDescription ?? token.Error!);
        }

        return token;
    }

    /// <summary>
    /// MSA 令牌 → Xbox Live 令牌。
    /// <para>
    /// <c>RpsTicket</c> 的前缀取决令牌来源：微软账号中心 v2 端点拿到的是 JWT，用 <c>d=</c>；
    /// Live SDK 那套拿到的是紧凑令牌，得用 <c>t=</c>。两种在国内都有人踩过，
    /// 所以第一种失败就换前缀再试一次；两次都失败时抛**第一次**的错误，保留最原始的错误语义。
    /// </para>
    /// </summary>
    private async Task<string> AuthenticateXboxLiveAsync(string msaToken, CancellationToken ct)
    {
        try
        {
            return await RequestXboxLiveTokenAsync("d=" + msaToken, ct).ConfigureAwait(false);
        }
        catch (MsAuthException first)
        {
            _log.Warn($"Xbox Live 认证用 d= 前缀失败（{first.Failure}），改用 t= 前缀重试一次");

            try
            {
                return await RequestXboxLiveTokenAsync("t=" + msaToken, ct).ConfigureAwait(false);
            }
            catch (MsAuthException)
            {
                throw first;
            }
        }
    }

    private async Task<string> RequestXboxLiveTokenAsync(string rpsTicket, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = rpsTicket,
            },
            RelyingParty = MsAuthEndpoints.XboxLiveRelyingParty,
            TokenType = "JWT",
        });

        try
        {
            var json = await _downloader
                .PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", payload, null, ct)
                .ConfigureAwait(false);

            var response = JsonUtil.Deserialize<XblTokenResponse>(json);
            return response?.Token
                   ?? throw new MsAuthException(MsAuthFailure.Unknown, "Xbox Live 没有返回令牌");
        }
        catch (HttpApiException ex)
        {
            // 这一步同样会回 XErr（比如封禁 / 无 Xbox 档案），不能当成通用的 HTTP 失败处理。
            throw MapXboxFailure(ex);
        }
    }

    private async Task<(string Token, string Uhs, string? Gamertag)> AuthorizeXstsAsync(string xblToken, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken },
            },
            RelyingParty = MsAuthEndpoints.XstsRelyingParty,
            TokenType = "JWT",
        });

        try
        {
            var json = await _downloader
                .PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", payload, null, ct)
                .ConfigureAwait(false);

            var response = JsonUtil.Deserialize<XblTokenResponse>(json)
                           ?? throw new MsAuthException(MsAuthFailure.Unknown, "XSTS 响应无法解析");

            var claims = response.DisplayClaims?.Xui?.FirstOrDefault();
            var uhs = claims?.GetValueOrDefault("uhs");
            var gamertag = claims?.GetValueOrDefault("gtg");
            if (string.IsNullOrWhiteSpace(response.Token) || string.IsNullOrWhiteSpace(uhs))
            {
                throw new MsAuthException(MsAuthFailure.Unknown, "XSTS 没有返回令牌或 uhs");
            }

            return (response.Token!, uhs!, gamertag);
        }
        catch (HttpApiException ex)
        {
            // XSTS 的失败几乎都靠响应体里的 XErr 才能分辨原因。
            throw MapXboxFailure(ex);
        }
    }

    /// <summary>
    /// 取 Xbox 玩家代号。以 <c>http://xboxlive.com</c> 为 relying party 调一次 XSTS，
    /// 声明里才会带 <c>gtg</c>（Minecraft 的 relying party 只返回 uhs）。
    /// 只是展示用，失败不影响登录。
    /// </summary>
    private async Task<string?> FetchGamertagAsync(string xblToken, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[] { xblToken },
                },
                RelyingParty = MsAuthEndpoints.XboxProfileRelyingParty,
                TokenType = "JWT",
            });

            var json = await _downloader
                .PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", payload, null, ct)
                .ConfigureAwait(false);

            var response = JsonUtil.Deserialize<XblTokenResponse>(json);
            var gamertag = response?.DisplayClaims?.Xui?.FirstOrDefault()?.GetValueOrDefault("gtg");
            return string.IsNullOrWhiteSpace(gamertag) ? null : gamertag;
        }
        catch (Exception ex)
        {
            _log.Warn("获取 Xbox 玩家代号失败（不影响登录）", ex);
            return null;
        }
    }

    private async Task<string> LoginWithXboxAsync(string uhs, string xstsToken, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            identityToken = $"XBL3.0 x={uhs};{xstsToken}",
        });

        try
        {
            var json = await _downloader
                .PostJsonAsync(MsAuthEndpoints.MinecraftLoginUrl, payload, null, ct)
                .ConfigureAwait(false);

            var response = JsonUtil.Deserialize<MinecraftTokenResponse>(json);
            return response?.AccessToken
                   ?? throw new MsAuthException(MsAuthFailure.Unknown, "Minecraft 登录没有返回访问令牌");
        }
        catch (HttpApiException ex)
        {
            throw MapApiException(ex);
        }
    }

    private async Task EnsureGameOwnershipAsync(string mcToken, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + mcToken };

        string json;
        try
        {
            json = await _downloader
                .RequestStringAsync(MsAuthEndpoints.MinecraftEntitlementsUrl, HttpMethod.Get, null, null, headers, ct)
                .ConfigureAwait(false);
        }
        catch (HttpApiException ex)
        {
            throw MapApiException(ex);
        }

        var entitlements = JsonUtil.Deserialize<MinecraftEntitlements>(json);
        var owned = entitlements?.Items?.Any(item =>
            item.Name is not null
            && (item.Name.StartsWith("product_minecraft", StringComparison.OrdinalIgnoreCase)
                || item.Name.StartsWith("game_minecraft", StringComparison.OrdinalIgnoreCase))) ?? false;

        if (!owned)
        {
            throw new MsAuthException(MsAuthFailure.NoGameOwnership, "该账号没有 Minecraft：Java 版的授权");
        }
    }

    private async Task<MinecraftProfile> GetProfileAsync(string mcToken, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + mcToken };

        string json;
        try
        {
            json = await _downloader
                .RequestStringAsync(MsAuthEndpoints.MinecraftProfileUrl, HttpMethod.Get, null, null, headers, ct)
                .ConfigureAwait(false);
        }
        catch (HttpApiException ex)
        {
            // 404 = 这个账号没有 Minecraft 档案。
            throw ex.StatusCode == HttpStatusCode.NotFound
                ? new MsAuthException(MsAuthFailure.NoGameOwnership, "该账号没有 Minecraft 档案", ex)
                : MapApiException(ex);
        }

        var profile = JsonUtil.Deserialize<MinecraftProfile>(json);
        if (profile?.Id is null || profile.Name is null)
        {
            throw new MsAuthException(MsAuthFailure.Unknown, "玩家档案缺少 id 或 name");
        }

        return profile;
    }

    // ------------------------------------------------------------------ 错误映射

    /// <summary>
    /// Xbox Live / XSTS 两处共用的失败映射：这两步都会在响应体里回 <c>XErr</c>，
    /// 拿到 XErr 才能分辨"封禁 / 没有 Xbox 档案 / 地区不支持 / 未成年"。
    /// </summary>
    private MsAuthException MapXboxFailure(HttpApiException ex)
    {
        var body = ex.ResponseBody;
        if (!string.IsNullOrWhiteSpace(body))
        {
            XblTokenResponse? parsed = null;
            try
            {
                parsed = JsonUtil.Deserialize<XblTokenResponse>(body);
            }
            catch (JsonException)
            {
                // 不是预期的 JSON，退回通用映射。
            }

            if (parsed is not null && parsed.XErr != 0)
            {
                return parsed.XErr switch
                {
                    XErrBanned => new MsAuthException(MsAuthFailure.AccountBanned, "Xbox 账号被封禁", ex),
                    XErrNoXboxAccount => new MsAuthException(MsAuthFailure.NoXboxAccount, "该账号没有 Xbox 档案", ex),
                    XErrRegionBlocked => new MsAuthException(MsAuthFailure.RegionBlocked, "所在地区不支持 Xbox Live", ex),
                    XErrChildAccount => new MsAuthException(MsAuthFailure.ChildAccount, "未成年账号需要家长授权", ex),
                    _ => new MsAuthException(
                        MsAuthFailure.Unknown,
                        $"Xbox 验证失败：XErr={parsed.XErr} {parsed.Message}（{ex.Url}）",
                        ex),
                };
            }
        }

        return MapApiException(ex);
    }

    private static MsAuthException MapApiException(HttpApiException ex)
    {
        // Mojang 对未获许可的应用注册会回 403 + errorMessage "Invalid app registration"。
        // 这不是配置写错，而是"新应用必须先进允许清单"，必须单独识别出来，
        // 否则会被下面的通用 401/403 分支糊成一句没用的"微软拒绝了这次请求"。
        if (ex.ResponseBody is { } rawBody
            && rawBody.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
        {
            return new MsAuthException(MsAuthFailure.AppNotApproved, ex.Message, ex);
        }
        switch (ex.StatusCode)
        {
            case HttpStatusCode.TooManyRequests:
                return new MsAuthException(MsAuthFailure.RateLimited, "请求过于频繁", ex);

            case HttpStatusCode.ServiceUnavailable:
            case HttpStatusCode.BadGateway:
            case HttpStatusCode.GatewayTimeout:
                return new MsAuthException(MsAuthFailure.ServiceUnavailable, "微软服务暂时不可用", ex);

            case HttpStatusCode.Unauthorized when ex.ResponseBody?.Contains("must sign in again", StringComparison.OrdinalIgnoreCase) == true:
                return new MsAuthException(MsAuthFailure.MustSignInAgain, "令牌已失效", ex);
        }

        var body = ex.ResponseBody ?? string.Empty;

        if (body.Contains("must sign in again", StringComparison.OrdinalIgnoreCase))
        {
            return new MsAuthException(MsAuthFailure.MustSignInAgain, "令牌已失效", ex);
        }

        if (body.Contains("ACCOUNT_SUSPENDED", StringComparison.OrdinalIgnoreCase))
        {
            return new MsAuthException(MsAuthFailure.AccountSuspended, "账号已停用", ex);
        }

        return ex.StatusCode switch
        {
            // 报错里必须带上"哪个接口 + 服务端原话"，否则 401/403 根本无从排查。
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new MsAuthException(
                    MsAuthFailure.Unknown,
                    $"微软拒绝了这次请求（HTTP {(int)ex.StatusCode}）｜接口：{ex.Url}｜服务端返回：{Summarize(ex.ResponseBody)}",
                    ex),
            _ => new MsAuthException(
                MsAuthFailure.Unknown,
                $"请求失败（HTTP {(int)ex.StatusCode}）｜接口：{ex.Url}｜服务端返回：{Summarize(ex.ResponseBody)}",
                ex),
        };
    }

    /// <summary>把服务端原始响应截断成可展示的一小段 —— 排查 401/403 全靠它。</summary>
    private static string Summarize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "（无响应体）";
        }

        var text = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private static void Report(IProgress<MsAuthProgress>? progress, string stage, string? detail = null) =>
        progress?.Report(new MsAuthProgress { Stage = stage, Detail = detail });
}
