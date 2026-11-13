using System.IO;
using System.Windows.Media.Imaging;

namespace QingJie.Core;

internal static class ImageIo
{
    /// <summary>把位图写入剪贴板（CF_DIB + CF_BITMAP + PNG，兼容主流软件）。</summary>
    public static bool CopyToClipboard(BitmapSource image) => ClipboardImage.Copy(image);

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, EncodePng(image));
    }

    /// <summary>生成默认文件名：轻截_20260101_120000.png</summary>
    public static string BuildFileName(string directory, string extension = ".png")
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var name = $"轻截_{stamp}{extension}";
        var full = Path.Combine(directory, name);
        int index = 2;
        while (File.Exists(full))
        {
            full = Path.Combine(directory, $"轻截_{stamp}_{index}{extension}");
            index++;
        }

        return full;
    }
}
