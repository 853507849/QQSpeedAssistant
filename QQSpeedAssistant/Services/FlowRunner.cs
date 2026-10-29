using OpenCvSharp;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 流程执行器：按步骤驱动「抓屏识别 + 键鼠操作」，失败时抛出 <see cref="FlowException"/>。
/// </summary>
public sealed class FlowRunner
{
    private const int PollIntervalMs = 400;
    private readonly TemplateMatcher _matcher;
    private readonly RewardService _rewards;
    private readonly LogService _log;
    private readonly ConfigService _config;

    /// <summary>构造流程执行器。</summary>
    public FlowRunner(TemplateMatcher matcher, RewardService rewards, ConfigService config, LogService log)
    {
        _matcher = matcher;
        _rewards = rewards;
        _config = config;
        _log = log;
    }

    /// <summary>当前运行设置（每次读取最新值，配置重新加载后也能生效）。</summary>
    private AppSettings Settings => _config.Settings;

    /// <summary>执行整段流程。</summary>
    public async Task ExecuteAsync(AutomationFlow flow, GameSession session, FlowContext context, CancellationToken ct)
    {
        for (var index = 0; index < flow.Steps.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var step = flow.Steps[index];
            try
            {
                await ExecuteStepAsync(step, session, context, ct);
            }
            catch (FlowException ex)
            {
                _log.Warn($"流程「{flow.Name}」第 {index + 1} 步失败：{ex.Message}", context.Account.Account);
                throw;
            }
        }
    }

