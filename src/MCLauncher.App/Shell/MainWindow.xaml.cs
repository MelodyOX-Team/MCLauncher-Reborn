using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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

    public MainWindow()
    {
        InitializeComponent();
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

    /// <summary>点击署名：用默认浏览器打开项目主页。</summary>
    private void OnCreditClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
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
