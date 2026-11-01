using System.Net;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services.Auth;

namespace MCLauncher.Core.Services;

/// <summary>账号可用性检查结果。</summary>
public enum AccountHealth
{
    /// <summary>可用（离线账号直接算可用）。</summary>
    Usable,

    /// <summary>服务器明确拒绝：令牌失效、账号被封等，需要重新登录或更换账号。</summary>
    Unavailable,

    /// <summary>网络或服务端临时故障，无法判断；不要因此禁用账号。</summary>
    Unknown,
}

/// <summary>一次账号检查的结果。</summary>
public sealed class AccountCheckResult
{
    public required Account Account { get; init; }

    public required AccountHealth Health { get; init; }

    /// <summary>可直接展示的说明。</summary>
    public string? Message { get; init; }
}

/// <summary>当前账号已失效，需要重新登录才能继续。</summary>
public sealed class AccountUnavailableException : Exception
{
    public AccountUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// 账号可用性检查：外置登录走 authlib 的 validate/refresh，微软账号用刷新令牌重走换链。
/// <para>
/// 只有服务器**明确拒绝**才判为失效并置灰；网络错误、限流、服务临时不可用都只算「未知」，
/// 避免断网一次就把用户账号永久禁用。
/// </para>
/// </summary>
public sealed class AccountHealthService
{
    private readonly AccountService _accounts;
    private readonly MicrosoftAuthService _msAuth;
    private readonly AuthlibAuthService _authlib;
    private readonly ConfigService _config;
    private readonly ILogger _log;

    public AccountHealthService(
        AccountService accounts,
        MicrosoftAuthService msAuth,
        AuthlibAuthService authlib,
        ConfigService config,
        ILogger log)
    {
        _accounts = accounts;
        _msAuth = msAuth;
        _authlib = authlib;
        _config = config;
        _log = log;
    }

    /// <summary>检查一个账号；结果会写回设置（刷新后的令牌、失效标记）。</summary>
    public async Task<AccountCheckResult> CheckAsync(Account account, CancellationToken ct = default)
    {
        if (account.Kind == AccountKind.Offline)
        {
            return new AccountCheckResult
            {
                Account = account,
                Health = AccountHealth.Usable,
                Message = "离线账号不需要检查",
            };
        }

        try
        {
            var refreshed = await RefreshAsync(account, ct).ConfigureAwait(false);

            _accounts.Update(refreshed);
            _accounts.SetDisabled(refreshed, false);
            _config.Save();

            return new AccountCheckResult
            {
                Account = refreshed,
                Health = AccountHealth.Usable,
                Message = "可用",
            };
        }
        catch (AccountUnavailableException ex)
        {
            _accounts.SetDisabled(account, true, ex.Message);
            _config.Save();

            return new AccountCheckResult
            {
                Account = account,
                Health = AccountHealth.Unavailable,
                Message = ex.Message,
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"检查账号失败（{account.Name}）", ex);
            return new AccountCheckResult
            {
                Account = account,
                Health = AccountHealth.Unknown,
                Message = "暂时无法检查：" + ex.Message,
            };
        }
    }

    /// <summary>
    /// 启动前准备账号：微软账号临近过期时刷新，外置登录校验/续期。
    /// 明确失效时置灰并抛出 <see cref="AccountUnavailableException"/>。
    /// </summary>
    public async Task<Account> PrepareForLaunchAsync(Account account, CancellationToken ct = default)
    {
        if (account.Kind == AccountKind.Offline)
        {
            return account;
        }

        if (account.IsDisabled)
        {
            throw new AccountUnavailableException(DescribeDisabled(account));
        }

        try
        {
            var prepared = account.IsMicrosoft
                ? await _msAuth.EnsureFreshAsync(account, TimeSpan.FromMinutes(5), null, ct).ConfigureAwait(false)
                : await _authlib.EnsureValidAsync(account, ct).ConfigureAwait(false);

            _accounts.Update(prepared);
            _config.Save();
            return prepared;
        }
        catch (MsAuthException ex) when (IsPermanent(ex.Failure))
        {
            _accounts.SetDisabled(account, true, ex.Message);
            _config.Save();
            throw new AccountUnavailableException(DescribeDisabled(account), ex);
        }
        catch (AuthlibAuthException ex) when (IsPermanent(ex))
        {
            _accounts.SetDisabled(account, true, ex.Message);
            _config.Save();
            throw new AccountUnavailableException(DescribeDisabled(account), ex);
        }
    }

    private async Task<Account> RefreshAsync(Account account, CancellationToken ct)
    {
        if (account.IsMicrosoft)
        {
            if (string.IsNullOrWhiteSpace(account.RefreshToken))
            {
                throw new AccountUnavailableException("没有刷新令牌，需要重新登录");
            }

            try
            {
                return await _msAuth.RefreshAsync(account, null, ct).ConfigureAwait(false);
            }
            catch (MsAuthException ex) when (IsPermanent(ex.Failure))
            {
                throw new AccountUnavailableException(ex.Message, ex);
            }
        }

        try
        {
            return await _authlib.EnsureValidAsync(account, ct).ConfigureAwait(false);
        }
        catch (AuthlibAuthException ex) when (IsPermanent(ex))
        {
            throw new AccountUnavailableException(ex.Message, ex);
        }
    }

    private static string DescribeDisabled(Account account) =>
        $"账号「{account.Name}」已失效（{account.DisabledReason ?? "需要重新登录"}），"
        + "请到「账号」页重新登录后再启动。";

    /// <summary>微软侧的永久性失败：重新尝试也不会恢复，只能重新登录或换账号。</summary>
    private static bool IsPermanent(MsAuthFailure failure) => failure is
        MsAuthFailure.MustSignInAgain
        or MsAuthFailure.AccountBanned
        or MsAuthFailure.AccountSuspended
        or MsAuthFailure.NoGameOwnership
        or MsAuthFailure.NoXboxAccount
        or MsAuthFailure.RegionBlocked
        or MsAuthFailure.ChildAccount;

    /// <summary>
    /// 外置登录的永久性失败：4xx 是服务器明确拒绝；没有 HTTP 内层异常说明返回体本身就不可用。
    /// 5xx / 超时之类的服务端问题按「暂时无法检查」处理。
    /// </summary>
    private static bool IsPermanent(AuthlibAuthException ex) =>
        ex.InnerException is not HttpApiException api
        || api.StatusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden;
}
