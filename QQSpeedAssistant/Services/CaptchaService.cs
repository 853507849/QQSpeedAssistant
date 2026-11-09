using OpenCvSharp;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>验证码处理结果。</summary>
public enum CaptchaOutcome
{
    /// <summary>没有出现验证码。</summary>
    NotPresent,

    /// <summary>已自动答对并通过。</summary>
    Solved,

    /// <summary>用户手动完成。</summary>
    ManualSolved,

    /// <summary>自动识别失败（已存调试图）。</summary>
    Failed,

    /// <summary>等人工超时，验证码窗口还在。</summary>
    TimedOut
}

/// <summary>
/// 登录时的图片验证码自动处理。
/// 链路：等窗口出现 → 抓图 → 切出候选图 → 视觉模型给编号 → 点图 → 点「确定」→ 确认窗口关闭；
/// 没配密钥或识别失败时退化成「等你手动点完」，绝不因为验证码把账号判成登录失败。
/// </summary>
public sealed class CaptchaService
{
    private readonly LogService _log;
    private readonly VisionCaptchaClient _vision;

    /// <summary>构造验证码服务。</summary>
    public CaptchaService(LogService log)
    {
        _log = log;
        _vision = new VisionCaptchaClient(log);
    }

    /// <summary>当前屏幕上是否有验证码窗口。</summary>
    public static bool IsPresent(AppSettings settings) => CaptchaWindowLocator.Find(settings) != null;

    /// <summary>
    /// 登录提交后调用：等一会儿看有没有验证码弹窗，有就处理掉。
    /// 返回 NotPresent 表示这轮登录没有验证码，调用方按原流程继续。
    /// </summary>
    /// <param name="settings">运行设置。</param>
    /// <param name="account">当前账号（只用于日志）。</param>
    /// <param name="ct">取消标记。</param>
    /// <param name="detectTimeoutMs">等弹窗出现的毫秒数。</param>
    public async Task<CaptchaOutcome> HandleIfPresentAsync(AppSettings settings, string account, CancellationToken ct,
        int detectTimeoutMs = 12000)
    {
        var window = CaptchaWindowLocator.WaitFor(settings, detectTimeoutMs, ct);
        if (window == null)
        {
            return CaptchaOutcome.NotPresent;
        }

        _log.Warn($"弹出登录验证码（窗口「{window.Title}」{window.Width}x{window.Height}），开始处理", account);

        // 真实点击要求验证码窗口在前台，否则点不到（点到游戏画面上了）。
        NativeMethods.Activate(window.Handle);
        Thread.Sleep(400);

        if (!settings.AutoCaptchaEnabled)
        {
            _log.Info("设置里关闭了「自动过码」，改为等待人工完成", account);
            return await WaitManualAsync(settings, account, ct);
        }

        if (!VisionCaptchaClient.CanCall(settings))
        {
            _log.Warn("没有配置验证码识别接口（设置 → 登录验证码），改为等待人工完成", account);
            return await WaitManualAsync(settings, account, ct);
        }

        var maxAttempts = Math.Clamp(settings.CaptchaMaxAttempts, 1, 6);

        // 弹窗刚出来时只有「正在加载验证码…」，图块是异步拉下来的；这段时间抓图必然切不出东西。
        // 先轮询等图块出现，别把它算成一次答题失败（否则 3 次机会全耗在加载上）。
        window = await WaitTilesReadyAsync(window, _log, account, ct);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (CaptchaWindowLocator.Find(settings) == null)
            {
                _log.Success("验证码窗口已消失", account);
                return CaptchaOutcome.Solved;
            }

            window = CaptchaWindowLocator.Refresh(window);
            using var image = CaptureForTiles(window, _log, out var tileCount);
            if (image == null || image.Empty())
            {
                _log.Warn($"第 {attempt} 次：抓不到验证码画面，稍后重试", account);
                await Task.Delay(1200, ct);
                continue;
            }

            if (tileCount < 2)
            {
                _log.Error($"第 {attempt} 次：没能切出候选图（{tileCount} 块）", account);
                var report = CaptchaGrid.Detect(image).Report;
                foreach (var line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    _log.Info("  " + line.Trim(), account);
                }

                SaveDebug(image, null, "grid");

                // 抓到了画面但切不出图块，最常见的原因是图还在加载：等它出来再试，别空耗次数。
                window = await WaitTilesReadyAsync(window, _log, account, ct);
                continue;
            }

            var grid = CaptchaGrid.Detect(image);
            using var annotated = CaptchaGrid.Annotate(image, grid.Tiles);
            Cv2.ImEncode(".jpg", annotated, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 92));

