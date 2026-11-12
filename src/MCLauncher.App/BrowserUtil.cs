using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MCLauncher.App;

/// <summary>
/// 以默认浏览器的无痕（隐私）窗口打开链接；识别不了默认浏览器、
/// 浏览器不支持无痕模式或启动失败时，退回系统默认方式打开。
/// </summary>
public static class BrowserUtil
{
    /// <summary>
    /// 常见浏览器的可执行文件名。注册表命令里的路径常常不带引号，
    /// 只能靠程序名定位路径的结束位置。
    /// </summary>
    private static readonly string[] BrowserExecutableNames =
    {
        "firefox.exe", "msedge.exe", "chrome.exe", "brave.exe", "vivaldi.exe", "opera.exe",
        "chromium.exe", "360chrome.exe", "360se.exe", "qqbrowser.exe", "sogouexplorer.exe",
        "liebao.exe", "2345explorer.exe",
    };

    /// <summary>优先用默认浏览器的无痕窗口打开；失败时调用 <paramref name="onFallback"/> 并退回普通打开。</summary>
    public static void OpenIncognito(string url, Action<Exception>? onFallback = null)
    {
        try
        {
            var browser = GetDefaultBrowserPath();
            var flag = GetIncognitoFlag(browser);
            if (browser is not null && flag is not null)
            {
                // 用 ArgumentList 逐项传递：网址里可能带空格或引号，
                // 拼成一个 Arguments 字符串会让它们被当成额外的参数分隔符。
                var startInfo = new ProcessStartInfo(browser) { UseShellExecute = false };
                startInfo.ArgumentList.Add(flag);
                startInfo.ArgumentList.Add(url);
                Process.Start(startInfo);
                return;
            }
        }
        catch (Exception ex)
        {
            onFallback?.Invoke(ex);
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>从注册表读取默认浏览器可执行文件路径；读不到返回 null。</summary>
    private static string? GetDefaultBrowserPath()
    {
        var progId = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\URLAssociations\http\UserChoice",
            "ProgId", null) as string;
        if (string.IsNullOrWhiteSpace(progId))
        {
            return null;
        }

        var command = Registry.GetValue(
            $@"HKEY_CLASSES_ROOT\{progId}\shell\open\command", null, null) as string;
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        // 命令通常形如 "C:\...\chrome.exe" -- "%1"，或（不带引号）C:\Program Files\...\firefox.exe -osint -url "%1"。
        var byName = FindPathByExecutableName(command);
        if (byName is not null)
        {
            return byName;
        }

        // 兜底：引号内的路径。
        var firstQuote = command.IndexOf('"');
        var secondQuote = firstQuote >= 0 ? command.IndexOf('"', firstQuote + 1) : -1;
        if (firstQuote >= 0 && secondQuote > firstQuote)
        {
            return command[(firstQuote + 1)..secondQuote];
        }

        // 都没有时取第一个空格前的部分。
        var space = command.IndexOf(' ');
        return (space > 0 ? command[..space] : command).Trim();
    }

    /// <summary>
    /// 按已知浏览器可执行文件名定位命令里的路径并截取到文件名结尾；
    /// 找不到返回 null。这样即使路径带空格且没有引号也不会被截成 <c>C:\Program</c>。
    /// </summary>
    private static string? FindPathByExecutableName(string command)
    {
        var matchIndex = -1;
        var matchEnd = -1;

        foreach (var name in BrowserExecutableNames)
        {
            var index = command.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            // 取最靠前的一次匹配：路径总在命令开头，靠后的匹配可能出现在参数里。
            if (matchIndex < 0 || index < matchIndex)
            {
                matchIndex = index;
                matchEnd = index + name.Length;
            }
        }

        if (matchIndex < 0)
        {
            return null;
        }

        // 带引号的命令会连引号一起截到，去掉首尾引号才是真实路径。
        return command[..matchEnd].Trim().Trim('"').Trim();
    }

    /// <summary>按浏览器程序名给出无痕参数；不认识的浏览器返回 null（退回普通打开）。</summary>
    private static string? GetIncognitoFlag(string? browserPath)
    {
        var name = Path.GetFileNameWithoutExtension(browserPath ?? string.Empty)?.ToLowerInvariant();
        return name switch
        {
            "msedge" => "--inprivate",
            "firefox" => "-private-window",
            "opera" => "--private",
            // Chrome 系（含国产 Chromium 套壳）基本都兼容 --incognito。
            "chrome" or "brave" or "vivaldi" or "chromium"
                or "360chrome" or "360se" or "qqbrowser" or "sogouexplorer"
                or "liebao" or "2345explorer" => "--incognito",
            _ => null,
        };
    }
}
