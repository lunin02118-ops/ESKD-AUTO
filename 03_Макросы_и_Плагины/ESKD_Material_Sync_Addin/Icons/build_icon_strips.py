# -*- coding: utf-8 -*-
"""Собирает полосы иконок вкладки ЕСКД: icons_small.bmp (N×16) и icons_large.bmp (N×24).

Порядок изображений совпадает с индексами AddCommandItem2 в SwAddin:
0 — «Настройки ЕСКД», 1 — «Синхронизировать», 2 — «Деталь БЧ», 3 — «Ведомость ЛЗК»,
4 — «Проверить изделие», 5 — «Отчёт проверки», 6 — «Выгрузить в производство»,
7 — «Сделать независимым», 8 — «Новая ревизия», 9 — «Снимок эталона»,
10 — «Выдать в производство», 11 — «Закрыть заказ». Первые два изображения
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


def draw_revision(size):
    """Лист с треугольником ревизии: в штампе изменение помечают именно так."""
    img = Image.new("RGB", (size, size), WHITE)
    px = img.load()
    left, right = (2, 13) if size == 16 else (3, 20)
    top, bottom = (1, 14) if size == 16 else (1, 22)
    for x in range(left, right + 1):
        for y in range(top, bottom + 1):
            px[x, y] = BORDER if x in (left, right) or y in (top, bottom) else SHEET
    step = 3 if size == 16 else 4
    y = top + step
    while y < bottom - step:
        for x in range(left + 2, right - 1):
            px[x, y] = GRID
        y += step
    # треугольник изменения у нижнего края листа
    height = 5 if size == 16 else 8
    cx = left + (5 if size == 16 else 8)
    base = bottom - 2
    for row in range(height):
        for x in range(cx - row, cx + row + 1):
            if left < x < right and top < base - height + 1 + row < bottom:
                px[x, base - height + 1 + row] = LINK if row in (0, height - 1) or x in (cx - row, cx + row) else SHEET
    return img


def draw_snapshot(size):
    """Папка со слоями: снимок эталона — это его состояние, отложенное на полку."""
    img = Image.new("RGB", (size, size), WHITE)
    px = img.load()
    layers = ((2, 12, 4, 9), (3, 13, 7, 12)) if size == 16 else ((3, 18, 6, 13), (5, 20, 11, 19))
    for n, (left, right, top, bottom) in enumerate(layers):
        fill = ETALON if n == 0 else SHEET
        for x in range(left, right + 1):
            for y in range(top, bottom + 1):
                px[x, y] = BORDER if x in (left, right) or y in (top, bottom) else fill
        # корешок папки
        tab_right = left + (4 if size == 16 else 6)
        for x in range(left, tab_right + 1):
            px[x, top - 1] = BORDER if x in (left, tab_right) else fill
    return img


def draw_issue(size):
    """Ящик со стрелкой наружу: заявка и документы уходят из КТО в цех."""
    img = Image.new("RGB", (size, size), WHITE)
    px = img.load()
    left, right = (1, 9) if size == 16 else (2, 14)
    top, bottom = (5, 14) if size == 16 else (7, 21)
    for x in range(left, right + 1):
        for y in range(top, bottom + 1):
            px[x, y] = BORDER if x in (left, right) or y in (top, bottom) else PLATE
    # крышка ящика
    lid = top + (3 if size == 16 else 4)
    for x in range(left + 1, right):
        px[x, lid] = GRID
    # стрелка вправо-вверх: документы уезжают
    thickness = 1 if size == 16 else 2
    y0 = top - (3 if size == 16 else 5)
    for i in range(5 if size == 16 else 8):
        for t2 in range(thickness):
            x = right - 1 + i
            if x < size and 0 <= y0 + t2 < size:
                px[x, y0 + t2] = ARROW
    head = 3 if size == 16 else 5
    tip = min(size - 1, right - 1 + (5 if size == 16 else 8))
    for i in range(head):
        for t2 in range(thickness):
            for dy in (-i, i):
                x, y = tip - i, y0 + dy + t2
                if 0 <= x < size and 0 <= y < size:
                    px[x, y] = ARROW
    return img


def draw_close(size):
    """Папка с галочкой: заказ собран, сверен и сдан в архив."""
    img = Image.new("RGB", (size, size), WHITE)
    px = img.load()
    left, right = (1, 12) if size == 16 else (2, 18)
    top, bottom = (4, 13) if size == 16 else (6, 20)
    for x in range(left, right + 1):
        for y in range(top, bottom + 1):
            px[x, y] = BORDER if x in (left, right) or y in (top, bottom) else FOLD
    tab_right = left + (5 if size == 16 else 8)
    for x in range(left, tab_right + 1):
        px[x, top - 1] = BORDER if x in (left, tab_right) else FOLD
    # галочка сдачи поверх папки
    thickness = 1 if size == 16 else 2
    x0, y0 = (4, 9) if size == 16 else (6, 14)
    for i in range(3 if size == 16 else 5):
        for t2 in range(thickness):
            px[min(size - 1, x0 + i), min(size - 1, y0 + i + t2)] = CHECK
    for i in range(5 if size == 16 else 8):
        for t2 in range(thickness):
            x = x0 + (3 if size == 16 else 5) + i
            y = y0 + (3 if size == 16 else 5) - i
            if 0 <= x < size and 0 <= y < size:
                px[x, y + t2] = CHECK
    return img


def tiles(strip_path, size, count):
    strip = Image.open(strip_path).convert("RGB")
    return [strip.crop((i * size, 0, (i + 1) * size, size)) for i in range(count)]


def build(name, size):
    path = os.path.join(HERE, name)
    old = Image.open(path)
    existing = old.size[0] // size
    parts = tiles(path, size, min(existing, 2)) + [draw_bch(size), draw_lzk(size), draw_check(size), draw_report(size),
                                                   draw_export(size), draw_independent(size),
                                                   draw_revision(size), draw_snapshot(size),
                                                   draw_issue(size), draw_close(size)]
    out = Image.new("RGB", (size * len(parts), size), WHITE)
    for i, tile in enumerate(parts):
        out.paste(tile, (i * size, 0))
    out.save(path, format="BMP")
    return out.size


if __name__ == "__main__":
    print("icons_small.bmp", build("icons_small.bmp", 16))
    print("icons_large.bmp", build("icons_large.bmp", 24))
