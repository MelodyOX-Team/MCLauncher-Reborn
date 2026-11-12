using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using MCLauncher.Core.Abstractions;

namespace MCLauncher.Core.Connect;

/// <summary>
/// 好友直连房间：检测局域网端口、启动双栈转发器、尝试 UPnP 映射，生成 24 小时有效的邀请。
/// 不依赖任何外部服务，也不做 DNS 解析；网络路径只有局域网、公网 IPv6 与路由器映射。
/// </summary>
public sealed class DirectConnectService : IDisposable
{
    private static readonly TimeSpan NetworkCacheLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan MappingLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MappingRenewInterval = TimeSpan.FromMinutes(2);

    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Session? _session;
    private DirectHostState _idleState = new();
    private DirectNetworkInfo? _cachedNetwork;
    private IReadOnlyList<UpnpGateway> _cachedGateways = Array.Empty<UpnpGateway>();
    private DateTimeOffset _cachedAt;

    public DirectConnectService(ILogger log)
    {
        _log = log;
    }

    public event EventHandler<DirectHostState>? StateChanged;

    public DirectHostState State
    {
        get
        {
            var session = _session;
            return session is null ? _idleState : Snapshot(session);
        }
    }

    /// <summary>扫描本机地址与 UPnP 网关；结果缓存 60 秒。</summary>
    public async Task<DirectNetworkInfo> InspectNetworkAsync(
        bool force = false,
        CancellationToken ct = default)
    {
        if (!force && _cachedNetwork is not null
            && DateTimeOffset.UtcNow - _cachedAt < NetworkCacheLifetime)
        {
            return _cachedNetwork;
        }

        var addresses = CollectAddresses();
        var gateways = await UpnpGateway.DiscoverAsync(_log, ct).ConfigureAwait(false);
        var gatewayInfos = new List<UpnpGatewayInfo>();
        var messages = new List<string>
        {
            "公网可达性需要好友从另一个网络实际连接验证，本机检测无法确认运营商与防火墙是否放行。",
        };

        foreach (var gateway in gateways)
        {
            var external = gateway.ExternalAddress;
            var scope = string.IsNullOrWhiteSpace(external)
                ? (Ipv4Scope?)null
                : DirectProtocol.ClassifyIpv4(external!);

            gatewayInfos.Add(new UpnpGatewayInfo
            {
                ControlHost = gateway.ControlHost,
                ExternalAddress = external,
                CanMap = scope == Ipv4Scope.Public,
                Diagnosis = scope switch
                {
                    Ipv4Scope.Public => "发现公网 IPv4，开启房间时可以尝试 UPnP 映射。",
                    Ipv4Scope.Cgnat => "网关 WAN 地址属于运营商级 NAT（100.64.0.0/10），UPnP 无法穿透运营商。",
                    Ipv4Scope.Private => "网关 WAN 仍是私网地址，存在上级 NAT；只映射本层路由器不能保证公网可达。",
                    _ => "网关未提供可用的公网 IPv4。",
                },
            });
        }

        if (!addresses.Any(address => address.Kind == DirectEndpointKind.Ipv6))
        {
            messages.Add("未发现公网 IPv6 地址。");
        }

        if (gateways.Count == 0)
        {
            messages.Add("未发现可用 UPnP 网关：路由器可能未开启 UPnP，或当前网络不支持。可使用公网 IPv6、手动端口映射，或改用樱花穿透/陶瓦联机。");
        }

        _cachedNetwork = new DirectNetworkInfo
        {
            Addresses = addresses,
            Gateways = gatewayInfos,
            Messages = messages,
        };
        _cachedGateways = gateways;
        _cachedAt = DateTimeOffset.UtcNow;

        return _cachedNetwork;
    }

    /// <summary>读取实例 <c>logs/latest.log</c>，找出最后一次「对局域网开放」的端口。</summary>
    public async Task<int?> DetectLanPortAsync(string instanceDirectory, CancellationToken ct = default)
    {
        var path = Path.Combine(instanceDirectory, "logs", "latest.log");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous);

            var length = stream.Length;
            var readLength = (int)Math.Min(length, 256 * 1024);
            if (readLength <= 0)
            {
                return null;
            }

            stream.Seek(length - readLength, SeekOrigin.Begin);
            var buffer = new byte[readLength];
            var total = 0;

