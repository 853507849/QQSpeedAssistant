using System.Runtime.InteropServices;
using Vanara.PInvoke;

namespace QQSpeedAssistant.Services.Input;

/// <summary>
/// Interception 驱动级输入：通过内核过滤驱动注入键鼠，事件在游戏看来与真实硬件一致，
/// 可绕过只过滤 SendInput 的反外挂。
/// 使用前需要：安装 Interception 驱动（install-interception.exe /install）并重启，
/// 且 interception.dll 与程序同目录（或在设置里指定路径）。
/// </summary>
internal sealed class InterceptionBackend : IInputBackend
{
    // 驱动里的设备号定义（interception.h）：
    //   INTERCEPTION_KEYBOARD(index) = index + 1        → 0x01 ~ 0x0A
    //   INTERCEPTION_MOUSE(index)    = 10 + index + 1   → 0x0B ~ 0x14
    // 0x00 是无效设备，所以这里不能从 0 开始，否则鼠标事件会被当成键盘发出去（send 直接返回 0）。

    /// <summary>第一个键盘设备（INTERCEPTION_KEYBOARD(0) = 0x01）。</summary>
    private const ushort KeyboardDevice = 0x01;

    /// <summary>第一个鼠标设备（INTERCEPTION_MOUSE(0) = 0x0B）。</summary>
    private const ushort MouseDevice = 0x0B;

    private const ushort KeyDownState = 0x0000;
    private const ushort KeyUpState = 0x0001;
    private const ushort KeyExtended = 0x0002;

    private const ushort MouseAbsolute = 0x0001;
    private const ushort MouseVirtualDesktop = 0x0002;
    private const ushort MouseLeftDown = 0x0001;
    private const ushort MouseLeftUp = 0x0002;

    private static readonly HashSet<User32.VK> ExtendedKeys =
    [
        User32.VK.VK_INSERT, User32.VK.VK_DELETE, User32.VK.VK_HOME, User32.VK.VK_END,
        User32.VK.VK_PRIOR, User32.VK.VK_NEXT, User32.VK.VK_LEFT, User32.VK.VK_RIGHT,
        User32.VK.VK_UP, User32.VK.VK_DOWN, User32.VK.VK_RCONTROL, User32.VK.VK_RMENU,
        User32.VK.VK_LWIN, User32.VK.VK_RWIN, User32.VK.VK_DIVIDE, User32.VK.VK_NUMLOCK,
        User32.VK.VK_SNAPSHOT, User32.VK.VK_APPS
    ];

    private nint _context;
    private LogService? _log;
    private bool _sendFailedLogged;
    private bool _sendResultLogged;
    private ushort _keyboardDevice = KeyboardDevice;
    private ushort _mouseDevice = MouseDevice;

    private InterceptionBackend(nint context, LogService? log)
    {
        _context = context;
        _log = log;
    }

    /// <inheritdoc />
    public string Name => "Interception";

    /// <summary>原生结构体尺寸自检：键鼠结构体必须与驱动的 C 定义完全一致（8／20 字节）。</summary>
    public static string DescribeLayout() =>
        $"KeyStroke {Marshal.SizeOf<KeyStroke>()} 字节（应为 8）／MouseStroke {Marshal.SizeOf<MouseStroke>()} 字节（应为 20）";

    /// <summary>尝试创建通道；驱动未安装或 DLL 缺失时返回 null。</summary>
    public static InterceptionBackend? TryCreate(string? dllPath, LogService? log = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(dllPath) && File.Exists(dllPath))
            {
                // 先按显式路径加载；同名模块加载后 DllImport 会复用该模块。
                NativeLibrary.Load(dllPath);
            }

            var context = interception_create_context();
            if (context == nint.Zero || context == -1)
            {
                log?.Warn("Interception 上下文创建失败：驱动可能未安装或未重启", "输入");
                return null;
            }

