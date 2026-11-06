using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MCLauncher.Core.Security;

/// <summary>
/// TPM 2.0 机器密钥（Windows Platform Crypto Provider）：
/// 密钥持久化在 TPM 中，只有同一块 TPM 才能解密。创建或打开失败时返回 null，由调用方降级到 DPAPI。
/// </summary>
internal sealed class TpmMachineKey : IDisposable
{
    private const string ProviderName = "Microsoft Platform Crypto Provider";
    private const string RsaAlgorithm = "RSA";
    private const string LengthProperty = "Length";
    private const string PublicBlobType = "RSAPUBLICBLOB";
    private const uint OaepFlag = 0x00000004; // NCRYPT_PAD_OAEP_FLAG
    private const uint RsaPublicMagic = 0x31415352;

    private IntPtr _provider;
    private IntPtr _key;

    private TpmMachineKey(IntPtr provider, IntPtr key)
    {
        _provider = provider;
        _key = key;
    }

    /// <summary>打开（不存在时创建）一个 TPM 持久化 RSA-2048 密钥；任何失败都返回 null。</summary>
    public static TpmMachineKey? TryOpen(string keyName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (NCryptOpenStorageProvider(out var provider, ProviderName, 0) != 0)
            {
                return null;
            }

            var status = NCryptOpenKey(provider, out var key, keyName, 0, 0);
            if (status != 0)
            {
                status = NCryptCreatePersistedKey(provider, out key, RsaAlgorithm, keyName, 0, 0);
                if (status != 0)
                {
                    NCryptFreeObject(provider);
                    return null;
                }

                // 长度尽力设置为 2048；不要设置 KeyUsage：部分固件（如 Intel PTT）不支持
                // 创建时设置该属性（0x80090029 NTE_NOT_SUPPORTED），而默认创建的 RSA 密钥本来就允许解密。
                // 密钥是否真的可用由 MachineKey.Wrap 的「加密后回解验证」兜底，此处宽松即可。
                SetDwordProperty(key, LengthProperty, 2048);

                if (NCryptFinalizeKey(key, 0) != 0)
                {
                    NCryptDeleteKey(key, 0);
                    NCryptFreeObject(key);
                    NCryptFreeObject(provider);
                    return null;
                }
            }

            return new TpmMachineKey(provider, key);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>删除 TPM 中的持久化密钥（释放本机密钥）；不存在或本机无 TPM 时返回 false。</summary>
    public static bool Delete(string keyName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (NCryptOpenStorageProvider(out var provider, ProviderName, 0) != 0)
            {
                return false;
            }

            try
            {
                if (NCryptOpenKey(provider, out var key, keyName, 0, 0) != 0)
                {
                    return false;
                }

                try
                {
                    return NCryptDeleteKey(key, 0) == 0;
                }
                finally
                {
                    NCryptFreeObject(key);
                }
            }
            finally
            {
                NCryptFreeObject(provider);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>用 TPM 密钥的公开部分做 RSA-OAEP(SHA-256) 加密。</summary>
    public byte[] Wrap(byte[] data)
    {
        var parameters = ExportPublicParameters();
        using var rsa = RSA.Create();
        rsa.ImportParameters(parameters);
        return rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
    }

    /// <summary>
    /// 用 TPM 内的私钥做 RSA-OAEP 解密。优先 OAEP-SHA256，少数 TPM 固件只实现了 OAEP-SHA1，
    /// 失败时自动退回。解密走 NCryptDecrypt，与创建/打开同一套 API，结构体用 Unicode 封送。
    /// </summary>
    public byte[] Unwrap(byte[] data)
    {
        var status = TryDecrypt(data, "SHA256", out var result);
        if (status == 0)
        {
            return result;
        }

        // 部分固件只支持 OAEP-SHA1。
        status = TryDecrypt(data, "SHA1", out result);
        if (status != 0)
        {
            throw new InvalidOperationException($"TPM 解密失败（0x{status:X8}）");
        }

        return result;
    }

    private int TryDecrypt(byte[] data, string hashAlgorithm, out byte[] result)
    {
        var info = new BcryptOaepPaddingInfo { pszAlgId = hashAlgorithm, pbLabel = IntPtr.Zero, cbLabel = 0 };

        var status = NCryptDecrypt(_key, data, data.Length, ref info, null, 0, out var size, OaepFlag);
        if (status != 0)
        {
            result = Array.Empty<byte>();
            return status;
        }

        var output = new byte[size];
        status = NCryptDecrypt(_key, data, data.Length, ref info, output, size, out size, OaepFlag);
        if (status != 0)
        {
            result = Array.Empty<byte>();
            return status;
        }

        result = size == output.Length ? output : output[..size];
        return 0;
    }

    private static bool SetDwordProperty(IntPtr key, string name, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        return NCryptSetProperty(key, name, bytes, bytes.Length, 0) == 0;
    }

    private RSAParameters ExportPublicParameters()
    {
        var status = NCryptExportKey(_key, IntPtr.Zero, PublicBlobType, IntPtr.Zero, null, 0, out var size, 0);
        if (status != 0 || size < 24)
        {
            throw new InvalidOperationException($"无法导出 TPM 公钥（0x{status:X8}）");
        }

        var blob = new byte[size];
        status = NCryptExportKey(_key, IntPtr.Zero, PublicBlobType, IntPtr.Zero, blob, size, out size, 0);
        if (status != 0)
        {
            throw new InvalidOperationException($"无法导出 TPM 公钥（0x{status:X8}）");
        }

        if (BitConverter.ToUInt32(blob, 0) != RsaPublicMagic)
        {
            throw new InvalidDataException("TPM 公钥格式不是 RSA 公钥 blob");
        }

        var exponentLength = (int)BitConverter.ToUInt32(blob, 8);
        var modulusLength = (int)BitConverter.ToUInt32(blob, 12);
        const int headerSize = 24;

        return new RSAParameters
        {
            Exponent = blob[headerSize..(headerSize + exponentLength)],
            Modulus = blob[(headerSize + exponentLength)..(headerSize + exponentLength + modulusLength)],
        };
    }

    public void Dispose()
    {
        if (_key != IntPtr.Zero)
        {
            NCryptFreeObject(_key);
            _key = IntPtr.Zero;
        }

        if (_provider != IntPtr.Zero)
        {
            NCryptFreeObject(_provider);
            _provider = IntPtr.Zero;
        }
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenStorageProvider(out IntPtr phProvider, string pszProviderName, uint dwFlags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenKey(
        IntPtr hProvider, out IntPtr phKey, string pszKeyName, uint dwLegacyKeySpec, uint dwFlags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptCreatePersistedKey(
        IntPtr hProvider, out IntPtr phKey, string pszAlgId, string? pszKeyName, uint dwLegacyKeySpec, uint dwFlags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptSetProperty(
        IntPtr hObject, string pszProperty, byte[] pbInput, int cbInput, uint dwFlags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFinalizeKey(IntPtr hKey, uint dwFlags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptDeleteKey(IntPtr hKey, uint dwFlags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptExportKey(
        IntPtr hKey, IntPtr hExportKey, string pszBlobType, IntPtr pParameterList,
        byte[]? pbOutput, int cbOutput, out int pcbResult, uint dwFlags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptDecrypt(
        IntPtr hKey, byte[] pbInput, int cbInput, ref BcryptOaepPaddingInfo pPaddingInfo,
        byte[]? pbOutput, int cbOutput, out int pcbResult, uint dwFlags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFreeObject(IntPtr hObject);

    /// <summary>OAEP padding 结构体；pszAlgId 用 Unicode 封送，由运行时管理字符串生命周期。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BcryptOaepPaddingInfo
    {
        public string pszAlgId;
        public IntPtr pbLabel;
        public int cbLabel;
    }
}
