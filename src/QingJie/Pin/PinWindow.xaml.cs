using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using QingJie.Core;

namespace QingJie.Pin;

/// <summary>
/// 贴图窗口：把截图钉在屏幕上，可拖动、滚轮缩放、Ctrl+滚轮调透明度，
/// 右键菜单支持复制 / 另存 / 置顶 / 关闭。
/// </summary>
public partial class PinWindow : Window
{
    private readonly BitmapSource _image;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private readonly double _dpiScale;
    private double _zoom = 1.0;

    public PinWindow(BitmapSource image, double dpiScale)
    {
        InitializeComponent();

        _image = image;
        _pixelWidth = Math.Max(1, image.PixelWidth);
        _pixelHeight = Math.Max(1, image.PixelHeight);
        _dpiScale = dpiScale > 0.1 && dpiScale < 5 ? dpiScale : 1.0;

        Preview.Source = image;
        Width = _pixelWidth / _dpiScale;
        Height = _pixelHeight / _dpiScale;

        Left = -20000;
        Top = -20000;
        Opacity = 0;
    }

    /// <summary>按物理像素定位，保证贴图与截图位置严格对齐。</summary>
    public void ShowAt(int physicalX, int physicalY)
    {
        Show();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, physicalX, physicalY,
                _pixelWidth, _pixelHeight, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        Opacity = 1;
    }

    private void ApplyGeometry()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        Native.GetWindowRect(hwnd, out var rect);

        int width = Math.Max(12, (int)Math.Round(_pixelWidth * _zoom));
        int height = Math.Max(12, (int)Math.Round(_pixelHeight * _zoom));

        // 缩放时保持窗口左上角不动，并限制在虚拟桌面内
        int x = Math.Clamp(rect.Left, -4000, 12000);
        int y = Math.Clamp(rect.Top, -4000, 12000);

        Native.SetWindowPos(hwnd, Topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, x, y, width, height,
            Native.SWP_NOACTIVATE);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 拖拽过程中鼠标释放可能抛异常，忽略
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double step = e.Delta > 0 ? 0.1 : -0.1;
            Opacity = Math.Clamp(Opacity + step, 0.2, 1.0);
            return;
        }

        SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
    }

    private void SetZoom(double zoom)
    {
        double target = Math.Clamp(zoom, 0.2, 4.0);
        if (Math.Abs(target - _zoom) < 0.001) return;
        _zoom = target;
        ApplyGeometry();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C)
        {
            CopyImage();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            SaveAs();
            e.Handled = true;
        }
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) => CopyImage();

    private void CopyImage()
    {
        bool ok = ImageIo.CopyToClipboard(_image);
        Toast.Show(ok ? "已复制到剪贴板" : "复制失败：剪贴板被占用");
    }

    private void MenuSave_Click(object sender, RoutedEventArgs e) => SaveAs();

    private void SaveAs()
    {
        var config = App.CurrentInstance.Config;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存贴图",
            Filter = "PNG 图片|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = Path.GetFileName(ImageIo.BuildFileName(config.SaveDirectory)),
            InitialDirectory = config.SaveDirectory,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            ImageIo.SavePng(_image, dialog.FileName);
            Toast.Show($"已保存：{Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            Logger.Error("保存贴图失败", ex);
            Toast.Show("保存失败，请检查目录权限");
        }
    }

    private void MenuTopmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = MenuTopmost.IsChecked;
        ApplyGeometry();
    }

    private void MenuResetZoom_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void MenuClose_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        App.CurrentInstance.UnregisterPin(this);
        base.OnClosed(e);
    }
}