            var backend = new InterceptionBackend(context, log);
            backend.ProbeDevices();
            return backend;
        }
        catch (DllNotFoundException)
        {
            log?.Warn("未找到 interception.dll：请把 DLL 放到程序目录，或在设置里指定路径", "输入");
            return null;
        }
        catch (Exception ex)
        {
            log?.Warn($"Interception 初始化失败：{ex.Message}", "输入");
            return null;
        }
    }

    /// <summary>驱动与 DLL 是否可用（用于界面状态展示，不抛异常）。</summary>
    public static bool IsAvailable(string? dllPath)
    {
        var backend = TryCreate(dllPath);
        if (backend == null)
        {
            return false;
        }

        backend.Dispose();
        return true;
    }

    /// <summary>
    /// 探测驱动上报的可用设备并选第一个可用的键鼠（有的机器鼠标不在 0 号设备上）。
    /// 结果写进日志，便于判断“发送成功但游戏没反应”是不是发到了不存在的设备。
    /// </summary>
    private void ProbeDevices()
    {
        var keyboards = new List<ushort>();
        var mice = new List<ushort>();
        for (var index = 0; index < 10; index++)
        {
            var device = (ushort)(KeyboardDevice + index);
            if (interception_is_invalid(device) == 0)
            {
                keyboards.Add(device);
            }
        }

        for (var index = 0; index < 10; index++)
        {
            var device = (ushort)(MouseDevice + index);
            if (interception_is_invalid(device) == 0)
            {
                mice.Add(device);
            }
        }

        if (keyboards.Count > 0)
        {
            _keyboardDevice = keyboards[0];
        }

        if (mice.Count > 0)
        {
            _mouseDevice = mice[0];
        }

        _log?.Info($"Interception 设备：键盘 {(keyboards.Count > 0 ? string.Join("、", keyboards.Select(d => $"0x{d:X2}")) : "无")}；" +
                   $"鼠标 {(mice.Count > 0 ? string.Join("、", mice.Select(d => $"0x{d:X2}")) : "无")}；" +
                   $"实际使用 键盘 0x{_keyboardDevice:X2}／鼠标 0x{_mouseDevice:X2}", "输入");
    }

    /// <summary>按相对位移移动光标（绝对注入无效时的备选路径）。</summary>
    public void MoveMouseBy(int dx, int dy) =>
        SendMouse(new MouseStroke { State = 0, Flags = 0, Rolling = 0, X = dx, Y = dy, Information = 0 });

    /// <inheritdoc />
    public void MoveMouseTo(int screenX, int screenY)
    {
        // 绝对移动必须按「虚拟桌面」归一化（MouseVirtualDesktop 标志），
        // 只按主屏尺寸换算会在多显示器/非主屏窗口上整体偏移几十像素。
        var (nx, ny) = NativeMethods.ToVirtualDesktopAbsolute(screenX, screenY);

        var stroke = new MouseStroke
        {
            State = 0,
            Flags = MouseAbsolute | MouseVirtualDesktop,
            Rolling = 0,
            X = nx,
            Y = ny,
            Information = 0
        };

        SendMouse(stroke);
    }

    /// <summary>
    /// 直接用 0..65535 归一化坐标做绝对移动。坐标的真实映射由系统决定（多显示器／缩放下可能与
    /// 我们算的不完全一致），所以调用方通常配合「测量误差再修正」的闭环使用。
    /// </summary>
    public void MoveMouseToNormalized(int normalizedX, int normalizedY) =>
        SendMouse(new MouseStroke
        {
            State = 0,
            Flags = MouseAbsolute | MouseVirtualDesktop,
            Rolling = 0,
            X = Math.Clamp(normalizedX, 0, 65535),
            Y = Math.Clamp(normalizedY, 0, 65535),
            Information = 0
        });

    /// <inheritdoc />
    public void ClickLeft()
    {
        // 按钮事件用相对移动（0,0）发送，避免把光标挪到屏幕左上角。
        SendMouse(new MouseStroke { State = MouseLeftDown, Flags = 0, X = 0, Y = 0 });
        Thread.Sleep(30);
        SendMouse(new MouseStroke { State = MouseLeftUp, Flags = 0, X = 0, Y = 0 });
    }

    /// <inheritdoc />
    public void KeyPress(User32.VK vk)
    {
        KeyDown(vk);
        Thread.Sleep(20);
        KeyUp(vk);
    }

    /// <inheritdoc />
    public void KeyDown(User32.VK vk) => SendKey(vk, down: true);

    /// <inheritdoc />
    public void KeyUp(User32.VK vk) => SendKey(vk, down: false);

    /// <inheritdoc />
    public void ModifiedKeyStroke(IReadOnlyList<User32.VK> modifiers, User32.VK key)
    {
        foreach (var modifier in modifiers)
        {
            KeyDown(modifier);
            Thread.Sleep(15);
        }

        KeyPress(key);

        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            Thread.Sleep(15);
            KeyUp(modifiers[i]);
        }
    }

    /// <inheritdoc />
    public bool TryTypeUnicode(char ch) => false;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_context != nint.Zero)
        {
            interception_destroy_context(_context);
            _context = nint.Zero;
        }
    }

    private void SendKey(User32.VK vk, bool down)
    {
        if (_context == nint.Zero)
        {
            return;
        }

        var state = (ushort)(down ? KeyDownState : KeyUpState);
        if (ExtendedKeys.Contains(vk))
        {
            state |= KeyExtended;
        }

        // 驱动要的是「硬件扫描码」，不是虚拟键码。直接塞 VK（例如 'A' = 0x41）会被系统当成
        // 另一个键的扫描码，表现为打出来的字符完全不对甚至没反应。
        var scan = (ushort)MapVirtualKey((uint)vk, MapVkToVsc);
        if (scan == 0)
        {
            scan = (ushort)vk;
        }

        var stroke = new KeyStroke { Code = scan, State = state, Information = 0 };
        Send(_keyboardDevice, stroke, Marshal.SizeOf<KeyStroke>());
    }

    private void SendMouse(MouseStroke stroke) => Send(_mouseDevice, stroke, Marshal.SizeOf<MouseStroke>());

    private void Send<T>(ushort device, T stroke, int size) where T : struct
    {
        if (_context == nint.Zero)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(stroke, buffer, false);
            var sent = interception_send(_context, device, buffer, 1);
            if (!_sendResultLogged)
            {
                _sendResultLogged = true;
                _log?.Info($"Interception 首次发送：设备 0x{device:X2}，返回 {sent}（1 表示成功），结构体 {size} 字节", "输入");
            }

            if (sent == 0 && !_sendFailedLogged)
            {
                _sendFailedLogged = true;
                _log?.Error("Interception 发送失败：驱动未正确安装或设备被其他程序独占", "输入");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KeyStroke
    {
        public ushort Code;
        public ushort State;
        public uint Information;
    }

    /// <summary>
    /// 必须与驱动里的 <c>InterceptionMouseStroke</c> 完全一致：C 结构体在 rolling 之后有 2 字节对齐填充，
    /// 总长 20 字节。这里不能写 Pack = 1（那样只有 18 字节，X/Y 会整体错位 2 字节，
    /// 驱动读到的坐标是垃圾值，表现为“光标乱跳、点了没反应”）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseStroke
    {
        public ushort State;
        public ushort Flags;
        public short Rolling;
        public int X;
        public int Y;
        public uint Information;
    }

    /// <summary>MapVirtualKey 的「虚拟键码 → 扫描码」转换方式。</summary>
    private const uint MapVkToVsc = 0;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint interception_create_context();

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void interception_destroy_context(nint context);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int interception_send(nint context, ushort device, nint stroke, uint nstroke);

    /// <summary>设备号是否无效（返回非 0 表示无效）。</summary>
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int interception_is_invalid(int device);
}
