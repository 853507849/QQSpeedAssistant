namespace QQSpeedAssistant.Services;

/// <summary>
/// 组合根：集中创建并持有全部服务，界面只依赖这个容器。
/// </summary>
public sealed class AppServices
{
    private AppServices()
    {
        Log = new LogService();
        Config = new ConfigService(Log);
        Flows = new FlowLoader(Log);
        Matcher = new TemplateMatcher(Log);
        Rewards = new RewardService(Log);
        Runner = new FlowRunner(Matcher, Rewards, Config, Log);
        Sessions = new SessionManager(Config, Log);
        Captcha = new CaptchaService(Log);
        Login = new AccountLoginService(Flows, Runner, Matcher, Log, Captcha);
        Logout = new LogoutService(Flows, Runner, Log);
        ModeExecution = new ModeExecutionService(Flows, Runner, Rewards, Log);
        FrameRate = new FrameRateService(Flows, Runner, Log);
        Shutdown = new ShutdownService(Log);
        Finish = new FinishActionService(Shutdown, Log);
        AutoStart = new AutoStartService(Log);
        Presets = new PresetService(Config, Log);
        Schedule = new ScheduleService(Config, Log);
        Orchestrator = new TaskOrchestrator(Config, Log, Sessions, Login, Logout, ModeExecution, FrameRate, Shutdown, Finish);
        Schedule.Triggered += OnScheduleTriggered;
        Schedule.StopTriggered += OnScheduleStopTriggered;
        Finish.SetRerunHandler(() =>
        {
            Orchestrator.Start();
            return Task.CompletedTask;
        });
    }

    /// <summary>日志服务。</summary>
    public LogService Log { get; }

    /// <summary>配置服务。</summary>
    public ConfigService Config { get; }

    /// <summary>流程加载器。</summary>
    public FlowLoader Flows { get; }

    /// <summary>模板匹配器。</summary>
    public TemplateMatcher Matcher { get; }

    /// <summary>流程执行器。</summary>
    public FlowRunner Runner { get; }

    /// <summary>奖励截图服务。</summary>
    public RewardService Rewards { get; }

    /// <summary>窗口会话管理。</summary>
    public SessionManager Sessions { get; }

    /// <summary>登录服务。</summary>
    public AccountLoginService Login { get; }

    /// <summary>登录验证码（自动过码）服务。</summary>
    public CaptchaService Captcha { get; }

    /// <summary>退出登录服务。</summary>
    public LogoutService Logout { get; }

    /// <summary>玩法执行服务。</summary>
    public ModeExecutionService ModeExecution { get; }

    /// <summary>降帧服务。</summary>
    public FrameRateService FrameRate { get; }

    /// <summary>关机服务。</summary>
    public ShutdownService Shutdown { get; }

    /// <summary>票完动作服务。</summary>
    public FinishActionService Finish { get; }

    /// <summary>开机自启服务。</summary>
    public AutoStartService AutoStart { get; }

    /// <summary>预设方案服务。</summary>
    public PresetService Presets { get; }

    /// <summary>定时服务。</summary>
    public ScheduleService Schedule { get; }

    /// <summary>任务编排。</summary>
    public TaskOrchestrator Orchestrator { get; }

    /// <summary>创建并初始化全部服务（创建目录、安装示例流程、读取配置、启动定时检查）。</summary>
    public static AppServices Create()
    {
        PathService.EnsureDirectories();
        var services = new AppServices();
        services.InstallSampleFlows();
        services.Config.Load();
        services.AutoStart.SyncFromSettings(services.Config.Settings.AutoStartWithWindows);
        services.Log.Info($"程序目录：{PathService.BaseDir}", "启动");
        services.Log.Info($"流程目录：{PathService.FlowDir}，模板目录：{PathService.TemplateDir}", "启动");
        services.Schedule.Start();
        return services;
    }

    /// <summary>
    /// 首次运行时把 Assets/Flows 下的示例流程复制到 flows 目录；已存在的文件不覆盖，避免覆盖用户校准结果。
    /// </summary>
    private void InstallSampleFlows()
    {
        var source = Path.Combine(PathService.BaseDir, "Assets", "Flows");
        if (!Directory.Exists(source))
        {
            return;
        }

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*.json"))
        {
            var target = Path.Combine(PathService.FlowDir, Path.GetFileName(file));
            if (File.Exists(target))
            {
                continue;
            }

            try
            {
                File.Copy(file, target);
                copied++;
            }
            catch (Exception ex)
            {
                Log.Warn($"示例流程复制失败：{ex.Message}", "启动");
            }
        }

        if (copied > 0)
        {
            Log.Info($"已安装 {copied} 个示例流程到 flows 目录，请按本机客户端校准模板", "启动");
        }
    }

    /// <summary>定时触发时启动任务。</summary>
    private Task OnScheduleTriggered()
    {
        Orchestrator.Start();
        return Task.CompletedTask;
    }

    /// <summary>定时下号触发时停止任务并执行票完动作。</summary>
    private Task OnScheduleStopTriggered() => Orchestrator.StopAndFinishAsync();

    /// <summary>程序退出时释放资源。</summary>
    public void Dispose()
    {
        Schedule.Stop();
        Orchestrator.Stop();
        Sessions.Dispose();
        Matcher.ClearCache();
    }
}
