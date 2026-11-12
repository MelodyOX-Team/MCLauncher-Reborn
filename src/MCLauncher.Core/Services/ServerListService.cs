using System.IO;
using System.Text;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Models;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Services;

/// <summary>
/// 实例目录下 <c>servers.dat</c> 的读写。
/// <para>
/// 该文件是 <b>未压缩</b>的 NBT：根 TAG_Compound 里只有一个 <c>servers</c> TAG_List，
/// 每个元素是含 <c>name</c> / <c>ip</c> / <c>icon</c> / <c>acceptTextures</c> 的 compound。
/// 结构简单且字段固定，因此这里直接按二进制读写，不引入通用 NBT 树。
/// </para>
/// </summary>
public sealed class ServerListService
{
    public const string FileName = "servers.dat";

    private readonly ILogger _log;

    public ServerListService(ILogger log)
    {
        _log = log;
    }

    public static string GetPath(string instanceDirectory) => Path.Combine(instanceDirectory, FileName);

    /// <summary>读取服务器列表；文件不存在或损坏时返回空列表。</summary>
    public IReadOnlyList<MinecraftServerEntry> Load(string instanceDirectory)
    {
        var path = GetPath(instanceDirectory);
        if (!File.Exists(path))
        {
            return Array.Empty<MinecraftServerEntry>();
        }

        var servers = LoadBigEndian(path);

        // 旧版本启动器曾用小端写入，游戏无法读取。读出来后立即转换成标准大端格式；
        // Save 内部对"结构无法解析且不是旧版格式"的文件有覆盖保护，不会误删用户数据。
        if (servers.Count == 0 && IsLegacyLittleEndian(path))
        {
            servers = LoadLegacyLittleEndian(path);
            if (servers.Count > 0)
            {
                if (Save(instanceDirectory, servers))
                {
                    _log.Info(
                        $"检测到旧版小端 servers.dat，已转换 {servers.Count} 条为标准格式：{path}");
                }
                else
                {
                    _log.Warn(
                        $"检测到旧版小端 servers.dat，已读取 {servers.Count} 条，但转换写入失败：{path}");
                }
            }
        }

        return servers;
    }

    private IReadOnlyList<MinecraftServerEntry> LoadBigEndian(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadByte() != NbtReader.TagCompound)
            {
                return Array.Empty<MinecraftServerEntry>();
            }

            _ = NbtReader.ReadString(reader); // 根标签名（通常为空串）

            while (true)
            {
                var type = reader.ReadByte();
                if (type == NbtReader.TagEnd)
                {
                    return Array.Empty<MinecraftServerEntry>();
                }

                var name = NbtReader.ReadString(reader);
                if (type == NbtReader.TagList && name == "servers")
                {
                    return ReadServers(reader);
                }

                if (!NbtReader.Skip(reader, type))
                {
                    return Array.Empty<MinecraftServerEntry>();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
        {
            _log.Warn($"读取服务器列表失败：{path}", ex);
            return Array.Empty<MinecraftServerEntry>();
        }
    }

    /// <summary>只检查文件结构里是否存在 <c>servers</c> 列表，不解析具体条目。</summary>
    private static bool HasReadableServersList(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadByte() != NbtReader.TagCompound)
            {
                return false;
            }

            _ = NbtReader.ReadString(reader); // 根标签名

            while (true)
            {
                var type = reader.ReadByte();
                if (type == NbtReader.TagEnd)
                {
                    return false;
                }

                var name = NbtReader.ReadString(reader);
                if (type == NbtReader.TagList && name == "servers")
                {
                    return true;
                }

                if (!NbtReader.Skip(reader, type))
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>识别旧版启动器写入的小端 NBT 文件头。</summary>
    private static bool IsLegacyLittleEndian(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[13];
            if (stream.Read(header, 0, header.Length) != header.Length)
            {
                return false;
            }

            return header[0] == NbtReader.TagCompound
                   && header[1] == 0
                   && header[2] == 0
                   && header[3] == NbtReader.TagList
                   && header[4] == 7
                   && header[5] == 0
                   && Encoding.ASCII.GetString(header, 6, 7) == "servers";
        }
        catch (IOException)
        {
            return false;
        }
    }

    private IReadOnlyList<MinecraftServerEntry> LoadLegacyLittleEndian(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadByte() != NbtReader.TagCompound)
            {
                return Array.Empty<MinecraftServerEntry>();
            }

            _ = ReadLegacyString(reader);

            var type = reader.ReadByte();
            var name = ReadLegacyString(reader);
            if (type != NbtReader.TagList || name != "servers")
            {
                return Array.Empty<MinecraftServerEntry>();
            }

            var elementType = reader.ReadByte();
            var count = reader.ReadInt32();
            if (elementType != NbtReader.TagCompound || count <= 0 || count > 4096)
            {
                return Array.Empty<MinecraftServerEntry>();
            }

            var servers = new List<MinecraftServerEntry>(count);
            for (var i = 0; i < count; i++)
            {
                if (!TryReadLegacyServer(reader, out var server))
                {
                    break;
                }

                servers.Add(server!);
            }

            return servers;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
        {
            _log.Warn($"读取旧版服务器列表失败：{path}", ex);
            return Array.Empty<MinecraftServerEntry>();
        }
    }

