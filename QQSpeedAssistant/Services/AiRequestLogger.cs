using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace QQSpeedAssistant.Services;

/// <summary>
/// AI 调用专用日志：每一次向模型发请求，都把完整的请求体、响应体、状态码、耗时单独落盘。
/// 和 run-*.log 分开写，原因有两个：模型返回的内容动辄几千字，混进运行日志会把有用信息冲掉；
/// 排查「到底是提示词的问题，还是接口报错」时，一次调用一节，直接往上翻就够。
/// 文本写入 User/log/ai/ai-yyyyMMdd.log，请求里带的图片另存到 User/log/ai/media/。
/// </summary>
public static class AiRequestLogger
{
    /// <summary>文本日志目录。</summary>
    public static string LogDir => Path.Combine(PathService.LogDir, "ai");

    /// <summary>请求附件（图片）目录。</summary>
    public static string MediaDir => Path.Combine(LogDir, "media");

    /// <summary>文本日志保留天数。</summary>
    private const int KeepDays = 14;

    /// <summary>图片保留天数（图片体积大，不必留那么久）。</summary>
    private const int KeepMediaDays = 7;

    /// <summary>单段内容上限。异常响应可能是一整页 HTML，超过就截断，避免把磁盘写满。</summary>
    private const int MaxBlockChars = 256 * 1024;

    /// <summary>是否记录。由设置里的开关控制，关掉后 Begin 直接返回 null。</summary>
    public static bool Enabled { get; set; } = true;

    private static readonly Lock Gate = new();

    private static int _sequence;

    /// <summary>请求体里的 data URI（图片 base64），正文里换成占位符，原图另存文件。</summary>
    private static readonly Regex DataUriPattern = new(
        @"data:(?<mime>image/[a-zA-Z0-9.+-]+);base64,(?<data>[A-Za-z0-9+/=\s]+)",
        RegexOptions.Compiled);

    static AiRequestLogger()
    {
        Purge();
    }

    /// <summary>
    /// 开始记录一次调用。返回的对象必须结束（<see cref="AiCall.Success"/> 或 <see cref="AiCall.Failure"/>），
    /// 用 using 包住即可：没显式结束的会补一条「未完成」。
    /// </summary>
    /// <param name="scene">场景名，例如「验证码识别」。</param>
    /// <param name="model">模型名。</param>
    /// <param name="endpoint">请求地址。</param>
    /// <param name="requestBody">完整请求体 JSON（可以带图片 base64，会自动换成占位符）。</param>
    /// <param name="apiKey">密钥，只记录脱敏后的片段。</param>
    /// <param name="log">主运行日志，用于写一行摘要；可为空。</param>
    /// <param name="image">请求里带的图片原始字节，会存成 media 下的独立文件；可为空。</param>
    public static AiCall? Begin(string scene, string model, string endpoint, string requestBody,
        string? apiKey, LogService? log, byte[]? image = null)
    {
        if (!Enabled)
        {
            return null;
        }

        var id = Interlocked.Increment(ref _sequence);
        var startedAt = DateTime.Now;

        var imageName = image is { Length: > 0 } ? SaveImage(image, startedAt, id) : null;
        var body = RedactImages(requestBody, imageName, image?.Length ?? 0);

        return new AiCall(id, scene, model, endpoint, MaskKey(apiKey), body, startedAt, log);
    }

    /// <summary>今天的日志文件路径。</summary>
    public static string FileOf(DateTime time) => Path.Combine(LogDir, $"ai-{time:yyyyMMdd}.log");

