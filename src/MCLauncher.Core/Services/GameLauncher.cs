using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Logging;
using MCLauncher.Core.Models;

namespace MCLauncher.Core.Services;

/// <summary>
/// 游戏进程的启动与日志接管。
/// 参数一律通过 <see cref="ProcessStartInfo.ArgumentList"/> 传递，
/// 不自己拼命令行字符串 —— 否则路径里的空格、引号、中文迟早会把参数切错。
/// </summary>
public sealed class GameLauncher
{
    private readonly ILogger _log;

    public GameLauncher(ILogger log)
    {
        _log = log;
    }

    /// <param name="javaMajorVersion">目标 Java 主版本，用于决定输出编码（Java 18 起固定 UTF-8）。</param>
    public GameProcess Start(LaunchCommand command, LaunchLogBuffer logBuffer, int javaMajorVersion)
    {
        var encoding = ResolveOutputEncoding(javaMajorVersion);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };

        // 包装命令：把 java 命令整体交给外层程序，例如 wrapper.exe --arg java ...
        var wrapper = LaunchArgumentBuilder.SplitArguments(command.WrapperCommand ?? string.Empty);
        if (wrapper.Count > 0)
        {
            startInfo.FileName = wrapper[0];
            for (var i = 1; i < wrapper.Count; i++)
            {
                startInfo.ArgumentList.Add(wrapper[i]);
            }

            startInfo.ArgumentList.Add(command.JavaPath);
        }
        else
        {
            startInfo.FileName = command.JavaPath;
        }

        foreach (var argument in command.ToArgumentList())
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in command.Environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logBuffer.Append(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logBuffer.Append(e.Data);
            }
        };

        _log.Info($"启动游戏：{command.JavaPath}（工作目录 {command.WorkingDirectory}）");
        _log.Trace(command.ToSafeDisplayString());

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动 Java 进程：{command.JavaPath}");
            }
        }
        catch
        {
            // Start 失败时进程对象已经创建，必须释放它持有的句柄。
            process.Dispose();
            throw;
        }

        ApplyPriority(process, command.Priority);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new GameProcess(process);
    }

    private void ApplyPriority(Process process, GameProcessPriority priority)
    {
        if (priority == GameProcessPriority.Normal)
        {
            return;
        }

        var priorityClass = priority switch
        {
            GameProcessPriority.Low => ProcessPriorityClass.Idle,
            GameProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
            GameProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
            GameProcessPriority.High => ProcessPriorityClass.High,
            GameProcessPriority.RealTime => ProcessPriorityClass.RealTime,
            _ => ProcessPriorityClass.Normal,
        };

        try
        {
            process.PriorityClass = priorityClass;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // 实时优先级通常需要管理员权限；设置失败不影响游戏启动。
            _log.Warn($"设置游戏进程优先级失败（{priority}）", ex);
        }
    }

    /// <summary>
    /// Java 18 起标准流固定为 UTF-8；更早的版本用系统 ANSI 代码页
    /// （中文 Windows 上是 GBK），否则中文日志会变成乱码。
    /// </summary>
    public static Encoding ResolveOutputEncoding(int javaMajorVersion)
    {
        if (javaMajorVersion >= 18)
        {
            return new UTF8Encoding(false);
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new UTF8Encoding(false);
        }
    }

    /// <summary>把退出码翻译成人话（崩溃分析在后续里程碑细化）。</summary>
    public static string DescribeExitCode(int exitCode) => exitCode switch
    {
        0 => "正常退出",
        -1 => "进程被强制结束",
        1 => "游戏异常退出（常见于 Mod 报错或缺少依赖）",
        _ => $"游戏异常退出（退出码 {exitCode}）",
    };
}

/// <summary>正在运行的游戏进程。</summary>
public sealed class GameProcess : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    internal GameProcess(Process process)
    {
        _process = process;
        Id = process.Id;
    }

    public int Id { get; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// 游戏主窗口句柄；窗口尚未创建或进程已退出时为 0。
    /// 只有 Windows 上才有意义，启动器用它判断「游戏窗口是否真的出来了」。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public IntPtr MainWindowHandle
    {
        get
        {
            try
            {
                return _process.MainWindowHandle;
            }
            catch (InvalidOperationException)
            {
                return IntPtr.Zero;
            }
        }
    }

    /// <summary>等待进程结束并返回退出码。</summary>
    public async Task<int> WaitForExitAsync(CancellationToken ct = default)
    {
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);

        // 再同步等一次，确保异步输出回调全部投递完毕，日志不会丢尾巴。
        _process.WaitForExit();

        return _process.ExitCode;
    }

    public void Kill()
    {
        try
        {
            if (!HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程可能已在退出中，忽略。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Dispose();
    }
}
