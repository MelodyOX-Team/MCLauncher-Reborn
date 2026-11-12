namespace MCLauncher.Core.Utils;

/// <summary>路径比较与拼接的公共规则。Windows 下路径比较必须忽略大小写。</summary>
public static class PathUtil
{
    public static string Combine(params string[] parts) => Path.Combine(parts);

    /// <summary>确保目录存在。</summary>
    public static void EnsureDirectory(string directory) => Directory.CreateDirectory(directory);

    /// <summary>确保文件所在目录存在。</summary>
    public static void EnsureParentDirectory(string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    /// <summary>把路径规范化为全路径并去掉末尾分隔符，便于比较。</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    public static bool IsSamePath(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>判断 child 是否位于 parent 目录之下（含本身）。</summary>
    public static bool IsParentOf(string parent, string child)
    {
        var p = Normalize(parent);
        var c = Normalize(child);
        return c.Equals(p, StringComparison.OrdinalIgnoreCase)
               || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取相对路径，失败时回退为全路径。</summary>
    public static string GetRelative(string basePath, string fullPath) =>
        Path.GetRelativePath(Normalize(basePath), Normalize(fullPath));

    /// <summary>把字符串里 Windows 文件名不允许的字符替换成下划线。</summary>
    public static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = name.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            if (Array.IndexOf(invalid, buffer[i]) >= 0)
            {
                buffer[i] = '_';
            }
        }

        var result = new string(buffer).Trim();
        return result.Length == 0 ? "_" : result;
    }

    /// <summary>Windows 保留设备名：即使带扩展名也会被系统当成设备。</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>文件名是否可安全用作实例名 / 目录名。</summary>
    public static bool IsValidFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains("..", StringComparison.Ordinal)
            || name.EndsWith(' ')
            || name.EndsWith('.'))
        {
            return false;
        }

        // 设备名判断要去掉扩展名；Windows 同时忽略结尾的点与空格。
        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ', '.');
        return !ReservedDeviceNames.Contains(stem);
    }
}
