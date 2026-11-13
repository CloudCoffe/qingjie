using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie.Capture;

public enum AnnotationTool
{
    None = 0,
    Rectangle,
    Ellipse,
    Arrow,
    Pen,
    Text,
    Mosaic,
}

/// <summary>
/// 一条标注。所有坐标都是「相对选区左上角的设备无关像素（DIU）」，
/// 预览与导出共用同一套坐标，导出时整体乘以缩放比即可。
/// </summary>
public sealed class Annotation
{
    public AnnotationTool Tool { get; init; }

    public List<Point> Points { get; } = new();

    public Color Color { get; set; } = Color.FromRgb(0xFF, 0x3B, 0x30);

    public double Thickness { get; set; } = 3;

    public string Text { get; set; } = string.Empty;

    public double FontSize { get; set; } = 16;

    public Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return Rect.Empty;
            if (Tool == AnnotationTool.Text)
            {
                return new Rect(Points[0], new Size(Math.Max(1, Text.Length * FontSize * 0.9), FontSize * 1.4));
            }

            double minX = Points.Min(p => p.X);
            double minY = Points.Min(p => p.Y);
            double maxX = Points.Max(p => p.X);
            double maxY = Points.Max(p => p.Y);
            return new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
        }
    }

    public Annotation Clone()
    {
        var copy = new Annotation
        {
            Tool = Tool,
            Color = Color,
            Thickness = Thickness,
            Text = Text,
            FontSize = FontSize,
        };
        copy.Points.AddRange(Points);
        return copy;
    }
}

/// <summary>马赛克瓦片：把区域切成 cols×rows 个色块，直接画方块，避免依赖位图缩放模式。</summary>
public sealed class MosaicTile
{
    public MosaicTile(int columns, int rows, byte[] bgra)
    {
        Columns = columns;
        Rows = rows;
        Bgra = bgra;
    }

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>BGRA 顺序的像素数据（每块一个像素）。</summary>
    public byte[] Bgra { get; }
}

/// <summary>标注绘制：预览与最终导出使用同一份逻辑，保证所见即所得。</summary>
internal static class AnnotationPainter
{
    /// <summary>马赛克块边长（DIU）。</summary>
    public const double MosaicBlock = 8;

    /// <summary>
    /// 在 DIU 坐标系中绘制全部标注。
    /// 调用方通过 <see cref="DrawingContext.PushTransform"/> 施加整体缩放（导出时 = DPI 缩放比）。
    /// </summary>
    public static void Paint(DrawingContext dc, IReadOnlyList<Annotation> items, double? scale = null,
        Func<Rect, MosaicTile?>? mosaicProvider = null)
    {
        if (items.Count == 0) return;

        if (scale.HasValue && Math.Abs(scale.Value - 1) > 0.001)
        {
            dc.PushTransform(new ScaleTransform(scale.Value, scale.Value));
            PaintCore(dc, items, mosaicProvider);
            dc.Pop();
        }
        else
        {
            PaintCore(dc, items, mosaicProvider);
        }
    }

