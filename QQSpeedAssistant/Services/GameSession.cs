using Fischless.GameCapture;
using OpenCvSharp;
using QQSpeedAssistant.Models;
using QQSpeedAssistant.Services.Input;
using Vanara.PInvoke;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 一个游戏客户端实例：负责抓屏、把键鼠输入发送到已经激活的窗口，并记录当前绑定账号。
/// </summary>
public sealed class GameSession : IDisposable
{
    /// <summary>输入框里出现文字后，区域像素变化的判定阈值（0~1）。</summary>
    private const double TypeVisibleThreshold = 0.005;

    /// <summary>点击后画面发生变化的判定阈值（0~1）。</summary>
    private const double ClickVisibleThreshold = 0.01;

    /// <summary>探测输入框焦点时写入的探针字符（写完立刻退格删掉；用 3 个字符让画面变化更明显）。</summary>
    private const string ProbeCharacters = "xxx";

    private readonly LogService _log;
    private readonly AppSettings _settings;
    private readonly IInputBackend _backend;
    private readonly string? _captureModeOverride;
    private IGameCapture? _capture;
    private int _frameWidth;
    private int _frameHeight;
    private CursorMoveMode _moveMode = CursorMoveMode.Auto;
    private double? _absoluteSlopeX;
    private double? _absoluteSlopeY;
    private double _absoluteOriginX;
    private double _absoluteOriginY;
    private bool _absoluteCalibrating;
    private bool _disposed;

    /// <summary>光标定位方式：首次成功后固定，避免每次点击都试错。</summary>
    private enum CursorMoveMode
    {
        /// <summary>还没试过，按最优到最差的顺序尝试。</summary>
        Auto,

        /// <summary>通道绝对注入。</summary>
        Driver,

        /// <summary>通道相对注入（闭环逼近）。</summary>
        DriverRelative,

        /// <summary>系统 API（SetCursorPos）兜底。</summary>
        SystemApi
    }

    /// <summary>构造会话。</summary>
    /// <param name="window">要接管的窗口。</param>
    /// <param name="settings">运行设置。</param>
    /// <param name="log">日志。</param>
    /// <param name="captureMode">可选的截图方式覆盖（用某个方式单独试抓一帧时用），为空则读设置。</param>
    public GameSession(GameWindowInfo window, AppSettings settings, LogService log, string? captureMode = null)
    {
        _settings = settings;
        _log = log;
        _captureModeOverride = captureMode;
        _backend = InputBackendFactory.Create(settings, log);
        Handle = window.Handle;
        ProcessId = window.ProcessId;
        Title = window.Title;
        ProcessName = window.ProcessName;
    }

    /// <summary>当前使用的输入通道名称。</summary>
    public string InputBackendName => _backend.Name;

    /// <summary>窗口句柄。</summary>
    public nint Handle { get; private set; }

    /// <summary>进程 Id。</summary>
    public int ProcessId { get; }

    /// <summary>窗口标题。</summary>
    public string Title { get; }

    /// <summary>进程名。</summary>
    public string ProcessName { get; }

    /// <summary>当前绑定的账号，未绑定为 null。</summary>
    public AccountEntry? BoundAccount { get; private set; }

    /// <summary>是否已完成登录流程。</summary>
    public bool IsLoggedIn { get; private set; }

    /// <summary>截图器是否已经启动。</summary>
    public bool IsCapturing => _capture?.IsCapturing == true;

    /// <summary>会话显示名，用于日志。</summary>
    public string DisplayName => BoundAccount != null ? BoundAccount.Account : $"窗口{Handle}";

    /// <summary>绑定账号，切换账号时重置登录标记。</summary>
    public void Bind(AccountEntry account)
    {
        BoundAccount = account;
        IsLoggedIn = false;
    }

    /// <summary>解除账号绑定，恢复为空闲窗口。</summary>
    public void Unbind()
    {
        BoundAccount = null;
        IsLoggedIn = false;
    }

    /// <summary>标记登录成功。</summary>
    public void MarkLoggedIn()
    {
        IsLoggedIn = true;
    }

    /// <summary>游戏内（登录之后）统一的客户区宽度：游戏内模板都按 800x600 采集与识别。</summary>
    public const int TemplateClientWidth = 800;

    /// <summary>游戏内统一的客户区高度。</summary>
    public const int TemplateClientHeight = 600;

    /// <summary>判定「窗口尺寸已经符合模板基准」的容差（像素）。</summary>
    public const int ClientSizeTolerance = 8;

