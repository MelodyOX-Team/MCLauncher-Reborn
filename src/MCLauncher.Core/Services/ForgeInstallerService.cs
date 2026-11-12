using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// Forge / NeoForge 安装器（installer jar）的本地解析与处理器执行。
/// <para>
/// 安装器本身只是数据包：<c>install_profile.json</c> 描述版本 JSON、
/// 内嵌 maven 制品、变量表和处理器列表。这里不去运行安装器的
/// <c>--installClient</c>，而是按同样的规则自己跑处理器，
/// 因此 <c>versions</c> 下不需要先放一份原版版本目录。
/// </para>
/// <list type="bullet">
/// <item>1.12.2 及更早：<c>spec 0</c>、无处理器，只有内嵌 maven + <c>version.json</c>。</item>
/// <item>1.13+：处理器按顺序执行，生成 SRG / 精简 / 补丁后的客户端 jar。</item>
/// </list>
/// </summary>
public sealed class ForgeInstallerService
{
    private const string LibrariesPrefix = "maven/";
    private const string TempDirectoryName = "temp";

    private static readonly TimeSpan ProcessorTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpDownloader _downloader;
    private readonly DownloadSourceResolver _resolver;
    private readonly GameInstaller _installer;
    private readonly JavaService _javaService;
    private readonly ZuluJavaService? _zuluJava;
    private readonly ILogger _log;

    public ForgeInstallerService(
        HttpDownloader downloader,
        DownloadSourceResolver resolver,
        GameInstaller installer,
        JavaService javaService,
        ILogger log,
        ZuluJavaService? zuluJava = null)
    {
        _downloader = downloader;
        _resolver = resolver;
        _installer = installer;
        _javaService = javaService;
        _zuluJava = zuluJava;
        _log = log;
    }

    // ------------------------------------------------------------------ 入口

    /// <summary>下载（或复用）安装器 jar，返回本地路径。</summary>
    public async Task<string> DownloadInstallerAsync(
        string gameRoot,
        ModLoaderKind kind,
        string loaderVersion,
        string installerUrl,
        CancellationToken ct = default)
    {
        var tempDirectory = Path.Combine(gameRoot, ".mclauncher", TempDirectoryName);
        PathUtil.EnsureDirectory(tempDirectory);

        var fileName = Sanitize($"{ModLoaderService.DisplayName(kind)}-{loaderVersion}-installer.jar");
        var path = Path.Combine(tempDirectory, fileName);

        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            // 上次中断或安全软件扫描可能留下不完整的文件；结构不可读就删掉重下。
            if (IsReadableZip(path))
            {
                _log.Info($"复用已下载的 {ModLoaderService.DisplayName(kind)} 安装器：{path}");
                return path;
            }

            _log.Warn($"已缓存的 {ModLoaderService.DisplayName(kind)} 安装器不是有效的 ZIP，将重新下载：{path}");
            TryDeleteFile(path);
        }

        var batch = new DownloadBatch(_downloader, _log);
        var result = await batch
            .RunAsync(
                new[]
                {
                    new DownloadRequest
                    {
                        Name = fileName,
                        Candidates = _resolver.Resolve(installerUrl, DownloadUrlKind.ModLoaderInstaller),
                        TargetPath = path,
                        Checker = FileChecker.ExistenceOnly,
                    },
                },
                1,
                null,
                ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            var error = result.Failures.Count > 0 ? result.Failures[0].Error : "没有可用的下载源";
            throw new InvalidOperationException(
                $"下载 {ModLoaderService.DisplayName(kind)} 安装器失败：{error}");
        }

