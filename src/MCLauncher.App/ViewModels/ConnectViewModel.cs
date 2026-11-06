using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Connect;
using MCLauncher.Core.Models;

namespace MCLauncher.App.ViewModels;

/// <summary>联机方式。</summary>
public enum ConnectMethod
{
    Choose,
    Direct,
    Frp,
    Terracotta,
}

/// <summary>
/// 联机页：好友直连、樱花穿透、陶瓦联机三种方式，以及按地址直接加入。
/// </summary>
public sealed class ConnectViewModel : ViewModelBase
{
    private readonly LauncherStack _stack;
    private readonly Action<string, string, int> _launchServer;

    private readonly AsyncRelayCommand _refreshNetworkCommand;
    private readonly AsyncRelayCommand _detectPortCommand;
    private readonly AsyncRelayCommand _startDirectCommand;
    private readonly AsyncRelayCommand _stopDirectCommand;
    private readonly AsyncRelayCommand _joinDirectCommand;
    private readonly AsyncRelayCommand _startFrpCommand;
    private readonly AsyncRelayCommand _stopFrpCommand;
    private readonly AsyncRelayCommand _installFrpCommand;
    private readonly AsyncRelayCommand _installTcCommand;
    private readonly AsyncRelayCommand _startTcHostCommand;
    private readonly AsyncRelayCommand _joinTcCommand;
    private readonly AsyncRelayCommand _stopTcCommand;
    private readonly RelayCommand _joinAddressCommand;

    private ConnectMethod _method = ConnectMethod.Choose;
    private InstalledVersionInfo? _selectedInstance;

    // ---- 直连 ----
    private string _lanPortText = string.Empty;
    private bool _useUpnp = true;
    private string _publicAddress = string.Empty;
    private bool _isDirectHosting;
    private string _directInvite = string.Empty;
    private string _directStatus = "先在游戏里选择「对局域网开放」，再开启房间。";
    private string _directConnectionsText = string.Empty;

    // ---- 樱花穿透 ----
    private string _frpAccessKey = string.Empty;
    private string _frpStatusText = "尚未启动";
    private string _frpRemoteAddress = string.Empty;
    private bool _isFrpRunning;
    private bool _isFrpInstalling;
    private bool _frpBinaryReady;

    // ---- 樱花穿透隧道管理（API v4） ----
    private readonly AsyncRelayCommand _loadFrpTunnelsCommand;
    private readonly AsyncRelayCommand _openTunnelEditorCommand;
    private readonly AsyncRelayCommand _createTunnelCommand;
    private readonly AsyncRelayCommand<FrpTunnelItem> _deleteTunnelCommand;
    private readonly RelayCommand _scanLocalPortsCommand;
    private readonly RelayCommand _closeTunnelEditorCommand;
    private readonly Dictionary<int, string> _frpNodeNames = new();

    private string _frpTunnelStatus = "填写访问密钥后点「刷新隧道」。";
    private bool _isFrpBusy;
    private bool _isTunnelEditorOpen;
    private string _newTunnelName = "Minecraft";
    private string _newTunnelPort = string.Empty;
    private string _newTunnelRemote = string.Empty;
    private string _newTunnelNote = string.Empty;
    private string _createTunnelStatus = string.Empty;
    private FrpNodeOption? _selectedTunnelNode;
    private TunnelTypeOption? _selectedTunnelType;
    private LocalPortOption? _selectedLocalPort;
    private FrpTunnelItem? _selectedTunnel;

    // ---- 陶瓦联机 ----
    private string _tcPlayerName = string.Empty;
    private string _tcStatusText = "尚未检测陶瓦联机工具。";
    private string _tcRoom = string.Empty;
    private string _tcUrl = string.Empty;
    private string _tcJoinCode = string.Empty;
    private bool _tcBinaryReady;
    private bool _isTcInstalling;
    private bool _isTcRunning;
    private bool _isTcBusy;
    private double _tcInstallProgress;
    private string _tcSpeedText = string.Empty;

    // ---- 加入 ----
    private string _joinInviteText = string.Empty;
    private string _joinAddressText = string.Empty;
    private string _joinStatusText = string.Empty;

    private string _networkStatus = "尚未扫描网络。";

