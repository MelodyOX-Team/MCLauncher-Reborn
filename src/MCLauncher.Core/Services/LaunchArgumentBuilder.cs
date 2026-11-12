using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>一次启动要交给 java 的完整命令行。</summary>
public sealed class LaunchCommand
{
    public required string JavaPath { get; init; }

    public required string MainClass { get; init; }

    public required IReadOnlyList<string> JvmArguments { get; init; }

    public required IReadOnlyList<string> GameArguments { get; init; }

    public required string Classpath { get; init; }

    /// <summary>进程工作目录。</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>传给进程的额外环境变量。</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>游戏进程优先级。</summary>
    public GameProcessPriority Priority { get; init; } = GameProcessPriority.Normal;

    /// <summary>放在 java 命令前面的包装命令。</summary>
    public string? WrapperCommand { get; init; }

    /// <summary>出现在参数里的敏感值（访问令牌）；安全展示时按值替换。</summary>
    public IReadOnlyList<string> SensitiveValues { get; init; } = Array.Empty<string>();

    /// <summary>已替换完变量的完整参数表（不含 java 自身路径）。</summary>
    public IReadOnlyList<string> ToArgumentList() =>
        JvmArguments.Concat(new[] { MainClass }).Concat(GameArguments).ToArray();

    /// <summary>原始参数的可读形式；只用于开发期排查，<b>不得写入日志</b>。</summary>
    public string ToDisplayString() => "java " + string.Join(' ', ToArgumentList().Select(QuoteIfNeeded));

    /// <summary>
    /// 用于落盘日志的可读形式：访问令牌等敏感参数只留下参数名或 <c>***</c>。
    /// 日志经常被用户拿去求助，不能把可冒用的令牌一起带出去。
    /// </summary>
    public string ToSafeDisplayString()
    {
        var secrets = SensitiveValues.Where(value => value.Length >= 4).ToArray();
        var redacted = new List<string>();
        var hideNext = false;

        foreach (var argument in ToArgumentList())
        {
            if (hideNext)
            {
                hideNext = false;
                redacted.Add("***");
                continue;
            }

            var separator = argument.IndexOf('=');
            if (separator > 0 && SensitiveArgumentNames.Contains(argument[..separator]))
            {
                redacted.Add(argument[..(separator + 1)] + "***");
                continue;
            }

            if (SensitiveArgumentNames.Contains(argument))
            {
                hideNext = true;
                redacted.Add(argument);
                continue;
            }

            // 旧版 JSON 会把 ${auth_session} 当作位置参数，没有参数名可依，只能按值替换。
            var value = argument;
            foreach (var secret in secrets)
            {
                value = value.Replace(secret, "***", StringComparison.Ordinal);
            }

            redacted.Add(value);
        }

        return "java " + string.Join(' ', redacted.Select(QuoteIfNeeded));
    }

    /// <summary>需要打码的参数名（命令行里以「参数名 值」或「参数名=值」出现）。</summary>
    private static readonly HashSet<string> SensitiveArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "--accessToken",
        "--authSession",
        "--clientToken",
        "--session",
    };

    private static string QuoteIfNeeded(string value) =>
        value.Contains(' ') ? "\"" + value + "\"" : value;
}

/// <summary>构建 <see cref="LaunchCommand"/> 所需的上下文。</summary>
public sealed class LaunchContext
{
    /// <summary>已合并继承链的自洽版本 JSON。</summary>
    public required GameVersionJson Version { get; init; }

    public required Account Account { get; init; }

    public required LaunchOptions Options { get; init; }

    public required string JavaPath { get; init; }

    /// <summary>实例 id（<c>${version_name}</c>）。</summary>
    public required string VersionId { get; init; }

    /// <summary>实例目录（隔离时为 <c>versions/&lt;id&gt;</c>）。</summary>
    public required string GameDirectory { get; init; }

    /// <summary>进程工作目录；为空时使用 <see cref="GameDirectory"/>。</summary>
    public string? WorkingDirectory { get; init; }

    public required string AssetsRoot { get; init; }

    public required string NativesDirectory { get; init; }

    public required string LibrariesDirectory { get; init; }

    public required string ClientJarPath { get; init; }

    public string? AssetsIndexName { get; init; }

    /// <summary>下载好的 log4j 配置文件路径（老版本需要）。</summary>
    public string? LogConfigPath { get; init; }

    /// <summary>外置登录：authlib-injector jar 的绝对路径。</summary>
    public string? AuthlibInjectorJar { get; init; }

