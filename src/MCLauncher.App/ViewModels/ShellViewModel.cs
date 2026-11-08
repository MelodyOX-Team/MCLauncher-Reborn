using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services;

namespace MCLauncher.App.ViewModels;

/// <summary>导航项。</summary>
public sealed class NavItem
{
    public NavItem(string id, string title, string glyph)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>图标字形（Segoe MDL2 Assets 或普通字符）。</summary>
    public string Glyph { get; }

    /// <summary>列表项的自动化名称；屏幕阅读器与 UI 测试都依赖它。</summary>
    public override string ToString() => Title;
}

/// <summary>
/// 外壳 ViewModel：负责导航与状态栏文字。
/// </summary>
public sealed class ShellViewModel : ViewModelBase
{
    private readonly Dictionary<string, object> _pageCache = new();
    private readonly Func<string, object> _pageFactory;
    private object? _currentPage;
    private NavItem? _selectedNavItem;
    private string _statusText = "就绪";

    private bool _hasNotice;
    private string _noticeTitle = string.Empty;
    private string _noticeMessage = string.Empty;
    private string? _noticeUrl;
    private Action? _noticeDismiss;

    private bool _hasUpdate;
    private string _updateTitle = string.Empty;
    private string _updateMessage = string.Empty;
    private string? _updateUrl;

    private readonly LauncherUpdater? _updater;
    private LauncherUpdate? _updateSource;
    private bool _updateReady;
    private bool _isUpdateDrawerOpen;

    public ShellViewModel(
        IEnumerable<NavItem> navItems,
        Func<string, object> pageFactory,
        object initialPage,
        int initialWidth,
        int initialHeight,
        CrashOverlayViewModel crash,
        SetupViewModel setup,
        LauncherUpdater? updater = null)
    {
        _pageFactory = pageFactory;
        NavItems = new ObservableCollection<NavItem>(navItems);
        _currentPage = initialPage;
        _selectedNavItem = NavItems.FirstOrDefault();
        _statusText = _selectedNavItem?.Title ?? "就绪";
        InitialWidth = initialWidth;
        InitialHeight = initialHeight;
        Crash = crash;
        Setup = setup;
        _updater = updater;

        OpenNoticeCommand = new RelayCommand(() => OpenUrl(_noticeUrl));
        DismissNoticeCommand = new RelayCommand(DismissNotice);
        UpdateActionCommand = new AsyncRelayCommand(RunUpdateAsync, () => _hasUpdate);
        CollapseUpdateDrawerCommand = new RelayCommand(() => IsUpdateDrawerOpen = false);
        OpenUpdateDrawerCommand = new RelayCommand(() => IsUpdateDrawerOpen = true);
    }

    /// <summary>全局崩溃界面，盖在所有页面之上。</summary>
    public CrashOverlayViewModel Crash { get; }

