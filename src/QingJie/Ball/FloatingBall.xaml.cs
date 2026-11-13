using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QingJie.Core;

namespace QingJie.Ball;

/// <summary>
/// 桌面悬浮球：单击进入截图、按住拖动换位置、右键出菜单。
/// 用物理像素定位（SetWindowPos），保证跨 DPI 显示器时大小与位置都准确。
/// </summary>
public partial class FloatingBall : Window
{
    /// <summary>
    /// 移动超过这个距离（物理像素）才算拖动，否则算点击。
    /// 取 8 是为了容下按下时的手抖，200% 缩放下相当于 4 个逻辑像素。
    /// </summary>
    private const int DragThreshold = 8;

    /// <summary>松手时离工作区边缘多近就吸附过去。</summary>
    private const int SnapDistance = 20;

    private IntPtr _hwnd;
    private bool _pressed;
    private bool _moved;
    private Native.POINT _pressCursor;
    private Native.RECT _pressRect;

    public FloatingBall()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            if (Root.ToolTip is ToolTip tip)
            {
                tip.PlacementTarget = this;
                tip.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            }
        };

        MouseEnter += (_, _) => Animate(1.07, 0.5);
        MouseLeave += (_, _) =>
        {
            Animate(1.0, 0.0);
            // 离开后重新允许提示，避免球刚恢复就在光标下面弹框
            ToolTipService.SetIsEnabled(Root, true);
        };

        Closed += (_, _) => _hwnd = IntPtr.Zero;
    }

    /// <summary>单击（按下与抬起之间几乎没移动）。</summary>
    public event Action? Clicked;

    /// <summary>拖动结束且位置发生了变化。</summary>
    public event Action? Moved;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        // 不进入 Alt+Tab 列表
        long style = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
            new IntPtr(style | Native.WS_EX_TOOLWINDOW));
    }

    // ------------------------------------------------------------ 对外接口

    /// <summary>当前窗口的物理像素矩形。</summary>
    internal Native.RECT Bounds()
        => _hwnd != IntPtr.Zero && Native.GetWindowRect(_hwnd, out var rect) ? rect : default;

    /// <summary>显示到指定物理坐标；传入 null 使用默认位置（主屏右侧中部）。</summary>
    public void ShowAt(int? physicalX, int? physicalY)
    {
        if (!IsVisible) Show();

        // 恢复显示时光标可能正压在球上，先屏蔽提示，等鼠标离开再启用
        ToolTipService.SetIsEnabled(Root, false);

        if (physicalX is int x && physicalY is int y)
        {
            MoveTo(x, y);
            return;
        }

        var (dx, dy) = DefaultPosition();
        MoveTo(dx, dy);
    }

    /// <summary>隐藏悬浮球（先收起可能存在的提示，避免它留在截图里）。</summary>
    internal void HideBall()
    {
        if (Root.ToolTip is ToolTip { IsOpen: true } tip) tip.IsOpen = false;
        Hide();
    }

    /// <summary>默认位置：主屏工作区右侧、垂直方向约三分之一处。</summary>
    public (int X, int Y) DefaultPosition()
    {
        var monitors = Native.EnumMonitors();
        var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (monitor is null) return (60, 160);

        var rect = Bounds();
        int width = rect.Width > 0 ? rect.Width : 74;
        int margin = (int)Math.Round(26 * monitor.Scale);

        return (monitor.WorkX + monitor.WorkWidth - width - margin,
                monitor.WorkY + (int)Math.Round(monitor.WorkHeight * 0.3));
    }

    /// <summary>移动到物理坐标，并夹在当前显示器的工作区内。</summary>
    public void MoveTo(int x, int y, bool clampToWorkArea = true)
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        Native.GetWindowRect(_hwnd, out var rect);
        if (clampToWorkArea) (x, y) = Clamp(x, y, rect.Width, rect.Height);

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    /// <summary>右键菜单是否处于打开状态（自检用）。</summary>
    internal bool MenuIsOpen => BallMenu.IsOpen;

    internal void CloseMenu() => BallMenu.IsOpen = false;

    /// <summary>最近一次按下/松手的坐标轨迹，便于排查“点击被判成拖动”这类问题。</summary>
    internal string LastGesture { get; private set; } = "-";

    // ------------------------------------------------------------ 鼠标交互

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _pressed = true;
        _moved = false;
        Native.GetCursorPos(out _pressCursor);
        Native.GetWindowRect(_hwnd, out _pressRect);
        LastGesture = $"down=({_pressCursor.X},{_pressCursor.Y}) rect=({_pressRect.Left},{_pressRect.Top})";

        Root.CaptureMouse();
        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;

        Native.GetCursorPos(out var cursor);
        int dx = cursor.X - _pressCursor.X;
        int dy = cursor.Y - _pressCursor.Y;

        if (!_moved && Math.Abs(dx) + Math.Abs(dy) > DragThreshold)
        {
            _moved = true;
            Cursor = Cursors.SizeAll;
        }

        if (_moved) MoveTo(_pressRect.Left + dx, _pressRect.Top + dy);
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;

        _pressed = false;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        Cursor = Cursors.Hand;

        Native.GetCursorPos(out var released);
        LastGesture += $" up=({released.X},{released.Y}) moved={_moved} " +
                       $"d=({released.X - _pressCursor.X},{released.Y - _pressCursor.Y})";

        if (_moved)
        {

            SnapToEdge();
            Moved?.Invoke();
            return;
        }

        Pulse();
        Clicked?.Invoke();
    }

    /// <summary>松手时贴近屏幕边缘就吸附过去，和常见的悬浮球手感一致。</summary>
    private void SnapToEdge()
    {
        var rect = Bounds();
        if (rect.Width <= 0) return;

        var monitors = Native.EnumMonitors();
        int cx = rect.Left + rect.Width / 2;
        int cy = rect.Top + rect.Height / 2;

        var monitor = monitors.FirstOrDefault(m =>
                          cx >= m.X && cx < m.X + m.Width && cy >= m.Y && cy < m.Y + m.Height)
                      ?? monitors.FirstOrDefault(m => m.IsPrimary);
        if (monitor is null) return;

        int inset = 2;
        int x = rect.Left;
        int y = rect.Top;

        if (Math.Abs(x - monitor.WorkX) <= SnapDistance)
            x = monitor.WorkX + inset;
        else if (Math.Abs(monitor.WorkX + monitor.WorkWidth - (x + rect.Width)) <= SnapDistance)
            x = monitor.WorkX + monitor.WorkWidth - rect.Width - inset;

        if (Math.Abs(y - monitor.WorkY) <= SnapDistance)
            y = monitor.WorkY + inset;
        else if (Math.Abs(monitor.WorkY + monitor.WorkHeight - (y + rect.Height)) <= SnapDistance)
            y = monitor.WorkY + monitor.WorkHeight - rect.Height - inset;

        if (x != rect.Left || y != rect.Top) MoveTo(x, y);
    }

    private static (int X, int Y) Clamp(int x, int y, int width, int height)
    {
        var monitors = Native.EnumMonitors();
        if (monitors.Count == 0) return (x, y);

        int cx = x + width / 2;
        int cy = y + height / 2;

        var monitor = monitors.FirstOrDefault(m =>
                          cx >= m.X && cx < m.X + m.Width && cy >= m.Y && cy < m.Y + m.Height)
                      ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        int maxX = monitor.WorkX + monitor.WorkWidth - width;
        int maxY = monitor.WorkY + monitor.WorkHeight - height;

        x = maxX >= monitor.WorkX ? Math.Clamp(x, monitor.WorkX, maxX) : monitor.WorkX;
        y = maxY >= monitor.WorkY ? Math.Clamp(y, monitor.WorkY, maxY) : monitor.WorkY;
        return (x, y);
    }

    // ------------------------------------------------------------ 动画

    private void Animate(double scale, double haloOpacity)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(130);

        BallScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = ease });
        BallScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = ease });
        Halo.BeginAnimation(OpacityProperty,
            new DoubleAnimation(haloOpacity, duration) { EasingFunction = ease });
    }

    /// <summary>点击反馈：轻压一下再弹回。</summary>
    private void Pulse()
    {
        var frames = new DoubleAnimationUsingKeyFrames();
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(0.84,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));

        BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, frames);
        BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, frames);
    }

    // ------------------------------------------------------------ 菜单

    private void BallMenu_Opened(object sender, RoutedEventArgs e)
    {
        var config = App.CurrentInstance.Config;
        MenuCapture.InputGestureText = config.Hotkey.ToString();
        Logger.Info("悬浮球菜单已打开");
    }

    private void MenuCapture_Click(object sender, RoutedEventArgs e) => Clicked?.Invoke();

    private void MenuHide_Click(object sender, RoutedEventArgs e)
        => App.CurrentInstance.HideBall(persist: true);

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
        => App.CurrentInstance.OpenSettings();

    private void MenuExit_Click(object sender, RoutedEventArgs e)
        => App.CurrentInstance.ExitApp();
}