    /// <summary>外置登录：认证服务器 API 根地址。</summary>
    public string? AuthlibServer { get; init; }

    /// <summary>外置登录：认证服务器元数据（base64），供 authlib-injector 预取。</summary>
    public string? AuthlibPrefetchedMetadata { get; init; }

    /// <summary>额外特性开关（默认按分辨率为用户设定推导）。</summary>
    public IReadOnlyDictionary<string, bool>? Features { get; init; }

    /// <summary>显式指定 classpath 顺序（留空时由构建器按"库在前、jar 在后"生成）。</summary>
    public IReadOnlyList<string>? ClasspathOverride { get; init; }
}

/// <summary>
/// 参数拼装。要点：
/// <list type="bullet">
/// <item>新式 <c>arguments.jvm/game</c> 与旧式 <c>minecraftArguments</c> 双分支，两者都必须支持。</item>
/// <item>每条带 <c>rules</c> 的参数都要按当前环境 + 特性开关判定是否生效。</item>
/// <item>变量表要齐全，任何残留的 <c>${...}</c> 都会让游戏收到字面量参数。</item>
/// <item>classpath 顺序为「库在前、客户端 jar 在后」，这样加载器才能覆盖原版的同名类。</item>
/// </list>
/// </summary>
public sealed class LaunchArgumentBuilder
{
    private readonly ILogger _log;

    public LaunchArgumentBuilder(ILogger log)
    {
        _log = log;
    }

    public LaunchCommand Build(LaunchContext context)
    {
        var version = context.Version;
        var options = context.Options;
        var features = context.Features ?? BuildFeatures(options);

        var classpath = context.ClasspathOverride is { Count: > 0 }
            ? string.Join(Path.PathSeparator, context.ClasspathOverride)
            : BuildClasspath(context);

        var variables = BuildVariables(context, classpath);

        var jvm = new List<string>();
        var maxMemory = Math.Max(64, options.MaxMemoryMb);
        // 初始堆不能大于最大堆，否则 JVM 会直接拒绝启动。
        var minMemory = Math.Min(Math.Max(16, options.MinMemoryMb), maxMemory);
        jvm.Add($"-Xmx{maxMemory}M");
        jvm.Add($"-Xms{minMemory}M");

        if (!string.IsNullOrWhiteSpace(context.LogConfigPath))
        {
            // Log4Shell 缓解措施：老版本自带的 log4j 无法升级，只能靠这个开关兜住。
            jvm.Add("-Dlog4j2.formatMsgNoLookups=true");
        }

        // 外置登录：交给 authlib-injector 拦截会话请求。
        if (!string.IsNullOrWhiteSpace(context.AuthlibInjectorJar) &&
            !string.IsNullOrWhiteSpace(context.AuthlibServer))
        {
            jvm.Add($"-javaagent:{context.AuthlibInjectorJar}={context.AuthlibServer}");
            jvm.Add("-Dauthlibinjector.side=client");

            if (!string.IsNullOrWhiteSpace(context.AuthlibPrefetchedMetadata))
            {
                jvm.Add("-Dauthlibinjector.yggdrasil.prefetched=" + context.AuthlibPrefetchedMetadata);
            }
        }

        if (version.Arguments?.Jvm is { Count: > 0 } jvmFromJson)
        {
            Expand(jvm, jvmFromJson, features, "JVM");
        }
        else
        {
            AddLegacyJvmDefaults(jvm);
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraJvmArgs))
        {
            jvm.AddRange(SplitArguments(options.ExtraJvmArgs!));
        }

