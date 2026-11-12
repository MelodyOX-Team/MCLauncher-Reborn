using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCLauncher.Core.Utils;

/// <summary>
/// Minecraft 生态的 JSON 有大量历史遗留：注释、尾随逗号、大小写不一致、
/// 甚至出现 NaN。这里统一放宽解析策略，模型侧用 JsonPropertyName 精确映射。
/// </summary>
public static class JsonUtil
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 历史版本 JSON（尤其 Forge/光影配置）真的会写出 NaN/Infinity，
        // 不接受这些字面量会让整个版本文件解析失败。
        NumberHandling = JsonNumberHandling.AllowReadingFromString
            | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 写入时显式去掉 AllowNamedFloatingPointLiterals：读得进来不代表要写回去，
    // 保持「NaN 无法序列化」的严格行为，避免把非标准 JSON 传播给别的解析器。
    private static readonly JsonSerializerOptions WriteOptions = new(Options)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonSerializerOptions CompactWriteOptions = new(Options)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>同一路径的写入串行化；并发 Save 抢同一个临时文件会互相失败。</summary>
    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? DeserializeFromFile<T>(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Deserialize<T>(text);
    }

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? WriteOptions : CompactWriteOptions);

    /// <summary>原子写入：先写临时文件再替换，避免中途崩溃留下半个坏文件。</summary>
    public static void SerializeToFile<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = PathLocks.GetOrAdd(fullPath, static _ => new object());

        lock (gate)
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 临时文件名必须唯一：两个线程即使绕过锁（例如不同进程）也不该抢同一个 .tmp。
            var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.WriteAllText(temp, Serialize(value), new UTF8Encoding(false));

                if (File.Exists(fullPath))
                {
                    try
                    {
                        File.Replace(temp, fullPath, null, ignoreMetadataErrors: true);
                    }
                    catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                    {
                        // 某些文件系统不支持 Replace（或目标刚被删除）：退回到覆盖式 Move。
                        File.Move(temp, fullPath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(temp, fullPath);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 清理临时文件失败不影响已写入的结果。
                }
            }
        }
    }
}
