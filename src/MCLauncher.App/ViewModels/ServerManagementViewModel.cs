using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Models;

namespace MCLauncher.App.ViewModels;

/// <summary>服务器列表的一行。</summary>
public sealed class ServerRowItem : ViewModelBase
{
    private string _name;
    private string _address;

    public ServerRowItem(MinecraftServerEntry entry)
    {
        Entry = entry;
        _name = entry.Name;
        _address = entry.Address;
        Icon = DecodeIcon(entry.IconBase64);
    }

    public MinecraftServerEntry Entry { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                Entry.Name = value;
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string Address
    {
        get => _address;
        set
        {
            if (SetProperty(ref _address, value))
            {
                Entry.Address = value;
            }
        }
    }

    public ImageSource? Icon { get; }

    public bool HasIcon => Icon is not null;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名服务器" : Name;

    private static ImageSource? DecodeIcon(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var stream = new MemoryStream(bytes);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>资源包下载策略下拉项。</summary>
public sealed class ResourcePolicyOption
{
    public required string Label { get; init; }

    public required ServerResourcePackPolicy Policy { get; init; }
}

/// <summary>
/// 实例管理的「服务器管理」：读写实例目录下的 servers.dat。
/// 列表、编辑字段与增删排序行为对齐 Minecraft 的服务器列表。
/// </summary>
public sealed class ServerManagementViewModel : ViewModelBase
{
    private static readonly IReadOnlyList<ResourcePolicyOption> PolicyOptions = new[]
    {
        new ResourcePolicyOption { Label = "要求下载时下载", Policy = ServerResourcePackPolicy.Ask },
        new ResourcePolicyOption { Label = "总是下载", Policy = ServerResourcePackPolicy.Always },
        new ResourcePolicyOption { Label = "从不下载", Policy = ServerResourcePackPolicy.Never },
    };

    private readonly LauncherStack _stack;
    private readonly Action<string, string, int> _join;
    private readonly DispatcherTimer _saveTimer;

    private string _instanceId = string.Empty;
    private string _instanceDirectory = string.Empty;
    private string _statusText = string.Empty;

    private ServerRowItem? _selectedServer;
    private string _editName = string.Empty;
    private string _editAddress = string.Empty;
    private ResourcePolicyOption? _selectedPolicy;
    private bool _loadingEditor;
    private bool _dirty;

    public ServerManagementViewModel(LauncherStack stack, Action<string, string, int> join)
    {
        _stack = stack;
        _join = join;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        RefreshCommand = new RelayCommand(() =>
        {
            FlushPendingSave();
            Show(_instanceId, _instanceDirectory);
        });
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove, () => SelectedServer is not null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => Move(1), () => CanMove(1));
        JoinCommand = new RelayCommand(Join, () => SelectedServer is not null);
    }

    public ObservableCollection<ServerRowItem> Servers { get; } = new();

    public IReadOnlyList<ResourcePolicyOption> ResourcePolicies => PolicyOptions;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasSelection => SelectedServer is not null;

    public ServerRowItem? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (!SetProperty(ref _selectedServer, value))
            {
                return;
            }

            LoadEditor(value);
            OnPropertyChanged(nameof(HasSelection));
            RaiseCommandStates();
        }
    }

    public string EditName
    {
        get => _editName;
        set
        {
            if (!SetProperty(ref _editName, value) || _loadingEditor || SelectedServer is null)
            {
                return;
            }

            SelectedServer.Name = value;
            ScheduleSave();
        }
    }

    public string EditAddress
    {
        get => _editAddress;
        set
        {
            if (!SetProperty(ref _editAddress, value) || _loadingEditor || SelectedServer is null)
            {
                return;
            }

            SelectedServer.Address = value;
            ScheduleSave();
        }
    }

    public ResourcePolicyOption? SelectedPolicy
    {
        get => _selectedPolicy;
        set
        {
            if (!SetProperty(ref _selectedPolicy, value) || _loadingEditor || SelectedServer is null || value is null)
            {
                return;
            }

            SelectedServer.Entry.ResourcePolicy = value.Policy;
            ScheduleSave();
        }
    }

    public ICommand RefreshCommand { get; }

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand JoinCommand { get; }

    /// <summary>切换实例时重新读取 servers.dat。</summary>
    public void Show(string versionId, string instanceDirectory)
    {
        // 切实例前先把上一个实例的编辑落盘：否则防抖计时器稍后会把旧列表写进新实例，
        // 旧实例的改动也会丢失（FlushPendingSave 内部会停止计时器）。
        FlushPendingSave();
        _saveTimer.Stop();

        _instanceId = versionId;
        _instanceDirectory = instanceDirectory;

        if (instanceDirectory.Length == 0)
        {
            Servers.Clear();
            SelectedServer = null;
            StatusText = "请先打开一个实例。";
            return;
        }

        var previousName = _selectedServer?.Entry.Name;
        var previousAddress = _selectedServer?.Entry.Address;

        Servers.Clear();
        foreach (var entry in _stack.ServerList.Load(instanceDirectory))
        {
            Servers.Add(new ServerRowItem(entry));
        }

        SelectedServer = previousName is null
            ? Servers.FirstOrDefault()
            : Servers.FirstOrDefault(row =>
                row.Entry.Name == previousName && row.Entry.Address == previousAddress)
              ?? Servers.FirstOrDefault();

        RaiseCommandStates();
        StatusText = Servers.Count == 0
            ? "还没有服务器。点「添加」新建一条，填好地址后就能直接「加入」。"
            : $"共 {Servers.Count} 个服务器。编辑内容会自动保存到 servers.dat。";
    }

