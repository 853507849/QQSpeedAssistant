using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;

namespace QQSpeedAssistant.Models;

/// <summary>
/// 一个游戏账号。配置文本格式为 <c>账号----密码----区服</c>，区服可为空。
/// </summary>
public sealed partial class AccountEntry : ObservableObject
{
    /// <summary>账号（QQ 号）。</summary>
    [ObservableProperty]
    private string _account = "";

    /// <summary>密码，仅保存在本地 User/accounts.json。</summary>
    [ObservableProperty]
    private string _password = "";

    /// <summary>区服，例如「电信」「联通」「电二」。</summary>
    [ObservableProperty]
    private string _server = "";

    /// <summary>备注。</summary>
    [ObservableProperty]
    private string _remark = "";

    /// <summary>是否参与本次运行。</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>运行顺序，从 1 开始。</summary>
    [ObservableProperty]
    private int _order = 1;

    /// <summary>本次运行状态（不落盘）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private AccountRunStatus _status = AccountRunStatus.NotStarted;

    /// <summary>本次运行状态说明（不落盘）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _statusDetail = "";

    /// <summary>列表展示用的账号文本。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Server) ? Account : $"{Account}（{Server}）";

    /// <summary>状态显示文本，带失败原因。</summary>
    [JsonIgnore]
    public string StatusText => string.IsNullOrEmpty(StatusDetail) ? Status.ToText() : $"{Status.ToText()}：{StatusDetail}";

    partial void OnStatusChanged(AccountRunStatus value) => OnPropertyChanged(nameof(StatusText));

    partial void OnStatusDetailChanged(string value) => OnPropertyChanged(nameof(StatusText));
}
