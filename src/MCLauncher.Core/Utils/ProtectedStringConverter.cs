using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCLauncher.Core.Utils;

/// <summary>
/// 敏感字符串的 JSON 转换器：落盘前用 DPAPI（当前用户范围）加密，读取时自动解密。
/// <para>格式：<c>enc:v1:</c> + Base64 密文。没有该前缀的值视为旧版明文，原样返回，下次保存时自动加密。</summary>
/// <para>解密失败（换了 Windows 用户 / 配置文件被拷贝到别的机器）时返回空串，让上层走重新登录流程，
/// 而不是把损坏密文当成令牌使用。</summary>
/// </remarks>
public sealed class ProtectedStringConverter : JsonConverter<string?>
{
    private const string Prefix = "enc:v1:";

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }

        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 无法解密，按空串处理而不是把密文当令牌用。
            return string.Empty;
        }

        try
        {
            var cipher = Convert.FromBase64String(value[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or PlatformNotSupportedException)
        {
            return string.Empty;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue(value);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 没有 DPAPI，写明文等于把令牌直接公开，宁可丢掉这一项。
            Debug.WriteLine("ProtectedStringConverter：当前平台不支持 DPAPI，已跳过敏感字段写入。");
            writer.WriteNullValue();
            return;
        }

        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            writer.WriteStringValue(Prefix + Convert.ToBase64String(cipher));
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            // 加密失败时不能退回明文：敏感字段直接写 null，让调用方/加载逻辑按「没有凭据」处理。
            Debug.WriteLine($"ProtectedStringConverter：DPAPI 加密失败，已跳过该字段：{ex}");
            writer.WriteNullValue();
        }
    }
}
