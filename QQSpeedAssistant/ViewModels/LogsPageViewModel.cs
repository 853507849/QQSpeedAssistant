using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQSpeedAssistant.Models;
using QQSpeedAssistant.Services;

namespace QQSpeedAssistant.ViewModels;

/// <summary>日志页：显示全部运行日志并支持清空与打开日志目录。</summary>
public sealed partial class LogsPageViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// <summary>构造日志页 ViewModel。</summary>
    public LogsPageViewModel(AppServices services)
    {
        _services = services;
        Logs = _services.Log.Entries;
        Logs.CollectionChanged += OnLogsChanged;
        UpdateSummary();
    }

    /// <summary>日志集合，直接引用日志服务的集合。</summary>
    public ObservableCollection<LogEntry> Logs { get; }

    /// <summary>统计文本。</summary>
    [ObservableProperty]
    private string _summaryText = "";

    /// <summary>复制结果提示（「已复制 N 条」或失败原因），由视图在复制后写入。</summary>
    [ObservableProperty]
    private string _copyHint = "";

    /// <summary>清空界面日志。</summary>
    [RelayCommand]
    private void Clear() => _services.Log.Clear();

    /// <summary>打开日志目录。</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(PathService.LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PathService.LogDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Log.Error($"打开目录失败：{ex.Message}", "界面");
        }
    }

    /// <summary>集合变化时刷新统计。</summary>
    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSummary();

    /// <summary>刷新统计文本。</summary>
    private void UpdateSummary()
    {
        var errors = Logs.Count(l => l.Level == LogLevel.Error);
        SummaryText = $"共 {Logs.Count} 条日志，其中错误 {errors} 条";
    }
}
