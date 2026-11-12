using System.IO.Compression;

namespace MCLauncher.Core.Utils;

/// <summary>
/// 极简 NBT 读取器，只服务于"读存档名与版本"这一个用途。
/// <para>
/// 做法是流式跳过不需要的标签，只把 <c>Data/LevelName</c> 与 <c>Data/Version/Name</c> 取出来，
/// 因此不需要建立完整对象树 —— 存档的 level.dat 动辄几十 KB，全量建树既慢又没必要。
/// </para>
/// <para>level.dat 是 **gzip 压缩** 的 NBT，需要先识别魔数再解压。</para>
/// </summary>
public static class NbtReader
{
    /// <summary>
    /// NBT 嵌套深度上限。损坏或恶意构造的存档可以嵌套上万层 compound/list，
    /// 递归不设限会直接抛 StackOverflowException（进程无法捕获），因此超限一律按不可解析处理。
    /// </summary>
    internal const int MaxDepth = 512;

    private static readonly byte[] GzipMagic = { 0x1F, 0x8B };

    /// <summary>读取存档的显示名与游戏版本；失败时返回 false（损坏的存档不应让界面崩掉）。</summary>
    public static bool TryReadLevelDat(string levelDatPath, out string? levelName, out string? versionName)
    {
        levelName = null;
        versionName = null;

        if (!File.Exists(levelDatPath))
        {
            return false;
        }

        try
        {
            using var file = File.OpenRead(levelDatPath);
            using var buffered = new BufferedStream(file);

            Stream stream = buffered;
            GZipStream? gzip = null;

            var header = new byte[2];
            if (buffered.Read(header, 0, 2) == 2
                && header[0] == GzipMagic[0] && header[1] == GzipMagic[1])
            {
                buffered.Seek(0, SeekOrigin.Begin);
                gzip = new GZipStream(buffered, CompressionMode.Decompress);
                stream = gzip;
            }
            else
            {
                buffered.Seek(0, SeekOrigin.Begin);
            }

            try
            {
                var reader = new BinaryReader(stream, System.Text.Encoding.UTF8);

                var rootType = reader.ReadByte();
                if (rootType != TagCompound)
                {
                    return false;
                }

                _ = ReadString(reader); // 根标签名（通常是空串）
                if (!ReadCompound(reader, string.Empty, ref levelName, ref versionName, depth: 0))
                {
                    return false;
                }

                return levelName is not null || versionName is not null;
            }
            finally
            {
                gzip?.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    internal const byte TagEnd = 0;
    internal const byte TagByte = 1;
    internal const byte TagShort = 2;
    internal const byte TagInt = 3;
    internal const byte TagLong = 4;
    internal const byte TagFloat = 5;
    internal const byte TagDouble = 6;
    internal const byte TagByteArray = 7;
    internal const byte TagString = 8;
    internal const byte TagList = 9;
    internal const byte TagCompound = 10;
    internal const byte TagIntArray = 11;
    internal const byte TagLongArray = 12;

    private static bool ReadCompound(
        BinaryReader reader,
        string prefix,
        ref string? levelName,
        ref string? versionName,
        int depth)
    {
        while (true)
        {
            var type = reader.ReadByte();
            if (type == TagEnd)
            {
                // 这里只表示"结构完整地读完了"，是否命中字段由调用方判断。
                return true;
            }

            var name = ReadString(reader);
            var path = prefix.Length == 0 ? name : $"{prefix}/{name}";

            if (type == TagString && (path is "Data/LevelName" or "LevelName"))
            {
                levelName = ReadString(reader);
                continue;
            }

            if (type == TagCompound)
            {
                // 深度超限直接放弃整个存档，避免递归继续加深导致栈溢出。
                if (depth >= MaxDepth)
                {
                    return false;
                }

                if (!ReadCompound(reader, path, ref levelName, ref versionName, depth + 1))
                {
                    return false;
                }

                continue;
            }

            if (type == TagString && (path is "Data/Version/Name" or "Version/Name"))
            {
                versionName = ReadString(reader);
                continue;
            }

            if (!Skip(reader, type))
            {
                return false;
            }
        }
    }

    /// <summary>跳过指定类型的负载。返回 false 表示数据已损坏或嵌套过深。</summary>
    internal static bool Skip(BinaryReader reader, byte type, int depth = 0)
    {
        switch (type)
        {
            case TagByte:
                reader.ReadByte();
                return true;

            case TagShort:
                DiscardBytes(reader, 2);
                return true;

            case TagInt:
            case TagFloat:
                DiscardBytes(reader, 4);
                return true;

            case TagLong:
            case TagDouble:
                DiscardBytes(reader, 8);
                return true;

            case TagByteArray:
                {
                    var length = ReadInt32BigEndian(reader);
                    if (length < 0)
                    {
                        return false;
                    }

                    DiscardBytes(reader, length);
                    return true;
                }

            case TagString:
                SeekString(reader);
                return true;

            case TagList:
                {
                    // 列表元素也可能是 compound / 嵌套 list，深度超限时按损坏处理。
                    if (depth >= MaxDepth)
                    {
                        return false;
                    }

                    var elementType = reader.ReadByte();
                    var length = ReadInt32BigEndian(reader);
                    if (length < 0)
                    {
                        return false;
                    }

                    for (var i = 0; i < length; i++)
                    {
                        if (elementType == TagEnd)
                        {
                            // TAG_End 列表（长度通常为 0），无需再读。
                            break;
                        }

                        if (!Skip(reader, elementType, depth + 1))
                        {
                            return false;
                        }
                    }

                    return true;
                }

            case TagCompound:
                return SkipCompound(reader, depth);

            case TagIntArray:
                {
                    var length = ReadInt32BigEndian(reader);
                    if (length < 0)
                    {
                        return false;
                    }

                    DiscardBytes(reader, length * 4L);
                    return true;
                }

            case TagLongArray:
                {
                    var length = ReadInt32BigEndian(reader);
                    if (length < 0)
                    {
                        return false;
                    }

                    DiscardBytes(reader, length * 8L);
                    return true;
                }

            default:
                return false;
        }
    }

    private static bool SkipCompound(BinaryReader reader, int depth)
    {
        if (depth >= MaxDepth)
        {
            return false;
        }

        while (true)
        {
            var type = reader.ReadByte();
            if (type == TagEnd)
            {
                return true;
            }

            SeekString(reader); // 标签名
            if (!Skip(reader, type, depth + 1))
            {
                return false;
            }
        }
    }

    internal static string ReadString(BinaryReader reader)
    {
        var length = ReadUInt16BigEndian(reader);
        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = reader.ReadBytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static void SeekString(BinaryReader reader)
    {
        var length = ReadUInt16BigEndian(reader);
        DiscardBytes(reader, length);
    }

    /// <summary>
    /// 跳过流中指定长度的字节。level.dat 外面套了 gzip，GZipStream 不支持 Seek，
    /// 只能分块读出来丢弃；读不动（数据被截断）时按损坏处理。
    /// </summary>
    private static void DiscardBytes(BinaryReader reader, long count)
    {
        if (count <= 0)
        {
            return;
        }

        var buffer = new byte[Math.Min(count, 81920)];
        while (count > 0)
        {
            var read = reader.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            count -= read;
        }
    }

    /// <summary>NBT 的所有多字节整数都是大端序，不能直接用 BinaryReader 的小端读取。</summary>
    internal static ushort ReadUInt16BigEndian(BinaryReader reader)
    {
        var high = reader.ReadByte();
        var low = reader.ReadByte();
        return (ushort)((high << 8) | low);
    }

    internal static int ReadInt32BigEndian(BinaryReader reader)
    {
        var b0 = reader.ReadByte();
        var b1 = reader.ReadByte();
        var b2 = reader.ReadByte();
        var b3 = reader.ReadByte();
        return (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
    }
}
