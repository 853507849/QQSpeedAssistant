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
}
