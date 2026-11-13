using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie.Core;

/// <summary>
/// 把截图写进系统剪贴板，一次提供四类内容，尽量覆盖各种软件：
/// <list type="bullet">
///   <item><description><c>CF_DIB</c>：自己构造的 <c>BI_RGB</c> 32bpp 位图，Word / 微信 / WPS / 画图 等按此解析；</description></item>
///   <item><description><c>PNG</c> 与 <c>image/png</c>：Chromium 系（企业微信、浏览器等）优先读它；</description></item>
///   <item><description><c>Bitmap</c>：.NET / WinForms 系程序 <c>Clipboard.GetImage()</c> 依赖它。</description></item>
/// </list>
/// <para>
/// 关键点：CF_DIB 必须显式写入 <c>BI_RGB</c>。若只调用 <see cref="DataObject.SetImage"/>，
/// 让 OLE 从 <c>Bitmap</c> 自动转换，WPF 会给出 <c>BI_BITFIELDS</c> 头（biCompression=3），
/// 部分原生读图实现（Qt 系）会拒收 —— 表现就是「只有个别软件能粘贴」。
/// </para>
/// </summary>
internal static class ClipboardImage
{
    private const string PngName = "PNG";
    private const string PngMimeName = "image/png";

    private const uint CF_BITMAP = 2;
    private const uint CF_DIB = 8;
    private const uint BI_RGB = 0;

    private static readonly uint PngFormat = RegisterClipboardFormat(PngName);
    private static readonly uint PngMimeFormat = RegisterClipboardFormat(PngMimeName);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    /// <summary>写入剪贴板，返回是否成功。</summary>
    public static bool Copy(BitmapSource image)
    {
        byte[] png = ImageIo.EncodePng(image);
        byte[] dib = BuildDib(ToBgra32(image));

        // 剪贴板可能被其它程序短暂占用，重试几次
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.Dib, new MemoryStream(dib, writable: false));
                // 用 MemoryStream 而不是 byte[]：byte[] 会被 .NET 序列化一层，
                // 原生程序按 HGLOBAL 直接读会拿到带头的垃圾数据
                data.SetData(PngName, new MemoryStream(png, writable: false));
                data.SetData(PngMimeName, new MemoryStream(png, writable: false));
                data.SetImage(image);

                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                Logger.Warn($"写剪贴板失败：{ex.Message}");
                Thread.Sleep(60);
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- 格式构造

    /// <summary>统一成非预乘 Bgra32，并把 alpha 拉满（CF_DIB 没有 alpha 语义）。</summary>
    private static BitmapSource ToBgra32(BitmapSource image)
    {
        BitmapSource source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        int stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>构造 CF_DIB：BITMAPINFOHEADER + 自下而上的 BGRA 像素，BI_RGB 32bpp。</summary>
    private static byte[] BuildDib(BitmapSource bgra)
    {
        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;

        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var header = new BITMAPINFOHEADER
        {
            biSize = 40,
            biWidth = width,
            biHeight = height,          // 正数 = 像素自下而上
            biPlanes = 1,
            biBitCount = 32,
            biCompression = (int)BI_RGB,
            biSizeImage = stride * height,
        };

        var dib = new byte[40 + stride * height];

        IntPtr temp = Marshal.AllocHGlobal(40);
        try
        {
            Marshal.StructureToPtr(header, temp, false);
            Marshal.Copy(temp, dib, 0, 40);
        }
        finally
        {
            Marshal.FreeHGlobal(temp);
        }

        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * stride, dib, 40 + (height - 1 - y) * stride, stride);
        }

        return dib;
    }

    // ---------------------------------------------------------------- 自检

    /// <summary>开发自检：读回指定格式的数据，模拟别的软件粘贴时看到的内容。</summary>
    internal static byte[]? ReadBack(uint format)
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;

            int size = (int)GlobalSize(handle);
            if (size <= 0) return null;

            IntPtr source = GlobalLock(handle);
            if (source == IntPtr.Zero) return null;

            try
            {
                var buffer = new byte[size];
                Marshal.Copy(source, buffer, 0, size);
                return buffer;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    internal static bool IsAvailable(uint format) => IsClipboardFormatAvailable(format);

    internal static uint DibFormat => CF_DIB;

    internal static uint BitmapFormat => CF_BITMAP;

    internal static uint Png => PngFormat;

    internal static uint PngMime => PngMimeFormat;
}
