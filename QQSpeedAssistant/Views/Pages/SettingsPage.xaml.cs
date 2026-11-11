using System.Windows;
using System.Windows.Controls;

namespace QQSpeedAssistant.Views.Pages;

/// <summary>运行设置视图，DataContext 为 SettingsPageViewModel。</summary>
public partial class SettingsPage : UserControl
{
    /// <summary>构造设置页视图。</summary>
    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 顶部锚点按钮：把 Tag 里指定的卡片滚到内容区顶部。
    /// 顶栏不参与滚动（ScrollViewer 在第二行），所以按卡片在内容面板里的 Y 坐标定位即可，不会被顶栏挡住。
    /// </summary>
    private void OnAnchorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name })
        {
            return;
        }

        if (FindName(name) is not FrameworkElement card || ContentScroll.Content is not UIElement content)
        {
            return;
        }

        var y = card.TranslatePoint(new Point(0, 0), content).Y;
        ContentScroll.ScrollToVerticalOffset(Math.Max(0, y - 8));
    }
}
