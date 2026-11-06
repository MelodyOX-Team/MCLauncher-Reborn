using MCLauncher.Core.Models;
using MCLauncher.Core.Services;
using MCLauncher.Core.Tests.Support;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Tests;

public sealed class AccountTests
{
    /// <summary>
    /// 离线 UUID 必须等于 <c>UUID.nameUUIDFromBytes(("OfflinePlayer:"+name).getBytes(UTF_8))</c>。
    /// 这几个值是按 Java 算法独立算出来的参照值，写死在这里防止将来"顺手改成普通 MD5"。
    /// </summary>
    [Theory]
    [InlineData("Notch", "b50ad385829d3141a2167e7d7539ba7f")]
    [InlineData("Steve", "5627dd98e6be3c21b8a8e92344183641")]
    [InlineData("Player", "a01e3843e5213998958af459800e4d11")]
    public void 离线玩家UUID符合协议(string name, string expected) =>
        Assert.Equal(expected, HashUtil.OfflinePlayerUuid(name));

    [Fact]
    public void 离线账号带上版本位与变体位()
    {
        var uuid = HashUtil.OfflinePlayerUuid("Notch");

        Assert.Equal(32, uuid.Length);
        // version 3
        Assert.Equal('3', uuid[12]);
        // RFC 4122 variant（8/9/a/b）
        Assert.Contains(uuid[16], "89ab");
    }

    [Fact]
    public void 离线账号的默认字段()
    {
        var account = Account.CreateOffline("Player");

        Assert.Equal(AccountKind.Offline, account.Kind);
        Assert.Equal("legacy", account.UserType);
        Assert.Equal("0", account.AccessToken);
        Assert.False(account.IsMicrosoft);
        Assert.Null(account.Xuid);
    }

    [Fact]
    public void 微软账号使用msa类型()
    {
        var account = new Account { Kind = AccountKind.Microsoft, Name = "Steve" };
        Assert.Equal("msa", account.UserType);
        Assert.True(account.IsMicrosoft);
    }

    [Fact]
    public void 失效账号会被跳过且重新登录后恢复()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcl-accounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new TestLog();
            var config = new ConfigService(
                new JsonConfigStore(Path.Combine(root, "config"), log),
                log,
                includeBuiltInSecrets: false);
            config.Load();

            var service = new AccountService(config, log);
            var first = service.AddOffline("First");
            var second = service.AddOffline("Second");

            service.Select(second);
            service.SetDisabled(second, true, "令牌已失效");

            // 失效账号不再被视为当前账号，自动落到下一个可用账号。
            Assert.Equal(first.Uuid, service.Selected?.Uuid);

