using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// 本地版本仓库。<c>&lt;游戏目录&gt;/versions/&lt;id&gt;/&lt;id&gt;.json</c> 是唯一的事实来源，
/// 版本 JSON 与 jar 都按这个布局存放，加载器版本通过 <c>inheritsFrom</c> 指回原版。
/// </summary>
public sealed class VersionRepository
{
    private readonly VersionResolver _resolver;
    private readonly ILogger _log;

    public VersionRepository(VersionResolver resolver, ILogger log)
    {
        _resolver = resolver;
        _log = log;
    }

    public static string VersionsDirectory(string gameDirectory) => Path.Combine(gameDirectory, "versions");

    public static string VersionDirectory(string gameDirectory, string versionId) =>
        Path.Combine(VersionsDirectory(gameDirectory), versionId);

    public static string VersionJsonPath(string gameDirectory, string versionId) =>
        Path.Combine(VersionDirectory(gameDirectory, versionId), versionId + ".json");

    /// <summary>客户端 jar 路径。<paramref name="jarId"/> 取版本 JSON 的 <c>jar</c> 字段（旧版 Forge 会指向原版）。</summary>
    public static string VersionJarPath(string gameDirectory, string jarId) =>
        Path.Combine(VersionDirectory(gameDirectory, jarId), jarId + ".jar");

    /// <summary>natives 解压目录。</summary>
    public static string NativesDirectory(string gameDirectory, string versionId) =>
        Path.Combine(VersionDirectory(gameDirectory, versionId), "natives-" + GameEnvironment.OsName);

    public bool IsInstalled(string gameDirectory, string versionId) =>
        File.Exists(VersionJsonPath(gameDirectory, versionId));

    /// <summary>读取单个版本 JSON（不解析继承链）。</summary>
    public GameVersionJson? TryLoad(string gameDirectory, string versionId)
    {
        var path = VersionJsonPath(gameDirectory, versionId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonUtil.DeserializeFromFile<GameVersionJson>(path);
        }
        catch (Exception ex)
        {
            _log.Warn($"版本 JSON 解析失败：{path}", ex);
            return null;
        }
    }

    /// <summary>写入版本 JSON（原子写）。</summary>
    public void Save(string gameDirectory, GameVersionJson version)
    {
        var id = version.Id ?? throw new InvalidOperationException("版本 JSON 没有 id，无法落盘");
        EnsureValidId(id);

        JsonUtil.SerializeToFile(VersionJsonPath(gameDirectory, id), version);
        _log.Info($"已写入版本 JSON：{VersionJsonPath(gameDirectory, id)}");
    }

    /// <summary>解析出完整（已合并继承链）的版本 JSON。</summary>
    public GameVersionJson Resolve(string gameDirectory, string versionId)
    {
        var leaf = TryLoad(gameDirectory, versionId)
                   ?? throw new InvalidDataException($"本地不存在版本：{versionId}（{VersionJsonPath(gameDirectory, versionId)}）");

        return _resolver.Resolve(leaf, parentId => TryLoad(gameDirectory, parentId));
    }

    /// <summary>列出本地已安装的版本 id（<c>versions/*/*.json</c> 扫描）。</summary>
    public IReadOnlyList<string> ListInstalledIds(string gameDirectory)
    {
        var versionsDirectory = VersionsDirectory(gameDirectory);
        if (!Directory.Exists(versionsDirectory))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
        {
            var id = Path.GetFileName(directory);
            if (!string.IsNullOrWhiteSpace(id) && File.Exists(VersionJsonPath(gameDirectory, id)))
            {
                result.Add(id);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>删除某个版本目录（仅限本地目录内，防止路径穿越）。</summary>
    public bool Delete(string gameDirectory, string versionId)
    {
        var directory = VersionDirectory(gameDirectory, versionId);
        if (!PathUtil.IsParentOf(VersionsDirectory(gameDirectory), directory) || !Directory.Exists(directory))
        {
            return false;
        }

        Directory.Delete(directory, recursive: true);
        _log.Info($"已删除版本目录：{directory}");
        return true;
    }

    /// <summary>重命名版本：把目录与其中的 JSON/jar 一起改名。</summary>
    public void Rename(string gameDirectory, string oldId, string newId)
    {
        EnsureValidId(newId);

        var oldDirectory = VersionDirectory(gameDirectory, oldId);
        var newDirectory = VersionDirectory(gameDirectory, newId);
        if (!Directory.Exists(oldDirectory))
        {
            throw new DirectoryNotFoundException($"版本目录不存在：{oldDirectory}");
        }

        // 仅大小写不同的改名（ABC → abc）仍指向同一个目录，不算冲突，必须放行。
        var caseOnlyRename = !string.Equals(oldDirectory, newDirectory, StringComparison.Ordinal)
                             && string.Equals(oldDirectory, newDirectory, StringComparison.OrdinalIgnoreCase);

        if (Directory.Exists(newDirectory) && !caseOnlyRename)
        {
            throw new IOException($"目标版本已存在：{newId}");
        }

        // 必须在移动目录之前读出来：移动后按新 id 就找不到旧文件名的 JSON 了。
        var version = TryLoad(gameDirectory, oldId)
                      ?? throw new InvalidDataException($"版本 JSON 不存在：{VersionJsonPath(gameDirectory, oldId)}");

        Directory.Move(oldDirectory, newDirectory);

        if (!string.Equals(oldId, newId, StringComparison.Ordinal))
        {
            var oldJson = Path.Combine(newDirectory, oldId + ".json");
            if (File.Exists(oldJson))
            {
                File.Move(oldJson, Path.Combine(newDirectory, newId + ".json"));
            }

            var oldJar = Path.Combine(newDirectory, oldId + ".jar");
            if (File.Exists(oldJar))
            {
                File.Move(oldJar, Path.Combine(newDirectory, newId + ".jar"));
            }
        }

        version.Id = newId;
        Save(gameDirectory, version);
        _log.Info($"版本已重命名：{oldId} → {newId}");
    }

    /// <summary>取实际要加载的客户端 jar 版本号（<c>jar</c> 字段优先，缺失则用 <c>id</c>）。</summary>
    public static string ResolveJarId(GameVersionJson version) =>
        string.IsNullOrWhiteSpace(version.Jar) ? version.Id ?? string.Empty : version.Jar!;

    private static void EnsureValidId(string versionId)
    {
        if (!PathUtil.IsValidFileName(versionId))
        {
            throw new ArgumentException($"版本名不是合法的目录名：{versionId}", nameof(versionId));
        }
    }
}