    public ConnectViewModel(LauncherStack stack, Action<string, string, int> launchServer)
    {
        _stack = stack;
        _launchServer = launchServer;

        _frpAccessKey = stack.Config.Settings.FrpAccessKey ?? string.Empty;
        _frpBinaryReady = stack.Frp.IsBinaryReady();
        _tcPlayerName = stack.Config.Settings.TerracottaPlayerName
                        ?? stack.Accounts.Selected?.Name
                        ?? "MCLauncher";

        _refreshNetworkCommand = new AsyncRelayCommand(
            () => RefreshNetworkAsync(force: true),
            () => true,
            ex => NetworkStatus = "扫描网络失败：" + ex.Message);
        _detectPortCommand = new AsyncRelayCommand(
            () => DetectPortAsync(silent: false),
            () => SelectedInstance is not null,
            ex => DirectStatus = "检测端口失败：" + ex.Message);
        _startDirectCommand = new AsyncRelayCommand(
            StartDirectAsync,
            () => !IsDirectHosting && SelectedInstance is not null,
            ex => DirectStatus = ex.Message);
        _stopDirectCommand = new AsyncRelayCommand(
            StopDirectAsync,
            () => IsDirectHosting,
            ex => DirectStatus = ex.Message);
        _joinDirectCommand = new AsyncRelayCommand(
            JoinDirectAsync,
            () => !string.IsNullOrWhiteSpace(JoinInviteText),
            ex => JoinStatusText = ex.Message);

        _startFrpCommand = new AsyncRelayCommand(
            StartFrpAsync,
            () => !IsFrpRunning
                  && !IsFrpInstalling
                  && !string.IsNullOrWhiteSpace(FrpAccessKey)
                  && FrpTunnels.Any(t => t.IsSelected),
            ex => FrpStatusText = ex.Message);
        _stopFrpCommand = new AsyncRelayCommand(
            StopFrpAsync,
            () => IsFrpRunning,
            ex => FrpStatusText = ex.Message);
        _installFrpCommand = new AsyncRelayCommand(
            InstallFrpAsync,
            () => !IsFrpRunning && !IsFrpInstalling,
            ex => FrpStatusText = ex.Message);
        _loadFrpTunnelsCommand = new AsyncRelayCommand(
            LoadFrpTunnelsAsync,
            () => !IsFrpBusy && !string.IsNullOrWhiteSpace(FrpAccessKey),
            ex => FrpTunnelStatus = ex.Message);
        _openTunnelEditorCommand = new AsyncRelayCommand(
            OpenTunnelEditorAsync,
            () => !IsFrpBusy && !string.IsNullOrWhiteSpace(FrpAccessKey),
            ex => FrpTunnelStatus = ex.Message);
        _createTunnelCommand = new AsyncRelayCommand(
            CreateTunnelAsync,
            () => !IsFrpBusy
                  && IsTunnelEditorOpen
                  && !string.IsNullOrWhiteSpace(NewTunnelName)
                  && SelectedTunnelNode is not null
                  && SelectedTunnelType is not null
                  && (!SelectedTunnelType.NeedsPort || !string.IsNullOrWhiteSpace(NewTunnelPort)),
            ex => CreateTunnelStatus = ex.Message);
        _deleteTunnelCommand = new AsyncRelayCommand<FrpTunnelItem>(
            DeleteTunnelAsync,
            _ => !IsFrpBusy);
        _scanLocalPortsCommand = new RelayCommand(ScanLocalPorts);
        _closeTunnelEditorCommand = new RelayCommand(() => IsTunnelEditorOpen = false);

        _installTcCommand = new AsyncRelayCommand(
            InstallTerracottaAsync,
            () => !IsTcInstalling && !IsTcRunning,
            ex => TcStatusText = ex.Message);
        _startTcHostCommand = new AsyncRelayCommand(
            StartTerracottaHostAsync,
            () => TcBinaryReady && !IsTcRunning && !IsTcInstalling,
            ex => TcStatusText = ex.Message);
        _joinTcCommand = new AsyncRelayCommand(
            JoinTerracottaAsync,
            () => TcBinaryReady && !IsTcRunning && !IsTcInstalling && !string.IsNullOrWhiteSpace(TcJoinCode),
            ex => TcStatusText = ex.Message);
        _stopTcCommand = new AsyncRelayCommand(
            StopTerracottaAsync,
            () => IsTcRunning || IsTcBusy,
            ex => TcStatusText = ex.Message);
        _joinAddressCommand = new RelayCommand(
            JoinByAddress,
            () => SelectedInstance is not null && !string.IsNullOrWhiteSpace(JoinAddressText));

        ShowDirectCommand = new RelayCommand(() => SwitchMethod(ConnectMethod.Direct));
        ShowFrpCommand = new RelayCommand(() => SwitchMethod(ConnectMethod.Frp));
        ShowTerracottaCommand = new RelayCommand(() => SwitchMethod(ConnectMethod.Terracotta));
        BackCommand = new RelayCommand(() => SwitchMethod(ConnectMethod.Choose));
        CopyDirectInviteCommand = new RelayCommand(() => Copy(DirectInvite));
        CopyFrpAddressCommand = new RelayCommand(() => Copy(FrpRemoteAddress));
        CopyTcRoomCommand = new RelayCommand(() => Copy(TcRoom));
        CopyTcUrlCommand = new RelayCommand(() => Copy(TcUrl));

        _stack.DirectConnect.StateChanged += (_, state) => UiThread.Post(() => ApplyDirectState(state));
        _stack.Frp.StateChanged += (_, state) => UiThread.Post(() => ApplyFrpState(state));
        _stack.Frp.LogReceived += (_, line) => UiThread.Post(() => AppendFrpLog(line));
        _stack.Terracotta.StateChanged += (_, state) => UiThread.Post(() => ApplyTerracottaState(state));
        _stack.Changed += (_, _) => UiThread.Post(RefreshInstances);

        RefreshInstances();
        ApplyDirectState(_stack.DirectConnect.State);
        ApplyFrpState(_stack.Frp.State);
    }

    // ---------------------------------------------------------------- 导航

    public bool IsChoosePage => _method == ConnectMethod.Choose;

    public bool IsDirectPage => _method == ConnectMethod.Direct;

    public bool IsFrpPage => _method == ConnectMethod.Frp;

    public bool IsTerracottaPage => _method == ConnectMethod.Terracotta;

    public bool IsMethodPage => _method != ConnectMethod.Choose;

    public string HeaderTitle => _method switch
    {
        ConnectMethod.Direct => "好友直连",
        ConnectMethod.Frp => "樱花穿透",
        ConnectMethod.Terracotta => "陶瓦联机",
        _ => "联机",
    };

    public string HeaderSubtitle => _method switch
    {
        ConnectMethod.Direct => "公网 IPv6 / 路由器 UPnP / 同一局域网，不需要第三方账号。",
        ConnectMethod.Frp => "用樱花穿透官方 frpc 把本地世界映射到公网，需要自备访问密钥。",
        ConnectMethod.Terracotta => "独立开源联机工具（AGPL-3.0），用房间码和好友互联。",
        _ => "选择一种联机方式，按指引与好友一起游玩。",
    };

    public ICommand ShowDirectCommand { get; }

    public ICommand ShowFrpCommand { get; }

    public ICommand ShowTerracottaCommand { get; }

    public ICommand BackCommand { get; }

    // ---------------------------------------------------------------- 实例

    public ObservableCollection<InstalledVersionInfo> Instances { get; } = new();