    private static bool TryReadLegacyServer(BinaryReader reader, out MinecraftServerEntry? server)
    {
        server = new MinecraftServerEntry();

        while (true)
        {
            var type = reader.ReadByte();
            if (type == NbtReader.TagEnd)
            {
                return true;
            }

            var name = ReadLegacyString(reader);

            if (type == NbtReader.TagString)
            {
                var value = ReadLegacyString(reader);
                switch (name)
                {
                    case "name":
                        server.Name = value;
                        break;
                    case "ip":
                        server.Address = value;
                        break;
                    case "icon":
                        server.IconBase64 = value;
                        break;
                }

                continue;
            }

            if (type == NbtReader.TagByte)
            {
                var value = reader.ReadByte();
                if (name == "acceptTextures")
                {
                    server.ResourcePolicy = value switch
                    {
                        1 => ServerResourcePackPolicy.Always,
                        2 => ServerResourcePackPolicy.Never,
                        _ => ServerResourcePackPolicy.Ask,
                    };
                }

                continue;
            }

            return false;
        }
    }

    private static string ReadLegacyString(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        if (length == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    /// <summary>写入服务器列表。列表为空时会写出空列表而不是删除文件，保证删除操作生效。</summary>
    public bool Save(string instanceDirectory, IReadOnlyList<MinecraftServerEntry> servers)
    {
        var path = GetPath(instanceDirectory);
        var temp = path + ".tmp";

        try
        {
            // 现有文件存在但结构读不出来时，说明用户手里是一份我们无法安全解析的列表；
            // 此时覆盖会永久丢失原数据，因此直接放弃写入（旧版小端格式仍允许转换）。
            if (File.Exists(path)
                && new FileInfo(path).Length > 0
                && !HasReadableServersList(path)
                && !IsLegacyLittleEndian(path))
            {
                _log.Warn($"现有 servers.dat 无法解析，为避免覆盖原始内容已取消写入：{path}");
                return false;
            }

            Directory.CreateDirectory(instanceDirectory);

            using (var stream = File.Create(temp))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(NbtReader.TagCompound);
                WriteString(writer, string.Empty);

                writer.Write(NbtReader.TagList);
                WriteString(writer, "servers");
                writer.Write(NbtReader.TagCompound); // 元素类型
                WriteInt32(writer, servers.Count);

                foreach (var server in servers)
                {
                    writer.Write(NbtReader.TagString);
                    WriteString(writer, "name");
                    WriteString(writer, server.Name.Trim());

                    writer.Write(NbtReader.TagString);
                    WriteString(writer, "ip");
                    WriteString(writer, server.Address.Trim());

                    if (!string.IsNullOrWhiteSpace(server.IconBase64))
                    {
                        writer.Write(NbtReader.TagString);
                        WriteString(writer, "icon");
                        WriteString(writer, server.IconBase64!);
                    }

                    // TODO(待实测)：acceptTextures 是 TAG_Byte，但 1/2 分别代表 Always/Never 的
                    // 映射尚未用实机存档确认；在确证前沿用现有编码，且不再自动重写用户的 servers.dat。
                    if (server.ResourcePolicy != ServerResourcePackPolicy.Ask)
                    {
                        writer.Write(NbtReader.TagByte);
                        WriteString(writer, "acceptTextures");
                        writer.Write((byte)server.ResourcePolicy);
                    }

                    writer.Write(NbtReader.TagEnd);
                }

                writer.Write(NbtReader.TagEnd); // 根 compound 结束
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"写入服务器列表失败：{path}", ex);
            TryDelete(temp);
            return false;
        }
    }

    private static IReadOnlyList<MinecraftServerEntry> ReadServers(BinaryReader reader)
    {
        var elementType = reader.ReadByte();
        var count = NbtReader.ReadInt32BigEndian(reader);

        // 损坏文件里的计数可能是任意值，先拦掉不合理的数量，避免按它分配内存。
        if (count <= 0 || count > 4096)
        {
            return Array.Empty<MinecraftServerEntry>();
        }

        var servers = new List<MinecraftServerEntry>(count);

        for (var i = 0; i < count; i++)
        {
            if (elementType != NbtReader.TagCompound || !TryReadServer(reader, out var server))
            {
                break;
            }

            servers.Add(server!);
        }

        return servers;
    }

    private static bool TryReadServer(BinaryReader reader, out MinecraftServerEntry? server)
    {
        server = new MinecraftServerEntry();

        while (true)
        {
            var type = reader.ReadByte();
            if (type == NbtReader.TagEnd)
            {
                return true;
            }

            var name = NbtReader.ReadString(reader);

            if (type == NbtReader.TagString)
            {
                var value = NbtReader.ReadString(reader);
                switch (name)
                {
                    case "name":
                        server.Name = value;
                        break;
                    case "ip":
                        server.Address = value;
                        break;
                    case "icon":
                        server.IconBase64 = value;
                        break;
                }

                continue;
            }

            if (type == NbtReader.TagByte)
            {
                var value = reader.ReadByte();
                if (name == "acceptTextures")
                {
                    server.ResourcePolicy = value switch
                    {
                        1 => ServerResourcePackPolicy.Always,
                        2 => ServerResourcePackPolicy.Never,
                        _ => ServerResourcePackPolicy.Ask,
                    };
                }

                continue;
            }

            if (!NbtReader.Skip(reader, type))
            {
                return false;
            }
        }
    }

    /// <summary>NBT 的字符串长度与列表计数都是大端序，不能用 BinaryWriter 的默认小端写入。</summary>
    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        // NBT 的字符串长度是 16 位无符号整数；超长内容会被游戏拒绝，这里直接截断到安全范围。
        if (bytes.Length > ushort.MaxValue)
        {
            bytes = bytes[..ushort.MaxValue];
        }

        writer.Write((byte)(bytes.Length >> 8));
        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteInt32(BinaryWriter writer, int value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private void TryDelete(string path)
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
            _log.Warn($"清理临时服务器列表失败：{path}", ex);
        }
    }
}
