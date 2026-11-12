using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Logging;
using MCLauncher.Core.Models;
using MCLauncher.Core.Services.Auth;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>一次启动请求。</summary>
public sealed class GameLaunchRequest
{
    /// <summary>游戏根目录（<c>.minecraft</c>）。</summary>
    public required string GameRoot { get; init; }

    public required string VersionId { get; init; }

    public required Account Account { get; init; }

    public LaunchOptions Options { get; init; } = new();

    /// <summary>是否版本隔离（true 时游戏目录为 <c>versions/&lt;id&gt;</c>，mods/存档都在里面）。</summary>
    public bool Isolated { get; init; }

    /// <summary>指定 Java；为空时自动按版本要求挑选。</summary>
    public JavaInfo? JavaOverride { get; init; }

    /// <summary>启动器配置目录（外置登录时缓存 authlib-injector）。</summary>
    public string? AuthlibCacheDirectory { get; init; }
}

/// <summary>
/// 「启动游戏」按钮背后的完整流程：补全文件 → 选 Java → 拼参数 → 起进程。
/// 把这条链放在 Core 里，界面层只剩一次调用。
/// </summary>
public sealed class GameLaunchService
{
    private readonly VersionRepository _repository;
    private readonly GameInstaller _installer;
    private readonly JavaService _javaService;
    private readonly LaunchArgumentBuilder _argumentBuilder;
    private readonly GameLauncher _launcher;
    private readonly AuthlibAuthService _authlibAuth;
    private readonly AuthlibInjectorService _authlibInjector;
    private readonly ILogger _log;

    public GameLaunchService(
        VersionRepository repository,
        GameInstaller installer,
        JavaService javaService,
        LaunchArgumentBuilder argumentBuilder,
        GameLauncher launcher,
        AuthlibAuthService authlibAuth,
        AuthlibInjectorService authlibInjector,
        ILogger log)
    {
        _repository = repository;
        _installer = installer;
        _javaService = javaService;
        _argumentBuilder = argumentBuilder;
        _launcher = launcher;
        _authlibAuth = authlibAuth;
        _authlibInjector = authlibInjector;
        _log = log;
    }

    public async Task<GameProcess> LaunchAsync(
        GameLaunchRequest request,
        LaunchLogBuffer logBuffer,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!_repository.IsInstalled(request.GameRoot, request.VersionId))
        {
            throw new InvalidOperationException($"本地没有安装版本 {request.VersionId}，请先安装");
        }

        if (request.Options.CompleteBeforeLaunch)
        {
            var report = await _installer
                .EnsureCompleteAsync(
                    request.GameRoot,
                    request.VersionId,
                    new InstallOptions { DownloadAssets = true, ReportAsCheck = true },
                    progress,
                    ct)
                .ConfigureAwait(false);

            if (!report.Success)
            {
                throw new InvalidOperationException(
                    $"启动前补全文件失败（{report.Failed} 个文件）：{string.Join("；", report.Failures.Take(5).Select(f => f.Name))}");
            }
        }

        var version = _repository.Resolve(request.GameRoot, request.VersionId);
        var jarId = VersionRepository.ResolveJarId(version);
        var clientJarPath = VersionRepository.VersionJarPath(request.GameRoot, jarId);

        if (!File.Exists(clientJarPath))
        {
            throw new FileNotFoundException($"客户端 jar 缺失：{clientJarPath}");
        }

        var indexInfo = version.AssetIndex;
        var index = indexInfo is null ? null : _installer.TryLoadAssetIndex(request.GameRoot, indexInfo);
        var assetsRoot = GameInstaller.ResolveAssetsRoot(request.GameRoot, indexInfo, index);

        var java = request.JavaOverride ?? _javaService.SelectBest(version.RequiredJavaMajor);
        if (java is null)
        {
            throw new InvalidOperationException(
                $"没有找到可用的 Java {version.RequiredJavaMajor}（64 位）。请在设置页指定 Java 路径，或安装对应版本。");
        }

        var logConfigPath = version.Logging?.Client?.File?.Id is { Length: > 0 } logId
            ? Path.Combine(GameInstaller.LogConfigDirectory(request.GameRoot), logId)
            : null;

        if (logConfigPath is not null && !File.Exists(logConfigPath))
        {
            logConfigPath = null;
        }

