using System.IO;
using System.Windows;
using MCLauncher.App.Shell;
using MCLauncher.App.ViewModels;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Logging;
using MCLauncher.Core.Services;
using MCLauncher.Core.Utils;

namespace MCLauncher.App;

/// <summary>
/// 应用入口：装配服务容器、挂日志、建壳并显示窗口。
/// （不使用 StartupUri，因为窗口需要注入 ViewModel。）
/// </summary>
public partial class App : Application
{
    public static ServiceContainer Services { get; } = new();

    /// <summary>运行时装配好的服务栈（窗口关闭时释放）。</summary>
    public static LauncherStack? Stack { get; private set; }

    /// <summary>文件日志 sink：普通日志是批量落盘的，退出前必须关掉才能把最后一批写进文件。</summary>
    private static FileLogSink? _fileSink;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _fileSink = new FileLogSink(GetLogDirectory());
        Log.Default.AttachSink(_fileSink);
        var log = Log.Default;

        // 未单独处理异常的异步命令统一落到日志，避免静默吞掉或崩进程。
        AsyncCommandErrors.Handler = exception => log.Error("界面命令执行失败", exception);

        SplashWindow? splash = null;

        try
        {
            log.Info("================ MCLauncher-Reborn 启动 ================");
            log.Info($"运行目录：{AppContext.BaseDirectory}");

            // ---- 配置 ----
            var store = new JsonConfigStore(GetConfigDirectory(), log);
            var config = new ConfigService(store, log);
            config.Load();
            config.EnsureKnownGameFolders(AppContext.BaseDirectory);
            ApplyFirstRunGameWindowSize(config, log);

            // 背景：默认状态下如果启动器目录已有 bgskin，就自动切到目录模式；不会主动创建该目录。
            var backgroundFolder = BackgroundPicker.FolderPath(AppContext.BaseDirectory);
            if (!config.Settings.BackgroundModeConfigured
                && config.Settings.BackgroundMode == BackgroundMode.None
                && Directory.Exists(backgroundFolder))
            {
                config.Settings.BackgroundMode = BackgroundMode.Folder;
                config.Settings.BackgroundModeConfigured = true;
                log.Info($"检测到 bgskin 目录，背景图切换为目录模式：{backgroundFolder}");
            }

            Services.RegisterSingleton<ILogger>(log);
            Services.RegisterSingleton<IConfigStore>(store);
            Services.RegisterSingleton(config);

            // ---- 业务服务栈 ----
            var stack = LauncherStack.Create(config, log);
            Stack = stack;
            Services.RegisterSingleton(stack);

            // 上一次自更新留下的 .old 备份在这里清掉（那时旧进程已经退出）。
            LauncherUpdater.CleanupLeftovers(log);
            var updater = new LauncherUpdater(stack.Downloader, log, AppContext.BaseDirectory);

            // 首次启动不再自动创建离线账号，交给初始化设置让用户自己添加。
            var setup = new SetupViewModel(stack, needsSetup: !config.Settings.SetupCompleted);
            config.Save();

            var account = stack.Accounts.Selected;
            log.Info(account is null ? "尚未添加账号，等待初始化设置" : $"当前账号：{account}");

            // 预热版本清单：失败不影响启动（内部已有缓存回退）。
            _ = PrefetchManifestAsync(stack, log);

            // ---- 页面与导航 ----
            var pageFactory = new PageFactory(stack);
            var homePage = pageFactory.Create(PageFactory.HomePageId);
            var crashOverlay = new CrashOverlayViewModel(stack);

            var shellViewModel = new ShellViewModel(
                PageFactory.NavItems,
                pageFactory.Create,
                homePage,
                config.Settings.WindowWidth,
                config.Settings.WindowHeight,
                crashOverlay,
                setup,
                updater);

            // 崩溃界面的「重新启动」：回到首页并用当前实例再启动一次。
            crashOverlay.RestartAction = () =>
            {
                shellViewModel.NavigateTo(PageFactory.HomePageId);
                if (homePage is HomeViewModel home)
                {
                    home.RelaunchSelected();
                }
            };

            // 启动时没有可用账号：直接弹出初始化设置里的「添加账户」窗口，加完账号自动收起。
            if (homePage is HomeViewModel launchHome)
            {
                launchHome.AccountRequired += (_, _) => setup.OpenAddAccountForLaunch();
            }

            stack.GameCrashed += (_, args) => crashOverlay.Show(args);

            pageFactory.AttachNavigator(shellViewModel.NavigateTo);
            pageFactory.AttachPageNavigator(shellViewModel.NavigateToPage);
            Services.RegisterSingleton(shellViewModel);

            var window = new MainWindow { DataContext = shellViewModel };

            // ---- 启动页：预取网络背景图、公告与更新；可随时跳过 ----
            // 启动阶段先不让主窗口成为「最后一个窗口」，否则启动页一关进程就退出。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            splash = new SplashWindow();
            splash.Show();

            using var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            splash.SkipClicked += (_, _) => startupCts.Cancel();

            var launcherInfo = new LauncherInfoService(stack.Downloader, log);

            var noticeTask = launcherInfo.FetchNoticeAsync(startupCts.Token);
            var updateTask = launcherInfo.CheckUpdateAsync(startupCts.Token);
            var backgroundTask = BackgroundPicker.PreloadNetworkAsync(
                config.Settings, stack.Downloader, log, startupCts.Token);

            // 三个预取并行进行，启动页按实际剩余的阶段刷新文案；
            // 不能连着调用 SetStatus，那样窗口渲染时只会剩下最后一句「正在准备主界面」。
            var startupStages = new (string Label, Task Task)[]
            {
                ("获取公告", noticeTask),
                ("检查更新", updateTask),
                ("加载背景图", backgroundTask),
            };
            await TrackStartupStagesAsync(splash, startupStages);

            splash.SetStatus("正在准备主界面…");
            await Task.WhenAll(noticeTask, updateTask, backgroundTask);

            ApplyStartupContent(shellViewModel, noticeTask.Result, updateTask.Result, log);

            // 启动阶段只使用预取结果：没拿到就保持默认底图，避免主窗口先默认后「换脸」。
            BackgroundPicker.Apply(
                window,
                config.Settings,
                AppContext.BaseDirectory,
                stack.Downloader,
                log,
                backgroundTask.Result,
                allowNetworkLoad: false);

            // 显式设置初始尺寸：Window.Width/Height 的绑定在 DataContext 赋值时序上不可靠，
            // 这里直接按配置赋初值，低于最小尺寸时钳到 720×450。
            window.Width = Math.Max(shellViewModel.InitialWidth, window.MinWidth);
            window.Height = Math.Max(shellViewModel.InitialHeight, window.MinHeight);

            log.Info("外壳已创建，准备显示窗口");
            window.Show();
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            splash.Close();
            log.Info($"窗口已显示：{window.ActualWidth}x{window.ActualHeight}");
        }
        catch (Exception ex)
        {
            log.Error("启动失败", ex);
            splash?.Close();
            MessageBox.Show(
                $"启动器启动失败：{ex.Message}{Environment.NewLine}{Environment.NewLine}详情见日志：{GetLogDirectory()}",
                "MCLauncher-Reborn",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services.TryResolve<ConfigService>(out var config) && config is not null)
        {
            config.Save();
        }

        Stack?.Dispose();
        Log.Default.Info("================ MCLauncher-Reborn 退出 ================");

        // 文件日志是批量的，这里关掉 sink 才能把缓冲区里的最后几行写完。
        _fileSink?.Dispose();
        _fileSink = null;

        base.OnExit(e);
    }

