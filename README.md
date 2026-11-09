# QQ飞车端游 多账号任务助手

基于工作区同一技术栈实现的端游多账号自动化辅助框架：批量账号登录、按玩法配置局数、多开相互完成任务、奖励截图归档、定时上号与自动关机。

> **许可与免责**：本软件仅允许个人学习、研究及非商业性使用；禁止任何形式的商业使用（含商业产品集成、企业内部商业运营）。本软件按「原样」提供，不附带任何明示或暗示的担保。完整条款见 [LICENSE](LICENSE)。

**功能概览**：号池顶替（同时挂 N 个号，完成即换号顶上）、票完五动作（停止／小化／关机／重启／重爆）、定时下号、开机自启 + 启动即开跑、快速登录（复用 QQ 登录态点头像）、登录前选大区、领奖／开宝箱／永久判定步骤、冻结／验证码／环境异常换号、掉线自动重连、三套预设方案、单号匹配。

- 技术栈：.NET 10（WPF）+ WPF-UI 4.3 + CommunityToolkit.Mvvm 8.2 + OpenCvSharp 4.11 + Newtonsoft.Json；抓屏与键鼠直接复用工作区的 `Fischless.GameCapture` / `Fischless.WindowsInput`。
- 玩法清单：`GameModeCatalog` 收录了《QQ飞车端游现有可玩模式一览》中的全部 41 个模式，含分类、档期、人数要求与游戏内入口路径。

## 快速开始

1. 环境：Windows 10/11 x64，.NET 10 SDK。
2. 编译：

   ```powershell
   cd QQSpeedAssistant
   dotnet build QQSpeedAssistant.sln -c Debug
   ```

3. 运行 `QQSpeedAssistant/bin/Debug/net10.0-windows10.0.22621.0/QQSpeedAssistant.exe`（建议右键以管理员身份运行，否则可能无法向游戏窗口投递键鼠输入）。
   也可以在 Visual Studio 里打开 `QQSpeedAssistant.sln`，在解决方案资源管理器中右键 `QQSpeedAssistant` 项目 →「设为启动项目」，然后按 F5。
4. 打开游戏客户端（窗口化运行，便于抓屏），在「账号」页批量导入账号。
5. 在「玩法任务」页勾选要跑的玩法并填写局数。
6. 在「运行设置」页配置起始账号、密码重试次数、定时上号、自动关机、降帧。
7. 回到「主页」点「开始任务」。

## 需求实现对照

