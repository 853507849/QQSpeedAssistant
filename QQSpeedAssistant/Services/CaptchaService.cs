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
    TimedOut,

    /// <summary>
    /// 验证码窗口关掉了，但紧接着弹出「登录失败／推荐扫码」提示：
    /// 这是 QQ 的风控拦截，账号并没进游戏，继续自动重试只会把风控分刷得更高。
    /// </summary>
    Rejected
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
    /// <param name="gameProcessId">游戏进程号，用来认「登录失败」提示框；传 0 表示不限进程。</param>
    public async Task<CaptchaOutcome> HandleIfPresentAsync(AppSettings settings, string account, CancellationToken ct,
        int detectTimeoutMs = 12000, int gameProcessId = 0)
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

        // 模型读题读糊了（题目只剩一个字、把图全选）的次数。这种答案不算答题次数，
        // 但不能无限重问，连着几次都读不出来就直接交人工。
        var readFailures = 0;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (CaptchaWindowLocator.Find(settings) == null)
            {
                // 窗口没了也可能是被「登录失败，请扫码」那个提示顶掉的，别急着判通过。
                var settled = await ConfirmAfterSubmitAsync(settings, account, gameProcessId, ct);
                if (settled != CaptchaOutcome.NotPresent)
                {
                    if (settled == CaptchaOutcome.Solved)
                    {
                        _log.Success("验证码窗口已消失", account);
                    }

                    return settled;
                }

                window = CaptchaWindowLocator.Find(settings) ?? window;
                continue;
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

            // 标注图只留给人工看调试图，**不再送给模型**：
            // 纯红边框是全图对比度最高的元素，会把原本就很淡的藏字直接压下去。
            using var annotated = CaptchaGrid.Annotate(image, grid.Tiles);

            // 先把「这次真的要调模型了」写进日志：主日志里能看到模型名，
            // 就不用再靠「有没有报错」来反推验证码识别有没有跑。
            var primary = settings.CaptchaModel.Trim();
            var backup = settings.CaptchaFallbackModel?.Trim() ?? "";
            var modelText = backup.Length == 0 || string.Equals(backup, primary, StringComparison.OrdinalIgnoreCase)
                ? primary
                : $"{primary}（备用 {backup}）";
            _log.Info($"切出 {grid.Tiles.Count} 张候选图，逐块识别（模型：{modelText}）", account);

            // 切图报告平时不刷屏，只在「补了格子」或「剔除了异常块」时打出来：
            // 这两种情况意味着掩码切图不完全可信，事后回溯必须能看到当时的坐标。
            if (grid.Report.Contains("网格补块") || grid.Report.Contains("跳过尺寸异常"))
            {
                foreach (var line in grid.Report.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    _log.Info("  " + line.Trim(), account);
                }
            }

            var answer = await RecognizeAsync(image, grid, settings, account, ct);
            if (answer == null || !answer.IsUsable)
            {
                _log.Warn($"第 {attempt} 次：逐块判定没得到可用结果（{answer?.Raw ?? "无响应"}），稍后重试", account);
                SaveDebug(image, annotated, "answer");
                await Task.Delay(1200, ct);
                continue;
            }

            // 题目只读到一个字（「片」「长」「振」这种），或者干脆把全部候选都选了 ——
            // 这不是答案，是没读懂题。交上去必错，还会给账号多记一次风控，宁可不交。
            if (!LooksReliable(answer, grid.Tiles.Count, out var doubt))
            {
                readFailures++;
                SaveDebug(image, annotated, "unreliable");

                if (readFailures >= 3)
                {
                    _log.Error($"连续 {readFailures} 次没读出像样的题目，改按人工完成（{doubt}）", account);
                    return await WaitManualAsync(settings, account, ct);
                }

                _log.Warn($"第 {attempt} 次：{doubt}（模型给的：题目「{answer.Target}」→ "
                          + $"第 {string.Join("、", answer.Picks)} 张），这次不提交，重拍后重识别", account);

                // 读不准不算一次答题，把这次机会还回去；读不准的次数由 readFailures 单独兜底。
                attempt--;
                await Task.Delay(1400, ct);
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

            if (CaptchaWindowLocator.Find(settings) != null)
            {
                _log.Warn($"第 {attempt} 次提交后验证码窗口还在，按答错换图重新识别", account);
                await Task.Delay(800, ct);
                window = CaptchaWindowLocator.Find(settings) ?? window;

                // 换图同样是异步加载的，等新图出现再进下一轮，否则会拿上一张的残影去识别。
                window = await WaitTilesReadyAsync(window, _log, account, ct);
                continue;
            }

            // 窗口关了不等于过码：QQ 也可能把它换成「登录失败，推荐扫码」的提示框。
            var confirm = await ConfirmAfterSubmitAsync(settings, account, gameProcessId, ct);
            if (confirm == CaptchaOutcome.Rejected)
            {
                return CaptchaOutcome.Rejected;
            }

            if (confirm == CaptchaOutcome.NotPresent)
            {
                // 验证码换图重开了，按答错处理。
                _log.Warn($"第 {attempt} 次提交后验证码重新弹出，按答错换图重新识别", account);
                window = CaptchaWindowLocator.Find(settings) ?? window;
                window = await WaitTilesReadyAsync(window, _log, account, ct);
                continue;
            }

            _log.Success($"验证码已通过（自动，第 {attempt} 次）", account);
            return CaptchaOutcome.Solved;
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
    /// 两帧画面是否基本一致：缩到 64×64 灰度后比平均像素差。
    /// 用来判断换图动画结束了没有——动画中途抓到的题目会缺字。
    /// </summary>
    private static bool IsSameFrame(Mat? a, Mat? b)
    {
        if (a == null || b == null || a.Empty() || b.Empty())
        {
            return false;
        }

        try
        {
            using var grayA = new Mat();
            using var grayB = new Mat();
            Cv2.CvtColor(a, grayA, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(b, grayB, ColorConversionCodes.BGR2GRAY);

            using var smallA = new Mat();
            using var smallB = new Mat();
            Cv2.Resize(grayA, smallA, new Size(64, 64));
            Cv2.Resize(grayB, smallB, new Size(64, 64));

            using var diff = new Mat();
            Cv2.Absdiff(smallA, smallB, diff);
            return Cv2.Mean(diff).Val0 < 6;
        }
        catch
        {
            // 比例尺或尺寸异常时不要卡死流程，按「一致」放行，让后面正常的识别去判断。
            return true;
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
                // 切出图块 ≠ 画面画完：换图后题目文字还在动画里，抓到半截就会被读成「片」「长」这种单字，
                // 拿这种答案去提交必错，还给账号记一次风控。隔一会儿再抓一帧，两帧基本一致才算就绪。
                await Task.Delay(350, ct);
                using var again = CaptureForTiles(window, null, out var tilesAgain);
                if (tilesAgain == tiles && IsSameFrame(image, again))
                {
                    if (polls > 1)
                    {
                        log.Info($"验证码画面已就绪（第 {polls} 次抓图，{tiles} 块），开始识别", account ?? "验证码");
                    }

                    return window;
                }

                continue;
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

    /// <summary>
    /// 新的识别链路：先把题目区单独裁出来送去读字，再把每张候选图放大后单独问「符不符合」。
    /// 相比原来「整图一次问完」有两处本质区别：
    /// ① 每张图独占分辨率预算——整图 563x540 折合约 380 个视觉 token，六块平分后每块只剩 25 个，
    ///    藏字题的那个字连一个 token 都摊不到；单块放大 4 倍后能拿到约 400 个。
    /// ② 多选退化成多次二分类——小参数量模型做「先抄题、再逐张比、再聚合」会退化，
    ///    直接吐最省事的答案（全选）；只问一张图是或不是，它反而答得实。
    /// </summary>
    private async Task<CaptchaAnswer?> RecognizeAsync(Mat image, CaptchaGrid.Result grid, AppSettings settings,
        string account, CancellationToken ct)
    {
        using var questionImage = CaptchaGrid.CropQuestion(image, grid);
        if (questionImage == null)
        {
            _log.Warn("题目区裁不出来（切图边界异常），本轮跳过", account);
            return null;
        }

        var question = await _vision.ReadQuestionAsync(CaptchaGrid.EncodePng(questionImage), settings, ct);
        if (string.IsNullOrWhiteSpace(question))
        {
            _log.Warn("读题没有返回内容", account);
            return null;
        }

        _log.Info($"读题：{question}", account);

        // 题目分两类，逐块提问的方式不同：藏字题问「辨认到什么字」，语义题问「画面里最主要的东西」。
        // 两类都不能把题目里的目标词抖给模型，否则小模型会顺着题目答（把海滩说成「绵羊」）。
        var literal = ExtractLiteral(question);
        var lookForText = literal != null;

        var verdicts = new List<TileVerdict>();
        foreach (var tile in grid.Tiles)
        {
            ct.ThrowIfCancellationRequested();

            using var tileImage = CaptchaGrid.CropTile(image, tile, 4);
            if (tileImage == null)
            {
                _log.Warn($"第 {tile.Index} 张裁不出来，跳过判定", account);
                continue;
            }

            var verdict = await _vision.CheckTileAsync(CaptchaGrid.EncodePng(tileImage), lookForText, tile.Index,
                settings, ct);
            if (verdict != null)
            {
                verdicts.Add(verdict);
            }
        }

        if (verdicts.Count == 0)
        {
            _log.Warn("六张候选图一张都没判出来", account);
            return null;
        }

        // 命中判定一律由程序做，不看模型给的 hit：对「符不符合」这种抽象判断，3B 会敷衍——
        // 实测它把提示词示例里的 "hit":true 原样抄回来，六块全「符合」，程序只好按置信度挑一张，
        // 结果每次都点置信度并列最高的第一张。让它只「报看到什么」再由程序比对，才有区分度。
        var judged = ApplyQuestionMatch(verdicts, question);

        // 匹配用的目标词：藏字题取引号里的字（「个」），语义题取冒号后的描述（「绵羊」）。
        var target = literal ?? ExtractTarget(question);
        if (target.Length == 0)
        {
            _log.Warn($"题目里取不出要比对的目标词（原文「{question}」），本轮跳过", account);
            return null;
        }

        _log.Info($"比对目标：「{target}」" + (lookForText ? "（藏字题，按字面匹配）" : "（语义题，按画面描述匹配）"), account);

        _log.Info("逐块判定：" + string.Join("，", judged.OrderBy(v => v.Index)
            .Select(v => $"#{v.Index} {(v.Hit ? "符合" : "不符")}"
                         + $"{(v.Seen.Length > 0 ? $"「{v.Seen}」" : "「」")}"
                         + $"(匹配度 {MatchStrength(v.Seen, target)})")), account);

        var answer = Decide(question, verdicts);
        if (answer == null)
        {
            _log.Warn($"题目里取不出要比对的目标词（原文「{question}」），本轮跳过", account);
            return null;
        }

        // 题目问「最符合」是单选，多张命中说明判定不够尖，只能取匹配最紧的那张。
        var matched = judged.Count(v => v.Hit);
        if (matched > 1 && answer.Picks.Count == 1 && !IsMultiSelect(question))
        {
            _log.Info($"题目是单选但有 {matched} 张命中，只取匹配最紧的第 {answer.Picks[0]} 张", account);
        }

        return answer;
    }

    /// <summary>
    /// 由「每张图报回来的内容」+ 题目，算出最终该点哪几张。
    /// 提取成独立函数是为了让自检（设置页「测试识别」/ --captchacheck）走同一套判定，
    /// 而不是各写一份、又慢慢跑偏。返回 null 表示题目里取不出要比对的目标词。
    /// </summary>
    public static CaptchaAnswer? Decide(string question, IReadOnlyList<TileVerdict> verdicts)
    {
        var target = ExtractLiteral(question) ?? ExtractTarget(question);
        if (target.Length == 0)
        {
            return null;
        }

        var judged = ApplyQuestionMatch(verdicts, question);

        // 多张命中时按「匹配得多紧」排：完全同名最优先，其次描述里包含目标词，最后才看模型自报的置信度。
        // 原来只按置信度排，而 3B 对同类画面给的置信度完全相同，并列时稳定排序就永远落到第 1 张。
        var hits = judged.Where(v => v.Hit)
            .OrderByDescending(v => MatchStrength(v.Seen, target))
            .ThenBy(v => v.Seen.Length)
            .ThenByDescending(v => v.Confidence)
            .ToList();

        if (hits.Count == 0)
        {
            return new CaptchaAnswer(question, Array.Empty<int>(), 0, "六张候选图都判为不符合");
        }

        // 题目问「最符合」是单选，多张命中只能取匹配最紧的那张；
        // 只有「所有符合」「哪些」这类问法才允许多选。
        if (!IsMultiSelect(question) && hits.Count > 1)
        {
            hits = [hits[0]];
        }

        return new CaptchaAnswer(question, hits.Select(h => h.Index).ToList(), hits[0].Confidence,
            string.Join(" ; ", judged.Select(v => v.Raw)));
    }

    /// <summary>题目是不是「选出所有符合的」这种多选问法。</summary>
    private static bool IsMultiSelect(string question)
        => question.Contains("所有", StringComparison.Ordinal)
           || question.Contains("哪些", StringComparison.Ordinal)
           || question.Contains("全部", StringComparison.Ordinal);

    /// <summary>
    /// 从题目里抠出「包含文字 "X"」要匹配的那个字。
    /// 模型抄回来的原文可能写成「包含文字：个」，也可能带各种引号，这里把它们都去掉。
    /// 抠不出就返回 null，说明这是语义题，按画面描述比对（<see cref="ExtractTarget"/>）。
    /// </summary>
    public static string? ExtractLiteral(string question)
    {
        var idx = question.IndexOf("文字", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var tail = question[(idx + 2)..].Trim()
            .TrimStart('：', ':', ' ', '"', '\'', '「', '『', '“', '‘')
            .TrimEnd(' ', '"', '\'', '」', '』', '”', '’', '。', '？', '?');

        return tail.Length is > 0 and <= 4 ? tail : null;
    }

    /// <summary>
    /// 模型报回来的 Seen 是不是「它辨认到的字」。
    /// 模型有两种输出：报字（"个"），以及描述画面（"一个湖，周围有山和树木"）。
    /// 后者带着量词「个」，直接 Contains 会把画面描述全判成命中——实测六张里有四张描述含「一个」。
    /// 所以先按长度和标点把这两种输出分开。
    /// </summary>
    private static bool IsLiteralReport(string seen)
    {
        var text = (seen ?? "").Trim();
        if (text.Length is 0 or > 6)
        {
            return false;
        }

        return text.IndexOfAny(['，', '、', '。', ',', '.', ' ', '和', '与', '的', '有']) < 0;
    }

    /// <summary>
    /// 从语义题里取出要比对的目标描述：取最后一个冒号之后那段。
    /// 「选择最符合描述的图片：绵羊」→「绵羊」；模型把整行都抄回来的话，再按标点截断。
    /// </summary>
    public static string ExtractTarget(string question)
    {
        var text = (question ?? "").Trim();
        if (text.Length == 0)
        {
            return "";
        }

        var idx = text.LastIndexOfAny(['：', ':']);
        var tail = (idx >= 0 ? text[(idx + 1)..] : text).Trim();

        // 模型抄回来的原文常带引号／句号，先剥掉；冒号后面若还有逗号句号，只取到第一个标点。
        tail = tail.Trim(' ', '"', '\'', '“', '”', '「', '」', '『', '』', '。', '，', '？', '?', '!', '！');
        var cut = tail.IndexOfAny(['，', '。', '；', ';', '、', '（', '(', ' ']);
        if (cut > 0)
        {
            tail = tail[..cut];
        }

        tail = tail.Trim();
        return tail.Length <= 12 ? tail : tail[..12];
    }

    /// <summary>
    /// 目标词与「模型报回来看到的内容」的匹配强度，0 表示不匹配。
    /// 3 完全同名；2 描述里包含目标且描述不长；1 只是沾边（长描述里出现，或目标里包含描述）。
    /// 之所以还要分强度：单选题目多张命中时，得靠它挑出最贴题的那张，而不是靠模型瞎给的置信度。
    /// </summary>
    public static int MatchStrength(string seen, string target)
    {
        var s = (seen ?? "").Trim();
        var t = (target ?? "").Trim();
        if (s.Length == 0 || t.Length == 0)
        {
            return 0;
        }

        if (string.Equals(s, t, StringComparison.Ordinal))
        {
            return 3;
        }

        if (s.Contains(t, StringComparison.Ordinal))
        {
            // 「一只绵羊」这种短描述算紧匹配；「蓝天下有一片麦田和几只鸟」这种长句只算沾边。
            return s.Length <= Math.Max(4, t.Length * 2) ? 2 : 1;
        }

        // 目标比描述更长时（题目写「一只绵羊」，模型只报「绵羊」）反向包含也算沾边。
        if (t.Contains(s, StringComparison.Ordinal) && s.Length >= 2)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// 把模型「看到的内容」按题目要求折算成命中判定。
    /// 藏字题走字面匹配（要求模型报的是字而不是画面描述），语义题走目标词匹配。
    /// 两条路都不采信模型自己给的 hit。公开出来是为了让自检能单独验证这一步（它不依赖模型）。
    /// </summary>
    public static IReadOnlyList<TileVerdict> ApplyQuestionMatch(IReadOnlyList<TileVerdict> verdicts, string question)
    {
        var literal = ExtractLiteral(question);
        if (literal != null)
        {
            return verdicts
                .Select(v => v with
                {
                    Hit = IsLiteralReport(v.Seen) && v.Seen.Contains(literal, StringComparison.Ordinal)
                })
                .ToList();
        }

        var target = ExtractTarget(question);
        if (target.Length == 0)
        {
            return verdicts;
        }

        return verdicts
            .Select(v => v with { Hit = MatchStrength(v.Seen, target) > 0 })
            .ToList();
    }

    /// <summary>
    /// 模型给的答案像不像话。注意 QQ 有一类题是「选择所有符合描述的图片：包含文字 "个"」，
    /// 题目本身就是一个汉字，所以不能拿字数当判据（早先那版就是拿「字数 &lt; 2」判，会误杀这类题）。
    /// 只保留两条明确不成立的：题目没读出来，以及把候选全选上。
    /// </summary>
    private static bool LooksReliable(CaptchaAnswer answer, int tileCount, out string reason)
    {
        var target = (answer.Target ?? "").Trim();

        if (target.Length == 0)
        {
            reason = "题目是空的，等于没读出题目";
            return false;
        }

        if (answer.Picks.Count >= tileCount)
        {
            reason = $"{tileCount} 张候选全选了，题目再刁也不会全对";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// 提交后确认结果。验证码窗口已经消失时调用：
    /// 既可能真过了，也可能 QQ 直接换成「登录失败，推荐使用手机QQ扫码登录」的提示框。
    /// 后者是风控拦截，账号根本没进游戏，必须当场认出来，不能按成功往下跑。
    /// 返回 Solved＝没发现异常；Rejected＝被拦下；NotPresent＝验证码又弹回来了（当作答错）。
    /// </summary>
    private async Task<CaptchaOutcome> ConfirmAfterSubmitAsync(AppSettings settings, string account,
        int gameProcessId, CancellationToken ct)
    {
        // 提示框一般在验证码关掉后一两秒内出现，盯 4 秒足够，也不至于让每次过码都白等太久。
        var deadline = DateTime.UtcNow.AddSeconds(4);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (CaptchaWindowLocator.Find(settings) != null)
            {
                return CaptchaOutcome.NotPresent;
            }

            var notice = LoginRejectWatcher.Find(gameProcessId);
            if (notice != null)
            {
                _log.Error($"验证码关掉后紧接着弹出「{notice.Title}」对话框（{notice.Width}x{notice.Height}）——"
                           + "QQ 判定本次登录异常，正文通常就是「当前网络不稳定，登录失败。推荐使用手机QQ扫码登录」。"
                           + "这不是密码或验证码答错，是风控；本号本次不再自动重试，继续试只会把风控分越刷越高", account);

                SaveRejectShot(notice);
                return CaptchaOutcome.Rejected;
            }

            await Task.Delay(500, ct);
        }

        return CaptchaOutcome.Solved;
    }

    /// <summary>把「登录失败」提示框截一张存到 debug 目录，方便事后核对到底弹的是哪句话。</summary>
    private void SaveRejectShot(LoginRejectNotice notice)
    {
        try
        {
            var info = new CaptchaWindowInfo(notice.Handle, notice.Title, notice.Left, notice.Top,
                notice.Width, notice.Height);
            using var shot = CaptchaWindowLocator.CaptureByScreen(info, null);
            if (shot == null || shot.Empty())
            {
                return;
            }

            var dir = Path.Combine(PathService.UserDir, "debug");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Cv2.ImWrite(Path.Combine(dir, $"login-rejected-{stamp}.png"), shot);
        }
        catch (Exception ex)
        {
            _log.Warn($"保存登录被拒截图失败：{ex.Message}", "验证码");
        }
    }

    /// <summary>保存验证码调试图。</summary>
    private void SaveDebug(Mat image, Mat? annotated, string tag)
    {
        try
        {
            var dir = Path.Combine(PathService.UserDir, "debug");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Cv2.ImWrite(Path.Combine(dir, $"captcha-{tag}-{stamp}.png"), annotated ?? image);

            // 同时留一份未标注的原图。标注图上的红框会被切图当成内容，反复校准切图时要用真正的原始画面。
            if (annotated != null)
            {
                Cv2.ImWrite(Path.Combine(dir, $"captcha-{tag}-{stamp}-raw.png"), image);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"保存验证码调试图失败：{ex.Message}", "验证码");
        }
    }
}
