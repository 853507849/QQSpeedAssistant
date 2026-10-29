using System.Diagnostics;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 自动关机：全部账号跑完后按倒计时关机，并提供取消入口避免误关机。
/// </summary>
public sealed class ShutdownService
{
    private readonly LogService _log;

    /// <summary>构造关机服务。</summary>
    public ShutdownService(LogService log)
    {
        _log = log;
    }

    /// <summary>发起关机倒计时。</summary>
    public bool Schedule(int delaySeconds, string reason)
    {
        var delay = Math.Clamp(delaySeconds, 10, 3600);
        try
        {
            RunShutdown($"/s /t {delay} /c \"{reason}\"");
            _log.Warn($"系统将在 {delay} 秒后关机（运行 shutdown /a 可取消）", "关机");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"自动关机命令执行失败：{ex.Message}", "关机");
            return false;
        }
    }

    /// <summary>发起重启倒计时（票完重启用）。</summary>
    public bool ScheduleRestart(int delaySeconds, string reason)
    {
        var delay = Math.Clamp(delaySeconds, 10, 3600);
        try
        {
            RunShutdown($"/r /t {delay} /c \"{reason}\"");
            _log.Warn($"系统将在 {delay} 秒后重启（运行 shutdown /a 可取消）", "关机");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"自动重启命令执行失败：{ex.Message}", "关机");
            return false;
        }
    }

    /// <summary>取消关机倒计时。</summary>
    public bool Cancel()
    {
        try
        {
            RunShutdown("/a");
            _log.Info("已取消关机倒计时", "关机");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"取消关机失败：{ex.Message}", "关机");
            return false;
        }
    }

    /// <summary>调用系统 shutdown 命令。</summary>
    private static void RunShutdown(string arguments)
    {
        Process.Start(new ProcessStartInfo("shutdown", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }
}
