namespace MCLauncher.Core.Network;

/// <summary>
/// 下载地址的语义类别。
/// 镜像映射必须按类别分别处理：把 <c>libraries.minecraft.net</c> 换成
/// <c>…/maven</c>，把资源对象换成 <c>…/assets</c>，把元数据换成镜像根 —— 一刀切换域名必然 404。
/// </summary>
public enum DownloadUrlKind
{
    /// <summary>版本清单、版本 JSON 等元数据（piston-meta / launchermeta / piston-data 根路径）。</summary>
    Metadata,

    /// <summary>客户端 jar（piston-data 的 v1/objects，或镜像的 versions/&lt;id&gt;/&lt;id&gt;.jar）。</summary>
    Client,

    /// <summary>资源索引 JSON（piston-meta 的 v1/packages）。</summary>
    AssetIndex,

    /// <summary>资源对象（resources.download.minecraft.net 下的 hash 分片）。</summary>
    Asset,

    /// <summary>原版库（libraries.minecraft.net 等）。</summary>
    Library,

    /// <summary>加载器系库（maven.minecraftforge.net、maven.neoforged.net、fabricmc 等）。</summary>
    ModLoaderLibrary,

    /// <summary>加载器安装器 jar 或元数据。</summary>
    ModLoaderInstaller,

    /// <summary>加载器版本列表 / profile 等元数据（Fabric/Quilt meta、Forge promos、NeoForge maven API）。</summary>
    ModLoaderMetadata,

    /// <summary>Mod 平台接口（Modrinth / CurseForge）。</summary>
    ModPlatform,

    /// <summary>不参与镜像映射（用户自定义地址）。</summary>
    Custom,
}