    /// <summary>把请求里的 base64 图片换成占位符，正文保持可读。</summary>
    private static string RedactImages(string body, string? imageName, int byteLength)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "<空>";
        }

        return DataUriPattern.Replace(body, match =>
        {
            var mime = match.Groups["mime"].Value;
            var size = byteLength > 0 ? $"{byteLength / 1024.0:F1} KB" : $"{match.Groups["data"].Length} 字符";

            // 不加引号：原地址本来就写在 JSON 的字符串里，外面那对引号留给它。
            return imageName != null
                ? $"<{mime} {size}，原图见 media/{imageName}>"
                : $"<{mime} {size} base64，未保存原图>";
        });
    }

    /// <summary>密钥脱敏：只留头尾，避免日志文件本身变成泄密源。</summary>
    private static string MaskKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "（未设置，可能是本地模型）";
        }

        var key = apiKey.Trim();
        return key.Length <= 10
            ? $"Bearer ****（长度 {key.Length}）"
            : $"Bearer {key[..4]}****{key[^4..]}（长度 {key.Length}）";
    }

    /// <summary>把请求用的图片存成独立文件，返回文件名；失败返回 null，不影响调用本身。</summary>
    private static string? SaveImage(byte[] image, DateTime at, int id)
    {
        try
        {
            Directory.CreateDirectory(MediaDir);
            var name = $"{at:yyyyMMdd-HHmmss-fff}-{id:D6}.jpg";
            File.WriteAllBytes(Path.Combine(MediaDir, name), image);
            return name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清理过期日志与图片。放在静态构造里，进程内只跑一次。</summary>
    private static void Purge()
    {
        try
        {
            var now = DateTime.Now;

            if (Directory.Exists(LogDir))
            {
                foreach (var file in Directory.EnumerateFiles(LogDir, "ai-*.log"))
                {
                    if (now - File.GetLastWriteTime(file) > TimeSpan.FromDays(KeepDays))
                    {
                        File.Delete(file);
                    }
                }
            }

            if (Directory.Exists(MediaDir))
            {
                foreach (var file in Directory.EnumerateFiles(MediaDir))
                {
                    if (now - File.GetLastWriteTime(file) > TimeSpan.FromDays(KeepMediaDays))
                    {
                        File.Delete(file);
                    }
                }
            }
        }
        catch
        {
            // 清理失败与业务无关。
        }
    }

    /// <summary>正文截断，保留原始长度提示。</summary>
    private static string Clamp(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<空>";
        }

        return text.Length <= MaxBlockChars
            ? text
            : text[..MaxBlockChars] + $"\n[已截断，原始 {text.Length} 字符]";
    }

    /// <summary>一次 AI 调用的记录。</summary>
    public sealed class AiCall : IDisposable
    {
        private readonly int _id;
        private readonly string _scene;
        private readonly string _model;
        private readonly string _endpoint;
        private readonly string _auth;
        private readonly string _requestBody;
        private readonly DateTime _startedAt;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly LogService? _log;
        private bool _closed;

        internal AiCall(int id, string scene, string model, string endpoint, string auth,
            string requestBody, DateTime startedAt, LogService? log)
        {
            _id = id;
            _scene = scene;
            _model = model;
            _endpoint = endpoint;
            _auth = auth;
            _requestBody = requestBody;
            _startedAt = startedAt;
            _log = log;
        }

        /// <summary>记录成功返回。responseBody 为完整响应体，parsed 为程序解析出的结论。</summary>
        public void Success(int statusCode, string responseBody, string? parsed = null)
            => Close("OK", statusCode, responseBody, parsed, null);

        /// <summary>记录失败返回。statusCode 为 0 表示没拿到 HTTP 响应。</summary>
        public void Failure(string reason, int statusCode = 0, string? responseBody = null)
            => Close("FAIL", statusCode, responseBody, null, reason);

        /// <inheritdoc />
        public void Dispose()
        {
            if (!_closed)
            {
                Close("ABORT", 0, null, null, "调用方没有显式结束（取消或中途抛异常）");
            }
        }

        /// <summary>一次性写出这一节记录；写盘失败不影响业务流程。</summary>
        private void Close(string outcome, int statusCode, string? responseBody, string? parsed, string? note)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _watch.Stop();

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine($"[{_startedAt:yyyy-MM-dd HH:mm:ss.fff}] #{_id:D6}  {_scene}");
                sb.AppendLine($"model    : {_model}");
                sb.AppendLine($"endpoint : {_endpoint}");
                sb.AppendLine($"auth     : {_auth}");
                sb.AppendLine($"outcome  : {outcome}    status: {(statusCode == 0 ? "-" : statusCode.ToString())}    耗时: {_watch.ElapsedMilliseconds} ms");
                if (!string.IsNullOrEmpty(note))
                {
                    sb.AppendLine($"note     : {note}");
                }

                sb.AppendLine("---- REQUEST ----");
                sb.AppendLine(_requestBody);
                sb.AppendLine("---- RESPONSE ----");
                sb.AppendLine(Clamp(responseBody));

                if (!string.IsNullOrEmpty(parsed))
                {
                    sb.AppendLine("---- PARSED ----");
                    sb.AppendLine(Clamp(parsed));
                }

                sb.AppendLine();

                var file = FileOf(_startedAt);
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDir);
                    File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
                }

                // 主日志只留一行索引，正文都在独立文件里。
                var relative = Path.GetRelativePath(PathService.BaseDir, file).Replace('\\', '/');
                _log?.Info($"AI #{_id:D6} [{_scene}] {_model} → {outcome} {(statusCode == 0 ? "" : statusCode.ToString())}"
                           + $"（{_watch.ElapsedMilliseconds} ms），详见 {relative}", "AI");
            }
            catch
            {
                // 日志写不进去不能影响调用。
            }
        }
    }
}
