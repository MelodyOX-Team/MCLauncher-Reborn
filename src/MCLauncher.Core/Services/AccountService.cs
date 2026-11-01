using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// 账号管理：离线账号的增删改查与选中项持久化。
/// 微软账号的登录链在 <c>MicrosoftAuthService</c>（M7），这里只负责把结果存下来。
/// </summary>
public sealed class AccountService
{
    private readonly ConfigService _config;
    private readonly ILogger _log;

    public AccountService(ConfigService config, ILogger log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>全部账号（离线 + 微软）。</summary>
    public IReadOnlyList<Account> Accounts => _config.Settings.Accounts;

    /// <summary>当前选中账号；失效账号会被跳过，没有可用账号时返回 null（上层应引导创建）。</summary>
    public Account? Selected
    {
        get
        {
            var accounts = _config.Settings.Accounts;
            var uuid = _config.Settings.SelectedAccountUuid;

            if (!string.IsNullOrWhiteSpace(uuid))
            {
                var match = accounts.FirstOrDefault(a => a.Uuid == uuid);
                if (match is not null && !match.IsDisabled)
                {
                    return match;
                }
            }

            return accounts.FirstOrDefault(a => !a.IsDisabled);
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
        var existing = _config.Settings.Accounts.FirstOrDefault(a => a.Uuid == uuid);
        if (existing is not null)
        {
            Select(existing);
            return existing;
        }

        var account = Account.CreateOffline(trimmed, uuid);
        _config.Settings.Accounts.Add(account);
        _config.Settings.SelectedAccountUuid = account.Uuid;
        _log.Info($"已添加离线账号：{account.Name}");
        return account;
    }

    /// <summary>保存（或替换）一个账号，通常由微软登录成功后调用。</summary>
    public void Upsert(Account account)
    {
        var index = _config.Settings.Accounts.FindIndex(a => a.Uuid == account.Uuid);
        if (index >= 0)
        {
            _config.Settings.Accounts[index] = account;
        }
        else
        {
            _config.Settings.Accounts.Add(account);
        }

        _config.Settings.SelectedAccountUuid = account.Uuid;
    }

    public void Select(Account account)
    {
        _config.Settings.SelectedAccountUuid = account.Uuid;
        _log.Info($"已切换账号：{account}");
    }

    /// <summary>用新的账号数据替换同 UUID 的记录（刷新令牌/档案后调用），不改变「当前账号」。</summary>
    public void Update(Account account)
    {
        var index = _config.Settings.Accounts.FindIndex(a => a.Uuid == account.Uuid);
        if (index >= 0)
        {
            _config.Settings.Accounts[index] = account;
        }
        else
        {
            _config.Settings.Accounts.Add(account);
        }
    }

    /// <summary>标记账号是否失效；失效账号会被 <see cref="Selected"/> 跳过，并禁止设为当前。</summary>
    public void SetDisabled(Account account, bool disabled, string? reason = null)
    {
        var target = _config.Settings.Accounts.Find(a => a.Uuid == account.Uuid) ?? account;
        target.IsDisabled = disabled;
        target.DisabledReason = disabled ? reason : null;

        _log.Info(disabled
            ? $"账号已标记为失效：{account.Name}（{reason}）"
            : $"账号已恢复可用：{account.Name}");
    }

    public bool Remove(Account account)
    {
        var removed = _config.Settings.Accounts.RemoveAll(a => a.Uuid == account.Uuid) > 0;
        if (!removed)
        {
            return false;
        }

        if (_config.Settings.SelectedAccountUuid == account.Uuid)
        {
            _config.Settings.SelectedAccountUuid = _config.Settings.Accounts.FirstOrDefault()?.Uuid;
        }

        _log.Info($"已删除账号：{account.Name}");
        return true;
    }

    /// <summary>取启动时一定可用的账号；一个都没有时自动补一个默认离线账号。</summary>
    public Account EnsureUsable()
    {
        var selected = Selected;
        if (selected is not null)
        {
            return selected;
        }

        return AddOffline("Player");
    }
}
