using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// 六类本地资源的扫描、启用/禁用、导入与删除。
/// <para>
/// 启用/禁用的约定与主流启动器一致：**把文件名后缀改成 <c>.disabled</c>**。
/// 游戏只认扩展名，所以改后缀是唯一不需要动游戏代码的开关方式。
/// </para>
/// </summary>
public sealed class ResourceRepository
{
    /// <summary>禁用的后缀。</summary>
    public const string DisabledSuffix = ".disabled";

    private readonly ILogger _log;

    public ResourceRepository(ILogger log)
    {
        _log = log;
    }

    /// <summary>各类资源在实例目录下的子目录名。</summary>
    public static string DirectoryName(ResourceKind kind) => kind switch
    {
        ResourceKind.Mod => "mods",
        ResourceKind.ResourcePack => "resourcepacks",
        ResourceKind.ShaderPack => "shaderpacks",
        ResourceKind.World => "saves",
        ResourceKind.Schematic => "schematics",
        ResourceKind.Modpack => "modpacks",
        _ => "datapacks",
    };

    public static string DisplayName(ResourceKind kind) => kind switch
    {
        ResourceKind.Mod => "Mod",
        ResourceKind.ResourcePack => "资源包",
        ResourceKind.ShaderPack => "光影包",
        ResourceKind.World => "世界存档",
        ResourceKind.Schematic => "原理图",
        ResourceKind.Modpack => "整合包",
        _ => "数据包",
    };

    public static string GetDirectory(string instanceDirectory, ResourceKind kind) =>
        Path.Combine(instanceDirectory, DirectoryName(kind));

    /// <summary>该类资源是否是"目录型"（存档必须按目录处理）。</summary>
    public static bool IsDirectoryKind(ResourceKind kind) =>
        kind is ResourceKind.World or ResourceKind.ResourcePack or ResourceKind.ShaderPack or ResourceKind.Datapack;

    /// <summary>扫描某个实例下的某一类资源。</summary>
    public IReadOnlyList<LocalResource> Scan(string instanceDirectory, ResourceKind kind)
    {
        var directory = GetDirectory(instanceDirectory, kind);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<LocalResource>();
        }

        var result = new List<LocalResource>();

        // 单个文件在扫描期间被删除或占用时跳过并记录；整类扫描失败则让异常向上抛，
        // 由界面统一清空列表，避免把残缺结果当成完整列表展示。
        void Add(string itemPath, bool isDirectory)
        {
            var resource = Build(kind, itemPath, isDirectory);
            if (resource is not null)
            {
                result.Add(resource);
            }
        }

        if (IsDirectoryKind(kind))
        {
            // 目录型：既支持"目录形式"（存档、解压过的资源包），也支持"压缩包形式"。
            foreach (var dir in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || name.Equals("debug", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 禁用目录在下面的循环统一收集，这里跳过避免同一目录出现两次。
                if (name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Add(dir, isDirectory: true);
            }
        }

        var patterns = kind switch
        {
            ResourceKind.Mod => new[] { "*.jar" },
            // 原理图有多种格式：Litematica / WorldEdit / Sponge / 原版结构。
            ResourceKind.Schematic => new[] { "*.litematic", "*.schematic", "*.schem", "*.nbt" },
            _ => new[] { "*.zip" },
        };

        foreach (var pattern in patterns)
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                Add(file, isDirectory: false);
            }
        }

