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
5. **密码错误／冻结／验证码／环境异常**：各自按阈值重试或换号。
6. **全部结束**：写入摘要（完成账号数、失败玩法次数、耗时），再执行「票完动作」——停止／小化／关机／重启／重爆。

## 数据目录

| 路径 | 内容 |
| --- | --- |
| `User/accounts.json` | 账号（含密码，明文） |
| `User/tasks.json` | 玩法任务配置 |
| `User/settings.json` | 运行设置 |
| `User/rewards/{账号}/` | 奖励截图 |
| `User/log/run-yyyyMMdd.log` | 按天日志；启动异常写入 `startup-error.txt` |
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
