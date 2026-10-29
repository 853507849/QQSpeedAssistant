using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace QQSpeedAssistant.Models;

/// <summary>玩法分类，对应文档「类型」列。</summary>
public enum ModeCategory
{
    Racing,
    Item,
    Casual,
    Team,
    Ranked,
    Event,
    Border,
    Pve,
    Other
}

/// <summary>玩法档期，对应文档「档期」列。档期不满足时不会执行该玩法。</summary>
public enum ModeAvailability
{
    Always,
    Weekend,
    Daily,
    Weekly,
    Monthly,
    Event,
    Seasonal
}

/// <summary>单个账号在一次运行中的状态。</summary>
public enum AccountRunStatus
{
    NotStarted,
    Running,
    Completed,
    PasswordFailed,
    LoginTimeout,
    FlowMissing,
    Failed,
    Skipped,

    /// <summary>账号被冻结或提示需要验证，已跳过。</summary>
    Frozen,

    /// <summary>登录时出现验证码，无法自动通过。</summary>
    Captcha,

    /// <summary>提示登录环境异常（异地、环境风险等）。</summary>
    EnvAbnormal,

    /// <summary>游戏中途掉线且重连失败。</summary>
    Disconnected
}

/// <summary>全部账号跑完后的收尾动作链。</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum FinishAction
{
    /// <summary>什么也不做，停在界面。</summary>
    None,

    /// <summary>最小化本程序。</summary>
    Minimize,

    /// <summary>关机。</summary>
    Shutdown,

    /// <summary>重启电脑（配合开机自启实现隔天自动重来）。</summary>
    Restart,

    /// <summary>重新开始白票（未开定时上号时立即重来，开了则隔天到点重来）。</summary>
    Rerun
}

/// <summary>整次运行（全部账号）的状态。</summary>
public enum RunState
{
    Idle,
    Running,
    Stopping,
    Completed,
    Failed
}

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Info,
    Warning,
    Error,
    Success
}

/// <summary>枚举的中文显示文本。</summary>
public static class EnumText
{
    /// <summary>玩法分类显示名。</summary>
    public static string ToText(this ModeCategory category) => category switch
    {
        ModeCategory.Racing => "竞速",
        ModeCategory.Item => "道具",
        ModeCategory.Casual => "休闲",
        ModeCategory.Team => "团队",
        ModeCategory.Ranked => "排位",
        ModeCategory.Event => "活动",
        ModeCategory.Border => "边境",
        ModeCategory.Pve => "PVE",
        _ => "其他"
    };

    /// <summary>档期显示名。</summary>
    public static string ToText(this ModeAvailability availability) => availability switch
    {
        ModeAvailability.Always => "常驻",
        ModeAvailability.Weekend => "周末限时",
        ModeAvailability.Daily => "每日限时",
        ModeAvailability.Weekly => "每周定点",
        ModeAvailability.Monthly => "每月限时",
        ModeAvailability.Event => "活动期",
        _ => "赛季轮换"
    };

    /// <summary>账号运行状态显示名。</summary>
    public static string ToText(this AccountRunStatus status) => status switch
    {
        AccountRunStatus.NotStarted => "未开始",
        AccountRunStatus.Running => "进行中",
        AccountRunStatus.Completed => "已完成",
        AccountRunStatus.PasswordFailed => "密码错误，已跳过",
        AccountRunStatus.LoginTimeout => "登录超时",
        AccountRunStatus.FlowMissing => "流程未配置",
        AccountRunStatus.Frozen => "账号冻结，已跳过",
        AccountRunStatus.Captcha => "验证码拦截，已跳过",
        AccountRunStatus.EnvAbnormal => "环境异常，已跳过",
        AccountRunStatus.Disconnected => "掉线未重连",
        AccountRunStatus.Failed => "失败",
        _ => "已跳过"
    };

    /// <summary>票完动作显示名。</summary>
    public static string ToText(this FinishAction action) => action switch
    {
        FinishAction.None => "票完停止",
        FinishAction.Minimize => "票完小化",
        FinishAction.Shutdown => "票完关机",
        FinishAction.Restart => "票完重启",
        _ => "票完重爆"
    };

    /// <summary>整体运行状态显示名。</summary>
    public static string ToText(this RunState state) => state switch
    {
        RunState.Idle => "空闲",
        RunState.Running => "运行中",
        RunState.Stopping => "正在停止",
        RunState.Completed => "已完成",
        _ => "运行异常"
    };
}
