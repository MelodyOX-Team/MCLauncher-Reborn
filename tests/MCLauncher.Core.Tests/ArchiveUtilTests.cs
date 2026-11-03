using System.IO.Compression;
using System.Text;
using MCLauncher.Core.Utils;
using MCLauncher.Core.Tests.Support;

namespace MCLauncher.Core.Tests;

public sealed class ArchiveUtilTests
{
    private static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    [Fact]
    public void 解压全部条目()
    {
        using var temp = new TempDirectory();
        var zip = temp.File("a.zip");
        CreateZip(zip, ("x.txt", "one"), ("sub/y.txt", "two"));

        var dest = temp.File("out");
        ArchiveUtil.ExtractZip(zip, dest);

        Assert.Equal("one", File.ReadAllText(Path.Combine(dest, "x.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(dest, "sub", "y.txt")));
    }

    [Fact]
    public void natives解压时排除META_INF()
    {
        using var temp = new TempDirectory();
        var zip = temp.File("natives.zip");
        CreateZip(zip, ("lwjgl.dll", "bin"), ("META-INF/MANIFEST.MF", "sig"));

        var dest = temp.File("natives");
        ArchiveUtil.ExtractZip(zip, dest, new[] { "META-INF/" });

        Assert.True(File.Exists(Path.Combine(dest, "lwjgl.dll")));
        Assert.False(Directory.Exists(Path.Combine(dest, "META-INF")));
    }

    [Fact]
    public void 越出目标目录的条目会被拒绝()
    {
        using var temp = new TempDirectory();
        var zip = temp.File("evil.zip");
        CreateZip(zip, ("../../evil.txt", "pwned"));

        var dest = temp.File("out");
        Assert.Throws<InvalidDataException>(() => ArchiveUtil.ExtractZip(zip, dest));
        Assert.False(File.Exists(Path.Combine(temp.Path, "evil.txt")));
    }

    [Fact]
    public void 可读取单个条目文本()
    {
        using var temp = new TempDirectory();
        var zip = temp.File("pack.zip");
        CreateZip(zip, ("mcbbs.packmeta", "{\"name\":\"x\"}"), ("mods/a.jar", "jar"));

        Assert.Equal("{\"name\":\"x\"}", ArchiveUtil.ReadTextEntry(zip, "mcbbs.packmeta"));
        Assert.Null(ArchiveUtil.ReadTextEntry(zip, "missing.json"));
    }

    [Fact]
    public void 可按文件名在后缀层级中查找()
    {
        using var temp = new TempDirectory();
        var zip = temp.File("pack.zip");
        CreateZip(zip, ("wrapper/.minecraft/versions/1.20.4/1.20.4.json", "{}"));

        var found = ArchiveUtil.FindEntryName(zip, "1.20.4.json");
        Assert.NotNull(found);
        Assert.True(ArchiveUtil.ContainsFileName(zip, "1.20.4.json"));
        Assert.True(ArchiveUtil.ContainsSuffix(zip, "/versions/1.20.4/1.20.4.json"));
        Assert.False(ArchiveUtil.ContainsFileName(zip, "nope.json"));
    }

    [Fact]
    public void 识别zip魔数()
    {
        using var temp = new TempDirectory();
        var zip = temp.File("a.zip");
        CreateZip(zip, ("x.txt", "one"));
        var notZip = temp.File("b.txt");
        File.WriteAllText(notZip, "plain");

        Assert.True(ArchiveUtil.LooksLikeZip(zip));
        Assert.False(ArchiveUtil.LooksLikeZip(notZip));
    }

    [Fact]
    public void 写入被拒绝的条目会被跳过并记录()
    {
        using var temp = new TempDirectory();
        var zip = temp.File("jdk.zip");
        CreateZip(zip, ("bin/java.exe", "exe"), ("bin/msvcp140.dll", "dll"));

        var dest = temp.File("out");

        // 预先在目标路径建同名目录，写文件必然失败，模拟安全软件拦截 DLL 落盘。
        Directory.CreateDirectory(Path.Combine(dest, "bin", "msvcp140.dll"));

        var blocked = ArchiveUtil.ExtractZipSkippingBlocked(zip, dest);

        Assert.Equal(new[] { "bin/msvcp140.dll" }, blocked);
        Assert.True(File.Exists(Path.Combine(dest, "bin", "java.exe")));
    }
}
