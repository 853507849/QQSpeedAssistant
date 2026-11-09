using OpenCvSharp;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>验证码里的一张候选图（图片坐标）。Index 从 1 开始，方便直接报给视觉模型。</summary>
public sealed record CaptchaTile(int Index, int X, int Y, int Width, int Height)
{
    /// <summary>中心点 X。</summary>
    public int CenterX => X + Width / 2;

    /// <summary>中心点 Y。</summary>
    public int CenterY => Y + Height / 2;

    /// <summary>矩形。</summary>
    public Rect Rect => new(X, Y, Width, Height);
}

/// <summary>
/// 把验证码截图切成一张张候选图。
/// 这个验证码是「选择最符合描述的图片：XXX」，图块数量不固定（实测 3x2=6 块，也可能是 9 块），
/// 不能按固定宫格硬切，做法是：
/// ① 用「彩色或偏暗」把照片区域从对话框底色里分出来（底色接近纯白，饱和度和亮度都过不了阈值）；
/// ② 连通域直接给出每张照片的外框（实测图块之间只有 2～3 像素间隙，任何形态学处理都会把它们粘成一片）；
/// ③ 按尺寸一致性剔除题目文字、盾牌图标和「确定」按钮。
/// </summary>
public static class CaptchaGrid
{
    /// <summary>单个图块的最小边长（像素）。按钮（实测 62x29）、图标、题目文字都小于这个值。</summary>
    private const int MinTileSide = 55;

    /// <summary>图块之间允许的尺寸差异（相对中位数），超过就当成别的控件。</summary>
    private const double SizeTolerance = 0.35;

    /// <summary>切图结果。ContentBox 是真正有画面的区域（PrintWindow 会留黑边），点击时按它换算比例。</summary>
    public sealed record Result(IReadOnlyList<CaptchaTile> Tiles, string Report, Rect ContentBox);

    /// <summary>切图。</summary>
    public static Result Detect(Mat image)
    {
        var report = new System.Text.StringBuilder();
        if (image.Empty())
        {
            return new Result([], "图像为空", new Rect(0, 0, image.Width, image.Height));
        }

        using var bgr = ToBgr(image);
        report.AppendLine($"图像 {bgr.Width}x{bgr.Height}");

        // PrintWindow 有时画不满整块位图，右侧／下方是纯黑；黑边在掩码里等同于「内容」，
        // 会把所有照片连成一整块，所以先按非黑区域收紧到真实内容。
        var box = FindContentBox(bgr);
        using var content = new Mat(bgr, box);
        if (box.X > 0 || box.Y > 0 || box.Width < bgr.Width || box.Height < bgr.Height)
        {
            report.AppendLine($"有效内容 ({box.X},{box.Y},{box.Width}x{box.Height})，先去掉无效边再切图");
        }

        // 主路径：饱和度／亮度掩码。这版验证码的图块都是彩色照片，底色是 #F0F0F0 级的浅灰。
        var candidates = ExtractTiles(content, useEdges: false, report);
        if (candidates.Count < 2)
        {
            report.AppendLine("彩色／偏暗掩码只找到 " + candidates.Count + " 块，改用边缘掩码重试");
            candidates = ExtractTiles(content, useEdges: true, report);
        }

        // 子图坐标要平移回整图坐标，后面点击和标注都用整图坐标。
        if (box.X != 0 || box.Y != 0)
        {
            candidates = candidates
                .Select(r => new Rect(r.X + box.X, r.Y + box.Y, r.Width, r.Height))
                .ToList();
        }

        if (candidates.Count == 0)
        {
            return new Result([], report.AppendLine("没有找到任何图块").ToString(), box);
        }

        // 图块尺寸应当基本一致：按中位宽高把不像图块的东西剔掉。
        var medianWidth = Median(candidates.Select(c => c.Width));
        var medianHeight = Median(candidates.Select(c => c.Height));
        var kept = new List<Rect>();
        foreach (var rect in candidates)
        {
            var widthOk = Math.Abs(rect.Width - medianWidth) <= medianWidth * SizeTolerance;
            var heightOk = Math.Abs(rect.Height - medianHeight) <= medianHeight * SizeTolerance;
            if (widthOk && heightOk)
            {
                kept.Add(rect);
            }
            else
            {
                report.AppendLine($"  跳过尺寸异常 ({rect.X},{rect.Y},{rect.Width}x{rect.Height})");
            }
        }

        if (kept.Count == 0)
        {
            kept = candidates;
        }

        // 先按行分组（y 中心接近的算同一行），行内按 x 排序，最后统一编号。
        var tiles = new List<CaptchaTile>();
        var index = 1;
        var rowTolerance = Math.Max(8, medianHeight * 0.5);
        foreach (var row in kept.OrderBy(r => r.Y + r.Height / 2.0)
                     .GroupBy(r => (int)Math.Round((r.Y + r.Height / 2.0) / rowTolerance))
                     .OrderBy(g => g.Key))
        {
            foreach (var rect in row.OrderBy(r => r.X))
            {
                tiles.Add(new CaptchaTile(index++, rect.X, rect.Y, rect.Width, rect.Height));
                report.AppendLine($"  #{index - 1} ({rect.X},{rect.Y},{rect.Width}x{rect.Height})");
            }
        }

        return new Result(tiles, report.ToString(), box);
    }

