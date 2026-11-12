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

/// <summary>单张候选图的判定结果。Seen 是模型在图里实际看到的内容。</summary>
public sealed record TileVerdict(int Index, bool Hit, double Confidence, string Seen, string Raw);

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
        // 本地 Ollama 还有个坑：模型闲置 5 分钟会被卸载，冷启动要 40 秒以上（实测 49 秒），
        // 60 秒会踩线，所以一律放开到 120 秒。
        Timeout = TimeSpan.FromSeconds(120)
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

    /// <summary>
    /// 第一步：只把题目区送去读字，读回题目原文。
    /// 读题和选图拆成两次调用，是因为小参数量模型在「先抄题、再逐张比对」这种多步任务上会退化——
    /// 它给不出中间比对过程，就直接吐一个最省事的答案（全选）。
    /// </summary>
    public async Task<string?> ReadQuestionAsync(byte[] png, AppSettings settings, CancellationToken ct)
    {
        var endpoint = ResolveEndpoint(settings);

        var prompt =
            "这是 QQ 登录验证码弹窗的顶部区域截图。这里通常有两行文字：\n"
            + "第一行是说明，形如「为了你的账号安全，请按要求完成下列操作」；\n"
            + "第二行才是题目，形如「选择最符合描述的图片：一只猫」，"
            + "或者「选择所有符合描述的图片：包含文字 \"个\"」。\n"
            + "请只抄出第二行题目的完整原文，一个字都不要改，也不要抄第一行的说明。\n"
            + "直接输出这行文字本身：不要加引号、不要加解释、不要用 JSON、不要换行。";

        var key = settings.CaptchaApiKey?.Trim() ?? "";

        foreach (var model in ResolveModels(settings))
        {
            var text = await TryModelAsync(endpoint, model, "验证码读题", prompt, png, false, "image/png", key,
                static content => string.IsNullOrWhiteSpace(content) ? "" : content.Trim(), ct);

            if (!string.IsNullOrWhiteSpace(text))
            {
                RateLimitedAt.TryRemove(model, out _);
                return text;
            }
        }

        return null;
    }

    /// <summary>
    /// 第二步：把单张候选图放大后单独送去判定，只让模型报告「它在图里实际看到了什么」。
    /// 二分类比「在六张里挑出所有符合的」容易一个量级，而且单张图能独占总分辨率预算，
    /// 不用和另外五张分那几百个视觉 token。
    /// </summary>
    /// <param name="png">单张候选图（已放大）。</param>
    /// <param name="lookForText">
    /// 题目是不是「包含文字 X」那一类。是就只问「辨认到什么字」，否则只问「画面里最主要的东西」。
    /// 两种问法都**不能把题目原文告诉模型**——见下面 prompt 的注释。
    /// </param>
    /// <param name="index">候选图编号，只用于日志。</param>
    /// <param name="settings">运行设置。</param>
    /// <param name="ct">取消标记。</param>
    public async Task<TileVerdict?> CheckTileAsync(byte[] png, bool lookForText, int index, AppSettings settings,
        CancellationToken ct)
    {
        var endpoint = ResolveEndpoint(settings);

        var prompt = BuildTilePrompt(lookForText);

        var key = settings.CaptchaApiKey?.Trim() ?? "";

        foreach (var model in ResolveModels(settings, preferFallback: lookForText))
        {
            var verdict = await TryModelAsync(endpoint, model, $"验证码判定 #{index}", prompt, png, true, "image/png",
                key, content => ParseVerdict(content, index), ct);

            if (verdict != null)
            {
                RateLimitedAt.TryRemove(model, out _);
                return verdict;
            }
        }

        return null;
    }

    /// <summary>
    /// 第二道复核：把题目要找的目标词直接告诉模型，问它这张图里有没有，只要一个是/否。
    /// 只在「逐块报看到什么」判出零命中之后才调用——这条问法是有代价的：
    /// 目标词进了提示词，模型报回来的 seen 会被带偏（实测问「足球」时六张里四张把 seen 写成「足球」，
    /// 而那些图里并没有足球），所以这里**只采信 has，绝不拿 seen 去比对**。
    /// 反过来的好处是：目标在画面里、但不是最显眼的东西时（紫色盘子被说成「叉子」），
    /// 只有这种问法救得回来。实测 18 次判定（含目标完全不存在的一轮）全对，没有出现一律答 true。
    /// </summary>
    public async Task<TileVerdict?> ConfirmTileAsync(byte[] png, string target, bool lookForText, int index,
        AppSettings settings, CancellationToken ct)
    {
        var endpoint = ResolveEndpoint(settings);
        var prompt = BuildConfirmPrompt(target, lookForText);
        var key = settings.CaptchaApiKey?.Trim() ?? "";

        foreach (var model in ResolveModels(settings, preferFallback: lookForText))
        {
            var verdict = await TryModelAsync(endpoint, model, $"验证码复核 #{index}", prompt, png, true, "image/png",
                key, content => ParseConfirm(content, index), ct);

            if (verdict != null)
            {
                RateLimitedAt.TryRemove(model, out _);
                return verdict;
            }
        }

        return null;
    }

    /// <summary>
    /// 单张候选图的第一道问法：只问「你看到什么」，一个字都不提题目。
    /// 三个坑，全是实测踩出来的：
    /// ① 问「这张图符不符合题目」这种抽象判断，3B 会敷衍——一律回 false、confidence 0.01；
    /// ② 把题目里的词（「绵羊」）写进单块提示词，3B 会顺着题目答：实测沙漠日落、海滩日落两张
    ///    都被它报成「绵羊」，六张里只有一张真是羊；
    /// ③ JSON 示例里给出具体取值，它会把这个示例值原样抄回来——写 "hit":true 就六块全「符合」，
    ///    写 {"seen":"个"} 就六块全报「个」（2026-11-12 实测，藏字题 6/6 全是「个」，一次都没真看）。
    /// 所以：示例值一律留空串，只问事实（图里是什么/什么字），判断留给调用方做字符串匹配。
    /// </summary>
    private static string BuildTilePrompt(bool lookForText)
        => lookForText
            ? "这是一张 QQ 登录验证码里的候选图。\n"
              + "图里可能叠着一个很淡、半透明的汉字（有时是用景物拼出来的字形），也可能没有。\n"
              + "只回答一件事：你辨认到的汉字是什么？一个字都没辨认到就留空，不要猜、不要编。\n"
              + "seen 填你辨认到的那个字；示例里的空字符串只是格式示意，不是答案。\n"
              + "只输出 JSON，不要解释、不要用 Markdown 代码块：{\"seen\":\"\"}"
            : "这是一张 QQ 登录验证码里的候选图。\n"
              + "只回答一件事：这张图里最主要的东西是什么？\n"
              + "只写名称，不超过 4 个字，不要写句子、不要写颜色和方位，不要猜图里没有的东西。\n"
              + "seen 填你看到的名称；示例里的空字符串只是格式示意，不是答案。\n"
              + "只输出 JSON，不要解释、不要用 Markdown 代码块：{\"seen\":\"\"}";

    /// <summary>
    /// 复核问法：把目标词摆出来问有／没有。
    /// 两处克制：示例里 has 写 false（往「没有」的方向偏，宁可漏判也不错判），
    /// 并要求先报看到什么再判断，避免模型不看图直接答。
    /// </summary>
    private static string BuildConfirmPrompt(string target, bool lookForText)
        => lookForText
            ? "这是一张 QQ 登录验证码里的候选图。\n"
              + "图里可能叠着一个很淡、半透明的汉字（有时是用景物拼出来的字形），也可能没有。\n"
              + $"题目要找的字是「{target}」。\n"
              + "先说你辨认到的字是什么（一个字都没辨认到就留空），再判断是不是题目要找的字。\n"
              + "seen 填你辨认到的字，has 只填 true 或 false。\n"
              + "只输出 JSON，不要解释、不要用 Markdown 代码块：{\"seen\":\"\",\"has\":false}"
            : "这是一张 QQ 登录验证码里的候选图。\n"
              + $"题目要找的是「{target}」。\n"
              + "先看这张图里最主要的东西是什么，再判断它是不是题目要找的东西。\n"
              + "seen 填你看到的东西（不超过 4 个字），has 只填 true 或 false。\n"
              + "只输出 JSON，不要解释、不要用 Markdown 代码块：{\"seen\":\"\",\"has\":false}";

    /// <summary>拼出 chat/completions 的完整地址。</summary>
    private static string ResolveEndpoint(AppSettings settings)
    {
        var baseUrl = settings.CaptchaApiBaseUrl.Trim().TrimEnd('/');
        return baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/chat/completions";
    }

    /// <summary>按设置拼出候选模型列表；刚被限流的排到最后。</summary>
    /// <param name="preferFallback">
    /// 是否优先用备用模型。藏字题（包含文字 X）对 3B 这类小模型基本无解——实测换六种问法、
    /// 加边缘掩码、按笔画紧裁放大，输出全部退化成同一个字（山/空），而同一模型读印刷体汉字
    /// 却基本全对，说明是画面里那个淡字形超出它的能力。用户通常把更强的模型填在备用位，
    /// 所以这类题目先让备用上。
    /// </param>
    private static List<string> ResolveModels(AppSettings settings, bool preferFallback = false)
    {
        var models = new List<string> { settings.CaptchaModel.Trim() };

        var fallback = settings.CaptchaFallbackModel?.Trim() ?? "";
        if (fallback.Length > 0 && !string.Equals(fallback, models[0], StringComparison.OrdinalIgnoreCase))
        {
            models.Add(fallback);
        }

        if (preferFallback)
        {
            models.Reverse();
        }

        return models.OrderBy(m => InCooldown(m) ? 1 : 0).ToList();
    }

    /// <summary>解析单张候选图的判定结果。</summary>
    private static TileVerdict? ParseVerdict(string content, int index)
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

            // seen 是唯一必需的字段：命中与否一律由调用方按题目做匹配。
            // 模型自己给的 hit 不可信——3B 会把提示词示例里的 true 原样抄回来，六块全「符合」。
            var seenToken = json["seen"];
            if (seenToken == null || seenToken.Type == JTokenType.Null)
            {
                return null;
            }

            var seen = seenToken.ToString().Trim();

            // hit 只在模型主动给出时才采信；置信度也一样，缺了给个中性值，不参与选图排序。
            var hitToken = json["hit"];
            var hit = hitToken != null && hitToken.Type != JTokenType.Null && (hitToken.Type == JTokenType.Boolean
                ? (bool)hitToken
                : hitToken.ToString().Trim().ToLowerInvariant() is "true" or "1" or "是" or "符合");

            var confidence = json["confidence"]?.Type is JTokenType.Float or JTokenType.Integer
                ? json["confidence"]!.Value<double>()
                : 0.6;

            return new TileVerdict(index, hit, confidence, seen, content.Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析复核结果。只看 has：这条问法下 seen 会被题目里的目标词带偏，不能拿来比对。
    /// 模型漏了 has（可能只回了 seen）时按「没有」处理——复核本来就是给漏判兜底的，宁缺勿错。
    /// </summary>
    private static TileVerdict? ParseConfirm(string content, int index)
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

            var hasToken = json["has"];
            var seenToken = json["seen"];
            if (hasToken == null && seenToken == null)
            {
                return null;
            }

            var has = hasToken != null && hasToken.Type != JTokenType.Null && (hasToken.Type == JTokenType.Boolean
                ? (bool)hasToken
                : hasToken.ToString().Trim().ToLowerInvariant() is "true" or "1" or "是" or "有");

            var confidence = json["confidence"]?.Type is JTokenType.Float or JTokenType.Integer
                ? json["confidence"]!.Value<double>()
                : 0.5;

            return new TileVerdict(index, has, confidence, seenToken?.ToString().Trim() ?? "", content.Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把解析结果压成一行写进主日志，别把整段响应贴进去。</summary>
    private static string DescribeResult(object result) => result switch
    {
        CaptchaAnswer a => $"target={a.Target} picks=[{string.Join(",", a.Picks)}] confidence={a.Confidence:F2}",
        TileVerdict v => $"#{v.Index} hit={v.Hit} conf={v.Confidence:F2} seen=\"{v.Seen}\"",
        string s => s.Length <= 120 ? s : s[..120] + "…",
        _ => result.ToString() ?? ""
    };

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

    /// <summary>
    /// 对单个模型发一次请求；撞限流或服务端抖动时退避重试。
    /// 响应怎么解析交给 <paramref name="parse"/>，读题（纯文本）与逐块判定（JSON）共用这一套重试／剥字段逻辑。
    /// </summary>
    private async Task<T?> TryModelAsync<T>(string endpoint, string model, string task, string prompt,
        byte[] image, bool jsonMode, string mime, string key, Func<string, T?> parse, CancellationToken ct)
        where T : class
    {
        var payload = BuildPayload(endpoint, model, prompt, image, jsonMode, mime);

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
                    round > 1 ? $"{task}（第 {round} 次尝试）" : task,
                    model, endpoint, payload.ToString(Newtonsoft.Json.Formatting.None), key, _log, image);

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

                        var answer = parse(content);
                        call?.Success(status, body, answer == null
                            ? "程序解析失败：回答里没有可用的内容"
                            : DescribeResult(answer));
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
    private static JObject BuildPayload(string endpoint, string model, string prompt, byte[] image,
        bool jsonMode, string mime)
    {
        var payload = new JObject
        {
            ["model"] = model,
            // 有些平台（含智谱）的 temperature 是 (0,1]，传 0 会被判参数错误，0.01 足够确定。
            ["temperature"] = 0.01,
            // 思考型模型的推理过程也占 token，200 容易被思考吃光、content 变成空串。
            ["max_tokens"] = 800,
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
                                ["url"] = $"data:{mime};base64," + Convert.ToBase64String(image),
                                ["detail"] = "high"
                            }
                        }
                    }
                }
            }
        };

        // 只有要 JSON 的任务才强制 response_format。读题要的是纯文本，
        // 加了反而可能让模型把整句话塞进 JSON 字符串里，解析时多一层要剥的东西。
        if (jsonMode)
        {
            payload["response_format"] = new JObject { ["type"] = "json_object" };
        }

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

    /// <summary>截断长文本，日志里不要贴整段响应。</summary>
    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
