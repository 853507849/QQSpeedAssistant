using CommunityToolkit.Mvvm.ComponentModel;

namespace QQSpeedAssistant.Models;

/// <summary>某个玩法的任务配置：是否执行、执行局数、结算后是否截图保存奖励。</summary>
public sealed partial class ModeTaskConfig : ObservableObject
{
    /// <summary>玩法标识，对应 <see cref="GameModeDefinition.Id"/>。</summary>
    [ObservableProperty]
    private string _modeId = "";

    /// <summary>是否执行该玩法。</summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>需要完成的局数。</summary>
    [ObservableProperty]
    private int _rounds = 1;

    /// <summary>每局结束后是否截图保存奖励。</summary>
    [ObservableProperty]
    private bool _captureReward = true;

    /// <summary>
    /// 单号匹配：本玩法虽然需要多人，但用「快速加入」由系统随机配队友，
    /// 只用一个账号也能完成（单人匹配跑法）。开启后不参与组队分批。
    /// </summary>
    [ObservableProperty]
    private bool _soloMatch;
}
