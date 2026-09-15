# -*- coding: utf-8 -*-
"""Справочник «Нормативы производства» (ТЗ-02, Т-14) и эталонный расчёт раскроя хлыстов для К-4.

Справочник лежит в сетевой папке рядом с шаблонами: `02_Шаблоны_и_Форматки\\Справочники\\Нормативы_производства.xlsx`.
Технолог правит значения в Excel — кнопка читает файл при каждом запуске, переустановка не нужна. Строки ищутся
по столбцу «Ключ», поэтому порядок строк, подписи и примечания можно менять. Перед расчётом К-4 показывает значения
в диалоге, и их можно поправить для одного заказа, не меняя справочник.

Расчёт раскроя здесь — эталон для юнит-тестов C# (`Core/Cutting.cs`): те же входы должны давать те же хлысты.
"""
import math

from openpyxl import Workbook, load_workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side

FONT = "Arial"
THIN = Side(style="thin", color="808080")
BOX = Border(top=THIN, bottom=THIN, left=THIN, right=THIN)
HEAD = PatternFill("solid", fgColor="DDEBF7")
KEY = PatternFill("solid", fgColor="F2F2F2")

# (ключ, параметр, значение, ед. изм., примечание)
NORMS = [
    ("труба.хлыст", "Длина хлыста трубы и профиля", 6000, "мм", "Решение заказчика 15.09.2026"),
    ("труба.захват", "Захват патрона лазерного трубореза", 200, "мм", "Не режется; остаётся в последнем обрезке хлыста"),
    ("труба.торцовка", "Торцовка хлыста", 20, "мм", "Срезается с начала хлыста, в отход"),
    ("труба.рез", "Ширина реза", 4, "мм", "Из заявки 85_Т; для лазера уточнить у технолога"),
    ("труба.деловой", "Минимальная длина делового обрезка", 500, "мм", "Короче — отход (металлолом)"),
    ("лист.формат", "Формат листа по умолчанию", "1250x2500", "мм", "Ширина x длина"),
    ("лист.отход", "Коэффициент отхода листа", 1.15, "", "Расход листа = площадь деталей x коэффициент"),
    ("краска.норма", "Норма расхода порошковой краски", 140, "г/м2", "На 1 м2 окрашиваемой поверхности"),
    ("краска.потери", "Потери краски", 15, "%", "Добавляются к норме"),
    ("краска.тара", "Тара краски", 25, "кг", "Выдача кратно таре"),
]

# (операция, к чему, участок, лист СЗ, автоподсказка)
OPERATIONS = [
    ("Лазерная резка листа", "деталь", "Заготовительный участок", "1 Заготовка", "деталь из листового металла"),
    ("Лазерная резка трубы", "деталь", "Заготовительный участок", "1 Заготовка", "деталь из трубы или профиля (элемент сварной конструкции)"),
    ("Гибка", "деталь", "Заготовительный участок", "1 Заготовка", "листовой металл со сгибами"),
    ("Сварная сборка", "сборка", "Сварочно-сборочный цех", "2 Сварка", "в сборке есть сварные швы или сборка сварной конструкции"),
    ("Механическая сборка", "сборка", "Участок комплектации и сборки", "2 Сварка", "сборка без сварных швов"),
    ("Покраска", "деталь, сборка", "Участок полимерно-порошковой покраски", "3 Покраска", "заполнено свойство «Покрытие»"),
]

# ОК 015-94 (МК 002-97) ОКЕИ: код, наименование, условное обозначение, где применяется
UNITS = [
    ("796", "Штука", "шт", "Детали, сборочные единицы, покупные изделия"),
    ("839", "Комплект", "компл", "Покупные комплекты (замки, фурнитура)"),
    ("006", "Метр", "м", "Трубы и профили — расход"),
    ("055", "Квадратный метр", "м2", "Плиты, настил, площадь окраски"),
    ("166", "Килограмм", "кг", "Металлопрокат к списанию, краска"),
    ("168", "Тонна", "т", "Металлопрокат крупными партиями"),
    ("112", "Литр", "л", "Жидкости"),
]


def _table(ws, headers, rows, widths):
    for c, (title, width) in enumerate(zip(headers, widths), start=1):
        cell = ws.cell(row=1, column=c, value=title)
        cell.font = Font(name=FONT, bold=True, size=10)
        cell.fill = HEAD
        cell.border = BOX
        cell.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)
        ws.column_dimensions[cell.column_letter].width = width
    for r, row in enumerate(rows, start=2):
        for c, value in enumerate(row, start=1):
            cell = ws.cell(row=r, column=c, value=value)
            cell.font = Font(name=FONT, size=10)
            cell.border = BOX
            cell.alignment = Alignment(vertical="top", wrap_text=True)
    ws.freeze_panes = "A2"


