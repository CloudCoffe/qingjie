using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using QingJie.Core;
using QingJie.Ocr;

namespace QingJie.Capture;

public enum CaptureAction
{
    Copy,
    Save,
    Pin,
}

/// <summary>一次截图的结果。</summary>
public sealed record OverlayResult(BitmapSource Image, int X, int Y, double Scale);

/// <summary>覆盖在某个显示器上的截图界面：划区、标注、复制/保存/钉图。</summary>
public sealed partial class OverlayWindow : Window
{
    private const double HandleSize = 7;
    private const double HandleHitSlop = 8;

    private readonly List<Annotation> _annotations = new();
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly Dictionary<(int, int, int, int), MosaicTile> _mosaicCache = new();

    private ScreenGrab _grab = null!;
    private MonitorInfo _monitor = null!;
    private HwndSource? _hwndSource;

    private double _scale = 1.0;
    private Rect _selection = Rect.Empty;
    private Annotation? _draft;
    private AnnotationTool _tool = AnnotationTool.None;
    private DragMode _mode = DragMode.None;
    private Point _dragOrigin;
    private Rect _selectionOrigin;
    private int _activeHandle = -1;
    private Point _textAnchor;
    private Point _textAnchorLocal;
    private Color _color = Color.FromRgb(0xFF, 0x3B, 0x30);
    private double _thickness = 4;
    private bool _closing;

    public OverlayWindow()
    {
        InitializeComponent();
        CreateHandles();
        ColorDot.Fill = new SolidColorBrush(_color);
    }

    internal event Action<OverlayWindow, OverlayResult, CaptureAction>? Finished;

    internal event Action<OverlayWindow>? SelectionStarted;

    internal event Action? CancelRequested;

    internal MonitorInfo Monitor => _monitor;

    internal bool HasSelection => !_selection.IsEmpty && _selection.Width >= 1 && _selection.Height >= 1;

    /// <summary>开发自检：已收到的鼠标移动事件数，用于判断事件是否真的路由到了画布。</summary>
    internal int MoveCount { get; private set; }

    /// <summary>开发自检：已收到的按键事件数。</summary>
    internal int KeyCount { get; private set; }

    private string _lastKey = "-";

    /// <summary>开发自检：把当前拖拽状态打平成字符串。</summary>
    internal string DebugState()
    {
        var phys = SelectionPhysical;
        var hwnd = new WindowInteropHelper(this).Handle;
        string win = "n/a";
        if (hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var r))
        {
            win = $"{r.Left},{r.Top} {r.Width}×{r.Height}";
        }

