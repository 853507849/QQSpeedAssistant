using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using QQSpeedAssistant.Models;
using QQSpeedAssistant.Services;
using QQSpeedAssistant.Services.Input;

namespace QQSpeedAssistant.ViewModels;

/// <summary>
/// 运行设置页：起始账号、密码重试、定时上号、自动关机、降帧与窗口参数。
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// <summary>构造设置页 ViewModel。</summary>
    public SettingsPageViewModel(AppServices services)
    {
        _services = services;
        CaptureModes = ["BitBlt", "WindowsGraphicsCapture", "WindowsGraphicsCaptureV2"];
        Settings.PropertyChanged += (_, e) => OnSettingChanged(e.PropertyName);
        PresetNames = _services.Presets.Names;
        _selectedPreset = PresetNames.FirstOrDefault() ?? "";

        _selectedInputBackend = InputBackendOptions.First(o => o.Value == Settings.InputBackend);
        _inputBackendStatus = InputBackendFactory.DescribeAvailability(Settings);
        _selectedFinishAction = FinishActionOptions.First(o => o.Value == Settings.FinishAction);
        // 旧版本存的是「电信」「联通」「电二」这类写法，读回来先换成下拉里的标准值。
        var savedServer = Models.ServerOptions.Normalize(Settings.DefaultServer);
        _selectedServer = string.IsNullOrWhiteSpace(savedServer) ? NoServerOption : savedServer;
        _autoStartEnabled = _services.AutoStart.IsEnabled();
    }

    /// <summary>「按账号文本」的大区选项文本。</summary>
    private const string NoServerOption = "（按账号文本）";

    /// <summary>运行设置。</summary>
    public AppSettings Settings => _services.Config.Settings;

    /// <summary>可选截图方式。</summary>
    public IReadOnlyList<string> CaptureModes { get; }

    /// <summary>可选输入通道。</summary>
    public IReadOnlyList<InputBackendOption> InputBackendOptions { get; } =
    [
        new("自动（优先驱动级）", InputBackendKind.Auto),
        new("SendInput（系统模拟）", InputBackendKind.SendInput),
        new("Interception（驱动级）", InputBackendKind.Interception)
    ];

    /// <summary>可选票完动作。</summary>
    public IReadOnlyList<FinishActionOption> FinishActionOptions { get; } =
    [
        new("票完停止（不做任何事）", FinishAction.None),
        new("票完小化（最小化窗口）", FinishAction.Minimize),
        new("票完关机", FinishAction.Shutdown),
        new("票完重启（配合开机自启隔天重来）", FinishAction.Restart),
        new("票完重爆（重跑一轮白票）", FinishAction.Rerun)
    ];

    /// <summary>当前选择的票完动作。</summary>
    [ObservableProperty]
    private FinishActionOption _selectedFinishAction = new("票完停止（不做任何事）", FinishAction.None);

    /// <summary>切换票完动作时写回设置。</summary>
    partial void OnSelectedFinishActionChanged(FinishActionOption value)
    {
        if (value != null)
        {
            Settings.FinishAction = value.Value;
        }
    }

    /// <summary>可选大区，与账号页的下拉保持同一套写法。</summary>
    public IReadOnlyList<string> ServerOptions { get; } = [NoServerOption, .. Models.ServerOptions.All];

    /// <summary>默认大区（空表示按账号文本里的区服）。</summary>
    [ObservableProperty]
    private string _selectedServer = NoServerOption;

    /// <summary>切换默认大区时写回设置。</summary>
    partial void OnSelectedServerChanged(string value)
        => Settings.DefaultServer = string.IsNullOrWhiteSpace(value) || value == NoServerOption ? "" : value;

    /// <summary>可用预设方案名称。</summary>
    public IReadOnlyList<string> PresetNames { get; }

    /// <summary>当前选中的预设。</summary>
    [ObservableProperty]
    private string _selectedPreset = "";

    /// <summary>套用预设方案（常用／快捷／推荐设置2）。</summary>
    [RelayCommand]
    private void ApplyPreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedPreset))
        {
            StatusMessage = "请先选择一个预设";
            return;
        }

        StatusMessage = _services.Presets.Apply(SelectedPreset);

        // 预设会改动设置对象，这里把下拉框重新对齐一次
        _selectedFinishAction = FinishActionOptions.First(o => o.Value == Settings.FinishAction);
        OnPropertyChanged(nameof(SelectedFinishAction));
    }

    /// <summary>开机自启当前是否已写入注册表。</summary>
    [ObservableProperty]
    private bool _autoStartEnabled;

    /// <summary>手动刷新开机自启状态。</summary>
    [RelayCommand]
    private void RefreshAutoStart() => AutoStartEnabled = _services.AutoStart.IsEnabled();

    /// <summary>当前选择的输入通道。</summary>
    [ObservableProperty]
    private InputBackendOption _selectedInputBackend = new("自动（优先驱动级）", InputBackendKind.Auto);

    /// <summary>驱动可用状态。</summary>
    [ObservableProperty]
    private string _inputBackendStatus = "";

    /// <summary>切换输入通道时写回设置并刷新状态。</summary>
    partial void OnSelectedInputBackendChanged(InputBackendOption value)
    {
        if (value == null)
        {
            return;
        }

        Settings.InputBackend = value.Value;
        RefreshInputBackendStatus();
    }

    /// <summary>操作提示。</summary>
    [ObservableProperty]
    private string _statusMessage = "修改后会自动保存";

    /// <summary>刷新驱动状态文案。</summary>
    [RelayCommand]
    private void RefreshInputBackendStatus()
    {
        InputBackendStatus = InputBackendFactory.DescribeAvailability(Settings);
    }

    /// <summary>选择 interception.dll。</summary>
    [RelayCommand]
    private void BrowseInterceptionDll()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 interception.dll",
            Filter = "动态库 (interception.dll)|interception.dll|所有文件 (*.dll)|*.dll",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Settings.InterceptionDllPath = dialog.FileName;
        RefreshInputBackendStatus();
        StatusMessage = $"已选择 interception.dll：{dialog.FileName}";
    }

    /// <summary>保存设置。</summary>
    [RelayCommand]
    private void Save()
    {
        _services.Config.SaveSettings();
        StatusMessage = $"已保存（{DateTime.Now:HH:mm:ss}）";
    }

    /// <summary>选择游戏主程序。</summary>
    [RelayCommand]
    private void BrowseGameExe()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 QQ飞车 客户端主程序",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            Settings.GameExePath = dialog.FileName;
            StatusMessage = $"已选择：{dialog.FileName}";
        }
    }

    /// <summary>选择游戏安装目录（程序会自动在目录里找客户端主程序）。</summary>
    [RelayCommand]
    private void ChooseGameFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择 QQ飞车 安装目录" };

        var current = Settings.GameExePath;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var folder = File.Exists(current) ? Path.GetDirectoryName(current) : current;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                dialog.InitialDirectory = folder;
            }
        }

        if (dialog.ShowDialog() == true)
        {
            Settings.GameExePath = dialog.FolderName;
            StatusMessage = $"已选择目录：{dialog.FolderName}";
        }
    }

    /// <summary>打开用户数据目录。</summary>
    [RelayCommand]
    private void OpenUserFolder() => OpenFolder(PathService.UserDir);

    /// <summary>打开 AI 调用日志目录：每次模型请求的完整请求与返回都在这里。</summary>
    [RelayCommand]
    private void OpenAiLogFolder() => OpenFolder(AiRequestLogger.LogDir);

    /// <summary>打开流程目录。</summary>
    [RelayCommand]
    private void OpenFlowFolder() => OpenFolder(PathService.FlowDir);

    /// <summary>打开模板目录。</summary>
    [RelayCommand]
    private void OpenTemplateFolder() => OpenFolder(PathService.TemplateDir);

    /// <summary>取消关机倒计时。</summary>
    [RelayCommand]
    private void CancelShutdown() => _services.Shutdown.Cancel();

    /// <summary>
    /// 测试验证码识别：先让验证码弹出来，再点这个按钮，程序会抓图、切图、调模型，
    /// 并把结论和标注图写进 User/debug，用来确认密钥、模型名和提示词是否可用。
    /// </summary>
    [RelayCommand]
    private async Task TestCaptchaAsync()
    {
        StatusMessage = "验证码自检中…（请先让验证码弹出来）";
        var result = await Task.Run(() =>
            CaptchaSelfCheck.RunAsync(Settings, _services.Log, null, false, CancellationToken.None));

        try
        {
            var directory = Path.Combine(PathService.UserDir, "debug");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "captchacheck.txt"), result.Report);
        }
        catch (Exception ex)
        {
            _services.Log.Warn($"写验证码自检报告失败：{ex.Message}", "验证码");
        }

        StatusMessage = result.ExitCode == 0
            ? "验证码自检通过：切图与识别都正常（详情见 User/debug/captchacheck.txt）"
            : "验证码自检未通过：原因见 User/debug/captchacheck.txt";
    }

    /// <summary>
    /// 设置项变化：自动保存；开机自启是注册表操作，需要立刻同步，不能只改配置文件。
    /// </summary>
    private void OnSettingChanged(string? propertyName)
    {
        // 日志开关是静态字段，改了要立刻生效，不能等下次启动读配置。
        if (propertyName == nameof(AppSettings.AiRequestLogEnabled))
        {
            AiRequestLogger.Enabled = Settings.AiRequestLogEnabled;
        }

        if (propertyName == nameof(AppSettings.AutoStartWithWindows))
        {
            var ok = _services.AutoStart.Apply(Settings.AutoStartWithWindows);
            AutoStartEnabled = _services.AutoStart.IsEnabled();
            if (!ok)
            {
                StatusMessage = "开机自启写入注册表失败，请检查安全软件拦截";
                return;
            }
        }

        _services.Config.SaveSettings();
        StatusMessage = $"已自动保存（{DateTime.Now:HH:mm:ss}）";
    }

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
}

/// <summary>票完动作下拉选项。</summary>
public sealed class FinishActionOption
{
    /// <summary>构造选项。</summary>
    public FinishActionOption(string name, FinishAction value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>显示名。</summary>
    public string Name { get; }

    /// <summary>枚举值。</summary>
    public FinishAction Value { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>输入通道下拉选项。</summary>
public sealed class InputBackendOption
{
    /// <summary>构造选项。</summary>
    public InputBackendOption(string name, InputBackendKind value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>显示名。</summary>
    public string Name { get; }

    /// <summary>枚举值。</summary>
    public InputBackendKind Value { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}
