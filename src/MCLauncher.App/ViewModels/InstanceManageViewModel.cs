using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Models;
using MCLauncher.Core.Services;

namespace MCLauncher.App.ViewModels;

/// <summary>实例管理左侧的资源类别。</summary>
public sealed class ManageTabItem
{
    /// <summary>资源类别；服务器管理等非资源页签为 null。</summary>
    public ResourceKind? Kind { get; init; }

    /// <summary>侧栏文字，如「模组管理」。</summary>
    public required string Label { get; init; }

    /// <summary>用于按钮和状态文字的短名，如「模组」。</summary>
    public required string ShortName { get; init; }

    /// <summary>Segoe MDL2 字形。</summary>
    public required string Glyph { get; init; }

    /// <summary>是否是「服务器管理」页签（不扫描资源目录）。</summary>
    public bool IsServer => Kind is null;
}

/// <summary>资源列表的一行。</summary>
public sealed class ResourceRowItem : ViewModelBase
{
    public required LocalResource Resource { get; init; }

    public required InstanceManageViewModel Owner { get; init; }

    public ResourceKind Kind => Resource.Kind;

    public string Title => Resource.Title;

    public string FileName => Resource.FileName;

    /// <summary>可展示的版本：模组版本、资源包格式、存档版本。</summary>
    public string? VersionText => Resource.Version ?? Resource.GameVersion;

    public bool HasVersion => !string.IsNullOrWhiteSpace(VersionText);

    public string? LoaderText => Resource.Loader;

    public bool HasLoader => !string.IsNullOrWhiteSpace(LoaderText);

    public bool IsEnabled => Resource.Enabled;

    /// <summary>只有模组 / 资源包 / 光影包适合用改后缀的方式禁用。</summary>
    public bool CanToggle =>
        Kind is ResourceKind.Mod or ResourceKind.ResourcePack or ResourceKind.ShaderPack;

    public bool IsWorld => Kind == ResourceKind.World;

    public string Subtitle => Kind == ResourceKind.World
        ? $"{Resource.SizeText} · {Resource.ModifyText}"
        : $"{FileName} · {Resource.SizeText}";

    public string ToolTipText => string.IsNullOrWhiteSpace(Resource.Description)
        ? Resource.FilePath
        : $"{Resource.Description}{Environment.NewLine}{Resource.FilePath}";

    private ImageSource? _icon;
    private bool _iconLoaded;

    /// <summary>Mod logo / 资源包封面 / 存档图标；没有时界面退回字形图标。</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = DecodeIcon(Resource.IconBytes);
            }

            return _icon;
        }
    }

    public bool HasIcon => Icon is not null;

    private ResourceFile? _updateFile;
    private string? _updateVersionText;

    public ResourceFile? UpdateFile => _updateFile;

    public bool UpdateAvailable => _updateFile is not null;

    public string? UpdateVersionText => _updateVersionText;

    public bool HasUpdateVersion => !string.IsNullOrWhiteSpace(UpdateVersionText);

    public void SetUpdate(ResourceUpdateInfo? update)
    {
        _updateFile = update?.File;
        _updateVersionText = update?.LatestVersionText;

        OnPropertyChanged(nameof(UpdateFile));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(UpdateVersionText));
        OnPropertyChanged(nameof(HasUpdateVersion));
    }

    public string IconGlyph => Kind switch
    {
        ResourceKind.Mod => "\uE7B8",
        ResourceKind.ResourcePack => "\uE8B9",
        ResourceKind.ShaderPack => "\uE706",
        ResourceKind.World => "\uE909",
        ResourceKind.Schematic => "\uE7C3",
        _ => "\uE7C3",
    };

    public bool Matches(string filter) =>
        string.IsNullOrWhiteSpace(filter)
        || Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (Resource.Version?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private static ImageSource? DecodeIcon(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

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
}

/// <summary>「检查更新」弹窗里的一条可更新资源，默认勾选。</summary>
public sealed class ResourceUpdateItem : ViewModelBase
{
    private readonly Action _onSelectionChanged;
    private bool _isSelected = true;

    public ResourceUpdateItem(ResourceUpdateInfo update, Action onSelectionChanged)
    {
        Update = update;
        _onSelectionChanged = onSelectionChanged;
    }

    public ResourceUpdateInfo Update { get; }

    public string Title => Update.Resource.Title;

    public string VersionText =>
        $"{Update.CurrentVersionText ?? "当前版本未知"} → {Update.LatestVersionText}";

    /// <summary>这条更新是从哪个站点匹配到的。</summary>
    public string SourceText => Update.Source == ResourceSource.Modrinth ? "Modrinth" : "CurseForge";

    /// <summary>是否在「更新选中」时更新这个资源。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onSelectionChanged();
            }
        }
    }
}

/// <summary>
/// 实例管理页：按实例管理模组、资源包、光影包、世界与原理图。
/// 列表来自 <see cref="ResourceRepository"/>，启用/禁用沿用改后缀的约定。
/// </summary>
public sealed class InstanceManageViewModel : ViewModelBase
{
    private readonly LauncherStack _stack;
    private readonly Action _back;

    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _addCommand;
    private readonly AsyncRelayCommand _checkUpdatesCommand;
    private readonly AsyncRelayCommand _applyUpdatesCommand;