        return path;
    }

    /// <summary>
    /// 解析安装器并执行全部客户端处理器，返回可以交给合并流程的加载器版本 JSON。
    /// <paramref name="clientJarPath"/> 是实例最终要用的客户端 jar；
    /// 处理器执行前会确保它已就位（优先复制本机已有的原版 jar，其次下载）。
    /// </summary>
    public async Task<GameVersionJson> PrepareAsync(
        ModLoaderKind kind,
        string gameRoot,
        string gameVersion,
        string loaderVersion,
        GameVersionJson baseVersion,
        string installerPath,
        string? clientJarPath,
        InstallOptions options,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var display = ModLoaderService.DisplayName(kind);

        progress?.Report(new InstallProgress
        {
            Stage = $"读取 {display} 安装配置",
            Completed = 0,
            Total = 1,
            Current = installerPath,
        });

        using var archive = OpenInstaller(installerPath);
        var profile = ReadProfile(archive);

        var installerGameVersion = profile.ResolveMinecraftVersion();
        if (!string.IsNullOrWhiteSpace(installerGameVersion)
            && !string.Equals(installerGameVersion, gameVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{display} 安装器对应的是 {installerGameVersion}，与请求的 {gameVersion} 不一致");
        }

        var versionJson = ReadVersionJson(archive, profile);
        if (string.IsNullOrWhiteSpace(versionJson.Id))
        {
            versionJson.Id = profile.Version;
        }

        if (string.IsNullOrWhiteSpace(versionJson.Id))
        {
            throw new InvalidDataException($"{display} 安装器里没有可用的版本 id");
        }

        // 安装器内嵌的 maven 制品（老版本 Forge 的 universal jar 都在这里）。
        var embedded = ExtractEmbeddedLibraries(archive, gameRoot, profile);
        if (embedded > 0)
        {
            _log.Info($"{display} 安装器内嵌制品已展开：{embedded} 个");
        }

        // 处理器依赖（同时也包含版本 JSON 里的绝大部分库）。
        var libraries = profile.Libraries ?? new List<Library>();
        if (libraries.Count > 0)
        {
            var report = await _installer
                .EnsureLibrariesAsync(gameRoot, libraries, options, progress, ct)
                .ConfigureAwait(false);

            if (!report.Success)
            {
                throw new InvalidOperationException(
                    $"{display} 安装器依赖下载失败：{report.Failed} 个文件");
            }
        }

        var processors = (profile.Processors ?? new List<ForgeProcessor>())
            .Where(processor => processor.AppliesToClient)
            .ToList();

        if (processors.Count > 0)
        {
            await RunProcessorsAsync(
                    kind, gameRoot, gameVersion, display, archive, installerPath,
                    profile, processors, baseVersion, clientJarPath, progress, ct)
                .ConfigureAwait(false);
        }
        else
        {
            _log.Info($"{display} {loaderVersion} 不需要处理器，直接使用安装器里的版本 JSON");
        }

        progress?.Report(new InstallProgress
        {
            Stage = $"{display} 处理器完成",
            Completed = 1,
            Total = 1,
        });

        // 必须先释放安装器 ZIP：Windows 上文件仍被占用时删除缓存目录会失败。
        archive.Dispose();

        CleanupInstallerCache(gameRoot);
        return versionJson;
    }

    /// <summary>安装成功后清理安装器缓存；失败时保留，方便直接重试而不用重新下载。</summary>
    private void CleanupInstallerCache(string gameRoot)
    {
        TryDeleteDirectory(Path.Combine(gameRoot, ".mclauncher", TempDirectoryName));
        GameInstaller.CleanupManagedDirectory(gameRoot);
    }

    // ------------------------------------------------------------------ 处理器

    private async Task RunProcessorsAsync(
        ModLoaderKind kind,
        string gameRoot,
        string gameVersion,
        string display,
        ZipArchive archive,
        string installerPath,
        ForgeInstallProfile profile,
        IReadOnlyList<ForgeProcessor> processors,
        GameVersionJson baseVersion,
        string? clientJarPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var tempDirectory = Path.Combine(
            gameRoot, ".mclauncher", TempDirectoryName,
            Sanitize($"{display}-{gameVersion}-{Guid.NewGuid():N}"));
        PathUtil.EnsureDirectory(tempDirectory);

        try
        {
            // 变量先按「客户端 jar 应该落在哪」构造好；只有确实要跑处理器时才去准备 jar，
            // 这样已装好的实例重复安装时不需要再下一遍四十多兆的客户端。
            var variables = BuildVariables(
                archive, profile, gameRoot, installerPath, gameVersion,
                ResolveClientJarTarget(gameRoot, gameVersion, clientJarPath), tempDirectory);

            // 处理器的输出目录不一定预先存在（例如补丁 jar 的 forge 版本目录）。
            EnsureVariableParents(variables);

            var pending = processors
                .Where(processor => !AreOutputsSatisfied(processor, variables, gameRoot))
                .ToList();

            if (pending.Count == 0)
            {
                _log.Info($"{display} 处理器的输出全部已就绪，跳过执行");
                return;
            }

            var requiredJava = ModLoaderService.RequiredJavaMajor(gameVersion);
            var java = _javaService.SelectBest(requiredJava);

            if (java is null && _zuluJava is not null)
            {
                progress?.Report(new InstallProgress { Stage = $"正在下载 Java {requiredJava}…" });
                var javaProgress = new InlineProgress<JavaRuntimeProgress>(p =>
                    progress?.Report(new InstallProgress
                    {
                        Stage = p.Stage,
                        FractionOverride = p.Fraction,
                        BytesPerSecond = p.BytesPerSecond,
                        ActiveFiles = p.ActiveFiles,
                    }));

                java = await _zuluJava
                    .EnsureAsync(requiredJava, javaProgress, ct)
                    .ConfigureAwait(false);
            }

            if (java is null)
            {
                throw new InvalidOperationException(
                    $"没有可用的 Java {requiredJava}，无法运行 {display} 处理器");
            }

            var minecraftJar = await EnsureClientJarAsync(
                    gameRoot, gameVersion, baseVersion, clientJarPath, ct)
                .ConfigureAwait(false);

            variables["MINECRAFT_JAR"] = Path.GetFullPath(minecraftJar);

            for (var i = 0; i < pending.Count; i++)
            {
                var processor = pending[i];
                var name = processor.Jar ?? $"processor-{i + 1}";

                progress?.Report(new InstallProgress
                {
                    Stage = $"运行 {display} 处理器",
                    Completed = i,
                    Total = pending.Count,
                    Current = name,
                });

                await RunProcessorAsync(
                        java.Path, gameRoot, processor, variables, name, ct)
                    .ConfigureAwait(false);
            }

            progress?.Report(new InstallProgress
            {
                Stage = $"运行 {display} 处理器",
                Completed = pending.Count,
                Total = pending.Count,
            });

            _log.Info($"{display} 处理器全部完成（{pending.Count} 个）");
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private string ResolveClientJarTarget(string gameRoot, string gameVersion, string? clientJarPath) =>
        string.IsNullOrWhiteSpace(clientJarPath)
            ? Path.Combine(gameRoot, ".mclauncher", TempDirectoryName,
                Sanitize($"minecraft-{gameVersion}.jar"))
            : clientJarPath!;

    private bool AreOutputsSatisfied(
        ForgeProcessor processor,
        IReadOnlyDictionary<string, string> variables,
        string gameRoot)
    {
        var outputs = ResolveOutputs(processor, variables, gameRoot);
        return outputs.Count > 0 && outputs.All(output => VerifyFile(output.Key, output.Value));
    }

    private async Task RunProcessorAsync(
        string javaPath,
        string gameRoot,
        ForgeProcessor processor,
        IReadOnlyDictionary<string, string> variables,
        string name,
        CancellationToken ct)
    {
        var outputs = ResolveOutputs(processor, variables, gameRoot);

        if (outputs.Count > 0 && outputs.All(output => VerifyFile(output.Key, output.Value)))
        {
            _log.Trace($"处理器 {name} 的输出已齐全，跳过执行");
            return;
        }

        if (string.IsNullOrWhiteSpace(processor.Jar))
        {
            throw new InvalidDataException($"处理器 {name} 缺少 jar 坐标");
        }

        var processorJar = GameInstaller.LibraryFilePath(gameRoot, ArtifactPath(processor.Jar));
        if (!File.Exists(processorJar))
        {
            throw new FileNotFoundException($"处理器 {name} 的 jar 不存在", processorJar);
        }

        var classpath = new List<string>();
        foreach (var coordinate in processor.Classpath ?? new List<string>())
        {
            var path = GameInstaller.LibraryFilePath(gameRoot, ArtifactPath(coordinate));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"处理器 {name} 的依赖缺失：{coordinate}", path);
            }

            classpath.Add(path);
        }

        classpath.Add(processorJar);

        var mainClass = ReadMainClass(processorJar)
                        ?? throw new InvalidDataException($"处理器 {name} 的 jar 没有 Main-Class");

        var arguments = new List<string>
        {
            "-cp",
            string.Join(Path.PathSeparator, classpath),
            mainClass,
        };

        foreach (var arg in processor.Args ?? new List<string>())
        {
            arguments.Add(ParseLiteral(arg, variables, coordinate =>
                GameInstaller.LibraryFilePath(gameRoot, ArtifactPath(coordinate))));
        }

        _log.Info($"执行处理器：{name}（{mainClass}）");
        var (exitCode, output) = await RunJavaAsync(javaPath, arguments, gameRoot, ct).ConfigureAwait(false);

        if (exitCode != 0)
        {
            _log.Error($"处理器 {name} 退出码 {exitCode}：{Tail(output)}");
            throw new InvalidOperationException($"处理器 {name} 执行失败（退出码 {exitCode}）：{Tail(output)}");
        }

        foreach (var (outputPath, outputSha1) in outputs)
        {
            if (VerifyFile(outputPath, outputSha1))
            {
                continue;
            }

            // 压缩实现不同（例如 JDK 自带的 zlib 版本变化）会让重新打包的 jar 哈希对不上，
            // 但内容结构仍然有效。与 HMCL 一样，jar 只要 ZIP 结构可读就放行。
            if (outputPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                && IsReadableZip(outputPath))
            {
                _log.Warn($"处理器 {name} 的输出哈希不一致，但 ZIP 结构有效，按通过处理：{outputPath}");
                continue;
            }

            throw new InvalidDataException($"处理器 {name} 的输出校验失败：{outputPath}");
        }
    }

    private static bool IsReadableZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IReadOnlyDictionary<string, string> ResolveOutputs(
        ForgeProcessor processor,
        IReadOnlyDictionary<string, string> variables,
        string gameRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, rawValue) in processor.Outputs ?? new Dictionary<string, string>())
        {
            var key = ParseLiteral(rawKey, variables, coordinate =>
                GameInstaller.LibraryFilePath(gameRoot, ArtifactPath(coordinate)));
            var value = ParseLiteral(rawValue, variables, coordinate =>
                GameInstaller.LibraryFilePath(gameRoot, ArtifactPath(coordinate)));

            result[key] = value;
        }

        return result;
    }

    private static bool VerifyFile(string path, string sha1) =>
        string.IsNullOrWhiteSpace(sha1) ||
        FileChecker.Of(sha1).Check(path);

    private static string? ReadMainClass(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var entry = archive.GetEntry("META-INF/MANIFEST.MF");
            if (entry is null)
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                const string prefix = "Main-Class:";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line[prefix.Length..].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // 交给上层报「没有 Main-Class」。
        }

        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunJavaAsync(
        string javaPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 Java 处理器进程");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProcessorTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 已经退出就忽略。
            }

            // 进程被杀后管道会关闭；必须等待输出读取任务结束，否则会留下孤儿任务。
            try
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 输出读取随进程结束而失败，不影响超时判定。
            }

            throw new TimeoutException($"处理器超过 {ProcessorTimeout.TotalMinutes:0} 分钟没有结束");
        }

        var output = string.Empty;
        try
        {
            output = await stdout.ConfigureAwait(false)
                     + Environment.NewLine
                     + await stderr.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 输出读取失败不影响退出码判定。
        }

        return (process.ExitCode, output);
    }

    // ------------------------------------------------------------------ 安装器解析

    private static ZipArchive OpenInstaller(string installerPath)
    {
        try
        {
            return ZipFile.OpenRead(installerPath);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"安装器不是有效的 jar：{installerPath}", ex);
        }
    }

    private static ForgeInstallProfile ReadProfile(ZipArchive archive)
    {
        var entry = archive.GetEntry("install_profile.json")
                    ?? throw new InvalidDataException("安装器里没有 install_profile.json");

        using var stream = entry.Open();
        var profile = JsonUtil.Deserialize<ForgeInstallProfile>(ReadAllText(stream))
                      ?? throw new InvalidDataException("install_profile.json 无法解析");

        return profile;
    }

    private static GameVersionJson ReadVersionJson(ZipArchive archive, ForgeInstallProfile profile)
    {
        // 老格式把版本 JSON 直接写在 install_profile 里。
        if (profile.VersionInfo is not null)
        {
            return profile.VersionInfo;
        }

        var entryName = NormalizeEntryName(profile.Json ?? "version.json");
        var entry = archive.GetEntry(entryName)
                    ?? throw new InvalidDataException($"安装器里没有版本 JSON：{entryName}");

        using var stream = entry.Open();
        return JsonUtil.Deserialize<GameVersionJson>(ReadAllText(stream))
               ?? throw new InvalidDataException($"安装器里的版本 JSON 无法解析：{entryName}");
    }

    private int ExtractEmbeddedLibraries(ZipArchive archive, string gameRoot, ForgeInstallProfile profile)
    {
        var extracted = 0;

        foreach (var library in profile.Libraries ?? new List<Library>())
        {
            if (string.IsNullOrWhiteSpace(library.Name))
            {
                continue;
            }

            var artifactPath = library.GetDownloadInfo()?.Path;
            if (string.IsNullOrWhiteSpace(artifactPath))
            {
                artifactPath = ArtifactPath(library.Name!);
            }

            extracted += ExtractEmbeddedArtifact(archive, gameRoot, artifactPath!);
        }

        if (!string.IsNullOrWhiteSpace(profile.Path))
        {
            extracted += ExtractEmbeddedArtifact(archive, gameRoot, ArtifactPath(profile.Path!));
        }

        // 老式安装器：universal jar 单独放在 install.filePath。
        if (profile.Install is { FilePath.Length: > 0 } install && !string.IsNullOrWhiteSpace(install.Path))
        {
            var entry = archive.GetEntry(NormalizeEntryName(install.FilePath!));
            var target = GameInstaller.LibraryFilePath(gameRoot, ArtifactPath(install.Path!));
            if (entry is not null)
            {
                PathUtil.EnsureParentDirectory(target);
                entry.ExtractToFile(target, overwrite: true);
                extracted++;
            }
        }

        return extracted;
    }

    private int ExtractEmbeddedArtifact(ZipArchive archive, string gameRoot, string artifactPath)
    {
        var entry = archive.GetEntry(LibrariesPrefix + artifactPath);
        if (entry is null)
        {
            return 0;
        }

        var target = GameInstaller.LibraryFilePath(gameRoot, artifactPath);
        PathUtil.EnsureParentDirectory(target);
        entry.ExtractToFile(target, overwrite: true);
        _log.Trace($"已展开安装器内嵌制品：{artifactPath}");
        return 1;
    }

    // ------------------------------------------------------------------ 变量

    private Dictionary<string, string> BuildVariables(
        ZipArchive archive,
        ForgeInstallProfile profile,
        string gameRoot,
        string installerPath,
        string gameVersion,
        string minecraftJar,
        string tempDirectory)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, entry) in profile.Data ?? new Dictionary<string, ForgeDataEntry>())
        {
            var value = entry.Client ?? entry.Server;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            variables[key] = ParseDataValue(
                value,
                coordinate => GameInstaller.LibraryFilePath(gameRoot, ArtifactPath(coordinate)),
                path => ExtractDataFile(archive, tempDirectory, key, path));
        }

        variables["SIDE"] = "client";
        variables["MINECRAFT_JAR"] = Path.GetFullPath(minecraftJar);
        variables["MINECRAFT_VERSION"] = profile.ResolveMinecraftVersion() ?? gameVersion;
        variables["ROOT"] = Path.GetFullPath(gameRoot);
        variables["INSTALLER"] = Path.GetFullPath(installerPath);
        variables["LIBRARY_DIR"] = Path.GetFullPath(GameInstaller.LibrariesDirectory(gameRoot));

        return variables;
    }

    private string ExtractDataFile(ZipArchive archive, string tempDirectory, string key, string entryPath)
    {
        var entry = archive.GetEntry(NormalizeEntryName(entryPath))
                    ?? throw new InvalidDataException($"安装器里缺少数据文件：{entryPath}");

        var target = Path.Combine(tempDirectory, Sanitize($"{key}-{Path.GetFileName(entryPath)}"));
        entry.ExtractToFile(target, overwrite: true);
        return target;
    }

    private async Task<string> EnsureClientJarAsync(
        string gameRoot,
        string gameVersion,
        GameVersionJson baseVersion,
        string? clientJarPath,
        CancellationToken ct)
    {
        var download = baseVersion.Downloads?.Client;
        var checker = FileChecker.Of(download?.Sha1, download?.Size ?? -1);

        var target = string.IsNullOrWhiteSpace(clientJarPath)
            ? Path.Combine(gameRoot, ".mclauncher", TempDirectoryName,
                Sanitize($"minecraft-{gameVersion}.jar"))
            : clientJarPath!;

        PathUtil.EnsureParentDirectory(target);

        if (checker.Check(target))
        {
            return target;
        }

        var installedJar = VersionRepository.VersionJarPath(gameRoot, gameVersion);
        if (File.Exists(installedJar) && checker.Check(installedJar))
        {
            File.Copy(installedJar, target, overwrite: true);
            _log.Info($"已从原版版本目录复制客户端 jar：{installedJar}");
            return target;
        }

        if (download?.Url is not { Length: > 0 } url)
        {
            if (File.Exists(installedJar))
            {
                File.Copy(installedJar, target, overwrite: true);
                return target;
            }

            throw new InvalidDataException(
                $"{gameVersion} 缺少客户端 jar，也没有可用于下载的路径");
        }

        var batch = new DownloadBatch(_downloader, _log);
        var result = await batch
            .RunAsync(
                new[]
                {
                    new DownloadRequest
                    {
                        Name = $"{gameVersion} 客户端 jar",
                        Candidates = _resolver.Resolve(url, DownloadUrlKind.Client, gameVersion),
                        TargetPath = target,
                        Checker = checker,
                    },
                },
                1,
                null,
                ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            var error = result.Failures.Count > 0 ? result.Failures[0].Error : "没有可用的下载源";
            throw new InvalidOperationException(
                $"下载 {gameVersion} 客户端 jar 失败：{error}");
        }

        return target;
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>
    /// 把 <c>group:artifact:version[:classifier][@ext]</c> 解析成 maven 相对路径。
    /// <c>@ext</c> 不能忽略：Forge 的变量表里有 <c>[...:mappings@txt]</c> 这类非 jar 制品。
    /// </summary>
    public static string ArtifactPath(string coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate))
        {
            throw new ArgumentException("Maven 坐标为空", nameof(coordinate));
        }

        var text = coordinate.Trim();
        var extension = "jar";

        var at = text.IndexOf('@');
        if (at >= 0)
        {
            extension = text[(at + 1)..];
            text = text[..at];
        }

        var parts = text.Split(':');
        if (parts.Length < 3)
        {
            throw new ArgumentException($"Maven 坐标格式不正确：{coordinate}", nameof(coordinate));
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? "-" + parts[3] : string.Empty;

        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
    }

    /// <summary>
    /// 展开 Forge 的 <c>{变量}</c> / <c>'字面量'</c> 语法，支持 <c>\</c> 转义。
    /// </summary>
    public static string ReplaceTokens(string value, IReadOnlyDictionary<string, string> variables)
    {
        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];

            if (current == '\\')
            {
                if (i + 1 >= value.Length)
                {
                    throw new InvalidDataException($"安装配置里有非法转义：{value}");
                }

                builder.Append(value[++i]);
                continue;
            }

            if (current is not ('{' or '\''))
            {
                builder.Append(current);
                continue;
            }

            var close = current == '{' ? '}' : '\'';
            var key = new StringBuilder();
            var closed = false;

            for (var j = i + 1; j < value.Length; j++)
            {
                var inside = value[j];
                if (inside == '\\')
                {
                    if (j + 1 >= value.Length)
                    {
                        throw new InvalidDataException($"安装配置里有非法转义：{value}");
                    }

                    key.Append(value[++j]);
                    continue;
                }

                if (inside == close)
                {
                    i = j;
                    closed = true;
                    break;
                }

                key.Append(inside);
            }

            if (!closed)
            {
                throw new InvalidDataException($"安装配置里有未闭合的占位符：{value}");
            }

            if (current == '\'')
            {
                builder.Append(key);
                continue;
            }

            if (!variables.TryGetValue(key.ToString(), out var replacement))
            {
                throw new InvalidDataException($"安装配置引用了未定义的变量：{key}（{value}）");
            }

            builder.Append(replacement);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 解析处理器参数 / 输出声明：
    /// <c>{变量}</c>、<c>'字面量'</c>、<c>[Maven 坐标]</c>，其余按普通字符串展开。
    /// </summary>
    public static string ParseLiteral(
        string literal,
        IReadOnlyDictionary<string, string> variables,
        Func<string, string> artifactResolver)
    {
        if (IsWrapped(literal, '{', '}'))
        {
            var key = literal[1..^1];
            return variables.TryGetValue(key, out var value)
                ? value
                : throw new InvalidDataException($"安装配置引用了未定义的变量：{key}");
        }

        if (IsWrapped(literal, '\'', '\''))
        {
            return literal[1..^1];
        }

        if (IsWrapped(literal, '[', ']'))
        {
            return artifactResolver(literal[1..^1]);
        }

        return ReplaceTokens(literal, variables);
    }

    /// <summary>
    /// 解析变量表的值：<c>[Maven 坐标]</c>、<c>'字面量'</c>，
    /// 其余一律当作安装器 jar 里的文件条目，解压到临时目录后返回路径。
    /// </summary>
    public static string ParseDataValue(
        string value,
        Func<string, string> artifactResolver,
        Func<string, string> fileExtractor)
    {
        if (IsWrapped(value, '\'', '\''))
        {
            return value[1..^1];
        }

        if (IsWrapped(value, '[', ']'))
        {
            return artifactResolver(value[1..^1]);
        }

        if (IsWrapped(value, '{', '}'))
        {
            throw new InvalidDataException($"变量表不能引用另一个变量：{value}");
        }

        return fileExtractor(value);
    }

    private static bool IsWrapped(string value, char open, char close) =>
        value.Length >= 2 && value[0] == open && value[^1] == close;

    /// <summary>把变量表里所有绝对路径的父目录建好，处理器写输出时不会因为目录缺失失败。</summary>
    private static void EnsureVariableParents(IReadOnlyDictionary<string, string> variables)
    {
        foreach (var value in variables.Values)
        {
            if (!Path.IsPathRooted(value))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(value);
            if (!string.IsNullOrEmpty(parent))
            {
                PathUtil.EnsureDirectory(parent!);
            }
        }
    }

    private static string NormalizeEntryName(string path)
    {
        var name = path.Replace('\\', '/');
        return name.StartsWith('/') ? name[1..] : name;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    private static string ReadAllText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string Tail(string text, int lines = 8)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "（没有输出）";
        }

        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" / ", all.TakeLast(lines).Select(line => line.Trim()));
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"清理安装临时目录失败：{path}", ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删除失败时下载阶段会覆盖写入，不影响继续。
        }
    }
}
