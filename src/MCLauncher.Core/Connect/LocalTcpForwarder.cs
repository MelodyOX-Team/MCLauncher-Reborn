using System.Net;
using System.Net.Sockets;

namespace MCLauncher.Core.Connect;

/// <summary>
/// 房主本机的双栈 TCP 转发器：把公网/IPv6 入站连接原样接到 Minecraft 的局域网监听端口。
/// 只转发字节流，不解析、不改写游戏协议；最多 32 条并发连接。
/// </summary>
public sealed class LocalTcpForwarder : IAsyncDisposable
{
    private const int MaxConnections = 32;

    private readonly Socket _listener;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _connections;
    private int _disposed;

    private LocalTcpForwarder(Socket listener, int targetPort)
    {
        _listener = listener;
        _targetPort = targetPort;
        Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>对外监听端口。</summary>
    public int Port { get; }

    public int Connections => Volatile.Read(ref _connections);

    public static LocalTcpForwarder Start(int targetPort)
    {
        if (!DirectProtocol.IsValidPort(targetPort))
        {
            throw new ArgumentOutOfRangeException(nameof(targetPort), "目标端口必须在 1–65535 之间");
        }

        Socket listener;
        try
        {
            listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
            {
                DualMode = true,
            };

            try
            {
                listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                listener.Listen(64);
            }
            catch
            {
                // Bind 或 Listen 失败都要立刻释放，否则回退 IPv4 前会泄漏一个句柄。
                listener.Dispose();
                throw;
            }
        }
        catch (SocketException)
        {
            // 系统未启用 IPv6 时退回 IPv4。
            listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, 0));
                listener.Listen(64);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        return new LocalTcpForwarder(listener, targetPort);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            if (Volatile.Read(ref _connections) >= MaxConnections)
            {
                client.Dispose();
                continue;
            }

            Interlocked.Increment(ref _connections);
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(Socket client)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            NetworkStream? inbound = null;
            NetworkStream? outbound = null;

            try
            {
                // 联机是小型交互报文，禁用 Nagle 可减少额外延迟。
                client.NoDelay = true;

                await upstream
                    .ConnectAsync(IPAddress.Loopback, _targetPort, _cts.Token)
                    .ConfigureAwait(false);

                upstream.NoDelay = true;

                inbound = new NetworkStream(client, ownsSocket: false);
                outbound = upstream.GetStream();

                var up = PumpAsync(inbound, outbound, upstream.Client);
                var down = PumpAsync(outbound, inbound, client);

                // 半关闭：两个方向都读到 EOF 才算结束，单方向 EOF 只关目标的写方向。
                // 再给一个短暂排空上限，防止对端半开时连接永久占用。
                try
                {
                    await Task.WhenAll(up, down)
                        .WaitAsync(TimeSpan.FromSeconds(15), _cts.Token)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 排空超时，交给 finally 关闭两端。
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                // 对端关闭、游戏世界关闭或转发器停止，直接回收连接。
            }
            finally
            {
                Interlocked.Decrement(ref _connections);

                try
                {
                    inbound?.Dispose();
                }
                catch (IOException)
                {
                }

                try
                {
                    outbound?.Dispose();
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private async Task PumpAsync(NetworkStream source, NetworkStream destination, Socket destinationSocket)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    // 源端读到 EOF：只对目标端关写方向，另一方向仍可继续传完剩余数据。
                    try
                    {
                        destinationSocket.Shutdown(SocketShutdown.Send);
                    }
                    catch (SocketException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    return;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), _cts.Token).ConfigureAwait(false);
                await destination.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        try
        {
            _listener.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
