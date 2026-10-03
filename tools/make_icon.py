# -*- coding: utf-8 -*-
"""生成应用图标：红色圆角方块 + 白色电源符号，输出多尺寸 .ico 和预览 PNG。"""
import os
from PIL import Image, ImageDraw

OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src")
S = 512
R = 108          # 圆角半径


def lerp(c1, c2, t):
    return tuple(int(round(a + (b - a) * t)) for a, b in zip(c1, c2))


def build_base():
    """红色渐变圆角方块（带投影感）。"""
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    top = (238, 82, 74)
    bottom = (183, 28, 28)
    for y in range(S):
        t = y / float(S - 1)
        # 轻微的对角高光
        gd.line([(0, y), (S, y)], fill=lerp(top, bottom, t) + (255,))

    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=R, fill=255)
    img.paste(grad, (0, 0), mask)
    return img


def draw_power(img):
    d = ImageDraw.Draw(img)
    cx, cy = S // 2, S // 2 + 10
    ring_r = 132
    ring_w = 40
    bbox = [cx - ring_r, cy - ring_r, cx + ring_r, cy + ring_r]
    d.ellipse(bbox, outline=(255, 255, 255, 255), width=ring_w)

    # 用背景色挖出顶部缺口
    bg = build_base()
    gap_half_w = 52
    for y in range(cy - ring_r - 4, cy - ring_r + ring_w + 6):
        col = bg.getpixel((cx, y))
        d.line([(cx - gap_half_w, y), (cx + gap_half_w, y)], fill=col, width=1)

    # 竖线
    bar_w = 40
    bar_top = cy - ring_r - 6
    bar_bottom = cy + 6
    d.rounded_rectangle(
        [cx - bar_w // 2, bar_top, cx + bar_w // 2, bar_bottom],
        radius=bar_w // 2, fill=(255, 255, 255, 255))


def main():
    base = build_base()
    icon_img = base.copy()
    draw_power(icon_img)

    png = os.path.join(OUT_DIR, "icon_preview.png")
    icon_img.resize((256, 256), Image.LANCZOS).save(png)

    ico = os.path.join(OUT_DIR, "app.ico")
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    icon_img.save(ico, format="ICO", sizes=sizes)
    print("OK  " + ico)
    print("OK  " + png)


if __name__ == "__main__":
    main()
