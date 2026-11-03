using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace MCLauncher.App.Controls;

/// <summary>
/// 可拖拽旋转的 3D 皮肤预览：按原版模型摆出头、身体、四肢与背后的披风，
/// 外套层按逐像素透明叠在底图上。每个面单独裁切纹理，避免整图 UV 受模型包围盒影响；
/// 纤细模型的胳膊按 3 像素宽处理。
/// </summary>
public partial class SkinPreview3D : UserControl
{
    public static readonly DependencyProperty SkinTextureProperty = DependencyProperty.Register(
        nameof(SkinTexture), typeof(BitmapSource), typeof(SkinPreview3D),
        new PropertyMetadata(null, OnModelChanged));

    public static readonly DependencyProperty CapeTextureProperty = DependencyProperty.Register(
        nameof(CapeTexture), typeof(BitmapSource), typeof(SkinPreview3D),
        new PropertyMetadata(null, OnModelChanged));

    public static readonly DependencyProperty IsSlimProperty = DependencyProperty.Register(
        nameof(IsSlim), typeof(bool), typeof(SkinPreview3D),
        new PropertyMetadata(false, OnModelChanged));

    private const double DefaultYaw = 20;
    private const double DefaultPitch = 8;
    private const double DefaultDistance = 60;
    private const double MinDistance = 42;
    private const double MaxDistance = 110;

    private readonly AxisAngleRotation3D _yawRotation = new(new Vector3D(0, 1, 0), DefaultYaw);
    private readonly AxisAngleRotation3D _pitchRotation = new(new Vector3D(1, 0, 0), DefaultPitch);
    private readonly Dictionary<Rect, Material> _faceMaterials = new();

    private TexturePixels? _skinPixels;
    private TexturePixels? _capePixels;
    private TexturePixels? _currentPixels;
    private Point? _dragStart;
    private double _dragYaw;
    private double _dragPitch;
    private double _distance = DefaultDistance;

    public SkinPreview3D()
    {
        InitializeComponent();

        var rotations = new Transform3DGroup();
        rotations.Children.Add(new RotateTransform3D(_yawRotation));
        rotations.Children.Add(new RotateTransform3D(_pitchRotation));
        RootVisual.Transform = rotations;
        Camera.Position = new Point3D(0, 0, DefaultDistance);

        Loaded += (_, _) => RebuildModel();
    }

    /// <summary>皮肤纹理；为空时只显示空场景。</summary>
    public BitmapSource? SkinTexture
    {
        get => (BitmapSource?)GetValue(SkinTextureProperty);
        set => SetValue(SkinTextureProperty, value);
    }

    /// <summary>披风纹理（64×32）；为空时不画披风。</summary>
    public BitmapSource? CapeTexture
    {
        get => (BitmapSource?)GetValue(CapeTextureProperty);
        set => SetValue(CapeTextureProperty, value);
    }