    private InstalledVersionInfo? _instance;
    private string _instanceId = string.Empty;
    private string _instanceDisplay = string.Empty;
    private string _instanceDirectory = string.Empty;
    private bool _isolated;

    private ManageTabItem? _selectedTab;
    private bool _isSettingsMode;
    private bool _isEngineMode;
    private bool _isServerMode;
    private IReadOnlyList<LocalResource> _allResources = Array.Empty<LocalResource>();
    private readonly DownloadFileRows _downloadRows = new();
    private string _searchText = string.Empty;
    private string _statusText = string.Empty;
    private string _downloadSpeedText = string.Empty;
    private bool _isBusy;
    private bool _isDownloading;
    private bool _isCheckingUpdates;
    private bool _isUpdateDialogOpen;
    private bool _isApplyingUpdates;
    private bool _hasUpdateCheckResult;
    private bool _hasUpdateResult;
    private bool _updateResultFailed;
    private string _updateDialogStatus = string.Empty;
    private string _updateResultText = string.Empty;
    private double _updateProgress;
    private string _updateSpeedText = string.Empty;
    private readonly ObservableCollection<ResourceUpdateItem> _updateItems = new();
    private int _scanVersion;
    private int _updateCheckVersion;
    private IReadOnlyDictionary<string, ResourceUpdateInfo> _updatesByPath =
        new Dictionary<string, ResourceUpdateInfo>(StringComparer.OrdinalIgnoreCase);

    private bool _isDetailOpen;
    private string _detailTitle = string.Empty;
    private string _detailSubtitle = string.Empty;
    private string _detailText = string.Empty;
    private string _detailPath = string.Empty;

    public InstanceManageViewModel(
        LauncherStack stack,
        Action back,
        Action<string, string, int> joinServer)
    {
        _stack = stack;
        _back = back;

        Settings = new InstanceSettingsViewModel(stack);
        Settings.Saved += (_, _) => OnInstanceSaved();

        Engine = new EngineManagementViewModel(stack);
        Engine.InstanceCreated += OnInstanceCreated;

        Servers = new ServerManagementViewModel(stack, joinServer);

        Download = new ResourceDownloadViewModel(stack);
        Download.ResourceDownloaded += OnResourceDownloaded;

        Tabs.Add(new ManageTabItem
        {
            Kind = ResourceKind.Mod, Label = "模组管理", ShortName = "模组", Glyph = "\uE7B8",
        });
        Tabs.Add(new ManageTabItem
        {
            Kind = ResourceKind.ResourcePack, Label = "资源包管理", ShortName = "资源包", Glyph = "\uE8B9",
        });
        Tabs.Add(new ManageTabItem
        {
            Kind = ResourceKind.ShaderPack, Label = "光影包管理", ShortName = "光影包", Glyph = "\uE706",
        });
        Tabs.Add(new ManageTabItem
        {
            Kind = ResourceKind.World, Label = "世界管理", ShortName = "世界", Glyph = "\uE909",
        });
        Tabs.Add(new ManageTabItem
        {
            Kind = ResourceKind.Schematic, Label = "原理图管理", ShortName = "原理图", Glyph = "\uE7C3",
        });
        Tabs.Add(new ManageTabItem
        {
            Label = "服务器管理", ShortName = "服务器", Glyph = "\uE774",
        });

        _selectedTab = Tabs[0];

        _refreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && _instanceId.Length > 0);
        RefreshCommand = _refreshCommand;
        _addCommand = new AsyncRelayCommand(AddAsync, () => !IsBusy && _instanceId.Length > 0);
        AddCommand = _addCommand;
        _checkUpdatesCommand = new AsyncRelayCommand(
            CheckUpdatesAsync,
            () => !IsBusy && !IsCheckingUpdates && !IsApplyingUpdates && SupportsUpdate);
        CheckUpdatesCommand = _checkUpdatesCommand;

        _applyUpdatesCommand = new AsyncRelayCommand(ApplyUpdatesAsync, () => CanApplyUpdates);
        ApplyUpdatesCommand = _applyUpdatesCommand;
        CloseUpdateDialogCommand = new RelayCommand(CloseUpdateDialog);

        OpenFolderCommand = new RelayCommand(OpenFolder);
        DeleteCommand = new RelayCommand<ResourceRowItem>(DeleteResource);
        ToggleEnabledCommand = new RelayCommand<ResourceRowItem>(ToggleEnabled);
        InfoCommand = new RelayCommand<ResourceRowItem>(ShowInfo);
        BackupCommand = new RelayCommand<ResourceRowItem>(BackupWorld);
        UpdateResourceCommand = new RelayCommand<ResourceRowItem>(row => _ = UpdateResourceAsync(row));
        OpenDetailFolderCommand = new RelayCommand(OpenDetailFolder);
        CloseDetailCommand = new RelayCommand(() => IsDetailOpen = false);
        BackCommand = new RelayCommand(_back);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenEngineCommand = new RelayCommand(OpenEngine);
        OpenDownloadCommand = new RelayCommand(OpenDownload, () => SupportsDownload);

