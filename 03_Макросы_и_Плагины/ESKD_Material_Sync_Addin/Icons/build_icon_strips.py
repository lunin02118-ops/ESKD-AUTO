# -*- coding: utf-8 -*-
"""Собирает полосы иконок вкладки ЕСКД: icons_small.bmp (N×16) и icons_large.bmp (N×24).

Порядок изображений совпадает с индексами AddCommandItem2 в SwAddin:
0 — «Настройки ЕСКД», 1 — «Синхронизировать», 2 — «Деталь БЧ», 3 — «Ведомость ЛЗК»,
4 — «Проверить изделие», 5 — «Отчёт проверки», 6 — «Выгрузить в производство»,
7 — «Сделать независимым». Первые два изображения
берутся из прежних полос,
остальные рисуются здесь.
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


SHEET = (255, 255, 255)
GRID = (120, 140, 165)
HEAD = (46, 125, 50)
FOLD = (190, 200, 215)


def draw_lzk(size):
    """Лист ведомости: загнутый угол, зелёная строка заголовка, строки таблицы."""
    img = Image.new("RGB", (size, size), WHITE)
    px = img.load()
    left, right = (2, 13) if size == 16 else (3, 20)
    top, bottom = (1, 14) if size == 16 else (1, 22)
    fold = 3 if size == 16 else 5
    for x in range(left, right + 1):
        for y in range(top, bottom + 1):
            if x > right - fold and y < top + fold and (x - (right - fold)) > (y - top):
                continue
            edge = x in (left, right) or y in (top, bottom) or (x - (right - fold)) == (y - top)
            px[x, y] = BORDER if edge else SHEET
    for i in range(1, fold):
        for j in range(i, fold):
            px[right - fold + i, top + j] = FOLD
    step = 3 if size == 16 else 4
    head = top + fold + 1
    for x in range(left + 2, right - 1):
        for y in range(head, head + (2 if size == 16 else 3)):
            px[x, y] = HEAD
    y = head + (2 if size == 16 else 3) + step - 1
    while y < bottom - 1:
        for x in range(left + 2, right - 1):
            px[x, y] = GRID
        y += step
    col = left + (4 if size == 16 else 6)
    for yy in range(head, bottom - 1):
        px[col, yy] = GRID if px[col, yy] != HEAD else HEAD
    return img


CHECK = (46, 125, 50)


def draw_check(size):
    """Лист с галочкой: проверка изделия закончилась отчётом."""
    img = Image.new("RGB", (size, size), WHITE)
    px = img.load()
    left, right = (2, 11) if size == 16 else (3, 17)
    top, bottom = (1, 14) if size == 16 else (1, 22)
    for x in range(left, right + 1):
        for y in range(top, bottom + 1):
            px[x, y] = BORDER if x in (left, right) or y in (top, bottom) else SHEET
    step = 3 if size == 16 else 4
    y = top + step
    while y < bottom - 1:
        for x in range(left + 2, right - 1):
            px[x, y] = GRID
        y += step
    # галочка справа внизу, поверх листа
    thickness = 1 if size == 16 else 2
    short_leg = 3 if size == 16 else 4
    long_leg = 6 if size == 16 else 9
    cx, cy = (9, 12) if size == 16 else (13, 18)
    for i in range(short_leg):
        for t in range(thickness):
            px[cx - i, cy - i + t] = CHECK
    for i in range(long_leg):
        for t in range(thickness):
            px[cx + i, cy - i + t] = CHECK
    return img


def draw_report(size):
    """Тот же лист, но галочка синяя и лежит в строке отчёта: открыть готовый файл, ничего не проверяя."""
    img = draw_check(size)
    px = img.load()
    left, right = (2, 11) if size == 16 else (3, 17)
    top, bottom = (1, 14) if size == 16 else (1, 22)
    thickness = 1 if size == 16 else 2
    short_leg = 3 if size == 16 else 4
    long_leg = 6 if size == 16 else 9
    cx, cy = (9, 12) if size == 16 else (13, 18)
    for i in range(short_leg):
        for t in range(thickness):
            px[cx - i, cy - i + t] = BORDER
    for i in range(long_leg):
        for t in range(thickness):
            px[cx + i, cy - i + t] = BORDER
    # уголок «файл уже готов»: полоска у правого края листа
    for y in range(top + 2, top + (5 if size == 16 else 8)):
        px[right - 1, y] = HEAD
    return img


ARROW = (179, 58, 30)


def draw_export(size):
    """Лист и стрелка вниз-вправо: документы уходят из изделия в цех."""
    img = Image.new("RGB", (size, size), WHITE)
    px = img.load()
    left, right = (2, 10) if size == 16 else (3, 15)
    top, bottom = (1, 11) if size == 16 else (1, 17)
    for x in range(left, right + 1):
        for y in range(top, bottom + 1):
            px[x, y] = BORDER if x in (left, right) or y in (top, bottom) else SHEET
    step = 3 if size == 16 else 4
    y = top + step
    while y < bottom - 1:
        for x in range(left + 2, right - 1):
            px[x, y] = GRID
        y += step
    # стрелка вниз у правого нижнего угла
    thickness = 1 if size == 16 else 2
    cx = right + (2 if size == 16 else 4)
    for y in range(top + 3, bottom + (3 if size == 16 else 5)):
        for t in range(thickness):
            if cx + t < size:
                px[cx + t, y] = ARROW
    tip = bottom + (3 if size == 16 else 5)
    for i in range(3 if size == 16 else 4):
        for t in range(thickness):
            if cx - i >= 0 and tip - i < size:
                px[cx - i, tip - i] = ARROW
            if cx + i + t < size and tip - i < size:
                px[cx + i + t, tip - i] = ARROW
    return img


LINK = (179, 58, 30)
ETALON = (214, 219, 228)


def draw_independent(size):
    """Два листа и разорванная связь между ними: копия живёт своей жизнью, эталон остаётся общим."""
    img = Image.new("RGB", (size, size), WHITE)
    px = img.load()
    # позади — серый эталон, впереди — белая копия
    back = (1, 8, 1, 10) if size == 16 else (2, 12, 1, 15)
    front = (6, 13, 4, 14) if size == 16 else (9, 20, 6, 21)
    for left, right, top, bottom in (back, front):
        fill = ETALON if (left, right, top, bottom) == back else SHEET
        for x in range(left, right + 1):
            for y in range(top, bottom + 1):
                px[x, y] = BORDER if x in (left, right) or y in (top, bottom) else fill
    step = 3 if size == 16 else 4
    y = front[2] + step
    while y < front[3] - 1:
        for x in range(front[0] + 2, front[1] - 1):
            px[x, y] = GRID
        y += step
    # разрыв связи: короткая красная черта наискось между листами
    thickness = 1 if size == 16 else 2
    cx, cy = (6, 4) if size == 16 else (9, 6)
    for i in range(4 if size == 16 else 6):
        for t in range(thickness):
            x, yy = cx - i + t, cy + i
            if 0 <= x < size and 0 <= yy < size:
                px[x, yy] = LINK
    return img


def tiles(strip_path, size, count):
    strip = Image.open(strip_path).convert("RGB")
    return [strip.crop((i * size, 0, (i + 1) * size, size)) for i in range(count)]


def build(name, size):
    path = os.path.join(HERE, name)
    old = Image.open(path)
    existing = old.size[0] // size
    parts = tiles(path, size, min(existing, 2)) + [draw_bch(size), draw_lzk(size), draw_check(size), draw_report(size),
                                                   draw_export(size), draw_independent(size)]
    out = Image.new("RGB", (size * len(parts), size), WHITE)
    for i, tile in enumerate(parts):
        out.paste(tile, (i * size, 0))
    out.save(path, format="BMP")
    return out.size


if __name__ == "__main__":
    print("icons_small.bmp", build("icons_small.bmp", 16))
    print("icons_large.bmp", build("icons_large.bmp", 24))