            // 重新登录成功（标记恢复）后回到原来的当前账号。
            service.SetDisabled(second, false);
            Assert.Equal(second.Uuid, service.Selected?.Uuid);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void 过期判定()
    {
        var account = new Account
        {
            Kind = AccountKind.Microsoft,
            Name = "S",
            ExpiresAt = DateTimeOffset.Now.AddMinutes(5),
        };

        Assert.False(account.IsExpired(TimeSpan.FromMinutes(1)));
        Assert.True(account.IsExpired(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void UUID格式化()
    {
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", HashUtil.FormatUuid("b50ad385829d3141a2167e7d7539ba7f"));
        // 已是标准写法时原样返回
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", HashUtil.FormatUuid("b50ad385-829d-3141-a216-7e7d7539ba7f"));
        // 非法长度不抛异常
        Assert.Equal("short", HashUtil.FormatUuid("short"));
    }

    [Fact]
    public void 游戏输出编码按Java版本选择()
    {
        var modern = MCLauncher.Core.Services.GameLauncher.ResolveOutputEncoding(21);
        Assert.Equal(System.Text.Encoding.UTF8.WebName, modern.WebName);

        // 老版本不应抛异常
        var legacy = MCLauncher.Core.Services.GameLauncher.ResolveOutputEncoding(8);
        Assert.NotNull(legacy);
    }

    [Fact]
    public void 退出码有可读解释()
    {
        Assert.Equal("正常退出", MCLauncher.Core.Services.GameLauncher.DescribeExitCode(0));
        Assert.Contains("异常退出", MCLauncher.Core.Services.GameLauncher.DescribeExitCode(1));
        Assert.Contains("异常退出", MCLauncher.Core.Services.GameLauncher.DescribeExitCode(255));
    }

    [Fact]
    public void 账号写入独立的加密文件且settings不再包含账号列表()
    {
        WithConfig((config, log, dir) =>
        {
            var service = new AccountService(config, log);
            service.AddOffline("加密测试甲");

            var accountsPath = Path.Combine(config.ConfigDirectory, AccountStore.FileName);
            Assert.True(File.Exists(accountsPath));

            // 文件说明保留在明文里，账号与令牌在加密载荷中。
            var vault = File.ReadAllText(accountsPath);
            Assert.Contains("readme", vault);
            Assert.DoesNotContain("加密测试甲", vault);

            // settings.json 只保留当前账号 ID。
            var settings = File.ReadAllText(Path.Combine(config.ConfigDirectory, "settings.json"));
            Assert.DoesNotContain("\"accounts\"", settings);
            Assert.NotNull(config.Settings.SelectedAccountUuid);

            // 重新构造服务（相当于重启启动器）能解回同一个账号。
            var reloaded = new AccountService(config, log);
            Assert.Equal("加密测试甲", Assert.Single(reloaded.Accounts).Name);
            Assert.Equal(reloaded.Accounts[0].Uuid, reloaded.Selected?.Uuid);
        });
    }

    [Fact]
    public void 删除账号文件后当前账号置空()
    {
        WithConfig((config, log, dir) =>
        {
            var service = new AccountService(config, log);
            service.AddOffline("待删除");
            Assert.NotNull(config.Settings.SelectedAccountUuid);

            File.Delete(Path.Combine(config.ConfigDirectory, AccountStore.FileName));

            var reloaded = new AccountService(config, log);
            Assert.Empty(reloaded.Accounts);
            Assert.Null(reloaded.Selected);
            Assert.Null(config.Settings.SelectedAccountUuid);
        });
    }

    [Fact]
    public void 指向不存在的账号时置空()
    {
        WithConfig((config, log, dir) =>
        {
            var service = new AccountService(config, log);
            service.AddOffline("甲");

            config.Settings.SelectedAccountUuid = "00000000000000000000000000000000";
            config.Save();

            var reloaded = new AccountService(config, log);
            Assert.Single(reloaded.Accounts);
            Assert.Null(config.Settings.SelectedAccountUuid);
        });
    }

    [Fact]
    public void 旧版settings里的账号迁移到accounts()
    {
        WithConfig((config, log, dir) =>
        {
            var legacy = Account.CreateOffline("旧账号");
            config.Settings.LegacyAccounts = new List<Account> { legacy };
            config.Settings.SelectedAccountUuid = legacy.Uuid;
            config.Save();

            var reloaded = new AccountService(config, log);

            Assert.Equal("旧账号", Assert.Single(reloaded.Accounts).Name);
            Assert.Equal(legacy.Uuid, reloaded.Selected?.Uuid);
            Assert.True(File.Exists(Path.Combine(config.ConfigDirectory, AccountStore.FileName)));
            Assert.DoesNotContain(
                "\"accounts\"",
                File.ReadAllText(Path.Combine(config.ConfigDirectory, "settings.json")));
        });
    }

    [Fact]
    public void 销毁本机凭据后账号文件删除且当前账号置空()
    {
        WithConfig((config, log, dir) =>
        {
            var service = new AccountService(config, log);
            service.AddOffline("销毁测试");

            Assert.True(service.PurgeAllAccounts(releaseMachineKey: false));
            Assert.False(File.Exists(Path.Combine(config.ConfigDirectory, AccountStore.FileName)));
            Assert.Empty(service.Accounts);
            Assert.Null(config.Settings.SelectedAccountUuid);

            var reloaded = new AccountService(config, log);
            Assert.Empty(reloaded.Accounts);
            Assert.Null(reloaded.Selected);
        });
    }

    [Fact]
    public void 无法解密的账号文件改名保留且不清空指向()
    {
        WithConfig((config, log, dir) =>
        {
            var path = Path.Combine(config.ConfigDirectory, AccountStore.FileName);
            config.Settings.SelectedAccountUuid = "d958d2314b274db180f07c59a04ec611";
            config.Save();

            // 模拟 TPM 密钥失效后留下的旧文件：结构合法但密文解不开。
            File.WriteAllText(
                path,
                "{\"version\":1,\"protector\":\"tpm2\","
                + "\"machineKey\":\"MCLauncherReborn-Accounts-v1\",\"outer\":\"AAAA\",\"payload\":\"AAAA\"}");

            var service = new AccountService(config, log);

            Assert.Empty(service.Accounts);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(config.ConfigDirectory, "accounts-unreadable-*.json"));
            Assert.Equal("d958d2314b274db180f07c59a04ec611", config.Settings.SelectedAccountUuid);

            // 备份之后仍能正常新增账号，并且新文件重启后能解密。
            service.AddOffline("重新添加");
            Assert.True(File.Exists(path));
            Assert.Equal("重新添加", Assert.Single(new AccountService(config, log).Accounts).Name);
        });
    }

    [Fact]
    public void TPM密文失效时用DPAPI备份恢复账号()
    {
        WithConfig((config, log, dir) =>
        {
            var service = new AccountService(config, log);
            service.AddOffline("兜底账号");

            var path = Path.Combine(config.ConfigDirectory, AccountStore.FileName);
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            // 本机没有可用 TPM 时只写 DPAPI 一路，没有备份可测。
            if (node["outerFallback"] is null)
            {
                return;
            }

            // 只破坏 TPM 那一路密文，模拟 TPM 密钥被系统清除后换成了新密钥。
            node["outer"] = Convert.ToBase64String(new byte[256]);
            File.WriteAllText(path, node.ToJsonString());

            var reloaded = new AccountService(config, log);
            Assert.Equal("兜底账号", Assert.Single(reloaded.Accounts).Name);
        });
    }

    private static void WithConfig(Action<ConfigService, TestLog, string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcl-accounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new TestLog();
            var config = new ConfigService(
                new JsonConfigStore(Path.Combine(root, "config"), log),
                log,
                includeBuiltInSecrets: false);
            config.Load();
            body(config, log, root);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
