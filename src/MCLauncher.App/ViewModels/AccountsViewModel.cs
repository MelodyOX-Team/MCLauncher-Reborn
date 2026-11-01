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
using MCLauncher.Core.Services.Auth;

namespace MCLauncher.App.ViewModels;

/// <summary>账号列表的一行：头像、游戏 ID、Xbox 用户名、账号类型。</summary>
public sealed class AccountItem : ViewModelBase
{
    private bool _isSelected;
    private ImageSource? _avatar;

    public required Account Account { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(CurrentMark));
            }
        }
    }

    /// <summary>当前账号列的勾选标记。</summary>
    public string CurrentMark => _isSelected ? "√" : string.Empty;

    /// <summary>头像：离线/外置先按 UUID 选默认皮肤，取到真实皮肤后替换。</summary>
    public ImageSource? Avatar
    {
        get => _avatar;
        set => SetProperty(ref _avatar, value);
    }

    /// <summary>Minecraft 游戏内玩家名。</summary>
    public string GameId => Account.Name;

    /// <summary>Xbox 玩家代号；只有微软账号有。</summary>
    public string XboxName => Account.IsMicrosoft
        ? string.IsNullOrWhiteSpace(Account.XboxName) ? "未获取" : Account.XboxName!
        : "无";

    /// <summary>账号已被判定失效：整行置灰，只能删除或重新登录。</summary>
    public bool IsDisabled => Account.IsDisabled;

    /// <summary>离线账号没有可校验的令牌，不显示行内检查按钮。</summary>
    public bool CanCheck => Account.Kind != AccountKind.Offline;

    /// <summary>失效原因，鼠标悬停时提示。</summary>
    public string DisabledReasonText => Account.DisabledReason ?? "账号已失效，请重新登录";

    public string AccountTypeText => Account.Kind switch
    {
        AccountKind.Microsoft => "微软账户",
        AccountKind.External => "外置登录",
        _ => "离线模式",
    };

    /// <summary>只有微软账户能改动 Mojang 档案里的皮肤与披风。</summary>
    public bool IsMicrosoft => Account.IsMicrosoft;
}

/// <summary>换肤弹窗里的一件披风。</summary>
public sealed class CapeItem
{
    public required MinecraftCape Cape { get; init; }

    public string Id => Cape.Id;

    public string Name => string.IsNullOrWhiteSpace(Cape.Alias) ? "披风" : Cape.Alias!;

    public bool IsActive => Cape.IsActive;

    public ImageSource? Thumbnail { get; init; }
}

/// <summary>左栏「添加账户」里的外置登录服务器条目。</summary>
public sealed class AuthServerItem
{
    public required AuthServerEntry Entry { get; init; }

    /// <summary>下拉框末尾的「自定义」占位项，选中后由调用方展开地址输入。</summary>
    public bool IsCustom { get; init; }

    public string Name => Entry.Name;

    /// <summary>去掉协议与路径后的主机名，作为副标题。</summary>
    public string Host
    {
        get
        {
            return Uri.TryCreate(Entry.Url, UriKind.Absolute, out var uri) ? uri.Host : Entry.Url;
        }
    }

    public override string ToString() => Name;
}

/// <summary>账号页：离线 / 微软正版 / 外置登录三种账号的统一管理。</summary>
public sealed class AccountsViewModel : ViewModelBase
{
    private readonly LauncherStack _stack;
    private readonly AsyncRelayCommand _loginCommand;
    private readonly AsyncRelayCommand _addServerCommand;
    private readonly AsyncRelayCommand _resetSkinCommand;
    private readonly AsyncRelayCommand _reloadSkinCommand;
    private readonly AsyncRelayCommand _checkAllCommand;
    private readonly AsyncRelayCommand<AccountItem> _checkAccountCommand;
    private readonly AvatarService _avatars;

    private string _newAccountName = string.Empty;
    private string _statusText = "选择左侧任意方式添加账号。";
    private bool _isBusy;
    private bool _isCheckingAccounts;
    private CancellationTokenSource? _loginCts;
    private string? _deviceCodeText;
    private string? _deviceCodeLink;

    /// <summary>自动复制验证码后停留多久再打开浏览器（让用户看清提示）。</summary>
    private static readonly TimeSpan DeviceCodePageOpenDelay = TimeSpan.FromSeconds(3);

    /// <summary>倒计时期间屏蔽登录阶段文案，避免「等待授权」立刻盖掉自动复制提示。</summary>
    private bool _msProgressMuted;

    private bool _isOfflineDialogOpen;
    private bool _isExternalDialogOpen;
    private bool _isMicrosoftDialogOpen;
    private bool _isServerDialogOpen;

    private AuthServerItem? _selectedAuthServer;
    private string _externalUsername = string.Empty;
    private string _externalError = string.Empty;
    private string _externalHomepage = string.Empty;
    private string _externalRegister = string.Empty;
    private bool _isExternalBusy;

    private string _newServerName = string.Empty;
    private string _newServerUrl = string.Empty;
    private string _serverError = string.Empty;
    private bool _isServerBusy;

    private string _offlineError = string.Empty;

    private AccountItem? _selectedAccountItem;
    private bool _isSkinDialogOpen;
    private bool _isSkinBusy;
    private string _skinStatusText = string.Empty;
    private string _personalizeAccountText = string.Empty;
    private string _selectedSkinModel = SkinModelClassic;
    private ImageSource? _skinPreview;
    private ImageSource? _capePreview;
    private string _capeName = string.Empty;
    private BitmapSource? _skinTexture;
    private Account? _skinAccount;
    private bool _skinModelTouched;

