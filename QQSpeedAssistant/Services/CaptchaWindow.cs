using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>找到的验证码窗口（屏幕坐标）。</summary>
public sealed record CaptchaWindowInfo(nint Handle, string Title, int Left, int Top, int Width, int Height)
{
    /// <summary>窗口是否还有效（用户点掉或答对后窗口会销毁）。</summary>
    public bool IsAlive => Handle != nint.Zero && NativeMethods.IsWindowVisible(Handle);
}

/// <summary>
/// 定位并抓取「QQ安全中心」图片验证码窗口。
/// 这个窗口是独立顶层窗口（CEF 自绘），既不是游戏画面的一部分，也没有 UIA 结构可读，
/// 只能按标题找到句柄，再用 PrintWindow 把内容画出来做图像识别。
/// </summary>
public static class CaptchaWindowLocator
{
    /// <summary>窗口再小就不可能是验证码弹窗（实测 408x400 左右）。</summary>
    private const int MinWidth = 260;

    /// <summary>窗口最小高度。</summary>
    private const int MinHeight = 220;

    /// <summary>标题关键字默认值，设置里的值为空时使用。</summary>
    private const string FallbackTitles = "QQ安全中心|安全中心|安全验证|验证码";

    /// <summary>按设置里的标题关键字找出所有验证码窗口，最上层／最大的排前面。</summary>
    public static IReadOnlyList<CaptchaWindowInfo> FindAll(AppSettings settings)
    {
        var keywords = SplitKeywords(settings.CaptchaWindowTitles);
        var found = new List<CaptchaWindowInfo>();
        var selfPid = Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
                if ((int)pid == selfPid)
                {
                    return true;
                }

                var title = NativeMethods.GetWindowTitle(hWnd);
                if (title.Length == 0)
                {
                    return true;
                }

                var matched = keywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (!matched)
                {
                    return true;
                }

                if (!NativeMethods.GetWindowRect(hWnd, out var rect))
                {
                    return true;
                }

                var width = rect.Width;
                var height = rect.Height;
                if (width < MinWidth || height < MinHeight)
                {
                    return true;
                }

                found.Add(new CaptchaWindowInfo(hWnd, title, rect.Left, rect.Top, width, height));
            }
            catch
            {
                // 枚举过程中窗口可能已销毁，直接跳过。
            }

