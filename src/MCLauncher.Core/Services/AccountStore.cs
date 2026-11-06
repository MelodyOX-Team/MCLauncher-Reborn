using System.Text.Json.Serialization;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Security;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// 账号持久化：账号列表独立保存在 <c>mclcfg/accounts.json</c>，整个文件经过
/// 「启动器密钥 + 本机密钥」双层加密，<c>settings.json</c> 里只保留当前账号 ID。
/// 复制文件到其他电脑、或者解除绑定的账号文件都无法解密，因此不能用于分享账号。
/// </summary>
public sealed class AccountStore
{
    public const string FileName = "accounts.json";

    /// <summary>写在 accounts.json 顶部的说明（JSON 不支持注释，这里用固定字段代替）。</summary>
    public const string ReadmeText =
        "本文件保存启动器账号的登录凭据，已使用本机密钥与启动器密钥双层加密，只对当前电脑有效。\n" +
        "请绝对不要复制、上传、分享或泄露此文件：一旦泄露，他人可能冒充你的游戏账号。\n" +
        "删除此文件只会清空启动器里的账号列表，不会影响游戏本体，可在启动器中重新添加账号。";

    private readonly ConfigService _config;
    private readonly ILogger _log;
    private readonly List<Account> _accounts = new();
    private bool _loadFailed;
    private bool _blockOverwrite;

    public AccountStore(ConfigService config, ILogger log)
    {
        _config = config;
        _log = log;
    }

    public IReadOnlyList<Account> Accounts => _accounts;

    /// <summary>可变列表，仅供同程序集的 <see cref="AccountService"/> 修改后调用 <see cref="Save"/>。</summary>
    internal List<Account> Items => _accounts;

    public string FilePath => Path.Combine(_config.ConfigDirectory, FileName);

    /// <summary>
    /// 读取账号文件；文件缺失、损坏或无法解密时按「没有账号」处理，而不是崩溃。
    /// 无法解密时把文件改名保留（<c>accounts-unreadable-*.json</c>），避免后续保存把原文件覆盖掉。
    /// </summary>
    public void Load()
    {
        _accounts.Clear();
        _loadFailed = false;

        var path = FilePath;
        if (File.Exists(path))
        {
            try
            {
                var vault = JsonUtil.DeserializeFromFile<AccountsVault>(path);
                if (vault is null)
                {
                    throw new InvalidDataException("accounts.json 内容为空");
                }

                _accounts.AddRange(AccountsVaultCrypto.Open(vault, out var usedFallback));
                _log.Info($"已加载加密账号文件（{vault.Protector}），共 {_accounts.Count} 个账号");

                // TPM 密钥被系统清除/重建时，用 DPAPI 备份救回账号，并重新封装一次文件。
                if (usedFallback)
                {
                    _log.Warn("TPM 密钥已失效，已通过 DPAPI 备份解开账号，正在重新加密保存");
                    Save();
                }
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                _log.Warn($"accounts.json 无法解密，按没有账号处理：{path}", ex);
                PreserveUnreadableFile(path);
                _accounts.Clear();
            }
        }

        MigrateLegacyAccounts();
        ValidateSelection();
    }

    /// <summary>
    /// 把无法解密的账号文件改名保留，避免它被下一次保存直接覆盖。
    /// 用户若之后修好 TPM/换回原来的 Windows 账户，仍可从这个备份里找回账号。
    /// </summary>
    private bool PreserveUnreadableFile(string path)
    {
        try
        {
            var backup = Path.Combine(
                _config.ConfigDirectory,
                $"accounts-unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(path, backup, overwrite: true);
            _log.Warn($"已将无法解密的账号文件保留为：{backup}");
            return true;
        }
        catch (Exception ex)
        {
            _blockOverwrite = true;
            _log.Error("保留无法解密的账号文件失败，本次不写入账号文件以免覆盖", ex);
            return false;
        }
    }

    /// <summary>把账号列表加密写回 accounts.json；失败时保留内存中的账号并返回 false。</summary>
    public bool Save()
    {
        if (_blockOverwrite)
        {
            _log.Error($"上次无法解密的账号文件没能备份，拒绝覆盖：{FilePath}");
            return false;
        }

        try
        {
            var vault = AccountsVaultCrypto.Seal(
                _accounts,
                reason => _log.Warn($"TPM 不可用，本次用 DPAPI 保护账号：{reason}"));
            JsonUtil.SerializeToFile(FilePath, vault);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"保存 accounts.json 失败：{FilePath}", ex);
            return false;
        }
    }

