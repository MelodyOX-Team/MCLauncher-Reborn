using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Models;
using MCLauncher.Core.Services;

namespace MCLauncher.App.ViewModels;

/// <summary>引擎管理页里的一行组件（Minecraft 基底或某个加载器）。</summary>
public sealed class EngineComponent
{
    public required string Name { get; init; }

    public required string BadgeText { get; init; }

    public required string BadgeColor { get; init; }

    /// <summary>Minecraft 基底行为 null。</summary>
    public ModLoaderKind? Kind { get; init; }

    public bool IsBase { get; init; }

    public bool IsInstalled { get; init; }

    public bool IsBlocked { get; init; }

    public bool IsUnsupported { get; init; }

    public string Status { get; init; } = string.Empty;

    public bool ShowInstall => Kind is not null && !IsBase && !IsInstalled && !IsBlocked && !IsUnsupported;

    public bool ShowUninstall => Kind is not null && !IsBase && IsInstalled;
}

/// <summary>
/// 实例管理的「引擎管理」：列出 Minecraft 基底与各类加载器的状态，
/// 支持给没有加载器的实例安装加载器（会创建一个新实例），以及卸载现有加载器。
/// </summary>
public sealed class EngineManagementViewModel : ViewModelBase
{
    private readonly LauncherStack _stack;
    private readonly DownloadFileRows _installRows = new();

    private InstalledVersionInfo? _instance;
    private string _instanceId = string.Empty;
    private string _baseVersion = string.Empty;
    private string _instanceDisplay = string.Empty;

    private string _statusText = string.Empty;
    private bool _isDialogOpen;
    private bool _isInstalling;
    private bool _isLoadingVersions;
    private double _installProgress;
    private string _installSpeedText = string.Empty;
    private string _installStatus = string.Empty;
    private string _dialogTitle = string.Empty;
    private string _dialogSubtitle = string.Empty;

    private ModLoaderKind? _pendingKind;
    private LoaderVersionOption? _selectedLoaderVersion;

    public EngineManagementViewModel(LauncherStack stack)
    {
        _stack = stack;

        RefreshCommand = new RelayCommand(() => Show(_instanceId));
        OpenInstallCommand = new AsyncRelayCommand<EngineComponent>(OpenInstallAsync);
        ConfirmInstallCommand = new AsyncRelayCommand(InstallAsync);
        CancelInstallCommand = new RelayCommand(CloseDialog);
        UninstallCommand = new AsyncRelayCommand<EngineComponent>(UninstallAsync);
    }

    /// <summary>安装成功后创建了新实例，通知实例管理页切换过去。</summary>
    public event Action<string>? InstanceCreated;

    // ---------------------------------------------------------------- 实例信息

