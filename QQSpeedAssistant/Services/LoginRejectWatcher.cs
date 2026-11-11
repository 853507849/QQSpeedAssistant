namespace QQSpeedAssistant.Services;

/// <summary>找到的登录被拒提示框（屏幕坐标）。</summary>
public sealed record LoginRejectNotice(nint Handle, int ProcessId, string Title, int Left, int Top, int Width, int Height)
{
    /// <summary>屏幕坐标矩形。</summary>
    public System.Drawing.Rectangle Bounds => new(Left, Top, Width, Height);
}

/// <summary>
/// 检测「登录被拒」提示框。
///
/// 验证码点对之后，QQ 未必放行：风控拦下时会顶一个
/// 「提示：当前网络不稳定，登录失败。推荐使用手机QQ扫码登录。」的对话框。
/// 这个框盖在游戏窗口上，验证码窗口同时消失 —— 只看「验证码窗口还在不在」
/// 就会把它当成过码成功，然后带着一个根本没登录的客户端去跑玩法，模板全 0 相似度。
///
/// 它的正文是自绘的，读不到控件文字，所以按三条特征认：
/// 属于游戏进程 + 标题是「提示」这类 + 是对话框尺寸（比游戏主窗口小得多）。
/// </summary>
public static class LoginRejectWatcher
{
    /// <summary>提示类对话框的标题（全等匹配，避免命中游戏主窗口的名字）。</summary>
    private static readonly string[] Titles = { "提示", "警告", "错误", "安全提示", "腾讯游戏安全中心" };

    /// <summary>尺寸下限，用来滤掉零尺寸的隐藏辅助窗口。</summary>
    private const int MinWidth = 150;

    /// <summary>尺寸下限（高）。</summary>
    private const int MinHeight = 60;

    /// <summary>尺寸上限，超过这个尺寸的多半是游戏主窗口或内嵌网页。</summary>
    private const int MaxWidth = 1000;

    /// <summary>尺寸上限（高）。</summary>
    private const int MaxHeight = 700;

    /// <summary>
    /// 找登录被拒提示框。找不到返回 null。
    /// 先只看游戏进程；没找到再看全系统（有些腾讯弹窗挂在安全组件自己的进程里）。
    /// </summary>
    /// <param name="gameProcessId">游戏进程号；传 0 表示不限进程（只排除本程序自己）。</param>
    public static LoginRejectNotice? Find(int gameProcessId = 0)
    {
        var inGame = FindIn(gameProcessId, Titles);
        if (inGame != null || gameProcessId <= 0)
        {
            return inGame;
        }

        // 兜底：这个框也可能不是游戏主进程弹的。标题限制得更死一点，避免误抓别的软件。
        return FindIn(0, new[] { "提示", "警告" });
    }

    /// <summary>按进程和标题白名单找第一个匹配的窗口。</summary>
    private static LoginRejectNotice? FindIn(int gameProcessId, IReadOnlyCollection<string> titles)
    {
        foreach (var window in Scan(gameProcessId))
        {
            if (titles.Contains(window.Title, StringComparer.Ordinal))
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>
    /// 列出游戏进程下所有可见的小窗口（诊断用）。
    /// 提示框如果是独立窗口，这里一定能看到它，顺便能看到真实标题是什么。
    /// </summary>
    public static IReadOnlyList<LoginRejectNotice> Scan(int gameProcessId = 0)
    {
        var list = new List<LoginRejectNotice>();
        var selfPid = Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
                if ((int)pid == selfPid)
                {
                    return true;
                }

                if (gameProcessId > 0 && (int)pid != gameProcessId)
                {
                    return true;
                }

                if (!NativeMethods.GetWindowRect(hWnd, out var rect))
                {
                    return true;
                }

                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width < MinWidth || height < MinHeight || width > MaxWidth || height > MaxHeight)
                {
                    return true;
                }

                list.Add(new LoginRejectNotice(hWnd, (int)pid, NativeMethods.GetWindowTitle(hWnd).Trim(),
                    rect.Left, rect.Top, width, height));
                return true;
            }
            catch
            {
                return true;
            }
        }, nint.Zero);

        return list;
    }
}