    // ---------------------------------------------------------------- 列表操作

    private void Add()
    {
        var entry = new MinecraftServerEntry();
        var row = new ServerRowItem(entry);
        var index = SelectedServer is null ? Servers.Count : Servers.IndexOf(SelectedServer) + 1;

        Servers.Insert(index, row);
        SelectedServer = row;
        RaiseCommandStates();
        ScheduleSave();

        StatusText = "已添加一行，请填写服务器地址（例如 play.example.com:25565）。";
    }

    private void Remove()
    {
        var row = SelectedServer;
        if (row is null)
        {
            return;
        }

        if (MessageBox.Show($"从列表删除服务器「{row.DisplayName}」？", "删除服务器",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var index = Servers.IndexOf(row);
        Servers.Remove(row);
        SelectedServer = Servers.Count == 0
            ? null
            : Servers[Math.Min(index, Servers.Count - 1)];
        RaiseCommandStates();
        ScheduleSave();

        StatusText = Servers.Count == 0 ? "服务器列表已清空。" : $"已删除 {row.DisplayName}。";
    }

    private void Move(int delta)
    {
        var row = SelectedServer;
        if (row is null)
        {
            return;
        }

        var index = Servers.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Servers.Count)
        {
            return;
        }

        Servers.Move(index, target);
        SelectedServer = row;
        RaiseCommandStates();
        ScheduleSave();
        StatusText = delta < 0 ? "已上移。" : "已下移。";
    }

    private bool CanMove(int delta)
    {
        var row = SelectedServer;
        if (row is null)
        {
            return false;
        }

        var target = Servers.IndexOf(row) + delta;
        return target >= 0 && target < Servers.Count;
    }

    private void Join()
    {
        var row = SelectedServer;
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.Address))
        {
            StatusText = "请先填写服务器地址，例如 play.example.com:25565。";
            return;
        }

        if (_instanceId.Length == 0)
        {
            return;
        }

        var (host, port) = ParseAddress(row.Address);
        if (host.Length == 0)
        {
            StatusText = "服务器地址无效，请填写 host 或 host:port。";
            return;
        }

        FlushPendingSave();
        StatusText = $"正在启动游戏并加入 {host}:{port} …";
        _join(_instanceId, host, port);
    }

    /// <summary>把 <c>host</c> / <c>host:port</c> / <c>[ipv6]:port</c> 拆成主机与端口。</summary>
    public static (string Host, int Port) ParseAddress(string address)
    {
        var text = address.Trim();
        if (text.Length == 0)
        {
            return (string.Empty, 25565);
        }

        if (text[0] == '[')
        {
            var end = text.IndexOf(']');
            if (end > 1)
            {
                var host = text[1..end];
                return end + 1 < text.Length && text[end + 1] == ':'
                       && int.TryParse(text[(end + 2)..], out var bracketedPort)
                    ? (host, ClampPort(bracketedPort))
                    : (host, 25565);
            }
        }

        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            var host = text[..colon];
            if (int.TryParse(text[(colon + 1)..], out var port))
            {
                return (host, ClampPort(port));
            }
        }

        return (text, 25565);
    }

    private static int ClampPort(int port) => port is > 0 and <= 65535 ? port : 25565;

    // ---------------------------------------------------------------- 编辑与保存

    private void LoadEditor(ServerRowItem? row)
    {
        _loadingEditor = true;
        try
        {
            EditName = row?.Name ?? string.Empty;
            EditAddress = row?.Address ?? string.Empty;
            SelectedPolicy = row is null
                ? null
                : PolicyOptions.FirstOrDefault(option => option.Policy == row.Entry.ResourcePolicy)
                  ?? PolicyOptions[0];
        }
        finally
        {
            _loadingEditor = false;
        }
    }

    private void ScheduleSave()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushPendingSave()
    {
        if (!_dirty)
        {
            return;
        }

        _saveTimer.Stop();
        SaveNow();
    }

    private void SaveNow()
    {
        _dirty = false;
        if (_instanceDirectory.Length == 0)
        {
            return;
        }

        var entries = Servers.Select(row => row.Entry).ToList();
        StatusText = _stack.ServerList.Save(_instanceDirectory, entries)
            ? $"已保存 {entries.Count} 个服务器到 servers.dat。"
            : "保存 servers.dat 失败，文件可能被占用。";
    }

    private void RaiseCommandStates()
    {
        (RemoveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (JoinCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
