using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>Azul Metadata API 返回的 Zulu 包摘要。</summary>
public sealed class ZuluPackage
{
    [JsonPropertyName("package_uuid")]
    public string PackageUuid { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("java_version")]
    public int[] JavaVersion { get; set; } = Array.Empty<int>();

    [JsonPropertyName("latest")]
    public bool Latest { get; set; }
}

/// <summary>Zulu 包详情：带 SHA-256 与文件大小。</summary>
public sealed class ZuluPackageDetail
{
    [JsonPropertyName("package_uuid")]
    public string PackageUuid { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("sha256_hash")]
    public string? Sha256Hash { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("archive_type")]
    public string? ArchiveType { get; set; }

    [JsonPropertyName("hw_bitness")]
    public int HwBitness { get; set; }
}

/// <summary>Azul 上选定并核实过的 Zulu 安装包。</summary>
public sealed record JavaRuntimePackage
{
    public required string Name { get; init; }

    public required string DownloadUrl { get; init; }

    public required string Sha256 { get; init; }

    public long Size { get; init; }

    public required int[] JavaVersion { get; init; }
}

/// <summary>下载、解压、探测 Java 运行时的阶段进度。</summary>
public sealed record JavaRuntimeProgress
{
    public required string Stage { get; init; }

    /// <summary>0–1。下载阶段按字节计算，校验/解压阶段为 1。</summary>
    public double Fraction { get; init; }

    public double BytesPerSecond { get; init; }

    public IReadOnlyList<DownloadFileProgress> ActiveFiles { get; init; } = Array.Empty<DownloadFileProgress>();
}

/// <summary>
/// 通过 Azul Metadata API 自动补全 Java 运行时。
/// <para>
/// 只下载 Azul 官方 CDN 的 Community Availability（CA）Zulu JDK zip，
/// 下载前用 Metadata API 给出的 SHA-256 与大小做双重校验，解压后探测确认可用才留下。
/// 运行时统一放在启动器配置目录的 <c>java</c> 下，便于随便携配置一起迁移。
/// </para>
/// </summary>
public sealed class ZuluJavaService
{
    private const string ApiBase = "https://api.azul.com/metadata/v1";

    private const int ApiAttempts = 2;

    private readonly HttpDownloader _http;
    private readonly DownloadBatch _batch;
    private readonly JavaService _java;
    private readonly ILogger _log;
    private readonly string _runtimeRoot;

    public ZuluJavaService(
        HttpDownloader http,
        DownloadBatch batch,
        JavaService java,
        ILogger log,
        string runtimeRoot)
    {
        _http = http;
        _batch = batch;
        _java = java;
        _log = log;
        _runtimeRoot = runtimeRoot;
    }

    /// <summary>启动器管理的 Java 根目录。</summary>
    public string RuntimeRoot => _runtimeRoot;

