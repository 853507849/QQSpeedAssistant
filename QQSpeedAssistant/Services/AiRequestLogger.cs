using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QQSpeedAssistant.Services;

/// <summary>
/// AI 调用专用日志：每一次向模型发请求，都把完整的请求体、响应体、状态码、耗时单独落盘。
/// 和 run-*.log 分开写，原因有两个：模型返回的内容动辄几千字，混进运行日志会把有用信息冲掉；
/// 排查「到底是提示词的问题，还是接口报错」时，一次调用一节，直接往上翻就够。
///
/// 目录按天归档，同一天的正文和图片放在同一个文件夹里，拷走或删掉都是一次操作：
/// User/log/ai/2026-11-11/ai-20261111.log
/// User/log/ai/2026-11-11/media/20261111-091233-511-000003.jpg
/// 过期清理见 <see cref="LogRetention"/>。
/// </summary>
public static class AiRequestLogger
{
    /// <summary>单段内容上限。异常响应可能是一整页 HTML，超过就截断，避免把磁盘写满。</summary>
    private const int MaxBlockChars = 256 * 1024;

    /// <summary>是否记录。由设置里的开关控制，关掉后 Begin 直接返回 null。</summary>
    public static bool Enabled { get; set; } = true;

    private static readonly Lock Gate = new();

    private static int _sequence;

    /// <summary>日期目录名的格式，也是识别「这是不是一天的数据」的依据。</summary>
    public const string DayFormat = "yyyy-MM-dd";

    /// <summary>请求体里的 data URI（图片 base64），正文里换成占位符，原图另存文件。</summary>
    private static readonly Regex DataUriPattern = new(
        @"data:(?<mime>image/[a-zA-Z0-9.+-]+);base64,(?<data>[A-Za-z0-9+/=\s]+)",
        RegexOptions.Compiled);

    static AiRequestLogger()
    {
        MigrateLegacyLayout();
    }

    /// <summary>AI 日志根目录，下面按天分子目录。</summary>
    public static string LogDir => Path.Combine(PathService.LogDir, "ai");

    /// <summary>某一天的归档目录：正文与 media 子目录都放在这里。</summary>
    public static string DayDirOf(DateTime time) => Path.Combine(LogDir, time.ToString(DayFormat));

    /// <summary>某一天的请求附件（图片）目录。</summary>
    public static string MediaDirOf(DateTime time) => Path.Combine(DayDirOf(time), "media");

    /// <summary>某一天的日志文件。</summary>
    public static string FileOf(DateTime time) => Path.Combine(DayDirOf(time), $"ai-{time:yyyyMMdd}.log");

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

        // 程序常常跨天常驻，清理不能只靠启动那一次。
        LogRetention.PurgeIfNewDay();

        var id = Interlocked.Increment(ref _sequence);
        var startedAt = DateTime.Now;

        var imageName = image is { Length: > 0 } ? SaveImage(image, startedAt, id) : null;
        var body = RedactImages(requestBody, imageName, image?.Length ?? 0);

        return new AiCall(id, scene, model, endpoint, MaskKey(apiKey), body, startedAt, log);
    }

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

    /// <summary>把请求用的图片存进当天的 media 子目录，返回文件名；失败返回 null，不影响调用本身。</summary>
    private static string? SaveImage(byte[] image, DateTime at, int id)
    {
        try
        {
            var dir = MediaDirOf(at);
            Directory.CreateDirectory(dir);

            var name = $"{at:yyyyMMdd-HHmmss-fff}-{id:D6}.jpg";
            File.WriteAllBytes(Path.Combine(dir, name), image);
            return name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把旧版扁平结构搬进按天目录：旧版是 User/log/ai/ai-yyyyMMdd.log 与 User/log/ai/media/*.jpg 平铺，
    /// 图片不带日期目录，长期下来既分不清是哪天，也没法按天整目录清理。
    /// </summary>
    private static void MigrateLegacyLayout()
    {
        try
        {
            if (!Directory.Exists(LogDir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(LogDir, "ai-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length <= 3 || !TryParseDay(name[3..], "yyyyMMdd", out var day))
                {
                    continue;
                }

                var target = FileOf(day);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // 同一天的目标已存在（升级当天已经按新结构写过），把旧内容接在后面而不是覆盖。
                if (File.Exists(target))
                {
                    File.AppendAllText(target, File.ReadAllText(file));
                    File.Delete(file);
                }
                else
                {
                    File.Move(file, target);
                }
            }

            var legacyMedia = Path.Combine(LogDir, "media");
            if (Directory.Exists(legacyMedia))
            {
                foreach (var file in Directory.EnumerateFiles(legacyMedia))
                {
                    var name = Path.GetFileName(file);

                    // 旧图片名形如 20261111-091233-511-000003.jpg，前 8 位就是日期。
                    if (name.Length < 8 || !TryParseDay(name[..8], "yyyyMMdd", out var day))
                    {
                        continue;
                    }

                    var dir = MediaDirOf(day);
                    Directory.CreateDirectory(dir);

                    var target = Path.Combine(dir, name);
                    if (File.Exists(target))
                    {
                        File.Delete(file);
                    }
                    else
                    {
                        File.Move(file, target);
                    }
                }

                // 搬空了就把旧 media 目录去掉，免得留一个空壳继续误导。
                if (!Directory.EnumerateFileSystemEntries(legacyMedia).Any())
                {
                    Directory.Delete(legacyMedia);
                }
            }
        }
        catch
        {
            // 迁移失败不影响程序启动，旧文件留在原地，下次启动再试。
        }
    }

    /// <summary>按指定格式解析日期，失败返回 false 而不是抛异常。</summary>
    internal static bool TryParseDay(string text, string format, out DateTime day)
        => DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

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
                    Directory.CreateDirectory(DayDirOf(_startedAt));
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
