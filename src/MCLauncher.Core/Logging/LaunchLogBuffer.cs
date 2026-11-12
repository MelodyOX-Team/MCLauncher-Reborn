using System.Text;
using MCLauncher.Core.Abstractions;

namespace MCLauncher.Core.Logging;

/// <summary>
/// 游戏输出的内存环形缓冲。UI 绑定它显示启动日志，避免把几千行日志塞进 ObservableCollection。
/// </summary>
public sealed class LaunchLogBuffer
{
    private readonly Queue<string> _lines;
    private readonly int _capacity;
    private readonly object _gate = new();

    public LaunchLogBuffer(int capacity = 2000)
    {
        _capacity = Math.Max(64, capacity);
        _lines = new Queue<string>(_capacity);
    }

    /// <summary>有新行时触发（在工作线程上，界面需自行 marshal）。</summary>
    public event Action<string>? LineAdded;

    /// <summary>
    /// 缓冲被清空时触发（例如每次启动游戏前）。界面据此丢掉旧内容，
    /// 避免上一次游戏的输出和 latest.log 的旧内容混在新一次启动里。
    /// </summary>
    public event Action? Cleared;

    public int Capacity => _capacity;

    /// <summary>
    /// 启动代次：每调用一次 <see cref="Clear"/> 加一。界面在启动之后才第一次创建时
    /// 可以据此判断"本次启动已经开始"，从而不再去读上一次的 latest.log。
    /// </summary>
    public int Generation { get; private set; }

    public void Append(string line)
    {
        if (line is null)
        {
            return;
        }

        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }

        try
        {
            LineAdded?.Invoke(line);
        }
        catch
        {
            // 订阅方异常不能影响游戏进程的读取循环。
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            Generation++;
        }

        try
        {
            Cleared?.Invoke();
        }
        catch
        {
            // 订阅方异常不能影响调用方。
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToArray();
        }
    }

    public string Join() => string.Join(Environment.NewLine, Snapshot());

    /// <summary>把缓冲区接成一个 <see cref="ILogSink"/>，方便与启动器日志共用同一出口。</summary>
    public ILogSink CreateSink() => new BufferSink(this);

    private sealed class BufferSink : ILogSink
    {
        private readonly LaunchLogBuffer _buffer;

        public BufferSink(LaunchLogBuffer buffer) => _buffer = buffer;

        public void Write(LogLevel level, string message) => _buffer.Append(message);
    }
}
