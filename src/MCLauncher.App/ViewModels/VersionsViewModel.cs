using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services;
using MCLauncher.Core.Utils;

namespace MCLauncher.App.ViewModels;

/// <summary>左栏的游戏目录条目。</summary>
public sealed class GameFolderItem
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    /// <summary>是否是「当前文件夹 / 官方启动器文件夹」这类内置条目。</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>内置目录每次启动都会补回来，因此不提供移除按钮。</summary>
    public bool CanRemove => !IsBuiltIn;
}

/// <summary>实例列表的一行；命令挂在页面 VM 上，通过 <see cref="Owner"/> 访问。</summary>
public sealed class InstanceRowItem : ViewModelBase
{
    private bool _isCurrent;

    public required InstalledVersionInfo Info { get; init; }

    public required VersionsViewModel Owner { get; init; }

    public string Id => Info.Id;

    public string Display => Info.Display;

    public string Subtitle => Info.Subtitle;

    /// <summary>是否是主页当前选中的实例。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}

/// <summary>安装新游戏里的加载器卡片。</summary>
public sealed class LoaderCardItem : ViewModelBase
{
    private string _status = "不安装";
    private bool _isEnabled = true;
    private bool _isSelected;

    public required ModLoaderKind Kind { get; init; }

    public required string Title { get; init; }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>安装弹窗里的一行并发下载进度。</summary>
public sealed class InstallFileItem : ViewModelBase
{
    private double _progress;
    private string _detail = string.Empty;

    public InstallFileItem(string name)
    {
        Name = name;
        DisplayName = Path.GetFileName(name);
    }

    /// <summary>下载请求里的完整名称。</summary>
    public string Name { get; }

    /// <summary>界面上只显示文件名。</summary>
    public string DisplayName { get; }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>已下载 / 总大小。</summary>
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public void Update(long received, long total)
    {
        Progress = total > 0 ? Math.Clamp((double)received / total, 0d, 1d) : 0d;
        Detail = total > 0
            ? $"{FormatBytes(received)} / {FormatBytes(total)}"
            : FormatBytes(received);
    }

    public static string FormatBytes(double bytes)
    {
        var value = Math.Max(0, bytes);
        string[] units = { "B", "KB", "MB", "GB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} B" : $"{value:0.0} {units[unit]}";
    }

    /// <summary>下载速度文案；速度为 0 时返回空串，界面据此折叠。</summary>
    public static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond > 0 ? $"下载速度 {FormatBytes(bytesPerSecond)}/s" : string.Empty;
}

/// <summary>
/// 实例页：左侧游戏目录（本地 .minecraft / 官方启动器目录 / 自定义目录），
/// 右侧实例列表。安装新游戏可以同时装加载器，安装整合包支持本地压缩包。
/// </summary>
public sealed class VersionsViewModel : ViewModelBase
{
    private readonly LauncherStack _stack;
    private readonly Action _openGlobalSettings;
    private readonly Action<string?> _openInstanceSettings;
    private readonly Action<string> _launchInstance;
    private readonly Action<string> _openInstanceManage;

    private readonly AsyncRelayCommand _installGameCommand;
    private readonly AsyncRelayCommand _installModpackCommand;
    private readonly AsyncRelayCommand _openInstallDialogCommand;
    private readonly AsyncRelayCommand _downloadModpackUrlCommand;

    private readonly List<InstalledVersionInfo> _allInstances = new();
    private IReadOnlyList<VersionManifestEntry> _allVersions = Array.Empty<VersionManifestEntry>();

    private GameFolderItem? _selectedFolder;
    private InstanceRowItem? _selectedInstance;
    private string _searchText = string.Empty;
    private string _statusText = "选择左侧游戏目录查看实例。";
    private double _progress;
    private string _progressSpeedText = string.Empty;
    private bool _isBusy;

    private bool _isInstallDialogOpen;
    private string _newInstanceName = string.Empty;
    private bool _instanceNameTouched;
    private bool _suppressNameTouch;
    private string? _selectedMinecraftVersion;
    private bool _includeReleases = true;
    private bool _includeSnapshots;
    private bool _includeAprilFools;
    private bool _includeOldVersions;
    private LoaderCardItem? _selectedLoaderCard;
    private LoaderVersionOption? _selectedLoaderVersion;
    private string _installStatus = string.Empty;
    private bool _isInstalling;
    private double _installProgress;
    private string _installSpeedText = string.Empty;
    private readonly Dictionary<string, InstallFileItem> _installFileRows = new(StringComparer.Ordinal);
    private bool _isLoadingLoaderVersions;

    private bool _isModpackDialogOpen;
    private bool _isModpackPickerOpen;
    private bool _isModpackUrlDialogOpen;
    private string _modpackPath = string.Empty;
    private ModpackInfo? _modpackInfo;
    private string _modpackTitle = string.Empty;
    private string _modpackDetail = string.Empty;
    private string _modpackNote = string.Empty;
    private string _modpackInstanceName = string.Empty;
    private string _modpackStatus = string.Empty;
    private bool _isModpackInstalling;
    private double _modpackProgress;
    private string _modpackUrl = string.Empty;
    private string _modpackUrlStatus = string.Empty;
    private bool _isModpackDownloading;
    private double _modpackDownloadProgress;
    private string _modpackDownloadSpeedText = string.Empty;
    private string _modpackSpeedText = string.Empty;
    private readonly DownloadFileRows _modpackRows = new();
    private CancellationTokenSource? _modpackDownloadCts;

    private bool _suppressStackChanged;

