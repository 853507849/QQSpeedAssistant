using System.Diagnostics;
using System.Security.Principal;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 管理员权限相关：游戏客户端通常以管理员身份运行，而 Windows 的 UIPI 会丢弃低权限进程发送的
/// 鼠标键盘消息，因此本程序也需要同样的权限，否则会“点了没反应、输入进不去”。
/// </summary>
public static class ElevationService
{
    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>以管理员身份重新启动本程序，成功返回 true（调用方随后应退出当前实例）。</summary>
    public static bool TryRestartAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = PathService.BaseDir
            });
            return true;
        }
        catch
        {
            // 用户取消了 UAC 提示。
            return false;
        }
    }
}