    /// <summary>
    /// 找出真正有画面的区域：PrintWindow 抓「QQ安全中心」时，位图常常比窗口内容大，
    /// 没画到的部分是纯黑。按行／列统计「非黑像素」，把四周整片纯黑切掉。
    /// 阈值取得很低（亮度 &gt; 6），深色照片不会被误裁。
    /// </summary>
    private static Rect FindContentBox(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        using var notBlack = new Mat();
        Cv2.Threshold(gray, notBlack, 6, 1, ThresholdTypes.Binary);

        using var colSum = new Mat();
        using var rowSum = new Mat();
        Cv2.Reduce(notBlack, colSum, ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_32S);
        Cv2.Reduce(notBlack, rowSum, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_32S);

        // 一行／列里非黑像素超过 1% 才算「有内容」，避免零星噪点撑大边界。
        var minCol = Math.Max(2, (int)(bgr.Height * 0.01));
        var minRow = Math.Max(2, (int)(bgr.Width * 0.01));

        var left = 0;
        var right = bgr.Width - 1;
        var top = 0;
        var bottom = bgr.Height - 1;

        while (left < right && colSum.At<int>(0, left) < minCol)
        {
            left++;
        }

        while (right > left && colSum.At<int>(0, right) < minCol)
        {
            right--;
        }

        while (top < bottom && rowSum.At<int>(top, 0) < minRow)
        {
            top++;
        }

        while (bottom > top && rowSum.At<int>(bottom, 0) < minRow)
        {
            bottom--;
        }

        return new Rect(left, top, Math.Max(1, right - left + 1), Math.Max(1, bottom - top + 1));
    }

    /// <summary>取连通域候选框。useEdges=true 时把边缘也算作内容，用于照片边界不明显的版本。</summary>
    private static List<Rect> ExtractTiles(Mat bgr, bool useEdges, System.Text.StringBuilder report)
    {
        using var mask = useEdges ? BuildEdgeMask(bgr) : BuildColorMask(bgr);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            PixelConnectivity.Connectivity8, MatType.CV_32S);

        var minArea = bgr.Width * bgr.Height * 0.004;
        var rects = new List<Rect>();
        report.AppendLine($"  [{(useEdges ? "边缘" : "彩色")}] 连通域 {count - 1} 个，最小面积 {minArea:F0}");
        for (var i = 1; i < count; i++)
        {
            var x = stats.At<int>(i, 0);
            var y = stats.At<int>(i, 1);
            var w = stats.At<int>(i, 2);
            var h = stats.At<int>(i, 3);
            var area = stats.At<int>(i, 4);

            if (area < minArea || w < MinTileSide || h < MinTileSide)
            {
                continue;
            }

            var ratio = (double)w / h;
            if (ratio is < 0.45 or > 2.4)
            {
                continue;
            }

            // 两张图块粘连成一个超宽块时，按高度均分切开。
            var expectedParts = (int)Math.Round((double)w / h);
            if (expectedParts >= 2)
            {
                var partWidth = w / expectedParts;
                for (var part = 0; part < expectedParts; part++)
                {
                    rects.Add(new Rect(x + part * partWidth, y, partWidth, h));
                }

                report.AppendLine($"  粘连块 ({x},{y},{w}x{h}) 已按高度切成 {expectedParts} 块");
                continue;
            }

            rects.Add(new Rect(x, y, w, h));
            report.AppendLine($"  候选 ({x},{y},{w}x{h}) 面积 {area}");
        }

