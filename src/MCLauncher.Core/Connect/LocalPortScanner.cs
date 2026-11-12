using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace MCLauncher.Core.Connect;

/// <summary>本机正在监听的一个 TCP 端口。</summary>
public sealed record LocalListeningPort(int Port, string ProcessName);

/// <summary>
/// 枚举本机处于 LISTEN 状态的 TCP 端口（IPv4），带上持有它的进程名。
/// 直接读 iphlpapi 的 TCP 表，避免起 netstat 子进程、也不会被本地化输出影响。
/// 用于「新建樱花穿透隧道」时给本地端口做候选。
/// </summary>
public static class LocalPortScanner
{
    private const int AddressFamilyInet = 2;

    /// <summary>TCP_TABLE_OWNER_PID_LISTENER，行结构含 OwningPid。</summary>
    private const int TableClassListener = 3;

    /// <summary>MIB_TCPROW_OWNER_PID：5 个 DWORD + PID = 24 字节。</summary>
    private const int RowSize = 24;

    /// <summary>ERROR_INSUFFICIENT_BUFFER：两次取表之间监听端口变多，缓冲区不够。</summary>
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>按端口升序返回，重复端口（同一端口绑了多个地址）只保留一条。</summary>
    public static IReadOnlyList<LocalListeningPort> GetListeningTcpPorts()
    {
        var buffer = IntPtr.Zero;

        try
        {
            var size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AddressFamilyInet, TableClassListener, 0);
            if (size <= 0)
            {
                return Array.Empty<LocalListeningPort>();
            }

            buffer = Marshal.AllocHGlobal(size);
            var result = 0;

            // 第一次取表只是探长度，紧跟着的取表可能仍返回 122；
            // 此时 size 已被写成新长度，按新长度重新分配再取，避免直接返回空列表。
            for (var attempt = 0; attempt < 3; attempt++)
            {
                result = GetExtendedTcpTable(buffer, ref size, false, AddressFamilyInet, TableClassListener, 0);
                if (result != ErrorInsufficientBuffer || size <= 0)
                {
                    break;
                }

                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
            }

            if (result != 0)
            {
                return Array.Empty<LocalListeningPort>();
            }

            var count = Marshal.ReadInt32(buffer);
            var table = IntPtr.Add(buffer, 4);
            var processNames = new Dictionary<int, string>();
            var ports = new Dictionary<int, LocalListeningPort>();

            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(table, i * RowSize);
                var port = ReadLocalPort(row);
                if (port == 0 || ports.ContainsKey(port))
                {
                    continue;
                }

                var pid = Marshal.ReadInt32(row, 20);
                if (!processNames.TryGetValue(pid, out var name))
                {
                    name = ResolveProcessName(pid);
                    processNames[pid] = name;
                }

                ports[port] = new LocalListeningPort(port, name);
            }

            return ports.Values.OrderBy(p => p.Port).ToList();
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                       or EntryPointNotFoundException
                                       or MarshalDirectiveException
                                       or OutOfMemoryException)
        {
            return Array.Empty<LocalListeningPort>();
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary><c>dwLocalPort</c> 用网络字节序存低 16 位。</summary>
    private static ushort ReadLocalPort(IntPtr row) =>
        (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));

    private static string ResolveProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        bool order,
        int addressFamily,
        int tableClass,
        int reserved);
}
