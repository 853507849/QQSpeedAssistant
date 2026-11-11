using System.Diagnostics;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 游戏窗口会话管理：优先复用已接管窗口，其次挂载新窗口，最后按配置自动启动客户端。
/// 多开玩法需要多个会话同时在线，这里按账号分配。
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly List<GameSession> _sessions = [];
    private readonly HashSet<int> _launchedProcessIds = [];

    /// <summary>构造会话管理器。</summary>
    public SessionManager(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>当前已接管的会话。</summary>
    public IReadOnlyList<GameSession> Sessions => _sessions;

    /// <summary>
    /// 确保给定账号都有可用会话；返回数量可能少于账号数量（窗口不足时）。
    /// </summary>
    public async Task<List<GameSession>> EnsureAsync(IReadOnlyList<AccountEntry> accounts, CancellationToken ct)
    {
        var result = new List<GameSession>();
        var settings = _config.Settings;

        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();

            // 1. 已有绑定该账号的会话直接复用
            var session = _sessions.FirstOrDefault(s => s.BoundAccount == account);
            if (session != null)
            {
                result.Add(session);
                continue;
            }

            // 2. 复用空闲会话
            session = _sessions.FirstOrDefault(s => s.BoundAccount == null);
            if (session != null)
            {
                session.Bind(account);
                result.Add(session);
                _log.Info($"复用空闲窗口（句柄 {session.Handle}）", account.Account);
                continue;
            }

            // 3. 挂载尚未接管的新窗口
            session = TryAttachNew();
            if (session != null)
            {
                session.Bind(account);
                result.Add(session);
                continue;
            }

            // 4. 按配置自动启动一个客户端
            if (settings.AutoLaunchGame)
            {
                if (!TryResolveGameExe(out _, out var launchError))
                {
                    _log.Error($"无法自动启动游戏：{launchError}", "窗口");
                }
                else if (await LaunchAsync(ct))
                {
                    session = TryAttachNew();
                    if (session != null)
                    {
                        session.Bind(account);
                        result.Add(session);
                        continue;
                    }
                }
            }

            _log.Error("没有可用的游戏窗口，且无法自动启动客户端（检查游戏路径与最大同时登录数）", account.Account);
        }

        return result;
    }

    /// <summary>释放某个账号的会话绑定。</summary>
    public void Release(AccountEntry account)
    {
        var session = _sessions.FirstOrDefault(s => s.BoundAccount == account);
        session?.Unbind();
    }

    /// <summary>释放全部会话。</summary>
    public void ReleaseAll()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    /// <summary>查找当前可接管的游戏窗口（已接管的除外），供界面显示状态使用。</summary>
    public IReadOnlyList<GameWindowInfo> FindWindows()
    {
        var excluded = _sessions.Select(s => s.Handle).ToHashSet();
        return WindowLocator.Find(_config.Settings, excluded, _launchedProcessIds);
    }

    /// <summary>
    /// 解析配置中的游戏路径：既支持直接填客户端 exe，也支持填安装目录（自动在目录内找主程序）。
    /// </summary>
    /// <param name="exePath">解析出的客户端 exe 全路径。</param>
    /// <param name="error">失败原因，成功时为空串。</param>
    public bool TryResolveGameExe(out string exePath, out string error)
    {
        exePath = "";
        error = "";

        var raw = _config.Settings.GameExePath?.Trim().Trim('"') ?? "";
        if (raw.Length == 0)
        {
            error = "还没有配置游戏主程序路径（可以选客户端 exe，也可以只选安装目录）";
            return false;
        }

        string expanded;
        try
        {
            expanded = Environment.ExpandEnvironmentVariables(raw);
        }
        catch
        {
            expanded = raw;
        }

        if (File.Exists(expanded))
        {
            if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                error = $"所选文件不是可执行程序：{expanded}";
                return false;
            }

            exePath = expanded;
            return true;
        }

        if (Directory.Exists(expanded))
        {
            var candidate = FindGameExe(expanded);
            if (candidate != null)
            {
                exePath = candidate;
                return true;
            }

            error = $"目录里没有唯一确定的客户端主程序，请直接选择 exe：{expanded}";
            return false;
        }

        error = $"路径不存在：{expanded}";
        return false;
    }

    /// <summary>在安装目录里找客户端主程序，优先名字带 QQSpeed / speed 的 exe。</summary>
    private static string? FindGameExe(string folder)
    {
        string[] excluded = ["unins", "uninst", "update", "repair", "crash", "report", "setup", "install", "launcher"];

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).ToList();
            if (files.Count == 0)
            {
                files = Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories)
                    .Take(400)
                    .ToList();
            }
        }
        catch
        {
            return null;
        }

        var candidates = files
            .Where(f => !excluded.Any(k => Path.GetFileName(f).Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var preferred = candidates.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains("QQSpeed", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains("speed", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains("game", StringComparison.OrdinalIgnoreCase));

        return preferred ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    /// <summary>挂载一个尚未被接管、且截图器可以启动的窗口。</summary>
    private GameSession? TryAttachNew()
    {
        var excluded = _sessions.Select(s => s.Handle).ToHashSet();
        var windows = WindowLocator.Find(_config.Settings, excluded, _launchedProcessIds);
        foreach (var window in windows)
        {
            var session = new GameSession(window, _config.Settings, _log);
            if (!session.EnsureCapture())
            {
                session.Dispose();
                continue;
            }

            _sessions.Add(session);
            if (WindowLocator.IsHelperProcess(window.ProcessName))
            {
                _log.Warn($"接管到的是启动器／更新器窗口「{window.Title}」（进程 {window.ProcessName}），登录流程会找不到登录框；请把游戏主程序改为客户端 exe（例如 QQSpeed.exe）", "窗口");
            }
            else
            {
                _log.Info($"已接管游戏窗口「{window.Title}」（进程 {window.ProcessName}，{window.Width}x{window.Height}）", "窗口");
            }

            return session;
        }

        return null;
    }

    /// <summary>启动一个新的客户端进程，并等待窗口出现。</summary>
    private async Task<bool> LaunchAsync(CancellationToken ct)
    {
        var settings = _config.Settings;
        if (!TryResolveGameExe(out var exePath, out var resolveError))
        {
            _log.Error($"游戏主程序不可用：{resolveError}", "窗口");
            return false;
        }

        if (_sessions.Count >= Math.Max(1, settings.MaxConcurrentInstances))
        {
            _log.Warn($"已达到最大同时登录数 {settings.MaxConcurrentInstances}，不再启动新客户端", "窗口");
            return false;
        }

        if (WindowLocator.LooksLikeLauncher(exePath))
        {
            _log.Warn($"启动的是启动器 {Path.GetFileName(exePath)}：程序只负责拉起进程并等待游戏窗口，"
                      + "不会去点启动器上的「开始游戏」；启动器多为单实例，已在运行时再点不会有任何新窗口", "窗口");
        }

        // 启动器基本都做了单实例：已经在运行时再启动一次不会有新窗口，先提示清楚，别让人以为程序卡住了。
        var exeName = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            if (exeName.Length > 0 && Process.GetProcessesByName(exeName).Length > 0)
            {
                _log.Warn($"{exeName} 已经在运行：重复启动通常不会开新窗口，这里改为等待现有窗口出现（必要时请手动点启动器上的「开始游戏」）", "窗口");
            }
        }
        catch
        {
            // 进程枚举失败不影响启动。
        }

        Process? process;
        try
        {
            _log.Info($"启动客户端：{exePath}", "窗口");
            process = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            });

            // 记住本次拉起的进程，窗口定位时即使关键字不匹配也能认出这个客户端。
            if (process != null)
            {
                _launchedProcessIds.Add(process.Id);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"客户端启动失败：{ex.Message}", "窗口");
            return false;
        }

        // 等待新窗口出现：定时打进度日志，否则界面上看起来就是「点了没反应」。
        var waitSeconds = Math.Max(30, settings.LaunchWaitSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        var startedAt = DateTime.UtcNow;
        var lastReported = 0;
        var exitLogged = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);
            if (FindWindows().Count > 0)
            {
                return true;
            }

            // 启动器常常拉起真正的客户端后自己立刻退出，所以退出本身不算失败，只记一次日志便于排查。
            var exitCode = TryGetExitCode(process);
            if (!exitLogged && exitCode != null)
            {
                exitLogged = true;
                _log.Info($"本次拉起的进程已退出（退出码 {exitCode}），继续等待游戏窗口出现", "窗口");
            }

            var elapsed = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
            if (elapsed - lastReported >= 15)
            {
                lastReported = elapsed;
                _log.Info($"仍在等待游戏窗口（已等 {elapsed}/{waitSeconds} 秒）：若启动器已在运行，请手动点启动器上的「开始游戏」", "窗口");
            }
        }

        _log.Warn($"等待客户端窗口超时（{waitSeconds} 秒），请确认客户端已窗口化启动", "窗口");
        return false;
    }

    /// <summary>安全读取进程退出码：进程还在跑或句柄已失效时返回 null。</summary>
    private static int? TryGetExitCode(Process? process)
    {
        try
        {
            return process is { HasExited: true } ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>释放全部会话。</summary>
    public void Dispose() => ReleaseAll();
}
