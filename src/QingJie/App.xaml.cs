using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QingJie.Ball;
using QingJie.Capture;
using QingJie.Core;
using QingJie.Pin;
using QingJie.Views;

namespace QingJie;

public partial class App : Application
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "QingJie";
    private const string CaptureSignalName = @"Local\QingJie_Capture_Signal";

    private readonly List<PinWindow> _pins = new();

    private Mutex? _mutex;
    private HotkeyManager? _hotkeyManager;
    private AppTray? _tray;
    private SettingsWindow? _settingsWindow;
    private FloatingBall? _ball;
    private bool _captureOnStart;

    /// <summary>当前 App 实例（供各窗口访问设置与托盘）。</summary>
    public static App CurrentInstance => (App)Current;

    public Settings Config { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 单实例：已有实例在跑时，若是“带 --capture 再次启动”，通知它截图后退出
        _mutex = new Mutex(true, @"Local\QingJie_SingleInstance_7C1D4F", out bool isNewInstance);
        if (!isNewInstance)
        {
            Logger.Info("已存在运行中的实例");
            if (e.Args.Contains("--capture")) SignalRunningInstance();
            Shutdown();
            return;
        }

        Config = Settings.Load();
        Logger.Info($"轻截启动 v{VersionText()}，热键 {Config.Hotkey}，日志目录 {AppPaths.DataDirectory}");

        // 截图结束后要把悬浮球放回来，任何入口（含自检模式）都要挂上
        CaptureSession.Closed += OnCaptureClosed;

        if (HandleCommandLine(e.Args)) return;

        _hotkeyManager = new HotkeyManager();
        _hotkeyManager.Pressed += CaptureNow;

        bool registered = _hotkeyManager.Register(Config.Hotkey);

        _tray = new AppTray(this, Config);
        ApplyStartupRegistration(Config.StartWithWindows);

        if (!_captureOnStart && Config.ShowFloatingBall) ShowBall();

        if (!registered)
        {
            _tray.ShowBalloon("轻截", $"快捷键 {Config.Hotkey} 被其它程序占用，请右键托盘图标进入设置修改。");
        }

        if (_captureOnStart)
        {
            Dispatcher.BeginInvoke(new Action(CaptureNow), DispatcherPriority.ApplicationIdle);
        }
        else
        {
            StartCaptureListener();
        }

        Logger.Info("轻截已就绪");
    }

    private EventWaitHandle? _captureSignal;

    /// <summary>监听其它实例发来的“开始截图”信号（本实例为主实例时）。</summary>
    private void StartCaptureListener()
    {
        try
        {
            _captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureSignalName);
            var handle = _captureSignal;

            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        handle.WaitOne();
                        Dispatcher.BeginInvoke(new Action(CaptureNow));
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"截图信号处理失败：{ex.Message}");
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "QingJie.CaptureSignal",
            };

            thread.Start();
        }
        catch (Exception ex)
        {
            Logger.Warn($"创建截图信号监听失败：{ex.Message}");
        }
    }

    /// <summary>通知已经在运行的实例开始截图。</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(CaptureSignalName);
            signal.Set();
        }
        catch (Exception ex)
        {
            Logger.Warn($"通知已有实例失败：{ex.Message}");
        }
    }

    private static string VersionText()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>处理命令行开关，返回 true 表示已完成（不再初始化托盘）。</summary>
    private bool HandleCommandLine(string[] args)
    {
        if (args.Length == 0) return false;

        if (args.Contains("--snapshot"))
        {
            int index = Array.IndexOf(args, "--snapshot");
            if (index + 1 < args.Length)
            {
                RunSnapshot(args[index + 1]);
                return true;
            }
        }

        if (args.Contains("--compose"))
        {
            int index = Array.IndexOf(args, "--compose");
            if (index + 1 < args.Length)
            {
                RunCompose(args[index + 1]);
                return true;
            }
        }

        if (args.Contains("--pintest"))
        {
            int index = Array.IndexOf(args, "--pintest");
            if (index + 1 < args.Length)
            {
                RunPinTest(args[index + 1]);
                return true;
            }
        }

        if (args.Contains("--dragtest"))
        {
            int index = Array.IndexOf(args, "--dragtest");
            if (index + 1 < args.Length)
            {
                RunDragTest(args[index + 1]);
                return true;
            }
        }

        if (args.Contains("--cliptest"))
        {
            int index = Array.IndexOf(args, "--cliptest");
            if (index + 1 < args.Length)
            {
                RunClipTest(args[index + 1]);
                return true;
            }
        }

        if (args.Contains("--annotest"))
        {
            int index = Array.IndexOf(args, "--annotest");
            if (index + 1 < args.Length)
            {
                RunAnnoTest(args[index + 1]);
                return true;
            }
        }

        if (args.Contains("--ocrtest"))
        {
            int index = Array.IndexOf(args, "--ocrtest");
            if (index + 1 < args.Length)
            {
                RunOcrTest(args[index + 1]);
                return true;
            }
        }

        if (args.Contains("--balltest"))
        {
            int index = Array.IndexOf(args, "--balltest");
            if (index + 1 < args.Length)
            {
                RunBallTest(args[index + 1]);
                return true;
            }
        }

        if (args.Contains("--capture"))
        {
            _captureOnStart = true;
        }

        return false;
    }

    // ------------------------------------------------------------ 主要操作

    /// <summary>开始截图。悬浮球先退场，避免自己被截进画面。</summary>
    public async void CaptureNow()
    {
        if (CaptureSession.IsCapturing) return;

        if (_ball is { IsVisible: true })
        {
            _ball.HideBall();
            // 等合成器真正把球从屏幕上撤掉，否则可能截到残影
            await Task.Delay(120);
        }

        try
        {
            CaptureSession.Start(Config);
        }
        catch (Exception ex)
        {
            Logger.Error("开始截图失败", ex);
            Toast.Show("截图失败，请查看日志");
        }

        // 没真正进入截图（例如抓屏失败），立刻把球放回来
        if (!CaptureSession.IsCapturing) RestoreBall();
    }

    /// <summary>截图流程结束（复制 / 保存 / 贴图 / 取消）后把悬浮球放回来。</summary>
    private void OnCaptureClosed() => RestoreBall();

    private void RestoreBall()
    {
        if (Config.ShowFloatingBall) ShowBall();
    }

    // ------------------------------------------------------------ 悬浮球

    /// <summary>显示悬浮球（首次调用时创建窗口）。</summary>
    public void ShowBall()
    {
        if (_ball is null)
        {
            _ball = new FloatingBall();
            _ball.Clicked += CaptureNow;
            _ball.Moved += OnBallMoved;
        }

        _ball.ShowAt(Config.BallX, Config.BallY);
    }

    /// <summary>隐藏悬浮球；persist 为 true 时同时关掉设置里的开关。</summary>
    public void HideBall(bool persist)
    {
        _ball?.HideBall();

        if (!persist) return;

        Config.ShowFloatingBall = false;
        Config.Save();
        _tray?.RefreshBallItem(false);
    }

    /// <summary>托盘菜单里的「显示悬浮球」开关。</summary>
    public void ToggleBall()
    {
        bool show = !(Config.ShowFloatingBall && _ball is { IsVisible: true });

        Config.ShowFloatingBall = show;
        Config.Save();
        _tray?.RefreshBallItem(show);

        if (show) ShowBall();
        else _ball?.HideBall();

        Logger.Info($"悬浮球：{(show ? "显示" : "隐藏")}");
    }

    /// <summary>拖动结束后记住位置（物理像素）。</summary>
    private void OnBallMoved()
    {
        if (_ball is null) return;

        var rect = _ball.Bounds();
        if (rect.Width <= 0) return;

        Config.BallX = rect.Left;
        Config.BallY = rect.Top;
        Config.Save();
        Logger.Info($"悬浮球位置：({rect.Left},{rect.Top})");
    }

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(Config);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void OpenSaveDirectory()
    {
        try
        {
            Directory.CreateDirectory(Config.SaveDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Config.SaveDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("打开截图目录失败", ex);
            Toast.Show("打开目录失败");
        }
    }

    public void ApplySettings(Settings updated)
    {
        Config = updated;

        if (_hotkeyManager is not null)
        {
            if (!_hotkeyManager.Register(Config.Hotkey))
            {
                _tray?.ShowBalloon("轻截", $"快捷键 {Config.Hotkey} 注册失败，可能被其它程序占用。");
            }
        }

        _tray?.RefreshHotkeyText(Config.Hotkey);
        ApplyStartupRegistration(Config.StartWithWindows);

        if (Config.ShowFloatingBall) ShowBall();
        else _ball?.HideBall();
        _tray?.RefreshBallItem(Config.ShowFloatingBall);

        Logger.Info($"设置已更新：热键 {Config.Hotkey}");
    }

    public void RegisterPin(PinWindow pin)
    {
        _pins.Add(pin);
    }

    public void UnregisterPin(PinWindow pin)
    {
        _pins.Remove(pin);
    }

    public void ExitApp()
    {
        Logger.Info("退出轻截");

        foreach (var pin in _pins.ToList())
        {
            try
            {
                pin.Close();
            }
            catch
            {
                // ignored
            }
        }

        _pins.Clear();
        _ball?.Close();
        _ball = null;
        _hotkeyManager?.Dispose();
        _hotkeyManager = null;
        _tray?.Dispose();
        _tray = null;

        Shutdown();
    }

    // ------------------------------------------------------------ 自启动

    private static void ApplyStartupRegistration(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(StartupValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(StartupValueName) is not null)
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"写入开机自启动失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------ 异常

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("未处理异常", e.Exception);
        e.Handled = true;
        Toast.Show("轻截遇到异常，已记录日志");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CaptureSession.Closed -= OnCaptureClosed;
        _hotkeyManager?.Dispose();
        _tray?.Dispose();
        _captureSignal?.Dispose();

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // 非持有者（重复启动退出的实例）释放会抛异常，忽略
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ------------------------------------------------------------ 开发自检

    /// <summary>开发用：把截图界面渲染状态抓成图片，便于核对 UI。</summary>
    private async void RunSnapshot(string outputPath)
    {
        try
        {
            SaveScreen(Path.Combine(Path.GetDirectoryName(outputPath) ?? ".",
                Path.GetFileNameWithoutExtension(outputPath) + "_base.png"));

            CaptureSession.Start(Config);
            await Task.Delay(900);
            SaveScreen(outputPath);

            CaptureSession.ApplyDemoState();
            await Task.Delay(600);

            var selected = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".",
                Path.GetFileNameWithoutExtension(outputPath) + "_selected.png");
            SaveScreen(selected);
        }
        catch (Exception ex)
        {
            Logger.Error("快照模式失败", ex);
        }
        finally
        {
            ExitApp();
        }
    }

    /// <summary>开发用：导出示例选区合成的结果图，核对裁剪/标注缩放/剪贴板。</summary>
    private async void RunCompose(string outputPath)
    {
        try
        {
            CaptureSession.Start(Config);
            await Task.Delay(800);
            CaptureSession.ApplyDemoState();
            await Task.Delay(500);

            var image = CaptureSession.ComposeActive();
            if (image is not null)
            {
                ImageIo.SavePng(image, outputPath);
                Logger.Info($"合成结果已保存：{outputPath} ({image.PixelWidth}x{image.PixelHeight})");

                bool copied = ImageIo.CopyToClipboard(image);
                var back = Clipboard.GetImage();
                Logger.Info(back is null
                    ? $"剪贴板校验：写入={copied}，读回失败"
                    : $"剪贴板校验：写入={copied}，读回 {back.PixelWidth}x{back.PixelHeight}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("合成模式失败", ex);
        }
        finally
        {
            ExitApp();
        }
    }

    /// <summary>开发用：验证贴图窗口的定位精度与 1:1 显示。</summary>
    private async void RunPinTest(string outputPath)
    {
        try
        {
            var monitors = Native.EnumMonitors();
            var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (monitor is null) return;

            using var grab = ScreenGrab.Capture();
            var crop = grab.CropPhysical(monitor.X + 300, monitor.Y + 300, 600, 400);

            var srcPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".",
                Path.GetFileNameWithoutExtension(outputPath) + "_src.png");
            ImageIo.SavePng(crop, srcPath);

            var pin = new PinWindow(crop, monitor.Scale);
            RegisterPin(pin);
            pin.ShowAt(monitor.X + 300, monitor.Y + 300);

            await Task.Delay(700);
            pin.ContextMenu.PlacementTarget = pin;
            pin.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            pin.ContextMenu.IsOpen = true;
            await Task.Delay(500);
            SaveScreen(outputPath);
            Logger.Info($"贴图自检完成：位置 ({monitor.X + 300},{monitor.Y + 300}) 尺寸 600x400");
            pin.ContextMenu.IsOpen = false;
            pin.Close();
        }
        catch (Exception ex)
        {
            Logger.Error("贴图自检失败", ex);
        }
        finally
        {
            ExitApp();
        }
    }

    /// <summary>开发用：用合成鼠标事件真实拖拽一次，验证划区/移动是否响应。</summary>
    private async void RunDragTest(string outputPath)
    {
        try
        {
            var monitors = Native.EnumMonitors();
            var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (monitor is null) return;

            // 先拍一张没有覆盖层的桌面，用于比对“拖出来的选区”裁得准不准
            SaveScreen(Path.Combine(Path.GetDirectoryName(outputPath) ?? ".",
                Path.GetFileNameWithoutExtension(outputPath) + "_base.png"));

            CaptureSession.Start(Config);
            await Task.Delay(900);

            var overlay = CaptureSession.ActiveWindow;
            if (overlay is null)
            {
                Logger.Error("拖拽自检失败：覆盖层未创建");
                return;
            }

            Logger.Info($"拖拽自检开始 | {overlay.DebugState()}");

            // 阶段一：空白处按下并拖出一个选区
            await DragAsync(monitor.X + 200, monitor.Y + 200, monitor.X + 900, monitor.Y + 700, 12);
            await Task.Delay(250);
            string afterCreate = overlay.DebugState();
            Logger.Info($"阶段1 划区后 | {afterCreate}");

            // 阶段二：在选区内部按下并整体拖动
            int cx = monitor.X + 550;
            int cy = monitor.Y + 450;
            await DragAsync(cx, cy, cx + 100, cy + 60, 10);
            await Task.Delay(250);
            Logger.Info($"阶段2 移动后 | {overlay.DebugState()}");

            // 阶段三：拖右下角控制点改尺寸
            var before = overlay.DebugState();
            await DragAsync(monitor.X + 1000, monitor.Y + 760, monitor.X + 1100, monitor.Y + 810, 8);
            await Task.Delay(250);
            Logger.Info($"阶段3 缩放后 | {overlay.DebugState()}");

            // 阶段四：按 R 切矩形工具，再拖一笔标注
            var hwnd = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            Logger.Info($"焦点检查：foreground={Native.GetForegroundWindow()} overlay={hwnd} " +
                        $"wpfFocus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name ?? "null"} " +
                        $"isActive={overlay.IsActive}");
            InputSim.KeyPress(0x52);
            await Task.Delay(200);
            Logger.Info($"按键后 | {overlay.DebugState()}");
            await DragAsync(monitor.X + 350, monitor.Y + 320, monitor.X + 650, monitor.Y + 520, 10);
            await Task.Delay(300);
            Logger.Info($"阶段4 标注后 | {overlay.DebugState()}");

            // 阶段五：T 切文字工具 → 点一下 → 打字 → 回车提交
            InputSim.KeyPress(0x54);
            await Task.Delay(200);
            InputSim.MoveTo(monitor.X + 450, monitor.Y + 420);
            await Task.Delay(150);
            InputSim.LeftDown();
            await Task.Delay(120);
            InputSim.LeftUp();
            await Task.Delay(250);
            Logger.Info($"阶段5 打开输入框 | {overlay.DebugState()}");
            overlay.SetTextForTest("轻截 QJTEST");
            await Task.Delay(150);
            InputSim.KeyPress(0x0D);
            await Task.Delay(300);
            Logger.Info($"阶段5 提交文字 | {overlay.DebugState()}");

            SaveScreen(Path.Combine(Path.GetDirectoryName(outputPath) ?? ".",
                Path.GetFileNameWithoutExtension(outputPath) + "_ui.png"));

            var image = overlay.ComposeResult();
            ImageIo.SavePng(image, outputPath);
            Logger.Info($"拖拽自检合成：{image.PixelWidth}x{image.PixelHeight} 红色像素 {CountRedPixels(image)} " +
                        $"(阶段2 前 {before})");
        }
        catch (Exception ex)
        {
            Logger.Error("拖拽自检失败", ex);
        }
        finally
        {
            ExitApp();
        }
    }

    /// <summary>
    /// 开发用：验证标注「从鼠标按下点开始画」，且落在选区内不偏移。
    /// 先划一个固定选区，再在选区内拖一个矩形标注，最后比对合成图里红色像素的外接矩形。
    /// </summary>
    private async void RunAnnoTest(string outputPath)
    {
        try
        {
            var monitors = Native.EnumMonitors();
            var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (monitor is null) return;

            CaptureSession.Start(Config);
            await Task.Delay(900);

            var overlay = CaptureSession.ActiveWindow;
            if (overlay is null)
            {
                Logger.Error("标注自检：覆盖层未创建");
                return;
            }

            // 1) 划一个固定选区（物理像素）
            int selX = monitor.X + 200, selY = monitor.Y + 160;
            int selW = 640, selH = 440;
            await DragAsync(selX, selY, selX + selW, selY + selH, 10);
            await Task.Delay(250);

            // 2) 切矩形工具，在选区内从 A 拖到 B
            InputSim.KeyPress(0x52); // R
            await Task.Delay(250);

            int fromX = selX + 160, fromY = selY + 120;
            int toX = fromX + 220, toY = fromY + 160;
            await DragAsync(fromX, fromY, toX, toY, 10);
            await Task.Delay(300);

            var phys = overlay.SelectionPhysicalRect;
            double scale = overlay.Scale;

            var image = overlay.ComposeResult();
            ImageIo.SavePng(image, outputPath);

            var bounds = RedBounds(image);
            // 期望：标注在合成图中的位置 = 按下点相对选区左上角的物理偏移
            int expectLeft = fromX - phys.X;
            int expectTop = fromY - phys.Y;
            int expectRight = toX - phys.X;
            int expectBottom = toY - phys.Y;
            int tolerance = (int)Math.Ceiling(Math.Max(6, overlay.Scale * 6));

            Logger.Info($"标注自检：scale={scale:0.###} 选区物理=({phys.X},{phys.Y}) {phys.Width}×{phys.Height} " +
                        $"图像={image.PixelWidth}×{image.PixelHeight}");
            Logger.Info($"标注自检：期望 bbox=({expectLeft},{expectTop})-({expectRight},{expectBottom}) " +
                        $"实际 bbox=({bounds.X},{bounds.Y})-({bounds.X + bounds.Width},{bounds.Y + bounds.Height}) 容差={tolerance}");

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                Logger.Error("标注自检：合成图中没找到红色标注");
                return;
            }

            int dl = Math.Abs(bounds.X - expectLeft);
            int dt = Math.Abs(bounds.Y - expectTop);
            int dr = Math.Abs(bounds.X + bounds.Width - expectRight);
            int db = Math.Abs(bounds.Y + bounds.Height - expectBottom);
            bool pass = dl <= tolerance && dt <= tolerance && dr <= tolerance && db <= tolerance;

            Logger.Info(pass
                ? $"标注自检：通过（左上偏差 {dl},{dt} 右下偏差 {dr},{db}）"
                : $"标注自检：失败（左上偏差 {dl},{dt} 右下偏差 {dr},{db}）");
        }
        catch (Exception ex)
        {
            Logger.Error("标注自检失败", ex);
        }
        finally
        {
            ExitApp();
        }
    }

    /// <summary>开发用：验证取字的完整链路（造字 → 划区 → 系统 OCR → 面板文本）。</summary>
    private async void RunOcrTest(string outputPath)
    {
        Window? probe = null;
        try
        {
            probe = new Window
            {
                Width = 520,
                Height = 150,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Background = Brushes.White,
                Left = 320,
                Top = 300,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "轻截取字自检 QJ-OCR-2026",
                    FontSize = 38,
                    Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            probe.Show();
            await Task.Delay(700);

            var hwnd = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
            if (!Native.GetWindowRect(hwnd, out var rect))
            {
                Logger.Error("取字自检：拿不到测试窗口位置");
                return;
            }

            CaptureSession.Start(Config);
            await Task.Delay(900);

            var overlay = CaptureSession.ActiveWindow;
            if (overlay is null)
            {
                Logger.Error("取字自检：覆盖层未创建");
                return;
            }

            await DragAsync(rect.Left + 4, rect.Top + 4, rect.Right - 4, rect.Bottom - 4, 8);
            await Task.Delay(300);

            var watch = Stopwatch.StartNew();
            overlay.RunOcrForTest();

            while (watch.ElapsedMilliseconds < 25000)
            {
                await Task.Delay(120);
                if (!overlay.IsOcrBusy && overlay.OcrPanelVisible) break;
            }

            string text = overlay.OcrPanelText.Replace("\r\n", " / ").Replace("\n", " / ");
            Logger.Info($"取字自检：耗时 {watch.ElapsedMilliseconds} ms 面板={overlay.OcrPanelVisible} " +
                        $"状态=[{overlay.OcrPanelStatus}] 文本=[{text}]");

            if (overlay.OcrPanelStatus is "识别中…" or "")
            {
                Logger.Error("取字自检：超时未返回");
                return;
            }

            bool ok = text.Contains("2026") && (text.Contains("自检") || text.Contains("取字"));
            Logger.Info(ok ? "取字自检：通过" : "取字自检：失败（未识别出预期文字）");

            string clipboard = string.Empty;
            try
            {
                if (Clipboard.ContainsText()) clipboard = Clipboard.GetText().Replace("\r\n", " / ").Replace("\n", " / ");
            }
            catch (Exception ex)
            {
                Logger.Warn($"取字自检：读剪贴板失败 {ex.Message}");
            }

            Logger.Info($"取字自检：剪贴板=[{clipboard}]，与识别文本一致={clipboard == text}");

            await Task.Delay(500); // 等界面重绘完再截屏
            SaveScreen(outputPath);
        }
        catch (Exception ex)
        {
            Logger.Error("取字自检失败", ex);
        }
        finally
        {
            probe?.Close();
            ExitApp();
        }
    }

    /// <summary>开发用：验证剪贴板写入的格式与像素，模拟别的软件粘贴时看到的内容。</summary>
    private void RunClipTest(string outputPath)
    {
        try
        {
            var image = BuildClipTestImage();
            ImageIo.SavePng(image, outputPath);

            bool copied = ImageIo.CopyToClipboard(image);
            Logger.Info($"剪贴板自检：写入={copied} 图像 {image.PixelWidth}x{image.PixelHeight} {image.Format}");

            bool dipAvailable = ClipboardImage.IsAvailable(ClipboardImage.DibFormat);
            bool bitmapAvailable = ClipboardImage.IsAvailable(ClipboardImage.BitmapFormat);
            bool pngAvailable = ClipboardImage.IsAvailable(ClipboardImage.Png);
            bool mimeAvailable = ClipboardImage.IsAvailable(ClipboardImage.PngMime);
            Logger.Info($"剪贴板格式：CF_DIB={dipAvailable} CF_BITMAP={bitmapAvailable} " +
                        $"PNG={pngAvailable} image/png={mimeAvailable}");

            var dib = ClipboardImage.ReadBack(ClipboardImage.DibFormat);
            if (dib is null || dib.Length < 40)
            {
                Logger.Error("剪贴板自检：读不回 CF_DIB");
            }
            else
            {
                int biSize = BitConverter.ToInt32(dib, 0);
                int width = BitConverter.ToInt32(dib, 4);
                int height = BitConverter.ToInt32(dib, 8);
                short planes = BitConverter.ToInt16(dib, 12);
                short bitCount = BitConverter.ToInt16(dib, 14);
                int compression = BitConverter.ToInt32(dib, 16);
                int sizeImage = BitConverter.ToInt32(dib, 20);
                int expectedStride = width * 4;

                Logger.Info($"CF_DIB 头：biSize={biSize} {width}x{height} planes={planes} " +
                            $"bitCount={bitCount} compression={compression} sizeImage={sizeImage} " +
                            $"总长={dib.Length} 期望={40 + expectedStride * height}");

                // BI_RGB(0) 是各软件解析最稳的压缩方式；BI_BITFIELDS(3) 会让 GDI+/Qt 系读图失败
                bool headerOk = biSize == 40 && width == image.PixelWidth && height == image.PixelHeight
                                && planes == 1 && bitCount == 32 && compression == 0
                                && dib.Length == 40 + expectedStride * height;

                bool alphaOk = true;
                for (int i = 43; i < dib.Length; i += 4)
                {
                    if (dib[i] != 0xFF)
                    {
                        alphaOk = false;
                        break;
                    }
                }

                Logger.Info($"CF_DIB 校验：头部={headerOk} alpha全不透明={alphaOk} " +
                            $"左下角像素={DescribePixel(dib, width, height, 4, 4)} " +
                            $"中部像素={DescribePixel(dib, width, height, width / 2, height / 2)}");
            }

            var png = ClipboardImage.ReadBack(ClipboardImage.Png);
            bool pngOk = png is { Length: > 8 }
                         && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47;
            Logger.Info($"PNG 格式校验：{pngOk}（{(png?.Length ?? 0)} 字节）");
        }
        catch (Exception ex)
        {
            Logger.Error("剪贴板自检失败", ex);
        }
        finally
        {
            ExitApp();
        }
    }

    /// <summary>开发用：用合成鼠标验证悬浮球——显示、拖动、点击截图、退场、恢复。</summary>
    private async void RunBallTest(string outputPath)
    {
        string dir = Path.GetDirectoryName(outputPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(outputPath);
        string Shot(string suffix) => Path.Combine(dir, stem + suffix + ".png");

        // 记住用户真实设置，跑完还原，避免自检把球挪走
        bool oldShow = Config.ShowFloatingBall;
        int? oldX = Config.BallX;
        int? oldY = Config.BallY;

        try
        {
            Config.ShowFloatingBall = true;
            Config.BallX = null;
            Config.BallY = null;

            // 基准：还没有悬浮球的桌面
            SaveScreen(Shot("_base"));

            ShowBall();
            await Task.Delay(700);

            var ball = _ball;
            if (ball is null)
            {
                Logger.Error("悬浮球自检失败：窗口未创建");
                return;
            }

            var r1 = ball.Bounds();
            Logger.Info($"阶段1 悬浮球显示 | visible={ball.IsVisible} " +
                        $"rect={r1.Left},{r1.Top} {r1.Width}x{r1.Height}");

            // 阶段二：拖动 —— 窗口必须精确跟随光标，且不能被当成点击
            // 向左下拖，避开右边缘的工作区夹取
            int cx = r1.Left + r1.Width / 2;
            int cy = r1.Top + r1.Height / 2;
            await DragAsync(cx, cy, cx - 160, cy + 120, 12);
            await Task.Delay(300);

            var r2 = ball.Bounds();
            int dx = r2.Left - r1.Left;
            int dy = r2.Top - r1.Top;
            Logger.Info($"阶段2 拖动后 | rect={r2.Left},{r2.Top} dx={dx} dy={dy} " +
                        $"符合预期={Math.Abs(dx + 160) <= 3 && Math.Abs(dy - 120) <= 3} " +
                        $"capturing={CaptureSession.IsCapturing}");

            SaveScreen(Shot("_ball"));

            // 阶段三：单击球心 —— 应当进入截图，同时球自己退场
            int bx = r2.Left + r2.Width / 2;
            int by = r2.Top + r2.Height / 2;
            InputSim.MoveTo(bx, by);
            await Task.Delay(200);
            InputSim.LeftDown();
            await Task.Delay(90);
            InputSim.LeftUp();
            await Task.Delay(900);

            Logger.Info($"阶段3 单击后 | capturing={CaptureSession.IsCapturing} " +
                        $"ballVisible={ball.IsVisible} gesture=[{ball.LastGesture}]");
            SaveScreen(Shot("_overlay"));

            // 阶段四：Esc 取消 —— 球必须回到原位置
            CaptureSession.CancelActive();
            await Task.Delay(800);

            var r3 = ball.Bounds();
            Logger.Info($"阶段4 取消后 | capturing={CaptureSession.IsCapturing} " +
                        $"ballVisible={ball.IsVisible} rect={r3.Left},{r3.Top}");

            SaveScreen(Shot("_restored"));

            // 阶段五：右键 —— 菜单必须能弹出来
            var r4 = ball.Bounds();
            InputSim.MoveTo(r4.Left + r4.Width / 2, r4.Top + r4.Height / 2);
            await Task.Delay(250);
            InputSim.RightDown();
            await Task.Delay(90);
            InputSim.RightUp();
            await Task.Delay(500);

            SaveScreen(Shot("_menu"));
            Logger.Info($"阶段5 右键后 | menuOpen={ball.MenuIsOpen}");
            ball.CloseMenu();
        }
        catch (Exception ex)
        {
            Logger.Error("悬浮球自检失败", ex);
        }
        finally
        {
            Config.ShowFloatingBall = oldShow;
            Config.BallX = oldX;
            Config.BallY = oldY;
            Config.Save();
            ExitApp();
        }
    }

    /// <summary>造一张带已知色块的测试图，便于逐点核对剪贴板内容。</summary>
    private static BitmapSource BuildClipTestImage()
    {
        const int width = 640;
        const int height = 400;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0xCC)), null,
                new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), null,
                new Rect(40, 60, 120, 80));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48)), null,
                new Rect(500, 300, 100, 60));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>按 DIB 的自下而上布局取一个像素，返回 BGR 十六进制。</summary>
    private static string DescribePixel(byte[] dib, int width, int height, int x, int y)
    {
        int stride = width * 4;
        int offset = 40 + (height - 1 - y) * stride + x * 4;
        if (offset + 3 >= dib.Length) return "越界";
        return $"#{dib[offset + 2]:X2}{dib[offset + 1]:X2}{dib[offset]:X2}";
    }

    /// <summary>把光标从 (x1,y1) 分步移到 (x2,y2)，中间按下左键再抬起。</summary>
    private static async Task DragAsync(int x1, int y1, int x2, int y2, int steps)
    {
        InputSim.MoveTo(x1, y1);
        await Task.Delay(120);
        InputSim.LeftDown();
        await Task.Delay(80);

        for (int i = 1; i <= steps; i++)
        {
            int x = x1 + (x2 - x1) * i / steps;
            int y = y1 + (y2 - y1) * i / steps;
            InputSim.MoveTo(x, y);
            await Task.Delay(30);
        }

        await Task.Delay(80);
        InputSim.LeftUp();
        await Task.Delay(120);
    }

    /// <summary>统计合成图里接近纯红的像素数，用于确认标注确实画进了结果。</summary>
    private static int CountRedPixels(BitmapSource image)
    {
        int stride = image.PixelWidth * 4;
        var buffer = new byte[stride * image.PixelHeight];
        image.CopyPixels(buffer, stride, 0);

        int count = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            if (buffer[i + 2] > 190 && buffer[i + 1] < 110 && buffer[i] < 110) count++;
        }

        return count;
    }

    /// <summary>求合成图里接近纯红像素的外接矩形，用于核对标注落点。</summary>
    private static Int32Rect RedBounds(BitmapSource image)
    {
        int stride = image.PixelWidth * 4;
        var buffer = new byte[stride * image.PixelHeight];
        image.CopyPixels(buffer, stride, 0);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < image.PixelHeight; y++)
        {
            int row = y * stride;
            for (int x = 0; x < image.PixelWidth; x++)
            {
                int i = row + x * 4;
                if (buffer[i + 2] > 190 && buffer[i + 1] < 110 && buffer[i] < 110)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        return maxX < 0 ? new Int32Rect(0, 0, 0, 0) : new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static void SaveScreen(string path)
    {
        try
        {
            using var grab = ScreenGrab.Capture();
            ImageIo.SavePng(grab.Full, path);
            Logger.Info($"快照已保存：{path}");
        }
        catch (Exception ex)
        {
            Logger.Error("保存快照失败", ex);
        }
    }
}
