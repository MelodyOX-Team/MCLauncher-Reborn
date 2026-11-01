using System.Text.Json.Serialization;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Models;

/// <summary>账号类型。</summary>
public enum AccountKind
{
    /// <summary>离线账号：UUID 由玩家名派生，token 用占位值。</summary>
    Offline,

    /// <summary>微软正版账号。</summary>
    Microsoft,

    /// <summary>外置登录账号（authlib-injector / Yggdrasil 认证服务器）。</summary>
    External,
}

/// <summary>
/// 启动用的账号。离线账号的 UUID 必须按协议派生：
/// <c>UUID.nameUUIDFromBytes(("OfflinePlayer:" + name).getBytes(UTF_8))</c> —— 即 MD5 后按 version 3 规范化，
/// 自己随便编一个会导致皮肤/统计/成就全部异常。
/// </summary>
public sealed class Account
{
    public AccountKind Kind { get; set; } = AccountKind.Offline;

    /// <summary>玩家名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>UUID（32 位十六进制，无横杠）。</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>访问令牌。离线账号使用占位值；落盘时由 DPAPI 加密。</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string AccessToken { get; set; } = "0";

    /// <summary>Xbox 用户 ID（仅微软账号有）。</summary>
    public string? Xuid { get; set; }

    /// <summary>
    /// Xbox 玩家代号（仅微软账号有）。来自以 <c>http://xboxlive.com</c> 为依赖方的 XSTS 声明
    /// （Minecraft 依赖方的响应里只有 uhs）；旧数据可能为空。
    /// </summary>
    public string? XboxName { get; set; }

    /// <summary>微软 OAuth 刷新令牌（仅微软账号有），落盘时由 DPAPI 加密。</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string? RefreshToken { get; set; }

    /// <summary>外置登录认证服务器 API 根地址（authlib-injector）。</summary>
    public string? AuthServer { get; set; }

    /// <summary>外置登录认证服务器显示名。</summary>
    public string? AuthServerName { get; set; }

    /// <summary>authlib clientToken，刷新令牌时要用；落盘时由 DPAPI 加密。</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string? ClientToken { get; set; }

    /// <summary>令牌过期时间。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// 已被服务器判定为失效（令牌/刷新令牌被拒绝）：界面置灰，只能删除或重新登录，不能设为当前。
    /// </summary>
    public bool IsDisabled { get; set; }

    /// <summary>失效原因，界面提示用。</summary>
    public string? DisabledReason { get; set; }

    [JsonIgnore]
    public bool IsMicrosoft => Kind == AccountKind.Microsoft;

    [JsonIgnore]
    public bool IsExternal => Kind == AccountKind.External;

    /// <summary>协议里的 <c>${user_type}</c>：微软/外置登录 <c>msa</c>，离线账号 <c>legacy</c>。</summary>
    [JsonIgnore]
    public string UserType => IsMicrosoft || IsExternal ? "msa" : "legacy";

    /// <summary>微软账号的客户端 ID（对应 <c>${clientid}</c>）。</summary>
    public string? ClientId { get; set; }

    public bool IsExpired(TimeSpan margin) =>
        ExpiresAt is not null && ExpiresAt.Value - margin <= DateTimeOffset.Now;

    public static Account CreateOffline(string name, string? uuid = null) => new()
    {
        Kind = AccountKind.Offline,
        Name = name,
        Uuid = string.IsNullOrWhiteSpace(uuid) ? HashUtil.OfflinePlayerUuid(name) : uuid!,
        AccessToken = "0",
    };

    public override string ToString() => Kind switch
    {
        AccountKind.Microsoft => $"{Name}（微软）",
        AccountKind.External => $"{Name}（{AuthServerName ?? "外置登录"}）",
        _ => $"{Name}（离线）",
    };
}