            return true;
        }, nint.Zero);

        // 越靠前的窗口越可能是新弹出的那个：按窗口在 Z 序里的先后（EnumWindows 本身就是 Z 序）排序即可。
        return found;
    }

    /// <summary>取第一个验证码窗口。</summary>
    public static CaptchaWindowInfo? Find(AppSettings settings) => FindAll(settings).FirstOrDefault();

    /// <summary>
    /// 重新读一次窗口矩形。弹窗刚出来时内容还没撑开，窗口会再长一次；
    /// 一直用第一次抓到的矩形去建位图，PrintWindow 就只画左上角一小块，剩下全是黑的。
    /// </summary>
    public static CaptchaWindowInfo Refresh(CaptchaWindowInfo window)
    {
        try
        {
            if (!NativeMethods.GetWindowRect(window.Handle, out var rect))
            {
                return window;
            }

            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return window;
            }

            return window with { Left = rect.Left, Top = rect.Top, Width = rect.Width, Height = rect.Height };
        }
        catch
        {
            return window;
        }
    }

    /// <summary>按标题关键字轮询等待验证码窗口出现。</summary>
    public static CaptchaWindowInfo? WaitFor(AppSettings settings, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(200, timeoutMs));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var window = Find(settings);
            if (window != null)
            {
                return window;
            }

            Thread.Sleep(250);
        }

        return Find(settings);
    }

    /// <summary>标题关键字拆分，空值回退到默认关键字。</summary>
    private static string[] SplitKeywords(string? raw)
    {
        var text = string.IsNullOrWhiteSpace(raw) ? FallbackTitles : raw;
        return text.Split(['|', ',', ';', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// 抓一张验证码窗口的图（BGR）。先屏幕拷贝：像素与屏幕一一对应，点击坐标不用换算；
    /// 拷不到（窗口被遮挡且激活失败）再退回 PrintWindow 离屏渲染。
    /// </summary>
    public static Mat? Capture(CaptchaWindowInfo window, LogService? log = null)
    {
        if (window.Width <= 0 || window.Height <= 0)
        {
            return null;
        }

        var viaScreen = CaptureByScreen(window, log);
        if (viaScreen != null && !IsBlank(viaScreen))
        {
            return viaScreen;
        }

        viaScreen?.Dispose();

        var viaPrint = CaptureByPrintWindow(window);
        if (viaPrint != null && !IsBlank(viaPrint))
        {
            return viaPrint;
        }

        viaPrint?.Dispose();
        return null;
    }

    /// <summary>PrintWindow 离屏抓图。</summary>
    public static Mat? CaptureByPrintWindow(CaptchaWindowInfo window)
    {
        try
        {
            using var bitmap = new Bitmap(window.Width, window.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                bool ok;
                try
                {
                    ok = NativeMethods.PrintWindow(window.Handle, hdc, NativeMethods.PW_RENDERFULLCONTENT);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }

                if (!ok)
                {
                    return null;
                }
            }

            using var mat = BitmapConverter.ToMat(bitmap);
            return ToBgr(mat);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 屏幕拷贝（窗口必须可见、未被遮挡）。用 BitBlt 而不是 Graphics.CopyFromScreen：
    /// 后者会把 Rop 交给 Enum 校验，`SourceCopy | CaptureBlt` 这种组合会直接抛参数异常，
    /// 而 BitBlt 的 Rop 是纯位掩码，能正常带上 CAPTUREBLT（分层窗口也要拷进来）。
    /// </summary>
    public static Mat? CaptureByScreen(CaptchaWindowInfo window, LogService? log)
    {
        try
        {
            NativeMethods.Activate(window.Handle);
            Thread.Sleep(200);

            var screenDc = NativeMethods.GetDC(nint.Zero);
            if (screenDc == nint.Zero)
            {
                return null;
            }

            nint memoryDc = nint.Zero;
            nint bitmap = nint.Zero;
            nint previous = nint.Zero;
            try
            {
                memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
                bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, window.Width, window.Height);
                if (memoryDc == nint.Zero || bitmap == nint.Zero)
                {
                    return null;
                }

                previous = NativeMethods.SelectObject(memoryDc, bitmap);
                var ok = NativeMethods.BitBlt(memoryDc, 0, 0, window.Width, window.Height, screenDc,
                    window.Left, window.Top, NativeMethods.SrcCopyCaptureBlt);
                if (!ok)
                {
                    return null;
                }

                using var copy = (Bitmap)Image.FromHbitmap(bitmap);
                using var mat = BitmapConverter.ToMat(copy);
                return ToBgr(mat);
            }
            finally
            {
                if (memoryDc != nint.Zero && previous != nint.Zero)
                {
                    NativeMethods.SelectObject(memoryDc, previous);
                }

                if (bitmap != nint.Zero)
                {
                    NativeMethods.DeleteObject(bitmap);
                }

                if (memoryDc != nint.Zero)
                {
                    NativeMethods.DeleteDC(memoryDc);
                }

                NativeMethods.ReleaseDC(nint.Zero, screenDc);
            }
        }
        catch (Exception ex)
        {
            log?.Warn($"屏幕拷贝验证码窗口失败：{ex.Message}", "验证码");
            return null;
        }
    }

    /// <summary>统一转成 3 通道 BGR，后续算法只处理这一种格式。</summary>
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

    /// <summary>整张图几乎没有变化 → 视为抓屏失败（全黑／全白）。</summary>
    private static bool IsBlank(Mat image)
    {
        if (image.Empty())
        {
            return true;
        }

        Cv2.MeanStdDev(image, out _, out var stdDev);
        return stdDev.Val0 + stdDev.Val1 + stdDev.Val2 < 2.0;
    }

    /// <summary>
    /// 在窗口图片坐标上点一下。图片不一定是 1:1 铺满窗口：PrintWindow 对高分屏窗口
    /// 会按 96 DPI 画进左上角一块、其余留黑。所以按「渲染区域 → 窗口」的比例换算，
    /// 屏幕拷贝那种铺满的情况比例就是 1，等于直接平移。
    /// </summary>
    public static bool ClickImagePoint(CaptchaWindowInfo window, Mat image, int x, int y, Rect? renderBox = null)
    {
        if (image.Empty())
        {
            return false;
        }

        var box = renderBox ?? new Rect(0, 0, image.Width, image.Height);
        if (box.Width <= 0 || box.Height <= 0)
        {
            box = new Rect(0, 0, image.Width, image.Height);
        }

        var fx = (x - box.X) / (double)box.Width;
        var fy = (y - box.Y) / (double)box.Height;
        var screenX = window.Left + (int)Math.Round(fx * window.Width);
        var screenY = window.Top + (int)Math.Round(fy * window.Height);

        NativeMethods.SetCursorPos(screenX, screenY);
        Thread.Sleep(80);
        NativeMethods.GetCursorPos(out var actual);
        if (Math.Abs(actual.X - screenX) > 2 || Math.Abs(actual.Y - screenY) > 2)
        {
            // 多显示器／缩放导致落点偏移时按实际误差纠一次。
            NativeMethods.SetCursorPos(screenX + (screenX - actual.X), screenY + (screenY - actual.Y));
            Thread.Sleep(80);
        }

        NativeMethods.ClickLeftButton();
        Thread.Sleep(60);
        return true;
    }
}
