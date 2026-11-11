using System.Security.Cryptography;
using System.Text;

namespace MCLauncher.Core.Security;

/// <summary>
/// 本机密钥：优先使用 TPM 2.0 的持久化 RSA 密钥；本机不支持或调用失败时降级为
/// 「软件 RSA 私钥 + DPAPI(CurrentUser) 保护」。两种方式都是非对称加密，
/// 私钥只存在本机，复制 accounts.json 到别的电脑无法解密。
/// 两级都不可用时直接抛 <see cref="MachineKeyUnavailableException"/>，不做任何弱保护降级。
/// </summary>
internal static class MachineKey
{
    private const string TpmKeyName = "MCLauncherReborn-Accounts-v1";

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("MCLauncher-Reborn/accounts/v1");

    private const string UnavailableMessage =
        "当前系统无法提供安全存储（TPM 与 DPAPI 均不可用），账号凭据已拒绝保存。"
        + "请检查系统用户配置或安全策略后再试。";

    public sealed record SealedBlob(string Protector, string KeyBlob, byte[] Wrapped);

    /// <summary>
    /// 用本机密钥包装：优先 TPM，失败则用 DPAPI 保护的软件私钥。
    /// DPAPI 也不可用时抛出 <see cref="MachineKeyUnavailableException"/>。
    /// </summary>
    public static SealedBlob Wrap(byte[] data, Action<string>? onTpmFailure = null)
    {
        if (OperatingSystem.IsWindows())
        {
            using var tpm = TpmMachineKey.TryOpen(TpmKeyName);
            if (tpm is not null)
            {
                try
                {
                    var wrapped = tpm.Wrap(data);

                    // 立即用同一密钥回解验证：TPM 密钥可能是签名专用、或本机驱动不支持 OAEP，
                    // 这种情况下写出去的账号文件重启后必然解不开，必须直接降级到 DPAPI。
                    var check = tpm.Unwrap(wrapped);
                    if (CryptographicOperations.FixedTimeEquals(check, data))
                    {
                        return new SealedBlob("tpm2", TpmKeyName, wrapped);
                    }

                    onTpmFailure?.Invoke("回解结果与原文不一致");
                }
                catch (Exception ex)
                {
                    // TPM 不可用（例如策略禁用、密钥被清除、驱动不支持 OAEP）：继续走 DPAPI 降级。
                    onTpmFailure?.Invoke(ex.Message);
                }
            }
            else
            {
                onTpmFailure?.Invoke("无法打开或创建 TPM 密钥");
            }

            return WrapWithDpapi(data);
        }

        throw new MachineKeyUnavailableException(UnavailableMessage);
    }

    /// <summary>
    /// 降级方案：临时生成软件 RSA 私钥，用 DPAPI(CurrentUser) 加密后随文件保存。
    /// DPAPI 调用失败时抛 <see cref="MachineKeyUnavailableException"/>。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SealedBlob WrapWithDpapi(byte[] data)
    {
        try
        {
            using var rsa = RSA.Create(2048);
            var protectedKey = ProtectedData.Protect(
                rsa.ExportRSAPrivateKey(), DpapiEntropy, DataProtectionScope.CurrentUser);

            return new SealedBlob(
                "dpapi",
                Convert.ToBase64String(protectedKey),
                rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256));
        }
        catch (Exception ex)
        {
            throw new MachineKeyUnavailableException(UnavailableMessage, ex);
        }
    }

    /// <summary>
    /// 销毁本机密钥：删除 TPM 中的持久化密钥；DPAPI 模式的软件私钥只保存在 accounts.json 里，
    /// 随账号文件删除一起销毁。返回是否真的删掉了 TPM 密钥。
    /// </summary>
    public static bool DeleteTpmKey() => TpmMachineKey.Delete(TpmKeyName);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] UnwrapWithTpm(string keyBlob, byte[] wrapped)
    {
        using var tpm = TpmMachineKey.TryOpen(keyBlob)
                        ?? throw new InvalidOperationException("TPM 密钥不可用，无法解密账号文件");
        return tpm.Unwrap(wrapped);
    }

    public static byte[] Unwrap(string protector, string keyBlob, byte[] wrapped)
    {
        switch (protector)
        {
            case "tpm2":
                if (!OperatingSystem.IsWindows())
                {
                    throw new MachineKeyUnavailableException(UnavailableMessage);
                }

                return UnwrapWithTpm(keyBlob, wrapped);

            case "dpapi":
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new MachineKeyUnavailableException(UnavailableMessage);
                }

                try
                {
                    var der = ProtectedData.Unprotect(
                        Convert.FromBase64String(keyBlob), DpapiEntropy, DataProtectionScope.CurrentUser);
                    using var rsa = RSA.Create();
                    rsa.ImportRSAPrivateKey(der, out _);
                    return rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
                }
                catch (MachineKeyUnavailableException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new MachineKeyUnavailableException("DPAPI 无法解开账号文件，当前用户环境可能已变化。", ex);
                }
            }

            default:
                throw new InvalidDataException($"未知的账号文件保护方式：{protector}");
        }
    }
}
