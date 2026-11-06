using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Connect;

public enum TerracottaPhase
{
    Idle,
    Downloading,
    Starting,
    Hosting,
    Joining,
    Ready,
}

/// <summary>陶瓦联机状态快照。</summary>
public sealed record TerracottaState
{
    public TerracottaPhase Phase { get; init; }

    /// <summary>房主拿到的房间码，形如 <c>U/XXXX-XXXX-XXXX-XXXX</c>。</summary>
    public string? Room { get; init; }

    /// <summary>加入成功后引擎给出的本地地址。</summary>
    public string? Url { get; init; }

    /// <summary>引擎原始状态（host-ok / guest-ok / …）。</summary>
    public string? StateRaw { get; init; }

    public string? Error { get; init; }

    /// <summary>进行中的状态说明（例如「正在下载陶瓦联机官方工具…」），不是错误。</summary>
    public string? Message { get; init; }

    public bool BinaryReady { get; init; }

    public bool Running { get; init; }
}

/// <summary>陶瓦工具下载进度：0–1 的比例与实际网速（字节/秒）。</summary>
public sealed record TerracottaInstallProgress(double Fraction, double BytesPerSecond);

/// <summary>
/// 陶瓦联机（Terracotta）官方工具集成。
/// 只下载并调用独立开源项目 burningtnt/Terracotta 的官方二进制（AGPL-3.0），
/// 下载后会按固定 SHA-256 校验；不修改、不重打包、不内置该工具的代码。
/// </summary>
public sealed class TerracottaService : IDisposable
{
    private const string ToolVersion = "0.4.2";

    private const string ReleaseBase = $"https://github.com/burningtnt/Terracotta/releases/download/v{ToolVersion}";

    private static readonly string[] DownloadBases =
    {
        ReleaseBase,
        $"https://gitee.com/burningtnt/Terracotta/releases/download/v{ToolVersion}",
        $"https://cnb.cool/HMCL-Terracotta/Terracotta/-/releases/download/v{ToolVersion}",
        $"https://mirror.ghproxy.com/{ReleaseBase}",
    };

    private static readonly Regex RoomCodePattern =
        new(@"^U/[A-Z0-9]{4}(-[A-Z0-9]{4}){3}$", RegexOptions.Compiled);

    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private readonly ILogger _log;
    private readonly string _directory;
    private readonly JobObject _job;

    private Process? _process;
    private HttpClient? _localHttp;
    private CancellationTokenSource? _pollCts;
    private TerracottaState _state = new() { Phase = TerracottaPhase.Idle };
    private int _epoch;
    private int _busy;

    public TerracottaService(ILogger log, string configDirectory)
    {
        _log = log;
        _directory = Path.Combine(configDirectory, "terracotta");
        _job = new JobObject();
    }

    public TerracottaState State => _state;

    /// <summary>房间码形如 <c>U/ABCD-EFGH-IJKL-MNOP</c>；比较前会去掉首尾空白并转大写。</summary>
    public static bool IsValidRoomCode(string? roomCode) =>
        !string.IsNullOrWhiteSpace(roomCode)
        && RoomCodePattern.IsMatch(roomCode.Trim().ToUpperInvariant());

    public event EventHandler<TerracottaState>? StateChanged;

    /// <summary>官方工具版本，界面用于展示。</summary>
    public string ToolVersionText => ToolVersion;

    public string BinaryPath => Path.Combine(_directory, Asset.ExeName);

    private static TerracottaAsset Asset => RuntimeInformation.OSArchitecture == Architecture.Arm64
        ? new TerracottaAsset(
            "terracotta-0.4.2-windows-arm64-pkg.tar.gz",
            "terracotta-0.4.2-windows-arm64.exe",
            "acfab0a87a02dedc6dab7c05303186c8907f56f815548b693fb3324358da7d14",
            "782c2fa911488d487447694acca6b17fa68304c87023fb6814b83a167fc2845f")
        : new TerracottaAsset(
            "terracotta-0.4.2-windows-x86_64-pkg.tar.gz",
            "terracotta-0.4.2-windows-x86_64.exe",
            "07ebe139e3ca5f74576e58b1a96efe59abdfbe148d3f1a49bfdca8b6f70745f0",
            "74c10568a7fea9c1d38cf8d2d4ca90baf1517f8e5a26c63d3349db70bc449796");

