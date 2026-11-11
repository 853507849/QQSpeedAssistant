using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>登录结果。</summary>
public sealed record LoginResult(bool Success, AccountRunStatus Status, string Message)
{
    /// <summary>登录成功。</summary>
    public static LoginResult Ok() => new(true, AccountRunStatus.Completed, "");

    /// <summary>登录失败。</summary>
    public static LoginResult Fail(AccountRunStatus status, string message) => new(false, status, message);
}

/// <summary>
/// 账号登录，三条通道依次尝试：
/// ① 快速登录（flows/login_fast.json，复用 QQ 客户端登录态点客户端头像，不输密码）；
/// ② UIA 直接填表（不发键鼠事件，绕过反外挂过滤）；
/// ③ 键鼠流程（flows/login.json）。
/// 登录前可按配置先跑 flows/login_server.json 选大区；密码错误／冻结／验证码／环境异常按各自阈值换号。
/// </summary>
public sealed class AccountLoginService
{
    /// <summary>
    /// 登录流程跑完后，最多再等这么久来确认真的进了大厅（秒）。
    /// 正常情况下大厅模板一命中就立刻返回，不会等满；只有卡在登录界面/验证码时才会用到这里。
    /// </summary>
    private const int CaptchaSettleSeconds = 40;

    private readonly FlowLoader _flows;
    private readonly FlowRunner _runner;
    private readonly TemplateMatcher _matcher;
    private readonly LogService _log;
    private readonly CaptchaService _captcha;

    /// <summary>构造登录服务。</summary>
    public AccountLoginService(FlowLoader flows, FlowRunner runner, TemplateMatcher matcher, LogService log,
        CaptchaService captcha)
    {
        _flows = flows;
        _runner = runner;
        _matcher = matcher;
        _log = log;
        _captcha = captcha;
    }

