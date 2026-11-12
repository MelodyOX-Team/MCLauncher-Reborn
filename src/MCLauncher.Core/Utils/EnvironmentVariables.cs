using System.Diagnostics;

namespace MCLauncher.Core.Utils;

/// <summary>
/// 把设置页里的「名称=值」多行文本解析成环境变量表。
/// 空行与 <c>#</c> 开头的注释会被跳过；值两端的双引号会被去掉。
/// </summary>
public static class EnvironmentVariables
{
    public static Dictionary<string, string> Parse(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                Debug.WriteLine($"已忽略环境变量（缺少名称=值格式）：{line}");
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            // 在这里就挡掉非法名称/值：ProcessStartInfo 要到启动进程时才报错，
            // 那时错误信息完全指不到是哪一行配置写错了。
            if (name.Length == 0)
            {
                Debug.WriteLine($"已忽略环境变量（名称为空）：{line}");
                continue;
            }

            if (name.Contains('=') || name.Contains('\0'))
            {
                Debug.WriteLine($"已忽略环境变量（名称含非法字符）：{name}");
                continue;
            }

            if (value.Contains('\0'))
            {
                Debug.WriteLine($"已忽略环境变量（值含 NUL 字符）：{name}");
                continue;
            }

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            result[name] = value;
        }

        return result;
    }
}
