using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>单个玩法的执行结果。</summary>
public sealed record ModeRunResult(bool Success, bool NotConfigured, string Message, string? Reason = null)
{
    /// <summary>执行成功。</summary>
    public static ModeRunResult Ok() => new(true, false, "");

    /// <summary>流程未配置。</summary>
    public static ModeRunResult MissingFlow(string flowName)
        => new(false, true, $"缺少 flows/{flowName}.json", FlowFailReasons.FlowMissing);

    /// <summary>执行失败。</summary>
    public static ModeRunResult Fail(string message, string? reason = null) => new(false, false, message, reason);
}

/// <summary>
/// 玩法执行：按玩法 Id 加载流程并驱动一局。组队玩法支持 <c>{玩法Id}.member.json</c> 作为队员流程。
/// </summary>
public sealed class ModeExecutionService
{
    private readonly FlowLoader _flows;
    private readonly FlowRunner _runner;
    private readonly RewardService _rewards;
    private readonly LogService _log;

    /// <summary>构造玩法执行服务。</summary>
    public ModeExecutionService(FlowLoader flows, FlowRunner runner, RewardService rewards, LogService log)
    {
        _flows = flows;
        _runner = runner;
        _rewards = rewards;
        _log = log;
    }

    /// <summary>是否存在可执行的流程。</summary>
    public bool HasFlow(GameModeDefinition mode, bool isHost = true)
        => _flows.Exists(isHost ? mode.Id : $"{mode.Id}.member") || _flows.Exists(mode.Id);

    /// <summary>执行一局玩法，并在需要时截图保存奖励。</summary>
    public async Task<ModeRunResult> RunRoundAsync(GameSession session, AccountEntry account, GameModeDefinition mode,
        ModeTaskConfig task, int round, bool isHost, AppSettings settings, CancellationToken ct)
    {
        var flowName = isHost ? mode.Id : $"{mode.Id}.member";
        var flow = _flows.Load(flowName) ?? (isHost ? null : _flows.Load(mode.Id));
        if (flow == null)
        {
            return ModeRunResult.MissingFlow(flowName);
        }

        var context = new FlowContext { Account = account, ModeId = mode.Id, Round = round };
        context.Variables["role"] = isHost ? "host" : "member";
        context.Variables["mode"] = mode.Name;

        try
        {
            await _runner.ExecuteAsync(flow, session, context, ct);
        }
        catch (FlowException ex)
        {
            return ModeRunResult.Fail(ex.Message, ex.Reason);
        }

        // 流程内若已有 saveReward 步骤会在结算画面当时截图；这里只兜底没有配置该步骤的玩法
        if (task.CaptureReward && !context.RewardRequested)
        {
            await Task.Delay(Math.Max(0, settings.RewardScreenshotDelayMs), ct);
            _rewards.Capture(session, account, mode.Name, round);
        }

        return ModeRunResult.Ok();
    }
}