    /// <summary>首次启动的初始化设置，未完成时盖在主界面上。</summary>
    public SetupViewModel Setup { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    public int InitialWidth { get; }

    public int InitialHeight { get; }

    /// <summary>当前页面 ViewModel。</summary>
    public object? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(IsHomePage));
            }
        }
    }

    /// <summary>当前停在首页：首页不铺白色底板，直接露出背景图。</summary>
    public bool IsHomePage => _currentPage is HomeViewModel;

    /// <summary>选中的导航项；赋值即切页。</summary>
    public NavItem? SelectedNavItem
    {
        get => _selectedNavItem;
        set
        {
            if (!SetProperty(ref _selectedNavItem, value) || value is null)
            {
                return;
            }

            Navigate(value);
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    // ---------------------------------------------------------------- 启动公告 / 更新抽屉

    public bool HasNotice
    {
        get => _hasNotice;
        private set
        {
            if (SetProperty(ref _hasNotice, value))
            {
                OnPropertyChanged(nameof(HasNoticeUrl));
            }
        }
    }

    public string NoticeTitle
    {
        get => _noticeTitle;
        private set => SetProperty(ref _noticeTitle, value);
    }

    public string NoticeMessage
    {
        get => _noticeMessage;
        private set => SetProperty(ref _noticeMessage, value);
    }

    public bool HasNoticeUrl => _hasNotice && !string.IsNullOrWhiteSpace(_noticeUrl);

    public bool HasUpdate
    {
        get => _hasUpdate;
        private set
        {
            if (SetProperty(ref _hasUpdate, value))
            {
                OnPropertyChanged(nameof(IsUpdateDrawerCollapsed));
            }
        }
    }

    public string UpdateTitle
    {
        get => _updateTitle;
        private set => SetProperty(ref _updateTitle, value);
    }

    public string UpdateMessage
    {
        get => _updateMessage;
        private set => SetProperty(ref _updateMessage, value);
    }

    /// <summary>更新抽屉是否展开：展开时从右侧滑出，收起后只在右边缘留一个小方块。</summary>
    public bool IsUpdateDrawerOpen
    {
        get => _isUpdateDrawerOpen;
        private set
        {
            if (SetProperty(ref _isUpdateDrawerOpen, value))
            {
                OnPropertyChanged(nameof(IsUpdateDrawerCollapsed));
            }
        }
    }

    /// <summary>抽屉已收起（有更新、但当前是收起态）。</summary>
    public bool IsUpdateDrawerCollapsed => _hasUpdate && !_isUpdateDrawerOpen;

    public ICommand OpenNoticeCommand { get; }

    public ICommand DismissNoticeCommand { get; }

    /// <summary>点抽屉正文：下载更新；已下载则替换并重启；没有 exe 附件时打开 Release 页面。</summary>
    public ICommand UpdateActionCommand { get; }

    /// <summary>点抽屉左侧的「&gt;」：缩回右边缘。</summary>
    public ICommand CollapseUpdateDrawerCommand { get; }

    /// <summary>点右边缘小方块的「&lt;」：把抽屉抽出来。</summary>
    public ICommand OpenUpdateDrawerCommand { get; }

    /// <summary>显示公告横幅；<paramref name="onDismiss"/> 用于记住「不再显示」。</summary>
    public void ShowNotice(string title, string message, string? url, Action? onDismiss = null)
    {
        NoticeTitle = title;
        NoticeMessage = message;
        _noticeUrl = url;
        _noticeDismiss = onDismiss;
        HasNotice = true;
    }

    public void DismissNotice()
    {
        if (!_hasNotice)
        {
            return;
        }

        HasNotice = false;
        var dismiss = _noticeDismiss;
        _noticeDismiss = null;
        dismiss?.Invoke();
    }

    /// <summary>显示更新抽屉；<paramref name="source"/> 带附件信息时支持原地自更新。</summary>
    public void ShowUpdate(
        string title,
        string message,
        string? url,
        LauncherUpdate? source = null)
    {
        UpdateTitle = title;
        UpdateMessage = message;
        _updateUrl = url;
        _updateSource = source;
        HasUpdate = true;
        IsUpdateDrawerOpen = true;

        // 命令是在构造时创建的，那时还没有更新；这里要主动让按钮重新按 CanExecute 求值。
        (UpdateActionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        _ = CollapseUpdateDrawerLaterAsync();
    }

    /// <summary>启动时展示 5 秒，然后自动缩回右侧边缘。</summary>
    private async Task CollapseUpdateDrawerLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        if (_hasUpdate)
        {
            IsUpdateDrawerOpen = false;
        }
    }

    /// <summary>点抽屉正文：下载新版本；下载完成后再点一次替换并重启。</summary>
    private async Task RunUpdateAsync()
    {
        if (_updateSource is null)
        {
            return;
        }

        if (_updateReady && _updater is not null)
        {
            try
            {
                _updater.Apply();
                LauncherUpdater.Restart();
                Application.Current?.Shutdown();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                UpdateMessage = $"替换失败：{ex.Message}，可到 Release 页面手动下载";
                throw;
            }

            return;
        }

        var asset = _updateSource.ExecutableAsset;
        if (asset is null || _updater is null)
        {
            // 没有可替换的 exe 附件（或不是单文件 exe 启动）：退回打开 Release 页面。
            OpenUrl(_updateUrl);
            return;
        }

        UpdateMessage = "正在下载更新…";
        var progress = new Progress<DownloadProgress>(p => UpdateMessage = p.Fraction is { } fraction
            ? $"正在下载更新… {fraction:P0}"
            : $"正在下载更新… {p.BytesReceived / 1024.0 / 1024.0:0.0} MB");

        var staged = await _updater.DownloadAsync(asset, progress).ConfigureAwait(true);
        if (staged is null)
        {
            UpdateMessage = "下载失败，点抽屉可到 Release 页面手动下载";
            return;
        }

        _updateReady = true;
        UpdateMessage = "已下载，再点一次完成替换并重启";
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or ArgumentException)
        {
            Debug.WriteLine($"打开链接失败：{url}（{ex.Message}）");
        }
    }

    public ICommand NavigateCommand => new RelayCommand<object>(param =>
    {
        if (param is NavItem item)
        {
            SelectedNavItem = item;
        }
    });

    /// <summary>切换到指定 id 的页面（带缓存，保证页面状态不丢失）。</summary>
    public void NavigateTo(string pageId)
    {
        var item = NavItems.FirstOrDefault(n => n.Id == pageId);
        if (item is not null)
        {
            SelectedNavItem = item;
        }
    }

    /// <summary>
    /// 打开不在顶部导航里的页面（如实例管理）：清空导航选中项，仍复用页面缓存。
    /// </summary>
    public void NavigateToPage(string pageId, string title)
    {
        RefreshBackgroundIfLeavingSettings();

        if (!_pageCache.TryGetValue(pageId, out var page))
        {
            page = _pageFactory(pageId);
            _pageCache[pageId] = page;
        }

        if (_selectedNavItem is not null)
        {
            _selectedNavItem = null;
            OnPropertyChanged(nameof(SelectedNavItem));
        }

        CurrentPage = page;
        StatusText = title;
    }

    private void Navigate(NavItem item)
    {
        RefreshBackgroundIfLeavingSettings();

        if (!_pageCache.TryGetValue(item.Id, out var page))
        {
            page = _pageFactory(item.Id);
            _pageCache[item.Id] = page;
        }

        CurrentPage = page;
        StatusText = item.Title;
    }

    /// <summary>
    /// 离开设置页时，如果改过背景图设置就立即重新应用窗口背景，不用等保存或重启。
    /// </summary>
    private void RefreshBackgroundIfLeavingSettings()
    {
        if (_currentPage is not SettingsViewModel settings || !settings.IsBackgroundDirty)
        {
            return;
        }

        App.RefreshBackground();
        settings.ClearBackgroundDirty();
        Debug.WriteLine("背景图设置已修改，离开设置页时已立即应用");
    }
}