    /// <summary>
    /// 并行等待启动预取任务，每完成一个阶段就把启动页文案刷新成「还剩哪些阶段」，
    /// 让用户看到获取公告、检查更新、加载背景图这些真实步骤。
    /// </summary>
    private static async Task TrackStartupStagesAsync(
        SplashWindow splash,
        IReadOnlyList<(string Label, Task Task)> stages)
    {
        var pending = stages.ToList();
        splash.SetStatus(FormatPendingStages(pending));

        while (pending.Count > 0)
        {
            await Task.WhenAny(pending.Select(stage => stage.Task));

            // 几个阶段可能在同一时刻结束，已完成的都从待办里移除。
            pending.RemoveAll(stage => stage.Task.IsCompleted);
            if (pending.Count == 0)
            {
                break;
            }

            splash.SetStatus(FormatPendingStages(pending));
        }
    }

    private static string FormatPendingStages(IReadOnlyList<(string Label, Task Task)> pending) =>
        pending.Count == 1
            ? $"正在{pending[0].Label}…"
            : $"正在{string.Join("、", pending.Select(stage => stage.Label))}…";

    /// <summary>把启动页拿到的公告与更新显示到主界面。</summary>
    private static void ApplyStartupContent(
        ShellViewModel shell,
        LauncherNotice? notice,
        LauncherUpdate? update,
        ILogger log)
    {
        // 公告每次启动都显示，「知道了」只关闭本次；停用公告请把 notice.json 的 enabled 设为 false。
        if (notice is not null)
        {
            shell.ShowNotice(notice.Title, notice.Message ?? string.Empty, notice.Url);
            log.Info($"已显示启动公告：{notice.Id}");
        }

        var current = CurrentVersion();
        if (update is null || !LauncherInfoService.IsNewerVersion(update.Version, current))
        {
            return;
        }

        var detail = TrimForBanner(update.Notes) is { Length: > 0 } notes
            ? notes
            : TrimForBanner(update.Title);

        shell.ShowUpdate($"发现新版本 {update.Version}", detail, update.Url, update);

        log.Info($"发现启动器新版本：{update.Version}（当前 {current}）");
    }