    /// <summary>登录指定账号到指定会话。</summary>
    public async Task<LoginResult> LoginAsync(GameSession session, AccountEntry account, AppSettings settings, CancellationToken ct)
    {
        if (session.IsLoggedIn && ReferenceEquals(session.BoundAccount, account))
        {
            return LoginResult.Ok();
        }

        // 登录前检查：账号没填就别去点「登录」，否则游戏只会弹一个提示框。
        // 开了快速登录时密码可以为空（走 QQ 客户端登录态，不需要密码）。
        var requirePassword = !(settings.FastLoginEnabled && _flows.Exists("login_fast"));
        if (string.IsNullOrWhiteSpace(account.Account))
        {
            _log.Error("登录前检查：账号为空，已跳过（请先在「账号」页补全）", account.Account);
            return LoginResult.Fail(AccountRunStatus.Skipped, "账号为空");
        }

        if (requirePassword && string.IsNullOrEmpty(account.Password))
        {
            _log.Error("登录前检查：密码为空，已跳过（请先在「账号」页补全，或开启快速登录）", account.Account);
            return LoginResult.Fail(AccountRunStatus.Skipped, "密码为空");
        }

        // 注意：登录界面保持原尺寸（登录框的模板是按登录窗口采的）；
        // 登录成功后也不再改窗口——用户选了「窗口模式 + 800x600(32位色)」，窗口本来就对。
        // 下面几处 EnsureGameClientSize() 只做尺寸核对与提示，不会动窗口。
        var server = ResolveServer(account, settings);
        await SelectServerAsync(session, account, server, settings, ct);

        // 通道一：快速登录
        if (settings.FastLoginEnabled && await TryFastLoginAsync(session, account, settings, ct))
        {
            session.MarkLoggedIn();
            _log.Success("登录成功（快速登录，未输入密码）", account.Account);
            session.EnsureGameClientSize();
            return LoginResult.Ok();
        }

        // 通道二：UIA 直接填表
        try
        {
            if (await TryUiaLoginAsync(session, account, settings, ct))
            {
                session.MarkLoggedIn();
                _log.Success("登录成功（UIA 直接填表，未使用鼠标键盘）", account.Account);
                session.EnsureGameClientSize();
                return LoginResult.Ok();
            }
        }
        catch (FlowException ex) when (ex.Reason == FlowFailReasons.EnvAbnormal)
        {
            _log.Error("QQ 拦下了本次登录：过码之后弹出「登录失败／推荐扫码」提示。"
                       + "这是风控不是密码问题，这个号本次不再自动重试；"
                       + "建议先用手机QQ扫码登录一次，把登录态和常用环境养回来", account.Account);
            return LoginResult.Fail(AccountRunStatus.EnvAbnormal, ex.Message);
        }

        // 通道三：键鼠流程
        var flow = _flows.Load("login");
        if (flow == null)
        {
            return LoginResult.Fail(AccountRunStatus.FlowMissing,
                "缺少 flows/login.json，请先按自己客户端校准登录流程");
        }

        var maxRetries = Math.Clamp(settings.MaxPasswordRetries, 0, 10);
        var captchaLimit = Math.Clamp(settings.CaptchaMaxAttempts, 1, 6);
        var freezeCount = 0;
        var envAbnormalCount = 0;
        var captchaRecoveries = 0;
        var attempt = 1;

        while (attempt <= maxRetries + 1)
        {
            ct.ThrowIfCancellationRequested();
            var context = new FlowContext { Account = account, ModeId = "login" };
            context.Variables["server"] = server;

            FlowException? error = null;
            try
            {
                _log.Info($"开始登录（第 {attempt} 次）", account.Account);
                await _runner.ExecuteAsync(flow, session, context, ct);

                // 流程跑完 ≠ 真进游戏了。QQ 的验证码是点登录之后异步弹的（实测能晚二十多秒），
                // 弹窗盖在画面上时，流程里「等登录按钮消失」「等大厅」这些步骤只会一路对不上但仍继续，
                // 最后照样返回成功 —— 后面玩法的模板全 0 相似度就是这么来的。
                // 所以补一段收尾确认：一边等大厅画面，一边接住这期间弹出的验证码。
                var pending = await WaitForHallOrCaptchaAsync(session, account, settings, ct);

                if (pending == CaptchaOutcome.TimedOut)
                {
                    _log.Error("验证码未通过（人工等待超时），本次登录判失败", account.Account);
                    return LoginResult.Fail(AccountRunStatus.Captcha, "验证码未通过");
                }

                if (pending == CaptchaOutcome.Rejected)
                {
                    _log.Error("QQ 拦下了本次登录：过码之后弹出「登录失败／推荐扫码」提示，"
                               + "这是风控不是密码问题。已停止这个号的自动重试，别再连点；"
                               + "建议先用手机QQ扫码登录一次，把登录态和常用环境养回来", account.Account);
                    return LoginResult.Fail(AccountRunStatus.EnvAbnormal, "登录被风控拦截（提示登录失败／请扫码）");
                }

                if (pending is CaptchaOutcome.Solved or CaptchaOutcome.ManualSolved)
                {
                    _log.Success("登录后的验证码已通过", account.Account);
                    // 过码时验证码窗口抢走了前台焦点，拿回来再继续。
                    session.Activate();
                }

                session.MarkLoggedIn();
                _log.Success("登录成功", account.Account);
                session.EnsureGameClientSize();
                return LoginResult.Ok();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FlowException ex)
            {
                error = ex;
            }

            // 验证码弹窗盖在游戏上面时，后面的模板全对不上，流程会以各种理由报错。
            // 所以先查有没有验证码：处理掉了就原地重来，这一次不算密码重试次数。
            if (captchaRecoveries < captchaLimit)
            {
                var outcome = await TryHandleCaptchaAsync(settings, account.Account, ct, 2500, session.ProcessId);
                if (outcome is CaptchaOutcome.Solved or CaptchaOutcome.ManualSolved)
                {
                    captchaRecoveries++;
                    _log.Info($"验证码已通过，重新执行登录流程（{captchaRecoveries}/{captchaLimit}）", account.Account);
                    continue;
                }

                if (outcome == CaptchaOutcome.Rejected)
                {
                    _log.Error("QQ 拦下了本次登录（过码后弹出「登录失败／推荐扫码」），这是风控不是密码问题；"
                               + "已停止这个号的自动重试，建议先用手机QQ扫码登一次", account.Account);
                    return LoginResult.Fail(AccountRunStatus.EnvAbnormal, "登录被风控拦截（提示登录失败／请扫码）");
                }

                if (outcome == CaptchaOutcome.TimedOut)
                {
                    _log.Error("验证码未通过（人工等待超时），切换到下一个账号", account.Account);
                    return LoginResult.Fail(AccountRunStatus.Captcha, "验证码未通过");
                }
            }

            switch (error!.Reason)
            {
                case FlowFailReasons.WrongPassword:
                    if (attempt > maxRetries)
                    {
                        _log.Error($"密码错误已达 {attempt} 次，切换到下一个账号", account.Account);
                        return LoginResult.Fail(AccountRunStatus.PasswordFailed, $"密码错误 {attempt} 次");
                    }

                    _log.Warn($"密码错误，2 秒后重试（{attempt}/{maxRetries + 1}）", account.Account);
                    await Task.Delay(2000, ct);
                    attempt++;
                    break;

                case FlowFailReasons.Frozen:
                    freezeCount++;
                    var freezeThreshold = Math.Max(1, settings.FreezeSwapThreshold);
                    if (freezeCount >= freezeThreshold)
                    {
                        _log.Error($"冻结提示已出现 {freezeCount} 次，切换到下一个账号", account.Account);
                        return LoginResult.Fail(AccountRunStatus.Frozen, $"账号冻结（提示 {freezeCount} 次）");
                    }

                    _log.Warn($"出现冻结提示（{freezeCount}/{freezeThreshold}），等待后重试", account.Account);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, settings.TopUpDelaySeconds)), ct);
                    attempt++;
                    break;

