using System.Collections.Concurrent;
using System.Diagnostics;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Network;

/// <summary>一条批量下载请求。</summary>
public sealed class DownloadRequest
{
    /// <summary>展示名（如 <c>1.20.4.json</c>），用于日志与进度提示。</summary>
    public required string Name { get; init; }

    /// <summary>候选下载源，按顺序轮询。</summary>
    public required IReadOnlyList<DownloadCandidate> Candidates { get; init; }

    /// <summary>落盘目标路径。</summary>
    public required string TargetPath { get; init; }

    /// <summary>校验规则。</summary>
    public FileChecker Checker { get; init; } = FileChecker.ExistenceOnly;

    /// <summary>可选文件：失败只记录，不让整批失败（例如某个可有可无的库）。</summary>
    public bool Optional { get; init; }
}

/// <summary>一条失败记录。</summary>
public readonly record struct DownloadFailure(string Name, string TargetPath, string Error);

/// <summary>批量下载汇总结果。</summary>
public sealed class DownloadBatchResult
{
    public int Total { get; init; }

    public int Completed { get; init; }

    public int Skipped { get; init; }

    public int Failed { get; init; }

    public IReadOnlyList<DownloadFailure> Failures { get; init; } = Array.Empty<DownloadFailure>();

    /// <summary>必需文件全部成功即视为成功（可选文件的失败不算）。</summary>
    public bool Success => Failures.Count == 0;
}

