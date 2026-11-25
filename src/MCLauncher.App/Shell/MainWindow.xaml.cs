using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MCLauncher.App.ViewModels;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Services;

namespace MCLauncher.App.Shell;

/// <summary>
/// 外壳窗口：顶部横向导航、贴角窗口按钮、署名链接、拖动与窗口尺寸持久化。
/// </summary>
public partial class MainWindow : Window
{
    private const string ProjectUrl = "https://melodyox.github.io";

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE</summary>
    private const int DwmWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND</summary>
    private const int DwmWindowCornerRound = 2;

    /// <summary>拖入提示的存活计时器：拖拽停留在窗口内时持续刷新，离开或取消后自动收起。</summary>
    private readonly DispatcherTimer _dropHintTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private long _lastFileDragTicks;

    public MainWindow()
    {
        InitializeComponent();
        _dropHintTimer.Tick += OnDropHintTimerTick;
    }

    /// <summary>窗口没有标题栏，按住顶部空白区即可拖动。</summary>
    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // ---------------------------------------------------------------- 文件拖放

    private void OnFileDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || IsBlockingOverlayOpen())
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        ShowDropHint();
        e.Handled = true;
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || IsBlockingOverlayOpen())
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        ShowDropHint();
        e.Handled = true;
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        // 子元素之间的移动也会冒泡 DragLeave；指针还在窗口内时先不收起，
        // 真的离开窗口/取消拖拽则由计时器兜底（DragLeave 在 Esc 取消时并不可靠）。
        var position = e.GetPosition(this);
        if (position.X < 0 || position.Y < 0
            || position.X > ActualWidth || position.Y > ActualHeight)
        {
            HideDropHint();
        }
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        HideDropHint();
        e.Handled = true;

        if (IsBlockingOverlayOpen()
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths
            || paths.Length == 0)
        {
            return;
        }

        if (DataContext is ShellViewModel shell)
        {
            await shell.HandleDropAsync(paths);
        }
    }

    /// <summary>显示拖入提示并刷新存活时间；DragOver 持续触发期间提示不会消失。</summary>
    private void ShowDropHint()
    {
        _lastFileDragTicks = Stopwatch.GetTimestamp();
        DropHint.Visibility = Visibility.Visible;

        if (!_dropHintTimer.IsEnabled)
        {
            _dropHintTimer.Start();
        }
    }

    private void HideDropHint()
    {
        _dropHintTimer.Stop();
        DropHint.Visibility = Visibility.Collapsed;
    }

    private void OnDropHintTimerTick(object? sender, EventArgs e)
    {
        // 300ms 内没有收到新的 DragOver，说明指针已离开窗口、拖拽被取消或已经落下。
        if (Stopwatch.GetElapsedTime(_lastFileDragTicks) < _dropHintTimer.Interval)
        {
            return;
        }

        HideDropHint();
    }

    /// <summary>初始化设置或崩溃界面打开时不接收文件。</summary>
    private bool IsBlockingOverlayOpen() =>
        DataContext is ShellViewModel shell && (shell.Setup.IsOpen || shell.Crash.IsOpen);

    /// <summary>点击署名：用默认浏览器打开项目主页。</summary>
    private void OnCreditClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 默认浏览器关联损坏或被组策略禁用时不能把整个启动器带崩。
            System.Diagnostics.Debug.WriteLine($"打开项目主页失败：{ex.Message}");
        }
    }

    /// <summary>窗口创建完成时申请圆角（Windows 11 生效，其它系统静默失败）。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var preference = DwmWindowCornerRound;
            _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // 圆角不是必须能力，失败就保持直角。
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        PersistWindowState();
        base.OnClosing(e);
    }

    private void PersistWindowState()
    {
        if (!App.Services.TryResolve<ConfigService>(out var config) || config is null)
        {
            return;
        }

        // 最大化时记录的是最大化尺寸，不应写回配置。
        if (WindowState == WindowState.Normal)
        {
            config.Settings.WindowWidth = (int)Math.Round(ActualWidth);
            config.Settings.WindowHeight = (int)Math.Round(ActualHeight);
        }

        config.Save();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
