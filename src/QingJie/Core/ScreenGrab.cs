using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie.Core;

/// <summary>
/// 一次性抓取的整块虚拟桌面位图（物理像素）。
/// 所有坐标均以“物理屏幕坐标”为准，避免多屏不同缩放时的模糊/错位。
/// </summary>
internal sealed class ScreenGrab : IDisposable
{
    private readonly byte[] _pixelBuffer = new byte[4];

    private ScreenGrab(BitmapSource full, int x, int y, int width, int height)
    {
        Full = full;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>虚拟桌面左上角物理坐标。</summary>
    public int X { get; }

    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>整块虚拟桌面（Bgra32，已 Freeze，可跨线程使用）。</summary>
    public BitmapSource Full { get; }

    public Int32Rect VirtualBounds => new(X, Y, Width, Height);

    public static ScreenGrab Capture()
    {
        var monitors = Native.EnumMonitors();
        int left, top, right, bottom;
        if (monitors.Count == 0)
        {
            left = 0;
            top = 0;
            right = 1920;
            bottom = 1080;
        }
        else
        {
            left = monitors.Min(m => m.X);
            top = monitors.Min(m => m.Y);
            right = monitors.Max(m => m.X + m.Width);
            bottom = monitors.Max(m => m.Y + m.Height);
        }

        int width = Math.Max(1, right - left);
        int height = Math.Max(1, bottom - top);

        using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        source.Freeze();

        return new ScreenGrab(source, left, top, width, height);
    }

    /// <summary>从整块位图中裁出物理像素矩形。</summary>
    public BitmapSource CropPhysical(int x, int y, int w, int h)
    {
        int localX = x - X;
        int localY = y - Y;
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        localX = Math.Clamp(localX, 0, Math.Max(0, Width - 1));
        localY = Math.Clamp(localY, 0, Math.Max(0, Height - 1));
        if (localX + w > Width) w = Width - localX;
        if (localY + h > Height) h = Height - localY;

        var rect = new Int32Rect(localX, localY, Math.Max(1, w), Math.Max(1, h));
        var cropped = new CroppedBitmap(Full, rect);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>读取某个物理屏幕坐标处的颜色。</summary>
    public System.Windows.Media.Color GetPixel(int screenX, int screenY)
    {
        int lx = screenX - X;
        int ly = screenY - Y;
        if (lx < 0 || ly < 0 || lx >= Width || ly >= Height) return Colors.Black;

        try
        {
            Full.CopyPixels(new Int32Rect(lx, ly, 1, 1), _pixelBuffer, 4, 0);
            return System.Windows.Media.Color.FromRgb(_pixelBuffer[2], _pixelBuffer[1], _pixelBuffer[0]);
        }
        catch
        {
            return Colors.Black;
        }
    }

    public void Dispose()
    {
        // BitmapSource 由 GC 管理，这里保留接口便于后续扩展（如释放 GDI 资源）
    }
}
