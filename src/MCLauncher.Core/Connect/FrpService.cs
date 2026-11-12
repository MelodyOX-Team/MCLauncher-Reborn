using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Connect;

public enum FrpStatusKind
{
    Idle,
    Downloading,
    Starting,
    Running,
    AuthFailed,
    TunnelOffline,
    Error,
    Stopped,
}

/// <summary>樱花穿透（SakuraFrp）运行状态快照（多隧道汇总）。</summary>
public sealed class FrpState
{
    public FrpStatusKind Status { get; init; }

    /// <summary>第一个已连通隧道的远程地址（兼容单隧道展示）。</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>所有已连通隧道的远程地址，key = 隧道 id。</summary>
    public IReadOnlyDictionary<int, string> RemoteAddresses { get; init; } = new Dictionary<int, string>();

    public string Message { get; init; } = string.Empty;

    public int RunningTunnelCount { get; init; }

    public IReadOnlyList<string> Logs { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 樱花穿透 frpc 集成：使用 SakuraFrp 官方 frpc 客户端，支持同时启动多条隧道（多进程）。
/// 每个隧道独立一个 frpc 进程，共用同一个 Job Object（启动器崩溃时内核级联清理）。
/// </summary>
public sealed class FrpService : IDisposable
{
    private const string FrpcUrl =
        "https://nya.globalslb.net/natfrp/client/frpc/0.51.0-sakura-14/frpc_windows_amd64.exe";

    private const string FrpcSha256 = "b705262edad9f0de04b38f342e811e9d97d0beada75edab3f790e105dcfb5234";

    private const long FrpcSize = 14_133_248;

    private const int MaxLogLines = 300;

    private static readonly Regex AnsiPattern = new(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private static readonly Regex AddressPattern = new(
        @"(?<host>(?:[a-z0-9-]+\.)+[a-z0-9-]+)\s*:\s*(?<port>\d{1,5})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex KnownHostPattern = new(
        @"(?:[\w-]+\.)*(?:natfrp\.cloud|nyatwork\.cn|frp-hub\.com|frp\.com|frp\.net)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ContextPattern = new(
        @"需要输\s*IP\s*[:：]\s*端口|远程地址|remote address|(?:电信|联通|移动)\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger _log;
    private readonly string _directory;
    private readonly HttpDownloader _downloader;
    private readonly bool _ownsDownloader;
    private readonly List<string> _logs = new();
    private readonly object _logLock = new();
    private readonly JobObject _job;

    /// <summary>保护 <see cref="_processes"/> 与 <see cref="_remoteAddresses"/> 的唯一锁。</summary>
    private readonly object _processLock = new();

    /// <summary>隧道 id → 进程。多进程并存，每个对应一条勾选的隧道。</summary>
    private readonly Dictionary<string, Process> _processes = new();

    /// <summary>隧道 id → 已解析出的远程地址。用于汇总状态。</summary>
    private readonly Dictionary<int, string> _remoteAddresses = new();

    private FrpState _state = new() { Status = FrpStatusKind.Idle, Message = "尚未启动" };
    private int _epoch;
    private bool _binaryVerified;

    public FrpService(ILogger log, string configDirectory, HttpDownloader? downloader = null)
    {
        _log = log;
        _directory = Path.Combine(configDirectory, "frp");
        _downloader = downloader ?? new HttpDownloader(log);
        _ownsDownloader = downloader is null;
        _job = new JobObject();
    }

    public FrpState State => _state;

    public event EventHandler<FrpState>? StateChanged;

    /// <summary>每收到一行 frpc 日志时触发（已脱敏，带隧道 id 前缀）。</summary>
    public event EventHandler<string>? LogReceived;

    public string BinaryPath => Path.Combine(_directory, "frpc.exe");

    public bool IsBinaryReady()
    {
        if (_binaryVerified) return File.Exists(BinaryPath);

        if (!File.Exists(BinaryPath)
            || !IsLikelyFrpc(BinaryPath)
            || !HashUtil.VerifySha256(BinaryPath, FrpcSha256))
        {
            return false;
        }

        _binaryVerified = true;
        return true;
    }

    /// <summary>下载官方 frpc（已存在时直接复用）。</summary>
    public async Task EnsureBinaryAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var path = BinaryPath;
        if (IsBinaryReady()) return;

        Directory.CreateDirectory(_directory);

        SetStateSafe(new FrpState
        {
            Status = FrpStatusKind.Downloading,
            Message = "正在下载樱花穿透官方客户端…",
            Logs = SnapshotLogs(),
        });
        log?.Invoke("正在下载樱花穿透官方客户端…");

        try
        {
            if (File.Exists(path)) TryDelete(path);
            _binaryVerified = false;

            var outcome = await _downloader
                .DownloadAsync(
                    new[] { new DownloadCandidate(FrpcUrl, "SakuraFrp 官方") },
                    path,
                    FileChecker.OfSize(FrpcSize),
                    null,
                    ct)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                throw new InvalidDataException(outcome.Error ?? "下载没有成功");
            }

            if (!IsLikelyFrpc(path) || !HashUtil.VerifySha256(path, FrpcSha256))
            {
                TryDelete(path);
                throw new InvalidDataException("frpc 完整性校验失败（SHA-256 与固定值不符）");
            }

            _binaryVerified = true;
            log?.Invoke("frpc 下载完成");
            _log.Info($"frpc 已就绪：{path}");

            SetStateSafe(new FrpState
            {
                Status = FrpStatusKind.Idle,
                Message = "frpc 已就绪",
                Logs = SnapshotLogs(),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(path);
            SetStateSafe(new FrpState
            {
                Status = FrpStatusKind.Error,
                Message = "frpc 下载已取消",
                Logs = SnapshotLogs(),
            });
            throw;
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or InvalidDataException)
        {
            TryDelete(path);
            SetStateSafe(new FrpState
            {
                Status = FrpStatusKind.Error,
                Message = "frpc 下载失败",
                Logs = SnapshotLogs(),
            });
            throw new InvalidOperationException(
                $"frpc 下载失败：{ex.Message}。请检查网络后重试；也可以手动下载后放到 {_directory}", ex);
        }
    }

    /// <summary>
    /// 批量启动多条隧道。每个隧道独立一个 frpc 进程。
    /// 已在运行但不在新列表里的隧道会被停止。
    /// </summary>
    public async Task<FrpState> StartAsync(
        string accessKey,
        IReadOnlyList<string> tunnelIds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            throw new InvalidOperationException("请填写樱花穿透的访问密钥。");
        }

        if (tunnelIds is null || tunnelIds.Count == 0)
        {
            throw new InvalidOperationException("请先勾选至少一条隧道。");
        }

        var key = accessKey.Trim();
        var ids = tunnelIds.Select(id => id.Trim())
                           .Where(id => !string.IsNullOrWhiteSpace(id))
                           .Distinct()
                           .ToList();

        if (ids.Count == 0)
        {
            throw new InvalidOperationException("没有有效的隧道 ID。");
        }

        var epoch = ++_epoch;

        await EnsureBinaryAsync(ct: ct).ConfigureAwait(false);
        if (epoch != _epoch)
        {
            throw new OperationCanceledException("启动已取消");
        }

        // 停掉不再需要的隧道（用户之前勾过、现在没勾的）
        List<string> toStop;
        lock (_processLock)
        {
            toStop = _processes.Keys.Except(ids).ToList();
        }

        foreach (var id in toStop)
        {
            await StopOneAsync(id).ConfigureAwait(false);
        }

        SetStateSafe(new FrpState
        {
            Status = FrpStatusKind.Starting,
            Message = ids.Count == 1
                ? "正在连接樱花穿透…"
                : $"正在连接樱花穿透（{ids.Count} 条隧道）…",
            Logs = SnapshotLogs(),
        });

        ClearLogs();
        AppendLog($"批量启动命令：frpc -f {MaskKey(key)}:<隧道ID> --disable_log_color（共 {ids.Count} 条）");

        foreach (var tunnelId in ids)
        {
            if (epoch != _epoch)
            {
                throw new OperationCanceledException("启动已取消");
            }

            // 这条已经在跑了，跳过。
            bool alreadyRunning;
            lock (_processLock)
            {
                alreadyRunning = _processes.ContainsKey(tunnelId);
            }

            if (alreadyRunning)
            {
                continue;
            }

            var process = CreateProcess(key, tunnelId, epoch);

            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    AppendLog($"[隧道 #{tunnelId}] 无法启动 frpc 进程");
                    _log.Warn($"隧道 #{tunnelId} 无法启动 frpc 进程");
                    continue;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                process.Dispose();
                AppendLog($"[隧道 #{tunnelId}] 启动失败：{ex.Message}");
                _log.Warn($"隧道 #{tunnelId} 启动失败", ex);
                continue;
            }

            // 挂到 Job Object。Job Object 整个服务共用一个句柄，多条进程都放进去。
            try
            {
                _job.AssignProcess(process);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.Warn("frpc 未成功挂到 Job Object，崩溃时可能残留子进程", ex);
            }

            // 必须先登记再开始读输出：进程可能瞬间退出，
            // 若 Exited 回调早于登记触发，HandleExit 的引用校验会找不到当前进程而漏掉清理。
            lock (_processLock)
            {
                _processes[tunnelId] = process;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _log.Info($"已启动樱花穿透 frpc（PID {process.Id}，隧道 #{tunnelId}）");
        }

        RebuildState();
        return _state;
    }

    /// <summary>停止所有隧道。</summary>
    public async Task<FrpState> StopAsync()
    {
        ++_epoch;

        List<string> runningIds;
        lock (_processLock)
        {
            runningIds = _processes.Keys.ToList();
        }

        foreach (var id in runningIds)
        {
            await StopOneAsync(id).ConfigureAwait(false);
        }

        lock (_processLock)
        {
            _remoteAddresses.Clear();
        }

        SetStateSafe(new FrpState
        {
            Status = FrpStatusKind.Stopped,
            Message = "已停止",
            Logs = SnapshotLogs(),
        });

        return _state;
    }

    /// <summary>停止某一条隧道（其他不受影响）。</summary>
    public async Task<FrpState> StopOneAsync(string tunnelId)
    {
        var key = tunnelId.Trim();

        Process? process = null;
        lock (_processLock)
        {
            if (_processes.TryGetValue(key, out var tracked))
            {
                process = tracked;
                _processes.Remove(key);
                _remoteAddresses.Remove(int.TryParse(key, out var n) ? n : 0);
            }
        }

        if (process is null)
        {
            RebuildState();
            return _state;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            _log.Warn($"停止隧道 #{key} 超时或进程已退出", ex);
        }
        finally
        {
            process.Dispose();
        }

        AppendLog($"[隧道 #{key}] 已停止");
        RebuildState();
        return _state;
    }

    private Process CreateProcess(string accessKey, string tunnelId, int epoch)
    {
        var startInfo = new ProcessStartInfo(BinaryPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add($"{accessKey}:{tunnelId}");
        startInfo.ArgumentList.Add("--disable_log_color");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, args) => HandleLine(accessKey, tunnelId, epoch, args.Data);
        process.ErrorDataReceived += (_, args) => HandleLine(accessKey, tunnelId, epoch, args.Data);
        process.Exited += (_, _) => HandleExit(epoch, tunnelId, process);

        return process;
    }

    private void HandleLine(string accessKey, string tunnelId, int epoch, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        var line = AnsiPattern.Replace(raw, string.Empty).Trim();
        var sanitized = line.Replace(accessKey, MaskKey(accessKey), StringComparison.Ordinal);
        AppendLog($"[隧道 #{tunnelId}] {sanitized}");

        if (epoch != _epoch) return;

        var status = ParseStatus(line);
        var address = ParseAddress(line);
        var tunnelKey = int.TryParse(tunnelId, out var n) ? n : 0;

        if (address is not null)
        {
            lock (_processLock)
            {
                _remoteAddresses[tunnelKey] = address;
            }
        }

        if (status is not null)
        {
            AppendLog($"[隧道 #{tunnelId}] 状态变更：{status}");
        }

        RebuildState(status);
    }

    private void HandleExit(int epoch, string tunnelId, Process process)
    {
        if (epoch != _epoch) return;

        var code = TryReadExitCode(process);
        AppendLog($"[隧道 #{tunnelId}] frpc 已退出（code={code}）");
        _log.Info($"隧道 #{tunnelId} frpc 已退出（code={code}）");

        // 只认当前登记的同一条进程：旧进程的退出事件可能晚到，
        // 若不校验引用，会把刚启动的新进程从表里误删。
        lock (_processLock)
        {
            if (_processes.TryGetValue(tunnelId, out var current) && ReferenceEquals(current, process))
            {
                _processes.Remove(tunnelId);
                if (int.TryParse(tunnelId, out var n))
                {
                    _remoteAddresses.Remove(n);
                }
            }
        }

        try { process.Dispose(); } catch { }

        RebuildState();
    }

    /// <summary>按当前所有存活进程 + 已解析地址重建汇总状态。</summary>
    private void RebuildState(FrpStatusKind? forceStatus = null)
    {
        // 先在锁内取快照，计算与事件触发都放到锁外，避免回调里再次进锁造成死锁。
        List<Process> alive;
        Dictionary<int, string> remoteAddrs;
        lock (_processLock)
        {
            alive = _processes.Values.Where(p => !p.HasExited).ToList();
            remoteAddrs = new Dictionary<int, string>(_remoteAddresses);
        }

        var total = alive.Count;
        var connected = remoteAddrs.Count;

        // 全部进程都退出了 → Error
        if (total == 0)
        {
            var finalStatus = forceStatus ?? FrpStatusKind.Error;
            var finalMessage = finalStatus switch
            {
                FrpStatusKind.AuthFailed => "访问密钥无效或已失效",
                FrpStatusKind.TunnelOffline => "某些隧道不存在或已被禁用",
                _ => connected > 0 ? "所有隧道已断开" : "frpc 进程已全部退出",
            };

            SetStateSafe(new FrpState
            {
                Status = finalStatus,
                Message = finalMessage,
                RemoteAddress = remoteAddrs.Values.FirstOrDefault(),
                RemoteAddresses = remoteAddrs,
                RunningTunnelCount = 0,
                Logs = SnapshotLogs(),
            });
            return;
        }

        // 至少还有一个进程活着
        var remote = remoteAddrs.Values.FirstOrDefault();
        var hasConnected = remoteAddrs.Count > 0;

        var status = forceStatus ?? (hasConnected ? FrpStatusKind.Running : FrpStatusKind.Starting);

        var message = status switch
        {
            FrpStatusKind.AuthFailed => "访问密钥无效或已失效",
            FrpStatusKind.TunnelOffline => "某些隧道不存在或已被禁用",
            FrpStatusKind.Running => total == 1
                ? $"已连接，远程地址 {remote}"
                : $"已连接 {connected}/{total} 条隧道",
            _ => total == 1
                ? "正在连接樱花穿透…"
                : $"正在连接樱花穿透（{total} 条隧道）…",
        };

        SetStateSafe(new FrpState
        {
            Status = status,
            Message = message,
            RemoteAddress = remote,
            RemoteAddresses = remoteAddrs,
            RunningTunnelCount = total,
            Logs = SnapshotLogs(),
        });
    }

    private static FrpStatusKind? ParseStatus(string line)
    {
        if (Regex.IsMatch(line, @"invalid token|login to server failed|authorization failed|访问密钥.*(?:无效|失效)|认证失败",
                RegexOptions.IgnoreCase))
        {
            return FrpStatusKind.AuthFailed;
        }

        if (Regex.IsMatch(line, @"tunnel not exists|tunnel offline|proxy not found|隧道不存在|隧道已被禁用",
                RegexOptions.IgnoreCase))
        {
            return FrpStatusKind.TunnelOffline;
        }

        if (Regex.IsMatch(line, @"start proxy (?:error|failed)|隧道启动失败", RegexOptions.IgnoreCase))
        {
            return FrpStatusKind.Error;
        }

        if (Regex.IsMatch(line, @"\b(?:start )?proxy success\b|隧道启动成功", RegexOptions.IgnoreCase))
        {
            return FrpStatusKind.Running;
        }

        return null;
    }

    private static string? ParseAddress(string line)
    {
        var contextual = ContextPattern.IsMatch(line);

        foreach (Match match in AddressPattern.Matches(line))
        {
            var host = match.Groups["host"].Value;
            if (!int.TryParse(match.Groups["port"].Value, out var port)
                || !DirectProtocol.IsValidPort(port)
                || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("127.", StringComparison.Ordinal)
                || host == "0.0.0.0")
            {
                continue;
            }

            if (contextual || KnownHostPattern.IsMatch(host))
            {
                return $"{host}:{port}";
            }
        }

        return null;
    }

    private void AppendLog(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss}  {line}";

        lock (_logLock)
        {
            _logs.Add(entry);
            if (_logs.Count > MaxLogLines)
            {
                _logs.RemoveRange(0, _logs.Count - MaxLogLines);
            }
        }

        LogReceived?.Invoke(this, entry);
    }

    private void ClearLogs()
    {
        lock (_logLock)
        {
            _logs.Clear();
        }
    }

    private IReadOnlyList<string> SnapshotLogs()
    {
        lock (_logLock)
        {
            return _logs.ToArray();
        }
    }

    private void SetStateSafe(FrpState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private static int? TryReadExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool IsLikelyFrpc(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1_000_000) return false;

            using var stream = File.OpenRead(path);
            var b0 = stream.ReadByte();
            var b1 = stream.ReadByte();
            return b0 == 'M' && b1 == 'Z';
        }
        catch (IOException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
    }

    private static string MaskKey(string key)
    {
        if (key.Length == 0) return string.Empty;
        return key.Length <= 4
            ? new string('*', key.Length)
            : $"{key[..2]}***{key[^2..]}";
    }

    public void Dispose()
    {
        ++_epoch;

        List<Process> tracked;
        lock (_processLock)
        {
            tracked = _processes.Values.ToList();
            _processes.Clear();
            _remoteAddresses.Clear();
        }

        foreach (var process in tracked)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                try { process.WaitForExit(3000); } catch { }
            }
            catch { }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        // Job Object 句柄关闭 → 内核自动级联杀所有子进程。
        _job.Dispose();

        if (_ownsDownloader) _downloader.Dispose();
    }
}