    public bool IsBinaryReady()
    {
        if (!File.Exists(BinaryPath))
        {
            return false;
        }

        return Sha256OfFile(BinaryPath).Equals(Asset.ExeSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>下载、校验并解出官方工具；进度为 0–1。</summary>
    public async Task InstallAsync(IProgress<TerracottaInstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("陶瓦工具正在安装或运行中，请稍候。");
        }

        try
        {
            if (IsBinaryReady())
            {
                Publish(new TerracottaState { Phase = TerracottaPhase.Idle, BinaryReady = true });
                return;
            }

            Directory.CreateDirectory(_directory);
            SetPhase(TerracottaPhase.Downloading, "正在下载陶瓦联机官方工具…");

            var packagePath = Path.Combine(_directory, Asset.PackageName + ".part");
            var lastError = string.Empty;

            foreach (var baseUrl in DownloadBases)
            {
                ct.ThrowIfCancellationRequested();

                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        TryDelete(packagePath);

                        await DownloadAsync(new Uri(baseUrl.TrimEnd('/') + "/" + Asset.PackageName), packagePath, progress, ct)
                            .ConfigureAwait(false);

                        if (!Sha256OfFile(packagePath).Equals(Asset.PackageSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("压缩包 SHA-256 校验失败，已丢弃并换源重试");
                        }

                        await ExtractAsync(packagePath, ct).ConfigureAwait(false);

                        if (!IsBinaryReady())
                        {
                            throw new InvalidDataException("陶瓦可执行文件 SHA-256 校验失败");
                        }

                        TryDelete(packagePath);
                        _log.Info($"陶瓦联机工具已就绪：{BinaryPath}");
                        Publish(new TerracottaState { Phase = TerracottaPhase.Idle, BinaryReady = true });
                        return;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException
                                                   or InvalidDataException or TaskCanceledException
                                                   or UnauthorizedAccessException)
                    {
                        lastError = ex.Message;
                        _log.Warn($"陶瓦工具下载失败（{baseUrl}）", ex);
                        TryDelete(packagePath);
                    }
                }
            }

            throw new InvalidOperationException(
                $"陶瓦工具下载失败：{lastError}。可到 https://github.com/burningtnt/Terracotta/releases 手动下载后放到 {_directory}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public async Task<TerracottaState> StartHostAsync(string playerName, CancellationToken ct = default)
    {
        return await StartAsync(TerracottaPhase.Hosting, async http =>
        {
            await GetAsync(http, $"/state/scanning?player={Uri.EscapeDataString(playerName)}", ct).ConfigureAwait(false);

            var final = await PollAsync(http, "host", 30, ct).ConfigureAwait(false);
            if (!IsValidRoomCode(final.Room))
            {
                throw new InvalidOperationException("陶瓦没有返回有效的房间码，请重试。");
            }

            return new TerracottaState
            {
                Phase = TerracottaPhase.Ready,
                Room = final.Room,
                StateRaw = final.State,
                BinaryReady = true,
                Running = true,
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<TerracottaState> StartJoinAsync(
        string roomCode,
        string playerName,
        CancellationToken ct = default)
    {
        var code = (roomCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!IsValidRoomCode(code))
        {
            throw new InvalidOperationException("房间码格式应为 U/XXXX-XXXX-XXXX-XXXX。");
        }

        return await StartAsync(TerracottaPhase.Joining, async http =>
        {
            await GetAsync(
                    http,
                    $"/state/guesting?room={Uri.EscapeDataString(code)}&player={Uri.EscapeDataString(playerName)}",
                    ct)
                .ConfigureAwait(false);

            var final = await PollAsync(http, "join", 60, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(final.Url))
            {
                throw new InvalidOperationException("陶瓦没有返回可用的本地地址，请重试。");
            }

            return new TerracottaState
            {
                Phase = TerracottaPhase.Ready,
                Room = code,
                Url = final.Url,
                StateRaw = final.State,
                BinaryReady = true,
                Running = true,
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<TerracottaState> StopAsync()
    {
        ++_epoch;

        var http = _localHttp;
        var process = _process;

        if (http is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await GetAsync(http, "/panic?peaceful=true", cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                // 进程可能已经退出。
            }

            http.Dispose();
            _localHttp = null;
        }

        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        _process = null;
        _busy = 0;

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
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException
                                           or System.ComponentModel.Win32Exception)
            {
                _log.Warn("停止陶瓦进程失败", ex);
            }
            finally
            {
                process.Dispose();
            }
        }

        Publish(new TerracottaState
        {
            Phase = TerracottaPhase.Idle,
            BinaryReady = IsBinaryReady(),
        });

        return _state;
    }

    private async Task<TerracottaState> StartAsync(
        TerracottaPhase workingPhase,
        Func<HttpClient, Task<TerracottaState>> action,
        CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("陶瓦联机正在启动或运行中，请先关闭当前房间。");
        }

        var epoch = ++_epoch;

        try
        {
            if (!IsBinaryReady())
            {
                throw new InvalidOperationException("请先下载并校验陶瓦联机官方工具。");
            }

            SetPhase(workingPhase, workingPhase == TerracottaPhase.Hosting ? "正在创建房间…" : "正在加入房间…");

            _localHttp = await StartProcessAsync(ct).ConfigureAwait(false);
            if (epoch != _epoch)
            {
                throw new OperationCanceledException("启动已取消");
            }

            var ready = await action(_localHttp).ConfigureAwait(false);
            if (epoch != _epoch)
            {
                throw new OperationCanceledException("启动已取消");
            }

            Publish(ready);
            return ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            Publish(new TerracottaState
            {
                Phase = TerracottaPhase.Idle,
                Error = ex.Message,
                BinaryReady = IsBinaryReady(),
            });
            throw;
        }
        finally
        {
            if (epoch == _epoch)
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }
    }

    private async Task<HttpClient> StartProcessAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var portFile = Path.Combine(_directory, "port.json");
        TryDelete(portFile);

        var startInfo = new ProcessStartInfo(BinaryPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--hmcl2");
        startInfo.ArgumentList.Add(portFile);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            if (_process != process || Volatile.Read(ref _busy) != 1)
            {
                return;
            }

            _log.Warn("陶瓦进程已退出");
            Publish(new TerracottaState
            {
                Phase = TerracottaPhase.Idle,
                Error = "陶瓦进程已退出，请重试。",
                BinaryReady = IsBinaryReady(),
            });
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("无法启动陶瓦联机工具。");
        }

        // 挂到 Job Object：启动器崩溃/被杀时内核自动级联清理陶瓦进程。
        try
        {
            _job.AssignProcess(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Warn("陶瓦进程未成功挂到 Job Object，崩溃时可能残留子进程", ex);
        }

        _process = process;
        _log.Info($"已启动陶瓦联机工具（PID {process.Id}）");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException("陶瓦联机工具在启动阶段退出了，请重试或查看日志。");
            }

            try
            {
                if (File.Exists(portFile))
                {
                    var json = await File.ReadAllTextAsync(portFile, ct).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);

                    if (document.RootElement.TryGetProperty("port", out var portElement)
                        && portElement.TryGetInt32(out var port)
                        && DirectProtocol.IsValidPort(port))
                    {
                        _log.Info($"陶瓦 HTTP 端口：{port}");
                        return new HttpClient(CreateLocalHandler())
                        {
                            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                            Timeout = TimeSpan.FromSeconds(8),
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // 端口文件可能还没写完，继续轮询。
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("陶瓦联机工具启动超时（120 秒），请重试。");
    }

    private async Task<TerracottaPollResult> PollAsync(
        HttpClient http,
        string mode,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        TerracottaPollResult? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is null || _process.HasExited)
            {
                throw new InvalidOperationException("陶瓦进程已退出，请重试。");
            }

            string body;
            try
            {
                body = await GetAsync(http, "/state", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            var parsed = ParseState(body);
            if (parsed is not null)
            {
                last = parsed;

                if (parsed.State != _state.StateRaw)
                {
                    Publish(_state with
                    {
                        Phase = mode == "host" ? TerracottaPhase.Hosting : TerracottaPhase.Joining,
                        StateRaw = parsed.State,
                        Running = true,
                    });
                }

                if (parsed.State is "exception" or "fatal" or "failed")
                {
                    try
                    {
                        await GetAsync(http, "/state/ide", ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException)
                    {
                    }

                    throw new InvalidOperationException($"陶瓦进入异常状态（{parsed.State}），已复位，可重试。");
                }

                if (mode == "host" && parsed.State == "host-ok" && !string.IsNullOrWhiteSpace(parsed.Room))
                {
                    return parsed;
                }

                if (mode == "join" && parsed.State == "guest-ok" && !string.IsNullOrWhiteSpace(parsed.Url))
                {
                    return parsed;
                }
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(mode == "host"
            ? "等待房间号超时（30 秒），请重试。"
            : $"等待连接就绪超时（{timeoutSeconds} 秒），请检查房间码或重试。");
    }

    private static TerracottaPollResult? ParseState(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var state = root.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
            var room = root.TryGetProperty("room", out var roomElement) ? roomElement.GetString() : null;
            var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;

            return new TerracottaPollResult(state, room, url);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> GetAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}：{body[..Math.Min(body.Length, 200)]}");
        }

        return body;
    }

    private async Task DownloadAsync(
        Uri url,
        string targetPath,
        IProgress<TerracottaInstallProgress>? progress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await DownloadHttp
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        var received = 0L;
        var started = Stopwatch.StartNew();

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                if (total > 0)
                {
                    var elapsed = started.Elapsed.TotalSeconds;
                    var speed = elapsed > 0.2 ? received / elapsed : 0;
                    progress?.Report(new TerracottaInstallProgress(
                        Math.Clamp((double)received / total, 0d, 1d),
                        speed));
                }
            }
        }

        progress?.Report(new TerracottaInstallProgress(1d, 0));
    }

    private async Task ExtractAsync(string packagePath, CancellationToken ct)
    {
        var staging = Path.Combine(_directory, "extract-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(staging);

            await using (var file = File.OpenRead(packagePath))
            await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, cancellationToken: ct)
                    .ConfigureAwait(false);
            }

            var extracted = Directory
                .EnumerateFiles(staging, Asset.ExeName, SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException($"压缩包里没有找到 {Asset.ExeName}");

            File.Move(extracted, BinaryPath, overwrite: true);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"清理陶瓦工具解压目录失败：{staging}", ex);
            }
        }
    }

    private void SetPhase(TerracottaPhase phase, string? message = null)
    {
        Publish(new TerracottaState
        {
            Phase = phase,
            Message = message,
            BinaryReady = IsBinaryReady(),
            Running = _process is { HasExited: false },
        });
    }

    private void Publish(TerracottaState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static SocketsHttpHandler CreateLocalHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    };

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCLauncher-Reborn/6.0");
        return client;
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

    public void Dispose()
    {
        ++_epoch;
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        // 先尝试优雅退出：陶瓦有 HTTP API /panic?peaceful=true 可以让它干净收尾。
        var http = _localHttp;
        _localHttp = null;
        if (http is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    http.GetAsync("/panic?peaceful=true", cts.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    // 进程可能已经退出，忽略。
                }

                http.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }

        var process = _process;
        _process = null;
        _busy = 0;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                // 最多等 3 秒让进程彻底退出。
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
    }

    private sealed record TerracottaAsset(
        string PackageName,
        string ExeName,
        string PackageSha256,
        string ExeSha256);

    private sealed record TerracottaPollResult(string? State, string? Room, string? Url);
}
