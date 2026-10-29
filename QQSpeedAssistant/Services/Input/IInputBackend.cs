using Vanara.PInvoke;

namespace QQSpeedAssistant.Services.Input;

/// <summary>
/// 键鼠输入通道抽象。
/// 默认实现是系统 SendInput，容易被游戏反外挂过滤；Interception 实现走内核驱动，伪装成真实硬件输入。
/// </summary>
public interface IInputBackend : IDisposable
{
    /// <summary>通道名称，用于日志。</summary>
    string Name { get; }

    /// <summary>把光标移动到屏幕绝对坐标（物理像素）。</summary>
    void MoveMouseTo(int screenX, int screenY);

    /// <summary>在当前光标位置按下并抬起左键。</summary>
    void ClickLeft();

    /// <summary>按一次键。</summary>
    void KeyPress(User32.VK vk);

    /// <summary>按下按键（长按用）。</summary>
    void KeyDown(User32.VK vk);

    /// <summary>抬起按键。</summary>
    void KeyUp(User32.VK vk);

    /// <summary>组合键，例如 Ctrl+A。</summary>
    void ModifiedKeyStroke(IReadOnlyList<User32.VK> modifiers, User32.VK key);

    /// <summary>输入无法映射到虚拟键的字符（如中文），返回 false 表示该通道不支持。</summary>
    bool TryTypeUnicode(char ch);
}
