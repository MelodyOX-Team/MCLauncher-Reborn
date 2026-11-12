using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;

namespace MCLauncher.Core.Services.Auth;

/// <summary>外置登录（authlib-injector / Yggdrasil）失败。消息可直接展示给用户。</summary>
public sealed class AuthlibAuthException : Exception
{
    public AuthlibAuthException(string message, Exception? inner = null, string? errorCode = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// 服务器返回的 Yggdrasil 错误码（如 <c>ForbiddenOperationException</c>）。
    /// 调用方需要据此区分「账号确实被拒绝」和「这次响应读不懂」，不能只看异常消息。
    /// </summary>
    public string? ErrorCode { get; }
}

/// <summary>认证服务器根路径返回的元数据。</summary>
public sealed class AuthServerMetadata
{
    public required string RawJson { get; init; }

    /// <summary>服务器名（meta.serverName）。</summary>
    public string? Name { get; init; }

    /// <summary>主页地址。</summary>
    public string? Homepage { get; init; }

    /// <summary>注册页地址。</summary>
    public string? Register { get; init; }
}

/// <summary>
/// 外置登录：按 authlib-injector 的 Yggdrasil 协议登录、校验与刷新令牌。
/// 只使用标准的 <c>/authserver/*</c> 与 <c>/sessionserver/*</c> 端点。
/// </summary>
public sealed class AuthlibAuthService
{
    private readonly HttpDownloader _downloader;
    private readonly ILogger _log;

    public AuthlibAuthService(HttpDownloader downloader, ILogger log)
    {
        _downloader = downloader;
        _log = log;
    }

    /// <summary>
    /// 认证服务器地址是否安全：必须 HTTPS；<c>localhost</c> / 回环地址允许 HTTP，便于本机调试。
    /// 密码与令牌走明文 HTTP 等于直接公开，不能只在界面上提醒。
    /// </summary>
    public static bool IsSecureServerUrl(string? url, out Uri? uri)
    {
        uri = null;

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttps)
        {
            uri = parsed;
            return true;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp
            && (parsed.IsLoopback
                || parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed;
            return true;
        }

        return false;
    }

    /// <summary>校验并规范化认证服务器地址；不安全时抛出可直接展示的异常。</summary>
    public static string RequireSecureServerUrl(string url)
    {
        if (!IsSecureServerUrl(url, out _))
        {
            throw new AuthlibAuthException(
                "认证服务器地址必须使用 https（本机调试可用 http://localhost）。");
        }

        return NormalizeUrl(url);
    }

    /// <summary>读取认证服务器元数据（用于确认地址有效、展示主页/注册入口、启动时预取）。</summary>
    public async Task<AuthServerMetadata> FetchMetadataAsync(string apiRoot, CancellationToken ct = default)
    {
        var root = RequireSecureServerUrl(apiRoot);

        string json;
        try
        {
            json = await _downloader
                .RequestStringAsync(root, HttpMethod.Get, null, null, null, ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (HttpApiException ex)
        {
            throw new AuthlibAuthException($"无法访问认证服务器（HTTP {(int)ex.StatusCode}）：{root}", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AuthlibAuthException($"无法访问认证服务器：{root}", ex);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var type = document.RootElement;
            if (!type.TryGetProperty("meta", out var meta))
            {
                throw new AuthlibAuthException("这个地址不是有效的 authlib-injector 认证服务器（缺少 meta）");
            }

            string? homepage = null;
            string? register = null;
            if (meta.TryGetProperty("links", out var links))
            {
                homepage = links.TryGetProperty("homepage", out var h) ? h.GetString() : null;
                register = links.TryGetProperty("register", out var r) ? r.GetString() : null;
            }

            return new AuthServerMetadata
            {
                RawJson = json,
                Name = meta.TryGetProperty("serverName", out var n) ? n.GetString() : null,
                Homepage = homepage,
                Register = register,
            };
        }
        catch (JsonException ex)
        {
            throw new AuthlibAuthException("认证服务器返回的内容不是有效 JSON", ex);
        }
    }

    /// <summary>用户名 + 密码登录，成功后得到可直接启动游戏的账号。</summary>
    public async Task<Account> LoginAsync(
        AuthServerEntry server,
        string username,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new AuthlibAuthException("用户名不能为空");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new AuthlibAuthException("密码不能为空");
        }

        var root = RequireSecureServerUrl(server.Url);
        var clientToken = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new
        {
            agent = new { name = "Minecraft", version = 1 },
            username = username.Trim(),
            password,
            clientToken,
            requestUser = false,
        });

        var json = await PostAsync($"{root}/authserver/authenticate", payload, "登录", ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var response = JsonSerializer.Deserialize<YggdrasilAuthResponse>(json)
                       ?? throw new AuthlibAuthException("认证服务器响应无法解析");

        if (string.IsNullOrWhiteSpace(response.AccessToken))
        {
            throw new AuthlibAuthException("认证服务器没有返回访问令牌");
        }

        var profile = response.SelectedProfile
                      ?? response.AvailableProfiles?.FirstOrDefault();

        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new AuthlibAuthException("该账号没有可用的 Minecraft 角色");
        }

        var account = new Account
        {
            Kind = AccountKind.External,
            Name = profile.Name!,
            Uuid = profile.Id!.ToLowerInvariant(),
            AccessToken = response.AccessToken,
            ClientToken = string.IsNullOrWhiteSpace(response.ClientToken) ? clientToken : response.ClientToken,
            AuthServer = root,
            AuthServerName = server.Name,
        };

        _log.Info($"外置登录成功：{account.Name}（{account.AuthServerName}，{account.Uuid}）");
        return account;
    }

    /// <summary>校验 accessToken 是否仍然有效。</summary>
    public async Task<bool> ValidateAsync(Account account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.AuthServer) || string.IsNullOrWhiteSpace(account.ClientToken))
        {
            return false;
        }

