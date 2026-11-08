using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Threading;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Models;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services;

namespace MCLauncher.App.ViewModels;

/// <summary>「版本列表源 / 下载源」下拉里的一项。</summary>
public sealed class DownloadSourceChoice
{
    public required DownloadSourcePreference Value { get; init; }

    public required string Label { get; init; }
}

/// <summary>附加内容默认下载源下拉里的一项。</summary>
public sealed class ContentSourceChoice
{
    public required ResourceSource Value { get; init; }

    public required string Label { get; init; }
}

/// <summary>进程优先级下拉里的一项。</summary>
public sealed class ProcessPriorityChoice
{
    public required GameProcessPriority Value { get; init; }

    public required string Label { get; init; }
}

/// <summary>启动器可见性下拉里的一项。</summary>
public sealed class LauncherVisibilityChoice
{
    public required LauncherVisibilityMode Value { get; init; }

    public required string Label { get; init; }
}

/// <summary>
/// 设置页：只放全局设置。
/// 实例的独立设置由实例管理页的「游戏设置」在右侧原地展示。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private static readonly Regex WindowSizePattern =
        new(@"^\s*(\d{2,5})\s*[xX×*]\s*(\d{2,5})\s*$", RegexOptions.Compiled);

    private readonly LauncherStack _stack;
    private readonly AsyncRelayCommand _detectJavaCommand;
    private readonly DispatcherTimer _backgroundApplyTimer;

    private string _statusText = "修改后点「保存设置」生效";
    private bool _isBusy;
    private DetectedJava? _selectedJava;

    public SettingsViewModel(LauncherStack stack)
    {
        _stack = stack;

        // 背景图文本输入防抖：输入框是逐字符触发，网络模式边打边下会刷出大量无效请求，
        // 停止输入 600ms 后才应用一次。
        _backgroundApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _backgroundApplyTimer.Tick += OnBackgroundApplyTimerTick;

        SaveCommand = new RelayCommand(Save);
        BrowseGameFolderCommand = new RelayCommand(BrowseGameFolder);
        BrowseJavaCommand = new RelayCommand(BrowseJava);
        BrowseBackgroundFileCommand = new RelayCommand(BrowseBackgroundFile);
        BrowseBackgroundFolderCommand = new RelayCommand(BrowseBackgroundFolder);
        OpenBackgroundFolderCommand = new RelayCommand(OpenBackgroundFolder);
        BrowseWorkingDirectoryCommand = new RelayCommand(BrowseWorkingDirectory);
        OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
        _detectJavaCommand = new AsyncRelayCommand(DetectJavaAsync);
        DetectJavaCommand = _detectJavaCommand;

        foreach (var java in _stack.Config.Settings.DetectedJavas)
        {
            Javas.Add(java);
        }

        SyncSelectedJava();
    }

    // ---------------------------------------------------------------- 全局设置

    public ObservableCollection<DetectedJava> Javas { get; } = new();

    public int MaxMemoryMb
    {
        get => _stack.Config.Settings.MaxMemoryMb;
        set
        {
            if (_stack.Config.Settings.MaxMemoryMb == value)
            {
                return;
            }

            _stack.Config.Settings.MaxMemoryMb = value;
            OnPropertyChanged();
        }
    }

    public int MinMemoryMb
    {
        get => _stack.Config.Settings.MinMemoryMb;
        set
        {
            if (_stack.Config.Settings.MinMemoryMb == value)
            {
                return;
            }

            _stack.Config.Settings.MinMemoryMb = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<DownloadSourceChoice> DownloadSourceChoices { get; } = new[]
    {
        new DownloadSourceChoice
        {
            Value = DownloadSourcePreference.Auto,
            Label = "自动选择下载源",
        },
        new DownloadSourceChoice
        {
            Value = DownloadSourcePreference.Official,
            Label = "尽量使用官方源",
        },
        new DownloadSourceChoice
        {
            Value = DownloadSourcePreference.Mirror,
            Label = "尽量使用镜像源",
        },
    };

    public IReadOnlyList<ContentSourceChoice> ContentSourceChoices { get; } = new[]
    {
        new ContentSourceChoice { Value = ResourceSource.Modrinth, Label = "Modrinth" },
        new ContentSourceChoice { Value = ResourceSource.CurseForge, Label = "CurseForge" },
    };

    /// <summary>版本清单、版本 JSON 的下载源。</summary>
    public DownloadSourcePreference VersionListSource
    {
        get => _stack.Config.Settings.VersionListSource;
        set
        {
            if (_stack.Config.Settings.VersionListSource == value)
            {
                return;
            }

            _stack.Config.Settings.VersionListSource = value;
            _stack.Resolver.VersionListSource = value;
            OnPropertyChanged();
        }
    }

    /// <summary>客户端、依赖库、资源对象等游戏文件的下载源。</summary>
    public DownloadSourcePreference FileSource
    {
        get => _stack.Config.Settings.FileSource;
        set
        {
            if (_stack.Config.Settings.FileSource == value)
            {
                return;
            }

            _stack.Config.Settings.FileSource = value;
            _stack.Resolver.FileSource = value;
            OnPropertyChanged();
        }
    }

    /// <summary>模组 / 资源包 / 光影等附加内容的默认源。</summary>
    public ResourceSource DefaultContentSource
    {
        get => _stack.Config.Settings.DefaultContentSource;
        set
        {
            if (_stack.Config.Settings.DefaultContentSource == value)
            {
                return;
            }

            _stack.Config.Settings.DefaultContentSource = value;
            OnPropertyChanged();
        }
    }

    public bool AutoDownloadThreads
    {
        get => _stack.Config.Settings.AutoDownloadThreads;
        set
        {
            if (_stack.Config.Settings.AutoDownloadThreads == value)
            {
                return;
            }

            _stack.Config.Settings.AutoDownloadThreads = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CustomDownloadThreads));
            OnPropertyChanged(nameof(DownloadThreadsText));
        }
    }

    public bool CustomDownloadThreads
    {
        get => !_stack.Config.Settings.AutoDownloadThreads;
        set => AutoDownloadThreads = !value;
    }

    public int DownloadThreads
    {
        get => Math.Clamp(_stack.Config.Settings.DownloadThreads, 1, AppSettings.MaxThreadCount);
        set
        {
            var clamped = Math.Clamp(value, 1, AppSettings.MaxThreadCount);
            if (_stack.Config.Settings.DownloadThreads == clamped)
            {
                return;
            }

            _stack.Config.Settings.DownloadThreads = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadThreadsText));
        }
    }

    public string DownloadThreadsText => _stack.Config.Settings.AutoDownloadThreads
        ? $"自动（{AppSettings.AutoThreadCount} 线程）"
        : $"{_stack.Config.Settings.DownloadThreads} 线程";

    // ---------------------------------------------------------------- 背景图

    public string BackgroundFolderPath => BackgroundPicker.FolderPath(AppContext.BaseDirectory);

    public bool IsBackgroundDefault
    {
        get => _stack.Config.Settings.BackgroundMode == BackgroundMode.None;
        set
        {
            if (value)
            {
                SetBackgroundMode(BackgroundMode.None);
            }
        }
    }

    public bool IsBackgroundFolder
    {
        get => _stack.Config.Settings.BackgroundMode == BackgroundMode.Folder;
        set
        {
            if (value)
            {
                SetBackgroundMode(BackgroundMode.Folder);
            }
        }
    }

    public bool IsBackgroundCustom
    {
        get => _stack.Config.Settings.BackgroundMode == BackgroundMode.Custom;
        set
        {
            if (value)
            {
                SetBackgroundMode(BackgroundMode.Custom);
            }
        }
    }

    public bool IsBackgroundNetwork
    {
        get => _stack.Config.Settings.BackgroundMode == BackgroundMode.Network;
        set
        {
            if (value)
            {
                SetBackgroundMode(BackgroundMode.Network);
            }
        }
    }

    /// <summary>自定义背景：目录（随机取图）或单张图片路径。</summary>
    public string BackgroundCustomPath
    {
        get => _stack.Config.Settings.BackgroundCustomPath ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.BackgroundCustomPath, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.BackgroundCustomPath = normalized;
            OnPropertyChanged();
            ScheduleBackgroundApply();
        }
    }

    public string BackgroundUrl
    {
        get => _stack.Config.Settings.BackgroundUrl ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.BackgroundUrl, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.BackgroundUrl = normalized;
            OnPropertyChanged();
            ScheduleBackgroundApply();
        }
    }

    private bool _backgroundDirty;

    /// <summary>
    /// 背景图设置是否被改过：离开设置页时据此刷新窗口背景，
    /// 避免每次切页都重设背景（网络模式会触发一次下载）。
    /// </summary>
    public bool IsBackgroundDirty => _backgroundDirty;

    /// <summary>背景已重新应用（或已随保存落盘）后清除脏标记。</summary>
    public void ClearBackgroundDirty() => _backgroundDirty = false;

    /// <summary>模式/路径等即时修改：立刻重新应用窗口背景。</summary>
    private void ApplyBackgroundNow()
    {
        _backgroundApplyTimer.Stop();
        App.RefreshBackground();
        _backgroundDirty = false;
    }

    /// <summary>文本输入：防抖后应用，避免逐字符触发（网络模式会重复下载）。</summary>
    private void ScheduleBackgroundApply()
    {
        _backgroundDirty = true;
        _backgroundApplyTimer.Stop();
        _backgroundApplyTimer.Start();
    }

    private void OnBackgroundApplyTimerTick(object? sender, EventArgs e)
    {
        _backgroundApplyTimer.Stop();
        if (_backgroundDirty)
        {
            App.RefreshBackground();
            _backgroundDirty = false;
        }
    }

    private void SetBackgroundMode(BackgroundMode mode)
    {
        var settings = _stack.Config.Settings;
        if (settings.BackgroundMode == mode)
        {
            return;
        }

        settings.BackgroundMode = mode;
        settings.BackgroundModeConfigured = true;

        if (mode == BackgroundMode.Folder)
        {
            BackgroundPicker.EnsureFolder(AppContext.BaseDirectory, _stack.Log);
        }

        // 默认底图 / 目录模式：点一下单选就立即换背景。
        ApplyBackgroundNow();

        OnPropertyChanged(nameof(IsBackgroundDefault));
        OnPropertyChanged(nameof(IsBackgroundFolder));
        OnPropertyChanged(nameof(IsBackgroundCustom));
        OnPropertyChanged(nameof(IsBackgroundNetwork));
    }

    public bool DefaultIsolation
    {
        get => _stack.Config.Settings.DefaultIsolation;
        set
        {
            if (_stack.Config.Settings.DefaultIsolation == value)
            {
                return;
            }

            _stack.Config.Settings.DefaultIsolation = value;
            OnPropertyChanged();
        }
    }

    /// <summary>窗口大小的常用预设；下拉框允许直接输入任意「宽x高」。</summary>
    public IReadOnlyList<string> WindowSizePresets { get; } = new[]
    {
        "854x480",
        "1280x720",
        "1600x900",
        "1920x1080",
        "2560x1440",
        "3840x2160",
    };

    public bool IsWindowed
    {
        get => _stack.Config.Settings.WindowMode == GameWindowMode.Windowed;
        set
        {
            if (value)
            {
                SetWindowMode(GameWindowMode.Windowed);
            }
        }
    }

    public bool IsFullscreen
    {
        get => _stack.Config.Settings.WindowMode == GameWindowMode.Fullscreen;
        set
        {
            if (value)
            {
                SetWindowMode(GameWindowMode.Fullscreen);
            }
        }
    }

    public bool IsMaximized
    {
        get => _stack.Config.Settings.WindowMode == GameWindowMode.Maximized;
        set
        {
            if (value)
            {
                SetWindowMode(GameWindowMode.Maximized);
            }
        }
    }

    /// <summary>窗口化尺寸，格式「宽x高」；选预设或直接改数字都走这里。</summary>
    public string GameWindowSize
    {
        get
        {
            var settings = _stack.Config.Settings;
            return settings.DefaultWidth > 0 && settings.DefaultHeight > 0
                ? $"{settings.DefaultWidth}x{settings.DefaultHeight}"
                : "854x480";
        }
        set
        {
            var match = WindowSizePattern.Match(value ?? string.Empty);
            if (!match.Success
                || !int.TryParse(match.Groups[1].Value, out var width)
                || !int.TryParse(match.Groups[2].Value, out var height)
                || width < 100 || height < 100)
            {
                // 输入不合法：还原成当前有效值（失焦时才触发，不会打断输入过程）。
                OnPropertyChanged();
                return;
            }

            var settings = _stack.Config.Settings;
            if (settings.DefaultWidth == width && settings.DefaultHeight == height)
            {
                return;
            }

            settings.DefaultWidth = width;
            settings.DefaultHeight = height;
            OnPropertyChanged();
        }
    }

    private void SetWindowMode(GameWindowMode mode)
    {
        var settings = _stack.Config.Settings;
        if (settings.WindowMode == mode)
        {
            return;
        }

        settings.WindowMode = mode;
        OnPropertyChanged(nameof(IsWindowed));
        OnPropertyChanged(nameof(IsFullscreen));
        OnPropertyChanged(nameof(IsMaximized));
    }

    public string GameFolder
    {
        get => _stack.Config.Settings.SelectedGameFolder ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? null
                : LauncherStack.NormalizeGameFolder(value.Trim());

            if (string.Equals(_stack.Config.Settings.SelectedGameFolder, normalized, StringComparison.Ordinal))
            {
                return;
            }

            if (normalized is not null && Directory.Exists(normalized))
            {
                // 有效目录直接交给栈处理：会补进目录列表并保存/通知其它页面。
                _stack.SelectGameFolder(normalized);
            }
            else
            {
                _stack.Config.Settings.SelectedGameFolder = normalized;
                _stack.Config.Save();
            }

            OnPropertyChanged();
        }
    }

    public string JavaPath
    {
        get => _stack.Config.Settings.JavaPath ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.JavaPath, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.JavaPath = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(JavaHint));
            SyncSelectedJava();
        }
    }

    /// <summary>设置页 Java 列表里选中的项；选中即写入 <see cref="JavaPath"/>。</summary>
    public DetectedJava? SelectedJava
    {
        get => _selectedJava;
        set
        {
            if (!SetProperty(ref _selectedJava, value) || value is null)
            {
                return;
            }

            JavaPath = value.Path;
            _stack.Config.Save();
        }
    }

    public string JavaHint => string.IsNullOrWhiteSpace(_stack.Config.Settings.JavaPath)
        ? "留空时按版本要求自动挑选；缺少合适版本会在启动时自动下载 Zulu JDK。"
        : "已手动指定：适配当前版本时优先使用，不匹配时自动改用合适版本。";

    /// <summary>让列表选中项跟随当前 Java 路径（手动输入或探测完成后调用）。</summary>
    private void SyncSelectedJava()
    {
        var path = _stack.Config.Settings.JavaPath;
        var match = string.IsNullOrWhiteSpace(path)
            ? null
            : Javas.FirstOrDefault(j => string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));

        if (ReferenceEquals(_selectedJava, match))
        {
            return;
        }

        _selectedJava = match;
        OnPropertyChanged(nameof(SelectedJava));
    }

    public string ExtraJvmArgs
    {
        get => _stack.Config.Settings.ExtraJvmArgs ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.ExtraJvmArgs, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.ExtraJvmArgs = normalized;
            OnPropertyChanged();
        }
    }

    public string ExtraGameArgs
    {
        get => _stack.Config.Settings.ExtraGameArgs ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.ExtraGameArgs, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.ExtraGameArgs = normalized;
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- 快速游玩

    public QuickPlayMode QuickPlayMode
    {
        get => _stack.Config.Settings.QuickPlayMode;
        set
        {
            if (_stack.Config.Settings.QuickPlayMode == value)
            {
                return;
            }

            _stack.Config.Settings.QuickPlayMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsQuickPlayNone));
            OnPropertyChanged(nameof(IsQuickPlayMultiplayer));
            OnPropertyChanged(nameof(IsQuickPlaySingleplayer));
            OnPropertyChanged(nameof(IsQuickPlayRealms));
        }
    }

    public bool IsQuickPlayNone
    {
        get => QuickPlayMode == QuickPlayMode.None;
        set
        {
            if (value)
            {
                QuickPlayMode = QuickPlayMode.None;
            }
        }
    }

    public bool IsQuickPlayMultiplayer
    {
        get => QuickPlayMode == QuickPlayMode.Multiplayer;
        set
        {
            if (value)
            {
                QuickPlayMode = QuickPlayMode.Multiplayer;
            }
        }
    }

    public bool IsQuickPlaySingleplayer
    {
        get => QuickPlayMode == QuickPlayMode.Singleplayer;
        set
        {
            if (value)
            {
                QuickPlayMode = QuickPlayMode.Singleplayer;
            }
        }
    }

    public bool IsQuickPlayRealms
    {
        get => QuickPlayMode == QuickPlayMode.Realms;
        set
        {
            if (value)
            {
                QuickPlayMode = QuickPlayMode.Realms;
            }
        }
    }

    /// <summary>快速游玩：服务器地址（host:port，可省略端口）。</summary>
    public string QuickPlayServer
    {
        get => _stack.Config.Settings.QuickPlayServer ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.QuickPlayServer, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.QuickPlayServer = normalized;
            OnPropertyChanged();
        }
    }

    /// <summary>快速游玩：单人存档的文件夹名。</summary>
    public string QuickPlayWorld
    {
        get => _stack.Config.Settings.QuickPlayWorld ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.QuickPlayWorld, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.QuickPlayWorld = normalized;
            OnPropertyChanged();
        }
    }

    /// <summary>快速游玩：Realms 服务器 ID。</summary>
    public string QuickPlayRealmsId
    {
        get => _stack.Config.Settings.QuickPlayRealmsId ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.QuickPlayRealmsId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.QuickPlayRealmsId = normalized;
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- 进程与启动器

    /// <summary>游戏进程工作目录；留空时使用实例目录。</summary>
    public string LaunchWorkingDirectory
    {
        get => _stack.Config.Settings.LaunchWorkingDirectory ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.LaunchWorkingDirectory, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.LaunchWorkingDirectory = normalized;
            OnPropertyChanged();
        }
    }

    /// <summary>额外环境变量，每行 NAME=VALUE。</summary>
    public string EnvironmentVariablesText
    {
        get => _stack.Config.Settings.EnvironmentVariablesText ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (string.Equals(_stack.Config.Settings.EnvironmentVariablesText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.EnvironmentVariablesText = normalized;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ProcessPriorityChoice> ProcessPriorityChoices { get; } = new[]
    {
        new ProcessPriorityChoice { Value = GameProcessPriority.Low, Label = "低" },
        new ProcessPriorityChoice { Value = GameProcessPriority.BelowNormal, Label = "低于正常" },
        new ProcessPriorityChoice { Value = GameProcessPriority.Normal, Label = "正常" },
        new ProcessPriorityChoice { Value = GameProcessPriority.AboveNormal, Label = "高于正常" },
        new ProcessPriorityChoice { Value = GameProcessPriority.High, Label = "高" },
        new ProcessPriorityChoice { Value = GameProcessPriority.RealTime, Label = "实时（通常需要管理员权限）" },
    };

    public GameProcessPriority ProcessPriority
    {
        get => _stack.Config.Settings.ProcessPriority;
        set
        {
            if (_stack.Config.Settings.ProcessPriority == value)
            {
                return;
            }

            _stack.Config.Settings.ProcessPriority = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<LauncherVisibilityChoice> LauncherVisibilityChoices { get; } = new[]
    {
        new LauncherVisibilityChoice { Value = LauncherVisibilityMode.KeepVisible, Label = "保持启动器可见" },
        new LauncherVisibilityChoice { Value = LauncherVisibilityMode.Hide, Label = "隐藏启动器" },
        new LauncherVisibilityChoice { Value = LauncherVisibilityMode.Close, Label = "退出启动器" },
        new LauncherVisibilityChoice { Value = LauncherVisibilityMode.HideAndReopen, Label = "隐藏，游戏结束后重新打开" },
    };

    public LauncherVisibilityMode LauncherVisibility
    {
        get => _stack.Config.Settings.LauncherVisibility;
        set
        {
            if (_stack.Config.Settings.LauncherVisibility == value)
            {
                return;
            }

            _stack.Config.Settings.LauncherVisibility = value;
            OnPropertyChanged();
        }
    }

    /// <summary>启动前跳过完整性检查，直接启动。</summary>
    public bool SkipIntegrityCheck
    {
        get => _stack.Config.Settings.SkipIntegrityCheck;
        set
        {
            if (_stack.Config.Settings.SkipIntegrityCheck == value)
            {
                return;
            }

            _stack.Config.Settings.SkipIntegrityCheck = value;
            OnPropertyChanged();
        }
    }

    /// <summary>放在 java 命令前面的包装命令。</summary>
    public string WrapperCommand
    {
        get => _stack.Config.Settings.WrapperCommand ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.WrapperCommand, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.WrapperCommand = normalized;
            OnPropertyChanged();
        }
    }

    /// <summary>游戏启动前执行的命令（cmd 语法）。</summary>
    public string PreLaunchCommand
    {
        get => _stack.Config.Settings.PreLaunchCommand ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.PreLaunchCommand, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.PreLaunchCommand = normalized;
            OnPropertyChanged();
        }
    }

    /// <summary>游戏结束后执行的命令（cmd 语法）。</summary>
    public string PostLaunchCommand
    {
        get => _stack.Config.Settings.PostLaunchCommand ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_stack.Config.Settings.PostLaunchCommand, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _stack.Config.Settings.PostLaunchCommand = normalized;
            OnPropertyChanged();
        }
    }

    public string ConfigDirectory => _stack.Config.ConfigDirectory;

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
            if (SetProperty(ref _isBusy, value))
            {
                _detectJavaCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand SaveCommand { get; }

    public ICommand BrowseGameFolderCommand { get; }

    public ICommand BrowseJavaCommand { get; }

    public ICommand BrowseBackgroundFileCommand { get; }

    public ICommand BrowseBackgroundFolderCommand { get; }

    public ICommand OpenBackgroundFolderCommand { get; }

    public ICommand BrowseWorkingDirectoryCommand { get; }

    public ICommand OpenLogDirectoryCommand { get; }

    public ICommand DetectJavaCommand { get; }

    // ---------------------------------------------------------------- 全局操作

    private void Save()
    {
        var saved = _stack.Config.Save();
        _backgroundApplyTimer.Stop();
        App.RefreshBackground();
        ClearBackgroundDirty();

        if (!saved)
        {
            StatusText = "设置保存失败：请检查配置目录是否可写，详情见日志。";
            _stack.Log.Warn("设置保存失败");
            return;
        }

        StatusText = $"已保存到 {_stack.Config.ConfigDirectory}";
        _stack.Log.Info("设置已保存");
        _stack.RaiseChanged();
    }

    private void BrowseGameFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择游戏目录（.minecraft）",
            Multiselect = false,
        };

        if (Directory.Exists(GameFolder))
        {
            dialog.InitialDirectory = GameFolder;
        }

        if (dialog.ShowDialog() == true)
        {
            GameFolder = dialog.FolderName;
            _stack.Config.Save();
        }
    }

    private void BrowseJava()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 java 可执行文件",
            Filter = "java 可执行文件|java.exe;javaw.exe|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            JavaPath = dialog.FileName;
            _stack.Config.Save();
        }
    }

    private void BrowseBackgroundFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            BackgroundCustomPath = dialog.FileName;
            SetBackgroundMode(BackgroundMode.Custom);
        }
    }

    private void BrowseBackgroundFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择背景图片目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            BackgroundCustomPath = dialog.FolderName;
            SetBackgroundMode(BackgroundMode.Custom);
        }
    }

    private void OpenBackgroundFolder()
    {
        BackgroundPicker.EnsureFolder(AppContext.BaseDirectory, _stack.Log);
        var folder = BackgroundPicker.FolderPath(AppContext.BaseDirectory);

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _stack.Log.Warn($"打开背景图目录失败：{folder}", ex);
            StatusText = "打开 bgskin 目录失败：" + ex.Message;
        }
    }

    private void BrowseWorkingDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择游戏运行路径",
            Multiselect = false,
        };

        if (Directory.Exists(LaunchWorkingDirectory))
        {
            dialog.InitialDirectory = LaunchWorkingDirectory;
        }

        if (dialog.ShowDialog() == true)
        {
            LaunchWorkingDirectory = dialog.FolderName;
        }
    }

    private void OpenLogDirectory()
    {
        var folder = Path.Combine(_stack.Config.ConfigDirectory, "logs");

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _stack.Log.Warn($"打开日志目录失败：{folder}", ex);
            StatusText = "打开日志目录失败：" + ex.Message;
        }
    }

    private async Task DetectJavaAsync()
    {
        IsBusy = true;
        StatusText = "正在探测本机 Java（可能需要十几秒）…";

        try
        {
            var detected = await Task.Run(() => _stack.Java.Detect(forceRefresh: true));

            Javas.Clear();
            _stack.Config.Settings.DetectedJavas.Clear();

            foreach (var java in detected)
            {
                var item = new DetectedJava
                {
                    Path = java.Path,
                    RawVersion = java.RawVersion,
                    MajorVersion = java.MajorVersion,
                    Is64Bit = java.Is64Bit,
                    Vendor = java.Vendor,
                };

                Javas.Add(item);
                _stack.Config.Settings.DetectedJavas.Add(item);
            }

            _stack.Config.Save();
            SyncSelectedJava();
            StatusText = detected.Count == 0
                ? "没有探测到可用的 64 位 Java，请手动指定路径。"
                : $"探测到 {detected.Count} 个可用 Java。";
        }
        catch (Exception ex)
        {
            _stack.Log.Error("探测 Java 失败", ex);
            StatusText = "探测失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
