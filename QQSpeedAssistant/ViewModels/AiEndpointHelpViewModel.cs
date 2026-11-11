using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.ViewModels;

/// <summary>一套接口方案里的某个模型：Id 是填进「模型名」的字符串，Remark 是给人看的说明。</summary>
public sealed record AiModelOption(string Id, string Remark);

/// <summary>
/// 一套可直接使用的接口方案。地址、密钥从哪拿、可选模型、注意事项都给齐，
/// 用户点「一键填入」就能把地址和模型写进设置，不用手抄。
/// </summary>
public sealed record AiEndpointPreset(
    string Name,
    string Badge,
    Brush BadgeBrush,
    string Summary,
    string BaseUrl,
    bool NeedsKey,
    string KeyHint,
    string KeyUrl,
    string ConsoleUrl,
    IReadOnlyList<AiModelOption> Models,
    string Note);

/// <summary>
/// 「接口地址和模型怎么填」说明窗：左侧列可用方案，右侧按三步讲清楚每个框该填什么，
/// 选好模型可以一键写回设置。本质是一份能动手点的小抄。
/// </summary>
public sealed partial class AiEndpointHelpViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _save;

    /// <summary>
    /// 构造说明窗 ViewModel。只依赖设置对象和一个「保存」回调，不碰服务容器——
    /// 这样这一步能在隔离程序里单独验证，不用把整套服务都拉起来。
    /// </summary>
    public AiEndpointHelpViewModel(AppSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        Presets = BuildPresets();

        var current = settings.CaptchaApiBaseUrl?.Trim().TrimEnd('/') ?? "";
        _selectedPreset = Presets.FirstOrDefault(p =>
                             string.Equals(p.BaseUrl.TrimEnd('/'), current, StringComparison.OrdinalIgnoreCase))
                         ?? Presets[0];

        _selectedModel = _selectedPreset.Models.FirstOrDefault();
    }

    /// <summary>全部可选方案。</summary>
    public IReadOnlyList<AiEndpointPreset> Presets { get; }

    /// <summary>当前选中的方案。</summary>
    [ObservableProperty]
    private AiEndpointPreset _selectedPreset;

    /// <summary>当前选中的模型（一键填入时写进「模型名」）。</summary>
    [ObservableProperty]
    private AiModelOption? _selectedModel;

    /// <summary>底部状态行。</summary>
    [ObservableProperty]
    private string _status = "选一个方案，再点「一键填入设置」；也可以照右边的三步手动抄进设置页。";

    /// <summary>切换方案时同步重置模型选择与派生的提示文案。</summary>
    partial void OnSelectedPresetChanged(AiEndpointPreset value)
    {
        if (value == null)
        {
            return;
        }

        SelectedModel = value.Models.FirstOrDefault();

        OnPropertyChanged(nameof(KeyStepHint));
        OnPropertyChanged(nameof(CanOpenKeyUrl));
        OnPropertyChanged(nameof(CanOpenConsole));
        OnPropertyChanged(nameof(IsInUse));
        OnPropertyChanged(nameof(FallbackHint));
    }

    /// <summary>第二步「接口密钥」下面那行说明。</summary>
    public string KeyStepHint => SelectedPreset.NeedsKey
        ? SelectedPreset.KeyHint
        : "这套方案跑在你自己电脑上，不需要密钥，这栏留空就行。";

    /// <summary>是否有密钥获取页可跳转。</summary>
    public bool CanOpenKeyUrl => !string.IsNullOrWhiteSpace(SelectedPreset.KeyUrl);

    /// <summary>是否有官网可跳转。</summary>
    public bool CanOpenConsole => !string.IsNullOrWhiteSpace(SelectedPreset.ConsoleUrl);

    /// <summary>当前设置是否已经是这套方案（右侧显示个小绿标）。</summary>
    public bool IsInUse =>
        string.Equals(SelectedPreset.BaseUrl.TrimEnd('/'),
            _settings.CaptchaApiBaseUrl?.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>备用模型会填成什么，提前告诉用户。</summary>
    public string FallbackHint
    {
        get
        {
            var fallback = PickFallback(SelectedPreset, SelectedModel);
            return fallback == null
                ? "这套方案只有一个可用模型，备用模型会留空（主模型被限流时直接转人工）。"
                : $"「备用模型」会自动填成 {fallback}，主模型被限流时顶上去。";
        }
    }

    /// <summary>复制接口地址到剪贴板。</summary>
    [RelayCommand]
    private void CopyBaseUrl()
    {
        var url = SelectedPreset.BaseUrl;
        try
        {
            Clipboard.SetText(url);
            Status = $"已复制接口地址：{url}";
        }
        catch (Exception ex)
        {
            Status = $"复制失败（剪贴板被别的程序占用？）：{ex.Message}";
        }
    }

    /// <summary>复制模型名到剪贴板。</summary>
    [RelayCommand]
    private void CopyModel()
    {
        if (SelectedModel == null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(SelectedModel.Id);
            Status = $"已复制模型名：{SelectedModel.Id}";
        }
        catch (Exception ex)
        {
            Status = $"复制失败（剪贴板被别的程序占用？）：{ex.Message}";
        }
    }

    /// <summary>打开该方案的密钥获取页。</summary>
    [RelayCommand]
    private void OpenKeyUrl() => OpenUrl(SelectedPreset.KeyUrl);

    /// <summary>打开该方案的官网/控制台/模型列表。</summary>
    [RelayCommand]
    private void OpenConsole() => OpenUrl(SelectedPreset.ConsoleUrl);

    /// <summary>把地址、模型、备用模型一起写进设置并保存。</summary>
    [RelayCommand]
    private void Apply()
    {
        var settings = _settings;
        var preset = SelectedPreset;
        var model = SelectedModel ?? preset.Models.FirstOrDefault();
        if (model == null)
        {
            Status = "这套方案没有可用模型，换一个方案试试。";
            return;
        }

        var switched = !string.Equals(settings.CaptchaApiBaseUrl?.Trim().TrimEnd('/'),
            preset.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        settings.CaptchaApiBaseUrl = preset.BaseUrl;
        settings.CaptchaModel = model.Id;

        // 换平台时旧密钥一定要清：各家密钥格式不同，留着只会换来 401，
        // 而 401 在界面上看起来像「模型配错了」，很难查。
        if (switched)
        {
            settings.CaptchaApiKey = "";
        }

        var fallback = PickFallback(preset, model) ?? "";
        settings.CaptchaFallbackModel = fallback;

        _save();

        OnPropertyChanged(nameof(IsInUse));

        if (preset.NeedsKey && string.IsNullOrWhiteSpace(settings.CaptchaApiKey))
        {
            Status = $"已填入 {preset.Name} · {model.Id}，并已保存。还差密钥："
                     + (CanOpenKeyUrl ? "点「去获取密钥」拿一个，粘到设置页的「接口密钥」里。" : "去官网申请一个，粘到「接口密钥」里。");
            return;
        }

        Status = $"已填入 {preset.Name} · {model.Id}"
                 + (fallback.Length > 0 ? $"（备用 {fallback}）" : "")
                 + "，并已保存。回设置页点「测试验证码识别」确认能认出图。";
    }

    /// <summary>同方案里挑一个与主模型不同的模型做备用；没有就返回 null。</summary>
    private static string? PickFallback(AiEndpointPreset preset, AiModelOption? primary) =>
        primary == null
            ? preset.Models.FirstOrDefault()?.Id
            : preset.Models.Select(m => m.Id)
                .FirstOrDefault(id => !string.Equals(id, primary.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>用默认浏览器打开链接。</summary>
    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Status = $"已在浏览器打开：{url}";
        }
        catch (Exception ex)
        {
            Status = $"打不开浏览器：{ex.Message}";
        }
    }

    private static SolidColorBrush Hex(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>
    /// 内置方案表。地址里都只写到版本号（/v1、/v4），程序会自动补 /chat/completions。
    /// 免费额度与模型上下架会变，所以每套都给一个官网入口，让用户能自己核对。
    /// </summary>
    private static IReadOnlyList<AiEndpointPreset> BuildPresets() =>
    [
        new(
            "智谱 GLM（默认推荐）",
            "长期免费",
            Hex("#34D399"),
            "国内直连、不用梯子。视觉模型 glm-4v-flash 系列长期免费，识别验证码够用，是程序默认配置。",
            "https://open.bigmodel.cn/api/paas/v4",
            true,
            "登录 bigmodel.cn → 右上角头像 →「API Keys」→ 新建，复制那串形如 abc12345.xxxxxxxx 的字符。",
            "https://bigmodel.cn/usercenter/apikeys",
            "https://bigmodel.cn/dev/howuse/model",
            [
                new("glm-4.6v-flash", "当前默认，认得最准"),
                new("glm-4v-flash", "老牌免费视觉模型，速度更快"),
                new("glm-4.1v-thinking-flash", "带思考过程，慢一些")
            ],
            "免费模型有每分钟/每天调用上限，撞上会返回 429。程序会自动换「备用模型」重试，所以备用那栏别留空。"),

        new(
            "魔搭 ModelScope",
            "每天免费额度",
            Hex("#60A5FA"),
            "阿里达摩院的模型社区，Qwen 系列视觉模型免费调用，额度按天刷新，适合当智谱的替补。",
            "https://api-inference.modelscope.cn/v1",
            true,
            "登录 modelscope.cn → 右上角头像 →「访问令牌」→ 新建，复制 ms- 开头的那串。",
            "https://modelscope.cn/my/myaccesstoken",
            "https://modelscope.cn/models?filter=vision",
            [
                new("Qwen/Qwen3-VL-30B-A3B-Instruct", "新版 Qwen3 视觉，推荐"),
                new("Qwen/Qwen2.5-VL-7B-Instruct", "小参数，响应快"),
                new("Qwen/Qwen2.5-VL-72B-Instruct", "大参数，额度消耗快")
            ],
            "模型名要连「发布者/模型名」一起填，斜杠不能省。绑定阿里云账号后每日额度会更多。"),

        new(
            "本地 Ollama",
            "完全离线",
            Hex("#A78BFA"),
            "跑在自己显卡上，不花钱、不联网、不看别人脸色，缺点是 8G 显存只能用 3B 小模型，识别率不如云端。",
            "http://127.0.0.1:11434/v1",
            false,
            "",
            "",
            "https://ollama.com/library/qwen2.5vl",
            [
                new("qwen2.5vl:3b", "约 3GB，8G 显存够用"),
                new("qwen2.5vl:7b", "约 6GB，识别更准但更慢"),
                new("llava:7b", "老模型，效果一般")
            ],
            "用之前先在命令行执行 ollama pull qwen2.5vl:3b 把模型下下来；程序闲置 5 分钟模型会被卸载，下次首帧要等 40 秒以上（超时已放宽到 120 秒）。"),

        new(
            "阿里云百炼（通义千问）",
            "新用户免费额度",
            Hex("#FBBF24"),
            "通义千问官方接口，qwen-vl 系列识别能力在线；新用户有一定免费 token，用完转按量计费。",
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            true,
            "登录百炼控制台 → 右上角「API-KEY」→ 创建，复制 sk- 开头的密钥。",
            "https://bailian.console.aliyun.com/?apiKey=1",
            "https://help.aliyun.com/zh/model-studio/models",
            [
                new("qwen-vl-max-latest", "效果最好，按量计费"),
                new("qwen-vl-plus-latest", "便宜，够用"),
                new("qwen3-vl-plus", "新版，速度快")
            ],
            "免费额度用完会自动开始扣费，不想花钱的话建议只用智谱或本地 Ollama。"),

        new(
            "硅基流动 SiliconFlow",
            "注册送额度",
            Hex("#F472B6"),
            "一个地址聚合很多开源视觉模型，注册送额度，部分模型限时免费。",
            "https://api.siliconflow.cn/v1",
            true,
            "登录 cloud.siliconflow.cn →「API 密钥」→ 新建，复制 sk- 开头的密钥。",
            "https://cloud.siliconflow.cn/account/ak",
            "https://cloud.siliconflow.cn/models?type=vision",
            [
                new("Qwen/Qwen2.5-VL-7B-Instruct", "小参数，便宜"),
                new("Qwen/Qwen2.5-VL-32B-Instruct", "中等参数，效果稳"),
                new("THUDM/GLM-4.1V-9B-Thinking", "限时免费，可当主模型")
            ],
            "限时免费的模型会随时调整，用之前先到官网模型列表确认标签还写着「免费」。"),

        new(
            "OpenRouter",
            "部分 :free",
            Hex("#94A3B8"),
            "一个密钥访问几十家模型，带 :free 后缀的模型免费，但每天有调用条数限制。",
            "https://openrouter.ai/api/v1",
            true,
            "登录 openrouter.ai →「Keys」→ Create Key，复制 sk-or- 开头的密钥。",
            "https://openrouter.ai/keys",
            "https://openrouter.ai/models?max_price=0",
            [
                new("qwen/qwen2.5-vl-72b-instruct:free", "免费视觉，通用性好"),
                new("google/gemini-2.0-flash-exp:free", "免费视觉，识别力强")
            ],
            "国内访问可能不稳定；:free 模型有每日条数上限，超了会报 429。模型名必须带 :free 后缀才是免费的。")
    ];
}
