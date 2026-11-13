#!/usr/bin/env python3
"""裁剪 / 放大 PNG 的一段区域，仅用标准库，便于肉眼核对渲染细节。

用法： python tools/crop_png.py in.png out.png x y w h [zoom]
坐标与尺寸都是像素，zoom 为整数倍放大（最近邻）。
"""

import struct
import sys
import zlib


def decode(path):
    data = open(path, "rb").read()
    pos, idat, width = 8, [], None
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        if tag == b"IHDR":
            width, height, _, color_type = struct.unpack(">IIBB", chunk[:10])
        elif tag == b"IDAT":
            idat.append(chunk)
        pos += 12 + length

    raw = zlib.decompress(b"".join(idat))
    bpp = 4 if color_type == 6 else 3
    stride = width * bpp
    out = bytearray(stride * height)
    prev = bytearray(stride)
    i = 0
    for y in range(height):
        filt = raw[i]
        i += 1
        line = bytearray(raw[i:i + stride])
        i += stride
        if filt == 1:
            for x in range(bpp, stride):
                line[x] = (line[x] + line[x - bpp]) & 0xFF
        elif filt == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 0xFF
        elif filt == 3:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 0xFF
        elif filt == 4:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                b = prev[x]
                c = prev[x - bpp] if x >= bpp else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 0xFF
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return width, height, bpp, out


def encode(path, width, height, rgb):
    def chunk(tag, payload):
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    raw = b"".join(b"\x00" + rgb[y * width * 3:(y + 1) * width * 3] for y in range(height))
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    open(path, "wb").write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header)
                           + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    if len(sys.argv) < 8:
        print(__doc__)
        return 1

    src, dst = sys.argv[1], sys.argv[2]
    x, y, w, h = (int(v) for v in sys.argv[3:7])
    zoom = int(sys.argv[7])

    width, height, bpp, pixels = decode(src)
    x = max(0, min(x, width - 1))
    y = max(0, min(y, height - 1))
    w = min(w, width - x)
    h = min(h, height - y)

    out = bytearray(w * zoom * h * zoom * 3)
    for oy in range(h * zoom):
        sy = y + oy // zoom
        row = sy * width * bpp
        base = oy * w * zoom * 3
        for ox in range(w * zoom):
            sx = x + ox // zoom
            s = row + sx * bpp
            d = base + ox * 3
            out[d] = pixels[s]
            out[d + 1] = pixels[s + 1]
            out[d + 2] = pixels[s + 2]

    encode(dst, w * zoom, h * zoom, bytes(out))
    print(f"{dst}  {w * zoom}x{h * zoom}  (源 {width}x{height} 裁 {x},{y} {w}x{h} x{zoom})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
