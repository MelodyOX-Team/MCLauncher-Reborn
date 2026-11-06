using System.Security.Cryptography;
using System.Text;

namespace MCLauncher.Core.Security;

/// <summary>
/// 本机密钥：优先使用 TPM 2.0 的持久化 RSA 密钥；本机不支持或调用失败时降级为
/// 「软件 RSA 私钥 + DPAPI(CurrentUser) 保护」的系统级方案。两种方式都是非对称加密，
/// 私钥只存在本机，复制 accounts.json 到别的电脑无法解密。
/// </summary>
internal static class MachineKey
{
    private const string TpmKeyName = "MCLauncherReborn-Accounts-v1";

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("MCLauncher-Reborn/accounts/v1");

    public sealed record SealedBlob(string Protector, string KeyBlob, byte[] Wrapped);

    public static SealedBlob Wrap(byte[] data)
    {
        if (OperatingSystem.IsWindows())
        {
            using var tpm = TpmMachineKey.TryOpen(TpmKeyName);
            if (tpm is not null)
            {
                try
                {
                    return new SealedBlob("tpm2", TpmKeyName, tpm.Wrap(data));
                }
                catch (Exception)
                {
                    // TPM 不可用（例如策略禁用、密钥被清除）：继续走 DPAPI 降级。
                }
            }

            using var rsa = RSA.Create(2048);
            var protectedKey = ProtectedData.Protect(
                rsa.ExportRSAPrivateKey(), DpapiEntropy, DataProtectionScope.CurrentUser);

            return new SealedBlob(
                "dpapi",
                Convert.ToBase64String(protectedKey),
                rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256));
        }

        // 非 Windows 开发环境没有 DPAPI/TPM，只能退回未绑定的软件密钥。
        using var fallback = RSA.Create(2048);
        return new SealedBlob(
            "plain",
            Convert.ToBase64String(fallback.ExportRSAPrivateKey()),
            fallback.Encrypt(data, RSAEncryptionPadding.OaepSHA256));
    }

    public static byte[] Unwrap(string protector, string keyBlob, byte[] wrapped)
    {
        switch (protector)
        {
            case "tpm2":
            {
                using var tpm = TpmMachineKey.TryOpen(keyBlob)
                                ?? throw new InvalidOperationException("TPM 密钥不可用，无法解密账号文件");
                return tpm.Unwrap(wrapped);
            }

            case "dpapi":
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("DPAPI 仅在 Windows 上可用，无法解密账号文件");
                }

                var der = ProtectedData.Unprotect(
                    Convert.FromBase64String(keyBlob), DpapiEntropy, DataProtectionScope.CurrentUser);
                using var rsa = RSA.Create();
                rsa.ImportRSAPrivateKey(der, out _);
                return rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
            }

            case "plain":
            {
                using var rsa = RSA.Create();
                rsa.ImportRSAPrivateKey(Convert.FromBase64String(keyBlob), out _);
                return rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
            }

            default:
                throw new InvalidDataException($"未知的账号文件保护方式：{protector}");
        }
    }
}