        _stack.Changed += (_, _) =>
        {
            if (_instanceId.Length > 0)
            {
                UiThread.Post(() => _ = RefreshAsync());
            }
        };
    }

    // ---------------------------------------------------------------- 实例信息

    public ObservableCollection<ManageTabItem> Tabs { get; } = new();

    /// <summary>右侧「游戏设置」面板的 ViewModel。</summary>
    public InstanceSettingsViewModel Settings { get; }

    /// <summary>右侧「引擎管理」面板的 ViewModel。</summary>
    public EngineManagementViewModel Engine { get; }

    /// <summary>右侧「服务器管理」面板的 ViewModel。</summary>
    public ServerManagementViewModel Servers { get; }

    /// <summary>资源下载对话框的 ViewModel。</summary>
    public ResourceDownloadViewModel Download { get; }

    /// <summary>当前页签是否支持从 CurseForge 下载。</summary>
    public bool SupportsDownload =>
        _selectedTab?.Kind is ResourceKind kind && CurseForgeService.SupportsDownload(kind);

    /// <summary>当前页签是否支持检查更新。</summary>
    public bool SupportsUpdate =>
        _selectedTab?.Kind is ResourceKind kind && CurseForgeService.SupportsUpdate(kind);

    /// <summary>null 表示左侧没有选中资源页签（此时正在看实例设置）。</summary>
    public ManageTabItem? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value))
            {
                return;
            }

            OnPropertyChanged(nameof(AddButtonText));
            OnPropertyChanged(nameof(EmptyHint));
            OnPropertyChanged(nameof(UpdateDialogSubtitle));

            // 切换页签时旧页签的检查结果已经失效，直接收起弹窗。
            if (!IsCheckingUpdates && !IsApplyingUpdates)
            {
                IsUpdateDialogOpen = false;
            }

            if (value is null)
            {
                return;
            }

            IsSettingsMode = false;
            IsEngineMode = false;
            OnTabCapabilitiesChanged();

            if (value.IsServer)
            {
                IsServerMode = true;
                Servers.Show(_instanceId, _instanceDirectory);
                return;
            }

            IsServerMode = false;
            _ = RefreshAsync();
        }
    }

    /// <summary>右侧显示实例设置而非资源列表。</summary>
    public bool IsSettingsMode
    {
        get => _isSettingsMode;
        private set => SetProperty(ref _isSettingsMode, value);
    }

    /// <summary>右侧显示引擎管理面板。</summary>
    public bool IsEngineMode
    {
        get => _isEngineMode;
        private set => SetProperty(ref _isEngineMode, value);
    }

    /// <summary>右侧显示服务器管理面板。</summary>
    public bool IsServerMode
    {
        get => _isServerMode;
        private set => SetProperty(ref _isServerMode, value);
    }

    public string InstanceDisplay
    {
        get => _instanceDisplay;
        private set => SetProperty(ref _instanceDisplay, value);
    }

    public string InstanceSubtitle
    {
        get => _instanceId.Length == 0
            ? string.Empty
            : $"{_instanceId} · {(_isolated ? "版本隔离" : "共享游戏目录")}";
    }

    /// <summary>当前实例实际读写 mods / saves 等目录的位置。</summary>
    public string InstanceDirectory
    {
        get => _instanceDirectory;
        private set => SetProperty(ref _instanceDirectory, value);
    }

    // ---------------------------------------------------------------- 列表

    public ObservableCollection<ResourceRowItem> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public bool ShowEmptyHint => Items.Count == 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>正在下载的资源文件；更新单个资源时队列里只有一行。</summary>
    public ObservableCollection<InstallFileItem> DownloadFiles => _downloadRows.Items;

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    public string DownloadSpeedText
    {
        get => _downloadSpeedText;
        private set => SetProperty(ref _downloadSpeedText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                _refreshCommand.RaiseCanExecuteChanged();
                _addCommand.RaiseCanExecuteChanged();
                _checkUpdatesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingUpdates, value))
            {
                RefreshUpdateState();
            }
        }
    }

    /// <summary>「检查更新」弹窗是否打开。</summary>
    public bool IsUpdateDialogOpen
    {
        get => _isUpdateDialogOpen;
        private set => SetProperty(ref _isUpdateDialogOpen, value);
    }

    /// <summary>弹窗副标题：实例名 + 当前管理的资源类别。</summary>
    public string UpdateDialogSubtitle =>
        $"{InstanceDisplay} · {SelectedTab?.ShortName ?? "资源"}";

    public string UpdateDialogStatus
    {
        get => _updateDialogStatus;
        private set => SetProperty(ref _updateDialogStatus, value);
    }

    /// <summary>检查完成（或失败）后才显示结果区域。</summary>
    public bool HasUpdateCheckResult
    {
        get => _hasUpdateCheckResult;
        private set => SetProperty(ref _hasUpdateCheckResult, value);
    }

    /// <summary>弹窗里列出的可更新资源，默认全部勾选。</summary>
    public ObservableCollection<ResourceUpdateItem> UpdateItems => _updateItems;

    public bool IsApplyingUpdates
    {
        get => _isApplyingUpdates;
        private set
        {
            if (SetProperty(ref _isApplyingUpdates, value))
            {
                RefreshUpdateState();
            }
        }
    }

    /// <summary>检查或更新正在进行。</summary>
    public bool IsUpdateBusy => IsCheckingUpdates || IsApplyingUpdates;

    public double UpdateProgress
    {
        get => _updateProgress;
        private set => SetProperty(ref _updateProgress, value);
    }

    public string UpdateSpeedText
    {
        get => _updateSpeedText;
        private set => SetProperty(ref _updateSpeedText, value);
    }

    public string UpdateResultText
    {
        get => _updateResultText;
        private set => SetProperty(ref _updateResultText, value);
    }

    public bool HasUpdateResult
    {
        get => _hasUpdateResult;
        private set => SetProperty(ref _hasUpdateResult, value);
    }

    public bool UpdateResultFailed
    {
        get => _updateResultFailed;
        private set => SetProperty(ref _updateResultFailed, value);
    }

    /// <summary>至少勾选了一个可更新资源，且当前没有检查 / 更新在进行。</summary>
    public bool CanApplyUpdates =>
        !IsApplyingUpdates && !IsCheckingUpdates && _updateItems.Any(item => item.IsSelected);

    private void RefreshUpdateState()
    {
        OnPropertyChanged(nameof(CanApplyUpdates));
        OnPropertyChanged(nameof(IsUpdateBusy));
        _applyUpdatesCommand.RaiseCanExecuteChanged();
        _checkUpdatesCommand.RaiseCanExecuteChanged();
    }

    public string AddButtonText => SelectedTab?.Kind switch
    {
        ResourceKind.Mod => "添加模组",
        ResourceKind.ResourcePack => "添加资源包",
        ResourceKind.ShaderPack => "添加光影包",
        ResourceKind.World => "导入世界",
        ResourceKind.Schematic => "添加原理图",
        _ => "添加",
    };

    public string EmptyHint => SelectedTab?.Kind switch
    {
        ResourceKind.Mod => "还没有模组。点「添加模组」导入 .jar，或安装整合包。",
        ResourceKind.ResourcePack => "还没有资源包。点「添加资源包」导入 .zip。",
        ResourceKind.ShaderPack => "还没有光影包。点「添加光影包」导入 .zip。",
        ResourceKind.World => "还没有存档。进入游戏创建世界，或点「导入世界」选择存档文件夹。",
        ResourceKind.Schematic => "还没有原理图。支持 .litematic / .schematic / .schem / .nbt。",
        _ => "这里还是空的。",
    };

    // ---------------------------------------------------------------- 命令

    public ICommand RefreshCommand { get; }

    public ICommand AddCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand ToggleEnabledCommand { get; }

    public ICommand InfoCommand { get; }

    public ICommand BackupCommand { get; }

    public ICommand UpdateResourceCommand { get; }

    public ICommand OpenDetailFolderCommand { get; }

    public ICommand CloseDetailCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenEngineCommand { get; }

    public ICommand OpenDownloadCommand { get; }

    public ICommand CheckUpdatesCommand { get; }

    /// <summary>在「检查更新」弹窗里更新勾选的资源。</summary>
    public ICommand ApplyUpdatesCommand { get; }

    public ICommand CloseUpdateDialogCommand { get; }

    // ---------------------------------------------------------------- 显示实例

    /// <summary>由实例列表的「管理」进入时调用，默认显示资源列表。</summary>
    public void Show(string versionId)
    {
        var changed = !string.Equals(_instanceId, versionId, StringComparison.OrdinalIgnoreCase);

        // 重新进入实例管理页时旧实例的检查结果一律作废：递增代号让在途请求回包后自行丢弃，
        // 并复位忙碌标记，否则新实例的检查会被旧的「正在检查」挡住。
        _updateCheckVersion++;
        IsCheckingUpdates = false;

        _instanceId = versionId;
        _isolated = _stack.IsIsolated(versionId);
        _instanceDirectory = _stack.GetInstanceDirectory(versionId, _isolated);
        _instance = _stack.GetInstalledVersions()
            .FirstOrDefault(version => string.Equals(version.Id, versionId, StringComparison.OrdinalIgnoreCase));

        InstanceDisplay = _instance?.Display ?? versionId;
        OnPropertyChanged(nameof(InstanceSubtitle));
        OnPropertyChanged(nameof(InstanceDirectory));
        OnPropertyChanged(nameof(UpdateDialogSubtitle));

        if (changed)
        {
            Download.Close();
            ResetUpdateDialogState();
            IsUpdateDialogOpen = false;
            _updatesByPath = new Dictionary<string, ResourceUpdateInfo>(StringComparer.OrdinalIgnoreCase);
            SearchText = string.Empty;
            _selectedTab = Tabs[0];
            OnPropertyChanged(nameof(SelectedTab));
        }
        else if (_selectedTab is null)
        {
            _selectedTab = Tabs[0];
            OnPropertyChanged(nameof(SelectedTab));
        }

        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(EmptyHint));
        OnTabCapabilitiesChanged();

        IsSettingsMode = false;
        IsEngineMode = false;
        IsDetailOpen = false;

        if (_selectedTab is { IsServer: true })
        {
            IsServerMode = true;
            Servers.Show(_instanceId, _instanceDirectory);
        }
        else
        {
            IsServerMode = false;
            _ = RefreshAsync();
        }
    }

    /// <summary>由实例列表的「实例设置」或首页的设置按钮进入：直接打开右侧游戏设置。</summary>
    public void ShowSettings(string versionId)
    {
        Show(versionId);
        OpenSettings();
    }

    private void OpenSettings()
    {
        if (_instanceId.Length == 0)
        {
            return;
        }

        Settings.Show(_instanceId);
        SelectedTab = null;
        IsEngineMode = false;
        IsServerMode = false;
        IsSettingsMode = true;
    }

    private void OpenEngine()
    {
        if (_instanceId.Length == 0)
        {
            return;
        }

        Engine.Show(_instanceId);
        SelectedTab = null;
        IsServerMode = false;
        IsSettingsMode = false;
        IsEngineMode = true;
    }

    private void OnInstanceCreated(string versionId)
    {
        Show(versionId);
        OpenEngine();
    }

    /// <summary>实例被重命名后通知外壳更新页面标题。</summary>
    public event Action<string>? InstanceRenamed;

    /// <summary>实例设置保存后同步显示名、隔离目录与资源扫描位置。</summary>
    private void OnInstanceSaved()
    {
        // 实例设置里可能改了实例名，目录 id 会跟着变。
        var currentId = Settings.InstanceId;
        if (currentId.Length == 0)
        {
            return;
        }

        var renamed = !string.Equals(_instanceId, currentId, StringComparison.OrdinalIgnoreCase);
        _instanceId = currentId;
        _isolated = _stack.IsIsolated(_instanceId);
        _instanceDirectory = _stack.GetInstanceDirectory(_instanceId, _isolated);
        _instance = _stack.GetInstalledVersions()
            .FirstOrDefault(version => string.Equals(version.Id, _instanceId, StringComparison.OrdinalIgnoreCase));

        InstanceDisplay = _instance?.Display ?? _instanceId;
        OnPropertyChanged(nameof(InstanceSubtitle));
        OnPropertyChanged(nameof(InstanceDirectory));
        OnPropertyChanged(nameof(UpdateDialogSubtitle));

        if (_selectedTab is { IsServer: true })
        {
            Servers.Show(_instanceId, _instanceDirectory);
        }
        else if (SelectedTab is not null)
        {
            _ = RefreshAsync();
        }

        if (renamed)
        {
            InstanceRenamed?.Invoke(_instanceId);
        }
    }

    // ---------------------------------------------------------------- 扫描

    private async Task RefreshAsync()
    {
        var tab = SelectedTab;
        if (_instanceId.Length == 0 || tab is null || tab.Kind is not ResourceKind kind)
        {
            return;
        }

        var scan = ++_scanVersion;
        var directory = ResourceRepository.GetDirectory(_instanceDirectory, kind);

        IsBusy = true;
        StatusText = $"正在扫描{tab.ShortName}…";

        try
        {
            var resources = await Task.Run(() => _stack.Resources.Scan(_instanceDirectory, kind));

            if (scan != _scanVersion)
            {
                return;
            }

            _allResources = resources;
            ApplyFilter();

            var disabled = resources.Count(resource => !resource.Enabled);
            StatusText = disabled > 0
                ? $"共 {resources.Count} 个{tab.ShortName}，其中 {disabled} 个已禁用。"
                : $"共 {resources.Count} 个{tab.ShortName}。";
        }
        catch (Exception ex)
        {
            _stack.Log.Error($"扫描实例资源失败：{directory}", ex);
            if (scan != _scanVersion)
            {
                return;
            }

            // 扫描失败时清空列表：否则从别的页签切过来，界面上会残留上一个页签的资源。
            _allResources = Array.Empty<LocalResource>();
            ApplyFilter();
            StatusText = "扫描失败：" + ex.Message;
        }
        finally
        {
            if (scan == _scanVersion)
            {
                IsBusy = false;
            }
        }
    }

    private void ApplyFilter()
    {
        var filter = SearchText.Trim();

        Items.Clear();
        foreach (var resource in _allResources)
        {
            var row = new ResourceRowItem { Resource = resource, Owner = this };
            if (!row.Matches(filter))
            {
                continue;
            }

            if (_updatesByPath.TryGetValue(resource.FilePath, out var update))
            {
                row.SetUpdate(update);
            }

            Items.Add(row);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmptyHint));
    }

    private void OnTabCapabilitiesChanged()
    {
        OnPropertyChanged(nameof(SupportsDownload));
        OnPropertyChanged(nameof(SupportsUpdate));
        (OpenDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
        _checkUpdatesCommand.RaiseCanExecuteChanged();
    }

    private void OpenDownload()
    {
        if (_instanceId.Length == 0
            || _selectedTab?.Kind is not ResourceKind kind
            || !CurseForgeService.SupportsDownload(kind))
        {
            return;
        }

        // 用原版版本号筛选资源；拿不到时传 null，让下载页退回「全部」而不是拿实例名去筛。
        Download.Show(kind, _instanceId, _instanceDirectory, _instance?.BaseVersionId, _instance?.LoaderName);
    }

    private void OnResourceDownloaded()
    {
        if (_instanceId.Length > 0 && _selectedTab?.Kind is not null)
        {
            _ = RefreshAsync();
        }
    }

    // ---------------------------------------------------------------- 检查更新

    /// <summary>
    /// 打开「检查更新」弹窗：先按设置的默认下载源检查（Modrinth / CurseForge，另一个源兜底），
    /// 再把可更新项列出来并默认全部勾选，等玩家点「更新选中」后才真正下载。
    /// </summary>
    private async Task CheckUpdatesAsync()
    {
        var tab = SelectedTab;
        if (_instanceId.Length == 0
            || tab?.Kind is not ResourceKind kind
            || !CurseForgeService.SupportsUpdate(kind)
            || IsCheckingUpdates
            || IsApplyingUpdates)
        {
            return;
        }

        // 记录本次检查针对的实例与请求代号：回包时要确认还是同一个实例、同一次检查。
        var check = ++_updateCheckVersion;
        var instanceId = _instanceId;

        _updatesByPath = new Dictionary<string, ResourceUpdateInfo>(StringComparer.OrdinalIgnoreCase);
        ApplyFilter();
        ResetUpdateDialogState();
        IsUpdateDialogOpen = true;
        UpdateDialogStatus = "正在检查更新…";
        StatusText = UpdateDialogStatus;
        IsCheckingUpdates = true;

        try
        {
            var progress = new Progress<string>(message =>
            {
                UpdateDialogStatus = message;
                StatusText = message;
            });

            var result = await _stack.ResourceUpdates.CheckAsync(
                _allResources,
                _instance?.VersionText,
                _instance?.LoaderName,
                _stack.Config.Settings.DefaultContentSource,
                progress,
                CancellationToken.None);

            // 已有更新的检查接手时，弹窗与结果都归新请求管理，这里只丢弃旧结果。
            if (check != _updateCheckVersion)
            {
                return;
            }

            // 检查期间玩家切走了页签或切到了别的实例，结果已不属于当前页面，丢弃并收起弹窗。
            if (!string.Equals(_instanceId, instanceId, StringComparison.OrdinalIgnoreCase)
                || SelectedTab?.Kind != kind)
            {
                IsUpdateDialogOpen = false;
                return;
            }

            _updatesByPath = result.Updates.ToDictionary(
                update => update.Resource.FilePath,
                StringComparer.OrdinalIgnoreCase);
            ApplyFilter();

            foreach (var update in result.Updates)
            {
                _updateItems.Add(new ResourceUpdateItem(update, RefreshUpdateState));
            }

            HasUpdateCheckResult = true;
            UpdateDialogStatus = result.Checked == 0
                ? "当前列表里没有可检查的程序文件。"
                : result.Updates.Count == 0
                    ? $"已是最新：检查 {result.Checked} 个，匹配到 {result.Matched} 个。"
                    : $"发现 {result.Updates.Count} 个可更新（检查 {result.Checked} 个，匹配 {result.Matched} 个），已默认勾选。";
            StatusText = UpdateDialogStatus;
            RefreshUpdateState();
        }
        catch (Exception ex)
        {
            _stack.Log.Error("检查资源更新失败", ex);

            // 结果已过期的请求不再改界面，把状态留给当前那次检查。
            if (check == _updateCheckVersion)
            {
                UpdateDialogStatus = "检查失败：" + ex.Message;
                StatusText = UpdateDialogStatus;
            }
        }
        finally
        {
            // 旧请求不能复位新请求的忙碌状态。
            if (check == _updateCheckVersion)
            {
                IsCheckingUpdates = false;
            }
        }
    }

    /// <summary>更新弹窗里勾选的资源；成功的会替换原文件，失败项留在结果说明里。</summary>
    private async Task ApplyUpdatesAsync()
    {
        var selected = _updateItems
            .Where(item => item.IsSelected)
            .Select(item => item.Update)
            .ToList();

        if (selected.Count == 0 || IsApplyingUpdates || IsCheckingUpdates || _instanceId.Length == 0)
        {
            return;
        }

        IsApplyingUpdates = true;
        IsBusy = true;
        IsDownloading = true;
        UpdateProgress = 0;
        UpdateSpeedText = string.Empty;
        HasUpdateResult = false;
        UpdateResultText = string.Empty;
        UpdateResultFailed = false;
        _downloadRows.Clear();

        var succeeded = 0;
        var failures = new List<string>();

        var progress = new Progress<InstallProgress>(value =>
        {
            UpdateProgress = value.Fraction;
            UpdateSpeedText = InstallFileItem.FormatSpeed(value.BytesPerSecond);
            UpdateDialogStatus = value.Total > 0
                ? $"{value.Stage} {value.Completed}/{value.Total}"
                : value.Stage;
            StatusText = UpdateDialogStatus;
            _downloadRows.Update(value.ActiveFiles);
        });

        try
        {
            foreach (var update in selected)
            {
                var result = await _stack.ResourceDownloads.DownloadAsync(
                    new ResourceDownloadRequest
                    {
                        Kind = update.Resource.Kind,
                        File = update.File,
                        InstanceDirectory = _instanceDirectory,
                        ReplacePath = update.Resource.FilePath,
                    },
                    progress,
                    CancellationToken.None);

                if (result.Success)
                {
                    succeeded++;
                    _stack.Log.Info($"更新资源 {update.Resource.Title} → {update.File.FileName}");
                }
                else
                {
                    failures.Add($"{update.Resource.Title}：{result.Error ?? "下载失败"}");
                }
            }
        }
        catch (Exception ex)
        {
            _stack.Log.Error("更新资源失败", ex);
            failures.Add(ex.Message);
        }
        finally
        {
            IsApplyingUpdates = false;
            IsBusy = false;
            IsDownloading = false;
            UpdateSpeedText = string.Empty;
            _downloadRows.Clear();
        }

        _updatesByPath = new Dictionary<string, ResourceUpdateInfo>(StringComparer.OrdinalIgnoreCase);
        _updateItems.Clear();
        HasUpdateCheckResult = false;
        HasUpdateResult = true;
        UpdateResultFailed = failures.Count > 0;
        UpdateResultText = failures.Count > 0
            ? $"已更新 {succeeded}/{selected.Count} 个；失败：{string.Join("；", failures)}"
            : $"已更新 {succeeded} 个。";
        UpdateDialogStatus = UpdateResultFailed ? "部分资源更新失败。" : "更新完成。";
        StatusText = UpdateDialogStatus;

        await RefreshAsync();
    }

    private void CloseUpdateDialog()
    {
        if (IsCheckingUpdates || IsApplyingUpdates)
        {
            return;
        }

        IsUpdateDialogOpen = false;
    }

    private void ResetUpdateDialogState()
    {
        _updateItems.Clear();
        HasUpdateCheckResult = false;
        HasUpdateResult = false;
        UpdateResultText = string.Empty;
        UpdateResultFailed = false;
        UpdateDialogStatus = string.Empty;
        UpdateProgress = 0;
        UpdateSpeedText = string.Empty;
        RefreshUpdateState();
    }

    private async Task UpdateResourceAsync(ResourceRowItem? row)
    {
        if (row?.UpdateFile is not { } file || IsBusy || _instanceId.Length == 0)
        {
            return;
        }

        var message = $"更新「{row.Title}」到 {row.UpdateVersionText}？\n\n"
                      + "旧文件会被新下载的文件替换。";
        if (MessageBox.Show(message, "更新资源", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        var title = row.Title;
        var success = false;

        IsBusy = true;
        IsDownloading = true;
        DownloadSpeedText = string.Empty;
        _downloadRows.Clear();
        StatusText = $"正在更新 {title}…";

        try
        {
            var result = await _stack.ResourceDownloads.DownloadAsync(
                new ResourceDownloadRequest
                {
                    Kind = row.Kind,
                    File = file,
                    InstanceDirectory = _instanceDirectory,
                    ReplacePath = row.Resource.FilePath,
                },
                new Progress<InstallProgress>(progress =>
                {
                    StatusText = progress.Stage;
                    DownloadSpeedText = InstallFileItem.FormatSpeed(progress.BytesPerSecond);
                    _downloadRows.Update(progress.ActiveFiles);
                }),
                CancellationToken.None);

            if (result.Success)
            {
                success = true;
                _stack.Log.Info($"更新资源 {title} → {file.FileName}");
            }
            else
            {
                StatusText = result.Error ?? "更新失败。";
            }
        }
        catch (Exception ex)
        {
            _stack.Log.Error("更新资源失败", ex);
            StatusText = "更新失败：" + ex.Message;
        }
        finally
        {
            IsDownloading = false;
            DownloadSpeedText = string.Empty;
            _downloadRows.Clear();
            IsBusy = false;
        }

        if (success)
        {
            await RefreshAsync();
            StatusText = $"已更新 {title}：{file.FileName}";
        }
    }

    // ---------------------------------------------------------------- 操作

    private void OpenFolder()
    {
        var tab = SelectedTab;
        if (tab?.Kind is not ResourceKind kind)
        {
            return;
        }

        try
        {
            var directory = ResourceRepository.GetDirectory(_instanceDirectory, kind);
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _stack.Log.Warn("打开资源目录失败", ex);
            StatusText = "打开目录失败：" + ex.Message;
        }
    }

    private async Task AddAsync()
    {
        var tab = SelectedTab;
        if (_instanceId.Length == 0 || tab is null || tab.Kind is not ResourceKind kind)
        {
            return;
        }

        var imported = 0;

        try
        {
            if (kind == ResourceKind.World)
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "选择要导入的存档文件夹（包含 level.dat）",
                    Multiselect = false,
                };

                var savesRoot = ResourceRepository.GetDirectory(_instanceDirectory, ResourceKind.World);
                if (Directory.Exists(savesRoot))
                {
                    dialog.InitialDirectory = savesRoot;
                }

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                if (!File.Exists(Path.Combine(dialog.FolderName, "level.dat")))
                {
                    StatusText = "这个文件夹不是有效存档：缺少 level.dat。";
                    return;
                }

                if (_stack.Resources.Import(_instanceDirectory, kind, dialog.FolderName))
                {
                    imported = 1;
                }
            }
            else
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = AddButtonText,
                    Filter = FilterFor(kind),
                    Multiselect = true,
                    CheckFileExists = true,
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                foreach (var file in dialog.FileNames)
                {
                    if (_stack.Resources.Import(_instanceDirectory, kind, file))
                    {
                        imported++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _stack.Log.Error("导入资源失败", ex);
            StatusText = "导入失败：" + ex.Message;
            return;
        }

        await RefreshAsync();
        StatusText = $"已导入 {imported} 个{tab.ShortName}。";
    }

    private void ToggleEnabled(ResourceRowItem? row)
    {
        if (row is null || !row.CanToggle)
        {
            return;
        }

        var target = !row.Resource.Enabled;
        if (_stack.Resources.SetEnabled(row.Resource, target))
        {
            StatusText = $"{(target ? "已启用" : "已禁用")} {row.Title}。";
            _ = RefreshAsync();
        }
        else
        {
            StatusText = "切换启用状态失败，请检查文件是否被占用。";
        }
    }

    private void DeleteResource(ResourceRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        var message = row.IsWorld
            ? $"确定删除存档「{row.Title}」？\n\n整个世界文件夹都会被删除，无法恢复。"
            : $"确定删除「{row.Title}」？此操作无法恢复。";

        if (MessageBox.Show(message, "删除" + (SelectedTab?.ShortName ?? ResourceRepository.DisplayName(row.Kind)),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (_stack.Resources.Delete(row.Resource))
        {
            StatusText = $"已删除 {row.Title}。";
            _ = RefreshAsync();
        }
        else
        {
            StatusText = "删除失败，文件可能被占用。";
        }
    }

    private void ShowInfo(ResourceRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        var resource = row.Resource;
        var lines = new List<string>
        {
            $"名称：{resource.Title}",
            $"类型：{ResourceRepository.DisplayName(resource.Kind)}",
            $"状态：{(resource.Enabled ? "已启用" : "已禁用")}",
            $"文件：{resource.FileName}",
            $"大小：{resource.SizeText}",
            $"修改时间：{resource.ModifyText}",
        };

        if (!string.IsNullOrWhiteSpace(resource.Version))
        {
            lines.Add($"版本：{resource.Version}");
        }

        if (!string.IsNullOrWhiteSpace(resource.GameVersion))
        {
            lines.Add($"游戏版本：{resource.GameVersion}");
        }

        if (!string.IsNullOrWhiteSpace(resource.Description))
        {
            lines.Add(string.Empty);
            lines.Add("描述：");
            lines.Add(resource.Description!.Trim());
        }

        lines.Add(string.Empty);
        lines.Add("路径：");
        lines.Add(resource.FilePath);

        DetailTitle = resource.Title;
        DetailSubtitle = $"{ResourceRepository.DisplayName(resource.Kind)} · {(resource.Enabled ? "已启用" : "已禁用")}";
        DetailText = string.Join(Environment.NewLine, lines);
        DetailPath = resource.FilePath;
        IsDetailOpen = true;
    }

    private void BackupWorld(ResourceRowItem? row)
    {
        if (row is null || !row.IsWorld)
        {
            return;
        }

        var backups = Path.Combine(_instanceDirectory, "backups");
        var target = _stack.Resources.BackupWorld(row.Resource, backups);

        StatusText = target is null
            ? "备份失败，请检查磁盘空间或文件占用。"
            : $"已备份到 {target}";
    }

    private void OpenDetailFolder()
    {
        if (string.IsNullOrWhiteSpace(DetailPath))
        {
            return;
        }

        try
        {
            var directory = Directory.Exists(DetailPath)
                ? DetailPath
                : Path.GetDirectoryName(DetailPath);

            if (directory is not null)
            {
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _stack.Log.Warn("打开资源位置失败", ex);
        }
    }

    private static string FilterFor(ResourceKind kind) => kind switch
    {
        ResourceKind.Mod => "模组 (*.jar)|*.jar|所有文件 (*.*)|*.*",
        ResourceKind.ResourcePack => "资源包 (*.zip;*.mcpack)|*.zip;*.mcpack|所有文件 (*.*)|*.*",
        ResourceKind.ShaderPack => "光影包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        ResourceKind.Schematic =>
            "原理图 (*.litematic;*.schematic;*.schem;*.nbt)|*.litematic;*.schematic;*.schem;*.nbt|所有文件 (*.*)|*.*",
        _ => "所有文件 (*.*)|*.*",
    };

    // ---------------------------------------------------------------- 详情弹窗

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        private set => SetProperty(ref _isDetailOpen, value);
    }

    public string DetailTitle
    {
        get => _detailTitle;
        private set => SetProperty(ref _detailTitle, value);
    }

    public string DetailSubtitle
    {
        get => _detailSubtitle;
        private set => SetProperty(ref _detailSubtitle, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string DetailPath
    {
        get => _detailPath;
        private set => SetProperty(ref _detailPath, value);
    }
}
