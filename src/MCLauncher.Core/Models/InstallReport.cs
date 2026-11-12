using MCLauncher.Core.Network;

namespace MCLauncher.Core.Models;

/// <summary>安装选项。</summary>
public sealed class InstallOptions
{
    /// <summary>是否下载资源对象（跳过可省掉 90% 的下载量，但游戏会丢贴图/音效）。</summary>
    public bool DownloadAssets { get; init; } = true;

    /// <summary>是否解压 natives。</summary>
    public bool ExtractNatives { get; init; } = true;

    /// <summary>是否写 <c>launcher_profiles.json</c>（Forge 安装器需要它）。</summary>
    public bool CreateLauncherProfiles { get; init; } = true;

    /// <summary>并发下载数。默认与官方源的主机并发桶一致。</summary>
    public int MaxParallel { get; init; } = 16;

    /// <summary>
    /// 对已存在的文件是否做完整哈希校验。
    /// true = 严格（慢，但能发现被改坏的文件）；false = 只检查存在（快）。
    /// </summary>
    public bool VerifyExisting { get; init; } = true;

    /// <summary>强制重新下载（无视已存在文件）。</summary>
    public bool ForceRedownload { get; init; }

    /// <summary>
    /// 阶段文案按「检查」而不是「下载」报告。启动前校验时文件通常已经齐全，
    /// 仍显示「下载资源索引 / 下载游戏文件」会让用户误以为每次启动都在重新下载。
    /// </summary>
    public bool ReportAsCheck { get; init; }
}

/// <summary>安装过程中的阶段性进度。</summary>
public sealed class InstallProgress
{
    public required string Stage { get; init; }

    public int Completed { get; init; }

    public int Total { get; init; }

    public long DownloadedBytes { get; init; }

    public long TotalBytes { get; init; }

    public string? Current { get; init; }

    /// <summary>当前并发下载的文件（按开始顺序，最多若干个）。</summary>
    public IReadOnlyList<DownloadFileProgress> ActiveFiles { get; init; } = Array.Empty<DownloadFileProgress>();

    /// <summary>实时下载速度（字节/秒）。</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>
    /// 由下载字节数直接给出的进度（0–1）。用于 Java 运行时这类单文件大下载；
    /// 为空时按 <see cref="Completed"/> / <see cref="Total"/> 计算文件数进度。
    /// </summary>
    public double? FractionOverride { get; init; }

    public double Fraction => FractionOverride
                              ?? (Total > 0 ? Math.Clamp((double)Completed / Total, 0d, 1d) : 0d);

    public override string ToString() => $"{Stage} {Completed}/{Total}";
}

/// <summary>安装结果。</summary>
public sealed class InstallReport
{
    public bool Success { get; init; }

    public int Downloaded { get; init; }

    public int Skipped { get; init; }

    public int Failed { get; init; }

    public long TotalBytes { get; init; }

    public IReadOnlyList<DownloadFailure> Failures { get; init; } = Array.Empty<DownloadFailure>();

    /// <summary>校验模式下缺失或被改动的文件。</summary>
    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();

    public static InstallReport Ok(int downloaded, int skipped, long totalBytes) => new()
    {
        Success = true,
        Downloaded = downloaded,
        Skipped = skipped,
        TotalBytes = totalBytes,
    };

    public static InstallReport FromBatch(DownloadBatchResult batch) => new()
    {
        Success = batch.Success,
        Downloaded = batch.Completed,
        Skipped = batch.Skipped,
        Failed = batch.Failed,
        Failures = batch.Failures,
    };
}
