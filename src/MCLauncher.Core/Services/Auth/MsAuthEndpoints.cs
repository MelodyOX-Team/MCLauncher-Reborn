namespace MCLauncher.Core.Services.Auth;

/// <summary>
/// 微软登录（设备码流程）使用的端点与常量。
/// <para>
/// 设备码流程不需要注册任何回调地址，因此这里没有 authorize / redirect 相关配置。
/// </para>
/// </summary>
public static class MsAuthEndpoints
{
    /// <summary>请求的权限范围：Xbox Live 登录 + 刷新令牌。</summary>
    public const string Scope = "XboxLive.signin offline_access";

    /// <summary>设备码申请端点。</summary>
    public const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";

    /// <summary>令牌端点（设备码轮询与刷新令牌共用）。</summary>
    public const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    /// <summary>验证页根地址（服务端未返回时兜底）。</summary>
    public const string VerificationPage = "https://microsoft.com/devicelogin";

    /// <summary>Xbox Live 的 <c>RpsTicket</c> 与 XSTS 的 RelyingParty。</summary>
    public const string XboxLiveRelyingParty = "http://auth.xboxlive.com";

    public const string XstsRelyingParty = "rp://api.minecraftservices.com/";

    /// <summary>
    /// 取 Xbox 玩家代号用的 XSTS relying party。Minecraft 的 XSTS 响应只有 uhs，
    /// 换成这个 relying party 再请求一次才会在声明里带上 gtg（玩家代号）。
    /// </summary>
    public const string XboxProfileRelyingParty = "http://xboxlive.com";

    public const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";

    public const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    public const string MinecraftEntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";
}