            // 先把「这次真的要调模型了」写进日志：主日志里能看到模型名，
            // 就不用再靠「有没有报错」来反推验证码识别有没有跑。
            var primary = settings.CaptchaModel.Trim();
            var backup = settings.CaptchaFallbackModel?.Trim() ?? "";
            var modelText = backup.Length == 0 || string.Equals(backup, primary, StringComparison.OrdinalIgnoreCase)
                ? primary
                : $"{primary}（备用 {backup}）";
            _log.Info($"切出 {grid.Tiles.Count} 张候选图，调用视觉模型：{modelText}", account);

            var answer = await _vision.AskAsync(jpeg, grid.Tiles.Count, settings, ct);
            if (answer == null || !answer.IsUsable)
            {
                _log.Warn($"第 {attempt} 次：模型没给出可用编号（{answer?.Raw ?? "无响应"}），稍后重试", account);
                SaveDebug(image, annotated, "answer");
                await Task.Delay(1200, ct);
                continue;
            }

            _log.Info($"识别结果：题目「{answer.Target}」→ 第 {string.Join("、", answer.Picks)} 张"
                      + $"（{grid.Tiles.Count} 张候选，置信度 {answer.Confidence:F2}）", account);

            NativeMethods.Activate(window.Handle);
            Thread.Sleep(250);
            foreach (var index in answer.Picks)
            {
                var tile = grid.Tiles.FirstOrDefault(t => t.Index == index);
                if (tile == null)
                {
                    continue;
                }

                CaptchaWindowLocator.ClickImagePoint(window, image, tile.CenterX, tile.CenterY, grid.ContentBox);
                await Task.Delay(220, ct);
            }

            await Task.Delay(400, ct);
            var gridTop = grid.Tiles.Min(t => t.Y);
            if (!ClickConfirm(window, image, gridTop, grid.ContentBox))
            {
                _log.Warn("没找到「确定」按钮，改按回车提交", account);
                PressEnter();
            }

            await Task.Delay(2600, ct);

            if (CaptchaWindowLocator.Find(settings) == null)
            {
                _log.Success($"验证码已通过（自动，第 {attempt} 次）", account);
                return CaptchaOutcome.Solved;
            }

            _log.Warn($"第 {attempt} 次提交后验证码窗口还在，按答错换图重新识别", account);
            await Task.Delay(800, ct);
            window = CaptchaWindowLocator.Find(settings) ?? window;