    private const string SkinModelClassic = "经典（Steve）";
    private const string SkinModelSlim = "纤细（Alex）";

    public AccountsViewModel(LauncherStack stack)
    {
        _stack = stack;
        _avatars = new AvatarService(stack.Config.ConfigDirectory, stack.Log);

        AddOfflineCommand = new RelayCommand(AddOffline);
        RemoveCommand = new RelayCommand<AccountItem>(Remove);
        SelectCommand = new RelayCommand<AccountItem>(Select);
        RefreshCommand = new RelayCommand(Refresh);
        CancelLoginCommand = new RelayCommand(() => _loginCts?.Cancel());
        OpenDeviceCodePageCommand = new RelayCommand(OpenDeviceCodePage);
        CopyDeviceCodeCommand = new RelayCommand(CopyDeviceCode);
        OpenOfflineDialogCommand = new RelayCommand(OpenOfflineDialog);
        OpenExternalDialogCommand = new RelayCommand<AuthServerItem>(OpenExternalDialog);
        OpenMicrosoftDialogCommand = new RelayCommand(OpenMicrosoftDialog);
        OpenAddServerDialogCommand = new RelayCommand(OpenAddServerDialog);
        CloseDialogCommand = new RelayCommand(CloseDialogs);
        RemoveServerCommand = new RelayCommand<AuthServerItem>(RemoveServer);
        OpenExternalHomepageCommand = new RelayCommand(() => OpenUrl(_externalHomepage));
        OpenExternalRegisterCommand = new RelayCommand(() => OpenUrl(_externalRegister));
        OpenPersonalizeCommand = new RelayCommand(OpenPersonalize);
        ApplyNoCapeCommand = new RelayCommand(() => _ = ApplyCapeAsync(null));
        ApplyCapeCommand = new AsyncRelayCommand<CapeItem>(ApplyCapeAsync);

        _loginCommand = new AsyncRelayCommand(LoginMicrosoftAsync, () => !IsBusy && HasMsClientId);
        LoginMicrosoftCommand = _loginCommand;

        _addServerCommand = new AsyncRelayCommand(AddServerAsync, () => !IsServerBusy);
        AddServerCommand = _addServerCommand;

        _resetSkinCommand = new AsyncRelayCommand(ResetSkinAsync);
        ResetSkinCommand = _resetSkinCommand;
        _reloadSkinCommand = new AsyncRelayCommand(LoadProfileAsync);
        ReloadSkinCommand = _reloadSkinCommand;

        _checkAllCommand = new AsyncRelayCommand(CheckAllAsync, () => !IsCheckingAccounts);
        CheckAllCommand = _checkAllCommand;

        _checkAccountCommand = new AsyncRelayCommand<AccountItem>(CheckAccountAsync, _ => !IsCheckingAccounts);
        CheckAccountCommand = _checkAccountCommand;

        _stack.Changed += (_, _) => UiThread.Post(Refresh);

        RefreshAuthServers();
        Refresh();
    }

    public ObservableCollection<AccountItem> Accounts { get; } = new();

    public ObservableCollection<AuthServerItem> AuthServers { get; } = new();

