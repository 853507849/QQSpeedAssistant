namespace QQSpeedAssistant.Services;

/// <summary>
/// 定时上号／定时下号：每 20 秒检查一次配置的时间点，同一个时间点当天只触发一次。
/// </summary>
public sealed class ScheduleService : IDisposable
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private Timer? _timer;
    private DateOnly _lastStartDate = DateOnly.MinValue;
    private DateOnly _lastStopDate = DateOnly.MinValue;

    /// <summary>构造定时服务。</summary>
    public ScheduleService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>到达定时上号时间时触发，回调负责启动任务。</summary>
    public event Func<Task>? Triggered;

    /// <summary>到达定时下号时间时触发，回调负责停止任务并执行票完动作。</summary>
    public event Func<Task>? StopTriggered;

    /// <summary>启动定时检查。</summary>
    public void Start()
    {
        _timer ??= new Timer(Check, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20));
    }

    /// <summary>停止定时检查。</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>定时回调。</summary>
    private async void Check(object? state)
    {
        try
        {
            var settings = _config.Settings;
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);

            // 定时上号与定时下号各自独立判定，互不影响
            if (settings.ScheduledStartEnabled
                && TimeOnly.TryParse(settings.ScheduledStartTime, out var start)
                && _lastStartDate != today
                && IsDue(now, start))
            {
                _lastStartDate = today;
                _log.Info($"到达定时上号时间 {settings.ScheduledStartTime}，开始执行任务", "定时");
                if (Triggered != null)
                {
                    await Triggered.Invoke();
                }
            }

            if (settings.ScheduledStopEnabled
                && TimeOnly.TryParse(settings.ScheduledStopTime, out var stop)
                && _lastStopDate != today
                && IsDue(now, stop))
            {
                _lastStopDate = today;
                _log.Info($"到达定时下号时间 {settings.ScheduledStopTime}，停止任务并执行票完动作", "定时");
                if (StopTriggered != null)
                {
                    await StopTriggered.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"定时任务异常：{ex.Message}", "定时");
        }
    }

    /// <summary>是否已经到了触发点：允许 5 分钟误差，避免系统休眠错过时间点。</summary>
    private static bool IsDue(DateTime now, TimeOnly target)
    {
        var diff = now.TimeOfDay - target.ToTimeSpan();
        return diff >= TimeSpan.Zero && diff <= TimeSpan.FromMinutes(5);
    }

    /// <summary>释放定时器。</summary>
    public void Dispose() => Stop();
}
