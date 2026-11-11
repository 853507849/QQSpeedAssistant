namespace QQSpeedAssistant.Services;

/// <summary>
/// 统一管理程序使用的目录：配置、流程、模板、奖励截图与日志都集中在程序目录下，便于整体迁移。
/// </summary>
public static class PathService
{
    /// <summary>程序所在目录。</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>用户数据目录。</summary>
    public static string UserDir => Path.Combine(BaseDir, "User");

    /// <summary>流程定义目录。</summary>
    public static string FlowDir => Path.Combine(BaseDir, "flows");

    /// <summary>模板图片目录。</summary>
    public static string TemplateDir => Path.Combine(BaseDir, "templates");

    /// <summary>奖励截图目录。</summary>
    public static string RewardDir => Path.Combine(UserDir, "rewards");

    /// <summary>日志目录。</summary>
    public static string LogDir => Path.Combine(UserDir, "log");

    /// <summary>调试截图目录（失败截图、验证码识别现场、自检报告）。</summary>
    public static string DebugDir => Path.Combine(UserDir, "debug");

    /// <summary>账号配置文件。</summary>
    public static string AccountsFile => Path.Combine(UserDir, "accounts.json");

    /// <summary>玩法任务配置文件。</summary>
    public static string TasksFile => Path.Combine(UserDir, "tasks.json");

    /// <summary>运行设置配置文件。</summary>
    public static string SettingsFile => Path.Combine(UserDir, "settings.json");

    /// <summary>创建运行所需目录。</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(UserDir);
        Directory.CreateDirectory(FlowDir);
        Directory.CreateDirectory(TemplateDir);
        Directory.CreateDirectory(RewardDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(DebugDir);
    }

    /// <summary>把账号、玩法名等文本转换为合法目录名。</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrEmpty(result) ? "unknown" : result;
    }
}
