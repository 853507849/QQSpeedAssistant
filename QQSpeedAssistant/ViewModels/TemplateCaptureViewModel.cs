using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using QQSpeedAssistant.Services;

namespace QQSpeedAssistant.ViewModels;

/// <summary>可供截图采集的游戏窗口。</summary>
public sealed record GameWindowItem(GameWindowInfo Window)
{
    /// <summary>下拉框显示文本。</summary>
    public string DisplayText => $"{Window.Title}（{Window.Width}x{Window.Height}）";

    /// <summary>下拉项与选中项统一使用该文本显示。</summary>
    public override string ToString() => DisplayText;
}

/// <summary>
/// 模板采集：从游戏窗口抓取一帧，框选按钮区域保存为模板图片，供流程识别使用。
/// </summary>
public sealed partial class TemplateCaptureViewModel : ObservableObject
{
    /// <summary>抓不到画面时依次尝试的截图方式（当前的设置排在最前）。</summary>
    private static readonly string[] FallbackCaptureModes =
        ["WindowsGraphicsCapture", "WindowsGraphicsCaptureV2", "DwmGetDxSharedSurface", "BitBlt"];

    private readonly AppServices _services;
    private Mat? _frame;

    /// <summary>构造模板采集 ViewModel。</summary>
    public TemplateCaptureViewModel(AppServices services)
    {
        _services = services;
        Windows = [];
        RefreshWindows();
    }

    /// <summary>游戏窗口列表。</summary>
    public ObservableCollection<GameWindowItem> Windows { get; }

    /// <summary>当前窗口。</summary>
    [ObservableProperty]
    private GameWindowItem? _selectedWindow;

    /// <summary>当前帧预览。</summary>
    [ObservableProperty]
    private BitmapSource? _preview;

    /// <summary>模板名称（不含扩展名）。</summary>
    [ObservableProperty]
    private string _templateName = "";

    /// <summary>提示信息。</summary>
    [ObservableProperty]
    private string _status = "先刷新窗口，抓取画面后在预览上拖拽框选按钮区域";

    /// <summary>是否已抓到画面。</summary>
    [ObservableProperty]
    private bool _hasFrame;

    /// <summary>请求保存当前框选区域，由视图提供像素坐标。</summary>
    public event Action? SelectionSaveRequested;

    /// <summary>保存框选（具体坐标由视图回传）。</summary>
    [RelayCommand]
    private void SaveSelection()
    {
        if (!HasFrame)
        {
            Status = "请先抓取画面";
            return;
        }

        SelectionSaveRequested?.Invoke();
    }

    /// <summary>由视图设置提示信息。</summary>
    public void SetStatus(string text) => Status = text;

    /// <summary>刷新游戏窗口列表。</summary>
    [RelayCommand]
    private void RefreshWindows()
    {
        var currentHandle = SelectedWindow?.Window.Handle ?? nint.Zero;
        Windows.Clear();
        foreach (var window in WindowLocator.Find(_services.Config.Settings))
        {
            Windows.Add(new GameWindowItem(window));
        }

        SelectedWindow = Windows.FirstOrDefault(w => w.Window.Handle == currentHandle) ?? Windows.FirstOrDefault();
        Status = Windows.Count == 0
            ? "没有找到游戏窗口，请先启动客户端（或在设置里修改进程名／标题关键字）"
            : $"找到 {Windows.Count} 个游戏窗口";
    }