        if (!IsSecureServerUrl(account.AuthServer, out _))
        {
            _log.Warn($"认证服务器地址不是 HTTPS，已跳过令牌校验：{account.AuthServer}");
            return false;
        }

        var payload = JsonSerializer.Serialize(new
        {
            accessToken = account.AccessToken,
            clientToken = account.ClientToken,
        });

        try
        {
            await _downloader
                .PostJsonAsync($"{NormalizeUrl(account.AuthServer!)}/authserver/validate", payload, null, ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return true;
        }
        catch (HttpApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            // 取消不是「令牌无效」：吞掉会让调用方继续拿已取消的令牌去刷新或启动。
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("校验外置登录令牌失败", ex);
            return false;
        }
    }

    /// <summary>用 accessToken + clientToken 续期。</summary>
    public async Task<Account> RefreshAsync(Account account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.AuthServer) || string.IsNullOrWhiteSpace(account.ClientToken))
        {
            throw new AuthlibAuthException("外置登录信息不完整，请重新登录");
        }

        var root = RequireSecureServerUrl(account.AuthServer!);

        var payload = JsonSerializer.Serialize(new
        {
            accessToken = account.AccessToken,
            clientToken = account.ClientToken,
            requestUser = false,
        });

        string json;
        try
        {
            json = await _downloader
                .PostJsonAsync($"{root}/authserver/refresh", payload, null, ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (HttpApiException ex)
        {
            throw MapAuthError(ex, "外置登录已失效，请重新登录");
        }

        var response = JsonSerializer.Deserialize<YggdrasilAuthResponse>(json)
                       ?? throw new AuthlibAuthException("认证服务器响应无法解析");

        if (string.IsNullOrWhiteSpace(response.AccessToken))
        {
            throw new AuthlibAuthException("外置登录已失效，请重新登录");
        }

        // 不原地修改传入的 Account：调用方（账号列表 / 健康检查）可能仍持有旧实例，
        // 原地改会让「刷新失败但对象已被改脏」的情况无法回滚。返回新实例由调用方 Upsert。
        var refreshed = new Account
        {
            Kind = account.Kind,
            Name = account.Name,
            Uuid = account.Uuid,
            AccessToken = response.AccessToken,
            Xuid = account.Xuid,
            XboxName = account.XboxName,
            RefreshToken = account.RefreshToken,
            AuthServer = account.AuthServer,
            AuthServerName = account.AuthServerName,
            ClientToken = string.IsNullOrWhiteSpace(response.ClientToken) ? account.ClientToken : response.ClientToken,
            ExpiresAt = account.ExpiresAt,
            IsDisabled = account.IsDisabled,
            DisabledReason = account.DisabledReason,
            ClientId = account.ClientId,
        };

        var profile = response.SelectedProfile ?? response.AvailableProfiles?.FirstOrDefault();
        if (profile?.Id is { Length: > 0 } id && profile.Name is { Length: > 0 } name)
        {
            refreshed.Uuid = id.ToLowerInvariant();
            refreshed.Name = name;
        }

        _log.Info($"外置登录令牌已续期：{refreshed.Name}（{refreshed.AuthServerName}）");
        return refreshed;
    }

    /// <summary>启动前保证令牌可用：先校验，失效再续期；续期也失败则要求重新登录。</summary>
    public async Task<Account> EnsureValidAsync(Account account, CancellationToken ct = default)
    {
        if (await ValidateAsync(account, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            return account;
        }

        var refreshed = await RefreshAsync(account, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return refreshed;
    }

    /// <summary>去掉结尾斜杠，保证拼端点时不会出现双斜杠。</summary>
    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    private async Task<string> PostAsync(string url, string payload, string stage, CancellationToken ct)
    {
        try
        {
            var json = await _downloader.PostJsonAsync(url, payload, null, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return json;
        }
        catch (HttpApiException ex)
        {
            throw MapAuthError(ex, $"外置{stage}失败");
        }
    }

    /// <summary>把 Yggdrasil 的错误体翻成用户能看懂的提示。</summary>
    private static AuthlibAuthException MapAuthError(HttpApiException ex, string fallback)
    {
        var message = fallback;
        var code = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(ex.ResponseBody ?? string.Empty);
            var root = document.RootElement;
            code = root.TryGetProperty("error", out var e) ? e.GetString() ?? string.Empty : string.Empty;
            if (root.TryGetProperty("errorMessage", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
            {
                message = m.GetString()!;
            }
        }
        catch (JsonException)
        {
            // 忽略：服务器没按协议返回错误体。
        }

        message = code switch
        {
            "ForbiddenOperationException" => "用户名或密码错误，或该账号不存在",
            "IllegalArgumentException" => string.IsNullOrWhiteSpace(message) ? "登录参数不合法" : message,
            _ => message,
        };

        return new AuthlibAuthException(
            message, ex, string.IsNullOrWhiteSpace(code) ? null : code);
    }

    private sealed class YggdrasilAuthResponse
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("clientToken")]
        public string? ClientToken { get; set; }

        [JsonPropertyName("selectedProfile")]
        public YggdrasilProfile? SelectedProfile { get; set; }

        [JsonPropertyName("availableProfiles")]
        public List<YggdrasilProfile>? AvailableProfiles { get; set; }
    }

    private sealed class YggdrasilProfile
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
