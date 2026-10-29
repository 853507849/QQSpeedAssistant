using Newtonsoft.Json;

namespace QQSpeedAssistant.Models;

/// <summary>
/// 一段可执行的自动化流程，从 flows/{名称}.json 反序列化。
/// 流程按步骤顺序执行，任何必填步骤失败都会抛出 <see cref="FlowException"/>。
/// </summary>
public sealed class AutomationFlow
{
    /// <summary>流程名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>流程说明。</summary>
    public string Description { get; set; } = "";

    /// <summary>步骤列表。</summary>
    public List<FlowStep> Steps { get; set; } = [];
}

/// <summary>流程中的一个步骤。</summary>
public sealed class FlowStep
{
    /// <summary>步骤类型，取值见 <see cref="FlowStepTypes"/>。</summary>
    public string Type { get; set; } = FlowStepTypes.Log;

    /// <summary>按键，支持「Space」「Enter」「Esc」「F5」「Ctrl+A」这类写法。</summary>
    public string? Key { get; set; }

    /// <summary>模板图片文件名（相对模板目录，可省略 .png）。</summary>
    public string? Template { get; set; }

    /// <summary>日志文本或输入文本，支持 {account} {password} {server} {round} 占位符。</summary>
    public string? Text { get; set; }

    /// <summary>失败原因，用于上层区分密码错误、超时等场景。</summary>
    public string? FailReason { get; set; }

    /// <summary>点击位置占宽度的比例（0 至 1），与 <see cref="YRatio"/> 同时设置时优先使用比例。</summary>
    public double XRatio { get; set; }

    /// <summary>点击位置占高度的比例（0 至 1）。</summary>
    public double YRatio { get; set; }

    /// <summary>客户区绝对 X 坐标（像素），比例未设置时生效。</summary>
    public int X { get; set; }

    /// <summary>客户区绝对 Y 坐标（像素）。</summary>
    public int Y { get; set; }

    /// <summary>模板匹配阈值，0 至 1。</summary>
    public double Threshold { get; set; } = 0.8;

    /// <summary>
    /// 模板不可用（还没采集，或临时认不出来）时的兜底点击位置，占客户区宽度的比例。
    /// 与 <see cref="FallbackYRatio"/> 同时大于 0 才生效，用于「控件位置固定但截图不好采」的场景。
    /// </summary>
    public double FallbackXRatio { get; set; }

    /// <summary>兜底点击位置，占客户区高度的比例。</summary>
    public double FallbackYRatio { get; set; }

    /// <summary>是否配置了兜底点击坐标。</summary>
    public bool HasFallback => FallbackXRatio > 0 && FallbackYRatio > 0;

    /// <summary>等待超时（毫秒）。</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// 是否自检：clickImage 会校验点击后画面是否变化（没变化自动重试），
    /// typeText 会校验文字是否真的进了输入框（没进去自动重新点击再输入）。
    /// 建议登录这类关键步骤开启，玩法流程可关闭以节省时间。
    /// </summary>
    public bool Verify { get; set; }

    /// <summary>自检时的最多尝试次数（点击类步骤），默认 2 次，避免重复点击触发多次登录。</summary>
    public int VerifyAttempts { get; set; } = 2;

    /// <summary>自动聚焦输入框时最多按几次 Tab（typeTextAutoFocus 步骤）。</summary>
    public int MaxTabs { get; set; } = 5;

    /// <summary>
    /// 账号框里已经填着配置里的账号时，跳过这一步的输入（typeTextAutoFocus 步骤）。
    /// 登录界面有「记住账号」时，账号框本来就填着上次的账号：对它清空再重打同样的内容，
    /// 画面往往一点变化都没有，靠像素判定会误判成「没输进去」，把整个流程卡在账号这一步。
    /// 设为 true 时先读一次 UIA 文本比对，一致就直接进入下一步（密码）。
    /// </summary>
    public bool SkipIfAccountRemembered { get; set; }

    /// <summary>
    /// 多个模板名。waitImageAny 用「任一出现即可」，checkPermanent 用「任一命中即算永久」，
    /// claimReward／openChest 用「按顺序逐个点击」。
    /// </summary>
    public List<string>? Templates { get; set; }

    /// <summary>repeat 步骤的重复次数。</summary>
    public int Count { get; set; } = 1;

    /// <summary>randomWait 的随机等待下限（毫秒）。</summary>
    public int MinMs { get; set; }

