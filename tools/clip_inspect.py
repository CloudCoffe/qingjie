"""用 Win32 剪贴板 API 检查当前剪贴板里的图像，模拟 Paint/Word/微信 这类原生程序看到的内容。

用法：
    python tools/clip_inspect.py                    # 列出格式并校验 CF_DIB 头部
    python tools/clip_inspect.py out.png            # 顺便把 CF_DIB 还原成 PNG
"""
import ctypes
import struct
import sys
import zlib
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

HANDLE = ctypes.c_void_p

user32.OpenClipboard.argtypes = [wintypes.HWND]
user32.OpenClipboard.restype = wintypes.BOOL
user32.CloseClipboard.restype = wintypes.BOOL
user32.EnumClipboardFormats.argtypes = [wintypes.UINT]
user32.EnumClipboardFormats.restype = wintypes.UINT
user32.GetClipboardFormatNameW.argtypes = [wintypes.UINT, wintypes.LPWSTR, ctypes.c_int]
user32.GetClipboardFormatNameW.restype = ctypes.c_int
user32.GetClipboardData.argtypes = [wintypes.UINT]
user32.GetClipboardData.restype = HANDLE
user32.RegisterClipboardFormatW.argtypes = [wintypes.LPCWSTR]
user32.RegisterClipboardFormatW.restype = wintypes.UINT
kernel32.GlobalLock.argtypes = [HANDLE]
kernel32.GlobalLock.restype = HANDLE
kernel32.GlobalUnlock.argtypes = [HANDLE]
kernel32.GlobalSize.argtypes = [HANDLE]
kernel32.GlobalSize.restype = ctypes.c_size_t

CF_NAMES = {
    1: "CF_TEXT", 2: "CF_BITMAP", 3: "CF_METAFILEPICT", 6: "CF_TIFF",
    8: "CF_DIB", 9: "CF_PALETTE", 13: "CF_UNICODETEXT", 14: "CF_ENHMETAFILE",
    15: "CF_HDROP", 16: "CF_LOCALE", 17: "CF_DIBV5",
}


def format_name(fmt):
    if fmt in CF_NAMES:
        return CF_NAMES[fmt]
    buf = ctypes.create_unicode_buffer(256)
    n = user32.GetClipboardFormatNameW(fmt, buf, 256)
    return buf.value if n else f"#{fmt}"


def payload(fmt):
    # CF_BITMAP 是 GDI 位图句柄，不是全局内存块；对它调 GlobalSize/GlobalLock 会崩
    if fmt == 2:
        handle = user32.GetClipboardData(fmt)
        return None if not handle else b""
    handle = user32.GetClipboardData(fmt)
    if not handle:
        return None
    size = kernel32.GlobalSize(handle)
    if not size:
        return None
    ptr = kernel32.GlobalLock(handle)
    if not ptr:
        return None
    try:
        return ctypes.string_at(ptr, size)
    finally:
        kernel32.GlobalUnlock(handle)


def parse_dib(data):
    """返回 (宽, 高, 位深, 压缩方式, 像素偏移, 每行字节)。"""
    bi_size, width, height, _, bitcount, compression = struct.unpack("<IiiHHI", data[:20])
    if bi_size < 40:
        raise ValueError(f"不认识的 DIB 头：biSize={bi_size}")
    bpp = bitcount // 8
    stride = ((width * bpp + 3) // 4) * 4
    # BI_BITFIELDS 会在头后面跟 3 个颜色掩码
    offset = bi_size + (12 if compression == 3 and bi_size == 40 else 0)
    return width, abs(height), bitcount, compression, offset, stride


def dib_to_png(data, path):
    width, height, bitcount, compression, offset, stride = parse_dib(data)
    if compression != 0:
        raise SystemExit(f"BI 压缩方式 = {compression}（不是 BI_RGB），GDI+/Qt 系大概率读不了")
    if bitcount not in (24, 32):
        raise SystemExit(f"不支持的位深 {bitcount}")

    bpp = bitcount // 8
    # 高度为正表示像素自下而上（Windows 约定）：内存第一行对应图像最后一行
    bottom_up = struct.unpack("<i", data[8:12])[0] > 0
    raw = bytearray()
    for y in range(height):
        src_y = height - 1 - y if bottom_up else y
        line = data[offset + src_y * stride: offset + src_y * stride + width * bpp]
        raw.append(0)
        for x in range(width):
            b, g, r = line[x * bpp: x * bpp + 3]
            raw += bytes((r, g, b))

    def chunk(tag, body):
        return struct.pack(">I", len(body)) + tag + body + struct.pack(">I", zlib.crc32(tag + body) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
           + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))
    open(path, "wb").write(png)
    return width, height


def main():
    dump = sys.argv[1] if len(sys.argv) > 1 else None

    if not user32.OpenClipboard(None):
        raise SystemExit(f"打开剪贴板失败 err={ctypes.get_last_error()}")

    found = {}
    try:
        fmt = 0
        while True:
            fmt = user32.EnumClipboardFormats(fmt)
            if fmt == 0:
                break
            name = format_name(fmt)
            found[name] = payload(fmt)

        print(f"共 {len(found)} 种格式：")
        for name, data in found.items():
            if name == "CF_BITMAP":
                detail = "GDI 句柄" if data is not None else "缺失"
            elif data is None:
                detail = "缺失"
            else:
                detail = f"{len(data)} 字节"
            print(f"  {name:<20} {detail}")

        print()
        for probe in ("CF_DIB", "CF_BITMAP", "PNG", "image/png"):
            print(f"  {probe:<12} {'有' if probe in found else '缺失'}")

        dib = found.get("CF_DIB")
        if not dib:
            raise SystemExit("\n没有 CF_DIB：只有认 PNG 的软件（Chromium 系）能粘贴")
        width, height, bitcount, compression, offset, stride = parse_dib(dib)
        print(f"\nCF_DIB：{width}x{height} {bitcount}bpp compression={compression} "
              f"像素偏移={offset} 行字节={stride} 总长={len(dib)}")
        if compression == 0:
            print("  → BI_RGB，Word / 画图 / 微信 / WPS / 浏览器 均可直接解析")
        else:
            print("  → 非 BI_RGB，兼容性差")

        if dump:
            w, h = dib_to_png(dib, dump)
            print(f"  已还原为 PNG：{dump} ({w}x{h})")
    finally:
        user32.CloseClipboard()


if __name__ == "__main__":
    main()
