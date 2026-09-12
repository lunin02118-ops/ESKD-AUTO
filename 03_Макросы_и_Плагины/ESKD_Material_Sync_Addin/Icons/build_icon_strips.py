# -*- coding: utf-8 -*-
"""Собирает полосы иконок вкладки ЕСКД: icons_small.bmp (N×16) и icons_large.bmp (N×24).

Порядок изображений совпадает с индексами AddCommandItem2 в SwAddin:
0 — «Настройки ЕСКД», 1 — «Синхронизировать», 2 — «Деталь БЧ».
Первые два изображения берутся из прежних полос, третье рисуется здесь.
Запуск: python build_icon_strips.py (нужен Pillow).
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))

GLYPH_B = ["#####", "#....", "#....", "####.", "#...#", "#...#", "####."]
GLYPH_CH = ["#...#", "#...#", "#...#", ".####", "....#", "....#", "....#"]

WHITE = (255, 255, 255)
PLATE = (220, 230, 240)
BORDER = (74, 93, 120)
TEXT = (179, 58, 30)


def draw_bch(size):
    scale = 1 if size == 16 else 2
    img = Image.new("RGB", (size, size), WHITE)
    px = img.load()
    top, bottom = (2, 13) if size == 16 else (3, 20)
    for x in range(size):
        for y in range(top, bottom + 1):
            edge = x in (0, size - 1) or y in (top, bottom)
            corner = x in (0, size - 1) and y in (top, bottom)
            if corner:
                continue
            px[x, y] = BORDER if edge else PLATE
    glyph_w = 5 * scale
    gap = scale
    text_w = glyph_w * 2 + gap
    x0 = (size - text_w) // 2
    y0 = top + ((bottom - top + 1) - 7 * scale) // 2
    for gi, glyph in enumerate((GLYPH_B, GLYPH_CH)):
        gx = x0 + gi * (glyph_w + gap)
        for row, line in enumerate(glyph):
            for col, ch in enumerate(line):
                if ch != "#":
                    continue
                for dx in range(scale):
                    for dy in range(scale):
                        px[gx + col * scale + dx, y0 + row * scale + dy] = TEXT
    return img


def tiles(strip_path, size, count):
    strip = Image.open(strip_path).convert("RGB")
    return [strip.crop((i * size, 0, (i + 1) * size, size)) for i in range(count)]


def build(name, size):
    path = os.path.join(HERE, name)
    old = Image.open(path)
    existing = old.size[0] // size
    parts = tiles(path, size, min(existing, 2)) + [draw_bch(size)]
    out = Image.new("RGB", (size * len(parts), size), WHITE)
    for i, tile in enumerate(parts):
        out.paste(tile, (i * size, 0))
    out.save(path, format="BMP")
    return out.size


if __name__ == "__main__":
    print("icons_small.bmp", build("icons_small.bmp", 16))
    print("icons_large.bmp", build("icons_large.bmp", 24))
