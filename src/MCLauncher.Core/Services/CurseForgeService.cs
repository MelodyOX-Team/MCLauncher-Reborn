using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// CurseForge 检索、文件查询与指纹匹配。
/// <para>
/// classId 实测对应关系：Mod=6、资源包=12、世界=17、光影包=6552。
/// 所有请求都带 <c>x-api-key</c>，因此只能访问 CurseForge 官方域名，绝不回退到第三方镜像
/// （否则 Key 会泄露给镜像）；只有下载文件本身允许走镜像。
/// </para>
/// </summary>
public sealed class CurseForgeService
{
    private const string BaseUrl = "https://api.curseforge.com";
    private const int MinecraftGameId = 432;

    private static readonly HashSet<string> KnownLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Forge", "NeoForge", "Fabric", "Quilt", "LiteLoader", "Rift",
    };

    private readonly HttpDownloader _downloader;
    private readonly DownloadSourceResolver _resolver;
    private readonly ILogger _log;
    private readonly string? _apiKey;

    public CurseForgeService(
        HttpDownloader downloader,
        DownloadSourceResolver resolver,
        string? apiKey,
        ILogger log)
    {
        _downloader = downloader;
        _resolver = resolver;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _log = log;
    }

    public bool IsConfigured => _apiKey is not null;

    /// <summary>资源类别对应的 CurseForge classId；不支持时为 -1。</summary>
    public static int ClassIdFor(ResourceKind kind) => kind switch
    {
        ResourceKind.Mod => 6,
        ResourceKind.ResourcePack => 12,
        ResourceKind.World => 17,
        ResourceKind.ShaderPack => 6552,
        ResourceKind.Modpack => 4471,
        _ => -1,
    };

    /// <summary>该资源类别是否支持从 CurseForge 下载。</summary>
    public static bool SupportsDownload(ResourceKind kind) => ClassIdFor(kind) > 0;

    /// <summary>该资源类别是否支持检查更新（存档是目录，无法用文件指纹匹配）。</summary>
    public static bool SupportsUpdate(ResourceKind kind) =>
        kind is ResourceKind.Mod or ResourceKind.ResourcePack or ResourceKind.ShaderPack;

    /// <summary>按名称检索项目。</summary>
    public async Task<CurseForgeSearchPage> SearchAsync(
        ResourceKind kind,
        string? search,
        string? gameVersion,
        CurseForgeSort sort,
        int pageIndex,
        int pageSize,
        CancellationToken ct)
    {
        var classId = ClassIdFor(kind);
        if (classId < 0)
        {
            throw new InvalidOperationException($"{ResourceRepository.DisplayName(kind)}不支持从 CurseForge 下载。");
        }

        var parameters = new List<string>
        {
            $"gameId={MinecraftGameId}",
            $"classId={classId}",
            $"index={Math.Max(pageIndex, 0) * pageSize}",
            $"pageSize={pageSize}",
            $"sortField={(int)sort}",
            $"sortOrder={(sort == CurseForgeSort.Name ? "asc" : "desc")}",
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            parameters.Add("searchFilter=" + Uri.EscapeDataString(search.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            parameters.Add("gameVersion=" + Uri.EscapeDataString(gameVersion.Trim()));
        }

        var json = await SendAsync(
            $"{BaseUrl}/v1/mods/search?{string.Join("&", parameters)}",
            HttpMethod.Get,
            null,
            ct).ConfigureAwait(false);

        var response = JsonUtil.Deserialize<SearchResponse>(json);
        var projects = response?.Data is null
            ? new List<CurseForgeProject>()
            : response.Data.Select(ToProject).ToList();

        return new CurseForgeSearchPage
        {
            Projects = projects,
            TotalCount = response?.Pagination?.TotalCount ?? projects.Count,
            PageIndex = pageIndex,
            PageSize = pageSize,
        };
    }

    /// <summary>获取项目适用于某个游戏版本的全部文件（按发布时间倒序）。</summary>
    public async Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(
        int projectId,
        string? gameVersion,
        CancellationToken ct)
    {
        var files = await GetFilesPageAsync(projectId, 0, 50, gameVersion, ct).ConfigureAwait(false);

        return files
            .Select(ToFile)
            .OrderByDescending(file => file.FileDate)
            .ToList();
    }

    /// <summary>
    /// 翻页拉取项目的全部文件（接口单页上限 50，默认最多 150 条）。
    /// 详情页要按 Minecraft 版本分组，必须拿到足够多的历史文件。
    /// </summary>
    public async Task<IReadOnlyList<CurseForgeFile>> GetAllFilesAsync(
        int projectId,
        CancellationToken ct,
        int maxFiles = 150)
    {
        const int pageSize = 50;
        var result = new List<CurseForgeFile>();

        for (var index = 0; index < maxFiles; index += pageSize)
        {
            var batch = await GetFilesPageAsync(projectId, index, pageSize, null, ct).ConfigureAwait(false);
            result.AddRange(batch.Select(ToFile));

            if (batch.Count < pageSize)
            {
                break;
            }
        }

        return result;
    }

    private async Task<List<FileDto>> GetFilesPageAsync(
        int projectId,
        int index,
        int pageSize,
        string? gameVersion,
        CancellationToken ct)
    {
        var url = $"{BaseUrl}/v1/mods/{projectId}/files?index={index}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            url += "&gameVersion=" + Uri.EscapeDataString(gameVersion.Trim());
        }

        var json = await SendAsync(url, HttpMethod.Get, null, ct).ConfigureAwait(false);
        var response = JsonUtil.Deserialize<FilesResponse>(json);
        return response?.Data ?? new List<FileDto>();
    }

    /// <summary>获取项目详情（详情页头部使用）。</summary>
    public async Task<CurseForgeProject> GetProjectAsync(int projectId, CancellationToken ct)
    {
        var json = await SendAsync(
                $"{BaseUrl}/v1/mods/{projectId}",
                HttpMethod.Get,
                null,
                ct)
            .ConfigureAwait(false);

        var dto = JsonUtil.Deserialize<SingleProjectResponse>(json)?.Data
                  ?? throw new InvalidOperationException("CurseForge 返回的项目信息为空。");

        return ToProject(dto);
    }

    /// <summary>批量获取项目摘要（一次解析一个版本的全部前置内容）。</summary>
    public async Task<IReadOnlyList<CurseForgeProject>> GetProjectsAsync(
        IReadOnlyCollection<int> projectIds,
        CancellationToken ct)
    {
        if (projectIds.Count == 0)
        {
            return Array.Empty<CurseForgeProject>();
        }

        var body = JsonSerializer.Serialize(
            new { modIds = projectIds.ToArray() },
            JsonUtil.Options);

        var json = await SendAsync($"{BaseUrl}/v1/mods", HttpMethod.Post, body, ct)
            .ConfigureAwait(false);

        var response = JsonUtil.Deserialize<ProjectsResponse>(json);
        return response?.Data is null
            ? Array.Empty<CurseForgeProject>()
            : response.Data.Select(ToProject).ToList();
    }

    /// <summary>
    /// 获取项目全部版本的下载信息（不做游戏版本 / 加载器过滤，交给界面按实例推荐分组）。
    /// </summary>
    public async Task<IReadOnlyList<ResourceVersionInfo>> GetVersionInfosAsync(
        int projectId,
        ResourceKind kind,
        CancellationToken ct)
    {
        var files = await GetAllFilesAsync(projectId, ct).ConfigureAwait(false);
        var result = new List<ResourceVersionInfo>(files.Count);

        foreach (var file in files)
        {
            if (!MatchesExtension(kind, file.FileName)
                || string.IsNullOrWhiteSpace(file.DownloadUrl))
            {
                continue;
            }

            result.Add(new ResourceVersionInfo
            {
                Source = ResourceSource.CurseForge,
                Id = file.Id.ToString(CultureInfo.InvariantCulture),
                Name = string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName,
                VersionType = file.ReleaseType switch
                {
                    2 => "beta",
                    3 => "alpha",
                    _ => "release",
                },
                PublishedAt = file.FileDate,
                GameVersions = file.GameVersions,
                Loaders = file.GameVersions
                    .Where(ResourceVersionCatalog.IsLoaderName)
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                File = ResourceFile.FromCurseForge(file),
                Dependencies = file.Dependencies
                    .Where(dependency => dependency.ModId > 0 && dependency.RelationType is 2 or 3)
                    .GroupBy(dependency => dependency.ModId)
                    .Select(group => new ResourceDependency
                    {
                        Source = ResourceSource.CurseForge,
                        CurseForgeProjectId = group.Key,
                        Kind = group.Any(dependency => dependency.RelationType == 3)
                            ? ResourceDependencyKind.Required
                            : ResourceDependencyKind.Optional,
                    })
                    .ToList(),
            });
        }

        return result;
    }

    /// <summary>挑出最合适的文件：优先同类加载器、优先正式版，再按发布时间取最新。</summary>
    public static CurseForgeFile? PickBestFile(
        IReadOnlyList<CurseForgeFile> files,
        ResourceKind kind,
        string? loaderName)
    {
        if (files.Count == 0)
        {
            return null;
        }

        var pool = files.Where(file => MatchesExtension(kind, file.FileName)).ToList();
        if (pool.Count == 0)
        {
            return null;
        }

        if (kind == ResourceKind.Mod)
        {
            var family = NormalizeLoader(loaderName);
            if (family is not null)
            {
                var withLoader = pool
                    .Where(file => file.GameVersions.Any(version =>
                        string.Equals(version, family, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                pool = withLoader.Count > 0
                    ? withLoader
                    : pool.Where(file => !file.GameVersions.Any(version => KnownLoaders.Contains(version))).ToList();

                if (pool.Count == 0)
                {
                    return null;
                }
            }
        }

        var ordered = pool.OrderByDescending(file => file.FileDate).ToList();
        return ordered.FirstOrDefault(file => file.IsRelease)
               ?? ordered.FirstOrDefault(file => file.ReleaseType == 2)
               ?? ordered.FirstOrDefault();
    }

    /// <summary>查询并挑选项目当前最合适的文件。</summary>
    public async Task<CurseForgeFile?> PickBestFileAsync(
        int projectId,
        ResourceKind kind,
        string? gameVersion,
        string? loaderName,
        CancellationToken ct)
    {
        var files = await GetFilesAsync(projectId, gameVersion, ct).ConfigureAwait(false);
        return PickBestFile(files, kind, loaderName);
    }

    /// <summary>批量指纹匹配，返回与本地文件对应的项目/文件。</summary>
    public async Task<IReadOnlyList<CurseForgeFingerprintMatch>> MatchFingerprintsAsync(
        IReadOnlyList<uint> fingerprints,
        CancellationToken ct)
    {
        if (fingerprints.Count == 0)
        {
            return Array.Empty<CurseForgeFingerprintMatch>();
        }

        var body = JsonSerializer.Serialize(new { fingerprints }, JsonUtil.Options);
        var json = await SendAsync(
            $"{BaseUrl}/v1/fingerprints?gameId={MinecraftGameId}",
            HttpMethod.Post,
            body,
            ct).ConfigureAwait(false);

        var response = JsonUtil.Deserialize<FingerprintResponse>(json);
        var matches = response?.Data?.ExactMatches;
        if (matches is null)
        {
            return Array.Empty<CurseForgeFingerprintMatch>();
        }

        // exactFingerprints 与 exactMatches 一一对应；数量对不上时按发送顺序兜底。
        var matchedFingerprints = response!.Data!.ExactFingerprints ?? new List<uint>();

        return matches
            .Select((match, index) => (Match: match, Index: index))
            .Where(pair => pair.Match.Id > 0 && pair.Match.File is not null)
            .Select(pair => new CurseForgeFingerprintMatch
            {
                Fingerprint = pair.Index < matchedFingerprints.Count ? matchedFingerprints[pair.Index] : 0,
                ProjectId = pair.Match.Id,
                File = ToFile(pair.Match.File!),
                LatestFiles = pair.Match.LatestFiles?.Select(ToFile).ToList() ?? new List<CurseForgeFile>(),
            })
            .ToList();
    }

    // ---------------------------------------------------------------- 请求

    private async Task<string> SendAsync(string url, HttpMethod method, string? body, CancellationToken ct)
    {
        if (_apiKey is null)
        {
            throw new InvalidOperationException("CurseForge 服务不可用，无法使用资源下载与更新检查。");
        }

        var headers = new Dictionary<string, string>
        {
            ["x-api-key"] = _apiKey,
            ["Accept"] = "application/json",
        };

        Exception? lastError = null;

        foreach (var candidate in _resolver.Resolve(url, DownloadUrlKind.ModPlatform))
        {
            // Key 只能发给官方 API；候选里的镜像地址直接跳过。
            if (!IsOfficialApiUrl(candidate.Url))
            {
                continue;
            }

            try
            {
                return await _downloader.RequestStringAsync(
                        candidate.Url,
                        method,
                        body,
                        body is null ? null : "application/json",
                        headers,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpApiException ex)
            {
                lastError = ex;
                _log.Warn($"CurseForge 请求失败（{candidate.SourceName}）：HTTP {(int)ex.StatusCode} {url}", ex);

                // 4xx 是请求本身有问题（Key 无效、参数错误），换镜像也没有意义。
                if ((int)ex.StatusCode is >= 400 and < 500)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                lastError = ex;
                _log.Warn($"CurseForge 请求失败（{candidate.SourceName}）：{url}", ex);
            }
        }

        var hint = lastError is HttpApiException api && api.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "CurseForge 拒绝了请求（访问受限），请改用 Modrinth。"
            : "CurseForge 请求失败：" + (lastError?.Message ?? "网络不可用");

        throw new InvalidOperationException(hint, lastError);
    }

    /// <summary>该地址是否是 CurseForge 官方 API（唯一允许接收 x-api-key 的主机）。</summary>
    private static bool IsOfficialApiUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- 映射

    private static CurseForgeProject ToProject(ProjectDto dto) => new()
    {
        Id = dto.Id,
        ClassId = dto.ClassId,
        Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Slug ?? string.Empty : dto.Name!,
        Slug = dto.Slug ?? string.Empty,
        Summary = dto.Summary ?? string.Empty,
        DownloadCount = dto.DownloadCount,
        LogoUrl = dto.Logo?.ThumbnailUrl ?? dto.Logo?.Url,
        Author = dto.Authors?.FirstOrDefault()?.Name,
        Categories = dto.Categories?
                         .Select(category => category.Name)
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Select(name => name!)
                         .Take(4)
                         .ToList()
                     ?? new List<string>(),
        WebsiteUrl = string.IsNullOrWhiteSpace(dto.Links?.WebsiteUrl) ? null : dto.Links!.WebsiteUrl,
        LatestFiles = dto.LatestFilesIndexes?
                          .Select(index => new CurseForgeFileIndex
                          {
                              FileId = index.FileId,
                              FileName = index.FileName ?? string.Empty,
                              GameVersion = index.GameVersion ?? string.Empty,
                              ModLoader = index.ModLoader,
                              ReleaseType = index.ReleaseType,
                          })
                          .ToList()
                      ?? new List<CurseForgeFileIndex>(),
    };

    private static CurseForgeFile ToFile(FileDto dto) => new()
    {
        Id = dto.Id,
        ModId = dto.ModId,
        DisplayName = dto.DisplayName ?? dto.FileName ?? string.Empty,
        FileName = dto.FileName ?? string.Empty,
        DownloadUrl = string.IsNullOrWhiteSpace(dto.DownloadUrl) ? null : dto.DownloadUrl,
        FileLength = dto.FileLength,
        ReleaseType = dto.ReleaseType,
        FileDate = dto.FileDate,
        GameVersions = dto.GameVersions ?? new List<string>(),
        Dependencies = dto.Dependencies?
                           .Select(dependency => new CurseForgeFileDependency
                           {
                               ModId = dependency.ModId,
                               RelationType = dependency.RelationType,
                           })
                           .ToList()
                       ?? new List<CurseForgeFileDependency>(),
        Sha1 = dto.Hashes?
            .FirstOrDefault(hash => hash.Algo == 1)?
            .Value,
    };

    private static bool MatchesExtension(ResourceKind kind, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return kind switch
        {
            ResourceKind.Mod => extension.Equals(".jar", StringComparison.OrdinalIgnoreCase),
            ResourceKind.ResourcePack => extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                                         || extension.Equals(".mcpack", StringComparison.OrdinalIgnoreCase),
            ResourceKind.ShaderPack => extension.Equals(".zip", StringComparison.OrdinalIgnoreCase),
            ResourceKind.World => extension.Equals(".zip", StringComparison.OrdinalIgnoreCase),
            ResourceKind.Schematic => extension.Equals(".litematic", StringComparison.OrdinalIgnoreCase)
                                      || extension.Equals(".schematic", StringComparison.OrdinalIgnoreCase)
                                      || extension.Equals(".schem", StringComparison.OrdinalIgnoreCase)
                                      || extension.Equals(".nbt", StringComparison.OrdinalIgnoreCase),
            ResourceKind.Modpack => extension.Equals(".zip", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string? NormalizeLoader(string? loaderName)
    {
        if (string.IsNullOrWhiteSpace(loaderName))
        {
            return null;
        }

        var name = loaderName.Trim();
        return name switch
        {
            "Forge" => "Forge",
            "NeoForge" => "NeoForge",
            "Fabric" => "Fabric",
            "Quilt" => "Quilt",
            _ => null,
        };
    }

    // ---------------------------------------------------------------- DTO

    private sealed class SearchResponse
    {
        [JsonPropertyName("data")]
        public List<ProjectDto>? Data { get; set; }

        [JsonPropertyName("pagination")]
        public PaginationDto? Pagination { get; set; }
    }

    private sealed class FilesResponse
    {
        [JsonPropertyName("data")]
        public List<FileDto>? Data { get; set; }
    }

    private sealed class SingleProjectResponse
    {
        [JsonPropertyName("data")]
        public ProjectDto? Data { get; set; }
    }

    private sealed class ProjectsResponse
    {
        [JsonPropertyName("data")]
        public List<ProjectDto>? Data { get; set; }
    }

    private sealed class FingerprintResponse
    {
        [JsonPropertyName("data")]
        public FingerprintDataDto? Data { get; set; }
    }

    private sealed class FingerprintDataDto
    {
        [JsonPropertyName("exactMatches")]
        public List<FingerprintMatchDto>? ExactMatches { get; set; }

        [JsonPropertyName("exactFingerprints")]
        public List<uint>? ExactFingerprints { get; set; }
    }

    private sealed class FingerprintMatchDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("file")]
        public FileDto? File { get; set; }

        [JsonPropertyName("latestFiles")]
        public List<FileDto>? LatestFiles { get; set; }
    }

    private sealed class PaginationDto
    {
        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }
    }

    private sealed class ProjectDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("classId")]
        public int ClassId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("downloadCount")]
        public long DownloadCount { get; set; }

        [JsonPropertyName("logo")]
        public LogoDto? Logo { get; set; }

        [JsonPropertyName("authors")]
        public List<AuthorDto>? Authors { get; set; }

        [JsonPropertyName("categories")]
        public List<CategoryDto>? Categories { get; set; }

        [JsonPropertyName("latestFilesIndexes")]
        public List<FileIndexDto>? LatestFilesIndexes { get; set; }

        [JsonPropertyName("links")]
        public LinksDto? Links { get; set; }
    }

    private sealed class LinksDto
    {
        [JsonPropertyName("websiteUrl")]
        public string? WebsiteUrl { get; set; }
    }

    private sealed class LogoDto
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("thumbnailUrl")]
        public string? ThumbnailUrl { get; set; }
    }

    private sealed class AuthorDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class CategoryDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class FileIndexDto
    {
        [JsonPropertyName("fileId")]
        public int FileId { get; set; }

        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }

        [JsonPropertyName("gameVersion")]
        public string? GameVersion { get; set; }

        [JsonPropertyName("modLoader")]
        public int ModLoader { get; set; }

        [JsonPropertyName("releaseType")]
        public int ReleaseType { get; set; }
    }

    private sealed class FileDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("modId")]
        public int ModId { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("fileLength")]
        public long FileLength { get; set; } = -1;

        [JsonPropertyName("releaseType")]
        public int ReleaseType { get; set; }

        [JsonPropertyName("fileDate")]
        public DateTimeOffset FileDate { get; set; }

        [JsonPropertyName("gameVersions")]
        public List<string>? GameVersions { get; set; }

        [JsonPropertyName("hashes")]
        public List<HashDto>? Hashes { get; set; }

        [JsonPropertyName("dependencies")]
        public List<DependencyDto>? Dependencies { get; set; }
    }

    private sealed class DependencyDto
    {
        [JsonPropertyName("modId")]
        public int ModId { get; set; }

        [JsonPropertyName("relationType")]
        public int RelationType { get; set; }
    }

    private sealed class HashDto
    {
        [JsonPropertyName("algo")]
        public int Algo { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}