    public string InstanceDisplay
    {
        get => _instanceDisplay;
        private set => SetProperty(ref _instanceDisplay, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ObservableCollection<EngineComponent> Components { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand OpenInstallCommand { get; }

    public ICommand ConfirmInstallCommand { get; }

    public ICommand CancelInstallCommand { get; }

    public ICommand UninstallCommand { get; }

    /// <summary>切换实例后重建组件列表。</summary>
    public void Show(string versionId)
    {
        _instanceId = versionId;
        _instance = _stack.GetInstalledVersions()
            .FirstOrDefault(version => string.Equals(version.Id, versionId, StringComparison.OrdinalIgnoreCase));

        InstanceDisplay = _instance?.Display ?? versionId;
        _baseVersion = _instance?.BaseVersionId ?? versionId;

        CloseDialog();
        BuildComponents();

        StatusText = _instance?.HasLoader == true
            ? $"当前加载器：{_instance?.LoaderText}。卸载后会恢复为原版启动。"
            : "选择要安装的加载器；安装会创建一个基于当前实例的新实例。";
    }

    private void BuildComponents()
    {
        Components.Clear();

        var loaderName = _instance?.LoaderName ?? "原版";
        var hasLoader = _instance?.HasLoader == true;

        Components.Add(new EngineComponent
        {
            Name = "Minecraft",
            BadgeText = "MC",
            BadgeColor = "#4C9A4C",
            IsBase = true,
            IsInstalled = true,
            Status = $"已安装 {_baseVersion}",
        });

        AddLoader(ModLoaderKind.Forge, "Forge", "FG", "#3B6E8F", loaderName, hasLoader);
        AddLoader(ModLoaderKind.NeoForge, "NeoForge", "NF", "#C97A3D", loaderName, hasLoader);

        Components.Add(new EngineComponent
        {
            Name = "OptiFine",
            BadgeText = "OF",
            BadgeColor = "#8E6FB0",
            Kind = ModLoaderKind.OptiFine,
            IsInstalled = string.Equals(loaderName, "OptiFine", StringComparison.OrdinalIgnoreCase),
            IsBlocked = hasLoader && !string.Equals(loaderName, "OptiFine", StringComparison.OrdinalIgnoreCase),
            IsUnsupported = !hasLoader,
            Status = string.Equals(loaderName, "OptiFine", StringComparison.OrdinalIgnoreCase)
                ? $"已安装 {_instance?.LoaderVersion}".Trim()
                : hasLoader
                    ? $"与 {loaderName} 不兼容"
                    : "需本地 jar，暂不支持",
        });

        AddLoader(ModLoaderKind.Fabric, "Fabric", "FB", "#D08A3E", loaderName, hasLoader);
        AddLoader(ModLoaderKind.Quilt, "Quilt", "QT", "#9B59B6", loaderName, hasLoader);
    }

    private void AddLoader(
        ModLoaderKind kind,
        string name,
        string badgeText,
        string badgeColor,
        string loaderName,
        bool hasLoader)
    {
        var installed = hasLoader
                        && string.Equals(loaderName, ModLoaderService.DisplayName(kind), StringComparison.OrdinalIgnoreCase);

        var status = installed
            ? string.IsNullOrWhiteSpace(_instance?.LoaderVersion)
                ? "已安装"
                : $"已安装 {_instance?.LoaderVersion}"
            : hasLoader
                ? $"与 {loaderName} 不兼容"
                : "未安装";

        Components.Add(new EngineComponent
        {
            Name = name,
            BadgeText = badgeText,
            BadgeColor = badgeColor,
            Kind = kind,
            IsInstalled = installed,
            IsBlocked = hasLoader && !installed,
            Status = status,
        });
    }

    // ---------------------------------------------------------------- 安装弹窗

    public ObservableCollection<LoaderVersionOption> LoaderVersions { get; } = new();

    public LoaderVersionOption? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set
        {
            if (SetProperty(ref _selectedLoaderVersion, value))
            {
                OnPropertyChanged(nameof(CanConfirmInstall));
            }
        }
    }

    public bool IsDialogOpen
    {
        get => _isDialogOpen;
        private set
        {
            if (SetProperty(ref _isDialogOpen, value))
            {
                OnPropertyChanged(nameof(CanConfirmInstall));
            }
        }
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (SetProperty(ref _isInstalling, value))
            {
                OnPropertyChanged(nameof(CanConfirmInstall));
            }
        }
    }

    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        private set
        {
            if (SetProperty(ref _isLoadingVersions, value))
            {
                OnPropertyChanged(nameof(CanConfirmInstall));
            }
        }
    }

    public double InstallProgress
    {
        get => _installProgress;
        private set => SetProperty(ref _installProgress, value);
    }

    /// <summary>安装加载器时的实时下载速度文案。</summary>
    public string InstallSpeedText
    {
        get => _installSpeedText;
        private set => SetProperty(ref _installSpeedText, value);
    }

    /// <summary>安装加载器时的下载队列（单文件时只有一行）。</summary>
    public ObservableCollection<InstallFileItem> InstallFiles => _installRows.Items;

    public string InstallStatus
    {
        get => _installStatus;
        private set => SetProperty(ref _installStatus, value);
    }

    public string DialogTitle
    {
        get => _dialogTitle;
        private set => SetProperty(ref _dialogTitle, value);
    }

    public string DialogSubtitle
    {
        get => _dialogSubtitle;
        private set => SetProperty(ref _dialogSubtitle, value);
    }

    public bool CanConfirmInstall =>
        IsDialogOpen && !IsInstalling && !IsLoadingVersions && SelectedLoaderVersion is not null;

    private async Task OpenInstallAsync(EngineComponent? component)
    {
        if (component?.Kind is null || !component.ShowInstall)
        {
            return;
        }

        _pendingKind = component.Kind;
        DialogTitle = $"安装 {component.Name}";
        DialogSubtitle = $"Minecraft {_baseVersion} · 安装后创建一个新实例";
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        InstallProgress = 0;
        InstallStatus = "正在获取可用版本…";
        IsDialogOpen = true;
        IsLoadingVersions = true;

        try
        {
            var versions = await _stack.ModLoaders.ListLoaderVersionsAsync(component.Kind.Value, _baseVersion);

            foreach (var version in versions)
            {
                LoaderVersions.Add(version);
            }

            SelectedLoaderVersion = versions.FirstOrDefault(version => version.IsRecommended)
                                    ?? versions.FirstOrDefault();
            InstallStatus = versions.Count == 0
                ? "没有找到适用于该 Minecraft 版本的加载器版本。"
                : "选择版本后点「安装」。";
        }
        catch (Exception ex)
        {
            _stack.Log.Error($"获取 {component.Name} 版本列表失败", ex);
            InstallStatus = "获取版本列表失败：" + ex.Message;
        }
        finally
        {
            IsLoadingVersions = false;
        }
    }

