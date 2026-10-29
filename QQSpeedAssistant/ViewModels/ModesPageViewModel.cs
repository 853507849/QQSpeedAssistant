using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQSpeedAssistant.Models;
using QQSpeedAssistant.Services;

namespace QQSpeedAssistant.ViewModels;

/// <summary>玩法任务列表中的一行。</summary>
public sealed partial class ModeTaskItemViewModel : ObservableObject
{
    private readonly FlowLoader _flows;

    /// <summary>构造任务行。</summary>
    public ModeTaskItemViewModel(GameModeDefinition definition, ModeTaskConfig config, FlowLoader flows)
    {
        Definition = definition;
        Config = config;
        _flows = flows;
    }

    /// <summary>玩法定义。</summary>
    public GameModeDefinition Definition { get; }

    /// <summary>任务配置，界面直接绑定编辑。</summary>
    public ModeTaskConfig Config { get; }

    /// <summary>玩法名称。</summary>
    public string Name => Definition.Name;

    /// <summary>分类。</summary>
    public string CategoryText => Definition.CategoryText;

    /// <summary>档期。</summary>
    public string AvailabilityText => Definition.AvailabilityText;

    /// <summary>人数要求。</summary>
    public string PlayerCountText => Definition.PlayerCountText;

    /// <summary>入口路径。</summary>
    public string EntryPath => Definition.EntryPath;

    /// <summary>补充说明。</summary>
    public string Note => Definition.Note;

    /// <summary>是否已配置流程。</summary>
    public bool HasFlow => _flows.Exists(Definition.Id) || _flows.Exists($"{Definition.Id}.member");

    /// <summary>流程状态文本。</summary>
    public string FlowState => HasFlow ? "已配置" : "未配置";

    /// <summary>是否为多开玩法。</summary>
    public bool IsTeamMode => Definition.IsTeamMode;

    /// <summary>流程文件变化后刷新状态显示。</summary>
    public void NotifyFlowStateChanged()
    {
        OnPropertyChanged(nameof(HasFlow));
        OnPropertyChanged(nameof(FlowState));
    }
}

/// <summary>
/// 玩法任务页：为每种玩法设置是否执行与局数，并展示入口路径与流程配置状态。
/// </summary>
public sealed partial class ModesPageViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// <summary>构造玩法任务页 ViewModel。</summary>
    public ModesPageViewModel(AppServices services)
    {
        _services = services;
        Categories = ["全部", .. new[] { ModeCategory.Racing, ModeCategory.Item, ModeCategory.Casual, ModeCategory.Team, ModeCategory.Ranked, ModeCategory.Event, ModeCategory.Border, ModeCategory.Pve }.Select(c => c.ToText())];

        foreach (var config in _services.Config.Tasks)
        {
            var definition = GameModeCatalog.Find(config.ModeId);
            if (definition == null)
            {
                continue;
            }

            AllItems.Add(new ModeTaskItemViewModel(definition, config, _services.Flows));
            config.PropertyChanged += (_, _) =>
            {
                _services.Config.SaveTasks();
                UpdateSummary();
            };
        }

        ApplyFilter();
        UpdateSummary();
    }

    /// <summary>全部玩法行。</summary>
    public ObservableCollection<ModeTaskItemViewModel> AllItems { get; } = [];

    /// <summary>按筛选条件展示的行。</summary>
    public ObservableCollection<ModeTaskItemViewModel> VisibleItems { get; } = [];

    /// <summary>分类筛选选项。</summary>
    public ObservableCollection<string> Categories { get; }

    /// <summary>当前分类。</summary>
    [ObservableProperty]
    private string _selectedCategory = "全部";

    /// <summary>是否只看已启用。</summary>
    [ObservableProperty]
    private bool _onlyEnabled;

    /// <summary>统计文本。</summary>
    [ObservableProperty]
    private string _summaryText = "";

    /// <summary>分类或筛选变化时刷新列表。</summary>
    partial void OnSelectedCategoryChanged(string value)
    {
        // 下拉控件绑定数据源时会把选中项推成 null，这里兜回「全部」，
        // 否则 SelectedCategory 为空会让下面的过滤把所有玩法都筛掉，界面看起来就是空表。
        if (string.IsNullOrWhiteSpace(value))
        {
            _services.Log.Info("玩法任务：分类筛选被清空，已恢复为「全部」", "界面");
            SelectedCategory = "全部";
            return;
        }

        ApplyFilter();
    }

    /// <summary>只看启用变化时刷新列表。</summary>
    partial void OnOnlyEnabledChanged(bool value) => ApplyFilter();

    /// <summary>全部启用。</summary>
    [RelayCommand]
    private void EnableAll()
    {
        foreach (var item in VisibleItems)
        {
            item.Config.Enabled = true;
        }

        UpdateSummary();
    }

    /// <summary>全部关闭。</summary>
    [RelayCommand]
    private void DisableAll()
    {
        foreach (var item in VisibleItems)
        {
            item.Config.Enabled = false;
        }

        UpdateSummary();
    }

    /// <summary>把局数重置为玩法默认值。</summary>
    [RelayCommand]
    private void ResetRounds()
    {
        foreach (var item in VisibleItems)
        {
            item.Config.Rounds = item.Definition.DefaultRounds;
        }

        UpdateSummary();
    }

    /// <summary>保存任务配置。</summary>
    [RelayCommand]
    private void Save() => _services.Config.SaveTasks();

    /// <summary>打开模板采集窗口，从游戏画面框选保存模板。</summary>
    [RelayCommand]
    private void CaptureTemplate()
    {
        var viewModel = new TemplateCaptureViewModel(_services);
        var window = new Views.Windows.TemplateCaptureWindow(viewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.ShowDialog();

        foreach (var item in AllItems)
        {
            item.NotifyFlowStateChanged();
        }
    }

    /// <summary>打开流程目录。</summary>
    [RelayCommand]
    private void OpenFlowFolder() => OpenFolder(PathService.FlowDir);

    /// <summary>打开模板目录。</summary>
    [RelayCommand]
    private void OpenTemplateFolder() => OpenFolder(PathService.TemplateDir);

    /// <summary>打开目录。</summary>
    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Log.Error($"打开目录失败：{ex.Message}", "界面");
        }
    }

    /// <summary>按分类与启用状态刷新可见列表。</summary>
    private void ApplyFilter()
    {
        IEnumerable<ModeTaskItemViewModel> query = AllItems;
        // 分类为空时按「全部」处理，宁可多显示也不要显示空表。
        if (!string.IsNullOrWhiteSpace(SelectedCategory) && SelectedCategory != "全部")
        {
            query = query.Where(i => i.CategoryText == SelectedCategory);
        }

        if (OnlyEnabled)
        {
            query = query.Where(i => i.Config.Enabled);
        }

        VisibleItems.Clear();
        foreach (var item in query)
        {
            VisibleItems.Add(item);
        }
    }

    /// <summary>刷新统计文本。</summary>
    private void UpdateSummary()
    {
        var enabled = AllItems.Where(i => i.Config.Enabled).ToList();
        var rounds = enabled.Sum(i => Math.Max(0, i.Config.Rounds));
        SummaryText = $"已启用 {enabled.Count} 个玩法，共 {rounds} 局；其中多开玩法 {enabled.Count(i => i.IsTeamMode)} 个";
    }
}
