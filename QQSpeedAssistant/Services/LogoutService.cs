using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 退出登录：执行 flows/logout.json，把客户端退回登录界面，供下一个账号复用同一个窗口。
/// 未配置流程时不做任何操作，由上层决定是否另开客户端。
/// </summary>
public sealed class LogoutService
{
    private readonly FlowLoader _flows;
    private readonly FlowRunner _runner;
    private readonly LogService _log;

    /// <summary>构造退出服务。</summary>
    public LogoutService(FlowLoader flows, FlowRunner runner, LogService log)
    {
        _flows = flows;
        _runner = runner;
        _log = log;
    }

    /// <summary>执行退出流程，成功返回 true。</summary>
    public async Task<bool> RunAsync(GameSession session, AccountEntry account, CancellationToken ct)
    {
        var flow = _flows.Load("logout");
        if (flow == null)
        {
            return false;
        }

        try
        {
            var context = new FlowContext { Account = account, ModeId = "logout" };
            await _runner.ExecuteAsync(flow, session, context, ct);
            session.Unbind();
            _log.Info("已退出当前账号，窗口可供下一个账号复用", account.Account);
            return true;
        }
        catch (FlowException ex)
        {
            _log.Warn($"退出登录流程失败：{ex.Message}", account.Account);
            return false;
        }
    }
}
