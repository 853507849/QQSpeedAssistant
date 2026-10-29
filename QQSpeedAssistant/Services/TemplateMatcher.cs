using Newtonsoft.Json;
using OpenCvSharp;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>模板匹配结果。</summary>
public sealed record TemplateMatch(bool Found, double Score, int X, int Y, int Width, int Height, double Scale = 1)
{
    /// <summary>中心点坐标。</summary>
    public int CenterX => X + Width / 2;

    /// <summary>中心点坐标。</summary>
    public int CenterY => Y + Height / 2;

    /// <summary>未匹配结果。</summary>
    public static TemplateMatch Miss() => new(false, 0, 0, 0, 0, 0);
}

/// <summary>
/// 多尺度模板匹配。
/// <para>
/// 不依赖「模板采集时的窗口宽度」是否准确：先在缩略图上粗扫 0.3～2.6 倍的全部缩放比，
/// 再对最像的几档在全分辨率上细化。命中后把可用缩放比记下来，后续帧只做一次匹配。
/// </para>
/// <para>
/// 之所以要这样：游戏窗口尺寸会随分辨率和 DPI 缩放变化（800x600 客户端在 150% 缩放下是
/// 1200x900），而模板是按采集当时的画面裁出来的。只有一个固定缩放比时，界面明明在眼前
/// 也只能匹配到 0.5～0.6，被判定成「模板不匹配」。
/// </para>
/// </summary>
public sealed class TemplateMatcher
{
    private const double BaseWidth = 1920d;

    /// <summary>粗扫使用的小图宽度，越小越快。</summary>
    private const int CoarseWidth = 480;

    private const double MinScale = 0.30;
    private const double MaxScale = 2.60;
    private const double CoarseStep = 0.10;
    private const double RefineStep = 0.03;
    private const double RefineRadius = 0.10;

    /// <summary>粗扫被完全信任的最低分：低于它说明画面里根本没有这个东西，不必全分辨率细化。</summary>
    private const double CoarseFloor = 0.35;

    /// <summary>细化到这个分数就不再看别的缩放比了，省时间。</summary>
    private const double ConfidentScore = 0.95;

    /// <summary>缩放比缓存上限，超出时整体清空，避免长时间运行内存缓慢增长。</summary>
    private const int ScaledCacheLimit = 600;

    private readonly LogService _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Mat> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Mat> _scaledCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _baseWidthCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _scaleHint = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 最近一次匹配所用画面的宽度。BuildScaleGrid 用它反推「元数据缩放比」，
    /// 由 Find 在调用前写入，避免把 frame 宽度一路透传。
    /// </summary>
    private double _frameWidthHint = BaseWidth;

    /// <summary>构造匹配器，模板目录默认为程序下的 templates。</summary>
    public TemplateMatcher(LogService log)
    {
        _log = log;
    }

