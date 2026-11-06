using System.Runtime.InteropServices;
using System.Security;

namespace MCLauncher.Core.Utils;

/// <summary>
/// Windows Job Object：把子进程挂到一个 Job 上，设置 KILL_ON_JOB_CLOSE 后，
/// 只要 Job 句柄被关闭（包括启动器进程崩溃、被 Task Manager 杀掉），
/// 内核会自动级联杀掉 Job 里所有的子进程，不会留下 frpc / 陶瓦 孤儿进程。
///
/// 在嵌套 Job 或受限环境里创建 Job Object 会失败（Windows 限制），
/// 此时 <see cref="IsAvailable"/> 为 false，服务方应降级为普通 Kill，
/// 功能不受影响，只是失去崩溃时的内核级自动清理。
/// </summary>
public sealed class JobObject : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>Job Object 是否创建成功。false 表示当前进程处于嵌套或受限环境，无法使用 Job Object。</summary>
    public bool IsAvailable => _handle != IntPtr.Zero;

    public JobObject()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            _logWarn("CreateJobObject", Marshal.GetLastWin32Error());
            return;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
            // 不设 ProcessMemoryLimit：某些环境下 KILL_ON_JOB_CLOSE + JOB_MEMORY 组合会被拒绝
        };

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(_handle, JobObjectInfoType.ExtendedLimitInformation, ref info, length))
        {
            _logWarn("SetInformationJobObject", Marshal.GetLastWin32Error());
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>把一个已启动的进程挂到 Job 上。之后进程的子进程也自动归入这个 Job。</summary>
    /// <exception cref="InvalidOperationException">Job Object 不可用或分配失败。</exception>
    public void AssignProcess(System.Diagnostics.Process process)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(JobObject));
        }

        if (!IsAvailable)
        {
            throw new InvalidOperationException("Job Object 不可用");
        }

        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new InvalidOperationException("AssignProcessToJobObject 失败：" + Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // CloseHandle 会触发 KILL_ON_JOB_CLOSE，内核自动干掉 Job 里所有子进程。
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static void _logWarn(string api, int error)
    {
        // 这里不挂 ILogger，因为 JobObject 是 Utils 层无依赖工具。
        // 失败的话服务方在 try/catch 里已经做了降级处理，不需要额外日志。
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[JobObject] {api} 失败：{error}");
#endif
    }

    // ---- Win32 常量与结构 ----

    private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public long ProcessMemoryLimit;
        public long JobMemoryLimit;
        public long PeakUsedProcessMemory;
        public long PeakUsedJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public int LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public int ActiveProcessLimit;
        public long Affinity;
        public int PriorityClass;
        public int SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [SuppressUnmanagedCodeSecurity]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [SuppressUnmanagedCodeSecurity]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoType JobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        int cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [SuppressUnmanagedCodeSecurity]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [SuppressUnmanagedCodeSecurity]
    private static extern bool CloseHandle(IntPtr hObject);
}
