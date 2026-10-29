using Microsoft.Win32;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 开机自启：把本程序写入 HKCU 的 Run 项（不需要管理员，也不用建计划任务）。
/// 带 <c>--autostart</c> 参数启动时，界面会按设置决定是否直接开始白票。
/// </summary>
public sealed class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QQSpeedAssistant";

    /// <summary>开机自启时附加的命令行参数。</summary>
    public const string AutoStartArgument = "--autostart";

    private readonly LogService _log;

    /// <summary>构造开机自启服务。</summary>
    public AutoStartService(LogService log)
    {
        _log = log;
    }

    /// <summary>本程序的启动命令行（含自启参数，路径带引号）。</summary>
    public static string Command
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                exe = Path.Combine(PathService.BaseDir, "QQSpeedAssistant.exe");
            }

            return $"\"{exe}\" {AutoStartArgument}";
        }
    }

    /// <summary>当前是否已经写入开机自启。</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按设置开启或关闭开机自启，返回是否成功。</summary>
    public bool Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key == null)
            {
                _log.Error("写入开机自启失败：无法打开注册表 Run 项", "自启");
                return false;
            }

            if (enable)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
                _log.Info($"已设置开机自启：{Command}", "自启");
            }
            else
            {
                if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName, false);
                    _log.Info("已取消开机自启", "自启");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"设置开机自启失败：{ex.Message}", "自启");
            return false;
        }
    }

    /// <summary>让注册表状态与设置保持一致（启动时调用一次即可）。</summary>
    public void SyncFromSettings(bool enabled)
    {
        var current = IsEnabled();
        if (current == enabled)
        {
            return;
        }

        // 只在「设置要求开但没写」或「设置要求关但还留着」时动注册表，
        // 避免每次启动都重写一遍键值。
        Apply(enabled);
    }
}
