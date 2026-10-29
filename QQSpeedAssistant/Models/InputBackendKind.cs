namespace QQSpeedAssistant.Models;

/// <summary>键鼠输入通道。</summary>
public enum InputBackendKind
{
    /// <summary>优先用驱动级输入，不可用时回退到系统 SendInput。</summary>
    Auto = 0,

    /// <summary>只用系统 SendInput（权限一致即可，但可能被游戏反外挂过滤）。</summary>
    SendInput = 1,

    /// <summary>只用 Interception 驱动级输入（需要安装驱动并放置 interception.dll）。</summary>
    Interception = 2
}
