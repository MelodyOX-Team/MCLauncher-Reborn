using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>整合包类型。**识别顺序有语义**：同一个包里可能同时能匹配多种标记，必须按固定优先级判定。</summary>
public enum ModpackKind
{
    /// <summary>MCBBS 整合包（<c>mcbbs.packmeta</c>，或带 <c>addons</c> 的 manifest）。</summary>
    Mcbbs,

    /// <summary>MultiMC / Prism 整合包（<c>mmc-pack.json</c>）。</summary>
    Mmc,

    /// <summary>Modrinth 整合包（<c>modrinth.index.json</c>）。</summary>
    Modrinth,

    /// <summary>CurseForge 整合包（<c>manifest.json</c>，需要 API Key 才能解析文件）。</summary>
    CurseForge,

    /// <summary>普通压缩包（直接就是 .minecraft 的内容或 versions/... 结构）。</summary>
    PlainArchive,

    /// <summary>不支持的 .rar。</summary>
    Rar,

    Unknown,
}

/// <summary>整合包元信息。</summary>
public sealed class ModpackInfo
{
    public required ModpackKind Kind { get; init; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    /// <summary>整合包声明的游戏版本。</summary>
    public string? GameVersion { get; set; }

    /// <summary>整合包声明的加载器（如 <c>Fabric 0.15.3</c>、<c>Forge 49.0.30</c>）。</summary>
    public string? Loader { get; set; }

    /// <summary>需要下载的文件数量。</summary>
    public int FileCount { get; set; }

    /// <summary>给用户的提示（比如"需要 CurseForge API Key"）。</summary>
    public string? Note { get; set; }

    public string KindName => Kind switch
    {
        ModpackKind.Mcbbs => "MCBBS 整合包",
        ModpackKind.Mmc => "MultiMC 整合包",
        ModpackKind.Modrinth => "Modrinth 整合包",
        ModpackKind.CurseForge => "CurseForge 整合包",
        ModpackKind.PlainArchive => "普通压缩包",
        ModpackKind.Rar => "RAR 压缩包（不支持）",
        _ => "无法识别的压缩包",
    };

    public override string ToString() => $"{KindName}：{Name ?? "(未命名)"}";
}

/// <summary>整合包导入请求。</summary>
public sealed class ModpackImportRequest
{
    public required string ArchivePath { get; init; }

    public required string GameRoot { get; init; }

    /// <summary>资源与覆盖文件落到哪个实例目录（一般是 <c>versions/&lt;id&gt;</c>）。</summary>
    public required string InstanceDirectory { get; init; }

    /// <summary>是否下载整合包列出的文件（关闭时只铺覆盖文件）。</summary>
    public bool DownloadFiles { get; init; } = true;

    /// <summary>CurseForge API Key；为空时 CurseForge 整合包无法解析文件。</summary>
    public string? CurseForgeApiKey { get; init; }
}

/// <summary>整合包导入结果。</summary>
public sealed class ModpackImportResult
{
    public bool Success { get; init; }

    public int Downloaded { get; init; }

    public int Overwritten { get; init; }

    public int Skipped { get; init; }

    /// <summary>下载失败的文件数；&gt;0 时 <see cref="Success"/> 为 false。</summary>
    public int Failed { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>整合包要求的原版基底版本（供上层自动安装）。</summary>
    public string? RequiredGameVersion { get; init; }

    /// <summary>整合包要求的加载器（供上层自动安装）。</summary>
    public string? RequiredLoader { get; init; }
}

/// <summary>
/// 整合包导入。
/// <para>识别顺序：<c>mcbbs.packmeta</c> &gt; <c>mmc-pack.json</c> &gt; <c>modrinth.index.json</c> &gt; 带 addons 的 manifest。</para>
/// <para>不支持 <c>.rar</c>，会明确提示用户转成 zip。</para>
/// </summary>
public sealed partial class ModpackImporter
{
    private readonly HttpDownloader _downloader;
    private readonly DownloadSourceResolver _resolver;
    private readonly ResourceRepository _resources;
    private readonly ILogger _log;

    public ModpackImporter(
        HttpDownloader downloader,
        DownloadSourceResolver resolver,
        ResourceRepository resources,
        ILogger log)
    {
        _downloader = downloader;
        _resolver = resolver;
        _resources = resources;
        _log = log;
    }

    /// <summary>只看压缩包结构，判断类型并读出元信息（不下载任何东西）。</summary>
    public ModpackInfo Inspect(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("整合包文件不存在", archivePath);
        }