            while (total < readLength)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return DirectProtocol.DetectLanPort(Encoding.UTF8.GetString(buffer, 0, total));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"读取游戏日志失败：{path}", ex);
            return null;
        }
    }

    public async Task<DirectHostState> StartHostAsync(
        DirectHostRequest request,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        LocalTcpForwarder? forwarder = null;
        var mappings = new List<Mapping>();

        try
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("已有房间在运行，请先关闭当前房间。");
            }

            var port = request.Port ?? await DetectLanPortAsync(request.InstanceDirectory, ct).ConfigureAwait(false);
            if (port is null || !DirectProtocol.IsValidPort(port.Value))
            {
                throw new InvalidOperationException(
                    "没有检测到局域网端口。请先在游戏里选择「对局域网开放」，然后刷新端口或手动填写。");
            }

            if (!await DirectProtocol
                    .ProbeTcpAsync("127.0.0.1", port.Value, ProbeTimeout, ct)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "本机世界端口未开放。请在游戏里选择「对局域网开放」，然后刷新端口或手动输入。");
            }

            var network = await InspectNetworkAsync(ct: ct).ConfigureAwait(false);
            forwarder = LocalTcpForwarder.Start(port.Value);

            var messages = new List<string>(network.Messages);
            var endpoints = new List<DirectEndpoint>();

            if (request.UseUpnp)
            {
                foreach (var gateway in _cachedGateways
                             .Where(item => item.ExternalAddress is not null
                                            && DirectProtocol.ClassifyIpv4(item.ExternalAddress!) == Ipv4Scope.Public)
                             .Take(2))
                {
                    ct.ThrowIfCancellationRequested();

                    var internalClient = FindInternalClientFor(gateway.ControlHost);
                    if (internalClient is null)
                    {
                        messages.Add("找不到与 UPnP 网关同网段的 IPv4 地址，跳过映射。");
                        continue;
                    }

                    var description = $"MCLR-{Random.Shared.Next(1, 0x7FFFFFFF):X8}";

                    try
                    {
                        await gateway
                            .AddPortMappingAsync(
                                forwarder.Port,
                                internalClient,
                                description,
                                (int)MappingLease.TotalSeconds,
                                ct)
                            .ConfigureAwait(false);

                        mappings.Add(new Mapping(gateway, internalClient, forwarder.Port, description));
                        endpoints.Add(new DirectEndpoint(gateway.ExternalAddress!, forwarder.Port, DirectEndpointKind.Ipv4));
                        messages.Add($"UPnP 临时映射已建立（{MappingLease.TotalMinutes:0} 分钟租约，开房期间自动续租）。");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
                                                   or TaskCanceledException or IOException)
                    {
                        messages.Add($"UPnP 映射失败：{ex.Message}");
                        _log.Warn("UPnP 端口映射失败", ex);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(request.PublicAddress))
            {
                var address = request.PublicAddress.Trim();
                if (DirectProtocol.ClassifyIpv4(address) != Ipv4Scope.Public)
                {
                    throw new InvalidOperationException("手动填写的公网 IPv4 地址格式无效。");
                }

                endpoints.Add(new DirectEndpoint(address, forwarder.Port, DirectEndpointKind.Ipv4));
                messages.Add($"手动 IPv4：请确认路由器已把 TCP {forwarder.Port} 映射到本机同端口。");
            }

            foreach (var address in network.Addresses)
            {
                endpoints.Add(new DirectEndpoint(address.Address, forwarder.Port, address.Kind));
            }

            var unique = endpoints
                .DistinctBy(endpoint => endpoint.Address)
                .Take(DirectProtocol.MaxEndpoints)
                .ToList();

            if (!unique.Any(endpoint => endpoint.Kind != DirectEndpointKind.Lan))
            {
                messages.Add("当前仅可供同一局域网加入，尚无公网房主路径。可改用樱花穿透或陶瓦联机。");
            }

            messages.Add("开启房间后需要保持启动器和游戏运行；退出游戏或关闭局域网世界会断开直连。");

            var invitation = new DirectInvitation
            {
                Name = request.InstanceName,
                MinecraftVersion = request.MinecraftVersion,
                Loader = NormalizeLoader(request.Loader),
                LoaderVersion = string.IsNullOrWhiteSpace(request.LoaderVersion) ? null : request.LoaderVersion.Trim(),
                Endpoints = unique,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            };

            var cts = new CancellationTokenSource();
            var session = new Session
            {
                Forwarder = forwarder,
                Mappings = mappings,
                LocalPort = port.Value,
                Endpoints = unique,
                Invite = unique.Count > 0 ? DirectProtocol.EncodeInvitation(invitation) : null,
                Messages = messages,
                StartedAt = DateTimeOffset.UtcNow,
                Monitor = cts,
            };

            session.MonitorTask = Task.Run(() => MonitorAsync(session));
            _session = session;
            _idleState = Snapshot(session);

            _log.Info($"好友直连房间已开启：游戏端口 {port.Value}，对外端口 {forwarder.Port}，"
                      + $"候选地址 {unique.Count} 个");

            RaiseStateChanged();
            return State;
        }
        catch
        {
            // 映射建立后若后续步骤失败（例如手动填写的公网地址无效），
            // 要立刻逐个删除已建立的映射，不能等 5 分钟租约到期才释放端口。
            foreach (var mapping in mappings)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await mapping.Gateway
                        .DeletePortMappingAsync(mapping.Port, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
                                               or OperationCanceledException or IOException)
                {
                    _log.Warn("回滚 UPnP 映射失败，租约到期后会自动失效", ex);
                }
            }

            if (forwarder is not null)
            {
                await forwarder.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DirectHostState> StopHostAsync(
        string? message = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var session = _session;
            _session = null;

            if (session is not null)
            {
                session.Monitor.Cancel();
                await session.Forwarder.DisposeAsync().ConfigureAwait(false);

                foreach (var mapping in session.Mappings)
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(3));
                        await mapping.Gateway
                            .DeletePortMappingAsync(mapping.Port, cts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
                                                   or OperationCanceledException or IOException)
                    {
                        _log.Warn("删除 UPnP 映射失败，租约到期后会自动失效", ex);
                    }
                }

                session.Monitor.Dispose();
            }

            _idleState = new DirectHostState
            {
                Active = false,
                Messages = new[]
                {
                    message ?? "房间已关闭。",
                    "下一次开启会分配新的对外端口，公网地址变化后请重新发送邀请。",
                },
            };

            RaiseStateChanged();
            return _idleState;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>解析邀请并探测所有候选地址，返回第一个可达的。</summary>
    public async Task<DirectJoinResult> ResolveInvitationAsync(
        string input,
        CancellationToken ct = default)
    {
        var invitation = DirectProtocol.ParseInvitation(input);

        var ordered = invitation.Endpoints
            .OrderBy(endpoint => endpoint.Kind switch
            {
                DirectEndpointKind.Ipv6 => 0,
                DirectEndpointKind.Ipv4 => 1,
                _ => 2,
            })
            .ToList();

        var probes = await Task.WhenAll(ordered.Select(async endpoint => (
            Endpoint: endpoint,
            Online: await DirectProtocol
                .ProbeTcpAsync(endpoint.Host, endpoint.Port, ProbeTimeout, ct)
                .ConfigureAwait(false)))).ConfigureAwait(false);

        var reachable = probes.FirstOrDefault(probe => probe.Online).Endpoint;
        var failures = probes
            .Where(probe => !probe.Online)
            .Select(probe => $"{probe.Endpoint.Address}：连接超时或被拒绝")
            .ToList();

        return new DirectJoinResult
        {
            Invitation = invitation,
            Endpoint = reachable,
            Failures = failures,
        };
    }

    private async Task MonitorAsync(Session session)
    {
        var lastRenew = DateTimeOffset.UtcNow;
        var probeFailures = 0;

        while (!session.Monitor.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), session.Monitor.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (session.Monitor.IsCancellationRequested)
            {
                return;
            }

            var alive = await DirectProtocol
                .ProbeTcpAsync("127.0.0.1", session.LocalPort, TimeSpan.FromSeconds(2), session.Monitor.Token)
                .ConfigureAwait(false);

            if (!alive)
            {
                // 单次探测失败可能只是游戏短暂卡顿（GC、加载区块），
                // 连续两次都失败才判定世界已关闭，避免误拆房间。
                probeFailures++;
                if (probeFailures < 2)
                {
                    continue;
                }

                await StopHostAsync("本机世界已关闭，房间与端口映射已停止。").ConfigureAwait(false);
                return;
            }

            probeFailures = 0;

            if (DateTimeOffset.UtcNow - lastRenew < MappingRenewInterval)
            {
                continue;
            }

            lastRenew = DateTimeOffset.UtcNow;

            foreach (var mapping in session.Mappings)
            {
                try
                {
                    await mapping.Gateway
                        .AddPortMappingAsync(
                            mapping.Port,
                            mapping.InternalClient,
                            mapping.Description,
                            (int)MappingLease.TotalSeconds,
                            session.Monitor.Token)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (IsMappingConflict(ex))
                {
                    // 路由器报 718（ConflictInMappingEntry）：映射已存在，视为续租成功。
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
                                               or OperationCanceledException or IOException)
                {
                    AddMessage(session, $"UPnP 续租失败：{ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// UPnP 错误 718 / ConflictInMappingEntry：相同外部端口已有映射。
    /// 续租场景下这说明目标映射已经存在，可直接按成功处理。
    /// </summary>
    private static bool IsMappingConflict(Exception ex) =>
        ex.Message.Contains("ConflictInMappingEntry", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("UPnP 错误 718", StringComparison.OrdinalIgnoreCase);

    private void AddMessage(Session session, string message)
    {
        if (session.Messages.Contains(message))
        {
            return;
        }

        session.Messages.Add(message);
        if (session.Messages.Count > 24)
        {
            session.Messages.RemoveAt(0);
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, State);

    private DirectHostState Snapshot(Session session) => new()
    {
        Active = true,
        Endpoints = session.Endpoints,
        Invite = session.Invite,
        LocalPort = session.LocalPort,
        ExposedPort = session.Forwarder.Port,
        Connections = session.Forwarder.Connections,
        Messages = session.Messages.ToArray(),
        StartedAt = session.StartedAt,
    };

    private static IReadOnlyList<DirectAddressInfo> CollectAddresses()
    {
        var result = new List<DirectAddressInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var scope = DirectProtocol.ClassifyIpv4(address.ToString());
                    if (scope is not (Ipv4Scope.Private or Ipv4Scope.Public))
                    {
                        continue;
                    }

                    var display = address.ToString();
                    if (seen.Add(display))
                    {
                        result.Add(new DirectAddressInfo
                        {
                            InterfaceName = networkInterface.Name,
                            Address = display,
                            Kind = scope == Ipv4Scope.Public ? DirectEndpointKind.Ipv4 : DirectEndpointKind.Lan,
                        });
                    }
                }
                else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    var display = address.ToString();
                    if (DirectProtocol.IsGlobalIpv6(display) && seen.Add(display))
                    {
                        result.Add(new DirectAddressInfo
                        {
                            InterfaceName = networkInterface.Name,
                            Address = display,
                            Kind = DirectEndpointKind.Ipv6,
                        });
                    }
                }
            }
        }

        return result;
    }

    /// <summary>找出与 UPnP 网关同网段的本机 IPv4，作为映射的内部地址。</summary>
    private static string? FindInternalClientFor(string gatewayHost)
    {
        if (!IPAddress.TryParse(gatewayHost, out var gateway) || gateway.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                    || unicast.IPv4Mask is null
                    || DirectProtocol.ClassifyIpv4(unicast.Address.ToString()) is not (Ipv4Scope.Private))
                {
                    continue;
                }

                var local = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var remote = gateway.GetAddressBytes();

                if (local.Length != 4 || mask.Length != 4 || remote.Length != 4)
                {
                    continue;
                }

                var sameNetwork = true;
                for (var index = 0; index < 4; index++)
                {
                    if ((local[index] & mask[index]) != (remote[index] & mask[index]))
                    {
                        sameNetwork = false;
                        break;
                    }
                }

                if (sameNetwork)
                {
                    return unicast.Address.ToString();
                }
            }
        }

        // 没有同网段地址时返回 null，由调用方跳过该网关；
        // 用其它网卡的地址建映射只会得到一条并不可达的“成功”记录。
        return null;
    }

    private static string? NormalizeLoader(string? loader)
    {
        if (string.IsNullOrWhiteSpace(loader))
        {
            return null;
        }

        var value = loader.Trim();
        if (value.Equals("原版", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.ToLowerInvariant();
    }

    public void Dispose()
    {
        var session = _session;
        _session = null;
        if (session is null)
        {
            return;
        }

        session.Monitor.Cancel();

        // 退出阶段不能无限期等待：转发器最多等 2 秒，超时交给进程回收。
        try
        {
            if (!session.Forwarder.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)))
            {
                _log.Warn("退出时等待直连转发器关闭超时");
            }
        }
        catch (AggregateException ex)
        {
            _log.Warn("退出时关闭直连转发器失败", ex);
        }

        session.Monitor.Dispose();

        // 删除映射只是尽力而为，租约到期后路由器会自行回收。
        _ = Task.WhenAll(session.Mappings.Select(async mapping =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await mapping.Gateway.DeletePortMappingAsync(mapping.Port, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 退出阶段不再打扰用户。
            }
        }));
    }

    private sealed record Mapping(
        UpnpGateway Gateway,
        string InternalClient,
        int Port,
        string Description);

    private sealed class Session
    {
        public required LocalTcpForwarder Forwarder { get; init; }

        public required List<Mapping> Mappings { get; init; }

        public required int LocalPort { get; init; }

        public required IReadOnlyList<DirectEndpoint> Endpoints { get; init; }

        public required string? Invite { get; init; }

        public required List<string> Messages { get; init; }

        public required DateTimeOffset StartedAt { get; init; }

        public required CancellationTokenSource Monitor { get; init; }

        public Task MonitorTask { get; set; } = Task.CompletedTask;
    }
}