    /// <summary>randomWait 的随机等待上限（毫秒），0 时取下限 + 500。</summary>
    public int MaxMs { get; set; }

    /// <summary>repeat 步骤内嵌的子步骤。</summary>
    public List<FlowStep>? Steps { get; set; }

    /// <summary>步骤失败时是否中断流程。</summary>
    public bool Required { get; set; } = true;

    /// <summary>是否使用比例坐标。</summary>
    [JsonIgnore]
    public bool UseRatio => XRatio > 0 || YRatio > 0;
}

/// <summary>流程步骤类型常量。</summary>
public static class FlowStepTypes
{
    /// <summary>输出日志。</summary>
    public const string Log = "log";

    /// <summary>固定等待。</summary>
    public const string Wait = "wait";

    /// <summary>按键（支持组合键）。</summary>
    public const string Key = "key";

    /// <summary>点击客户区坐标。</summary>
    public const string Click = "click";

    /// <summary>输入文本。</summary>
    public const string TypeText = "typeText";

    /// <summary>
    /// 不点鼠标，用键盘自动把焦点移到目标输入框后再输入：
    /// 先打一个探针字符并观察目标区域有没有变化，没变化就按 Tab 换下一个控件。
    /// </summary>
    public const string TypeTextAutoFocus = "typeTextAutoFocus";

    /// <summary>清空当前输入框（Ctrl+A + Delete + 退格兜底）。</summary>
    public const string ClearText = "clearText";

    /// <summary>查找模板并点击其中心。</summary>
    public const string ClickImage = "clickImage";

    /// <summary>等待模板出现。</summary>
    public const string WaitImage = "waitImage";

    /// <summary>等待模板从画面上消失（用于确认已离开某个界面）。</summary>
    public const string WaitImageGone = "waitImageGone";

    /// <summary>断言模板必须出现。</summary>
    public const string ExpectImage = "expectImage";

    /// <summary>断言模板必须不出现（用于识别密码错误等异常界面）。</summary>
    public const string ExpectNoImage = "expectNoImage";

    /// <summary>标记本局需要截图保存奖励。</summary>
    public const string SaveReward = "saveReward";

    /// <summary>领取本玩法奖励（按 templates 顺序逐个点击，默认 reward_claim → reward_claim_confirm）。</summary>
    public const string ClaimReward = "claimReward";

    /// <summary>开宝箱（按 templates 顺序逐个点击，默认 reward_chest → reward_chest_open）。</summary>
    public const string OpenChest = "openChest";

    /// <summary>判定是否中了永久道具，中了就单独截图归档到「永久」目录。</summary>
    public const string CheckPermanent = "checkPermanent";

    /// <summary>等待多个模板中的任意一个出现。</summary>
    public const string WaitImageAny = "waitImageAny";

    /// <summary>随机等待一段时间（防机械节奏）。</summary>
    public const string RandomWait = "randomWait";

    /// <summary>把内嵌步骤重复执行 count 次。</summary>
    public const string Repeat = "repeat";
}

/// <summary>流程失败原因常量。</summary>
public static class FlowFailReasons
{
    /// <summary>密码错误。</summary>
    public const string WrongPassword = "wrong_password";

    /// <summary>等待超时。</summary>
    public const string Timeout = "timeout";

    /// <summary>玩法入口不可用（未开放、次数用尽等）。</summary>
    public const string ModeUnavailable = "mode_unavailable";

    /// <summary>模板缺失，流程尚未校准。</summary>
    public const string TemplateMissing = "template_missing";

    /// <summary>流程文件缺失。</summary>
    public const string FlowMissing = "flow_missing";

    /// <summary>其他断言失败。</summary>
    public const string AssertFailed = "assert_failed";

    /// <summary>账号被冻结／需要解冻验证。</summary>
    public const string Frozen = "frozen";

    /// <summary>登录时出现验证码。</summary>
    public const string Captcha = "captcha";

    /// <summary>登录环境异常（异地登录、环境风险提示）。</summary>
    public const string EnvAbnormal = "env_abnormal";

    /// <summary>游戏中途掉线／网络中断。</summary>
    public const string Disconnected = "disconnected";
}

/// <summary>流程执行失败。</summary>
public sealed class FlowException : Exception
{
    /// <summary>构造一个流程失败异常。</summary>
    public FlowException(string reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>失败原因，取值见 <see cref="FlowFailReasons"/>。</summary>
    public string Reason { get; }
}
