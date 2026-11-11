namespace MCLauncher.Core.Security;

/// <summary>
/// 本机安全存储（TPM 与 DPAPI）都不可用。此时账号凭据无法安全落盘，
/// 必须拒绝保存账号并告知用户环境存在风险，而不是降级成明文或弱保护。
/// </summary>
public sealed class MachineKeyUnavailableException : Exception
{
    public MachineKeyUnavailableException(string message) : base(message)
    {
    }

    public MachineKeyUnavailableException(string message, Exception inner) : base(message, inner)
    {
    }
}
