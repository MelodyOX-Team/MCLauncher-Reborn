using System.Security.Cryptography;
using System.Text;
using MCLauncher.Core.Models;
using MCLauncher.Core.Services;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Security;

/// <summary>
/// accounts.json 的加解密流程：
/// 随机数据密钥 → AES-256-GCM 加密账号 JSON → 数据密钥先被启动器固定密钥包装 → 再被本机密钥包装。
/// 解密必须同时具备启动器密钥与本机 TPM/DPAPI 私钥，缺一不可。
/// </summary>
internal static class AccountsVaultCrypto
{
    public static AccountsVault Seal(IReadOnlyList<Account> accounts, Action<string>? onTpmFailure = null)
    {
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var payload = AesGcmBox.Wrap(dataKey, Encoding.UTF8.GetBytes(JsonUtil.Serialize(accounts.ToList())));

        var inner = LauncherKey.Wrap(dataKey);
        var machine = MachineKey.Wrap(inner, onTpmFailure);

        // TPM 密钥可能被系统清除或在别的机器上重建；额外用 DPAPI 备份一份数据密钥，
        // 这样 TPM 解不开时账号仍能恢复，不会因为硬件密钥变化而丢账号。
        var fallback = machine.Protector == "tpm2" ? MachineKey.WrapSoftware(inner) : null;
        CryptographicOperations.ZeroMemory(dataKey);

        return new AccountsVault
        {
            Protector = machine.Protector,
            MachineKey = machine.KeyBlob,
            Outer = Convert.ToBase64String(machine.Wrapped),
            MachineKeyFallback = fallback?.KeyBlob,
            OuterFallback = fallback is null ? null : Convert.ToBase64String(fallback.Wrapped),
            Payload = Convert.ToBase64String(payload),
        };
    }

    public static IReadOnlyList<Account> Open(AccountsVault vault) => Open(vault, out _);

    /// <param name="usedFallback">是否通过 DPAPI 备份解开的（说明 TPM 密钥已失效，调用方应重新保存一次）。</param>
    public static IReadOnlyList<Account> Open(AccountsVault vault, out bool usedFallback)
    {
        usedFallback = false;
        byte[] inner;

        try
        {
            inner = MachineKey.Unwrap(
                vault.Protector, vault.MachineKey, Convert.FromBase64String(vault.Outer));
        }
        catch (Exception) when (vault.MachineKeyFallback is not null && vault.OuterFallback is not null)
        {
            usedFallback = true;
            inner = MachineKey.Unwrap(
                "dpapi",
                vault.MachineKeyFallback,
                Convert.FromBase64String(vault.OuterFallback));
        }

        var dataKey = LauncherKey.Unwrap(inner);
        var json = AesGcmBox.Unwrap(dataKey, Convert.FromBase64String(vault.Payload));
        CryptographicOperations.ZeroMemory(dataKey);

        return JsonUtil.Deserialize<List<Account>>(Encoding.UTF8.GetString(json)) ?? new List<Account>();
    }
}