    public VersionsViewModel(
        LauncherStack stack,
        Action openGlobalSettings,
        Action<string?> openInstanceSettings,
        Action<string> launchInstance,
        Action<string> openInstanceManage)
    {
        _stack = stack;
        _openGlobalSettings = openGlobalSettings;
        _openInstanceSettings = openInstanceSettings;
        _launchInstance = launchInstance;
        _openInstanceManage = openInstanceManage;

        RefreshCommand = new RelayCommand(RefreshAll);
        AddFolderCommand = new RelayCommand(AddGameFolder);
        RemoveFolderCommand = new RelayCommand<GameFolderItem>(RemoveGameFolder);
        SetCurrentCommand = new RelayCommand<InstanceRowItem>(SetCurrent);
        DeleteCommand = new RelayCommand<InstanceRowItem>(DeleteInstance);
        OpenFolderCommand = new RelayCommand<InstanceRowItem>(OpenInstanceFolder);
        InstanceSettingsCommand = new RelayCommand<InstanceRowItem>(row =>
        {
            if (row is not null)
            {
                _openInstanceSettings(row.Id);
            }
        });
        LaunchRowCommand = new RelayCommand<InstanceRowItem>(LaunchInstance);
        ManageCommand = new RelayCommand<InstanceRowItem>(ManageInstance);
        CompleteInstanceCommand = new RelayCommand<InstanceRowItem>(row => _ = CompleteInstanceAsync(row));
        OpenGlobalSettingsCommand = new RelayCommand(_openGlobalSettings);
        OpenModpackCommand = new RelayCommand(OpenModpackPicker);
        OpenLocalModpackCommand = new AsyncRelayCommand(ImportLocalModpackAsync);
        OpenModpackUrlCommand = new RelayCommand(OpenModpackUrlDialog);
        BrowseModpackCommand = new RelayCommand(OpenModpackBrowser);
        SelectLoaderCommand = new RelayCommand<LoaderCardItem>(SelectLoader);
        CloseDialogsCommand = new RelayCommand(CloseDialogs);

        Download = new ResourceDownloadViewModel(stack);
        Download.ModpackDownloaded += archive =>
            UiThread.Post(() => _ = ShowModpackConfirmAsync(archive));

        _openInstallDialogCommand = new AsyncRelayCommand(OpenInstallDialogAsync);
        OpenInstallCommand = _openInstallDialogCommand;
        _installGameCommand = new AsyncRelayCommand(InstallGameAsync, () => CanConfirmInstall);
        ConfirmInstallCommand = _installGameCommand;
        _installModpackCommand = new AsyncRelayCommand(InstallModpackAsync, () => CanConfirmModpack);
        ConfirmModpackCommand = _installModpackCommand;
        _downloadModpackUrlCommand = new AsyncRelayCommand(DownloadModpackFromUrlAsync, () => CanDownloadModpackUrl);
        DownloadModpackUrlCommand = _downloadModpackUrlCommand;

        foreach (var kind in new[] { ModLoaderKind.Forge, ModLoaderKind.NeoForge, ModLoaderKind.Fabric, ModLoaderKind.Quilt })
        {
            LoaderCards.Add(new LoaderCardItem { Kind = kind, Title = ModLoaderService.DisplayName(kind) });
        }

        _stack.Changed += (_, _) =>
        {
            if (!_suppressStackChanged)
            {
                UiThread.Post(RefreshAll);
            }
        };

        RefreshAll();
    }

    // ---------------------------------------------------------------- 页面数据

    public ObservableCollection<GameFolderItem> Folders { get; } = new();

    public ObservableCollection<InstanceRowItem> Instances { get; } = new();

    public GameFolderItem? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!SetProperty(ref _selectedFolder, value) || value is null)
            {
                return;
            }

            if (LauncherStack.IsSameGameFolder(_stack.GameRoot, value.Path))
            {
                return;
            }

            _stack.Config.Settings.SelectedGameFolder = value.Path;
            _stack.Config.Save();
            RefreshInstances();
            NotifyChanged();
            StatusText = $"已切换到 {value.Name}";
        }
    }