    private async Task InstallAsync()
    {
        if (_pendingKind is null || SelectedLoaderVersion is null || IsInstalling)
        {
            return;
        }

        var kind = _pendingKind.Value;
        var loaderVersion = SelectedLoaderVersion.Version;
        var loaderName = ModLoaderService.DisplayName(kind);

        IsInstalling = true;
        InstallProgress = 0;
        _installRows.Clear();
        InstallStatus = $"正在安装 {loaderName} {loaderVersion}…";

        string? newInstanceId = null;
        try
        {
            var progress = new Progress<InstallProgress>(value =>
            {
                InstallStatus = string.IsNullOrWhiteSpace(value.Stage)
                    ? InstallStatus
                    : value.Stage;
                InstallSpeedText = InstallFileItem.FormatSpeed(value.BytesPerSecond);
                _installRows.Update(value.ActiveFiles);
                InstallProgress = value.Fraction;
            });

            // 原版 JSON 按需获取（本地没有就从版本清单下载），只在内存里与加载器合并。
            var baseVersion = await _stack.GetBaseVersionJsonAsync(_baseVersion);

            var desiredName = $"{loaderName}{InstanceDisplay}";
            newInstanceId = _stack.Instances.CreateUniqueInstanceId(
                _stack.GameRoot, desiredName, $"{loaderName}{_baseVersion}");

            var loaderProfile = kind is ModLoaderKind.Fabric or ModLoaderKind.Quilt
                ? await _stack.ModLoaders.GetFabricLikeProfileAsync(kind, _baseVersion, loaderVersion)
                : await _stack.ModLoaders.GetForgeLikeVersionAsync(
                    kind,
                    _stack.GameRoot,
                    _baseVersion,
                    loaderVersion,
                    baseVersion,
                    VersionRepository.VersionJarPath(_stack.GameRoot, newInstanceId),
                    _stack.BuildInstallOptions(),
                    progress);

            var report = await _stack.Installer.InstallInstanceAsync(
                _stack.GameRoot, newInstanceId, baseVersion, loaderProfile,
                _stack.BuildInstallOptions(), progress);

            if (!report.Success)
            {
                throw new InvalidOperationException($"安装失败：{report.Failed} 个文件未能下载");
            }

            // 新实例默认开启版本隔离（mods / 存档 / 配置都放在 instances/<id> 下）。
            _stack.Instances.SetIsolation(_stack.GameRoot, newInstanceId, true);

            _stack.Config.Settings.SelectedInstanceId = newInstanceId;
            _stack.Config.Save();
            _stack.RaiseChanged();

            InstallProgress = 1;
            IsDialogOpen = false;
            StatusText = $"已创建实例 {newInstanceId}（{loaderName} {loaderVersion}）。";
            _stack.Log.Info($"引擎管理安装 {loaderName} {loaderVersion} → 新实例 {newInstanceId}");

            InstanceCreated?.Invoke(newInstanceId);
        }
        catch (Exception ex)
        {
            // 半成品会以「实例」身份出现在列表里，失败就整个删掉；libraries / assets 是共享的。
            if (newInstanceId is not null)
            {
                try
                {
                    _stack.Instances.Delete(_stack.GameRoot, newInstanceId);
                }
                catch (Exception cleanup)
                {
                    _stack.Log.Warn($"清理未完成的实例 {newInstanceId} 失败", cleanup);
                }
            }

            _stack.Log.Error($"引擎管理安装 {loaderName} 失败", ex);
            InstallStatus = "安装失败：" + ex.Message;
        }
        finally
        {
            InstallSpeedText = string.Empty;
            _installRows.Clear();
            IsInstalling = false;
        }
    }

    private void CloseDialog()
    {
        if (IsInstalling)
        {
            return;
        }

        IsDialogOpen = false;
        InstallSpeedText = string.Empty;
        _installRows.Clear();
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        _pendingKind = null;
    }

    // ---------------------------------------------------------------- 卸载

    private async Task UninstallAsync(EngineComponent? component)
    {
        if (component?.Kind is null || !component.ShowUninstall || _instance is null)
        {
            return;
        }

        var loaderName = ModLoaderService.DisplayName(component.Kind.Value);
        var baseVersion = _instance.BaseVersionId;

        if (string.IsNullOrWhiteSpace(baseVersion))
        {
            StatusText = "找不到原版基底，无法卸载加载器。";
            return;
        }

        var message = $"卸载 {loaderName}？\n\n"
                      + $"实例会恢复为原版 {baseVersion} 启动，mods / 存档 / 配置都会保留。";
        if (MessageBox.Show(message, "卸载加载器", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
        {
            return;
        }

        StatusText = $"正在卸载 {loaderName}…";

        try
        {
            if (await _stack.Instances.RemoveLoaderAsync(_stack.GameRoot, _instanceId, baseVersion))
            {
                _stack.RaiseChanged();
                Show(_instanceId);
                StatusText = $"已卸载 {loaderName}，实例已恢复为原版 {baseVersion}。";
            }
            else
            {
                StatusText = "卸载失败：找不到原版基底或版本 JSON 损坏。";
            }
        }
        catch (Exception ex)
        {
            _stack.Log.Error($"卸载 {loaderName} 失败", ex);
            StatusText = "卸载失败：" + ex.Message;
        }
    }
}
