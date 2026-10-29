using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QQSpeedAssistant.Services;

namespace QQSpeedAssistant.ViewModels;

/// <summary>左侧导航项。</summary>
public sealed class NavigationItem
{
    /// <summary>构造导航项。</summary>
    public NavigationItem(string title, string glyph, object page)
    {
        Title = title;
        Glyph = glyph;
        Page = page;
    }

    /// <summary>标题。</summary>
    public string Title { get; }

    /// <summary>图标字符（Segoe Fluent Icons）。</summary>
    public string Glyph { get; }

    /// <summary>页面 ViewModel，由 DataTemplate 映射到具体视图。</summary>
    public object Page { get; }
}

/// <summary>主窗口 ViewModel：只负责导航与页面实例化。</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>构造主窗口 ViewModel。</summary>
    public MainWindowViewModel(AppServices services)
    {
        Dashboard = new DashboardPageViewModel(services);
        Accounts = new AccountsPageViewModel(services);
        Modes = new ModesPageViewModel(services);
        Settings = new SettingsPageViewModel(services);
        Rewards = new RewardsPageViewModel(services);
        Logs = new LogsPageViewModel(services);

        NavigationItems =
        [
            new NavigationItem("主页", "\uE80F", Dashboard),
            new NavigationItem("账号", "\uE716", Accounts),
            new NavigationItem("玩法任务", "\uE8FD", Modes),
            new NavigationItem("运行设置", "\uE713", Settings),
            new NavigationItem("奖励截图", "\uE722", Rewards),
            new NavigationItem("日志", "\uE7C3", Logs)
        ];

        SelectedNavigationItem = NavigationItems[0];
    }

    /// <summary>主页。</summary>
    public DashboardPageViewModel Dashboard { get; }

    /// <summary>账号页。</summary>
    public AccountsPageViewModel Accounts { get; }

    /// <summary>玩法任务页。</summary>
    public ModesPageViewModel Modes { get; }

    /// <summary>运行设置页。</summary>
    public SettingsPageViewModel Settings { get; }

    /// <summary>奖励截图页。</summary>
    public RewardsPageViewModel Rewards { get; }

    /// <summary>日志页。</summary>
    public LogsPageViewModel Logs { get; }

    /// <summary>导航项集合。</summary>
    public ObservableCollection<NavigationItem> NavigationItems { get; }

    /// <summary>当前选中导航项。</summary>
    [ObservableProperty]
    private NavigationItem? _selectedNavigationItem;

    /// <summary>窗口标题。</summary>
    public string Title => "QQ飞车任务助手";

    /// <summary>副标题。</summary>
    public string Subtitle => "多账号自动登录 · 玩法任务 · 奖励截图";
}
