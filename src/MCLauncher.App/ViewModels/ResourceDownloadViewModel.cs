using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services;
using MCLauncher.Core.Utils;
using Microsoft.Win32;

namespace MCLauncher.App.ViewModels;

/// <summary>
/// 资源下载对话框：检索 → 项目详情 → 版本选择 → 前置提示 → 安装 / 另存。
/// <para>
/// 详情页参考 HMCL：按当前实例的游戏版本与加载器给出一条推荐版本，
/// 其余版本按 Minecraft 版本分组折叠；前置内容分必需 / 可选，点击可跳转。
/// </para>
/// </summary>
public sealed class ResourceDownloadViewModel : ViewModelBase
{
    private const int PageSize = 20;
    private const string AllVersions = "全部";

    /// <summary>详情页的一次快照，用于从前置项目返回。</summary>
    private sealed class DetailSnapshot
    {
        public required ResourceProjectSummary Project { get; init; }

        public required IReadOnlyList<ResourceVersionInfo> Versions { get; init; }
    }

    private readonly LauncherStack _stack;
    private readonly AsyncRelayCommand _searchCommand;
    private readonly AsyncRelayCommand<DownloadProjectRow> _downloadCommand;
    private readonly AsyncRelayCommand<DownloadProjectRow> _openDetailCommand;
    private readonly AsyncRelayCommand<DownloadDependencyRow> _openDependencyCommand;
    private readonly AsyncRelayCommand _confirmFileNameCommand;
    private readonly RelayCommand _previousPageCommand;
    private readonly RelayCommand _nextPageCommand;
    private readonly RelayCommand _installCommand;
    private readonly RelayCommand _downloadToFolderCommand;

    private readonly Stack<DetailSnapshot> _detailHistory = new();
    private DetailSnapshot? _currentDetail;

    private ResourceKind _kind = ResourceKind.Mod;
    private string _instanceId = string.Empty;
    private string _instanceDirectory = string.Empty;
    private string? _gameVersion;
    private string? _loaderName;

    private bool _isOpen;
    private bool _isBusy;
    private bool _isDownloading;
    private bool _isSearchMode = true;
    private bool _initializingSource;
    private bool _suppressFilterReload;
    private CancellationTokenSource? _filterReloadCts;
    private string _title = "下载";
    private string _statusText = string.Empty;
    private string _searchText = string.Empty;
    private string? _selectedGameVersion;
    private DownloadSourceOption? _selectedSource;
    private DownloadSortOption? _selectedSort;
    private int _pageIndex;
    private int _totalPages;
    private int _totalCount;
    private double _downloadProgress;
    private string _downloadSpeedText = string.Empty;
    private readonly DownloadFileRows _downloadRows = new();

    private string _detailName = string.Empty;
    private string _detailSummary = string.Empty;
    private string _detailMetaText = string.Empty;
    private string _detailStatusText = string.Empty;
    private IReadOnlyList<string> _detailCategories = Array.Empty<string>();
    private ImageSource? _detailIcon;
    private string _recommendedHeader = "推荐版本";
    private bool _hasRecommended;

    private bool _isVersionDialogOpen;
    private bool _isResolvingDependencies;
    private DownloadVersionRow? _dialogVersion;
    private ResourceVersionInfo? _pendingVersion;
    private string _versionDialogTitle = "下载";
    private string _versionDialogMeta = string.Empty;
    private string _versionDialogStatus = string.Empty;

    private bool _isFileNameDialogOpen;
    private string _fileNameInput = string.Empty;
    private string _fileNameError = string.Empty;
    private bool _canConfirmFileName;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _resolveCts;

    public ResourceDownloadViewModel(LauncherStack stack)
    {
        _stack = stack;

        _searchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy);
        _downloadCommand = new AsyncRelayCommand<DownloadProjectRow>(
            DownloadAsync,
            row => !IsBusy && row is not null);
        _openDetailCommand = new AsyncRelayCommand<DownloadProjectRow>(
            OpenDetailAsync,
            row => !IsBusy && row is not null);
        _openDependencyCommand = new AsyncRelayCommand<DownloadDependencyRow>(
            OpenDependencyAsync,
            row => !IsBusy && row?.Dependency.Project is not null);
        _confirmFileNameCommand = new AsyncRelayCommand(
            ConfirmFileNameAsync,
            () => CanConfirmFileName);
        _previousPageCommand = new RelayCommand(
            () => _ = LoadPageAsync(_pageIndex - 1),
            () => !IsBusy && _pageIndex > 0);
        _nextPageCommand = new RelayCommand(
            () => _ = LoadPageAsync(_pageIndex + 1),
            () => !IsBusy && _pageIndex + 1 < _totalPages);
        _installCommand = new RelayCommand(
            InstallToInstance,
            () => _pendingVersion is not null && !IsDownloading);
        _downloadToFolderCommand = new RelayCommand(
            DownloadToFolder,
            () => _pendingVersion is not null && !IsDownloading);

        SearchCommand = _searchCommand;
        DownloadCommand = _downloadCommand;
        OpenDetailCommand = _openDetailCommand;
        OpenDependencyCommand = _openDependencyCommand;
        ConfirmFileNameCommand = _confirmFileNameCommand;
        PreviousPageCommand = _previousPageCommand;
        NextPageCommand = _nextPageCommand;
        InstallToInstanceCommand = _installCommand;
        DownloadToFolderCommand = _downloadToFolderCommand;
        SelectVersionCommand = new RelayCommand<DownloadVersionRow>(SelectVersion, row => row is not null);
        BackCommand = new RelayCommand(GoBack);
        CloseCommand = new RelayCommand(Close);
        CancelVersionDialogCommand = new RelayCommand(CancelVersionDialog);
        CancelFileNameCommand = new RelayCommand(() => IsFileNameDialogOpen = false);

