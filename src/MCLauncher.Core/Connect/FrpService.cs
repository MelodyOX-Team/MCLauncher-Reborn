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

/// <summary>樱花穿透（SakuraFrp）运行状态快照。</summary>
public sealed class FrpState
{
    public FrpStatusKind Status { get; init; }

    public string? RemoteAddress { get; init; }

    public string Message { get; init; } = string.Empty;

    public int ProcessId { get; init; }

    public IReadOnlyList<string> Logs { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 樱花穿透 frpc 集成：使用 SakuraFrp 官方 frpc 客户端与访问密钥/隧道 ID 建立 TCP 隧道。
/// 只负责下载官方客户端、启动进程并从日志里识别远程地址，不内置任何凭据。
/// </summary>
public sealed class FrpService : IDisposable
{
    private const string FrpcUrl =
        "https://nya.globalslb.net/natfrp/client/frpc/0.51.0-sakura-14/frpc_windows_amd64.exe";

    /// <summary>
    /// 固定版本官方客户端的 SHA-256。升级 frpc 时必须同步更新这里，
    /// 否则校验失败会拒绝运行——这是刻意的「失败即关闭」。
    /// </summary>
    private const string FrpcSha256 = "b705262edad9f0de04b38f342e811e9d97d0beada75edab3f790e105dcfb5234";

    private const long FrpcSize = 14_133_248;

    private const int MaxLogLines = 200;

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

    private Process? _process;
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

    /// <summary>每收到一行 frpc 日志时触发（已脱敏）。</summary>
    public event EventHandler<string>? LogReceived;

    public string BinaryPath => Path.Combine(_directory, "frpc.exe");

    /// <summary>frpc 是否已经下载并通过大小、PE 头与 SHA-256 校验。</summary>
    public bool IsBinaryReady()
    {
        // 校验一次后缓存结果：14 MB 文件的 SHA-256 不应在界面线程上反复算。
        if (_binaryVerified)
        {
            return File.Exists(BinaryPath);
        }

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

        if (IsBinaryReady())
        {
            return;
        }

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
            // 旧版本或校验失败的残留一律删掉，绝不把未校验的文件交给进程执行。
            if (File.Exists(path))
            {
                TryDelete(path);
            }

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

            // 独立下载完成后把状态从「下载中」收回来，避免界面一直停在下载态。
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
        catch (Exception ex) when (ex is IOException
                                       or TaskCanceledException
                                       or InvalidDataException)
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

    /// <summary>启动隧道。accessKey 与 tunnelId 来自 https://www.natfrp.com/。</summary>
    public async Task<FrpState> StartAsync(
        string accessKey,
        string tunnelId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            throw new InvalidOperationException("请填写樱花穿透的访问密钥。");
        }

        if (string.IsNullOrWhiteSpace(tunnelId))
        {
            throw new InvalidOperationException("请填写樱花穿透的隧道 ID。");
        }

        if (_process is not null && !_process.HasExited)
        {
            throw new InvalidOperationException("frpc 已在运行中，请先停止。");
        }

        var key = accessKey.Trim();
        var tunnel = tunnelId.Trim();
        var epoch = ++_epoch;

        await EnsureBinaryAsync(ct: ct).ConfigureAwait(false);
        if (epoch != _epoch)
        {
            throw new OperationCanceledException("启动已取消");
        }

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
        startInfo.ArgumentList.Add($"{key}:{tunnel}");
        startInfo.ArgumentList.Add("--disable_log_color");

        ClearLogs();
        AppendLog($"启动命令：frpc -f {MaskKey(key)}:{tunnel} --disable_log_color");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, args) => HandleLine(key, args.Data);
        process.ErrorDataReceived += (_, args) => HandleLine(key, args.Data);
        process.Exited += (_, _) => HandleExit(epoch, process);

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("无法启动 frpc 进程。");
        }

        // 挂到 Job Object：启动器崩溃/被杀时内核自动级联清理 frpc。
        try
        {
            _job.AssignProcess(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Job Object 分配失败不阻塞功能，只是失去崩溃时的自动清理保障。
            _log.Warn("frpc 未成功挂到 Job Object，崩溃时可能残留子进程", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _process = process;
        _log.Info($"已启动樱花穿透 frpc（PID {process.Id}，隧道 {tunnel}）");

        SetStateSafe(new FrpState
        {
            Status = FrpStatusKind.Starting,
            Message = "正在连接 SakuraFrp…",
            ProcessId = process.Id,
            Logs = SnapshotLogs(),
        });

        return _state;
    }

    public async Task<FrpState> StopAsync()
    {
        ++_epoch;

        var process = _process;
        _process = null;

        if (process is not null)
        {
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
                _log.Warn("停止 frpc 超时或进程已退出", ex);
            }
            finally
            {
                process.Dispose();
            }
        }

        SetStateSafe(new FrpState
        {
            Status = FrpStatusKind.Stopped,
            Message = "已停止",
            Logs = SnapshotLogs(),
        });

        return _state;
    }

    private void HandleLine(string accessKey, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var line = AnsiPattern.Replace(raw, string.Empty).Trim();
        var sanitized = line.Replace(accessKey, MaskKey(accessKey), StringComparison.Ordinal);
        AppendLog(sanitized);

        var status = ParseStatus(line);
        var address = ParseAddress(line);

        var current = _state;
        var nextStatus = status ?? current.Status;
        var nextAddress = address ?? current.RemoteAddress;

        if (nextStatus == current.Status && nextAddress == current.RemoteAddress)
        {
            return;
        }

        var message = nextStatus switch
        {
            FrpStatusKind.AuthFailed => "访问密钥无效或已失效",
            FrpStatusKind.TunnelOffline => "隧道不存在或已被禁用",
            FrpStatusKind.Error => "frpc 进程异常退出",
            FrpStatusKind.Running => nextAddress is null ? "已连接，等待远程地址" : $"已连接，远程地址 {nextAddress}",
            _ => current.Message,
        };

        SetStateSafe(new FrpState
        {
            Status = nextStatus,
            RemoteAddress = nextAddress,
            Message = message,
            ProcessId = _process?.Id ?? current.ProcessId,
            Logs = SnapshotLogs(),
        });
    }

    private void HandleExit(int epoch, Process process)
    {
        if (epoch != _epoch)
        {
            return;
        }

        var code = TryReadExitCode(process);
        AppendLog($"frpc 已退出（code={code}）");
        _log.Info($"frpc 已退出（code={code}）");

        if (_process != process)
        {
            return;
        }

        _process = null;

        SetStateSafe(new FrpState
        {
            Status = _state.Status is FrpStatusKind.AuthFailed or FrpStatusKind.TunnelOffline
                ? _state.Status
                : FrpStatusKind.Error,
            RemoteAddress = null,
            Message = _state.Status is FrpStatusKind.AuthFailed or FrpStatusKind.TunnelOffline
                ? _state.Message
                : "frpc 进程异常退出",
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
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsLikelyFrpc(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1_000_000)
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            var b0 = stream.ReadByte();
            var b1 = stream.ReadByte();
            return b0 == 'M' && b1 == 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string MaskKey(string key)
    {
        if (key.Length == 0)
        {
            return string.Empty;
        }

        return key.Length <= 4
            ? new string('*', key.Length)
            : $"{key[..2]}***{key[^2..]}";
    }

    public void Dispose()
    {
        ++_epoch;
        var process = _process;
        _process = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                // 最多等 3 秒让进程彻底退出，避免句柄泄漏。
                try
                {
                    process.WaitForExit(3000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        // 关闭 Job Object 句柄：内核自动级联杀掉 Job 里所有剩余子进程，
        // 这是启动器崩溃/被 Task Manager 杀掉时的最后一道防线。
        _job.Dispose();

        if (_ownsDownloader)
        {
            _downloader.Dispose();
        }
    }
}
