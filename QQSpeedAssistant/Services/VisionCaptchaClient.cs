using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>视觉模型的识别结果。</summary>
public sealed record CaptchaAnswer(string Target, IReadOnlyList<int> Picks, double Confidence, string Raw)
{
    /// <summary>是否拿到了可点击的编号。</summary>
    public bool IsUsable => Picks.Count > 0;
}

/// <summary>
/// 调用「OpenAI 兼容」的视觉模型接口识别图片验证码。
/// 地址、密钥、模型名都在设置里填，因此云端（通义千问／智谱等）和本地（Ollama）都能直接用。
/// </summary>
public sealed class VisionCaptchaClient
{
    private readonly LogService _log;

    private static readonly HttpClient Http = new()
    {
        // 视觉模型要读图，比普通文本请求慢，给足超时。
        Timeout = TimeSpan.FromSeconds(60)
    };

    /// <summary>构造客户端。</summary>
    public VisionCaptchaClient(LogService log)
    {
        _log = log;
    }

    /// <summary>是否具备调用条件（有密钥，或指向本机服务）。</summary>
    public static bool CanCall(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CaptchaApiBaseUrl) || string.IsNullOrWhiteSpace(settings.CaptchaModel))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(settings.CaptchaApiKey))
        {
            return true;
        }

        // 本地 Ollama／vLLM 通常不需要密钥，允许空密钥直连本机地址。
        var url = settings.CaptchaApiBaseUrl;
        return url.Contains("127.0.0.1", StringComparison.Ordinal)
               || url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
               || url.Contains("[::1]", StringComparison.Ordinal);
    }

    /// <summary>把标注过编号的截图交给视觉模型，要它返回题目关键词与命中编号。</summary>
    public async Task<CaptchaAnswer?> AskAsync(byte[] jpeg, int tileCount, AppSettings settings, CancellationToken ct)
    {
        var baseUrl = settings.CaptchaApiBaseUrl.Trim().TrimEnd('/');
        var endpoint = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/chat/completions";

        var prompt =
            "这是一张 QQ 登录时弹出的图片验证码截图。图中每张候选图都已用红框框出，左上角写着红色编号，"
            + $"一共 {tileCount} 张，编号 1 到 {tileCount}。\n"
            + "请完成两件事：\n"
            + "① 读出截图里的题目文字（形如「选择最符合描述的图片：XXX」），把 XXX 原样作为 target；\n"
            + "② 逐张判断候选图是否与 target 相符，按题目要求选出最符合的（可以 1 张，也可以多张）。\n"
            + "只输出一个 JSON 对象，不要输出解释、不要用 Markdown 代码块：\n"
            + "{\"target\":\"XXX\",\"picks\":[1],\"confidence\":0.8}\n"
            + "picks 里只能出现上面那些编号，确认不了就返回空数组。";

        // 免费模型有每分钟调用上限：智谱 glm-4.6v-flash 打满就直接回 429，
        // 换个同平台的免费模型往往还有余量，比干等它解封强。
        var models = new List<string> { settings.CaptchaModel.Trim() };

        var fallback = settings.CaptchaFallbackModel?.Trim() ?? "";
        if (fallback.Length > 0 && !string.Equals(fallback, models[0], StringComparison.OrdinalIgnoreCase))
        {
            models.Add(fallback);
        }

        // 刚被限流过的模型排到后面：免费额度一旦打满往往好几分钟不恢复，
        // 每次都先撞它两轮只会白白拖住验证码窗口。
        models = models
            .OrderBy(m => InCooldown(m) ? 1 : 0)
            .ToList();

        var key = settings.CaptchaApiKey?.Trim() ?? "";
        CaptchaAnswer? spare = null;

        foreach (var model in models)
        {
            var answer = await TryModelAsync(endpoint, model, prompt, jpeg, tileCount, key, ct);
            if (answer is { IsUsable: true })
            {
                RateLimitedAt.TryRemove(model, out _);
                return answer;
            }

            // 调通了但一张都没认出来，换模型多半也救不回来；留作兜底结果。
            spare ??= answer;
        }

        return spare;
    }

    /// <summary>被限流后冷却多久再重新尝试（秒）。</summary>
    private const int CooldownSeconds = 180;

    /// <summary>最近一次被限流的时间，按模型记。</summary>
    private static readonly ConcurrentDictionary<string, DateTime> RateLimitedAt = new();

    /// <summary>这个模型是否还在限流冷却期。</summary>
    private static bool InCooldown(string model)
    {
        if (!RateLimitedAt.TryGetValue(model, out var at))
        {
            return false;
        }

        if ((DateTime.UtcNow - at).TotalSeconds > CooldownSeconds)
        {
            RateLimitedAt.TryRemove(model, out _);
            return false;
        }

        return true;
    }

    /// <summary>对单个模型发一次请求；撞限流或服务端抖动时退避重试。</summary>
    private async Task<CaptchaAnswer?> TryModelAsync(string endpoint, string model, string prompt,
        byte[] jpeg, int tileCount, string key, CancellationToken ct)
    {
        var payload = BuildPayload(endpoint, model, prompt, jpeg);

        // 429/5xx 的重试次数；400 那种「剥字段重发」不占这个额度。
        const int maxAttempts = 2;

        // 400 时依次剥掉的可选字段：各家兼容层实现不一，硬试比照着文档写死参数更扛换模型。
        // response_format 在前、thinking 在后——glm-4.x 关掉思考能快好几秒，别急着放弃它。
        var optionalFields = new List<string> { "response_format", "thinking" };

        // 轮次上限：网络重试 2 次 + 字段最多剥 2 个，兜底防死循环。
        const int maxRounds = maxAttempts + 2;

        try
        {
            var round = 0;
            var networkAttempt = 0;

            while (true)
            {
                round++;
                if (round > maxRounds)
                {
                    return null;
                }

                // 每次真正发出去都单独记一节 AI 日志：完整请求体（图片换成占位符）、完整响应体、
                // 状态码、耗时，连 400/429 的失败响应也照样落盘，排查时不用猜接口到底回了什么。
                using var call = AiRequestLogger.Begin(
                    round > 1 ? $"验证码识别（第 {round} 次尝试）" : "验证码识别",
                    model, endpoint, payload.ToString(Newtonsoft.Json.Formatting.None), key, _log, jpeg);

                HttpResponseMessage response;
                string body;

                try
                {
                    using var request = BuildRequest(endpoint, payload, key);
                    response = await Http.SendAsync(request, ct);
                    body = await response.Content.ReadAsStringAsync(ct);
                }
                catch (Exception ex)
                {
                    call?.Failure($"请求异常：{ex.GetType().Name} {ex.Message}");
                    throw;
                }

                using (response)
                {
                    var status = (int)response.StatusCode;

                    if (response.IsSuccessStatusCode)
                    {
                        var content = ParseContent(body);
                        if (content == null)
                        {
                            call?.Failure("响应里没有 choices[0].message.content", status, body);
                            _log.Error($"[{model}] 返回内容无法解析：{Trim(body, 300)}", "验证码");
                            return null;
                        }

                        var answer = ParseAnswer(content, tileCount);
                        call?.Success(status, body, answer == null
                            ? "程序解析失败：回答里没有可用的 JSON"
                            : $"target={answer.Target} picks=[{string.Join(",", answer.Picks)}] confidence={answer.Confidence}");
                        return answer;
                    }

                    // 400 通常是某个可选字段不被这家接受：少数兼容层没实现 response_format，
                    // 智谱 glm-5 系又不认 thinking=disabled（回 code 1210「该模型始终思考」）。
                    // 一次剥一个重发，剥到没得剥为止，换模型时不用再改代码。
                    var stripped = status == 400 ? StripOptionalField(payload, optionalFields) : null;
                    if (stripped != null)
                    {
                        call?.Failure($"400，去掉 {stripped} 后重发", status, body);
                        _log.Warn($"[{model}] 返回 400（{Trim(body, 160)}），去掉 {stripped} 重试", "验证码");
                        continue;
                    }

                    if ((status == 429 || status >= 500) && ++networkAttempt < maxAttempts)
                    {
                        var wait = TimeSpan.FromSeconds(2 * networkAttempt);
                        call?.Failure($"{status}，{wait.TotalSeconds:F0} 秒后重试", status, body);
                        _log.Warn($"[{model}] 返回 {status}，{wait.TotalSeconds:F0} 秒后重试（{networkAttempt}/{maxAttempts}）", "验证码");
                        await Task.Delay(wait, ct);
                        continue;
                    }

                    if (status == 429)
                    {
                        // 记下来，接下来几分钟把这个模型排到最后，别再让它拖住验证码窗口。
                        RateLimitedAt[model] = DateTime.UtcNow;
                    }

                    call?.Failure($"HTTP {status}", status, body);
                    _log.Error($"[{model}] 识别接口返回 {status}：{Trim(body, 200)}", "验证码");
                    return null;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Error($"[{model}] 识别接口超时（60 秒），请检查网络或模型名", "验证码");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"[{model}] 识别请求失败：{ex.Message}", "验证码");
            return null;
        }
    }

    /// <summary>
    /// 从请求体里剥掉一个可选字段，返回被剥的字段名；没有可剥的返回 null。
    /// 请求体里本来就没有的字段直接跳过，不浪费一次请求。
    /// </summary>
    private static string? StripOptionalField(JObject payload, List<string> fields)
    {
        while (fields.Count > 0)
        {
            var name = fields[0];
            fields.RemoveAt(0);
            if (payload.Remove(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>按模型名拼请求体；智谱要显式关掉思考模式，否则会白等好几秒。</summary>
    private static JObject BuildPayload(string endpoint, string model, string prompt, byte[] jpeg)
    {
        var payload = new JObject
        {
            ["model"] = model,
            // 有些平台（含智谱）的 temperature 是 (0,1]，传 0 会被判参数错误，0.01 足够确定。
            ["temperature"] = 0.01,
            // 思考型模型的推理过程也占 token，200 容易被思考吃光、content 变成空串。
            ["max_tokens"] = 800,
            // 小参数量模型容易加解释或 Markdown 围栏，强制 JSON 能少一层解析失败。
            ["response_format"] = new JObject { ["type"] = "json_object" },
            ["messages"] = new JArray
            {
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject { ["type"] = "text", ["text"] = prompt },
                        new JObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JObject
                            {
                                ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
                                ["detail"] = "high"
                            }
                        }
                    }
                }
            }
        };

        if (endpoint.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase))
        {
            // glm-4.x 默认会先思考几秒，验证码场景纯属白等，显式关掉；
            // glm-5 系不允许关闭（400 / code 1210「该模型始终思考」），只能用 low 把思考压到最少。
            payload["thinking"] = model.StartsWith("glm-5", StringComparison.OrdinalIgnoreCase)
                ? new JObject { ["type"] = "low" }
                : new JObject { ["type"] = "disabled" };
        }

        return payload;
    }


    /// <summary>构造一次识别请求；重试时要重新建，HttpRequestMessage 不能复用。</summary>
    private static HttpRequestMessage BuildRequest(string endpoint, JObject payload, string key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8,
                "application/json")
        };

        if (key.Length > 0)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return message;
    }

    /// <summary>
    /// 从 OpenAI 兼容响应里取出回答文本。思考型模型有时把结论写进 reasoning_content、
    /// content 留空（思考把 max_tokens 吃满时也会这样），所以 content 为空就回退到 reasoning_content。
    /// </summary>
    private static string? ParseContent(string body)
    {
        try
        {
            var json = JObject.Parse(body);
            var message = json["choices"]?[0]?["message"];

            var content = message?["content"];
            if (IsEmptyText(content))
            {
                content = message?["reasoning_content"];
            }

            if (IsEmptyText(content))
            {
                return null;
            }

            var text = content!;

            // 部分实现返回分段数组（[{type:text,text:...}]）。
            if (text.Type == JTokenType.Array)
            {
                return string.Join("\n", text.Children<JObject>()
                    .Select(o => o["text"]?.ToString())
                    .Where(t => !string.IsNullOrWhiteSpace(t)));
            }

            return text.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>这个 token 是不是「没有内容」：null、空字符串或纯空白。</summary>
    private static bool IsEmptyText(JToken? token)
        => token == null
           || (token.Type == JTokenType.String && string.IsNullOrWhiteSpace(token.ToString()));

    /// <summary>从模型回答里抠出 JSON 并解析成识别结果。</summary>
    public static CaptchaAnswer? ParseAnswer(string content, int tileCount)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var json = JObject.Parse(content.Substring(start, end - start + 1));
            var target = json["target"]?.ToString() ?? "";
            var confidence = json["confidence"]?.Type == JTokenType.Float
                ? (double)json["confidence"]!
                : 0;

            var picks = new List<int>();
            if (json["picks"] is JArray array)
            {
                foreach (var item in array)
                {
                    if (int.TryParse(item.ToString(), out var index) && index >= 1 && index <= tileCount)
                    {
                        if (!picks.Contains(index))
                        {
                            picks.Add(index);
                        }
                    }
                }
            }

            return new CaptchaAnswer(target, picks, confidence, content.Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>截断长文本，日志里不要贴整段响应。</summary>
    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