        return $"mode={_mode} move={MoveCount} captured={Root.IsMouseCaptured} anno={_annotations.Count} tool={_tool} key={KeyCount}:{_lastKey} " +
               $"diu={_selection.X:0.#},{_selection.Y:0.#} {_selection.Width:0.#}×{_selection.Height:0.#} " +
               $"phys={phys.X},{phys.Y} {phys.Width}×{phys.Height} scale={_scale:0.###} " +
               $"win={win} monitor={_monitor.X},{_monitor.Y} {_monitor.Width}×{_monitor.Height}";
    }

    private enum DragMode
    {
        None,
        Creating,
        Moving,
        Resizing,
        Drawing,
    }

    internal void Setup(ScreenGrab grab, MonitorInfo monitor)
    {
        _grab = grab;
        _monitor = monitor;

        BackImage.Source = grab.CropPhysical(monitor.X, monitor.Y, monitor.Width, monitor.Height);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 不进入 Alt+Tab / 任务栏
        long exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(exStyle | Native.WS_EX_TOOLWINDOW));

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        PlaceOnMonitor();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        UpdateScale();
        UpdateSelectionVisuals();
        UpdateMagnifierToCursorIfNeeded();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_DPICHANGED)
        {
            // WPF 默认会按比例重设窗口大小，这里再对齐回显示器物理边界
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PlaceOnMonitor();
                UpdateSelectionVisuals();
            }), DispatcherPriority.Loaded);
        }

        return IntPtr.Zero;
    }

    /// <summary>把窗口精确铺到目标显示器的物理矩形上。</summary>
    internal void PlaceOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        UpdateScale();
    }

    /// <summary>计算 DIU → 物理像素的比例（窗口铺满显示器时，物理宽度 / 逻辑宽度）。</summary>
    private void UpdateScale()
    {
        double fallback = _monitor.Scale > 0 ? _monitor.Scale : 1.0;
        int physWidth = _monitor.Width;

        if (ActualWidth > 1 && physWidth > 0)
        {
            double byLayout = physWidth / ActualWidth;
            _scale = byLayout > 0.2 && byLayout < 5 ? byLayout : fallback;
        }
        else
        {
            _scale = fallback;
        }

        Logger.Info($"屏幕 {_monitor.Device} 物理 {physWidth}x{_monitor.Height} 逻辑 {ActualWidth:0.#}x{ActualHeight:0.#} 缩放 {_scale:0.###}");
    }

    private void CreateHandles()
    {
        for (int i = 0; i < _handles.Length; i++)
        {
            var rect = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2E, 0x7C, 0xF6)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };

            _handles[i] = rect;
            HandleLayer.Children.Add(rect);
        }
    }

    // ---------------------------------------------------------------- 鼠标

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        if (Toolbar.Visibility == Visibility.Visible && Toolbar.IsMouseOver) return;
        if (OcrPanel.Visibility == Visibility.Visible && OcrPanel.IsMouseOver) return;

        HideTextEditor(commit: true);

        var pos = e.GetPosition(Root);

        // 双击 = 复制
        if (e.ClickCount == 2 && HasSelection && _selection.Contains(pos) && _tool == AnnotationTool.None)
        {
            Submit(CaptureAction.Copy);
            return;
        }

        if (HasSelection)
        {
            int handle = HitTestHandle(pos);
            if (handle >= 0)
            {
                _mode = DragMode.Resizing;
                _activeHandle = handle;
                _dragOrigin = pos;
                _selectionOrigin = _selection;
                Root.CaptureMouse();
                return;
            }

            if (_selection.Contains(pos))
            {
                if (_tool == AnnotationTool.None)
                {
                    _mode = DragMode.Moving;
                    _dragOrigin = pos;
                    _selectionOrigin = _selection;
                    Root.CaptureMouse();
                    return;
                }

                BeginAnnotation(pos);
                return;
            }
        }

        // 在选区外按下 = 重新划区
        _annotations.Clear();
        _mosaicCache.Clear();
        _draft = null;
        HideOcrPanel();
        _mode = DragMode.Creating;
        _dragOrigin = pos;
        _selection = new Rect(pos, new Size(0, 0));
        SelectionStarted?.Invoke(this);
        UpdateSelectionVisuals();
        Root.CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (_closing) return;

        MoveCount++;
        var pos = e.GetPosition(Root);

        switch (_mode)
        {
            case DragMode.Creating:
                _selection = NormalizeRect(_dragOrigin, pos);
                UpdateSelectionVisuals();
                UpdateMagnifier(pos);
                return;

            case DragMode.Moving:
            {
                var delta = pos - _dragOrigin;
                double left = Math.Clamp(_selectionOrigin.X + delta.X, 0, Math.Max(0, ActualWidth - _selectionOrigin.Width));
                double top = Math.Clamp(_selectionOrigin.Y + delta.Y, 0, Math.Max(0, ActualHeight - _selectionOrigin.Height));
                _selection = new Rect(left, top, _selectionOrigin.Width, _selectionOrigin.Height);
                UpdateSelectionVisuals();
                return;
            }

            case DragMode.Resizing:
            {
                var delta = pos - _dragOrigin;
                _selection = ApplyResize(_selectionOrigin, _activeHandle, delta);
                UpdateSelectionVisuals();
                return;
            }

            case DragMode.Drawing:
            {
                if (_draft is null) return;
                var local = ClampToSelection(pos);
                if (_draft.Tool == AnnotationTool.Pen)
                {
                    var last = _draft.Points[^1];
                    if ((local - last).Length >= 1.5)
                    {
                        _draft.Points.Add(local);
                        RefreshAnnotations();
                    }
                }
                else
                {
                    _draft.Points[1] = local;
                    RefreshAnnotations();
                }

                return;
            }
        }

        if (HasSelection)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            UpdateCursor(pos);
        }
        else
        {
            UpdateMagnifier(pos);
            Cursor = Cursors.Cross;
        }
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;

        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();

        switch (_mode)
        {
            case DragMode.Creating:
                if (_selection.Width < 4 || _selection.Height < 4)
                {
                    _selection = Rect.Empty;
                }

                break;

            case DragMode.Drawing:
                if (_draft is not null)
                {
                    bool valid = _draft.Tool switch
                    {
                        AnnotationTool.Pen => _draft.Points.Count > 1,
                        _ => _draft.Bounds.Width > 2 || _draft.Bounds.Height > 2,
                    };

                    if (valid) _annotations.Add(_draft);
                    _draft = null;
                    RefreshAnnotations();
                }

                break;
        }

        _mode = DragMode.None;
        _activeHandle = -1;
        UpdateSelectionVisuals();
    }

    private void Root_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        if (Toolbar.Visibility == Visibility.Visible && Toolbar.IsMouseOver) return;

        // 有工具时右键先退出工具，否则退出截图
        if (_tool != AnnotationTool.None)
        {
            SelectTool(AnnotationTool.None);
            return;
        }

        if (HasSelection)
        {
            _annotations.Clear();
            _mosaicCache.Clear();
            _selection = Rect.Empty;
            UpdateSelectionVisuals();
            return;
        }

        CancelRequested?.Invoke();
    }

    private void UpdateCursor(Point pos)
    {
        if (!HasSelection)
        {
            Cursor = Cursors.Cross;
            return;
        }

        if (_tool != AnnotationTool.None)
        {
            Cursor = _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
            return;
        }

        int handle = HitTestHandle(pos);
        Cursor = handle switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            3 or 7 => Cursors.SizeWE,
            _ => _selection.Contains(pos) ? Cursors.SizeAll : Cursors.Cross,
        };
    }

    private int HitTestHandle(Point pos)
    {
        var points = GetHandlePoints();
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (Math.Abs(pos.X - p.X) <= HandleHitSlop && Math.Abs(pos.Y - p.Y) <= HandleHitSlop)
            {
                return i;
            }
        }

        return -1;
    }

    private Point[] GetHandlePoints()
    {
        if (_selection.IsEmpty) return Array.Empty<Point>();
        var s = _selection;
        return new[]
        {
            new Point(s.Left, s.Top),
            new Point(s.Left + s.Width / 2, s.Top),
            new Point(s.Right, s.Top),
            new Point(s.Right, s.Top + s.Height / 2),
            new Point(s.Right, s.Bottom),
            new Point(s.Left + s.Width / 2, s.Bottom),
            new Point(s.Left, s.Bottom),
            new Point(s.Left, s.Top + s.Height / 2),
        };
    }

    /// <summary>把窗口坐标夹进选区，结果仍是窗口坐标。</summary>
    private Point ClampToWindow(Point p) => new(
        Math.Clamp(p.X, _selection.X, _selection.Right),
        Math.Clamp(p.Y, _selection.Y, _selection.Bottom));

    /// <summary>
    /// 把窗口坐标换算成「相对选区左上角」的标注坐标（标注统一使用这套坐标），
    /// 换算后再夹进选区内，避免笔画超出边界。
    /// </summary>
    private Point ClampToSelection(Point p)
    {
        if (_selection.IsEmpty) return p;
        var w = ClampToWindow(p);
        return new Point(w.X - _selection.X, w.Y - _selection.Y);
    }

    private static Rect NormalizeRect(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private Rect ApplyResize(Rect origin, int handle, Vector delta)
    {
        double left = origin.Left, top = origin.Top, right = origin.Right, bottom = origin.Bottom;

        switch (handle)
        {
            case 0:
                left += delta.X;
                top += delta.Y;
                break;
            case 1:
                top += delta.Y;
                break;
            case 2:
                right += delta.X;
                top += delta.Y;
                break;
            case 3:
                right += delta.X;
                break;
            case 4:
                right += delta.X;
                bottom += delta.Y;
                break;
            case 5:
                bottom += delta.Y;
                break;
            case 6:
                left += delta.X;
                bottom += delta.Y;
                break;
            case 7:
                left += delta.X;
                break;
        }

        double maxW = ActualWidth, maxH = ActualHeight;
        left = Math.Clamp(left, 0, maxW);
        right = Math.Clamp(right, 0, maxW);
        top = Math.Clamp(top, 0, maxH);
        bottom = Math.Clamp(bottom, 0, maxH);

        return new Rect(Math.Min(left, right), Math.Min(top, bottom),
            Math.Abs(right - left), Math.Abs(bottom - top));
    }

    // ---------------------------------------------------------------- 标注

    private void BeginAnnotation(Point pos)
    {
        if (_tool == AnnotationTool.Text)
        {
            ShowTextEditor(pos);
            return;
        }

        _draft = new Annotation
        {
            Tool = _tool,
            Color = _color,
            Thickness = _thickness,
            FontSize = 12 + _thickness * 2.5,
        };
        _draft.Points.Add(ClampToSelection(pos));
        _draft.Points.Add(ClampToSelection(pos));
        _mode = DragMode.Drawing;
        Root.CaptureMouse();
        RefreshAnnotations();
    }

    private void ShowTextEditor(Point pos)
    {
        _textAnchor = ClampToWindow(pos);
        _textAnchorLocal = ClampToSelection(pos);
        TextEditor.Text = string.Empty;
        TextEditor.Foreground = new SolidColorBrush(_color);
        TextEditor.FontSize = 12 + _thickness * 2.5;
        TextEditor.Visibility = Visibility.Visible;
        Canvas.SetLeft(TextEditor, _textAnchor.X);
        Canvas.SetTop(TextEditor, _textAnchor.Y);
        TextEditor.Width = Math.Max(120, _selection.Right - _textAnchor.X);
        TextEditor.Height = double.NaN;
        TextEditor.Focus();
        Keyboard.Focus(TextEditor);
    }

    private void TextEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideTextEditor(commit: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            HideTextEditor(commit: true);
            e.Handled = true;
        }
    }

    private void TextEditor_LostFocus(object sender, RoutedEventArgs e) => HideTextEditor(commit: true);

    /// <summary>开发自检：往文字输入框里填入内容（真实键盘输入会被输入法接管，无法脚本化）。</summary>
    internal void SetTextForTest(string text)
    {
        if (TextEditor.Visibility == Visibility.Visible) TextEditor.Text = text;
    }

    private void HideTextEditor(bool commit)
    {
        if (TextEditor.Visibility != Visibility.Visible) return;

        var text = TextEditor.Text;
        TextEditor.Visibility = Visibility.Collapsed;
        TextEditor.Text = string.Empty;

        if (commit && !string.IsNullOrWhiteSpace(text))
        {
            var anno = new Annotation
            {
                Tool = AnnotationTool.Text,
                Color = _color,
                Thickness = _thickness,
                FontSize = TextEditor.FontSize,
                Text = text,
            };
            anno.Points.Add(_textAnchorLocal);
            _annotations.Add(anno);
            RefreshAnnotations();
        }

        Focus();
    }

    private void RefreshAnnotations()
    {
        AnnoLayer.SetContent(_draft is null ? _annotations : _annotations.Append(_draft).ToList(), ProvideMosaic);
        UndoButton.Opacity = _annotations.Count > 0 ? 1.0 : 0.35;
    }

    /// <summary>为马赛克提供按块取样的色块（从原始截图裁剪，避免把已画的标注一起打码）。</summary>
    private MosaicTile? ProvideMosaic(Rect rect)
    {
        if (rect.Width < 2 || rect.Height < 2) return null;

        var sel = SelectionPhysical;
        int px = (int)Math.Round(sel.X + rect.X * _scale);
        int py = (int)Math.Round(sel.Y + rect.Y * _scale);
        int pw = Math.Max(1, (int)Math.Round(rect.Width * _scale));
        int ph = Math.Max(1, (int)Math.Round(rect.Height * _scale));

        var key = (px, py, pw, ph);
        if (_mosaicCache.TryGetValue(key, out var cached)) return cached;

        var crop = _grab.CropPhysical(px, py, pw, ph);
        int cols = Math.Clamp((int)Math.Round(rect.Width / AnnotationPainter.MosaicBlock), 1, 400);
        int rows = Math.Clamp((int)Math.Round(rect.Height / AnnotationPainter.MosaicBlock), 1, 400);

        var tile = AnnotationPainter.BuildMosaicTile(crop, cols, rows);

        if (_mosaicCache.Count > 96) _mosaicCache.Clear();
        _mosaicCache[key] = tile;
        return tile;
    }

    // ---------------------------------------------------------------- 选区绘制

    /// <summary>当前选区对应的物理像素矩形（屏幕坐标）。</summary>
    internal Int32Rect SelectionPhysicalRect => SelectionPhysical;

    /// <summary>DIU → 物理像素的缩放比。</summary>
    internal double Scale => _scale;

    private Int32Rect SelectionPhysical
    {
        get
        {
            if (_selection.IsEmpty) return new Int32Rect(0, 0, 0, 0);

            int x = (int)Math.Round(_monitor.X + _selection.X * _scale);
            int y = (int)Math.Round(_monitor.Y + _selection.Y * _scale);
            int w = Math.Max(1, (int)Math.Round(_selection.Width * _scale));
            int h = Math.Max(1, (int)Math.Round(_selection.Height * _scale));

            int maxX = _monitor.X + _monitor.Width;
            int maxY = _monitor.Y + _monitor.Height;
            x = Math.Clamp(x, _monitor.X, maxX - 1);
            y = Math.Clamp(y, _monitor.Y, maxY - 1);
            w = Math.Min(w, maxX - x);
            h = Math.Min(h, maxY - y);

            return new Int32Rect(x, y, Math.Max(1, w), Math.Max(1, h));
        }
    }

    private void UpdateSelectionVisuals()
    {
        double w = Math.Max(1, ActualWidth);
        double h = Math.Max(1, ActualHeight);

        if (!HasSelection)
        {
            MaskFull.Visibility = Visibility.Visible;
            MaskFull.Width = w;
            MaskFull.Height = h;
            MaskTop.Visibility = MaskLeft.Visibility = MaskRight.Visibility = MaskBottom.Visibility = Visibility.Collapsed;

            SelectionBorder.Visibility = Visibility.Collapsed;
            HandleLayer.Visibility = Visibility.Collapsed;
            AnnoLayer.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = Visibility.Collapsed;
            HintPanel.Visibility = Visibility.Visible;
            Canvas.SetLeft(HintPanel, Math.Max(8, (w - 420) / 2));
            Canvas.SetTop(HintPanel, 28);
            HideOcrPanel();

            return;
        }

        var s = _selection;
        MaskFull.Visibility = Visibility.Collapsed;
        HintPanel.Visibility = Visibility.Collapsed;
        Magnifier.Visibility = Visibility.Collapsed;

        PositionMask(MaskTop, 0, 0, w, s.Top);
        PositionMask(MaskBottom, 0, s.Bottom, w, h - s.Bottom);
        PositionMask(MaskLeft, 0, s.Top, s.Left, s.Height);
        PositionMask(MaskRight, s.Right, s.Top, w - s.Right, s.Height);

        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Width = s.Width;
        SelectionBorder.Height = s.Height;
        Canvas.SetLeft(SelectionBorder, s.X);
        Canvas.SetTop(SelectionBorder, s.Y);

        AnnoLayer.Visibility = Visibility.Visible;
        AnnoLayer.Width = s.Width;
        AnnoLayer.Height = s.Height;
        Canvas.SetLeft(AnnoLayer, s.X);
        Canvas.SetTop(AnnoLayer, s.Y);
        RefreshAnnotations();

        HandleLayer.Visibility = Visibility.Visible;
        var points = GetHandlePoints();
        for (int i = 0; i < _handles.Length; i++)
        {
            var p = points[i];
            var rect = _handles[i];
            rect.Visibility = Visibility.Visible;
            Canvas.SetLeft(rect, p.X - HandleSize / 2);
            Canvas.SetTop(rect, p.Y - HandleSize / 2);
        }

        var phys = SelectionPhysical;
        SizeText.Text = $"{phys.Width} × {phys.Height}";
        SizeLabel.Visibility = Visibility.Visible;
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double labelW = SizeLabel.DesiredSize.Width;
        Canvas.SetLeft(SizeLabel, Math.Clamp(s.X, 0, Math.Max(0, w - labelW)));
        Canvas.SetTop(SizeLabel, s.Top > 26 ? s.Top - 26 : s.Bottom + 6);

        UpdateToolbarPosition(s);

        if (OcrPanel.Visibility == Visibility.Visible) PositionOcrPanel();
    }

    private void UpdateToolbarPosition(Rect s)
    {
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Toolbar.DesiredSize;
        double w = Math.Max(1, ActualWidth);
        double h = Math.Max(1, ActualHeight);

        double top = s.Bottom + 12;
        if (top + size.Height > h - 6)
        {
            top = s.Top - size.Height - 12;
        }

        if (top < 6) top = Math.Min(s.Bottom + 12, h - size.Height - 6);

        double left = Math.Clamp(s.X, 6, Math.Max(6, w - size.Width - 6));
        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, Math.Max(6, top));
    }

    private static void PositionMask(Rectangle rect, double left, double top, double width, double height)
    {
        rect.Visibility = Visibility.Visible;
        rect.Width = Math.Max(0, width);
        rect.Height = Math.Max(0, height);
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
    }

    // ---------------------------------------------------------------- 放大镜

    private void UpdateMagnifier(Point localPos)
    {
        if (!App.CurrentInstance.Config.ShowMagnifier)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        Native.GetCursorPos(out var cursor);
        int physX = cursor.X;
        int physY = cursor.Y;

        // 8 倍屏幕放大：每 1 个物理像素在屏幕上占 8 个物理像素
        const double zoom = 8.0;
        int srcW = Math.Max(4, (int)Math.Round(132 / zoom * _scale));
        int srcH = Math.Max(3, (int)Math.Round(92 / zoom * _scale));

        int srcX = Math.Clamp(physX - srcW / 2, _monitor.X, Math.Max(_monitor.X, _monitor.X + _monitor.Width - srcW));
        int srcY = Math.Clamp(physY - srcH / 2, _monitor.Y, Math.Max(_monitor.Y, _monitor.Y + _monitor.Height - srcH));

        LoupeImage.Source = _grab.CropPhysical(srcX, srcY, srcW, srcH);

        var color = _grab.GetPixel(physX, physY);
        LoupeInfo.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ColorChip.Background = new SolidColorBrush(color);
        LoupeSize.Text = $"X:{physX}  Y:{physY}";

        Magnifier.Visibility = Visibility.Visible;
        Magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Magnifier.DesiredSize;

        double left = localPos.X + 20;
        double top = localPos.Y + 20;
        if (left + size.Width > Math.Max(1, ActualWidth) - 6) left = localPos.X - size.Width - 20;
        if (top + size.Height > Math.Max(1, ActualHeight) - 6) top = localPos.Y - size.Height - 20;

        Canvas.SetLeft(Magnifier, Math.Max(4, left));
        Canvas.SetTop(Magnifier, Math.Max(4, top));
    }

    private void UpdateMagnifierToCursorIfNeeded()
    {
        Native.GetCursorPos(out var cursor);
        var local = new Point((cursor.X - _monitor.X) / Math.Max(_scale, 0.1),
            (cursor.Y - _monitor.Y) / Math.Max(_scale, 0.1));
        if (local.X >= 0 && local.Y >= 0 && local.X <= ActualWidth && local.Y <= ActualHeight)
        {
            UpdateMagnifier(local);
        }
    }

    // ---------------------------------------------------------------- 工具栏

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } button)
        {
            var tool = Enum.TryParse<AnnotationTool>(tag, out var parsed) ? parsed : AnnotationTool.None;
            bool activate = button.IsChecked == true;
            SelectTool(activate ? tool : AnnotationTool.None);
        }
    }

    private void SelectTool(AnnotationTool tool)
    {
        _tool = tool;
        ToolRectangle.IsChecked = tool == AnnotationTool.Rectangle;
        ToolEllipse.IsChecked = tool == AnnotationTool.Ellipse;
        ToolArrow.IsChecked = tool == AnnotationTool.Arrow;
        ToolPen.IsChecked = tool == AnnotationTool.Pen;
        ToolText.IsChecked = tool == AnnotationTool.Text;
        ToolMosaic.IsChecked = tool == AnnotationTool.Mosaic;

        Cursor = tool switch
        {
            AnnotationTool.None => Cursors.Cross,
            AnnotationTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };

        if (tool != AnnotationTool.Text) HideTextEditor(commit: true);
    }

    private void StyleButton_Click(object sender, RoutedEventArgs e) => StylePopup.IsOpen = true;

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } && TryParseColor(hex, out var color))
        {
            _color = color;
            ColorDot.Fill = new SolidColorBrush(color);
            StylePopup.IsOpen = false;
        }
    }

    private void ThicknessButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string raw } && double.TryParse(raw, out double thickness))
        {
            _thickness = thickness;
            StylePopup.IsOpen = false;
        }
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            color = Colors.Red;
            return false;
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_annotations.Count == 0) return;
        _annotations.RemoveAt(_annotations.Count - 1);
        RefreshAnnotations();
    }

    // ---------------------------------------------------------------- 取字（OCR）

    private bool _ocrBusy;

    private void OcrButton_Click(object sender, RoutedEventArgs e) => StartOcr();

    /// <summary>对当前选区识别文字：用原图（不含标注）→ 系统 OCR → 面板展示并复制到剪贴板。</summary>
    private async void StartOcr()
    {
        if (_closing || _ocrBusy || !HasSelection) return;

        HideTextEditor(commit: true);

        var phys = SelectionPhysical;
        if (phys.Width < 40 || phys.Height < 40)
        {
            OcrStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x6A));
            ShowOcrPanel("区域太小，请框选得更大一些", string.Empty, busy: false);
            return;
        }

        _ocrBusy = true;
        var crop = _grab.CropPhysical(phys.X, phys.Y, phys.Width, phys.Height);

        ShowOcrPanel("识别中…", string.Empty, busy: true);

        try
        {
            var outcome = await OcrService.RecognizeAsync(crop);
            if (_closing || !IsLoaded) return;

            if (outcome.Error is not null)
            {
                OcrStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
                ShowOcrPanel(outcome.Error, string.Empty, busy: false);
                return;
            }

            if (string.IsNullOrWhiteSpace(outcome.Text))
            {
                OcrStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x6A));
                ShowOcrPanel("没有识别到文字", string.Empty, busy: false);
                return;
            }

            OcrStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xD6, 0xA0));
            ShowOcrPanel(string.Empty, outcome.Text.Trim(), busy: false);
            CopyOcrText(OcrText.Text, announce: true);
        }
        finally
        {
            _ocrBusy = false;
        }
    }

    private void ShowOcrPanel(string status, string text, bool busy)
    {
        OcrStatus.Text = status;
        OcrText.Text = text;
        OcrCopyButton.IsEnabled = !busy && text.Length > 0;
        OcrPanel.Visibility = Visibility.Visible;
        OcrPanel.UpdateLayout();
        PositionOcrPanel();
    }

    private void PositionOcrPanel()
    {
        double w = Math.Max(1, ActualWidth);
        double h = Math.Max(1, ActualHeight);

        OcrPanel.Measure(new Size(OcrPanel.Width, double.PositiveInfinity));
        double panelW = OcrPanel.Width;
        double panelH = Math.Max(90, OcrPanel.DesiredSize.Height);

        // 工具栏也贴在选区下方，面板要排在它下面，两者不能叠在一起
        double anchorBottom = _selection.Bottom;
        if (Toolbar.Visibility == Visibility.Visible)
        {
            Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            anchorBottom = Math.Max(anchorBottom, Canvas.GetTop(Toolbar) + Toolbar.DesiredSize.Height);
        }

        double left = Math.Clamp(_selection.X, 8, Math.Max(8, w - panelW - 8));
        double top = anchorBottom + 10;
        if (top + panelH > h - 8) top = _selection.Top - panelH - 12;
        if (top < 8) top = Math.Max(8, h - panelH - 8);

        Canvas.SetLeft(OcrPanel, left);
        Canvas.SetTop(OcrPanel, top);
    }

    private void HideOcrPanel()
    {
        if (OcrPanel.Visibility != Visibility.Visible) return;
        OcrPanel.Visibility = Visibility.Collapsed;
        OcrText.Text = string.Empty;
        OcrStatus.Text = string.Empty;
    }

    private void OcrCopyButton_Click(object sender, RoutedEventArgs e) => CopyOcrText(OcrText.Text, announce: true);

    private void OcrCloseButton_Click(object sender, RoutedEventArgs e) => HideOcrPanel();

    private void CopyOcrText(string text, bool announce)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
            if (announce) OcrStatus.Text = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            Logger.Warn($"复制文字失败：{ex.Message}");
            if (announce) OcrStatus.Text = "复制失败，可手动选中复制";
        }
    }

    /// <summary>开发自检：触发一次取字，并回报面板状态。</summary>
    internal void RunOcrForTest() => StartOcr();

    internal bool IsOcrBusy => _ocrBusy;

    internal bool OcrPanelVisible => OcrPanel.Visibility == Visibility.Visible;

    internal string OcrPanelText => OcrText.Text;

    internal string OcrPanelStatus => OcrStatus.Text;

    private void SaveButton_Click(object sender, RoutedEventArgs e) => Submit(CaptureAction.Save);
    private void CopyButton_Click(object sender, RoutedEventArgs e) => Submit(CaptureAction.Copy);

    private void PinButton_Click(object sender, RoutedEventArgs e) => Submit(CaptureAction.Pin);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    // ---------------------------------------------------------------- 键盘

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        KeyCount++;
        _lastKey = $"{e.Key}/{e.SystemKey}/{(int)e.Key} mod={Keyboard.Modifiers}";
        if (_closing) return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (e.Key == Key.Escape)
        {
            if (TextEditor.Visibility == Visibility.Visible)
            {
                HideTextEditor(commit: false);
            }
            else if (OcrPanel.Visibility == Visibility.Visible)
            {
                HideOcrPanel();
            }
            else if (_tool != AnnotationTool.None)
            {
                SelectTool(AnnotationTool.None);
            }
            else
            {
                CancelRequested?.Invoke();
            }

            e.Handled = true;
            return;
        }

        if (TextEditor.Visibility == Visibility.Visible) return;
        if (OcrText.IsKeyboardFocusWithin) return;

        if (e.Key == Key.Enter)
        {
            Submit(CaptureAction.Copy);
            e.Handled = true;
            return;
        }

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.C:
                    Submit(CaptureAction.Copy);
                    e.Handled = true;
                    return;
                case Key.S:
                    Submit(CaptureAction.Save);
                    e.Handled = true;
                    return;
                case Key.P:
                    Submit(CaptureAction.Pin);
                    e.Handled = true;
                    return;
                case Key.Z:
                    Undo();
                    e.Handled = true;
                    return;
                case Key.A:
                    _selection = new Rect(0, 0, ActualWidth, ActualHeight);
                    UpdateSelectionVisuals();
                    e.Handled = true;
                    return;
            }
        }

        if (HasSelection && !ctrl)
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            bool moved = true;
            switch (e.Key)
            {
                case Key.Left:
                    _selection = new Rect(_selection.X - step, _selection.Y, _selection.Width, _selection.Height);
                    break;
                case Key.Right:
                    _selection = new Rect(_selection.X + step, _selection.Y, _selection.Width, _selection.Height);
                    break;
                case Key.Up:
                    _selection = new Rect(_selection.X, _selection.Y - step, _selection.Width, _selection.Height);
                    break;
                case Key.Down:
                    _selection = new Rect(_selection.X, _selection.Y + step, _selection.Width, _selection.Height);
                    break;
                default:
                    moved = false;
                    break;
            }

            if (moved)
            {
                _selection = new Rect(
                    Math.Clamp(_selection.X, 0, Math.Max(0, ActualWidth - _selection.Width)),
                    Math.Clamp(_selection.Y, 0, Math.Max(0, ActualHeight - _selection.Height)),
                    _selection.Width, _selection.Height);
                UpdateSelectionVisuals();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.R:
                    SelectTool(AnnotationTool.Rectangle);
                    e.Handled = true;
                    return;
                case Key.O:
                    SelectTool(AnnotationTool.Ellipse);
                    e.Handled = true;
                    return;
                case Key.A:
                    SelectTool(AnnotationTool.Arrow);
                    e.Handled = true;
                    return;
                case Key.P:
                    SelectTool(AnnotationTool.Pen);
                    e.Handled = true;
                    return;
                case Key.T:
                    SelectTool(AnnotationTool.Text);
                    e.Handled = true;
                    return;
                case Key.M:
                    SelectTool(AnnotationTool.Mosaic);
                    e.Handled = true;
                    return;
                case Key.X:
                    StartOcr();
                    e.Handled = true;
                    return;
            }
        }
    }

    // ---------------------------------------------------------------- 输出

    /// <summary>把当前选区 + 标注合成为最终位图。</summary>
    internal BitmapSource ComposeResult()
    {
        var phys = SelectionPhysical;
        var crop = _grab.CropPhysical(phys.X, phys.Y, phys.Width, phys.Height);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);

        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(crop, new Rect(0, 0, crop.PixelWidth, crop.PixelHeight));
            if (_annotations.Count > 0)
            {
                dc.PushTransform(new ScaleTransform(_scale, _scale));
                AnnotationPainter.Paint(dc, _annotations, null, ProvideMosaic);
                dc.Pop();
            }
        }

        var rtb = new RenderTargetBitmap(crop.PixelWidth, crop.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    internal void Submit(CaptureAction action)
    {
        if (_closing || !HasSelection) return;

        HideTextEditor(commit: true);
        _closing = true;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();

        try
        {
            var image = ComposeResult();
            var phys = SelectionPhysical;
            var result = new OverlayResult(image, phys.X, phys.Y, _scale);
            Finished?.Invoke(this, result, action);
        }
        catch (Exception ex)
        {
            Logger.Error("生成截图失败", ex);
            _closing = false;
        }
    }

    internal void ClearSelection()
    {
        _annotations.Clear();
        _mosaicCache.Clear();
        _draft = null;
        _tool = AnnotationTool.None;
        _selection = Rect.Empty;
        HideOcrPanel();
        UpdateSelectionVisuals();
    }

    /// <summary>开发自检用：铺一个示例选区与示例标注，便于核对渲染效果。</summary>
    internal void ApplyDemoState()
    {
        double w = Math.Max(200, ActualWidth);
        double h = Math.Max(200, ActualHeight);

        var red = Color.FromRgb(0xFF, 0x3B, 0x30);
        _selection = new Rect(w * 0.14, h * 0.18, w * 0.46, h * 0.40);

        var rect = new Annotation { Tool = AnnotationTool.Rectangle, Color = red, Thickness = 4 };
        rect.Points.Add(new Point(26, 26));
        rect.Points.Add(new Point(238, 146));
        _annotations.Add(rect);

        var arrow = new Annotation { Tool = AnnotationTool.Arrow, Color = red, Thickness = 4 };
        arrow.Points.Add(new Point(266, 40));
        arrow.Points.Add(new Point(392, 130));
        _annotations.Add(arrow);

        var mosaic = new Annotation { Tool = AnnotationTool.Mosaic, Color = red, Thickness = 4 };
        mosaic.Points.Add(new Point(430, 250));
        mosaic.Points.Add(new Point(640, 350));
        _annotations.Add(mosaic);

        var text = new Annotation
        {
            Tool = AnnotationTool.Text,
            Color = red,
            Thickness = 4,
            FontSize = 22,
            Text = "轻截 QingJie",
        };
        text.Points.Add(new Point(250, 176));
        _annotations.Add(text);

        SelectTool(AnnotationTool.Rectangle);
        UpdateSelectionVisuals();
    }
}
