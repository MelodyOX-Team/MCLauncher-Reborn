using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MCLauncher.App;

/// <summary>
/// 皮肤纹理的解码、模型判定与披风缩略图裁剪；角色本体由 3D 预览控件绘制。
/// </summary>
public static class SkinPreviewRenderer
{
    /// <summary>把 PNG 字节解码成可跨线程使用的纹理。</summary>
    public static BitmapSource? LoadTexture(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>皮肤必须是 64×64 或旧版 64×32。</summary>
    public static bool IsValidSkinSize(BitmapSource texture) =>
        texture.PixelWidth == 64 && texture.PixelHeight is 64 or 32;

    /// <summary>标准判定：64×64 皮肤 (54,20) 透明即为纤细模型。</summary>
    public static bool DetectSlim(BitmapSource texture)
    {
        if (texture.PixelWidth < 64 || texture.PixelHeight < 64)
        {
            return false;
        }

        var converted = new FormatConvertedBitmap(texture, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(54, 20, 1, 1), pixel, 4, 0);
        return pixel[3] == 0;
    }

    /// <summary>披风背面：纹理里的 (1,1) 10×16 区域。</summary>
    public static BitmapSource? DrawCape(BitmapSource texture, int width, int height)
    {
        if (texture.PixelWidth < 12 || texture.PixelHeight < 18 || width <= 0 || height <= 0)
        {
            return null;
        }

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);

        using (var dc = visual.RenderOpen())
        {
            var cape = new CroppedBitmap(texture, new Int32Rect(1, 1, 10, 16));
            dc.DrawImage(cape, new Rect(0, 0, width, height));
        }

        return Render(visual, width, height);
    }

    private static BitmapSource Render(DrawingVisual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
