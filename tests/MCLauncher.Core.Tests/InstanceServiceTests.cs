using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services;
using MCLauncher.Core.Tests.Support;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Tests;

public sealed class InstanceServiceTests
{
    private static InstanceService Create(TempDirectory temp, out VersionRepository repository)
    {
        var log = new TestLog();
        repository = new VersionRepository(new VersionResolver(log), log);

        var config = new ConfigService(
            new JsonConfigStore(Path.Combine(temp.Path, "config"), log),
            log,
            includeBuiltInSecrets: false);
        config.Load();

        // 用例不触发联网分支；这里只提供 RemoveLoaderAsync 需要的版本清单服务。
        var manifests = new VersionManifestService(
            new HttpDownloader(log),
            new DownloadSourceResolver(log),
            log,
            Path.Combine(temp.Path, "cache"));

        return new InstanceService(repository, config, manifests, log);
    }

    [Fact]
    public void 加载器实例会隐藏它依赖的原版基底()
    {
        using var temp = new TempDirectory();
        var instances = Create(temp, out var repository);

        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>("""{"id":"26.3"}""")!);
        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>(
            """{"id":"Fabric26.3","inheritsFrom":"26.3"}""")!);

        var visible = instances.ListVisibleInstanceIds(temp.Path);

        Assert.Equal(new[] { "Fabric26.3" }, visible);
    }

    [Fact]
    public void 没有被继承的原版仍然显示()
    {
        using var temp = new TempDirectory();
        var instances = Create(temp, out var repository);

        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>("""{"id":"1.20.1"}""")!);
        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>("""{"id":"26.3"}""")!);
        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>(
            """{"id":"Fabric26.3","inheritsFrom":"26.3"}""")!);

        var visible = instances.ListVisibleInstanceIds(temp.Path);

        Assert.Equal(new[] { "1.20.1", "Fabric26.3" }, visible);
    }

    [Fact]
    public void 实例覆盖项保存在全局设置且会清理空条目()
    {
        using var temp = new TempDirectory();
        var instances = Create(temp, out _);

        var config = instances.Load(temp.Path, "26.3");
        config.MaxMemoryMb = 4096;
        config.Isolated = false;
        instances.Save(temp.Path, "26.3", config);

        Assert.Equal(4096, instances.Load(temp.Path, "26.3").MaxMemoryMb);
        Assert.False(instances.IsIsolated(temp.Path, "26.3", globalDefault: true));

        config.MaxMemoryMb = null;
        config.Isolated = null;
        instances.Save(temp.Path, "26.3", config);

        // 全部为 null 时条目被移除，实例重新跟随全局默认。
        Assert.True(instances.IsIsolated(temp.Path, "26.3", globalDefault: true));
        Assert.Null(instances.Load(temp.Path, "26.3").MaxMemoryMb);
    }

    [Fact]
    public void 重命名实例会迁移全局覆盖项()
    {
        using var temp = new TempDirectory();
        var instances = Create(temp, out var repository);

        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>(
            """{"id":"OldName","jar":"OldName","mainClass":"net.minecraft.client.main.Main"}""")!);

        var config = instances.Load(temp.Path, "OldName");
        config.MaxMemoryMb = 8192;
        instances.Save(temp.Path, "OldName", config);

        var newId = instances.RenameInstance(temp.Path, "OldName", "Eternal");

        Assert.Equal("Eternal", newId);
        Assert.True(repository.IsInstalled(temp.Path, "Eternal"));
        Assert.False(repository.IsInstalled(temp.Path, "OldName"));
        Assert.Equal(8192, instances.Load(temp.Path, "Eternal").MaxMemoryMb);
        Assert.Null(instances.Load(temp.Path, "OldName").MaxMemoryMb);
    }

    [Fact]
    public void 重命名为已存在的实例名会追加序号()
    {
        using var temp = new TempDirectory();
        var instances = Create(temp, out var repository);

        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>(
            """{"id":"One","mainClass":"net.minecraft.client.main.Main"}""")!);
        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>(
            """{"id":"Two","mainClass":"net.minecraft.client.main.Main"}""")!);

        var newId = instances.RenameInstance(temp.Path, "One", "Two");

        Assert.Equal("Two (2)", newId);
        Assert.True(repository.IsInstalled(temp.Path, "Two"));
        Assert.True(repository.IsInstalled(temp.Path, "Two (2)"));
        Assert.False(repository.IsInstalled(temp.Path, "One"));
    }

    [Fact]
    public void 删除实例会清掉全局覆盖项()
    {
        using var temp = new TempDirectory();
        var instances = Create(temp, out var repository);

        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>(
            """{"id":"26.3","mainClass":"net.minecraft.client.main.Main"}""")!);

        var config = instances.Load(temp.Path, "26.3");
        config.MaxMemoryMb = 4096;
        instances.Save(temp.Path, "26.3", config);

        Assert.True(instances.Delete(temp.Path, "26.3"));
        Assert.Null(instances.Load(temp.Path, "26.3").MaxMemoryMb);
    }

    [Fact]
    public async Task 基底存在时卸载加载器恢复为继承原版()
    {
        using var temp = new TempDirectory();
        var instances = Create(temp, out var repository);

        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>(
            """{"id":"26.3","mainClass":"net.minecraft.client.main.Main","libraries":[{"name":"base:a:1"}]}""")!);
        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>(
            """{"id":"Fabric26.3","mainClass":"net.fabricmc.KnotClient","libraries":[{"name":"fabric:b:1"}]}""")!);

        Assert.True(await instances.RemoveLoaderAsync(temp.Path, "Fabric26.3", "26.3"));

        var restored = repository.TryLoad(temp.Path, "Fabric26.3")!;
        Assert.Equal("26.3", restored.InheritsFrom);
        Assert.Null(restored.MainClass);

        // 卸载后版本依赖基底，实例列表里只剩加载器实例这一条（基底被继承而隐藏）。
        Assert.Equal(new[] { "Fabric26.3" }, instances.ListVisibleInstanceIds(temp.Path));
    }

    [Fact]
    public async Task 原版版本相同时卸载加载器不做改动()
    {
        using var temp = new TempDirectory();
        var instances = Create(temp, out var repository);

        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>(
            """{"id":"26.3","mainClass":"net.minecraft.client.main.Main"}""")!);

        Assert.False(await instances.RemoveLoaderAsync(temp.Path, "26.3", "26.3"));
    }

    [Fact]
    public void 从客户端jar读取导入实例的游戏版本()
    {
        using var temp = new TempDirectory();
        var instances = Create(temp, out var repository);

        repository.Save(temp.Path, JsonUtil.Deserialize<GameVersionJson>(
            """{"id":"Eternal","jar":"Eternal","mainClass":"net.fabricmc.KnotClient"}""")!);
        var jarPath = VersionRepository.VersionJarPath(temp.Path, "Eternal");
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        ZipBuilder.Create(jarPath, ("version.json", """{"id":"26.3","name":"26.3"}"""));

        Assert.Equal("26.3", instances.ReadJarGameVersion(temp.Path, "Eternal"));
    }

    [Fact]
    public void 客户端jar没有版本信息时返回空()
    {
        using var temp = new TempDirectory();
        var instances = Create(temp, out _);

        var jarPath = VersionRepository.VersionJarPath(temp.Path, "Eternal");
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        ZipBuilder.Create(jarPath, ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0"));

        Assert.Null(instances.ReadJarGameVersion(temp.Path, "Eternal"));
        Assert.Null(instances.ReadJarGameVersion(temp.Path, "不存在"));
    }
}
