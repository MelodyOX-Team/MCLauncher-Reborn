using System.Text.Json.Serialization;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>窗口背景图来源。</summary>
public enum BackgroundMode
{
    /// <summary>默认底图（三种背景模式都未启用）。</summary>
    None,

    /// <summary>启动器目录下的 <c>bgskin</c> 目录，每次启动随机取一张。</summary>
    Folder,

    /// <summary>自定义目录（随机取图）或单张图片文件。</summary>
    Custom,

    /// <summary>从网络 URL 下载，每次启动重新拉取。</summary>
    Network,
}

/// <summary>游戏启动后启动器窗口的行为。</summary>
public enum LauncherVisibilityMode
{
    /// <summary>保持显示。</summary>
    KeepVisible = 0,

    /// <summary>隐藏启动器窗口（进程保留，游戏结束后仍隐藏）。</summary>
    Hide = 1,

    /// <summary>结束启动器进程，游戏继续运行。</summary>
    Close = 2,

    /// <summary>隐藏启动器，游戏结束后重新显示。</summary>
    HideAndReopen = 3,
}

/// <summary>启动器全局设置。序列化到便携目录 <c>mclcfg/settings.json</c>。</summary>
public sealed class AppSettings
{
    // ---- 初始化 ----
    /// <summary>是否已完成首次启动的初始化设置（添加账户、内存、Java）。</summary>
    public bool SetupCompleted { get; set; }

    // ---- 界面 ----
    /// <summary>窗口宽度（默认 720，可自由缩放）。</summary>
    public int WindowWidth { get; set; } = 720;

    /// <summary>窗口高度（默认 450）。</summary>
    public int WindowHeight { get; set; } = 450;

    /// <summary>窗口背景图来源；默认使用内置底图。</summary>
    public BackgroundMode BackgroundMode { get; set; } = BackgroundMode.None;

    /// <summary>用户是否明确选择过背景模式；未选择过时才按 bgskin 目录自动切换。</summary>
    public bool BackgroundModeConfigured { get; set; }

    /// <summary>自定义背景：目录（随机取图）或单张图片路径。</summary>
    public string? BackgroundCustomPath { get; set; }

    /// <summary>网络背景图直链；使用随机图片 API 时每次启动都会重新拉取。</summary>
    public string? BackgroundUrl { get; set; }

    // ---- 游戏目录与实例 ----
    public List<string> GameFolders { get; set; } = new();

    public string? SelectedGameFolder { get; set; }

    public string? SelectedInstanceId { get; set; }

    /// <summary>
    /// 按实例 id 索引的实例级覆盖项（隔离、内存、Java、额外参数、窗口）。
    /// 与 HMCL 的 <c>instance-game-settings.json</c> 一样集中存放在启动器全局设置里，
    /// 实例目录下不再写 <c>.mclauncher</c> 配置文件；没有覆盖项的实例不会产生条目。
    /// </summary>
    public Dictionary<string, InstanceConfig> InstanceOverrides { get; set; } = new();

    // ---- 下载 ----
    /// <summary>版本清单、版本 JSON 的下载源偏好。</summary>
    public DownloadSourcePreference VersionListSource { get; set; } = DownloadSourcePreference.Auto;

    /// <summary>客户端、依赖库、资源对象等游戏文件的下载源偏好。</summary>
    public DownloadSourcePreference FileSource { get; set; } = DownloadSourcePreference.Auto;

    /// <summary>模组 / 资源包 / 光影等附加内容的默认检索与下载源。</summary>
    public ResourceSource DefaultContentSource { get; set; } = ResourceSource.Modrinth;

    /// <summary>是否自动选择下载并发数。</summary>
    public bool AutoDownloadThreads { get; set; } = true;

    /// <summary>自定义下载并发数（仅在关闭自动选择时生效）。</summary>
    public int DownloadThreads { get; set; } = AutoThreadCount;

    /// <summary>自动模式使用的下载并发数。</summary>
    public const int AutoThreadCount = 16;

    /// <summary>自定义下载并发数的上限。</summary>
    public const int MaxThreadCount = 64;

    /// <summary>实际生效的下载并发数。</summary>
    [JsonIgnore]
    public int EffectiveDownloadThreads =>
        AutoDownloadThreads ? AutoThreadCount : Math.Clamp(DownloadThreads, 1, MaxThreadCount);

    /// <summary>自定义镜像根地址，为空时用内置默认值。</summary>
    public string? MirrorBase { get; set; }

    // ---- 启动参数 ----
    public int MaxMemoryMb { get; set; } = 2048;