    /// <summary>
    /// 销毁本地账号凭据：删除 accounts.json（DPAPI 模式的软件私钥就在文件里）、清空内存列表、
    /// 清掉 settings.json 里的当前账号 ID，并删除 TPM 中的持久化密钥。
    /// </summary>
    /// <param name="releaseMachineKey">
    /// 是否同时删除 TPM 中的持久化密钥；测试中传 false，避免删掉开发机上真实账号正在用的密钥。
    /// </param>
    /// <returns>账号文件是否已删除（文件本来不存在也算成功）。</returns>
    public bool Purge(bool releaseMachineKey = true)
    {
        var fileGone = true;
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex)
        {
            fileGone = false;
            _log.Error($"删除账号文件失败：{FilePath}", ex);
        }

        // 连同「无法解密」的备份一起销毁，否则令牌仍以密文形式留在磁盘上。
        foreach (var backup in Directory.GetFiles(_config.ConfigDirectory, "accounts-unreadable-*.json"))
        {
            try
            {
                File.Delete(backup);
            }
            catch (Exception ex)
            {
                _log.Warn($"删除账号备份失败：{backup}", ex);
            }
        }

        _accounts.Clear();
        _config.Settings.SelectedAccountUuid = null;
        _config.Save();

        // 即使文件删除失败也要释放 TPM 密钥：残留的 accounts.json 会变成无法解密的死文件。
        var tpmReleased = releaseMachineKey && MachineKey.DeleteTpmKey();
        _log.Info($"已销毁本地账号凭据（删除文件：{fileGone}，释放 TPM 密钥：{tpmReleased}）");
        return fileGone;
    }

    /// <summary>
    /// 旧版把账号明文（令牌经 DPAPI）放在 settings.json 的 <c>accounts</c> 里。
    /// 首次以新版启动时自动迁移到加密的 accounts.json，并从 settings.json 移除。
    /// </summary>
    private void MigrateLegacyAccounts()
    {
        var legacy = _config.Settings.LegacyAccounts;
        if (legacy is null || legacy.Count == 0)
        {
            _config.Settings.LegacyAccounts = null;
            return;
        }

        _accounts.Clear();
        _accounts.AddRange(legacy);
        _config.Settings.LegacyAccounts = null;

        if (!Save())
        {
            _log.Warn("旧账号迁移写入 accounts.json 失败，本次会话仍可使用内存中的账号");
        }

        _config.Save();
        _log.Info($"已将 settings.json 里的 {_accounts.Count} 个旧账号迁移到 accounts.json");
    }

    /// <summary>当前账号 ID 指向的账号不存在（例如 accounts.json 被删除/替换）时清空选择。</summary>
    private void ValidateSelection()
    {
        // 解密失败时不要动用户的指向：账号文件已保留为备份，修好本机密钥后仍能恢复。
        if (_loadFailed)
        {
            return;
        }

        var uuid = _config.Settings.SelectedAccountUuid;
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return;
        }

        if (_accounts.Any(account => string.Equals(account.Uuid, uuid, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _config.Settings.SelectedAccountUuid = null;
        _config.Save();
        _log.Warn($"当前账号 {uuid} 在 accounts.json 中不存在，已清空选择");
    }
}

/// <summary>accounts.json 的落盘结构：说明 + 双层包装后的数据密钥 + 加密载荷。</summary>
internal sealed class AccountsVault
{
    [JsonPropertyName("readme")]
    public string Readme { get; set; } = AccountStore.ReadmeText;

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary><c>tpm2</c> / <c>dpapi</c> / <c>plain</c>。</summary>
    [JsonPropertyName("protector")]
    public string Protector { get; set; } = string.Empty;

    /// <summary>TPM 模式的密钥名，或 DPAPI 模式受保护的软件私钥。</summary>
    [JsonPropertyName("machineKey")]
    public string MachineKey { get; set; } = string.Empty;

    /// <summary>本机密钥包装后的数据密钥（Base64）。TPM 密钥失效时用下面的 DPAPI 备份解开。</summary>
    [JsonPropertyName("outer")]
    public string Outer { get; set; } = string.Empty;

    /// <summary>DPAPI 兜底备份的受保护软件私钥（Base64）；仅 TPM 模式会写入。</summary>
    [JsonPropertyName("machineKeyFallback")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MachineKeyFallback { get; set; }

    /// <summary>DPAPI 兜底备份包装后的数据密钥（Base64）。</summary>
    [JsonPropertyName("outerFallback")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OuterFallback { get; set; }

    /// <summary>AES-256-GCM 加密的账号列表（Base64：nonce + tag + ciphertext）。</summary>
    [JsonPropertyName("payload")]
    public string Payload { get; set; } = string.Empty;
}
