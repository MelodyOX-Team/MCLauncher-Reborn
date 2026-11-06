using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using MCLauncher.App.ViewModels.Mvvm;
using MCLauncher.Core.Models;
using MCLauncher.Core.Services;
using MCLauncher.Core.Services.Auth;

namespace MCLauncher.App.ViewModels;

/// <summary>初始化设置里「添加账户」当前选中的方式。</summary>
public enum SetupAccountKind
{
    Offline,
    Microsoft,
    External,
}

/// <summary>
/// 首次启动的初始化设置：添加账户、最大内存、Java 路径。
/// 三项齐备并点「完成设置」后才进入主界面；进入时自动探测 Java，内存默认取本机内存的 1/4（封顶 16 GB）。
/// </summary>
public sealed class SetupViewModel : ViewModelBase
{
    private const int MemoryFloorMb = 512;
    private const int MemoryCapMb = 16384;

    private readonly LauncherStack _stack;
    private readonly RelayCommand _confirmCommand;

    private bool _isOpen;
    private string _maxMemoryText;
    private string _javaPath = string.Empty;
    private string _javaStatusText = "正在探测本机 Java…";
    private string _errorText = string.Empty;
    private string _accountName = "尚未添加账户";
    private string _accountTypeText = string.Empty;
    private bool _hasAccount;
    private long _totalMemoryMb;

    private AccountsViewModel? _accounts;
    private bool _isAddAccountOpen;
    private SetupAccountKind _accountKind = SetupAccountKind.Offline;
    private AuthServerItem? _selectedExternalServer;
    private bool _isCustomServer;
    private string _customServerName = string.Empty;
    private string _customServerUrl = string.Empty;
    private string _customServerError = string.Empty;
    private bool _isCustomServerBusy;
    private int _accountCountAtDialogOpen;
    private bool _closeAfterAccountAdded;

    public SetupViewModel(LauncherStack stack, bool needsSetup)
    {
        _stack = stack;
        _totalMemoryMb = GetTotalPhysicalMemoryMb();
        _maxMemoryText = RecommendedMaxMemoryMb(_totalMemoryMb).ToString();
        _javaPath = stack.Config.Settings.JavaPath ?? string.Empty;

        AddAccountCommand = new RelayCommand(OpenAddAccount);
        SelectOfflineCommand = new RelayCommand(() => SelectKind(SetupAccountKind.Offline));
        SelectMicrosoftCommand = new RelayCommand(() => SelectKind(SetupAccountKind.Microsoft));
        SelectExternalCommand = new RelayCommand(() => SelectKind(SetupAccountKind.External));
        CancelAddAccountCommand = new RelayCommand(CancelAddAccount);
        BrowseJavaCommand = new RelayCommand(BrowseJava);
        ExitCommand = new RelayCommand(() => System.Windows.Application.Current.Shutdown());
        _confirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        ConfirmCommand = _confirmCommand;

        _stack.Changed += (_, _) => UiThread.Post(OnAccountsChanged);

        if (needsSetup)
        {
            IsOpen = true;
            RefreshAccount();
            _ = DetectJavaAsync();
        }
    }

    // ---------------------------------------------------------------- 状态

    /// <summary>初始化设置是否仍覆盖在主界面上。</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool HasAccount
    {
        get => _hasAccount;
        private set
        {
            if (SetProperty(ref _hasAccount, value))
            {
                OnPropertyChanged(nameof(AccountDisplay));
            }
        }
    }

    public string AccountName
    {
        get => _accountName;
        private set
        {
            if (SetProperty(ref _accountName, value))
            {
                OnPropertyChanged(nameof(AccountDisplay));
            }
        }
    }

    public string AccountTypeText
    {
        get => _accountTypeText;
        private set
        {
            if (SetProperty(ref _accountTypeText, value))
            {
                OnPropertyChanged(nameof(AccountDisplay));
            }
        }
    }

    /// <summary>设置页显示的「游戏名字」，即当前账号 ID 与类型。</summary>
    public string AccountDisplay => string.IsNullOrWhiteSpace(AccountTypeText)
        ? AccountName
        : $"{AccountName}（{AccountTypeText}）";

    public string MaxMemoryText
    {
        get => _maxMemoryText;
        set
        {
            if (SetProperty(ref _maxMemoryText, value))
            {
                ErrorText = string.Empty;
                NotifyCanConfirm();
            }
        }
    }