def norms_workbook():
    wb = Workbook()
    ws = wb.active
    ws.title = "Нормативы"
    _table(ws, ["Ключ", "Параметр", "Значение", "Ед. изм.", "Примечание"], NORMS, [16, 40, 12, 9, 50])
    for r in range(2, len(NORMS) + 2):
        ws.cell(row=r, column=1).fill = KEY
        ws.cell(row=r, column=1).font = Font(name=FONT, size=9, color="606060")
    _table(wb.create_sheet("Операции"), ["Операция", "Применяется к", "Участок", "Лист СЗ", "Автоподсказка"],
           OPERATIONS, [24, 14, 34, 14, 50])
    _table(wb.create_sheet("Единицы ОКЕИ"), ["Код", "Наименование", "Обозначение", "Применение"], UNITS, [8, 20, 12, 50])
    _table(wb.create_sheet("Коды 1С"), ["Номенклатура", "Обозначение или материал", "Код 1С", "Ед. изм."], [], [40, 34, 16, 10])
    help_ws = wb.create_sheet("Справка")
    for r, text in enumerate([
        "Справочник читают кнопки вкладки ЕСКД при каждом запуске — после правки и сохранения значения действуют сразу.",
        "«Нормативы»: меняйте только столбец «Значение». Столбец «Ключ» не трогайте — по нему кнопка находит строку.",
        "«Операции»: список, из которого конструктор выбирает свойство модели «Операции». Можно добавлять строки; "
        "«Лист СЗ» — в какой лист СЗ на производство попадает деталь или сборка.",
        "«Единицы ОКЕИ»: единицы измерения по ОК 015-94 для СЗ на производство и списания.",
        "«Коды 1С»: заполняется позже; кнопка ищет код по номенклатуре или обозначению.",
    ], start=1):
        cell = help_ws.cell(row=r, column=1, value=text)
        cell.font = Font(name=FONT, size=10)
        cell.alignment = Alignment(wrap_text=True, vertical="top")
    help_ws.column_dimensions["A"].width = 110
    return wb


def read_norms(path):
    """Нормативы по ключу; строки без ключа и неизвестные ключи пропускаются."""
    wb = load_workbook(path, data_only=True, read_only=True)
    rows = list(wb["Нормативы"].iter_rows(values_only=True))
    head = [str(h or "").strip().lower() for h in rows[0]]
    k, v = head.index("ключ"), head.index("значение")
    return {str(r[k]).strip(): r[v] for r in rows[1:] if r[k]}


def cut_bars(lengths_mm, norms):
    """Раскрой одной позиции сортамента на хлысты «первый подходящий по убыванию».

    Хлыст: сначала торцовка, затем детали с резом после каждой; последние `захват` мм держит патрон, их не режут.
    Остаток хлыста (с захватом) — деловой, если не короче `деловой`, иначе отход.
    """
    bar = float(norms["труба.хлыст"])
    grip = float(norms["труба.захват"])
    trim = float(norms["труба.торцовка"])
    kerf = float(norms["труба.рез"])
    min_offcut = float(norms["труба.деловой"])
    usable = bar - trim - grip
    bars = []
    for length in sorted((float(x) for x in lengths_mm), reverse=True):
        if length + kerf > usable:
            raise ValueError(f"деталь {length:g} мм не помещается в хлыст: полезная длина {usable:g} мм")
        for b in bars:
            if b["used"] + length + kerf <= usable:
                b["used"] += length + kerf
                b["pieces"].append(length)
                break
        else:
            bars.append({"used": length + kerf, "pieces": [length]})
    net = sum(sum(b["pieces"]) for b in bars)
    offcuts, waste = [], 0.0
    for b in bars:
        rest = bar - trim - b["used"]
        waste += trim + kerf * len(b["pieces"])
        if rest >= min_offcut:
            offcuts.append(rest)
        else:
            waste += rest
    gross = bar * len(bars)
    return {"bars": len(bars), "net_m": net / 1000, "gross_m": gross / 1000, "kim": net / gross if gross else 0,
            "offcuts_mm": offcuts, "waste_m": waste / 1000, "pattern": [b["pieces"] for b in bars]}


def offcut_text(offcuts_mm, kg_per_m):
    if not offcuts_mm:
        return "—"
    total = sum(offcuts_mm) / 1000
    groups = {}
    for x in offcuts_mm:
        key = int(math.floor(x / 10) * 10)
        groups[key] = groups.get(key, 0) + 1
    parts = [f"{n} шт ~{mm} мм" for mm, n in sorted(groups.items(), key=lambda kv: -kv[1])]
    return f"{total:.1f} м ({total * kg_per_m:.1f} кг) — " + " + ".join(parts[:3]) + (" …" if len(parts) > 3 else "")