    public InstanceRowItem? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value))
            {
                return;
            }

            if (value is not null)
            {
                _stack.Config.Settings.SelectedInstanceId = value.Id;
                _stack.Config.Save();
            }

            foreach (var row in Instances)
            {
                row.IsCurrent = value is not null && row.Id == value.Id;
            }

            NotifyChanged();
        }
    }

    public bool HasInstances => Instances.Count > 0;

    public bool ShowEmptyHint => Instances.Count == 0;

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

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>实例页底部补全时的实时下载速度文案。</summary>
    public string ProgressSpeedText
    {
        get => _progressSpeedText;
        private set => SetProperty(ref _progressSpeedText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    // ---------------------------------------------------------------- 安装新游戏

    public ObservableCollection<string> MinecraftVersions { get; } = new();

    public ObservableCollection<LoaderCardItem> LoaderCards { get; } = new();

    public ObservableCollection<LoaderVersionOption> LoaderVersions { get; } = new();

    public bool IsInstallDialogOpen
    {
        get => _isInstallDialogOpen;
        private set
        {
            if (SetProperty(ref _isInstallDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsAnyDialogOpen));
            }
        }
    }

    public string NewInstanceName
    {
        get => _newInstanceName;
        set
        {
            if (!SetProperty(ref _newInstanceName, value))
            {
                return;
            }

            if (!_suppressNameTouch)
            {
                _instanceNameTouched = true;
            }
        }
    }

    public string? SelectedMinecraftVersion
    {
        get => _selectedMinecraftVersion;
        set
        {
            if (!SetProperty(ref _selectedMinecraftVersion, value) || value is null)
            {
                return;
            }

            if (!_instanceNameTouched)
            {
                SetDefaultInstanceName(DefaultInstanceName(value, _selectedLoaderCard?.Kind));
            }

            if (_selectedLoaderCard is not null)
            {
                _ = LoadLoaderVersionsAsync();
            }

            RaiseInstallState();
        }
    }

    public bool IncludeReleases
    {
        get => _includeReleases;
        set
        {
            if (SetProperty(ref _includeReleases, value)) BuildMinecraftVersionList();
        }
    }

    public bool IncludeSnapshots
    {
        get => _includeSnapshots;
        set
        {
            if (SetProperty(ref _includeSnapshots, value)) BuildMinecraftVersionList();
        }
    }

    public bool IncludeAprilFools
    {
        get => _includeAprilFools;
        set
        {
            if (SetProperty(ref _includeAprilFools, value)) BuildMinecraftVersionList();
        }
    }

    public bool IncludeOldVersions
    {
        get => _includeOldVersions;
        set
        {
            if (SetProperty(ref _includeOldVersions, value)) BuildMinecraftVersionList();
        }
    }

    public LoaderCardItem? SelectedLoaderCard
    {
        get => _selectedLoaderCard;
        private set
        {
            if (SetProperty(ref _selectedLoaderCard, value))
            {
                OnPropertyChanged(nameof(HasSelectedLoader));
                RaiseInstallState();
            }
        }
    }

    public bool HasSelectedLoader => _selectedLoaderCard is not null;

    public LoaderVersionOption? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set
        {
            if (SetProperty(ref _selectedLoaderVersion, value))
            {
                if (_selectedLoaderCard is not null)
                {
                    _selectedLoaderCard.Status = value?.Display ?? "选择版本";
                }

                RaiseInstallState();
            }
        }
    }

    public string InstallStatus
    {
        get => _installStatus;
        private set => SetProperty(ref _installStatus, value);
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (SetProperty(ref _isInstalling, value))
            {
                RaiseInstallState();
            }
        }
    }

    public double InstallProgress
    {
        get => _installProgress;
        private set => SetProperty(ref _installProgress, value);
    }

    /// <summary>安装过程中正在并发下载的文件。</summary>
    public ObservableCollection<InstallFileItem> InstallFiles { get; } = new();

    /// <summary>实时下载速度文案。</summary>
    public string InstallSpeedText
    {
        get => _installSpeedText;
        private set => SetProperty(ref _installSpeedText, value);
    }

    public bool CanConfirmInstall =>
        !IsInstalling &&
        !_isLoadingLoaderVersions &&
        !string.IsNullOrWhiteSpace(SelectedMinecraftVersion) &&
        (_selectedLoaderCard is null || SelectedLoaderVersion is not null);

    // ---------------------------------------------------------------- 安装整合包

    public bool IsModpackDialogOpen
    {
        get => _isModpackDialogOpen;
        private set
        {
            if (SetProperty(ref _isModpackDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsAnyDialogOpen));
            }
        }
    }

    /// <summary>三步选择弹窗：本地导入 / 链接下载 / 在线浏览。</summary>
    public bool IsModpackPickerOpen
    {
        get => _isModpackPickerOpen;
        private set
        {
            if (SetProperty(ref _isModpackPickerOpen, value))
            {
                OnPropertyChanged(nameof(IsAnyDialogOpen));
            }
        }
    }

    /// <summary>从互联网下载整合包的弹窗。</summary>
    public bool IsModpackUrlDialogOpen
    {
        get => _isModpackUrlDialogOpen;
        private set
        {
            if (SetProperty(ref _isModpackUrlDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsAnyDialogOpen));
            }
        }
    }

    public string ModpackUrl
    {
        get => _modpackUrl;
        set
        {
            if (!SetProperty(ref _modpackUrl, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanDownloadModpackUrl));
            _downloadModpackUrlCommand?.RaiseCanExecuteChanged();
        }
    }

    public string ModpackUrlStatus
    {
        get => _modpackUrlStatus;
        private set => SetProperty(ref _modpackUrlStatus, value);
    }

    public bool IsModpackDownloading
    {
        get => _isModpackDownloading;
        private set
        {
            if (SetProperty(ref _isModpackDownloading, value))
            {
                OnPropertyChanged(nameof(CanDownloadModpackUrl));
                _downloadModpackUrlCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public double ModpackDownloadProgress
    {
        get => _modpackDownloadProgress;
        private set => SetProperty(ref _modpackDownloadProgress, value);
    }

    /// <summary>整合包链接下载的实时速度文案。</summary>
    public string ModpackDownloadSpeedText
    {
        get => _modpackDownloadSpeedText;
        private set => SetProperty(ref _modpackDownloadSpeedText, value);
    }

    public bool CanDownloadModpackUrl =>
        !IsModpackDownloading && !string.IsNullOrWhiteSpace(_modpackUrl);

    public string ModpackTitle
    {
        get => _modpackTitle;
        private set => SetProperty(ref _modpackTitle, value);
    }

    public string ModpackDetail
    {
        get => _modpackDetail;
        private set => SetProperty(ref _modpackDetail, value);
    }

    public string ModpackNote
    {
        get => _modpackNote;
        private set => SetProperty(ref _modpackNote, value);
    }

    public bool HasModpackNote => !string.IsNullOrWhiteSpace(_modpackNote);

    public string ModpackInstanceName
    {
        get => _modpackInstanceName;
        set => SetProperty(ref _modpackInstanceName, value);
    }

    public string ModpackStatus
    {
        get => _modpackStatus;
        private set => SetProperty(ref _modpackStatus, value);
    }

    public bool IsModpackInstalling
    {
        get => _isModpackInstalling;
        private set
        {
            if (SetProperty(ref _isModpackInstalling, value))
            {
                _installModpackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public double ModpackProgress
    {
        get => _modpackProgress;
        private set => SetProperty(ref _modpackProgress, value);
    }

    /// <summary>整合包安装时的实时下载速度文案。</summary>
    public string ModpackSpeedText
    {
        get => _modpackSpeedText;
        private set => SetProperty(ref _modpackSpeedText, value);
    }

    /// <summary>整合包下载 / 安装时的文件队列（单文件时只有一行）。</summary>
    public ObservableCollection<InstallFileItem> ModpackFiles => _modpackRows.Items;

    public bool CanConfirmModpack =>
        !IsModpackInstalling &&
        _modpackInfo is not null &&
        _modpackInfo.Kind is not (ModpackKind.Unknown or ModpackKind.Rar) &&
        !string.IsNullOrWhiteSpace(_modpackInfo.GameVersion);

    /// <summary>任意弹窗打开时为 true，用于显示遮罩。</summary>
    public bool IsAnyDialogOpen =>
        IsInstallDialogOpen || IsModpackDialogOpen || IsModpackPickerOpen || IsModpackUrlDialogOpen;

    // ---------------------------------------------------------------- 命令

    public ICommand RefreshCommand { get; }

    public ICommand AddFolderCommand { get; }

    public ICommand RemoveFolderCommand { get; }

    public ICommand SetCurrentCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand InstanceSettingsCommand { get; }

    public ICommand LaunchRowCommand { get; }

    public ICommand ManageCommand { get; }

    public ICommand CompleteInstanceCommand { get; }

    public ICommand OpenGlobalSettingsCommand { get; }

    public ICommand OpenInstallCommand { get; }

    public ICommand OpenModpackCommand { get; }

    /// <summary>从本地文件导入整合包。</summary>
    public ICommand OpenLocalModpackCommand { get; }

    /// <summary>从互联网下载整合包。</summary>
    public ICommand OpenModpackUrlCommand { get; }

    /// <summary>到 CurseForge / Modrinth 浏览并安装整合包。</summary>
    public ICommand BrowseModpackCommand { get; }

    public ICommand DownloadModpackUrlCommand { get; }

    /// <summary>整合包在线浏览与下载（复用资源下载界面）。</summary>
    public ResourceDownloadViewModel Download { get; }

    public ICommand SelectLoaderCommand { get; }

    public ICommand CloseDialogsCommand { get; }

    public ICommand ConfirmInstallCommand { get; }

    public ICommand ConfirmModpackCommand { get; }

    // ---------------------------------------------------------------- 列表刷新

    public void RefreshAll()
    {
        RefreshFolders();
        RefreshInstances();
    }

    private void RefreshFolders()
    {
        var before = _stack.Config.Settings.GameFolders.Count;
        _stack.Config.EnsureKnownGameFolders(AppContext.BaseDirectory);
        if (_stack.Config.Settings.GameFolders.Count != before)
        {
            _stack.Config.Save();
        }

        var selectedPath = _stack.GameRoot;
        var launcherFolder = Path.Combine(AppContext.BaseDirectory, ".minecraft");

        Folders.Clear();

        foreach (var path in _stack.Config.Settings.GameFolders.Where(Directory.Exists))
        {
            var name = LauncherStack.IsSameGameFolder(path, launcherFolder)
                ? "当前文件夹"
                : LauncherStack.IsSameGameFolder(path, LauncherStack.OfficialGameFolder)
                    ? "官方启动器文件夹"
                    : new DirectoryInfo(path).Name;

            Folders.Add(new GameFolderItem
            {
                Path = path,
                Name = name,
                IsBuiltIn = LauncherStack.IsSameGameFolder(path, launcherFolder) ||
                            LauncherStack.IsSameGameFolder(path, LauncherStack.OfficialGameFolder),
            });
        }

        var selected = Folders.FirstOrDefault(folder => LauncherStack.IsSameGameFolder(folder.Path, selectedPath))
                       ?? Folders.FirstOrDefault();

        if (selected is not null && !LauncherStack.IsSameGameFolder(selected.Path, selectedPath))
        {
            _stack.Config.Settings.SelectedGameFolder = selected.Path;
            _stack.Config.Save();
        }

        SetProperty(ref _selectedFolder, selected, nameof(SelectedFolder));
    }

    private void RefreshInstances(string? selectId = null)
    {
        var previousId = selectId ?? SelectedInstance?.Id ?? _stack.Config.Settings.SelectedInstanceId;
        var currentId = _stack.Config.Settings.SelectedInstanceId;

        _allInstances.Clear();
        _allInstances.AddRange(_stack.GetInstalledVersions());

        ApplyFilterCore(previousId, currentId);
    }

    private void ApplyFilter()
    {
        ApplyFilterCore(SelectedInstance?.Id ?? _stack.Config.Settings.SelectedInstanceId,
            _stack.Config.Settings.SelectedInstanceId);
    }

    private void ApplyFilterCore(string? selectedId, string? currentId)
    {
        var filter = SearchText.Trim();

        Instances.Clear();
        foreach (var info in _allInstances)
        {
            if (filter.Length > 0 &&
                !info.Display.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !info.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !(info.LoaderText?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            Instances.Add(new InstanceRowItem
            {
                Info = info,
                Owner = this,
                IsCurrent = string.Equals(info.Id, currentId, StringComparison.OrdinalIgnoreCase),
            });
        }

        OnPropertyChanged(nameof(HasInstances));
        OnPropertyChanged(nameof(ShowEmptyHint));

        _selectedInstance = null;
        OnPropertyChanged(nameof(SelectedInstance));

        if (Instances.Count == 0)
        {
            return;
        }

        var target = Instances.FirstOrDefault(row =>
                         string.Equals(row.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                     ?? Instances.FirstOrDefault(row => row.IsCurrent)
                     ?? Instances[0];

        SelectedInstance = target;
    }

    private void NotifyChanged()
    {
        if (_suppressStackChanged)
        {
            return;
        }

        _suppressStackChanged = true;
        try
        {
            _stack.RaiseChanged();
        }
        finally
        {
            _suppressStackChanged = false;
        }
    }

    // ---------------------------------------------------------------- 目录操作

    private void AddGameFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择游戏目录（.minecraft）",
            Multiselect = false,
        };

        if (Directory.Exists(_stack.GameRoot))
        {
            dialog.InitialDirectory = _stack.GameRoot;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!_stack.AddGameFolder(dialog.FolderName))
        {
            StatusText = "这个目录已经在列表里了。";
            return;
        }

        RefreshFolders();
        SelectedFolder = Folders.FirstOrDefault(folder =>
            LauncherStack.IsSameGameFolder(folder.Path, dialog.FolderName));
        StatusText = $"已添加游戏目录：{dialog.FolderName}";
    }

    private void RemoveGameFolder(GameFolderItem? folder)
    {
        if (folder is null)
        {
            return;
        }

        if (!_stack.RemoveGameFolder(folder.Path))
        {
            StatusText = "至少保留一个游戏目录。";
            return;
        }

        RefreshAll();
        StatusText = $"已从列表移除：{folder.Name}";
    }

    // ---------------------------------------------------------------- 实例操作

    private void SetCurrent(InstanceRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedInstance = row;
        StatusText = $"已把 {row.Display} 设为当前实例。";
    }

    private void LaunchInstance(InstanceRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedInstance = row;
        _launchInstance(row.Id);
    }

    private void ManageInstance(InstanceRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedInstance = row;
        _openInstanceManage(row.Id);
    }

    private void OpenInstanceFolder(InstanceRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            var directory = _stack.GetInstanceDirectory(row.Id, _stack.IsIsolated(row.Id));
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _stack.Log.Warn("打开实例目录失败", ex);
            StatusText = "打开目录失败：" + ex.Message;
        }
    }

    private void DeleteInstance(InstanceRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        var result = MessageBox.Show(
            $"确定删除实例「{row.Display}」？\n\n整个版本目录（含 mods / 存档）都会被删除，且不可恢复。",
            "删除实例",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (_stack.Instances.Delete(_stack.GameRoot, row.Id))
            {
                if (string.Equals(_stack.Config.Settings.SelectedInstanceId, row.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _stack.Config.Settings.SelectedInstanceId = null;
                }

                _stack.Config.Save();
                StatusText = $"已删除实例：{row.Display}";
                RefreshInstances();
                NotifyChanged();
            }
            else
            {
                StatusText = "删除失败：目录不存在。";
            }
        }
        catch (Exception ex)
        {
            _stack.Log.Error($"删除实例 {row.Id} 失败", ex);
            StatusText = "删除失败：" + ex.Message;
        }
    }

    private async Task CompleteInstanceAsync(InstanceRowItem? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Progress = 0;
        ProgressSpeedText = string.Empty;
        StatusText = $"正在校验并补全 {row.Display} …";

        try
        {
            var report = await _stack.CompleteVersionAsync(
                row.Id,
                new Progress<InstallProgress>(p =>
                {
                    Progress = p.Fraction;
                    ProgressSpeedText = InstallFileItem.FormatSpeed(p.BytesPerSecond);
                    StatusText = p.Total > 0
                        ? $"{p.Stage} {p.Completed}/{p.Total}"
                        : p.Stage;
                }));

            StatusText = report.Success
                ? $"补全完成：新下载 {report.Downloaded} 个，跳过 {report.Skipped} 个"
                : $"补全失败：{report.Failed} 个文件未能下载";
        }
        catch (Exception ex)
        {
            _stack.Log.Error($"补全 {row.Id} 失败", ex);
            StatusText = "补全失败：" + ex.Message;
        }
        finally
        {
            Progress = 1;
            ProgressSpeedText = string.Empty;
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- 安装新游戏

    private async Task OpenInstallDialogAsync()
    {
        if (IsInstalling)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在获取版本清单…";

        try
        {
            await EnsureManifestAsync();
            BuildMinecraftVersionList();

            _instanceNameTouched = false;
            _selectedLoaderCard = null;
            OnPropertyChanged(nameof(SelectedLoaderCard));
            SelectedLoaderVersion = null;
            LoaderVersions.Clear();
            ResetLoaderCards();

            var latest = MinecraftVersions.FirstOrDefault();
            _selectedMinecraftVersion = latest;
            OnPropertyChanged(nameof(SelectedMinecraftVersion));
            SetDefaultInstanceName(latest ?? string.Empty);

            InstallStatus = latest is null ? "没有可安装的正式版版本。" : "选择版本与要一并安装的加载器。";
            InstallProgress = 0;
            IsInstallDialogOpen = true;
            RaiseInstallState();
            StatusText = "安装新游戏：选择 Minecraft 版本与加载器。";
        }
        catch (Exception ex)
        {
            _stack.Log.Error("打开安装新游戏失败", ex);
            StatusText = "获取版本清单失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnsureManifestAsync()
    {
        if (_allVersions.Count > 0)
        {
            return;
        }

        var manifest = await _stack.Manifests.GetAsync();
        _allVersions = manifest.Versions
            .GroupBy(version => version.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(version => version.ReleaseTime ?? version.Time ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private void BuildMinecraftVersionList()
    {
        var previous = SelectedMinecraftVersion;

        MinecraftVersions.Clear();
        foreach (var entry in _allVersions)
        {
            var include = entry switch
            {
                { IsRelease: true } => IncludeReleases,
                { IsSnapshot: true, IsAprilFools: true } => IncludeAprilFools,
                { IsSnapshot: true } => IncludeSnapshots,
                { IsOld: true } => IncludeOldVersions,
                // old_snapshot 是 Minecraft 后来加入的类型（如愚人节之后单独存档），
                // 但它的条目一般也会匹配 IsSnapshot=false、IsOld=true——已经被上面覆盖。
                _ => IncludeOldVersions,
            };

            if (!include) continue;

            MinecraftVersions.Add(entry.Id);
        }

        if (previous is not null && MinecraftVersions.Contains(previous))
        {
            _selectedMinecraftVersion = previous;
        }
        else
        {
            _selectedMinecraftVersion = MinecraftVersions.FirstOrDefault();
        }

        OnPropertyChanged(nameof(SelectedMinecraftVersion));
        SetDefaultInstanceName(DefaultInstanceName(_selectedMinecraftVersion, _selectedLoaderCard?.Kind));
        RaiseInstallState();
    }

    private void SelectLoader(LoaderCardItem? card)
    {
        if (card is null || IsInstalling || !card.IsEnabled)
        {
            return;
        }

        if (card.IsSelected)
        {
            card.IsSelected = false;
            SelectedLoaderCard = null;
            SelectedLoaderVersion = null;
            LoaderVersions.Clear();
            ResetLoaderCards();

            if (!_instanceNameTouched)
            {
                SetDefaultInstanceName(DefaultInstanceName(SelectedMinecraftVersion, null));
            }

            InstallStatus = "未选择加载器，将只安装原版。";
            return;
        }

        foreach (var item in LoaderCards)
        {
            item.IsSelected = item.Kind == card.Kind;
        }

        SelectedLoaderCard = card;
        SelectedLoaderVersion = null;
        LoaderVersions.Clear();
        UpdateLoaderCards();

        if (!_instanceNameTouched)
        {
            SetDefaultInstanceName(DefaultInstanceName(SelectedMinecraftVersion, card.Kind));
        }

        _ = LoadLoaderVersionsAsync();
    }

    private void ResetLoaderCards()
    {
        foreach (var card in LoaderCards)
        {
            card.IsSelected = false;
            card.IsEnabled = true;
            card.Status = "不安装";
        }
    }

    private void UpdateLoaderCards()
    {
        var selected = SelectedLoaderCard;
        foreach (var card in LoaderCards)
        {
            if (selected is null)
            {
                card.IsEnabled = true;
                card.Status = "不安装";
                continue;
            }

            if (card.Kind == selected.Kind)
            {
                card.IsEnabled = true;
                card.Status = SelectedLoaderVersion?.Display ?? "正在获取版本…";
            }
            else
            {
                card.IsEnabled = false;
                card.Status = $"与 {selected.Title} 不兼容";
            }
        }
    }

    private async Task LoadLoaderVersionsAsync()
    {
        var card = SelectedLoaderCard;
        var gameVersion = SelectedMinecraftVersion;

        if (card is null || string.IsNullOrWhiteSpace(gameVersion))
        {
            return;
        }

        var kind = card.Kind;
        _isLoadingLoaderVersions = true;
        RaiseInstallState();

        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        card.Status = "正在获取版本…";
        InstallStatus = $"正在获取 {card.Title} 支持 {gameVersion} 的版本…";

        try
        {
            var versions = await Task.Run(() => _stack.ModLoaders.ListLoaderVersionsAsync(kind, gameVersion!));

            if (SelectedLoaderCard?.Kind != kind || SelectedMinecraftVersion != gameVersion)
            {
                return;
            }

            foreach (var option in versions)
            {
                LoaderVersions.Add(option);
            }

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(version => version.IsRecommended)
                                    ?? LoaderVersions.FirstOrDefault();

            card.Status = SelectedLoaderVersion?.Display
                          ?? (LoaderVersions.Count == 0 ? "不可用" : "选择版本");

            InstallStatus = LoaderVersions.Count == 0
                ? $"{card.Title} 没有支持 {gameVersion} 的版本，请换一个加载器或原版版本。"
                : $"共 {LoaderVersions.Count} 个 {card.Title} 版本可用于 {gameVersion}。";
        }
        catch (Exception ex)
        {
            _stack.Log.Error($"获取 {card.Title} 版本失败", ex);
            card.Status = "获取失败";
            InstallStatus = $"获取 {card.Title} 版本失败：{ex.Message}";
        }
        finally
        {
            _isLoadingLoaderVersions = false;
            UpdateLoaderCards();
            RaiseInstallState();
        }
    }

    private async Task InstallGameAsync()
    {
        var gameVersion = SelectedMinecraftVersion;
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            InstallStatus = "请选择 Minecraft 版本。";
            return;
        }

        var card = SelectedLoaderCard;
        var loaderVersion = SelectedLoaderVersion?.Version;

        if (card is not null && string.IsNullOrWhiteSpace(loaderVersion))
        {
            InstallStatus = "请选择加载器版本。";
            return;
        }

        IsInstalling = true;
        InstallProgress = 0;
        ClearInstallFiles();
        InstallStatus = $"准备安装 {gameVersion}…";

        try
        {
            var trimmedName = NewInstanceName.Trim();
            var loaderName = card?.Title;

            if (card is null &&
                (trimmedName.Length == 0 || string.Equals(trimmedName, gameVersion, StringComparison.OrdinalIgnoreCase)))
            {
                // 不装加载器且名字就是版本号：直接安装到 versions/<版本号>，并登记成显式实例。
                InstallStatus = $"正在安装原版 {gameVersion}…";
                var baseVersion = await FetchBaseVersionAsync(gameVersion!);
                var vanillaReport = await _stack.Installer.InstallInstanceAsync(
                    _stack.GameRoot, gameVersion!, baseVersion, null,
                    _stack.BuildInstallOptions(), new Progress<InstallProgress>(ReportInstallProgress));

                if (!vanillaReport.Success)
                {
                    throw new InvalidOperationException(
                        $"原版 {gameVersion} 安装失败：{vanillaReport.Failed} 个文件未能下载");
                }

                _stack.Instances.RegisterVersionAsInstance(_stack.GameRoot, gameVersion!);
                _stack.Config.Settings.SelectedInstanceId = gameVersion;
                _stack.Config.Save();
                InstallStatus = $"已安装原版 {gameVersion}，并设为当前实例。";
            }
            else
            {
                var fallback = card is null
                    ? gameVersion!
                    : $"{loaderName}{gameVersion}";
                var instanceId = _stack.Instances.CreateUniqueInstanceId(
                    _stack.GameRoot, trimmedName, fallback);

                // 原版与加载器在内存里合并，直接写进实例目录，再一次性并发下载全部文件；
                // versions 下不会出现临时原版基底目录。Forge / NeoForge 的处理器也在这一步执行。
                InstallStatus = card is null
                    ? $"正在安装原版 {gameVersion}…"
                    : $"正在安装 {loaderName} {loaderVersion}…";

                var baseVersion = await FetchBaseVersionAsync(gameVersion!);
                GameVersionJson? loaderProfile = null;

                try
                {
                    if (card is not null)
                    {
                        loaderProfile = card.Kind is ModLoaderKind.Fabric or ModLoaderKind.Quilt
                            ? await _stack.ModLoaders.GetFabricLikeProfileAsync(
                                card.Kind, gameVersion!, loaderVersion!)
                            : await _stack.ModLoaders.GetForgeLikeVersionAsync(
                                card.Kind,
                                _stack.GameRoot,
                                gameVersion!,
                                loaderVersion!,
                                baseVersion,
                                VersionRepository.VersionJarPath(_stack.GameRoot, instanceId),
                                _stack.BuildInstallOptions(),
                                new Progress<InstallProgress>(ReportInstallProgress));
                    }

                    var report = await _stack.Installer.InstallInstanceAsync(
                        _stack.GameRoot, instanceId, baseVersion, loaderProfile,
                        _stack.BuildInstallOptions(), new Progress<InstallProgress>(ReportInstallProgress));

                    if (!report.Success)
                    {
                        throw new InvalidOperationException(
                            $"安装失败：{report.Failed} 个文件未能下载");
                    }
                }
                catch
                {
                    // 半成品会以「实例」身份出现在列表里，失败就整个删掉；
                    // libraries / assets 是跨版本共享的，不受影响。
                    try
                    {
                        _stack.Instances.Delete(_stack.GameRoot, instanceId);
                    }
                    catch (Exception cleanup)
                    {
                        _stack.Log.Warn($"清理未完成的实例 {instanceId} 失败", cleanup);
                    }

                    throw;
                }

                _stack.Instances.RegisterSelfContainedInstance(
                    _stack.GameRoot,
                    instanceId,
                    gameVersion!,
                    baseVersion,
                    card is null ? null : $"{loaderName} {loaderVersion}",
                    instanceId);

                _stack.Config.Settings.SelectedInstanceId = instanceId;
                _stack.Config.Save();
                InstallStatus = $"已安装实例 {instanceId}。";
            }

            InstallProgress = 1;
            CloseDialogs();
            RefreshInstances(_stack.Config.Settings.SelectedInstanceId);
            NotifyChanged();
            StatusText = InstallStatus;
        }
        catch (Exception ex)
        {
            _stack.Log.Error("安装新游戏失败", ex);
            InstallStatus = "安装失败：" + ex.Message;
        }
        finally
        {
            IsInstalling = false;
            ClearInstallFiles();
        }
    }

    /// <summary>
    /// 取原版版本 JSON：本地已安装就直接用，否则从版本清单拉取。
    /// 这样安装实例前不需要先把原版装进 versions。
    /// </summary>
    private async Task<GameVersionJson> FetchBaseVersionAsync(string gameVersion)
    {
        var installed = _stack.Repository.TryLoad(_stack.GameRoot, gameVersion);
        if (installed is { InheritsFrom: null })
        {
            return installed;
        }

        var entry = await _stack.Manifests.FindAsync(gameVersion)
                    ?? throw new InvalidOperationException($"版本清单里找不到 Minecraft {gameVersion}");

        return await _stack.Manifests.GetVersionJsonAsync(entry);
    }

    private void ReportInstallProgress(InstallProgress progress)
    {
        InstallProgress = progress.Fraction;
        UpdateInstallFiles(progress.ActiveFiles);

        InstallSpeedText = InstallFileItem.FormatSpeed(progress.BytesPerSecond);

        if (progress.Total > 0)
        {
            InstallStatus = $"{progress.Stage} {progress.Completed}/{progress.Total}";
        }
        else if (!string.IsNullOrWhiteSpace(progress.Stage))
        {
            InstallStatus = progress.Stage;
        }
    }

    /// <summary>把当前并发下载的文件同步到界面；列表按开始顺序稳定更新。</summary>
    private void UpdateInstallFiles(IReadOnlyList<DownloadFileProgress> files)
    {
        if (files.Count == 0)
        {
            if (InstallFiles.Count > 0)
            {
                ClearInstallFiles();
            }

            return;
        }

        var alive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            alive.Add(file.Name);

            if (!_installFileRows.TryGetValue(file.Name, out var row))
            {
                row = new InstallFileItem(file.Name);
                _installFileRows[file.Name] = row;
                InstallFiles.Add(row);
            }

            row.Update(file.BytesReceived, file.TotalBytes);
        }

        foreach (var stale in _installFileRows.Keys.Where(key => !alive.Contains(key)).ToList())
        {
            InstallFiles.Remove(_installFileRows[stale]);
            _installFileRows.Remove(stale);
        }
    }

    private void ClearInstallFiles()
    {
        _installFileRows.Clear();
        InstallFiles.Clear();
        InstallSpeedText = string.Empty;
    }

    private void SetDefaultInstanceName(string value)
    {
        _suppressNameTouch = true;
        try
        {
            NewInstanceName = value;
        }
        finally
        {
            _suppressNameTouch = false;
            _instanceNameTouched = false;
        }
    }

    private static string DefaultInstanceName(string? gameVersion, ModLoaderKind? loader)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return string.Empty;
        }

        return loader is null
            ? gameVersion
            : $"{ModLoaderService.DisplayName(loader.Value)}{gameVersion}";
    }

    private void RaiseInstallState()
    {
        OnPropertyChanged(nameof(CanConfirmInstall));
        _installGameCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsAnyDialogOpen));
    }

    // ---------------------------------------------------------------- 安装整合包

    /// <summary>打开整合包安装方式选择：本地导入 / 链接下载 / 在线浏览。</summary>
    private void OpenModpackPicker()
    {
        CloseDialogs();
        ModpackUrl = string.Empty;
        ModpackUrlStatus = string.Empty;
        ModpackDownloadProgress = 0;
        IsModpackPickerOpen = true;
    }

    private async Task ImportLocalModpackAsync()
    {
        IsModpackPickerOpen = false;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包",
            Filter = "整合包 (*.zip;*.mrpack)|*.zip;*.mrpack|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ShowModpackConfirmAsync(dialog.FileName);
    }

    private void OpenModpackUrlDialog()
    {
        IsModpackPickerOpen = false;
        ModpackUrl = string.Empty;
        ModpackUrlStatus = "填写整合包的下载链接（.zip / .mrpack）。";
        ModpackDownloadProgress = 0;
        IsModpackUrlDialogOpen = true;
    }

    private void OpenModpackBrowser()
    {
        IsModpackPickerOpen = false;
        IsModpackUrlDialogOpen = false;
        OnPropertyChanged(nameof(IsAnyDialogOpen));

        // 整合包先落到缓存目录，确认后再由导入流程创建实例。
        var cacheDirectory = Path.Combine(_stack.Config.ConfigDirectory, "cache");
        Download.Show(ResourceKind.Modpack, string.Empty, cacheDirectory, null, null);
    }

    private async Task DownloadModpackFromUrlAsync()
    {
        var url = ModpackUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ModpackUrlStatus = "请输入 http(s) 开头的整合包下载链接。";
            return;
        }

        var directory = Path.Combine(_stack.Config.ConfigDirectory, "cache", "modpacks");
        Directory.CreateDirectory(directory);

        _modpackDownloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _modpackDownloadCts = cts;

        IsModpackDownloading = true;
        ModpackDownloadProgress = 0;
        ModpackDownloadSpeedText = string.Empty;
        _modpackRows.Clear();
        ModpackUrlStatus = "正在下载整合包…";

        try
        {
            var result = await _stack.ResourceDownloads.DownloadToFolderAsync(
                new ResourceFile
                {
                    FileName = BuildModpackFileName(uri),
                    DownloadUrl = uri.AbsoluteUri,
                },
                directory,
                new Progress<InstallProgress>(progress =>
                {
                    ModpackDownloadProgress = progress.Total > 0
                        ? (double)progress.Completed / progress.Total
                        : 0;
                    ModpackDownloadSpeedText = InstallFileItem.FormatSpeed(progress.BytesPerSecond);
                    _modpackRows.Update(progress.ActiveFiles);
                    ModpackUrlStatus = progress.Stage;
                }),
                cts.Token);

            if (!result.Success || string.IsNullOrWhiteSpace(result.InstalledPath))
            {
                ModpackUrlStatus = result.Error ?? "下载失败。";
                return;
            }

            ModpackDownloadProgress = 1;
            ModpackUrlStatus = "下载完成，正在识别整合包…";
            await ShowModpackConfirmAsync(result.InstalledPath);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _stack.Log.Error("下载整合包失败", ex);
            ModpackUrlStatus = "下载失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_modpackDownloadCts, cts))
            {
                _modpackDownloadCts = null;
            }

            ModpackDownloadSpeedText = string.Empty;
            _modpackRows.Clear();

            cts.Dispose();
            IsModpackDownloading = false;
        }
    }

    /// <summary>从链接里取文件名；没有扩展名时按 zip 处理，识别顺序交给导入器。</summary>
    private static string BuildModpackFileName(Uri uri)
    {
        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name) || !PathUtil.IsValidFileName(name))
        {
            return "modpack.zip";
        }

        return Path.GetExtension(name).Length == 0 ? name + ".zip" : name;
    }

    /// <summary>读取整合包信息并打开安装确认弹窗（本地导入与在线下载共用）。</summary>
    private async Task ShowModpackConfirmAsync(string archivePath)
    {
        try
        {
            var info = await Task.Run(() => _stack.Modpacks.Inspect(archivePath));

            _modpackPath = archivePath;
            _modpackInfo = info;
            ModpackTitle = string.IsNullOrWhiteSpace(info.Name)
                ? Path.GetFileNameWithoutExtension(archivePath)
                : info.Name!;
            ModpackDetail = $"{info.KindName} · 游戏版本 {info.GameVersion ?? "未知"} · "
                            + $"加载器 {info.Loader ?? "无"} · 文件 {info.FileCount} 个";
            ModpackNote = info.Note ?? string.Empty;
            OnPropertyChanged(nameof(HasModpackNote));
            ModpackInstanceName = ModpackTitle;
            ModpackStatus = info.Kind switch
            {
                ModpackKind.Unknown => "无法识别这个压缩包。",
                ModpackKind.Rar => "不支持 .rar，请先转成 .zip。",
                _ when string.IsNullOrWhiteSpace(info.GameVersion) =>
                    "整合包没有声明游戏版本，暂无法自动创建实例。",
                _ => "确认实例名后开始安装。",
            };
            ModpackProgress = 0;
            IsModpackPickerOpen = false;
            IsModpackUrlDialogOpen = false;
            IsModpackDialogOpen = true;
            OnPropertyChanged(nameof(CanConfirmModpack));
            _installModpackCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _stack.Log.Error("读取整合包失败", ex);
            StatusText = "读取整合包失败：" + ex.Message;

            if (IsModpackUrlDialogOpen)
            {
                ModpackUrlStatus = "读取整合包失败：" + ex.Message;
            }
        }
    }

    private async Task InstallModpackAsync()
    {
        var info = _modpackInfo;
        var archivePath = _modpackPath;

        if (info is null || string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(info.GameVersion))
        {
            ModpackStatus = "整合包没有声明游戏版本，无法创建可用实例。";
            return;
        }

        IsModpackInstalling = true;
        ModpackProgress = 0;
        _modpackRows.Clear();
        ModpackStatus = "准备安装…";

        try
        {
            var loader = ModLoaderService.ParseLoaderDescription(info.Loader);
            if (!string.IsNullOrWhiteSpace(info.Loader) && loader is null)
            {
                throw new InvalidOperationException($"暂不支持自动安装这个加载器：{info.Loader}");
            }

            var fallback = string.IsNullOrWhiteSpace(ModpackInstanceName) ? ModpackTitle : ModpackInstanceName.Trim();
            var instanceId = _stack.Instances.CreateUniqueInstanceId(_stack.GameRoot, ModpackInstanceName, fallback);

            // 原版与加载器（Fabric / Quilt / Forge / NeoForge）在内存里合并后直接装进实例目录，
            // 不生成原版基底；Forge / NeoForge 的处理器在这一步执行。
            GameVersionJson? loaderProfile = null;
            string? loaderText = null;

            var baseVersion = await FetchBaseVersionAsync(info.GameVersion!);

            try
            {
                if (loader is { } parsed)
                {
                    var loaderVersion = parsed.Version;
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                    {
                        var options = await _stack.ModLoaders.ListLoaderVersionsAsync(parsed.Kind, info.GameVersion!);
                        loaderVersion = (options.FirstOrDefault(option => option.IsRecommended) ?? options.FirstOrDefault())?.Version
                                        ?? throw new InvalidOperationException(
                                            $"{ModLoaderService.DisplayName(parsed.Kind)} 没有支持 {info.GameVersion} 的版本");
                    }

                    loaderText = $"{ModLoaderService.DisplayName(parsed.Kind)} {loaderVersion}";
                    ModpackStatus = $"正在安装 {loaderText}…";

                    loaderProfile = parsed.Kind is ModLoaderKind.Fabric or ModLoaderKind.Quilt
                        ? await _stack.ModLoaders.GetFabricLikeProfileAsync(
                            parsed.Kind, info.GameVersion!, loaderVersion)
                        : await _stack.ModLoaders.GetForgeLikeVersionAsync(
                            parsed.Kind,
                            _stack.GameRoot,
                            info.GameVersion!,
                            loaderVersion,
                            baseVersion,
                            VersionRepository.VersionJarPath(_stack.GameRoot, instanceId),
                            _stack.BuildInstallOptions(),
                            new Progress<InstallProgress>(ReportModpackProgress));
                }
                else
                {
                    ModpackStatus = $"正在安装原版 {info.GameVersion}…";
                }

                var report = await _stack.Installer.InstallInstanceAsync(
                    _stack.GameRoot, instanceId, baseVersion, loaderProfile,
                    _stack.BuildInstallOptions(), new Progress<InstallProgress>(ReportModpackProgress));

                if (!report.Success)
                {
                    throw new InvalidOperationException($"安装失败：{report.Failed} 个文件未能下载");
                }
            }
            catch
            {
                DeleteInstanceQuietly(instanceId);
                throw;
            }

            _stack.Instances.RegisterSelfContainedInstance(
                _stack.GameRoot, instanceId, info.GameVersion!, baseVersion, loaderText, instanceId);

            ModpackStatus = "正在铺开整合包文件…";

            ModpackImportResult import;
            try
            {
                import = await _stack.Modpacks.ImportAsync(
                    new ModpackImportRequest
                    {
                        ArchivePath = archivePath,
                        GameRoot = _stack.GameRoot,
                        InstanceDirectory = VersionRepository.VersionDirectory(_stack.GameRoot, instanceId),
                        DownloadFiles = true,
                        CurseForgeApiKey = _stack.Config.ResolveCurseForgeApiKey(),
                    },
                    new Progress<InstallProgress>(ReportModpackProgress));
            }
            catch
            {
                DeleteInstanceQuietly(instanceId);
                throw;
            }

            if (!import.Success)
            {
                DeleteInstanceQuietly(instanceId);
                throw new InvalidOperationException(
                    (import.Error ?? "整合包文件导入失败")
                    + (import.Failed > 0 ? $"（{import.Failed} 个文件）" : string.Empty));
            }

            _stack.Config.Settings.SelectedInstanceId = instanceId;
            _stack.Config.Save();

            ModpackProgress = 1;
            ModpackStatus = $"已安装整合包实例 {instanceId}："
                            + $"下载 {import.Downloaded} 个，覆盖 {import.Overwritten} 个。";

            var warnings = import.Warnings;
            CloseDialogs();
            RefreshInstances(instanceId);
            NotifyChanged();

            StatusText = warnings.Count == 0
                ? ModpackStatus
                : ModpackStatus + $" 有 {warnings.Count} 条提示：" + string.Join("；", warnings.Take(3));
        }
        catch (Exception ex)
        {
            _stack.Log.Error("安装整合包失败", ex);
            ModpackStatus = "安装失败：" + ex.Message;
        }
        finally
        {
            ModpackSpeedText = string.Empty;
            _modpackRows.Clear();
            IsModpackInstalling = false;
        }
    }

    /// <summary>安装失败时清掉未完成的实例；清理失败只记日志，不覆盖原始错误。</summary>
    private void DeleteInstanceQuietly(string instanceId)
    {
        try
        {
            _stack.Instances.Delete(_stack.GameRoot, instanceId);
        }
        catch (Exception cleanup)
        {
            _stack.Log.Warn($"清理未完成的整合包实例 {instanceId} 失败", cleanup);
        }
    }

    private void ReportModpackProgress(InstallProgress progress)
    {
        ModpackProgress = progress.Fraction;
        ModpackSpeedText = InstallFileItem.FormatSpeed(progress.BytesPerSecond);
        _modpackRows.Update(progress.ActiveFiles);

        if (progress.Total > 0)
        {
            ModpackStatus = $"{progress.Stage} {progress.Completed}/{progress.Total}";
        }
        else if (!string.IsNullOrWhiteSpace(progress.Stage))
        {
            ModpackStatus = progress.Stage;
        }
    }

    // ---------------------------------------------------------------- 弹窗

    private void CloseDialogs()
    {
        _modpackDownloadCts?.Cancel();
        _modpackDownloadCts = null;

        IsInstallDialogOpen = false;
        IsModpackDialogOpen = false;
        IsModpackPickerOpen = false;
        IsModpackUrlDialogOpen = false;
        IsModpackDownloading = false;
        ModpackDownloadProgress = 0;
        ModpackDownloadSpeedText = string.Empty;
        ModpackSpeedText = string.Empty;
        _modpackRows.Clear();
        Download.Close();
        OnPropertyChanged(nameof(IsAnyDialogOpen));
    }
}