        var game = new List<string>();
        if (version.Arguments?.Game is { Count: > 0 } gameFromJson)
        {
            Expand(game, gameFromJson, features, "游戏");
        }
        else
        {
            var legacy = string.IsNullOrWhiteSpace(version.MinecraftArguments)
                ? version.DefaultLegacyArguments
                : version.MinecraftArguments!;

            game.AddRange(SplitArguments(legacy));

            // 老版本 JSON 没有分辨率的规则化参数，只能自己补。
            if (options.HasCustomResolution)
            {
                game.Add("--width");
                game.Add(options.Width.ToString());
                game.Add("--height");
                game.Add(options.Height.ToString());
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ServerIp))
        {
            if (McVersionUtil.IsAtLeast(options.GameVersion, 1, 20, 0))
            {
                // 1.20 起 --server / --port 被 Quick Play 取代。
                game.Add("--quickPlayMultiplayer");
                game.Add(Connect.DirectProtocol.FormatAddress(options.ServerIp!, options.ServerPort));
            }
            else
            {
                game.Add("--server");
                game.Add(options.ServerIp!);
                game.Add("--port");
                game.Add(options.ServerPort.ToString());
            }
        }
        else if (options.QuickPlayMode is QuickPlayMode.Singleplayer or QuickPlayMode.Realms)
        {
            AddQuickPlayArguments(game, options);
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraGameArgs))
        {
            game.AddRange(SplitArguments(options.ExtraGameArgs!));
        }

        ReplaceVariables(jvm, variables);
        ReplaceVariables(game, variables);

        var mainClass = version.MainClass;
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new InvalidDataException($"版本 {context.VersionId} 没有 mainClass，无法启动");
        }

        _log.Info($"已生成启动参数：JVM {jvm.Count} 项，游戏 {game.Count} 项，classpath {classpath.Split(Path.PathSeparator).Length} 项");

        return new LaunchCommand
        {
            JavaPath = context.JavaPath,
            MainClass = mainClass!,
            JvmArguments = jvm,
            GameArguments = game,
            Classpath = classpath,
            WorkingDirectory = context.WorkingDirectory ?? context.GameDirectory,
            Environment = options.EnvironmentVariables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Priority = options.ProcessPriority,
            WrapperCommand = options.WrapperCommand,
            SensitiveValues = new[] { context.Account.AccessToken }
                .Where(token => !string.IsNullOrWhiteSpace(token) && token != "0")
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };
    }

    /// <summary>单人 / Realms 的快速游玩参数；仅 1.20 及以上支持。</summary>
    private void AddQuickPlayArguments(List<string> game, LaunchOptions options)
    {
        if (!McVersionUtil.IsAtLeast(options.GameVersion, 1, 20, 0))
        {
            _log.Warn("快速游玩（单人 / Realms）需要 Minecraft 1.20 及以上版本，已忽略该设置");
            return;
        }

        if (options.QuickPlayMode == QuickPlayMode.Singleplayer
            && !string.IsNullOrWhiteSpace(options.QuickPlayWorld))
        {
            game.Add("--quickPlaySingleplayer");
            game.Add(options.QuickPlayWorld!);
        }
        else if (options.QuickPlayMode == QuickPlayMode.Realms
                 && !string.IsNullOrWhiteSpace(options.QuickPlayRealmsId))
        {
            game.Add("--quickPlayRealms");
            game.Add(options.QuickPlayRealmsId!);
        }
    }

    /// <summary>
    /// classpath：先所有生效的非 natives 库，最后是客户端 jar。
    /// 注意库路径必须基于「共享库目录」而不是实例目录 —— 版本隔离时实例目录在
    /// <c>versions/&lt;id&gt;</c>，而 libraries 仍然在游戏根目录下。
    /// </summary>
    public string BuildClasspath(LaunchContext context)
    {
        var entries = new List<string>();

        foreach (var library in context.Version.Libraries ?? new List<Library>())
        {
            if (!library.Applies() || library.IsNativeLibrary)
            {
                continue;
            }

            var artifactPath = library.ArtifactPath();
            if (artifactPath is null)
            {
                continue;
            }

            entries.Add(LibraryPath(context.LibrariesDirectory, artifactPath));
        }

        // 客户端 jar 放最后：库中的同名类优先，加载器才能覆盖原版。
        entries.Add(context.ClientJarPath);

        return string.Join(Path.PathSeparator, entries);
    }

    public static string LibraryPath(string librariesDirectory, string artifactPath) =>
        Path.Combine(librariesDirectory, artifactPath.Replace('/', Path.DirectorySeparatorChar));

    private static Dictionary<string, bool> BuildFeatures(LaunchOptions options) => new(StringComparer.Ordinal)
    {
        ["is_demo_user"] = false,
        ["has_custom_resolution"] = options.HasCustomResolution,
        ["has_quick_plays_support"] = false,
        ["is_quick_play_singleplayer"] = false,
        ["is_quick_play_multiplayer"] = false,
        ["is_quick_play_realms"] = false,
    };

    private void Expand(
        List<string> target,
        IReadOnlyList<ArgumentEntry> entries,
        IReadOnlyDictionary<string, bool> features,
        string kind)
    {
        foreach (var entry in entries)
        {
            if (!entry.Applies(features))
            {
                continue;
            }

            foreach (var value in entry.Values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    target.Add(value);
                }
            }
        }
    }

    /// <summary>版本 JSON 不含 <c>arguments.jvm</c> 时（1.13 之前）使用的默认 JVM 参数。</summary>
    private static void AddLegacyJvmDefaults(List<string> jvm)
    {
        if (GameEnvironment.IsWindows)
        {
            jvm.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");

            if (Environment.OSVersion.Version.Major >= 10)
            {
                jvm.Add("-Dos.name=Windows 10");
                jvm.Add("-Dos.version=10.0");
            }
        }
        else if (GameEnvironment.IsMacOs)
        {
            jvm.Add("-XstartOnFirstThread");
        }

        jvm.Add("-Djava.library.path=${natives_directory}");
        jvm.Add("-cp");
        jvm.Add("${classpath}");
    }

    private Dictionary<string, string> BuildVariables(LaunchContext context, string classpath)
    {
        var account = context.Account;
        var options = context.Options;

        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = account.Name,
            ["${auth_uuid}"] = account.Uuid,
            ["${auth_access_token}"] = account.AccessToken,
            ["${auth_session}"] = account.AccessToken,
            ["${user_type}"] = account.UserType,
            ["${user_properties}"] = "{}",
            // 微软账号才有；离线账号给空串，避免 --clientId 后面出现字面量占位符。
            ["${auth_xuid}"] = account.Xuid ?? string.Empty,
            ["${clientid}"] = account.ClientId ?? string.Empty,
            ["${version_name}"] = context.VersionId,
            ["${version_type}"] = context.Version.Type ?? "release",
            ["${game_directory}"] = context.GameDirectory,
            ["${assets_root}"] = context.AssetsRoot,
            ["${game_assets}"] = context.AssetsRoot,
            ["${assets_index_name}"] = context.AssetsIndexName ?? context.Version.Assets ?? "legacy",
            ["${natives_directory}"] = context.NativesDirectory,
            ["${library_directory}"] = context.LibrariesDirectory,
            ["${libraries_directory}"] = context.LibrariesDirectory,
            ["${classpath_separator}"] = Path.PathSeparator.ToString(),
            ["${classpath}"] = classpath,
            ["${primary_jar}"] = context.ClientJarPath,
            ["${launcher_name}"] = options.LauncherName,
            ["${launcher_version}"] = options.LauncherVersion,
            ["${resolution_width}"] = options.Width.ToString(),
            ["${resolution_height}"] = options.Height.ToString(),
            ["${quickPlayPath}"] = string.Empty,
            ["${quickPlaySingleplayer}"] = string.Empty,
            ["${quickPlayMultiplayer}"] = string.Empty,
            ["${quickPlayRealms}"] = string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(context.LogConfigPath))
        {
            variables["${path}"] = context.LogConfigPath!;
        }

        return variables;
    }

    /// <summary>逐个参数做变量替换，并对残留的占位符给出告警（宁可为空也不要把字面量传给游戏）。</summary>
    private void ReplaceVariables(List<string> arguments, Dictionary<string, string> variables)
    {
        var unresolved = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < arguments.Count; i++)
        {
            var value = arguments[i];
            if (value.IndexOf("${", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            foreach (var (key, replacement) in variables)
            {
                if (value.Contains(key, StringComparison.Ordinal))
                {
                    value = value.Replace(key, replacement, StringComparison.Ordinal);
                }
            }

            // 该参数没被任何已知变量完全替换掉 → 剩下的占位符按空串处理。
            var start = value.IndexOf("${", StringComparison.Ordinal);
            while (start >= 0)
            {
                var end = value.IndexOf('}', start);
                if (end < 0)
                {
                    break;
                }

                var token = value[start..(end + 1)];
                unresolved.Add(token);
                value = value.Replace(token, string.Empty, StringComparison.Ordinal);
                start = value.IndexOf("${", StringComparison.Ordinal);
            }

            arguments[i] = value;
        }

        if (unresolved.Count > 0)
        {
            _log.Warn($"以下变量在变量表中不存在，已按空串处理：{string.Join("，", unresolved)}");
        }
    }

    /// <summary>
    /// 按空格切分参数字符串，支持双引号分组。
    /// 老版本的 <c>minecraftArguments</c> 与用户自定义参数都走这里。
    /// </summary>
    public static List<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return result;
        }

        var buffer = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (buffer.Length > 0)
                {
                    result.Add(buffer.ToString());
                    buffer.Clear();
                }

                continue;
            }

            buffer.Append(ch);
        }

        if (buffer.Length > 0)
        {
            result.Add(buffer.ToString());
        }

        return result;
    }
}
