# -*- coding: utf-8 -*-
"""生成 QQSpeedAssistant 的流程示例文件（14 个白票玩法 + 登录 / 选大区 / 领奖 / 开箱 / 永久判定）。"""
import json, os

out = os.path.join(os.path.dirname(__file__), "..", "QQSpeedAssistant", "Assets", "Flows")
out = os.path.abspath(out)
os.makedirs(out, exist_ok=True)

def dump(name, obj):
    p = os.path.join(out, name + ".json")
    with open(p, "w", encoding="utf-8-sig") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print("wrote", p)

# ── 白票玩法列表：id / 显示名 / 模式模板名 / 预计单局上限秒 ──
MODES = [
    ("ice_snow",       "冰雪",      "mode_ice_snow",       240),
    ("speed_duel",     "极速对决",  "mode_speed_duel",     240),
    ("doom",           "绝命",      "mode_doom",           240),
    ("wild_chase",     "狂野追逐",  "mode_wild_chase",     300),
    ("city_wanted",    "全城通缉令","mode_city_wanted",    300),
    ("super_race",     "超能竞速赛","mode_super_race",     300),
    ("duo_assault",    "双人冲锋战","mode_duo_assault",    300),
    ("mutual_relay",   "互助接力赛","mode_mutual_relay",   300),
    ("transform_party","变身派对",  "mode_transform_party",300),
    ("jump_jump",      "跳跳",      "mode_jump_jump",      240),
    ("straight_poker", "顺子大作战","mode_straight_poker", 300),
    ("romantic_meet",  "浪漫邂逅",  "mode_romantic_meet",  360),
    ("duo_summit",     "双人冲顶赛","mode_duo_summit",     360),
    ("kubi",           "抢酷比",    "mode_kubi",           300),
]

for mode_id, mode_name, mode_tpl, timeout_sec in MODES:
    flow = {
        "name": f"{mode_name}（白票流程）",
        "description": (
            "白票跑法：大厅 → 多人游戏 → 快速加入 → 选模式 → 匹配 → 结算截图 → 永久判定 → "
            "领奖 → 开宝箱 → 回大厅。所有模板都要按自己客户端采集；需要多人但只能开一个号时，"
            "在「玩法任务」勾上「单号匹配」。"
        ),
        "_模板清单": [
            "hall_main", "hall_multiplayer", "hall_quick_join", mode_tpl,
            "match_start_button", "race_finish_panel", "race_finish_confirm",
            "permanent_badge", "reward_claim", "reward_claim_confirm",
            "reward_chest", "reward_chest_open", "disconnected"
        ],
        "steps": [
            {"type": "log", "text": f"第 {{round}} 局：{mode_name}"},
            # 开跑前先确认没掉线：命中 disconnected 模板会抛 disconnected，编排器据此重连
            {"type": "expectNoImage", "template": "disconnected", "threshold": 0.8,
             "timeoutMs": 1200, "failReason": "disconnected", "required": False},
            {"type": "waitImageAny", "templates": ["hall_main", "hall_multiplayer"],
             "threshold": 0.85, "timeoutMs": 15000, "required": False},
            {"type": "clickImage", "template": "hall_multiplayer", "threshold": 0.85,
             "timeoutMs": 15000, "required": False},
            {"type": "clickImage", "template": "hall_quick_join", "threshold": 0.85,
             "timeoutMs": 10000, "required": False},
            {"type": "clickImage", "template": mode_tpl, "threshold": 0.85, "timeoutMs": 15000},
            {"type": "clickImage", "template": "match_start_button", "threshold": 0.85, "timeoutMs": 15000},
            {"type": "expectImage", "template": "race_finish_panel", "threshold": 0.8,
             "timeoutMs": timeout_sec * 1000, "failReason": "timeout"},
            {"type": "saveReward"},
            {"type": "checkPermanent", "templates": ["permanent_badge", "permanent_car"],
             "threshold": 0.85, "timeoutMs": 1500, "required": False, "text": mode_name},
            {"type": "clickImage", "template": "race_finish_confirm", "threshold": 0.85,
             "timeoutMs": 15000, "required": False},
            {"type": "claimReward", "templates": ["reward_claim", "reward_claim_confirm"],
             "timeoutMs": 4000, "required": False},
            {"type": "openChest", "templates": ["reward_chest", "reward_chest_open"],
             "timeoutMs": 3000, "required": False},
            {"type": "randomWait", "minMs": 400, "maxMs": 1200},
        ],
    }
    dump(mode_id, flow)