    /// <summary>
    /// 检查游戏内客户区是否就是模板基准 800x600，<b>不会改动窗口</b>。
    /// 登录界面已经选了「窗口模式 + 800x600(32位色)」，进游戏后窗口尺寸本来就是对的；
    /// 之前这里强行动 SetWindowPos，会把用户摆好的窗口改大改小（实测还常遇到 801x600 这种差 1 像素的
    /// 情况，改了也是白改，窗口还会闪一下）。模板匹配本身是多尺度搜索，几像素的差异不影响命中。
    /// 只有偏差超过容差才记警告，让用户自己去游戏设置里改分辨率。
    /// </summary>
    public bool EnsureGameClientSize()
    {
        NativeMethods.GetClientRect(Handle, out var current);
        var deltaWidth = current.Width - TemplateClientWidth;
        var deltaHeight = current.Height - TemplateClientHeight;

        if (Math.Abs(deltaWidth) <= ClientSizeTolerance && Math.Abs(deltaHeight) <= ClientSizeTolerance)
        {
            if (deltaWidth != 0 || deltaHeight != 0)
            {
                _log.Info($"窗口客户区 {current.Width}x{current.Height}，与模板基准 {TemplateClientWidth}x{TemplateClientHeight} 相差 "
                          + $"{deltaWidth.ToString("+#;-#;0")}/{deltaHeight.ToString("+#;-#;0")} 像素，在容差内，保持窗口不动", DisplayName);
            }

            return true;
        }

        _log.Warn($"窗口客户区是 {current.Width}x{current.Height}，与模板基准 {TemplateClientWidth}x{TemplateClientHeight} 相差较大；"
                  + "程序不会自动改窗口，请到游戏设置里把分辨率选成 800x600(32位色)", DisplayName);
        return false;
    }

    /// <summary>启动截图器，已启动时直接返回。</summary>
    public bool EnsureCapture()
    {
        if (_capture?.IsCapturing == true)
        {
            return true;
        }

        try
        {
            var modeText = string.IsNullOrWhiteSpace(_captureModeOverride) ? _settings.CaptureMode : _captureModeOverride;
            var mode = Enum.TryParse<CaptureModes>(modeText, true, out var parsed)
                ? parsed
                : CaptureModes.BitBlt;
            _capture?.Dispose();
            _capture = GameCaptureFactory.Create(mode);
            var captureSettings = new Dictionary<string, object>
            {
                // BitBlt 必须显式传该键，否则截图器不会真正启动
                ["autoFixWin11BitBlt"] = Environment.OSVersion.Version.Build >= 22000,
                ["MinUpdateIntervalMs"] = 0,
                ["UseCpuConvert"] = false
            };
            _capture.Start(Handle, captureSettings);
            return _capture.IsCapturing;
        }
        catch (Exception ex)
        {
            _log.Error($"截图器启动失败：{ex.Message}", DisplayName);
            return false;
        }
    }

    /// <summary>
    /// 抓取一帧（调用方负责释放）。失败时重试两次。
    /// </summary>
    public Mat? CaptureFrame()
    {
        if (_disposed || !EnsureCapture())
        {
            return null;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var frame = _capture!.Capture();
                if (frame?.Frame == null || frame.Frame.Empty())
                {
                    Thread.Sleep(120);
                    continue;
                }

                // 记录画面尺寸：模板坐标属于画面像素，客户区尺寸可能与之不同（缩放/边框），点击前要换算。
                _frameWidth = frame.Frame.Width;
                _frameHeight = frame.Frame.Height;
                return frame.Frame.Clone();
            }
            catch (Exception ex)
            {
                _log.Warn($"抓屏失败（第 {attempt + 1} 次）：{ex.Message}", DisplayName);
                Thread.Sleep(200);
            }
        }

        return null;
    }

    /// <summary>把窗口切到前台，输入前调用；返回是否真的拿到了前台焦点。</summary>
    public bool Activate()
    {
        var switched = NativeMethods.Activate(Handle);
        Thread.Sleep(Math.Max(50, _settings.WindowActivateDelayMs));

        if (!switched || NativeMethods.GetForegroundWindow() != Handle)
        {
            _log.Error($"无法把游戏窗口切到前台（句柄 {Handle}）：游戏若以管理员身份运行，本程序也必须以管理员身份运行，否则点击与输入会被系统丢弃", DisplayName);
            return false;
        }

        return true;
    }

    /// <summary>按客户区比例点击，比例取值 0 至 1。</summary>
    public void ClickClientRatio(double xRatio, double yRatio)
    {
        var rect = WindowLocator.GetClientRect(Handle);
        ClickClientPixel((int)(rect.Width * xRatio), (int)(rect.Height * yRatio));
    }

    /// <summary>
    /// 按客户区像素坐标点击。
    /// 落点用屏幕物理像素定位，定位后校验光标是否真的到了目标点，偏移时自动重试并纠偏，
    /// 仍然偏移时把环境信息打进日志（多显示器/缩放/远程桌面都会造成偏移）。
    /// </summary>
    public void ClickClientPixel(int x, int y)
    {
        var point = WindowLocator.ClientToScreen(Handle, x, y);
        MoveCursorSafely(point);
        _backend.ClickLeft();
        Thread.Sleep(30);
    }

    /// <summary>按抓屏画面的像素坐标点击（模板匹配结果可直接用，自动换算到客户区）。</summary>
    public void ClickFramePixel(int frameX, int frameY)
    {
        var (x, y) = FrameToClient(frameX, frameY);
        ClickClientPixel(x, y);
    }

    /// <summary>最近一帧的画面尺寸，未抓过屏时为 0。</summary>
    public (int Width, int Height) FrameSize => (_frameWidth, _frameHeight);

    /// <summary>把抓屏画面坐标换算成客户区坐标（画面尺寸与客户区不一致时按比例缩放）。</summary>
    public (int X, int Y) FrameToClient(int frameX, int frameY)
    {
        var client = WindowLocator.GetClientRect(Handle);
        if (_frameWidth <= 0 || _frameHeight <= 0 || client.Width <= 0 || client.Height <= 0)
        {
            return (frameX, frameY);
        }

        if (_frameWidth == client.Width && _frameHeight == client.Height)
        {
            return (frameX, frameY);
        }

        var x = (int)Math.Round(frameX * (double)client.Width / _frameWidth);
        var y = (int)Math.Round(frameY * (double)client.Height / _frameHeight);
        return (Math.Clamp(x, 0, Math.Max(0, client.Width - 1)), Math.Clamp(y, 0, Math.Max(0, client.Height - 1)));
    }

