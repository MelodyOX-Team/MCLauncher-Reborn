using System.Collections.Generic;
using MCLauncher.App.ViewModels;

namespace MCLauncher.App.Shell;

/// <summary>
/// 页面注册表：导航项与页面 ViewModel 的唯一映射处。
/// 新增页面时只需改这里 + 加一对 View/ViewModel。
/// </summary>
public sealed class PageFactory
{
    public const string HomePageId = "home";
    public const string VersionsPageId = "versions";
    public const string AccountsPageId = "accounts";
    public const string ConnectPageId = "connect";
    public const string LaunchLogPageId = "log";
    public const string SettingsPageId = "settings";

    /// <summary>实例管理页不放在顶部导航里，只能从实例列表进入。</summary>
    public const string InstanceManagePageId = "instance-manage";

    private readonly LauncherStack _stack;
    private Action<string>? _navigate;
    private Action<string, string>? _pageNavigate;

    // 页面由 ShellViewModel 缓存，这里再持有一份引用，方便跨页面跳转时直接调用目标页的方法。
    private HomeViewModel? _home;
    private SettingsViewModel? _settings;
    private VersionsViewModel? _versions;
    private InstanceManageViewModel? _instanceManage;
    private ConnectViewModel? _connect;

    public PageFactory(LauncherStack stack)
    {
        _stack = stack;
    }

    public static IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem(HomePageId, "首页", "\uE80F"),
        new NavItem(AccountsPageId, "账号", "\uE77B"),
        new NavItem(VersionsPageId, "实例列表", "\uE896"),
        new NavItem(ConnectPageId, "联机", "\uE774"),
        new NavItem(LaunchLogPageId, "启动日志", "\uE9D9"),
        new NavItem(SettingsPageId, "设置", "\uE713"),
    };

    /// <summary>
    /// 壳创建好之后把导航回调接进来。
    /// 各页面里的跳转按钮通过 lambda 调用 <see cref="Navigate(string)"/>，
    /// 因此这里晚一点注入也来得及。
    /// </summary>
    public void AttachNavigator(Action<string> navigate) => _navigate = navigate;

    /// <summary>用于打开不在顶部导航里的页面（实例管理）。</summary>
    public void AttachPageNavigator(Action<string, string> navigate) => _pageNavigate = navigate;

    public void Navigate(string pageId) => _navigate?.Invoke(pageId);

    public object Create(string pageId) => pageId switch
    {
        VersionsPageId => Versions,
        AccountsPageId => new AccountsViewModel(_stack),
        ConnectPageId => Connect,
        LaunchLogPageId => new LaunchLogViewModel(_stack),
        SettingsPageId => Settings,
        InstanceManagePageId => InstanceManage,
        _ => Home,
    };

    /// <summary>首页设置按钮 / 实例列表「实例设置」：打开实例管理页并在右侧显示游戏设置。</summary>
    public void OpenInstanceSettings(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            Navigate(VersionsPageId);
            return;
        }

        InstanceManage.ShowSettings(versionId);
        _pageNavigate?.Invoke(InstanceManagePageId, $"实例管理 · {versionId}");
    }

    /// <summary>设置页现在只包含全局设置。</summary>
    public void OpenGlobalSettings() => Navigate(SettingsPageId);

    /// <summary>实例页的「启动」：回到首页并立即启动该实例。</summary>
    public void LaunchFromVersions(string versionId)
    {
        var home = Home;
        Navigate(HomePageId);
        home.SelectAndLaunch(versionId);
    }

    /// <summary>服务器管理的「加入」：回到首页、启动该实例并直接进入服务器。</summary>
    public void LaunchWithServer(string versionId, string host, int port)
    {
        var home = Home;
        Navigate(HomePageId);
        home.SelectAndLaunchServer(versionId, host, port);
    }

    /// <summary>首页右下角「安装新实例」：切到实例列表并打开安装新游戏弹窗。</summary>
    public void OpenInstallNewGame()
    {
        var versions = Versions;
        Navigate(VersionsPageId);
        versions.OpenInstallCommand.Execute(null);
    }

    /// <summary>实例列表的「实例管理」：打开模组 / 资源包 / 世界等管理页。</summary>
    public void OpenInstanceManage(string versionId)
    {
        InstanceManage.Show(versionId);
        _pageNavigate?.Invoke(InstanceManagePageId, $"实例管理 · {versionId}");
    }

    /// <summary>把文件拖进窗口且被识别为整合包：切到实例列表并打开安装确认。</summary>
    public void OpenModpackFromDrop(string archivePath)
    {
        var versions = Versions;
        Navigate(VersionsPageId);
        _ = versions.OpenDroppedModpackAsync(archivePath);
    }

    private HomeViewModel Home =>
        _home ??= new HomeViewModel(
            _stack, OpenInstanceSettings, () => Navigate(AccountsPageId), OpenInstallNewGame);

    private SettingsViewModel Settings => _settings ??= new SettingsViewModel(_stack);

    private ConnectViewModel Connect => _connect ??= new ConnectViewModel(_stack, LaunchWithServer);

    private VersionsViewModel Versions => _versions ??= new VersionsViewModel(
        _stack, OpenGlobalSettings, OpenInstanceSettings, LaunchFromVersions, OpenInstanceManage);

    private InstanceManageViewModel InstanceManage
    {
        get
        {
            if (_instanceManage is null)
            {
                _instanceManage = new InstanceManageViewModel(
                    _stack, () => Navigate(VersionsPageId), LaunchWithServer);
                _instanceManage.InstanceRenamed += id =>
                    _pageNavigate?.Invoke(InstanceManagePageId, $"实例管理 · {id}");
            }

            return _instanceManage;
        }
    }
}
