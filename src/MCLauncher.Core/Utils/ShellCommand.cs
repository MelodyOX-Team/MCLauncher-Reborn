using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MCLauncher.Core.Utils;

/// <summary>
/// 在 Windows 命令解释器里执行启动前 / 结束后命令。
/// 命令写法和 <c>cmd.exe</c> 一致，输出逐行回传给调用方。
/// </summary>
public static class ShellCommand
{
    public static async Task<int> RunAsync(
        string command,
        string workingDirectory,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return 0;
        }

        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(comspec))
        {
            comspec = "cmd.exe";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = comspec,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ResolveConsoleEncoding(),
            StandardErrorEncoding = ResolveConsoleEncoding(),
        };

        // 整条命令交给 cmd 自己解析。不能用 ArgumentList：.NET 的转义会破坏命令里的引号，
        // 必须按 /d /s /c "命令" 的原始形式交给 cmd。
        startInfo.Arguments = "/d /s /c \"" + command + "\"";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput?.Invoke(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动命令解释器");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 已经退出就忽略。
            }

            // Kill 之后必须等进程真正退出，避免留下仍在写输出的子进程。
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 进程已消失。
            }

            throw;
        }

        // WaitForExitAsync 在进程退出时就会返回，再等一次确保重定向流已读完。
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>cmd 的输出按系统 ANSI 代码页编码（中文 Windows 上是 GBK）。</summary>
    private static Encoding ResolveConsoleEncoding()
    {
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
}