/// <summary>
/// 批量下载调度：给一批文件加并发上限，并把每条文件的进度聚合成一个整体进度上报。
/// 真正的限流（主机并发桶 / 请求间隔 / 断点续传）由 <see cref="HttpDownloader"/> 负责。
/// </summary>
public sealed class DownloadBatch
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromMilliseconds(400);

    /// <summary>安装弹窗里最多同时展示几条文件进度。</summary>
    private const int MaxActiveFiles = 5;

    private readonly HttpDownloader _downloader;
    private readonly ILogger _log;

    public DownloadBatch(HttpDownloader downloader, ILogger log)
    {
        _downloader = downloader;
        _log = log;
    }

    public async Task<DownloadBatchResult> RunAsync(
        IReadOnlyList<DownloadRequest> requests,
        int maxParallel = 8,
        IProgress<DownloadBatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 同一目标可能被多条下载路径要求；并发写同一个 .part 会互相破坏，按规范化路径只保留一条。
        requests = requests
            .GroupBy(request => PathUtil.Normalize(request.TargetPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (requests.Count == 0)
        {
            return new DownloadBatchResult();
        }

        var total = requests.Count;
        var totalBytes = requests.Sum(r => r.Checker.Size > 0 ? r.Checker.Size : 0);

        var completed = 0;
        var skipped = 0;
        var failed = 0;
        long downloadedBytes = 0;
        var failures = new ConcurrentQueue<DownloadFailure>();

        // 正在下载的文件：key 是开始顺序，便于界面稳定地按启动顺序展示。
        // 用普通字典 + 锁而不是 ConcurrentDictionary：后者在并发枚举时可能读到已被移除的空值。
        var active = new Dictionary<int, ActiveFile>();
        var activeLock = new object();
        var activeSequence = 0;

        // 上报状态必须随本次运行走：共享的 DownloadBatch 并发跑两批时，
        // 用实例字段会互相覆盖节流时间与速度窗口。
        var reportState = new ReportState();

        Report(progress, total, 0, 0, 0, totalBytes, 0, null, null, "开始下载", force: true, reportState);

        _log.Info($"开始批量下载：共 {total} 个文件，并发 {maxParallel}");

        await Parallel.ForEachAsync(
            requests,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallel), CancellationToken = ct },
            async (request, token) =>
            {
                long localReported = 0;
                var slot = Interlocked.Increment(ref activeSequence);
                var entry = new ActiveFile(request.Name, request.Checker.Size);
                lock (activeLock)
                {
                    active[slot] = entry;
                }

                var fileProgress = new InlineProgress<DownloadProgress>(p =>
                {
                    if (p.TotalBytes > 0)
                    {
                        Volatile.Write(ref entry.TotalBytes, p.TotalBytes);
                    }

                    var delta = p.BytesReceived - localReported;
                    if (delta != 0)
                    {
                        localReported = p.BytesReceived;
                        Volatile.Write(ref entry.BytesReceived, p.BytesReceived);
                        Interlocked.Add(ref downloadedBytes, delta);
                    }

                    Report(progress, total, Volatile.Read(ref completed), Volatile.Read(ref skipped), Volatile.Read(ref failed),
                        totalBytes, Interlocked.Read(ref downloadedBytes), active, activeLock, request.Name, force: false,
                        reportState);
                });

                DownloadOutcome outcome;
                try
                {
                    outcome = await _downloader
                        .DownloadAsync(request.Candidates, request.TargetPath, request.Checker, fileProgress, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    outcome = DownloadOutcome.Failed(0, ex.Message);
                }
                finally
                {
                    lock (activeLock)
                    {
                        active.Remove(slot);
                    }
                }

                if (outcome.Success)
                {
                    if (outcome.Skipped)
                    {
                        // DownloadAsync 在「已存在」时已经通过进度回调把体积计入一次，这里不能再加。
                        Interlocked.Increment(ref skipped);
                    }
                    else
                    {
                        Interlocked.Increment(ref completed);
                    }
                }
                else if (request.Optional)
                {
                    _log.Warn($"可选文件下载失败（已忽略）：{request.Name} —— {outcome.Error}");
                    Interlocked.Increment(ref skipped);
                }
                else
                {
                    failures.Enqueue(new DownloadFailure(request.Name, request.TargetPath, outcome.Error ?? "未知错误"));
                    Interlocked.Increment(ref failed);
                }

                Report(progress, total, Volatile.Read(ref completed), Volatile.Read(ref skipped), Volatile.Read(ref failed),
                    totalBytes, Interlocked.Read(ref downloadedBytes), active, activeLock, request.Name, force: true,
                    reportState);
            }).ConfigureAwait(false);

        var result = new DownloadBatchResult
        {
            Total = total,
            Completed = completed,
            Skipped = skipped,
            Failed = failed,
            Failures = failures.ToArray(),
        };

        if (result.Success)
        {
            _log.Info($"批量下载完成：共 {total}，新下载 {completed}，已存在 {skipped}");
        }
        else
        {
            _log.Warn($"批量下载存在失败：共 {total}，失败 {failed}");
            foreach (var failure in result.Failures)
            {
                _log.Warn($"  失败文件：{failure.Name} -> {failure.TargetPath}（{failure.Error}）");
            }
        }

        return result;
    }

    private static IReadOnlyList<DownloadFileProgress> SnapshotActive(
        Dictionary<int, ActiveFile> active,
        object activeLock)
    {
        lock (activeLock)
        {
            if (active.Count == 0)
            {
                return Array.Empty<DownloadFileProgress>();
            }

            return active
                .OrderBy(pair => pair.Key)
                .Take(MaxActiveFiles)
                .Select(pair => new DownloadFileProgress(
                    pair.Value.Name,
                    Volatile.Read(ref pair.Value.BytesReceived),
                    Volatile.Read(ref pair.Value.TotalBytes)))
                .ToArray();
        }
    }

    private void Report(
        IProgress<DownloadBatchProgress>? progress,
        int total,
        int completed,
        int skipped,
        int failed,
        long totalBytes,
        long downloadedBytes,
        Dictionary<int, ActiveFile>? active,
        object? activeLock,
        string? current,
        bool force,
        ReportState state)
    {
        if (progress is null)
        {
            return;
        }

        lock (state.Gate)
        {
            var now = Stopwatch.GetTimestamp();

            if (!force)
            {
                var last = state.LastReportTicks;
                if (last != 0 && Stopwatch.GetElapsedTime(last, now) < ReportInterval)
                {
                    return;
                }
            }

            state.LastReportTicks = now;

            if (state.SpeedWindowTicks == 0)
            {
                state.SpeedWindowTicks = now;
                state.SpeedWindowBytes = downloadedBytes;
            }
            else
            {
                var elapsed = Stopwatch.GetElapsedTime(state.SpeedWindowTicks, now);
                if (elapsed >= SpeedWindow)
                {
                    var delta = Math.Max(0, downloadedBytes - state.SpeedWindowBytes);
                    state.LastSpeed = delta / elapsed.TotalSeconds;
                    state.SpeedWindowTicks = now;
                    state.SpeedWindowBytes = downloadedBytes;
                }
            }

            var activeFiles = active is null || activeLock is null
                ? Array.Empty<DownloadFileProgress>()
                : SnapshotActive(active, activeLock);

            progress.Report(new DownloadBatchProgress
            {
                Total = total,
                Completed = completed,
                Skipped = skipped,
                Failed = failed,
                TotalBytes = totalBytes,
                DownloadedBytes = downloadedBytes,
                Current = current,
                ActiveFiles = activeFiles,
                BytesPerSecond = state.LastSpeed,
            });
        }
    }

    /// <summary>单次 <see cref="RunAsync"/> 的进度上报状态。</summary>
    private sealed class ReportState
    {
        public object Gate { get; } = new();

        public long LastReportTicks;

        public long SpeedWindowTicks;

        public long SpeedWindowBytes;

        public double LastSpeed;
    }

    /// <summary>单个正在下载的文件状态；由并行线程并发更新。</summary>
    private sealed class ActiveFile
    {
        public ActiveFile(string name, long totalBytes)
        {
            Name = name;
            TotalBytes = totalBytes;
        }

        public string Name { get; }

        public long TotalBytes;

        public long BytesReceived;
    }
}