    public int MinMemoryMb { get; set; } = 512;

    public string? ExtraJvmArgs { get; set; }

    public string? ExtraGameArgs { get; set; }

    public int DefaultWidth { get; set; } = 854;

    public int DefaultHeight { get; set; } = 480;

    /// <summary>游戏窗口类型：窗口化 / 全屏 / 最大化。</summary>
    public GameWindowMode WindowMode { get; set; } = GameWindowMode.Windowed;

    /// <summary>默认是否对实例启用版本隔离。</summary>
    public bool DefaultIsolation { get; set; } = true;

    /// <summary>启动前跳过完整性与缺失文件检查（更快，但缺文件时可能启动失败）。</summary>
    public bool SkipIntegrityCheck { get; set; }

    /// <summary>游戏进程工作目录；为空时使用实例目录。</summary>
    public string? LaunchWorkingDirectory { get; set; }

    /// <summary>传给游戏进程的额外环境变量，每行 <c>NAME=VALUE</c>。</summary>
    public string? EnvironmentVariablesText { get; set; }

    /// <summary>游戏进程优先级。</summary>
    public GameProcessPriority ProcessPriority { get; set; } = GameProcessPriority.Normal;

    /// <summary>游戏启动后启动器窗口的行为。</summary>
    public LauncherVisibilityMode LauncherVisibility { get; set; } = LauncherVisibilityMode.KeepVisible;

    /// <summary>快速游玩模式。</summary>
    public QuickPlayMode QuickPlayMode { get; set; } = QuickPlayMode.None;

    /// <summary>快速游玩：服务器地址（<c>host:port</c>，端口省略时用 25565）。</summary>
    public string? QuickPlayServer { get; set; }

    /// <summary>快速游玩：单人存档文件夹名。</summary>
    public string? QuickPlayWorld { get; set; }

    /// <summary>快速游玩：Realms 服务器 ID。</summary>
    public string? QuickPlayRealmsId { get; set; }

    /// <summary>放在 java 命令前面的包装命令。</summary>
    public string? WrapperCommand { get; set; }

    /// <summary>游戏启动前执行的命令（Windows <c>cmd</c> 语法）。</summary>
    public string? PreLaunchCommand { get; set; }

    /// <summary>游戏结束后执行的命令（Windows <c>cmd</c> 语法）。</summary>
    public string? PostLaunchCommand { get; set; }

    // ---- Java ----
    /// <summary>手动指定的 Java 可执行文件路径；为空时自动探测。</summary>
    public string? JavaPath { get; set; }

    /// <summary>是否允许自动挑选 Java。</summary>
    public bool AutoSelectJava { get; set; } = true;

    /// <summary>最近一次探测到的 Java 列表缓存（路径 + 版本串）。</summary>
    public List<DetectedJava> DetectedJavas { get; set; } = new();

    // ---- 账号 ----
    /// <summary>
    /// 旧版 settings.json 中的账号列表。账号已独立保存到加密的 <c>mclcfg/accounts.json</c>，
    /// 这里只为一次性迁移保留：迁移完成后置空，不再写回 settings.json。
    /// </summary>
    [JsonPropertyName("accounts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<Account>? LegacyAccounts { get; set; }

    /// <summary>当前选中的账号 ID（对应 accounts.json 里账号的 UUID）。</summary>
    public string? SelectedAccountUuid { get; set; }

    /// <summary>已添加的外置登录认证服务器（默认带 LittleSkin）。</summary>
    public List<AuthServerEntry> AuthServers { get; set; } = new() { AuthServerEntry.LittleSkin() };

    // ---- 联机 ----
    /// <summary>樱花穿透访问密钥（玩家自备，只保存在本地设置里；落盘时由 DPAPI 加密）。</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string? FrpAccessKey { get; set; }

    /// <summary>樱花穿透隧道 ID。</summary>
    public string? FrpTunnelId { get; set; }

    /// <summary>陶瓦联机展示给其他玩家的名字；为空时用当前账号名。</summary>
    public string? TerracottaPlayerName { get; set; }

    // ---- 凭据（均不由仓库分发，缺失时界面引导）----
    /// <summary>微软登录用 Azure 应用客户端 ID。</summary>
    public string? MsClientId { get; set; }

    /// <summary>CurseForge API Key；落盘时由 DPAPI 加密。</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string? CurseForgeApiKey { get; set; }

    [JsonIgnore]
    public bool HasMsClientId => !string.IsNullOrWhiteSpace(MsClientId);
}
