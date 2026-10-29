using System.Collections.ObjectModel;
using Newtonsoft.Json;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 配置读写：账号、玩法任务与运行设置分别保存在 User 目录下的三个 JSON 文件中。
/// 写入采用临时文件 + 覆盖的方式，避免异常退出时损坏配置。
/// </summary>
public sealed class ConfigService
{
    private readonly LogService _log;

    private readonly JsonSerializerSettings _jsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        ObjectCreationHandling = ObjectCreationHandling.Replace
    };

    /// <summary>构造配置服务。</summary>
    public ConfigService(LogService log)
    {
        _log = log;
        Settings = new AppSettings();
        Accounts = [];
        Tasks = [];
    }

    /// <summary>运行设置。</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>账号列表（界面直接绑定）。</summary>
    public ObservableCollection<AccountEntry> Accounts { get; }

    /// <summary>玩法任务配置（界面直接绑定）。</summary>
    public ObservableCollection<ModeTaskConfig> Tasks { get; }

    /// <summary>读取全部配置，缺失或损坏时回退到默认值。</summary>
    public void Load()
    {
        Settings = Read<AppSettings>(PathService.SettingsFile) ?? new AppSettings();

        var accounts = Read<List<AccountEntry>>(PathService.AccountsFile) ?? [];
        Accounts.Clear();
        var order = 1;
        foreach (var account in accounts.Where(a => !string.IsNullOrWhiteSpace(a.Account)))
        {
            account.Order = order++;
            account.Status = AccountRunStatus.NotStarted;
            Accounts.Add(account);
        }

        var savedTasks = Read<List<ModeTaskConfig>>(PathService.TasksFile) ?? [];
        Tasks.Clear();
        foreach (var definition in GameModeCatalog.All)
        {
            var saved = savedTasks.FirstOrDefault(t => t.ModeId == definition.Id);
            Tasks.Add(saved ?? new ModeTaskConfig
            {
                ModeId = definition.Id,
                Rounds = definition.DefaultRounds,
                CaptureReward = true
            });
        }

        _log.Info($"配置已加载：{Accounts.Count} 个账号，{Tasks.Count(t => t.Enabled)} 个玩法启用", "配置");
    }

    /// <summary>保存账号列表。</summary>
    public void SaveAccounts()
    {
        var order = 1;
        foreach (var account in Accounts)
        {
            account.Order = order++;
        }

        Write(PathService.AccountsFile, Accounts.ToList());
    }

    /// <summary>保存玩法任务配置。</summary>
    public void SaveTasks() => Write(PathService.TasksFile, Tasks.ToList());

    /// <summary>保存运行设置。</summary>
    public void SaveSettings() => Write(PathService.SettingsFile, Settings);

    /// <summary>保存全部配置。</summary>
    public void SaveAll()
    {
        SaveAccounts();
        SaveTasks();
        SaveSettings();
    }

    /// <summary>读取 JSON，损坏的文件会重命名为 .bak 并返回 null。</summary>
    private T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), _jsonSettings);
        }
        catch (Exception ex)
        {
            var backup = $"{path}.bak";
            try
            {
                File.Copy(path, backup, true);
            }
            catch
            {
                // 备份失败时忽略。
            }

            _log.Error($"配置文件解析失败，已使用默认值：{Path.GetFileName(path)}（{ex.Message}）", "配置");
            return null;
        }
    }

    /// <summary>原子写入 JSON。</summary>
    private void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = $"{path}.tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(value, _jsonSettings));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            _log.Error($"配置保存失败：{Path.GetFileName(path)}（{ex.Message}）", "配置");
        }
    }
}
