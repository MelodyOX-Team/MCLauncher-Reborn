using System.Text.Json.Serialization;

namespace MCLauncher.Core.Models;

/// <summary>
/// 实例（版本）级覆盖项。保存在启动器全局设置 <c>mclcfg/settings.json</c> 的
/// <c>instanceOverrides</c> 字典里（按实例 id 索引），版本目录下不写任何配置文件。
/// 与 HMCL 的 <c>instance-game-settings.json</c> 思路一致：没有覆盖项的实例不产生条目。
/// </summary>
public sealed class InstanceConfig
{
    /// <summary>是否启用版本隔离；null 表示跟随全局设置。</summary>
    [JsonPropertyName("isolated")]
    public bool? Isolated { get; set; }

    // ---- 实例级覆盖项：null 表示跟随全局设置 ----

    [JsonPropertyName("maxMemoryMb")]
    public int? MaxMemoryMb { get; set; }

    [JsonPropertyName("minMemoryMb")]
    public int? MinMemoryMb { get; set; }

    /// <summary>实例专用 Java；为空时跟随全局设置。</summary>
    [JsonPropertyName("javaPath")]
    public string? JavaPath { get; set; }

    [JsonPropertyName("extraJvmArgs")]
    public string? ExtraJvmArgs { get; set; }

    [JsonPropertyName("extraGameArgs")]
    public string? ExtraGameArgs { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("windowMode")]
    public GameWindowMode? WindowMode { get; set; }

    /// <summary>没有任何覆盖项；用于从全局设置里移除该实例的条目。</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Isolated is null
        && MaxMemoryMb is null
        && MinMemoryMb is null
        && JavaPath is null
        && ExtraJvmArgs is null
        && ExtraGameArgs is null
        && Width is null
        && Height is null
        && WindowMode is null;
}