| 需求 | 实现位置 | 说明 |
| --- | --- | --- |
| 每种玩法可设定任务局数 | 玩法任务页 / `User/tasks.json` | 每个模式独立勾选与局数，另有「奖励截图」开关 |
| 多账号自动登录、相互完成任务 | `TaskOrchestrator` + `SessionManager` | 单人玩法逐号执行；组队玩法按模式所需账号数分批多开，先全部登录再逐局执行，第一个账号执行房主流程 |
| 奖励截图按账号归档 | `RewardService` / 「奖励截图」页 | 保存到 `User/rewards/{账号}/时间_玩法_第N局.png`，页内可筛选账号、大图预览 |
| 定时上号 + 全部完成自动关机 | `ScheduleService` / `ShutdownService` | 定时按 `HH:mm` 触发（允许 5 分钟误差，一天一次）；关机有倒计时，可在主页或设置页取消 |
| 从第几个账号开始 | 运行设置 → 起始账号序号 | 序号更小的账号本次不登录，状态标记为「已跳过」 |
| 密码错误多次换号 | 运行设置 → 密码错误重试次数 | 登录流程识别到密码错误画面即重试，超过次数后标记「密码错误，已跳过」并换下一个号 |
| 多号卡顿、统一降帧 | `FrameRateService` + `flows/lower_fps.json` | 每个登录账号都执行：进程优先级降为 BelowNormal，并按流程调整游戏内帧率 |
| 覆盖文档内全部玩法 | `Models/GameModeCatalog.cs` | 44 个模式，含「多开」标记与入口路径提示；其中冰雪／绝命／跳跳按客户端实际名称补入 |
| **号池顶替（多开并行）** | `TaskOrchestrator.RunPoolAsync` | 同时挂 N 个号（N=最大同时登录数），谁先跑完谁下号、号池补下一个顶上，直到跑完 |
| **票完动作五选一** | `FinishActionService` | 票完停止／小化／关机／重启／重爆；定时下号触发时同样执行 |
| **定时下号** | `ScheduleService` + `StopAndFinishAsync` | 与定时上号各自独立，到点停下并按票完动作收尾 |
| **开机自启 + 启动即开跑** | `AutoStartService` + `App.OnStartup` | 写 HKCU 的 Run 项（带 `--autostart`），启动后 15 秒自动开始白票 |
| **快速登录** | `AccountLoginService.TryFastLoginAsync` + `flows/login_fast.json` | 复用 QQ 客户端登录态点头像，不输密码，避开验证码／异地改密；失败回退 UIA 和键鼠 |
| **登录前选大区** | `flows/login_server_*.json` | 按账号区服自动挑 电信／电信二／联通／网通 的点击流程 |
| **领奖 / 开宝箱 / 永久判定** | `FlowRunner` 新步骤 + `RewardService.CapturePermanent` | 每局结算后自动领奖、开箱；命中「永久」单独存 `永久_*.png` |
| **冻结 / 验证码 / 环境异常换号** | `AccountLoginService` | 各自阈值可配，验证码直接换号，冻结／环境异常可按次数重试后换号 |
| **掉线自动重连** | `RunRoundWithReconnectAsync` | 流程里 `expectNoImage disconnected` 命中即重连并重跑该局 |
| **三套预设方案** | `PresetService` | 常用／快捷／推荐设置2，一键套用玩法勾选、局数、并发与票完动作 |
| **单号匹配** | `ModeTaskConfig.SoloMatch` | 需要多人的玩法也能用一个号跑（系统随机配队友），不参与组队分批 |

## 模板与流程校准（必须做一次）

程序内置的是**流程骨架**：它知道「点哪里、等哪个画面」，但按钮长什么样需要按本机客户端采集一次识别模板。

1. 「玩法任务」页 → 「采集模板」→ 选择游戏窗口 → 「抓取画面」→ 在预览上拖拽框选按钮 → 填写模板名 → 「保存框选」。
2. 模板保存在 `templates/`，命名参考各示例流程里的 `_模板清单`（例如 `login_account_box`、`login_button`、`hall_main`）。
3. 框选时只框按钮本体，避免包含会变的数字、倒计时、角色名。
4. 模板尺寸不用刻意对齐某个分辨率：匹配时会自动在 0.3～2.6 倍之间找最贴合的缩放比（见下节），
   采 800×600 的图拿到 1200×900 的窗口上一样能认出（实测相似度 0.99）。
5. 每个玩法都需要一个流程文件 `flows/{玩法Id}.json`，未配置时日志会提示「缺少 flows/xxx.json」并跳过该玩法。

第一次运行会把 `Assets/Flows` 下的示例流程复制到 `flows/`，**已存在的文件不会被覆盖**，可以放心修改。

### 多尺度匹配（不用再对分辨率）

窗口尺寸会随客户端分辨率、DPI 缩放、窗口模式变化（800×600 客户端在 150% 缩放下就是 1200×900），
模板却是按采集当时的画面裁的。只用一个固定缩放比时，按钮明明在眼前也只能匹配到 0.5～0.6，被判成「没认出」。

`Services/TemplateMatcher.cs` 的做法：

1. 粗扫：把画面缩到 480 宽，在 0.3～2.6 倍之间每 0.1 一档全试一遍，挑最像的两档；
2. 细化：对这两档在同一分辨率上按 0.03 步长细化，超过 0.95 立刻收敛；
3. 记忆：命中的缩放比按模板缓存，之后每帧只做一次匹配（首次约 70～300 ms，之后 60～100 ms）。

模板旁的 `*.meta.json` 里的采集宽度只作为「候选缩放比之一」，写错了也不影响识别。

验证识别效果不用开游戏：

