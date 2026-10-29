using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QQSpeedAssistant.ViewModels;
using Wpf.Ui.Controls;

namespace QQSpeedAssistant.Views.Windows;

/// <summary>
/// 模板采集窗口：显示游戏画面并支持鼠标拖拽框选，把选区保存为识别模板。
/// </summary>
public partial class TemplateCaptureWindow : FluentWindow
{
    private readonly TemplateCaptureViewModel _viewModel;
    private Point _startPoint;
    private bool _dragging;
    private Int32Rect? _selection;

    /// <summary>构造模板采集窗口。</summary>
    public TemplateCaptureWindow(TemplateCaptureViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.SelectionSaveRequested += OnSaveSelectionRequested;
        Closed += OnClosed;
    }

    /// <summary>按下开始框选。</summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource)
        {
            _viewModel.SetStatus("请先点击「抓取画面」");
            return;
        }

        _startPoint = e.GetPosition(PreviewHost);
        _dragging = true;
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _startPoint.X);
        Canvas.SetTop(SelectionRect, _startPoint.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        PreviewHost.CaptureMouse();
    }

    /// <summary>拖动更新选区。</summary>
    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var current = e.GetPosition(PreviewHost);
        var x = Math.Min(_startPoint.X, current.X);
        var y = Math.Min(_startPoint.Y, current.Y);
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = Math.Abs(current.X - _startPoint.X);
        SelectionRect.Height = Math.Abs(current.Y - _startPoint.Y);
    }

    /// <summary>松开结束框选并换算为画面像素坐标。</summary>
    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        PreviewHost.ReleaseMouseCapture();
        _selection = ToPixelSelection();
        _viewModel.SetStatus(_selection == null
            ? "框选区域太小，请重新拖拽"
            : $"已框选 {_selection.Value.Width}x{_selection.Value.Height} 像素，填写模板名后点击「保存框选」");
    }

    /// <summary>把窗口坐标换算为画面像素坐标。</summary>
    private Int32Rect? ToPixelSelection()
    {
        if (PreviewImage.Source is not BitmapSource source)
        {
            return null;
        }

        var hostWidth = PreviewHost.ActualWidth;
        var hostHeight = PreviewHost.ActualHeight;
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            return null;
        }

        var scale = Math.Min(hostWidth / source.PixelWidth, hostHeight / source.PixelHeight);
        if (scale <= 0)
        {
            return null;
        }

        var offsetX = (hostWidth - source.PixelWidth * scale) / 2;
        var offsetY = (hostHeight - source.PixelHeight * scale) / 2;

        var left = Canvas.GetLeft(SelectionRect);
        var top = Canvas.GetTop(SelectionRect);
        var x = (int)Math.Round((left - offsetX) / scale);
        var y = (int)Math.Round((top - offsetY) / scale);
        var width = (int)Math.Round(SelectionRect.Width / scale);
        var height = (int)Math.Round(SelectionRect.Height / scale);

        x = Math.Clamp(x, 0, source.PixelWidth - 1);
        y = Math.Clamp(y, 0, source.PixelHeight - 1);
        width = Math.Clamp(width, 0, source.PixelWidth - x);
        height = Math.Clamp(height, 0, source.PixelHeight - y);

        return width < 8 || height < 8 ? null : new Int32Rect(x, y, width, height);
    }

    /// <summary>响应 ViewModel 的保存请求。</summary>
    private void OnSaveSelectionRequested()
    {
        _selection ??= ToPixelSelection();
        if (_selection == null)
        {
            _viewModel.SetStatus("请先在预览画面上拖拽框选要识别的区域");
            return;
        }

        _viewModel.SaveTemplate(_selection.Value.X, _selection.Value.Y, _selection.Value.Width, _selection.Value.Height);
    }

    /// <summary>关闭窗口。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>关闭时释放资源。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.SelectionSaveRequested -= OnSaveSelectionRequested;
        _viewModel.Release();
    }
}
