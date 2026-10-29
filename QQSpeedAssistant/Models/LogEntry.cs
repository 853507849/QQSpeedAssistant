namespace QQSpeedAssistant.Models;

/// <summary>一条运行日志。</summary>
public sealed class LogEntry
{
    /// <summary>产生时间。</summary>
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>级别。</summary>
    public LogLevel Level { get; init; }

    /// <summary>来源（账号、玩法或服务名）。</summary>
    public string Source { get; init; } = "";

    /// <summary>内容。</summary>
    public string Message { get; init; } = "";

    /// <summary>时间显示文本。</summary>
    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>级别显示文本。</summary>
    public string LevelText => Level switch
    {
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        LogLevel.Success => "成功",
        _ => "信息"
    };
}