    public string NewAccountName
    {
        get => _newAccountName;
        set => SetProperty(ref _newAccountName, value);
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
            if (SetProperty(ref _isBusy, value))
            {
                _loginCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>正在检查账号可用性；期间禁用检查按钮，避免重复请求。</summary>
    public bool IsCheckingAccounts
    {
        get => _isCheckingAccounts;
        private set
        {
            if (SetProperty(ref _isCheckingAccounts, value))
            {
                OnPropertyChanged(nameof(CanCheckAccounts));
                _checkAllCommand.RaiseCanExecuteChanged();
                _checkAccountCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanCheckAccounts => !IsCheckingAccounts;

    // ---------------------------------------------------------------- 对话框状态

    /// <summary>任意添加账号/认证服务器/换肤对话框打开时为 true，用于显示遮罩。</summary>
    public bool IsDialogOpen =>
        _isOfflineDialogOpen || _isExternalDialogOpen || _isMicrosoftDialogOpen || _isServerDialogOpen ||
        _isSkinDialogOpen;

    public bool IsOfflineDialogOpen
    {
        get => _isOfflineDialogOpen;
        private set => SetDialogFlag(ref _isOfflineDialogOpen, value);
    }

    public bool IsExternalDialogOpen
    {
        get => _isExternalDialogOpen;
        private set => SetDialogFlag(ref _isExternalDialogOpen, value);
    }

    public bool IsMicrosoftDialogOpen
    {
        get => _isMicrosoftDialogOpen;
        private set => SetDialogFlag(ref _isMicrosoftDialogOpen, value);
    }

    public bool IsServerDialogOpen
    {
        get => _isServerDialogOpen;
        private set => SetDialogFlag(ref _isServerDialogOpen, value);
    }

    public bool IsSkinDialogOpen
    {
        get => _isSkinDialogOpen;
        private set => SetDialogFlag(ref _isSkinDialogOpen, value);
    }

    public string OfflineError
    {
        get => _offlineError;
        private set => SetProperty(ref _offlineError, value);
    }

    // ---------------------------------------------------------------- 选中账号与换肤

    /// <summary>表格里选中的账号行；「个性化」按钮的可用性跟着它变。</summary>
    public AccountItem? SelectedAccountItem
    {
        get => _selectedAccountItem;
        set
        {
            if (SetProperty(ref _selectedAccountItem, value))
            {
                OnPropertyChanged(nameof(CanPersonalize));
                OnPropertyChanged(nameof(CanSetCurrent));
            }
        }
    }

    /// <summary>
    /// 「个性化」作用于手动选中的账号行；没有手动选行时作用于当前账号。
    /// 只有微软账户且未失效时才可用。
    /// </summary>
    public bool CanPersonalize =>
        PersonalizeTarget is { Account.IsMicrosoft: true, Account.IsDisabled: false };

    /// <summary>个性化目标：优先手动选中的行，其次当前账号。</summary>
    private AccountItem? PersonalizeTarget =>
        SelectedAccountItem
        ?? Accounts.FirstOrDefault(item =>
            string.Equals(item.Account.Uuid, _stack.Accounts.Selected?.Uuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>失效账号不能设为当前，只能删除或重新登录。</summary>
    public bool CanSetCurrent => SelectedAccountItem is { Account.IsDisabled: false };

    public bool IsSkinBusy
    {
        get => _isSkinBusy;
        private set
        {
            if (SetProperty(ref _isSkinBusy, value))
            {
                OnPropertyChanged(nameof(CanInteractSkin));
            }
        }
    }

    public bool CanInteractSkin => !IsSkinBusy;

    public string SkinStatusText
    {
        get => _skinStatusText;
        private set => SetProperty(ref _skinStatusText, value);
    }

    public string PersonalizeAccountText
    {
        get => _personalizeAccountText;
        private set => SetProperty(ref _personalizeAccountText, value);
    }

    /// <summary>可选的皮肤模型（Mojang 只认 CLASSIC / SLIM）。</summary>
    public IReadOnlyList<string> SkinModels { get; } = new[] { SkinModelClassic, SkinModelSlim };

    public string SelectedSkinModel
    {
        get => _selectedSkinModel;
        set
        {
            if (SetProperty(ref _selectedSkinModel, value))
            {
                _skinModelTouched = true;
                UpdateSkinPreview();
            }
        }
    }

    public ImageSource? SkinPreview => _skinPreview;

    public ImageSource? CapePreview => _capePreview;

    /// <summary>没有预览图时显示占位提示。</summary>
    public bool ShowSkinPlaceholder => _skinPreview is null;

    public bool ShowCapePlaceholder => _capePreview is null;

    public bool ShowCapeListPlaceholder => Capes.Count == 0;

    public string CapeSummary => string.IsNullOrWhiteSpace(_capeName)
        ? "当前没有披风"
        : $"当前披风：{_capeName}";

    public ObservableCollection<CapeItem> Capes { get; } = new();

    public AuthServerItem? SelectedAuthServer
    {
        get => _selectedAuthServer;
        private set
        {
            if (SetProperty(ref _selectedAuthServer, value))
            {
                OnPropertyChanged(nameof(SelectedAuthServerName));
            }
        }
    }

    public string SelectedAuthServerName => SelectedAuthServer?.Name ?? string.Empty;

    public string ExternalUsername
    {
        get => _externalUsername;
        set => SetProperty(ref _externalUsername, value);
    }

    public string ExternalError
    {
        get => _externalError;
        private set
        {
            if (SetProperty(ref _externalError, value))
            {
                OnPropertyChanged(nameof(HasExternalError));
            }
        }
    }

    public bool HasExternalError => !string.IsNullOrWhiteSpace(ExternalError);

    public bool HasExternalHomepage => !string.IsNullOrWhiteSpace(_externalHomepage);

    public bool HasExternalRegister => !string.IsNullOrWhiteSpace(_externalRegister);

    public bool IsExternalBusy
    {
        get => _isExternalBusy;
        private set => SetProperty(ref _isExternalBusy, value);
    }

    public string NewServerName
    {
        get => _newServerName;
        set => SetProperty(ref _newServerName, value);
    }

    public string NewServerUrl
    {
        get => _newServerUrl;
        set => SetProperty(ref _newServerUrl, value);
    }

    public string ServerError
    {
        get => _serverError;
        private set => SetProperty(ref _serverError, value);
    }

    public bool IsServerBusy
    {
        get => _isServerBusy;
        private set
        {
            if (SetProperty(ref _isServerBusy, value))
            {
                _addServerCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ---------------------------------------------------------------- 微软登录状态

    /// <summary>设备码登录时展示给用户的短码；未在登录流程中时为空。</summary>
    public string DeviceCodeText
    {
        get => _deviceCodeText ?? string.Empty;
        private set
        {
            _deviceCodeText = string.IsNullOrWhiteSpace(value) ? null : value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDeviceCode));
        }
    }

    /// <summary>浏览器打开的微软验证页地址。</summary>
    public string DeviceCodeLink
    {
        get => _deviceCodeLink ?? string.Empty;
        private set
        {
            _deviceCodeLink = string.IsNullOrWhiteSpace(value) ? null : value;
            OnPropertyChanged();
        }
    }

    public bool HasDeviceCode => _deviceCodeText is not null;

    /// <summary>是否已配置微软客户端 ID（未配置时界面给出引导，而不是报错）。</summary>
    public bool HasMsClientId => _stack.Config.ResolveMsClientId() is not null;

    public string MsClientIdHint => HasMsClientId
        ? "已启用微软正版登录。"
        : "当前版本未配置微软登录客户端 ID，无法使用微软正版登录；请使用离线或外置登录账户。";

    public string MsSetupHint => _stack.MicrosoftAuth.DescribeSetup();

    public string SelectedAccountText => _stack.Accounts.Selected is { } account
        ? $"当前账号：{account}"
        : "当前没有可用账号（请先添加一个账号）";

    // ---------------------------------------------------------------- 命令

    public ICommand AddOfflineCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand SelectCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand LoginMicrosoftCommand { get; }

    public ICommand OpenDeviceCodePageCommand { get; }

    /// <summary>复制短码（浏览器没自动打开时，用户可以手贴或用它手动输入）。</summary>
    public ICommand CopyDeviceCodeCommand { get; }

    public ICommand CancelLoginCommand { get; }

    public ICommand OpenOfflineDialogCommand { get; }

    public ICommand OpenExternalDialogCommand { get; }

    public ICommand OpenMicrosoftDialogCommand { get; }

    public ICommand OpenAddServerDialogCommand { get; }

    public ICommand CloseDialogCommand { get; }

    public ICommand RemoveServerCommand { get; }

    public ICommand OpenExternalHomepageCommand { get; }

    public ICommand OpenExternalRegisterCommand { get; }

    public ICommand AddServerCommand { get; }

    public ICommand OpenPersonalizeCommand { get; }

    public ICommand ResetSkinCommand { get; }

    public ICommand ReloadSkinCommand { get; }

    public ICommand ApplyCapeCommand { get; }

    public ICommand ApplyNoCapeCommand { get; }

    /// <summary>检查所有外置/微软账号是否可用（离线账号不检查）。</summary>
    public ICommand CheckAllCommand { get; }

    /// <summary>检查单行账号是否可用。</summary>
    public ICommand CheckAccountCommand { get; }

    // ---------------------------------------------------------------- 列表刷新

    public void Refresh()
    {
        var selectedUuid = _stack.Accounts.Selected?.Uuid;
        var selectedRowUuid = SelectedAccountItem?.Account.Uuid;

        Accounts.Clear();
        foreach (var account in _stack.Accounts.Accounts)
        {
            var item = new AccountItem
            {
                Account = account,
                IsSelected = account.Uuid == selectedUuid,
                Avatar = AvatarService.DefaultHeadFor(account.Uuid),
            };

            Accounts.Add(item);

            if (account.IsMicrosoft || account.IsExternal)
            {
                _ = LoadAvatarAsync(item, account);
            }
        }

        // 重建列表会把 ListView 的选中项清空：优先恢复手动选中的行；
        // 没有选过行时（例如首次进入账号页）默认选中当前账号，
        // 这样「个性化」不必先点一下当前账号。
        var rowUuid = selectedRowUuid ?? selectedUuid;
        SelectedAccountItem =
            (rowUuid is null
                ? null
                : Accounts.FirstOrDefault(item =>
                    string.Equals(item.Account.Uuid, rowUuid, StringComparison.OrdinalIgnoreCase)))
            ?? Accounts.FirstOrDefault(item =>
                string.Equals(item.Account.Uuid, selectedUuid, StringComparison.OrdinalIgnoreCase))
            ?? Accounts.FirstOrDefault();

        OnPropertyChanged(nameof(HasMsClientId));
        OnPropertyChanged(nameof(MsClientIdHint));
        OnPropertyChanged(nameof(MsSetupHint));
        OnPropertyChanged(nameof(SelectedAccountText));
        _loginCommand.RaiseCanExecuteChanged();
    }

    private void RefreshAuthServers()
    {
        AuthServers.Clear();
        foreach (var entry in _stack.Config.Settings.AuthServers)
        {
            AuthServers.Add(new AuthServerItem { Entry = entry });
        }
    }

    // ---------------------------------------------------------------- 账号可用性检查

    /// <summary>检查所有外置登录与微软账号；离线账号不检查。失效账号会被置灰。</summary>
    private async Task CheckAllAsync()
    {
        if (IsCheckingAccounts)
        {
            return;
        }

        IsCheckingAccounts = true;
        StatusText = "正在检查账号可用性…";

        var usable = 0;
        var unavailable = 0;
        var unknown = 0;

        try
        {
            var targets = _stack.Accounts.Accounts
                .Where(a => a.Kind != AccountKind.Offline)
                .ToList();

            if (targets.Count == 0)
            {
                StatusText = "没有需要检查的账号（离线账号不需要检查）";
                return;
            }

            foreach (var account in targets)
            {
                var result = await _stack.AccountHealth.CheckAsync(account);
                switch (result.Health)
                {
                    case AccountHealth.Usable:
                        usable++;
                        break;
                    case AccountHealth.Unavailable:
                        unavailable++;
                        break;
                    default:
                        unknown++;
                        break;
                }
            }

            StatusText = unknown > 0
                ? $"检查完成：{usable} 个可用，{unavailable} 个已失效，{unknown} 个暂时无法检查"
                : $"检查完成：{usable} 个可用，{unavailable} 个已失效";
        }
        catch (Exception ex)
        {
            _stack.Log.Error("检查账号可用性失败", ex);
            StatusText = "检查失败：" + ex.Message;
        }
        finally
        {
            IsCheckingAccounts = false;
            Refresh();
            _stack.RaiseChanged();
        }
    }

    /// <summary>检查单个账号；微软账号顺带补上缺失的 Xbox 用户名。</summary>
    private async Task CheckAccountAsync(AccountItem? item)
    {
        if (item is null || IsCheckingAccounts)
        {
            return;
        }

        IsCheckingAccounts = true;
        StatusText = $"正在检查 {item.Account.Name} …";

        try
        {
            var result = await _stack.AccountHealth.CheckAsync(item.Account);
            StatusText = result.Health switch
            {
                AccountHealth.Usable => $"{item.Account.Name}：可用",
                AccountHealth.Unavailable => $"{item.Account.Name} 已失效，请删除后重新登录",
                _ => $"{item.Account.Name}：{result.Message}",
            };
        }
        catch (Exception ex)
        {
            _stack.Log.Error($"检查账号失败（{item.Account.Name}）", ex);
            StatusText = $"检查失败：{ex.Message}";
        }
        finally
        {
            IsCheckingAccounts = false;
            Refresh();
            _stack.RaiseChanged();
        }
    }

    /// <summary>微软/外置账号：后台取真实皮肤头像，失败就保留 UUID 对应的默认皮肤。</summary>
    private async Task LoadAvatarAsync(AccountItem item, Account account)
    {
        try
        {
            var image = account.IsExternal && !string.IsNullOrWhiteSpace(account.AuthServer)
                ? await _avatars.LoadExternalHeadAsync(account.AuthServer!, account.Uuid, CancellationToken.None)
                    .ConfigureAwait(false)
                : await _avatars.LoadMicrosoftHeadAsync(account.Uuid, CancellationToken.None)
                    .ConfigureAwait(false);

            if (image is not null)
            {
                UiThread.Post(() => item.Avatar = image);
            }
        }
        catch (Exception ex)
        {
            _stack.Log.Warn($"加载账号头像失败（{account.Name}）", ex);
        }
    }

    /// <summary>双击账号行时设为当前账号（与「设为当前」按钮同一逻辑）。</summary>
    public void SetCurrent(AccountItem? item) => Select(item);

    // ---------------------------------------------------------------- 添加/删除账号

    private void OpenOfflineDialog()
    {
        OfflineError = string.Empty;
        NewAccountName = string.Empty;
        IsOfflineDialogOpen = true;
    }

    private void OpenMicrosoftDialog()
    {
        StatusText = HasMsClientId ? "正在申请登录码…" : MsClientIdHint;
        IsMicrosoftDialogOpen = true;

        if (HasMsClientId && _loginCommand.CanExecute(null))
        {
            _loginCommand.Execute(null);
        }
    }

    private void OpenExternalDialog(AuthServerItem? server)
    {
        if (server is null)
        {
            return;
        }

        SelectedAuthServer = server;
        ExternalUsername = string.Empty;
        ExternalError = string.Empty;
        _externalHomepage = string.Empty;
        _externalRegister = string.Empty;
        OnPropertyChanged(nameof(HasExternalHomepage));
        OnPropertyChanged(nameof(HasExternalRegister));
        IsExternalDialogOpen = true;

        _ = LoadServerLinksAsync(server.Entry.Url);
    }

    private async Task LoadServerLinksAsync(string url)
    {
        try
        {
            var metadata = await _stack.AuthlibAuth.FetchMetadataAsync(url).ConfigureAwait(false);
            UiThread.Post(() =>
            {
                _externalHomepage = metadata.Homepage ?? string.Empty;
                _externalRegister = metadata.Register ?? string.Empty;
                OnPropertyChanged(nameof(HasExternalHomepage));
                OnPropertyChanged(nameof(HasExternalRegister));
            });
        }
        catch (Exception ex)
        {
            // 主页/注册入口只是便利链接，取不到不影响登录。
            _stack.Log.Warn("获取认证服务器元数据失败", ex);
        }
    }

    private void OpenAddServerDialog()
    {
        NewServerName = string.Empty;
        NewServerUrl = string.Empty;
        ServerError = string.Empty;
        IsServerDialogOpen = true;
    }

    private void CloseDialogs()
    {
        if (IsMicrosoftDialogOpen)
        {
            _loginCts?.Cancel();
        }

        IsOfflineDialogOpen = false;
        IsExternalDialogOpen = false;
        IsMicrosoftDialogOpen = false;
        IsServerDialogOpen = false;
        IsSkinDialogOpen = false;
        DeviceCodeText = string.Empty;
        DeviceCodeLink = string.Empty;
    }

    private void AddOffline()
    {
        OfflineError = string.Empty;

        try
        {
            var account = _stack.Accounts.AddOffline(NewAccountName);
            NewAccountName = string.Empty;
            _stack.Config.Save();
            StatusText = $"已添加离线账号：{account.Name}";
            CloseDialogs();
            Refresh();
            _stack.RaiseChanged();
        }
        catch (Exception ex)
        {
            OfflineError = ex.Message;
        }
    }

    private void Remove(AccountItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (_stack.Accounts.Remove(item.Account))
        {
            _stack.Config.Save();
            StatusText = $"已删除账号：{item.Account.Name}";
            Refresh();
            _stack.RaiseChanged();
        }
    }

    private void Select(AccountItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.Account.IsDisabled)
        {
            StatusText = $"账号「{item.Account.Name}」已失效，只能删除或重新登录";
            return;
        }

        _stack.Accounts.Select(item.Account);
        _stack.Config.Save();
        StatusText = $"已切换账号：{item.Account}";
        Refresh();
        _stack.RaiseChanged();
    }

    // ---------------------------------------------------------------- 外置登录

    /// <summary>外置登录（密码由 PasswordBox 在代码后置里传入，不放进绑定）。</summary>
    public async Task LoginExternalAsync(string password)
    {
        if (SelectedAuthServer is null)
        {
            ExternalError = "请选择认证服务器";
            return;
        }

        if (string.IsNullOrWhiteSpace(ExternalUsername))
        {
            ExternalError = "请输入用户名";
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ExternalError = "请输入密码";
            return;
        }

        IsExternalBusy = true;
        ExternalError = string.Empty;

        try
        {
            var account = await _stack.AuthlibAuth
                .LoginAsync(SelectedAuthServer.Entry, ExternalUsername.Trim(), password)
                .ConfigureAwait(true);

            _stack.Accounts.Upsert(account);
            _stack.Config.Save();
            StatusText = $"已登录 {account.Name}（{account.AuthServerName}）";
            CloseDialogs();
            Refresh();
            _stack.RaiseChanged();
        }
        catch (AuthlibAuthException ex)
        {
            ExternalError = ex.Message;
        }
        catch (Exception ex)
        {
            _stack.Log.Error("外置登录异常", ex);
            ExternalError = "登录失败：" + ex.Message;
        }
        finally
        {
            IsExternalBusy = false;
        }
    }

    // ---------------------------------------------------------------- 个性化：皮肤与披风

    private void OpenPersonalize()
    {
        var item = PersonalizeTarget;
        if (item is null || item.Account.IsMicrosoft != true || item.Account.IsDisabled)
        {
            return;
        }

        _skinAccount = item.Account;
        PersonalizeAccountText = $"{item.Account.Name} · 微软账户";
        SkinStatusText = "正在读取皮肤与披风…";
        _skinTexture = null;
        _skinModelTouched = false;
        _capeName = string.Empty;
        SetSkinPreview(null);
        SetCapePreview(null);
        Capes.Clear();
        OnPropertyChanged(nameof(ShowCapeListPlaceholder));
        IsSkinDialogOpen = true;

        _ = LoadProfileAsync();
    }

    /// <summary>重新读取档案；失败时把原因留在弹窗底部。</summary>
    private async Task LoadProfileAsync()
    {
        if (_skinAccount is null || IsSkinBusy)
        {
            return;
        }

        IsSkinBusy = true;
        SkinStatusText = "正在读取皮肤与披风…";

        try
        {
            await LoadProfileCoreAsync();
        }
        catch (MinecraftProfileException ex)
        {
            SkinStatusText = ex.Message;
        }
        catch (Exception ex)
        {
            _stack.Log.Error("读取皮肤与披风失败", ex);
            SkinStatusText = "读取失败：" + ex.Message;
        }
        finally
        {
            IsSkinBusy = false;
        }
    }

    private async Task LoadProfileCoreAsync()
    {
        var account = _skinAccount;
        if (account is null)
        {
            return;
        }

        var profile = await _stack.Profiles.GetProfileAsync(account);

        var skinTexture = await TryDownloadTextureAsync(profile.SkinUrl);
        var capeTexture = await TryDownloadTextureAsync(profile.ActiveCape?.Url);

        var capeItems = new List<CapeItem>();
        foreach (var cape in profile.Capes)
        {
            var texture = await TryDownloadTextureAsync(cape.Url);
            capeItems.Add(new CapeItem
            {
                Cape = cape,
                Thumbnail = texture is null ? null : SkinPreviewRenderer.DrawCape(texture, 34, 54),
            });
        }

        if (!IsSkinDialogOpen)
        {
            return;
        }

        _skinTexture = skinTexture;
        _selectedSkinModel = profile.SkinVariant == SkinVariant.Slim ? SkinModelSlim : SkinModelClassic;
        _skinModelTouched = false;
        OnPropertyChanged(nameof(SelectedSkinModel));
        UpdateSkinPreview();

        SetCapePreview(
            capeTexture is null ? null : SkinPreviewRenderer.DrawCape(capeTexture, 90, 144));
        _capeName = profile.ActiveCape is null
            ? string.Empty
            : profile.ActiveCape.Alias ?? "默认披风";
        OnPropertyChanged(nameof(CapeSummary));

        Capes.Clear();
        foreach (var item in capeItems)
        {
            Capes.Add(item);
        }

        OnPropertyChanged(nameof(ShowCapeListPlaceholder));
        SkinStatusText = skinTexture is null
            ? "该账号还没有上传皮肤（游戏内使用默认皮肤）。"
            : "已加载当前皮肤与披风。";
    }

    private async Task<BitmapSource?> TryDownloadTextureAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            var bytes = await _stack.Profiles.DownloadTextureAsync(url);
            return SkinPreviewRenderer.LoadTexture(bytes);
        }
        catch (Exception ex)
        {
            _stack.Log.Warn($"下载纹理失败：{url}", ex);
            return null;
        }
    }

    /// <summary>选好本地 PNG 后由代码后置调用（OpenFileDialog 不在 MVVM 里）。</summary>
    public async Task ApplySkinFileAsync(string path)
    {
        if (_skinAccount is null || IsSkinBusy)
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex)
        {
            SkinStatusText = "读取皮肤文件失败：" + ex.Message;
            return;
        }

        var texture = SkinPreviewRenderer.LoadTexture(bytes);
        if (texture is null || !SkinPreviewRenderer.IsValidSkinSize(texture))
        {
            SkinStatusText = "皮肤文件无效：请选择 64×64（或 64×32）的 PNG。";
            return;
        }

        // 用户手动改过模型就按用户选择上传，否则按纹理自动识别。
        if (!_skinModelTouched)
        {
            SelectedSkinModel = texture.PixelHeight >= 64 && SkinPreviewRenderer.DetectSlim(texture)
                ? SkinModelSlim
                : SkinModelClassic;
            _skinModelTouched = false;
        }

        var slim = string.Equals(SelectedSkinModel, SkinModelSlim, StringComparison.Ordinal);

        IsSkinBusy = true;
        SkinStatusText = "正在上传皮肤…";

        try
        {
            await _stack.Profiles.UploadSkinAsync(
                _skinAccount, bytes, slim ? SkinVariant.Slim : SkinVariant.Classic);
            await LoadProfileCoreAsync();
            SkinStatusText = "皮肤已更新，进入游戏后生效。";
        }
        catch (MinecraftProfileException ex)
        {
            SkinStatusText = ex.Message;
        }
        catch (Exception ex)
        {
            _stack.Log.Error("上传皮肤失败", ex);
            SkinStatusText = "上传失败：" + ex.Message;
        }
        finally
        {
            IsSkinBusy = false;
        }
    }

    private async Task ResetSkinAsync()
    {
        if (_skinAccount is null || IsSkinBusy)
        {
            return;
        }

        IsSkinBusy = true;
        SkinStatusText = "正在恢复默认皮肤…";

        try
        {
            await _stack.Profiles.ResetSkinAsync(_skinAccount);
            await LoadProfileCoreAsync();
            SkinStatusText = "已恢复默认皮肤。";
        }
        catch (MinecraftProfileException ex)
        {
            SkinStatusText = ex.Message;
        }
        catch (Exception ex)
        {
            _stack.Log.Error("恢复默认皮肤失败", ex);
            SkinStatusText = "恢复失败：" + ex.Message;
        }
        finally
        {
            IsSkinBusy = false;
        }
    }

    /// <summary>应用披风；参数为空表示隐藏当前披风。</summary>
    private async Task ApplyCapeAsync(CapeItem? item)
    {
        if (_skinAccount is null || IsSkinBusy)
        {
            return;
        }

        IsSkinBusy = true;
        SkinStatusText = item is null ? "正在隐藏披风…" : $"正在更换披风：{item.Name}…";

        try
        {
            await _stack.Profiles.SetCapeAsync(_skinAccount, item?.Id);
            await LoadProfileCoreAsync();
            SkinStatusText = item is null ? "披风已隐藏。" : $"已更换披风：{item.Name}。";
        }
        catch (MinecraftProfileException ex)
        {
            SkinStatusText = ex.Message;
        }
        catch (Exception ex)
        {
            _stack.Log.Error("更换披风失败", ex);
            SkinStatusText = "更换披风失败：" + ex.Message;
        }
        finally
        {
            IsSkinBusy = false;
        }
    }

    private void UpdateSkinPreview()
    {
        if (_skinTexture is null)
        {
            SetSkinPreview(null);
            return;
        }

        SetSkinPreview(SkinPreviewRenderer.DrawCharacter(
            _skinTexture, string.Equals(SelectedSkinModel, SkinModelSlim, StringComparison.Ordinal)));
    }

    private void SetSkinPreview(ImageSource? image)
    {
        _skinPreview = image;
        OnPropertyChanged(nameof(SkinPreview));
        OnPropertyChanged(nameof(ShowSkinPlaceholder));
    }

    private void SetCapePreview(ImageSource? image)
    {
        _capePreview = image;
        OnPropertyChanged(nameof(CapePreview));
        OnPropertyChanged(nameof(ShowCapePlaceholder));
    }

    // ---------------------------------------------------------------- 认证服务器管理

    /// <summary>选中认证服务器（初始化设置登记自定义地址后直接用它登录）。</summary>
    public void SelectAuthServer(AuthServerItem server) => SelectedAuthServer = server;

    /// <summary>
    /// 校验并登记一个认证服务器；相同地址已存在时更新名称。
    /// 账号页与初始化设置共用，登记后账号页也能继续使用。
    /// </summary>
    public async Task<AuthServerItem> RegisterServerAsync(string name, string url)
    {
        var trimmed = url.Trim();
        if (!AuthlibAuthService.IsSecureServerUrl(trimmed, out var uri) || uri is null)
        {
            throw new AuthlibAuthException("请输入 https 开头的 API 根地址（本机调试可用 http://localhost）");
        }

        var metadata = await _stack.AuthlibAuth.FetchMetadataAsync(trimmed);
        var normalized = AuthlibAuthService.NormalizeUrl(trimmed);
        var resolvedName = string.IsNullOrWhiteSpace(name)
            ? metadata.Name ?? uri.Host
            : name.Trim();

        var entry = _stack.Config.Settings.AuthServers.FirstOrDefault(
            server => string.Equals(server.Url, normalized, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            entry = new AuthServerEntry { Name = resolvedName, Url = normalized };
            _stack.Config.Settings.AuthServers.Add(entry);
        }
        else
        {
            entry.Name = resolvedName;
        }

        _stack.Config.Save();
        RefreshAuthServers();

        return AuthServers.First(item => string.Equals(
            item.Entry.Url, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private async Task AddServerAsync()
    {
        ServerError = string.Empty;
        var url = NewServerUrl.Trim();

        if (!AuthlibAuthService.IsSecureServerUrl(url, out var uri) || uri is null)
        {
            ServerError = "请输入 https 开头的 API 根地址（本机调试可用 http://localhost）";
            return;
        }

        IsServerBusy = true;

        try
        {
            var metadata = await _stack.AuthlibAuth.FetchMetadataAsync(url).ConfigureAwait(true);
            var name = string.IsNullOrWhiteSpace(NewServerName)
                ? metadata.Name ?? uri.Host
                : NewServerName.Trim();

            var normalized = AuthlibAuthService.NormalizeUrl(url);
            var existing = _stack.Config.Settings.AuthServers.FirstOrDefault(
                server => string.Equals(server.Url, normalized, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Name = name;
            }
            else
            {
                _stack.Config.Settings.AuthServers.Add(new AuthServerEntry { Name = name, Url = normalized });
            }

            _stack.Config.Save();
            RefreshAuthServers();
            StatusText = $"已添加认证服务器：{name}";
            CloseDialogs();
        }
        catch (AuthlibAuthException ex)
        {
            ServerError = ex.Message;
        }
        catch (Exception ex)
        {
            _stack.Log.Error("添加认证服务器异常", ex);
            ServerError = "添加失败：" + ex.Message;
        }
        finally
        {
            IsServerBusy = false;
        }
    }

    private void RemoveServer(AuthServerItem? server)
    {
        if (server is null)
        {
            return;
        }

        _stack.Config.Settings.AuthServers.RemoveAll(
            entry => string.Equals(entry.Url, server.Entry.Url, StringComparison.OrdinalIgnoreCase));
        _stack.Config.Save();
        RefreshAuthServers();
        StatusText = $"已移除认证服务器：{server.Name}";
    }

    // ---------------------------------------------------------------- 微软登录

    /// <summary>
    /// 微软正版登录：申请设备码后打开微软验证页（<c>www.microsoft.com/link</c>），
    /// 验证码可一键复制、粘贴到页面里确认授权。
    /// </summary>
    private async Task LoginMicrosoftAsync()
    {
        IsBusy = true;
        StatusText = "正在申请登录码…";
        _loginCts = new CancellationTokenSource();

        try
        {
            var account = await _stack.MicrosoftAuth.LoginWithDeviceCodeAsync(
                new Progress<MsDeviceCodePrompt>(p =>
                {
                    DeviceCodeText = p.UserCode;
                    DeviceCodeLink = p.LinkUri;
                    CopyAndOpenDeviceCodePageAsync(p.UserCode);
                }),
                new Progress<MsAuthProgress>(p =>
                {
                    if (!_msProgressMuted)
                    {
                        StatusText = p.Stage;
                    }
                }),
                _loginCts.Token);

            ApplySignedIn(account);
        }
        catch (MsAuthException ex)
        {
            _stack.Log.Warn($"微软登录失败（{ex.Failure}）", ex);
            StatusText = ex.ToUserMessage();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消登录。";
        }
        catch (Exception ex)
        {
            _stack.Log.Error("微软登录异常", ex);
            StatusText = "登录失败：" + ex.Message;
        }
        finally
        {
            _msProgressMuted = false;
            DeviceCodeText = string.Empty;
            DeviceCodeLink = string.Empty;
            _loginCts?.Dispose();
            _loginCts = null;
            IsBusy = false;
        }
    }

    private void ApplySignedIn(Account account)
    {
        _stack.Accounts.Upsert(account);
        _stack.Config.Save();
        StatusText = $"已登录微软账号：{account.Name}";
        CloseDialogs();
        Refresh();
        _stack.RaiseChanged();
    }

    private void CopyDeviceCode()
    {
        if (string.IsNullOrWhiteSpace(_deviceCodeText))
        {
            return;
        }

        StatusText = TryCopyDeviceCode(_deviceCodeText!)
            ? "验证码已复制到剪贴板。"
            : "复制失败，请手动选中验证码复制。";
    }

    /// <summary>
    /// 拿到设备码后先把验证码自动复制并提示，停留 3 秒让用户看到，
    /// 再打开微软验证页，避免浏览器抢走焦点后用户不知道验证码已复制好。
    /// </summary>
    private async void CopyAndOpenDeviceCodePageAsync(string code)
    {
        _msProgressMuted = true;
        StatusText = TryCopyDeviceCode(code)
            ? "验证码已自动复制，3 秒后打开浏览器登录页…"
            : "自动复制失败，3 秒后打开浏览器登录页，请手动记下验证码…";

        try
        {
            var token = _loginCts?.Token ?? CancellationToken.None;
            try
            {
                await Task.Delay(DeviceCodePageOpenDelay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 登录已取消或已结束（验证码被清空）时不再打开浏览器。
            if (token.IsCancellationRequested || !HasDeviceCode || DeviceCodeText != code)
            {
                return;
            }

            StatusText = "浏览器已打开登录页，请粘贴验证码并确认授权";
            OpenDeviceCodePage();
        }
        finally
        {
            _msProgressMuted = false;
        }
    }

    private bool TryCopyDeviceCode(string code)
    {
        try
        {
            Clipboard.SetText(code);
            return true;
        }
        catch (Exception ex)
        {
            _stack.Log.Warn("复制验证码失败", ex);
            return false;
        }
    }

    private void OpenDeviceCodePage()
    {
        var uri = _deviceCodeLink;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        OpenUrl(uri);
    }

    private void OpenUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _stack.Log.Warn($"打开链接失败：{uri}", ex);
        }
    }

    private void SetDialogFlag(ref bool field, bool value)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(nameof(IsDialogOpen));
        OnPropertyChanged(nameof(IsOfflineDialogOpen));
        OnPropertyChanged(nameof(IsExternalDialogOpen));
        OnPropertyChanged(nameof(IsMicrosoftDialogOpen));
        OnPropertyChanged(nameof(IsServerDialogOpen));
        OnPropertyChanged(nameof(IsSkinDialogOpen));
    }
}