                case FlowFailReasons.Captcha:
                    _log.Error("登录需要验证码且未能自动通过，切换到下一个账号", account.Account);
                    return LoginResult.Fail(AccountRunStatus.Captcha, "验证码拦截");

                case FlowFailReasons.EnvAbnormal:
                    envAbnormalCount++;
                    var envThreshold = Math.Max(1, settings.EnvAbnormalSwapThreshold);
                    if (envAbnormalCount >= envThreshold)
                    {
                        _log.Error($"环境异常提示已出现 {envAbnormalCount} 次，切换到下一个账号（建议改用快速登录）", account.Account);
                        return LoginResult.Fail(AccountRunStatus.EnvAbnormal, $"环境异常（提示 {envAbnormalCount} 次）");
                    }

                    _log.Warn($"环境异常提示（{envAbnormalCount}/{envThreshold}），等待后重试；建议开启快速登录", account.Account);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, settings.TopUpDelaySeconds)), ct);
                    attempt++;
                    break;

                default:
                    _log.Error($"登录流程失败：{error.Message}", account.Account);
                    return LoginResult.Fail(AccountRunStatus.LoginTimeout, error.Message);
            }
        }

        return LoginResult.Fail(AccountRunStatus.PasswordFailed, "密码错误");
    }

    /// <summary>检测并处理登录验证码：没有验证码时立即返回 NotPresent（不阻塞流程）。</summary>
    private Task<CaptchaOutcome> TryHandleCaptchaAsync(AppSettings settings, string account, CancellationToken ct,
        int detectTimeoutMs, int gameProcessId = 0)
        => _captcha.HandleIfPresentAsync(settings, account, ct, detectTimeoutMs, gameProcessId);

    /// <summary>
    /// 登录流程跑完后的收尾确认：一边等大厅画面出现，一边接住这期间异步弹出的验证码。
    /// 大厅模板命中就立刻返回（正常登录只多抓一帧），不会把每个号都拖慢几十秒；
    /// 模板不存在时退化成固定观察窗，至少能覆盖大部分异步弹窗。
    /// </summary>
    private async Task<CaptchaOutcome> WaitForHallOrCaptchaAsync(GameSession session, AccountEntry account,
        AppSettings settings, CancellationToken ct)
    {
        var hasHallTemplate = _matcher.TemplateExists("hall_main");
        var deadline = DateTime.UtcNow.AddSeconds(hasHallTemplate ? CaptchaSettleSeconds : 8);
        var solved = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // 有验证码就先处理：弹窗盖在画面上时，大厅判定永远不可能命中。
            if (CaptchaWindowLocator.Find(settings) != null)
            {
                var outcome = await _captcha.HandleIfPresentAsync(settings, account.Account, ct, 1500, session.ProcessId);
                if (outcome is CaptchaOutcome.TimedOut or CaptchaOutcome.Rejected)
                {
                    return outcome;
                }

                solved |= outcome is CaptchaOutcome.Solved or CaptchaOutcome.ManualSolved;
                continue;
            }

            // 验证码没了但也没进大厅：先看是不是顶上来了「登录失败／请扫码」的提示框。
            // 这个框是风控拦截，跟密码、验证码都无关，必须当场认出来，别继续跑玩法。
            var notice = LoginRejectWatcher.Find(session.ProcessId);
            if (notice != null)
            {
                _log.Error($"弹出「{notice.Title}」对话框（{notice.Width}x{notice.Height}）："
                           + "QQ 判定本次登录异常，账号并没进游戏。已停止这个号的自动重试", account.Account);
                return CaptchaOutcome.Rejected;
            }

            if (_matcher.TemplateExists("login_failed_notice"))
            {
                using var noticeFrame = session.CaptureFrame();
                if (noticeFrame != null && !noticeFrame.Empty()
                    && _matcher.Find(noticeFrame, "login_failed_notice", 0.85).Found)
                {
                    _log.Error("画面命中「login_failed_notice」模板：登录失败提示框，账号并没进游戏", account.Account);
                    return CaptchaOutcome.Rejected;
                }
            }

            if (hasHallTemplate)
            {
                using var frame = session.CaptureFrame();
                if (frame != null && !frame.Empty() && _matcher.Find(frame, "hall_main", 0.8).Found)
                {
                    return solved ? CaptchaOutcome.Solved : CaptchaOutcome.NotPresent;
                }
            }

            await Task.Delay(800, ct);
        }

        // 等不到大厅，最后再确认一次有没有被风控顶掉：有就判失败，别把一个没登录的客户端拖进玩法。
        var lastNotice = LoginRejectWatcher.Find(session.ProcessId);
        if (lastNotice != null)
        {
            _log.Error($"等到超时也没进大厅，屏幕上却挂着「{lastNotice.Title}」对话框 —— 登录已被拦下", account.Account);
            return CaptchaOutcome.Rejected;
        }

        // 顺手把游戏进程下的小窗口记进日志：下次再遇到「登录失败」提示时，
        // 看一眼这行就知道它到底是独立窗口（标题是什么），还是直接画在游戏画面里的。
        var scan = LoginRejectWatcher.Scan(session.ProcessId);
        if (scan.Count > 0)
        {
            _log.Info("游戏进程下的可见小窗口：" + string.Join("；", scan.Select(w => $"「{w.Title}」{w.Width}x{w.Height}")),
                account.Account);
        }

        if (hasHallTemplate)
        {
            _log.Warn($"{CaptchaSettleSeconds} 秒内没等到大厅画面，可能仍卡在登录界面；先按登录成功继续", account.Account);
        }

        return solved ? CaptchaOutcome.Solved : CaptchaOutcome.NotPresent;
    }

    /// <summary>
    /// 快速登录：复用 QQ 客户端的登录态，在客户端上直接点头像进入游戏。
    /// 流程跑完还要校验一次大厅模板，确认真的进去了才返回成功。
    /// </summary>
    private async Task<bool> TryFastLoginAsync(GameSession session, AccountEntry account, AppSettings settings, CancellationToken ct)
    {
        var flow = _flows.Load("login_fast");
        if (flow == null)
        {
            return false;
        }

        try
        {
            var context = new FlowContext { Account = account, ModeId = "login_fast" };
            context.Variables["server"] = ResolveServer(account, settings);
            await _runner.ExecuteAsync(flow, session, context, ct);
            session.MarkLoggedIn();
            return true;
        }
        catch (FlowException ex)
        {
            _log.Info($"快速登录不可用（{ex.Message}），改用常规登录", account.Account);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>登录前选大区：按大区名找对应的 flows/login_server_xxx.json。</summary>
    private async Task SelectServerAsync(GameSession session, AccountEntry account, string server, AppSettings settings,
        CancellationToken ct)
    {
        if (!settings.SelectServerBeforeLogin || string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        var flowName = ServerFlowName(server);
        var flow = _flows.Load(flowName);
        if (flow == null)
        {
            _log.Info($"没有 flows/{flowName}.json，跳过选择大区", account.Account);
            return;
        }

        try
        {
            var context = new FlowContext { Account = account, ModeId = flowName };
            context.Variables["server"] = server;
            await _runner.ExecuteAsync(flow, session, context, ct);

            // 流程跑完不代表真的切换了：模板缺失时步骤会被跳过，此时不能报「已选择大区」。
            if (context.ClickCount == 0)
            {
                _log.Info($"大区选择流程没有点到任何按钮（模板缺失或未匹配），沿用当前大区（目标：{server}）", account.Account);
            }
            else
            {
                _log.Info($"已选择大区：{server}", account.Account);
            }
        }
        catch (FlowException ex)
        {
            // 选大区失败不该拦住登录：登录界面多数情况会沿用上次的大区
            _log.Warn($"选择大区「{server}」失败：{ex.Message}（继续登录）", account.Account);
        }
    }

    /// <summary>把账号文本里的大区写法映射到流程名。</summary>
    private static string ServerFlowName(string server)
    {
        var text = server.Trim();
        if (text.Contains('二') || text.Contains("2"))
        {
            return "login_server_dianxin2";
        }

        if (text.Contains("联通"))
        {
            return "login_server_liantong";
        }

        if (text.Contains("网通"))
        {
            return "login_server_wangtong";
        }

        if (text.Contains("电信") || text.Contains("电一") || text.Contains("电1"))
        {
            return "login_server_dianxin";
        }

        return "login_server_dianxin";
    }

    /// <summary>账号自身的区服优先，其次用设置里的默认大区。</summary>
    private static string ResolveServer(AccountEntry account, AppSettings settings)
        => string.IsNullOrWhiteSpace(account.Server) ? settings.DefaultServer?.Trim() ?? "" : account.Server.Trim();

    /// <summary>抓屏坐标上的一块矩形区域。</summary>
    private readonly record struct FieldRegion(int X, int Y, int Width, int Height)
    {
        public override string ToString() => $"({X},{Y},{Width}x{Height})";
    }

    /// <summary>
    /// 用 UI Automation 填表并提交。四道关都必须过才算成功：
    /// ① 提交前确认画面确实是登录界面；② 定位到账号框、密码框各自的像素区域；
    /// ③ 两块框各自出现文字且提交前仍在；④ 提交后登录按钮连续多次消失。
    /// 任一关不过就返回 false，交给键鼠流程处理（绝不凭空判定成功）。
    /// </summary>
    private async Task<bool> TryUiaLoginAsync(GameSession session, AccountEntry account, AppSettings settings,
        CancellationToken ct)
    {
        // 先把窗口切到前台：后台窗口抓屏可能拿到过期画面，会让“有没有写进去”判断失真；
        // 而且真实点击与真实按键都要求窗口在前台，否则点击会落到别的窗口上。
        if (!session.Activate())
        {
            _log.Warn("UIA：无法把游戏窗口切到前台，改用键鼠流程", account.Account);
            return false;
        }

        // 登录界面底部勾「窗口模式」并把分辨率选成 800x600(32位色)，进游戏后画面才是 800x600。
        if (UiaAutomationService.EnsureWindowedResolution(session.Handle, _log))
        {
            Thread.Sleep(400);
        }

        // ① 前置：画面必须真的是登录界面（否则后面的“按钮消失”毫无意义）。
        var buttonBefore = await FindTemplateAsync(session, "login_button", 0.85, 6000, ct);
        if (buttonBefore == null)
        {
            _log.Warn("画面里没有「登录」按钮，判定当前不是登录界面，改用键鼠流程", account.Account);
            return false;
        }

        // ② 定抓屏区域：优先用 UIA 控件自己的位置（精确到像素，账号框／密码框不会搞混），
        //    拿不到才退回模板匹配。这样即使没采账号框／密码框模板，UIA 通道也能用。
        var rects = UiaAutomationService.LocateInputRects(session.Handle, _log);
        var accountRegion = ResolveRegion(session, rects.Account, "login_account_box", account.Account);
        var passwordRegion = ResolveRegion(session, rects.Password, "login_password_box", account.Account);
        if (accountRegion == null || passwordRegion == null)
        {
            _log.Warn("没定位到账号／密码输入框区域，无法校验 UIA 是否写入，改用键鼠流程", account.Account);
            return false;
        }

        _log.Info($"UIA 校验区域（抓屏坐标）：账号框 {accountRegion.Value}、密码框 {passwordRegion.Value}",
            account.Account);

        // 两块框分开抓、分开比：以前合成一个 InputChange，密码没写进去也会被账号的像素盖住。
        FieldSnapshot? Capture(FieldKind kind)
        {
            var region = kind == FieldKind.Account ? accountRegion.Value : passwordRegion.Value;

            // 先空抓一帧丢掉。抓屏后端（BitBlt／WGC）常常返回上一帧缓存，直接拿来当基线，
            // 会在「刚清空」时读到清空前的样子；接着打同样的内容差异为 0，被判成「没写进去」。
            using (var warm = session.GrabRegion(region.X, region.Y, region.Width, region.Height))
            {
            }

            return new FieldSnapshot(session.GrabRegion(region.X, region.Y, region.Width, region.Height));
        }

        static FieldEvidence Compare(FieldKind kind, FieldSnapshot? before, FieldSnapshot? after)
        {
            var diff = GameSession.Diff(before?.Mat, after?.Mat);
            return new FieldEvidence(diff.ChangedPixels, diff.Area,
                after?.Width ?? 0, after?.Height ?? 0,
                diff.InkWidth, diff.InkHeight, diff.CenterPixels);
        }

        var result = UiaAutomationService.Login(session, account.Account, account.Password, Capture, Compare, _log);
        if (result != UiaResult.Submitted)
        {
            var reason = result switch
            {
                UiaResult.Unsupported => "界面没有暴露可操作的输入框（CEF 未开启无障碍）",
                UiaResult.Failed => "控件找到了，但没能把账号密码真正写进去或触发登录",
                _ => "未提交成功"
            };
            _log.Warn($"UIA 未完成登录：{reason}，改用键鼠流程", account.Account);
            return false;
        }

        // ③ 提交后先看有没有验证码：弹窗会盖住登录界面，让「登录按钮消失」的判定彻底失效。
        var captcha = await TryHandleCaptchaAsync(settings, account.Account, ct, 12000, session.ProcessId);
        if (captcha == CaptchaOutcome.TimedOut)
        {
            _log.Error("验证码未通过（人工等待超时），本次登录判失败", account.Account);
            return false;
        }

        if (captcha == CaptchaOutcome.Rejected)
        {
            // 风控拦截：再拿键鼠流程登一遍只会雪上加霜，直接把整个账号中止掉。
            throw new FlowException(FlowFailReasons.EnvAbnormal, "登录被风控拦截（提示登录失败／请扫码）");
        }

        if (captcha is CaptchaOutcome.Solved or CaptchaOutcome.ManualSolved)
        {
            // 验证码通过后，把游戏窗口重新拿回前台，后续判定与点击都要求它在最前面。
            session.Activate();
        }

        // ④ 提交后：登录按钮必须连续 2 次匹配不到，才认为界面真的切换了。
        var deadline = DateTime.UtcNow.AddSeconds(25);
        var missed = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var frame = session.CaptureFrame();
            if (frame != null && !frame.Empty())
            {
                var match = _matcher.Find(frame, "login_button", 0.85);
                missed = match.Found ? 0 : missed + 1;
                if (missed >= 2)
                {
                    _log.Info("登录按钮已消失，判定 UIA 登录成功", account.Account);
                    return true;
                }
            }

            await Task.Delay(500, ct);
        }

        _log.Warn("UIA 已提交登录，但 25 秒内登录按钮没有消失，改用键鼠流程重试", account.Account);
        return false;
    }

    /// <summary>
    /// 取一块输入框的抓屏区域。优先用 UIA 给的控件位置：它比模板匹配精确，
    /// 而且天然区分账号框与密码框；UIA 拿不到位置时才退回模板匹配。
    /// </summary>
    private FieldRegion? ResolveRegion(GameSession session, FieldRect uia, string template, string account)
    {
        if (uia.IsSane)
        {
            var region = ToFrameRegion(session, uia);
            if (region.Width > 8 && region.Height > 8)
            {
                return region;
            }
        }

        if (!_matcher.TemplateExists(template))
        {
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var frame = session.CaptureFrame();
            if (frame == null || frame.Empty())
            {
                continue;
            }

            var match = _matcher.Find(frame, template, 0.85);
            if (match.Found)
            {
                var region = new FieldRegion(match.X, match.Y, match.Width, match.Height);
                _log.Info($"UIA：UIA 没给出位置，改用模板「{template}」的匹配结果 {region}", account);
                return region;
            }

            Thread.Sleep(300);
        }

        return null;
    }

    /// <summary>屏幕坐标 → 抓屏画面坐标（两者尺寸不同时按比例换算）。</summary>
    private static FieldRegion ToFrameRegion(GameSession session, FieldRect rect)
    {
        // FrameSize 只有抓过帧才有值，先强制抓一帧，否则换算会退化成 1:1。
        using (var warm = session.CaptureFrame())
        {
        }

        var (frameWidth, frameHeight) = session.FrameSize;
        var client = WindowLocator.GetClientRect(session.Handle);
        var origin = WindowLocator.ClientToScreen(session.Handle, 0, 0);

        var clientX = rect.X - origin.X;
        var clientY = rect.Y - origin.Y;
        if (frameWidth <= 0 || frameHeight <= 0 || client.Width <= 0 || client.Height <= 0)
        {
            return new FieldRegion(clientX, clientY, rect.Width, rect.Height);
        }

        var scaleX = (double)frameWidth / client.Width;
        var scaleY = (double)frameHeight / client.Height;
        return new FieldRegion(
            (int)Math.Round(clientX * scaleX),
            (int)Math.Round(clientY * scaleY),
            (int)Math.Round(rect.Width * scaleX),
            (int)Math.Round(rect.Height * scaleY));
    }

    /// <summary>在限定时间内轮询查找模板，找到返回匹配结果。</summary>
    private async Task<TemplateMatch?> FindTemplateAsync(GameSession session, string template, double threshold,
        int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeoutMs));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var frame = session.CaptureFrame();
            if (frame != null && !frame.Empty())
            {
                var match = _matcher.Find(frame, template, threshold);
                if (match.Found)
                {
                    return match;
                }
            }

            await Task.Delay(400, ct);
        }

        return null;
    }
}
