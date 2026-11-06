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
    /// <summary>优先用默认浏览器的无痕窗口打开；失败时调用 <paramref name="onFallback"/> 并退回普通打开。</summary>
    public static void OpenIncognito(string url, Action<Exception>? onFallback = null)
    {
        try
        {
            var browser = GetDefaultBrowserPath();
            var flag = GetIncognitoFlag(browser);
            if (browser is not null && flag is not null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = browser,
                    Arguments = $"{flag} \"{url}\"",
                    UseShellExecute = false,
                });
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

        // 命令通常形如 "C:\...\chrome.exe" -- "%1"，取引号内的 exe 路径。
        var firstQuote = command.IndexOf('"');
        var secondQuote = firstQuote >= 0 ? command.IndexOf('"', firstQuote + 1) : -1;
        if (firstQuote >= 0 && secondQuote > firstQuote)
        {
            return command[(firstQuote + 1)..secondQuote];
        }

        // 无引号时取第一个空格前的部分。
        var space = command.IndexOf(' ');
        return (space > 0 ? command[..space] : command).Trim();
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
