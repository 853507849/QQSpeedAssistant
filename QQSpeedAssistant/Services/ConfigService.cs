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

        // 日志开关从配置里读回来，不跟着走的话界面上关掉也照样记。
        AiRequestLogger.Enabled = Settings.AiRequestLogEnabled;

        // 保留天数同样要立刻生效：这里会顺带把过期日志清一遍（每天只跑一次）。
        LogRetention.Apply(Settings.AiLogRetainDays);

        var accounts = Read<List<AccountEntry>>(PathService.AccountsFile) ?? [];
        Accounts.Clear();
        var order = 1;
        var serverChanged = false;
        foreach (var account in accounts.Where(a => !string.IsNullOrWhiteSpace(a.Account)))
        {
            account.Order = order++;
            account.Status = AccountRunStatus.NotStarted;
            var before = account.Server;
            account.Server = NormalizeServer(account);
            serverChanged |= !string.Equals(before, account.Server, StringComparison.Ordinal);
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

        // 区服写法有改动才回写文件，免得每次启动都重复换算、也免得用户下次看到旧值。
        if (serverChanged)
        {
            SaveAccounts();
        }
    }

    /// <summary>
    /// 把账号上的区服折成「电信区／联通区／电信2区」三种标准写法。
    /// 账号页那一列是下拉框，存的必须是下拉里的值，否则界面上会显示成空白。
    /// 认不出的写法会归到默认区服并记一条警告，避免它静默失效。
    /// </summary>
    private string NormalizeServer(AccountEntry account)
    {
        var normalized = ServerOptions.Normalize(account.Server);
        if (normalized.Length == 0)
        {
            return "";
        }

        if (!ServerOptions.IsKnown(normalized))
        {
            _log.Warn($"账号 {account.Account} 的区服「{account.Server}」不在下拉选项"
                      + $"（{string.Join("／", ServerOptions.All)}）里，已归为「{ServerOptions.Default}」", "配置");
            return ServerOptions.Default;
        }

        if (!string.Equals(normalized, account.Server, StringComparison.Ordinal))
        {
            _log.Info($"账号 {account.Account} 的区服「{account.Server}」已统一写作「{normalized}」", "配置");
        }

        return normalized;
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
    public void SaveSettings()
    {
        Write(PathService.SettingsFile, Settings);

        // 用户把保留天数改小之后，希望马上看到效果，而不是等下次启动。
        // 天数没变时 Apply 内部会按「当天只跑一次」短路，不会反复扫目录。
        LogRetention.Apply(Settings.AiLogRetainDays);
    }

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