        return rects;
    }

    /// <summary>彩色或偏暗的像素：图块是照片，对话框底色是浅灰／白，天然被排除。</summary>
    private static Mat BuildColorMask(Mat bgr)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        var channels = Cv2.Split(hsv);
        // Split 出来是 H／S／V 三个通道：这里要的是饱和度（含「彩色」信息）和亮度。
        channels[0].Dispose();
        using var saturation = channels[1];
        using var value = channels[2];

        using var saturated = new Mat();
        Cv2.Threshold(saturation, saturated, 18, 255, ThresholdTypes.Binary);

        using var dark = new Mat();
        Cv2.Threshold(value, dark, 210, 255, ThresholdTypes.BinaryInv);

        var mask = new Mat();
        Cv2.BitwiseOr(saturated, dark, mask);
        return mask;
    }

    /// <summary>边缘掩码：给「照片非常浅、饱和度上不去」的版本兜底。</summary>
    private static Mat BuildEdgeMask(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);

        using var gradX = new Mat();
        using var gradY = new Mat();
        Cv2.Sobel(blurred, gradX, MatType.CV_16S, 1, 0, 3);
        Cv2.Sobel(blurred, gradY, MatType.CV_16S, 0, 1, 3);
        using var absX = new Mat();
        using var absY = new Mat();
        Cv2.ConvertScaleAbs(gradX, absX);
        Cv2.ConvertScaleAbs(gradY, absY);

        using var edges = new Mat();
        Cv2.Add(absX, absY, edges);

        var mask = new Mat();
        Cv2.Threshold(edges, mask, 40, 255, ThresholdTypes.Binary);

        // 只做「闭」不做「开」：间隙只有 2～3 像素，闭运算核一大就会把相邻图块粘起来。
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        return mask;
    }

    /// <summary>把原图统一成 3 通道 BGR。</summary>
    private static Mat ToBgr(Mat source)
    {
        if (source.Channels() == 3)
        {
            return source.Clone();
        }

        var code = source.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.GRAY2BGR;
        var converted = new Mat();
        Cv2.CvtColor(source, converted, code);
        return converted;
    }

    /// <summary>中位数。</summary>
    private static double Median(IEnumerable<int> values)
    {
        var list = values.OrderBy(v => v).ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        var mid = list.Count / 2;
        return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2.0;
    }

    /// <summary>
    /// 给每张图块画红框和编号，再交给视觉模型：编号直接落在图上，模型不用去猜行列，
    /// 回答只要给编号，点击时就不会错位。
    /// </summary>
    public static Mat Annotate(Mat source, IReadOnlyList<CaptchaTile> tiles)
    {
        var canvas = source.Clone();
        var thickness = Math.Max(2, Math.Min(canvas.Width, canvas.Height) / 200);
        var fontScale = Math.Max(0.6, Math.Min(canvas.Width, canvas.Height) / 520.0);

        foreach (var tile in tiles)
        {
            Cv2.Rectangle(canvas, tile.Rect, new Scalar(0, 0, 255), thickness);

            var label = tile.Index.ToString();
            var origin = new Point(tile.X + 8, tile.Y + 30);

            // 编号压在照片上，先垫一块白底再写红字，任何底图上都看得清。
            var size = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, fontScale, thickness, out _);
            Cv2.Rectangle(canvas,
                new Rect(origin.X - 4, origin.Y - size.Height - 6, size.Width + 10, size.Height + 12),
                new Scalar(255, 255, 255), -1);
            Cv2.PutText(canvas, label, origin, HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 0, 255),
                thickness, LineTypes.AntiAlias);
        }

        return canvas;
    }
}
