using Newtonsoft.Json;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 从 flows 目录读取流程定义。流程名与玩法 Id 或固定流程名（login、lower_fps）对应。
/// </summary>
public sealed class FlowLoader
{
    private readonly LogService _log;

    /// <summary>构造流程加载器。</summary>
    public FlowLoader(LogService log)
    {
        _log = log;
    }

    /// <summary>流程目录。</summary>
    public string Directory => PathService.FlowDir;

    /// <summary>流程文件路径（不含扩展名）。</summary>
    public string GetPath(string name) => Path.Combine(Directory, $"{name}.json");

    /// <summary>流程是否存在。</summary>
    public bool Exists(string name) => File.Exists(GetPath(name));

    /// <summary>读取流程，不存在或解析失败返回 null。</summary>
    public AutomationFlow? Load(string name)
    {
        var path = GetPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var flow = JsonConvert.DeserializeObject<AutomationFlow>(File.ReadAllText(path));
            if (flow == null || flow.Steps.Count == 0)
            {
                _log.Warn($"流程文件没有步骤：{Path.GetFileName(path)}", "流程");
                return null;
            }

            flow.Name = string.IsNullOrEmpty(flow.Name) ? name : flow.Name;
            return flow;
        }
        catch (Exception ex)
        {
            _log.Error($"流程解析失败：{Path.GetFileName(path)}（{ex.Message}）", "流程");
            return null;
        }
    }

    /// <summary>列出已存在的流程名。</summary>
    public IReadOnlyList<string> ListNames()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory
            .EnumerateFiles(Directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();
    }
}
