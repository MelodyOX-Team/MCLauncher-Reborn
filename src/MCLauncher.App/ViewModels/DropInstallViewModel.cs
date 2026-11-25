using System.IO;
using System.Windows.Input;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Services;
using MCLauncher.Core.Utils;

namespace MCLauncher.App.ViewModels;

/// <summary>拖放安装弹层里可选的一个实例。</summary>
public sealed class DropInstanceOption
{
    public required InstalledVersionInfo Info { get; init; }

    /// <summary>该实例当前是否开启版本隔离（用于显示安装位置）。</summary>
    public required bool Isolated { get; init; }

    public string Id => Info.Id;

    public string Display => Info.Display;

    public string Subtitle => Isolated
        ? $"{Info.Subtitle} · 版本隔离"
        : $"{Info.Subtitle} · 共享游戏目录";
}

/// <summary>
/// 把拖入的文件安装到某个实例：列出识别结果、选择实例、复制到对应资源目录。
/// 隔离实例落到 <c>versions/&lt;id&gt;/mods</c>，未隔离实例落到 <c>.minecraft/mods</c>。
/// </summary>
public sealed class DropInstallViewModel : ViewModelBase
{
    private readonly LauncherStack _stack;
    private readonly Action<string> _setShellStatus;
    private readonly AsyncRelayCommand _installCommand;
    private readonly RelayCommand _closeCommand;

    private bool _isOpen;
    private bool _isBusy;
    private string _title = string.Empty;
    private string _summary = string.Empty;
    private string _note = string.Empty;
    private string _statusText = string.Empty;
    private string _destinationText = string.Empty;
    private DropInstanceOption? _selectedInstance;

    public DropInstallViewModel(LauncherStack stack, Action<string> setShellStatus)
    {
        _stack = stack;
        _setShellStatus = setShellStatus;

        _installCommand = new AsyncRelayCommand(InstallAsync, () => CanInstall);
        InstallCommand = _installCommand;
        _closeCommand = new RelayCommand(Hide, () => CanClose);
        CloseCommand = _closeCommand;
    }

    /// <summary>拖入的文件（已排除整合包）。</summary>
    public IReadOnlyList<DroppedFileInfo> Files { get; private set; } = Array.Empty<DroppedFileInfo>();

