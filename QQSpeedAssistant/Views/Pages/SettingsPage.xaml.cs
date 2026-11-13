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

    /// <summary>
    /// 输入框旁边的「粘贴」按钮：Tag 写目标 TextBox 的名字。
    /// 有的机器上 Ctrl+V 和右键粘贴都进不来（管理员进程读别的进程写的剪贴板会被系统挡），
    /// 这条路径直接读剪贴板写控件，是最后一层兜底。
    /// </summary>
    private void OnPasteButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name } || FindName(name) is not TextBox box)
        {
            return;
        }

        var (ok, detail) = App.PasteInto(box);
        App.LogUi($"按钮粘贴 → {(ok ? "已写入" : "未写入")}｜{detail}");

        if (!ok)
        {
            MessageBox.Show(
                $"没能把剪贴板内容填进来。\n\n{detail}\n\n"
                + "把这段提示连同日志页里「界面」那几条一起发出来，就能看出是剪贴板被占用还是键盘事件被别的程序吃了。",
                "粘贴失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        box.Focus();
        box.CaretIndex = box.Text.Length;
    }
}