            // 换图同样是异步加载的，等新图出现再进下一轮，否则会拿上一张的残影去识别。
            window = await WaitTilesReadyAsync(window, _log, account, ct);
        }

        _log.Error($"验证码连续 {maxAttempts} 次没通过，改为等待人工完成", account);
        return await WaitManualAsync(settings, account, ct);
    }

    /// <summary>
    /// 抓一帧「能切出图块」的画面。屏幕拷贝和 PrintWindow 各抓一次，取切出图块多的那个。
    /// PrintWindow 对 CEF 窗口不稳定：同一窗口有时只渲染出对话框底色、照片没画进去，
    /// 而屏幕拷贝就是用户当下看到的内容，两者互为保险。
    /// </summary>
    internal static Mat? CaptureForTiles(CaptchaWindowInfo window, LogService? log, out int tiles)
    {
        tiles = 0;

        var screen = CaptchaWindowLocator.CaptureByScreen(window, log);
        var screenTiles = CountTiles(screen);
        if (screenTiles >= 2)
        {
            tiles = screenTiles;
            return screen;
        }

        var print = CaptchaWindowLocator.CaptureByPrintWindow(window);
        var printTiles = CountTiles(print);
        if (printTiles > screenTiles)
        {
            log?.Warn($"屏幕拷贝只切出 {screenTiles} 块，改用 PrintWindow 帧（{printTiles} 块）", "验证码");
            screen?.Dispose();
            tiles = printTiles;
            return print;
        }

        print?.Dispose();
        tiles = screenTiles;
        return screen;
    }

    /// <summary>这一帧能切出多少块候选图；异常按 0 处理。</summary>
    private static int CountTiles(Mat? image)
    {
        if (image == null || image.Empty())
        {
            return 0;
        }

        try
        {
            return CaptchaGrid.Detect(image).Tiles.Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 轮询等候选图加载出来。判据是「能切出至少 2 块尺寸一致的图」，
    /// 而不是看文字——加载中的画面也有题目文字，只有真图块出现才算就绪。
    /// </summary>
    internal static async Task<CaptchaWindowInfo> WaitTilesReadyAsync(CaptchaWindowInfo window, LogService log,
        string? account, CancellationToken ct)
    {
        const int maxWaitMs = 12000;
        const int intervalMs = 400;
        var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
        var polls = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!window.IsAlive)
            {
                return window;
            }

            // 每次抓图前重读窗口矩形：窗口撑开后才不会把内容画成左上角一小块。
            window = CaptchaWindowLocator.Refresh(window);

            polls++;
            using var image = CaptureForTiles(window, null, out var tiles);
            if (tiles >= 2)
            {
                if (polls > 1)
                {
                    log.Info($"验证码图块已加载（第 {polls} 次抓图，{tiles} 块），开始识别", account ?? "验证码");
                }

                return window;
            }

            await Task.Delay(intervalMs, ct);
        }

        log.Warn($"等了 {maxWaitMs / 1000} 秒候选图仍未出现（网络慢或换了验证码样式），先按现有画面试一次",
            account ?? "验证码");
        return window;
    }

    /// <summary>等用户手动点完验证码（窗口消失即视为完成）。</summary>
    private async Task<CaptchaOutcome> WaitManualAsync(AppSettings settings, string account, CancellationToken ct)
    {
        var seconds = Math.Clamp(settings.CaptchaManualWaitSeconds, 10, 900);
        _log.Warn($"请在 {seconds} 秒内手动完成验证码，完成后程序会自动继续", account);

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(800, ct);
            if (CaptchaWindowLocator.Find(settings) == null)
            {
                _log.Success("验证码已由人工完成", account);
                return CaptchaOutcome.ManualSolved;
            }
        }

        _log.Error($"等待人工完成验证码超时（{seconds} 秒）", account);
        return CaptchaOutcome.TimedOut;
    }

    /// <summary>
    /// 点「确定」按钮。按钮是蓝色圆角块，用颜色定位；找不到返回 false（调用方退回按回车）。
    /// </summary>
    public static bool ClickConfirm(CaptchaWindowInfo window, Mat image, int maxY, Rect? renderBox = null)
    {
        var point = FindConfirmButton(image, maxY);
        if (point == null)
        {
            return false;
        }

        return CaptchaWindowLocator.ClickImagePoint(window, image, point.Value.X, point.Value.Y, renderBox);
    }

    /// <summary>
    /// 在截图里找蓝色「确定」按钮的中心点。
    /// maxY 用来限制只在图块上方找：海景、天空这类照片本身也是蓝色，不限制就会点到图里去。
    /// </summary>
    public static Point? FindConfirmButton(Mat image, int maxY = int.MaxValue)
    {
        if (image.Empty())
        {
            return null;
        }

        using var hsv = new Mat();
        Cv2.CvtColor(image, hsv, ColorConversionCodes.BGR2HSV);

        // 「确定」的蓝 #1677FF 在 HSV 里约 (105, 234, 255)；盾牌图标面积小、接近正方，靠宽高比剔除。
        using var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(95, 110, 110), new Scalar(125, 255, 255), mask);

        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        Point? best = null;
        var bestWidth = 0;
        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            if (rect.Y + rect.Height > maxY)
            {
                continue;
            }

            if (rect.Width < 44 || rect.Height < 16 || rect.Height > 90)
            {
                continue;
            }

            var ratio = (double)rect.Width / rect.Height;
            if (ratio < 1.3 || ratio > 6)
            {
                continue;
            }

            if (rect.Width > bestWidth)
            {
                bestWidth = rect.Width;
                best = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            }
        }

        return best;
    }

    /// <summary>按回车提交（部分版本的验证码弹窗也吃回车）。</summary>
    private static void PressEnter()
    {
        const byte vkReturn = 0x0D;
        NativeMethods.keybd_event(vkReturn, 0, 0, nint.Zero);
        Thread.Sleep(40);
        NativeMethods.keybd_event(vkReturn, 0, 0x0002, nint.Zero);
    }

    /// <summary>把现场图存到 User/debug，便于回头校准切图与提示词。</summary>
    private void SaveDebug(Mat image, Mat? annotated, string tag)
    {
        try
        {
            var dir = Path.Combine(PathService.UserDir, "debug");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Cv2.ImWrite(Path.Combine(dir, $"captcha-{tag}-{stamp}.png"), annotated ?? image);
        }
        catch (Exception ex)
        {
            _log.Warn($"保存验证码调试图失败：{ex.Message}", "验证码");
        }
    }
}
