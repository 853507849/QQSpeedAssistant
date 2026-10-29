using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using QQSpeedAssistant.Models;
using QQSpeedAssistant.Services;

namespace QQSpeedAssistant.ViewModels;

/// <summary>主页：展示运行状态、进度与最近日志，并提供启动／停止入口。</summary>
public sealed partial class DashboardPageViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// <summary>从配置反向同步路径时置位，避免回写配置形成循环。</summary>
    private bool _synchronizingPath;

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

        // 运行设置页改游戏路径时，主页的输入框与状态提示同步刷新。
        _services.Config.Settings.PropertyChanged += (_, _) => OnSettingsChanged();

        foreach (var entry in _services.Log.Entries.TakeLast(10))
        {
            RecentLogs.Add(entry);
        }

        Refresh();
        RefreshCounts();
        OnSettingsChanged();
    }

    /// <summary>设置变化时同步路径输入框与状态提示。</summary>
    private void OnSettingsChanged()
    {
        var latest = _services.Config.Settings.GameExePath ?? "";
        if (!string.Equals(GameExePath, latest, StringComparison.Ordinal))
        {
            _synchronizingPath = true;
            try
            {
                GameExePath = latest;
            }
            finally
            {
                _synchronizingPath = false;
            }
        }

        RefreshGameStatus();
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

    /// <summary>游戏主程序路径（也可填安装目录），与运行设置共用同一份配置。</summary>
    [ObservableProperty]
    private string _gameExePath = "";

    /// <summary>游戏客户端状态提示。</summary>
    [ObservableProperty]
    private string _gameStatusText = "未配置游戏主程序";

    /// <summary>是否正在启动游戏。</summary>
    [ObservableProperty]
    private bool _isGameBusy;

    /// <summary>是否正在做登录测试。</summary>
    [ObservableProperty]
    private bool _isTestingLogin;

    /// <summary>是否可以点「启动游戏」。</summary>
    public bool CanLaunchGame => !IsGameBusy && !IsTestingLogin;

    /// <summary>是否可以点「登录测试」。</summary>
    public bool CanTestLogin => !IsTestingLogin && !IsGameBusy && !IsRunning;

    /// <summary>当前是否为管理员权限（游戏以管理员运行时必须一致）。</summary>
    [ObservableProperty]
    private bool _isAdmin = ElevationService.IsElevated;

    /// <summary>以管理员身份重启本程序。</summary>
    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (ElevationService.IsElevated)
        {
            MessageBox.Show("当前已经是管理员权限。", "无需重启", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ElevationService.TryRestartAsAdmin())
        {
            _services.Log.Warn("正在以管理员身份重启…", "启动");
            Application.Current?.Shutdown();
            return;
        }

        MessageBox.Show("未能以管理员身份启动（可能取消了 UAC）。\n请右键程序 → 以管理员身份运行。", "重启失败",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>权限不足时引导提权重启，返回是否可以继续执行。</summary>
    private bool EnsureElevated()
    {
        if (ElevationService.IsElevated)
        {
            IsAdmin = true;
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

    /// <summary>启动状态变化时刷新按钮可用性。</summary>
    partial void OnIsGameBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunchGame));
        OnPropertyChanged(nameof(CanTestLogin));
    }

    /// <summary>登录测试状态变化时刷新按钮可用性。</summary>
    partial void OnIsTestingLoginChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunchGame));
        OnPropertyChanged(nameof(CanTestLogin));
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
        OnPropertyChanged(nameof(CanTestLogin));
    }

    /// <summary>路径修改后立即写回配置，避免忘记保存导致启动时仍用旧路径。</summary>
    partial void OnGameExePathChanged(string value)
    {
        if (_synchronizingPath)
        {
            return;
        }

        var normalized = value?.Trim() ?? "";
        if (!string.Equals(_services.Config.Settings.GameExePath, normalized, StringComparison.Ordinal))
        {
            _services.Config.Settings.GameExePath = normalized;
            _services.Config.SaveSettings();
        }

        RefreshGameStatus();
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

    /// <summary>选择客户端主程序 exe。</summary>
    [RelayCommand]
    private void BrowseGameExe()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 QQ飞车 客户端主程序",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        var current = GameExePath;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var folder = File.Exists(current) ? Path.GetDirectoryName(current) : current;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                dialog.InitialDirectory = folder;
            }
        }

        if (dialog.ShowDialog() == true)
        {
            GameExePath = dialog.FileName;
            _services.Log.Info($"已选择游戏主程序：{dialog.FileName}", "窗口");
        }
    }

    /// <summary>选择游戏安装目录，程序会自动在目录里找客户端主程序。</summary>
    [RelayCommand]
    private void ChooseGameFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择 QQ飞车 安装目录" };

        var current = GameExePath;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var folder = File.Exists(current) ? Path.GetDirectoryName(current) : current;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                dialog.InitialDirectory = folder;
            }
        }

        if (dialog.ShowDialog() == true)
        {
            GameExePath = dialog.FolderName;
            _services.Log.Info($"已选择游戏目录：{dialog.FolderName}", "窗口");
        }
    }

    /// <summary>按配置启动游戏并接管窗口（不执行任务）。</summary>
    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        if (IsGameBusy)
        {
            return;
        }

        _services.Config.SaveSettings();
        if (!_services.Sessions.TryResolveGameExe(out var exePath, out var error))
        {
            _services.Log.Error($"启动游戏失败：{error}", "窗口");
            RefreshGameStatus();
            MessageBox.Show($"{error}\n\n请先选择客户端 exe 或安装目录。", "还缺少游戏路径",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsGameBusy = true;
        try
        {
            _services.Log.Info($"启动游戏：{exePath}", "窗口");
            var session = await _services.Sessions.LaunchAndAttachAsync(CancellationToken.None);
            if (session == null)
            {
                MessageBox.Show(
                    "已尝试启动游戏，但暂时没有接管到窗口。\n\n"
                    + "常见原因：\n"
                    + "• 启动器已经在运行：多数启动器只允许一个实例，重复启动不会有新窗口\n"
                    + "• 配置的是启动器而不是客户端 exe：程序不会替你去点启动器上的「开始游戏」\n"
                    + "• 客户端还在加载：加载完成后可再点「检测窗口」",
                    "未找到游戏窗口", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                // 把窗口切到前台：机器人接管的是后台窗口，用户看不见任何变化就会以为「点了没反应」。
                var activated = session.Activate();
                _services.Log.Success(
                    $"窗口已就绪：{session.Title}（进程 {session.ProcessName}）"
                    + (activated ? "，已切到前台" : "，但没能切到前台")
                    + "，可直接点「登录测试」或「开始任务」", "窗口");
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error($"启动游戏异常：{ex.Message}", "窗口");
        }
        finally
        {
            IsGameBusy = false;
            RefreshGameStatus();
        }
    }

    /// <summary>登录测试：只跑一个账号的登录流程，用来验证登录环节是否打通。</summary>
    [RelayCommand]
    private async Task TestLoginAsync()
    {
        if (IsTestingLogin || IsRunning)
        {
            return;
        }

        var account = _services.Config.Accounts.FirstOrDefault(a => a.Enabled);
        if (account == null)
        {
            MessageBox.Show("还没有启用的账号，请先到「账号」页添加并启用。", "没有可用账号",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!CheckGameReady())
        {
            return;
        }

        var missing = CollectMissingTemplates("login");
        if (missing.Count > 0)
        {
            _services.Log.Warn($"登录流程还缺模板：{string.Join("、", missing)}", "任务");
            MessageBox.Show($"登录流程缺少模板：\n{string.Join("、", missing)}\n\n请先到「玩法任务 → 采集模板」补齐（至少要 login_account_box、login_password_box、login_button）。",
                "登录模板不全", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsTestingLogin = true;
        try
        {
            _services.Config.SaveAll();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            _services.Log.Info($"登录测试开始：{account.Account}", "任务");

            var sessions = await _services.Sessions.EnsureAsync([account], cts.Token);
            var session = sessions.FirstOrDefault();
            if (session == null)
            {
                _services.Log.Error("登录测试失败：没有可用的游戏窗口", "任务");
                return;
            }

            var result = await _services.Login.LoginAsync(session, account, _services.Config.Settings, cts.Token);
            if (result.Success)
            {
                account.Status = AccountRunStatus.Completed;
                account.StatusDetail = "登录测试成功";
                _services.Log.Success("登录测试成功：已进入游戏，可继续采集大厅/玩法模板", "任务");
                MessageBox.Show("登录成功，游戏已在运行。\n\n现在可以去「玩法任务 → 采集模板」采大厅与玩法模板（如 hall_main、hall_multiplayer）。",
                    "登录成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                account.Status = result.Status;
                account.StatusDetail = result.Message;
                _services.Log.Error($"登录测试失败：{result.Message}", "任务");
            }
        }
        catch (OperationCanceledException)
        {
            _services.Log.Warn("登录测试已取消或超时", "任务");
        }
        catch (Exception ex)
        {
            _services.Log.Error($"登录测试异常：{ex.Message}", "任务");
        }
        finally
        {
            IsTestingLogin = false;
            RefreshGameStatus();
        }
    }

    /// <summary>检查能否拿到游戏窗口（必要时按配置启动），返回是否继续。</summary>
    private bool CheckGameReady()
    {
        if (!EnsureElevated())
        {
            return false;
        }

        var settings = _services.Config.Settings;
        if (_services.Sessions.FindWindows().Count > 0)
        {
            return true;
        }

        if (!settings.AutoLaunchGame)
        {
            MessageBox.Show("当前没有游戏窗口，且已关闭「窗口不足时自动启动」。请先手动打开游戏再试。",
                "没有游戏窗口", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!_services.Sessions.TryResolveGameExe(out _, out var reason))
        {
            _services.Log.Error($"无法自动启动游戏：{reason}", "任务");
            RefreshGameStatus();
            MessageBox.Show($"{reason}\n\n请先在「游戏客户端」里选择客户端 exe 或安装目录。",
                "还缺少游戏路径", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 输入自检：只移动光标不点击，检查权限/DPI/多显示器造成的坐标偏移，
    /// 并把环境快照写进日志，用于定位“点不动、输不进”到底卡在哪一环。
    /// </summary>
    [RelayCommand]
    private async Task InputSelfCheckAsync()
    {
        var session = _services.Sessions.Sessions.FirstOrDefault();
        if (session == null)
        {
            _services.Log.Warn("输入自检：还没有接管游戏窗口，请先点「检测窗口」或「启动游戏」", "自检");
            MessageBox.Show("还没有接管游戏窗口。\n\n请先点「检测窗口」接管已运行的游戏，或点「启动游戏」。", "没有游戏窗口",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsGameBusy = true;
        try
        {
            var ok = await Task.Run(session.RunInputDiagnostics);
            MessageBox.Show(ok
                    ? "自检通过：光标能精确落到目标像素。\n\n若游戏里仍然点不动、输不进，说明输入被反外挂过滤，请安装 Interception 驱动（步骤见日志）。"
                    : "自检未通过：光标落点有偏差，请看「日志」页的偏差详情（常见原因：显示器缩放、多屏不同缩放、远程桌面、权限不足）。",
                ok ? "输入自检通过" : "输入自检未通过",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _services.Log.Error($"输入自检异常：{ex.Message}", "自检");
        }
        finally
        {
            IsGameBusy = false;
            RefreshGameStatus();
        }
    }

    /// <summary>
    /// 控件探测：用 UI Automation 导出游戏窗口的控件树，判断能否直接给输入框赋值。
    /// 结果同时写进日志和 User/debug 下的 txt，便于排查界面到底暴露了什么。
    /// </summary>
    [RelayCommand]
    private async Task ProbeControlsAsync()
    {
        var session = _services.Sessions.Sessions.FirstOrDefault();
        if (session == null)
        {
            _services.Log.Warn("控件探测：还没有接管游戏窗口，请先点「检测窗口」或「启动游戏」", "UIA");
            MessageBox.Show("还没有接管游戏窗口。\n\n请先点「检测窗口」接管已运行的游戏，或点「启动游戏」。", "没有游戏窗口",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsGameBusy = true;
        try
        {
            var tree = await Task.Run(() => UiaAutomationService.Describe(session.Handle));
            var lines = tree.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Take(20))
            {
                _services.Log.Info(line.TrimEnd(), "UIA");
            }

            if (lines.Length > 20)
            {
                _services.Log.Info($"控件树共 {lines.Length} 行，完整内容已保存到 User/debug", "UIA");
            }

            try
            {
                var dir = Path.Combine(PathService.UserDir, "debug");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"uia-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(file, tree);
                _services.Log.Info($"已保存：User/debug/{Path.GetFileName(file)}", "UIA");
            }
            catch (Exception ex)
            {
                _services.Log.Warn($"控件树保存失败：{ex.Message}", "UIA");
            }
        }
        finally
        {
            IsGameBusy = false;
        }
    }

    /// <summary>重新检测游戏窗口，刷新状态提示。</summary>
    [RelayCommand]
    private void RefreshGameStatus()
    {
        var settings = _services.Config.Settings;
        var windows = _services.Sessions.FindWindows();

        if (windows.Count > 0)
        {
            var first = windows[0];
            GameStatusText = WindowLocator.IsHelperProcess(first.ProcessName)
                ? $"当前只有启动器窗口：{first.Title}（进程 {first.ProcessName}），登录流程找不到登录框，请把主程序改选为游戏客户端 exe（如 QQSpeed.exe）"
                : $"已检测到 {windows.Count} 个游戏窗口：{first.Title}（{first.Width}x{first.Height}），点「开始任务」即可登录并执行勾选的玩法";
            return;
        }

        // 已经被会话接管的窗口不会出现在 FindWindows 里，这里单独提示，避免误报“没有窗口”。
        var attached = _services.Sessions.Sessions;
        if (attached.Count > 0)
        {
            var first = attached[0];
            GameStatusText = $"已接管 {attached.Count} 个游戏窗口：{first.Title}（进程 {first.ProcessName}），可直接点「开始任务」";
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.GameExePath))
        {
            GameStatusText = "未配置游戏主程序：选择客户端 exe 或安装目录后，点「开始任务」会自动启动游戏并登录";
            return;
        }

        if (!_services.Sessions.TryResolveGameExe(out var exePath, out var error))
        {
            GameStatusText = $"路径不可用：{error}";
            return;
        }

        GameStatusText = WindowLocator.LooksLikeLauncher(exePath)
            ? $"已配置：{exePath}　这是启动器，不是游戏主程序：启动器窗口无法用于登录，请改选客户端 exe（或选择安装目录让程序自动挑）"
            : $"已配置：{exePath}　当前没有运行中的游戏窗口，点「启动游戏」或「开始任务」会自动拉起";
    }

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
                RefreshGameStatus();
                MessageBox.Show($"{reason}\n\n请先在「游戏客户端」里选择客户端 exe 或安装目录，再点「开始任务」。",
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
                $"缺少 {missing.Count} 个识别模板：\n{list}\n\n没有模板时，对应步骤会失败。\n提示：可先点「登录测试」把登录跑通，进入大厅后再采大厅/玩法模板。\n\n是否仍然开始？",
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
        RefreshGameStatus();
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
