namespace QQSpeedAssistant.Models;

/// <summary>
/// 流程执行上下文：提供当前账号、局数与变量替换。
/// </summary>
public sealed class FlowContext
{
    /// <summary>当前账号。</summary>
    public required AccountEntry Account { get; init; }

    /// <summary>当前玩法标识。</summary>
    public required string ModeId { get; init; }

    /// <summary>当前局数，从 1 开始。</summary>
    public int Round { get; init; }

    /// <summary>流程是否请求截图保存奖励。</summary>
    public bool RewardRequested { get; set; }

    /// <summary>本次流程中判定到「永久」奖励的次数。</summary>
    public int PermanentHits { get; set; }

    /// <summary>最近一次识别到的模板区域（画面坐标），供后续步骤做输入／点击自检。</summary>
    public (int X, int Y, int Width, int Height)? LastRegion { get; set; }

    /// <summary>最近一次识别到的模板名称，用于日志提示。</summary>
    public string? LastTemplate { get; set; }

    /// <summary>本次流程中真正落到画面上的点击次数。</summary>
    public int ClickCount { get; set; }

    /// <summary>本次流程中因为模板缺失／未匹配而跳过的步骤数。</summary>
    public int SkippedSteps { get; set; }

    /// <summary>附加变量，可在流程文本中用 {键} 引用。</summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>替换文本中的占位符。</summary>
    public string Resolve(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var result = text
            .Replace("{account}", Account.Account)
            .Replace("{password}", Account.Password)
            .Replace("{server}", Account.Server)
            .Replace("{round}", Round.ToString());

        foreach (var pair in Variables)
        {
            result = result.Replace($"{{{pair.Key}}}", pair.Value);
        }

        return result;
    }
}
