using System.Collections.Specialized;
using System.Windows.Controls;
using QQSpeedAssistant.ViewModels;

namespace QQSpeedAssistant.Views.Pages;

/// <summary>日志视图：新增日志时自动滚动到底部。</summary>
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
}
