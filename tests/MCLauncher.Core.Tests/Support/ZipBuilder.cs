using System.IO.Compression;
using System.Text;

namespace MCLauncher.Core.Tests.Support;

/// <summary>在测试里造压缩包（带目录结构的条目）。</summary>
internal static class ZipBuilder
{
    public static void Create(string path, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    /// <summary>造一个 gzip 压缩的 level.dat（最小可用的 NBT）。</summary>
    public static void WriteLevelDat(string path, string levelName, string versionName)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        using var writer = new BinaryWriter(gzip, Encoding.UTF8);

        writer.Write((byte)10); // TAG_Compound
        WriteString(writer, string.Empty);

        writer.Write((byte)10); // Data
        WriteString(writer, "Data");

        writer.Write((byte)8); // LevelName
        WriteString(writer, "LevelName");
        WriteString(writer, levelName);

        writer.Write((byte)10); // Version
        WriteString(writer, "Version");

        writer.Write((byte)8); // Name
        WriteString(writer, "Name");
        WriteString(writer, versionName);

        writer.Write((byte)0); // End of Version
        writer.Write((byte)0); // End of Data
        writer.Write((byte)0); // End of root
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((byte)(bytes.Length >> 8));
        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>
    /// 造一个贴近真实存档的 gzip level.dat：LevelName 前面有一堆别的标签，
    /// 读取时必须逐个跳过（short/int/long/list/数组/嵌套 compound），
    /// 用来验证 gzip 流上不能 Seek 的跳过逻辑。
    /// </summary>
    public static void WriteLevelDatWithPayload(string path, string levelName, string versionName)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        using var writer = new BinaryWriter(gzip, Encoding.UTF8);

        writer.Write((byte)10); // 根 TAG_Compound
        WriteString(writer, string.Empty);

        writer.Write((byte)10); // Data
        WriteString(writer, "Data");

        writer.Write((byte)3); // DataVersion：TAG_Int
        WriteString(writer, "DataVersion");
        WriteInt32BigEndian(writer, 3465);

        writer.Write((byte)4); // Time：TAG_Long
        WriteString(writer, "Time");
        WriteInt64BigEndian(writer, 123456789);

        writer.Write((byte)1); // GameType：TAG_Byte
        WriteString(writer, "GameType");
        writer.Write((byte)0);

        writer.Write((byte)2); // Difficulty：TAG_Short
        WriteString(writer, "Difficulty");
        writer.Write((byte)0);
        writer.Write((byte)2);

        writer.Write((byte)5); // SpawnAngle：TAG_Float
        WriteString(writer, "SpawnAngle");
        writer.Write(1.0f);

        writer.Write((byte)10); // Player：嵌套 compound
        WriteString(writer, "Player");

        writer.Write((byte)5); // Health：TAG_Float
        WriteString(writer, "Health");
        writer.Write(20.0f);

        writer.Write((byte)2); // foodLevel：TAG_Short
        WriteString(writer, "foodLevel");
        writer.Write((byte)0);
        writer.Write((byte)20);

        writer.Write((byte)9); // Pos：TAG_List（3 个 double）
        WriteString(writer, "Pos");
        writer.Write((byte)6);
        WriteInt32BigEndian(writer, 3);
        writer.Write(1.5d);
        writer.Write(64.0d);
        writer.Write(-2.25d);

        writer.Write((byte)11); // UUID：TAG_IntArray
        WriteString(writer, "UUID");
        WriteInt32BigEndian(writer, 4);
        foreach (var value in new[] { 1, -2, 3, -4 })
        {
            WriteInt32BigEndian(writer, value);
        }

        writer.Write((byte)12); // LastHidden：TAG_LongArray
        WriteString(writer, "LastHidden");
        WriteInt32BigEndian(writer, 2);
        WriteInt64BigEndian(writer, 100);
        WriteInt64BigEndian(writer, 200);

        writer.Write((byte)0); // End of Player

        writer.Write((byte)8); // LevelName：TAG_String
        WriteString(writer, "LevelName");
        WriteString(writer, levelName);

        writer.Write((byte)10); // Version
        WriteString(writer, "Version");

        writer.Write((byte)3); // Id：TAG_Int
        WriteString(writer, "Id");
        WriteInt32BigEndian(writer, 3465);

        writer.Write((byte)8); // Name：TAG_String
        WriteString(writer, "Name");
        WriteString(writer, versionName);

        writer.Write((byte)0); // End of Version
        writer.Write((byte)0); // End of Data
        writer.Write((byte)0); // End of root
    }

    private static void WriteInt32BigEndian(BinaryWriter writer, int value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteInt64BigEndian(BinaryWriter writer, long value)
    {
        WriteInt32BigEndian(writer, (int)(value >> 32));
        WriteInt32BigEndian(writer, (int)value);
    }
}
