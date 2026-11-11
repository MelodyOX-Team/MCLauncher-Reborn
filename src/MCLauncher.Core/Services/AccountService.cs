using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Security;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// 账号管理：离线账号的增删改查与选中项持久化。
/// 账号列表存放在独立的加密文件 <c>mclcfg/accounts.json</c>（见 <see cref="AccountStore"/>），
/// <c>settings.json</c> 只保留当前账号 ID。
/// 微软账号的登录链在 <c>MicrosoftAuthService</c>，这里只负责把结果存下来。
/// </summary>
public sealed class AccountService
{
    private readonly ConfigService _config;
    private readonly AccountStore _store;
    private readonly ILogger _log;

    public AccountService(ConfigService config, ILogger log)
    {
        _config = config;
        _log = log;
        _store = new AccountStore(config, log);
        _store.Load();
    }

    /// <summary>全部账号（离线 + 微软）。</summary>
    public IReadOnlyList<Account> Accounts => _store.Accounts;

    /// <summary>当前选中账号；失效账号会被跳过，没有可用账号时返回 null（上层应引导创建）。</summary>
    public Account? Selected
    {
        get
        {
            var uuid = _config.Settings.SelectedAccountUuid;

            if (!string.IsNullOrWhiteSpace(uuid))
            {
                var match = _store.Accounts.FirstOrDefault(a => a.Uuid == uuid);
                if (match is not null && !match.IsDisabled)
                {
                    return match;
                }
            }

            return _store.Accounts.FirstOrDefault(a => !a.IsDisabled);
        }
    }

    /// <summary>创建一个离线账号。同名会直接复用已有账号。</summary>
    public Account AddOffline(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("玩家名不能为空", nameof(name));
        }

        if (trimmed.Length > 16)
        {
            throw new ArgumentException("玩家名不能超过 16 个字符", nameof(name));
        }

        var uuid = HashUtil.OfflinePlayerUuid(trimmed);
        var existing = _store.Accounts.FirstOrDefault(a => a.Uuid == uuid);
        if (existing is not null)
        {
            Select(existing);
            return existing;
        }

        var account = Account.CreateOffline(trimmed, uuid);
        _store.Items.Add(account);
        _config.Settings.SelectedAccountUuid = account.Uuid;
        PersistOrThrow();
        _log.Info($"已添加离线账号：{account.Name}");
        return account;
    }

    /// <summary>保存（或替换）一个账号，通常由微软登录成功后调用。</summary>
    public void Upsert(Account account)
    {
        var index = _store.Items.FindIndex(a => a.Uuid == account.Uuid);
        if (index >= 0)
        {
            _store.Items[index] = account;
        }
        else
        {
            _store.Items.Add(account);
        }

        _config.Settings.SelectedAccountUuid = account.Uuid;
        PersistOrThrow();
    }

    public void Select(Account account)
    {
        _config.Settings.SelectedAccountUuid = account.Uuid;
        _config.Save();
        _log.Info($"已切换账号：{account}");
    }

    /// <summary>用新的账号数据替换同 UUID 的记录（刷新令牌/档案后调用），不改变「当前账号」。</summary>
    public void Update(Account account)
    {
        var index = _store.Items.FindIndex(a => a.Uuid == account.Uuid);
        if (index >= 0)
        {
            _store.Items[index] = account;
        }
        else
        {
            _store.Items.Add(account);
        }

        Persist();
    }

    /// <summary>标记账号是否失效；失效账号会被 <see cref="Selected"/> 跳过，并禁止设为当前。</summary>
    public void SetDisabled(Account account, bool disabled, string? reason = null)
    {
        var target = _store.Items.Find(a => a.Uuid == account.Uuid) ?? account;
        target.IsDisabled = disabled;
        target.DisabledReason = disabled ? reason : null;
        Persist();

        _log.Info(disabled
            ? $"账号已标记为失效：{account.Name}（{reason}）"
            : $"账号已恢复可用：{account.Name}");
    }

    public bool Remove(Account account)
    {
        var removed = _store.Items.RemoveAll(a => a.Uuid == account.Uuid) > 0;
        if (!removed)
        {
            return false;
        }

        if (_config.Settings.SelectedAccountUuid == account.Uuid)
        {
            _config.Settings.SelectedAccountUuid = _store.Accounts.FirstOrDefault()?.Uuid;
        }

        Persist();
        _log.Info($"已删除账号：{account.Name}");
        return true;
    }

    /// <summary>
    /// 销毁本地账号凭据：删除加密的 accounts.json、清空当前账号、释放 TPM/DPAPI 本机密钥。
    /// 不可恢复，调用方应先向用户确认。
    /// </summary>
    public bool PurgeAllAccounts(bool releaseMachineKey = true) => _store.Purge(releaseMachineKey);

    /// <summary>账号列表与当前选择一起落盘（账号到 accounts.json，选择到 settings.json）。</summary>
    private void Persist()
    {
        _store.Save();
        _config.Save();
    }

    /// <summary>
    /// 登录/新增账号时的落盘：本机安全存储不可用会导致账号无法安全保存，
    /// 这里直接抛出并让界面提示用户，而不是静默丢弃凭据。
    /// </summary>
    private void PersistOrThrow()
    {
        if (!_store.Save())
        {
            throw new MachineKeyUnavailableException(
                _store.LastSaveError ?? "账号凭据无法安全保存，已拒绝登录。");
        }

        _config.Save();
    }
}
