using System.Text.RegularExpressions;

namespace MCLauncher.Core.Utils;

/// <summary>
/// Minecraft 版本号解析。
/// 版本号形态非常多：正式版 <c>1.20.4</c>、老版本 <c>a1.2.6</c>/<c>b1.7.3</c>、
/// 快照 <c>23w45a</c>、预发布 <c>1.21.2-pre1</c>、
/// 加载器版本 <c>1.20.4-forge-49.0.30</c> / <c>1.20.1-fabric0.15.3</c>。
/// </summary>
public static partial class McVersionUtil
{
    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+))?$")]
    private static partial Regex ReleasePattern();

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex ReleasePrefixPattern();

    [GeneratedRegex(@"^\d{2}w\d{2}[a-z]$")]
    private static partial Regex SnapshotPattern();

    [GeneratedRegex(@"^[ab]\d")]
    private static partial Regex OldAlphaBetaPattern();

    /// <summary>是否为纯净的正式版号（不含加载器后缀）。</summary>
    public static bool IsPlainRelease(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ReleasePattern().IsMatch(id!);

    public static bool IsSnapshot(string? id) =>
        !string.IsNullOrWhiteSpace(id) && SnapshotPattern().IsMatch(id!);

    /// <summary>是否为远古的 alpha / beta（a1.2.6、b1.7.3）。</summary>
    public static bool IsOldAlphaBeta(string? id) =>
        !string.IsNullOrWhiteSpace(id) && OldAlphaBetaPattern().IsMatch(id!);

    /// <summary>是否为远古版本（alpha / beta），这类版本不走版本清单，只能手动提供。</summary>
    public static bool IsAncient(string? id) => IsOldAlphaBeta(id);

    /// <summary>
    /// 取版本号的"基准版本"：剥掉加载器与预发布后缀。
    /// <c>1.20.4-forge-49.0.30</c> → <c>1.20.4</c>；<c>b1.7.3</c> → <c>b1.7.3</c>。
    /// </summary>
    public static string GetBaseVersion(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var match = ReleasePrefixPattern().Match(id);
        return match.Success ? match.Value : id;
    }

    /// <summary>解析正式版号；带加载器后缀的编号也能解析出前三位。</summary>
    public static bool TryParseRelease(string? id, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var match = ReleasePrefixPattern().Match(id);
        if (!match.Success)
        {
            return false;
        }

        // 版本号来自远端清单，超出 int 范围时按无法解析处理，不能让它抛异常。
        if (!int.TryParse(match.Groups[1].Value, out major)
            || !int.TryParse(match.Groups[2].Value, out minor))
        {
            return false;
        }

        if (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, out patch))
        {
            return false;
        }

        return true;
    }

    /// <summary>版本号是否不高于指定版本。</summary>
    public static bool IsAtMost(string? id, int major, int minor, int patch)
    {
        if (!TryParseRelease(id, out var m, out var n, out var p))
        {
            return false;
        }

        return (m, n, p).CompareTo((major, minor, patch)) <= 0;
    }

    /// <summary>版本号是否不低于指定版本。</summary>
    public static bool IsAtLeast(string? id, int major, int minor, int patch)
    {
        if (!TryParseRelease(id, out var m, out var n, out var p))
        {
            return false;
        }

        return (m, n, p).CompareTo((major, minor, patch)) >= 0;
    }

    /// <summary>
    /// 按版本号排序用的比较键（缺失字段补 0，解析失败时排到最后）。
    /// 注意：给版本列表排序时应优先使用清单里的 releaseTime，本方法只用于版本号本身的比较。
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var hasLeft = TryParseRelease(left, out var lm, out var ln, out var lp);
        var hasRight = TryParseRelease(right, out var rm, out var rn, out var rp);

        if (!hasLeft && !hasRight)
        {
            return string.CompareOrdinal(left, right);
        }

        if (!hasLeft)
        {
            return -1;
        }

        if (!hasRight)
        {
            return 1;
        }

        return (lm, ln, lp).CompareTo((rm, rn, rp));
    }
}