    private static void PaintCore(DrawingContext dc, IReadOnlyList<Annotation> items, Func<Rect, MosaicTile?>? mosaicProvider)
    {
        foreach (var item in items)
        {
            if (item.Points.Count == 0) continue;

            var brush = new SolidColorBrush(item.Color);
            brush.Freeze();
            var pen = new Pen(brush, Math.Max(1, item.Thickness))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();

            switch (item.Tool)
            {
                case AnnotationTool.Rectangle:
                    if (item.Points.Count >= 2)
                    {
                        dc.DrawRectangle(null, pen, Normalize(item.Points[0], item.Points[1]));
                    }

                    break;

                case AnnotationTool.Ellipse:
                    if (item.Points.Count >= 2)
                    {
                        var rect = Normalize(item.Points[0], item.Points[1]);
                        if (rect.Width > 0.5 && rect.Height > 0.5)
                        {
                            dc.DrawEllipse(null, pen, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
                                rect.Width / 2, rect.Height / 2);
                        }
                    }

                    break;

                case AnnotationTool.Arrow:
                    if (item.Points.Count >= 2)
                    {
                        DrawArrow(dc, brush, pen, item.Points[0], item.Points[1], item.Thickness);
                    }

                    break;

                case AnnotationTool.Pen:
                    if (item.Points.Count >= 2)
                    {
                        dc.DrawGeometry(null, pen, BuildPolyline(item.Points));
                    }

                    break;

                case AnnotationTool.Text:
                    DrawText(dc, item, brush);
                    break;

                case AnnotationTool.Mosaic:
                    if (item.Points.Count >= 2)
                    {
                        var rect = Normalize(item.Points[0], item.Points[1]);
                        if (rect.Width >= 2 && rect.Height >= 2)
                        {
                            var tile = mosaicProvider?.Invoke(rect);
                            if (tile is not null)
                            {
                                DrawMosaic(dc, rect, tile);
                            }
                            else
                            {
                                var fallback = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
                                fallback.Freeze();
                                dc.DrawRectangle(fallback, null, rect);
                            }
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>按块绘制马赛克：每块一个纯色矩形，缩放时依然是硬边方块。</summary>
    private static void DrawMosaic(DrawingContext dc, Rect rect, MosaicTile tile)
    {
        double blockWidth = rect.Width / tile.Columns;
        double blockHeight = rect.Height / tile.Rows;
        var brushes = new Dictionary<uint, SolidColorBrush>();

        for (int row = 0; row < tile.Rows; row++)
        {
            for (int col = 0; col < tile.Columns; col++)
            {
                int offset = (row * tile.Columns + col) * 4;
                uint key = (uint)(tile.Bgra[offset] | (tile.Bgra[offset + 1] << 8) | (tile.Bgra[offset + 2] << 16));

                if (!brushes.TryGetValue(key, out var brush))
                {
                    brush = new SolidColorBrush(Color.FromRgb(tile.Bgra[offset + 2], tile.Bgra[offset + 1], tile.Bgra[offset]));
                    brush.Freeze();
                    brushes[key] = brush;
                }

                // +0.6 补偿浮点取整造成的块间缝隙
                dc.DrawRectangle(brush, null,
                    new Rect(rect.X + col * blockWidth, rect.Y + row * blockHeight, blockWidth + 0.6, blockHeight + 0.6));
            }
        }
    }

    private static void DrawArrow(DrawingContext dc, Brush brush, Pen pen, Point start, Point end, double thickness)
    {
        var vector = end - start;
        double length = vector.Length;
        if (length < 1) return;

        var dir = vector / length;
        double headLength = Math.Min(length, Math.Max(9, thickness * 4.2));
        double headWidth = Math.Max(6, thickness * 2.6);
        var normal = new Vector(-dir.Y, dir.X);

        // 箭头主体（略缩短，避免被箭头盖住）
        var bodyEnd = end - dir * (headLength * 0.65);
        dc.DrawLine(pen, start, bodyEnd);

        var baseCenter = end - dir * headLength;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(end, true, true);
            ctx.LineTo(baseCenter + normal * (headWidth / 2), true, false);
            ctx.LineTo(baseCenter - normal * (headWidth / 2), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    private static void DrawText(DrawingContext dc, Annotation item, Brush brush)
    {
        var text = item.Text;
        if (string.IsNullOrEmpty(text)) return;

        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var lines = text.Replace("\r\n", "\n").Split('\n');
        double lineHeight = item.FontSize * 1.32;
        var origin = item.Points[0];

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            var formatted = new FormattedText(lines[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, item.FontSize, brush, 1.0);
            dc.DrawText(formatted, new Point(origin.X, origin.Y + i * lineHeight));
        }
    }

    private static StreamGeometry BuildPolyline(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(points[i], true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    public static Rect Normalize(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>按块求平均色，生成马赛克瓦片（块内均值，视觉上比点采样自然得多）。</summary>
    public static MosaicTile BuildMosaicTile(BitmapSource source, int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);

        var tile = new byte[columns * rows * 4];

        for (int row = 0; row < rows; row++)
        {
            int y0 = row * height / rows;
            int y1 = Math.Max(y0 + 1, (row + 1) * height / rows);

            for (int col = 0; col < columns; col++)
            {
                int x0 = col * width / columns;
                int x1 = Math.Max(x0 + 1, (col + 1) * width / columns);

                long sumB = 0, sumG = 0, sumR = 0;
                int count = 0;

                for (int y = y0; y < y1; y++)
                {
                    int offset = (y * width + x0) * 4;
                    for (int x = x0; x < x1; x++, offset += 4)
                    {
                        sumB += pixels[offset];
                        sumG += pixels[offset + 1];
                        sumR += pixels[offset + 2];
                        count++;
                    }
                }

                int target = (row * columns + col) * 4;
                if (count > 0)
                {
                    tile[target] = (byte)(sumB / count);
                    tile[target + 1] = (byte)(sumG / count);
                    tile[target + 2] = (byte)(sumR / count);
                    tile[target + 3] = 255;
                }
            }
        }

        return new MosaicTile(columns, rows, tile);
    }
}

/// <summary>标注预览层：按当前标注列表自绘，避免维护大量 UI 元素。</summary>
public sealed class AnnotationLayer : FrameworkElement
{
    private IReadOnlyList<Annotation> _items = Array.Empty<Annotation>();
    private Func<Rect, MosaicTile?>? _mosaicProvider;

    public AnnotationLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public void SetContent(IReadOnlyList<Annotation> items, Func<Rect, MosaicTile?>? mosaicProvider)
    {
        _items = items;
        _mosaicProvider = mosaicProvider;
        InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        AnnotationPainter.Paint(dc, _items, null, _mosaicProvider);
    }
}
