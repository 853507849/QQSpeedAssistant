using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using OpenCvSharp;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 一块输入框相对某张基线的变化证据。
/// 只记占比不够：光标闪烁在 255×42 的密码框上就能刷出 0.7%，
/// 和「真打进十几个圆点」的比例区分不开。所以同时记住变化像素数、外接框宽度和中间带占比：
/// 光标是 1~2 像素宽的竖线，边框高亮是「一圈空心」的变化，真正的文字／圆点又宽又是实心的。
/// </summary>
public readonly record struct FieldEvidence(int ChangedPixels, int Area, int RegionWidth, int RegionHeight,
    int InkWidth, int InkHeight, int CenterPixels)
{
    /// <summary>变化像素占比。</summary>
    public double Ratio => Area <= 0 ? 0 : ChangedPixels / (double)Area;
}

/// <summary>要操作的是哪一块输入框。</summary>
public enum FieldKind
{
    /// <summary>账号框。</summary>
    Account,

    /// <summary>密码框。</summary>
    Password
}

/// <summary>
/// 一块输入框区域的灰度快照。抓不到画面时内部为 null（判定一律按失败处理，方向安全）。
/// 由调用方创建、由调用方释放，这样「打字前基线」和「打字后画面」可以跨多次调用保存下来。
/// </summary>
public sealed class FieldSnapshot : IDisposable
{
    /// <summary>用一张灰度图构造快照，内部持有所有权。</summary>
    public FieldSnapshot(Mat? mat) => Mat = mat;

    /// <summary>灰度图，可能为 null。</summary>
    public Mat? Mat { get; }

    /// <summary>宽度，抓不到时为 0。</summary>
    public int Width => Mat?.Width ?? 0;

    /// <summary>高度，抓不到时为 0。</summary>
    public int Height => Mat?.Height ?? 0;

    /// <summary>是否抓到了画面。</summary>
    public bool HasImage => Mat != null && !Mat.Empty();

    /// <inheritdoc />
    public void Dispose() => Mat?.Dispose();
}

/// <summary>抓取指定输入框当前的画面；抓不到返回 null。</summary>
public delegate FieldSnapshot? CaptureField(FieldKind kind);

/// <summary>比较同一块输入框的两张快照，给出「有没有字」的判断依据。</summary>
public delegate FieldEvidence CompareField(FieldKind kind, FieldSnapshot? before, FieldSnapshot? after);

/// <summary>定位到的一块输入框在屏幕上的位置（物理像素）。</summary>
public readonly record struct FieldRect(int X, int Y, int Width, int Height, bool IsPassword, string Name)
{
    /// <summary>是否是一个尺寸合理的输入框。</summary>
    public bool IsSane => Width >= 60 && Height >= 14;

    /// <inheritdoc />
    public override string ToString() => $"({X},{Y},{Width}x{Height})";
}

/// <summary>UIA 填表结果。</summary>
public enum UiaResult
{
    /// <summary>已填写并触发登录按钮。</summary>
    Submitted,

    /// <summary>界面没有暴露可操作的输入框（例如 CEF 未开启无障碍）。</summary>
    Unsupported,

    /// <summary>找到控件但没能把文字真正写进去。</summary>
    Failed
}

/// <summary>
/// 用 Windows UI Automation 定位并操作游戏登录界面：控件位置靠无障碍树，输入靠真实按键。
/// CEF（Chromium）默认不构建无障碍树，这里先发 WM_GETOBJECT 主动触发。
/// 实测本机客户端（QQSpeed_loader 1200x900 登录界面）的真实结构：
/// 两块输入框是 CEF 自绘的 Pane（ClassName=Edit），没有 ValuePattern，账号文本挂在 Name 上，
/// IsPassword 也不声明；账号框约 206x27、密码框约 240x32，上下相邻。
/// 注意四点：
/// ① 不能按 ControlType.Edit 找输入框（会得到 0 个），要按 ClassName=Edit 找；
/// ② Chromium 的输入框声明了 ValuePattern 时，SetValue 也只写进 DOM、不重绘，失焦后画面又归零，
///    表现就是「接口说写成功、读回也有值、画面上一个字都没有」，所以本类不用 SetValue 写字；
/// ③ 向 Chromium 子窗口 PostMessage(WM_CHAR) 投递字符不会生效，真正写字必须用真实键盘事件；
/// ④ CEF 不认 UIA 的 SetFocus，只有「真实点击框内」才能把键盘焦点放进目标输入框。
/// 每条写入都必须用「该输入框自己的像素变化」确认，不能只看接口返回值。
/// </summary>
public static class UiaAutomationService
{
    private const int ObjIdClient = unchecked((int)0xFFFFFFFC);
    private const uint WmGetObject = 0x003D;

    /// <summary>每一块输入框最多重试几轮（每轮都会重新点击、清空、重打）。</summary>
    private const int FillAttempts = 2;

    /// <summary>
    /// 密码逐字符的按键间隔。账号默认 35ms 就够，密码必须更慢：
    /// 连续快速注入时游戏端的键盘过滤会丢键，实测表现为「8 位密码只进去 1 个圆点、变化区仅 15 像素宽」。
    /// </summary>
    private const int PasswordTypeIntervalMs = 90;

    /// <summary>
    /// 判定「框里确实有字」的像素底线。实测光标闪烁在密码框上只有 70～80 个变化像素，
    /// 而打进 16 位密码的圆点有几百个，中间留足余量。
    /// 以前用比例门限 0.5%，光标闪一下就「达标」，于是空密码点了登录。
    /// 90 像素是「4 位短密码也能过、光标闪烁过不去」的折中值。
    /// </summary>
    private const int MinInkPixels = 90;

    /// <summary>这块框的变化是否像「一排字／一排圆点」：像素数过底线、变化区够宽、且大部分落在中间带。</summary>
    private static bool InkOk(FieldEvidence evidence)
    {
        var needWidth = Math.Max(10, (int)(evidence.RegionWidth * 0.10));
        return evidence.ChangedPixels >= MinInkPixels
               && evidence.InkWidth >= needWidth
               && evidence.CenterPixels * 2 >= evidence.ChangedPixels;
    }

