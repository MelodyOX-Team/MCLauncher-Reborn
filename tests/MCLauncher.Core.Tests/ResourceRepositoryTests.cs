using MCLauncher.Core.Models;
using MCLauncher.Core.Services;
using MCLauncher.Core.Tests.Support;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Tests;

public sealed class ResourceRepositoryTests
{
    private static ResourceRepository Create(out TestLog log)
    {
        log = new TestLog();
        return new ResourceRepository(log);
    }

    private static void WriteMod(string modsDirectory, string fileName, string metadataJson)
    {
        Directory.CreateDirectory(modsDirectory);
        ZipBuilder.Create(Path.Combine(modsDirectory, fileName), ("fabric.mod.json", metadataJson));
    }

    [Fact]
    public void 扫描Mod并读出Fabric元数据()
    {
        using var temp = new TempDirectory();
        var repository = Create(out _);
        var mods = ResourceRepository.GetDirectory(temp.Path, ResourceKind.Mod);

        WriteMod(mods, "sodium.jar", """
        {"id":"sodium","name":"Sodium","version":"0.5.8","description":"性能优化"}
        """);

        var resources = repository.Scan(temp.Path, ResourceKind.Mod);

        var mod = Assert.Single(resources);
        Assert.Equal("Sodium", mod.DisplayName);
        Assert.Equal("0.5.8", mod.Version);
        Assert.True(mod.Enabled);
        Assert.Equal("sodium.jar", mod.FileName);
    }

    [Fact]
    public void 扫描Mod并读出Forge元数据()
    {
        using var temp = new TempDirectory();
        var repository = Create(out _);
        var mods = ResourceRepository.GetDirectory(temp.Path, ResourceKind.Mod);
        Directory.CreateDirectory(mods);

        ZipBuilder.Create(
            Path.Combine(mods, "jei.jar"),
            ("META-INF/mods.toml", "modLoader=\"javafml\"\ndisplayName=\"JEI\"\nversion=\"15.2.0\"\ndescription=\"物品管理器\"\n"));

        var mod = Assert.Single(repository.Scan(temp.Path, ResourceKind.Mod));

        Assert.Equal("JEI", mod.DisplayName);
        Assert.Equal("15.2.0", mod.Version);
    }

    [Fact]
    public void 启用禁用通过改后缀实现()
    {
        using var temp = new TempDirectory();
        var repository = Create(out _);
        var mods = ResourceRepository.GetDirectory(temp.Path, ResourceKind.Mod);
        WriteMod(mods, "sodium.jar", """{"name":"Sodium"}""");

        var mod = Assert.Single(repository.Scan(temp.Path, ResourceKind.Mod));
        Assert.True(repository.SetEnabled(mod, false));

        var disabled = Assert.Single(repository.Scan(temp.Path, ResourceKind.Mod));
        Assert.False(disabled.Enabled);
        Assert.EndsWith(ResourceRepository.DisabledSuffix, disabled.FileName);
        Assert.True(File.Exists(Path.Combine(mods, "sodium.jar.disabled")));

        Assert.True(repository.SetEnabled(disabled, true));
        var enabled = Assert.Single(repository.Scan(temp.Path, ResourceKind.Mod));
        Assert.True(enabled.Enabled);
        Assert.True(File.Exists(Path.Combine(mods, "sodium.jar")));
    }

    [Fact]
    public void 存档目录不支持禁用()
    {
        using var temp = new TempDirectory();
        var repository = Create(out _);
        var saves = ResourceRepository.GetDirectory(temp.Path, ResourceKind.World);
        Directory.CreateDirectory(Path.Combine(saves, "我的世界"));

        var world = Assert.Single(repository.Scan(temp.Path, ResourceKind.World));
        Assert.False(repository.SetEnabled(world, false));
    }

    [Fact]
    public void 读取存档名与版本()
    {
        using var temp = new TempDirectory();
        var repository = Create(out _);
        var saves = ResourceRepository.GetDirectory(temp.Path, ResourceKind.World);
        var world = Path.Combine(saves, "world");
        Directory.CreateDirectory(world);

        ZipBuilder.WriteLevelDat(Path.Combine(world, "level.dat"), "我的生存档", "1.20.4");

        var resource = Assert.Single(repository.Scan(temp.Path, ResourceKind.World));

        Assert.Equal("我的生存档", resource.DisplayName);
        Assert.Equal("1.20.4", resource.GameVersion);
    }