    public IReadOnlyList<DropInstanceOption> Instances { get; private set; } = Array.Empty<DropInstanceOption>();

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseInstallState();
            }
        }
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>形如「模组 ×2、资源包 ×1」。</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>整合包混在其它文件里等需要提示的情况。</summary>
    public string Note
    {
        get => _note;
        private set
        {
            if (SetProperty(ref _note, value))
            {
                OnPropertyChanged(nameof(HasNote));
            }
        }
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(_note);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>安装位置预览，例如 <c>versions\MyPack\mods（版本隔离）</c>。</summary>
    public string DestinationText
    {
        get => _destinationText;
        private set => SetProperty(ref _destinationText, value);
    }

    public DropInstanceOption? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (SetProperty(ref _selectedInstance, value))
            {
                UpdateDestination();
                RaiseInstallState();
            }
        }
    }

    public bool CanInstall =>
        !IsBusy
        && SelectedInstance is not null
        && Files.Any(file => file.IsInstallableResource);

    /// <summary>这一批里是否有可安装资源；全部无法识别时不显示实例选择。</summary>
    public bool HasInstallableFiles => Files.Any(file => file.IsInstallableResource);

    /// <summary>全部无法识别时按钮只是关闭弹层。</summary>
    public string CloseButtonText => HasInstallableFiles ? "取消" : "关闭";

    public bool CanClose => !IsBusy;

    public ICommand InstallCommand { get; }

    public ICommand CloseCommand { get; }

    /// <summary>打开弹层；<paramref name="note"/> 用于说明被排除的整合包等。</summary>
    public void Show(IReadOnlyList<DroppedFileInfo> files, string? note)
    {
        ApplyFiles(
            files,
            note,
            preferredInstanceId: _stack.Config.Settings.SelectedInstanceId,
            preserveNote: false);
    }

    /// <summary>弹层已经打开时继续拖入文件：合并到当前列表，并保留已选实例。</summary>
    public void AddFiles(IReadOnlyList<DroppedFileInfo> files, string? note)
    {
        if (IsBusy)
        {
            return;
        }

        var merged = Files
            .Concat(files)
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        ApplyFiles(merged, note, SelectedInstance?.Id, preserveNote: true);
    }

    private void ApplyFiles(
        IReadOnlyList<DroppedFileInfo> files,
        string? note,
        string? preferredInstanceId,
        bool preserveNote)
    {
        Files = files;
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(HasInstallableFiles));
        OnPropertyChanged(nameof(CloseButtonText));

        // 追加文件时没有新的提示就保留之前的说明（例如「整合包需单独拖入」）。
        if (!string.IsNullOrWhiteSpace(note) || !preserveNote)
        {
            Note = note ?? string.Empty;
        }

        Title = files.Count switch
        {
            0 => "没有可安装的文件",
            1 => files[0].FileName,
            _ => $"{files.Count} 个文件",
        };
        Summary = files.Count == 0
            ? "没有可安装的资源文件。"
            : files
                .GroupBy(file => file.KindText)
                .Select(group => $"{group.Key} ×{group.Count()}")
                .Aggregate((left, right) => $"{left}、{right}");

        if (HasInstallableFiles)
        {
            Instances = _stack.GetInstalledVersions()
                .Select(version => new DropInstanceOption
                {
                    Info = version,
                    Isolated = _stack.IsIsolated(version.Id),
                })
                .ToList();
            OnPropertyChanged(nameof(Instances));

            SelectedInstance = Instances.FirstOrDefault(instance =>
                                  string.Equals(instance.Id, preferredInstanceId, StringComparison.OrdinalIgnoreCase))
                              ?? Instances.FirstOrDefault();

            StatusText = Instances.Count == 0
                ? "还没有可用实例，请先到「实例列表」安装游戏。"
                : string.Empty;
        }
        else
        {
            // 全部无法识别：只展示识别结果，不要求选择实例。
            Instances = Array.Empty<DropInstanceOption>();
            OnPropertyChanged(nameof(Instances));
            SelectedInstance = null;
            StatusText = "请拖入模组、资源包、光影包、整合包或存档文件夹。";
        }

        IsOpen = true;
        UpdateDestination();
        RaiseInstallState();
    }

    /// <summary>关闭弹层（不执行安装）。</summary>
    public void Hide()
    {
        if (IsBusy)
        {
            return;
        }

        IsOpen = false;
        StatusText = string.Empty;
    }

    private async Task InstallAsync()
    {
        var target = SelectedInstance;
        if (target is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        var succeeded = 0;
        var failed = new List<string>();

        try
        {
            var instanceDirectory = _stack.GetInstanceDirectory(target.Id, target.Isolated);

            foreach (var file in Files.Where(file => file.IsInstallableResource))
            {
                if (file.ResourceKind is not { } kind)
                {
                    continue;
                }

                // 复制是同步文件操作，放到后台避免大文件把界面卡住。
                var imported = await Task.Run(() => _stack.Resources.Import(instanceDirectory, kind, file.Path));
                if (imported)
                {
                    succeeded++;
                }
                else
                {
                    failed.Add(file.FileName);
                }
            }
        }
        catch (Exception ex)
        {
            _stack.Log.Error("拖放安装失败", ex);
            failed.Add(ex.Message);
        }

        IsBusy = false;

        if (failed.Count > 0)
        {
            StatusText = $"已安装 {succeeded} 个，失败 {failed.Count} 个：{string.Join("；", failed.Take(3))}";
            return;
        }

        var destination = DescribeDestination(target);
        StatusText = $"已安装 {succeeded} 个文件到 {destination}";
        _setShellStatus(StatusText);
        _stack.RaiseChanged();
        IsOpen = false;
    }

    private void UpdateDestination()
    {
        DestinationText = SelectedInstance is null
            ? string.Empty
            : $"安装位置：{DescribeDestination(SelectedInstance)}";
    }

    private string DescribeDestination(DropInstanceOption instance)
    {
        var directory = _stack.GetInstanceDirectory(instance.Id, instance.Isolated);
        var root = _stack.GameRoot;

        var relative = PathUtil.IsSamePath(directory, root)
            ? ".minecraft"
            : Path.GetRelativePath(root, directory);

        var kinds = Files
            .Where(file => file.IsInstallableResource && file.ResourceKind is not null)
            .Select(file => ResourceRepository.DirectoryName(file.ResourceKind!.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var folders = kinds.Count > 0 ? string.Join("、", kinds) : "资源目录";
        var mode = instance.Isolated ? "版本隔离" : "共享目录";
        return $"{relative}\\{folders}（{mode}）";
    }

    private void RaiseInstallState()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanClose));
        _installCommand.RaiseCanExecuteChanged();
        _closeCommand.RaiseCanExecuteChanged();
    }
}
