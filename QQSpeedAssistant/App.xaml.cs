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

            // 区服自检：验证账号页区服下拉的三项取值与旧写法归一化
            if (e.Args.Contains("--servercheck"))
            {
                Shutdown(RunServerCheck());
                return;
            }

            // 抓图自检：用 BitBlt 抓一次当前前台窗口，确认这台机器上屏幕拷贝可用
            if (e.Args.Contains("--screencheck"))
            {
                Shutdown(RunScreenCheck(e.Args));
                return;
            }

            // 验证码自检：切图＋调用视觉模型，报告写进 User/debug/captchacheck.txt
            if (e.Args.Contains("--captchacheck"))
            {
                RunCaptchaCheck(_services, e.Args);
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

            // 下拉框自检：验证账号页「区服」下拉能展开、改值能写回账号
            if (e.Args.Contains("--combocheck"))
            {
                RunComboCheck(_services, e.Args.Contains("--keep"));
                return;
            }

            // 真实点击自检：用真实鼠标点下拉框，复现「点上去没反应」
            if (e.Args.Contains("--comboclick"))
            {
                RunComboClickCheck(_services);
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
    /// 下拉框自检：真的把账号页建出来（窗口放在屏幕外，不打扰操作），
    /// 检查「区服」那一列的下拉框能不能展开、改值能不能写回账号。
    /// 用法：QQSpeedAssistant.exe --combocheck（报告写进 User/debug/combocheck.txt）。
    /// </summary>
    private void RunComboCheck(AppServices services, bool keepWindow)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("区服下拉自检：选项 = " + string.Join(" ／ ", ServerOptions.All));
        report.AppendLine();

        var vm = new AccountsPageViewModel(services);
        var page = new Views.Pages.AccountsPage { DataContext = vm };
        var window = new Window
        {
            Content = page,
            Width = 1100,
            Height = 780,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000
        };

        MainWindow ??= window;
        window.Show();
        window.UpdateLayout();
        PumpDispatcher();

        var combos = new List<System.Windows.Controls.ComboBox>();
        CollectVisuals(page, combos);
        report.AppendLine($"账号行数：{vm.Accounts.Count}　找到下拉框 {combos.Count} 个");

        var failures = 0;
        for (var i = 0; i < combos.Count; i++)
        {
            var combo = combos[i];
            var items = combo.ItemsSource?.Cast<object>().Select(o => o?.ToString() ?? "").ToList() ?? [];
            report.AppendLine($"  [{i}] 选项 {items.Count} 个（{string.Join("／", items)}）"
                              + $"　选中={combo.SelectedIndex}／「{combo.SelectedItem}」"
                              + $"　尺寸={combo.ActualWidth:0}x{combo.ActualHeight:0}"
                              + $"　可用={combo.IsEnabled}　可点={combo.IsHitTestVisible}");
            if (items.Count == 0)
            {
                failures++;
                report.AppendLine("       [FAIL] ItemsSource 是空的，说明 ServerChoices 没绑上");
            }
        }

        var account = vm.Accounts.FirstOrDefault();
        if (combos.Count == 0 || account == null)
        {
            report.AppendLine("[FAIL] 页面上没有可用的下拉框或账号，无法继续验证");
            failures++;
        }
        else
        {
            var combo = combos[0];
            var original = account.Server;

            // ① 模板里的展开按钮（鼠标点的就是它）能不能把下拉打开。
            var toggle = combo.Template.FindName("DropDownToggle", combo)
                as System.Windows.Controls.Primitives.ToggleButton;
            report.AppendLine();
            report.AppendLine($"[展开] 模板里的展开按钮：{(toggle == null ? "没找到" : "存在")}");
            if (toggle == null)
            {
                failures++;
            }
            else
            {
                // 热区必须先存在：模板里的展开按钮如果被排成 0x0，鼠标点上去会落到外层边框，
                // 表现就是「下拉框怎么点都没反应」——之前只改 IsChecked 的自检发现不了这个。
                var hotZone = toggle.ActualWidth > 0 && toggle.ActualHeight > 0;
                report.AppendLine($"[热区] 展开按钮尺寸 {toggle.ActualWidth:0}x{toggle.ActualHeight:0}"
                                  + $"（下拉框 {combo.ActualWidth:0}x{combo.ActualHeight:0}） {(hotZone ? "[OK]" : "[FAIL]")}");
                if (!hotZone)
                {
                    failures++;
                    report.AppendLine("       [FAIL] 展开按钮被排成 0 尺寸，鼠标点不到它");
                }

                var centerHit = combo.InputHitTest(new Point(combo.ActualWidth / 2, combo.ActualHeight / 2))
                    as System.Windows.DependencyObject;
                var hitInsideToggle = IsDescendantOf(hotZone ? toggle : null, centerHit);
                report.AppendLine($"[命中] 下拉框中心命中的是 {centerHit?.GetType().Name ?? "null"}，"
                                  + $"属于展开按钮={hitInsideToggle} {(hitInsideToggle ? "[OK]" : "[FAIL]")}");
                if (!hitInsideToggle)
                {
                    failures++;
                    report.AppendLine("       [FAIL] 下拉框中心的点击不归展开按钮处理，鼠标点了不会有反应");
                }

                toggle.IsChecked = true;
                PumpDispatcher();
                var opened = combo.IsDropDownOpen;
                var popupItems = CountPopupItems(combo);
                report.AppendLine($"[展开] 按下展开按钮后 IsDropDownOpen={opened}，弹出层里的选项 {popupItems} 个");
                if (!opened || popupItems != combo.Items.Count)
                {
                    failures++;
                    report.AppendLine("       [FAIL] 下拉打不开或弹出层没生成选项，鼠标点上去会“点了没反应”");
                }

                toggle.IsChecked = false;
                PumpDispatcher();
            }

            // ② 改选择能不能写回账号（绑定的核心）。
            foreach (var (index, expected) in new[] { (1, ServerOptions.Liantong), (2, ServerOptions.Dianxin2) })
            {
                combo.SelectedIndex = index;
                PumpDispatcher();
                var pass = string.Equals(account.Server, expected, StringComparison.Ordinal);
                if (!pass)
                {
                    failures++;
                }

                report.AppendLine($"[改值] SelectedIndex={index} → 账号区服「{account.Server}」"
                                  + $"　期望「{expected}」 {(pass ? "[OK]" : "[FAIL]")}");
            }

            // 还原，别把用户数据改坏。
            account.Server = original;
            combo.SelectedItem = original;
            PumpDispatcher();
            report.AppendLine($"[还原] 账号区服已改回「{account.Server}」");
        }

        report.AppendLine();
        report.AppendLine(failures == 0
            ? "全部通过：下拉能展开、选项齐全、改值能写回账号。"
            : $"发现 {failures} 处问题。");

        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "combocheck.txt");
        File.WriteAllText(path, report.ToString());
        services.Log.Info($"下拉框自检完成：{path}", "自检");
        Console.WriteLine(report.ToString());

        if (!keepWindow)
        {
            window.Close();
        }

        Shutdown(failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// 真实点击自检：把账号页显示在屏幕左上角（无边框、置顶，几百毫秒后关闭），
    /// 用真实鼠标事件点下拉框、再点第二项，看值有没有真的换过去。
    /// 这是唯一能复现「鼠标点上去没反应」的办法——纯代码设置 IsDropDownOpen 是能过的。
    /// 用法：QQSpeedAssistant.exe --comboclick（报告写进 User/debug/comboclick.txt）。
    /// </summary>
    private void RunComboClickCheck(AppServices services)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("区服下拉真实点击自检（会短暂弹出一个置顶窗口，并用真实鼠标点两下）");
        report.AppendLine();

        var vm = new AccountsPageViewModel(services);
        var page = new Views.Pages.AccountsPage { DataContext = vm };
        var window = new Window
        {
            Content = page,
            Width = 1100,
            Height = 760,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 60,
            Top = 60
        };

        MainWindow ??= window;
        window.Show();
        window.Activate();
        window.UpdateLayout();
        PumpDispatcher();

        var failures = 0;
        var failuresReport = new List<string>();
        NativeMethods.GetCursorPos(out var savedCursor);

        var combos = new List<System.Windows.Controls.ComboBox>();
        CollectVisuals(page, combos);
        var account = vm.Accounts.FirstOrDefault();
        if (combos.Count == 0 || account == null)
        {
            report.AppendLine("[FAIL] 没找到下拉框或账号");
            failures++;
        }
        else
        {
            var combo = combos[0];
            var original = account.Server;
            report.AppendLine($"账号原区服：{original}");

            // 0. 先把坐标换算本身验一遍：DPI、命中测试、以及「点普通按钮」的对照。
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            report.AppendLine($"[环境] DPI 缩放={dpi.DpiScaleX:0.##}　窗口位置={window.Left:0},{window.Top:0}");

            var comboPoint = combo.PointToScreen(new Point(combo.ActualWidth / 2, combo.ActualHeight / 2));
            var local = page.PointFromScreen(comboPoint);
            var hit = page.InputHitTest(local) as System.Windows.DependencyObject;
            report.AppendLine($"[命中测试] 下拉框屏幕坐标 {comboPoint.X:0},{comboPoint.Y:0} 命中的是 "
                              + $"{hit?.GetType().Name ?? "null"}");

            // 命中链：看清那个 Border 到底属于下拉框外框，还是模板里的展开按钮。
            var chain = new List<string>();
            var node = hit;
            while (node != null && chain.Count < 7)
            {
                chain.Add(node is System.Windows.FrameworkElement element && !string.IsNullOrEmpty(element.Name)
                    ? $"{node.GetType().Name}「{element.Name}」"
                    : node.GetType().Name);
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }

            report.AppendLine("[命中链] " + string.Join(" ← ", chain));
            if (hit is not (System.Windows.Controls.ComboBox or System.Windows.Controls.Primitives.ToggleButton
                or System.Windows.Controls.Border or System.Windows.Controls.Grid
                or System.Windows.Controls.ContentPresenter or System.Windows.Shapes.Path
                or System.Windows.Controls.ScrollViewer or System.Windows.Controls.ItemsControl
                or System.Windows.Controls.TextBlock or System.Windows.Documents.Run))
            {
                failuresReport.Add($"坐标换算可能不准：那个点上命中 {hit?.GetType().Name ?? "null"} 而不是下拉框内部元素");
            }

            // 对照：点「添加一行」按钮，如果连按钮都点不动，就是坐标/注入问题而不是下拉框自己的问题。
            var before = vm.Accounts.Count;
            var addButton = new List<System.Windows.Controls.Button>();
            CollectVisuals(page, addButton);
            var addButtonControl = addButton.FirstOrDefault(b => (b.Content as string) == "添加一行");
            if (addButtonControl != null)
            {
                ClickAt(addButtonControl.PointToScreen(
                    new Point(addButtonControl.ActualWidth / 2, addButtonControl.ActualHeight / 2)));
                var clicks = vm.Accounts.Count - before;
                report.AppendLine($"[对照] 点「添加一行」后账号数 {before} → {vm.Accounts.Count} "
                                  + (clicks == 1 ? "[OK] 点击链路正常" : "[FAIL] 连普通按钮都点不动"));
                if (clicks != 1)
                {
                    failuresReport.Add("连普通按钮都点不动：坐标换算或鼠标注入有问题，不是下拉框的原因");
                }

                while (vm.Accounts.Count > before)
                {
                    vm.Accounts.RemoveAt(vm.Accounts.Count - 1);
                }
            }

            // ① 真实点一下下拉框中心。拆成「按下」「抬起」两步，看是不是刚展开就被关掉。
            // 同时挂上事件计数器：能分清「压根没收到点击」和「收到了但又被关掉」。
            var toggleButton = combo.Template.FindName("DropDownToggle", combo)
                as System.Windows.Controls.Primitives.ToggleButton;
            var checkedCount = 0;
            var uncheckedCount = 0;
            var dropDownOpened = 0;
            var dropDownClosed = 0;
            var comboGotDown = 0;
            if (toggleButton != null)
            {
                toggleButton.Checked += (_, _) => checkedCount++;
                toggleButton.Unchecked += (_, _) => uncheckedCount++;
            }

            combo.DropDownOpened += (_, _) => dropDownOpened++;
            combo.DropDownClosed += (_, _) => dropDownClosed++;
            combo.PreviewMouseLeftButtonDown += (_, _) => comboGotDown++;

            report.AppendLine($"[展开按钮] 尺寸={(toggleButton?.ActualWidth ?? -1):0}x{(toggleButton?.ActualHeight ?? -1):0}"
                              + $"　可见={toggleButton?.IsVisible}　可点={toggleButton?.IsHitTestVisible}"
                              + $"　可用={toggleButton?.IsEnabled}");

            NativeMethods.SetCursorPos((int)Math.Round(comboPoint.X), (int)Math.Round(comboPoint.Y));
            PumpDispatcher();
            NativeMethods.mouse_event(0x0002, 0, 0, 0, 0);
            PumpDispatcher();
            var openedAfterDown = combo.IsDropDownOpen;
            report.AppendLine($"[按下] IsDropDownOpen={openedAfterDown}　"
                              + $"展开按钮 Checked={checkedCount}／Unchecked={uncheckedCount}　"
                              + $"下拉 Opened={dropDownOpened}／Closed={dropDownClosed}　收到按下={comboGotDown}");

            NativeMethods.mouse_event(0x0004, 0, 0, 0, 0);
            PumpDispatcher();
            var opened = combo.IsDropDownOpen;
            report.AppendLine($"[抬起] IsDropDownOpen={opened}　"
                              + $"展开按钮 Checked={checkedCount}／Unchecked={uncheckedCount}　"
                              + $"下拉 Opened={dropDownOpened}／Closed={dropDownClosed}　收到按下={comboGotDown}");

            if (comboGotDown == 0)
            {
                failures++;
                failuresReport.Add("下拉框根本收不到鼠标按下事件：点击被上层元素吃掉了");
            }
            else if (checkedCount == 0)
            {
                failures++;
                failuresReport.Add("按下事件到了下拉框，但模板里的展开按钮没有切换状态（ToggleButton 没收到点击）");
            }
            else if (openedAfterDown && !opened)
            {
                failures++;
                failuresReport.Add("按下时能展开、抬起后又被关掉：抬起事件被当成「点了外面」，"
                                   + "所以鼠标点上去看起来毫无反应");
            }
            else if (!opened)
            {
                failures++;
                failuresReport.Add("真实点击下拉框没有展开，说明点击没落到展开按钮上");
            }

            // ② 真实点第二项（联通区）。
            var popupItems = new List<System.Windows.Controls.ComboBoxItem>();
            if (combo.Template.FindName("PART_Popup", combo) is System.Windows.Controls.Primitives.Popup popup
                && popup.Child is System.Windows.DependencyObject child)
            {
                CollectVisuals(child, popupItems);
            }

            report.AppendLine($"[弹出层] 选项 {popupItems.Count} 个"
                              + (popupItems.Count > 0
                                  ? "，宽度：" + string.Join("／", popupItems.Select(o => $"{o.ActualWidth:0}"))
                                  : ""));
            if (popupItems.Count >= 2)
            {
                var target = popupItems[1];
                report.AppendLine($"[第二项] 「{target.Content}」尺寸 {target.ActualWidth:0}x{target.ActualHeight:0}");

                // 点在第二项正中间（文字区域）。
                ClickAt(target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2)));
                var changed = string.Equals(account.Server, ServerOptions.Liantong, StringComparison.Ordinal);
                report.AppendLine($"[点击第二项] 账号区服=「{account.Server}」　期望「{ServerOptions.Liantong}」"
                                  + $" {(changed ? "[OK]" : "[FAIL]")}");
                if (!changed)
                {
                    failures++;
                    failuresReport.Add("点了第二项但区服没变，可能弹层在鼠标抬起前就关了，或点到了空白处");
                }

                // ③ 再验一次「点右半边」：选项背景只有文字那么宽时，右边是死区。
                combo.SelectedItem = original;
                Settle(200);
                for (var attempt = 0; attempt < 3 && !combo.IsDropDownOpen; attempt++)
                {
                    ClickAt(combo.PointToScreen(new Point(combo.ActualWidth / 2, combo.ActualHeight / 2)));
                    Settle(300);
                }

                popupItems.Clear();
                var livePopup = combo.Template.FindName("PART_Popup", combo)
                    as System.Windows.Controls.Primitives.Popup;
                if (livePopup?.Child is System.Windows.DependencyObject child2)
                {
                    CollectVisuals(child2, popupItems);
                }

                if (popupItems.Count >= 2)
                {
                    var target2 = popupItems[1];
                    var listWidth = (livePopup?.Child as System.Windows.FrameworkElement)?.ActualWidth ?? 0;
                    var deadZone = listWidth - target2.ActualWidth;
                    // 弹出层自己有一圈 1px 边框，量出来差 2～3px 属于正常，这里只揪明显没铺满的情况。
                    report.AppendLine($"[死区检查] 选中项宽 {target2.ActualWidth:0}，弹出层宽 {listWidth:0}；"
                                      + (deadZone > 6 ? $"右侧 {deadZone:0}px 是死区（点了不生效）" : "整行都能点"));
                    if (deadZone > 6)
                    {
                        failuresReport.Add("选项行没铺满弹出层宽度，点右侧空白不会选中（看起来就是“点不动”）");
                    }

                    // 点在文字右边（选项行的空白处）：这里以前是死区，点了不会选中。
                    ClickAt(target2.PointToScreen(new Point(target2.ActualWidth * 0.75, target2.ActualHeight / 2)));
                    var rightHit = string.Equals(account.Server, ServerOptions.Liantong, StringComparison.Ordinal);
                    report.AppendLine($"[点点右侧] 账号区服=「{account.Server}」 {(rightHit ? "[OK]" : "[FAIL] 这一侧点不中")}");
                    if (!rightHit)
                    {
                        failures++;
                    }
                }
            }

            account.Server = original;
            combo.SelectedItem = original;
            PumpDispatcher();
        }

        NativeMethods.SetCursorPos(savedCursor.X, savedCursor.Y);

        report.AppendLine();
        if (failuresReport.Count > 0)
        {
            report.AppendLine("问题清单：");
            foreach (var line in failuresReport)
            {
                report.AppendLine(" - " + line);
            }
        }

        report.AppendLine(failures == 0 ? "真实点击全部正常。" : $"发现 {failures} 处问题。");

        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "comboclick.txt");
        File.WriteAllText(path, report.ToString());
        services.Log.Info($"真实点击自检完成：{path}", "自检");
        Console.WriteLine(report.ToString());

        window.Close();
        Shutdown(failures == 0 ? 0 : 1);
    }

    /// <summary>在屏幕坐标处用真实鼠标左键点一下。</summary>
    private void ClickAt(Point screenPoint)
    {
        NativeMethods.SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
        PumpDispatcher();
        NativeMethods.mouse_event(0x0002, 0, 0, 0, 0);
        PumpDispatcher();
        NativeMethods.mouse_event(0x0004, 0, 0, 0, 0);
        PumpDispatcher();
    }

    /// <summary>
    /// 等界面稳定一会儿：弹出层有淡入动画，动画没跑完时里面的选项还不能点，
    /// 立刻点击会落到空处，看起来像「点了没反应」。
    /// </summary>
    private void Settle(int milliseconds)
    {
        for (var i = 0; i < Math.Max(1, milliseconds / 50); i++)
        {
            Thread.Sleep(50);
            PumpDispatcher();
        }
    }

    /// <summary>把界面线程上排队的布局/绑定任务跑完，再继续断言。</summary>
    private void PumpDispatcher()
        => Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    /// <summary>把一个可视化树里所有 T 类型控件收集出来。</summary>
    private static void CollectVisuals<T>(System.Windows.DependencyObject root, List<T> result)
        where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                result.Add(typed);
            }

            CollectVisuals(child, result);
        }
    }

    /// <summary>判断 element 是否在 ancestor 的可视子树里（用来确认点击落点归不归这个控件管）。</summary>
    private static bool IsDescendantOf(System.Windows.DependencyObject? ancestor,
        System.Windows.DependencyObject? element)
    {
        if (ancestor == null || element == null)
        {
            return false;
        }

        var node = element;
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>数一数下拉弹出层里真正生成出来的选项个数。</summary>
    private static int CountPopupItems(System.Windows.Controls.ComboBox combo)
    {
        if (combo.Template.FindName("PART_Popup", combo) is not System.Windows.Controls.Primitives.Popup popup)
        {
            return -1;
        }

        if (popup.Child is not System.Windows.DependencyObject child)
        {
            return 0;
        }

        var items = new List<System.Windows.Controls.ComboBoxItem>();
        CollectVisuals(child, items);
        return items.Count;
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
    /// 区服自检：验证「区服」下拉的取值与旧写法归一化规则。
    /// 账号页那一列是下拉框，只认 电信区／联通区／电信2区，历史数据里的
    /// 电信／电一／电二／联通／网通 都要能自动折成这三项。
    /// 用法：QQSpeedAssistant.exe --servercheck（报告写进 User/debug/servercheck.txt）。
    /// </summary>
    private static int RunServerCheck()
    {
        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);

        var report = new System.Text.StringBuilder();
        report.AppendLine("区服自检：下拉选项 = " + string.Join(" ／ ", Models.ServerOptions.All));
        report.AppendLine();

        var failures = 0;
        void Normalize(string label, string? input, string expected)
        {
            var actual = Models.ServerOptions.Normalize(input);
            var pass = string.Equals(actual, expected, StringComparison.Ordinal);
            if (!pass)
            {
                failures++;
            }

            report.AppendLine($"{(pass ? "[OK]" : "[FAIL]")} {label,-26} 「{input}」 → 「{actual}」 期望「{expected}」");
        }

        Normalize("实测旧数据", "联通", Models.ServerOptions.Liantong);
        Normalize("标准写法原样", Models.ServerOptions.Liantong, Models.ServerOptions.Liantong);
        Normalize("电信", "电信", Models.ServerOptions.Dianxin);
        Normalize("电一", "电一", Models.ServerOptions.Dianxin);
        Normalize("电1", "电1", Models.ServerOptions.Dianxin);
        Normalize("电二", "电二", Models.ServerOptions.Dianxin2);
        Normalize("电信二区", "电信二区", Models.ServerOptions.Dianxin2);
        Normalize("电信2区", "电信2区", Models.ServerOptions.Dianxin2);
        Normalize("网通", "网通", Models.ServerOptions.Liantong);
        Normalize("带空格", "  联通  ", Models.ServerOptions.Liantong);
        Normalize("空字符串", "", "");
        Normalize("null", null, "");
        Normalize("无法识别", "移动区", "移动区");

        report.AppendLine();
        report.AppendLine("二、哪些值算下拉里的合法项");

        void Known(string label, string? input, bool expected)
        {
            var actual = Models.ServerOptions.IsKnown(input);
            var pass = actual == expected;
            if (!pass)
            {
                failures++;
            }

            report.AppendLine($"{(pass ? "[OK]" : "[FAIL]")} {label,-26} 「{input}」 期望={expected} 实际={actual}");
        }

        Known("电信区", Models.ServerOptions.Dianxin, true);
        Known("联通区", Models.ServerOptions.Liantong, true);
        Known("电信2区", Models.ServerOptions.Dianxin2, true);
        Known("旧写法「联通」", "联通", false);
        Known("空值", "", false);
        Known("null", null, false);

        report.AppendLine();
        report.AppendLine(failures == 0
            ? "全部通过：旧写法都能折成下拉里的三项，无法识别的值按原样返回（导入时会提示并归到默认区服）。"
            : $"{failures} 个用例不符合预期。");

        var outPath = Path.Combine(directory, "servercheck.txt");
        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine(report.ToString());
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 抓图自检：用 BitBlt 把指定窗口（默认前台窗口）拷下来，报告尺寸、颜色种类并存图。
    /// 验证码的屏幕拷贝走的就是这条链路，换机器或换缩放比例后跑一次即可确认能抓。
    /// 用法：QQSpeedAssistant.exe --screencheck [窗口标题关键字]
    /// </summary>
    private static int RunScreenCheck(string[] args)
    {
        var directory = Path.Combine(PathService.UserDir, "debug");
        Directory.CreateDirectory(directory);

        var report = new System.Text.StringBuilder();
        var index = Array.IndexOf(args, "--screencheck");
        var keyword = index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--")
            ? args[index + 1]
            : "";

        var handle = nint.Zero;
        if (keyword.Length == 0)
        {
            handle = NativeMethods.GetForegroundWindow();
        }
        else
        {
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                {
                    return true;
                }

                if (!NativeMethods.GetWindowTitle(hWnd).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                handle = hWnd;
                return false;
            }, nint.Zero);
        }

        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var rect))
        {
            report.AppendLine(keyword.Length == 0 ? "取不到前台窗口。" : $"没有找到标题含「{keyword}」的窗口。");
            File.WriteAllText(Path.Combine(directory, "screencheck.txt"), report.ToString());
            return 1;
        }

        var window = new CaptchaWindowInfo(handle, NativeMethods.GetWindowTitle(handle), rect.Left, rect.Top,
            rect.Width, rect.Height);
        report.AppendLine($"窗口「{window.Title}」 {window.Width}x{window.Height} @({window.Left},{window.Top})");
        report.AppendLine($"虚拟桌面 {NativeMethods.VirtualScreenWidth}x{NativeMethods.VirtualScreenHeight}");

        // 窗口一部分在屏幕外时，屏幕拷贝拿到的那些像素必然是全黑，先提示出来，别误判成抓图坏了。
        var offscreen = window.Left < 0 || window.Top < 0
            || window.Left + window.Width > NativeMethods.VirtualScreenWidth
            || window.Top + window.Height > NativeMethods.VirtualScreenHeight;
        if (offscreen)
        {
            report.AppendLine("注意：窗口有部分在屏幕外，屏幕拷贝在那一块只能是黑的。");
        }

        using var image = CaptchaWindowLocator.CaptureByScreen(window, null);
        if (image == null || image.Empty())
        {
            report.AppendLine("屏幕拷贝失败：BitBlt 返回空。");
            File.WriteAllText(Path.Combine(directory, "screencheck.txt"), report.ToString());
            return 1;
        }

        // 全黑或只有一两种颜色，说明拷到了空 DC，屏幕拷贝在这台机器上不可用。
        using var small = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.Resize(image, small, new OpenCvSharp.Size(160, 100), 0, 0,
            OpenCvSharp.InterpolationFlags.Area);
        var colors = new HashSet<string>();
        for (var y = 0; y < small.Height; y++)
        {
            for (var x = 0; x < small.Width; x++)
            {
                var p = small.At<OpenCvSharp.Vec3b>(y, x);
                colors.Add($"{p.Item0},{p.Item1},{p.Item2}");
            }
        }

        report.AppendLine($"抓到 {image.Width}x{image.Height}，缩略后颜色种类 {colors.Count}");
        var answer = colors.Count >= 8
            ? "屏幕拷贝可用（内容正常）。"
            : "屏幕拷贝拿到的画面几乎是纯色，这台机器上需要依赖 PrintWindow 兜底。";
        report.AppendLine(answer);

        var outPath = Path.Combine(directory, "screencheck.png");
        OpenCvSharp.Cv2.ImWrite(outPath, image);
        report.AppendLine($"截图：{outPath}");

        var txtPath = Path.Combine(directory, "screencheck.txt");
        File.WriteAllText(txtPath, report.ToString());
        Console.WriteLine(report.ToString());
        return colors.Count >= 8 ? 0 : 1;
    }

    /// <summary>
    /// 验证码自检：把验证码弹窗（或指定截图）切图、标注、调用视觉模型，全程写进 User/debug/captchacheck.txt。
    /// 用法：
    ///   --captchacheck                 抓当前弹窗，只切图＋识别，不点击；
    ///   --captchacheck --image a.png   离线跑一张截图（没有弹窗时也能验证切图与提示词）；
    ///   --captchacheck --solve         真的把识别结果点下去。
    /// </summary>
    private void RunCaptchaCheck(AppServices services, string[] args)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var directory = Path.Combine(PathService.UserDir, "debug");
            Directory.CreateDirectory(directory);

            var index = Array.IndexOf(args, "--image");
            var imagePath = index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--")
                ? args[index + 1]
                : null;

            var result = await CaptchaSelfCheck.RunAsync(services.Config.Settings, services.Log, imagePath,
                args.Contains("--solve"), CancellationToken.None);

            File.WriteAllText(Path.Combine(directory, "captchacheck.txt"), result.Report);
            Console.WriteLine(result.Report);
            Shutdown(result.ExitCode);
        });
    }

    /// <summary>
    /// 输入判定自检：用合成画面验证「框里到底有没有输入」的判定规则，
    /// 四种典型情形（空框、只有光标、只有边框高亮、真打进圆点/数字）各跑一遍。
    /// 用法：QQSpeedAssistant.exe --inkcheck（不用开游戏，报告写进 User/debug/inkcheck.txt）。
    /// </summary>
    private static void RunInkCheck()    {
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