```
QQSpeedAssistant.exe --matchcheck [截图路径]
```

省略路径时取 `User/debug` 下最新的 `fail-*.png`。报告写到 `User/debug/matchcheck.txt`，
逐条列出每个模板的最高分数、缩放比、命中位置与耗时——采完模板拿一张截图跑一遍就知道能不能用。

### 兜底坐标

个别控件位置固定但不好裁图（例如登录界面的三个大区单选），可以在流程步骤里加相对坐标兜底：

```json
{ "type": "clickImage", "template": "server_option_dianxin", "threshold": 0.85,
  "timeoutMs": 2000, "required": false,
  "fallbackXRatio": 0.606, "fallbackYRatio": 0.418 }
```

模板缺失或没认出时，就按客户区比例点下去，并在日志里记一条「按相对坐标兜底点击」。
比例是相对客户区的，换分辨率仍能换算；但界面布局大改（换赛季横幅、改登录面板）后要重测。
`flows/login_server_*.json` 已经按 1200×900 实测填好了电信／联通／电信2 的位置。

### 流程文件格式

```json
{
  "name": "个人竞速",
  "steps": [
    { "type": "clickImage", "template": "hall_multiplayer", "timeoutMs": 15000 },
    { "type": "clickImage", "template": "mode_personal_race", "threshold": 0.85 },
    { "type": "expectImage", "template": "race_finish_panel", "timeoutMs": 240000, "failReason": "timeout" },
    { "type": "saveReward" }
  ]
}
```

步骤类型：

| type | 关键字段 | 说明 |
| --- | --- | --- |
| `log` | `text` | 输出日志 |
| `wait` | `timeoutMs` | 固定等待毫秒 |
| `key` | `key` | 按键，支持 `Space`、`Esc`、`F5`、`Ctrl+A` |
| `click` | `xRatio`/`yRatio` 或 `x`/`y` | 按客户区比例或像素点击 |
| `typeText` | `text` | 输入文本，支持变量 |
| `clickImage` | `template`、`threshold`、`timeoutMs` | 找到模板并点其中心 |
| `waitImage` | 同上 | 等待模板出现 |
| `expectImage` | `template`、`timeoutMs`、`failReason` | 必须出现，否则按 `failReason` 失败 |
| `expectNoImage` | 同上 | 必须不出现（用于识别密码错误等异常界面） |
| `saveReward` | — | 立即截图保存本局奖励（应在结算画面显示时调用） |
| `claimReward` | `templates`（默认 `reward_claim`、`reward_claim_confirm`） | 按顺序「找到就点」，缺模板只记日志 |
| `openChest` | `templates`（默认 `reward_chest`、`reward_chest_open`） | 宝箱没出现视为当日已开完，不报错 |
| `checkPermanent` | `templates`、`text`（标签） | 命中任一模板即判为永久，截图存 `永久_*.png` |
| `waitImageAny` | `templates`、`timeoutMs` | 等待任一模板出现，命中后 `{any0}`、`{any1}` 置 1 |
| `randomWait` | `minMs`、`maxMs` | 随机等待，防机械节奏 |
| `repeat` | `count`、`steps` | 把内嵌子步骤重复执行 count 次，`{index}` 为当前序号 |

通用字段：`required`（默认 true，false 时该步失败只记录警告）。

可用变量：`{account}`、`{password}`、`{server}`、`{round}`、`{role}`（host/member）、`{fps}`、`{mode}`。

### 组队玩法

- `flows/team_race.json`：批次中第一个登录的账号执行（房主：建房、设模式、开始）。
- `flows/team_race.member.json`：其余账号执行（队员：接受邀请/加入房间、准备）。
- 所需账号数来自模式定义（如组队竞速 3v3 为 3 个账号），批次不足时会跳过并提示。

## 运行机制

**默认：号池顶替（`PoolMode = true`）**

同时挂 N 个号（N = 运行设置里的「最大同时登录数」）。每个号各自走完整生命周期：占窗口 → 登录 → 降帧 → 跑完勾选的玩法 → 下号 → 等「顶号延迟」→ 窗口还回号池；号池里没上过的号立刻补位，直到全部跑完。比如「最大同时登录数」填 5，就是一次挂 5 个飞车，谁先跑完谁下号、号池再补一个上来。

