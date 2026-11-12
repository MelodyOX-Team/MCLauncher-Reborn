using System.Buffers;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Network;

/// <summary>
/// 健壮的文件下载器。关键行为：
/// <list type="bullet">
/// <item>多候选源轮询：官方失败自动切镜像，镜像失败回头试官方。</item>
/// <item><c>.part</c> 断点续传：发 <c>Range</c> 且只接受 206；服务器返回 200 说明不支持续传，丢弃重来。</item>
/// <item>下载完成后严格校验（SHA-1 / 大小），不合格立即删除并换源。</item>
/// <item>按主机限流：镜像 4 并发 + 100ms 间隔，官方 16 并发无限流。</item>
/// <item>进度节流：每 100ms 或每 256 KiB 报一次，避免 UI 被高频回调淹没。</item>
/// </list>
/// </summary>
public sealed class HttpDownloader : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private const long ProgressByteStep = 256 * 1024;
    private const int MaxAttemptsPerCandidate = 3;
    private const int MirrorConcurrency = 4;
    private const int OfficialConcurrency = 16;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MirrorMinInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 默认 User-Agent。不要带括号里的站点地址：部分镜像（BMCLAPI）的 WAF 会把带链接的 UA 判成爬虫并回 403，
    /// 导致镜像全部失效、只能退回官方源。
    /// </summary>
    public const string UserAgent = "MCLauncher-Reborn/6.0";

    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly Dictionary<string, HostGate> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gateLock = new();
    private bool _disposed;

    public HttpDownloader(ILogger log, HttpMessageHandler? handler = null)
    {
        _log = log;

        var messageHandler = handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,
        };

        _http = new HttpClient(messageHandler, disposeHandler: true)
        {
            // 用逐请求 / 逐次读取超时；整体超时会把大文件下载拦腰掐断。
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    // ---------------------------------------------------------------- 通用请求

    /// <summary>
    /// 发起一次带响应体的请求并返回文本。非 2xx 时抛出
    /// <see cref="HttpApiException"/>（带上响应体，便于解析服务端错误码）。
    /// </summary>
    public async Task<string> RequestStringAsync(
        string url,
        HttpMethod method,
        string? content = null,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        if (content is null)
        {
            return await RequestContentAsync(url, method, null, headers, ct).ConfigureAwait(false);
        }

        using var stringContent = new StringContent(
            content, System.Text.Encoding.UTF8, contentType ?? "application/json");
        return await RequestContentAsync(url, method, stringContent, headers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送任意 <see cref="HttpContent"/>（如表单 multipart）并返回响应文本。
    /// <paramref name="content"/> 随请求一起释放，调用方不要复用。
    /// </summary>
    public async Task<string> RequestContentAsync(
        string url,
        HttpMethod method,
        HttpContent? content,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        using var gate = await EnterHostAsync(url, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, url) { Content = content };

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                _ = request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HeaderTimeout);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.Trace($"请求失败：{method} {url} → {(int)response.StatusCode}");
            throw new HttpApiException(response.StatusCode, body, response.ReasonPhrase, url);
        }

        return body;
    }

    /// <summary>获取二进制内容（皮肤、披风这类小图片）。</summary>
    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        using var gate = await EnterHostAsync(url, ct).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HeaderTimeout);

        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.Trace($"请求失败：GET {url} → {(int)response.StatusCode}");
            throw new HttpApiException(response.StatusCode, body, response.ReasonPhrase, url);
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>表单 POST（OAuth token 端点用）。</summary>
    public Task<string> PostFormAsync(
        IReadOnlyDictionary<string, string> form,
        string url,
        CancellationToken ct = default) =>
        RequestStringAsync(
            url,
            HttpMethod.Post,
            string.Join("&", form.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")),
            "application/x-www-form-urlencoded",
            null,
            ct);

    /// <summary>JSON POST。</summary>
    public Task<string> PostJsonAsync(
        string url,
        string json,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default) =>
        RequestStringAsync(url, HttpMethod.Post, json, "application/json", headers, ct);

    // ---------------------------------------------------------------- 文本获取

    /// <summary>按候选顺序获取文本，全部失败时抛出最后一个异常。</summary>
    public async Task<string> GetStringAsync(
        IReadOnlyList<DownloadCandidate> candidates,
        CancellationToken ct = default)
    {
        Exception? lastError = null;

        foreach (var candidate in candidates)
        {
            try
            {
                return await GetStringFromCandidateAsync(candidate, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new HttpRequestException($"所有候选源均获取失败：{DescribeCandidates(candidates)}", lastError);
    }

    /// <summary>
    /// 并行请求所有候选源，返回最先成功的一份文本，其余请求立即取消。
    /// 适合公告这类「必须马上拿到、卡住就换源」的小请求：官方源不通时镜像能立刻补上，
    /// 不用等官方源超时。
    /// </summary>
    public async Task<string> GetStringRaceAsync(
        IReadOnlyList<DownloadCandidate> candidates,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
        {
            throw new ArgumentException("至少需要一个候选源", nameof(candidates));
        }

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = candidates
            .Select(candidate => GetStringFromCandidateAsync(candidate, raceCts.Token))
            .ToList();

        Exception? lastError = null;

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);

            try
            {
                var text = await finished.ConfigureAwait(false);
                raceCts.Cancel();
                await IgnoreRemainingAsync(tasks).ConfigureAwait(false);
                return text;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new HttpRequestException($"所有候选源均获取失败：{DescribeCandidates(candidates)}", lastError);
    }

    /// <summary>一个候选源的完整获取流程：主机限流 + 最多 <see cref="MaxAttemptsPerCandidate"/> 次重试。</summary>
    private async Task<string> GetStringFromCandidateAsync(DownloadCandidate candidate, CancellationToken ct)
    {
        Exception? lastError = null;

        using var gate = await EnterHostAsync(candidate.Url, ct).ConfigureAwait(false);
        for (var attempt = 1; attempt <= MaxAttemptsPerCandidate; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(HeaderTimeout);

                // 用 ResponseContentRead 而不是 ResponseHeadersRead：
                // 前者让上面这个超时把**响应体**也一起覆盖住。
                // 用后者的话，只有响应头受超时保护，服务端在响应体中途卡住会让这里永久挂起。
                using var response = await _http
                    .GetAsync(candidate.Url, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.Trace($"已获取 {candidate.SourceName} 文本：{candidate.Url}（{text.Length} 字符）");
                return text;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                _log.Warn($"获取文本超时（{candidate.SourceName} 第 {attempt}/{MaxAttemptsPerCandidate} 次）：{candidate.Url}");
                if (attempt < MaxAttemptsPerCandidate)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (IsPermanentClientError(ex))
            {
                // 404 / 403 这类客户端错误重试没有意义，直接放弃这个候选源。
                lastError = ex;
                _log.Info($"获取文本失败（{candidate.SourceName}，HTTP {ex.StatusCode}，不再重试）：{candidate.Url}");
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.Warn($"获取文本失败（{candidate.SourceName} 第 {attempt}/{MaxAttemptsPerCandidate} 次）：{candidate.Url}", ex);
                if (attempt < MaxAttemptsPerCandidate)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct).ConfigureAwait(false);
                }
            }
        }

        throw lastError ?? new HttpRequestException($"获取文本失败：{candidate.Url}");
    }

    /// <summary>已经拿到结果后吸收其余请求的失败/取消，避免留下未观察的异常。</summary>
    private static async Task IgnoreRemainingAsync(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // 竞速已经结束，剩余候选源的结果不再需要。
        }
    }

    // ---------------------------------------------------------------- 文件下载

    /// <summary>
    /// 下载文件到 <paramref name="targetPath"/>。目标已存在且通过校验时直接跳过。
    /// </summary>
    public async Task<DownloadOutcome> DownloadAsync(
        IReadOnlyList<DownloadCandidate> candidates,
        string targetPath,
        FileChecker checker,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (checker.Check(targetPath))
        {
            var size = checker.Size >= 0 ? checker.Size : 0;
            progress?.Report(new DownloadProgress(size, checker.Size >= 0 ? size : -1));
            return DownloadOutcome.AlreadyPresent();
        }

        Exception? lastError = null;
        var attempts = 0;

        foreach (var candidate in candidates)
        {
            using var gate = await EnterHostAsync(candidate.Url, ct).ConfigureAwait(false);

            for (var attempt = 1; attempt <= MaxAttemptsPerCandidate; attempt++)
            {
                attempts++;
                try
                {
                    await DownloadFromCandidateAsync(candidate, targetPath, checker, progress, ct).ConfigureAwait(false);
                    _log.Trace($"下载成功（{candidate.SourceName}）：{targetPath}");
                    return new DownloadOutcome(true, false, attempts, candidate.SourceName, null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex) when (IsPermanentClientError(ex))
                {
                    // 404 / 403 / 401 这类永久性客户端错误重试没有意义，直接换下一个候选源。
                    lastError = ex;
                    _log.Info(
                        $"下载失败（{candidate.SourceName}，HTTP {ex.StatusCode}，不再重试，文件 {Path.GetFileName(targetPath)}）");
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _log.Warn(
                        $"下载失败（{candidate.SourceName} 第 {attempt}/{MaxAttemptsPerCandidate} 次，文件 {Path.GetFileName(targetPath)}）",
                        ex);
                    if (attempt < MaxAttemptsPerCandidate)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct).ConfigureAwait(false);
                    }
                }
            }
        }

        return DownloadOutcome.Failed(attempts, lastError?.Message ?? "无可用候选源");
    }

    private async Task DownloadFromCandidateAsync(
        DownloadCandidate candidate,
        string targetPath,
        FileChecker checker,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var partPath = targetPath + ".part";
        PathUtil.EnsureParentDirectory(targetPath);

        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        HttpResponseMessage response;
        using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headerCts.CancelAfter(HeaderTimeout);
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)
                .ConfigureAwait(false);
        }

        using (response)
        {
            // 服务器认为请求范围无效（本地分片比远端文件还长，通常是远端换过文件）：清空重下。
            if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                _log.Warn($"{candidate.SourceName} 拒绝了续传范围（416），已清空本地分片");
                TryDelete(partPath);
                throw new HttpRequestException("HTTP 416：断点续传范围无效，已重置本地分片");
            }

            // 服务器忽略 Range 直接返回 200：已下载分片作废，从头再来。
            if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                _log.Trace($"{candidate.SourceName} 不支持断点续传，丢弃已下载的 {existing} 字节");
                existing = 0;
                TryDelete(partPath);
            }

            if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent)
            {
                // 带上状态码，外层才能识别永久性的 4xx 并跳过无意义的重试。
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
            }

            // 206 必须从本地已有长度处继续，否则拼接出来的文件是错的。
            if (existing > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From is not { } from || from != existing)
                {
                    TryDelete(partPath);
                    throw new InvalidDataException(
                        $"续传起点不一致：期望 {existing}，实际 {range?.From?.ToString() ?? "未知"}");
                }

                if (range.Length is { } remoteLength && checker.Size >= 0 && remoteLength != checker.Size)
                {
                    TryDelete(partPath);
                    throw new InvalidDataException(
                        $"远端文件长度与期望不符：期望 {checker.Size}，实际 {remoteLength}");
                }
            }

            InspectBmclapiHashHint(response, checker);

            var contentLength = response.Content.Headers.ContentLength;
            var totalBytes = contentLength is { } len ? len + existing : -1;

            long received;
            await using (var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(
                             partPath,
                             existing > 0 ? FileMode.Append : FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous))
            {
                received = await CopyWithProgressAsync(httpStream, fileStream, existing, totalBytes, progress, ct).ConfigureAwait(false);
                await fileStream.FlushAsync(ct).ConfigureAwait(false);
            }

            // 对端可能在响应体传完前就关闭连接；总长度已知时必须确认收全，
            // 否则截断的文件会被 ExistenceOnly 当成下载成功。
            if (totalBytes >= 0 && received != totalBytes)
            {
                TryDelete(partPath);
                throw new InvalidDataException($"下载不完整：期望 {totalBytes} 字节，实际收到 {received} 字节");
            }
        }

        if (!checker.Check(partPath))
        {
            var actual = GetLengthText(partPath);
            TryDelete(partPath);
            throw new InvalidDataException($"校验不通过：期望 {checker}，实际 {actual}");
        }

        File.Move(partPath, targetPath, overwrite: true);
        progress?.Report(new DownloadProgress(File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0, checker.Size));
    }

    private async Task<long> CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long alreadyBytes,
        long totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var received = alreadyBytes;
            var lastReportedBytes = alreadyBytes;
            var lastReportAt = Stopwatch.GetTimestamp();

            progress?.Report(new DownloadProgress(received, totalBytes));

            while (true)
            {
                var read = await ReadWithTimeoutAsync(source, buffer, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                var elapsed = Stopwatch.GetElapsedTime(lastReportAt);
                if (received - lastReportedBytes >= ProgressByteStep || elapsed >= ProgressInterval)
                {
                    progress?.Report(new DownloadProgress(received, totalBytes));
                    lastReportedBytes = received;
                    lastReportAt = Stopwatch.GetTimestamp();
                }
            }

            progress?.Report(new DownloadProgress(received, totalBytes));
            return received;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(ReadTimeout);

        try
        {
            return await stream.ReadAsync(buffer.AsMemory(0, BufferSize), readCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"读取超时：{ReadTimeout.TotalSeconds:0} 秒内没有收到数据");
        }
    }

    /// <summary>
    /// BMCLAPI 会在响应头里回传文件的真实 SHA-1（<c>X-BMCLAPI-Hash</c>）。
    /// 命中相同即说明镜像缓存与元数据一致；不一致时只告警，最终仍以本地校验为准。
    /// </summary>
    private void InspectBmclapiHashHint(HttpResponseMessage response, FileChecker checker)
    {
        if (!response.Headers.TryGetValues("X-BMCLAPI-Hash", out var values))
        {
            return;
        }

        var hint = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(hint) || hint.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(checker.Sha1))
        {
            _log.Trace($"BMCLAPI 回传哈希：{hint}");
            return;
        }

        if (string.Equals(hint, checker.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            _log.Trace($"BMCLAPI 哈希命中：{hint}");
        }
        else
        {
            _log.Warn($"BMCLAPI 回传哈希与期望不符（回传 {hint}，期望 {checker.Sha1}），将以本地校验结果为准");
        }
    }

    // ---------------------------------------------------------------- 主机限流

    private async Task<IDisposable> EnterHostAsync(string url, CancellationToken ct)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "unknown";

        HostGate gate;
        lock (_gateLock)
        {
            if (!_hostGates.TryGetValue(host, out gate!))
            {
                var concurrency = DownloadSourceResolver.IsOfficialHost(host) ? OfficialConcurrency : MirrorConcurrency;
                var interval = DownloadSourceResolver.IsOfficialHost(host) ? TimeSpan.Zero : MirrorMinInterval;
                gate = new HostGate(concurrency, interval);
                _hostGates[host] = gate;
            }
        }

        return await gate.EnterAsync(ct).ConfigureAwait(false);
    }

    private static bool IsPermanentClientError(HttpRequestException exception) =>
        exception.StatusCode is { } status
        && (int)status >= 400
        && (int)status < 500
        && status is not HttpStatusCode.RequestTimeout
        && status is not HttpStatusCode.TooManyRequests;

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
            // 删除失败不影响后续重试（下次会以追加模式继续）。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string GetLengthText(string path)
    {
        try
        {
            return File.Exists(path) ? $"{new FileInfo(path).Length} 字节" : "文件不存在";
        }
        catch (IOException)
        {
            return "无法读取";
        }
    }

    private static string DescribeCandidates(IReadOnlyList<DownloadCandidate> candidates) =>
        string.Join("；", candidates.Select(c => c.ToString()));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    /// <summary>单主机并发桶 + 最小请求间隔。间隔只作用于镜像主机，官方主机不节流。</summary>
    private sealed class HostGate
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly long _minIntervalTicks;
        private long _nextAllowedTicks;

        public HostGate(int maxConcurrency, TimeSpan minInterval)
        {
            _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            _minIntervalTicks = minInterval.Ticks;
        }

        public async Task<IDisposable> EnterAsync(CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                while (_minIntervalTicks > 0)
                {
                    var now = DateTime.UtcNow.Ticks;
                    var next = Volatile.Read(ref _nextAllowedTicks);
                    if (now >= next)
                    {
                        Volatile.Write(ref _nextAllowedTicks, now + _minIntervalTicks);
                        break;
                    }

                    var wait = TimeSpan.FromTicks(Math.Min(next - now, TimeSpan.TicksPerMillisecond * 500));
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                _semaphore.Release();
                throw;
            }

            return new Releaser(_semaphore);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim? _semaphore;

            public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

            public void Dispose()
            {
                _semaphore?.Release();
                _semaphore = null;
            }
        }
    }
}
