using CommunityToolkit.Mvvm.ComponentModel;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 任务编排：决定账号顺序、拆分单人／组队玩法、驱动登录与执行，并在全部完成后执行票完动作。
/// 支持两种调度：
/// ① 号池顶替（默认）：同时挂 N 个号，谁先跑完谁下号，号池里补下一个顶上；
/// ② 顺序执行：逐个账号跑完单人玩法，再按所需人数分批跑组队玩法。
/// </summary>
public sealed partial class TaskOrchestrator : ObservableObject
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly SessionManager _sessions;
    private readonly AccountLoginService _login;
    private readonly LogoutService _logout;
    private readonly ModeExecutionService _modes;
    private readonly FrameRateService _frameRate;
    private readonly ShutdownService _shutdown;
    private readonly FinishActionService _finish;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly object _countLock = new();
    private CancellationTokenSource? _cts;
    private bool _finishOnStop;

    /// <summary>构造任务编排器。</summary>
    public TaskOrchestrator(ConfigService config, LogService log, SessionManager sessions,
        AccountLoginService login, LogoutService logout, ModeExecutionService modes,
        FrameRateService frameRate, ShutdownService shutdown, FinishActionService finish)
    {
        _config = config;
        _log = log;
        _sessions = sessions;
        _login = login;
        _logout = logout;
        _modes = modes;
        _frameRate = frameRate;
        _shutdown = shutdown;
        _finish = finish;
    }

    /// <summary>整体运行状态。</summary>
    [ObservableProperty]
    private RunState _state = RunState.Idle;

    /// <summary>当前账号。</summary>
    [ObservableProperty]
    private string _currentAccount = "";

    /// <summary>当前执行的玩法。</summary>
    [ObservableProperty]
    private string _currentMode = "";

    /// <summary>进度文本。</summary>
    [ObservableProperty]
    private string _progressText = "尚未开始";

    /// <summary>已完成账号数。</summary>
    [ObservableProperty]
    private int _completedAccounts;

    /// <summary>本次需要执行的账号数。</summary>
    [ObservableProperty]
    private int _totalAccounts;

    /// <summary>结果摘要。</summary>
    [ObservableProperty]
    private string _summary = "";

    /// <summary>开始时间。</summary>
    [ObservableProperty]
    private DateTime? _startedAt;

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => State == RunState.Running;

    /// <summary>状态变化事件，界面用于刷新按钮可用性。</summary>
    public event Action? StateChanged;

    /// <summary>异步启动任务（界面调用后立即返回）。</summary>
    public void Start()
    {
        if (IsRunning)
        {
            _log.Warn("任务已在运行中", "任务");
            return;
        }

        _finishOnStop = false;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    /// <summary>请求停止任务。</summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        State = RunState.Stopping;
        StateChanged?.Invoke();
        _cts?.Cancel();
        _log.Warn("已请求停止任务，等待当前步骤结束", "任务");
    }

    /// <summary>
    /// 定时下号：停止任务，但停止后仍要执行票完动作（关机／重启／重爆／小化）。
    /// </summary>
    public async Task StopAndFinishAsync()
    {
        _finishOnStop = true;
        if (!IsRunning)
        {
            // 任务本来就没在跑，直接按设置收尾
            await SafeFinishAsync(_config.Settings);
            return;
        }

        Stop();

        // 等运行循环自然退出（最长 3 分钟），避免和收尾动作抢资源
        for (var i = 0; i < 90 && State == RunState.Stopping; i++)
        {
            await Task.Delay(2000);
        }
    }

    /// <summary>执行全部账号与全部玩法。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (State == RunState.Running)
        {
            return;
        }

        var settings = _config.Settings;
        State = RunState.Running;
        StartedAt = DateTime.Now;
        Summary = "";
        StateChanged?.Invoke();

        var allAccounts = _config.Accounts.Where(a => a.Enabled).OrderBy(a => a.Order).ToList();
        var startIndex = Math.Clamp(settings.StartAccountIndex, 1, Math.Max(1, allAccounts.Count));
        var targets = allAccounts.Skip(startIndex - 1).ToList();

        foreach (var account in _config.Accounts)
        {
            account.Status = AccountRunStatus.NotStarted;
            account.StatusDetail = "";
        }

        // 起始序号之前的账号不登录，只标记为跳过
        foreach (var skipped in allAccounts.Take(startIndex - 1))
        {
            skipped.Status = AccountRunStatus.Skipped;
            skipped.StatusDetail = "起始序号之前，本次不登录";
        }

        TotalAccounts = targets.Count;
        CompletedAccounts = 0;

        if (targets.Count == 0)
        {
            _log.Warn("没有可执行的账号，请检查启用状态与起始账号序号", "任务");
            State = RunState.Completed;
            ProgressText = "无账号可执行";
            StateChanged?.Invoke();
            return;
        }

        _log.Info($"开始执行：共 {targets.Count} 个账号，从第 {startIndex} 个账号开始"
                  + $"（{(settings.PoolMode ? $"号池顶替，同时 {Math.Max(1, settings.MaxConcurrentInstances)} 个号" : "顺序执行")}）", "任务");
        var failedModes = 0;

        try
        {
            var tasks = _config.Tasks
                .Where(t => t.Enabled && t.Rounds > 0)
                .Select(t => (Task: t, Definition: GameModeCatalog.Find(t.ModeId)))
                .Where(x => x.Definition != null)
                .Select(x => (x.Task, Definition: x.Definition!))
                .ToList();

            // 单人流程：本来就不需要多人，或者勾了「单号匹配」（系统随机配队友）
            var soloTasks = tasks.Where(x => !x.Definition.IsTeamMode || x.Task.SoloMatch).ToList();
            var teamTasks = tasks.Where(x => x.Definition.IsTeamMode && !x.Task.SoloMatch).ToList();
            _log.Info($"玩法计划：单号玩法 {soloTasks.Count} 个，组队玩法 {teamTasks.Count} 个", "任务");

            if (settings.PoolMode)
            {
                failedModes += await RunPoolAsync(targets, soloTasks, settings, ct);
            }
            else
            {
                failedModes += await RunSequentialAsync(targets, soloTasks, settings, ct);
            }

            // 组队玩法：号池模式下已完成账号不再挂着，统一按需分批
            var usable = targets.Where(a => a.Status is AccountRunStatus.Completed or AccountRunStatus.Running).ToList();
            if (teamTasks.Count > 0 && usable.Count > 0)
            {
                failedModes += await RunTeamTasksAsync(teamTasks, usable, settings, ct);
            }

            CurrentMode = "";
            CurrentAccount = "";
            var elapsed = DateTime.Now - (StartedAt ?? DateTime.Now);
            Summary = $"完成 {CompletedAccounts}/{TotalAccounts} 个账号，玩法失败 {failedModes} 次，用时 {elapsed:hh\\:mm\\:ss}";
            ProgressText = "全部任务完成";
            State = RunState.Completed;
            _log.Success(Summary, "任务");

            await SafeFinishAsync(settings);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "已停止";
            Summary = $"已停止，完成 {CompletedAccounts}/{TotalAccounts} 个账号";
            State = RunState.Idle;
            _log.Warn(Summary, "任务");

            // 定时下号触发的停止要照常执行票完动作；手动停止则不做
            if (_finishOnStop)
            {
                await SafeFinishAsync(settings);
            }
        }
        catch (Exception ex)
        {
            ProgressText = "运行异常";
            Summary = $"运行异常：{ex.Message}";
            State = RunState.Failed;
            _log.Error($"任务运行异常：{ex.Message}", "任务");
        }
        finally
        {
            StateChanged?.Invoke();
        }
    }

    /// <summary>执行票完动作，异常只记日志，不影响主流程状态。</summary>
    private async Task SafeFinishAsync(AppSettings settings)
    {
        try
        {
            await _finish.ExecuteAsync(settings, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error($"票完动作执行失败：{ex.Message}", "收尾");
        }
    }

    /// <summary>号池里多个账号并发完成，计数必须串行自增。</summary>
    private void BumpCompleted()
    {
        lock (_countLock)
        {
            CompletedAccounts++;
        }
    }

    /// <summary>
    /// 号池顶替：同时挂 N 个号，谁先跑完谁下号并补下一个，直到号池清空。
    /// 返回玩法失败次数。
    /// </summary>
    private async Task<int> RunPoolAsync(List<AccountEntry> targets,
        List<(ModeTaskConfig Task, GameModeDefinition Definition)> soloTasks, AppSettings settings, CancellationToken ct)
    {
        var slots = Math.Clamp(settings.MaxConcurrentInstances, 1, 16);
        var queue = new Queue<AccountEntry>(targets);
        var running = new List<Task>();
        var failed = 0;

        _log.Info($"号池启动：并发 {slots} 个号，共 {targets.Count} 个待跑", "号池");

        while (queue.Count > 0 || running.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            while (running.Count < slots && queue.Count > 0)
            {
                var account = queue.Dequeue();
                running.Add(RunPoolAccountAsync(account, soloTasks, settings, ct));
            }

            var finished = await Task.WhenAny(running);
            running.Remove(finished);

            if (finished.IsFaulted && finished.Exception != null)
            {
                failed++;
                _log.Error($"号池中的账号任务异常：{finished.Exception.GetBaseException().Message}", "号池");
            }
        }

        await Task.WhenAll(running);
        ProgressText = "号池已清空";
        return failed;
    }

    /// <summary>号池中单个账号的完整生命周期：占窗口 → 登录 → 跑玩法 → 下号 → 释放窗口。</summary>
    private async Task RunPoolAccountAsync(AccountEntry account,
        List<(ModeTaskConfig Task, GameModeDefinition Definition)> soloTasks,
        AppSettings settings, CancellationToken ct)
    {
        CurrentAccount = account.Account;
        account.Status = AccountRunStatus.Running;
        account.StatusDetail = "";
        ProgressText = $"号池 {CompletedAccounts + 1}/{TotalAccounts} 登录中：{account.Account}";
        _log.Info("号池取号，开始处理", account.Account);

        GameSession? session = null;
        try
        {
            // 会话管理内部是可变的 List，多个号同时取窗口必须串行
            await _sessionGate.WaitAsync(ct);
            try
            {
                var sessions = await _sessions.EnsureAsync([account], ct);
                session = sessions.FirstOrDefault();
            }
            finally
            {
                _sessionGate.Release();
            }

            if (session == null)
            {
                account.Status = AccountRunStatus.Failed;
                account.StatusDetail = "没有可用的游戏窗口";
                _log.Error("没有可用的游戏窗口，跳过该账号", account.Account);
                BumpCompleted();
                return;
            }

            var login = await _login.LoginAsync(session, account, settings, ct);
            if (!login.Success)
            {
                account.Status = login.Status;
                account.StatusDetail = login.Message;
                BumpCompleted();
                await ReleaseSessionAsync(session, account, settings, ct);
                return;
            }

            if (settings.LowFrameRateEnabled)
            {
                await _frameRate.ApplyAsync(session, account, settings, ct);
            }

            foreach (var (task, definition) in soloTasks)
            {
                ct.ThrowIfCancellationRequested();
                var (open, reason) = ModeAvailabilityRules.Check(definition.Availability, DateTime.Now);
                if (!open)
                {
                    _log.Info($"跳过 {definition.Name}：{reason}", account.Account);
                    continue;
                }

                if (!_modes.HasFlow(definition))
                {
                    _log.Warn($"跳过 {definition.Name}：缺少 flows/{definition.Id}.json", account.Account);
                    continue;
                }

                for (var round = 1; round <= task.Rounds; round++)
                {
                    ct.ThrowIfCancellationRequested();
                    CurrentMode = $"{definition.Name} 第 {round}/{task.Rounds} 局";
                    ProgressText = $"{CompletedAccounts + 1}/{TotalAccounts} {account.Account} · {CurrentMode}";
                    var result = await RunRoundWithReconnectAsync(session, account, definition, task, round, true, settings, ct);
                    if (!result.Success)
                    {
                        _log.Error($"{definition.Name} 第 {round} 局失败：{result.Message}", account.Account);
                        account.StatusDetail = $"{definition.Name} 第 {round} 局失败";
                        if (result.Reason == FlowFailReasons.Disconnected)
                        {
                            account.Status = AccountRunStatus.Disconnected;
                            BumpCompleted();
                            await ReleaseSessionAsync(session, account, settings, ct);
                            return;
                        }

                        break;
                    }
                }
            }

            if (account.Status == AccountRunStatus.Running)
            {
                account.Status = AccountRunStatus.Completed;
            }

            BumpCompleted();
            ProgressText = $"{CompletedAccounts}/{TotalAccounts} 账号完成";
            await ReleaseSessionAsync(session, account, settings, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            account.Status = AccountRunStatus.Failed;
            account.StatusDetail = ex.Message;
            CompletedAccounts++;
            _log.Error($"账号处理异常：{ex.Message}", account.Account);
        }
    }

    /// <summary>下号并释放窗口，随后等「顶号延迟」再让下一个号上，避免顶号冲突。</summary>
    private async Task ReleaseSessionAsync(GameSession session, AccountEntry account, AppSettings settings,
        CancellationToken ct)
    {
        try
        {
            var loggedOut = await _logout.RunAsync(session, account, ct);
            if (!loggedOut)
            {
                // 没有退出流程时只能解绑：窗口里的角色还挂着，下一个号登录会更快，但也更容易顶号
                await _sessionGate.WaitAsync(ct);
                try
                {
                    _sessions.Release(account);
                }
                finally
                {
                    _sessionGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"下号失败：{ex.Message}", account.Account);
        }

        var delay = Math.Clamp(settings.TopUpDelaySeconds, 0, 300);
        if (delay > 0)
        {
            _log.Info($"顶号延迟 {delay} 秒", account.Account);
            await Task.Delay(delay * 1000, ct);
        }
    }

    /// <summary>顺序执行：逐账号登录后依次跑完单号玩法。</summary>
    private async Task<int> RunSequentialAsync(List<AccountEntry> targets,
        List<(ModeTaskConfig Task, GameModeDefinition Definition)> soloTasks, AppSettings settings, CancellationToken ct)
    {
        var failedModes = 0;

        for (var accountIndex = 0; accountIndex < targets.Count; accountIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var account = targets[accountIndex];
            CurrentAccount = account.Account;
            account.Status = AccountRunStatus.Running;
            account.StatusDetail = "";
            ProgressText = $"{CompletedAccounts + 1}/{TotalAccounts} 登录中：{account.Account}";
            _log.Info("开始处理账号", account.Account);

            var sessions = await _sessions.EnsureAsync([account], ct);
            var session = sessions.FirstOrDefault();
            if (session == null)
            {
                account.Status = AccountRunStatus.Failed;
                account.StatusDetail = "没有可用的游戏窗口";
                _log.Error("没有可用的游戏窗口，跳过该账号", account.Account);
                continue;
            }

            var login = await _login.LoginAsync(session, account, settings, ct);
            if (!login.Success)
            {
                account.Status = login.Status;
                account.StatusDetail = login.Message;
                continue;
            }

            if (settings.LowFrameRateEnabled)
            {
                await _frameRate.ApplyAsync(session, account, settings, ct);
            }

            foreach (var (task, definition) in soloTasks)
            {
                ct.ThrowIfCancellationRequested();
                var (open, reason) = ModeAvailabilityRules.Check(definition.Availability, DateTime.Now);
                if (!open)
                {
                    _log.Info($"跳过 {definition.Name}：{reason}", account.Account);
                    continue;
                }

                if (!_modes.HasFlow(definition))
                {
                    _log.Warn($"跳过 {definition.Name}：缺少 flows/{definition.Id}.json", account.Account);
                    continue;
                }

                for (var round = 1; round <= task.Rounds; round++)
                {
                    ct.ThrowIfCancellationRequested();
                    CurrentMode = $"{definition.Name} 第 {round}/{task.Rounds} 局";
                    ProgressText = $"{CompletedAccounts + 1}/{TotalAccounts} {account.Account} · {CurrentMode}";
                    var result = await RunRoundWithReconnectAsync(session, account, definition, task, round, true, settings, ct);
                    if (!result.Success)
                    {
                        failedModes++;
                        _log.Error($"{definition.Name} 第 {round} 局失败：{result.Message}", account.Account);
                        account.StatusDetail = $"{definition.Name} 第 {round} 局失败";
                        break;
                    }
                }
            }

            if (account.Status == AccountRunStatus.Running)
            {
                account.Status = AccountRunStatus.Completed;
            }

            CompletedAccounts++;
            ProgressText = $"{CompletedAccounts}/{TotalAccounts} 账号完成";

            // 顺序跑号时复用同一个客户端：没有空闲窗口且还有账号要跑，就先退出当前账号
            var isLastAccount = accountIndex == targets.Count - 1;
            if (!isLastAccount && settings.ReuseClientForNextAccount
                && !_sessions.Sessions.Any(s => s.BoundAccount == null))
            {
                await _logout.RunAsync(session, account, ct);
            }
        }

        return failedModes;
    }

    /// <summary>执行一局；识别到掉线时按配置重连后重试同一局。</summary>
    private async Task<ModeRunResult> RunRoundWithReconnectAsync(GameSession session, AccountEntry account,
        GameModeDefinition definition, ModeTaskConfig task, int round, bool isHost, AppSettings settings,
        CancellationToken ct)
    {
        var attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _modes.RunRoundAsync(session, account, definition, task, round, isHost, settings, ct);
            if (result.Success || result.Reason != FlowFailReasons.Disconnected)
            {
                return result;
            }

            attempts++;
            var max = Math.Clamp(settings.ReconnectMaxAttempts, 0, 5);
            if (attempts > max)
            {
                _log.Error($"掉线重连 {max} 次仍失败，放弃该账号", account.Account);
                return result;
            }

            var wait = Math.Clamp(settings.ReconnectDelaySeconds, 3, 300);
            _log.Warn($"检测到掉线，{wait} 秒后重连（{attempts}/{max}）", account.Account);
            await Task.Delay(wait * 1000, ct);

            session.Unbind();
            var login = await _login.LoginAsync(session, account, settings, ct);
            if (!login.Success)
            {
                _log.Error($"掉线重连失败：{login.Message}", account.Account);
                return ModeRunResult.Fail($"掉线重连失败：{login.Message}", FlowFailReasons.Disconnected);
            }
        }
    }

    /// <summary>执行组队玩法：按所需账号数分批，先全部登录再逐局执行。</summary>
    private async Task<int> RunTeamTasksAsync(List<(ModeTaskConfig Task, GameModeDefinition Definition)> teamTasks,
        List<AccountEntry> usable, AppSettings settings, CancellationToken ct)
    {
        var failedModes = 0;
        foreach (var (task, definition) in teamTasks)
        {
            ct.ThrowIfCancellationRequested();
            var (open, reason) = ModeAvailabilityRules.Check(definition.Availability, DateTime.Now);
            if (!open)
            {
                _log.Info($"跳过 {definition.Name}：{reason}", "组队");
                continue;
            }

            if (!_modes.HasFlow(definition))
            {
                _log.Warn($"跳过 {definition.Name}：缺少 flows/{definition.Id}.json", "组队");
                continue;
            }

            if (definition.RequiredAccounts > Math.Max(1, settings.MaxConcurrentInstances))
            {
                _log.Warn($"{definition.Name} 需要 {definition.RequiredAccounts} 个号，但最大同时登录数只有 "
                          + $"{settings.MaxConcurrentInstances}，已跳过该玩法", "组队");
                continue;
            }

            var size = Math.Max(2, definition.RequiredAccounts);
            var batches = usable.Chunk(size).Where(b => b.Length == size).ToList();
            if (batches.Count == 0)
            {
                _log.Warn($"{definition.Name} 需要 {size} 个账号同时在线，可用账号不足，已跳过", "组队");
                continue;
            }

            _log.Info($"{definition.Name}：共 {batches.Count} 组，每组 {size} 个账号", "组队");

            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var batch = batches[batchIndex];
                CurrentMode = $"{definition.Name} 第 {batchIndex + 1}/{batches.Count} 组";
                ProgressText = $"{definition.Name}：第 {batchIndex + 1} 组登录中";

                var sessions = await _sessions.EnsureAsync(batch, ct);
                if (sessions.Count < size)
                {
                    _log.Warn($"窗口不足（{sessions.Count}/{size}），跳过本组 {definition.Name}", "组队");
                    continue;
                }

                var ready = new List<GameSession>();
                foreach (var session in sessions)
                {
                    var account = session.BoundAccount;
                    if (account == null)
                    {
                        continue;
                    }

                    var login = await _login.LoginAsync(session, account, settings, ct);
                    if (!login.Success)
                    {
                        account.Status = login.Status;
                        account.StatusDetail = login.Message;
                        continue;
                    }

                    if (settings.LowFrameRateEnabled)
                    {
                        await _frameRate.ApplyAsync(session, account, settings, ct);
                    }

                    ready.Add(session);
                }

                if (ready.Count < size)
                {
                    _log.Warn($"成功登录 {ready.Count}/{size}，跳过本组 {definition.Name}", "组队");
                    continue;
                }

                for (var round = 1; round <= task.Rounds; round++)
                {
                    ct.ThrowIfCancellationRequested();
                    for (var index = 0; index < ready.Count; index++)
                    {
                        var session = ready[index];
                        var account = session.BoundAccount!;
                        CurrentAccount = account.Account;
                        CurrentMode = $"{definition.Name} 第 {round}/{task.Rounds} 局（{(index == 0 ? "房主" : "队员")}）";
                        ProgressText = $"{definition.Name} 第 {round} 局：{account.Account}";
                        var result = await RunRoundWithReconnectAsync(session, account, definition, task, round,
                            index == 0, settings, ct);
                        if (!result.Success)
                        {
                            failedModes++;
                            _log.Error($"{definition.Name} 第 {round} 局失败：{result.Message}", account.Account);
                            break;
                        }
                    }
                }
            }
        }

        return failedModes;
    }
}
