using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Utils;

namespace MCLauncher.App;

/// <summary>
/// 账号头像：离线账号按 UUID 从官方 9 套默认皮肤里确定性地挑一套，
/// 微软账号从 Mojang 取真实皮肤（失败时退回同一套默认皮肤）。微软头像按 UUID 缓存。
/// </summary>
public sealed class AvatarService
{
    private const string ProfileUrl = "https://sessionserver.mojang.com/session/minecraft/profile/";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>官方默认皮肤字符集（与游戏内默认皮肤一致）。</summary>
    private static readonly string[] DefaultSkins = { "alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri" };

    private static readonly Dictionary<string, ImageSource> DefaultHeadCache = new();

    private readonly string _cacheDirectory;
    private readonly ILogger _log;

    public AvatarService(string configDirectory, ILogger log)
    {
        _cacheDirectory = Path.Combine(configDirectory, "avatars");
        _log = log;
    }

    /// <summary>离线账号的默认头像：按 UUID 哈希固定选一套，同一 UUID 每次结果一致。</summary>
    public static ImageSource DefaultHeadFor(string uuid)
    {
        var index = FloorMod(JavaUuidHash(uuid), DefaultSkins.Length);
        return LoadDefaultHead(DefaultSkins[index]);
    }

    private static ImageSource LoadDefaultHead(string name)
    {
        lock (DefaultHeadCache)
        {
            if (DefaultHeadCache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(
                $"pack://application:,,,/MCLauncher-Reborn;component/Assets/heads/{name}.png",
                UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            DefaultHeadCache[name] = image;
            return image;
        }
    }

    /// <summary>Java 的 UUID.hashCode：高/低两段 long 各自折叠后异或。</summary>
    private static int JavaUuidHash(string uuid)
    {
        var raw = uuid.Replace("-", string.Empty);
        if (raw.Length == 32 &&
            ulong.TryParse(raw[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high) &&
            ulong.TryParse(raw[16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low))
        {
            return (int)((high >> 32) ^ high ^ (low >> 32) ^ low);
        }

        // UUID 非法时用 FNV-1a 兜底，保证结果跨进程稳定。
        var hash = 2166136261u;
        foreach (var ch in uuid)
        {
            hash = (hash ^ ch) * 16777619u;
        }

        return (int)hash;
    }

    private static int FloorMod(int value, int mod) => ((value % mod) + mod) % mod;

    /// <summary>微软账号头像：优先读缓存，过期再找 Mojang 重新取皮肤；失败时退回旧缓存。</summary>
    public Task<ImageSource?> LoadMicrosoftHeadAsync(string uuid, CancellationToken ct) =>
        LoadHeadAsync(ProfileUrl + uuid, uuid, ct);

    /// <summary>外置登录头像：从认证服务器的 sessionserver 取皮肤，缓存按「服务器 + UUID」区分。</summary>
    public Task<ImageSource?> LoadExternalHeadAsync(string authServer, string uuid, CancellationToken ct)
    {
        var root = authServer.Trim().TrimEnd('/');
        var cacheKey = "ext-" + HashUtil.Sha1OfString(root)[..12] + "-" + uuid;
        return LoadHeadAsync($"{root}/sessionserver/session/minecraft/profile/{uuid}", cacheKey, ct);
    }

    private async Task<ImageSource?> LoadHeadAsync(string profileUrl, string cacheKey, CancellationToken ct)
    {
        var cachePath = Path.Combine(_cacheDirectory, cacheKey + ".png");
        var cached = TryLoadBitmap(cachePath);
        if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheLifetime)
        {
            return cached;
        }

        try
        {
            var skinUrl = await ResolveSkinUrlAsync(profileUrl, ct).ConfigureAwait(false);
            if (skinUrl is null)
            {
                return cached;
            }

            var skin = await Http.GetByteArrayAsync(skinUrl, ct).ConfigureAwait(false);
            var head = ComposeHead(skin);
            if (head is null)
            {
                return cached;
            }

            SavePng(head, cachePath);
            return head;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"获取账号头像失败（{cacheKey}）", ex);
            return cached;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCLauncher-Reborn/6.0");
        return client;
    }

    /// <summary>从档案 JSON 里取出皮肤纹理地址（properties.textures 是 base64 的 JSON）。</summary>
    private static async Task<string?> ResolveSkinUrlAsync(string profileUrl, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(profileUrl, ct).ConfigureAwait(false);
        using var profile = JsonDocument.Parse(json);
        if (!profile.RootElement.TryGetProperty("properties", out var properties))
        {
            return null;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if (!property.TryGetProperty("name", out var name) || name.GetString() != "textures")
            {
                continue;
            }

            if (!property.TryGetProperty("value", out var value) || value.GetString() is not { } base64)
            {
                continue;
            }

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            using var textures = JsonDocument.Parse(decoded);
            if (textures.RootElement.TryGetProperty("textures", out var bundle) &&
                bundle.TryGetProperty("SKIN", out var skin) &&
                skin.TryGetProperty("url", out var url))
            {
                // Mojang 给的是 http 地址，必须换成 https 才能走现代 TLS。
                return url.GetString()?.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
            }
        }

        return null;
    }

    /// <summary>
    /// 皮肤图是 64×64（新格式）或 64×32（旧格式）：脸部在 (8,8)，帽檐覆盖层在 (40,8)。
    /// 这里把 8×8 的头部按 alpha 合成好，界面用最近邻放大到 40×40。
    /// </summary>
    private static BitmapSource? ComposeHead(byte[] skinPng)
    {
        using var stream = new MemoryStream(skinPng);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width < 16 || height < 16)
        {
            return null;
        }

        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);

        // 只有 64×64 新格式才有帽檐覆盖层；旧版 64×32 的同位置是无关像素，不能拿来合成。
        var hasOverlay = width >= 64 && height >= 64;
        var head = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                int pixel = ((y + 8) * width + (x + 8)) * 4;
                int target = ((y * 8) + x) * 4;
                head[target] = pixels[pixel];
                head[target + 1] = pixels[pixel + 1];
                head[target + 2] = pixels[pixel + 2];
                head[target + 3] = pixels[pixel + 3];

                if (!hasOverlay)
                {
                    continue;
                }

                int overlay = ((y + 8) * width + (x + 40)) * 4;
                int alpha = pixels[overlay + 3];
                if (alpha == 0)
                {
                    continue;
                }

                // 帽檐可能半透明：按 alpha 盖在脸部像素上。
                for (var channel = 0; channel < 3; channel++)
                {
                    head[target + channel] = (byte)(((pixels[overlay + channel] * alpha)
                                                    + (head[target + channel] * (255 - alpha))) / 255);
                }

                head[target + 3] = 255;
            }
        }

        var result = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, head, 8 * 4);
        result.Freeze();
        return result;
    }

    private static BitmapFrame? TryLoadBitmap(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private void SavePng(BitmapSource head, string path)
    {
        // 登录成功时主页与账号页会并发刷新同一个头像，直接 File.Create 会因独占占用互相冲突。
        // 改成「写临时文件 + 原子替换」：并发写只有一个赢，双方都不再抛 IOException。
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            using (var output = File.Create(tempPath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(head));
                encoder.Save(output);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"保存账号头像缓存失败：{path}", ex);

            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 临时文件清理失败可以忽略，留在缓存目录里也无害。
            }
        }
    }
}