    /// <summary>抓取当前窗口画面。</summary>
    [RelayCommand]
    private void Grab()
    {
        if (SelectedWindow == null)
        {
            Status = "请先选择一个游戏窗口";
            return;
        }

        // 最小化的窗口抓出来只会是一片空白（BitBlt 尤其如此），先还原再抓。
        if (NativeMethods.IsIconic(SelectedWindow.Window.Handle))
        {
            NativeMethods.ShowWindow(SelectedWindow.Window.Handle, 9); // SW_RESTORE
            Thread.Sleep(400);
            _services.Log.Info("游戏窗口处于最小化状态，已自动还原后再抓屏", "模板");
        }

        var settings = _services.Config.Settings;
        var preferred = settings.CaptureMode;
        var frame = CaptureUsableFrame(SelectedWindow.Window, preferred, out var usedMode);
        if (frame == null)
        {
            Status = $"抓屏失败：{preferred} 等几种方式都没抓到有效画面。请确认窗口没有最小化，或换一个游戏窗口再试";
            return;
        }

        // 登录界面保持原尺寸（登录框模板是按登录窗口采的），游戏内模板基准是 800x600。
        // 这里不替用户改窗口：登录界面已经选了「窗口模式 + 800x600(32位色)」，强行 SetWindowPos
        // 会把用户摆好的窗口改掉。偏差在容差内照常采集（模板匹配是多尺度搜索，几像素不影响命中），
        // 超差只提示用户自己去游戏里改分辨率。
        if (IsLoginScreen(frame))
        {
            _services.Log.Info("当前是登录界面，保持原窗口尺寸采集（登录框模板按登录窗口尺寸）", "模板");
        }
        else
        {
            var deltaWidth = frame.Width - GameSession.TemplateClientWidth;
            var deltaHeight = frame.Height - GameSession.TemplateClientHeight;
            if (Math.Abs(deltaWidth) > GameSession.ClientSizeTolerance
                || Math.Abs(deltaHeight) > GameSession.ClientSizeTolerance)
            {
                _services.Log.Warn($"游戏内画面是 {frame.Width}x{frame.Height}，与模板基准 "
                                   + $"{GameSession.TemplateClientWidth}x{GameSession.TemplateClientHeight} 相差较大："
                                   + "请到游戏设置里把分辨率选成 800x600(32位色)（程序不会自动改窗口）", "模板");
            }
            else if (deltaWidth != 0 || deltaHeight != 0)
            {
                _services.Log.Info($"游戏内画面 {frame.Width}x{frame.Height}，与模板基准 800x600 相差 "
                                   + $"{deltaWidth.ToString("+#;-#;0")}/{deltaHeight.ToString("+#;-#;0")} 像素，"
                                   + "在容差内，保持窗口不动", "模板");
            }
        }

        _frame?.Dispose();
        _frame = frame;
        Preview = ToBitmapSource(frame);
        HasFrame = true;
        _services.Log.Info($"采集画面已抓取：{frame.Width}x{frame.Height}（截图方式 {usedMode}）", "模板");

        if (!string.Equals(usedMode, preferred, StringComparison.OrdinalIgnoreCase))
        {
            // D3D/CEF 渲染的窗口用 BitBlt 常常只能抓到一片白，这里把能用的方式写回去，运行流程才不会继续白屏。
            _services.Log.Warn($"截图方式 {preferred} 抓到的是空白画面，已自动改用 {usedMode} 并写回设置", "模板");
            settings.CaptureMode = usedMode;
            _services.Config.SaveSettings();
            Status = $"「{preferred}」抓不到游戏画面，已自动切换到 {usedMode}；请在预览上拖拽框选";
            return;
        }

        Status = "画面已抓取，请在预览上拖拽框选要识别的区域";
    }

