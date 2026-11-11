using CommunityToolkit.Mvvm.ComponentModel;

namespace QQSpeedAssistant.Models;

/// <summary>全局运行设置，持久化到 User/settings.json。</summary>
public sealed partial class AppSettings : ObservableObject
{
    /// <summary>从第几个账号开始执行（1 表示第一个），序号小于该值的账号会被跳过。</summary>
    [ObservableProperty]
    private int _startAccountIndex = 1;

    /// <summary>密码错误时的最多重试次数，超过后切换到下一个账号。</summary>
    [ObservableProperty]
    private int _maxPasswordRetries = 3;

    /// <summary>是否启用定时上号。</summary>
    [ObservableProperty]
    private bool _scheduledStartEnabled;

    /// <summary>定时启动时间，格式 HH:mm。</summary>
    [ObservableProperty]
    private string _scheduledStartTime = "08:00";

    /// <summary>全部账号跑完后是否自动关机。</summary>
    [ObservableProperty]
    private bool _autoShutdown = true;

    /// <summary>关机倒计时秒数，留出取消时间。</summary>
    [ObservableProperty]
    private int _shutdownDelaySeconds = 60;

    /// <summary>登录后是否执行降帧（降低游戏帧率）。</summary>
    [ObservableProperty]
    private bool _lowFrameRateEnabled = true;

    /// <summary>目标帧率，仅在游戏内降帧流程中使用。</summary>
    [ObservableProperty]
    private int _lowFrameRateTarget = 30;

    /// <summary>同时降低游戏进程优先级，多开时进一步降低 CPU 抢占。</summary>
    [ObservableProperty]
    private bool _lowerProcessPriority = true;

    /// <summary>游戏主程序路径，多开时用于自动启动客户端。</summary>
    [ObservableProperty]
    private string _gameExePath = "";

    /// <summary>窗口不足时是否自动启动客户端。</summary>
    [ObservableProperty]
    private bool _autoLaunchGame = true;

    /// <summary>启动客户端后等待窗口出现的秒数。</summary>
    [ObservableProperty]
    private int _launchWaitSeconds = 150;

    /// <summary>最多同时登录的客户端数量，多开玩法按需启动。</summary>
    [ObservableProperty]
    private int _maxConcurrentInstances = 3;

    /// <summary>顺序跑号时是否复用同一个客户端（先执行 flows/logout.json 退出当前账号）。</summary>
    [ObservableProperty]
    private bool _reuseClientForNextAccount = true;

    /// <summary>游戏进程名关键字（不区分大小写），用于定位窗口。</summary>
    [ObservableProperty]
    private string _processNameKeyword = "QQSpeed";

    /// <summary>游戏窗口标题关键字，用于定位窗口。</summary>
    [ObservableProperty]
    private string _windowTitleKeyword = "QQ飞车";

    /// <summary>截图方式：BitBlt／WindowsGraphicsCapture／WindowsGraphicsCaptureV2。</summary>
    [ObservableProperty]
    private string _captureMode = "BitBlt";

    /// <summary>键鼠操作之间的基础间隔（毫秒）。</summary>
    [ObservableProperty]
    private int _actionDelayMs = 120;

    /// <summary>逐字符输入时每个字符之间的间隔（毫秒），客户端吞字时可调大。</summary>
    [ObservableProperty]
    private int _typeTextIntervalMs = 35;

    /// <summary>键鼠输入通道。</summary>
    [ObservableProperty]
    private InputBackendKind _inputBackend = InputBackendKind.Auto;

    /// <summary>interception.dll 的完整路径，为空时使用程序目录下的同名文件。</summary>
    [ObservableProperty]
    private string _interceptionDllPath = "";

    /// <summary>每次发送输入前激活窗口后的等待（毫秒），避免输入丢失。</summary>
    [ObservableProperty]
    private int _windowActivateDelayMs = 350;

    /// <summary>单个流程步骤等待模板出现的默认超时（秒）。</summary>
    [ObservableProperty]
    private int _flowTimeoutSeconds = 60;

    /// <summary>结算画面出现后、截图前的等待（毫秒），等奖励动画播完。</summary>
    [ObservableProperty]
    private int _rewardScreenshotDelayMs = 1500;

    /// <summary>运行结束后是否把游戏窗口恢复前台。</summary>
    [ObservableProperty]
    private bool _restoreWindowOnStart = true;

    // ───────────────────────── 以下为白票扩展项 ─────────────────────────

    /// <summary>是否启用定时下号（到点停止任务并执行票完动作）。</summary>
    [ObservableProperty]
    private bool _scheduledStopEnabled;

    /// <summary>定时下号时间，格式 HH:mm。</summary>
    [ObservableProperty]
    private string _scheduledStopTime = "00:00";

    /// <summary>全部账号跑完后的动作。</summary>
    [ObservableProperty]
    private FinishAction _finishAction = FinishAction.None;

    /// <summary>
    /// 票完重爆时，若是「当天立即重来」则等待的秒数；
    /// 若同时开了定时上号，则改为隔天到点重来，不再立即开始。
    /// </summary>
    [ObservableProperty]
    private int _rerunDelaySeconds = 60;

    /// <summary>开机时自动启动本程序（写入注册表 Run 项）。</summary>
    [ObservableProperty]
    private bool _autoStartWithWindows;

