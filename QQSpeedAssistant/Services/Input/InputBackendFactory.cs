using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services.Input;

/// <summary>按设置创建输入通道，并在不可用时安全回退。</summary>
public static class InputBackendFactory
{
    /// <summary>当前实际可用的通道名称，供界面显示。</summary>
    public static string DescribeAvailability(AppSettings settings) =>
        InterceptionBackend.IsAvailable(settings.InterceptionDllPath)
            ? "Interception 驱动可用"
            : "未检测到 Interception 驱动（当前只能用 SendInput，游戏可能过滤）";

    /// <summary>创建通道。</summary>
    public static IInputBackend Create(AppSettings settings, LogService log)
    {
        if (settings.InputBackend == InputBackendKind.SendInput)
        {
            log.Info("输入通道：SendInput（系统模拟）", "输入");
            return new SendInputBackend();
        }

        var interception = InterceptionBackend.TryCreate(settings.InterceptionDllPath, log);
        if (interception != null)
        {
            log.Info("输入通道：Interception（驱动级，伪装真实硬件）", "输入");
            return interception;
        }

        if (settings.InputBackend == InputBackendKind.Interception)
        {
            log.Error("已在设置中选择 Interception，但驱动不可用，本次回退到 SendInput", "输入");
        }
        else
        {
            log.Warn("输入通道：SendInput（未检测到 Interception 驱动）。带反外挂的客户端会过滤 SendInput，" +
                     "表现为「点不动、输不进」", "输入");
        }

        log.Warn($"安装 Interception：1) 运行 install-interception.exe /install 后重启系统；" +
                 $"2) 把 interception.dll（x64）放到 {PathService.BaseDir}；" +
                 "3) 运行设置里把输入通道改成 Interception", "输入");

        return new SendInputBackend();
    }
}
