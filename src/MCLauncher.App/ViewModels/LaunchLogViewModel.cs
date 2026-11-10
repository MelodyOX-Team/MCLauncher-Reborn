using System.IO;
using System.Text;
using System.Windows.Input;
using MCLauncher.App.ViewModels.Mvvm;

namespace MCLauncher.App.ViewModels;

/// <summary>
/// 启动日志页：默认显示游戏自己写下的 latest.log（.minecraft/logs，没有则读当前实例目录的 logs），
/// 游戏运行后再叠加本次进程的输出。「清空」= 删除 logs 目录下的所有日志文件。
/// </summary>
public sealed class LaunchLogViewModel : ViewModelBase
{
    private const int MaxLines = 3000;

    /// <summary>只读日志文件尾部：大日志整个读进界面会卡住，最新内容都在末尾。</summary>
    private const int TailBytes = 256 * 1024;

    private readonly LauncherStack _stack;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private string _logText = string.Empty;
    private string _sourceText = "尚未读取日志文件";

    public LaunchLogViewModel(LauncherStack stack)
    {
        _stack = stack;

        ClearCommand = new RelayCommand(ClearAll);

        // 游戏进程的读取线程是后台线程，必须 marshal 回 UI。
        _stack.LaunchLog.LineAdded += line => UiThread.Post(() => Append(line));
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    /// <summary>当前显示的日志来源，或最近一次操作的结果。</summary>
    public string SourceText
    {
        get => _sourceText;
        private set => SetProperty(ref _sourceText, value);
    }

    public ICommand ClearCommand { get; }

    /// <summary>
    /// 读取 latest.log：优先 .minecraft/logs，没有则退回当前实例目录的 logs。
    /// 页面每次显示时调用（页面被缓存，导航回来不会重建）。
    /// </summary>
    public void RefreshFromFile()
    {
        foreach (var latest in LatestLogCandidates())
        {
            try
            {
                var text = ReadTail(latest);
                ReplaceLines(text);
                SourceText = latest;
                return;
            }
            catch (Exception ex)
            {
                _stack.Log.Warn($"读取启动日志失败：{latest}", ex);
                SourceText = $"读取失败：{latest}";
                return;
            }
        }

        // 没有任何 latest.log（比如还没启动过游戏）：界面继续显示本次进程的输出。
        SourceText = "未找到 latest.log，仅显示本次启动输出";
    }

    /// <summary>按优先级给出可能的 latest.log 路径。</summary>
    private IEnumerable<string> LatestLogCandidates()
    {
        foreach (var dir in LogDirectoryCandidates())
        {
            var latest = Path.Combine(dir, "latest.log");
            if (File.Exists(latest))
            {
                yield return latest;
            }
        }
    }

    /// <summary>
    /// 可能的日志目录：游戏根目录的 logs，以及当前实例目录的 logs（版本隔离时两者不同，去重）。
    /// </summary>
    private IEnumerable<string> LogDirectoryCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var root = Path.Combine(_stack.GameRoot, "logs");
        if (seen.Add(root))
        {
            yield return root;
        }

        var versionId = _stack.Config.Settings.SelectedInstanceId;
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            var instance = Path.Combine(
                _stack.GetInstanceDirectory(versionId, _stack.IsIsolated(versionId)), "logs");
            if (seen.Add(instance))
            {
                yield return instance;
            }
        }
    }

    /// <summary>读日志文件尾部（游戏可能正持有着文件，用共享读写打开）。</summary>
    private static string ReadTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > TailBytes)
        {
            stream.Seek(-TailBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void ReplaceLines(string text)
    {
        lock (_gate)
        {
            _lines.Clear();
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                _lines.Enqueue(trimmed);
                while (_lines.Count > MaxLines)
                {
                    _lines.Dequeue();
                }
            }
        }

        RebuildText();
    }

    private void ClearAll()
    {
        var deleted = 0;
        var failed = 0;

        foreach (var dir in LogDirectoryCandidates())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(dir))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _stack.Log.Warn($"删除日志文件失败：{file}", ex);
                }
            }
        }

        lock (_gate)
        {
            _lines.Clear();
        }

        LogText = string.Empty;
        _stack.LaunchLog.Clear();
        SourceText = failed == 0
            ? $"已删除 {deleted} 个日志文件"
            : $"已删除 {deleted} 个日志文件，{failed} 个删除失败（游戏运行中？）";
    }

    private void Append(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
            }
        }

        RebuildText();
    }

    private void RebuildText()
    {
        var builder = new StringBuilder();
        lock (_gate)
        {
            foreach (var item in _lines)
            {
                builder.AppendLine(item);
            }
        }

        LogText = builder.ToString();
    }
}