    /// <summary>程序启动后自动开始白票（配合开机自启实现无人值守）。</summary>
    [ObservableProperty]
    private bool _autoStartTaskOnLaunch;

    /// <summary>登录大区，空表示按账号文本里的区服；可选 电信／电信二／联通／网通。</summary>
    [ObservableProperty]
    private string _defaultServer = "";

    /// <summary>优先尝试「快速登录」（复用 QQ 客户端登录态，点客户端头像，不输密码）。</summary>
    [ObservableProperty]
    private bool _fastLoginEnabled = true;

    /// <summary>登录前是否先选择大区（执行 flows/login_server.json）。</summary>
    [ObservableProperty]
    private bool _selectServerBeforeLogin = true;

    /// <summary>冻结提示连续出现几次后换号。</summary>
    [ObservableProperty]
    private int _freezeSwapThreshold = 2;

    /// <summary>提示环境异常连续出现几次后换号。</summary>
    [ObservableProperty]
    private int _envAbnormalSwapThreshold = 2;

    /// <summary>游戏中途掉线后的最大重连次数。</summary>
    [ObservableProperty]
    private int _reconnectMaxAttempts = 2;

    /// <summary>掉线重连前的等待秒数。</summary>
    [ObservableProperty]
    private int _reconnectDelaySeconds = 15;

    /// <summary>换号时等待上一个号完全退出的秒数（顶号延迟）。</summary>
    [ObservableProperty]
    private int _topUpDelaySeconds = 8;

    /// <summary>
    /// 号池顶替模式：同时挂 N 个号（N = 最大同时登录数），谁先跑完谁下号、号池里补下一个顶上，
    /// 直到全部账号跑完。关闭时按「单人逐个 → 组队分批」的老流程执行。
    /// </summary>
    [ObservableProperty]
    private bool _poolMode = true;

    /// <summary>每次点击/按键前插入随机随机延时的下限（毫秒），0 表示不插入。</summary>
    [ObservableProperty]
    private int _randomDelayMinMs = 0;

    /// <summary>随机延时上限（毫秒）。</summary>
    [ObservableProperty]
    private int _randomDelayMaxMs = 300;

    /// <summary>票完动作生效前的等待秒数，留出取消时间。</summary>
    [ObservableProperty]
    private int _finishActionDelaySeconds = 20;

    // ───────────────────────── 登录验证码（自动过码） ─────────────────────────

    /// <summary>
    /// 登录弹出「QQ安全中心」图片验证码时，是否自动识别并点选。
    /// 关闭或没有配置密钥／识别失败时，改为等待人工完成，不会直接跳过这个账号。
    /// </summary>
    [ObservableProperty]
    private bool _autoCaptchaEnabled = true;

    /// <summary>
    /// 验证码识别接口地址（OpenAI 兼容的 /chat/completions 服务，本地 Ollama 也填这个格式）。
    /// 默认用智谱开放平台的 GLM-4.6V-Flash，长期免费、128K 上下文，注册后拿 Key 即可用。
    /// </summary>
    [ObservableProperty]
    private string _captchaApiBaseUrl = "https://open.bigmodel.cn/api/paas/v4";

    /// <summary>验证码识别接口密钥（Bearer）。留空则只做人工等待。</summary>
    [ObservableProperty]
    private string _captchaApiKey = "";

    /// <summary>
    /// 验证码识别模型名，必须是支持图片输入的模型。
    /// glm-4.6v-flash 完全免费；换其他家时按平台的模型 ID 改这里。
    /// </summary>
    [ObservableProperty]
    private string _captchaModel = "glm-4.6v-flash";

    /// <summary>
    /// 主模型被限流（429）时改用的备用模型，留空表示不降级。
    /// 智谱的免费模型各自独立配额，glm-4.6v-flash 打满时 glm-4v-flash 往往还有余量。
    /// 换成其他平台时记得把它清空或改成该平台的模型。
    /// </summary>
    [ObservableProperty]
    private string _captchaFallbackModel = "glm-4v-flash";

    /// <summary>同一张验证码最多作答几次（答错会换一张图，重新识别）。</summary>
    [ObservableProperty]
    private int _captchaMaxAttempts = 3;

    /// <summary>没配密钥或识别失败时，等待人工完成验证码的秒数。</summary>
    [ObservableProperty]
    private int _captchaManualWaitSeconds = 180;

    /// <summary>验证码窗口标题关键字，多个用竖线分隔。</summary>
    [ObservableProperty]
    private string _captchaWindowTitles = "QQ安全中心|安全中心|安全验证|验证码";

    /// <summary>
    /// 是否记录 AI 调用日志。开启后每一次模型请求（含失败、限流、重试）都会单独写一节到
    /// User/log/ai/yyyy-MM-dd/ai-yyyyMMdd.log，内容包括完整请求体、完整响应体、状态码与耗时；
    /// 请求里带的图片存在同一天目录的 media 子目录里，便于整体拷走或删除。
    /// </summary>
    [ObservableProperty]
    private bool _aiRequestLogEnabled = true;

    /// <summary>
    /// 日志与截图的保留天数。超过这个天数的 User/log/ai/日期目录（正文＋图片）、
    /// run-*.log 和 User/debug 下的调试截图会被自动删除，0 表示永不清理。
    /// </summary>
    [ObservableProperty]
    private int _aiLogRetainDays = 7;
}
