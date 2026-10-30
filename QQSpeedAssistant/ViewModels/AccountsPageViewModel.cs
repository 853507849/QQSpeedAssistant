using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQSpeedAssistant.Models;
using QQSpeedAssistant.Services;

namespace QQSpeedAssistant.ViewModels;

/// <summary>
/// 账号管理页：批量导入 <c>账号----密码----区服</c> 文本，支持启用、排序与删除。
/// </summary>
public sealed partial class AccountsPageViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// <summary>构造账号页 ViewModel。</summary>
    public AccountsPageViewModel(AppServices services)
    {
        _services = services;
        _services.Config.Accounts.CollectionChanged += OnAccountsChanged;
        foreach (var account in _services.Config.Accounts)
        {
            account.PropertyChanged += OnAccountPropertyChanged;
        }

        RefreshCounts();
    }

    /// <summary>账号集合，界面直接绑定。</summary>
    public ObservableCollection<AccountEntry> Accounts => _services.Config.Accounts;

    /// <summary>导入文本框内容。</summary>
    [ObservableProperty]
    private string _importText = "";

    /// <summary>区服下拉的选项，界面直接绑定。</summary>
    public IReadOnlyList<string> ServerChoices => ServerOptions.All;

    /// <summary>操作提示。</summary>
    [ObservableProperty]
    private string _statusMessage = "支持格式：2139444541----ds111200----电信区";

    /// <summary>当前选中账号。</summary>
    [ObservableProperty]
    private AccountEntry? _selectedAccount;

    /// <summary>启用账号数。</summary>
    [ObservableProperty]
    private int _enabledCount;

    /// <summary>解析并导入账号，已存在的账号会被更新密码与区服。</summary>
    [RelayCommand]
    private void Import()
    {
        var result = AccountTextParser.Parse(ImportText);
        if (result.Accounts.Count == 0 && result.Errors.Count == 0)
        {
            StatusMessage = "没有可导入的内容";
            return;
        }

        var added = 0;
        var updated = 0;
        foreach (var parsed in result.Accounts)
        {
            var existing = Accounts.FirstOrDefault(a => a.Account == parsed.Account);
            if (existing == null)
            {
                Accounts.Add(parsed);
                added++;
            }
            else
            {
                existing.Password = parsed.Password;
                existing.Server = ServerOptions.Normalize(parsed.Server);
                updated++;
            }
        }

        _services.Config.SaveAccounts();
        RefreshCounts();
        StatusMessage = $"导入完成：新增 {added} 个，更新 {updated} 个"
                        + (result.Errors.Count > 0 ? $"，{result.Errors.Count} 行格式错误" : "");
        foreach (var error in result.Errors.Take(5))
        {
            _services.Log.Warn(error, "账号");
        }

        ImportText = "";
    }

    /// <summary>手动添加一个空账号。</summary>
    [RelayCommand]
    private void AddBlank()
    {
        Accounts.Add(new AccountEntry
        {
            Account = "",
            Password = "",
            Server = ServerOptions.Default,
            Order = Accounts.Count + 1
        });
        _services.Config.SaveAccounts();
        RefreshCounts();
        StatusMessage = "已添加一行，请填写账号与密码";
    }

    /// <summary>删除指定账号。</summary>
    [RelayCommand]
    private void Remove(AccountEntry? account)
    {
        if (account == null)
        {
            return;
        }

        Accounts.Remove(account);
        _services.Config.SaveAccounts();
        RefreshCounts();
        StatusMessage = $"已删除账号 {account.Account}";
    }

    /// <summary>清空全部账号。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        Accounts.Clear();
        _services.Config.SaveAccounts();
        RefreshCounts();
        StatusMessage = "已清空账号列表";
    }

    /// <summary>上移账号顺序。</summary>
    [RelayCommand]
    private void MoveUp(AccountEntry? account)
    {
        if (account == null)
        {
            return;
        }

        var index = Accounts.IndexOf(account);
        if (index <= 0)
        {
            return;
        }

        Accounts.Move(index, index - 1);
        _services.Config.SaveAccounts();
    }

    /// <summary>下移账号顺序。</summary>
    [RelayCommand]
    private void MoveDown(AccountEntry? account)
    {
        if (account == null)
        {
            return;
        }

        var index = Accounts.IndexOf(account);
        if (index < 0 || index >= Accounts.Count - 1)
        {
            return;
        }

        Accounts.Move(index, index + 1);
        _services.Config.SaveAccounts();
    }

    /// <summary>保存账号配置。</summary>
    [RelayCommand]
    private void Save()
    {
        _services.Config.SaveAccounts();
        StatusMessage = $"已保存 {Accounts.Count} 个账号";
    }

    /// <summary>集合变化时维护事件订阅与计数。</summary>
    private void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (AccountEntry item in e.OldItems)
            {
                item.PropertyChanged -= OnAccountPropertyChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (AccountEntry item in e.NewItems)
            {
                item.PropertyChanged += OnAccountPropertyChanged;
            }
        }

        RefreshCounts();
    }

    /// <summary>账号启用状态变化时刷新计数。</summary>
    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountEntry.Enabled) or nameof(AccountEntry.Account))
        {
            RefreshCounts();
        }
    }

    /// <summary>刷新启用账号数。</summary>
    private void RefreshCounts() => EnabledCount = Accounts.Count(a => a.Enabled);
}