# ── 快速登录 ──
dump("login_fast", {
    "name": "快速登录（QQ 登录态点头像）",
    "description": (
        "快速登录：先在本机 QQ 客户端用该账号登录过任意小游戏（留下登录态缓存），"
        "飞车登录界面就会出现该账号头像，直接点头像进游戏，不用输密码，也就不会触发验证码／异地改密／环境异常。"
        "找不到头像模板时会抛异常，登录服务会自动回退到 UIA 填表和键鼠流程。"
    ),
    "_模板清单": ["login_button", "fast_login_avatar", "hall_main"],
    "steps": [
        {"type": "log", "text": "尝试快速登录 {account}"},
        {"type": "expectImage", "template": "login_button", "threshold": 0.85,
         "timeoutMs": 25000, "failReason": "assert_failed"},
        {"type": "clickImage", "template": "fast_login_avatar", "threshold": 0.88,
         "timeoutMs": 8000, "failReason": "assert_failed"},
        {"type": "waitImageGone", "template": "login_button", "threshold": 0.85,
         "timeoutMs": 60000, "failReason": "assert_failed"},
        {"type": "waitImage", "template": "hall_main", "threshold": 0.8, "timeoutMs": 30000, "required": False},
    ],
})

# ── 选大区（每个大区一份，按账号区服名自动挑） ──
for key, label in [("dianxin", "电信"), ("dianxin2", "电信二"), ("liantong", "联通"), ("wangtong", "网通")]:
    dump(f"login_server_{key}", {
        "name": f"选择大区：{label}",
        "description": f"在登录界面点开大区下拉，选中「{label}」。模板 server_dropdown_{key} / server_option_{key} 需按自己客户端采集。",
        "_模板清单": [f"server_dropdown", f"server_option_{key}"],
        "steps": [
            {"type": "log", "text": f"选择大区：{label}"},
            {"type": "clickImage", "template": "server_dropdown", "threshold": 0.85,
             "timeoutMs": 6000, "required": False},
            {"type": "clickImage", "template": f"server_option_{key}", "threshold": 0.85,
             "timeoutMs": 6000, "required": False},
            {"type": "randomWait", "minMs": 200, "maxMs": 600},
        ],
    })

# ── 通用领奖 / 开箱 / 永久判定（可单独复制进任意玩法流程） ──
dump("claim_reward", {
    "name": "领取奖励（通用）",
    "description": "结算后按模板点击「领取」和「确定」。可直接把这几步复制进任何玩法流程。",
    "_模板清单": ["reward_claim", "reward_claim_confirm"],
    "steps": [
        {"type": "claimReward", "templates": ["reward_claim", "reward_claim_confirm"],
         "timeoutMs": 5000, "required": False},
    ],
})

dump("open_chest", {
    "name": "开宝箱（通用）",
    "description": "宝箱出现才点开，没出现视为当日已开完，不报错。",
    "_模板清单": ["reward_chest", "reward_chest_open"],
    "steps": [
        {"type": "openChest", "templates": ["reward_chest", "reward_chest_open"],
         "timeoutMs": 4000, "required": False},
    ],
})

dump("check_permanent", {
    "name": "永久奖励判定（通用）",
    "description": "结算面板上出现「永久」角标就单独截图，存到 User/rewards/{账号}/永久_*.png。",
    "_模板清单": ["permanent_badge", "permanent_car"],
    "steps": [
        {"type": "checkPermanent", "templates": ["permanent_badge", "permanent_car"],
         "threshold": 0.85, "timeoutMs": 3000, "required": False, "text": "永久"},
    ],
})

print("done")