        // 外置登录：准备 authlib-injector 与认证服务器元数据。
        string? authlibJar = null;
        string? authlibPrefetched = null;
        if (request.Account.IsExternal)
        {
            if (string.IsNullOrWhiteSpace(request.Account.AuthServer))
            {
                throw new InvalidOperationException("外置登录账号缺少认证服务器地址，请重新登录");
            }

            if (string.IsNullOrWhiteSpace(request.AuthlibCacheDirectory))
            {
                throw new InvalidOperationException("缺少启动器配置目录，无法准备外置登录组件");
            }

            progress?.Report(new InstallProgress { Stage = "准备外置登录组件" });
            authlibJar = await _authlibInjector
                .EnsureJarAsync(request.AuthlibCacheDirectory, ct)
                .ConfigureAwait(false);

            try
            {
                var metadata = await _authlibAuth
                    .FetchMetadataAsync(request.Account.AuthServer, ct)
                    .ConfigureAwait(false);

                authlibPrefetched = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(metadata.RawJson));
            }
            catch (Exception ex)
            {
                // 预取只是加速；失败时让 authlib-injector 自己拉取。
                _log.Warn("预取认证服务器元数据失败，将由游戏内自行获取", ex);
            }
        }

        var context = new LaunchContext
        {
            Version = version,
            Account = request.Account,
            Options = request.Options,
            JavaPath = java.Path,
            VersionId = request.VersionId,
            GameDirectory = request.Isolated
                ? VersionRepository.VersionDirectory(request.GameRoot, request.VersionId)
                : request.GameRoot,
            WorkingDirectory = ResolveWorkingDirectory(request),
            AssetsRoot = assetsRoot,
            NativesDirectory = VersionRepository.NativesDirectory(request.GameRoot, request.VersionId),
            LibrariesDirectory = GameInstaller.LibrariesDirectory(request.GameRoot),
            ClientJarPath = clientJarPath,
            AssetsIndexName = indexInfo?.Id,
            LogConfigPath = logConfigPath,
            AuthlibInjectorJar = authlibJar,
            AuthlibServer = request.Account.AuthServer,
            AuthlibPrefetchedMetadata = authlibPrefetched,
        };

        if (request.Isolated)
        {
            PathUtil.EnsureDirectory(context.GameDirectory);
        }

        ApplyFullscreenOption(context.GameDirectory, request.Options.WindowMode == GameWindowMode.Fullscreen);

        var command = _argumentBuilder.Build(context);
        _log.Info($"使用 Java {java.MajorVersion}（{java.Path}）启动 {request.VersionId}");

        return _launcher.Start(command, logBuffer, java.MajorVersion);
    }

    /// <summary>
    /// 规范游戏进程的工作目录：相对路径按游戏根目录解析，目录不存在时自动创建。
    /// 留空则返回 null，由参数构建器回退到实例目录。
    /// </summary>
    private static string? ResolveWorkingDirectory(GameLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Options.WorkingDirectory))
        {
            return null;
        }

        var value = request.Options.WorkingDirectory!.Trim();
        var path = Path.IsPathRooted(value) ? value : Path.Combine(request.GameRoot, value);

        try
        {
            path = Path.GetFullPath(path);
            PathUtil.EnsureDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            throw new InvalidOperationException($"游戏运行路径不可用：{path}", ex);
        }
    }

    /// <summary>
    /// 把窗口类型落到 <c>options.txt</c> 的 fullscreen 开关：全屏为 true，窗口化/最大化为 false。
    /// 其它行原样保留，避免覆盖玩家的按键、音量等设置。
    /// </summary>
    private void ApplyFullscreenOption(string gameDirectory, bool fullscreen)
    {
        try
        {
            var path = Path.Combine(gameDirectory, "options.txt");
            var lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : new List<string>();

            var value = "fullscreen:" + (fullscreen ? "true" : "false");
            var index = lines.FindIndex(line => line.StartsWith("fullscreen:", StringComparison.Ordinal));

            if (index >= 0)
            {
                if (string.Equals(lines[index], value, StringComparison.Ordinal))
                {
                    return;
                }

                lines[index] = value;
            }
            else
            {
                lines.Add(value);
            }

            File.WriteAllLines(path, lines);
        }
        catch (Exception ex)
        {
            // 写不进去也不影响启动，最多是不按用户选择的窗口类型显示。
            _log.Warn($"写入 options.txt 的 fullscreen 开关失败：{gameDirectory}", ex);
        }
    }
}
