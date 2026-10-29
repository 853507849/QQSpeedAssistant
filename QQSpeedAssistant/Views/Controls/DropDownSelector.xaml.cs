using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QQSpeedAssistant.Views.Controls;

/// <summary>
/// 自绘下拉选择器：ToggleButton 负责开合，Popup 承载列表，不依赖 ComboBox 的内部模板机制。
/// 列表项通过 <c>ToString()</c> 显示，数据源建议提供可读的 ToString 或直接使用字符串。
/// </summary>
public partial class DropDownSelector : UserControl
{
    /// <summary>候选项集合。</summary>
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(System.Collections.IEnumerable), typeof(DropDownSelector),
        new PropertyMetadata(null, OnItemsSourceChanged));

    /// <summary>当前选中项，默认双向绑定。</summary>
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(object), typeof(DropDownSelector),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>构造下拉选择器。</summary>
    public DropDownSelector()
    {
        InitializeComponent();
    }

    /// <summary>数据源变化时保住原来的选中项。</summary>
    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DropDownSelector selector)
        {
            selector.RestoreSelection();
        }
    }

    /// <summary>
    /// ListBox 更换数据源时会先清空选中项，再通过双向绑定把 null 写回 ViewModel，
    /// 表现就是「筛选条件变成空白、列表被整个筛空」。这里等布局稳定后把原选中项还回去。
    /// </summary>
    private void RestoreSelection()
    {
        var previous = SelectedItem;
        if (previous == null)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SelectedItem == null)
            {
                SelectedItem = previous;
            }
        }), DispatcherPriority.DataBind);
    }

    /// <summary>候选项集合。</summary>
    public System.Collections.IEnumerable? ItemsSource
    {
        get => (System.Collections.IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>当前选中项。</summary>
    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>点击开合按钮。</summary>
    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (Toggle.IsChecked == true && List.SelectedItem != null)
        {
            List.ScrollIntoView(List.SelectedItem);
        }
    }

    /// <summary>选中某一项后收起下拉。</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem != null)
        {
            Toggle.IsChecked = false;
        }
    }

    /// <summary>下拉关闭时同步按钮状态。</summary>
    private void OnPopupClosed(object? sender, EventArgs e)
    {
        Toggle.IsChecked = false;
    }
}
