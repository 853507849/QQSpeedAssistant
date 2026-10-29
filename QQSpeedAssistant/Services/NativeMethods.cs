using System.Runtime.InteropServices;
using System.Text;

namespace QQSpeedAssistant.Services;

/// <summary>矩形（客户区或窗口区域），坐标为像素。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Win32Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    /// <summary>宽度。</summary>
    public readonly int Width => Right - Left;

    /// <summary>高度。</summary>
    public readonly int Height => Bottom - Top;
}

/// <summary>屏幕坐标点。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Win32Point
{
    public int X;
    public int Y;
}

/// <summary>
/// 窗口相关的最小 Win32 声明，避免为少量调用引入额外依赖。
/// </summary>
public static class NativeMethods
{
    /// <summary>窗口枚举回调。</summary>
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(nint hWnd, out Win32Rect rect);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hWnd, out Win32Rect rect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(nint hWnd, ref Win32Point point);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Win32Point lpPoint);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nint extraInfo);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern nint SetActiveWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, nint extraInfo);

    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(nint hWnd, bool altTab);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool AdjustWindowRect(ref Win32Rect rect, uint style, bool menu);

    private const int GwlStyle = -16;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// 把窗口「客户区」调整到指定像素尺寸（左上角位置不变），返回调整后是否真的达到目标尺寸。
    /// 模板都按固定的客户区尺寸采集，运行时也把窗口调成同一尺寸，坐标才能一一对应。
    /// </summary>
    public static bool ResizeClient(nint hWnd, int clientWidth, int clientHeight)
    {
        if (hWnd == nint.Zero || clientWidth <= 0 || clientHeight <= 0)
        {
            return false;
        }

        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, 9); // SW_RESTORE
            Thread.Sleep(200);
        }

        if (GetClientRect(hWnd, out var current)
            && current.Width == clientWidth
            && current.Height == clientHeight)
        {
            return true;
        }

        if (!GetWindowRect(hWnd, out var windowRect))
        {
            return false;
        }

        // 客户区尺寸 → 外框尺寸：要把标题栏和边框算进去，否则调整完客户区还是小一圈。
        var frame = new Win32Rect { Left = 0, Top = 0, Right = clientWidth, Bottom = clientHeight };
        var style = (uint)GetWindowLong(hWnd, GwlStyle);
        if (!AdjustWindowRect(ref frame, style, false))
        {
            return false;
        }

        if (!SetWindowPos(hWnd, nint.Zero, windowRect.Left, windowRect.Top,
                frame.Width, frame.Height, SWP_NOZORDER | SWP_NOACTIVATE))
        {
            return false;
        }

        Thread.Sleep(300);
        if (GetClientRect(hWnd, out var after)
            && after.Width == clientWidth
            && after.Height == clientHeight)
        {
            return true;
        }

        // 样式读不准（自绘边框、跨位数窗口）时按实测差值再修一次。
        var deltaWidth = clientWidth - after.Width;
        var deltaHeight = clientHeight - after.Height;
        if (GetWindowRect(hWnd, out var retry))
        {
            SetWindowPos(hWnd, nint.Zero, retry.Left, retry.Top,
                retry.Width + deltaWidth, retry.Height + deltaHeight, SWP_NOZORDER | SWP_NOACTIVATE);
            Thread.Sleep(250);
        }

        return GetClientRect(hWnd, out var final)
               && final.Width == clientWidth
               && final.Height == clientHeight;
    }

    /// <summary>
    /// 还原并强制把窗口切到前台。
    /// Windows 默认禁止后台进程抢前台，这里依次用 ALT 解锁、AttachThreadInput、
    /// SwitchToThisWindow 三种手段，并轮询确认结果（实测单一 SetForegroundWindow 经常静默失败）。
    /// </summary>
    public static bool Activate(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return false;
        }

        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, 9); // SW_RESTORE
        }

        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (GetForegroundWindow() == hWnd)
            {
                return true;
            }

            // 模拟一次 ALT 按键，可绕过 Windows 的前台锁定限制
            keybd_event(VK_MENU, 0, 0, nint.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, nint.Zero);

            var targetThread = GetWindowThreadProcessId(hWnd, out _);
            var currentThread = GetCurrentThreadId();
            var attached = targetThread != 0
                           && targetThread != currentThread
                           && AttachThreadInput(currentThread, targetThread, true);

            try
            {
                // 先置顶再取消置顶，能强迫窗口管理器把窗口提到最前
                SetWindowPos(hWnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                SetWindowPos(hWnd, -2 /* HWND_NOTOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                SetActiveWindow(hWnd);

                if (GetForegroundWindow() != hWnd)
                {
                    SwitchToThisWindow(hWnd, true);
                }
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, targetThread, false);
                }
            }

            // 轮询确认，给窗口管理器切换的时间
            for (var poll = 0; poll < 4; poll++)
            {
                if (GetForegroundWindow() == hWnd)
                {
                    return true;
                }

                Thread.Sleep(80);
            }
        }

        return GetForegroundWindow() == hWnd;
    }

    /// <summary>读取窗口标题。</summary>
    public static string GetWindowTitle(nint hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    /// <summary>主屏宽度。</summary>
    public static int ScreenWidth => Math.Max(1, GetSystemMetrics(0));

    /// <summary>主屏高度。</summary>
    public static int ScreenHeight => Math.Max(1, GetSystemMetrics(1));

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>虚拟桌面左上角 X（多显示器时可能为负数）。</summary>
    public static int VirtualScreenLeft => GetSystemMetrics(SM_XVIRTUALSCREEN);

    /// <summary>虚拟桌面左上角 Y。</summary>
    public static int VirtualScreenTop => GetSystemMetrics(SM_YVIRTUALSCREEN);

    /// <summary>虚拟桌面宽度（所有显示器合并）。</summary>
    public static int VirtualScreenWidth => Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));

    /// <summary>虚拟桌面高度（所有显示器合并）。</summary>
    public static int VirtualScreenHeight => Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>
    /// 把屏幕物理坐标换算成 SendInput／Interception 要求的 0..65535 归一化值。
    /// 必须按「虚拟桌面」换算：只按主屏尺寸换算时，窗口在主屏之外或主屏不是唯一显示器就会整体偏移。
    /// </summary>
    public static (int X, int Y) ToVirtualDesktopAbsolute(int screenX, int screenY)
    {
        var spanX = Math.Max(1, VirtualScreenWidth - 1);
        var spanY = Math.Max(1, VirtualScreenHeight - 1);
        var nx = (int)Math.Round((screenX - VirtualScreenLeft) * 65535.0 / spanX);
        var ny = (int)Math.Round((screenY - VirtualScreenTop) * 65535.0 / spanY);
        return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
    }
}
