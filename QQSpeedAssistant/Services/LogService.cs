using System.Collections.ObjectModel;
using System.Windows;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 运行日志：同时写入界面集合与按天滚动的日志文件。
/// </summary>
public sealed class LogService
{
    private const int MaxEntries = 800;
    private readonly Lock _fileLock = new();
    private readonly string _logFile = Path.Combine(PathService.LogDir, $"run-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>界面绑定的日志集合，最多保留 800 条。</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>新增日志事件。</summary>
    public event Action<LogEntry>? EntryAdded;

    /// <summary>记录普通信息。</summary>
    public void Info(string message, string source = "") => Write(LogLevel.Info, message, source);

    /// <summary>记录成功信息。</summary>
    public void Success(string message, string source = "") => Write(LogLevel.Success, message, source);

    /// <summary>记录警告。</summary>
    public void Warn(string message, string source = "") => Write(LogLevel.Warning, message, source);

    /// <summary>记录错误。</summary>
    public void Error(string message, string source = "") => Write(LogLevel.Error, message, source);

    /// <summary>清空界面日志（不影响文件）。</summary>
    public void Clear()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(Entries.Clear));
            return;
        }

        Entries.Clear();
    }

    /// <summary>写入一条日志，必要时切换到界面线程。</summary>
    private void Write(LogLevel level, string message, string source)
    {
        var entry = new LogEntry { Level = level, Message = message, Source = source };
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Append(entry)));
        }
        else
        {
            Append(entry);
        }

        WriteFile(entry);
    }

    /// <summary>追加到界面集合并通知订阅者。</summary>
    private void Append(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }

        EntryAdded?.Invoke(entry);
    }

    /// <summary>追加到日志文件，失败不影响主流程。</summary>
    private void WriteFile(LogEntry entry)
    {
        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(_logFile, $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.LevelText}] [{entry.Source}] {entry.Message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写盘失败不影响自动化流程。
        }
    }
}