**关闭号池顶替时：顺序执行**

1. **单人玩法**：按账号顺序逐个登录 → 降帧 → 依次执行勾选玩法 → 每局结算截图 → 完成。
2. **顺序跑号复用客户端**：当前账号跑完且没有空闲窗口时，先执行 `flows/logout.json` 退回登录界面，供下一个账号复用；未配置退出流程则改为另开客户端（受「最大同时登录数」限制）。
3. **组队玩法**：按模式所需人数分批，确保批次内窗口都在线并登录成功后，逐局执行房主/队员流程。
4. **掉线**：流程里 `expectNoImage disconnected` 命中即按「掉线重连次数」重连并重跑该局。
5. **密码错误／冻结／验证码／环境异常**：密码错误与冻结按各自阈值重试或换号；验证码先尝试自动过码（见下节），通过后原地重试，不消耗密码重试次数。
6. **全部结束**：写入摘要（完成账号数、失败玩法次数、耗时），再执行「票完动作」——停止／小化／关机／重启／重爆。

## 登录验证码（自动过码）

登录时 QQ 会弹一个独立的「QQ安全中心」窗口：一句描述（如「选择最符合描述的图片：海湾」）配几张 AI 生成图，点对图片再点「确定」。程序按下面的链路自动处理：

1. 按标题关键字（默认 `QQ安全中心|安全中心|安全验证|验证码`）找到这个独立窗口；
2. 抓图用两条腿走路：**屏幕拷贝优先**（窗口置前，像素与屏幕一一对应，点击不用换算），抓不出图块时再试 `PrintWindow`（`PW_RENDERFULLCONTENT`，窗口被挡住也能抓），两种帧按「能切出几块图」取优——`PrintWindow` 抓这个 CEF 窗口不稳定，同一窗口有时只画得出对话框底色、照片没画进去；
3. **先等图块加载**：弹窗刚出来时只有「正在加载验证码…」，图是异步拉的。程序每 400ms 抓一次、看能不能切出图块，最多等 12 秒，这段等待不消耗答题次数；
4. **切掉无效边**：`PrintWindow` 常常画不满整块位图，右侧／下方是纯黑；黑边在掩码里等同于内容，会把所有照片连成一整块。先按「非黑像素」把画面收紧到真实内容区，再切图；
5. 连通域切出每张候选图：图块是彩色照片、对话框底色接近纯灰白，用「饱和度／亮度」掩码就能分开；再按尺寸一致性剔除题目文字、盾牌图标和「确定」按钮（实测图块 139×138，按钮 62×29）；
6. 给每张图画红框并标上编号 1..N，整张截图发给视觉模型，要求只回 `{"target":"海湾","picks":[6],"confidence":0.9}`；
7. 按编号点图 → 用颜色定位蓝色「确定」按钮并点击（找不到就按回车）→ 等 2.6 秒看窗口是否关闭；点击落点按「实际渲染区域 → 窗口」成比例换算，所以 `PrintWindow` 那种按 96 DPI 缩着画、右侧留黑的情况也能点准；
8. 窗口还在就按「答错换图」重新识别，最多「最多尝试次数」次；仍不通过则转为人工等待（不会因此把这个号判成登录失败）。
9. **登录流程跑完还要再确认一次**：QQ 的验证码是点「登录」之后**异步**弹的，实测能比「登录按钮消失」晚二十多秒；这期间流程里「等大厅」的步骤只会一路匹配失败但仍继续，最后照样返回成功，导致后面玩法的模板全是 0 相似度。所以流程结束后程序不直接写「登录成功」，而是进入收尾确认：每 800ms 抓一帧看大厅模板 `hall_main` 是否命中，期间一旦出现验证码窗口就先把码过掉；大厅命中立刻返回（正常登录只多抓一帧，不拖慢），最多等 40 秒，等不到会记一条警告再继续。