    /// <summary>模板文件完整路径，找不到返回 null。</summary>
    public string? ResolvePath(string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
        {
            return null;
        }

        var fileName = templateName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? templateName
            : $"{templateName}.png";

        if (Path.IsPathRooted(fileName))
        {
            return File.Exists(fileName) ? fileName : null;
        }

        var direct = Path.Combine(PathService.TemplateDir, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        if (!Directory.Exists(PathService.TemplateDir))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(PathService.TemplateDir, Path.GetFileName(fileName), SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    /// <summary>模板是否已存在。</summary>
    public bool TemplateExists(string templateName) => ResolvePath(templateName) != null;

    /// <summary>
    /// 在画面中查找模板，未找到时返回 <see cref="TemplateMatch.Found"/> 为 false 的结果。
    /// 内部缓存不是线程安全的，而号池模式会并发跑多个账号，因此统一串行化（单次匹配 60～300ms，可接受）。
    /// </summary>
    public TemplateMatch Find(Mat frame, string templateName, double threshold)
    {
        lock (_gate)
        {
            return FindCore(frame, templateName, threshold);
        }
    }

    private TemplateMatch FindCore(Mat frame, string templateName, double threshold)
    {
        var path = ResolvePath(templateName);
        if (path == null)
        {
            return TemplateMatch.Miss();
        }

        var template = LoadTemplate(path);
        if (template == null || template.Empty())
        {
            return TemplateMatch.Miss();
        }

        try
        {
            using var gray = ToGray(frame);
            _frameWidthHint = gray.Width;

            // 热路径：上一次在这张模板上成功的缩放比，先直接试一次全分辨率匹配。
            if (_scaleHint.TryGetValue(path, out var hint))
            {
                var direct = MatchAt(gray, template, path, hint, threshold);
                if (direct.Found)
                {
                    return direct;
                }

                _scaleHint.Remove(path);
            }

            var candidates = CoarseSearch(gray, template, path);
            if (candidates.Count == 0)
            {
                return TemplateMatch.Miss();
            }

            var best = TemplateMatch.Miss();
            foreach (var scale in candidates)
            {
                var match = MatchAt(gray, template, path, scale, threshold);
                if (match.Score > best.Score)
                {
                    best = match;
                }

                // 已经很确定就不再往下试，够用即可；不够确定则继续找更贴合的那档缩放。
                if (best.Found && best.Score >= ConfidentScore)
                {
                    break;
                }
            }

            if (best.Found)
            {
                // 命中就把缩放比定下来，后续帧只做一次全分辨率匹配。
                _scaleHint[path] = best.Scale;
            }
            // 没命中但已经很接近，说明缩放比大致是对的（例如界面正在过渡），记下来省掉下一轮粗扫。
            else if (best.Score >= 0.5)
            {
                _scaleHint[path] = best.Scale;
            }

            return best;
        }
        catch (Exception ex)
        {
            _log.Warn($"模板匹配异常（{templateName}）：{ex.Message}", "识别");
            return TemplateMatch.Miss();
        }
    }

    /// <summary>清除模板缓存，替换模板文件后调用。</summary>
    public void ClearCache()
    {
        lock (_gate)
        {
            foreach (var mat in _cache.Values)
            {
                mat.Dispose();
            }

            foreach (var mat in _scaledCache.Values)
            {
                mat.Dispose();
            }

            _cache.Clear();
            _scaledCache.Clear();
            _baseWidthCache.Clear();
            _scaleHint.Clear();
        }
    }

    /// <summary>
    /// 粗扫：在缩略图上把 0.3～2.6 倍的缩放比都试一遍，返回最像的几档（已换算回全分辨率）。
    /// </summary>
    private List<double> CoarseSearch(Mat gray, Mat template, string path)
    {
        var factor = Math.Min(1d, CoarseWidth / (double)gray.Width);
        Mat small = gray;
        var ownsSmall = false;
        if (factor < 0.999)
        {
            small = new Mat();
            Cv2.Resize(gray, small, new Size(), factor, factor, InterpolationFlags.Area);
            ownsSmall = true;
        }

        try
        {
            var scored = new List<(double Scale, double Score)>();
            foreach (var scale in BuildScaleGrid(path))
            {
                var scaled = GetScaled(template, $"c{path}", scale * factor);
                if (scaled.Width < 6 || scaled.Height < 6 ||
                    scaled.Width > small.Width || scaled.Height > small.Height)
                {
                    continue;
                }

                using var result = new Mat();
                Cv2.MatchTemplate(small, scaled, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out var maxValue, out _, out _);
                scored.Add((scale, maxValue));
            }

            if (scored.Count == 0)
            {
                return [];
            }

            scored.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (scored[0].Score < CoarseFloor)
            {
                return [scored[0].Scale];
            }

            // 取前两名，各自从中心向外细化：最像的那档先试，命中即可提前结束。
            var refined = new List<double>();
            foreach (var item in scored.Take(2))
            {
                var steps = (int)Math.Round(RefineRadius / RefineStep);
                for (var step = 0; step <= steps; step++)
                {
                    AddScale(item.Scale, 0);
                    if (step > 0)
                    {
                        AddScale(item.Scale, step * RefineStep);
                        AddScale(item.Scale, -step * RefineStep);
                    }
                }

                void AddScale(double center, double offset)
                {
                    var s = Math.Round(center + offset, 3);
                    if (s is >= MinScale and <= MaxScale && !refined.Contains(s))
                    {
                        refined.Add(s);
                    }
                }
            }

            return refined;
        }
        finally
        {
            if (ownsSmall)
            {
                small.Dispose();
            }
        }
    }

    /// <summary>在指定缩放比下做一次全分辨率匹配。</summary>
    private TemplateMatch MatchAt(Mat gray, Mat template, string path, double scale, double threshold)
    {
        var scaled = GetScaled(template, path, scale);
        if (scaled.Width < 6 || scaled.Height < 6 ||
            scaled.Width > gray.Width || scaled.Height > gray.Height)
        {
            return TemplateMatch.Miss();
        }

        using var result = new Mat();
        Cv2.MatchTemplate(gray, scaled, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out var maxValue, out _, out var maxLocation);
        return new TemplateMatch(maxValue >= threshold, maxValue, maxLocation.X, maxLocation.Y,
            scaled.Width, scaled.Height, scale);
    }

    /// <summary>
    /// 候选缩放比：0.3～2.6 每 0.1 一档，再补上「元数据推算值」和 1.0。
    /// 元数据可能不准（采集时窗口尺寸和画面尺寸不是一回事），所以只作为补充而不是唯一依据。
    /// </summary>
    private List<double> BuildScaleGrid(string path)
    {
        var grid = new SortedSet<double>();
        for (var s = MinScale; s <= MaxScale + 1e-9; s += CoarseStep)
        {
            grid.Add(Math.Round(s, 2));
        }

        grid.Add(1d);
        var primary = ResolveBaseWidth(path);
        if (primary > 100)
        {
            var scale = _frameWidthHint / primary;
            if (scale is >= MinScale and <= MaxScale)
            {
                grid.Add(Math.Round(scale, 2));
            }
        }

        return [.. grid];
    }

    /// <summary>读取模板采集时的窗口宽度，没有 meta 文件时按 1920 基准处理。</summary>
    private double ResolveBaseWidth(string templatePath)
    {
        if (_baseWidthCache.TryGetValue(templatePath, out var cached))
        {
            return cached;
        }

        var width = BaseWidth;
        try
        {
            var metaPath = Path.ChangeExtension(templatePath, ".meta.json");
            if (File.Exists(metaPath))
            {
                var meta = JsonConvert.DeserializeObject<Dictionary<string, double>>(File.ReadAllText(metaPath));
                if (meta != null && meta.TryGetValue("width", out var captured) && captured > 100)
                {
                    width = captured;
                }
            }
        }
        catch
        {
            // meta 损坏时退回基准宽度，不影响匹配。
        }

        _baseWidthCache[templatePath] = width;
        return width;
    }

    /// <summary>读取模板灰度图，使用 ImDecode 以兼容中文路径。</summary>
    private Mat? LoadTemplate(string path)
    {
        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var mat = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
            if (mat.Empty())
            {
                mat.Dispose();
                return null;
            }

            _cache[path] = mat;
            return mat;
        }
        catch (Exception ex)
        {
            _log.Warn($"模板读取失败（{path}）：{ex.Message}", "识别");
            return null;
        }
    }

    /// <summary>按缩放比取模板（带缓存），1.0 时直接返回原图。</summary>
    private Mat GetScaled(Mat template, string cacheKey, double scale)
    {
        if (scale is <= 0 or > 3 || Math.Abs(scale - 1d) < 0.01)
        {
            return template;
        }

        var key = $"{cacheKey}|{scale:F3}";
        if (_scaledCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_scaledCache.Count > ScaledCacheLimit)
        {
            foreach (var mat in _scaledCache.Values)
            {
                mat.Dispose();
            }

            _scaledCache.Clear();
        }

        var scaled = new Mat();
        var interpolation = scale < 1d ? InterpolationFlags.Area : InterpolationFlags.Linear;
        Cv2.Resize(template, scaled, new Size(), scale, scale, interpolation);
        _scaledCache[key] = scaled;
        return scaled;
    }

    /// <summary>转为单通道灰度图。</summary>
    private static Mat ToGray(Mat frame)
    {
        var gray = new Mat();
        if (frame.Channels() == 1)
        {
            frame.CopyTo(gray);
        }
        else
        {
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        }

        return gray;
    }
}