    /// <summary>
    /// 「刚打进去的这几个字符确实落在这一块框里」的判定，比 <see cref="InkOk"/> 松：
    /// 只打 1～2 个字符时像素数本来就少，但仍必须比光标宽——光标 1~2 像素，字符／圆点 5 像素以上。
    /// </summary>
    private static bool FocusOk(FieldEvidence evidence, int chars)
    {
        var need = 25 * Math.Max(1, chars);
        var needWidth = chars >= 2 ? 10 : 5;
        return evidence.ChangedPixels >= need && evidence.InkWidth >= needWidth;
    }

    /// <summary>离线自检用：这块变化算不算「有字」，不写日志。</summary>
    public static bool LooksFilled(FieldEvidence evidence) => InkOk(evidence);

    /// <summary>离线自检用：刚打进去的这几个字符算不算落到了这块框里，不写日志。</summary>
    public static bool LooksFocused(FieldEvidence evidence, int chars) => FocusOk(evidence, chars);

    /// <summary>把判定结果写进日志：通过写「变化多少像素」，不通过写清哪一条没过。</summary>
    private static void ReportInk(FieldEvidence evidence, string label, string field, LogService log, bool ok)
    {
        if (ok)
        {
            log.Info($"{label} 写入生效：{field}变化 {evidence.ChangedPixels} 像素"
                     + $"（{evidence.Ratio:P2}，变化区 {evidence.InkWidth}×{evidence.InkHeight}）", "UIA");
            return;
        }

        var needWidth = Math.Max(10, (int)(evidence.RegionWidth * 0.10));
        string why;
        if (evidence.ChangedPixels < MinInkPixels)
        {
            why = $"只变化 {evidence.ChangedPixels} 像素（{evidence.Ratio:P2}，门限 {MinInkPixels}），像光标闪烁不像文字";
        }
        else if (evidence.InkWidth < needWidth)
        {
            why = $"变化区只有 {evidence.InkWidth} 像素宽（至少要 {needWidth}），是一条竖线不是一排字";
        }
        else
        {
            why = $"中间带只有 {evidence.CenterPixels}/{evidence.ChangedPixels} 像素，是边框高亮的空心变化，不是文字";
        }

        log.Warn($"{label} 后 {field}{why}，判定没写进去", "UIA");
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// 填写账号密码并触发登录。<paramref name="capture"/>／<paramref name="compare"/> 由调用方提供：
    /// 抓某块输入框的画面、比较两张快照（调用方知道这两块框在画面里的位置）。
    /// 顺序是死的：定位两块框 → 账号确认写进去 → 密码确认写进去 → 再过一道终检 → 才点「登录」。
    /// 哪一块没拿到证据就返回 Failed，绝不带着空密码去点登录。
    /// </summary>
    public static UiaResult Login(GameSession session, string account, string password,
        CaptureField capture, CompareField compare, LogService log)
    {
        FieldSnapshot? accountBaseline = null;
        FieldSnapshot? passwordBaseline = null;
        try
        {
            var located = LocateFields(session.Handle, log, verbose: true);
            var accountEdit = located.Account;
            var passwordEdit = located.Password;
            if (accountEdit == null || passwordEdit == null)
            {
                return UiaResult.Unsupported;
            }

            // ① 账号：先读真实文本。登录界面有「记住账号」时框里本来就填着上次的账号，
            //    对它做「清空→重打同样的内容」不会产生任何像素变化（清空在 CEF 里还可能不生效），
            //    靠画面比对判定「写进去了」必然误判失败。所以文本一致就直接认，不去动它。
            var accountText = SafeFieldText(accountEdit).Trim();
            var accountByText = SameAccount(accountText, account);

            if (accountByText)
            {
                log.Info($"UIA 账号框已填着「{accountText}」，与配置账号一致（登录界面记住账号），"
                         + "不再重输，直接进入密码步骤", "UIA");

                // 基线取「当前画面」，后面终检时账号这一块不再比像素（差值为 0 是正常的）。
                accountBaseline = capture(FieldKind.Account);
            }
            else
            {
                log.Info($"UIA 账号框当前{(accountText.Length == 0 ? "为空" : $"是「{accountText}」")}，"
                         + $"与配置账号「{account}」不一致，重新输入", "UIA");

                // 真实点击 → 清空 → 建基线 → 真实按键。
                // 以前先试 ValuePattern / ComboBox SetValue，实测 CEF 只把值写进 DOM 不重绘，
                // 失焦后画面又归零——「写进去了」只是假象，所以这条通道只留真实按键。
                if (!FillField(session, accountEdit, FieldKind.Account, "账号", account,
                        capture, compare, log, out accountBaseline))
                {
                    log.Warn("UIA：账号没能确认写进去，不点「登录」", "UIA");
                    return UiaResult.Failed;
                }

                // 打完再读一次：读得到就必须与目标账号一致，读不到才退回像素证据。
                var after = SafeFieldText(accountEdit).Trim();
                if (after.Length > 0 && !SameAccount(after, account))
                {
                    log.Warn($"UIA：账号框写完后读回是「{after}」，与目标账号「{account}」不一致，不点「登录」", "UIA");
                    return UiaResult.Failed;
                }
            }

            // ② 密码：同样真实点击 + 真实按键。每一轮都点一次框中心，
            //    并在打前两个字符后立刻看密码框自己有没有变化，确认键盘焦点真的落在这一块。
            if (!FillField(session, passwordEdit, FieldKind.Password, "密码", password,
                    capture, compare, log, out passwordBaseline))
            {
                log.Warn("UIA：密码没能确认写进去，不点「登录」", "UIA");
                return UiaResult.Failed;
            }

            // ③ 终检：重新读一次账号文本，一致就放行；两块框相对「各自的清空后基线」都还得有字。
            return SubmitChecked(session.Handle, accountEdit, account, capture, compare,
                accountBaseline, passwordBaseline, log);
        }
        catch (Exception ex)
        {
            log.Warn($"UIA 操作异常：{ex.Message}", "UIA");
            return UiaResult.Failed;
        }
        finally
        {
            accountBaseline?.Dispose();
            passwordBaseline?.Dispose();
        }
    }

    /// <summary>
    /// 往一块输入框里写真字：真实点击拿焦点 → 先打两个字符确认焦点真在这一块 → 清空 →
    /// 抓「清空后基线」→ 打完整内容 → 用基线比对确认框里确实有字。
    /// 顺序不能反过来（先清空再验焦点）：CEF 的点击可能落空，焦点还在另一块框上，
    /// 这时候清空会把那一块已经填好的内容清掉——之前就是在这里把刚写好的账号清成 0 像素的。
    /// </summary>
    /// <param name="baseline">成功时的「清空后基线」，调用方负责释放；失败时为 null。</param>
    private static bool FillField(GameSession session, AutomationElement element, FieldKind kind, string label,
        string text, CaptureField capture, CompareField compare, LogService log, out FieldSnapshot? baseline)
    {
        baseline = null;

        if (string.IsNullOrEmpty(text))
        {
            log.Warn($"UIA：{label}为空，不写入", "UIA");
            return false;
        }

        var rect = SafeRect(element);
        if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8)
        {
            log.Warn($"UIA：{label}框没有有效的位置信息（{rect}），无法点击定位", "UIA");
            return false;
        }

        log.Info($"UIA {label}框：{rect.Width:0}x{rect.Height:0} @ 屏幕({rect.X:0},{rect.Y:0})，"
                 + $"客户区({ToClientX(session, rect):0},{ToClientY(session, rect):0})", "UIA");

        // 只打前一小段用于验证焦点，剩下的一次补齐。
        var head = text[..Math.Min(2, text.Length)];

        // 密码逐字符必须放慢：连续快速注入时游戏端的键盘过滤会丢键，
        // 实测现象就是「8 位密码只进去 1 个圆点、变化区只有 15 像素宽」。
        var interval = kind == FieldKind.Password ? PasswordTypeIntervalMs : 0;

        void Type(string body)
        {
            if (interval > 0)
            {
                session.TypeText(body, interval);
            }
            else
            {
                session.TypeText(body);
            }
        }

        for (var attempt = 1; attempt <= FillAttempts; attempt++)
        {
            if (!session.Activate())
            {
                log.Warn($"UIA：窗口没能切到前台，无法用真实按键写{label}", "UIA");
                return false;
            }

            ClickElementCenter(session, rect, label, attempt, log);

            // 第一步只验焦点：打两个字符，看这一块框自己有没有反应。
            var clicked = capture(kind);
            try
            {
                Type(head);
                Thread.Sleep(240);

                using var probe = capture(kind);
                var probeEvidence = compare(kind, clicked, probe);
                if (!FocusOk(probeEvidence, head.Length))
                {
                    log.Warn($"UIA {label}（第 {attempt} 次）：点击后前 {head.Length} 个字符没进{label}框"
                             + $"（只变化 {probeEvidence.ChangedPixels} 像素、变化区 {probeEvidence.InkWidth} 宽），"
                             + "键盘焦点没落到这块框上", "UIA");

                    // 这几个字符可能落到了别的框里，只退格删掉它们，绝不能 Ctrl+A 全清——那会把别的框清空。
                    for (var i = 0; i < head.Length; i++)
                    {
                        session.PressKey("back");
                        Thread.Sleep(30);
                    }

                    continue;
                }

                log.Info($"UIA {label}（第 {attempt} 次）：键盘焦点已确认（前 {head.Length} 个字符让{label}框变化 "
                         + $"{probeEvidence.ChangedPixels} 像素）", "UIA");
            }
            finally
            {
                clicked?.Dispose();
            }

            // 焦点确认在这一块框上，此时清空才是安全的。
            session.PressKey("end");
            Thread.Sleep(30);
            session.ClearInput();
            Thread.Sleep(220);

            baseline?.Dispose();
            baseline = capture(kind);

            // 清空把探针字符也清掉了，这里打完整内容。
            Type(text);

            // 轮询取最佳：圆点是逐个淡入的，固定等一次可能正好抓到「只画了一半」的中间帧，
            // 于是 8 位密码被判成 1 个圆点。多抓几次，只要有一次像真文字就算成功。
            var best = default(FieldEvidence);
            for (var round = 0; round < 5; round++)
            {
                var evidence = Compare(kind, capture, compare, baseline);
                if (evidence.ChangedPixels > best.ChangedPixels)
                {
                    best = evidence;
                }

                if (InkOk(evidence))
                {
                    best = evidence;
                    break;
                }

                Thread.Sleep(160);
            }

            if (InkOk(best))
            {
                ReportInk(best, $"{label}（真实按键，第 {attempt} 次）", $"{label}框", log, true);
                return true;
            }

            // 密码框的像素证据不好拿时（清空与重打撞在一起、圆点太小），退回「读回长度」这条更硬的证据：
            // 客户端有时会把掩码长度暴露成 Name，读到的字符数与密码长度一致就算写进去了。
            if (kind == FieldKind.Password)
            {
                var shown = SafeFieldText(element).Trim();
                if (shown.Length > 0 && shown.Length == text.Length)
                {
                    log.Info($"{label}（真实按键，第 {attempt} 次）：像素变化不明显（{best.ChangedPixels} 像素），"
                             + $"但读回 {shown.Length} 个字符，与密码长度相同，判定已写入", "UIA");
                    return true;
                }
            }

            ReportInk(best, $"{label}（真实按键，第 {attempt} 次）", $"{label}框", log, false);

            // 留证据：下次排查直接看这两张图就知道是「只进去 1 位」还是「框选错了位置」。
            using (var after = capture(kind))
            {
                SaveFieldImages(baseline, after, $"{label}-{attempt}");
            }
        }

        baseline?.Dispose();
        baseline = null;
        return false;
    }

