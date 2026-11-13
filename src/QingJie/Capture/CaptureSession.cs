using System.IO;
using System.Windows;
using QingJie.Core;
using QingJie.Pin;

namespace QingJie.Capture;

/// <summary>一次完整的截图流程：抓屏 → 每个显示器一个覆盖层 → 用户操作 → 复制/保存/钉图。</summary>
internal sealed class CaptureSession
{
    private static CaptureSession? _active;

    private readonly Settings _config;
    private readonly ScreenGrab _grab;
    private readonly List<OverlayWindow> _windows = new();

    private CaptureSession(Settings config, ScreenGrab grab)
    {
        _config = config;
        _grab = grab;
    }

    public static bool IsCapturing => _active is not null;

    public static void Start(Settings config)
    {
        if (_active is not null)
        {
            Logger.Info("截图已在进行中，忽略重复触发");
            return;
        }

        ScreenGrab grab;
        try
        {
            grab = ScreenGrab.Capture();
        }
        catch (Exception ex)
        {
            Logger.Error("抓取屏幕失败", ex);
            Toast.Show("抓取屏幕失败，请重试");
            return;
        }

        var session = new CaptureSession(config, grab);
        _active = session;

        try
        {
            session.ShowOverlays();
        }
        catch (Exception ex)
        {
            Logger.Error("创建截图界面失败", ex);
            session.CloseAll();
        }
    }

    private void ShowOverlays()
    {
        var monitors = Native.EnumMonitors();
        if (monitors.Count == 0)
        {
            Logger.Warn("未检测到显示器");
            CloseAll();
            return;
        }

        Native.GetCursorPos(out var cursor);
        MonitorInfo cursorMonitor = monitors.FirstOrDefault(m =>
            cursor.X >= m.X && cursor.X < m.X + m.Width && cursor.Y >= m.Y && cursor.Y < m.Y + m.Height) ?? monitors[0];

        OverlayWindow? target = null;

        foreach (var monitor in monitors)
        {
            var window = new OverlayWindow();
            window.Setup(_grab, monitor);
            window.Finished += OnFinished;
            window.SelectionStarted += OnSelectionStarted;
            window.CancelRequested += Cancel;

            _windows.Add(window);
            window.Show();

            if (monitor == cursorMonitor) target = window;
        }

        target ??= _windows[0];
        target.Activate();
        target.Focus();
        Logger.Info($"截图界面已显示，共 {_windows.Count} 个屏幕");
    }

    private void OnSelectionStarted(OverlayWindow source)
    {
        foreach (var window in _windows)
        {
            if (!ReferenceEquals(window, source)) window.ClearSelection();
        }
    }

    private void OnFinished(OverlayWindow source, OverlayResult result, CaptureAction action)
    {
        CloseAll();

        switch (action)
        {
            case CaptureAction.Copy:
                CopyToClipboard(result);
                break;
            case CaptureAction.Save:
                SaveToFile(result);
                break;
            case CaptureAction.Pin:
                PinToScreen(result);
                break;
        }
    }

    private void CopyToClipboard(OverlayResult result)
    {
        bool ok = ImageIo.CopyToClipboard(result.Image);
        Logger.Info($"复制到剪贴板：{(ok ? "成功" : "失败")} {result.Image.PixelWidth}x{result.Image.PixelHeight}");
        Toast.Show(ok ? "已复制到剪贴板" : "复制失败：剪贴板被其它程序占用");
    }

    private void SaveToFile(OverlayResult result)
    {
        try
        {
            string path;
            if (_config.AskSavePath)
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "保存截图",
                    Filter = "PNG 图片|*.png",
                    DefaultExt = ".png",
                    AddExtension = true,
                    FileName = Path.GetFileName(ImageIo.BuildFileName(_config.SaveDirectory)),
                    InitialDirectory = _config.SaveDirectory,
                };

                if (dialog.ShowDialog() != true) return;
                path = dialog.FileName;
            }
            else
            {
                path = ImageIo.BuildFileName(_config.SaveDirectory);
            }

            ImageIo.SavePng(result.Image, path);
            Logger.Info($"截图已保存：{path}");
            Toast.Show($"已保存：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Logger.Error("保存截图失败", ex);
            Toast.Show("保存失败，请检查目录权限");
        }
    }

    private void PinToScreen(OverlayResult result)
    {
        try
        {
            var pin = new PinWindow(result.Image, result.Scale);
            App.CurrentInstance.RegisterPin(pin);
            pin.ShowAt(result.X, result.Y);
            Logger.Info($"贴图已创建 {result.Image.PixelWidth}x{result.Image.PixelHeight} @({result.X},{result.Y})");
        }
        catch (Exception ex)
        {
            Logger.Error("创建贴图失败", ex);
        }
    }

    public void Cancel()
    {
        Logger.Info("用户取消截图");
        CloseAll();
    }

    /// <summary>开发自检：给光标所在屏幕铺一个示例选区与标注。</summary>
    internal static void ApplyDemoState()
    {
        var window = _active?._windows.FirstOrDefault(w => w.Monitor.IsPrimary) ?? _active?._windows.FirstOrDefault();
        window?.ApplyDemoState();
    }

    /// <summary>开发自检：把当前示例选区合成为最终图片。</summary>
    internal static System.Windows.Media.Imaging.BitmapSource? ComposeActive()
    {
        var window = _active?._windows.FirstOrDefault(w => w.Monitor.IsPrimary) ?? _active?._windows.FirstOrDefault();
        return window?.ComposeResult();
    }

    private void CloseAll()
    {
        foreach (var window in _windows.ToList())
        {
            try
            {
                window.Finished -= OnFinished;
                window.SelectionStarted -= OnSelectionStarted;
                window.CancelRequested -= Cancel;
                window.Close();
            }
            catch (Exception ex)
            {
                Logger.Warn($"关闭截图窗口失败：{ex.Message}");
            }
        }

        _windows.Clear();
        _active = null;
    }
}
