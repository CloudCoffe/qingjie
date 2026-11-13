using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
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

        if (HandleCommandLine(e.Args)) return;

        _hotkeyManager = new HotkeyManager();
        _hotkeyManager.Pressed += CaptureNow;

        bool registered = _hotkeyManager.Register(Config.Hotkey);

        _tray = new AppTray(this, Config);
        ApplyStartupRegistration(Config.StartWithWindows);

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

        if (args.Contains("--capture"))
        {
            _captureOnStart = true;
        }

        return false;
    }

    // ------------------------------------------------------------ 主要操作

    public void CaptureNow()
    {
        try
        {
            CaptureSession.Start(Config);
        }
        catch (Exception ex)
        {
            Logger.Error("开始截图失败", ex);
            Toast.Show("截图失败，请查看日志");
        }
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

    private static void SaveScreen(string path)
    {        try
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
