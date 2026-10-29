using Fischless.WindowsInput;
using Vanara.PInvoke;

namespace QQSpeedAssistant.Services.Input;

/// <summary>系统级 SendInput 通道（原有实现，保留作为兜底）。</summary>
internal sealed class SendInputBackend : IInputBackend
{
    private readonly InputSimulator _input = new();

    /// <inheritdoc />
    public string Name => "SendInput";

    /// <summary>
    /// 移动光标：优先用 SendInput 的绝对移动（会生成 WM_INPUT 原始输入，游戏才认这是“真的动了鼠标”），
    /// 坐标按虚拟桌面归一化；万一系统没真的移动（被独占/剪裁），退回 SetCursorPos。
    /// </summary>
    public void MoveMouseTo(int screenX, int screenY)
    {
        var (nx, ny) = NativeMethods.ToVirtualDesktopAbsolute(screenX, screenY);
        _input.Mouse.MoveMouseToPositionOnVirtualDesktop(nx, ny);

        if (NativeMethods.GetCursorPos(out var actual)
            && Math.Abs(actual.X - screenX) <= 2 && Math.Abs(actual.Y - screenY) <= 2)
        {
            return;
        }

        NativeMethods.SetCursorPos(screenX, screenY);
    }

    /// <inheritdoc />
    public void ClickLeft() => _input.Mouse.LeftButtonClick();

    /// <inheritdoc />
    public void KeyPress(User32.VK vk) => _input.Keyboard.KeyPress(vk);

    /// <inheritdoc />
    public void KeyDown(User32.VK vk) => _input.Keyboard.KeyDown(vk);

    /// <inheritdoc />
    public void KeyUp(User32.VK vk) => _input.Keyboard.KeyUp(vk);

    /// <inheritdoc />
    public void ModifiedKeyStroke(IReadOnlyList<User32.VK> modifiers, User32.VK key) =>
        _input.Keyboard.ModifiedKeyStroke(modifiers, key);

    /// <inheritdoc />
    public bool TryTypeUnicode(char ch)
    {
        _input.Keyboard.TextEntry(ch);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // SendInput 无需释放资源。
    }
}
