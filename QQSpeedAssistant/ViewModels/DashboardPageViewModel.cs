using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQSpeedAssistant.Models;
using QQSpeedAssistant.Services;

namespace QQSpeedAssistant.ViewModels;

/// <summary>主页：展示运行状态、进度与最近日志，并提供启动／停止入口。</summary>
public sealed partial class DashboardPageViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// <summary>构造主页 ViewModel。</summary>
    public DashboardPageViewModel(AppServices services)
    {
        _services = services;
        RecentLogs = [];

        _services.Orchestrator.PropertyChanged += (_, _) => Refresh();
        _services.Orchestrator.StateChanged += Refresh;
        _services.Log.EntryAdded += OnLogAdded;
        _services.Config.Accounts.CollectionChanged += (_, _) => RefreshCounts();
        _services.Config.Tasks.CollectionChanged += (_, _) => RefreshCounts();

        foreach (var entry in _services.Log.Entries.TakeLast(10))
        {
            RecentLogs.Add(entry);
        }

        Refresh();
        RefreshCounts();
    }

    /// <summary>最近日志。</summary>
    public ObservableCollection<LogEntry> RecentLogs { get; }

    /// <summary>运行状态文本。</summary>
    [ObservableProperty]
    private string _stateText = "空闲";

    /// <summary>是否正在运行。</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>当前账号。</summary>
    [ObservableProperty]
    private string _currentAccount = "—";

    /// <summary>当前玩法。</summary>
    [ObservableProperty]
    private string _currentMode = "—";

    /// <summary>进度文本。</summary>
    [ObservableProperty]
    private string _progressText = "尚未开始";

    /// <summary>结果摘要。</summary>
    [ObservableProperty]
    private string _summary = "";

    /// <summary>账号统计。</summary>
    [ObservableProperty]
    private string _accountInfo = "0 个账号";

    /// <summary>任务统计。</summary>
    [ObservableProperty]
    private string _taskInfo = "0 个玩法启用";

    /// <summary>定时说明。</summary>
    [ObservableProperty]
    private string _scheduleText = "定时未开启";

    /// <summary>关机说明。</summary>
    [ObservableProperty]
    private string _shutdownText = "全部完成后不关机";

    /// <summary>权限不足时引导提权重启，返回是否可以继续执行。</summary>
    private bool EnsureElevated()
    {
        if (ElevationService.IsElevated)
        {
            return true;
        }

        _services.Log.Warn("当前不是管理员权限：游戏以管理员运行时，点击与输入会被系统丢弃", "启动");
        var answer = MessageBox.Show(
            "当前不是管理员权限。\n游戏客户端通常以管理员身份运行，此时 Windows 会丢弃本程序的点击与键盘输入（表现为“点不动、输不进”）。\n\n现在以管理员身份重启吗？",
            "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);

        if (answer != MessageBoxResult.Yes)
        {
            _services.Log.Warn("已选择在非管理员权限下继续：若游戏以管理员运行，点击与输入会被系统丢弃", "启动");
            return true;
        }

        if (ElevationService.TryRestartAsAdmin())
        {
            Application.Current?.Shutdown();
        }

        return false;
    }

    /// <summary>是否可以启动任务。</summary>
    public bool CanStart => !IsRunning;

    /// <summary>是否可以停止任务。</summary>
    public bool CanStop => IsRunning;

    /// <summary>运行状态变化时刷新按钮可用性。</summary>
    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    /// <summary>启动任务：先保存配置，再自检游戏路径与模板。</summary>
    [RelayCommand]
    private void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _services.Config.SaveAll();
        if (!CheckPreconditions())
        {
            return;
        }

        _services.Orchestrator.Start();
    }

    /// <summary>停止任务。</summary>
    [RelayCommand]
    private void Stop() => _services.Orchestrator.Stop();

    /// <summary>开始前的自检：没有可启动的游戏、或识别模板缺失时给出明确提示。</summary>
    private bool CheckPreconditions()
    {
        if (!EnsureElevated())
        {
            return false;
        }

        var settings = _services.Config.Settings;

        if (_services.Sessions.FindWindows().Count == 0)
        {
            if (!settings.AutoLaunchGame)
            {
                _services.Log.Warn("当前没有游戏窗口，且已关闭「窗口不足时自动启动」，任务会直接跳过账号", "任务");
            }
            else if (!_services.Sessions.TryResolveGameExe(out var exePath, out var reason))
            {
                _services.Log.Error($"无法自动启动游戏：{reason}", "任务");
                MessageBox.Show($"{reason}\n\n请先到「运行设置 → 客户端与窗口」里配置游戏主程序（exe 或安装目录），再点「开始任务」。",
                    "还缺少游戏路径", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            else if (WindowLocator.LooksLikeLauncher(exePath))
            {
                _services.Log.Warn($"配置的是启动器 {Path.GetFileName(exePath)}：启动后需要先手动进入游戏登录界面，程序只等待游戏窗口，不会点击启动器上的按钮", "任务");
            }
        }

        var missing = CollectMissingTemplates();
        if (missing.Count > 0)
        {
            var list = string.Join("、", missing);
            _services.Log.Warn($"缺少 {missing.Count} 个识别模板：{list}", "任务");
            var answer = MessageBox.Show(
                $"缺少 {missing.Count} 个识别模板：\n{list}\n\n没有模板时，对应步骤会失败。\n提示：可先单独跑一轮验证登录流程是否打通。\n\n是否仍然开始？",
                "模板尚未采集", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>收集即将执行的流程中缺失的必填模板；指定 <paramref name="onlyFlow"/> 时只检查该流程。</summary>
    private List<string> CollectMissingTemplates(string? onlyFlow = null)
    {
        var settings = _services.Config.Settings;
        var flowNames = new List<string>();

        if (onlyFlow != null)
        {
            flowNames.Add(onlyFlow);
        }
        else
        {
            flowNames.Add("login");

            if (settings.LowFrameRateEnabled)
            {
                flowNames.Add("lower_fps");
            }

            if (settings.ReuseClientForNextAccount)
            {
                flowNames.Add("logout");
            }

            flowNames.AddRange(_services.Config.Tasks
                .Where(t => t.Enabled && t.Rounds > 0)
                .Select(t => t.ModeId));
        }

        var missing = new List<string>();
        foreach (var name in flowNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var flow = _services.Flows.Load(name);
            if (flow == null)
            {
                continue;
            }

            foreach (var step in flow.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Template) || !step.Required)
                {
                    continue;
                }

                var template = step.Template!;
                if (_services.Matcher.TemplateExists(template))
                {
                    continue;
                }

                if (!missing.Contains(template, StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add(template);
                }
            }
        }

        return missing;
    }

    /// <summary>取消关机倒计时。</summary>
    [RelayCommand]
    private void CancelShutdown() => _services.Shutdown.Cancel();

    /// <summary>打开奖励截图目录。</summary>
    [RelayCommand]
    private void OpenRewardFolder() => OpenFolder(PathService.RewardDir);

    /// <summary>打开流程目录。</summary>
    [RelayCommand]
    private void OpenFlowFolder() => OpenFolder(PathService.FlowDir);

    /// <summary>打开模板目录。</summary>
    [RelayCommand]
    private void OpenTemplateFolder() => OpenFolder(PathService.TemplateDir);

    /// <summary>用资源管理器打开目录。</summary>
    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Log.Error($"打开目录失败：{ex.Message}", "界面");
        }
    }

    /// <summary>日志新增时追加到最近列表。</summary>
    private void OnLogAdded(LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => AppendLog(entry)));
            return;
        }

        AppendLog(entry);
    }

    /// <summary>追加一条最近日志。</summary>
    private void AppendLog(LogEntry entry)
    {
        RecentLogs.Add(entry);
        while (RecentLogs.Count > 10)
        {
            RecentLogs.RemoveAt(0);
        }
    }

    /// <summary>同步编排器状态。</summary>
    private void Refresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }

        var orchestrator = _services.Orchestrator;
        StateText = orchestrator.State.ToText();
        IsRunning = orchestrator.IsRunning;
        CurrentAccount = string.IsNullOrEmpty(orchestrator.CurrentAccount) ? "—" : orchestrator.CurrentAccount;
        CurrentMode = string.IsNullOrEmpty(orchestrator.CurrentMode) ? "—" : orchestrator.CurrentMode;
        ProgressText = orchestrator.ProgressText;
        Summary = orchestrator.Summary;

        var settings = _services.Config.Settings;
        ScheduleText = settings.ScheduledStartEnabled ? $"每日 {settings.ScheduledStartTime} 自动上号" : "定时未开启";
        ShutdownText = settings.AutoShutdown ? $"全部完成后 {settings.ShutdownDelaySeconds} 秒关机" : "全部完成后不关机";
        RefreshCounts();
    }

    /// <summary>刷新账号与玩法统计。</summary>
    private void RefreshCounts()
    {
        var accounts = _services.Config.Accounts;
        var enabled = accounts.Count(a => a.Enabled);
        AccountInfo = $"{accounts.Count} 个账号，启用 {enabled} 个";

        var tasks = _services.Config.Tasks.Where(t => t.Enabled).ToList();
        var rounds = tasks.Sum(t => Math.Max(0, t.Rounds));
        TaskInfo = $"{tasks.Count} 个玩法启用，共 {rounds} 局";
    }
}
