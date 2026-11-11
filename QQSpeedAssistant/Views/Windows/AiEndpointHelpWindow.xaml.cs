using System.Windows;
using QQSpeedAssistant.ViewModels;
using Wpf.Ui.Controls;

namespace QQSpeedAssistant.Views.Windows;

/// <summary>
/// 「接口地址和模型怎么填」说明窗：列出可直接使用的免费方案，点一下就把地址和模型写回设置。
/// </summary>
public partial class AiEndpointHelpWindow : FluentWindow
{
    /// <summary>构造说明窗。</summary>
    public AiEndpointHelpWindow(AiEndpointHelpViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>关闭窗口。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
