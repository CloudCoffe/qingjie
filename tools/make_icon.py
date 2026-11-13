# -*- coding: utf-8 -*-
"""生成应用图标 assets/app.ico（纯标准库实现，不依赖 Pillow）。

图标：圆角渐变蓝底 + 四角白色裁剪标记，代表"轻截"。
用法：python tools/make_icon.py
"""
import math
import os
import struct
import zlib

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets", "app.ico")
SS = 4  # 超采样倍数

BLUE_TOP = (96, 165, 250)
BLUE_BOTTOM = (37, 99, 235)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def rounded_rect_alpha(x, y, size, radius):
    """圆角矩形的抗锯齿覆盖率（0..1），坐标为中心采样点。"""
    left, top, right, bottom = 0.0, 0.0, size, size
    # 退化为内部矩形 + 四角圆判断
    cx = min(max(x, left + radius), right - radius)
    cy = min(max(y, top + radius), bottom - radius)
    dx, dy = x - cx, y - cy
    d = math.hypot(dx, dy)
    return 1.0 if d <= radius else 0.0


def in_bracket(x, y, size, inset, arm, thick):
    """四角裁剪标记：返回 True 表示该像素属于白色标记。"""
    r = size - inset
    # 左上
    if inset <= x <= inset + arm and inset <= y <= inset + thick:
        return True
    if inset <= y <= inset + arm and inset <= x <= inset + thick:
        return True
    # 右上
    if r - arm <= x <= r and inset <= y <= inset + thick:
        return True
    if inset <= y <= inset + arm and r - thick <= x <= r:
        return True
    # 左下
    if inset <= x <= inset + arm and r - thick <= y <= r:
        return True
    if r - arm <= y <= r and inset <= x <= inset + thick:
        return True
    # 右下
    if r - arm <= x <= r and r - thick <= y <= r:
        return True
    if r - arm <= y <= r and r - thick <= x <= r:
        return True
    return False


def render(size):
    """返回 size x size 的 RGBA 像素行列表（bytes 列表）。"""
    big = size * SS
    radius = big * 0.22
    inset = big * 0.20
    arm = big * 0.17
    thick = big * 0.075

    # 超采样缓冲
    buf = [[0.0, 0.0, 0.0, 0.0] for _ in range(big * big)]
    for py in range(big):
        for px in range(big):
            x = px + 0.5
            y = py + 0.5
            cov = rounded_rect_alpha(x, y, big, radius)
            if cov <= 0:
                continue
            t = (x + y) / (2.0 * big)
            c = lerp(BLUE_TOP, BLUE_BOTTOM, t)
            if in_bracket(x, y, big, inset, arm, thick):
                c = (255, 255, 255)
            buf[py * big + px] = [c[0], c[1], c[2], 255.0]

    # 盒式下采样
    rows = []
    for oy in range(size):
        row = bytearray()
        for ox in range(size):
            r = g = b = a = 0.0
            for dy in range(SS):
                base = (oy * SS + dy) * big + ox * SS
                for dx in range(SS):
                    p = buf[base + dx]
                    pa = p[3]
                    r += p[0] * pa
                    g += p[1] * pa
                    b += p[2] * pa
                    a += pa
            n = SS * SS
            if a > 0:
                row += bytes((int(r / a), int(g / a), int(b / a), int(a / n)))
            else:
                row += b"\x00\x00\x00\x00"
        rows.append(bytes(row))
    return rows


def png_bytes(size):
    rows = render(size)
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    sizes = [16, 24, 32, 48, 64, 128, 256]
    payloads = [(s, png_bytes(s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(payloads))
    entries = b""
    offset = 6 + 16 * len(payloads)
    for s, data in payloads:
        w = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as f:
        f.write(header + entries + b"".join(d for _, d in payloads))
    print("written:", OUT, os.path.getsize(OUT), "bytes")


if __name__ == "__main__":
    main()