    /// <summary>执行单个步骤。</summary>
    private async Task ExecuteStepAsync(FlowStep step, GameSession session, FlowContext context, CancellationToken ct)
    {
        var type = step.Type.Trim().ToLowerInvariant();
        switch (type)
        {
            case "log":
                _log.Info(context.Resolve(step.Text), context.Account.Account);
                return;

            case "wait":
                await Task.Delay(Math.Max(0, step.TimeoutMs), ct);
                return;

            case "key":
                RequireKey(step);
                ActivateOrThrow(session);
                session.PressKey(step.Key!);
                await DelayActionAsync(ct);
                return;

            case "click":
                ActivateOrThrow(session);
                if (step.UseRatio)
                {
                    session.ClickClientRatio(step.XRatio, step.YRatio);
                }
                else
                {
                    session.ClickClientPixel(step.X, step.Y);
                }

                context.ClickCount++;
                await DelayActionAsync(ct);
                return;

            case "typetext":
            {
                ActivateOrThrow(session);
                var text = context.Resolve(step.Text);

                if (!step.Verify)
                {
                    session.TypeText(text);
                }
                else if (context.LastRegion is not { } region)
                {
                    _log.Warn("typeText 开了自检，但前面没有识别到输入框区域，本次只做普通输入", context.Account.Account);
                    session.TypeText(text);
                }
                else if (!session.TypeTextVerified(text, region.X, region.Y, region.Width, region.Height))
                {
                    throw new FlowException(FlowFailReasons.AssertFailed,
                        $"文字没有进入「{context.LastTemplate ?? "输入框"}」：点击没落到输入框，或输入被系统／反外挂丢弃。" +
                        "请确认本程序与游戏同为管理员权限；仍然不行就安装 Interception 驱动并把输入通道切到 Interception");
                }

                await DelayActionAsync(ct);
                return;
            }

            case "cleartext":
                ActivateOrThrow(session);
                session.ClearInput();
                await DelayActionAsync(ct);
                return;

            case "typetextautofocus":
            {
                ActivateOrThrow(session);
                if (string.IsNullOrWhiteSpace(step.Template))
                {
                    throw new FlowException(FlowFailReasons.AssertFailed,
                        "typeTextAutoFocus 必须用 template 指明目标输入框");
                }

                // 登录界面记住账号时，账号框里本来就填着同一个账号：
                // 清空再重打同样的内容，画面往往一点变化都没有（CEF 的 Ctrl+A 还可能不生效），
                // 靠像素判定必然报「文字没有进入」。这里先读 UIA 文本，一致就直接跳过这一步。
                if (step.SkipIfAccountRemembered)
                {
                    var remembered = UiaAutomationService.ReadAccountText(session.Handle, _log);
                    if (UiaAutomationService.SameAccount(remembered, context.Account.Account))
                    {
                        _log.Info($"账号框已经填着「{remembered}」（登录界面记住账号），跳过账号输入，直接输密码",
                            context.Account.Account);
                        return;
                    }

                    _log.Info($"账号框当前{(remembered.Length == 0 ? "读不到文本" : $"是「{remembered}」")}，"
                              + $"与配置账号「{context.Account.Account}」不一致，按正常流程输入", context.Account.Account);
                }

                var match = await WaitForTemplateAsync(session, step, context, Math.Max(step.TimeoutMs, 1000), ct);
                if (!match.Found)
                {
                    throw new FlowException(step.FailReason ?? FlowFailReasons.TemplateMissing,
                        $"未找到输入框「{step.Template}」，无法确定输入与校验区域");
                }

                context.LastRegion = (match.X, match.Y, match.Width, match.Height);
                context.LastTemplate = step.Template;

                var maxTabs = Math.Clamp(step.MaxTabs, 0, 8);
                if (!session.FocusFieldByProbing(match.X, match.Y, match.Width, match.Height, maxTabs))
                {
                    // 这个客户端的 Tab 导航基本无效（实测连按 6 次焦点都不动），
                    // 所以 Tab 走完还没找到焦点时，直接用模板坐标点一次框中心再验一次，
                    // 不能只因为「Tab 不灵」就把整条登录流程判死。
                    _log.Warn($"Tab 导航没找到「{step.Template}」的焦点，改用点击框中心", context.Account.Account);
                    session.ClickVerified(match.CenterX, match.CenterY, match.Width, match.Height, 2);
                    Thread.Sleep(150);

                    if (!session.FocusFieldByProbing(match.X, match.Y, match.Width, match.Height, 0))
                    {
                        throw new FlowException(FlowFailReasons.AssertFailed,
                            $"键盘输入没有落到「{step.Template}」：Tab {maxTabs} 次和点击框中心都没拿到焦点。" +
                            "请确认以管理员身份运行、输入通道不要回退到 SendInput");
                    }
                }

                // 焦点确认在目标框上，此时清空是安全的（不会误删别的输入框）。
                session.ClearInput();
                Thread.Sleep(80);

                var text = context.Resolve(step.Text);
                if (session.TypeTextAndCheck(text, match.X, match.Y, match.Width, match.Height))
                {
                    _log.Info($"「{step.Template}」输入完成", context.Account.Account);
                }
                else if (step.Required)
                {
                    // 画面没变化不一定是没写进去：密码框里原本就填着同样的密码时，
                    // 清空再重打一遍画面完全一样。这种情况退回「读回长度」这条更硬的证据。
                    var isPassword = !string.IsNullOrEmpty(context.Account.Password)
                                     && string.Equals(text, context.Account.Password, StringComparison.Ordinal);
                    var shown = isPassword ? UiaAutomationService.ReadPasswordText(session.Handle, _log) : "";
                    if (shown.Length > 0 && shown.Length == text.Length)
                    {
                        _log.Info($"「{step.Template}」画面没变化，但读回 {shown.Length} 个字符、与密码长度一致，"
                                  + "判定已经写进去了", context.Account.Account);
                    }
                    else
                    {
                        throw new FlowException(FlowFailReasons.AssertFailed,
                            $"文字没有进入「{step.Template}」：键盘事件发出去了但界面没有变化");
                    }
                }
                else
                {
                    _log.Warn($"「{step.Template}」输入后画面没有变化，已跳过", context.Account.Account);
                }

                await DelayActionAsync(ct);
                return;
            }

            case "clickimage":
            {
                var match = await WaitForTemplateAsync(session, step, context, step.TimeoutMs, ct);
                if (!match.Found)
                {
                    // 配了兜底坐标就按比例点下去：模板没采到不至于整个流程走不下去。
                    if (step.HasFallback)
                    {
                        ActivateOrThrow(session);
                        session.ClickClientRatio(step.FallbackXRatio, step.FallbackYRatio);
                        context.ClickCount++;
                        _log.Warn($"「{step.Template}」不可用，按相对坐标（{step.FallbackXRatio:F3}, {step.FallbackYRatio:F3}）兜底点击",
                            context.Account.Account);
                        await DelayActionAsync(ct);
                        return;
                    }

                    if (step.Required)
                    {
                        throw new FlowException(step.FailReason ?? FlowFailReasons.TemplateMissing,
                            _matcher.TemplateExists(step.Template!)
                                ? $"没认出「{step.Template}」（最高相似度 {match.Score:F2}），请重新采集或降低阈值"
                                : $"模板「{step.Template}」还没采集，请先在「模板采集」里补一张");
                    }

                    context.SkippedSteps++;
                    if (!_matcher.TemplateExists(step.Template!))
                    {
                        _log.Warn($"模板「{step.Template}」还没采集，跳过该步骤", context.Account.Account);
                    }

                    return;
                }

                ActivateOrThrow(session);
                context.LastRegion = (match.X, match.Y, match.Width, match.Height);
                context.LastTemplate = step.Template;

                if (step.Verify)
                {
                    if (!session.ClickVerified(match.CenterX, match.CenterY, match.Width, match.Height,
                            Math.Clamp(step.VerifyAttempts, 1, 5)))
                    {
                        if (!string.IsNullOrWhiteSpace(step.Key))
                        {
                            _log.Warn($"点击「{step.Template}」没有生效，改用键盘 {step.Key} 兜底", context.Account.Account);
                            session.PressKey(step.Key!);
                        }
                        else
                        {
                            _log.Warn($"点击「{step.Template}」连续多次都没有让画面变化：可能坐标偏移或被反外挂过滤。" +
                                      "建议确认管理员权限，或安装 Interception 驱动后把输入通道切到 Interception", context.Account.Account);
                        }
                    }
                }
                else
                {
                    session.ClickFramePixel(match.CenterX, match.CenterY);
                }

                context.ClickCount++;
                await DelayActionAsync(ct);
                return;
            }

            case "waitimage":
            {
                var match = await WaitForTemplateAsync(session, step, context, step.TimeoutMs, ct);
                if (!match.Found && step.Required)
                {
                    throw new FlowException(step.FailReason ?? FlowFailReasons.Timeout, $"等待模板「{step.Template}」超时");
                }

                return;
            }

            case "waitimagegone":
                await WaitForTemplateGoneAsync(session, step, context, ct);
                return;

            case "expectimage":
            {
                var match = await WaitForTemplateAsync(session, step, context, Math.Max(step.TimeoutMs, 1000), ct);
                if (!match.Found)
                {
                    throw new FlowException(step.FailReason ?? FlowFailReasons.Timeout, $"未出现预期画面「{step.Template}」");
                }

                return;
            }

            case "expectnoimage":
            {
                var match = await WaitForTemplateAsync(session, step, context, Math.Max(step.TimeoutMs, 1000), ct);
                if (match.Found)
                {
                    throw new FlowException(step.FailReason ?? FlowFailReasons.AssertFailed, $"出现了异常画面「{step.Template}」");
                }

                return;
            }

            case "savereward":
            {
                // 结算画面此刻正在显示，立即截图，避免后续点击关掉面板
                context.RewardRequested = true;
                await Task.Delay(Math.Max(0, Settings.RewardScreenshotDelayMs), ct);
                var modeName = GameModeCatalog.Find(context.ModeId)?.Name ?? context.ModeId;
                _rewards.Capture(session, context.Account, modeName, Math.Max(1, context.Round));
                return;
            }

            case "randomwait":
            {
                var min = Math.Max(0, step.MinMs > 0 ? step.MinMs : Settings.RandomDelayMinMs);
                var max = step.MaxMs > 0 ? step.MaxMs : Math.Max(min + 300, Settings.RandomDelayMaxMs);
                if (max < min)
                {
                    max = min;
                }

                var delay = Random.Shared.Next(min, max + 1);
                await Task.Delay(delay, ct);
                return;
            }

            case "waitimageany":
            {
                var templates = TemplatesOf(step, "hall_main");
                var (found, template, _) = await WaitAnyAsync(session, templates, step.Threshold,
                    Math.Max(step.TimeoutMs, 1000), context, ct);
                if (!found && step.Required)
                {
                    throw new FlowException(step.FailReason ?? FlowFailReasons.Timeout,
                        $"等待任一画面超时（{string.Join(" / ", templates)}）");
                }

                for (var i = 0; i < templates.Count; i++)
                {
                    context.Variables[$"any{i}"] = found && template == templates[i] ? "1" : "0";
                }

                return;
            }

            case "claimreward":
            {
                await ClaimAsync(session, step, context, ct);
                return;
            }

            case "openchest":
            {
                await OpenChestAsync(session, step, context, ct);
                return;
            }

            case "checkpermanent":
            {
                var templates = TemplatesOf(step, "permanent_badge");
                var (found, template, _) = await WaitAnyAsync(session, templates, step.Threshold,
                    Math.Max(step.TimeoutMs, 800), context, ct);
                if (!found)
                {
                    return;
                }

                var modeName = GameModeCatalog.Find(context.ModeId)?.Name ?? context.ModeId;
                context.PermanentHits++;
                _log.Success($"命中永久奖励（模板「{template}」）", context.Account.Account);
                _rewards.CapturePermanent(session, context.Account, modeName, step.Text ?? template);
                return;
            }

            case "repeat":
            {
                if (step.Steps is not { Count: > 0 } children)
                {
                    throw new FlowException(FlowFailReasons.AssertFailed, "repeat 步骤缺少 steps");
                }

                var times = Math.Clamp(step.Count, 1, 999);
                for (var i = 0; i < times; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    context.Variables["index"] = (i + 1).ToString();
                    foreach (var child in children)
                    {
                        await ExecuteStepAsync(child, session, context, ct);
                    }
                }

                return;
            }

            default:
                _log.Warn($"未知步骤类型「{step.Type}」，已忽略", context.Account.Account);
                return;
        }
    }