    private static string CurrentVersion()
    {
        var version = typeof(App).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>发布说明是多行 Markdown，横幅只保留一段单行摘要。</summary>
    private static string TrimForBanner(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

        return normalized.Length <= 200 ? normalized : normalized[..200] + "…";
    }

    private static async Task PrefetchManifestAsync(LauncherStack stack, ILogger log)
    {
        try
        {
            await stack.Manifests.GetAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn("预热版本清单失败（不影响使用）", ex);
        }
    }

    /// <summary>设置页保存背景图配置后，立即刷新窗口背景。</summary>
    public static void RefreshBackground()
    {
        if (Current?.MainWindow is not MainWindow window
            || Stack is null
            || !Services.TryResolve<ConfigService>(out var config)
            || config is null)
        {
            return;
        }

        BackgroundPicker.Apply(window, config.Settings, AppContext.BaseDirectory, Stack.Downloader, Log.Default);
    }

    /// <summary>便携式配置目录：放在启动器所在目录，不再写入用户 AppData。</summary>
    private static string GetConfigDirectory() =>
        LauncherPaths.EnsureConfigDirectory(AppContext.BaseDirectory, Log.Default);

    /// <summary>
    /// 首次使用（还没有 settings.json）时，按主显示器分辨率挑一个合适的游戏窗口大小。
    /// 只执行这一次，之后一律以用户在设置里的调整为准。
    /// </summary>
    private static void ApplyFirstRunGameWindowSize(ConfigService config, ILogger log)
    {
        if (!config.IsFirstRun)
        {
            return;
        }

        var (screenWidth, screenHeight) = DisplayMetrics.PrimaryScreenSize();
        var (width, height) = DefaultWindowSize.Choose(screenWidth, screenHeight);

        config.Settings.DefaultWidth = width;
        config.Settings.DefaultHeight = height;

        log.Info(screenWidth > 0 && screenHeight > 0
            ? $"首次使用：按显示器 {screenWidth}x{screenHeight} 自动设定游戏窗口 {width}x{height}"
            : $"首次使用：取不到显示器分辨率，游戏窗口使用 {width}x{height}");
    }

    private static string GetLogDirectory() => Path.Combine(GetConfigDirectory(), "logs");
}