    [Fact]
    public void 损坏的level_dat不会让扫描崩溃()
    {
        using var temp = new TempDirectory();
        var repository = Create(out _);
        var saves = ResourceRepository.GetDirectory(temp.Path, ResourceKind.World);
        var world = Path.Combine(saves, "broken");
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "level.dat"), "这不是 NBT 数据");

        var resource = Assert.Single(repository.Scan(temp.Path, ResourceKind.World));

        // 读不出来就用目录名兜底
        Assert.Equal("broken", resource.DisplayName);
    }

    [Fact]
    public void 读取真实结构的level_dat()
    {
        using var temp = new TempDirectory();
        var repository = Create(out _);
        var saves = ResourceRepository.GetDirectory(temp.Path, ResourceKind.World);
        var world = Path.Combine(saves, "world");
        Directory.CreateDirectory(world);

        // LevelName 前面有大量其他类型标签，读取时必须逐个跳过；
        // gzip 流不支持 Seek，跳过逻辑只能分块读出来丢弃。
        ZipBuilder.WriteLevelDatWithPayload(Path.Combine(world, "level.dat"), "复杂生存档", "1.21.1");

        var resource = Assert.Single(repository.Scan(temp.Path, ResourceKind.World));

        Assert.Equal("复杂生存档", resource.DisplayName);
        Assert.Equal("1.21.1", resource.GameVersion);
    }

    [Fact]
    public void 扫描资源包并读出pack_mcmeta()
    {
        using var temp = new TempDirectory();
        var repository = Create(out _);
        var packs = ResourceRepository.GetDirectory(temp.Path, ResourceKind.ResourcePack);
        Directory.CreateDirectory(packs);

        ZipBuilder.Create(Path.Combine(packs, "shiny.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15,"description":"闪亮材质"}}"""));
        ZipBuilder.Create(Path.Combine(packs, "obj.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15,"description":{"translate":"packs.obj"}}}"""));

        var resources = repository.Scan(temp.Path, ResourceKind.ResourcePack);

        Assert.Equal(2, resources.Count);
        Assert.Contains(resources, r => r.Description == "闪亮材质");
        Assert.Contains(resources, r => r.Description == "packs.obj");
    }

    [Fact]
    public void 导入与删除()
    {
        using var temp = new TempDirectory();
        using var source = new TempDirectory();
        var repository = Create(out _);

        var modFile = source.File("cool.jar");
        File.WriteAllText(modFile, "jar bytes");

        Assert.True(repository.Import(temp.Path, ResourceKind.Mod, modFile));

        var mod = Assert.Single(repository.Scan(temp.Path, ResourceKind.Mod));
        Assert.Equal("cool.jar", mod.FileName);

        Assert.True(repository.Delete(mod));
        Assert.Empty(repository.Scan(temp.Path, ResourceKind.Mod));
    }

    [Fact]
    public void 扫描不存在的目录返回空列表()
    {
        using var temp = new TempDirectory();
        var repository = Create(out _);
        Assert.Empty(repository.Scan(temp.Path, ResourceKind.Schematic));
    }

    [Fact]
    public void 六类资源的目录名固定()
    {
        Assert.Equal("mods", ResourceRepository.DirectoryName(ResourceKind.Mod));
        Assert.Equal("resourcepacks", ResourceRepository.DirectoryName(ResourceKind.ResourcePack));
        Assert.Equal("shaderpacks", ResourceRepository.DirectoryName(ResourceKind.ShaderPack));
        Assert.Equal("saves", ResourceRepository.DirectoryName(ResourceKind.World));
        Assert.Equal("schematics", ResourceRepository.DirectoryName(ResourceKind.Schematic));
        Assert.Equal("datapacks", ResourceRepository.DirectoryName(ResourceKind.Datapack));
        Assert.Equal("modpacks", ResourceRepository.DirectoryName(ResourceKind.Modpack));
    }

    [Fact]
    public void 整合包有独立的显示名()
    {
        Assert.Equal("整合包", ResourceRepository.DisplayName(ResourceKind.Modpack));
    }
}
