using System.Text.Json.Serialization;
using MCLauncher.Core.Models;

namespace MCLauncher.Core.Models;

/// <summary><c>downloads</c> 块：客户端/服务端的 jar 与混淆映射表。</summary>
public sealed class VersionDownloads
{
    [JsonPropertyName("client")]
    public DownloadInfo? Client { get; set; }

    [JsonPropertyName("server")]
    public DownloadInfo? Server { get; set; }

    [JsonPropertyName("client_mappings")]
    public DownloadInfo? ClientMappings { get; set; }

    [JsonPropertyName("server_mappings")]
    public DownloadInfo? ServerMappings { get; set; }
}

/// <summary>
/// <c>assetIndex</c>：既是一个下载项，又带资源索引的元信息。
/// <c>virtual</c> / <c>map_to_resources</c> 在索引文件里（见 <see cref="AssetIndex"/>），这里只存索引自身的下载信息。
/// </summary>
public sealed class AssetIndexInfo : DownloadInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }
}

/// <summary><c>javaVersion</c>：该版本要求的 Java 主版本（缺失时按 8 处理）。</summary>
public sealed class JavaVersionRequirement
{
    [JsonPropertyName("component")]
    public string? Component { get; set; }

    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }

    /// <summary>缺失字段时的兜底：老版本一律要 Java 8。</summary>
    public const int DefaultMajorVersion = 8;

    public int EffectiveMajorVersion => MajorVersion > 0 ? MajorVersion : DefaultMajorVersion;
}

/// <summary><c>logging.client.file</c>：老版本用于 log4j 配置的文件。</summary>
public sealed class LoggingFileInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class LoggingClientConfig
{
    /// <summary>形如 <c>-Dlog4j.configurationFile=${path}</c>。</summary>
    [JsonPropertyName("argument")]
    public string? Argument { get; set; }

    [JsonPropertyName("file")]
    public LoggingFileInfo? File { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class LoggingConfig
{
    [JsonPropertyName("client")]
    public LoggingClientConfig? Client { get; set; }
}

/// <summary>
/// 版本 JSON。加载器版本通过 <see cref="InheritsFrom"/> 继承原版，
/// 合并顺序对 <c>libraries</c> 与 <c>arguments</c> 有硬要求，见 <c>VersionResolver</c>。
/// </summary>
public sealed class GameVersionJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    /// <summary>
    /// Fabric / Quilt 等第三方版本 JSON 有时直接塞原版版本号，
    /// 比如 <c>"clientVersion": "1.21.3"</c>。优先级高于 libraries 坐标解析。
    /// </summary>
    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }

    /// <summary>要用的客户端 jar 版本号；缺失时等同于 <see cref="Id"/>（旧版 Forge 依赖它）。</summary>
    [JsonPropertyName("jar")]
    public string? Jar { get; set; }

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    /// <summary>release / snapshot / old_beta / old_alpha。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("releaseTime")]
    [JsonConverter(typeof(LenientDateTimeOffsetConverter))]
    public DateTimeOffset? ReleaseTime { get; set; }

    [JsonPropertyName("time")]
    [JsonConverter(typeof(LenientDateTimeOffsetConverter))]
    public DateTimeOffset? Time { get; set; }

    /// <summary>1.13 之前的参数写法（一行空格分隔的字符串）。</summary>
    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    /// <summary>1.13 起的结构化参数。</summary>
    [JsonPropertyName("arguments")]
    public ArgumentsBlock? Arguments { get; set; }

    [JsonPropertyName("assetIndex")]
    public AssetIndexInfo? AssetIndex { get; set; }

    /// <summary>资源版本名（如 <c>1.20</c>、<c>legacy</c>）。</summary>
    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    [JsonPropertyName("javaVersion")]
    public JavaVersionRequirement? JavaVersion { get; set; }

    [JsonPropertyName("libraries")]
    public List<Library>? Libraries { get; set; }

    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; set; }

    [JsonPropertyName("logging")]
    public LoggingConfig? Logging { get; set; }

    [JsonPropertyName("complianceLevel")]
    public int ComplianceLevel { get; set; }

    [JsonPropertyName("minimumLauncherVersion")]
    public int MinimumLauncherVersion { get; set; }

    /// <summary>是否使用旧式参数（结构化 <see cref="Arguments"/> 为空且存在 <see cref="MinecraftArguments"/>）。</summary>
    [JsonIgnore]
    public bool UsesLegacyArguments =>
        (Arguments is null || (Arguments.Game.Count == 0 && Arguments.Jvm.Count == 0))
        && !string.IsNullOrWhiteSpace(MinecraftArguments);

    /// <summary>该版本要求的 Java 主版本。</summary>
    [JsonIgnore]
    public int RequiredJavaMajor => JavaVersion?.EffectiveMajorVersion ?? JavaVersionRequirement.DefaultMajorVersion;

    /// <summary>无 <see cref="Arguments"/> 时的兜底参数表（1.13 之前的历史默认值）。</summary>
    [JsonIgnore]
    public string DefaultLegacyArguments =>
        "${auth_player_name} ${version_type} ${game_directory} ${assets_root} ${assets_index_name} "
        + "${auth_uuid} ${auth_access_token} ${user_type} ${version_name}";

    public override string ToString() => Id ?? "(未命名版本)";
}
