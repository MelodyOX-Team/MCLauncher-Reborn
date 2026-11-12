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
/// 本机密钥只有一条通道（TPM 优先，TPM 不可用则改写为 DPAPI），不再保留任何备用旁路。
/// </summary>
internal static class AccountsVaultCrypto
{
    public static AccountsVault Seal(IReadOnlyList<Account> accounts, Action<string>? onTpmFailure = null)
    {
        var dataKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var payload = AesGcmBox.Wrap(dataKey, Encoding.UTF8.GetBytes(JsonUtil.Serialize(accounts.ToList())));

            var inner = LauncherKey.Wrap(dataKey);

            // TPM 不可用时这里会自动降级为 DPAPI；DPAPI 也不可用则抛出，
            // 由上层拒绝保存（不允许写出弱保护或明文凭据）。
            var machine = MachineKey.Wrap(inner, onTpmFailure);

            return new AccountsVault
            {
                Protector = machine.Protector,
                MachineKey = machine.KeyBlob,
                Outer = Convert.ToBase64String(machine.Wrapped),
                Payload = Convert.ToBase64String(payload),
            };
        }
        finally
        {
            // 中间任何一步抛异常（例如 TPM/DPAPI 不可用）时也必须销毁明文数据密钥。
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public static IReadOnlyList<Account> Open(AccountsVault vault) => Open(vault, out _);

    /// <param name="migratedFromLegacy">
    /// 是否借助旧版遗留的 DPAPI 备份才解开（说明 TPM 密钥已失效）。调用方应立即重写文件，
    /// 重写后的新文件不再包含任何备用旁路。
    /// </param>
    public static IReadOnlyList<Account> Open(AccountsVault vault, out bool migratedFromLegacy)
    {
        migratedFromLegacy = false;
        byte[]? inner = null;
        byte[]? dataKey = null;

        try
        {
            try
            {
                inner = MachineKey.Unwrap(
                    vault.Protector, vault.MachineKey, Convert.FromBase64String(vault.Outer));
            }
            catch (Exception) when (vault.LegacyMachineKeyFallback is not null && vault.LegacyOuterFallback is not null)
            {
                // 旧版在 TPM 模式额外写过一份 DPAPI 备份。这里只做一次性兼容读取，
                // 解出后由 AccountStore 立刻按新格式重写，新文件不会再写入这条旁路。
                inner = MachineKey.Unwrap(
                    "dpapi",
                    vault.LegacyMachineKeyFallback,
                    Convert.FromBase64String(vault.LegacyOuterFallback));
                migratedFromLegacy = true;
            }

            dataKey = LauncherKey.Unwrap(inner!);
            var json = AesGcmBox.Unwrap(dataKey, Convert.FromBase64String(vault.Payload));

            return JsonUtil.Deserialize<List<Account>>(Encoding.UTF8.GetString(json)) ?? new List<Account>();
        }
        finally
        {
            // 解密失败（例如密钥不匹配抛异常）时同样要清零，避免明文密钥留在堆上。
            if (dataKey is not null)
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }

            if (inner is not null)
            {
                CryptographicOperations.ZeroMemory(inner);
            }
        }
    }
}
