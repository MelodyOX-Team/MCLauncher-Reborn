using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>一次「更新游戏」检查所需的实例信息。</summary>
public sealed class GameUpdateCheckRequest
{
    public required string GameRoot { get; init; }

    public required string InstanceId { get; init; }

    /// <summary>实例目录（开启隔离时是 versions/&lt;id&gt;，否则是游戏根目录）。</summary>
    public required string InstanceDirectory { get; init; }

    /// <summary>对应的原版版本 id；纯原版实例可为空。</summary>
    public string? BaseVersionId { get; init; }

    public string? LoaderName { get; init; }

    public string? LoaderVersion { get; init; }
}

/// <summary>游戏本体文件检查结果。</summary>
public sealed class GameBodyUpdateInfo
{
    public bool Complete { get; init; }

    public int MissingCount { get; init; }
}

/// <summary>模组加载器检查结果。</summary>
public sealed class LoaderUpdateInfo
{
    public ModLoaderKind? Kind { get; init; }

    public string Name { get; init; } = "原版";

    public string? CurrentVersion { get; init; }

    /// <summary>可升级到的最新版本；无需升级时为 null。</summary>
    public string? LatestVersion { get; init; }

    /// <summary>没有可升级版本时的说明（未记录游戏版本、接口失败、加载器不支持自动升级等）。</summary>
    public string? UnavailableReason { get; init; }

    public bool HasUpdate => Kind is not null && !string.IsNullOrWhiteSpace(LatestVersion);
}

/// <summary>更新游戏的三项检查汇总。</summary>
public sealed class GameUpdateCheckResult
{
    public required GameBodyUpdateInfo Body { get; init; }

    public required LoaderUpdateInfo Loader { get; init; }

    public required ResourceUpdateCheckResult Mods { get; init; }

    /// <summary>模组检查因网络或接口失败而中断。</summary>
    public bool ModsCheckFailed { get; init; }
}

/// <summary>
/// 「更新游戏」：检查本体文件、模组加载器与已安装模组，并执行勾选的升级。
/// 加载器升级是原地替换：只重写实例的版本 JSON，mods / 存档 / 配置保持不变。
/// </summary>
public sealed class GameUpdateService
{
    private readonly GameInstaller _installer;
    private readonly ModLoaderService _modLoaders;
    private readonly VersionRepository _repository;
    private readonly VersionManifestService _manifests;
    private readonly ResourceRepository _resources;
    private readonly ResourceUpdateService _resourceUpdates;
    private readonly ResourceDownloadService _resourceDownloads;
    private readonly ILogger _log;

    public GameUpdateService(
        GameInstaller installer,
        ModLoaderService modLoaders,
        VersionRepository repository,
        VersionManifestService manifests,
        ResourceRepository resources,
        ResourceUpdateService resourceUpdates,
        ResourceDownloadService resourceDownloads,
        ILogger log)
    {
        _installer = installer;
        _modLoaders = modLoaders;
        _repository = repository;
        _manifests = manifests;
        _resources = resources;
        _resourceUpdates = resourceUpdates;
        _resourceDownloads = resourceDownloads;
        _log = log;
    }

    // ---------------------------------------------------------------- 检查

