using System.Security.Cryptography;

namespace MCLauncher.Core.Security;

/// <summary>AES-256-GCM 小封装：输出为 <c>nonce(12) + tag(16) + ciphertext</c>。</summary>
internal static class AesGcmBox
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] Wrap(byte[] key, byte[] data)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[data.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag);

        var output = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, output, NonceSize + TagSize, cipher.Length);
        return output;
    }

    public static byte[] Unwrap(byte[] key, byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize)
        {
            throw new InvalidDataException("加密数据长度不合法");
        }

        var nonce = blob[..NonceSize];
        var tag = blob[NonceSize..(NonceSize + TagSize)];
        var cipher = blob[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