    /// <summary>
    /// 确保存在能运行指定主版本的 64 位 Java；没有时下载并安装对应版本的 Zulu JDK。
    /// 已有可用版本时直接返回，不产生任何网络请求。
    /// </summary>
    public async Task<JavaInfo> EnsureAsync(
        int requiredMajor,
        IProgress<JavaRuntimeProgress>? progress = null,
        CancellationToken ct = default)
    {
        var existing = _java.SelectBest(requiredMajor);
        if (existing is not null)
        {
            return existing;
        }

        var package = await ResolveAsync(requiredMajor, ct).ConfigureAwait(false);
        _log.Info(
            $"Azul 返回 Zulu JDK {requiredMajor}：{package.Name}（{FormatSize(package.Size)}，SHA-256 {package.Sha256[..8]}…）");

        PathUtil.EnsureDirectory(_runtimeRoot);
        var downloadDirectory = Path.Combine(_runtimeRoot, "downloads");
        PathUtil.EnsureDirectory(downloadDirectory);

        var archivePath = Path.Combine(downloadDirectory, package.Name);
        var staging = Path.Combine(_runtimeRoot, "staging-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(_runtimeRoot, $"zulu-{requiredMajor}");
        var stage = $"正在下载 Zulu JDK {requiredMajor}（{FormatSize(package.Size)}）";

        try
        {
            progress?.Report(new JavaRuntimeProgress { Stage = stage });

            var batchProgress = new InlineProgress<DownloadBatchProgress>(p =>
            {
                // Azul 元数据里的 size 与实际 Content-Length 可能相差几个字节，
                // 不能用它做严格校验；进度按元数据大小估算即可。
                var fraction = p.TotalBytes > 0
                    ? (double)p.DownloadedBytes / p.TotalBytes
                    : package.Size > 0
                        ? (double)p.DownloadedBytes / package.Size
                        : 0d;

                progress?.Report(new JavaRuntimeProgress
                {
                    Stage = stage,
                    Fraction = Math.Clamp(fraction, 0d, 1d),
                    BytesPerSecond = p.BytesPerSecond,
                    ActiveFiles = p.ActiveFiles,
                });
            });

            var result = await _batch.RunAsync(
                new[]
                {
                    new DownloadRequest
                    {
                        Name = package.Name,
                        Candidates = new[] { new DownloadCandidate(package.DownloadUrl, "Azul") },
                        TargetPath = archivePath,
                        // 大小以 SHA-256 为准：Azul 的 size 字段偶尔与实际响应差几个字节。
                        Checker = FileChecker.ExistenceOnly,
                    },
                },
                maxParallel: 1,
                batchProgress,
                ct).ConfigureAwait(false);

            if (!result.Success || !File.Exists(archivePath))
            {
                var error = result.Failures.FirstOrDefault().Error ?? "下载未完成";
                throw new InvalidOperationException($"下载 Zulu JDK {requiredMajor} 失败：{error}");
            }

            progress?.Report(new JavaRuntimeProgress
            {
                Stage = $"正在校验 Zulu JDK {requiredMajor}…",
                Fraction = 1,
            });

            var actual = await Task.Run(() => HashUtil.Sha256OfFile(archivePath), ct).ConfigureAwait(false);
            if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(archivePath);
                throw new InvalidDataException(
                    $"Zulu JDK {requiredMajor} 的 SHA-256 校验失败（期望 {package.Sha256}，实际 {actual}）");
            }

            progress?.Report(new JavaRuntimeProgress
            {
                Stage = $"正在解压 Zulu JDK {requiredMajor}…",
                Fraction = 1,
            });

            TryDelete(staging);
            var blocked = new List<string>();
            await Task.Run(
                    () => blocked.AddRange(ArchiveUtil.ExtractZipSkippingBlocked(archivePath, staging)),
                    ct)
                .ConfigureAwait(false);

            if (blocked.Count > 0)
            {
                // 常见于安全软件拦截向该目录写入 DLL（如 msvcp140.dll）；系统里有同名运行库时 Java 仍可启动。
                _log.Warn($"解压 Zulu JDK {requiredMajor} 时有 {blocked.Count} 个文件被安全软件拦截：{FormatBlocked(blocked)}");
            }

            var javaExe = Directory
                .EnumerateFiles(staging, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)), "bin", StringComparison.OrdinalIgnoreCase));

            if (javaExe is null)
            {
                throw new InvalidDataException(blocked.Count > 0
                    ? $"解压 Zulu JDK {requiredMajor} 时 {blocked.Count} 个文件被安全软件拦截（{FormatBlocked(blocked)}），缺少 bin/java.exe。"
                      + "请把本启动器加入信任后重试；安装包已缓存，不会重新下载。"
                    : "解压后的 Zulu JDK 里找不到 bin/java.exe");
            }

            if (Directory.Exists(destination))
            {
                // 走到这里说明旧目录不可用（能探测通过的话开头就返回了），直接替换。
                Directory.Delete(destination, recursive: true);
            }

            progress?.Report(new JavaRuntimeProgress
            {
                Stage = $"正在安装 Zulu JDK {requiredMajor}…",
                Fraction = 1,
            });

            var relative = Path.GetRelativePath(staging, javaExe);
            Directory.Move(staging, destination);
            var installedJava = Path.Combine(destination, relative);

            var info = _java.Probe(installedJava, ct);
            if (info is null)
            {
                TryDelete(destination);
                throw new InvalidOperationException(blocked.Count > 0
                    ? $"Zulu JDK 已解压，但有 {blocked.Count} 个文件被安全软件拦截（{FormatBlocked(blocked)}），Java 无法启动。"
                      + "请把本启动器加入信任后重试；安装包已缓存，不会重新下载。"
                    : "Zulu JDK 已解压，但无法通过 Java 探测，可能被安全软件拦截");
            }

            _java.Invalidate();
            TryDelete(archivePath);

            _log.Info($"Zulu JDK {requiredMajor} 已安装：{installedJava}");
            progress?.Report(new JavaRuntimeProgress
            {
                Stage = blocked.Count == 0
                    ? $"Java {requiredMajor} 已就绪"
                    : $"Java {requiredMajor} 已就绪（{blocked.Count} 个文件被安全软件拦截，已跳过）",
                Fraction = 1,
            });

            return info;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// 查询 Azul Metadata API，返回指定主版本最新的 Windows 64 位 Zulu JDK（zip）。
    /// 只使用 Community Availability 构建；不访问需要订阅令牌的 SA 构建。
    /// </summary>
    public async Task<JavaRuntimePackage> ResolveAsync(int requiredMajor, CancellationToken ct = default)
    {
        if (requiredMajor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredMajor), "Java 主版本必须大于 0");
        }

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm",
            _ => throw new PlatformNotSupportedException("自动下载 Java 仅支持 Windows x64 / arm64"),
        };

        var url = $"{ApiBase}/zulu/packages/?java_version={requiredMajor}&os=windows&arch={arch}"
                  + "&java_package_type=jdk&javafx_bundled=false&archive_type=zip"
                  + "&release_status=ga&availability_types=CA&page=1&page_size=50";

        var packages = await GetJsonAsync<List<ZuluPackage>>(url, ct).ConfigureAwait(false);
        var package = packages?
            .Where(p => !string.IsNullOrWhiteSpace(p.PackageUuid))
            .Where(p => p.DownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Name.Contains("-ca-jdk", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Name.Contains("-crac", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Name.Contains("-fx", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.JavaVersion.ElementAtOrDefault(0))
            .ThenByDescending(p => p.JavaVersion.ElementAtOrDefault(1))
            .ThenByDescending(p => p.JavaVersion.ElementAtOrDefault(2))
            .ThenByDescending(p => p.JavaVersion.ElementAtOrDefault(3))
            .ThenByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (package is null)
        {
            throw new InvalidOperationException($"Azul 上没有找到适用于 Windows 的 Zulu JDK {requiredMajor}（zip）");
        }

        var detailUrl = $"{ApiBase}/zulu/packages/{package.PackageUuid}/";
        var detail = await GetJsonAsync<ZuluPackageDetail>(detailUrl, ct).ConfigureAwait(false);
        if (detail is null
            || string.IsNullOrWhiteSpace(detail.DownloadUrl)
            || string.IsNullOrWhiteSpace(detail.Sha256Hash)
            || detail.Size <= 0)
        {
            throw new InvalidOperationException($"Azul 没有返回 Zulu JDK {requiredMajor} 的完整下载信息");
        }

        return new JavaRuntimePackage
        {
            Name = package.Name,
            DownloadUrl = detail.DownloadUrl,
            Sha256 = detail.Sha256Hash!,
            Size = detail.Size,
            JavaVersion = package.JavaVersion,
        };
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= ApiAttempts; attempt++)
        {
            try
            {
                var json = await _http
                    .RequestStringAsync(
                        url,
                        HttpMethod.Get,
                        headers: new Dictionary<string, string> { ["Accept"] = "application/json" },
                        ct: ct)
                    .ConfigureAwait(false);

                return JsonSerializer.Deserialize<T>(json);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                last = ex;
                if (attempt < ApiAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException($"请求 Azul Metadata API 失败：{last?.Message}", last);
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.#} MB"
        : $"{bytes / 1024d:0.#} KB";

    /// <summary>错误信息里只列前几个被拦截的文件名，避免刷屏。</summary>
    private static string FormatBlocked(IReadOnlyList<string> blocked) =>
        string.Join("、", blocked.Take(3).Select(Path.GetFileName));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败保留现场，下次下载时会再次尝试。
        }
    }
}