        RebuildSortOptions();
    }

    /// <summary>下载完成后通知实例管理页刷新列表。</summary>
    public event Action? ResourceDownloaded;

    /// <summary>整合包下载完成：把压缩包路径交给实例页走导入流程。</summary>
    public event Action<string>? ModpackDownloaded;

    /// <summary>整合包不是装进当前实例，而是创建一个独立实例。</summary>
    public bool IsModpackMode => _kind == ResourceKind.Modpack;

    /// <summary>版本弹窗里的主按钮文案：整合包语义是「下载并安装成新实例」。</summary>
    public string InstallButtonText => IsModpackMode ? "下载并安装" : "安装到当前实例";

    public ObservableCollection<DownloadProjectRow> Results { get; } = new();

    public ObservableCollection<string> GameVersionOptions { get; } = new();

    public ObservableCollection<DownloadSourceOption> Sources { get; } = new();

    public ObservableCollection<DownloadSortOption> SortOptions { get; } = new();

    public ObservableCollection<DownloadVersionRow> RecommendedVersions { get; } = new();

    public ObservableCollection<DownloadVersionGroup> VersionGroups { get; } = new();

    public ObservableCollection<DownloadDependencyRow> RequiredDependencies { get; } = new();

    public ObservableCollection<DownloadDependencyRow> OptionalDependencies { get; } = new();

    public ICommand SearchCommand { get; }

    public ICommand DownloadCommand { get; }

    public ICommand OpenDetailCommand { get; }

    public ICommand OpenDependencyCommand { get; }

    public ICommand ConfirmFileNameCommand { get; }

    public ICommand PreviousPageCommand { get; }

    public ICommand NextPageCommand { get; }

    public ICommand InstallToInstanceCommand { get; }

    public ICommand DownloadToFolderCommand { get; }

    public ICommand SelectVersionCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand CancelVersionDialogCommand { get; }

    public ICommand CancelFileNameCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>检索列表或项目详情。两者互斥。</summary>
    public bool IsSearchMode
    {
        get => _isSearchMode;
        private set
        {
            if (SetProperty(ref _isSearchMode, value))
            {
                OnPropertyChanged(nameof(IsDetailMode));
            }
        }
    }

    public bool IsDetailMode => !IsSearchMode;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string SubtitleText => SelectedSource is null ? string.Empty : "从 " + SelectedSource.Label + " 检索并下载";

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string? SelectedGameVersion
    {
        get => _selectedGameVersion;
        set
        {
            if (!SetProperty(ref _selectedGameVersion, value))
            {
                return;
            }

            OnFilterChanged();
        }
    }

    public DownloadSourceOption? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SubtitleText));
            RebuildSortOptions();

            if (IsOpen && !_initializingSource)
            {
                _ = LoadPageAsync(0);
            }
        }
    }

    public DownloadSortOption? SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (!SetProperty(ref _selectedSort, value))
            {
                return;
            }

            OnFilterChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanPage));
            _searchCommand.RaiseCanExecuteChanged();
            _downloadCommand.RaiseCanExecuteChanged();
            _openDetailCommand.RaiseCanExecuteChanged();
            _openDependencyCommand.RaiseCanExecuteChanged();
            _previousPageCommand.RaiseCanExecuteChanged();
            _nextPageCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (!SetProperty(ref _isDownloading, value))
            {
                return;
            }

            _installCommand.RaiseCanExecuteChanged();
            _downloadToFolderCommand.RaiseCanExecuteChanged();
        }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    /// <summary>资源下载时的实时速度文案（底部进度条与版本弹窗共用）。</summary>
    public string DownloadSpeedText
    {
        get => _downloadSpeedText;
        private set => SetProperty(ref _downloadSpeedText, value);
    }

    /// <summary>资源下载时的文件队列（单文件时只有一行）。</summary>
    public ObservableCollection<InstallFileItem> DownloadFiles => _downloadRows.Items;

    public bool CanPage => !IsBusy && Results.Count > 0;

    /// <summary>世界存档只有 CurseForge 一个源，选择器不可用。</summary>
    public bool CanSelectSource => Sources.Count > 1;

    public string PageText => _totalCount == 0
        ? "暂无结果"
        : $"第 {_pageIndex + 1} / {Math.Max(_totalPages, 1)} 页 · 共 {_totalCount} 个";

    // ---------------------------------------------------------------- 详情页

    public string DetailName
    {
        get => _detailName;
        private set => SetProperty(ref _detailName, value);
    }

    public string DetailSummary
    {
        get => _detailSummary;
        private set => SetProperty(ref _detailSummary, value);
    }

    public string DetailMetaText
    {
        get => _detailMetaText;
        private set => SetProperty(ref _detailMetaText, value);
    }

    public string DetailStatusText
    {
        get => _detailStatusText;
        private set => SetProperty(ref _detailStatusText, value);
    }

    public IReadOnlyList<string> DetailCategories
    {
        get => _detailCategories;
        private set => SetProperty(ref _detailCategories, value);
    }

    public ImageSource? DetailIcon
    {
        get => _detailIcon;
        private set
        {
            if (SetProperty(ref _detailIcon, value))
            {
                OnPropertyChanged(nameof(HasDetailIcon));
            }
        }
    }

    public bool HasDetailIcon => DetailIcon is not null;

    public string RecommendedHeader
    {
        get => _recommendedHeader;
        private set => SetProperty(ref _recommendedHeader, value);
    }

    public bool HasRecommended
    {
        get => _hasRecommended;
        private set => SetProperty(ref _hasRecommended, value);
    }

    // ---------------------------------------------------------------- 版本下载弹窗

    public bool IsVersionDialogOpen
    {
        get => _isVersionDialogOpen;
        private set => SetProperty(ref _isVersionDialogOpen, value);
    }

    public bool IsResolvingDependencies
    {
        get => _isResolvingDependencies;
        private set => SetProperty(ref _isResolvingDependencies, value);
    }

    public DownloadVersionRow? DialogVersion
    {
        get => _dialogVersion;
        private set => SetProperty(ref _dialogVersion, value);
    }

    public string VersionDialogTitle
    {
        get => _versionDialogTitle;
        private set => SetProperty(ref _versionDialogTitle, value);
    }

    public string VersionDialogMeta
    {
        get => _versionDialogMeta;
        private set => SetProperty(ref _versionDialogMeta, value);
    }

    public string VersionDialogStatus
    {
        get => _versionDialogStatus;
        private set => SetProperty(ref _versionDialogStatus, value);
    }

    public bool HasRequiredDependencies => RequiredDependencies.Count > 0;

    public bool HasOptionalDependencies => OptionalDependencies.Count > 0;

    // ---------------------------------------------------------------- 文件名弹窗

    public bool IsFileNameDialogOpen
    {
        get => _isFileNameDialogOpen;
        private set => SetProperty(ref _isFileNameDialogOpen, value);
    }

    public string FileNameInput
    {
        get => _fileNameInput;
        set
        {
            if (SetProperty(ref _fileNameInput, value))
            {
                ValidateFileName();
            }
        }
    }

    public string FileNameError
    {
        get => _fileNameError;
        private set
        {
            if (SetProperty(ref _fileNameError, value))
            {
                OnPropertyChanged(nameof(HasFileNameError));
            }
        }
    }

    public bool HasFileNameError => FileNameError.Length > 0;

    public bool CanConfirmFileName
    {
        get => _canConfirmFileName;
        private set
        {
            if (SetProperty(ref _canConfirmFileName, value))
            {
                _confirmFileNameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ---------------------------------------------------------------- 打开 / 关闭

    /// <summary>
    /// 只有真正的版本号才用于筛选；实例名之类的字符串会让资源站查不到结果，直接退回「全部」。
    /// </summary>
    private static string? NormalizeGameVersion(string? gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return null;
        }

        var text = gameVersion.Trim();
        return ResourceVersionCatalog.IsGameVersionName(text) ? text : null;
    }

    /// <summary>打开对话框（由实例管理页调用）。</summary>
    public void Show(
        ResourceKind kind,
        string instanceId,
        string instanceDirectory,
        string? gameVersion,
        string? loaderName)
    {
        _kind = kind;
        _instanceId = instanceId;
        _instanceDirectory = instanceDirectory;
        _gameVersion = NormalizeGameVersion(gameVersion);
        _loaderName = loaderName;

        Title = "下载" + ResourceRepository.DisplayName(kind);
        OnPropertyChanged(nameof(IsModpackMode));
        OnPropertyChanged(nameof(InstallButtonText));
        SearchText = string.Empty;
        IsOpen = true;
        IsSearchMode = true;
        Results.Clear();
        _pageIndex = 0;
        _totalPages = 0;
        _totalCount = 0;
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanPage));

        ResetDetail();

        GameVersionOptions.Clear();
        GameVersionOptions.Add(AllVersions);
        if (_gameVersion is not null)
        {
            GameVersionOptions.Add(_gameVersion);
        }

        _suppressFilterReload = true;
        try
        {
            SelectedGameVersion = _gameVersion ?? AllVersions;
            BuildSources(kind);
        }
        finally
        {
            _suppressFilterReload = false;
        }

        _ = LoadPageAsync(0);
    }

    public void Close()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _downloadCts?.Cancel();
        _downloadCts = null;
        _detailCts?.Cancel();
        _detailCts = null;
        _resolveCts?.Cancel();
        _resolveCts = null;
        _filterReloadCts?.Cancel();
        _filterReloadCts = null;

        IsOpen = false;
        IsBusy = false;
        IsDownloading = false;
        DownloadProgress = 0;
        DownloadSpeedText = string.Empty;
        _downloadRows.Clear();
        IsVersionDialogOpen = false;
        IsFileNameDialogOpen = false;
        CancelVersionDialog();

        ResetDetail();
        IsSearchMode = true;
    }

    // ---------------------------------------------------------------- 安装源与排序

    private void BuildSources(ResourceKind kind)
    {
        // 首次打开用设置里的默认源；用户在本页切换过后沿用本页的选择。
        var preferred = _selectedSource?.Source ?? _stack.Config.Settings.DefaultContentSource;
        Sources.Clear();

        if (ModrinthService.SupportsDownload(kind))
        {
            Sources.Add(new DownloadSourceOption { Source = ResourceSource.Modrinth, Label = "Modrinth" });
        }

        Sources.Add(new DownloadSourceOption { Source = ResourceSource.CurseForge, Label = "CurseForge" });

        var target = Sources.FirstOrDefault(option => option.Source == preferred)
                     ?? Sources[0];

        _initializingSource = true;
        SelectedSource = target;
        _initializingSource = false;
        OnPropertyChanged(nameof(CanSelectSource));
    }

    private void RebuildSortOptions()
    {
        SortOptions.Clear();

        if (SelectedSource?.Source == ResourceSource.Modrinth)
        {
            SortOptions.Add(new DownloadSortOption
            {
                Label = "相关度", ModrinthIndex = "relevance", CurseForgeSort = CurseForgeSort.Popularity,
            });
            SortOptions.Add(new DownloadSortOption
            {
                Label = "下载量", ModrinthIndex = "downloads", CurseForgeSort = CurseForgeSort.TotalDownloads,
            });
            SortOptions.Add(new DownloadSortOption
            {
                Label = "最近更新", ModrinthIndex = "updated", CurseForgeSort = CurseForgeSort.LastUpdated,
            });
            SortOptions.Add(new DownloadSortOption
            {
                Label = "最新发布", ModrinthIndex = "newest", CurseForgeSort = CurseForgeSort.LastUpdated,
            });
        }
        else
        {
            SortOptions.Add(new DownloadSortOption { Label = "热度", CurseForgeSort = CurseForgeSort.Popularity });
            SortOptions.Add(new DownloadSortOption { Label = "最近更新", CurseForgeSort = CurseForgeSort.LastUpdated });
            SortOptions.Add(new DownloadSortOption { Label = "总下载量", CurseForgeSort = CurseForgeSort.TotalDownloads });
            SortOptions.Add(new DownloadSortOption { Label = "名称", CurseForgeSort = CurseForgeSort.Name });
        }

        _suppressFilterReload = true;
        SelectedSort = SortOptions[0];
        _suppressFilterReload = false;
    }

    /// <summary>筛选条件变化后自动重新检索（防抖），与切换来源的行为保持一致。</summary>
    private void OnFilterChanged()
    {
        if (!IsOpen || _suppressFilterReload)
        {
            return;
        }

        _ = ReloadForFilterAsync();
    }

    private async Task ReloadForFilterAsync()
    {
        _filterReloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterReloadCts = cts;

        try
        {
            try
            {
                // 连续切换下拉时只让最后一次生效。
                await Task.Delay(300, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cts.IsCancellationRequested || !IsOpen)
            {
                return;
            }

            try
            {
                _pageIndex = 0;
                await LoadPageAsync(0);
            }
            catch (OperationCanceledException)
            {
                // 新一轮检索取消上一次请求，属于正常流程。
            }
            catch (Exception ex)
            {
                AsyncCommandErrors.Report(ex);
            }
        }
        finally
        {
            // 每条筛选请求都在自己的 finally 里释放：先摘掉字段引用再 Dispose，
            // 避免回收掉已属于新请求的 CTS。
            if (ReferenceEquals(_filterReloadCts, cts))
            {
                _filterReloadCts = null;
            }

            cts.Dispose();
        }
    }

    // ---------------------------------------------------------------- 搜索

    private Task SearchAsync()
    {
        _pageIndex = 0;
        return LoadPageAsync(0);
    }

    private async Task LoadPageAsync(int pageIndex)
    {
        if (!IsOpen || SelectedSource is null)
        {
            return;
        }

        var source = SelectedSource.Source;
        if (source == ResourceSource.CurseForge && !_stack.CurseForge.IsConfigured)
        {
            StatusText = "CurseForge 检索暂时不可用，请改用 Modrinth。";
            return;
        }

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        _pageIndex = Math.Max(pageIndex, 0);
        IsBusy = true;
        StatusText = "正在从 " + (SelectedSource?.Label ?? "资源站") + " 检索…";
        Results.Clear();
        OnPropertyChanged(nameof(PageText));

        try
        {
            var gameVersion = string.Equals(SelectedGameVersion, AllVersions, StringComparison.Ordinal)
                ? null
                : SelectedGameVersion;

            if (source == ResourceSource.Modrinth)
            {
                var page = await _stack.Modrinth.SearchAsync(
                    _kind,
                    SearchText,
                    gameVersion,
                    SelectedSort?.ModrinthIndex ?? "relevance",
                    _loaderName,
                    _pageIndex,
                    PageSize,
                    cts.Token);

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _totalCount = page.TotalCount;
                _totalPages = page.TotalPages;

                foreach (var project in page.Projects)
                {
                    var row = DownloadProjectRow.FromModrinth(project);
                    Results.Add(row);
                    _ = LoadIconAsync(row, cts.Token);
                }

                StatusText = page.Projects.Count == 0
                    ? "没有找到结果，换个关键词或把游戏版本切到「全部」试试。"
                    : $"共 {_totalCount} 个结果。";
            }
            else
            {
                var page = await _stack.CurseForge.SearchAsync(
                    _kind,
                    SearchText,
                    gameVersion,
                    SelectedSort?.CurseForgeSort ?? CurseForgeSort.Popularity,
                    _pageIndex,
                    PageSize,
                    cts.Token);

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _totalCount = page.TotalCount;
                _totalPages = page.TotalPages;

                foreach (var project in page.Projects)
                {
                    var row = DownloadProjectRow.FromCurseForge(project);
                    Results.Add(row);
                    _ = LoadIconAsync(row, cts.Token);
                }

                StatusText = page.Projects.Count == 0
                    ? "没有找到结果，换个关键词或把游戏版本切到「全部」试试。"
                    : $"共 {_totalCount} 个结果。";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _stack.Log.Warn($"{(source == ResourceSource.Modrinth ? "Modrinth" : "CurseForge")} 检索失败", ex);
            StatusText = "检索失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsBusy = false;
                _searchCts = null;
            }

            cts.Dispose();
            OnPropertyChanged(nameof(PageText));
            OnPropertyChanged(nameof(CanPage));
            _previousPageCommand.RaiseCanExecuteChanged();
            _nextPageCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task LoadIconAsync(DownloadProjectRow row, CancellationToken ct)
    {
        var url = row.IconUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var bytes = await _stack.Downloader.GetBytesAsync(url!, ct).ConfigureAwait(false);
            var image = DecodeImage(bytes);
            if (image is null)
            {
                return;
            }

            UiThread.Post(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    row.Icon = image;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpApiException or HttpRequestException or IOException)
        {
            _stack.Log.Trace($"加载项目图标失败：{url}");
        }
    }

    private async Task LoadDependencyIconAsync(DownloadDependencyRow row, string url, CancellationToken ct)
    {
        try
        {
            var bytes = await _stack.Downloader.GetBytesAsync(url, ct).ConfigureAwait(false);
            var image = DecodeImage(bytes);
            if (image is null)
            {
                return;
            }

            UiThread.Post(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    row.Icon = image;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpApiException or HttpRequestException or IOException)
        {
            _stack.Log.Trace($"加载前置项目图标失败：{url}");
        }
    }

    private async Task LoadDetailIconAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var bytes = await _stack.Downloader.GetBytesAsync(url!, ct).ConfigureAwait(false);
            var image = DecodeImage(bytes);
            if (image is null)
            {
                return;
            }

            UiThread.Post(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    DetailIcon = image;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpApiException or HttpRequestException or IOException)
        {
            _stack.Log.Trace($"加载项目图标失败：{url}");
        }
    }

    private static ImageSource? DecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IOException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 项目详情

    private async Task OpenDetailAsync(DownloadProjectRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;

        IsBusy = true;
        _detailHistory.Clear();
        DetailStatusText = "正在获取项目信息…";

        try
        {
            ResourceProjectSummary? project = null;

            if (!string.IsNullOrWhiteSpace(row.ModrinthId))
            {
                project = await _stack.Modrinth
                    .GetProjectAsync(row.ModrinthId!, cts.Token)
                    .ConfigureAwait(true);
            }
            else if (row.CurseForgeId > 0)
            {
                var curseForgeProject = await _stack.CurseForge
                    .GetProjectAsync(row.CurseForgeId, cts.Token)
                    .ConfigureAwait(true);

                project = ToSummary(curseForgeProject);
            }

            if (project is null || cts.IsCancellationRequested)
            {
                DetailStatusText = "无法获取项目信息。";
                return;
            }

            await LoadProjectAsync(project, pushHistory: false, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _stack.Log.Warn("获取项目详情失败", ex);
            DetailStatusText = "获取项目详情失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_detailCts, cts))
            {
                _detailCts = null;
                IsBusy = false;
            }

            cts.Dispose();
        }
    }

    private async Task LoadProjectAsync(
        ResourceProjectSummary project,
        bool pushHistory,
        CancellationToken ct)
    {
        PrepareDetail(project);

        try
        {
            var versions = project.Source == ResourceSource.Modrinth
                ? await _stack.Modrinth.GetVersionInfosAsync(project.ModrinthId!, _kind, ct).ConfigureAwait(true)
                : await _stack.CurseForge.GetVersionInfosAsync(project.CurseForgeId, _kind, ct).ConfigureAwait(true);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (pushHistory && _currentDetail is not null)
            {
                _detailHistory.Push(_currentDetail);
            }

            _currentDetail = new DetailSnapshot { Project = project, Versions = versions };
            RenderVersions(versions);

            DetailStatusText = versions.Count == 0
                ? "该项目没有可下载的文件，换一个项目试试。"
                : $"共 {versions.Count} 个版本，点击任意版本开始下载。";

            _ = LoadDetailIconAsync(project.IconUrl, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _stack.Log.Warn("获取版本列表失败", ex);
            DetailStatusText = "获取版本列表失败：" + ex.Message;
        }
    }

    private void PrepareDetail(ResourceProjectSummary project)
    {
        DetailName = project.Name;
        DetailSummary = project.Summary;
        DetailCategories = project.Categories;
        DetailMetaText = BuildMetaText(project);
        DetailIcon = null;

        RecommendedVersions.Clear();
        VersionGroups.Clear();
        HasRecommended = false;
        RecommendedHeader = string.IsNullOrWhiteSpace(_gameVersion)
            ? "推荐版本"
            : "推荐版本 - Minecraft " + _gameVersion;
        DetailStatusText = "正在获取版本列表…";

        IsSearchMode = false;
    }

    private void RenderVersions(IReadOnlyList<ResourceVersionInfo> versions)
    {
        RecommendedVersions.Clear();
        HasRecommended = false;

        var recommended = ResourceVersionCatalog.PickRecommended(versions, _kind, _gameVersion, _loaderName);
        if (recommended is not null)
        {
            RecommendedVersions.Add(new DownloadVersionRow { Version = recommended });
            HasRecommended = true;
        }

        VersionGroups.Clear();

        var anyExpanded = false;
        foreach (var group in ResourceVersionCatalog.GroupByGameVersion(versions))
        {
            var expanded = string.Equals(group.GameVersion, _gameVersion, StringComparison.OrdinalIgnoreCase);
            anyExpanded |= expanded;

            DownloadVersionGroup? row = null;
            row = new DownloadVersionGroup
            {
                Header = "Minecraft " + group.GameVersion,
                CountText = group.Versions.Count + " 个版本",
                Versions = group.Versions
                    .Select(version => new DownloadVersionRow { Version = version })
                    .ToList(),
                IsExpanded = expanded,
                ToggleCommand = new RelayCommand(() => row!.IsExpanded = !row.IsExpanded),
            };

            VersionGroups.Add(row);
        }

        // 当前实例版本不在该项目支持列表里时，展开最近的一组，避免一屏全是折叠条。
        if (!anyExpanded && VersionGroups.Count > 0)
        {
            VersionGroups[0].IsExpanded = true;
        }
    }

    private void GoBack()
    {
        IsVersionDialogOpen = false;
        IsFileNameDialogOpen = false;

        // 这里会换掉 _detailCts，旧请求的 finally 因引用不匹配会跳过复位；
        // 必须在替换前显式收尾，否则 IsBusy 会永久停在 true。
        CancelDetailRequest();
        IsBusy = false;

        if (_detailHistory.Count == 0)
        {
            ResetDetail();
            IsSearchMode = true;
            return;
        }

        var snapshot = _detailHistory.Pop();
        _currentDetail = snapshot;

        PrepareDetail(snapshot.Project);
        RenderVersions(snapshot.Versions);
        DetailStatusText = $"共 {snapshot.Versions.Count} 个版本，点击任意版本开始下载。";

        var cts = new CancellationTokenSource();
        _detailCts = cts;
        _ = LoadDetailIconAsync(snapshot.Project.IconUrl, cts.Token);
    }

    /// <summary>取消并释放当前详情请求；旧请求的 finally 不会复位共享状态，由调用方负责。</summary>
    private void CancelDetailRequest()
    {
        var previous = _detailCts;
        _detailCts = null;
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }

    private void ResetDetail()
    {
        CancelDetailRequest();

        // 检索页与详情页共用 IsBusy：旧请求已被放弃，必须显式清除忙碌状态。
        IsBusy = false;
        _detailHistory.Clear();
        _currentDetail = null;
        RecommendedVersions.Clear();
        VersionGroups.Clear();
        HasRecommended = false;
        DetailName = string.Empty;
        DetailSummary = string.Empty;
        DetailMetaText = string.Empty;
        DetailStatusText = string.Empty;
        DetailCategories = Array.Empty<string>();
        DetailIcon = null;
        RequiredDependencies.Clear();
        OptionalDependencies.Clear();
        OnPropertyChanged(nameof(HasRequiredDependencies));
        OnPropertyChanged(nameof(HasOptionalDependencies));
    }

    private static string BuildMetaText(ResourceProjectSummary project)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(project.Author))
        {
            parts.Add("作者：" + project.Author);
        }

        if (project.DownloadCount > 0)
        {
            parts.Add(project.DownloadText + " 次下载");
        }

        return parts.Count == 0
            ? (project.Source == ResourceSource.Modrinth ? "Modrinth 项目" : "CurseForge 项目")
            : string.Join(" · ", parts);
    }

    private static ResourceProjectSummary ToSummary(CurseForgeProject project) => new()
    {
        Source = ResourceSource.CurseForge,
        CurseForgeId = project.Id,
        Name = project.Name,
        Summary = project.Summary,
        IconUrl = project.LogoUrl,
        Author = project.Author,
        Categories = project.Categories,
        DownloadCount = project.DownloadCount,
        WebsiteUrl = project.WebsiteUrl,
    };

    // ---------------------------------------------------------------- 版本选择与前置

    private void SelectVersion(DownloadVersionRow? row)
    {
        if (row is null)
        {
            return;
        }

        DialogVersion = row;
        _pendingVersion = row.Version;
        VersionDialogTitle = ResourceRepository.DisplayName(_kind) + "下载 - " + row.Name;
        VersionDialogMeta = row.DateText
                            + (row.HasLoaderText ? " · " + row.LoaderText : string.Empty);
        VersionDialogStatus = row.Version.Dependencies.Count == 0
            ? string.Empty
            : "正在获取前置内容…";

        RequiredDependencies.Clear();
        OptionalDependencies.Clear();
        OnPropertyChanged(nameof(HasRequiredDependencies));
        OnPropertyChanged(nameof(HasOptionalDependencies));

        IsVersionDialogOpen = true;
        _installCommand.RaiseCanExecuteChanged();
        _downloadToFolderCommand.RaiseCanExecuteChanged();

        if (row.Version.Dependencies.Count > 0)
        {
            _ = ResolveDependenciesAsync(row.Version);
        }
    }

    private async Task ResolveDependenciesAsync(ResourceVersionInfo version)
    {
        _resolveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resolveCts = cts;
        IsResolvingDependencies = true;

        try
        {
            var dependencies = version.Dependencies;

            if (version.Source == ResourceSource.Modrinth)
            {
                var ids = dependencies
                    .Select(dependency => dependency.ModrinthProjectId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var projects = await _stack.Modrinth.GetProjectsAsync(ids, cts.Token).ConfigureAwait(true);
                var map = projects
                    .Where(project => !string.IsNullOrWhiteSpace(project.ModrinthId))
                    .ToDictionary(project => project.ModrinthId!, StringComparer.OrdinalIgnoreCase);

                foreach (var dependency in dependencies)
                {
                    if (dependency.ModrinthProjectId is { } id
                        && map.TryGetValue(id, out var project))
                    {
                        dependency.Project = project;
                    }
                }
            }
            else
            {
                var ids = dependencies
                    .Select(dependency => dependency.CurseForgeProjectId)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                var projects = await _stack.CurseForge.GetProjectsAsync(ids, cts.Token).ConfigureAwait(true);
                var map = projects.ToDictionary(project => project.Id);

                foreach (var dependency in dependencies)
                {
                    if (dependency.CurseForgeProjectId > 0
                        && map.TryGetValue(dependency.CurseForgeProjectId, out var project))
                    {
                        dependency.Project = ToSummary(project);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _stack.Log.Warn("获取前置内容失败", ex);
            VersionDialogStatus = "前置内容获取失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_resolveCts, cts))
            {
                _resolveCts = null;
            }

            cts.Dispose();
            IsResolvingDependencies = false;

            if (IsVersionDialogOpen
                && DialogVersion is not null
                && ReferenceEquals(DialogVersion.Version, version))
            {
                BuildDependencyRows(version);
            }
        }
    }

    private void BuildDependencyRows(ResourceVersionInfo version)
    {
        RequiredDependencies.Clear();
        OptionalDependencies.Clear();

        foreach (var dependency in version.Dependencies)
        {
            var row = new DownloadDependencyRow { Dependency = dependency };
            var target = dependency.Kind == ResourceDependencyKind.Required
                ? RequiredDependencies
                : OptionalDependencies;

            target.Add(row);

            if (dependency.Project?.IconUrl is { Length: > 0 } url)
            {
                _ = LoadDependencyIconAsync(row, url, CancellationToken.None);
            }
        }

        OnPropertyChanged(nameof(HasRequiredDependencies));
        OnPropertyChanged(nameof(HasOptionalDependencies));

        if (VersionDialogStatus.StartsWith("正在获取前置内容", StringComparison.Ordinal))
        {
            VersionDialogStatus = string.Empty;
        }

        _openDependencyCommand.RaiseCanExecuteChanged();
    }

    private async Task OpenDependencyAsync(DownloadDependencyRow? row)
    {
        if (row?.Dependency.Project is not { } project || IsBusy)
        {
            return;
        }

        IsVersionDialogOpen = false;
        IsFileNameDialogOpen = false;

        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;

        IsBusy = true;

        try
        {
            await LoadProjectAsync(project, pushHistory: true, cts.Token).ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_detailCts, cts))
            {
                _detailCts = null;
                IsBusy = false;
            }

            cts.Dispose();
        }
    }

    private void CancelVersionDialog()
    {
        IsVersionDialogOpen = false;
        IsFileNameDialogOpen = false;
        _resolveCts?.Cancel();
        _resolveCts = null;
    }

    // ---------------------------------------------------------------- 安装 / 另存

    private void InstallToInstance()
    {
        if (_pendingVersion is null)
        {
            return;
        }

        if (_kind is ResourceKind.World or ResourceKind.Modpack)
        {
            _ = DownloadSelectedAsync(null);
            return;
        }

        FileNameInput = Path.GetFileName(_pendingVersion.File.FileName);
        IsFileNameDialogOpen = true;
        ValidateFileName();
    }

    private void ValidateFileName()
    {
        var name = (FileNameInput ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            FileNameError = "请输入文件名";
            CanConfirmFileName = false;
            return;
        }

        if (!PathUtil.IsValidFileName(name))
        {
            FileNameError = "文件名包含非法字符";
            CanConfirmFileName = false;
            return;
        }

        var directory = ResourceRepository.GetDirectory(_instanceDirectory, _kind);
        var exists = File.Exists(Path.Combine(directory, name))
                     || File.Exists(Path.Combine(directory, name + ResourceRepository.DisabledSuffix));

        if (exists)
        {
            FileNameError = "该名称已存在";
            CanConfirmFileName = false;
            return;
        }

        FileNameError = string.Empty;
        CanConfirmFileName = _pendingVersion is not null;
    }

    private async Task ConfirmFileNameAsync()
    {
        if (!CanConfirmFileName || _pendingVersion is null)
        {
            return;
        }

        var fileName = FileNameInput.Trim();
        IsFileNameDialogOpen = false;
        await DownloadSelectedAsync(fileName).ConfigureAwait(true);
    }

    private void DownloadToFolder()
    {
        if (_pendingVersion is null)
        {
            return;
        }

        var initial = ResourceRepository.GetDirectory(_instanceDirectory, _kind);
        var dialog = new OpenFolderDialog
        {
            Title = "选择下载目录",
            Multiselect = false,
            InitialDirectory = Directory.Exists(initial) ? initial : _instanceDirectory,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _ = DownloadToFolderAsync(dialog.FolderName);
    }

    private async Task DownloadSelectedAsync(string? targetFileName)
    {
        if (_pendingVersion is null)
        {
            return;
        }

        var version = _pendingVersion;

        _downloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        IsBusy = true;
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadSpeedText = string.Empty;
        _downloadRows.Clear();
        VersionDialogStatus = "正在准备下载…";

        try
        {
            var result = await _stack.ResourceDownloads.DownloadAsync(
                new ResourceDownloadRequest
                {
                    Kind = _kind,
                    File = version.File,
                    InstanceDirectory = _instanceDirectory,
                    TargetFileName = targetFileName,
                },
                new Progress<InstallProgress>(progress =>
                {
                    DownloadProgress = progress.Total > 0
                        ? (double)progress.Completed / progress.Total
                        : 0;
                    DownloadSpeedText = InstallFileItem.FormatSpeed(progress.BytesPerSecond);
                    _downloadRows.Update(progress.ActiveFiles);
                    VersionDialogStatus = progress.Stage;
                }),
                cts.Token).ConfigureAwait(true);

            if (result.Success)
            {
                DownloadProgress = 1;
                var installedName = Path.GetFileName(result.InstalledPath ?? string.Empty);
                _stack.Log.Info($"下载 {ResourceRepository.DisplayName(_kind)}：{installedName}");
                ResourceDownloaded?.Invoke();

                if (_kind == ResourceKind.Modpack && result.InstalledPath is { Length: > 0 } archive)
                {
                    ModpackDownloaded?.Invoke(archive);
                    Close();
                }
                else
                {
                    // 安装成功只收起版本弹窗，下载窗保持打开，方便接着装其它资源。
                    IsVersionDialogOpen = false;
                    IsFileNameDialogOpen = false;
                    var message = installedName.Length == 0
                        ? "安装完成，可继续下载其它资源。"
                        : $"已安装 {installedName}，可继续下载其它资源。";
                    StatusText = message;
                    DetailStatusText = message;
                }
            }
            else
            {
                VersionDialogStatus = result.Error ?? "下载失败。";
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _stack.Log.Error("下载资源失败", ex);
            VersionDialogStatus = "下载失败：" + ex.Message;
        }
        finally
        {
            // 只有当前请求才能复位共享状态：被新下载取消的旧请求不能清掉新任务的行与忙碌标记。
            if (ReferenceEquals(_downloadCts, cts))
            {
                _downloadCts = null;
                DownloadSpeedText = string.Empty;
                _downloadRows.Clear();
                IsBusy = false;
                IsDownloading = false;
            }

            cts.Dispose();
        }
    }

    private async Task DownloadToFolderAsync(string folder)
    {
        if (_pendingVersion is null)
        {
            return;
        }

        var version = _pendingVersion;

        _downloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        IsBusy = true;
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadSpeedText = string.Empty;
        _downloadRows.Clear();
        VersionDialogStatus = "正在准备下载…";

        try
        {
            var result = await _stack.ResourceDownloads.DownloadToFolderAsync(
                version.File,
                folder,
                new Progress<InstallProgress>(progress =>
                {
                    DownloadProgress = progress.Total > 0
                        ? (double)progress.Completed / progress.Total
                        : 0;
                    DownloadSpeedText = InstallFileItem.FormatSpeed(progress.BytesPerSecond);
                    _downloadRows.Update(progress.ActiveFiles);
                    VersionDialogStatus = progress.Stage;
                }),
                cts.Token).ConfigureAwait(true);

            if (result.Success)
            {
                DownloadProgress = 1;
                IsVersionDialogOpen = false;
                DetailStatusText = "已下载到：" + result.InstalledPath;
            }
            else
            {
                VersionDialogStatus = result.Error ?? "下载失败。";
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _stack.Log.Error("下载资源到指定目录失败", ex);
            VersionDialogStatus = "下载失败：" + ex.Message;
        }
        finally
        {
            // 只有当前请求才能复位共享状态：被新下载取消的旧请求不能清掉新任务的行与忙碌标记。
            if (ReferenceEquals(_downloadCts, cts))
            {
                _downloadCts = null;
                DownloadSpeedText = string.Empty;
                _downloadRows.Clear();
                IsBusy = false;
                IsDownloading = false;
            }

            cts.Dispose();
        }
    }

    // ---------------------------------------------------------------- 列表快速下载

    private async Task DownloadAsync(DownloadProjectRow? row)
    {
        if (row is null || IsBusy || SelectedSource is null)
        {
            return;
        }

        var source = SelectedSource.Source;
        var gameVersion = string.Equals(SelectedGameVersion, AllVersions, StringComparison.Ordinal)
            ? null
            : SelectedGameVersion;

        IsBusy = true;
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadSpeedText = string.Empty;
        _downloadRows.Clear();
        StatusText = $"正在获取 {row.Name} 的文件…";

        _downloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        try
        {
            ResourceFile? file;
            if (source == ResourceSource.Modrinth)
            {
                file = row.ModrinthId is null
                    ? null
                    : await _stack.Modrinth.PickFileAsync(
                        row.ModrinthId, _kind, gameVersion, _loaderName, cts.Token);
            }
            else
            {
                var picked = await _stack.CurseForge.PickBestFileAsync(
                    row.CurseForgeId, _kind, gameVersion, _loaderName, cts.Token);
                file = picked is null ? null : ResourceFile.FromCurseForge(picked);
            }

            if (file is null)
            {
                StatusText = "没有找到适用于当前游戏版本 / 加载器的文件。";
                return;
            }

            var result = await _stack.ResourceDownloads.DownloadAsync(
                new ResourceDownloadRequest
                {
                    Kind = _kind,
                    File = file,
                    InstanceDirectory = _instanceDirectory,
                },
                new Progress<InstallProgress>(progress =>
                {
                    DownloadProgress = progress.Total > 0
                        ? (double)progress.Completed / progress.Total
                        : 0;
                    DownloadSpeedText = InstallFileItem.FormatSpeed(progress.BytesPerSecond);
                    _downloadRows.Update(progress.ActiveFiles);
                    StatusText = progress.Stage;
                }),
                cts.Token);

            if (result.Success)
            {
                DownloadProgress = 1;
                StatusText = $"已下载 {file.FileName}。";
                _stack.Log.Info($"下载 {ResourceRepository.DisplayName(_kind)}（{source}）：{file.FileName}");
                ResourceDownloaded?.Invoke();

                if (_kind == ResourceKind.Modpack && result.InstalledPath is { Length: > 0 } archive)
                {
                    ModpackDownloaded?.Invoke(archive);
                    Close();
                }
            }
            else
            {
                StatusText = result.Error ?? "下载失败。";
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _stack.Log.Error("下载资源失败", ex);
            StatusText = "下载失败：" + ex.Message;
        }
        finally
        {
            // 只有当前请求才能复位共享状态：被新下载取消的旧请求不能清掉新任务的行与忙碌标记。
            if (ReferenceEquals(_downloadCts, cts))
            {
                _downloadCts = null;
                DownloadSpeedText = string.Empty;
                _downloadRows.Clear();
                IsBusy = false;
                IsDownloading = false;
            }

            cts.Dispose();
        }
    }
}
