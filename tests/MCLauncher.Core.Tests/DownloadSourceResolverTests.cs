using MCLauncher.Core.Network;
using MCLauncher.Core.Tests.Support;

namespace MCLauncher.Core.Tests;

/// <summary>
/// 镜像映射是"按 URL 类别分别处理"的，这里逐类锁定行为，防止将来被改成"一刀切换域名"。
/// </summary>
public sealed class DownloadSourceResolverTests
{
    private static DownloadSourceResolver Create(
        DownloadSourcePreference preference = DownloadSourcePreference.Mirror) =>
        new(new TestLog()) { VersionListSource = preference, FileSource = preference };

    [Fact]
    public void 元数据走镜像根路径()
    {
        var resolver = Create();
        var result = resolver.Resolve(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
            DownloadUrlKind.Metadata);

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
            result[0].Url);
        Assert.Contains(result, c => c.Url.StartsWith("https://piston-meta.mojang.com/", StringComparison.Ordinal));
    }

    [Fact]
    public void 资源对象落到镜像的assets子路径()
    {
        var resolver = Create();
        var result = resolver.Resolve(
            "https://resources.download.minecraft.net/ab/abcdef0123456789",
            DownloadUrlKind.Asset);

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/assets/ab/abcdef0123456789",
            result[0].Url);
    }

    [Fact]
    public void 库文件同时给出maven与libraries两个镜像候选()
    {
        var resolver = Create();
        var result = resolver.Resolve(
            "https://libraries.minecraft.net/org/ow2/asm/asm/9.6/asm-9.6.jar",
            DownloadUrlKind.Library);

        var mirrorUrls = result.Where(c => c.SourceName.StartsWith("BMCLAPI", StringComparison.Ordinal))
            .Select(c => c.Url)
            .ToList();

        Assert.Contains("https://bmclapi2.bangbang93.com/maven/org/ow2/asm/asm/9.6/asm-9.6.jar", mirrorUrls);
        Assert.Contains("https://bmclapi2.bangbang93.com/libraries/org/ow2/asm/asm/9.6/asm-9.6.jar", mirrorUrls);
    }

    [Fact]
    public void 客户端jar在已知版本号时给出镜像的versions短路径()
    {
        var resolver = Create();
        var result = resolver.Resolve(
            "https://piston-data.mojang.com/v1/objects/deadbeef/client.jar",
            DownloadUrlKind.Client,
            "1.20.4");

        Assert.Contains(
            result,
            c => c.Url == "https://bmclapi2.bangbang93.com/versions/1.20.4/1.20.4.jar");
    }

    [Fact]
    public void 旧版明文http地址会被升级为https()
    {
        var resolver = Create(DownloadSourcePreference.Official);
        var result = resolver.Resolve(
            "http://resources.download.minecraft.net/ab/abcdef",
            DownloadUrlKind.Asset);

        Assert.Equal("https://resources.download.minecraft.net/ab/abcdef", result[0].Url);
    }

    [Fact]
    public void 优先官方时官方排在首位且镜像仍然保留()
    {
        var resolver = Create(DownloadSourcePreference.Official);
        var result = resolver.Resolve(
            "https://libraries.minecraft.net/gson.jar",
            DownloadUrlKind.Library);

        Assert.Equal("官方", result[0].SourceName);
        Assert.Contains(result, c => c.SourceName.StartsWith("BMCLAPI", StringComparison.Ordinal));
    }

    [Fact]
    public void 自定义地址不做镜像映射()
    {
        var resolver = Create();
        var result = resolver.Resolve("https://example.com/foo/bar.jar", DownloadUrlKind.Custom);

        Assert.Single(result);
        Assert.Equal("https://example.com/foo/bar.jar", result[0].Url);
    }

    [Fact]
    public void Mod平台官方源始终排第一()
    {
        var resolver = Create(DownloadSourcePreference.Mirror);
        var result = resolver.Resolve(
            "https://api.modrinth.com/v2/project/fabric-api",
            DownloadUrlKind.ModPlatform);

        Assert.Equal("官方", result[0].SourceName);
        Assert.Contains(result, c => c.SourceName == "MCIMirror");
    }

    [Fact]
    public void 候选源不重复()
    {
        var resolver = Create();
        var result = resolver.Resolve(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
            DownloadUrlKind.Metadata);

        var distinct = result.Select(c => c.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(result.Count, distinct);
    }

    [Fact]
    public void 版本列表源与下载源互不影响()
    {
        var resolver = new DownloadSourceResolver(new TestLog())
        {
            VersionListSource = DownloadSourcePreference.Official,
            FileSource = DownloadSourcePreference.Mirror,
        };

        var metadata = resolver.Resolve(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
            DownloadUrlKind.Metadata);

        var library = resolver.Resolve(
            "https://libraries.minecraft.net/gson.jar",
            DownloadUrlKind.Library);

        Assert.Equal("官方", metadata[0].SourceName);
        Assert.Equal("BMCLAPI(Maven)", library[0].SourceName);
    }

    [Fact]
    public void 加载器元数据映射到镜像专用前缀()
    {
        var resolver = Create();

        var fabric = resolver.Resolve(
            "https://meta.fabricmc.net/v2/versions/loader",
            DownloadUrlKind.ModLoaderMetadata);
        Assert.Contains(
            "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader",
            fabric.Select(c => c.Url));

        var quilt = resolver.Resolve(
            "https://meta.quiltmc.org/v3/versions/loader",
            DownloadUrlKind.ModLoaderMetadata);
        Assert.Contains(
            "https://bmclapi2.bangbang93.com/quilt-meta/v3/versions/loader",
            quilt.Select(c => c.Url));

        var promos = resolver.Resolve(
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json",
            DownloadUrlKind.ModLoaderMetadata);
        Assert.Contains("https://bmclapi2.bangbang93.com/forge/promos", promos.Select(c => c.Url));

        var neoForge = resolver.Resolve(
            "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge",
            DownloadUrlKind.ModLoaderMetadata);
        Assert.Contains(
            "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/neoforge",
            neoForge.Select(c => c.Url));
    }

    [Fact]
    public void Quilt的profile不映射镜像()
    {
        var resolver = Create();
        var result = resolver.Resolve(
            "https://meta.quiltmc.org/v3/versions/loader/1.20.1/0.23.0/profile/json",
            DownloadUrlKind.ModLoaderMetadata);

        Assert.DoesNotContain(result, c => c.Url.Contains("quilt-meta", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("官方", result[0].SourceName);
    }

    [Fact]
    public void 加载器元数据跟随版本列表源()
    {
        var resolver = new DownloadSourceResolver(new TestLog())
        {
            VersionListSource = DownloadSourcePreference.Mirror,
            FileSource = DownloadSourcePreference.Official,
        };

        Assert.True(resolver.PreferMirrorForVersionList);

        var result = resolver.Resolve(
            "https://meta.fabricmc.net/v2/versions/loader",
            DownloadUrlKind.ModLoaderMetadata);

        Assert.StartsWith("BMCLAPI", result[0].SourceName);
    }
}
