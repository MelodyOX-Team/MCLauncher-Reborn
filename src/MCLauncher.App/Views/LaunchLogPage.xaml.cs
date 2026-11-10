using System.Windows;
using System.Windows.Controls;
using MCLauncher.App.ViewModels;

namespace MCLauncher.App.Views;

/// <summary>启动日志页：日志增长时自动滚到底部。</summary>
public partial class LaunchLogPage : UserControl
{
    public LaunchLogPage()
    {
        InitializeComponent();
        LogBox.TextChanged += OnLogTextChanged;

        // 页面被缓存复用，导航回来时重新读一次 latest.log。
        Loaded += (_, _) =>
        {
            if (DataContext is LaunchLogViewModel viewModel)
            {
                viewModel.RefreshFromFile();
            }
        };
    }

    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        // 只在新内容追加时滚动，避免用户在翻旧日志时被强行拽回底部。
        if (e.Changes.Any(change => change.AddedLength > 0))
        {
            LogBox.ScrollToEnd();
        }
    }
}
