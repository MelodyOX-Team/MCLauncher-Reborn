using System.Text.Json.Serialization;

namespace MCLauncher.Core.Models;

/// <summary><c>latest</c>：清单里标记的最新正式版 / 快照。</summary>
public sealed class LatestVersions
{
    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }
}

/// <summary>
/// 清单里的一个版本条目。
/// **注意**：<c>version_manifest_v2.json</c> 的条目里没有 <c>size</c> 字段，
/// 只有 id / type / url / time / releaseTime / sha1 / complianceLevel。
/// </summary>
public sealed class VersionManifestEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>release / snapshot / old_beta / old_alpha。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>该版本 JSON 的下载地址；**必须用清单里给出的 url**，不能自己拼。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("time")]
    [JsonConverter(typeof(LenientDateTimeOffsetConverter))]
    public DateTimeOffset? Time { get; set; }

    [JsonPropertyName("releaseTime")]
    [JsonConverter(typeof(LenientDateTimeOffsetConverter))]
    public DateTimeOffset? ReleaseTime { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; } = -1;

    [JsonPropertyName("complianceLevel")]
    public int ComplianceLevel { get; set; }

    [JsonIgnore]
    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsSnapshot => string.Equals(Type, "snapshot", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsOld => Type is "old_beta" or "old_alpha" or "old_snapshot";

    /// <summary>
    /// 愚人节版本：社区公认的那批带彩蛋的版本。清单里 type 通常是 snapshot / old_snapshot，
    /// 但 id 能区分。这里维护一张名单，缺了也不影响——用户可以通过快照分区里看到它们。
    /// </summary>
    [JsonIgnore]
    public bool IsAprilFools => KnownAprilFoolsIds.Contains(Id)
                                || Id.Contains("April Fools", StringComparison.OrdinalIgnoreCase)
                                || Id.Contains("aprilfools", StringComparison.OrdinalIgnoreCase);

    /// <summary>社区公认的愚人节版本 id。</summary>
    private static readonly HashSet<string> KnownAprilFoolsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "20w14infinite",            // 2020 无限愚人节
        "15w14a",                   // 2015 爱之拥抱愚人节（清单里 type=snapshot）
        "3D Shareware v1.34",       // 2019 年愚人节
        "23w13a_or_b",              // 2023 投票愚人节
        "1.RV-Pre1",                // 2018 愚人节
        "3D_Shareware_v1.34",
    };

    public override string ToString() => $"{Id} ({Type})";
}

/// <summary>版本清单。</summary>
public sealed class VersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersions Latest { get; set; } = new();

    [JsonPropertyName("versions")]
    public List<VersionManifestEntry> Versions { get; set; } = new();
}
