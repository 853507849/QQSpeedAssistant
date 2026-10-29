using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 档期判断：对可以按星期推断的玩法做本地校验，避免在未开放时间空跑。
/// 活动期／赛季轮换类玩法无法本地判断，由用户在任务列表里自行勾选。
/// </summary>
public static class ModeAvailabilityRules
{
    /// <summary>判断玩法当前是否可能开放，并给出原因。</summary>
    public static (bool Open, string Reason) Check(ModeAvailability availability, DateTime now)
    {
        var isWeekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return availability switch
        {
            ModeAvailability.Weekend when !isWeekend => (false, "该玩法仅周末开放"),
            ModeAvailability.Monthly when !isWeekend && now.DayOfWeek != DayOfWeek.Friday
                => (false, "该玩法仅周五至周日开放"),
            _ => (true, "")
        };
    }
}