    public InstalledVersionInfo? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedInstanceSummary));
            OnPropertyChanged(nameof(FrpLocalPortHint));
            RaiseCommandStates();
            _ = DetectPortAsync(silent: true);
        }
    }

    public string SelectedInstanceSummary => SelectedInstance is null
        ? "还没有选择实例。"
        : SelectedInstance.Subtitle;

    // ---------------------------------------------------------------- 直连

    public string LanPortText
    {
        get => _lanPortText;
        set
        {
            if (SetProperty(ref _lanPortText, value))
            {
                OnPropertyChanged(nameof(FrpLocalPortHint));
            }
        }
    }

    public bool UseUpnp
    {
        get => _useUpnp;
        set => SetProperty(ref _useUpnp, value);
    }

    public string PublicAddress
    {
        get => _publicAddress;
        set => SetProperty(ref _publicAddress, value);
    }

    public bool IsDirectHosting
    {
        get => _isDirectHosting;
        private set
        {
            if (SetProperty(ref _isDirectHosting, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string DirectInvite
    {
        get => _directInvite;
        private set => SetProperty(ref _directInvite, value);
    }

    public string DirectStatus
    {
        get => _directStatus;
        private set => SetProperty(ref _directStatus, value);
    }

    public string DirectConnectionsText
    {
        get => _directConnectionsText;
        private set => SetProperty(ref _directConnectionsText, value);
    }

    public ObservableCollection<string> DirectEndpoints { get; } = new();

    public ObservableCollection<string> DirectMessages { get; } = new();

    public ICommand RefreshNetworkCommand => _refreshNetworkCommand;

    public ICommand DetectPortCommand => _detectPortCommand;

    public ICommand StartDirectCommand => _startDirectCommand;

    public ICommand StopDirectCommand => _stopDirectCommand;

    public ICommand CopyDirectInviteCommand { get; }

    public string NetworkStatus
    {
        get => _networkStatus;
        private set => SetProperty(ref _networkStatus, value);
    }

    public ObservableCollection<string> NetworkLines { get; } = new();

    // ---------------------------------------------------------------- 樱花穿透

    public string FrpAccessKey
    {
        get => _frpAccessKey;
        set
        {
            if (SetProperty(ref _frpAccessKey, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string FrpStatusText
    {
        get => _frpStatusText;
        private set => SetProperty(ref _frpStatusText, value);
    }

    public string FrpRemoteAddress
    {
        get => _frpRemoteAddress;
        private set => SetProperty(ref _frpRemoteAddress, value);
    }

    public bool IsFrpRunning
    {
        get => _isFrpRunning;
        private set
        {
            if (SetProperty(ref _isFrpRunning, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>frpc 二进制是否已下载并通过基本校验。</summary>
    public bool FrpBinaryReady
    {
        get => _frpBinaryReady;
        private set
        {
            if (SetProperty(ref _frpBinaryReady, value))
            {
                OnPropertyChanged(nameof(FrpPreparationHint));
            }
        }
    }

    /// <summary>正在下载或校验 frpc。</summary>
    public bool IsFrpInstalling
    {
        get => _isFrpInstalling;
        private set
        {
            if (SetProperty(ref _isFrpInstalling, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string FrpPreparationHint => FrpBinaryReady
        ? "frpc 已就绪，可以直接启动穿透。"
        : "frpc 尚未下载：启动穿透时会自动下载，也可以先点「下载 / 校验 frpc」准备好。";

    public string FrpLocalPortHint => string.IsNullOrWhiteSpace(LanPortText)
        ? "选择账号里的隧道后会直接启动；隧道本地端口要设成游戏里「对局域网开放」显示的端口。"
        : $"已检测到局域网端口 {LanPortText}：隧道本地端口应设为它。";

    public ObservableCollection<string> FrpLogs { get; } = new();

    public ICommand StartFrpCommand => _startFrpCommand;

    public ICommand StopFrpCommand => _stopFrpCommand;

    public ICommand InstallFrpCommand => _installFrpCommand;

    public ICommand CopyFrpAddressCommand { get; }

    /// <summary>账号下的隧道列表（来自 SakuraFrp API）。</summary>
    public ObservableCollection<FrpTunnelItem> FrpTunnels { get; } = new();

    /// <summary>可用于新建隧道的节点。</summary>
    public ObservableCollection<FrpNodeOption> FrpNodes { get; } = new();

    /// <summary>当前节点支持的隧道类型（禁 UDP 的节点没有 UDP / WOL，不能建站的节点没有 HTTP / HTTPS）。</summary>
    public ObservableCollection<TunnelTypeOption> AvailableTunnelTypes { get; } = new();

    /// <summary>本机正在监听的 TCP 端口，供新建隧道时挑选。</summary>
    public ObservableCollection<LocalPortOption> FrpLocalPorts { get; } = new();

    public string FrpTunnelStatus
    {
        get => _frpTunnelStatus;
        private set => SetProperty(ref _frpTunnelStatus, value);
    }

    public bool IsFrpBusy
    {
        get => _isFrpBusy;
        private set
        {
            if (SetProperty(ref _isFrpBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsTunnelEditorOpen
    {
        get => _isTunnelEditorOpen;
        private set
        {
            if (SetProperty(ref _isTunnelEditorOpen, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string NewTunnelName
    {
        get => _newTunnelName;
        set
        {
            if (SetProperty(ref _newTunnelName, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>新建隧道的本地端口；可从下拉候选里选，也可直接输入。</summary>
    public string NewTunnelPort
    {
        get => _newTunnelPort;
        set
        {
            if (SetProperty(ref _newTunnelPort, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string NewTunnelNote
    {
        get => _newTunnelNote;
        set => SetProperty(ref _newTunnelNote, value);
    }

    /// <summary>HTTP / HTTPS 隧道绑定的域名，留空由服务端分配。</summary>
    public string NewTunnelRemote
    {
        get => _newTunnelRemote;
        set => SetProperty(ref _newTunnelRemote, value);
    }

    /// <summary>新建隧道选择的类型（TCP / UDP / HTTP / HTTPS / WOL）。</summary>
    public TunnelTypeOption? SelectedTunnelType
    {
        get => _selectedTunnelType;
        set
        {
            if (!SetProperty(ref _selectedTunnelType, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsTunnelPortVisible));
            OnPropertyChanged(nameof(IsTunnelDomainVisible));
            RaiseCommandStates();
        }
    }

    /// <summary>WOL 隧道不用填本地端口。</summary>
    public bool IsTunnelPortVisible => SelectedTunnelType?.NeedsPort ?? true;

    /// <summary>只有 HTTP / HTTPS 隧道需要绑定域名。</summary>
    public bool IsTunnelDomainVisible => SelectedTunnelType?.NeedsDomain ?? false;

    /// <summary>当前节点能力受限时的提示；没有任何限制时为空串。</summary>
    public string TunnelNodeCapabilityHint
    {
        get
        {
            var node = SelectedTunnelNode;
            if (node is null)
            {
                return string.Empty;
            }

            var hints = new List<string>();
            if (!node.SupportsUdp)
            {
                hints.Add("该节点禁 UDP，无法创建 UDP / WOL 隧道");
            }

            if (!node.WebsiteAllowed)
            {
                hints.Add("该节点不支持建站，无法创建 HTTP / HTTPS 隧道");
            }

            return hints.Count == 0 ? string.Empty : string.Join("；", hints) + "。";
        }
    }

    public string CreateTunnelStatus
    {
        get => _createTunnelStatus;
        private set => SetProperty(ref _createTunnelStatus, value);
    }

    public FrpNodeOption? SelectedTunnelNode
    {
        get => _selectedTunnelNode;
        set
        {
            if (!SetProperty(ref _selectedTunnelNode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TunnelNodeCapabilityHint));
            SyncTunnelTypes();
            RaiseCommandStates();
        }
    }

    public ICommand LoadFrpTunnelsCommand => _loadFrpTunnelsCommand;

    public ICommand OpenTunnelEditorCommand => _openTunnelEditorCommand;

    public ICommand CreateTunnelCommand => _createTunnelCommand;

    public ICommand DeleteTunnelCommand => _deleteTunnelCommand;

    /// <summary>从下拉里选一个已检测到的端口，选中后自动写进「本地端口」。</summary>
    public LocalPortOption? SelectedLocalPort
    {
        get => _selectedLocalPort;
        set
        {
            if (SetProperty(ref _selectedLocalPort, value) && value is not null)
            {
                NewTunnelPort = value.Port.ToString();
            }
        }
    }

    public ICommand ScanLocalPortsCommand => _scanLocalPortsCommand;

    public ICommand CloseTunnelEditorCommand => _closeTunnelEditorCommand;

    /// <summary>
    /// 列表视觉选中项（保留 ListBox 高亮）。
    /// 真正决定要穿透哪几条隧道的是每条的 <see cref="FrpTunnelItem.IsSelected"/> 勾选状态。
    /// </summary>
    public FrpTunnelItem? SelectedTunnel
    {
        get => _selectedTunnel;
        set
        {
            if (SetProperty(ref _selectedTunnel, value))
            {
                RaiseCommandStates();
            }
        }
    }

    // ---------------------------------------------------------------- 陶瓦联机

    public string TcPlayerName
    {
        get => _tcPlayerName;
        set => SetProperty(ref _tcPlayerName, value);
    }

    public string TcStatusText
    {
        get => _tcStatusText;
        private set => SetProperty(ref _tcStatusText, value);
    }

    public string TcRoom
    {
        get => _tcRoom;
        private set => SetProperty(ref _tcRoom, value);
    }

    public string TcUrl
    {
        get => _tcUrl;
        private set => SetProperty(ref _tcUrl, value);
    }

    public string TcJoinCode
    {
        get => _tcJoinCode;
        set
        {
            if (SetProperty(ref _tcJoinCode, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool TcBinaryReady
    {
        get => _tcBinaryReady;
        private set
        {
            if (SetProperty(ref _tcBinaryReady, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsTcInstalling
    {
        get => _isTcInstalling;
        private set
        {
            if (SetProperty(ref _isTcInstalling, value))
            {
                OnPropertyChanged(nameof(IsTcSpeedVisible));
                RaiseCommandStates();
            }
        }
    }

    public bool IsTcRunning
    {
        get => _isTcRunning;
        private set
        {
            if (SetProperty(ref _isTcRunning, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsTcBusy
    {
        get => _isTcBusy;
        private set
        {
            if (SetProperty(ref _isTcBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public double TcInstallProgress
    {
        get => _tcInstallProgress;
        private set => SetProperty(ref _tcInstallProgress, value);
    }

    public string TcSpeedText
    {
        get => _tcSpeedText;
        private set
        {
            if (SetProperty(ref _tcSpeedText, value))
            {
                OnPropertyChanged(nameof(IsTcSpeedVisible));
            }
        }
    }

    public bool IsTcSpeedVisible => IsTcInstalling && !string.IsNullOrWhiteSpace(TcSpeedText);

    public ICommand InstallTcCommand => _installTcCommand;

    public ICommand StartTcHostCommand => _startTcHostCommand;

    public ICommand JoinTcCommand => _joinTcCommand;

    public ICommand StopTcCommand => _stopTcCommand;

    public ICommand CopyTcRoomCommand { get; }

    public ICommand CopyTcUrlCommand { get; }

    // ---------------------------------------------------------------- 加入

    public string JoinInviteText
    {
        get => _joinInviteText;
        set
        {
            if (SetProperty(ref _joinInviteText, value))
            {
                _joinDirectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string JoinAddressText
    {
        get => _joinAddressText;
        set
        {
            if (SetProperty(ref _joinAddressText, value))
            {
                _joinAddressCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string JoinStatusText
    {
        get => _joinStatusText;
        private set => SetProperty(ref _joinStatusText, value);
    }

    public ICommand JoinDirectCommand => _joinDirectCommand;

    public ICommand JoinAddressCommand => _joinAddressCommand;

    // ---------------------------------------------------------------- 实现

    private void SwitchMethod(ConnectMethod method)
    {
        if (_method == method)
        {
            return;
        }

        _method = method;
        OnPropertyChanged(nameof(IsChoosePage));
        OnPropertyChanged(nameof(IsDirectPage));
        OnPropertyChanged(nameof(IsFrpPage));
        OnPropertyChanged(nameof(IsTerracottaPage));
        OnPropertyChanged(nameof(IsMethodPage));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));

        // 离开樱花穿透页时把新建弹层收起来，避免覆盖在其他页面上。
        if (method != ConnectMethod.Frp)
        {
            IsTunnelEditorOpen = false;
        }

        if (method == ConnectMethod.Direct)
        {
            _ = RefreshNetworkAsync(force: false);
            _ = DetectPortAsync(silent: true);
        }
        else if (method == ConnectMethod.Frp)
        {
            _ = DetectPortAsync(silent: true);

            // 已保存访问密钥就直接把隧道列表拉回来，用户打开页面即可选择。
            if (FrpTunnels.Count == 0
                && !IsFrpBusy
                && !string.IsNullOrWhiteSpace(FrpAccessKey))
            {
                _ = LoadFrpTunnelsAsync();
            }
        }
        else if (method == ConnectMethod.Terracotta)
        {
            _ = CheckTerracottaBinaryAsync();
        }
    }

    private void RefreshInstances()
    {
        var previousId = SelectedInstance?.Id ?? _stack.Config.Settings.SelectedInstanceId;
        var all = _stack.GetInstalledVersions();

        Instances.Clear();
        foreach (var info in all)
        {
            Instances.Add(info);
        }

        if (Instances.Count == 0)
        {
            SelectedInstance = null;
            return;
        }

        SelectedInstance = Instances.FirstOrDefault(item =>
                             string.Equals(item.Id, previousId, StringComparison.OrdinalIgnoreCase))
                         ?? Instances.FirstOrDefault(item =>
                             string.Equals(item.Id, _stack.Config.Settings.SelectedInstanceId, StringComparison.OrdinalIgnoreCase))
                         ?? Instances[0];
    }

    private async Task RefreshNetworkAsync(bool force)
    {
        NetworkStatus = "正在扫描本机地址与 UPnP 网关…";
        NetworkLines.Clear();

        try
        {
            var network = await _stack.DirectConnect.InspectNetworkAsync(force).ConfigureAwait(true);

            foreach (var address in network.Addresses)
            {
                NetworkLines.Add(address.Text);
            }

            foreach (var gateway in network.Gateways)
            {
                var external = string.IsNullOrWhiteSpace(gateway.ExternalAddress) ? "未知" : gateway.ExternalAddress;
                NetworkLines.Add($"UPnP 网关 {gateway.ControlHost}：WAN {external}");
            }

            foreach (var message in network.Messages)
            {
                NetworkLines.Add(message);
            }

            NetworkStatus = network.Addresses.Count == 0
                ? "没有发现可用的本机地址。"
                : $"已发现 {network.Addresses.Count} 个本机地址、{network.Gateways.Count} 个 UPnP 网关。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NetworkStatus = "扫描网络失败：" + ex.Message;
        }
    }

    private async Task DetectPortAsync(bool silent)
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            LanPortText = string.Empty;
            return;
        }

        try
        {
            var directory = _stack.GetInstanceDirectory(instance.Id, _stack.IsIsolated(instance.Id));
            var port = await _stack.DirectConnect.DetectLanPortAsync(directory).ConfigureAwait(true);

            if (port is { } value)
            {
                LanPortText = value.ToString();
                if (!silent)
                {
                    DirectStatus = $"已从游戏日志里检测到局域网端口 {value}。";
                }
            }
            else
            {
                if (!silent)
                {
                    DirectStatus = "日志里没有找到局域网端口。请先在游戏里「对局域网开放」，再刷新或手动填写。";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!silent)
            {
                DirectStatus = "读取游戏日志失败：" + ex.Message;
            }
        }
    }

    private async Task StartDirectAsync()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            DirectStatus = "请先选择要联机的实例。";
            return;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(LanPortText))
        {
            if (!int.TryParse(LanPortText.Trim(), out var parsed) || !DirectProtocol.IsValidPort(parsed))
            {
                DirectStatus = "端口必须是 1–65535 的整数。";
                return;
            }

            port = parsed;
        }

        var directory = _stack.GetInstanceDirectory(instance.Id, _stack.IsIsolated(instance.Id));
        DirectStatus = "正在开启房间…";

        var state = await _stack.DirectConnect.StartHostAsync(new DirectHostRequest
        {
            InstanceId = instance.Id,
            InstanceName = instance.Display,
            InstanceDirectory = directory,
            MinecraftVersion = instance.VersionText,
            Loader = instance.LoaderName,
            LoaderVersion = instance.LoaderVersion,
            Port = port,
            UseUpnp = UseUpnp,
            PublicAddress = string.IsNullOrWhiteSpace(PublicAddress) ? null : PublicAddress.Trim(),
        }).ConfigureAwait(true);

        ApplyDirectState(state);
    }

    private async Task StopDirectAsync()
    {
        DirectStatus = "正在关闭房间…";
        var state = await _stack.DirectConnect.StopHostAsync().ConfigureAwait(true);
        ApplyDirectState(state);
    }

    private void ApplyDirectState(DirectHostState state)
    {
        IsDirectHosting = state.Active;
        DirectInvite = state.Invite ?? string.Empty;
        DirectConnectionsText = state.Active ? $"已连接 {state.Connections} 个" : string.Empty;

        DirectEndpoints.Clear();
        foreach (var endpoint in state.Endpoints)
        {
            DirectEndpoints.Add($"{endpoint.Address}（{endpoint.KindText}）");
        }

        DirectMessages.Clear();
        foreach (var message in state.Messages)
        {
            DirectMessages.Add(message);
        }

        if (state.Active)
        {
            DirectStatus = $"房间已开启：游戏端口 {state.LocalPort}，对外端口 {state.ExposedPort}。";
        }
        else if (state.Messages.Count > 0)
        {
            DirectStatus = state.Messages[0];
        }
    }

    private async Task JoinDirectAsync()
    {
        JoinStatusText = "正在解析邀请并探测地址…";

        DirectJoinResult result;
        try
        {
            result = await _stack.DirectConnect.ResolveInvitationAsync(JoinInviteText).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            JoinStatusText = "邀请无效：" + ex.Message;
            return;
        }

        if (result.Endpoint is null)
        {
            JoinStatusText = "没有可达的好友地址。请确认房主已开启房间，并检查双方网络。"
                             + (result.Failures.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, result.Failures) : string.Empty);
            return;
        }

        var instance = FindMatchingInstance(result.Invitation);
        if (instance is null)
        {
            JoinStatusText = $"没有找到与房主匹配的实例（Minecraft {result.Invitation.MinecraftVersion}，"
                             + $"{result.Invitation.LoaderText}）。请先安装同版本实例。";
            return;
        }

        var endpoint = result.Endpoint;
        LaunchJoin(instance, endpoint.Host, endpoint.Port, $"联机 · {result.Invitation.Name}");
        JoinStatusText = $"正在启动 {instance.Display} 并加入 {endpoint.Address}…";
    }

    private InstalledVersionInfo? FindMatchingInstance(DirectInvitation invitation)
    {
        IEnumerable<InstalledVersionInfo> candidates = Instances.Where(item =>
            string.Equals(item.VersionText, invitation.MinecraftVersion, StringComparison.OrdinalIgnoreCase)
            && LoaderMatches(item, invitation));

        var preferred = SelectedInstance;
        if (preferred is not null && candidates.Contains(preferred))
        {
            return preferred;
        }

        return candidates.FirstOrDefault();
    }

    private static bool LoaderMatches(InstalledVersionInfo instance, DirectInvitation invitation)
    {
        var loader = instance.LoaderName.Equals("原版", StringComparison.OrdinalIgnoreCase)
            ? null
            : instance.LoaderName.ToLowerInvariant();

        return string.Equals(loader, invitation.Loader, StringComparison.OrdinalIgnoreCase);
    }

    private async Task StartFrpAsync()
    {
        var selected = FrpTunnels.Where(t => t.IsSelected && t.Status != 2).ToList();
        if (selected.Count == 0)
        {
            FrpStatusText = "请先勾选至少一条正常状态的隧道。";
            return;
        }

        _stack.Config.Settings.FrpAccessKey = FrpAccessKey.Trim();
        _stack.Config.Save();

        FrpLogs.Clear();
        FrpStatusText = "正在准备 frpc…";

        var ids = selected.Select(t => t.Id.ToString()).ToList();
        await _stack.Frp.StartAsync(FrpAccessKey, ids).ConfigureAwait(true);
        ApplyFrpState(_stack.Frp.State);
    }

    private async Task StopFrpAsync()
    {
        FrpStatusText = "正在停止 frpc…";
        var state = await _stack.Frp.StopAsync().ConfigureAwait(true);
        ApplyFrpState(state);
    }

    private void ApplyFrpState(FrpState state)
    {
        IsFrpInstalling = state.Status == FrpStatusKind.Downloading;
        IsFrpRunning = state.Status is FrpStatusKind.Starting or FrpStatusKind.Running;
        FrpBinaryReady = _stack.Frp.IsBinaryReady();
        FrpRemoteAddress = state.RemoteAddress ?? string.Empty;
        FrpStatusText = state.Message;
    }

    private async Task InstallFrpAsync()
    {
        FrpLogs.Clear();
        FrpStatusText = "正在下载 / 校验 frpc…";
        IsFrpInstalling = true;

        try
        {
            await _stack.Frp
                .EnsureBinaryAsync(line => UiThread.Post(() => AppendFrpLog(line)))
                .ConfigureAwait(true);

            FrpBinaryReady = _stack.Frp.IsBinaryReady();
            FrpStatusText = FrpBinaryReady ? "frpc 已就绪" : "frpc 校验失败，请重试";
        }
        finally
        {
            IsFrpInstalling = false;
        }
    }

    private async Task LoadFrpTunnelsAsync()
    {
        var token = FrpAccessKey.Trim();
        if (token.Length == 0)
        {
            FrpTunnelStatus = "请先填写访问密钥。";
            return;
        }

        IsFrpBusy = true;
        FrpTunnelStatus = "正在读取隧道列表…";

        try
        {
            ApplyNodes(await _stack.SakuraFrp.GetNodesAsync(token).ConfigureAwait(true));

            var tunnels = await _stack.SakuraFrp.GetTunnelsAsync(token).ConfigureAwait(true);

            ApplyTunnels(tunnels);

            // 密钥确实能通才落盘，避免把写错的密钥存进设置。
            _stack.Config.Settings.FrpAccessKey = token;
            _stack.Config.Save();

            FrpTunnelStatus = tunnels.Count == 0
                ? "已连接，账号下还没有隧道。点「新建隧道」创建一条。"
                : $"已连接，共 {tunnels.Count} 条隧道。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FrpTunnelStatus = ex.Message;
        }
        finally
        {
            IsFrpBusy = false;
        }
    }

    private void ApplyNodes(IReadOnlyList<SakuraFrpNode> nodes)
    {
        var selectedId = SelectedTunnelNode?.Id;

        _frpNodeNames.Clear();
        FrpNodes.Clear();

        foreach (var node in nodes.Where(n => n.CanCreate).OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            _frpNodeNames[node.Id] = node.Name;
            FrpNodes.Add(new FrpNodeOption(node));
        }

        // 优先保留用户已选的节点；没有就挑一个允许公开游戏的，再退到第一个。
        SelectedTunnelNode = FrpNodes.FirstOrDefault(n => n.Id == selectedId)
                             ?? FrpNodes.FirstOrDefault(n => n.PublicGameAllowed)
                             ?? FrpNodes.FirstOrDefault();
    }

    /// <summary>
    /// 按当前节点的能力刷新可选类型；原来选中的类型仍在列表里就保留，否则回退到第一项。
    /// </summary>
    private void SyncTunnelTypes()
    {
        var node = SelectedTunnelNode;
        var previous = SelectedTunnelType?.Type;

        AvailableTunnelTypes.Clear();
        if (node is null)
        {
            SelectedTunnelType = null;
            return;
        }

        foreach (var option in TunnelTypeOption.All.Where(option => node.Supports(option.Type)))
        {
            AvailableTunnelTypes.Add(option);
        }

        SelectedTunnelType = AvailableTunnelTypes.FirstOrDefault(option => option.Type == previous)
                             ?? AvailableTunnelTypes.FirstOrDefault();
    }

    private async Task OpenTunnelEditorAsync()
    {
        var token = FrpAccessKey.Trim();
        if (token.Length == 0)
        {
            FrpTunnelStatus = "请先填写访问密钥。";
            return;
        }

        IsTunnelEditorOpen = true;
        CreateTunnelStatus = string.Empty;
        ScanLocalPorts();

        if (FrpNodes.Count > 0)
        {
            return;
        }

        IsFrpBusy = true;
        try
        {
            ApplyNodes(await _stack.SakuraFrp.GetNodesAsync(token).ConfigureAwait(true));
            if (FrpNodes.Count == 0)
            {
                CreateTunnelStatus = "这个账号当前没有可用节点，请稍后再试。";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CreateTunnelStatus = ex.Message;
        }
        finally
        {
            IsFrpBusy = false;
        }
    }

    /// <summary>
    /// 扫描本机正在监听的 TCP 端口填进候选列表；优先选游戏日志里的局域网端口，
    /// 其次 25565，最后退到第一个检测到的端口。
    /// </summary>
    private void ScanLocalPorts()
    {
        FrpLocalPorts.Clear();
        foreach (var port in LocalPortScanner.GetListeningTcpPorts())
        {
            FrpLocalPorts.Add(new LocalPortOption(port));
        }

        var lanPort = int.TryParse(LanPortText.Trim(), out var lan) ? lan : 0;
        var typedPort = int.TryParse(NewTunnelPort.Trim(), out var typed) ? typed : 0;

        SelectedLocalPort = (lanPort > 0 ? FrpLocalPorts.FirstOrDefault(p => p.Port == lanPort) : null)
                            ?? (typedPort > 0 ? FrpLocalPorts.FirstOrDefault(p => p.Port == typedPort) : null)
                            ?? FrpLocalPorts.FirstOrDefault(p => p.Port == 25565)
                            ?? FrpLocalPorts.FirstOrDefault();
    }

    private async Task CreateTunnelAsync()
    {
        var token = FrpAccessKey.Trim();
        if (token.Length == 0)
        {
            CreateTunnelStatus = "请先填写访问密钥。";
            return;
        }

        var node = SelectedTunnelNode;
        if (node is null)
        {
            CreateTunnelStatus = "请先选择节点。";
            return;
        }

        var kind = SelectedTunnelType;
        if (kind is null)
        {
            CreateTunnelStatus = "请先选择隧道类型。";
            return;
        }

        if (!node.Supports(kind.Type))
        {
            CreateTunnelStatus = $"节点「{node.Name}」不支持创建 {kind.Display}。";
            return;
        }

        var port = 0;
        if (kind.NeedsPort
            && (!int.TryParse(NewTunnelPort.Trim(), out port) || port is < 1 or > 65535))
        {
            CreateTunnelStatus = "本地端口必须是 1-65535 之间的数字。";
            return;
        }

        IsFrpBusy = true;
        CreateTunnelStatus = "正在创建隧道…";

        try
        {
            var created = await _stack.SakuraFrp
                .CreateTunnelAsync(
                    token,
                    new SakuraFrpTunnelRequest
                    {
                        Name = NewTunnelName.Trim(),
                        NodeId = node.Id,
                        Type = kind.Type,
                        LocalPort = port,
                        Remote = NewTunnelRemote.Trim(),
                        Note = NewTunnelNote.Trim(),
                    })
                .ConfigureAwait(true);

            IsTunnelEditorOpen = false;
            NewTunnelNote = string.Empty;
            NewTunnelRemote = string.Empty;

            await LoadFrpTunnelsAsync().ConfigureAwait(true);
            // 新建的隧道自动勾选，方便直接点启动穿透。
            if (FrpTunnels.FirstOrDefault(t => t.Id == created.Id) is { } newItem) newItem.IsSelected = true;
            FrpTunnelStatus = $"已创建 {kind.Display}「{created.Name}」（#{created.Id}），远程地址 {created.Remote ?? "由服务端分配"}。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CreateTunnelStatus = ex.Message;
        }
        finally
        {
            IsFrpBusy = false;
        }
    }

    private async Task DeleteTunnelAsync(FrpTunnelItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (MessageBox.Show(
                $"删除隧道「{item.Name}」（#{item.Id}）？删除后无法恢复。",
                "删除隧道",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        IsFrpBusy = true;

        try
        {
            await _stack.SakuraFrp
                .DeleteTunnelsAsync(FrpAccessKey.Trim(), new[] { item.Id })
                .ConfigureAwait(true);

            // 如果这条正在穿透就停掉它。
            await _stack.Frp.StopOneAsync(item.Id.ToString()).ConfigureAwait(true);

            await LoadFrpTunnelsAsync().ConfigureAwait(true);
            FrpTunnelStatus = $"已删除隧道「{item.Name}」。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FrpTunnelStatus = ex.Message;
        }
        finally
        {
            IsFrpBusy = false;
        }
    }

    /// <summary>
    /// 重建隧道列表并保留原来的勾选状态（刷新时不丢用户勾选）。
    /// </summary>
    private void ApplyTunnels(IReadOnlyList<SakuraFrpTunnel> tunnels)
    {
        var prevSelected = FrpTunnels
            .Where(t => t.IsSelected)
            .ToDictionary(t => t.Id, _ => true);

        FrpTunnels.Clear();
        foreach (var tunnel in tunnels.OrderBy(t => t.Id))
        {
            var item = FrpTunnelItem.From(tunnel, _frpNodeNames,
                isSelected: prevSelected.TryGetValue(tunnel.Id, out _));
            item.SelectionChanged += () => _startFrpCommand.RaiseCanExecuteChanged();
            FrpTunnels.Add(item);
        }

        // 视觉选中保留（如果原来有）
        if (SelectedTunnel is { } oldSel)
        {
            SelectedTunnel = FrpTunnels.FirstOrDefault(t => t.Id == oldSel.Id) ?? FrpTunnels.FirstOrDefault();
        }
        else
        {
            SelectedTunnel = FrpTunnels.FirstOrDefault();
        }
    }

    private void AppendFrpLog(string line)
    {
        FrpLogs.Add(line);
        while (FrpLogs.Count > 200)
        {
            FrpLogs.RemoveAt(0);
        }
    }

    private async Task CheckTerracottaBinaryAsync()
    {
        TcStatusText = "正在校验陶瓦联机工具…";

        var ready = await Task.Run(() => _stack.Terracotta.IsBinaryReady()).ConfigureAwait(true);
        TcBinaryReady = ready;
        TcStatusText = ready
            ? "陶瓦联机工具已就绪。房主先在游戏里「对局域网开放」，再创建房间。"
            : "尚未下载陶瓦联机官方工具（独立开源项目，AGPL-3.0）。";
    }

    private async Task InstallTerracottaAsync()
    {
        IsTcInstalling = true;
        TcInstallProgress = 0;
        TcSpeedText = string.Empty;
        TcStatusText = "正在下载陶瓦联机官方工具…";

        var progress = new Progress<TerracottaInstallProgress>(value =>
        {
            TcInstallProgress = value.Fraction;
            TcSpeedText = FormatSpeed(value.BytesPerSecond);
        });

        try
        {
            await _stack.Terracotta.InstallAsync(progress).ConfigureAwait(true);
            TcBinaryReady = true;
            TcStatusText = "陶瓦联机工具已下载并通过 SHA-256 校验。";
        }
        finally
        {
            IsTcInstalling = false;
            TcSpeedText = string.Empty;
        }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return string.Empty;
        }

        return bytesPerSecond < 1024 * 1024
            ? $"{bytesPerSecond / 1024:0} KB/s"
            : $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
    }

    private async Task StartTerracottaHostAsync()
    {
        IsTcBusy = true;
        TcRoom = string.Empty;
        TcUrl = string.Empty;
        TcStatusText = "正在创建陶瓦房间…";

        try
        {
            var playerName = ResolvePlayerName();
            _stack.Config.Settings.TerracottaPlayerName = playerName;
            _stack.Config.Save();

            var state = await _stack.Terracotta.StartHostAsync(playerName).ConfigureAwait(true);
            ApplyTerracottaState(state);

            if (!string.IsNullOrWhiteSpace(state.Room))
            {
                TcStatusText = $"房间已创建：{state.Room}。把房间码发给好友；房主要保持游戏和启动器运行。";
            }
        }
        finally
        {
            IsTcBusy = false;
        }
    }

    private async Task JoinTerracottaAsync()
    {
        IsTcBusy = true;
        TcRoom = string.Empty;
        TcUrl = string.Empty;
        TcStatusText = "正在加入陶瓦房间…";

        try
        {
            var playerName = ResolvePlayerName();
            var state = await _stack.Terracotta.StartJoinAsync(TcJoinCode, playerName).ConfigureAwait(true);
            ApplyTerracottaState(state);

            if (string.IsNullOrWhiteSpace(state.Url))
            {
                return;
            }

            if (!DirectProtocol.TryParseAddress(state.Url!, out var host, out var port))
            {
                TcStatusText = $"已连接，但引擎给出的地址无法识别：{state.Url}";
                return;
            }

            var instance = SelectedInstance;
            if (instance is null)
            {
                TcStatusText = $"已连接（{state.Url}），请选择实例后手动加入。";
                return;
            }

            LaunchJoin(instance, host, port, $"陶瓦 · {TcJoinCode}");
            TcStatusText = $"已连接，正在启动 {instance.Display} 并加入 {state.Url}…";
        }
        finally
        {
            IsTcBusy = false;
        }
    }

    private async Task StopTerracottaAsync()
    {
        TcStatusText = "正在关闭陶瓦房间…";
        var state = await _stack.Terracotta.StopAsync().ConfigureAwait(true);
        ApplyTerracottaState(state);
        TcRoom = string.Empty;
        TcUrl = string.Empty;
        TcStatusText = "陶瓦房间已关闭。";
    }

    private void ApplyTerracottaState(TerracottaState state)
    {
        IsTcRunning = state.Phase is TerracottaPhase.Starting or TerracottaPhase.Hosting
            or TerracottaPhase.Joining or TerracottaPhase.Ready;

        if (!string.IsNullOrWhiteSpace(state.Room))
        {
            TcRoom = state.Room;
        }

        if (!string.IsNullOrWhiteSpace(state.Url))
        {
            TcUrl = state.Url;
        }

        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            TcStatusText = "陶瓦联机失败：" + state.Error;
        }
        else if (!string.IsNullOrWhiteSpace(state.Message))
        {
            TcStatusText = state.Message!;
        }
        else if (state.Phase == TerracottaPhase.Hosting)
        {
            TcStatusText = "正在等待房间号…";
        }
        else if (state.Phase == TerracottaPhase.Joining)
        {
            TcStatusText = "正在等待连接就绪…";
        }
        else if (state.Phase == TerracottaPhase.Ready)
        {
            TcStatusText = state.Room is not null && state.Url is null
                ? $"房间已创建：{state.Room}"
                : $"已连接：{state.Url}";
        }
    }

    private string ResolvePlayerName()
    {
        var configured = TerracottaPlayerName();
        return string.IsNullOrWhiteSpace(configured) ? "MCLauncher" : configured;
    }

    private string TerracottaPlayerName()
    {
        if (!string.IsNullOrWhiteSpace(TcPlayerName))
        {
            return TcPlayerName.Trim();
        }

        return _stack.Accounts.Selected?.Name ?? string.Empty;
    }

    private void JoinByAddress()
    {
        if (!DirectProtocol.TryParseAddress(JoinAddressText, out var host, out var port))
        {
            JoinStatusText = "地址格式无效，请填写 host 或 host:port，例如 1.2.3.4:25565。";
            return;
        }

        var instance = SelectedInstance;
        if (instance is null)
        {
            JoinStatusText = "请先选择实例。";
            return;
        }

        LaunchJoin(instance, host, port, $"联机 · {instance.Display}");
        JoinStatusText = $"正在启动 {instance.Display} 并加入 {DirectProtocol.FormatAddress(host, port)}…";
    }

    private void LaunchJoin(InstalledVersionInfo instance, string host, int port, string serverName)
    {
        var directory = _stack.GetInstanceDirectory(instance.Id, _stack.IsIsolated(instance.Id));
        var address = DirectProtocol.FormatAddress(host, port);

        try
        {
            var servers = _stack.ServerList.Load(directory).ToList();
            var existing = servers.FirstOrDefault(server =>
                string.Equals(server.Address, address, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                servers.Insert(0, new MinecraftServerEntry { Name = serverName, Address = address });
            }
            else
            {
                existing.Name = serverName;
            }

            _stack.ServerList.Save(directory, servers);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _stack.Log.Warn("写入服务器列表失败", ex);
        }

        _launchServer(instance.Id, host, port);
    }

    private void Copy(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _stack.Log.Warn("写入剪贴板失败", ex);
        }
    }

    private void RaiseCommandStates()
    {
        _refreshNetworkCommand.RaiseCanExecuteChanged();
        _detectPortCommand.RaiseCanExecuteChanged();
        _startDirectCommand.RaiseCanExecuteChanged();
        _stopDirectCommand.RaiseCanExecuteChanged();
        _joinDirectCommand.RaiseCanExecuteChanged();
        _startFrpCommand.RaiseCanExecuteChanged();
        _stopFrpCommand.RaiseCanExecuteChanged();
        _installFrpCommand.RaiseCanExecuteChanged();
        _installTcCommand.RaiseCanExecuteChanged();
        _startTcHostCommand.RaiseCanExecuteChanged();
        _joinTcCommand.RaiseCanExecuteChanged();
        _stopTcCommand.RaiseCanExecuteChanged();
        _joinAddressCommand.RaiseCanExecuteChanged();
        _loadFrpTunnelsCommand.RaiseCanExecuteChanged();
        _openTunnelEditorCommand.RaiseCanExecuteChanged();
        _createTunnelCommand.RaiseCanExecuteChanged();
        _deleteTunnelCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>隧道列表里的一行。</summary>
public class FrpTunnelItem : ViewModelBase
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public required string Type { get; init; }

    public required string NodeName { get; init; }

    public required string LocalAddress { get; init; }

    public required string Remote { get; init; }

    public bool Online { get; init; }

    /// <summary>0 正常、2 封禁。</summary>
    public int Status { get; init; }

    public string? StatusReason { get; init; }

    /// <summary>是否勾选为要穿透的隧道（可以勾多条）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke();
            }
        }
    }

    private bool _isSelected;

    /// <summary>勾选状态变化时通知外层 ConnectViewModel 刷新 StartFrpCommand 的 CanExecute。</summary>
    public event Action? SelectionChanged;

    public string Title => $"{Name}  #{Id}";

    public string Detail =>
        $"{Type.ToUpperInvariant()} · 节点 {NodeName} · 本地 {LocalAddress}"
        + (string.IsNullOrWhiteSpace(Remote) ? string.Empty : $" → {Remote}");

    public string StateText => Status == 2
        ? string.IsNullOrWhiteSpace(StatusReason) ? "已封禁" : $"已封禁：{StatusReason}"
        : Online ? "在线" : "离线";

    public static FrpTunnelItem From(SakuraFrpTunnel tunnel, IReadOnlyDictionary<int, string> nodeNames, bool isSelected = false) => new()
    {
        Id = tunnel.Id,
        Name = tunnel.Name,
        Type = tunnel.Type,
        NodeName = nodeNames.TryGetValue(tunnel.Node, out var name) ? name : $"#{tunnel.Node}",
        LocalAddress = $"{tunnel.LocalIp}:{tunnel.LocalPort}",
        Remote = tunnel.Remote,
        Online = tunnel.Online,
        Status = tunnel.Status,
        StatusReason = tunnel.StatusReason,
        IsSelected = isSelected,
    };
}

/// <summary>新建隧道时可选的节点。</summary>
public sealed class FrpNodeOption
{
    public FrpNodeOption(SakuraFrpNode node)
    {
        Id = node.Id;
        Name = node.Name;
        Host = node.Host;
        PublicGameAllowed = node.PublicGameAllowed;
        WebsiteAllowed = node.WebsiteAllowed;
        SupportsUdp = node.SupportsUdp;
        IsBeta = node.IsBeta;
    }

    public int Id { get; }

    public string Name { get; }

    public string Host { get; }

    /// <summary>是否可用于公开游戏服务器（节点名带「多线」或 flag 第 12 位）。</summary>
    public bool PublicGameAllowed { get; }

    /// <summary>是否支持建站（HTTP / HTTPS）。</summary>
    public bool WebsiteAllowed { get; }

    /// <summary>是否支持 UDP；false 即面板标记的「禁 UDP」。</summary>
    public bool SupportsUdp { get; }

    /// <summary>BETA 测试节点。</summary>
    public bool IsBeta { get; }

    public bool Supports(string type) => type switch
    {
        SakuraFrpTunnelTypes.Tcp => true,
        SakuraFrpTunnelTypes.Udp => SupportsUdp,
        // WOL 靠 UDP 发唤醒包，禁 UDP 的节点同样建不了。
        SakuraFrpTunnelTypes.Wol => SupportsUdp,
        SakuraFrpTunnelTypes.Http or SakuraFrpTunnelTypes.Https => WebsiteAllowed,
        _ => false,
    };

    /// <summary>节点标签，例如「可建站 / 公开游戏 / 禁 UDP / BETA」。</summary>
    public string Tags
    {
        get
        {
            var tags = new List<string>();
            if (WebsiteAllowed) tags.Add("可建站");
            if (PublicGameAllowed) tags.Add("公开游戏");
            if (!SupportsUdp) tags.Add("禁UDP");
            if (IsBeta) tags.Add("BETA");
            return string.Join(" / ", tags);
        }
    }

    public string Display
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Host) ? Name : $"{Name} · {Host}";
            return string.IsNullOrWhiteSpace(Tags) ? name : $"{name}（{Tags}）";
        }
    }

    public override string ToString() => Display;
}

/// <summary>新建隧道时可以选择的隧道类型。</summary>
public sealed class TunnelTypeOption
{
    public TunnelTypeOption(string type, string display, bool needsPort, bool needsDomain)
    {
        Type = type;
        Display = display;
        NeedsPort = needsPort;
        NeedsDomain = needsDomain;
    }

    /// <summary>全部可选类型（实际可选项按节点能力过滤）。</summary>
    public static readonly IReadOnlyList<TunnelTypeOption> All = new[]
    {
        new TunnelTypeOption(SakuraFrpTunnelTypes.Tcp, "TCP 隧道", needsPort: true, needsDomain: false),
        new TunnelTypeOption(SakuraFrpTunnelTypes.Udp, "UDP 隧道", needsPort: true, needsDomain: false),
        new TunnelTypeOption(SakuraFrpTunnelTypes.Http, "HTTP 建站隧道", needsPort: true, needsDomain: true),
        new TunnelTypeOption(SakuraFrpTunnelTypes.Https, "HTTPS 建站隧道", needsPort: true, needsDomain: true),
        new TunnelTypeOption(SakuraFrpTunnelTypes.Wol, "WOL 开机隧道", needsPort: false, needsDomain: false),
    };

    /// <summary>API 的 <c>type</c> 取值。</summary>
    public string Type { get; }

    public string Display { get; }

    /// <summary>需要填写本地端口。</summary>
    public bool NeedsPort { get; }

    /// <summary>需要填写绑定域名。</summary>
    public bool NeedsDomain { get; }

    public override string ToString() => Display;
}

/// <summary>本机监听端口下拉里的一项。</summary>
public sealed class LocalPortOption
{
    public LocalPortOption(LocalListeningPort port)
    {
        Port = port.Port;
        ProcessName = port.ProcessName;
    }

    public int Port { get; }

    public string ProcessName { get; }

    public string Display => string.IsNullOrWhiteSpace(ProcessName)
        ? Port.ToString()
        : $"{Port} · {ProcessName}";

    public override string ToString() => Display;
}
