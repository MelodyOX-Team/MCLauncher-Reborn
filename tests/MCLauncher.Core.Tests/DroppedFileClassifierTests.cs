using MCLauncher.Core.Services;
using MCLauncher.Core.Tests.Support;

namespace MCLauncher.Core.Tests;

/// <summary>拖入文件的类型识别：压缩包只能靠条目名判断，顺序错了会把资源包当整合包。</summary>
public sealed class DroppedFileClassifierTests
{
    [Fact]
    public void Jar识别为模组()
    {
        using var temp = new TempDirectory();
        var path = temp.File("sodium.jar");
        ZipBuilder.Create(path, ("fabric.mod.json", "{}"));

        Assert.Equal(DroppedFileKind.Mod, DroppedFileClassifier.Classify(path).Kind);
    }

    [Fact]
    public void 带pack_mcmeta的压缩包识别为资源包()
    {
        using var temp = new TempDirectory();
        var path = temp.File("pack.zip");
        ZipBuilder.Create(path, ("pack.mcmeta", "{}"), ("assets/minecraft/x.txt", "x"));

        Assert.Equal(DroppedFileKind.ResourcePack, DroppedFileClassifier.Classify(path).Kind);
    }

    [Fact]
    public void 带shaders目录的压缩包识别为光影包()
    {
        using var temp = new TempDirectory();
        var path = temp.File("shader.zip");
        ZipBuilder.Create(path, ("shaders/composite.vsh", "x"), ("pack.mcmeta", "{}"));

        Assert.Equal(DroppedFileKind.ShaderPack, DroppedFileClassifier.Classify(path).Kind);
    }

    [Fact]
    public void 整合包标记优先于资源包()
    {
        using var temp = new TempDirectory();
        var path = temp.File("pack.zip");
        ZipBuilder.Create(path, ("modrinth.index.json", "{}"), ("pack.mcmeta", "{}"));

        Assert.Equal(DroppedFileKind.Modpack, DroppedFileClassifier.Classify(path).Kind);
    }

    [Fact]
    public void CurseForge的manifest识别为整合包()
    {
        using var temp = new TempDirectory();
        var path = temp.File("cf.zip");
        ZipBuilder.Create(path, ("manifest.json", "{}"), ("overrides/config/a.toml", "x"));

        Assert.Equal(DroppedFileKind.Modpack, DroppedFileClassifier.Classify(path).Kind);
    }

    [Fact]
    public void 原理图按扩展名识别()
    {
        using var temp = new TempDirectory();
        var path = temp.File("build.litematic");
        File.WriteAllText(path, "x");

        Assert.Equal(DroppedFileKind.Schematic, DroppedFileClassifier.Classify(path).Kind);
    }

    [Fact]
    public void 存档文件夹按level_dat识别()
    {
        using var temp = new TempDirectory();
        var world = System.IO.Path.Combine(temp.Path, "world");
        Directory.CreateDirectory(world);
        File.WriteAllText(System.IO.Path.Combine(world, "level.dat"), "x");

        var info = DroppedFileClassifier.Classify(world);

        Assert.Equal(DroppedFileKind.World, info.Kind);
        Assert.True(info.IsInstallableResource);
    }

    [Fact]
    public void 无法识别的压缩包给出原因()
    {
        using var temp = new TempDirectory();
        var path = temp.File("unknown.zip");
        ZipBuilder.Create(path, ("readme.txt", "x"));

        var info = DroppedFileClassifier.Classify(path);

        Assert.Equal(DroppedFileKind.Unsupported, info.Kind);
        Assert.True(info.HasNote);
        Assert.False(info.IsInstallableResource);
    }

    [Fact]
    public void Rar明确提示不支持()
    {
        using var temp = new TempDirectory();
        var path = temp.File("pack.rar");
        File.WriteAllText(path, "x");

        var info = DroppedFileClassifier.Classify(path);

        Assert.Equal(DroppedFileKind.Unsupported, info.Kind);
        Assert.NotNull(info.Note);
        Assert.Contains("rar", info.Note!);
    }
}
