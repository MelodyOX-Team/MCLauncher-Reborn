using System.IO.Compression;

namespace MCLauncher.Core.Utils;

/// <summary>
/// 压缩包处理。Minecraft 用到的场景：natives 解压（需排除 META-INF）、整合包导入、单条目读取。
/// 只使用 BCL 自带的 <see cref="ZipArchive"/>，不引入第三方解压库。
/// </summary>
public static class ArchiveUtil
{
    /// <summary>单个压缩包允许解压出的最大字节数（按条目声明的大小累计），防止 zip 炸弹撑爆磁盘。</summary>
    private const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>单个压缩包允许的条目数。</summary>
    private const int MaxEntryCount = 100_000;

    /// <summary>常见压缩包魔数判断（zip 的本地文件头以 PK\x03\x04 开头）。</summary>
    public static bool LooksLikeZip(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[4];
            return stream.Read(header, 0, 4) == 4
                   && header[0] == 0x50 && header[1] == 0x4B
                   && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>列出压缩包内全部条目名（正斜杠分隔）。</summary>
    public static IReadOnlyList<string> ListEntries(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>
    /// 解压整个压缩包到目录。
    /// <paramref name="excludePrefixes"/> 命中的条目会被跳过（natives 必须排除 <c>META-INF/</c>，
    /// 否则会覆盖签名文件导致校验失败）。
    /// </summary>
    public static void ExtractZip(
        string archivePath,
        string destinationDirectory,
        IReadOnlyCollection<string>? excludePrefixes = null,
        bool overwrite = true)
    {
        PathUtil.EnsureDirectory(destinationDirectory);

        using var archive = ZipFile.OpenRead(archivePath);
        var extracted = new List<string>();
        var totalBytes = 0L;
        var entryCount = 0;

        try
        {
            foreach (var entry in archive.Entries)
            {
                if (ShouldSkip(entry, excludePrefixes))
                {
                    continue;
                }

                GuardLimits(entry, ref entryCount, ref totalBytes);
                extracted.Add(ExtractEntry(entry, destinationDirectory, overwrite));
            }
        }
        catch
        {
            CleanupExtracted(extracted);
            throw;
        }
    }

    /// <summary>只解压满足谓词的条目（整合包按需取用）。</summary>
    public static void ExtractEntries(
        string archivePath,
        string destinationDirectory,
        Func<string, bool> predicate,
        bool overwrite = true)
    {
        PathUtil.EnsureDirectory(destinationDirectory);

        using var archive = ZipFile.OpenRead(archivePath);
        var extracted = new List<string>();
        var totalBytes = 0L;
        var entryCount = 0;

        try
        {
            foreach (var entry in archive.Entries)
            {
                if (ShouldSkipBasic(entry) || !predicate(entry.FullName))
                {
                    continue;
                }

                GuardLimits(entry, ref entryCount, ref totalBytes);
                extracted.Add(ExtractEntry(entry, destinationDirectory, overwrite));
            }
        }
        catch
        {
            CleanupExtracted(extracted);
            throw;
        }
    }

    /// <summary>
    /// 解压整个压缩包；写入被安全软件拦截（例如防护规则禁止向该目录写 DLL）的条目跳过并记录，
    /// 由调用方探测结果是否仍然可用。返回被跳过的条目名。
    /// </summary>
    public static IReadOnlyList<string> ExtractZipSkippingBlocked(string archivePath, string destinationDirectory)
    {
        PathUtil.EnsureDirectory(destinationDirectory);
        var blocked = new List<string>();
        var extracted = new List<string>();
        var totalBytes = 0L;
        var entryCount = 0;

        using var archive = ZipFile.OpenRead(archivePath);

        try
        {
            foreach (var entry in archive.Entries)
            {
                if (ShouldSkipBasic(entry))
                {
                    continue;
                }

                GuardLimits(entry, ref entryCount, ref totalBytes);

                try
                {
                    extracted.Add(ExtractEntry(entry, destinationDirectory, overwrite: true));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    blocked.Add(entry.FullName);
                }
            }
        }
        catch
        {
            CleanupExtracted(extracted);
            throw;
        }

        return blocked;
    }

    /// <summary>把压缩包内某个条目解压到指定文件路径。</summary>
    public static bool ExtractEntryTo(string archivePath, string entryName, string destinationFile, bool overwrite = true)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(Normalize(e.FullName), Normalize(entryName), StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return false;
        }

        PathUtil.EnsureParentDirectory(destinationFile);
        if (File.Exists(destinationFile) && !overwrite)
        {
            return true;
        }

        entry.ExtractToFile(destinationFile, overwrite);
        return true;
    }

    /// <summary>读取压缩包内某个条目的文本内容；条目不存在时返回 null。</summary>
    public static string? ReadTextEntry(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = FindEntry(archive, entryName);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>读取压缩包内某个条目的字节内容；条目不存在时返回 null。</summary>
    public static byte[]? ReadBytesEntry(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = FindEntry(archive, entryName);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// 在压缩包内按「路径后缀 + 文件名」模糊查找第一个匹配的条目。
    /// 整合包结构千奇百怪（一层套一层目录），用后缀匹配最稳。
    /// </summary>
    public static string? FindEntryName(string archivePath, string fileName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => e.FullName)
            .FirstOrDefault(name => Path.GetFileName(name).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>压缩包内是否存在某个文件名（不区分目录层级）。</summary>
    public static bool ContainsFileName(string archivePath, string fileName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Any(e => e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>压缩包内是否存在某个路径后缀（如 <c>/mods/foo.jar</c>）。</summary>
    public static bool ContainsSuffix(string archivePath, string suffix)
    {
        var normalized = Normalize(suffix);
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Any(e => Normalize(e.FullName).EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryName)
    {
        var normalized = Normalize(entryName);
        return archive.Entries.FirstOrDefault(e =>
            string.Equals(Normalize(e.FullName), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldSkip(ZipArchiveEntry entry, IReadOnlyCollection<string>? excludePrefixes)
    {
        if (ShouldSkipBasic(entry))
        {
            return true;
        }

        if (excludePrefixes is null || excludePrefixes.Count == 0)
        {
            return false;
        }

        var name = Normalize(entry.FullName);
        return excludePrefixes.Any(prefix => name.StartsWith(Normalize(prefix), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>目录条目与空文件名条目直接跳过。</summary>
    private static bool ShouldSkipBasic(ZipArchiveEntry entry) =>
        string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/');

    private static string ExtractEntry(ZipArchiveEntry entry, string destinationDirectory, bool overwrite)
    {
        var target = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));

        // 防目录穿越：压缩包里的 ../../ 不能写到目标目录之外。
        if (!PathUtil.IsParentOf(destinationDirectory, target))
        {
            throw new InvalidDataException($"压缩包条目试图越出目标目录：{entry.FullName}");
        }

        PathUtil.EnsureParentDirectory(target);

        // 安全软件可能在文件刚落盘时独占扫描，稍等重试几次再放弃。
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                entry.ExtractToFile(target, overwrite);
                return target;
            }
            catch (Exception ex) when (attempt < 3 && (ex is IOException or UnauthorizedAccessException))
            {
                System.Threading.Thread.Sleep(TimeSpan.FromMilliseconds(150 * attempt));
            }
        }
    }

    /// <summary>累计条目数与声明解压大小；超过安全上限时直接中止。</summary>
    private static void GuardLimits(ZipArchiveEntry entry, ref int entryCount, ref long totalBytes)
    {
        entryCount++;
        totalBytes += entry.Length;

        if (entryCount > MaxEntryCount)
        {
            throw new InvalidDataException($"压缩包条目数超过上限（{MaxEntryCount}）：{entry.FullName}");
        }

        if (totalBytes > MaxUncompressedBytes)
        {
            throw new InvalidDataException($"压缩包解压后超过大小上限（{MaxUncompressedBytes} 字节）：{entry.FullName}");
        }
    }

    /// <summary>解压失败时删除本次已经写出的文件，避免留下半成品。</summary>
    private static void CleanupExtracted(IEnumerable<string> files)
    {
        foreach (var path in files)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败无法回滚，至少不要掩盖原始异常。
            }
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