        if (kind == ResourceKind.ResourcePack || kind == ResourceKind.Datapack)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.mcpack"))
            {
                Add(file, isDirectory: false);
            }
        }

        // 禁用项：Mod 是 *.jar.disabled；目录型是目录名带后缀。
        foreach (var file in Directory.EnumerateFiles(directory, "*" + DisabledSuffix))
        {
            if (!file.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".zip" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".mcpack" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".litematic" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".schematic" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".schem" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".nbt" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Add(file, isDirectory: false);
        }

        foreach (var dir in Directory.EnumerateDirectories(directory, "*" + DisabledSuffix))
        {
            Add(dir, isDirectory: true);
        }

        result.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>扫描全部六类资源。</summary>
    public IReadOnlyList<LocalResource> ScanAll(string instanceDirectory)
    {
        var result = new List<LocalResource>();
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            result.AddRange(Scan(instanceDirectory, kind));
        }

        return result;
    }

    /// <summary>启用或禁用一个资源（通过改后缀）。目录型资源不支持禁用。</summary>
    public bool SetEnabled(LocalResource resource, bool enabled)
    {
        if (resource.Kind == ResourceKind.World)
        {
            return false;
        }

        var path = resource.FilePath;

        // 只有在路径确实带禁用后缀时才能截断，否则会把正常文件名尾部截掉甚至越界。
        if (enabled && !path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var target = enabled
            ? path[..^DisabledSuffix.Length]
            : path + DisabledSuffix;

        if (File.Exists(target) || Directory.Exists(target))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            resource.FilePath = target;
            resource.FileName = Path.GetFileName(target);
            resource.Enabled = enabled;
            _log.Info($"{(enabled ? "启用" : "禁用")} {DisplayName(resource.Kind)}：{resource.FileName}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"切换启用状态失败：{path}", ex);
            return false;
        }
    }

    /// <summary>删除一个资源（文件或整个目录）。</summary>
    public bool Delete(LocalResource resource)
    {
        try
        {
            if (Directory.Exists(resource.FilePath))
            {
                Directory.Delete(resource.FilePath, recursive: true);
            }
            else if (File.Exists(resource.FilePath))
            {
                File.Delete(resource.FilePath);
            }
            else
            {
                return false;
            }

            _log.Info($"已删除 {DisplayName(resource.Kind)}：{resource.FileName}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"删除资源失败：{resource.FilePath}", ex);
            return false;
        }
    }

    /// <summary>
    /// 把一个存档目录打成 zip 备份，文件名形如 <c>世界名_20261023_120000.zip</c>。
    /// 返回备份文件路径；失败返回 null。
    /// </summary>
    public string? BackupWorld(LocalResource world, string backupsDirectory)
    {
        if (world.Kind != ResourceKind.World || !Directory.Exists(world.FilePath))
        {
            return null;
        }

        try
        {
            PathUtil.EnsureDirectory(backupsDirectory);

            var baseName = SanitizeFileName(world.Title);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var target = Path.Combine(backupsDirectory, $"{baseName}_{stamp}.zip");

            var suffix = 2;
            while (File.Exists(target))
            {
                target = Path.Combine(backupsDirectory, $"{baseName}_{stamp}_{suffix++}.zip");
            }

            ZipFile.CreateFromDirectory(world.FilePath, target, CompressionLevel.Optimal, includeBaseDirectory: false);
            _log.Info($"已备份存档 {world.Title} → {target}");
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"备份存档失败：{world.FilePath}", ex);
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);

        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var cleaned = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "world" : cleaned;
    }

    /// <summary>把文件（或目录）导入到对应资源目录。</summary>
    public bool Import(string instanceDirectory, ResourceKind kind, string sourcePath, string? newFileName = null)
    {
        try
        {
            var directory = GetDirectory(instanceDirectory, kind);
            PathUtil.EnsureDirectory(directory);
            var name = newFileName ?? Path.GetFileName(sourcePath);

            if (Directory.Exists(sourcePath))
            {
                var target = Path.Combine(directory, name);
                CopyDirectory(sourcePath, target);
                _log.Info($"已导入 {DisplayName(kind)} 目录：{name}");
                return true;
            }

            if (!File.Exists(sourcePath))
            {
                return false;
            }

            File.Copy(sourcePath, Path.Combine(directory, name), overwrite: true);
            _log.Info($"已导入 {DisplayName(kind)}：{name}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"导入资源失败：{sourcePath}", ex);
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        PathUtil.EnsureDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            PathUtil.EnsureParentDirectory(destination);
            File.Copy(file, destination, overwrite: true);
        }
    }

    // ------------------------------------------------------------------ 元数据解析

    private LocalResource? Build(ResourceKind kind, string path, bool isDirectory)
    {
        string name;
        DateTime modifiedAt;
        long sizeBytes;

        try
        {
            var info = new DirectoryInfo(path);
            name = isDirectory ? info.Name : Path.GetFileName(path);
            modifiedAt = isDirectory ? info.LastWriteTimeUtc : new FileInfo(path).LastWriteTimeUtc;

            // 扫描与用户操作并发时，文件可能在枚举后立刻被删除或被独占；
            // 这种情况只跳过这一项并记录，不能让整类扫描中断。
            sizeBytes = isDirectory ? EstimateSize(path) : new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"读取资源信息失败，已跳过：{path}", ex);
            return null;
        }

        var resource = new LocalResource
        {
            Kind = kind,
            FilePath = path,
            FileName = name,
            Enabled = !name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase),
            ModifiedAt = modifiedAt,
            SizeBytes = sizeBytes,
        };

        var effective = name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^DisabledSuffix.Length]
            : name;

        resource.DisplayName = Path.GetFileNameWithoutExtension(effective);

        try
        {
            switch (kind)
            {
                case ResourceKind.Mod:
                    ReadModMetadata(resource, path);
                    break;
                case ResourceKind.ResourcePack:
                case ResourceKind.Datapack:
                case ResourceKind.ShaderPack:
                    ReadPackMetadata(resource, path);
                    break;
                case ResourceKind.World:
                    ReadWorldMetadata(resource, path);
                    break;
                case ResourceKind.Schematic:
                    ReadSchematicMetadata(resource, path);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _log.Trace($"读取 {kind} 元数据失败（用文件名兜底）：{name}");
        }

        return resource;
    }

    private static long EstimateSize(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void ReadModMetadata(LocalResource resource, string path)
    {
        if (!File.Exists(path) || !ArchiveUtil.LooksLikeZip(path))
        {
            return;
        }

        // 元数据与图标都在同一个 jar 里，共用一次 ZipArchive，避免每个条目重复打开压缩包。
        using var archive = ZipFile.OpenRead(path);

        // Fabric：fabric.mod.json
        var json = ReadTextEntry(archive, "fabric.mod.json");
        if (json is not null)
        {
            var mod = JsonUtil.Deserialize<FabricModMetadata>(json);
            if (mod is not null)
            {
                resource.DisplayName = mod.Name ?? mod.Id ?? resource.DisplayName;
                resource.Version = mod.Version;
                resource.Description = mod.Description;
                resource.Loader = "Fabric";
                resource.IconBytes = ReadArchiveIcon(
                    archive,
                    ReadIconPath(mod.Icon),
                    mod.Id is null ? null : $"assets/{mod.Id}/icon.png",
                    "icon.png",
                    "logo.png");
                return;
            }
        }

        // Quilt：quilt.mod.json
        var quiltJson = ReadTextEntry(archive, "quilt.mod.json");
        if (quiltJson is not null)
        {
            var quilt = JsonUtil.Deserialize<QuiltModMetadata>(quiltJson)?.Loader;
            if (quilt is not null)
            {
                resource.DisplayName = quilt.Metadata?.Name ?? quilt.Id ?? resource.DisplayName;
                resource.Version = quilt.Version;
                resource.Description = quilt.Metadata?.Description;
                resource.Loader = "Quilt";
                resource.IconBytes = ReadArchiveIcon(
                    archive,
                    ReadIconPath(quilt.Metadata?.Icon ?? default),
                    quilt.Id is null ? null : $"assets/{quilt.Id}/icon.png",
                    "icon.png",
                    "logo.png");
                return;
            }
        }

        // Forge / NeoForge：META-INF/mods.toml 或 META-INF/neoforge.mods.toml
        var toml = ReadTextEntry(archive, "META-INF/mods.toml");
        var loader = "Forge";

        if (toml is null)
        {
            toml = ReadTextEntry(archive, "META-INF/neoforge.mods.toml");
            loader = "NeoForge";
        }

        if (toml is null)
        {
            resource.IconBytes = ReadArchiveIcon(archive, "icon.png", "logo.png");
            return;
        }

        string? logoFile = null;

        foreach (var line in toml.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("displayName", StringComparison.OrdinalIgnoreCase))
            {
                resource.DisplayName = ExtractTomlValue(trimmed) ?? resource.DisplayName;
            }
            else if (trimmed.StartsWith("version", StringComparison.OrdinalIgnoreCase) && resource.Version is null)
            {
                resource.Version = ExtractTomlValue(trimmed);
            }
            else if (trimmed.StartsWith("description", StringComparison.OrdinalIgnoreCase))
            {
                resource.Description = ExtractTomlValue(trimmed);
            }
            else if (trimmed.StartsWith("logoFile", StringComparison.OrdinalIgnoreCase))
            {
                logoFile = ExtractTomlValue(trimmed);
            }
        }

        resource.Loader = loader;
        resource.IconBytes = ReadArchiveIcon(archive, logoFile, "icon.png", "logo.png");
    }

    private const int MaxIconBytes = 2 * 1024 * 1024;

    /// <summary>从已打开的 jar / zip 里按候选路径找图标，找到第一个可用图片就返回。</summary>
    private static byte[]? ReadArchiveIcon(ZipArchive archive, params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var entry = FindEntry(archive, candidate);
            if (entry is null)
            {
                continue;
            }

            // 图标只用于列表缩略图；按条目声明的原始大小先过滤，避免超大 icon.png 被整个读进内存。
            if (entry.Length > MaxIconBytes)
            {
                continue;
            }

            try
            {
                using var stream = entry.Open();
                using var memory = new MemoryStream((int)entry.Length);
                stream.CopyTo(memory);
                var bytes = memory.ToArray();
                if (IsUsableIcon(bytes))
                {
                    return bytes;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    private static string? ReadTextEntry(ZipArchive archive, string entryName)
    {
        var entry = FindEntry(archive, entryName);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryName)
    {
        var normalized = entryName.Trim().TrimStart('/').Replace('\\', '/');
        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.Replace('\\', '/').TrimStart('/'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static byte[]? ReadFileIcon(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // 与压缩包条目同样先看文件大小，避免异常的大图一次性读进内存。
            if (new FileInfo(path).Length > MaxIconBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            return IsUsableIcon(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsUsableIcon(byte[]? bytes) => bytes is { Length: > 0 and <= MaxIconBytes };

    /// <summary>fabric.mod.json 的 icon 可以是字符串，也可能是「尺寸 → 路径」的对象。</summary>
    private static string? ReadIconPath(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static string? ExtractTomlValue(string line)
    {
        var separator = line.IndexOf('=');
        if (separator < 0)
        {
            return null;
        }

        var value = line[(separator + 1)..].Trim().Trim('"', '\'', ',');
        return value.Length == 0 ? null : value;
    }

    private static void ReadPackMetadata(LocalResource resource, string path)
    {
        if (Directory.Exists(path))
        {
            var file = Path.Combine(path, "pack.mcmeta");
            if (File.Exists(file))
            {
                ApplyPackMeta(resource, File.ReadAllText(file));
            }

            // 资源包 / 光影包的封面图都叫 pack.png。
            resource.IconBytes = ReadFileIcon(Path.Combine(path, "pack.png"));
            return;
        }

        if (!ArchiveUtil.LooksLikeZip(path))
        {
            return;
        }

        // 与 jar 相同：元数据与封面共用一次 ZipArchive。
        using var archive = ZipFile.OpenRead(path);

        var json = ReadTextEntry(archive, "pack.mcmeta")
                   ?? ReadTextEntry(archive, "pack/pack.mcmeta");
        if (json is not null)
        {
            ApplyPackMeta(resource, json);
        }

        resource.IconBytes = ReadArchiveIcon(archive, "pack.png");
    }

    private static void ApplyPackMeta(LocalResource resource, string json)
    {
        var pack = JsonUtil.Deserialize<PackMcMeta>(json);
        resource.Description = ReadPackDescription(pack);
        resource.Version = pack?.Pack?.PackFormat.ToString();
    }

    /// <summary>pack.mcmeta 的 description 既可能是字符串，也可能是 <c>{"translate": "..."}</c>。</summary>
    private static string? ReadPackDescription(PackMcMeta? pack)
    {
        var description = pack?.Pack?.Description;
        if (description is not { } element)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object when element.TryGetProperty("translate", out var translate) => translate.GetString(),
            _ => null,
        };
    }

    private static void ReadWorldMetadata(LocalResource resource, string path)
    {
        var levelDat = Path.Combine(path, "level.dat");
        if (NbtReader.TryReadLevelDat(levelDat, out var levelName, out var versionName))
        {
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                resource.DisplayName = levelName!;
            }

            resource.GameVersion = versionName;
        }

        // 存档图标是存档目录根下的 64×64 icon.png。
        resource.IconBytes = ReadFileIcon(Path.Combine(path, "icon.png"));
    }

    private static void ReadSchematicMetadata(LocalResource resource, string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".litematic", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // .litematic 也是 gzip 过的 NBT，但结构里没有 level.dat 那套键；
        // 这里只做一次轻量尝试，失败就用文件名。
        if (NbtReader.TryReadLevelDat(path, out var name, out var version))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                resource.DisplayName = name!;
            }

            resource.GameVersion = version;
        }
    }

    // ------------------------------------------------------------------ 元数据模型

    private sealed class FabricModMetadata
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("icon")]
        public JsonElement Icon { get; set; }
    }

    private sealed class QuiltModMetadata
    {
        [JsonPropertyName("quilt_loader")]
        public QuiltLoader? Loader { get; set; }
    }

    private sealed class QuiltLoader
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("metadata")]
        public QuiltMetadata? Metadata { get; set; }
    }

    private sealed class QuiltMetadata
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("icon")]
        public JsonElement Icon { get; set; }
    }

    private sealed class PackMcMeta
    {
        [JsonPropertyName("pack")]
        public PackSection? Pack { get; set; }
    }

    private sealed class PackSection
    {
        [JsonPropertyName("pack_format")]
        public int PackFormat { get; set; }

        /// <summary>字符串或对象两种形态，因此用 JsonElement 保留原样再判类型。</summary>
        [JsonPropertyName("description")]
        public JsonElement Description { get; set; }
    }
}
