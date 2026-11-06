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
    public static AccountsVault Seal(IReadOnlyList<Account> accounts)
    {
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var payload = AesGcmBox.Wrap(dataKey, Encoding.UTF8.GetBytes(JsonUtil.Serialize(accounts.ToList())));

        var inner = LauncherKey.Wrap(dataKey);
        var machine = MachineKey.Wrap(inner);
        CryptographicOperations.ZeroMemory(dataKey);

        return new AccountsVault
        {
            Protector = machine.Protector,
            MachineKey = machine.KeyBlob,
            Outer = Convert.ToBase64String(machine.Wrapped),
            Payload = Convert.ToBase64String(payload),
        };
    }

    public static IReadOnlyList<Account> Open(AccountsVault vault)
    {
        var inner = MachineKey.Unwrap(
            vault.Protector, vault.MachineKey, Convert.FromBase64String(vault.Outer));
        var dataKey = LauncherKey.Unwrap(inner);
        var json = AesGcmBox.Unwrap(dataKey, Convert.FromBase64String(vault.Payload));
        CryptographicOperations.ZeroMemory(dataKey);

        return JsonUtil.Deserialize<List<Account>>(Encoding.UTF8.GetString(json)) ?? new List<Account>();
    }
}
