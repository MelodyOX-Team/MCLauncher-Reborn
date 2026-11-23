using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MCLauncher.App.Services;

/// <summary>
/// 把已安装的 Java 写入当前用户环境变量（HKCU\Environment），不需要管理员权限。
/// JAVA_HOME 为空时写入；PATH 里没有对应 bin 目录时追加，并广播环境变更通知。
/// </summary>
public static class JavaEnvironmentService
{
    private const string EnvironmentKeyPath = "Environment";
    private const int HwndBroadcast = 0xFFFF;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    /// <summary>环境变量写入结果。</summary>
    public sealed record Result(bool JavaHomeWritten, bool PathUpdated, string JavaHome, string BinDirectory)
    {
        public bool Changed => JavaHomeWritten || PathUpdated;
    }

    /// <summary>
    /// 把 <paramref name="javaExePath"/> 所在的 Java 注册到用户环境变量：
    /// 已有 JAVA_HOME 时保留原值，只把它的 bin 目录补进 PATH。
    /// </summary>
    public static Result Register(string javaExePath)
    {
        if (string.IsNullOrWhiteSpace(javaExePath) || !File.Exists(javaExePath))
        {
            throw new FileNotFoundException("找不到要注册的 java.exe", javaExePath);
        }

        var binDirectory = Path.GetDirectoryName(Path.GetFullPath(javaExePath))
                           ?? throw new InvalidOperationException("无法确定 java.exe 所在目录");
        var javaHome = Directory.GetParent(binDirectory)?.FullName ?? binDirectory;

        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(EnvironmentKeyPath)
                        ?? throw new InvalidOperationException("无法打开用户环境变量注册表项");

        // 已有 JAVA_HOME 时不覆盖，避免影响用户原来的开发环境。
        var javaHomeWritten = false;
        var existingHome = key.GetValue("JAVA_HOME", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(existingHome))
        {
            key.SetValue("JAVA_HOME", javaHome, RegistryValueKind.String);
            javaHomeWritten = true;
        }

        // PATH 追加 java.exe 所在目录；已有对应项（含 %JAVA_HOME%\bin 这类变量写法）则跳过。
        var currentPath = key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                          ?? string.Empty;
        var pathUpdated = false;

        var present = currentPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => string.Equals(
                Environment.ExpandEnvironmentVariables(entry).TrimEnd('\\'),
                binDirectory.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase));

        if (!present)
        {
            var prefix = currentPath.Length == 0 || currentPath.EndsWith(';') ? string.Empty : ";";
            key.SetValue("Path", currentPath + prefix + binDirectory, RegistryValueKind.ExpandString);
            pathUpdated = true;
        }

        if (javaHomeWritten || pathUpdated)
        {
            BroadcastEnvironmentChange();
        }

        return new Result(javaHomeWritten, pathUpdated, javaHome, binDirectory);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);

    /// <summary>通知资源管理器等程序重新读取环境变量；新启动的进程会直接读注册表。</summary>
    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(
                new IntPtr(HwndBroadcast),
                WmSettingChange,
                UIntPtr.Zero,
                "Environment",
                SmtoAbortIfHung,
                2000,
                out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 广播失败不影响已写入的注册表值。
        }
    }
}
