using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>预设里的单个玩法项。</summary>
public sealed record PresetMode(string ModeId, int Rounds, bool SoloMatch);

/// <summary>一套预设方案。</summary>
public sealed record PresetDefinition(
    string Name,
    string Description,
    int Concurrency,
    FinishAction FinishAction,
    int RandomDelayMinMs,
    int RandomDelayMaxMs,
    IReadOnlyList<PresetMode> Modes);

/// <summary>
/// 预设方案：把「勾哪些玩法、各跑几局、并发几个号、票完做什么」一次性套上去。
/// 三套方案：常用 / 快捷 / 推荐设置2。
/// </summary>
public sealed class PresetService
{
    private readonly ConfigService _config;
    private readonly LogService _log;

    /// <summary>构造预设服务。</summary>
    public PresetService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// 默认的 14 个玩法与局数（冰雪 1 局、超能 5 局、酷比 30 局…）。
    /// 需要多人的玩法一律走「单号匹配」，用一个号也能跑完。
    /// </summary>
    private static readonly IReadOnlyList<PresetMode> DefaultModeList =
    [
        new("ice_snow", 1, false),
        new("speed_duel", 2, false),
        new("doom", 3, false),
        new("wild_chase", 3, false),
        new("city_wanted", 3, false),
        new("super_race", 5, true),
        new("duo_assault", 3, true),
        new("mutual_relay", 3, true),
        new("transform_party", 5, false),
        new("jump_jump", 5, false),
        new("straight_poker", 3, false),
        new("romantic_meet", 3, true),
        new("duo_summit", 3, true),
        new("kubi", 30, false)
    ];

    /// <summary>全部预设方案。</summary>
    public IReadOnlyList<PresetDefinition> Presets { get; } =
    [
        new("常用", "默认配置：14 个玩法全勾（邂逅／冲顶除外），局数按默认值，3 个号并发",
            3, FinishAction.None, 0, 200,
            DefaultModeList.Where(m => m.ModeId is not ("romantic_meet" or "duo_summit")).ToList()),

        new("快捷", "只跑最快的几个玩法，每个 1 局，2 个号并发，适合只想清日常",
            2, FinishAction.None, 150, 500,
            [
                new("ice_snow", 1, false),
                new("speed_duel", 1, false),
                new("straight_poker", 1, false),
                new("transform_party", 1, false)
            ]),

        new("推荐设置2", "推荐挂机配置：14 个玩法全跑、5 个号并发、带随机延时、票完关机",
            5, FinishAction.Shutdown, 200, 800,
            DefaultModeList)
    ];

    /// <summary>预设名称列表，供界面下拉使用。</summary>
    public IReadOnlyList<string> Names => Presets.Select(p => p.Name).ToList();

    /// <summary>按名称套用预设，返回给界面的提示文本。</summary>
    public string Apply(string name)
    {
        var preset = Presets.FirstOrDefault(p => p.Name == name);
        if (preset == null)
        {
            return $"没有名为「{name}」的预设";
        }

        var settings = _config.Settings;
        settings.PoolMode = true;
        settings.MaxConcurrentInstances = Math.Clamp(preset.Concurrency, 1, 16);
        settings.FinishAction = preset.FinishAction;
        settings.AutoShutdown = preset.FinishAction == FinishAction.Shutdown;
        settings.RandomDelayMinMs = preset.RandomDelayMinMs;
        settings.RandomDelayMaxMs = preset.RandomDelayMaxMs;
        settings.FastLoginEnabled = true;

        var wanted = preset.Modes.ToDictionary(m => m.ModeId, m => m, StringComparer.Ordinal);
        var enabled = 0;
        foreach (var task in _config.Tasks)
        {
            if (wanted.TryGetValue(task.ModeId, out var mode))
            {
                task.Enabled = true;
                task.Rounds = Math.Max(1, mode.Rounds);
                task.SoloMatch = mode.SoloMatch;
                task.CaptureReward = true;
                enabled++;
            }
            else
            {
                task.Enabled = false;
                task.SoloMatch = false;
            }
        }

        _config.SaveTasks();
        _config.SaveSettings();

        var message = $"已套用「{preset.Name}」：{enabled} 个玩法，{preset.Concurrency} 个号并发，票完{FinishActionText(preset.FinishAction)}";
        _log.Info(message, "预设");
        return message;
    }

    /// <summary>票完动作的中文短描述。</summary>
    private static string FinishActionText(FinishAction action) => action switch
    {
        FinishAction.Shutdown => "关机",
        FinishAction.Restart => "重启",
        FinishAction.Rerun => "重爆",
        FinishAction.Minimize => "小化",
        _ => "停止"
    };
}
