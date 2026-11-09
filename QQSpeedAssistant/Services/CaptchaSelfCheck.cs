using System.Text;
using OpenCvSharp;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>验证码自检结果。</summary>
public sealed record CaptchaCheckResult(int ExitCode, string Report, string AnnotatedPath);

/// <summary>
/// 验证码自检：抓当前弹窗（或读一张截图）→ 切图 → 标注 → 调模型，把每一步写进报告。
/// 命令行（--captchacheck）和设置页的「测试识别」按钮共用这一套逻辑，保证两边结论一致。
/// </summary>
public static class CaptchaSelfCheck
{
    /// <summary>执行自检。</summary>
    /// <param name="settings">运行设置。</param>
    /// <param name="log">日志。</param>
    /// <param name="imagePath">指定截图路径；为空时抓当前验证码窗口。</param>
    /// <param name="solve">是否真的点击作答。</param>
    /// <param name="ct">取消标记。</param>
    public static async Task<CaptchaCheckResult> RunAsync(AppSettings settings, LogService log, string? imagePath,
        bool solve, CancellationToken ct)
    {
        var report = new StringBuilder();
        var exitCode = 0;
        Mat? image = null;
        CaptchaWindowInfo? window = null;

        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);
        var annotatedPath = Path.Combine(directory, "captchacheck-annotated.png");

        try
        {
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                image = Cv2.ImRead(imagePath, ImreadModes.Color);
                report.AppendLine(image == null || image.Empty()
                    ? $"读不到图片：{imagePath}"
                    : $"图像来源：文件 {imagePath}（{image.Width}x{image.Height}）");
            }
            else
            {
                report.AppendLine($"标题关键字：{settings.CaptchaWindowTitles}");
                var windows = CaptchaWindowLocator.FindAll(settings);
                report.AppendLine($"找到 {windows.Count} 个验证码窗口");
                foreach (var item in windows)
                {
                    report.AppendLine($"  - 「{item.Title}」 {item.Width}x{item.Height} @({item.Left},{item.Top}) 句柄 {item.Handle}");
                }

                window = windows.FirstOrDefault();
                if (window == null)
                {
                    report.AppendLine("没有找到验证码窗口：请先让验证码弹出来，或指定一张截图。");
                    report.AppendLine("当前所有可见窗口标题（供核对标题关键字）：");
                    foreach (var title in VisibleWindowTitles())
                    {
                        report.AppendLine("  " + title);
                    }

                    exitCode = 1;
                }
                else
                {
                    // 刚弹出来时图块还在加载，等它出来再抓，否则只会抓到「正在加载验证码…」。
                    window = await CaptchaService.WaitTilesReadyAsync(window, log, null, ct);
                    image = CaptchaService.CaptureForTiles(window, log, out var tiles);
                    report.AppendLine(image == null || image.Empty()
                        ? "抓取验证码窗口失败"
                        : $"已抓取窗口画面 {image.Width}x{image.Height}（能切出 {tiles} 块）");
                }
            }

            if (image == null || image.Empty())
            {
                exitCode = 1;
                return new CaptchaCheckResult(exitCode, report.ToString(), "");
            }

            var grid = CaptchaGrid.Detect(image);
            report.AppendLine();
            report.AppendLine("切图结果：");
            report.AppendLine(grid.Report);
            if (grid.Tiles.Count == 0)
            {
                exitCode = 1;
            }

            using var annotated = CaptchaGrid.Annotate(image, grid.Tiles);
            Cv2.ImWrite(annotatedPath, annotated);
            report.AppendLine($"标注图：{annotatedPath}");

            var gridTop = grid.Tiles.Count > 0 ? grid.Tiles.Min(t => t.Y) : int.MaxValue;
            var confirm = CaptchaService.FindConfirmButton(image, gridTop);
            report.AppendLine(confirm == null
                ? "没有定位到蓝色的「确定」按钮"
                : $"「确定」按钮位置：({confirm.Value.X},{confirm.Value.Y})");

            report.AppendLine();
            if (!VisionCaptchaClient.CanCall(settings))
            {
                report.AppendLine("未配置识别接口（设置 → 登录验证码：接口地址／密钥／模型名），跳过模型识别。");
                return new CaptchaCheckResult(exitCode, report.ToString(), annotatedPath);
            }

            report.AppendLine($"识别接口：{settings.CaptchaApiBaseUrl} 模型：{settings.CaptchaModel}");
            Cv2.ImEncode(".jpg", annotated, out var jpeg,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, 92));

            var client = new VisionCaptchaClient(log);
            var answer = await client.AskAsync(jpeg, grid.Tiles.Count, settings, ct);
            if (answer == null)
            {
                report.AppendLine("模型调用失败，原因见运行日志。");
                exitCode = 1;
                return new CaptchaCheckResult(exitCode, report.ToString(), annotatedPath);
            }

            report.AppendLine($"模型回答：target=「{answer.Target}」 picks=[{string.Join(",", answer.Picks)}] "
                              + $"置信度={answer.Confidence:F2}");
            report.AppendLine($"原始回答：{answer.Raw.Replace("\n", " ")}");

            if (!solve || window == null || !answer.IsUsable)
            {
                return new CaptchaCheckResult(exitCode, report.ToString(), annotatedPath);
            }

            NativeMethods.Activate(window.Handle);
            Thread.Sleep(400);
            foreach (var pick in answer.Picks)
            {
                var tile = grid.Tiles.FirstOrDefault(t => t.Index == pick);
                if (tile == null)
                {
                    continue;
                }

                CaptchaWindowLocator.ClickImagePoint(window, image, tile.CenterX, tile.CenterY, grid.ContentBox);
                await Task.Delay(250, ct);
            }

            await Task.Delay(400, ct);
            var clicked = CaptchaService.ClickConfirm(window, image, gridTop, grid.ContentBox);
            await Task.Delay(2500, ct);
            var alive = CaptchaWindowLocator.Find(settings) != null;
            report.AppendLine($"点图={string.Join(",", answer.Picks)} 点确定={clicked} → "
                              + (alive ? "窗口仍在：未通过" : "窗口已关闭：通过"));
            exitCode = alive ? 1 : 0;
            return new CaptchaCheckResult(exitCode, report.ToString(), annotatedPath);
        }
        catch (Exception ex)
        {
            report.AppendLine("自检异常：" + ex);
            return new CaptchaCheckResult(2, report.ToString(), "");
        }
        finally
        {
            image?.Dispose();
        }
    }

    /// <summary>列出当前所有可见窗口标题，方便确认验证码窗口的标题关键字该填什么。</summary>
    private static List<string> VisibleWindowTitles()
    {
        var titles = new List<string>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hWnd))
            {
                var title = NativeMethods.GetWindowTitle(hWnd).Trim();
                if (title.Length > 0)
                {
                    titles.Add(title);
                }
            }

            return true;
        }, nint.Zero);

        return titles.Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
    }
}
