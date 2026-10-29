using System.Diagnostics;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>一个可接管的游戏窗口。</summary>
public sealed record GameWindowInfo(nint Handle, string Title, int ProcessId, string ProcessName, int Width, int Height);

/// <summary>
/// 按进程名与窗口标题定位 QQ飞车客户端窗口，多开时会有多个结果。
/// </summary>
public static class WindowLocator
{
    /// <summary>
    /// 查找当前所有可见的游戏窗口。
    /// </summary>
    /// <param name="settings">用于取进程名与标题关键字。</param>
    /// <param name="excludedHandles">已被其他会话占用的窗口句柄。</param>
    /// <param name="extraProcessIds">由本程序启动的客户端进程号，即使关键字不匹配也视为游戏窗口。</param>
    public static IReadOnlyList<GameWindowInfo> Find(
        AppSettings settings,
        IReadOnlyCollection<nint>? excludedHandles = null,
        IReadOnlyCollection<int>? extraProcessIds = null)
    {
        var result = new List<GameWindowInfo>();
        var helperWindows = new List<GameWindowInfo>();
        var processKeyword = settings.ProcessNameKeyword?.Trim() ?? "";
        var titleKeyword = settings.WindowTitleKeyword?.Trim() ?? "";
        var exeKeyword = ResolveExeKeyword(settings);
        var selfPid = Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                {
                    return true;
                }

                if (excludedHandles != null && excludedHandles.Contains(hWnd))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0 || (int)pid == selfPid)
                {
                    return true;
                }

                NativeMethods.GetClientRect(hWnd, out var client);
                if (client.Width < 640 || client.Height < 360)
                {
                    return true;
                }

                var title = NativeMethods.GetWindowTitle(hWnd);
                var processName = "";
                try
                {
                    processName = Process.GetProcessById((int)pid).ProcessName;
                }
                catch
                {
                    // 进程可能已退出，忽略标题以外的匹配。
                }

                // 更新器、启动器、崩溃报告之类的辅助进程不算游戏窗口，避免接管到错误的窗口。
                var helper = IsHelperProcess(processName);
                var processMatched = !helper
                                     && processKeyword.Length > 0
                                     && processName.Contains(processKeyword, StringComparison.OrdinalIgnoreCase);
                var titleMatched = titleKeyword.Length > 0
                                   && title.Contains(titleKeyword, StringComparison.OrdinalIgnoreCase);

                // 用户在界面上选过客户端主程序时，按该 exe 的进程名再匹配一次，省去手动改关键字。
                var exeMatched = !helper
                                 && exeKeyword.Length > 0
                                 && processName.Contains(exeKeyword, StringComparison.OrdinalIgnoreCase);
                var launchedMatched = extraProcessIds != null && extraProcessIds.Contains((int)pid);
                if (!processMatched && !titleMatched && !exeMatched && !launchedMatched)
                {
                    return true;
                }

                var info = new GameWindowInfo(hWnd, title, (int)pid, processName, client.Width, client.Height);
                if (helper)
                {
                    helperWindows.Add(info);
                }
                else
                {
                    result.Add(info);
                }
            }
            catch
            {
                // 枚举过程中窗口可能销毁，直接跳过。
            }

            return true;
        }, nint.Zero);

        // 真正的游戏窗口排前面；只找到启动器/更新器窗口时才退而使用它（并由调用方给出警告）。
        if (result.Count == 0)
        {
            result.AddRange(helperWindows);
        }

        return result;
    }

    /// <summary>启动器、更新器等辅助进程名关键字，这些窗口不应被当成游戏画面。</summary>
    private static readonly string[] HelperProcessKeywords =
        ["launcher", "update", "uninst", "repair", "crash", "report", "setup", "install"];

    /// <summary>判断是否为辅助进程（启动器／更新器／安装器）。</summary>
    public static bool IsHelperProcess(string processName) =>
        processName.Length > 0
        && HelperProcessKeywords.Any(k => processName.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>判断配置的路径是否指向启动器／更新器而不是游戏主程序。</summary>
    public static bool LooksLikeLauncher(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return IsHelperProcess(Path.GetFileNameWithoutExtension(path.Trim().Trim('"')));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从配置的主程序路径推导进程名关键字，路径为目录或为空时返回空串。</summary>
    private static string ResolveExeKeyword(AppSettings settings)
    {
        try
        {
            var configured = settings.GameExePath?.Trim().Trim('"') ?? "";
            if (configured.Length > 0 && File.Exists(configured))
            {
                return Path.GetFileNameWithoutExtension(configured);
            }
        }
        catch
        {
            // 路径非法时忽略，回退到关键字匹配。
        }

        return "";
    }

    /// <summary>按句柄取客户区矩形。</summary>
    public static Win32Rect GetClientRect(nint hWnd)
    {
        NativeMethods.GetClientRect(hWnd, out var rect);
        return rect;
    }

    /// <summary>把客户区坐标转换为屏幕坐标。</summary>
    public static Win32Point ClientToScreen(nint hWnd, int x, int y)
    {
        var point = new Win32Point { X = x, Y = y };
        NativeMethods.ClientToScreen(hWnd, ref point);
        return point;
    }
}