    /// <summary>纤细（Alex）模型。</summary>
    public bool IsSlim
    {
        get => (bool)GetValue(IsSlimProperty);
        set => SetValue(IsSlimProperty, value);
    }

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SkinPreview3D)d).RebuildModel();

    private void HandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragYaw = _yawRotation.Angle;
        _dragPitch = _pitchRotation.Angle;
        CaptureMouse();
        e.Handled = true;
    }

    private void HandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        _yawRotation.Angle = _dragYaw + (current.X - start.X) * 0.45;
        _pitchRotation.Angle = Math.Clamp(_dragPitch + (current.Y - start.Y) * 0.35, -85, 85);
    }

    private void HandleMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        _dragStart = null;
        e.Handled = true;
    }

    private void HandleLostMouseCapture(object sender, MouseEventArgs e) => _dragStart = null;

    private void HandleMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance - e.Delta / 120.0 * 5, MinDistance, MaxDistance);
        Camera.Position = new Point3D(0, 0, _distance);
        e.Handled = true;
    }

    private void RebuildModel()
    {
        if (SceneGroup is null)
        {
            return;
        }

        SceneGroup.Children.Clear();
        SceneGroup.Children.Add(new AmbientLight(Color.FromRgb(0xA8, 0xA8, 0xA8)));
        SceneGroup.Children.Add(new DirectionalLight(
            Color.FromRgb(0x70, 0x70, 0x70), new Vector3D(0.3, -0.5, -1)));

        _faceMaterials.Clear();
        _skinPixels = ToPixels(SkinTexture);
        _capePixels = ToPixels(CapeTexture);
        _currentPixels = null;
        if (SkinTexture is null || _skinPixels is null)
        {
            return;
        }

        _currentPixels = _skinPixels;
        if (SkinTexture.PixelHeight >= 64)
        {
            AddModernModel(IsSlim);
        }
        else
        {
            AddLegacyModel();
        }

        _currentPixels = _capePixels;
        if (_currentPixels is not null)
        {
            AddCape();
        }

        _currentPixels = null;
    }

    /// <summary>转成 BGRA32 像素缓冲，贴图时按最近邻放大，避免线性插值把像素糊掉。</summary>
    private static TexturePixels? ToPixels(BitmapSource? texture)
    {
        if (texture is null)
        {
            return null;
        }

        var converted = new FormatConvertedBitmap(texture, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new TexturePixels(pixels, stride);
    }

    /// <summary>64×64 皮肤：底图 + 外套层。</summary>
    private void AddModernModel(bool slim)
    {
        // 部件按原版尺寸互相贴合，不留缝（留缝会透出背景变成一条浅色细线）。
        // 贴合后相邻面会共面闪烁，所以把被完全挡住的面直接去掉，交给挡在前面的部件显示：
        // 躯干顶面 → 头部底面，腿顶面 → 躯干底面，腿/手臂朝向身体的面 → 躯干侧面。
        var torso = Torso with { Top = null };
        var rightLeg = RightLeg with { Top = null, Left = null };
        var leftLeg = LeftLeg with { Top = null, Right = null };

        AddBox(0, 12, 0, 8, 8, 8, Head);
        AddBox(0, 2, 0, 8, 12, 4, torso);

        if (slim)
        {
            AddBox(-5.5, 2, 0, 3, 12, 4, RightArmSlim with { Left = null });
            AddBox(5.5, 2, 0, 3, 12, 4, LeftArmSlim with { Right = null });
            AddBox(-5.5, 1.98, 0, 4, 13, 5.03, RightArmCoverSlim);
            AddBox(5.5, 1.98, 0, 4, 13, 5.03, LeftArmCoverSlim);
        }
        else
        {
            AddBox(-6, 2, 0, 4, 12, 4, RightArmClassic with { Left = null });
            AddBox(6, 2, 0, 4, 12, 4, LeftArmClassic with { Right = null });
            AddBox(-6, 1.98, 0, 5, 13, 5.03, RightArmCoverClassic);
            AddBox(6, 1.98, 0, 5, 13, 5.03, LeftArmCoverClassic);
        }

        AddBox(-2, -10, 0, 4, 12, 4, rightLeg);
        AddBox(2, -10, 0, 4, 12, 4, leftLeg);

        // 外套层：比底图各方向外扩 0.5 像素；腿套顶部错开 0.02，避免两块裤腿共面闪烁。
        AddBox(0, 12, 0, 9, 9, 9, HeadCover);
        AddBox(0, 2, 0, 9, 13, 5, TorsoCover);
        AddBox(-2, -10, 0, 5, 12.98, 5.01, RightLegCover);
        AddBox(2, -10, 0, 5, 13.02, 5.02, LeftLegCover);
    }

    /// <summary>64×32 旧皮肤：没有左臂左右腿独立纹理，也没有外套层，左右对称复用。</summary>
    private void AddLegacyModel()
    {
        AddBox(0, 12, 0, 8, 8, 8, Head);
        AddBox(0, 2, 0, 8, 12, 4, Torso with { Top = null });
        AddBox(-6, 2, 0, 4, 12, 4, RightArmClassic with { Left = null });
        AddBox(6, 2, 0, 4, 12, 4, RightArmClassic with { Right = null });
        AddBox(-2, -10, 0, 4, 12, 4, RightLeg with { Top = null, Left = null });
        AddBox(2, -10, 0, 4, 12, 4, RightLeg with { Top = null, Right = null });
    }

    /// <summary>
    /// 披风：10×16×1 的薄片从肩线（y=8）垂到膝盖上方，挂在背后并向外倾 10°，
    /// 和原版一样只在后背露出可见面。
    /// </summary>
    private void AddCape()
    {
        var tilt = new RotateTransform3D(
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), 10), new Point3D(0, 8, -2.5));
        AddBox(0, 0, -2.5, 10, 16, 1, Cape, tilt);
    }

    private void AddBox(
        double x,
        double y,
        double z,
        double width,
        double height,
        double depth,
        FaceMap map,
        Transform3D? transform = null)
    {
        var x0 = x - width / 2;
        var x1 = x + width / 2;
        var y0 = y - height / 2;
        var y1 = y + height / 2;
        var z0 = z - depth / 2;
        var z1 = z + depth / 2;

        // 每个面都按“站在外面看”的左下 → 右下 → 右上 → 左上排列，正面朝外。
        // map 里为 null 的面表示被相邻部件完全挡住，不生成。
        if (map.Front is { } front)
        {
            AddFace(
                new Point3D(x0, y0, z1), new Point3D(x1, y0, z1),
                new Point3D(x1, y1, z1), new Point3D(x0, y1, z1),
                new Vector3D(0, 0, 1), front, transform);
        }

        if (map.Back is { } back)
        {
            AddFace(
                new Point3D(x1, y0, z0), new Point3D(x0, y0, z0),
                new Point3D(x0, y1, z0), new Point3D(x1, y1, z0),
                new Vector3D(0, 0, -1), back, transform);
        }

        if (map.Left is { } left)
        {
            AddFace(
                new Point3D(x1, y0, z1), new Point3D(x1, y0, z0),
                new Point3D(x1, y1, z0), new Point3D(x1, y1, z1),
                new Vector3D(1, 0, 0), left, transform);
        }

        if (map.Right is { } right)
        {
            AddFace(
                new Point3D(x0, y0, z0), new Point3D(x0, y0, z1),
                new Point3D(x0, y1, z1), new Point3D(x0, y1, z0),
                new Vector3D(-1, 0, 0), right, transform);
        }

        if (map.Top is { } top)
        {
            AddFace(
                new Point3D(x0, y1, z1), new Point3D(x1, y1, z1),
                new Point3D(x1, y1, z0), new Point3D(x0, y1, z0),
                new Vector3D(0, 1, 0), top, transform);
        }

        if (map.Bottom is { } bottom)
        {
            AddFace(
                new Point3D(x0, y0, z0), new Point3D(x1, y0, z0),
                new Point3D(x1, y0, z1), new Point3D(x0, y0, z1),
                new Vector3D(0, -1, 0), bottom, transform);
        }
    }

    private void AddFace(
        Point3D p0,
        Point3D p1,
        Point3D p2,
        Point3D p3,
        Vector3D normal,
        Rect rect,
        Transform3D? transform = null)
    {
        var mesh = new MeshGeometry3D();
        mesh.Positions.Add(p0);
        mesh.Positions.Add(p1);
        mesh.Positions.Add(p2);
        mesh.Positions.Add(p3);

        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);

        // 裁切后的单面纹理直接铺满四边形。
        mesh.TextureCoordinates.Add(new Point(0, 1));
        mesh.TextureCoordinates.Add(new Point(1, 1));
        mesh.TextureCoordinates.Add(new Point(1, 0));
        mesh.TextureCoordinates.Add(new Point(0, 0));

        mesh.TriangleIndices.Add(0);
        mesh.TriangleIndices.Add(1);
        mesh.TriangleIndices.Add(2);
        mesh.TriangleIndices.Add(0);
        mesh.TriangleIndices.Add(2);
        mesh.TriangleIndices.Add(3);
        mesh.Freeze();

        var model = new GeometryModel3D(mesh, CreateFaceMaterial(rect));
        if (transform is not null)
        {
            model.Transform = transform;
        }

        model.Freeze();
        SceneGroup.Children.Add(model);
    }

    private Material CreateFaceMaterial(Rect rect)
    {
        if (_faceMaterials.TryGetValue(rect, out var cached))
        {
            return cached;
        }

        // WPF 3D 关不掉线性采样，8×8 的皮肤面直接贴上会糊；先按最近邻放大 8 倍，
        // 让每个纹理像素在屏幕上接近 1:1，边缘就保持锐利。
        const int scale = 8;
        var source = _currentPixels!.Value;
        var x0 = (int)rect.X;
        var y0 = (int)rect.Y;
        var width = (int)rect.Width * scale;
        var height = (int)rect.Height * scale;

        var scaled = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = (y0 + y / scale) * source.Stride;
            for (var x = 0; x < width; x++)
            {
                var from = sourceRow + (x0 + x / scale) * 4;
                var to = (y * width + x) * 4;
                scaled[to] = source.Pixels[from];
                scaled[to + 1] = source.Pixels[from + 1];
                scaled[to + 2] = source.Pixels[from + 2];
                scaled[to + 3] = source.Pixels[from + 3];
            }
        }

        var crop = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, scaled, width * 4);
        crop.Freeze();

        var brush = new ImageBrush(crop)
        {
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
        };
        brush.Freeze();

        var material = new DiffuseMaterial(brush);
        material.Freeze();
        _faceMaterials[rect] = material;
        return material;
    }

    /// <summary>一张纹理的 BGRA32 像素缓冲。</summary>
    private readonly record struct TexturePixels(byte[] Pixels, int Stride);

    /// <summary>
    /// 一个方块六个面的 UV 区域，顺序与 <see cref="AddBox"/> 的面顺序一致；
    /// 为 null 表示该面被相邻部件挡住，不生成。
    /// </summary>
    private readonly record struct FaceMap(
        Rect? Left,
        Rect? Right,
        Rect? Top,
        Rect? Bottom,
        Rect? Front,
        Rect? Back);

    private static readonly FaceMap Head = new(
        new Rect(16, 8, 8, 8), new Rect(0, 8, 8, 8),
        new Rect(8, 0, 8, 8), new Rect(16, 0, 8, 8),
        new Rect(8, 8, 8, 8), new Rect(24, 8, 8, 8));

    private static readonly FaceMap HeadCover = new(
        new Rect(48, 8, 8, 8), new Rect(32, 8, 8, 8),
        new Rect(40, 0, 8, 8), new Rect(48, 0, 8, 8),
        new Rect(40, 8, 8, 8), new Rect(56, 8, 8, 8));

    private static readonly FaceMap Torso = new(
        new Rect(28, 20, 4, 12), new Rect(16, 20, 4, 12),
        new Rect(20, 16, 8, 4), new Rect(28, 16, 8, 4),
        new Rect(20, 20, 8, 12), new Rect(32, 20, 8, 12));

    private static readonly FaceMap TorsoCover = new(
        new Rect(28, 36, 4, 12), new Rect(16, 36, 4, 12),
        new Rect(20, 32, 8, 4), new Rect(28, 32, 8, 4),
        new Rect(20, 36, 8, 12), new Rect(32, 36, 8, 12));

    private static readonly FaceMap RightArmClassic = new(
        new Rect(48, 20, 4, 12), new Rect(40, 20, 4, 12),
        new Rect(44, 16, 4, 4), new Rect(48, 16, 4, 4),
        new Rect(44, 20, 4, 12), new Rect(52, 20, 4, 12));

    private static readonly FaceMap RightArmSlim = new(
        new Rect(47, 20, 4, 12), new Rect(40, 20, 4, 12),
        new Rect(44, 16, 3, 4), new Rect(47, 16, 3, 4),
        new Rect(44, 20, 3, 12), new Rect(51, 20, 3, 12));

    private static readonly FaceMap RightArmCoverClassic = new(
        new Rect(48, 36, 4, 12), new Rect(40, 36, 4, 12),
        new Rect(44, 32, 4, 4), new Rect(48, 32, 4, 4),
        new Rect(44, 36, 4, 12), new Rect(52, 36, 4, 12));

    private static readonly FaceMap RightArmCoverSlim = new(
        new Rect(47, 36, 4, 12), new Rect(40, 36, 4, 12),
        new Rect(44, 32, 3, 4), new Rect(47, 32, 3, 4),
        new Rect(44, 36, 3, 12), new Rect(51, 36, 3, 12));

    private static readonly FaceMap LeftArmClassic = new(
        new Rect(40, 52, 4, 12), new Rect(32, 52, 4, 12),
        new Rect(36, 48, 4, 4), new Rect(40, 48, 4, 4),
        new Rect(36, 52, 4, 12), new Rect(44, 52, 4, 12));

    private static readonly FaceMap LeftArmSlim = new(
        new Rect(39, 52, 4, 12), new Rect(32, 52, 4, 12),
        new Rect(36, 48, 3, 4), new Rect(39, 48, 3, 4),
        new Rect(36, 52, 3, 12), new Rect(43, 52, 3, 12));

    private static readonly FaceMap LeftArmCoverClassic = new(
        new Rect(56, 52, 4, 12), new Rect(48, 52, 4, 12),
        new Rect(52, 48, 4, 4), new Rect(56, 48, 4, 4),
        new Rect(52, 52, 4, 12), new Rect(60, 52, 4, 12));

    private static readonly FaceMap LeftArmCoverSlim = new(
        new Rect(55, 52, 4, 12), new Rect(48, 52, 4, 12),
        new Rect(52, 48, 3, 4), new Rect(55, 48, 3, 4),
        new Rect(52, 52, 3, 12), new Rect(59, 52, 3, 12));

    private static readonly FaceMap RightLeg = new(
        new Rect(8, 20, 4, 12), new Rect(0, 20, 4, 12),
        new Rect(4, 16, 4, 4), new Rect(8, 16, 4, 4),
        new Rect(4, 20, 4, 12), new Rect(12, 20, 4, 12));

    private static readonly FaceMap RightLegCover = new(
        new Rect(8, 36, 4, 12), new Rect(0, 36, 4, 12),
        new Rect(4, 32, 4, 4), new Rect(8, 32, 4, 4),
        new Rect(4, 36, 4, 12), new Rect(12, 36, 4, 12));

    private static readonly FaceMap LeftLeg = new(
        new Rect(24, 52, 4, 12), new Rect(16, 52, 4, 12),
        new Rect(20, 48, 4, 4), new Rect(24, 48, 4, 4),
        new Rect(20, 52, 4, 12), new Rect(28, 52, 4, 12));

    private static readonly FaceMap LeftLegCover = new(
        new Rect(8, 52, 4, 12), new Rect(0, 52, 4, 12),
        new Rect(4, 48, 4, 4), new Rect(8, 48, 4, 4),
        new Rect(4, 52, 4, 12), new Rect(12, 52, 4, 12));

    /// <summary>
    /// 披风纹理（64×32）里的 10×16 薄片。原版把披风转 180° 挂在背后，
    /// 所以这里把左右与前后对调：(1,1) 那块才是从背后看到的面。
    /// </summary>
    private static readonly FaceMap Cape = new(
        new Rect(0, 1, 1, 16), new Rect(11, 1, 1, 16),
        new Rect(1, 0, 10, 1), new Rect(11, 0, 10, 1),
        new Rect(12, 1, 10, 16), new Rect(1, 1, 10, 16));
}
