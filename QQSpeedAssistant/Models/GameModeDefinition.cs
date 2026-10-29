namespace QQSpeedAssistant.Models;

/// <summary>
/// 一个可玩玩的定义：名称、分类、档期、完成任务所需账号数以及游戏内入口路径。
/// 数据来源为《QQ飞车端游现有可玩模式一览》，入口路径直接用于界面提示与流程校准。
/// </summary>
public sealed class GameModeDefinition
{
    /// <summary>稳定的玩法标识，同时作为流程文件与模板目录名。</summary>
    public required string Id { get; init; }

    /// <summary>玩法中文名。</summary>
    public required string Name { get; init; }

    /// <summary>玩法分类。</summary>
    public ModeCategory Category { get; init; }

    /// <summary>档期。</summary>
    public ModeAvailability Availability { get; init; }

    /// <summary>完成一局需要同时登录的账号数量，>1 表示需要多开组队。</summary>
    public int RequiredAccounts { get; init; } = 1;

    /// <summary>游戏内完整入口路径。</summary>
    public required string EntryPath { get; init; }

    /// <summary>补充说明（例如人数、开放时间）。</summary>
    public string Note { get; init; } = "";

    /// <summary>默认任务局数。</summary>
    public int DefaultRounds { get; init; } = 1;

    /// <summary>是否需要多开组队。</summary>
    public bool IsTeamMode => RequiredAccounts > 1;

    /// <summary>分类显示文本。</summary>
    public string CategoryText => Category.ToText();

    /// <summary>档期显示文本。</summary>
    public string AvailabilityText => Availability.ToText();

    /// <summary>人数显示文本。</summary>
    public string PlayerCountText => RequiredAccounts > 1 ? $"需 {RequiredAccounts} 个账号" : "单账号";
}