        if (archivePath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            return new ModpackInfo
            {
                Kind = ModpackKind.Rar,
                Note = "不支持 .rar 压缩包，请先用解压软件转成 .zip 再导入。",
            };
        }

        if (!ArchiveUtil.LooksLikeZip(archivePath))
        {
            return new ModpackInfo
            {
                Kind = ModpackKind.Unknown,
                Note = "这不是一个可识别的 zip 压缩包。",
            };
        }

        // 识别顺序不能改：MCBBS 的包里往往也带 manifest.json，先匹配更具体的标记。
        var entries = ArchiveUtil.ListEntries(archivePath)
            .Select(e => e.Replace('\\', '/').TrimStart('/'))
            .ToList();

        if (entries.Any(e => e.EndsWith("mcbbs.packmeta", StringComparison.OrdinalIgnoreCase)))
        {
            return InspectMcbbs(archivePath, entries);
        }

        if (entries.Any(e => e.EndsWith("mmc-pack.json", StringComparison.OrdinalIgnoreCase)))
        {
            return InspectMmc(archivePath, entries);
        }

        if (entries.Any(e => e.EndsWith("modrinth.index.json", StringComparison.OrdinalIgnoreCase)))
        {
            return InspectModrinth(archivePath, entries);
        }

        var manifestEntry = entries.FirstOrDefault(e =>
            e.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) && !e.Contains("modrinth", StringComparison.OrdinalIgnoreCase));