    /// <summary>取步骤的模板列表，缺省时用自带的默认模板名。</summary>
    private static List<string> TemplatesOf(FlowStep step, params string[] defaults)
    {
        if (step.Templates is { Count: > 0 })
        {
            return step.Templates.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(step.Template))
        {
            return [step.Template!];
        }

        return [.. defaults];
    }

    /// <summary>轮询等待多个模板中的任意一个出现。</summary>
    private async Task<(bool Found, string? Template, TemplateMatch Match)> WaitAnyAsync(
        GameSession session, IReadOnlyList<string> templates, double threshold, int timeoutMs,
        FlowContext context, CancellationToken ct)
    {
        var existing = templates.Where(_matcher.TemplateExists).ToList();
        if (existing.Count == 0)
        {
            _log.Warn($"模板都不存在（{string.Join(" / ", templates)}），请在模板采集里补齐", context.Account.Account);
            return (false, null, new TemplateMatch(false, 0, 0, 0, 0, 0));
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(timeoutMs, 500));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var frame = session.CaptureFrame();
            if (frame != null && !frame.Empty())
            {
                foreach (var template in existing)
                {
                    var match = _matcher.Find(frame, template, threshold);
                    if (match.Found)
                    {
                        _log.Info($"识别到「{template}」（相似度 {match.Score:F2}）", context.Account.Account);
                        return (true, template, match);
                    }
                }
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        return (false, null, new TemplateMatch(false, 0, 0, 0, 0, 0));
    }

    /// <summary>
    /// 领取奖励：按模板顺序逐个「找到就点」，典型顺序是「领取」→「确定」。
    /// 单个模板缺失只记日志，不中断流程（各玩法面板差别很大）。
    /// </summary>
    private async Task ClaimAsync(GameSession session, FlowStep step, FlowContext context, CancellationToken ct)
    {
        var templates = TemplatesOf(step, "reward_claim", "reward_claim_confirm");
        var clicked = 0;
        foreach (var template in templates)
        {
            if (!_matcher.TemplateExists(template))
            {
                _log.Warn($"领奖模板「{template}」不存在，跳过该步", context.Account.Account);
                continue;
            }

            var match = await WaitForNamedAsync(session, template, step.Threshold,
                Math.Max(step.TimeoutMs, 3000), context, ct);
            if (!match.Found)
            {
                continue;
            }

            ActivateOrThrow(session);
            session.ClickFramePixel(match.CenterX, match.CenterY);
            clicked++;
            await DelayActionAsync(ct);
        }

        if (clicked == 0)
        {
            _log.Warn("没有点到任何领奖按钮：可能是本局无奖励，或模板未采集", context.Account.Account);
        }
        else
        {
            _log.Info($"已领取奖励（点击 {clicked} 次）", context.Account.Account);
        }
    }

    /// <summary>
    /// 开宝箱：先等宝箱出现，再按「开箱 → 确定」逐级点。
    /// 宝箱没出现视为正常（当日宝箱已开完），不中断流程。
    /// </summary>
    private async Task OpenChestAsync(GameSession session, FlowStep step, FlowContext context, CancellationToken ct)
    {
        var templates = TemplatesOf(step, "reward_chest", "reward_chest_open");
        var first = templates[0];
        if (!_matcher.TemplateExists(first))
        {
            _log.Warn($"宝箱模板「{first}」不存在，跳过开宝箱", context.Account.Account);
            return;
        }

        var match = await WaitForNamedAsync(session, first, step.Threshold,
            Math.Max(step.TimeoutMs, 3000), context, ct);
        if (!match.Found)
        {
            _log.Info("本次没有出现宝箱（可能已开完），跳过", context.Account.Account);
            return;
        }

        ActivateOrThrow(session);
        session.ClickFramePixel(match.CenterX, match.CenterY);
        await DelayActionAsync(ct);

        foreach (var template in templates.Skip(1))
        {
            if (!_matcher.TemplateExists(template))
            {
                continue;
            }

            var confirm = await WaitForNamedAsync(session, template, step.Threshold, 4000, context, ct);
            if (!confirm.Found)
            {
                continue;
            }

            ActivateOrThrow(session);
            session.ClickFramePixel(confirm.CenterX, confirm.CenterY);
            await DelayActionAsync(ct);
        }

        _log.Success("宝箱已开启", context.Account.Account);
    }

    /// <summary>按名称轮询等待单个模板（供领奖／开箱复用，不抛异常）。</summary>
    private async Task<TemplateMatch> WaitForNamedAsync(GameSession session, string template, double threshold,
        int timeoutMs, FlowContext context, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(timeoutMs, 500));
        var last = new TemplateMatch(false, 0, 0, 0, 0, 0);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var frame = session.CaptureFrame();
            if (frame != null && !frame.Empty())
            {
                last = _matcher.Find(frame, template, threshold);
                if (last.Found)
                {
                    return last;
                }
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        return last;
    }

    /// <summary>轮询等待模板出现。</summary>
    private async Task<TemplateMatch> WaitForTemplateAsync(GameSession session, FlowStep step, FlowContext context,
        int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(step.Template))
        {
            throw new FlowException(FlowFailReasons.TemplateMissing, "步骤缺少 template 字段");
        }

        if (!_matcher.TemplateExists(step.Template))
        {
            if (step.Required)
            {
                throw new FlowException(FlowFailReasons.TemplateMissing,
                    $"模板「{step.Template}」还没有采集，请先在「模板采集」里补一张");
            }

            // 可选步骤缺模板属于「还没校准」，由调用方决定是否提示，这里不再重复告警。
            return TemplateMatch.Miss();
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(timeoutMs, 500));
        var last = TemplateMatch.Miss();
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var frame = session.CaptureFrame();
            if (frame != null && !frame.Empty())
            {
                last = _matcher.Find(frame, step.Template!, step.Threshold);
                if (last.Found)
                {
                    _log.Info($"识别到「{step.Template}」（相似度 {last.Score:F2}，缩放 {last.Scale:F2}）", context.Account.Account);
                    return last;
                }
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        _log.Warn($"没认出「{step.Template}」（最高相似度 {last.Score:F2}，缩放 {last.Scale:F2}，阈值 {step.Threshold:F2}），" +
                  "已保存当时的画面；可重新采集模板或降低阈值", context.Account.Account);
        SaveDebugFrame(session, step, context);
        return last;
    }

    /// <summary>
    /// 轮询等待模板从画面上消失，用于确认「已经离开某个界面」（例如登录按钮消失＝已进入游戏）。
    /// </summary>
    private async Task WaitForTemplateGoneAsync(GameSession session, FlowStep step, FlowContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(step.Template))
        {
            throw new FlowException(FlowFailReasons.TemplateMissing, "步骤缺少 template 字段");
        }

        if (!_matcher.TemplateExists(step.Template))
        {
            if (step.Required)
            {
                throw new FlowException(FlowFailReasons.TemplateMissing,
                    $"模板「{step.Template}」不存在，请放入 templates 目录");
            }

            _log.Warn($"模板「{step.Template}」不存在，已跳过可选步骤", context.Account.Account);
            return;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(step.TimeoutMs, 1000));
        var nextNotice = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var frame = session.CaptureFrame();
            if (frame != null && !frame.Empty())
            {
                var match = _matcher.Find(frame, step.Template!, step.Threshold);
                if (!match.Found)
                {
                    _log.Info($"「{step.Template}」已消失，继续下一步", context.Account.Account);
                    return;
                }
            }

            if (DateTime.UtcNow >= nextNotice)
            {
                nextNotice = DateTime.UtcNow.AddSeconds(15);
                _log.Warn($"仍在等待「{step.Template}」消失：界面没切换，常见原因是账号密码没真正输入、点击没生效（本程序与游戏请用同样的管理员权限运行）",
                    context.Account.Account);
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        if (step.Required)
        {
            SaveDebugFrame(session, step, context);
            throw new FlowException(step.FailReason ?? FlowFailReasons.Timeout,
                $"等待「{step.Template}」消失超时（{step.TimeoutMs} ms），界面没有切换");
        }

        _log.Warn($"「{step.Template}」仍未消失，已跳过该步骤", context.Account.Account);
    }

    /// <summary>激活窗口失败时立刻中断，避免后续点击与输入静默失效。</summary>
    private static void ActivateOrThrow(GameSession session)
    {
        if (!session.Activate())
        {
            throw new FlowException(FlowFailReasons.AssertFailed,
                "无法激活游戏窗口：请以管理员身份运行本程序（游戏以管理员身份运行时尤其需要）");
        }
    }

    /// <summary>识别超时后把当前画面存到 User/debug，便于对比模板与实际界面。</summary>
    private void SaveDebugFrame(GameSession session, FlowStep step, FlowContext context)
    {
        try
        {
            using var frame = session.CaptureFrame();
            if (frame == null || frame.Empty())
            {
                return;
            }

            var dir = Path.Combine(PathService.UserDir, "debug");
            Directory.CreateDirectory(dir);
            var name = $"fail-{step.Template}-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            Cv2.ImEncode(".png", frame, out var buffer);
            File.WriteAllBytes(Path.Combine(dir, name), buffer);
            _log.Warn($"已保存失败画面：User/debug/{name}", context.Account.Account);
        }
        catch
        {
            // 保存失败不影响主流程。
        }
    }

    /// <summary>步骤参数校验。</summary>
    private static void RequireKey(FlowStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Key))
        {
            throw new FlowException(FlowFailReasons.AssertFailed, "key 步骤缺少 key 字段");
        }
    }

    /// <summary>操作之间的基础间隔；配了随机延时区间时叠加一个抖动，避免节奏过于机械。</summary>
    private Task DelayActionAsync(CancellationToken ct)
    {
        var baseDelay = Math.Max(30, Settings.ActionDelayMs);
        var min = Settings.RandomDelayMinMs;
        var max = Settings.RandomDelayMaxMs;
        var jitter = max > min && min >= 0 ? Random.Shared.Next(min, max + 1) : 0;
        return Task.Delay(baseDelay + jitter, ct);
    }
}
