namespace QQSpeedAssistant.Models;

/// <summary>
/// 端游可玩模式目录，覆盖《QQ飞车端游现有可玩模式一览》中列出的全部模式。
/// 新增模式只需在 <see cref="All"/> 中追加一条定义，界面与任务计划会自动生效。
/// </summary>
public static class GameModeCatalog
{
    /// <summary>全部模式定义，顺序与文档一致。</summary>
    public static IReadOnlyList<GameModeDefinition> All { get; } =
    [
        new()
        {
            Id = "personal_race", Name = "个人竞速", Category = ModeCategory.Racing,
            Availability = ModeAvailability.Always, RequiredAccounts = 1, DefaultRounds = 10,
            EntryPath = "多人游戏 → 快速加入 → 竞速模式 → 个人竞速赛；或直接点「立即竞速」",
            Note = "6 人，首个冲线"
        },
        new()
        {
            Id = "team_race", Name = "组队竞速", Category = ModeCategory.Racing, Availability = ModeAvailability.Always,
            RequiredAccounts = 3, DefaultRounds = 5,
            EntryPath = "多人游戏 → 快速加入 → 竞速模式 → 组队竞速赛",
            Note = "3v3，需要 3 个账号同时在线"
        },
        new()
        {
            Id = "relay", Name = "接力", Category = ModeCategory.Racing, Availability = ModeAvailability.Always,
            RequiredAccounts = 3, DefaultRounds = 3,
            EntryPath = "多人游戏 → 快速加入 → 接力",
            Note = "3v3，持棒超过 10 秒不传会炸棒"
        },
        new()
        {
            Id = "doomsday_escape", Name = "末日逃亡", Category = ModeCategory.Racing,
            Availability = ModeAvailability.Always, DefaultRounds = 5,
            EntryPath = "多人游戏 → 快速加入 → 末日逃亡",
            Note = "6 人，HP 与吸血鬼机制"
        },
        new()
        {
            Id = "infinite_nitro", Name = "无限氮气", Category = ModeCategory.Racing,
            Availability = ModeAvailability.Weekend, DefaultRounds = 5,
            EntryPath = "快速进入 → 无限氮气；或经典飞车频道「无限氮气」页签",
            Note = "周末限时开放"
        },
        new()
        {
            Id = "personal_item", Name = "个人道具", Category = ModeCategory.Item,
            Availability = ModeAvailability.Always, DefaultRounds = 10,
            EntryPath = "多人游戏 → 快速加入 → 道具模式 → 个人道具赛；或点「立即道具」",
            Note = "6 人，Ctrl 释放道具，Alt 切换顺序"
        },
        new()
        {
            Id = "team_item", Name = "组队道具", Category = ModeCategory.Item, Availability = ModeAvailability.Always,
            RequiredAccounts = 3, DefaultRounds = 5,
            EntryPath = "多人游戏 → 快速加入 → 道具模式 → 组队道具赛",
            Note = "3v3，注意范围道具误伤队友"
        },
        new()
        {
            Id = "kubi", Name = "抢酷比", Category = ModeCategory.Casual, Availability = ModeAvailability.Always,
            DefaultRounds = 5,
            EntryPath = "多人游戏 → 快速进入 → 个人抢酷比／组队抢酷比",
            Note = "自由跑动收集酷比，被击中掉落 1 个"
        },
        new()
        {
            Id = "dance", Name = "舞蹈", Category = ModeCategory.Casual, Availability = ModeAvailability.Always,
            DefaultRounds = 5,
            EntryPath = "快速加入 → 舞蹈模式 → 飞车舞蹈／传统四键",
            Note = "1 至 4 人，节奏判定，得分与着装度相关"
        },
        new()
        {
            Id = "roller", Name = "炫动轮滑", Category = ModeCategory.Casual, Availability = ModeAvailability.Always,
            DefaultRounds = 5,
            EntryPath = "游戏大厅 → 炫动轮滑入口 → 轮滑频道",
            Note = "6 人，多一个跳跃键"
        },
        new()
        {
            Id = "duo_roller", Name = "双人轮滑", Category = ModeCategory.Casual, Availability = ModeAvailability.Always,
            RequiredAccounts = 2, DefaultRounds = 3,
            EntryPath = "轮滑频道 → 创建房间 → 模式选「双人轮滑」",
            Note = "两人一队，Tab 牵手触发 QTE"
        },
        new()
        {
            Id = "couple_race", Name = "情侣赛车", Category = ModeCategory.Team, Availability = ModeAvailability.Always,
            RequiredAccounts = 2, DefaultRounds = 3,
            EntryPath = "主界面左上角彩色圆形图标 → 竞速模式 → 右下角「情侣模式」",
            Note = "3 队 × 2 人，一人竞速一人道具，需异性角色"
        },
        new()
        {
            Id = "ranked", Name = "荣耀排位赛", Category = ModeCategory.Ranked, Availability = ModeAvailability.Always,
            DefaultRounds = 5,
            EntryPath = "大厅上方功能菜单排位入口，或快速栏「立即排位」",
            Note = "按赛季结算，当前 S57「沧海竞渡」"
        },
        new()
        {
            Id = "peak", Name = "巅峰赛", Category = ModeCategory.Ranked, Availability = ModeAvailability.Event,
            DefaultRounds = 3,
            EntryPath = "荣耀排位界面内 → 巅峰赛（仅赛季开启期间显示）",
            Note = "需技术大师段位"
        },
        new()
        {
            Id = "team_league", Name = "车队赛", Category = ModeCategory.Team,
            Availability = ModeAvailability.Weekly, DefaultRounds = 3,
            EntryPath = "我的车队 → 锦标赛页签；或赛事中心车队赛入口",
            Note = "每周定点开放，完成局数换宝箱"
        },
        new()
        {
            Id = "car_god", Name = "车神争霸赛", Category = ModeCategory.Ranked,
            Availability = ModeAvailability.Daily, DefaultRounds = 3,
            EntryPath = "赛事系统 → 车神争霸赛；或多人游戏 → 快速进入 → 车神争霸",
            Note = "每天 9:00 至 22:00，需加入车队且 40 级以上"
        },
        new()
        {
            Id = "car_swap_rally", Name = "换车拉力赛", Category = ModeCategory.Racing,
            Availability = ModeAvailability.Monthly, DefaultRounds = 1,
            EntryPath = "小橘子桌面；开放期间左侧快速入口也有",
            Note = "每月赛季，周五至周日 10:00 至 22:00，单局 20 点券"
        },
        new()
        {
            Id = "big_challenge", Name = "大闯关", Category = ModeCategory.Event,
            Availability = ModeAvailability.Event, DefaultRounds = 3,
            EntryPath = "大厅顶部当期活动按钮；小橘子桌面；多人游戏快速进入",
            Note = "每期一换机制，当前「钩索大闯关」"
        },
        new()
        {
            Id = "win_streak", Name = "连胜之星", Category = ModeCategory.Racing,
            Availability = ModeAvailability.Event, DefaultRounds = 3,
            EntryPath = "版本活动入口（大厅活动区）",
            Note = "6 人竞速淘汰赛，活动结束后入口关闭"
        },
        new()
        {
            Id = "global_challenge", Name = "全球挑战", Category = ModeCategory.Pve,
            Availability = ModeAvailability.Event, DefaultRounds = 3,
            EntryPath = "多人游戏 → 快速进入 → 全球挑战；或开始菜单 → 单人游戏 → 全球挑战",
            Note = "PVE 关卡，分中国／日本／欧洲三区"
        },
        new()
        {
            Id = "track_workshop", Name = "赛道工坊", Category = ModeCategory.Casual,
            Availability = ModeAvailability.Always, DefaultRounds = 3,
            EntryPath = "大厅或多人游戏的「共创空间」→ 赛道工坊",
            Note = "跑别人的图也计入通行证跑图进度"
        },
        new()
        {
            Id = "border_war", Name = "边境战争", Category = ModeCategory.Border,
            Availability = ModeAvailability.Always, DefaultRounds = 5,
            EntryPath = "左下角菜单「多人游戏」→ 边境战争；或小橘子桌面 → 多人赛事 → 边境战争",
            Note = "入场消耗点券：初级 10／高级 100／专业 300"
        },
        new()
        {
            Id = "training", Name = "训练场", Category = ModeCategory.Pve, Availability = ModeAvailability.Always,
            DefaultRounds = 1,
            EntryPath = "右下角「个人生涯」→ 左侧「自由训练模式」",
            Note = "可设置旗点反复练弯"
        },
        new()
        {
            Id = "leisure", Name = "休闲区", Category = ModeCategory.Casual, Availability = ModeAvailability.Always,
            DefaultRounds = 1,
            EntryPath = "主界面右下角休闲区入口（角色 9 级开放）",
            Note = "社交场景，可用于每日签到类任务"
        },
        new()
        {
            Id = "quest_story", Name = "任务与剧情", Category = ModeCategory.Pve, Availability = ModeAvailability.Always,
            DefaultRounds = 1,
            EntryPath = "任务面板领主线与日常；剧情在活动中心对应章节",
            Note = "PVE 线，产出经验、点券与养成资源"
        },
        new()
        {
            Id = "wild_chase", Name = "狂野追逐", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, DefaultRounds = 3,
            EntryPath = "大厅「狂野追逐」图标；左下角菜单 → 多人游戏 → 狂野追逐",
            Note = "3 警 3 狂，撞掉耐久"
        },
        new()
        {
            Id = "city_wanted", Name = "全城通缉令", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, DefaultRounds = 3,
            EntryPath = "大厅 → 快速进入；或小橘子桌面",
            Note = "3 先锋＋3 狂徒，比追捕进度"
        },
        new()
        {
            Id = "duo_assault", Name = "双人冲锋战", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, RequiredAccounts = 2, DefaultRounds = 3,
            EntryPath = "大厅「双人冲锋战」入口；多人游戏 → 快速进入",
            Note = "12 人／6 车／3 队，双人同车，一人开车一人射击"
        },
        new()
        {
            Id = "super_race", Name = "超能竞速赛", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, RequiredAccounts = 3, DefaultRounds = 3,
            EntryPath = "大厅「超能竞速赛」入口；多人游戏 → 快速进入",
            Note = "9 人／3 队，技能点拾取后 Alt 或 Space 释放"
        },
        new()
        {
            Id = "mutual_relay", Name = "互助接力赛", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, RequiredAccounts = 2, DefaultRounds = 3,
            EntryPath = "大厅活动入口；多人游戏 → 快速进入 → 互助接力赛",
            Note = "2 人组队，场外打 Call 给队友投放道具"
        },
        new()
        {
            Id = "transform_party", Name = "变身派对", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, DefaultRounds = 3,
            EntryPath = "小橘子桌面「变身派对」",
            Note = "躲藏对抗，魔术师变身场景物件"
        },
        new()
        {
            Id = "green_pitch", Name = "绿茵车王", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, RequiredAccounts = 2, DefaultRounds = 3,
            EntryPath = "大厅顶部「绿茵车王」；小橘子桌面；多人游戏 → 快速进入",
            Note = "2v2 赛车足球，先进 4 球获胜"
        },
        new()
        {
            Id = "snake_battle", Name = "蛇蛇大作战", Category = ModeCategory.Event,
            Availability = ModeAvailability.Event, DefaultRounds = 2,
            EntryPath = "大厅顶部「蛇蛇大作战」；小橘子桌面；多人游戏 → 快速进入",
            Note = "赛车吃鸡，数字大吃数字小"
        },
        new()
        {
            Id = "allstar_brawl", Name = "全明星大乱斗", Category = ModeCategory.Event,
            Availability = ModeAvailability.Always, DefaultRounds = 3,
            EntryPath = "大厅上方「全明星大乱斗」；或多人游戏内同名入口",
            Note = "4 人对局，靠明星角色技能"
        },
        new()
        {
            Id = "romantic_meet", Name = "浪漫邂逅", Category = ModeCategory.Team,
            Availability = ModeAvailability.Always, RequiredAccounts = 2, DefaultRounds = 3,
            EntryPath = "大厅界面入口；快速进入列表；小橘子桌面",
            Note = "3 队 × 2 人，需异性搭配，默契值触发大招"
        },
        new()
        {
            Id = "duo_summit", Name = "双人冲顶赛", Category = ModeCategory.Team,
            Availability = ModeAvailability.Daily, RequiredAccounts = 2, DefaultRounds = 3,
            EntryPath = "大厅活动按钮；多人游戏 → 快速进入 → 双人冲顶赛",
            Note = "每日 10:00 至 24:00，8 组 × 2 人三轮淘汰"
        },
        new()
        {
            Id = "summer_drink", Name = "夏日果饮竞速", Category = ModeCategory.Event,
            Availability = ModeAvailability.Daily, DefaultRounds = 2,
            EntryPath = "大厅活动入口；进入后点「立即匹配」或「组队匹配」",
            Note = "每日开放，抽车淘汰赛"
        },
        new()
        {
            Id = "straight_poker", Name = "顺子大作战", Category = ModeCategory.Casual,
            Availability = ModeAvailability.Seasonal, DefaultRounds = 2,
            EntryPath = "娱乐嘉年华界面（本赛季轮换池）",
            Note = "麻将「顺子」题材休闲对局"
        },
        new()
        {
            Id = "ask_sky", Name = "问天之约", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, DefaultRounds = 2,
            EntryPath = "娱乐嘉年华界面（本赛季轮换池）",
            Note = "航天联动玩法，赛道内御箭飞行"
        },
        new()
        {
            Id = "paint_brawl", Name = "涂抹大乱斗", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, DefaultRounds = 2,
            EntryPath = "娱乐嘉年华界面（本赛季轮换池）",
            Note = "攒能量放大招，任务如「释放 40 次大招」"
        },
        new()
        {
            Id = "speed_duel", Name = "极速对决", Category = ModeCategory.Racing,
            Availability = ModeAvailability.Seasonal, DefaultRounds = 2,
            EntryPath = "娱乐嘉年华界面（本赛季轮换池）",
            Note = "偏竞速的对抗玩法，专属时装可兑换"
        },
        new()
        {
            Id = "ice_snow", Name = "冰雪", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, DefaultRounds = 1,
            EntryPath = "大厅活动入口；多人游戏 → 快速进入",
            Note = "默认第 1 个跑、只跑 1 局；入口名称请按自己客户端核对"
        },
        new()
        {
            Id = "doom", Name = "绝命", Category = ModeCategory.Event,
            Availability = ModeAvailability.Seasonal, DefaultRounds = 3,
            EntryPath = "大厅活动入口；多人游戏 → 快速进入",
            Note = "客户端实际名称可能是「绝命逃亡」之类，请核对"
        },
        new()
        {
            Id = "jump_jump", Name = "跳跳", Category = ModeCategory.Casual,
            Availability = ModeAvailability.Seasonal, DefaultRounds = 5,
            EntryPath = "大厅活动入口；多人游戏 → 快速进入",
            Note = "默认跑 5 局；客户端实际名称请核对（跳跳乐／跳跳大作战）"
        }
    ];

    /// <summary>按 Id 查找玩法定义。</summary>
    public static GameModeDefinition? Find(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(m => m.Id == id);

    /// <summary>按分类过滤，category 为 null 时返回全部。</summary>
    public static IEnumerable<GameModeDefinition> Filter(ModeCategory? category)
        => category is null ? All : All.Where(m => m.Category == category);

    /// <summary>生成默认任务配置（全部玩法关闭，局数取默认值）。</summary>
    public static List<ModeTaskConfig> CreateDefaultTasks()
        => All.Select(m => new ModeTaskConfig
        {
            ModeId = m.Id,
            Enabled = false,
            Rounds = m.DefaultRounds,
            CaptureReward = true
        }).ToList();
}