    /// <summary>当前画面是不是登录界面（能匹配到「登录」按钮就算）。</summary>
    private bool IsLoginScreen(Mat frame)
    {
        try
        {
            if (!_services.Matcher.TemplateExists("login_button"))
            {
                return false;
            }

            using var bgr = frame.Channels() == 4 ? ToBgr(frame) : frame.Clone();
            return _services.Matcher.Find(bgr, "login_button", 0.8).Found;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>四通道画面转三通道，模板匹配只接受 BGR 灰度转换。</summary>
    private static Mat ToBgr(Mat frame)
    {
        var bgr = new Mat();
        Cv2.CvtColor(frame, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    /// <summary>按当前方式抓一帧；抓到全白／全黑这类没有内容的画面时，自动换其他截图方式重试。</summary>
    private Mat? CaptureUsableFrame(GameWindowInfo window, string preferredMode, out string usedMode)
    {
        usedMode = preferredMode;

        var modes = new List<string> { preferredMode };
        foreach (var mode in FallbackCaptureModes)
        {
            if (!modes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            {
                modes.Add(mode);
            }
        }

        foreach (var mode in modes)
        {
            var frame = TryCapture(window, mode);
            if (frame == null)
            {
                continue;
            }

            if (!IsFlat(frame))
            {
                usedMode = mode;
                return frame;
            }

            _services.Log.Warn($"截图方式 {mode} 抓到的是空白画面（整幅一个颜色），换下一种", "模板");
            frame.Dispose();
        }

        return null;
    }

    /// <summary>用指定截图方式连抓几帧，返回最后一帧（WGC 首帧经常还没内容）。</summary>
    private Mat? TryCapture(GameWindowInfo window, string mode)
    {
        using var session = new GameSession(window, _services.Config.Settings, _services.Log, mode);
        if (!session.EnsureCapture())
        {
            return null;
        }

        Mat? last = null;
        for (var i = 0; i < 6; i++)
        {
            var frame = session.CaptureFrame();
            if (frame == null)
            {
                Thread.Sleep(150);
                continue;
            }

            last?.Dispose();
            last = frame;
            if (!IsFlat(last))
            {
                return last;
            }

            Thread.Sleep(150);
        }

        return last;
    }

    /// <summary>判断画面是否「空白」：灰度标准差太小，说明整幅图基本一个颜色。</summary>
    private static bool IsFlat(Mat frame)
    {
        try
        {
            using var gray = new Mat();
            switch (frame.Channels())
            {
                case 4:
                    Cv2.CvtColor(frame, gray, ColorConversionCodes.BGRA2GRAY);
                    break;
                case 3:
                    Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                    break;
                default:
                    frame.CopyTo(gray);
                    break;
            }

            Cv2.MeanStdDev(gray, out _, out var stddev);
            return stddev.Val0 < 3.0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把框选区域保存为模板。</summary>
    public void SaveTemplate(int x, int y, int width, int height)
    {
        if (_frame == null)
        {
            Status = "请先抓取画面";
            return;
        }

        var name = TemplateName?.Trim() ?? "";
        if (name.Length == 0)
        {
            Status = "请填写模板名称，例如 login_account_box";
            return;
        }

        if (width < 8 || height < 8)
        {
            Status = "框选区域太小，请重新拖拽";
            return;
        }

        try
        {
            var rect = new OpenCvSharp.Rect(x, y, width, height);
            rect = rect.Intersect(new OpenCvSharp.Rect(0, 0, _frame.Width, _frame.Height));
            if (rect.Width < 8 || rect.Height < 8)
            {
                Status = "框选区域超出画面，请重新拖拽";
                return;
            }

            using var roi = new Mat(_frame, rect);
            Cv2.ImEncode(".png", roi, out var buffer);
            Directory.CreateDirectory(PathService.TemplateDir);
            var path = Path.Combine(PathService.TemplateDir, $"{name}.png");
            File.WriteAllBytes(path, buffer);

            // 记录采集时的画面尺寸，运行时按「当前宽度 / 采集宽度」缩放模板，窗口尺寸不同也能匹配。
            var metaPath = Path.ChangeExtension(path, ".meta.json");
            File.WriteAllText(metaPath, $"{{\"width\": {_frame.Width}, \"height\": {_frame.Height}}}");

            _services.Matcher.ClearCache();
            _services.Log.Success($"模板已保存：{Path.GetFileName(path)}（{rect.Width}x{rect.Height}，采集基准 {_frame.Width}px）", "模板");
            Status = $"已保存 {Path.GetFileName(path)}（基准 {_frame.Width}px），可在流程里用 \"template\": \"{name}\" 引用";
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>释放抓到的画面。</summary>
    public void Release()
    {
        _frame?.Dispose();
        _frame = null;
        Preview = null;
        HasFrame = false;
    }

    /// <summary>Mat 转 BitmapSource。</summary>
    private static BitmapSource ToBitmapSource(Mat mat)
    {
        var source = mat.Channels() == 4
            ? BitmapSource.Create(mat.Width, mat.Height, 96, 96, PixelFormats.Bgra32, null, mat.Data,
                mat.Width * mat.Height * 4, (int)mat.Step())
            : BitmapSource.Create(mat.Width, mat.Height, 96, 96, PixelFormats.Bgr24, null, mat.Data,
                mat.Width * mat.Height * 3, (int)mat.Step());
        source.Freeze();
        return source;
    }
}