    /// <summary>
    /// 点击并自检：点完对比该区域的画面，没变化说明点击没生效（坐标偏移、被反外挂拦截、
    /// 或窗口没拿到前台焦点），会自动重试。返回false表示重试后画面依然没变化。
    /// </summary>
    /// <param name="frameX">目标区域左上角（画面坐标）。</param>
    /// <param name="frameY">目标区域左上角（画面坐标）。</param>
    /// <param name="width">校验区域宽（画面像素）。</param>
    /// <param name="height">校验区域高（画面像素）。</param>
    /// <param name="attempts">最多尝试次数。</param>
    public bool ClickVerified(int frameX, int frameY, int width, int height, int attempts = 3)
    {
        for (var attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
        {
            using var before = GrabRegion(frameX, frameY, width, height);
            ClickFramePixel(frameX, frameY);
            Thread.Sleep(280);
            using var after = GrabRegion(frameX, frameY, width, height);

            var ratio = DiffRatio(before, after);
            if (ratio >= ClickVisibleThreshold)
            {
                if (attempt > 1)
                {
                    _log.Info($"重试第 {attempt} 次后点击生效（区域变化 {ratio:P1}）", DisplayName);
                }

                return true;
            }

            _log.Warn($"点击后该区域没有可见变化（变化仅 {ratio:P2}，第 {attempt} 次）：输入框类控件可能本来就没有视觉反馈；" +
                      "按钮／界面切换类步骤没有变化则说明点击没生效", DisplayName);
            Thread.Sleep(120);
        }

        return false;
    }

    /// <summary>
    /// 输入文本并自检：输入前后对比输入框区域，画面没变化说明文字根本没用进去
    /// （点击没落到输入框、或输入被系统/反外挂丢弃），会重新点击、清空、再输入。
    /// </summary>
    /// <param name="text">要输入的文本。</param>
    /// <param name="frameX">输入框区域左上角（画面坐标）。</param>
    /// <param name="frameY">输入框区域左上角（画面坐标）。</param>
    /// <param name="width">区域宽（画面像素）。</param>
    /// <param name="height">区域高（画面像素）。</param>
    /// <param name="retries">失败后重试次数。</param>
    public bool TypeTextVerified(string text, int frameX, int frameY, int width, int height, int retries = 2)
    {
        for (var attempt = 0; attempt <= Math.Max(0, retries); attempt++)
        {
            using var before = GrabRegion(frameX, frameY, width, height);

            if (attempt > 0)
            {
                // 重试时先重新点一次输入框：焦点没拿到是“输不进去”最常见的原因。
                ClickFramePixel(frameX + width / 2, frameY + height / 2);
                Thread.Sleep(150);
                ClearInput();
                Thread.Sleep(80);
            }

            TypeText(text);
            Thread.Sleep(260);

            using var after = GrabRegion(frameX, frameY, width, height);
            var ratio = DiffRatio(before, after);
            if (ratio >= TypeVisibleThreshold)
            {
                if (attempt > 0)
                {
                    _log.Info($"第 {attempt + 1} 次输入已生效（区域变化 {ratio:P1}）", DisplayName);
                }

                return true;
            }

            _log.Warn($"输入没有落到输入框（区域变化仅 {ratio:P2}，第 {attempt + 1} 次）", DisplayName);

            // 最后一次仍失败：把输入框区域存成图片，肉眼一看就知道到底是模板框错了还是真没输进去。
            if (attempt >= Math.Max(0, retries))
            {
                SaveRegionImages(before, after);
            }

            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>把自检区域的前后画面存到 User/debug，便于确认“没输进去”还是“模板框选错了区域”。</summary>
    private void SaveRegionImages(Mat? before, Mat? after)
    {
        try
        {
            var dir = Path.Combine(PathService.UserDir, "debug");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            if (before != null && !before.Empty())
            {
                Cv2.ImEncode(".png", before, out var buffer);
                File.WriteAllBytes(Path.Combine(dir, $"input-{stamp}-before.png"), buffer);
            }

            if (after != null && !after.Empty())
            {
                Cv2.ImEncode(".png", after, out var buffer);
                File.WriteAllBytes(Path.Combine(dir, $"input-{stamp}-after.png"), buffer);
            }

            _log.Warn($"已保存输入框区域截图：User/debug/input-{stamp}-before.png / -after.png（对比两张图即可判断文字有没有进框）",
                DisplayName);
        }
        catch
        {
            // 存图失败不影响主流程。
        }
    }

    /// <summary>
    /// 不点鼠标，用键盘把焦点移到目标输入框：反复「打一个探针字符 → 看目标区域有没有变化 → 退格删掉」，
    /// 没变化就按 Tab 换下一个控件。返回是否找到焦点。
    /// 全程只用键盘，绕开鼠标坐标偏移的问题；探针字符用退格清掉，不会破坏已输入的账号密码。
    /// </summary>
    /// <param name="frameX">目标输入框区域左上角（画面坐标）。</param>
    /// <param name="frameY">目标输入框区域左上角（画面坐标）。</param>
    /// <param name="width">区域宽（画面像素）。</param>
    /// <param name="height">区域高（画面像素）。</param>
    /// <param name="maxTabs">最多按几次 Tab。</param>
    public bool FocusFieldByProbing(int frameX, int frameY, int width, int height, int maxTabs = 5)
    {
        for (var attempt = 0; attempt <= Math.Max(0, maxTabs); attempt++)
        {
            using var before = GrabRegion(frameX, frameY, width, height);
            TypeText(ProbeCharacters);
            Thread.Sleep(220);
            using var after = GrabRegion(frameX, frameY, width, height);
            var ratio = DiffRatio(before, after);

            // 无论是否命中都要把探针字符删掉，避免污染输入框。
            for (var i = 0; i < ProbeCharacters.Length; i++)
            {
                _backend.KeyPress(User32.VK.VK_BACK);
                Thread.Sleep(40);
            }

            Thread.Sleep(100);

            if (ratio >= TypeVisibleThreshold)
            {
                _log.Info($"已定位到目标输入框（第 {attempt + 1} 次尝试，按了 {attempt} 次 Tab）", DisplayName);
                return true;
            }

            _log.Info($"当前焦点不在目标输入框，按 Tab 继续找（第 {attempt + 1} 次）", DisplayName);
            _backend.KeyPress(User32.VK.VK_TAB);
            Thread.Sleep(160);
        }

        return false;
    }

    /// <summary>输入文本并校验目标区域是否出现变化（只输入一次，不重试，也不点鼠标）。</summary>
    public bool TypeTextAndCheck(string text, int frameX, int frameY, int width, int height)
    {
        using var before = GrabRegion(frameX, frameY, width, height);
        TypeText(text);
        Thread.Sleep(300);
        using var after = GrabRegion(frameX, frameY, width, height);
        var ratio = DiffRatio(before, after);
        if (ratio < TypeVisibleThreshold)
        {
            SaveRegionImages(before, after);
            return false;
        }

        return true;
    }

    /// <summary>抓取画面中的一块区域，返回灰度副本（调用方负责释放）。</summary>
    public Mat? GrabRegion(int frameX, int frameY, int width, int height)
    {
        using var frame = CaptureFrame();
        if (frame == null || frame.Empty())
        {
            return null;
        }

        var x = Math.Clamp(frameX, 0, Math.Max(0, frame.Width - 1));
        var y = Math.Clamp(frameY, 0, Math.Max(0, frame.Height - 1));
        var w = Math.Clamp(width, 1, Math.Max(1, frame.Width - x));
        var h = Math.Clamp(height, 1, Math.Max(1, frame.Height - y));

        using var roi = new Mat(frame, new Rect(x, y, w, h));
        var gray = new Mat();
        if (roi.Channels() == 1)
        {
            roi.CopyTo(gray);
        }
        else
        {
            Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        }

        return gray;
    }

    /// <summary>
    /// 两块区域的差异比例（0~1）：亮度差超过 25 的像素占比。任一侧抓屏失败时按“有变化”处理，
    /// 避免因抓屏抖动而误判成输入失败。
    /// </summary>
    /// <summary>两块区域的逐像素差异结果。</summary>
    /// <param name="ChangedPixels">变化像素数。</param>
    /// <param name="Area">参与比较的总像素数。</param>
    /// <param name="InkWidth">变化像素的外接框宽度（用来区分「光标竖线」和「一排圆点／数字」）。</param>
    /// <param name="InkHeight">变化像素的外接框高度。</param>
    /// <param name="CenterPixels">落在中间带（纵向 15%～85%）里的变化像素数：边框高亮中间是空的，文字不是。</param>
    public readonly record struct DiffStats(int ChangedPixels, int Area, int InkWidth, int InkHeight,
        int CenterPixels)
    {
        /// <summary>变化像素占比。</summary>
        public double Ratio => Area <= 0 ? 0 : ChangedPixels / (double)Area;
    }

    /// <summary>
    /// 比较两块区域的差异，同时给出变化像素数、总面积、变化区外接框和中间带像素数。
    /// 只看占比会误判：光标在输入框里闪一下就有几十个像素（约 0.7%），
    /// 和「真的打进十几个圆点」在比例上区分不开，必须看绝对像素数与形状。
    /// </summary>
    public static DiffStats Diff(Mat? before, Mat? after)
    {
        if (before == null || after == null || before.Empty() || after.Empty())
        {
            return default;
        }

        var w = Math.Min(before.Width, after.Width);
        var h = Math.Min(before.Height, after.Height);
        if (w <= 0 || h <= 0)
        {
            return default;
        }

        var rect = new Rect(0, 0, w, h);
        using var a = new Mat(before, rect);
        using var b = new Mat(after, rect);
        using var diff = new Mat();
        using var mask = new Mat();
        Cv2.Absdiff(a, b, diff);
        Cv2.Threshold(diff, mask, 25, 255, ThresholdTypes.Binary);

        var changed = Cv2.CountNonZero(mask);
        var inkWidth = 0;
        var inkHeight = 0;
        var center = 0;
        if (changed > 0)
        {
            using var points = new Mat();
            Cv2.FindNonZero(mask, points);
            if (points.Rows > 0)
            {
                var box = Cv2.BoundingRect(points);
                inkWidth = box.Width;
                inkHeight = box.Height;
            }

            var top = (int)(h * 0.15);
            var bottom = (int)(h * 0.85);
            var bandHeight = Math.Min(h - top, Math.Max(1, bottom - top));
            if (top >= 0 && bandHeight > 0)
            {
                using var band = new Mat(mask, new Rect(0, top, w, bandHeight));
                center = Cv2.CountNonZero(band);
            }
        }

        return new DiffStats(changed, w * h, inkWidth, inkHeight, center);
    }

    /// <summary>变化像素占比（取不到画面时按“完全变了”处理，保持旧的保守语义）。</summary>
    public static double DiffRatio(Mat? before, Mat? after)
    {
        if (before == null || after == null || before.Empty() || after.Empty())
        {
            return 1;
        }

        return Diff(before, after).Ratio;
    }

    /// <summary>
    /// 输入链路自检：只移动光标不点击，测量定位误差并把关键环境信息写进日志，
    /// 用来判断“点不动”是权限、DPI、多显示器还是输入通道的问题。
    /// </summary>
    public bool RunInputDiagnostics()
    {
        var client = WindowLocator.GetClientRect(Handle);
        NativeMethods.GetWindowRect(Handle, out var window);
        EnsureCapture();

        // 自检必须重新测一遍所有定位方式，不能沿用上次记住的“兜底”结论。
        _moveMode = CursorMoveMode.Auto;
        _absoluteSlopeX = null;
        _absoluteSlopeY = null;

        _log.Info($"输入自检：通道 {_backend.Name}，管理员 {ElevationService.IsElevated}，窗口DPI {NativeMethods.GetDpiForWindow(Handle)}，" +
                  $"客户区 {client.Width}x{client.Height}（画面 {_frameWidth}x{_frameHeight}），窗口矩形 {window.Width}x{window.Height}，" +
                  $"主屏 {NativeMethods.ScreenWidth}x{NativeMethods.ScreenHeight}，虚拟桌面 {NativeMethods.VirtualScreenWidth}x{NativeMethods.VirtualScreenHeight}" +
                  $"（起点 {NativeMethods.VirtualScreenLeft},{NativeMethods.VirtualScreenTop}）", "自检");

        if (_backend is InterceptionBackend)
        {
            _log.Info($"驱动结构体自检：{InterceptionBackend.DescribeLayout()}", "自检");
        }

        if (!Activate())
        {
            _log.Error("输入自检失败：游戏窗口无法切到前台，点击与键盘输入都会被系统丢弃", "自检");
            return false;
        }

        NativeMethods.GetCursorPos(out var original);

        // 依次测三个点，任何一个点偏得多就说明坐标链路有问题（缩放/多显示器/被剪裁）。
        var probes = new[]
        {
            (client.Width / 2, client.Height / 2),
            (Math.Max(1, client.Width / 5), Math.Max(1, client.Height / 5)),
            (Math.Max(1, client.Width * 4 / 5), Math.Max(1, client.Height * 4 / 5))
        };

        var ok = true;
        var driverOk = true;
        foreach (var (x, y) in probes)
        {
            var target = WindowLocator.ClientToScreen(Handle, x, y);
            var moved = MoveCursorSafely(target, out var viaBackend);
            if (moved)
            {
                driverOk &= viaBackend;
                _log.Info($"定位正常（{(viaBackend ? "通道注入" : "系统 API 兜底")}）：客户区({x},{y}) → 屏幕({target.X},{target.Y})", "自检");
            }
            else
            {
                ok = false;
            }
        }

        NativeMethods.SetCursorPos(original.X, original.Y);

        if (!ok)
        {
            _log.Error("输入自检未通过：光标落点有偏差，见上面的偏差日志", "自检");
            return false;
        }

        if (!driverOk)
        {
            _log.Error($"输入自检未通过：当前通道「{_backend.Name}」的注入没有生效（只是系统 API 在动光标）。" +
                       "游戏只认通道注入的事件，请检查驱动是否已重启加载", "自检");
            return false;
        }

        _log.Info(_backend.Name == "Interception"
            ? "输入自检通过：Interception 驱动注入正常。若游戏仍无反应，说明事件被游戏端丢弃（注意反外挂会把注入标记为可疑）"
            : "输入自检通过：坐标链路正常。当前是 SendInput 通道，若游戏仍无反应请安装 Interception 驱动", "自检");

        return true;
    }

    /// <summary>移动光标并校验落点，偏移时逐级降级；仍偏移则记录环境信息。返回是否精确到位。</summary>
    private bool MoveCursorSafely(Win32Point point) => MoveCursorSafely(point, out _);

    /// <summary>
    /// 移动光标并校验落点；<paramref name="viaBackend"/> 表示落点是否由当前输入通道的注入实现
    /// （false 说明只是系统 API 在动光标，游戏可能不认这种事件）。
    /// </summary>
    private bool MoveCursorSafely(Win32Point point, out bool viaBackend)
    {
        viaBackend = true;

        // 1) 当前通道自己的绝对注入（最理想：游戏看到的就是“真硬件”事件）
        if (_moveMode is CursorMoveMode.Auto or CursorMoveMode.Driver)
        {
            var moved = _backend is InterceptionBackend interceptionDriver
                ? MoveAbsoluteLoop(interceptionDriver, point)   // 驱动注入：带误差反馈，自动吃掉缩放/多屏误差
                : MoveByBackend(point);

            if (moved)
            {
                _moveMode = CursorMoveMode.Driver;
                return true;
            }

            NativeMethods.GetCursorPos(out var afterDriver);
            _log.Warn($"通道「{_backend.Name}」的绝对移动没有落到目标点：期望 {point.X},{point.Y}，" +
                      $"实际 {afterDriver.X},{afterDriver.Y}", DisplayName);
        }

        // 2) 相对移动 + 闭环逼近：部分环境下绝对注入无效，相对位移反而能走通
        if (_backend is InterceptionBackend relativeDriver
            && _moveMode is CursorMoveMode.Auto or CursorMoveMode.Driver or CursorMoveMode.DriverRelative)
        {
            if (MoveRelativeLoop(relativeDriver, point))
            {
                if (_moveMode != CursorMoveMode.DriverRelative)
                {
                    _moveMode = CursorMoveMode.DriverRelative;
                    _log.Info("点击定位改用「驱动相对移动」（绝对注入在该环境下无效）", DisplayName);
                }

                return true;
            }
        }

        // 3) 系统 API 兜底：位置能到位，但游戏可能不认这种“没有真实移动”的点击
        if (NativeMethods.SetCursorPos(point.X, point.Y))
        {
            Thread.Sleep(40);
            if (IsCursorAt(point))
            {
                viaBackend = false;
                if (_moveMode != CursorMoveMode.SystemApi)
                {
                    _moveMode = CursorMoveMode.SystemApi;
                    _log.Warn($"点击定位改用「系统 API」兜底：通道「{_backend.Name}」的注入没能移动光标，" +
                              "游戏很可能不认这种点击", DisplayName);
                }

                return true;
            }
        }

        LogCoordinateEnvironment(point);
        return false;
    }

    /// <summary>用当前通道自己的绝对移动，并校验落点。</summary>
    private bool MoveByBackend(Win32Point point)
    {
        _backend.MoveMouseTo(point.X, point.Y);
        Thread.Sleep(50);
        return IsCursorAt(point);
    }

    /// <summary>
    /// 驱动绝对移动：先把「归一化值 ↔ 屏幕像素」的真实映射标定出来（缩放/多屏会让它偏离按虚拟桌面算的值），
    /// 之后一次就能算准；再留几轮误差反馈兜住驱动/系统的细微偏差。
    /// </summary>
    private bool MoveAbsoluteLoop(InterceptionBackend backend, Win32Point point)
    {
        EnsureAbsoluteCalibration(backend);

        double slopeX;
        double slopeY;
        int nx;
        int ny;

        if (_absoluteSlopeX is { } calibratedX && _absoluteSlopeY is { } calibratedY)
        {
            slopeX = calibratedX;
            slopeY = calibratedY;
            nx = (int)Math.Round((point.X - _absoluteOriginX) * slopeX);
            ny = (int)Math.Round((point.Y - _absoluteOriginY) * slopeY);
        }
        else
        {
            slopeX = 65535.0 / Math.Max(1, NativeMethods.VirtualScreenWidth - 1);
            slopeY = 65535.0 / Math.Max(1, NativeMethods.VirtualScreenHeight - 1);
            (nx, ny) = NativeMethods.ToVirtualDesktopAbsolute(point.X, point.Y);
        }

        for (var step = 0; step < 4; step++)
        {
            backend.MoveMouseToNormalized(nx, ny);
            // 第一次注入有冷启动延迟（设备刚被唤醒），多等一会儿再读落点，否则读到旧坐标会把闭环带偏。
            Thread.Sleep(step == 0 ? 110 : 55);

            if (!NativeMethods.GetCursorPos(out var actual))
            {
                return false;
            }

            var errorX = point.X - actual.X;
            var errorY = point.Y - actual.Y;
            if (Math.Abs(errorX) <= 2 && Math.Abs(errorY) <= 2)
            {
                return true;
            }

            nx = Math.Clamp(nx + (int)Math.Round(errorX * slopeX), 0, 65535);
            ny = Math.Clamp(ny + (int)Math.Round(errorY * slopeY), 0, 65535);
        }

        return IsCursorAt(point);
    }

    /// <summary>
    /// 标定驱动绝对定位的真实映射：打到约 10% 与 90% 两个点，用实测落点反推斜率与原点。
    /// 每个会话只做一次；标定失败时退回按虚拟桌面估算（仍由闭环兜底）。
    /// </summary>
    private void EnsureAbsoluteCalibration(InterceptionBackend backend)
    {
        if (_absoluteSlopeX is not null || _absoluteCalibrating)
        {
            return;
        }

        _absoluteCalibrating = true;
        try
        {
            const int nearValue = 6553;   // ≈10%
            const int farValue = 58982;   // ≈90%

            backend.MoveMouseToNormalized(nearValue, nearValue);
            Thread.Sleep(140);
            if (!NativeMethods.GetCursorPos(out var near))
            {
                return;
            }

            backend.MoveMouseToNormalized(farValue, farValue);
            Thread.Sleep(140);
            if (!NativeMethods.GetCursorPos(out var far))
            {
                return;
            }

            var deltaX = far.X - near.X;
            var deltaY = far.Y - near.Y;
            if (Math.Abs(deltaX) < 100 || Math.Abs(deltaY) < 100)
            {
                _log.Warn($"驱动绝对定位标定失败（两个标定点只差 {deltaX},{deltaY} 像素），改用误差反馈逼近", DisplayName);
                return;
            }

            var slopeX = (farValue - nearValue) / (double)deltaX;
            var slopeY = (farValue - nearValue) / (double)deltaY;
            _absoluteOriginX = near.X - nearValue / slopeX;
            _absoluteOriginY = near.Y - nearValue / slopeY;
            _absoluteSlopeX = slopeX;
            _absoluteSlopeY = slopeY;

            _log.Info($"驱动绝对定位已标定：{slopeX:F3} 归一化/像素（横）、{slopeY:F3}（纵），" +
                      $"原点约 ({_absoluteOriginX:F0},{_absoluteOriginY:F0})", DisplayName);
        }
        finally
        {
            _absoluteCalibrating = false;
        }
    }

    /// <summary>光标是否已经落在目标点（容差 2 像素）。</summary>
    private static bool IsCursorAt(Win32Point point) =>
        NativeMethods.GetCursorPos(out var actual)
        && Math.Abs(actual.X - point.X) <= 2
        && Math.Abs(actual.Y - point.Y) <= 2;

    /// <summary>用相对位移闭环逼近目标点：指针加速会放大位移，所以按剩余误差多次逼近。</summary>
    private bool MoveRelativeLoop(InterceptionBackend backend, Win32Point point)
    {
        for (var step = 0; step < 12; step++)
        {
            if (!NativeMethods.GetCursorPos(out var actual))
            {
                return false;
            }

            var dx = point.X - actual.X;
            var dy = point.Y - actual.Y;
            if (Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2)
            {
                return true;
            }

            backend.MoveMouseBy(dx, dy);
            Thread.Sleep(35);
        }

        return IsCursorAt(point);
    }

    /// <summary>定位彻底失败时打印一次环境快照，便于判断是缩放、多显示器还是坐标换算问题。</summary>
    private void LogCoordinateEnvironment(Win32Point target)
    {
        NativeMethods.GetCursorPos(out var actual);
        var client = WindowLocator.GetClientRect(Handle);
        NativeMethods.GetWindowRect(Handle, out var window);

        _log.Error($"点击定位失败：目标({target.X},{target.Y}) 实际({actual.X},{actual.Y})；" +
                   $"客户区 {client.Width}x{client.Height}（左上 {client.Left},{client.Top}），窗口 {window.Width}x{window.Height}，" +
                   $"画面 {_frameWidth}x{_frameHeight}，虚拟桌面 {NativeMethods.VirtualScreenWidth}x{NativeMethods.VirtualScreenHeight}" +
                   $"（起点 {NativeMethods.VirtualScreenLeft},{NativeMethods.VirtualScreenTop}），DPI {NativeMethods.GetDpiForWindow(Handle)}，" +
                   $"通道 {_backend.Name}", DisplayName);

        _log.Error("排查建议：1) 本程序与游戏必须同为管理员权限；2) 登录窗口请放在主显示器上（多屏不同缩放会偏移）；" +
                   "3) 显示器缩放 125%/150% 时保持程序清单里的 PerMonitorV2 生效；4) 远程桌面会改写光标位置", DisplayName);
    }

    /// <summary>按住按键指定毫秒，用于游戏内加速、漂移等长按操作。</summary>
    public void HoldKey(string key, int milliseconds)
    {
        var vk = ParseKey(key);
        _backend.KeyDown(vk);
        Thread.Sleep(Math.Max(20, milliseconds));
        _backend.KeyUp(vk);
    }

    /// <summary>按键或组合键，例如 <c>Space</c>、<c>Ctrl+A</c>。</summary>
    public void PressKey(string key)
    {
        var parts = key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1)
        {
            var modifiers = parts[..^1].Select(ParseKey).ToArray();
            _backend.ModifiedKeyStroke(modifiers, ParseKey(parts[^1]));
            return;
        }

        _backend.KeyPress(ParseKey(key));
    }

    /// <summary>
    /// 清空输入框：先 Ctrl+A + Delete，再补一串退格。
    /// 游戏登录框是自绘控件（CEF），实测 Ctrl+A 不生效，必须靠退格兜底，
    /// 否则新账号会追加到旧账号后面（例如 853507849 + 123456789）。
    /// </summary>
    /// <param name="backspaceCount">退格次数，覆盖常见账号密码长度。</param>
    public void ClearInput(int backspaceCount = 40)
    {
        _backend.ModifiedKeyStroke([User32.VK.VK_CONTROL], User32.VK.VK_A);
        Thread.Sleep(80);
        _backend.KeyPress(User32.VK.VK_DELETE);
        Thread.Sleep(80);

        for (var i = 0; i < Math.Clamp(backspaceCount, 0, 200); i++)
        {
            _backend.KeyPress(User32.VK.VK_BACK);
            Thread.Sleep(15);
        }

        Thread.Sleep(80);
    }

    /// <summary>
    /// 输入文本（账号、密码等）。逐字符发送虚拟键，兼容只认 RawInput/DirectInput 的游戏客户端；
    /// 非 ASCII 字符（中文等）退回 Unicode 输入。
    /// </summary>
    public void TypeText(string text) => TypeText(text, _settings.TypeTextIntervalMs);

    /// <summary>
    /// 按指定字符间隔输入。密码框必须用更慢的间隔：连续快速注入时游戏端的键盘过滤会丢键，
    /// 现象是「8 位密码只进去 1 个圆点」，而逐字符间隔足够时就能完整输入。
    /// </summary>
    public void TypeText(string text, int intervalMs)
    {
        foreach (var ch in text)
        {
            if (TryGetKeyStroke(ch, out var vk, out var shift))
            {
                if (shift)
                {
                    _backend.ModifiedKeyStroke([User32.VK.VK_SHIFT], vk);
                }
                else
                {
                    _backend.KeyPress(vk);
                }
            }
            else if (!_backend.TryTypeUnicode(ch))
            {
                _log.Warn($"当前输入通道（{_backend.Name}）不支持字符「{ch}」，已跳过；账号密码请只用英文数字", DisplayName);
            }

            Thread.Sleep(Math.Max(5, intervalMs));
        }

        // 键盘事件是异步投递的，等一下再抓屏，否则最后几个字符可能还没渲染出来。
        Thread.Sleep(Math.Max(80, intervalMs * 2));
    }

    /// <summary>把字符映射成虚拟键；返回 false 表示需要走 Unicode 输入。</summary>
    private static bool TryGetKeyStroke(char c, out User32.VK vk, out bool shift)
    {
        shift = false;
        vk = User32.VK.VK_SPACE;

        if (c is >= 'a' and <= 'z')
        {
            vk = (User32.VK)(0x41 + (c - 'a'));
            return true;
        }

        if (c is >= 'A' and <= 'Z')
        {
            vk = (User32.VK)(0x41 + (c - 'A'));
            shift = true;
            return true;
        }

        if (c is >= '0' and <= '9')
        {
            vk = (User32.VK)(0x30 + (c - '0'));
            return true;
        }

        var (code, needShift) = c switch
        {
            ' ' => (0x20, false),
            '!' => (0x31, true),
            '@' => (0x32, true),
            '#' => (0x33, true),
            '$' => (0x34, true),
            '%' => (0x35, true),
            '^' => (0x36, true),
            '&' => (0x37, true),
            '*' => (0x38, true),
            '(' => (0x39, true),
            ')' => (0x30, true),
            '-' => (0xBD, false),
            '_' => (0xBD, true),
            '=' => (0xBB, false),
            '+' => (0xBB, true),
            ',' => (0xBC, false),
            '<' => (0xBC, true),
            '.' => (0xBE, false),
            '>' => (0xBE, true),
            '/' => (0xBF, false),
            '?' => (0xBF, true),
            ';' => (0xBA, false),
            ':' => (0xBA, true),
            '\'' => (0xDE, false),
            '"' => (0xDE, true),
            '[' => (0xDB, false),
            '{' => (0xDB, true),
            ']' => (0xDD, false),
            '}' => (0xDD, true),
            '\\' => (0xDC, false),
            '|' => (0xDC, true),
            '`' => (0xC0, false),
            '~' => (0xC0, true),
            _ => (0, false)
        };

        if (code == 0)
        {
            return false;
        }

        vk = (User32.VK)code;
        shift = needShift;
        return true;
    }

    /// <summary>解析按键名称到虚拟键值，支持 Ctrl、Esc、Enter、F5、Space 等写法。</summary>
    private static User32.VK ParseKey(string name)
    {
        var normalized = name.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        if (normalized.Length == 0)
        {
            throw new ArgumentException("按键名称为空");
        }

        // 单个字母／数字直接换算，避免 Enum.TryParse 把 "5" 当成数值 5。
        if (normalized.Length == 1)
        {
            var c = normalized[0];
            if (c is >= '0' and <= '9')
            {
                return (User32.VK)(0x30 + (c - '0'));
            }

            if (c is >= 'A' and <= 'Z')
            {
                return (User32.VK)(0x41 + (c - 'A'));
            }
        }

        var alias = normalized switch
        {
            "ESC" => "ESCAPE",
            "ENTER" or "RETURN" => "RETURN",
            "CTRL" or "CONTROL" => "CONTROL",
            "ALT" => "MENU",
            "DEL" => "DELETE",
            "INS" => "INSERT",
            "SPACEBAR" or "SPACE" => "SPACE",
            "WIN" or "WINDOWS" or "LWIN" => "LWIN",
            "UP" => "UP",
            "DOWN" => "DOWN",
            "LEFT" => "LEFT",
            "RIGHT" => "RIGHT",
            "PGUP" => "PRIOR",
            "PGDN" => "NEXT",
            _ => normalized
        };

        // Vanara 的枚举成员都带 VK_ 前缀（VK_CONTROL、VK_RETURN…），两种写法都试一次。
        foreach (var candidate in new[] { $"VK_{alias}", alias })
        {
            if (Enum.TryParse<User32.VK>(candidate, true, out var vk)
                && Enum.IsDefined(typeof(User32.VK), vk))
            {
                return vk;
            }
        }

        throw new ArgumentException($"无法识别的按键：{name}");
    }

    /// <summary>释放截图器与输入资源。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _capture?.Stop();
            _capture?.Dispose();
        }
        catch
        {
            // 忽略释放异常。
        }

        _capture = null;

        try
        {
            _backend.Dispose();
        }
        catch
        {
            // 驱动通道释放异常时忽略，避免影响退出流程。
        }
    }
}