配置在「运行设置 → 登录验证码（自动过码）」：

| 项 | 说明 |
| --- | --- |
| 识别接口地址 | OpenAI 兼容地址，程序自动补 `/chat/completions`。默认智谱：`https://open.bigmodel.cn/api/paas/v4`；本地 Ollama：`http://127.0.0.1:11434/v1` |
| 接口密钥 | 平台 API Key；本地 Ollama 可以留空 |
| 模型名 | 必须支持图片输入。默认 `glm-4.6v-flash`（免费） |
| 备用模型 | 主模型被限流（429）时改用它，默认 `glm-4v-flash`，留空则不降级 |
| 最多尝试次数 | 答错换图后的重试次数，用完转人工等待 |
| 人工等待（秒） | 没配密钥或识别失败时，等你手动点完验证码的时间；窗口消失就自动继续 |
| 窗口标题关键字 | 多个用竖线分隔；换客户端或改标题后用自检导出当前窗口标题来核对 |

### 免费模型怎么选

| 平台 | 接口地址 | 免费模型 | 免费方式 |
| --- | --- | --- | --- |
| 智谱开放平台 | `https://open.bigmodel.cn/api/paas/v4` | `glm-4.6v-flash`（推荐，128K）、`glm-4v-flash`、`glm-4.1v-thinking-flash` | 长期免费，注册即用，无需充值 |
| 魔搭 ModelScope | `https://api-inference.modelscope.cn/v1` | `Qwen/Qwen3-VL-235B-A22B-Instruct` 等带「API-Inference」标识的模型 | 每天 2000 次，单模型 200～500 次/天，每日重置；需实名 |
| 硅基流动 | `https://api.siliconflow.cn/v1` | `Qwen/Qwen2.5-VL-7B-Instruct`、`GLM-4.1V-9B-Thinking` 等 9B 级模型 | 小参数模型标注「免费」，速率固定；需实名 |
| 阿里云百炼 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-vl-max-latest` | 新用户每个模型 100 万 token，90 天有效，过期转付费 |
| 本地 Ollama | `http://127.0.0.1:11434/v1` | `qwen2.5vl`、`minicpm-v` 等 | 完全免费，但要显卡；密钥留空 |

验证码识别单次大约消耗 1～2 千 token，一天顶号几十次也撞不到额度上限，用哪家都够。

实测（2026-11，智谱免费额度，同一张验证码连续调用）：

| 模型 | 表现 |
| --- | --- |
| `glm-4.6v-flash` | 识别最准（target 与图一致），但**限流最严**，连续调用会连续返回 429「访问量过大」 |
| `glm-4v-flash` | 稳定 200，单次 1.7 秒，识别同样正确；上下文只有 4K，够用（本场景约 390 token） |
| `glm-5.3-flash` | **不认 `thinking=disabled`**，直接回 400 `code 1210「该模型始终思考，不支持关闭思考」`；程序会自动改用 `thinking=low`、必要时再剥掉不兼容字段重发，但它是通用思考模型，做图片验证码不如专用视觉模型 |
| `glm-4.1v-thinking-flash` | 会把推理过程用 `<think>` 包进正文，JSON 解析失败，**不要选** |

所以默认「主模型 `glm-4.6v-flash` + 备用 `glm-4v-flash`」：主模型撞限流自动换备用的，不用等它解封。

换模型后如果接口回 400，程序不会直接判死：先去掉 `response_format` 重发，再不行去掉 `thinking` 重发，每次都会在 AI 日志里留一节，能看到剥掉的是哪个字段、接口原话是什么。

验证方式：先让验证码弹出来，再点设置页的「测试验证码识别」，或用命令行：

```powershell
QQSpeedAssistant.exe --captchacheck                # 抓当前弹窗，只切图＋识别，不点击
QQSpeedAssistant.exe --captchacheck --image a.png  # 离线跑一张截图，没有弹窗也能验证切图与提示词
QQSpeedAssistant.exe --captchacheck --solve        # 真的按识别结果点下去
QQSpeedAssistant.exe --screencheck                 # 只验证抓图：BitBlt 抓前台窗口并存图
QQSpeedAssistant.exe --screencheck WorkBuddy       # 抓标题含关键字的窗口，确认这台机器能拷屏
```

