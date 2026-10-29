using System.Diagnostics;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 降帧服务：多开时每个登录账号都执行一次，降低游戏渲染压力。
/// 采用两条手段：降低进程优先级（立即生效）＋执行 flows/lower_fps.json 修改游戏内帧率设置。
/// </summary>
public sealed class FrameRateService
{
    private readonly FlowLoader _flows;
    private readonly FlowRunner _runner;
    private readonly LogService _log;
    private readonly HashSet<nint> _applied = [];

    /// <summary>构造降帧服务。</summary>
    public FrameRateService(FlowLoader flows, FlowRunner runner, LogService log)
    {
        _flows = flows;
        _runner = runner;
        _log = log;
    }

    /// <summary>对已登录的会话执行降帧，同一窗口只执行一次。</summary>
    public async Task ApplyAsync(GameSession session, AccountEntry account, AppSettings settings, CancellationToken ct)
    {
        if (!_applied.Add(session.Handle))
        {
            return;
        }

        if (settings.LowerProcessPriority)
        {
            LowerPriority(session, account);
        }

        var flow = _flows.Load("lower_fps");
        if (flow == null)
        {
            _log.Warn("未找到 flows/lower_fps.json，本次只降低了进程优先级；如需降帧请在游戏内设置后记录流程", account.Account);
            return;
        }

        try
        {
            var context = new FlowContext { Account = account, ModeId = "lower_fps" };
            context.Variables["fps"] = settings.LowFrameRateTarget.ToString();
            await _runner.ExecuteAsync(flow, session, context, ct);
            _log.Success($"已执行降帧流程（目标 {settings.LowFrameRateTarget} FPS）", account.Account);
        }
        catch (FlowException ex)
        {
            _log.Warn($"降帧流程未完成：{ex.Message}", account.Account);
        }
    }

    /// <summary>把游戏进程优先级降为 BelowNormal。</summary>
    private void LowerPriority(GameSession session, AccountEntry account)
    {
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            _log.Info("已将游戏进程优先级降为 BelowNormal", account.Account);
        }
        catch (Exception ex)
        {
            _log.Warn($"调整进程优先级失败：{ex.Message}", account.Account);
        }
    }
}
