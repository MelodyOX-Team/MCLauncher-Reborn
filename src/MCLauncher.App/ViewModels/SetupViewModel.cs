using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using MCLauncher.App.Services;
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

    /// <summary>Azul 接口不可用时的内置版本（带游戏版本说明的在前）。</summary>
    private static readonly int[] DefaultJavaMajors = { 25, 21, 17, 8 };

    /// <summary>Azul 的 LTS 主版本（用于下拉框标注）。</summary>
    private static readonly int[] LtsMajors = { 8, 11, 17, 21, 25, 29 };

    private readonly LauncherStack _stack;
    private readonly RelayCommand _confirmCommand;
    private readonly RelayCommand _downloadJavaCommand;

    private bool _isOpen;
    private string _maxMemoryText;
    private string _javaPath = string.Empty;
    private string _javaStatusText = "正在探测本机 Java…";
    private string _errorText = string.Empty;
    private string _accountName = "尚未添加账户";
    private string _accountTypeText = string.Empty;
    private bool _hasAccount;
    private long _totalMemoryMb;

    private bool _isJavaMissing;
    private bool _isJavaDownloading;
    private bool _setJavaEnvironment = true;
    private double _javaDownloadProgress;
    private string _javaDownloadStatus = string.Empty;
    private JavaDownloadOption? _selectedJavaOption;

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

        foreach (var major in DefaultJavaMajors)
        {
            JavaVersionOptions.Add(CreateJavaOption(major));
        }

        _selectedJavaOption = JavaVersionOptions.FirstOrDefault(option => option.Major == 21)
                              ?? JavaVersionOptions.FirstOrDefault();

        _downloadJavaCommand = new RelayCommand(() => _ = DownloadJavaAsync(), () => !IsJavaDownloading);
        DownloadJavaCommand = _downloadJavaCommand;

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
            _ = LoadJavaVersionsAsync();
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

    /// <summary>没有可用的 64 位 Java 时，展示「选择版本并下载」区域。</summary>
    public bool IsJavaMissing
    {
        get => _isJavaMissing;
        private set => SetProperty(ref _isJavaMissing, value);
    }

    /// <summary>可下载的 Java 版本，打开设置时从 Azul API 动态填充。</summary>
    public ObservableCollection<JavaDownloadOption> JavaVersionOptions { get; } = new();

    public JavaDownloadOption? SelectedJavaOption
    {
        get => _selectedJavaOption;
        set => SetProperty(ref _selectedJavaOption, value);
    }

    public bool IsJavaDownloading
    {
        get => _isJavaDownloading;
        private set
        {
            if (SetProperty(ref _isJavaDownloading, value))
            {
                OnPropertyChanged(nameof(IsJavaDownloadIdle));
            }
        }
    }

    public bool IsJavaDownloadIdle => !IsJavaDownloading;

    /// <summary>Java 下载进度，0–100。</summary>
    public double JavaDownloadProgress
    {
        get => _javaDownloadProgress;
        private set => SetProperty(ref _javaDownloadProgress, value);
    }

    public string JavaDownloadStatus
    {
        get => _javaDownloadStatus;
        private set => SetProperty(ref _javaDownloadStatus, value);
    }

    /// <summary>下载完成后是否把 Java 写入用户环境变量（JAVA_HOME / PATH）。</summary>
    public bool SetJavaEnvironment
    {
        get => _setJavaEnvironment;
        set => SetProperty(ref _setJavaEnvironment, value);
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

    /// <summary>按选择的版本下载并安装 Zulu JDK。</summary>
    public ICommand DownloadJavaCommand { get; }

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

            IsJavaMissing = !File.Exists(JavaPath.Trim());
        }
        catch (Exception ex)
        {
            _stack.Log.Error("初始化设置探测 Java 失败", ex);
            JavaStatusText = "探测失败，请点击「浏览」手动指定 java.exe。";
            IsJavaMissing = true;
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
            IsJavaMissing = false;
        }
    }

    /// <summary>
    /// 从 Azul Metadata API 拉取当前可用的 Zulu 主版本并替换内置列表；
    /// 请求失败时保留内置的 8 / 17 / 21 / 25。
    /// </summary>
    private async Task LoadJavaVersionsAsync()
    {
        try
        {
            var majors = await _stack.ZuluJava.ListAvailableMajorsAsync();

            if (majors.Count == 0)
            {
                return;
            }

            var selectedMajor = SelectedJavaOption?.Major ?? 21;

            // 带游戏版本说明的常用版本置顶，其余按主版本降序排在后面。
            var options = majors
                .Select(CreateJavaOption)
                .OrderByDescending(option => option.HasGameHint)
                .ThenByDescending(option => option.Major)
                .ToList();

            JavaVersionOptions.Clear();
            foreach (var option in options)
            {
                JavaVersionOptions.Add(option);
            }

            SelectedJavaOption = JavaVersionOptions.FirstOrDefault(option => option.Major == selectedMajor)
                                 ?? JavaVersionOptions.FirstOrDefault(option => option.Major == 21)
                                 ?? JavaVersionOptions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _stack.Log.Info($"获取 Zulu 主版本列表失败，使用内置选项：{ex.Message}");
        }
    }

    private static JavaDownloadOption CreateJavaOption(int major)
    {
        var isLts = Array.IndexOf(LtsMajors, major) >= 0;
        var hint = major switch
        {
            8 => "LTS · 适合 1.16.5 及更早版本",
            11 => "LTS",
            17 => "LTS · 适合 1.17 – 1.20.4",
            21 => "LTS · 适合 1.20.5 – 1.21.x（推荐）",
            25 => "LTS · 适合最新快照 / 未来版本",
            _ => isLts ? "LTS" : "非 LTS",
        };

        return new JavaDownloadOption { Major = major, Hint = hint };
    }

    /// <summary>按用户选择的版本下载 Zulu JDK，安装完成后自动填入 Java 路径。</summary>
    private async Task DownloadJavaAsync()
    {
        var option = SelectedJavaOption;
        if (option is null || IsJavaDownloading)
        {
            return;
        }

        ErrorText = string.Empty;
        IsJavaDownloading = true;
        JavaDownloadProgress = 0;
        JavaDownloadStatus = $"正在准备 Java {option.Major}…";

        try
        {
            var progress = new Progress<JavaRuntimeProgress>(p =>
            {
                JavaDownloadProgress = Math.Round(Math.Clamp(p.Fraction, 0d, 1d) * 100, 1);

                var speed = p.BytesPerSecond > 0 && p.Fraction < 1
                    ? $" · {FormatSpeed(p.BytesPerSecond)}/s"
                    : string.Empty;

                JavaDownloadStatus = p.Fraction >= 1
                    ? p.Stage
                    : $"{p.Stage} · {JavaDownloadProgress:0}%{speed}";
            });

            var java = await _stack.ZuluJava.EnsureAsync(option.Major, progress);

            JavaPath = java.Path;
            JavaDownloadProgress = 100;
            JavaDownloadStatus = $"Java {java.MajorVersion} 已就绪";
            IsJavaMissing = false;
            JavaStatusText = $"已安装 Java {java.MajorVersion}：{java.Path}";

            if (SetJavaEnvironment)
            {
                ApplyJavaEnvironment(java.Path);
            }

            _stack.Log.Info($"初始化设置已安装 Java {java.MajorVersion}：{java.Path}");
            NotifyCanConfirm();
        }
        catch (Exception ex)
        {
            _stack.Log.Error($"下载安装 Java {option.Major} 失败", ex);
            ErrorText = $"下载 Java {option.Major} 失败：{ex.Message}";
            JavaDownloadStatus = "下载失败，可以重试，或手动指定已安装的 java.exe。";
        }
        finally
        {
            IsJavaDownloading = false;
        }
    }

    /// <summary>把新安装的 Java 写入用户环境变量；失败只提示，不影响安装结果。</summary>
    private void ApplyJavaEnvironment(string javaPath)
    {
        try
        {
            var result = JavaEnvironmentService.Register(javaPath);
            var details = new List<string>();

            if (result.JavaHomeWritten)
            {
                details.Add("JAVA_HOME 已写入用户环境变量");
            }

            if (result.PathUpdated)
            {
                details.Add("PATH 已追加 Java 目录");
            }

            JavaStatusText += "；" + (details.Count > 0
                ? string.Join("；", details)
                : "用户环境变量已包含该 Java，无需修改");

            _stack.Log.Info($"Java 环境变量已更新：{result.JavaHome}");
        }
        catch (Exception ex)
        {
            _stack.Log.Warn("写入 Java 用户环境变量失败", ex);
            JavaStatusText += "；写入环境变量失败，可手动添加 JAVA_HOME 与 PATH";
        }
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond >= 1024 * 1024
        ? $"{bytesPerSecond / 1024d / 1024d:0.#} MB"
        : $"{bytesPerSecond / 1024d:0.#} KB";

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

/// <summary>初始化设置里可下载的 Java 版本。</summary>
public sealed class JavaDownloadOption
{
    public required int Major { get; init; }

    public required string Hint { get; init; }

    /// <summary>带游戏版本说明的常用版本，在下拉框中置顶显示。</summary>
    public bool HasGameHint => Major is 8 or 17 or 21 or 25;

    /// <summary>下拉框显示文本。</summary>
    public string Display => $"Java {Major} · {Hint}";
}
