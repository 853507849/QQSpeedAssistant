using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 票完动作：全部账号跑完后按配置收尾——停止、最小化、关机、重启或重新白票。
/// </summary>
public sealed class FinishActionService
{
    private readonly ShutdownService _shutdown;
    private readonly LogService _log;
    private Func<Task>? _rerunAsync;

    /// <summary>构造票完动作服务。</summary>
    public FinishActionService(ShutdownService shutdown, LogService log)
    {
        _shutdown = shutdown;
        _log = log;
    }

    /// <summary>由任务编排注入「重新开始一轮」的回调。</summary>
    public void SetRerunHandler(Func<Task> rerunAsync) => _rerunAsync = rerunAsync;

    /// <summary>
    /// 计算本次实际要执行的动作：优先用 <see cref="AppSettings.FinishAction"/>；
    /// 未设置时兼容旧配置里的「全部完成自动关机」。
    /// </summary>
    public static FinishAction ResolveEffective(AppSettings settings)
        => settings.FinishAction == FinishAction.None && settings.AutoShutdown
            ? FinishAction.Shutdown
            : settings.FinishAction;

    /// <summary>执行票完动作。</summary>
    public async Task ExecuteAsync(AppSettings settings, CancellationToken ct)
    {
        var action = ResolveEffective(settings);
        _log.Info($"票完动作：{action.ToText()}", "收尾");

        switch (action)
        {
            case FinishAction.None:
                return;

            case FinishAction.Minimize:
                Minimize();
                return;

            case FinishAction.Shutdown:
                _shutdown.Schedule(Math.Max(10, settings.FinishActionDelaySeconds),
                    "QQ飞车任务全部完成，自动关机");
                return;

            case FinishAction.Restart:
                _shutdown.ScheduleRestart(Math.Max(10, settings.FinishActionDelaySeconds),
                    "QQ飞车任务全部完成，自动重启");
                return;

            case FinishAction.Rerun:
                await RerunAsync(settings, ct);
                return;
        }
    }

    /// <summary>把主窗口最小化到任务栏。</summary>
    private void Minimize()
    {
        try
        {
            var handle = Application.Current?.MainWindow is { } window
                ? new WindowInteropHelper(window).Handle
                : Process.GetCurrentProcess().MainWindowHandle;

            if (handle != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(handle, 6); // SW_MINIMIZE
                _log.Info("已最小化窗口", "收尾");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"最小化失败：{ex.Message}", "收尾");
        }
    }

    /// <summary>
    /// 票完重爆：开了定时上号就按「隔天到点再来」（当天的时间点已经过了），
    /// 没开就等一会儿立即重新开始。
    /// </summary>
    private async Task RerunAsync(AppSettings settings, CancellationToken ct)
    {
        if (settings.ScheduledStartEnabled)
        {
            _log.Info($"已开启定时上号（{settings.ScheduledStartTime}），今天的时间点已过，将在明天到点自动重来", "收尾");
            return;
        }

        if (_rerunAsync == null)
        {
            _log.Warn("没有注入重跑回调，票完重爆未执行", "收尾");
            return;
        }

        var delay = Math.Clamp(settings.RerunDelaySeconds, 5, 3600);
        _log.Warn($"{delay} 秒后重新开始白票（期间可点「停止任务」取消）", "收尾");
        await Task.Delay(delay * 1000, ct);
        await _rerunAsync();
    }
}