报告写进 `User/debug/captchacheck.txt`，标注图写进 `User/debug/captchacheck-annotated.png`（可直接确认编号有没有画在图块上、红框是否贴合边界）。

`--screencheck` 只看抓图这一层：报告里的「颜色种类」若只有个位数，说明屏幕拷贝在这台机器上拿到的是纯色，此时程序会自动退回 `PrintWindow`。

### AI 调用日志

每次向模型发请求都会单独写一节日志，**完整的请求体和完整的响应体都留**，包括失败、限流和重试的那几次：

- 位置：`User/log/ai/ai-yyyyMMdd.log`（按天一个文件，保留 14 天）
- 请求里带的图片：`User/log/ai/media/`，文件名写在日志的请求体里（保留 7 天）
- 一节记录包含：时间、序号、场景、模型、接口地址、脱敏后的密钥（`Bearer d4a6****eb18`）、结果（OK／FAIL／ABORT）、HTTP 状态码、耗时、请求体、响应体、程序解析出的结论
- 图片 base64 会被换成 `<image/jpeg 183.4 KB，原图见 media/xxx.jpg>` 占位符，避免正文被几十万字符淹没，原图随时能打开核对
- 设置页「登录验证码」区块有开关「记录 AI 调用日志」，关掉后不再写；旁边的「打开 AI 日志目录」直接跳过去

主日志 `run-*.log` 里只留一行索引，例如：

```
[09:54:17] [信息] [AI] AI #000012 [验证码识别] glm-4.6v-flash → OK 200（3421 ms），详见 User/log/ai/ai-20261109.log
```

排查「模型到底看到了什么、回了什么」时，先看这一行定位到第几号请求，再翻对应文件即可。新增其他 AI 调用时统一走 `AiRequestLogger.Begin(...)`，日志格式自动一致。

## 数据目录

| 路径 | 内容 |
| --- | --- |
| `User/accounts.json` | 账号（含密码，明文） |
| `User/tasks.json` | 玩法任务配置 |
| `User/settings.json` | 运行设置 |
| `User/rewards/{账号}/` | 奖励截图 |
| `User/log/run-yyyyMMdd.log` | 按天日志；启动异常写入 `startup-error.txt` |
| `User/log/ai/ai-yyyyMMdd.log` | AI 调用日志：每次模型请求的完整请求体／响应体／状态码／耗时 |
| `User/log/ai/media/` | AI 请求里带出的图片（验证码截图），日志里按文件名引用 |
| `flows/` | 流程定义，可自行增删 |
| `templates/` | 识别模板 PNG |

## 项目结构

```
QQSpeedAssistant/
├─ QQSpeedAssistant.sln
└─ QQSpeedAssistant/
   ├─ Models/       玩法定义与目录、账号、任务、设置、流程模型
   ├─ Services/     配置、日志、窗口定位、抓屏会话、模板匹配、流程执行、
   │                登录（快速登录/UIA/键鼠）、退出、玩法执行、奖励截图、
│                验证码自动过码（窗口定位/切图/视觉模型/点击）、
   │                降帧、定时上号下号、关机重启、票完动作、开机自启、
   │                预设方案、任务编排（号池/顺序）
   ├─ ViewModels/   主页、账号、玩法任务、运行设置、奖励、日志、模板采集
   ├─ Views/        MainWindow、6 个页面、模板采集窗口
   ├─ Converters/   日志级别配色
   └─ Assets/Flows/ 示例流程（首次运行安装到 flows/，含 14 个白票玩法）
```

## 已知限制与注意

