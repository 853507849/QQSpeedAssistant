using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QQSpeedAssistant.Models;
using QQSpeedAssistant.Services;
using QQSpeedAssistant.ViewModels;

namespace QQSpeedAssistant.Views.Pages;

/// <summary>日志视图：新增日志时自动滚动到底部，并把日志复制到剪贴板。</summary>
public partial class LogsPage : UserControl
{
    private LogsPageViewModel? _viewModel;

    /// <summary>构造日志页视图。</summary>
    public LogsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>切换 DataContext 时重新订阅日志集合。</summary>
    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.Logs.CollectionChanged -= OnLogsChanged;
        }

        _viewModel = e.NewValue as LogsPageViewModel;
        if (_viewModel != null)
        {
            _viewModel.Logs.CollectionChanged += OnLogsChanged;
        }
    }

    /// <summary>滚动到最后一条日志。</summary>
    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (LogList.Items.Count == 0)
        {
            return;
        }

        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    /// <summary>
    /// 右键先选中右键点到的那一行，再弹菜单。
    /// 不这么做的话「复制这一条」拿到的永远是上一次选中的行，粘出来的内容对不上。
    /// </summary>
    private void OnLogListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(LogList, source) is ListBoxItem item)
        {
            LogList.SelectedItem = item.DataContext;
            item.IsSelected = true;
        }
    }

    /// <summary>复制当前显示的全部日志。</summary>
    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var logs = _viewModel?.Logs;
        if (logs == null || logs.Count == 0)
        {
            SetHint("当前没有日志可复制");
            return;
        }

        CopyText(string.Join(Environment.NewLine, logs.Select(Format)), logs.Count);
    }

    /// <summary>复制列表里选中的那一条日志。</summary>
    private void OnCopySelected(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not LogEntry entry)
        {
            SetHint("先在列表里点一条日志，再复制");
            return;
        }

        CopyText(Format(entry), 1);
    }

    /// <summary>时间、级别、来源、内容拼成一行，和 User/log 下的文件格式保持一致。</summary>
    private static string Format(LogEntry entry)
        => $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.LevelText}] [{entry.Source}] {entry.Message}";

    /// <summary>
    /// 写剪贴板。走过 Win32（见 <see cref="ClipboardService"/>），不能再直接用 WPF 的 Clipboard——
    /// 本机管理员进程里它会抛 System.Reflection.Metadata 装载失败，点了「复制」毫无反应。
    /// 别的程序正占着剪贴板时的重试已经放在 WriteText 里。
    /// </summary>
    private void CopyText(string text, int count)
    {
        if (ClipboardService.WriteText(text, out var detail))
        {
            SetHint($"已复制 {count} 条到剪贴板");
            return;
        }

        SetHint($"复制失败：{detail}");
    }

    /// <summary>更新页面上的复制结果提示。</summary>
    private void SetHint(string text)
    {
        if (_viewModel != null)
        {
            _viewModel.CopyHint = text;
        }
    }
}