    /// <summary>把某块输入框的「清空后基线」与「打完之后的画面」存到 User/debug，用于事后看图判断。</summary>
    private static void SaveFieldImages(FieldSnapshot? before, FieldSnapshot? after, string name)
    {
        try
        {
            var directory = Path.Combine(PathService.UserDir, "debug");
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("HHmmss");
            var safe = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));

            if (before?.Mat is { } b && !b.Empty())
            {
                Cv2.ImWrite(Path.Combine(directory, $"uia-{safe}-{stamp}-before.png"), b);
            }

            if (after?.Mat is { } a && !a.Empty())
            {
                Cv2.ImWrite(Path.Combine(directory, $"uia-{safe}-{stamp}-after.png"), a);
            }
        }
        catch
        {
            // 存图失败不影响主流程。
        }
    }

    /// <summary>抓一次并与基线比对；抓不到按全零处理（全零必定判失败，方向安全）。</summary>
    private static FieldEvidence Compare(FieldKind kind, CaptureField capture, CompareField compare,
        FieldSnapshot? baseline)
    {
        try
        {
            using var now = capture(kind);
            return compare(kind, baseline, now);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// 真实点击输入框中心，把键盘焦点放进去。CEF 不接受 UIA 的 SetFocus，只有真实点击才可靠，
    /// 所以这里不再「先试 SetFocus、失败再点」——那一轮的空等再叠加清空，会把焦点所在的另一块框清掉。
    /// 重试时落点会在框内换位置（中→偏左→偏右）：密码框右侧常有「显示密码」图标，居中点击也可能被悬浮层挡住。
    /// </summary>
    private static void ClickElementCenter(GameSession session, System.Windows.Rect rect, string label,
        int attempt, LogService log)
    {
        var origin = WindowLocator.ClientToScreen(session.Handle, 0, 0);
        var ratio = attempt switch
        {
            1 => 0.50,
            2 => 0.30,
            _ => 0.70
        };

        var x = (int)(rect.X + rect.Width * ratio) - origin.X;
        var y = (int)(rect.Y + rect.Height / 2.0) - origin.Y;
        session.ClickClientPixel(x, y);
        Thread.Sleep(180);
        log.Info($"UIA：第 {attempt} 次点击{label}框（客户区 {x},{y}，框内 {ratio * 100:F0}% 处）", "UIA");
    }

    /// <summary>
    /// 找出账号框和密码框。实测本机客户端（QQSpeed_loader 1200x900 登录界面）的真相是：
    /// 两块输入框是 CEF 自绘的 **Pane**，`ClassName` 才是 `Edit`；**没有 ValuePattern**，账号文本挂在 `Name` 上；
    /// `IsPassword` 也不声明（全是 False）。所以：
    /// ① 不能只按 ControlType.Edit 找——那样会得到 0 个，这正是「密码框定位不到」的直接原因；
    /// ② 不能要求 ValuePattern 可写；③ 不能指望 IsPassword，只能按上下位置认。
    /// 另外 Chromium 的 UIA 会时不时让个别节点失效（读属性抛 InvalidOperationException），
    /// 读不到就跳过该节点并整轮重试，绝不因为一个坏节点就判定失败。
    /// </summary>
    private static (AutomationElement? Account, AutomationElement? Password) LocateFields(nint windowHandle,
        LogService log, bool verbose)
    {
        for (var round = 1; round <= 3; round++)
        {
            var located = LocateFieldsOnce(windowHandle, log, verbose, round);
            if (located.Account != null && located.Password != null)
            {
                return located;
            }

            if (round < 3)
            {
                Thread.Sleep(400);
            }
        }

        return (null, null);
    }

    private static (AutomationElement? Account, AutomationElement? Password) LocateFieldsOnce(nint windowHandle,
        LogService log, bool verbose, int round)
    {
        try
        {
            WakeUpAccessibility(windowHandle);

            var root = AutomationElement.FromHandle(windowHandle);
            if (root == null)
            {
                log.Warn("UIA：拿不到窗口的自动化根节点", "UIA");
                return (null, null);
            }

            DismissStalePrompt(root, log);

            var candidates = new List<(AutomationElement Element, FieldRect Rect)>();
            var scanned = 0;
            var skipped = 0;
            foreach (var element in FindAllDescendants(root))
            {
                scanned++;
                if (SafeOffscreen(element))
                {
                    continue;
                }

                // CEF 的输入框：ClassName 恒为 Edit，ControlType 可能是 Edit，也可能是 Pane。
                // 不把 ComboBox 算进来：登录界面上的分辨率下拉也是 ComboBox，混进来会把配对搞乱。
                var className = SafeClassName(element);
                var controlType = SafeControlType(element);
                var looksLikeInput = string.Equals(className, "Edit", StringComparison.OrdinalIgnoreCase)
                                     || controlType is "Edit";
                if (!looksLikeInput)
                {
                    continue;
                }

                var rect = SafeRect(element);
                var field = new FieldRect((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height,
                    SafeIsPassword(element), SafeFieldText(element));
                if (!field.IsSane)
                {
                    skipped++;
                    if (verbose)
                    {
                        log.Info($"UIA 跳过尺寸异常的输入框：{controlType}「{field.Name}」class='{className}' {field}", "UIA");
                    }

                    continue;
                }

                candidates.Add((element, field));
            }

            if (candidates.Count == 0)
            {
                log.Warn($"UIA：界面没有暴露可用的输入框（第 {round} 轮扫描 {scanned} 个节点，"
                         + $"尺寸异常 {skipped} 个；CEF 未开启无障碍或窗口不是登录界面）", "UIA");
                return (null, null);
            }

            if (verbose)
            {
                log.Info($"UIA 第 {round} 轮扫描 {scanned} 个节点，命中 {candidates.Count} 个输入框："
                         + string.Join("、", candidates.Select(c =>
                             $"「{c.Rect.Name}」IsPassword={c.Rect.IsPassword} {c.Rect}")), "UIA");
            }

            var ordered = candidates.OrderBy(c => c.Rect.Y).ThenBy(c => c.Rect.X).ToList();

            // ① 万一某个客户端真的声明了 IsPassword，就信它，账号框取上方最近的一块。
            var passwordPair = ordered.FirstOrDefault(c => c.Rect.IsPassword);
            AutomationElement? password = passwordPair.Element;
            AutomationElement? account = null;

            if (password != null)
            {
                account = ordered
                    .Where(c => !c.Rect.IsPassword && c.Rect.Y < passwordPair.Rect.Y)
                    .OrderByDescending(c => c.Rect.Y)
                    .Select(c => c.Element)
                    .FirstOrDefault();
            }
            else if (ordered.Count == 2)
            {
                // ② 本机实测就是两块框、上下相邻：上=账号、下=密码。
                account = ordered[0].Element;
                password = ordered[1].Element;
            }
            else
            {
                // ③ 树上还挂着别的输入框时，取「上下相邻、宽度接近、横向对齐、间距不大」的那一对。
                var bestScore = double.MaxValue;
                for (var i = 0; i + 1 < ordered.Count; i++)
                {
                    var upper = ordered[i].Rect;
                    var lower = ordered[i + 1].Rect;
                    var gap = lower.Y - (upper.Y + upper.Height);
                    if (gap < -6 || gap > 140)
                    {
                        continue;
                    }

                    var score = Math.Abs(upper.Width - lower.Width)
                                + Math.Abs(gap) * 0.5
                                + (Math.Abs(upper.X - lower.X) > 40 ? 200 : 0);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        account = ordered[i].Element;
                        password = ordered[i + 1].Element;
                    }
                }

                if (account == null && ordered.Count >= 2)
                {
                    account = ordered[0].Element;
                    password = ordered[1].Element;
                }
            }

            if (account == null || password == null)
            {
                log.Warn($"UIA：命中 {candidates.Count} 个输入框但配不出账号／密码这一对，"
                         + $"界面结构可能变了（第 {round} 轮）", "UIA");
                return (null, null);
            }

            if (verbose)
            {
                log.Info($"UIA 定位结果：账号框 {DescribeWithRect(account)}｜密码框 {DescribeWithRect(password)}", "UIA");
            }

            return (account, password);
        }
        catch (Exception ex)
        {
            log.Warn($"UIA 定位输入框异常（第 {round} 轮）：{ex.Message}", "UIA");
            return (null, null);
        }
    }

    /// <summary>
    /// 只取两块输入框的屏幕位置，给调用方换算抓屏区域用。找不到时返回 default（IsSane 为 false）。
    /// </summary>
    public static (FieldRect Account, FieldRect Password) LocateInputRects(nint windowHandle, LogService log)
    {
        var (account, password) = LocateFields(windowHandle, log, verbose: false);
        return (account == null ? default : ToFieldRect(account),
                password == null ? default : ToFieldRect(password));
    }

    /// <summary>
    /// 读账号框当前的文本。给「登录界面记住账号」的场景用：这类界面账号框里本来就填着上次的账号，
    /// 对它重打同样的内容不会让画面产生任何变化，靠像素判定「写进去了」必然误判失败。
    /// 改成读控件文本、与配置账号比，一致就跳过账号输入。读不到返回空串。
    /// </summary>
    public static string ReadAccountText(nint windowHandle, LogService log) =>
        ReadFieldText(windowHandle, log, FieldKind.Account);

    /// <summary>
    /// 读密码框当前的文本。多数客户端对密码框只暴露掩码或干脆不暴露，读不到就返回空串；
    /// 但只要能读到、且字符数与密码一致，就是比像素更硬的「确实写进去了」的证据。
    /// </summary>
    public static string ReadPasswordText(nint windowHandle, LogService log) =>
        ReadFieldText(windowHandle, log, FieldKind.Password);

    private static string ReadFieldText(nint windowHandle, LogService log, FieldKind kind)
    {
        try
        {
            WakeUpAccessibility(windowHandle);
            var (account, password) = LocateFields(windowHandle, log, verbose: false);
            var target = kind == FieldKind.Account ? account : password;
            return target == null ? "" : SafeFieldText(target).Trim();
        }
        catch (Exception ex)
        {
            log.Warn($"UIA：读取{(kind == FieldKind.Account ? "账号" : "密码")}框文本失败：{ex.Message}", "UIA");
            return "";
        }
    }

    /// <summary>
    /// 两段账号文本是否是同一个账号。忽略首尾与中间的空格，并把全角数字／字母折成半角
    /// （输入法忘了切半角时，「１２３」不该被当成另一个账号）。
    /// </summary>
    public static bool SameAccount(string? text, string? account)
    {
        var a = NormalizeAccount(text);
        var b = NormalizeAccount(account);
        return a.Length > 0 && string.Equals(a, b, StringComparison.Ordinal);
    }

    private static string NormalizeAccount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            builder.Append(ch switch
            {
                >= '０' and <= '９' => (char)(ch - '０' + '0'),
                >= 'Ａ' and <= 'Ｚ' => (char)(ch - 'Ａ' + 'A'),
                >= 'ａ' and <= 'ｚ' => (char)(ch - 'ａ' + 'a'),
                _ => ch
            });
        }

        return builder.ToString();
    }

    private static FieldRect ToFieldRect(AutomationElement element)
    {
        var rect = SafeRect(element);
        return new FieldRect((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height,
            SafeIsPassword(element), SafeFieldText(element));
    }

    /// <summary>
    /// 把界面上所有输入框的识别结果导出成文本：是否可写、是否声明为密码框、屏幕位置、尺寸、读回的值，
    /// 以及最终选中的账号框／密码框。密码框定位不准时先看这个，能区分
    /// 「根本没找到」「找到了但尺寸异常」「两个框顺序搞反」三种情况。
    /// </summary>
    public static string InspectInputs(nint windowHandle, LogService log)
    {
        var builder = new StringBuilder();
        try
        {
            WakeUpAccessibility(windowHandle);
            var root = AutomationElement.FromHandle(windowHandle);
            if (root == null)
            {
                return "UIA：拿不到根节点";
            }

            var all = FindAllDescendants(root);
            builder.AppendLine($"后代节点 {all.Count} 个（CEF 的输入框 ControlType 可能是 Pane，靠 ClassName=Edit 认）：");
            foreach (var element in all)
            {
                var className = SafeClassName(element);
                var controlType = SafeControlType(element);
                if (!string.Equals(className, "Edit", StringComparison.OrdinalIgnoreCase)
                    && controlType is not "Edit"
                    && controlType is not "ComboBox")
                {
                    continue;
                }

                var field = ToFieldRect(element);
                builder.AppendLine($"  {controlType} class='{className}' 「{field.Name}」IsPassword={field.IsPassword}"
                                   + $" 可写={SupportsValuePattern(element)} 可见={!SafeOffscreen(element)}"
                                   + $" 位置={field} 尺寸合理={field.IsSane} 文本=「{SafeFieldText(element)}」");
            }

            var (account, password) = LocateFields(windowHandle, log, verbose: false);
            builder.AppendLine($"选中账号框：{(account == null ? "无" : DescribeWithRect(account))}");
            builder.AppendLine($"选中密码框：{(password == null ? "无" : DescribeWithRect(password))}");

            var buttons = FindAll(root, ControlType.Button);
            builder.AppendLine($"Button 控件 {buttons.Count} 个：{string.Join("、", buttons.Take(8).Select(SafeName))}");

            return builder.ToString();
        }
        catch (Exception ex)
        {
            return $"UIA 结构导出异常：{ex.Message}";
        }
    }

    private static double ToClientX(GameSession session, System.Windows.Rect rect) =>
        rect.X - WindowLocator.ClientToScreen(session.Handle, 0, 0).X;

    private static double ToClientY(GameSession session, System.Windows.Rect rect) =>
        rect.Y - WindowLocator.ClientToScreen(session.Handle, 0, 0).Y;

    /// <summary>
    /// 登录前检查：界面若还挂着「请输入密码」之类的提示框，先点「确定」关掉。
    /// 这类弹窗多半是上一次密码为空时点了登录留下的，不关会挡住输入框、吞掉后续点击。
    /// </summary>
    private static void DismissStalePrompt(AutomationElement root, LogService log)
    {
        try
        {
            var hasPrompt = FindAll(root, ControlType.Text)
                .Any(t => SafeName(t).Contains("请输入密码", StringComparison.Ordinal));
            if (!hasPrompt)
            {
                return;
            }

            var ok = FindAll(root, ControlType.Button)
                .FirstOrDefault(b => SafeName(b) is "确定" or "OK");
            if (ok == null
                || !ok.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                || pattern is not InvokePattern invoke)
            {
                log.Warn("UIA 登录前检查：界面挂着「请输入密码」提示框，但找不到可点的「确定」，请手动关掉", "UIA");
                return;
            }

            invoke.Invoke();
            log.Info("UIA 登录前检查：已关闭残留的「请输入密码」提示框", "UIA");
            Thread.Sleep(400);
        }
        catch (Exception ex)
        {
            log.Warn($"UIA 登录前检查：关闭提示框失败：{ex.Message}", "UIA");
        }
    }

    /// <summary>登录界面底部「窗口模式」旁要选的分辨率：800x600，而且必须是 32 位色。</summary>
    private const string TargetResolution = "800x600";

    /// <summary>
    /// 给下拉里的分辨率选项打分，只认「800x600 且不是 16 位色」：
    /// 2 = 明确写了 32 位色；1 = 只写 800x600、没有位深标记；-1 = 不是目标选项。
    /// 以前用 `Contains("800x600")` 取第一个命中项，而列表里第一个正好是 800x600(16位色)，
    /// 于是每轮都选成 16 位色：进游戏后颜色深度不对，游戏内模板全部偏色匹配失败。
    /// </summary>
    public static int ResolutionScore(string? name)
    {
        var text = Normalize(name);
        if (!text.Contains(TargetResolution, StringComparison.Ordinal))
        {
            return -1;
        }

        // 明确 16 位色的一律排除（16位 / 16bit / 16bpp / 16色）。
        if (text.Contains("16位") || text.Contains("16bit") || text.Contains("16bpp") || text.Contains("16色"))
        {
            return -1;
        }

        return text.Contains("32位") || text.Contains("32bit") || text.Contains("32bpp") || text.Contains("32色")
            ? 2
            : 1;
    }

    /// <summary>
    /// 纯逻辑：从选项名列表里挑最优的分辨率项，返回下标（-1 = 没有合格项）。
    /// 优先明确的 32 位色，其次无位深标记的 800x600；16 位色与其它分辨率一律跳过。
    /// </summary>
    public static int PickBestResolutionName(IReadOnlyList<string?> names)
    {
        var best = -1;
        var bestScore = 0;
        for (var i = 0; i < names.Count; i++)
        {
            var score = ResolutionScore(names[i]);
            if (score > bestScore)
            {
                best = i;
                bestScore = score;
            }

            if (score >= 2)
            {
                // 已经是明确的 32 位色，不用继续找。
                break;
            }
        }

        return best;
    }

    /// <summary>从一批元素里挑出最优的分辨率项。</summary>
    private static (AutomationElement? Element, string Name, int Score) PickResolutionOption(
        IEnumerable<AutomationElement> elements)
    {
        var list = elements.ToList();
        if (list.Count == 0)
        {
            return (null, "", 0);
        }

        var names = list.Select(SafeName).ToList();
        var index = PickBestResolutionName(names);
        return index < 0 ? (null, "", 0) : (list[index], names[index], ResolutionScore(names[index]));
    }

    /// <summary>
    /// 登录前把显示模式选成「800x600(32位色)」：进游戏后画面就是 800x600，游戏内模板才对得上。
    /// 账号框外层也是 ComboBox，所以按「值像不像分辨率 / 展开后有没有 800x600 选项」来认；
    /// 尽力而为，找不到或选不动只记日志，不阻塞登录。
    /// 注意：选择分辨率可能让登录界面尺寸变化，调用方要在取模板坐标之前调用它。
    /// </summary>
    public static bool EnsureWindowedResolution(nint windowHandle, LogService log)
    {
        try
        {
            WakeUpAccessibility(windowHandle);
            var root = AutomationElement.FromHandle(windowHandle);
            if (root == null)
            {
                return false;
            }

            // 先保证勾上「窗口模式」，否则分辨率下拉可能不可用。
            EnsureWindowedModeChecked(root, log);

            foreach (var combo in FindAll(root, ControlType.ComboBox))
            {
                if (TrySelectResolution(root, combo, log))
                {
                    return true;
                }
            }

            // 有的界面把下拉选项常驻在树里（CEF 自绘），直接找现成的 ListItem。
            var item = PickResolutionOption(FindAll(root, ControlType.ListItem));
            if (item.Element != null && SelectListItem(item.Element))
            {
                log.Success($"UIA：显示模式已选为「{item.Name}」", "UIA");
                return true;
            }

            log.Info("UIA：没找到「800x600(32位色)」的分辨率选项，跳过自动选择"
                     + "（请手动勾「窗口模式」并选 800x600(32位色)；16 位色会让游戏内模板偏色）", "UIA");
            return false;
        }
        catch (Exception ex)
        {
            log.Warn($"UIA：设置 800x600(32位色) 显示模式失败：{ex.Message}", "UIA");
            return false;
        }
    }

    /// <summary>确保登录界面底部的「窗口模式」是勾选状态。</summary>
    private static void EnsureWindowedModeChecked(AutomationElement root, LogService log)
    {
        try
        {
            var box = FindAll(root, ControlType.CheckBox)
                .FirstOrDefault(c => SafeName(c).Contains("窗口模式", StringComparison.Ordinal));
            if (box == null)
            {
                log.Info("UIA：没找到「窗口模式」勾选框，跳过（请手动勾选一次）", "UIA");
                return;
            }

            if (!box.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern) || pattern is not TogglePattern toggle)
            {
                log.Info("UIA：「窗口模式」不支持 TogglePattern，跳过（请手动勾选一次）", "UIA");
                return;
            }

            if (toggle.Current.ToggleState == ToggleState.On)
            {
                log.Info("UIA：「窗口模式」已经是勾选状态", "UIA");
                return;
            }

            toggle.Toggle();
            Thread.Sleep(300);
            log.Success("UIA：已勾选「窗口模式」", "UIA");
        }
        catch (Exception ex)
        {
            log.Warn($"UIA：勾选「窗口模式」失败：{ex.Message}", "UIA");
        }
    }

    /// <summary>展开一个下拉框，找到「800x600(32位色)」选项并选中，选完读回确认。</summary>
    private static bool TrySelectResolution(AutomationElement root, AutomationElement combo, LogService log)
    {
        var current = ReadValue(combo);

        // 这个客户端的下拉常常不暴露 ValuePattern，读回是空串；此时退一步用控件显示的文字判断。
        // 若 Name 里把整个下拉的选项都拼在一起（含 16位色），这里只会得到“不合格”从而重新选一次，
        // 属于幂等操作，不会选错。
        var shown = current.Length > 0 ? current : SafeName(combo);
        if (ResolutionScore(shown) > 0)
        {
            log.Info($"UIA：显示模式已经是「{shown}」，不需要调整", "UIA");
            return true;
        }

        if (Normalize(shown).StartsWith(TargetResolution, StringComparison.Ordinal))
        {
            log.Info($"UIA：显示模式当前是「{shown}」，不是 32 位色，重新选择", "UIA");
        }

        // 账号框也是 ComboBox，值是纯数字，肯定不是分辨率下拉，跳过它。
        if (shown.Length > 0 && shown.All(char.IsDigit))
        {
            return false;
        }

        if (!combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern)
            || pattern is not ExpandCollapsePattern expand)
        {
            return false;
        }

        expand.Expand();
        Thread.Sleep(400);
        try
        {
            var pick = PickResolutionOption(
                FindAll(combo, ControlType.ListItem).Concat(FindAll(root, ControlType.ListItem)));
            if (pick.Element == null)
            {
                var names = string.Join("、", FindAll(root, ControlType.ListItem).Take(12).Select(SafeName));
                log.Warn("UIA：分辨率下拉里没有「800x600(32位色)」，保持当前设置"
                         + $"（下拉里的选项：{(names.Length == 0 ? "读不到" : names)}）", "UIA");
                return false;
            }

            if (!SelectListItem(pick.Element))
            {
                log.Warn($"UIA：找到「{pick.Name}」但选不中（选项不支持 SelectionItemPattern）", "UIA");
                return false;
            }

            // 选完读回确认：CEF 的 Select() 偶尔只改视觉不改真实值。
            var applied = ReadValue(combo);
            if (ResolutionScore(applied) > 0)
            {
                log.Success($"UIA：显示模式已选为「{applied}」（800x600 32 位色）", "UIA");
                return true;
            }

            if (Normalize(applied).Length > 0)
            {
                log.Warn($"UIA：已点选「{pick.Name}」，但下拉框读回「{applied}」，分辨率没有切成 32 位色", "UIA");
                return false;
            }

            // 读不到值不代表失败：这个客户端常常不暴露 ValuePattern，以点选成功的选项文本为准。
            log.Success($"UIA：显示模式已选为「{pick.Name}」", "UIA");
            return true;
        }
        finally
        {
            try
            {
                expand.Collapse();
            }
            catch
            {
                // 收起失败不影响后续填表。
            }

            Thread.Sleep(200);
        }
    }

    /// <summary>用 SelectionItemPattern 选中列表项。</summary>
    private static bool SelectListItem(AutomationElement item)
    {
        try
        {
            if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                && pattern is SelectionItemPattern selection)
            {
                selection.Select();
                Thread.Sleep(250);
                return true;
            }
        }
        catch
        {
            // 选项已消失时忽略。
        }

        return false;
    }

    /// <summary>统一文本：去掉空格、把各种乘号换成 x、转小写，便于比较 800x600。</summary>
    private static string Normalize(string? text) =>
        (text ?? "").Replace(" ", "").Replace("×", "x").Replace("*", "x").ToLowerInvariant();

    /// <summary>点击「登录」按钮。</summary>
    public static bool Submit(nint windowHandle, LogService log)
    {
        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            if (root == null)
            {
                return false;
            }

            var button = FindLoginButton(root);
            if (button == null)
            {
                log.Warn("UIA：没找到「登录」按钮", "UIA");
                return false;
            }

            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) && pattern is InvokePattern invoke)
            {
                invoke.Invoke();
                log.Info($"UIA 已触发「{SafeName(button)}」", "UIA");
                return true;
            }

            log.Warn("UIA：登录按钮不支持 InvokePattern", "UIA");
            return false;
        }
        catch (Exception ex)
        {
            log.Warn($"UIA 点击登录异常：{ex.Message}", "UIA");
            return false;
        }
    }

    /// <summary>把窗口的控件树导出成文本，用于确认界面结构。</summary>
    public static string Describe(nint windowHandle, int maxNodes = 120)
    {
        var builder = new StringBuilder();
        try
        {
            WakeUpAccessibility(windowHandle);
            var root = AutomationElement.FromHandle(windowHandle);
            if (root == null)
            {
                return "UIA：拿不到根节点";
            }

            var walker = TreeWalker.ControlViewWalker;
            var visited = 0;

            void Walk(AutomationElement element, int depth)
            {
                if (visited++ > maxNodes || depth > 8)
                {
                    return;
                }

                builder.Append(' ', depth * 2).Append(DescribeElement(element)).AppendLine();

                AutomationElement? child;
                try
                {
                    child = walker.GetFirstChild(element);
                }
                catch
                {
                    return;
                }

                while (child != null)
                {
                    Walk(child, depth + 1);
                    try
                    {
                        child = walker.GetNextSibling(child);
                    }
                    catch
                    {
                        return;
                    }
                }
            }

            Walk(root, 0);
            return builder.Length == 0 ? "UIA：窗口没有暴露任何子控件" : builder.ToString();
        }
        catch (Exception ex)
        {
            return $"UIA 探测异常：{ex.Message}";
        }
    }

    /// <summary>
    /// 点「登录」之前的最后一道闸：
    /// ① 账号文本必须与目标账号一致（读得到才比，读不到不拦）；
    /// ② 账号文本已确认一致时不再比像素（界面记住账号时这一块本来就没有变化）；
    /// ③ 密码这块：相对「清空后基线」必须像真写了一排字／圆点。
    /// 任一不满足就返回 Failed，交给键鼠流程重来——绝不带着空密码提交。
    /// </summary>
    private static UiaResult SubmitChecked(nint windowHandle, AutomationElement accountEdit, string account,
        CaptureField capture, CompareField compare, FieldSnapshot? accountBaseline, FieldSnapshot? passwordBaseline,
        LogService log)
    {
        // 账号框文本：CEF 的登录框没有 ValuePattern，文本挂在 Name 上，用 SafeFieldText 两种都读。
        // 这里必须重新读一次，不能沿用进入时的结论：中途若被别的操作清空，靠旧结论会带着空账号点登录。
        var accountValue = SafeFieldText(accountEdit).Trim();
        var accountOkByText = SameAccount(accountValue, account);
        if (accountValue.Length > 0 && !accountOkByText)
        {
            log.Warn($"UIA 登录前检查：账号框内容是「{accountValue}」，与目标账号「{account}」不一致，不点「登录」", "UIA");
            return UiaResult.Failed;
        }

        if (accountValue.Length == 0)
        {
            // 读不到 ≠ 没写进去：本机实测登录框就是不暴露可读文本，此时以像素证据为准，只提示不拦。
            log.Info("UIA 登录前检查：账号框读不到可读文本（CEF 未暴露 ValuePattern/Name），改以像素证据为准", "UIA");
        }

        // 相对「清空后基线」再核对一次：万一焦点离开后画面被重置（SetValue 就是这样），这一步能拦住。
        var accountEvidence = Compare(FieldKind.Account, capture, compare, accountBaseline);
        var passwordEvidence = Compare(FieldKind.Password, capture, compare, passwordBaseline);

        if (!accountOkByText && !InkOk(accountEvidence))
        {
            ReportInk(accountEvidence, "UIA 登录前检查（账号）", "账号框", log, false);
            log.Warn("UIA 登录前检查：账号框不像真写了字，不点「登录」", "UIA");
            return UiaResult.Failed;
        }

        if (!InkOk(passwordEvidence))
        {
            ReportInk(passwordEvidence, "UIA 登录前检查（密码）", "密码框", log, false);
            log.Warn("UIA 登录前检查：密码框不像真写进了密码，不点「登录」", "UIA");
            return UiaResult.Failed;
        }

        var accountPart = accountOkByText
            ? $"「{accountValue}」文本一致"
            : $"「读不到，以画面为准」（{accountEvidence.ChangedPixels} 像素）";
        log.Info($"UIA 登录前检查通过：账号 {accountPart}、"
                 + $"密码框 {passwordEvidence.ChangedPixels} 像素（掩码读回为空属正常）", "UIA");

        return Submit(windowHandle, log) ? UiaResult.Submitted : UiaResult.Failed;
    }

    /// <summary>读回控件的 ValuePattern 当前值，读不到就返回空串。</summary>
    private static string ReadValue(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
            {
                return valuePattern.Current.Value ?? "";
            }
        }
        catch
        {
            // 控件失效/被弹窗遮住时读不到值，按空处理。
        }

        return "";
    }

    private static void WakeUpAccessibility(nint windowHandle)
    {
        try
        {
            SendMessage(windowHandle, WmGetObject, ObjIdClient, nint.Zero);
            NativeMethods.EnumChildWindows(windowHandle, (child, _) =>
            {
                SendMessage(child, WmGetObject, ObjIdClient, nint.Zero);
                return true;
            }, nint.Zero);
        }
        catch
        {
            // 唤醒失败不影响后续枚举。
        }
    }

    private static List<AutomationElement> FindAll(AutomationElement root, ControlType type)
    {
        var result = new List<AutomationElement>();
        try
        {
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
            var found = root.FindAll(TreeScope.Descendants, condition);
            foreach (AutomationElement element in found)
            {
                result.Add(element);
            }
        }
        catch
        {
            // 遍历中断时返回已找到的部分。
        }

        return result;
    }

    /// <summary>
    /// 取全部后代节点，不按 ControlType 过滤。CEF 自绘的输入框 ControlType 会在 Edit 与 Pane 之间摆动，
    /// 只按 ControlType 找必然漏；这里一律先取全量，再用 ClassName 认。
    /// </summary>
    private static List<AutomationElement> FindAllDescendants(AutomationElement root)
    {
        var result = new List<AutomationElement>();
        try
        {
            var found = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement element in found)
            {
                result.Add(element);
            }
        }
        catch
        {
            // 整棵树取不到时只能返回空，调用方会整轮重试。
        }

        return result;
    }

    /// <summary>
    /// 读取输入框里的文本：有 ValuePattern 用 ValuePattern，没有就用 Name。
    /// 本机实测 CEF 的登录框**没有** ValuePattern，账号文本是挂在 Name 上的
    /// （例如 name='853507849'），所以只读 ValuePattern 会永远拿到空串。
    /// </summary>
    private static string SafeFieldText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
            {
                var value = valuePattern.Current.Value;
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }
        catch
        {
            // 没有 ValuePattern 或已失效，退回 Name。
        }

        return SafeName(element);
    }

    /// <summary>控件的 ClassName（CEF 的输入框 ClassName 恒为 Edit，比 ControlType 可靠）。</summary>
    private static string SafeClassName(AutomationElement element)
    {
        try
        {
            return element.Current.ClassName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool SupportsValuePattern(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>优先精确匹配「登录」，避免误点「快速登录」。</summary>
    private static AutomationElement? FindLoginButton(AutomationElement root)
    {
        var buttons = FindAll(root, ControlType.Button);
        return buttons.FirstOrDefault(b => SafeName(b) == "登录")
               ?? buttons.FirstOrDefault(b => SafeName(b).Contains("登录", StringComparison.Ordinal));
    }

    private static string DescribeElement(AutomationElement element) =>
        $"{SafeControlType(element)}「{SafeName(element)}」";

    private static string DescribeWithRect(AutomationElement element) =>
        $"{DescribeElement(element)}{ToFieldRect(element)}";

    private static System.Windows.Rect SafeRect(AutomationElement element)
    {
        try
        {
            return element.Current.BoundingRectangle;
        }
        catch
        {
            return System.Windows.Rect.Empty;
        }
    }

    /// <summary>控件是否声明为密码框（Chromium 会正确声明，这是定位密码框最可靠的依据）。</summary>
    private static bool SafeIsPassword(AutomationElement element)
    {
        try
        {
            return element.Current.IsPassword;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeOffscreen(AutomationElement element)
    {
        try
        {
            return element.Current.IsOffscreen;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string SafeControlType(AutomationElement element)
    {
        try
        {
            return element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
        }
        catch
        {
            return "Unknown";
        }
    }
}