- 账号密码以明文保存在本地 `User/accounts.json`，请勿把整个目录分享或提交到版本库。
- 程序不内置任何游戏截图素材，**必须先采集模板、按自己客户端校准流程**，否则玩法会以「缺少流程/模板」跳过。
- 客户端多开能力、是否允许窗口化、分辨率与帧率选项位置由游戏版本决定；入口路径来自文档，版本更新后可能变化。
- 使用自动化工具可能违反游戏用户协议，存在封号风险，请自行评估并承担后果。
- 调试用：
  - `QQSpeedAssistant.exe --selftest`：只加载全部 XAML 后退出（退出码 0 表示界面资源无误），不显示窗口。
  - `QQSpeedAssistant.exe --flowcheck`：解析 `flows/` 下所有流程，把「未知步骤类型」「缺哪些模板」「哪些步骤配了兜底坐标」写进 `User/debug/flowcheck.txt`。
  - `QQSpeedAssistant.exe --matchcheck [截图]`：拿一张截图把 `templates/` 里全部模板跑一遍多尺度匹配，输出分数／缩放比／耗时到 `User/debug/matchcheck.txt`。
  - `QQSpeedAssistant.exe --inkcheck`：用合成画面验证「登录时到底有没有把账号密码输进去」的判定规则（空框／只有光标／只有边框／真打进圆点四种情形），报告写进 `User/debug/inkcheck.txt`，不用开游戏。
  - `QQSpeedAssistant.exe --bindcheck`：真正打开主窗口并逐页切换，把所有绑定错误写进 `User/debug/bindcheck.txt`（界面改动后跑一次）。
  - `QQSpeedAssistant.exe --captchacheck`：把登录验证码弹窗（或 `--image` 指定的截图）切图、标注、调模型，过程写进 `User/debug/captchacheck.txt`；加 `--solve` 会真的点击作答。

## 编译验证

```powershell
cd QQSpeedAssistant
dotnet msbuild QQSpeedAssistant.sln -restore -p:Configuration=Debug -nologo -tl:off -clp:ErrorsOnly
```

## 常见问题

| 现象 | 处理 |
| --- | --- |
| VS 提示「无法启动调试。无法启动启动项目」 | 在解决方案资源管理器中右键 `QQSpeedAssistant` 项目 →「设为启动项目」，或直接打开 `QQSpeedAssistant.sln`（主程序已排在解决方案第一位） |
| 双击 exe 没反应或界面一闪而过 | 查看 `User/log/startup-error.txt` 与 `User/log/run-*.log`；多为缺少 .NET 10 桌面运行时 |
| 能识别画面但点不到按钮 | 以管理员身份运行本程序与客户端，并确认游戏为窗口化 / 无边框模式 |
| 模板永远是「未找到」 | 先跑 `--matchcheck <截图>` 看分数：分数 ≥0.85 说明模板没问题，是流程／步骤没走到；分数 0.5～0.8 说明框到了会变的内容（数字、倒计时、角色名）或框太大，重新框按钮本体；分数 <0.4 说明画面里确实没有这个东西 |
| 没输密码就直接点了「登录」，游戏弹「请输入密码」 | 现在不会了：提交前要求账号框和密码框各自都有「像文字」的变化（≥120 像素、变化区够宽、且大部分落在框的中间带）。日志里会写清是哪一条没过。想复核判定规则跑 `--inkcheck` |

## 许可与免责声明

**本软件仅允许个人学习、研究及非商业性使用。**

**禁止任何形式的商业使用**，包括但不限于：

- 商业产品集成（把本软件或其修改版本集成、嵌入到任何商业产品或商业服务中）；
- 企业内部商业运营（在企业、机构内部的商业运营、生产环境或营利性业务中使用）；
- 作为商品销售、出租、许可、订阅或按次收费；
- 以本软件为基础提供有偿代练、代跑、挂机托管等商业服务；
- 以本软件或其衍生版本进行商业宣传、引流或流量变现。

**本软件按「原样」提供，不附带任何明示或暗示的担保**，包括但不限于对适销性、特定用途适用性及不侵权的担保。在任何情况下，作者或版权持有人均不对因本软件或本软件的使用或其他交易而产生的任何索赔、损害或其他责任承担责任。

完整条款见仓库根目录的 [LICENSE](LICENSE)。使用本软件即表示你已阅读、理解并同意该协议；如不同意，请立即停止使用并删除本软件的全部副本。
