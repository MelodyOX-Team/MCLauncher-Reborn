using System.IO;
using MCLauncher.Core.Models;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>拖进启动器窗口的文件类型。</summary>
public enum DroppedFileKind
{
    Mod,
    ResourcePack,
    ShaderPack,
    Schematic,
    World,
    Modpack,
    Unsupported,
}

/// <summary>一个被拖入的文件及其识别结果。</summary>
public sealed class DroppedFileInfo
{
    public required string Path { get; init; }

    public required DroppedFileKind Kind { get; init; }

    /// <summary>给用户看的补充说明（为什么无法识别、需要先解压等）。</summary>
    public string? Note { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public string KindText => Kind switch
    {
        DroppedFileKind.Mod => "模组",
        DroppedFileKind.ResourcePack => "资源包",
        DroppedFileKind.ShaderPack => "光影包",
        DroppedFileKind.Schematic => "原理图",
        DroppedFileKind.World => "世界存档",
        DroppedFileKind.Modpack => "整合包",
        _ => "无法识别",
    };

    /// <summary>是否能按实例资源直接导入（整合包走单独的安装流程）。</summary>
    public bool IsInstallableResource => Kind is not (DroppedFileKind.Modpack or DroppedFileKind.Unsupported);

    /// <summary>对应的本地资源类别；整合包与无法识别的文件为空。</summary>
    public ResourceKind? ResourceKind => Kind switch
    {
        DroppedFileKind.Mod => Models.ResourceKind.Mod,
        DroppedFileKind.ResourcePack => Models.ResourceKind.ResourcePack,
        DroppedFileKind.ShaderPack => Models.ResourceKind.ShaderPack,
        DroppedFileKind.Schematic => Models.ResourceKind.Schematic,
        DroppedFileKind.World => Models.ResourceKind.World,
        _ => null,
    };
}

/// <summary>
/// 识别拖入的文件：模组、资源包、光影包、原理图、存档文件夹或整合包。
/// <para>
/// 压缩包既可能是整合包也可能是资源包 / 光影包，只能读条目名判断；
/// 识别顺序与 <see cref="ModpackImporter"/> 保持一致：先认整合包标记，再认光影 / 资源包。
/// </para>
/// </summary>
public static class DroppedFileClassifier
{
    public static DroppedFileInfo Classify(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return ClassifyDirectory(path);
            }

            if (!File.Exists(path))
            {
                return Unknown(path, "文件不存在或已被移动。");
            }

            return ClassifyFile(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Unknown(path, "读取文件失败：" + ex.Message);
        }
    }

    private static DroppedFileInfo ClassifyDirectory(string path)
    {
        if (File.Exists(System.IO.Path.Combine(path, "level.dat")))
        {
            return new DroppedFileInfo { Path = path, Kind = DroppedFileKind.World };
        }

        return Unknown(path, "只能拖入文件，或包含 level.dat 的存档文件夹。");
    }

    private static DroppedFileInfo ClassifyFile(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".rar")
        {
            return Unknown(path, "不支持 .rar，请先解压或转成 .zip。");
        }

        if (extension == ".jar")
        {
            return new DroppedFileInfo { Path = path, Kind = DroppedFileKind.Mod };
        }

        if (extension is ".litematic" or ".schematic" or ".schem" or ".nbt")
        {
            return new DroppedFileInfo { Path = path, Kind = DroppedFileKind.Schematic };
        }

        if (extension == ".mcpack")
        {
            return new DroppedFileInfo { Path = path, Kind = DroppedFileKind.ResourcePack };
        }

        if (extension is not (".zip" or ".mrpack"))
        {
            return Unknown(path, "支持 .jar、.zip、.mrpack、.litematic、.schematic、.schem、.nbt。");
        }

        if (!ArchiveUtil.LooksLikeZip(path))
        {
            return Unknown(path, "这不是一个可读取的 zip 压缩包。");
        }

        var entries = ArchiveUtil.ListEntries(path)
            .Select(entry => entry.Replace('\\', '/').TrimStart('/'))
            .ToList();

        // 与 ModpackImporter.Inspect 相同的优先级：整合包标记最具体，必须先判断。
        if (IsModpackArchive(entries))
        {
            return new DroppedFileInfo { Path = path, Kind = DroppedFileKind.Modpack };
        }

        // 光影包既可能把 shaders/ 放在根目录，也可能多包一层文件夹。
        if (entries.Any(entry =>
                entry.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("/shaders/", StringComparison.OrdinalIgnoreCase)))
        {
            return new DroppedFileInfo { Path = path, Kind = DroppedFileKind.ShaderPack };
        }

        if (entries.Any(entry =>
                string.Equals(entry, "pack.mcmeta", StringComparison.OrdinalIgnoreCase)
                || entry.EndsWith("/pack.mcmeta", StringComparison.OrdinalIgnoreCase)))
        {
            return new DroppedFileInfo { Path = path, Kind = DroppedFileKind.ResourcePack };
        }

        if (entries.Any(entry => entry.EndsWith("level.dat", StringComparison.OrdinalIgnoreCase)))
        {
            return Unknown(path, "这是存档压缩包，请解压后把里面包含 level.dat 的文件夹拖进来。");
        }

        return Unknown(path, "无法识别这个压缩包：不是整合包、资源包或光影包。");
    }

    private static bool IsModpackArchive(IReadOnlyList<string> entries)
    {
        if (entries.Any(entry =>
                entry.EndsWith("mcbbs.packmeta", StringComparison.OrdinalIgnoreCase)
                || entry.EndsWith("mmc-pack.json", StringComparison.OrdinalIgnoreCase)
                || entry.EndsWith("modrinth.index.json", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // manifest.json 是 CurseForge 的标志；带 addons 的是 MCBBS 变体，两者都按整合包处理。
        return entries.Any(entry =>
            entry.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)
            && !entry.Contains("modrinth", StringComparison.OrdinalIgnoreCase));
    }

    private static DroppedFileInfo Unknown(string path, string note) =>
        new() { Path = path, Kind = DroppedFileKind.Unsupported, Note = note };
}