        if (manifestEntry is not null)
        {
            var json = ArchiveUtil.ReadTextEntry(archivePath, manifestEntry);
            if (json is not null)
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // manifest.json 是 CurseForge 的标志；带 addons 的则是 MCBBS 变体。
                    return document.RootElement.TryGetProperty("addons", out _)
                        ? InspectMcbbs(archivePath, entries)
                        : InspectCurseForge(document);
                }
            }
        }

        return new ModpackInfo
        {
            Kind = ModpackKind.PlainArchive,
            Name = Path.GetFileNameWithoutExtension(archivePath),
            Note = "按普通压缩包处理：会把内容按 .minecraft 结构铺到实例目录。",
        };
    }

    // ------------------------------------------------------------------ 导入

    public async Task<ModpackImportResult> ImportAsync(
        ModpackImportRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            // Inspect 会打开压缩包并解析 manifest，损坏的 zip / JSON 都必须转成失败结果而不是抛出。
            var info = Inspect(request.ArchivePath);

            _log.Info($"开始导入整合包：{info}");

            PathUtil.EnsureDirectory(request.InstanceDirectory);

            return info.Kind switch
            {
                ModpackKind.Modrinth => await ImportModrinthAsync(request, progress, ct).ConfigureAwait(false),
                ModpackKind.Mcbbs => await ImportMcbbsAsync(request, progress, ct).ConfigureAwait(false),
                ModpackKind.Mmc => await ImportMmcAsync(request, progress, ct).ConfigureAwait(false),
                ModpackKind.CurseForge => await ImportCurseForgeAsync(request, progress, ct).ConfigureAwait(false),
                ModpackKind.PlainArchive => ImportPlainArchive(request, progress),
                ModpackKind.Rar => new ModpackImportResult
                {
                    Success = false,
                    Error = "不支持 .rar 压缩包，请先用解压软件转成 .zip 再导入。",
                },
                _ => new ModpackImportResult { Success = false, Error = "无法识别这个压缩包，既不是整合包也不是有效的 zip。" },
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("导入整合包失败", ex);
            return new ModpackImportResult { Success = false, Error = ex.Message };
        }
    }

    private async Task<ModpackImportResult> ImportModrinthAsync(
        ModpackImportRequest request,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var indexName = ArchiveUtil.FindEntryName(request.ArchivePath, "modrinth.index.json")
                        ?? throw new InvalidDataException("压缩包里找不到 modrinth.index.json");

        var json = ArchiveUtil.ReadTextEntry(request.ArchivePath, indexName)
                   ?? throw new InvalidDataException("modrinth.index.json 读不出来");

        var index = JsonUtil.Deserialize<ModrinthIndex>(json)
                    ?? throw new InvalidDataException("modrinth.index.json 解析失败");

        var warnings = new List<string>();
        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        var success = true;

        if (request.DownloadFiles)
        {
            var plan = new List<(string Path, string Url, string? Sha1, long Size)>();

            foreach (var file in index.Files ?? new List<ModrinthFile>())
            {
                if (file.Downloads is not { Count: > 0 } || string.IsNullOrWhiteSpace(file.Path))
                {
                    continue;
                }

                // env.client == "unsupported" 的文件是服务端专用的，导进客户端只会添乱。
                if (string.Equals(file.Environment?.Client, "unsupported", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                plan.Add((file.Path!, file.Downloads[0], file.Hashes?.Sha1, file.FileSize));
            }

            var result = await DownloadPlannedFilesAsync(request, plan, progress, ct).ConfigureAwait(false);
            downloaded += result.Downloaded;
            skipped += result.Skipped;
            failed += result.Failed;
            success &= result.Success;
            warnings.AddRange(result.Warnings);
        }

        // overrides / client-overrides 直接铺到实例目录。
        var overwritten = ExtractOverrides(request, "overrides") + ExtractOverrides(request, "client-overrides");

        return new ModpackImportResult
        {
            Success = success,
            Downloaded = downloaded,
            Overwritten = overwritten,
            Skipped = skipped,
            Failed = failed,
            Error = failed > 0 ? $"{failed} 个整合包文件下载失败" : null,
            Warnings = warnings,
            RequiredGameVersion = index.Dependencies?.GetValueOrDefault("minecraft"),
            RequiredLoader = BuildLoaderText(index.Dependencies),
        };
    }

    private async Task<ModpackImportResult> ImportMcbbsAsync(
        ModpackImportRequest request,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var metaName = ArchiveUtil.FindEntryName(request.ArchivePath, "mcbbs.packmeta")
                       ?? ArchiveUtil.FindEntryName(request.ArchivePath, "manifest.json")
                       ?? throw new InvalidDataException("压缩包里找不到 mcbbs.packmeta 或 manifest.json");

        var json = ArchiveUtil.ReadTextEntry(request.ArchivePath, metaName)
                   ?? throw new InvalidDataException("packmeta 读不出来");

        var meta = JsonUtil.Deserialize<McbbsPackMeta>(json)
                   ?? throw new InvalidDataException("packmeta 解析失败");

        var warnings = new List<string>();
        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        var success = true;
        var plan = new List<(string Path, string Url, string? Sha1, long Size)>();

        foreach (var addon in meta.Addons ?? new List<McbbsAddon>())
        {
            foreach (var file in addon.Files ?? new List<McbbsFile>())
            {
                if (string.IsNullOrWhiteSpace(file.Path))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(file.Url))
                {
                    plan.Add((file.Path!, file.Url!, file.Hashes?.Sha1, file.Size));
                    continue;
                }

                // 只给了 Modrinth 的 project/version 引用：可以用公开 API 解析出下载地址。
                if (string.Equals(file.Source?.Type, "modrinth", StringComparison.OrdinalIgnoreCase)
                    && file.Source!.Id is not null
                    && file.Source.Version is not null)
                {
                    var url = await ResolveModrinthFileUrlAsync(file.Source.Id!, file.Source.Version!, ct).ConfigureAwait(false);
                    if (url is not null)
                    {
                        plan.Add((file.Path!, url, file.Hashes?.Sha1, file.Size));
                        continue;
                    }

                    warnings.Add($"无法从 Modrinth 解析下载地址：{file.Path}");
                    skipped++;
                    continue;
                }

                warnings.Add(string.Equals(file.Source?.Type, "curseforge", StringComparison.OrdinalIgnoreCase)
                    ? $"无法解析 CurseForge 文件地址：{file.Path}"
                    : $"缺少下载地址，已跳过：{file.Path}");
                skipped++;
            }
        }

        if (request.DownloadFiles && plan.Count > 0)
        {
            var result = await DownloadPlannedFilesAsync(request, plan, progress, ct).ConfigureAwait(false);
            downloaded += result.Downloaded;
            skipped += result.Skipped;
            failed += result.Failed;
            success &= result.Success;
            warnings.AddRange(result.Warnings);
        }

        var overwritten = ExtractOverrides(request, "overrides") + ExtractOverrides(request, "minecraft");

        return new ModpackImportResult
        {
            Success = success,
            Downloaded = downloaded,
            Overwritten = overwritten,
            Skipped = skipped,
            Failed = failed,
            Error = failed > 0 ? $"{failed} 个整合包文件下载失败" : null,
            Warnings = warnings,
            RequiredGameVersion = meta.LaunchInfo?.Version,
            RequiredLoader = meta.LaunchInfo?.LoaderType is { Length: > 0 } loader
                ? $"{loader} {meta.LaunchInfo.LoaderVersion}"
                : null,
        };
    }

    private async Task<ModpackImportResult> ImportMmcAsync(
        ModpackImportRequest request,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var packName = ArchiveUtil.FindEntryName(request.ArchivePath, "mmc-pack.json")
                       ?? throw new InvalidDataException("压缩包里找不到 mmc-pack.json");

        var json = ArchiveUtil.ReadTextEntry(request.ArchivePath, packName)
                   ?? throw new InvalidDataException("mmc-pack.json 读不出来");

        var pack = JsonUtil.Deserialize<MmcPack>(json)
                   ?? throw new InvalidDataException("mmc-pack.json 解析失败");

        string? gameVersion = null;
        string? loaderText = null;

        foreach (var component in pack.Components ?? new List<MmcComponent>())
        {
            var uid = component.Uid ?? string.Empty;

            if (uid.Equals("net.minecraft", StringComparison.OrdinalIgnoreCase))
            {
                gameVersion = component.Version;
            }
            else if (uid.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase))
            {
                loaderText = $"Fabric {component.Version}";
            }
            else if (uid.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase))
            {
                loaderText = $"Quilt {component.Version}";
            }
            else if (uid.Contains("forge", StringComparison.OrdinalIgnoreCase))
            {
                loaderText = $"Forge {component.Version}";
            }
            else if (uid.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            {
                loaderText = $"NeoForge {component.Version}";
            }
        }

        // MultiMC 把实例内容放在 minecraft/ 与 .minecraft/ 下，一并铺过去。
        var overwritten = ExtractOverrides(request, "minecraft")
                          + ExtractOverrides(request, ".minecraft")
                          + ExtractOverrides(request, "overrides");

        progress?.Report(new InstallProgress { Stage = "铺开覆盖文件", Completed = 1, Total = 1 });

        return await Task.FromResult(new ModpackImportResult
        {
            Success = true,
            Overwritten = overwritten,
            RequiredGameVersion = gameVersion,
            RequiredLoader = loaderText,
            Warnings = new List<string>
            {
                "MultiMC 整合包只包含加载器与覆盖文件；请按提示先安装对应原版与加载器。",
            },
        }).ConfigureAwait(false);
    }

    private async Task<ModpackImportResult> ImportCurseForgeAsync(
        ModpackImportRequest request,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CurseForgeApiKey))
        {
            return new ModpackImportResult
            {
                Success = false,
                Error = "当前版本无法访问 CurseForge 文件服务，无法解析这个整合包的下载地址；"
                        + "请改用 Modrinth / MCBBS 版本的同款整合包。",
            };
        }

        var manifestName = ArchiveUtil.FindEntryName(request.ArchivePath, "manifest.json")
                           ?? throw new InvalidDataException("压缩包里找不到 manifest.json");

        var json = ArchiveUtil.ReadTextEntry(request.ArchivePath, manifestName)
                   ?? throw new InvalidDataException("manifest.json 读不出来");

        var manifest = JsonUtil.Deserialize<CurseForgeManifest>(json)
                       ?? throw new InvalidDataException("manifest.json 解析失败");

        var plan = new List<(string Path, string Url, string? Sha1, long Size)>();
        var warnings = new List<string>();

        // projectId → classId：同一项目可能有多个文件，避免重复查询项目分类。
        var classIdCache = new Dictionary<int, int>();

        foreach (var file in manifest.Files ?? new List<CurseForgeFile>())
        {
            var resolved = await ResolveCurseForgeFileAsync(
                file.ProjectId,
                file.FileId,
                request.CurseForgeApiKey!,
                classIdCache,
                ct).ConfigureAwait(false);

            if (resolved is null)
            {
                warnings.Add($"无法解析 CurseForge 文件（projectID={file.ProjectId}, fileID={file.FileId}）");
                continue;
            }

            plan.Add(resolved.Value);
        }

        var result = request.DownloadFiles && plan.Count > 0
            ? await DownloadPlannedFilesAsync(request, plan, progress, ct).ConfigureAwait(false)
            : new ModpackImportResult { Success = true };

        var overwritten = ExtractOverrides(request, manifest.Overrides ?? "overrides");
        warnings.AddRange(result.Warnings);

        return new ModpackImportResult
        {
            Success = result.Success,
            Downloaded = result.Downloaded,
            Overwritten = overwritten,
            Skipped = result.Skipped,
            Failed = result.Failed,
            Error = result.Failed > 0 ? $"{result.Failed} 个整合包文件下载失败" : null,
            Warnings = warnings,
            RequiredGameVersion = manifest.Minecraft?.Version,
            RequiredLoader = manifest.Minecraft?.ModLoaders?.FirstOrDefault()?.Id,
        };
    }

    private ModpackImportResult ImportPlainArchive(
        ModpackImportRequest request,
        IProgress<InstallProgress>? progress)
    {
        var entries = ArchiveUtil.ListEntries(request.ArchivePath)
            .Select(e => e.Replace('\\', '/').TrimStart('/'))
            .ToList();

        // 普通包常常把内容包在 .minecraft/ 下，必须真正裁掉这一层后再落盘。
        var prefix = FindMinecraftPrefix(entries);

        progress?.Report(new InstallProgress { Stage = "解压整合包", Completed = 0, Total = 1 });

        var overwritten = ExtractPlainArchive(request, prefix);

        progress?.Report(new InstallProgress { Stage = "解压整合包", Completed = 1, Total = 1 });

        var warnings = new List<string>();
        if (prefix.Length > 0)
        {
            warnings.Add($"已裁掉 .minecraft 外层目录后解压（{prefix}）");
        }
        else if (entries.Any(e => e.Contains(".minecraft/", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("压缩包里存在多个 .minecraft 目录，无法确定裁剪点，已按原样解压。");
        }

        return new ModpackImportResult
        {
            Success = true,
            Overwritten = overwritten,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// 找出普通压缩包里统一的 <c>…/.minecraft/</c> 前缀。没有或存在多个不同前缀时返回空串（保持原样更安全）。
    /// </summary>
    private static string FindMinecraftPrefix(IReadOnlyList<string> entries)
    {
        const string marker = ".minecraft/";
        string? prefix = null;

        foreach (var entry in entries)
        {
            var index = entry.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var candidate = entry[..(index + marker.Length)];
            if (prefix is null)
            {
                prefix = candidate;
            }
            else if (!string.Equals(prefix, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }

        return prefix ?? string.Empty;
    }

    /// <summary>按裁剪后的相对路径解压普通压缩包，返回实际写入的文件数。</summary>
    private static int ExtractPlainArchive(ModpackImportRequest request, string prefix)
    {
        var count = 0;

        using var archive = ZipFile.OpenRead(request.ArchivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            var relative = prefix.Length > 0
                           && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? name[prefix.Length..]
                : name;

            if (relative.Length == 0)
            {
                continue;
            }

            var target = Path.Combine(
                request.InstanceDirectory,
                relative.Replace('/', Path.DirectorySeparatorChar));

            if (!PathUtil.IsParentOf(request.InstanceDirectory, target))
            {
                continue;
            }

            PathUtil.EnsureParentDirectory(target);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>下载计划中的文件到实例目录（按 <c>path</c> 相对路径落位）。</summary>
    private async Task<ModpackImportResult> DownloadPlannedFilesAsync(
        ModpackImportRequest request,
        IReadOnlyList<(string Path, string Url, string? Sha1, long Size)> plan,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var requests = new List<DownloadRequest>(plan.Count);
        var warnings = new List<string>();

        foreach (var (relativePath, url, sha1, size) in plan)
        {
            // 禁止压缩包把文件写到实例目录之外。
            if (!PathUtil.IsValidFileName(Path.GetFileName(relativePath)) || relativePath.Contains("..", StringComparison.Ordinal))
            {
                warnings.Add($"跳过可疑路径：{relativePath}");
                continue;
            }

            var target = Path.Combine(request.InstanceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!PathUtil.IsParentOf(request.InstanceDirectory, target))
            {
                warnings.Add($"跳过越界路径：{relativePath}");
                continue;
            }

            requests.Add(new DownloadRequest
            {
                Name = relativePath,
                Candidates = _resolver.Resolve(url, DownloadUrlKind.ModPlatform),
                TargetPath = target,
                Checker = FileChecker.Of(sha1, size),
            });
        }

        if (requests.Count == 0)
        {
            return new ModpackImportResult { Success = true, Warnings = warnings };
        }

        var batch = new DownloadBatch(_downloader, _log);

        var batchProgress = progress is null
            ? null
            : new MCLauncher.Core.Abstractions.InlineProgress<DownloadBatchProgress>(p => progress.Report(new InstallProgress
            {
                Stage = "下载整合包文件",
                Completed = p.Completed + p.Skipped,
                Total = p.Total,
                DownloadedBytes = p.DownloadedBytes,
                TotalBytes = p.TotalBytes,
                Current = p.Current,
                ActiveFiles = p.ActiveFiles,
                BytesPerSecond = p.BytesPerSecond,
            }));

        var result = await batch.RunAsync(requests, 8, batchProgress, ct).ConfigureAwait(false);

        warnings.AddRange(result.Failures.Select(f => $"下载失败：{f.Name}（{f.Error}）"));

        return new ModpackImportResult
        {
            Success = result.Failed == 0,
            Downloaded = result.Completed,
            Skipped = result.Skipped,
            Failed = result.Failed,
            Error = result.Failed > 0
                ? $"{result.Failed} 个整合包文件下载失败：" + string.Join("；", result.Failures.Take(3).Select(f => f.Name))
                : null,
            Warnings = warnings,
        };
    }

    /// <summary>把压缩包里某个前缀下的内容铺到实例目录，返回铺开的文件数。</summary>
    private int ExtractOverrides(ModpackImportRequest request, string prefix)
    {
        var normalized = prefix.Replace('\\', '/').TrimEnd('/') + "/";
        var entries = ArchiveUtil.ListEntries(request.ArchivePath)
            .Select(e => e.Replace('\\', '/').TrimStart('/'))
            .Where(e => e.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Where(e => !e.EndsWith('/'))
            .ToList();

        if (entries.Count == 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var entry in entries)
        {
            var relative = entry[normalized.Length..];
            var target = Path.Combine(request.InstanceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

            if (!PathUtil.IsParentOf(request.InstanceDirectory, target))
            {
                continue;
            }

            if (ArchiveUtil.ExtractEntryTo(request.ArchivePath, entry, target, overwrite: true))
            {
                count++;
            }
        }

        return count;
    }

    private async Task<string?> ResolveModrinthFileUrlAsync(string projectId, string versionId, CancellationToken ct)
    {
        try
        {
            var json = await _downloader.RequestStringAsync(
                $"https://api.modrinth.com/v2/version/{Uri.EscapeDataString(versionId)}",
                HttpMethod.Get,
                null,
                null,
                null,
                ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("files", out var files)
                && files.ValueKind == JsonValueKind.Array
                && files.GetArrayLength() > 0
                && files[0].TryGetProperty("url", out var url))
            {
                return url.GetString();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"解析 Modrinth 文件失败（project={projectId}, version={versionId}）", ex);
        }

        return null;
    }

    private async Task<(string Path, string Url, string? Sha1, long Size)?> ResolveCurseForgeFileAsync(
        int projectId,
        int fileId,
        string apiKey,
        IDictionary<int, int> classIdCache,
        CancellationToken ct)
    {
        try
        {
            var headers = new Dictionary<string, string> { ["x-api-key"] = apiKey };
            var json = await _downloader.RequestStringAsync(
                $"https://api.curseforge.com/v1/mods/{projectId}/files/{fileId}",
                HttpMethod.Get,
                null,
                null,
                headers,
                ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                return null;
            }

            var url = data.TryGetProperty("downloadUrl", out var urlElement) ? urlElement.GetString() : null;
            var fileName = data.TryGetProperty("fileName", out var nameElement) ? nameElement.GetString() : null;
            var size = data.TryGetProperty("fileLength", out var sizeElement) ? sizeElement.GetInt64() : -1;

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var hashes = data.TryGetProperty("hashes", out var hashElement) && hashElement.ValueKind == JsonValueKind.Array
                ? hashElement.EnumerateArray()
                    .FirstOrDefault(h => h.TryGetProperty("algo", out var algo) && algo.GetInt32() == 1)
                : default;

            var sha1 = hashes.ValueKind == JsonValueKind.Object && hashes.TryGetProperty("value", out var value)
                ? value.GetString()
                : null;

            // CurseForge 不直接给相对路径，只能按项目分类 / 文件类型猜目录。
            // 光影包与资源包都是 zip，不能一律放进 resourcepacks。
            var classId = await GetCurseForgeClassIdAsync(projectId, apiKey, classIdCache, ct)
                .ConfigureAwait(false);
            var directory = GuessCurseForgeDirectory(fileName!, classId);
            return (Path.Combine(directory, fileName!), url!, sha1, size);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"解析 CurseForge 文件失败（{projectId}/{fileId}）", ex);
            return null;
        }
    }

    /// <summary>查询项目 classId；失败时缓存 -1，后续按文件名兜底，不再重复请求。</summary>
    private async Task<int> GetCurseForgeClassIdAsync(
        int projectId,
        string apiKey,
        IDictionary<int, int> classIdCache,
        CancellationToken ct)
    {
        if (classIdCache.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        var classId = -1;
        try
        {
            var headers = new Dictionary<string, string> { ["x-api-key"] = apiKey };
            var json = await _downloader.RequestStringAsync(
                $"https://api.curseforge.com/v1/mods/{projectId}",
                HttpMethod.Get,
                null,
                null,
                headers,
                ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("classId", out var classIdElement))
            {
                classId = classIdElement.GetInt32();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"查询 CurseForge 项目分类失败：{projectId}", ex);
        }

        classIdCache[projectId] = classId;
        return classId;
    }

    private static string GuessCurseForgeDirectory(string fileName, int classId)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension == ".jar")
        {
            return "mods";
        }

        if (extension is ".litematic" or ".schematic" or ".nbt")
        {
            return "schematics";
        }

        if (extension is ".zip" or ".mcpack")
        {
            // 6552 = 光影包、12 = 资源包；classId 查不到时按文件名兜底。
            if (classId == 6552)
            {
                return "shaderpacks";
            }

            if (classId == 12)
            {
                return "resourcepacks";
            }

            var name = Path.GetFileNameWithoutExtension(fileName);
            return name.Contains("shader", StringComparison.OrdinalIgnoreCase)
                ? "shaderpacks"
                : "resourcepacks";
        }

        return "mods";
    }

    private static string? BuildLoaderText(Dictionary<string, string>? dependencies)
    {
        if (dependencies is null)
        {
            return null;
        }

        foreach (var (key, value) in dependencies)
        {
            if (key.Contains("fabric", StringComparison.OrdinalIgnoreCase))
            {
                return $"Fabric {value}";
            }

            if (key.Contains("quilt", StringComparison.OrdinalIgnoreCase))
            {
                return $"Quilt {value}";
            }

            if (key.Contains("forge", StringComparison.OrdinalIgnoreCase))
            {
                return $"Forge {value}";
            }

            if (key.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            {
                return $"NeoForge {value}";
            }
        }

        return null;
    }

    private ModpackInfo InspectMcbbs(string archivePath, IReadOnlyList<string> entries)
    {
        var info = new ModpackInfo { Kind = ModpackKind.Mcbbs };

        var metaName = entries.FirstOrDefault(e => e.EndsWith("mcbbs.packmeta", StringComparison.OrdinalIgnoreCase))
                       ?? entries.FirstOrDefault(e => e.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));

        if (metaName is null)
        {
            return info;
        }

        var json = ArchiveUtil.ReadTextEntry(archivePath, metaName);
        if (json is null)
        {
            return info;
        }

        var meta = JsonUtil.Deserialize<McbbsPackMeta>(json);
        info.Name = meta?.Name;
        info.Version = meta?.Version;
        info.GameVersion = meta?.LaunchInfo?.Version;
        info.Loader = meta?.LaunchInfo?.LoaderType is { Length: > 0 } loader
            ? $"{loader} {meta.LaunchInfo.LoaderVersion}"
            : null;
        info.FileCount = meta?.Addons?.Sum(a => a.Files?.Count ?? 0) ?? 0;
        return info;
    }

    private ModpackInfo InspectMmc(string archivePath, IReadOnlyList<string> entries)
    {
        var info = new ModpackInfo { Kind = ModpackKind.Mmc };

        var packName = entries.FirstOrDefault(e => e.EndsWith("mmc-pack.json", StringComparison.OrdinalIgnoreCase));
        var json = packName is null ? null : ArchiveUtil.ReadTextEntry(archivePath, packName);

        if (json is not null)
        {
            var pack = JsonUtil.Deserialize<MmcPack>(json);
            foreach (var component in pack?.Components ?? new List<MmcComponent>())
            {
                var uid = component.Uid ?? string.Empty;
                if (uid.Equals("net.minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    info.GameVersion = component.Version;
                }
                else if (uid.Contains("loader", StringComparison.OrdinalIgnoreCase)
                         || uid.Contains("forge", StringComparison.OrdinalIgnoreCase))
                {
                    info.Loader = $"{component.CachedName ?? uid} {component.Version}";
                }
            }
        }

        info.Name = Path.GetFileNameWithoutExtension(archivePath);
        return info;
    }

    private ModpackInfo InspectModrinth(string archivePath, IReadOnlyList<string> entries)
    {
        var info = new ModpackInfo { Kind = ModpackKind.Modrinth };

        var indexName = entries.FirstOrDefault(e => e.EndsWith("modrinth.index.json", StringComparison.OrdinalIgnoreCase));
        var json = indexName is null ? null : ArchiveUtil.ReadTextEntry(archivePath, indexName);

        if (json is not null)
        {
            var index = JsonUtil.Deserialize<ModrinthIndex>(json);
            info.Name = index?.Name;
            info.Version = index?.VersionId;
            info.GameVersion = index?.Dependencies?.GetValueOrDefault("minecraft");
            info.Loader = BuildLoaderText(index?.Dependencies);
            info.FileCount = index?.Files?.Count ?? 0;
        }

        return info;
    }

    private static ModpackInfo InspectCurseForge(JsonDocument manifest)
    {
        var info = new ModpackInfo { Kind = ModpackKind.CurseForge };

        if (manifest.RootElement.TryGetProperty("name", out var name))
        {
            info.Name = name.GetString();
        }

        if (manifest.RootElement.TryGetProperty("version", out var version))
        {
            info.Version = version.GetString();
        }

        if (manifest.RootElement.TryGetProperty("minecraft", out var minecraft)
            && minecraft.ValueKind == JsonValueKind.Object)
        {
            if (minecraft.TryGetProperty("version", out var gameVersion))
            {
                info.GameVersion = gameVersion.GetString();
            }

            if (minecraft.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
            {
                var first = loaders.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var loaderId))
                {
                    info.Loader = loaderId.GetString();
                }
            }
        }

        if (manifest.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            info.FileCount = files.GetArrayLength();
        }

        return info;
    }

    // ------------------------------------------------------------------ 响应模型

    private sealed class ModrinthIndex
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("versionId")]
        public string? VersionId { get; set; }

        [JsonPropertyName("dependencies")]
        public Dictionary<string, string>? Dependencies { get; set; }

        [JsonPropertyName("files")]
        public List<ModrinthFile>? Files { get; set; }
    }

    private sealed class ModrinthFile
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("hashes")]
        public ModrinthHashes? Hashes { get; set; }

        [JsonPropertyName("downloads")]
        public List<string>? Downloads { get; set; }

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("env")]
        public ModrinthEnvironment? Environment { get; set; }
    }

    private sealed class ModrinthHashes
    {
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }
    }

    private sealed class ModrinthEnvironment
    {
        [JsonPropertyName("client")]
        public string? Client { get; set; }
    }

    private sealed class McbbsPackMeta
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("addons")]
        public List<McbbsAddon>? Addons { get; set; }

        [JsonPropertyName("launchInfo")]
        public McbbsLaunchInfo? LaunchInfo { get; set; }
    }

    private sealed class McbbsAddon
    {
        [JsonPropertyName("files")]
        public List<McbbsFile>? Files { get; set; }
    }

    private sealed class McbbsFile
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("hashes")]
        public ModrinthHashes? Hashes { get; set; }

        [JsonPropertyName("source")]
        public McbbsSource? Source { get; set; }
    }

    private sealed class McbbsSource
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    private sealed class McbbsLaunchInfo
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("loaderType")]
        public string? LoaderType { get; set; }

        [JsonPropertyName("loaderVersion")]
        public string? LoaderVersion { get; set; }
    }

    private sealed class MmcPack
    {
        [JsonPropertyName("components")]
        public List<MmcComponent>? Components { get; set; }
    }

    private sealed class MmcComponent
    {
        [JsonPropertyName("uid")]
        public string? Uid { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("cachedName")]
        public string? CachedName { get; set; }
    }

    private sealed class CurseForgeManifest
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("overrides")]
        public string? Overrides { get; set; }

        [JsonPropertyName("minecraft")]
        public CurseForgeMinecraft? Minecraft { get; set; }

        [JsonPropertyName("files")]
        public List<CurseForgeFile>? Files { get; set; }
    }

    private sealed class CurseForgeMinecraft
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("modLoaders")]
        public List<CurseForgeModLoader>? ModLoaders { get; set; }
    }

    private sealed class CurseForgeModLoader
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class CurseForgeFile
    {
        [JsonPropertyName("projectID")]
        public int ProjectId { get; set; }

        [JsonPropertyName("fileID")]
        public int FileId { get; set; }
    }
}
