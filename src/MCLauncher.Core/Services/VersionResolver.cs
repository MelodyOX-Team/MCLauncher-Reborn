using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;

namespace MCLauncher.Core.Services;

/// <summary>
/// <c>inheritsFrom</c> 链的合并。合并顺序有硬性要求，弄反了会直接导致启动失败：
/// <list type="bullet">
/// <item><b>libraries：子在前、父在后</b>。classpath 的顺序会影响同名类的取用优先级，加载器必须排在原版之前。</item>
/// <item><b>arguments：父在前、子在后</b>。后写的 JVM/游戏参数才能覆盖前面的默认值。</item>
/// <item>标量字段（mainClass / assetIndex / downloads / javaVersion …）：子有则用子，否则用父。</item>
/// </list>
/// </summary>
public sealed class VersionResolver
{
    private readonly ILogger _log;

    public VersionResolver(ILogger log)
    {
        _log = log;
    }

    /// <summary>
    /// 展开继承链（叶 → 根），并检测循环引用。
    /// </summary>
    public IReadOnlyList<GameVersionJson> BuildChain(
        GameVersionJson leaf,
        Func<string, GameVersionJson?> parentLookup)
    {
        var chain = new List<GameVersionJson>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var current = leaf;
        while (current is not null)
        {
            var id = current.Id ?? "(未命名)";
            if (!visited.Add(id))
            {
                throw new InvalidDataException($"版本继承出现循环引用：{string.Join(" → ", visited)} → {id}");
            }

            chain.Add(current);

            if (string.IsNullOrWhiteSpace(current.InheritsFrom))
            {
                break;
            }

            var parent = parentLookup(current.InheritsFrom!);
            if (parent is null)
            {
                throw new InvalidDataException($"找不到被继承的版本：{current.InheritsFrom}（由 {id} 继承）");
            }

            current = parent;
        }

        return chain;
    }

    /// <summary>把继承链合并成一个可直接安装/启动的自洽版本 JSON。</summary>
    public GameVersionJson Resolve(GameVersionJson leaf, Func<string, GameVersionJson?> parentLookup)
    {
        var chain = BuildChain(leaf, parentLookup);
        if (chain.Count == 1)
        {
            return leaf;
        }

        _log.Trace($"合并继承链：{string.Join(" → ", chain.Select(v => v.Id))}");

        // 从根往叶合并：先以根为基底，再逐层把子覆盖上去。
        var merged = chain[^1];
        for (var i = chain.Count - 2; i >= 0; i--)
        {
            merged = Merge(merged, chain[i]);
        }

        merged.InheritsFrom = null;
        return merged;
    }

    /// <summary>把 <paramref name="child"/> 覆盖到 <paramref name="parent"/> 上。</summary>
    public static GameVersionJson Merge(GameVersionJson parent, GameVersionJson child) => new()
    {
        Id = child.Id ?? parent.Id,
        InheritsFrom = null,
        ClientVersion = child.ClientVersion ?? parent.ClientVersion,
        Jar = child.Jar ?? parent.Jar,
        MainClass = child.MainClass ?? parent.MainClass,
        Type = child.Type ?? parent.Type,
        ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
        Time = child.Time ?? parent.Time,
        MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
        Arguments = MergeArguments(parent.Arguments, child.Arguments),
        AssetIndex = child.AssetIndex ?? parent.AssetIndex,
        Assets = child.Assets ?? parent.Assets,
        JavaVersion = child.JavaVersion ?? parent.JavaVersion,
        Downloads = MergeDownloads(parent.Downloads, child.Downloads),
        Logging = child.Logging ?? parent.Logging,
        ComplianceLevel = child.ComplianceLevel != 0 ? child.ComplianceLevel : parent.ComplianceLevel,
        MinimumLauncherVersion = child.MinimumLauncherVersion != 0
            ? child.MinimumLauncherVersion
            : parent.MinimumLauncherVersion,
        Libraries = MergeLibraries(parent.Libraries, child.Libraries),
    };

    /// <summary>父在前、子在后。</summary>
    private static ArgumentsBlock? MergeArguments(ArgumentsBlock? parent, ArgumentsBlock? child)
    {
        if (parent is null)
        {
            return child;
        }

        if (child is null)
        {
            return parent;
        }

        var result = new ArgumentsBlock();
        result.Game.AddRange(parent.Game);
        result.Game.AddRange(child.Game);
        result.Jvm.AddRange(parent.Jvm);
        result.Jvm.AddRange(child.Jvm);
        return result;
    }

    /// <summary>子在前、父在后；按坐标去重，先出现的（子的）胜出。</summary>
    private static List<Library>? MergeLibraries(List<Library>? parent, List<Library>? child)
    {
        if (child is null || child.Count == 0)
        {
            return parent;
        }

        if (parent is null || parent.Count == 0)
        {
            return child;
        }

        var result = new List<Library>(child.Count + parent.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in child)
        {
            if (seen.Add(library.DedupKey()))
            {
                result.Add(library);
            }
        }

        foreach (var library in parent)
        {
            if (seen.Add(library.DedupKey()))
            {
                result.Add(library);
            }
        }

        return result;
    }

    private static VersionDownloads? MergeDownloads(VersionDownloads? parent, VersionDownloads? child)
    {
        if (parent is null)
        {
            return child;
        }

        if (child is null)
        {
            return parent;
        }

        return new VersionDownloads
        {
            Client = child.Client ?? parent.Client,
            Server = child.Server ?? parent.Server,
            ClientMappings = child.ClientMappings ?? parent.ClientMappings,
            ServerMappings = child.ServerMappings ?? parent.ServerMappings,
        };
    }
}
