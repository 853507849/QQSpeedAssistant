using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQSpeedAssistant.Services;

namespace QQSpeedAssistant.ViewModels;

/// <summary>一张奖励截图。</summary>
public sealed class RewardItemViewModel
{
    /// <summary>构造奖励条目并生成缩略图。</summary>
    public RewardItemViewModel(RewardItem item)
    {
        Item = item;
        Thumbnail = LoadBitmap(item.FilePath, 260);
    }

    /// <summary>原始记录。</summary>
    public RewardItem Item { get; }

    /// <summary>账号。</summary>
    public string Account => Item.Account;

    /// <summary>文件名。</summary>
    public string FileName => Item.FileName;

    /// <summary>玩法名。</summary>
    public string ModeName => string.IsNullOrEmpty(Item.ModeName) ? "奖励" : Item.ModeName;

    /// <summary>保存时间。</summary>
    public string CreatedText => Item.CreatedAt.ToString("MM-dd HH:mm:ss");

    /// <summary>文件路径。</summary>
    public string FilePath => Item.FilePath;

    /// <summary>缩略图。</summary>
    public BitmapImage? Thumbnail { get; }

    /// <summary>读取图片，失败返回 null。</summary>
    internal static BitmapImage? LoadBitmap(string path, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0)
            {
                image.DecodePixelWidth = decodeWidth;
            }

            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 奖励截图页：按账号浏览每个账号各自的奖励截图，支持大图预览。
/// </summary>
public sealed partial class RewardsPageViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// <summary>构造奖励页 ViewModel。</summary>
    public RewardsPageViewModel(AppServices services)
    {
        _services = services;
        Accounts = ["全部账号"];
        Refresh();
    }

    /// <summary>全部奖励条目。</summary>
    public ObservableCollection<RewardItemViewModel> AllItems { get; } = [];

    /// <summary>筛选后的条目。</summary>
    public ObservableCollection<RewardItemViewModel> VisibleItems { get; } = [];

    /// <summary>账号筛选选项。</summary>
    public ObservableCollection<string> Accounts { get; }

    /// <summary>当前账号筛选。</summary>
    [ObservableProperty]
    private string _selectedAccount = "全部账号";

    /// <summary>当前选中条目。</summary>
    [ObservableProperty]
    private RewardItemViewModel? _selectedItem;

    /// <summary>大图预览。</summary>
    [ObservableProperty]
    private ImageSource? _preview;

    /// <summary>统计文本。</summary>
    [ObservableProperty]
    private string _summaryText = "还没有奖励截图";

    /// <summary>账号变化时刷新筛选。</summary>
    partial void OnSelectedAccountChanged(string value) => ApplyFilter();

    /// <summary>选中条目时加载大图。</summary>
    partial void OnSelectedItemChanged(RewardItemViewModel? value)
        => Preview = value == null ? null : RewardItemViewModel.LoadBitmap(value.FilePath, 0);

    /// <summary>重新扫描奖励目录。</summary>
    [RelayCommand]
    private void Refresh()
    {
        var items = _services.Rewards.LoadAll();
        AllItems.Clear();
        foreach (var item in items)
        {
            AllItems.Add(new RewardItemViewModel(item));
        }

        var accounts = items.Select(i => i.Account).Distinct().OrderBy(a => a).ToList();
        var previous = SelectedAccount;
        Accounts.Clear();
        Accounts.Add("全部账号");
        foreach (var account in accounts)
        {
            Accounts.Add(account);
        }

        SelectedAccount = Accounts.Contains(previous) ? previous : "全部账号";
        ApplyFilter();
    }

    /// <summary>打开奖励目录。</summary>
    [RelayCommand]
    private void OpenFolder() => Open(PathService.RewardDir);

    /// <summary>在资源管理器中定位当前截图。</summary>
    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedItem == null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SelectedItem.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Log.Error($"打开截图失败：{ex.Message}", "界面");
        }
    }

    /// <summary>按账号过滤。</summary>
    private void ApplyFilter()
    {
        VisibleItems.Clear();
        var query = SelectedAccount == "全部账号"
            ? AllItems
            : AllItems.Where(i => i.Account == SelectedAccount);
        foreach (var item in query)
        {
            VisibleItems.Add(item);
        }

        var accounts = AllItems.Select(i => i.Account).Distinct().Count();
        SummaryText = AllItems.Count == 0
            ? "还没有奖励截图，完成玩法后会自动保存"
            : $"共 {AllItems.Count} 张截图，来自 {accounts} 个账号";
    }

    /// <summary>用资源管理器打开目录。</summary>
    private void Open(string path)
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
}
