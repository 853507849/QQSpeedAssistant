using System.Windows;
using System.Windows.Threading;
using QQSpeedAssistant.Models;
using QQSpeedAssistant.Services;
using QQSpeedAssistant.ViewModels;

namespace QQSpeedAssistant;

/// <summary>应用入口：构建服务容器、创建主窗口并处理未捕获异常。</summary>
public partial class App : Application
{
    private AppServices? _services;

    /// <summary>启动应用。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 固定使用深色主题：避免跟随系统浅色主题时，默认控件模板出现白底浅字
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);

        try
        {
            _services = AppServices.Create();
            _services.Log.Info($"程序启动：管理员权限={ElevationService.IsElevated}，屏幕={NativeMethods.ScreenWidth}x{NativeMethods.ScreenHeight}", "启动");

            // 自检模式：只加载全部 XAML，不显示窗口，用于验证资源与绑定定义
            if (e.Args.Contains("--selftest"))
            {
                _ = new Views.MainWindow { DataContext = new MainWindowViewModel(_services) };
                _ = new Views.Pages.DashboardPage();
                _ = new Views.Pages.AccountsPage();
                _ = new Views.Pages.ModesPage();
                _ = new Views.Pages.SettingsPage();
                _ = new Views.Pages.RewardsPage();
                _ = new Views.Pages.LogsPage();
                _ = new Views.Windows.TemplateCaptureWindow(new TemplateCaptureViewModel(_services));
                Shutdown(0);
                return;
            }

            // 登录自检模式：不开界面，直接跑一次登录并把前后画面存到 User/debug，便于排查“输入没进游戏”
            if (e.Args.Contains("--login-test") || e.Args.Contains("--input-test"))
            {
                var services = _services;
                var inputOnly = e.Args.Contains("--input-test");
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        if (inputOnly)
                        {
                            await RunInputDiagnosticsAsync(services);
                        }
                        else
                        {
                            await RunLoginDiagnosticsAsync(services);
                        }
                    }
                    catch (Exception ex)
                    {
                        services.Log.Error($"自检异常：{ex.Message}", "自检");
                    }
                    finally
                    {
                        Shutdown(0);
                    }
                });

                return;
            }

            // 输入判定自检：用合成画面验证「框里到底有没有输入」的规则
            if (e.Args.Contains("--inkcheck"))
            {
                RunInkCheck();
                Shutdown(0);
                return;
            }

            // 账号比对自检：验证「账号框文本 == 配置账号」的判据（登录界面记住账号时靠它跳过输入）
            if (e.Args.Contains("--accountcheck"))
            {
                Shutdown(RunAccountCheck());
                return;
            }

            // 分辨率自检：验证「800x600 只选 32 位色、绝不选 16 位色」的选项判定
            if (e.Args.Contains("--resolutioncheck"))
            {
                Shutdown(RunResolutionCheck());
                return;
            }

            // 匹配自检：拿一张截图把 templates 里所有模板跑一遍多尺度匹配，报告最佳分数与缩放比
            if (e.Args.Contains("--matchcheck"))
            {
                RunMatchCheck(_services, e.Args);
                Shutdown(0);
                return;
            }

            // 流程自检：把 flows 目录里所有流程解析一遍，检查步骤类型与模板是否齐全
            if (e.Args.Contains("--flowcheck"))
            {
                RunFlowCheck(_services);
                Shutdown(0);
                return;
            }

            // UIA 结构自检：把登录界面所有输入框连同「是否密码框／屏幕位置」导出，确认密码框能否定位
            if (e.Args.Contains("--uinspect"))
            {
                RunUiInspect(_services);
                Shutdown(0);
                return;
            }

            // 绑定自检：真正建主窗口并逐页切换，收集所有 XAML 绑定错误
            if (e.Args.Contains("--bindcheck"))
            {
                RunBindingCheck(_services, e.Args.Contains("--keep"));
                return;
            }

            var viewModel = new MainWindowViewModel(_services);
            var window = new Views.MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();

            // 开机自启链路：启动后延时一会儿（等客户端/界面就绪）自动开始白票
            if (_services.Config.Settings.AutoStartTaskOnLaunch)
            {
                var autoStart = e.Args.Contains("--autostart");
                _services.Log.Info($"已开启「启动后自动开始白票」{(autoStart ? "（本次由开机自启拉起）" : "")}，15 秒后开始", "启动");
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    _services.Orchestrator.Start();
                });
            }

            if (e.Args.Contains("--uidiag"))
            {
                RunUiDiagnostics(window, viewModel);
            }
        }
        catch (Exception ex)
        {
            WriteStartupError(ex);
            Shutdown(2);
        }
    }

    /// <summary>
    /// 绑定自检：真正创建主窗口、逐页切换并把所有 XAML 绑定错误收集到 User/debug/bindcheck.txt。
    /// 界面改动后跑一次，能立刻发现「属性名写错」「绑定路径不存在」这类只在运行时暴露的问题。
    /// </summary>
    private void RunBindingCheck(AppServices services, bool keepWindow)
    {
        var listener = new BindingErrorListener();
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;

        var viewModel = new MainWindowViewModel(services);
        var window = new Views.MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        foreach (var item in viewModel.NavigationItems)
        {
            viewModel.SelectedNavigationItem = item;
            window.UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine($"页面数：{viewModel.NavigationItems.Count}");
        foreach (var item in viewModel.NavigationItems)
        {
            report.AppendLine($" - {item.Title}（{item.Page.GetType().Name}）");
        }

        report.AppendLine();
        report.AppendLine(listener.Errors.Length == 0 ? "没有绑定错误。" : listener.Errors.ToString());

        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bindcheck.txt");
        File.WriteAllText(path, report.ToString());
        services.Log.Info($"绑定自检完成：{path}", "自检");

        if (!keepWindow)
        {
            window.Close();
            Shutdown(0);
            return;
        }

        // --keep：留在「运行设置」页，方便人工看一眼新加的卡片有没有错位
        var settingsItem = viewModel.NavigationItems.FirstOrDefault(i => i.Title == "运行设置");
        if (settingsItem != null)
        {
            viewModel.SelectedNavigationItem = settingsItem;
            window.UpdateLayout();
        }
    }

    /// <summary>收集 WPF 绑定错误。</summary>
    private sealed class BindingErrorListener : System.Diagnostics.TraceListener
    {
        /// <summary>已收集的错误文本。</summary>
        public System.Text.StringBuilder Errors { get; } = new();

        /// <inheritdoc />
        public override void Write(string? message)
        {
        }

        /// <inheritdoc />
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Errors.AppendLine(message);
            }
        }
    }

    /// <summary>
    /// 账号比对自检：验证「账号框里的文本就是我配置的这个账号」这条判据。
    /// 登录界面记住账号时全靠它决定「跳过账号输入、直接输密码」，
    /// 判错两个方向都危险：把别的账号当成对的一路跳到密码（登错号），
    /// 或把对的账号当成不一致又去重输（重打同样的内容画面不变，被判失败卡死）。
    /// 用法：QQSpeedAssistant.exe --accountcheck（报告写进 User/debug/accountcheck.txt）。
    /// </summary>
    private static int RunAccountCheck()
    {
        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);

        var report = new System.Text.StringBuilder();
        report.AppendLine("账号比对自检：SameAccount(账号框文本, 配置账号)");
        report.AppendLine();

        var failures = 0;
        void Case(string name, string? text, string? account, bool expected)
        {
            var actual = UiaAutomationService.SameAccount(text, account);
            var pass = actual == expected;
            if (!pass)
            {
                failures++;
            }

            report.AppendLine($"{(pass ? "[OK]" : "[FAIL]")} {name,-18} 框内「{text}」 配置「{account}」 "
                              + $"期望={(expected ? "一致" : "不一致")} 实际={(actual ? "一致" : "不一致")}");
        }

        Case("完全相同", "853507849", "853507849", true);
        Case("首尾空格", " 853507849 ", "853507849", true);
        Case("中间夹空格", "853 507849", "853507849", true);
        Case("全角数字", "８５３５０７８４９", "853507849", true);
        Case("记住了别的号", "123456789", "853507849", false);
        Case("多一位", "8535078490", "853507849", false);
        Case("少一位", "85350784", "853507849", false);
        Case("框内读不到", "", "853507849", false);
        Case("框内 null", null, "853507849", false);
        Case("配置为空", "853507849", "", false);
        Case("两边都空", "", "", false);

        report.AppendLine();
        report.AppendLine(failures == 0
            ? "全部通过：一致才跳过账号输入，不一致一律走正常输入。"
            : $"{failures} 个用例不符合预期。");

        var outPath = Path.Combine(directory, "accountcheck.txt");
        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine(report.ToString());
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 分辨率自检：登录界面底部的分辨率下拉必须选「800x600(32位色)」。
    /// 实测这个下拉里第一项是「800x600(16位色)」，以前按 Contains("800x600") 取第一个命中项，
    /// 于是每轮都选成 16 位色 —— 进游戏后颜色深度不对，游戏内模板全部偏色匹配失败。
    /// 用法：QQSpeedAssistant.exe --resolutioncheck（报告写进 User/debug/resolutioncheck.txt）。
    /// </summary>
    private static int RunResolutionCheck()
    {
        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);

        var report = new System.Text.StringBuilder();
        report.AppendLine("分辨率自检：目标「800x600(32位色)」，16 位色必须排除");
        report.AppendLine();
        report.AppendLine("一、单个选项名的判定（2=明确32位色 1=未标位深 -1=不合格）");

        var failures = 0;
        void Score(string label, string? name, int expected)
        {
            var actual = UiaAutomationService.ResolutionScore(name);
            var pass = actual == expected;
            if (!pass)
            {
                failures++;
            }

            report.AppendLine($"{(pass ? "[OK]" : "[FAIL]")} {label,-22} 「{name}」 期望={expected} 实际={actual}");
        }

        Score("实测列表里的第一项", "800x600(16位色)", -1);
        Score("目标项", "800x600(32位色)", 2);
        Score("全角括号+乘号", "800×600（32位色）", 2);
        Score("大写英文", "800X600 32BIT", 2);
        Score("星号+bpp", "800*600(32bpp)", 2);
        Score("未标位深", "800x600", 1);
        Score("16bit 写法", "800x600(16bit)", -1);
        Score("其它分辨率", "1024x768(32位色)", -1);
        Score("其它分辨率16位", "1280x960(16位色)", -1);
        Score("空字符串", "", -1);
        Score("null", null, -1);

        report.AppendLine();
        report.AppendLine("二、从整个下拉列表里挑哪一项");

        void Pick(string label, string?[] options, string? expected)
        {
            var index = UiaAutomationService.PickBestResolutionName(options);
            var actual = index < 0 ? null : options[index];
            var pass = string.Equals(actual, expected, StringComparison.Ordinal);
            if (!pass)
            {
                failures++;
            }

            report.AppendLine($"{(pass ? "[OK]" : "[FAIL]")} {label,-22} 候选[{string.Join(" | ", options)}] "
                              + $"期望={(expected ?? "不选")} 实际={(actual ?? "不选")}");
        }

        Pick("实测顺序(16位在前)",
            ["640x480(16位色)", "800x600(16位色)", "800x600(32位色)", "1024x768(32位色)"], "800x600(32位色)");
        Pick("32位在前", ["800x600(32位色)", "800x600(16位色)"], "800x600(32位色)");
        Pick("只有16位可选", ["800x600(16位色)", "1024x768(32位色)"], null);
        Pick("无位深标记", ["800x600", "1024x768(32位色)"], "800x600");
        Pick("空列表", [], null);

        report.AppendLine();
        report.AppendLine(failures == 0
            ? "全部通过：只要列表里有 800x600(32位色) 就一定选它，绝不会退回 16 位色。"
            : $"{failures} 个用例不符合预期。");

        var outPath = Path.Combine(directory, "resolutioncheck.txt");
        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine(report.ToString());
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 输入判定自检：用合成画面验证「框里到底有没有输入」的判定规则，
    /// 四种典型情形（空框、只有光标、只有边框高亮、真打进圆点/数字）各跑一遍。
    /// 用法：QQSpeedAssistant.exe --inkcheck（不用开游戏，报告写进 User/debug/inkcheck.txt）。
    /// </summary>
    private static void RunInkCheck()
    {
        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);

        var report = new System.Text.StringBuilder();
        report.AppendLine("输入判定自检：模拟密码框 255x42（与实测 1200x900 客户端下 login_password_box 一致）");
        report.AppendLine();

        var failures = 0;
        void Case(string name, OpenCvSharp.Mat before, OpenCvSharp.Mat after, bool expectFilled)
        {
            var diff = GameSession.Diff(before, after);
            var evidence = new FieldEvidence(diff.ChangedPixels, diff.Area,
                after.Width, after.Height, diff.InkWidth, diff.InkHeight, diff.CenterPixels);
            var actual = UiaAutomationService.LooksFilled(evidence);
            var pass = actual == expectFilled;
            if (!pass)
            {
                failures++;
            }

            report.AppendLine($"{(pass ? "[OK]" : "[FAIL]")} {name,-22} 期望={(expectFilled ? "有字" : "没字")} "
                              + $"实际={(actual ? "有字" : "没字")}  像素 {diff.ChangedPixels}  "
                              + $"变化区 {diff.InkWidth}x{diff.InkHeight}  中间带 {diff.CenterPixels}");
        }

        OpenCvSharp.Mat Box()
        {
            var mat = new OpenCvSharp.Mat(42, 255, OpenCvSharp.MatType.CV_8UC1, new OpenCvSharp.Scalar(238));
            return mat;
        }

        // ① 完全没变：空框
        using (var before = Box())
        using (var after = Box())
        {
            Case("空框（没输入）", before, after, false);
        }

        // ② 只有光标：2 像素宽、20 像素高的竖线（实测失败那次的量级）
        using (var before = Box())
        using (var after = Box())
        {
            OpenCvSharp.Cv2.Rectangle(after, new OpenCvSharp.Rect(30, 11, 2, 20), new OpenCvSharp.Scalar(20),
                -1);
            Case("只有光标闪烁", before, after, false);
        }

        // ③ 只有边框高亮：一圈 2 像素的空心框
        using (var before = Box())
        using (var after = Box())
        {
            OpenCvSharp.Cv2.Rectangle(after, new OpenCvSharp.Rect(1, 1, 253, 40), new OpenCvSharp.Scalar(20), 2);
            Case("只有边框高亮", before, after, false);
        }

        // ④ 真的打进 16 位密码：16 个圆点
        using (var before = Box())
        using (var after = Box())
        {
            for (var i = 0; i < 16; i++)
            {
                OpenCvSharp.Cv2.Circle(after, new OpenCvSharp.Point(24 + i * 9, 21), 3,
                    new OpenCvSharp.Scalar(25), -1);
            }

            Case("16 位密码圆点", before, after, true);
        }

        // ⑤ 短密码：6 个圆点
        using (var before = Box())
        using (var after = Box())
        {
            for (var i = 0; i < 6; i++)
            {
                OpenCvSharp.Cv2.Circle(after, new OpenCvSharp.Point(24 + i * 9, 21), 3,
                    new OpenCvSharp.Scalar(25), -1);
            }

            Case("6 位密码圆点", before, after, true);
        }

        // ⑥ 焦点探针：点击后先打 2 个字符，确认键盘焦点真的落在这块框上
        void FocusCase(string name, OpenCvSharp.Mat before, OpenCvSharp.Mat after, int chars, bool expectFocused)
        {
            var diff = GameSession.Diff(before, after);
            var evidence = new FieldEvidence(diff.ChangedPixels, diff.Area,
                after.Width, after.Height, diff.InkWidth, diff.InkHeight, diff.CenterPixels);
            var actual = UiaAutomationService.LooksFocused(evidence, chars);
            var pass = actual == expectFocused;
            if (!pass)
            {
                failures++;
            }

            report.AppendLine($"{(pass ? "[OK]" : "[FAIL]")} {name,-22} 期望={(expectFocused ? "进了" : "没进")} "
                              + $"实际={(actual ? "进了" : "没进")}  像素 {diff.ChangedPixels}  "
                              + $"变化区 {diff.InkWidth}x{diff.InkHeight}");
        }

        using (var before = Box())
        using (var after = Box())
        {
            FocusCase("探针：空框没进字", before, after, 2, false);
        }

        using (var before = Box())
        using (var after = Box())
        {
            OpenCvSharp.Cv2.Rectangle(after, new OpenCvSharp.Rect(30, 11, 2, 20), new OpenCvSharp.Scalar(20), -1);
            FocusCase("探针：只有光标", before, after, 2, false);
        }

        using (var before = Box())
        using (var after = Box())
        {
            OpenCvSharp.Cv2.Circle(after, new OpenCvSharp.Point(24, 21), 3, new OpenCvSharp.Scalar(25), -1);
            FocusCase("探针：1 个圆点", before, after, 1, true);
        }

        using (var before = Box())
        using (var after = Box())
        {
            for (var i = 0; i < 2; i++)
            {
                OpenCvSharp.Cv2.Circle(after, new OpenCvSharp.Point(24 + i * 9, 21), 3,
                    new OpenCvSharp.Scalar(25), -1);
            }

            FocusCase("探针：2 个圆点", before, after, 2, true);
        }

        report.AppendLine();
        report.AppendLine(failures == 0 ? "全部通过：判定规则能挡掉光标/边框，也能认出真实输入。" : $"{failures} 个用例不符合预期。");

        var outPath = Path.Combine(directory, "inkcheck.txt");
        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine(report.ToString());
    }

    /// <summary>
    /// 匹配自检：把 templates 下所有模板在一张截图上跑一遍多尺度匹配，输出最佳分数与缩放比。
    /// 用法：QQSpeedAssistant.exe --matchcheck [图片路径]（省略则取 User/debug 下最新的 fail-*.png）。
    /// 采完模板不用开游戏就能验证「这张图能不能被认出来」。
    /// </summary>
    private static void RunMatchCheck(AppServices services, string[] args)
    {
        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);

        var index = Array.IndexOf(args, "--matchcheck");
        var imagePath = index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--")
            ? args[index + 1]
            : Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "fail-*.png").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                : null;

        var report = new System.Text.StringBuilder();
        if (imagePath == null || !File.Exists(imagePath))
        {
            report.AppendLine("没有找到可用的截图。请显式传图片路径：--matchcheck D:\\path\\shot.png");
            File.WriteAllText(Path.Combine(directory, "matchcheck.txt"), report.ToString());
            Console.WriteLine(report.ToString());
            return;
        }

        using var frame = OpenCvSharp.Cv2.ImDecode(File.ReadAllBytes(imagePath), OpenCvSharp.ImreadModes.Color);
        if (frame.Empty())
        {
            report.AppendLine($"图片读取失败：{imagePath}");
            File.WriteAllText(Path.Combine(directory, "matchcheck.txt"), report.ToString());
            Console.WriteLine(report.ToString());
            return;
        }

        report.AppendLine($"截图：{imagePath}");
        report.AppendLine($"画面尺寸：{frame.Width}x{frame.Height}（模板基准宽度会被自动换算到该尺寸）");
        report.AppendLine();

        var names = Directory.Exists(PathService.TemplateDir)
            ? Directory.EnumerateFiles(PathService.TemplateDir, "*.png", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".meta.png", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        var hit = 0;
        foreach (var name in names)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var match = services.Matcher.Find(frame, name!, 0.85);
            sw.Stop();
            if (match.Found)
            {
                hit++;
            }

            report.AppendLine(
                $"{(match.Found ? "[命中]" : "[未中]")} {name,-26} 分数 {match.Score:F3}  缩放 {match.Scale:F2}  " +
                $"位置 {match.X},{match.Y}  {match.Width}x{match.Height}  {sw.ElapsedMilliseconds}ms");
        }

        report.AppendLine();
        report.AppendLine($"共 {names.Count} 个模板，命中 {hit} 个");

        var outPath = Path.Combine(directory, "matchcheck.txt");
        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine(report.ToString());
        services.Log.Info($"匹配自检完成，报告：{outPath}", "自检");
    }

    /// <summary>
    /// 流程自检：解析 flows 目录下的全部流程，统计步骤数、未知步骤类型与缺失模板，
    /// 结果写到 User/debug/flowcheck.txt（命令行排查用，不改动任何配置）。
    /// </summary>
    private static void RunFlowCheck(AppServices services)
    {
        var report = new System.Text.StringBuilder();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            FlowStepTypes.Log, FlowStepTypes.Wait, FlowStepTypes.Key, FlowStepTypes.Click,
            FlowStepTypes.TypeText, FlowStepTypes.ClearText, FlowStepTypes.TypeTextAutoFocus,
            FlowStepTypes.ClickImage, FlowStepTypes.WaitImage, FlowStepTypes.WaitImageGone,
            FlowStepTypes.ExpectImage, FlowStepTypes.ExpectNoImage, FlowStepTypes.SaveReward,
            FlowStepTypes.ClaimReward, FlowStepTypes.OpenChest, FlowStepTypes.CheckPermanent,
            FlowStepTypes.WaitImageAny, FlowStepTypes.RandomWait, FlowStepTypes.Repeat
        };

        var missingTemplates = 0;
        var unknownSteps = 0;

        void Walk(IReadOnlyList<FlowStep> steps, ref int count)
        {
            foreach (var step in steps)
            {
                count++;
                var type = step.Type?.Trim().ToLowerInvariant() ?? "";
                if (!known.Contains(type))
                {
                    unknownSteps++;
                    report.AppendLine($"    [未知步骤] {step.Type}");
                }

                var templates = new List<string>();
                if (!string.IsNullOrWhiteSpace(step.Template))
                {
                    templates.Add(step.Template!);
                }

                if (step.Templates != null)
                {
                    templates.AddRange(step.Templates);
                }

                foreach (var template in templates.Where(t => !services.Matcher.TemplateExists(t)))
                {
                    missingTemplates++;
                    report.AppendLine($"    [缺模板] {type} → {template}");
                }

                if (step.HasFallback)
                {
                    report.AppendLine($"    [兜底坐标] {type} → {step.FallbackXRatio:F3}, {step.FallbackYRatio:F3}");
                }

                if (step.Steps is { Count: > 0 } children)
                {
                    Walk(children, ref count);
                }
            }
        }

        var names = services.Flows.ListNames();
        report.AppendLine($"流程目录：{services.Flows.Directory}");
        report.AppendLine($"共 {names.Count} 个流程");
        report.AppendLine();

        foreach (var name in names)
        {
            var flow = services.Flows.Load(name);
            if (flow == null)
            {
                report.AppendLine($"[解析失败] {name}.json");
                continue;
            }

            var count = 0;
            Walk(flow.Steps, ref count);
            report.AppendLine($"[OK] {name,-20} {flow.Name}  步骤 {count}");
        }

        report.AppendLine();
        report.AppendLine($"未知步骤总数：{unknownSteps}；缺失模板引用：{missingTemplates}");

        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "flowcheck.txt");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine(report.ToString());
        services.Log.Info($"流程自检完成，报告：{path}", "自检");
    }

    /// <summary>
    /// 登录自检：不开界面，直接对一个账号执行登录流程，并把关键现场写入 User/debug/login-test.txt 与前后截图。
    /// 用于排查「点击/输入没有真正到达游戏窗口」这类问题。
    /// </summary>
    /// <summary>
    /// UIA 结构自检：不输密码、不点登录，只把登录界面上所有输入框的识别结果导出到 User/debug/uinspect.txt。
    /// 密码框「定位不到」时先跑这个，日志里会写明是没找到、尺寸异常、还是两个框顺序搞反。
    /// </summary>
    private static void RunUiInspect(AppServices services)
    {
        var report = new System.Text.StringBuilder();
        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);

        try
        {
            report.AppendLine($"管理员权限={ElevationService.IsElevated}");
            var windows = WindowLocator.Find(services.Config.Settings);
            report.AppendLine($"找到游戏窗口 {windows.Count} 个");
            foreach (var window in windows)
            {
                report.AppendLine($" - 「{window.Title}」pid={window.ProcessId} {window.Width}x{window.Height}");
            }

            var target = windows.FirstOrDefault();
            if (target == null)
            {
                report.AppendLine("没有找到游戏窗口，请先启动客户端并停在登录界面");
                File.WriteAllText(Path.Combine(directory, "uinspect.txt"), report.ToString());
                services.Log.Warn("UIA 结构自检：没有找到游戏窗口", "自检");
                return;
            }

            report.AppendLine();
            report.AppendLine("=== 输入框识别结果 ===");
            report.AppendLine(UiaAutomationService.InspectInputs(target.Handle, services.Log));
            report.AppendLine();
            report.AppendLine("=== 控件树前 120 个节点 ===");
            report.AppendLine(UiaAutomationService.Describe(target.Handle));
        }
        catch (Exception ex)
        {
            report.AppendLine($"异常={ex}");
        }

        var path = Path.Combine(directory, "uinspect.txt");
        File.WriteAllText(path, report.ToString());
        services.Log.Info($"UIA 结构自检完成，结果已写入 {path}", "自检");
    }

    private static async Task RunLoginDiagnosticsAsync(AppServices services)
    {
        var report = new System.Text.StringBuilder();
        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);

        void Line(string text)
        {
            report.AppendLine(text);
            services.Log.Info(text, "自检");
        }

        Line($"管理员权限={ElevationService.IsElevated}");
        Line($"屏幕={NativeMethods.ScreenWidth}x{NativeMethods.ScreenHeight} 前台窗口={NativeMethods.GetForegroundWindow()}");

        var account = services.Config.Accounts.FirstOrDefault(a => a.Enabled);
        if (account == null)
        {
            Line("没有启用的账号");
            File.WriteAllText(Path.Combine(directory, "login-test.txt"), report.ToString());
            return;
        }

        Line($"账号={account.Account}，服务器={account.Server}");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        try
        {
            var sessions = await services.Sessions.EnsureAsync([account], cts.Token);
            var session = sessions.FirstOrDefault();
            if (session == null)
            {
                Line("没有可用的游戏窗口");
                File.WriteAllText(Path.Combine(directory, "login-test.txt"), report.ToString());
                return;
            }

            Line($"窗口「{session.Title}」进程={session.ProcessName} pid={session.ProcessId}");
            SaveFrame(session, directory, "login01-before");

            var activated = session.Activate();
            var foreground = NativeMethods.GetForegroundWindow();
            Line($"激活={activated} 激活后前台={foreground} 目标句柄={session.Handle}");

            var result = await services.Login
                .LoginAsync(session, account, services.Config.Settings, cts.Token);
            Line($"登录结果={result.Success} 说明={result.Message}");

            SaveFrame(session, directory, "login02-after");
        }
        catch (Exception ex)
        {
            Line($"异常={ex}");
        }

        File.WriteAllText(Path.Combine(directory, "login-test.txt"), report.ToString());
    }

    /// <summary>
    /// 输入自检：分别在账号框、密码框里点一下、清空、输入测试内容并截图，
    /// 用来判断「点击是否命中」「键盘输入是否被游戏接收」。
    /// </summary>
    private static async Task RunInputDiagnosticsAsync(AppServices services)
    {
        var report = new System.Text.StringBuilder();
        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);

        void Line(string text)
        {
            report.AppendLine(text);
            services.Log.Info(text, "自检");
        }

        var account = services.Config.Accounts.FirstOrDefault(a => a.Enabled);
        if (account == null)
        {
            Line("没有启用的账号，跳过输入自检");
            File.WriteAllText(Path.Combine(directory, "input-test.txt"), report.ToString());
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var sessions = await services.Sessions.EnsureAsync([account], cts.Token);
            var session = sessions.FirstOrDefault();
            if (session == null)
            {
                Line("没有可用的游戏窗口");
                File.WriteAllText(Path.Combine(directory, "input-test.txt"), report.ToString());
                return;
            }

            Line($"管理员权限={ElevationService.IsElevated} 屏幕={NativeMethods.ScreenWidth}x{NativeMethods.ScreenHeight}");
            Line($"窗口={session.Title} pid={session.ProcessId} proc={session.ProcessName}");
            Line($"输入通道={session.InputBackendName}");
            Line($"激活={session.Activate()} 前台={NativeMethods.GetForegroundWindow()} 句柄={session.Handle}");

            using (var frame = session.CaptureFrame())
            {
                if (frame == null)
                {
                    Line("抓屏失败");
                    File.WriteAllText(Path.Combine(directory, "input-test.txt"), report.ToString());
                    return;
                }

                var accountBox = services.Matcher.Find(frame, "login_account_box", 0.8);
                var passwordBox = services.Matcher.Find(frame, "login_password_box", 0.8);
                Line($"账号框={accountBox.Found} 相似度={accountBox.Score:F2} 位置=({accountBox.CenterX},{accountBox.CenterY}) 尺寸={accountBox.Width}x{accountBox.Height}");
                Line($"密码框={passwordBox.Found} 相似度={passwordBox.Score:F2} 位置=({passwordBox.CenterX},{passwordBox.CenterY}) 尺寸={passwordBox.Width}x{passwordBox.Height}");

                if (!accountBox.Found || !passwordBox.Found)
                {
                    File.WriteAllText(Path.Combine(directory, "input-test.txt"), report.ToString());
                    return;
                }

                // 第一组：只点击 + 直接输入，不清空，验证是否真的落到账号框
                session.ClickClientPixel(accountBox.CenterX, accountBox.CenterY);
                await Task.Delay(1000, cts.Token);
                session.TypeText("777");
                await Task.Delay(900, cts.Token);
                SaveFrame(session, directory, "input01-account");

                // 第二组：点击密码框 + 直接输入
                session.ClickClientPixel(passwordBox.CenterX, passwordBox.CenterY);
                await Task.Delay(1000, cts.Token);
                session.TypeText("888");
                await Task.Delay(900, cts.Token);
                SaveFrame(session, directory, "input02-password");

                // 第三组：清空后再输入，验证 ClearInput 是否可靠
                session.ClickClientPixel(accountBox.CenterX, accountBox.CenterY);
                await Task.Delay(700, cts.Token);
                session.ClearInput();
                session.TypeText("1357924680");
                await Task.Delay(900, cts.Token);
                SaveFrame(session, directory, "input03-cleared");

                // 第四组：点登录按钮（密码为空），若游戏弹出“请输入密码”提示则说明点击确实生效
                using var loginFrame = session.CaptureFrame();
                var loginButton = loginFrame == null
                    ? new TemplateMatch(false, 0, 0, 0, 0, 0)
                    : services.Matcher.Find(loginFrame, "login_button", 0.8);
                Line($"登录按钮={loginButton.Found} 相似度={loginButton.Score:F2} 位置=({loginButton.CenterX},{loginButton.CenterY})");
                if (loginButton.Found)
                {
                    session.Activate();
                    Thread.Sleep(200);
                    session.ClickClientPixel(loginButton.CenterX, loginButton.CenterY);
                    await Task.Delay(1500, cts.Token);
                    SaveFrame(session, directory, "input04-loginclick");
                }
                Line("输入自检完成，请查看 User/debug/input01-account.png、input02-password.png");
            }
        }
        catch (Exception ex)
        {
            Line($"异常={ex.Message}");
        }

        File.WriteAllText(Path.Combine(directory, "input-test.txt"), report.ToString());
    }

    /// <summary>保存当前游戏画面到指定目录。</summary>
    private static void SaveFrame(GameSession session, string directory, string name)
    {
        try
        {
            using var frame = session.CaptureFrame();
            if (frame == null || frame.Empty())
            {
                return;
            }

            OpenCvSharp.Cv2.ImEncode(".png", frame, out var buffer);
            File.WriteAllBytes(Path.Combine(directory, $"{name}.png"), buffer);
        }
        catch
        {
            // 诊断过程中截图失败不影响主流程。
        }
    }

    /// <summary>临时诊断：打开第一个下拉框并记录状态。</summary>
    private static void RunUiDiagnostics(Window window, MainWindowViewModel viewModel)
    {
        viewModel.SelectedNavigationItem = viewModel.NavigationItems[2];
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                var combos = new List<System.Windows.Controls.ComboBox>();
                CollectCombos(window, combos);
                var report = new System.Text.StringBuilder();
                report.AppendLine($"comboCount={combos.Count}");
                if (combos.Count > 0)
                {
                    var combo = combos[0];
                    report.AppendLine($"items={combo.Items.Count} closedOpen={combo.IsDropDownOpen} template={combo.Template != null}");
                    combo.IsDropDownOpen = true;
                    window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        try
                        {
                            report.AppendLine($"afterOpen={combo.IsDropDownOpen}");
                            var popup = combo.Template?.FindName("PART_Popup", combo) as System.Windows.Controls.Primitives.Popup;
                            var child = popup?.Child as System.Windows.FrameworkElement;
                            report.AppendLine($"popupNull={popup == null} child={(child?.GetType().Name ?? "null")}");
                            report.AppendLine($"childVisible={child?.IsVisible} size={child?.ActualWidth}x{child?.ActualHeight}");
                            report.AppendLine($"listItems={combo.Items.Count}");

                            // 检查点击命中元素与 ToggleButton 绑定是否生效
                            var hit = combo.InputHitTest(new System.Windows.Point(combo.ActualWidth / 2, combo.ActualHeight / 2)) as System.Windows.FrameworkElement;
                            report.AppendLine($"hitTest={(hit?.GetType().Name ?? "null")}");
                            var toggle = combo.Template?.FindName("DropDownToggle", combo) as System.Windows.Controls.Primitives.ToggleButton;
                            report.AppendLine($"toggleNull={toggle == null}");
                            if (toggle != null)
                            {
                                toggle.IsChecked = false;
                                combo.IsDropDownOpen = false;
                                toggle.IsChecked = true;
                                Thread.Sleep(300);
                                report.AppendLine($"toggleCheck->open={combo.IsDropDownOpen}");
                                combo.IsDropDownOpen = false;
                            }

                            // 模拟真实点击：先按下、再抬起，分别观察下拉状态
                            combo.IsDropDownOpen = false;
                            var center = combo.PointToScreen(new System.Windows.Point(combo.ActualWidth / 2, combo.ActualHeight / 2));
                            report.AppendLine($"centerScreen={center.X:F0},{center.Y:F0}");
                            NativeMethods.SetCursorPos((int)center.X, (int)center.Y);
                            Thread.Sleep(300);
                            NativeMethods.mouse_event(0x0002, 0, 0, 0, nint.Zero);
                            Thread.Sleep(400);
                            report.AppendLine($"afterDownOpen={combo.IsDropDownOpen}");
                            var popup3 = combo.Template?.FindName("PART_Popup", combo) as System.Windows.Controls.Primitives.Popup;
                            report.AppendLine($"afterDownPopupIsOpen={popup3?.IsOpen}");
                            NativeMethods.mouse_event(0x0004, 0, 0, 0, nint.Zero);
                            Thread.Sleep(400);
                            report.AppendLine($"afterUpOpen={combo.IsDropDownOpen}");
                            WaitForClickCapture(combo, report);
                        }
                        catch (Exception ex)
                        {
                            report.AppendLine("inner error: " + ex.Message);
                        }

                        File.WriteAllText(Path.Combine(PathService.LogDir, "uidiag.txt"), report.ToString());
                    }));
                }
                else
                {
                    File.WriteAllText(Path.Combine(PathService.LogDir, "uidiag.txt"), report.ToString());
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(PathService.LogDir, "uidiag.txt"), "error: " + ex);
            }
        }));
    }

    /// <summary>临时诊断：抓取点击后下拉区域截图。</summary>
    private static void WaitForClickCapture(System.Windows.Controls.ComboBox combo, System.Text.StringBuilder report)
    {
        try
        {
            var point = combo.PointToScreen(new System.Windows.Point(0, 0));
            var width = (int)Math.Max(combo.ActualWidth, 260);
            const int height = 420;
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen((int)point.X, (int)point.Y, 0, 0, bitmap.Size);
            }

            bitmap.Save(Path.Combine(PathService.LogDir, "uidiag.png"), System.Drawing.Imaging.ImageFormat.Png);
            report.AppendLine("capture saved");
        }
        catch (Exception ex)
        {
            report.AppendLine("capture error: " + ex.Message);
        }

        File.WriteAllText(Path.Combine(PathService.LogDir, "uidiag.txt"), report.ToString());
    }

    /// <summary>递归收集下拉框。</summary>
    private static void CollectCombos(System.Windows.DependencyObject root, List<System.Windows.Controls.ComboBox> result)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ComboBox combo)
            {
                result.Add(combo);
            }

            CollectCombos(child, result);
        }
    }

    /// <summary>退出时释放会话与定时器。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    /// <summary>未处理异常写入日志后继续运行，避免自动化过程中界面崩溃。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Error($"未处理异常：{e.Exception.Message}", "应用");
        WriteStartupError(e.Exception);
        e.Handled = true;
    }

    /// <summary>把启动异常写入文件，便于无界面环境下排查。</summary>
    private static void WriteStartupError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(PathService.LogDir);
            File.WriteAllText(Path.Combine(PathService.LogDir, "startup-error.txt"), exception.ToString());
        }
        catch
        {
            // 写盘失败时忽略。
        }
    }
}