    public string MemoryHint => _totalMemoryMb <= 0
        ? "可手动填写最大内存（MB）。"
        : $"本机内存约 {_totalMemoryMb / 1024.0:0.#} GB，已按 1/4 推荐（封顶 16 GB）。";

    public string JavaPath
    {
        get => _javaPath;
        set
        {
            if (SetProperty(ref _javaPath, value))
            {
                ErrorText = string.Empty;
                NotifyCanConfirm();
            }
        }
    }

    public string JavaStatusText
    {
        get => _javaStatusText;
        private set => SetProperty(ref _javaStatusText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>三项都齐了才允许完成设置：账号、合法内存、存在的 Java 路径。</summary>
    public bool CanConfirm => HasAccount && TryParseMemory(out _) && File.Exists(JavaPath.Trim());

    // ---------------------------------------------------------------- 添加账户弹窗

    /// <summary>账号相关逻辑复用账号页的 ViewModel（离线 / 微软 / 外置登录）。</summary>
    public AccountsViewModel? Accounts
    {
        get => _accounts;
        private set
        {
            if (SetProperty(ref _accounts, value))
            {
                OnPropertyChanged(nameof(SelectedExternalServer));
            }
        }
    }

    public bool IsAddAccountOpen
    {
        get => _isAddAccountOpen;
        private set => SetProperty(ref _isAddAccountOpen, value);
    }

    public bool IsOfflineMode => _accountKind == SetupAccountKind.Offline;

    public bool IsMicrosoftMode => _accountKind == SetupAccountKind.Microsoft;

    public bool IsExternalMode => _accountKind == SetupAccountKind.External;

    /// <summary>外置登录下拉框：现有认证服务器 + 末尾的「自定义」选项。</summary>
    public ObservableCollection<AuthServerItem> ExternalServerChoices { get; } = new();

    public AuthServerItem? SelectedExternalServer
    {
        get => _selectedExternalServer;
        set
        {
            if (!SetProperty(ref _selectedExternalServer, value))
            {
                return;
            }

            IsCustomServer = value?.IsCustom == true;
            CustomServerError = string.Empty;

            if (!IsCustomServer && value is not null && _accounts is not null)
            {
                _accounts.OpenExternalDialogCommand.Execute(value);
            }
        }
    }

    /// <summary>选中「自定义」时展开名称与 API 地址输入框。</summary>
    public bool IsCustomServer
    {
        get => _isCustomServer;
        private set => SetProperty(ref _isCustomServer, value);
    }

    public string CustomServerName
    {
        get => _customServerName;
        set => SetProperty(ref _customServerName, value);
    }

    public string CustomServerUrl
    {
        get => _customServerUrl;
        set
        {
            if (SetProperty(ref _customServerUrl, value))
            {
                CustomServerError = string.Empty;
            }
        }
    }

    public string CustomServerError
    {
        get => _customServerError;
        private set
        {
            if (SetProperty(ref _customServerError, value))
            {
                OnPropertyChanged(nameof(HasCustomServerError));
            }
        }
    }

    public bool HasCustomServerError => !string.IsNullOrWhiteSpace(CustomServerError);

    public bool IsCustomServerBusy
    {
        get => _isCustomServerBusy;
        private set => SetProperty(ref _isCustomServerBusy, value);
    }

    // ---------------------------------------------------------------- 命令

    public ICommand AddAccountCommand { get; }

    public ICommand SelectOfflineCommand { get; }

    public ICommand SelectMicrosoftCommand { get; }

    public ICommand SelectExternalCommand { get; }

    public ICommand CancelAddAccountCommand { get; }

    public ICommand BrowseJavaCommand { get; }

    public ICommand ConfirmCommand { get; }

    public ICommand ExitCommand { get; }

    /// <summary>外置登录：密码由界面的 PasswordBox 传入，不放进绑定。</summary>
    public async Task LoginExternalAsync(string password)
    {
        if (_accounts is null)
        {
            return;
        }

        if (!IsCustomServer)
        {
            await _accounts.LoginExternalAsync(password);
            return;
        }

        // 自定义地址：先校验 API 根地址并登记到设置里，再用它登录。
        CustomServerError = string.Empty;
        var url = CustomServerUrl.Trim();
        if (!AuthlibAuthService.IsSecureServerUrl(url, out _))
        {
            CustomServerError = "请输入 https 开头的 API 根地址（本机调试可用 http://localhost）";
            return;
        }

        IsCustomServerBusy = true;

        try
        {
            var server = await _accounts.RegisterServerAsync(CustomServerName, url);
            _accounts.SelectAuthServer(server);
            await _accounts.LoginExternalAsync(password);
        }
        catch (AuthlibAuthException ex)
        {
            CustomServerError = ex.Message;
        }
        catch (Exception ex)
        {
            _stack.Log.Error("自定义认证服务器登录失败", ex);
            CustomServerError = "登录失败：" + ex.Message;
        }
        finally
        {
            IsCustomServerBusy = false;
        }
    }

    // ---------------------------------------------------------------- 账户

    /// <summary>
    /// 启动游戏时没有任何可用账号：弹出初始化界面里的「添加账户」窗口，
    /// 账号加好后自动收起整个初始化界面（内存与 Java 已沿用现有设置）。
    /// </summary>
    public void OpenAddAccountForLaunch()
    {
        _closeAfterAccountAdded = true;
        IsOpen = true;
        RefreshAccount();
        OpenAddAccount();
    }

    private void OpenAddAccount()
    {
        // 必须走属性赋值：XAML 里的 Accounts.* 绑定靠 PropertyChanged 重新解析。
        var accounts = _accounts;
        if (accounts is null)
        {
            accounts = new AccountsViewModel(_stack);
            Accounts = accounts;
        }

        RefreshExternalServerChoices(accounts);
        _accountCountAtDialogOpen = _stack.Accounts.Accounts.Count;
        ErrorText = string.Empty;
        IsAddAccountOpen = true;

        SelectKind(_accountKind);
    }

    /// <summary>重建外置登录下拉框：现有服务器 + 「自定义」占位项。</summary>
    private void RefreshExternalServerChoices(AccountsViewModel accounts)
    {
        var previousUrl = _selectedExternalServer?.Entry.Url;
        ExternalServerChoices.Clear();

        foreach (var server in accounts.AuthServers)
        {
            ExternalServerChoices.Add(server);
        }

        ExternalServerChoices.Add(new AuthServerItem
        {
            Entry = new AuthServerEntry { Name = "自定义（手动填写地址）", Url = string.Empty },
            IsCustom = true,
        });

        _selectedExternalServer = null;
        var restored = string.IsNullOrWhiteSpace(previousUrl)
            ? null
            : ExternalServerChoices.FirstOrDefault(item =>
                !item.IsCustom &&
                string.Equals(item.Entry.Url, previousUrl, StringComparison.OrdinalIgnoreCase));

        SelectedExternalServer = restored
                                ?? ExternalServerChoices.FirstOrDefault(item => !item.IsCustom);
    }

    private void SelectKind(SetupAccountKind kind)
    {
        _accountKind = kind;
        OnPropertyChanged(nameof(IsOfflineMode));
        OnPropertyChanged(nameof(IsMicrosoftMode));
        OnPropertyChanged(nameof(IsExternalMode));

        if (_accounts is null)
        {
            return;
        }

        // 切页签时结束上一个方式里可能仍在进行的登录。
        _accounts.CloseDialogCommand.Execute(null);

        switch (kind)
        {
            case SetupAccountKind.Offline:
                _accounts.OpenOfflineDialogCommand.Execute(null);
                break;
            case SetupAccountKind.Microsoft:
                _accounts.OpenMicrosoftDialogCommand.Execute(null);
                break;
            case SetupAccountKind.External:
                if (_selectedExternalServer is null)
                {
                    SelectedExternalServer = ExternalServerChoices.FirstOrDefault();
                }
                else if (!_selectedExternalServer.IsCustom)
                {
                    _accounts.OpenExternalDialogCommand.Execute(_selectedExternalServer);
                }

                break;
        }
    }

    private void CancelAddAccount()
    {
        _accounts?.CloseDialogCommand.Execute(null);
        IsAddAccountOpen = false;

        if (_closeAfterAccountAdded)
        {
            _closeAfterAccountAdded = false;
            IsOpen = false;
        }
    }

    /// <summary>添加账号成功后（账号页 VM 会广播 Changed）收起弹窗并显示账号 ID。</summary>
    private void OnAccountsChanged()
    {
        RefreshAccount();

        if (IsAddAccountOpen && _stack.Accounts.Accounts.Count > _accountCountAtDialogOpen)
        {
            _accounts?.CloseDialogCommand.Execute(null);
            IsAddAccountOpen = false;
        }

        if (_closeAfterAccountAdded && HasAccount)
        {
            _closeAfterAccountAdded = false;
            IsAddAccountOpen = false;
            IsOpen = false;
        }
    }

    private void RefreshAccount()
    {
        var account = _stack.Accounts.Selected;
        HasAccount = account is not null;
        AccountName = account?.Name ?? "尚未添加账户";
        AccountTypeText = account is null
            ? string.Empty
            : account.IsMicrosoft ? "微软账户"
            : account.IsExternal ? "外置登录"
            : "离线模式";
        NotifyCanConfirm();
    }

    // ---------------------------------------------------------------- Java

    private async Task DetectJavaAsync()
    {
        JavaStatusText = "正在探测本机 Java…";

        try
        {
            var detected = await Task.Run(() => _stack.Java.Detect(forceRefresh: true));

            var settings = _stack.Config.Settings;
            settings.DetectedJavas.Clear();
            foreach (var java in detected)
            {
                settings.DetectedJavas.Add(new DetectedJava
                {
                    Path = java.Path,
                    RawVersion = java.RawVersion,
                    MajorVersion = java.MajorVersion,
                    Is64Bit = java.Is64Bit,
                    Vendor = java.Vendor,
                });
            }

            // 用户已经手动填过可用路径就不覆盖。
            if (!File.Exists(JavaPath.Trim()))
            {
                JavaPath = detected.FirstOrDefault()?.Path ?? string.Empty;
            }

            JavaStatusText = string.IsNullOrWhiteSpace(JavaPath)
                ? "没有探测到可用的 64 位 Java，请点击「浏览」手动指定。"
                : $"已自动选择：{JavaPath}";
        }
        catch (Exception ex)
        {
            _stack.Log.Error("初始化设置探测 Java 失败", ex);
            JavaStatusText = "探测失败，请点击「浏览」手动指定 java.exe。";
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
            JavaStatusText = "已手动指定 Java 路径。";
        }
    }

    // ---------------------------------------------------------------- 完成设置

    private void Confirm()
    {
        ErrorText = string.Empty;

        if (_stack.Accounts.Selected is null)
        {
            ErrorText = "请先添加账户。";
            return;
        }

        if (!TryParseMemory(out var memory))
        {
            ErrorText = $"最大内存需要填写不小于 {MemoryFloorMb} MB 的整数。";
            return;
        }

        var javaPath = JavaPath.Trim();
        if (!File.Exists(javaPath))
        {
            ErrorText = "Java 路径无效，请点击「浏览」重新选择 java.exe。";
            return;
        }

        var settings = _stack.Config.Settings;
        settings.MaxMemoryMb = memory;
        settings.MinMemoryMb = Math.Min(settings.MinMemoryMb, memory);
        settings.JavaPath = javaPath;
        settings.SetupCompleted = true;
        _stack.Config.Save();

        _stack.Log.Info(
            $"初始化设置完成：账号 {_stack.Accounts.Selected.Name}，最大内存 {memory} MB，Java {javaPath}");
        IsOpen = false;
        _stack.RaiseChanged();
    }

    private bool TryParseMemory(out int value) =>
        int.TryParse(MaxMemoryText.Trim(), out value) && value >= MemoryFloorMb;

    private void NotifyCanConfirm()
    {
        OnPropertyChanged(nameof(CanConfirm));
        _confirmCommand.RaiseCanExecuteChanged();
    }

    // ---------------------------------------------------------------- 本机内存

    /// <summary>推荐最大内存：本机物理内存的 1/4，封顶 16 GB，下限 512 MB。</summary>
    private static int RecommendedMaxMemoryMb(long totalMb)
    {
        if (totalMb <= 0)
        {
            return 2048;
        }

        return (int)Math.Clamp(totalMb / 4, MemoryFloorMb, MemoryCapMb);
    }

    private static long GetTotalPhysicalMemoryMb()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)(status.TotalPhys / 1024 / 1024) : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
