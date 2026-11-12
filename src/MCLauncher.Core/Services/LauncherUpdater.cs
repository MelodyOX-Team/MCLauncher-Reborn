using System.Diagnostics;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// 启动器自更新：先把新 exe 下载到配置目录，退出前把自身改名成 <c>.old</c> 备份、
/// 再把新文件换到原位，最后启动新进程。
/// Windows 不允许覆盖正在运行的 exe，但允许给它改名，所以用「先改名再换位」的方式。
/// </summary>
public sealed class LauncherUpdater
{
    /// <summary>暂存文件名固定：Release 附件叫什么无所谓，换到原位时统一用启动器的名字。</summary>
    private const string StagedFileName = "MCLauncher-Reborn.exe";

    private readonly HttpDownloader _downloader;
    private readonly ILogger _log;
    private readonly string _configDirectory;

    public LauncherUpdater(HttpDownloader downloader, ILogger log, string launcherDirectory)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _configDirectory = LauncherPaths.EnsureConfigDirectory(
            launcherDirectory ?? throw new ArgumentNullException(nameof(launcherDirectory)), log);
    }

    /// <summary>当前进程是不是可以自我替换的单文件 exe（从 <c>dotnet run</c> 启动时不是）。</summary>
    public static bool CanSelfUpdate
    {
        get
        {
            var path = CurrentExecutablePath;
            return path is not null
                   && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                   && !Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>当前启动器 exe 的完整路径。</summary>
    public static string? CurrentExecutablePath => Environment.ProcessPath;

    /// <summary>新版本 exe 的暂存位置。</summary>
    public string StagedFile => Path.Combine(_configDirectory, "update", StagedFileName);

    /// <summary>
    /// 下载新版本 exe 到暂存位置。校验（大小 + SHA-256）不通过、或文件不是有效 exe 时返回 null。
    /// </summary>
    public async Task<string?> DownloadAsync(
        LauncherUpdateAsset asset,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var target = StagedFile;
        PathUtil.EnsureParentDirectory(target);

        var checker = new FileChecker { Size = asset.Size, Sha256 = asset.Sha256 };

        // 暂存目录可能残留上一次（甚至旧版本）的安装包；校验不通过时先删掉再下载，
        // 避免断点续传把陈旧文件当成同一次下载继续拼。
        if (File.Exists(target) && !checker.Check(target))
        {
            _log.Info("暂存的更新文件校验不通过，已删除并重新下载");
            TryDelete(target);
            TryDelete(target + ".part");
        }

        var outcome = await _downloader
            .DownloadAsync(
                new[] { new DownloadCandidate(asset.Url, "GitHub") },
                target,
                checker,
                progress,
                ct)
            .ConfigureAwait(false);

        if (!outcome.Success)
        {
            _log.Warn($"启动器更新下载失败：{outcome.Error}");
            return null;
        }

        if (!LooksLikeExecutable(target))
        {
            _log.Warn("下载到的更新文件不是有效的 exe，已删除");
            TryDelete(target);
            return null;
        }

        _log.Info($"启动器更新已下载：{asset.Name}（{new FileInfo(target).Length} 字节）");
        return target;
    }

    /// <summary>
    /// 把暂存的新 exe 换到目标位置。目标默认是当前进程的 exe；
    /// 先给旧文件改名成 <c>.old</c>，换位失败时再回滚。
    /// </summary>
    public void Apply(string? targetExePath = null)
    {
        var exe = (targetExePath ?? CurrentExecutablePath)
                  ?? throw new InvalidOperationException("取不到当前启动器的可执行文件路径");

        var staged = StagedFile;
        if (!File.Exists(staged))
        {
            throw new FileNotFoundException("更新文件尚未下载完成", staged);
        }

        var backup = exe + ".old";
        TryDelete(backup);
        File.Move(exe, backup);

        try
        {
            File.Move(staged, exe);
        }
        catch
        {
            // 换位失败：把旧文件改回原名，保证启动器还能用。
            try
            {
                File.Move(backup, exe);
            }
            catch (Exception rollbackError)
            {
                _log.Error($"回滚启动器失败，请手动把 {backup} 改名回 {exe}", rollbackError);
            }

            throw;
        }

        _log.Info($"启动器已替换为更新文件：{exe}");
    }

    /// <summary>启动替换后的新进程。</summary>
    public static void Restart(string? exePath = null)
    {
        var exe = (exePath ?? CurrentExecutablePath)
                  ?? throw new InvalidOperationException("取不到当前启动器的可执行文件路径");

        Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
        });
    }

    /// <summary>启动时清理上一次更新留下的 <c>.old</c> 备份。</summary>
    public static void CleanupLeftovers(ILogger log)
    {
        var exe = CurrentExecutablePath;
        if (exe is null)
        {
            return;
        }

        var backup = exe + ".old";
        try
        {
            if (File.Exists(backup))
            {
                File.Delete(backup);
                log.Info("已清理上一次更新留下的旧版备份");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 旧进程可能还没完全退出，下次启动再删。
            log.Warn("清理旧版备份失败（下次启动会再试）", ex);
        }
    }

    private static bool LooksLikeExecutable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 2)
            {
                return false;
            }

            var header = new byte[2];
            _ = stream.Read(header, 0, header.Length);
            return header[0] == (byte)'M' && header[1] == (byte)'Z';
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
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
        catch (UnauthorizedAccessException)
        {
        }
    }
}