    public async Task<GameUpdateCheckResult> CheckAsync(
        GameUpdateCheckRequest request,
        ResourceSource preferredSource,
        IProgress<string>? status,
        CancellationToken ct = default)
    {
        status?.Report("正在校验游戏本体文件…");

        // 校验要逐个文件算哈希，放到后台线程避免卡住界面。
        var verify = await Task.Run(
                () => _installer.VerifyAsync(request.GameRoot, request.InstanceId, deep: true, ct),
                ct)
            .ConfigureAwait(false);

        var loader = await CheckLoaderAsync(request, ct).ConfigureAwait(false);

        status?.Report("正在检查已安装模组…");
        ResourceUpdateCheckResult mods;
        var modsFailed = false;

        if (string.IsNullOrWhiteSpace(request.BaseVersionId))
        {
            // 实例没有记录原版版本时，接口会返回任意版本的最新文件，
            // 把 1.21 的模组推给 1.20 实例，宁可不推荐。
            status?.Report("实例未记录游戏版本，已跳过模组更新检查");
            mods = new ResourceUpdateCheckResult();
        }
        else
        {
            try
            {
                var resources = _resources.Scan(request.InstanceDirectory, ResourceKind.Mod);
                mods = await _resourceUpdates
                    .CheckAsync(resources, request.BaseVersionId, request.LoaderName, preferredSource, status, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("检查模组更新失败", ex);
                status?.Report("模组更新检查失败：" + ex.Message);
                mods = new ResourceUpdateCheckResult();
                modsFailed = true;
            }
        }

        return new GameUpdateCheckResult
        {
            Body = new GameBodyUpdateInfo
            {
                Complete = verify.Missing.Count == 0,
                MissingCount = verify.Missing.Count,
            },
            Loader = loader,
            Mods = mods,
            ModsCheckFailed = modsFailed,
        };
    }

    private async Task<LoaderUpdateInfo> CheckLoaderAsync(
        GameUpdateCheckRequest request,
        CancellationToken ct)
    {
        var parsed = ModLoaderService.ParseLoaderDescription(request.LoaderName);
        var current = request.LoaderVersion?.Trim();

        if (parsed is null)
        {
            // 没装加载器（原版），或是 OptiFine 这类不参与自动升级的加载器。
            var detected = string.IsNullOrWhiteSpace(request.LoaderName) ? "原版" : request.LoaderName!;
            return new LoaderUpdateInfo
            {
                Name = detected,
                CurrentVersion = current,
                UnavailableReason = string.Equals(detected, "原版", StringComparison.Ordinal)
                    ? null
                    : "暂不支持自动升级这种加载器",
            };
        }

        var kind = parsed.Value.Kind;
        var name = ModLoaderService.DisplayName(kind);

        // Forge / NeoForge 的可用版本按原版版本查询，实例没记录游戏版本时无从比较。
        if (kind is ModLoaderKind.Forge or ModLoaderKind.NeoForge
            && string.IsNullOrWhiteSpace(request.BaseVersionId))
        {
            return new LoaderUpdateInfo
            {
                Kind = kind,
                Name = name,
                CurrentVersion = current,
                UnavailableReason = "未记录游戏版本，暂时无法检查更新",
            };
        }

        try
        {
            var versions = await _modLoaders
                .ListLoaderVersionsAsync(kind, request.BaseVersionId ?? string.Empty, ct)
                .ConfigureAwait(false);

            var latest = versions.FirstOrDefault(option => option.IsLatest)?.Version
                         ?? versions.FirstOrDefault()?.Version;

            _log.Info(
                $"加载器检查：{name} {current ?? "(版本未知)"}"
                + $" / 原版 {request.BaseVersionId ?? "(未记录)"}"
                + $" / 最新 {latest ?? "(未知)"}");

            var hasUpdate = !string.IsNullOrWhiteSpace(current)
                            && LauncherInfoService.IsNewerVersion(latest, current!);

            return new LoaderUpdateInfo
            {
                Kind = kind,
                Name = name,
                CurrentVersion = current,
                LatestVersion = hasUpdate ? latest : null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"检查 {name} 更新失败", ex);
            return new LoaderUpdateInfo
            {
                Kind = kind,
                Name = name,
                CurrentVersion = current,
                UnavailableReason = "检查失败，可稍后重新检查",
            };
        }
    }

    // ---------------------------------------------------------------- 升级

    /// <summary>补全缺失或损坏的本体文件。</summary>
    public Task<InstallReport> RepairBodyAsync(
        GameUpdateCheckRequest request,
        InstallOptions options,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default) =>
        _installer.EnsureCompleteAsync(request.GameRoot, request.InstanceId, options, progress, ct);

    /// <summary>
    /// 原地升级加载器：重新生成实例的版本 JSON 并补齐新加载器的依赖库，
    /// 实例目录里的 mods / 存档 / 配置都不动。
    /// </summary>
    public async Task UpgradeLoaderAsync(
        GameUpdateCheckRequest request,
        ModLoaderKind kind,
        string targetVersion,
        InstallOptions options,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var baseVersionId = request.BaseVersionId
                            ?? throw new InvalidOperationException("实例缺少原版版本信息，无法升级加载器。");

        // 本地已安装的原版 JSON 优先；没有时从版本清单获取（只在内存里使用，不留快照）。
        var baseVersion = _repository.TryLoad(request.GameRoot, baseVersionId);
        if (baseVersion is { InheritsFrom: not null })
        {
            baseVersion = null;
        }

        if (baseVersion is null)
        {
            progress?.Report(new InstallProgress
            {
                Stage = $"正在获取原版 {baseVersionId} 的版本信息",
                Completed = 0,
                Total = 1,
            });

            var entry = await _manifests.FindAsync(baseVersionId, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"版本清单里找不到原版 {baseVersionId}，无法升级加载器。");

            baseVersion = await _manifests.GetVersionJsonAsync(entry, ct).ConfigureAwait(false);
            baseVersion.Id = baseVersionId;
            baseVersion.InheritsFrom = null;
        }

        var display = ModLoaderService.DisplayName(kind);

        progress?.Report(new InstallProgress
        {
            Stage = $"正在准备 {display} {targetVersion}",
            Completed = 0,
            Total = 1,
        });

        var loaderVersion = kind is ModLoaderKind.Fabric or ModLoaderKind.Quilt
            ? await _modLoaders
                .GetFabricLikeProfileAsync(kind, baseVersionId, targetVersion, ct)
                .ConfigureAwait(false)
            : await _modLoaders
                .GetForgeLikeVersionAsync(
                    kind, request.GameRoot, baseVersionId, targetVersion,
                    baseVersion, clientJarPath: null, options, progress, ct)
                .ConfigureAwait(false);

        var merged = VersionResolver.Merge(baseVersion, loaderVersion);
        merged.Id = request.InstanceId;
        merged.Jar = request.InstanceId;

        // 写入前先备份实例版本 JSON：依赖没补齐时要把实例还原成升级前的样子，
        // 否则会留下一个指向半装加载器的版本配置。
        var versionJsonPath = VersionRepository.VersionJsonPath(request.GameRoot, request.InstanceId);
        var backupJson = File.Exists(versionJsonPath)
            ? await File.ReadAllTextAsync(versionJsonPath, ct).ConfigureAwait(false)
            : null;

        _repository.Save(request.GameRoot, merged);

        try
        {
            var report = await _installer
                .EnsureCompleteAsync(request.GameRoot, request.InstanceId, options, progress, ct)
                .ConfigureAwait(false);

            if (!report.Success)
            {
                throw new InvalidOperationException(
                    $"加载器已写入，但依赖文件下载失败（{report.Failed} 个）");
            }
        }
        catch
        {
            RestoreInstanceVersionJson(versionJsonPath, backupJson);
            throw;
        }

        _log.Info($"实例 {request.InstanceId} 的加载器已原地升级到 {display} {targetVersion}");
    }

    /// <summary>把实例版本 JSON 还原成升级前的内容；升级前没有该文件时删除即可。</summary>
    private void RestoreInstanceVersionJson(string path, string? backupJson)
    {
        try
        {
            if (backupJson is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                PathUtil.EnsureParentDirectory(path);
                File.WriteAllText(path, backupJson);
            }

            _log.Warn($"加载器升级失败，已恢复实例版本 JSON：{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"恢复实例版本 JSON 失败，请手动还原：{path}", ex);
        }
    }

    /// <summary>逐个替换选中的模组文件，返回成功更新的数量。</summary>
    public async Task<int> UpgradeModsAsync(
        string instanceDirectory,
        IReadOnlyList<ResourceUpdateInfo> updates,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var succeeded = 0;

        foreach (var update in updates)
        {
            var result = await _resourceDownloads.DownloadAsync(
                new ResourceDownloadRequest
                {
                    Kind = ResourceKind.Mod,
                    File = update.File,
                    InstanceDirectory = instanceDirectory,
                    ReplacePath = update.Resource.FilePath,
                },
                progress,
                ct).ConfigureAwait(false);

            if (result.Success)
            {
                succeeded++;
            }
            else
            {
                _log.Warn($"更新模组 {update.Resource.FileName} 失败：{result.Error}");
            }
        }

        return succeeded;
    }
}
