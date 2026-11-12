using System.Diagnostics;
using System.Text;
using MCLauncher.Core.Abstractions;

namespace MCLauncher.Core.Logging;

/// <summary>
/// 把日志追加到 logs\launcher-yyyyMMdd.log，并在超过阈值时做一次简单轮转。
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private const long MaxBytes = 4 * 1024 * 1024;

    /// <summary>普通日志攒够这么多行再 Flush，避免每行一次磁盘写。</summary>
    private const int FlushThreshold = 32;

    /// <summary>日志保留天数：更早的文件（含轮转出的备份）在启动时清理。</summary>
    private const int RetentionDays = 7;

    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _currentFile;
    private int _pendingLines;
    private bool _disposed;

    /// <param name="minimumLevel">
    /// 最低记录级别，默认 Info：Trace 只用于开发期排查，不应长期落在用户日志里。
    /// </param>
    public FileLogSink(string directory, LogLevel minimumLevel = LogLevel.Info)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(_directory);
        CleanupExpiredLogs();
    }

    public void Write(LogLevel level, string message)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        lock (_gate)
        {
            // 退出阶段后台线程仍可能写日志；已释放后再打开文件会留下无法关闭的句柄。
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter();
                _writer!.WriteLine(message);
                _pendingLines++;

                // 告警与错误立即落盘，保证崩溃前的最后几条不会丢；普通日志按批 Flush。
                if (level >= LogLevel.Warn || _pendingLines >= FlushThreshold)
                {
                    FlushLocked();
                }

                if (_writer!.BaseStream.Length > MaxBytes)
                {
                    FlushLocked();
                    _writer.Dispose();
                    _writer = null;
                    Rotate();
                }
            }
            catch
            {
                // 落盘失败不影响主流程。
            }
        }
    }

    private void FlushLocked()
    {
        _pendingLines = 0;
        _writer?.Flush();
    }

    private void EnsureWriter()
    {
        var file = Path.Combine(_directory, $"launcher-{DateTime.Now:yyyyMMdd}.log");
        if (_writer is not null && string.Equals(_currentFile, file, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _writer?.Dispose();
        _currentFile = file;
        _pendingLines = 0;
        _writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
    }

    private void Rotate()
    {
        var file = _currentFile;
        if (file is null || !File.Exists(file))
        {
            return;
        }

        try
        {
            var backup = Path.Combine(_directory, $"launcher-{DateTime.Now:yyyyMMdd}-{DateTime.Now:HHmmss}.log");
            File.Move(file, backup);
        }
        catch (Exception ex)
        {
            // 文件被占用时无法轮转：保持追加，下次写入再次超过阈值时会重试；
            // 失败原因必须留下，否则日志会无限增长却没人知道。
            AppendWarning(file, $"日志轮转失败，将在下次写入时重试：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动时删除超过保留期的日志。文件名有 launcher-yyyyMMdd.log 和轮转出的
    /// launcher-yyyyMMdd-HHmmss.log 两种形式，按最后写入时间判断比解析文件名可靠。
    /// </summary>
    private void CleanupExpiredLogs()
    {
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        var todayFile = Path.Combine(_directory, $"launcher-{DateTime.Now:yyyyMMdd}.log");

        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, "launcher-*.log");
        }
        catch (Exception ex)
        {
            // 目录都无法枚举时只能写到调试输出，不影响启动。
            Trace.TraceWarning($"日志清理失败，无法枚举目录 {_directory}：{ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            try
            {
                // 正在写入的当天文件始终保留，避免系统时间异常时把当前日志删掉。
                if (string.Equals(file, todayFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new FileInfo(file);
                if (info.Exists && info.LastWriteTime < cutoff)
                {
                    info.Delete();
                }
            }
            catch (Exception ex)
            {
                AppendWarning(todayFile, $"日志清理失败，无法删除 {Path.GetFileName(file)}：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 把警告直接追加到日志文件。不能走 <see cref="Write"/>：此时文件已超过阈值，
    /// 再走 Write 会立刻触发轮转并形成递归。
    /// </summary>
    private static void AppendWarning(string? file, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [WRN] {message}";
        Trace.TraceWarning(line);

        if (file is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 文件本身不可写（例如被其他进程独占）时，只能退到 Debug/Trace 输出。
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                FlushLocked();
            }
            catch
            {
                // 退出阶段写失败没有补救手段。
            }

            _writer?.Dispose();
            _writer = null;
        }
    }
}
