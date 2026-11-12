using System.Text.Json;
using System.Text.Json.Serialization;
using MCLauncher.Core.Models;

namespace MCLauncher.Core.Models;

/// <summary>
/// 版本 JSON 的 <c>arguments</c> 块。1.13 起用结构化参数；更早的版本用一行
/// <c>minecraftArguments</c> 字符串，两者必须都支持。
/// </summary>
public sealed class ArgumentsBlock
{
    private List<ArgumentEntry> _game = new();
    private List<ArgumentEntry> _jvm = new();

    // setter 兜底 null：JSON 里出现 "game": null 时反序列化会直接调用 setter，
    // 非空声明的默认值挡不住它，下游所有 .Count / AddRange 都会 NullReference。
    [JsonPropertyName("game")]
    public List<ArgumentEntry> Game
    {
        get => _game;
        set => _game = value ?? new();
    }

    [JsonPropertyName("jvm")]
    public List<ArgumentEntry> Jvm
    {
        get => _jvm;
        set => _jvm = value ?? new();
    }
}

/// <summary>
/// 一条参数条目，双态：
/// <list type="bullet">
/// <item>字符串态：<c>"${auth_player_name}"</c> —— 无条件生效。</item>
/// <item>对象态：<c>{ "rules": [...], "value": "x" | ["x","y"] }</c> —— 规则命中才生效。</item>
/// </list>
/// </summary>
[JsonConverter(typeof(ArgumentEntryJsonConverter))]
public sealed class ArgumentEntry
{
    /// <summary>无规则时为空。</summary>
    public List<CompatibilityRule>? Rules { get; set; }

    /// <summary>展开后的一个或多个参数片段。</summary>
    public List<string> Values { get; set; } = new();

    /// <summary>是否由规则控制。</summary>
    [JsonIgnore]
    public bool IsRuleBased => Rules is { Count: > 0 };

    /// <summary>规则是否在当前环境下命中；无规则时恒为 true。</summary>
    public bool Applies(IReadOnlyDictionary<string, bool>? features = null) =>
        !IsRuleBased || RuleEvaluator.AppliesToCurrentEnvironment(Rules, features);

    public override string ToString() => string.Join(" ", Values);
}

/// <summary>
/// 双态参数条目的读写转换器。
/// 写回时保持原形态：字符串态仍写字符串，对象态仍写对象（供重写版本 JSON 用）。
/// </summary>
public sealed class ArgumentEntryJsonConverter : JsonConverter<ArgumentEntry>
{
    public override ArgumentEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new ArgumentEntry { Values = { reader.GetString() ?? string.Empty } };

            case JsonTokenType.StartObject:
                return ReadObject(ref reader, options);

            default:
                throw new JsonException($"参数条目格式非法：{reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, ArgumentEntry value, JsonSerializerOptions options)
    {
        if (!value.IsRuleBased && value.Values.Count <= 1)
        {
            writer.WriteStringValue(value.Values.Count == 0 ? string.Empty : value.Values[0]);
            return;
        }

        writer.WriteStartObject();

        if (value.IsRuleBased)
        {
            writer.WritePropertyName("rules");
            JsonSerializer.Serialize(writer, value.Rules, options);
        }

        writer.WritePropertyName("value");
        if (value.Values.Count == 1)
        {
            writer.WriteStringValue(value.Values[0]);
        }
        else
        {
            writer.WriteStartArray();
            foreach (var item in value.Values)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static ArgumentEntry ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var entry = new ArgumentEntry();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var property = reader.GetString();
            reader.Read();

            switch (property)
            {
                case "rules":
                    entry.Rules = JsonSerializer.Deserialize<List<CompatibilityRule>>(ref reader, options);
                    break;

                case "value":
                    ReadValue(ref reader, entry);
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        return entry;
    }

    private static void ReadValue(ref Utf8JsonReader reader, ArgumentEntry entry)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                entry.Values.Add(reader.GetString() ?? string.Empty);
                break;

            case JsonTokenType.StartArray:
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        entry.Values.Add(reader.GetString() ?? string.Empty);
                    }
                }

                break;
        }
    }
}
