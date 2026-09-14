# -*- coding: utf-8 -*-
"""Макеты Excel для кнопок ТЗ-02 (этап Э0, Т-11, Т-12, Т-51) до выдачи шаблонов заказчиком.

Кнопки читают эти файлы по заголовкам столбцов (Т-10), поэтому шаблон заказчика может заменить макет без правки кода,
если в нём есть те же заголовки. Разделы СЗ на производство — отдельные листы: так их проще читать и дописывать.

    python 09_Тесты/tools/make_tz02_templates.py          — записать макеты в 02_Шаблоны_и_Форматки/Шаблоны документов
"""
import sys
from pathlib import Path

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.worksheet.datavalidation import DataValidation

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "02_Шаблоны_и_Форматки" / "Шаблоны документов"
THIN = Side(style="thin", color="808080")
BOX = Border(top=THIN, bottom=THIN, left=THIN, right=THIN)
HEAD_FILL = PatternFill("solid", fgColor="DDEBF7")
NOTE_FILL = PatternFill("solid", fgColor="FFF2CC")
FONT = "Arial"


def header_row(ws, row, columns, note_columns=()):
    for i, (title, width) in enumerate(columns, start=1):
        c = ws.cell(row=row, column=i, value=title)
        c.font = Font(name=FONT, bold=True, size=10)
        c.fill = NOTE_FILL if title in note_columns else HEAD_FILL
        c.border = BOX
        c.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)
        ws.column_dimensions[get_column_letter(i)].width = width
    ws.row_dimensions[row].height = 32
    ws.freeze_panes = ws.cell(row=row + 1, column=1)


def body_rows(ws, first, count, width):
    for r in range(first, first + count):
        for col in range(1, width + 1):
            c = ws.cell(row=r, column=col)
            c.border = BOX
            c.font = Font(name=FONT, size=10)


def title(ws, text, fields):
    ws["A1"] = text
    ws["A1"].font = Font(name=FONT, bold=True, size=14)
    for i, (label, value) in enumerate(fields, start=2):
        ws.cell(row=i, column=1, value=label).font = Font(name=FONT, bold=True, size=10)
        ws.cell(row=i, column=2, value=value).font = Font(name=FONT, size=10)


def order_note():
    wb = Workbook()
    ws = wb.active
    ws.title = "СЗ"
    title(ws, "Служебная записка на изготовление", [("№ заказа:", ""), ("Заказчик:", ""), ("Дата:", ""), ("Срок:", "")])
    columns = [("Позиция", 9), ("Изделие", 32), ("Стандартность", 14), ("Направление", 13), ("Шифр", 18), ("Эталон", 18),
               ("Исполнение", 12), ("Тираж", 9), ("RAL", 14), ("Поверхность", 14), ("Примечание", 30)]
    header_row(ws, 7, columns)
    body_rows(ws, 8, 30, len(columns))
    for col, values in ((3, "СТД,ТИП,НОВ"), (4, "МЕТАЛЛ,КОРПУС,КОМБИ"), (10, "глянец,матовый,шагрень,муар")):
        dv = DataValidation(type="list", formula1=f'"{values}"', allow_blank=True)
        letter = get_column_letter(col)
        dv.add(f"{letter}8:{letter}200")
        ws.add_data_validation(dv)
    dv = DataValidation(type="whole", operator="greaterThan", formula1="0", allow_blank=True)
    dv.add("H8:H200")
    ws.add_data_validation(dv)
    help_ws = wb.create_sheet("Справка")
    rows = [
        ("Столбец", "Что писать", "Кто читает"),
        ("Позиция", "номер строки служебной записки", "К-5 «Выдать в производство»"),
        ("Изделие", "наименование, как у заказчика", "человек"),
        ("Стандартность", "СТД — эталон из 02_БАЗА без изменений; ТИП — эталон с небольшими изменениями; НОВ — новое", "К-5: где искать папку изделия"),
        ("Направление", "МЕТАЛЛ, КОРПУС или КОМБИ (металл + корпус)", "К-5: папка направления"),
        ("Шифр", "шифр изделия в заказе; имя папки изделия", "К-5: сопоставление с папкой"),
        ("Эталон", "шифр эталона в 02_БАЗА (для СТД и ТИП)", "К-8: применяемость эталона"),
        ("Исполнение", "исполнение эталона, например -01", "К-5"),
        ("Тираж", "количество изделий, целое", "К-5: СЗ на производство × тираж"),
        ("RAL", "цвет покрытия, например RAL 7024; пусто — без покрытия", "К-4, К-5: раздел «Покрасить»"),
        ("Поверхность", "глянец, матовый, шагрень, муар", "К-4, К-5"),
        ("Примечание", "свободный текст", "человек"),
        ("", "Столбцы находятся по заголовкам в первых 10 строках: порядок и регистр не важны. Лишние столбцы можно "
             "добавлять. Кнопка может дописать скрытый столбец «Папка» — запомненный ответ сопоставления.", ""),
    ]
    for r, row in enumerate(rows, start=1):
        for c, v in enumerate(row, start=1):
            cell = help_ws.cell(row=r, column=c, value=v)
            cell.font = Font(name=FONT, bold=(r == 1), size=10)
            cell.alignment = Alignment(wrap_text=True, vertical="top")
    for letter, width in (("A", 16), ("B", 70), ("C", 34)):
        help_ws.column_dimensions[letter].width = width
    return wb


def prod_note():
    wb = Workbook()
    ws = wb.active
    ws.title = "Шапка"
    title(ws, "СЗ на производство", [("Заказ:", ""), ("Изделие (шифр):", ""), ("Обозначение:", ""), ("Наименование:", ""),
                                     ("Ревизия:", ""), ("Количество изделий:", ""), ("Дата:", ""), ("Составил:", "")])
    ws["A11"] = ("На изделие — количество на 1 шт. На заказ — разделы изделий умножены на тираж и сложены. Столбец "
                 "«Примечание технолога» кнопка при перезаписи сохраняет по обозначению.")
    ws["A11"].alignment = Alignment(wrap_text=True)
    ws.merge_cells("A11:F13")
    ws.column_dimensions["A"].width = 22
    ws.column_dimensions["B"].width = 40
    note = "Примечание технолога"
    sections = {
        "Заготовить": [("Обозначение", 24), ("Наименование", 30), ("Материал", 40), ("Толщина / профиль", 18),
                       ("Заготовка", 18), ("Кол. на 1 шт", 11), ("Кол. всего", 11), ("Файл ЧПУ", 36), ("Угол реза", 11), (note, 30)],
        "Сварить": [("Обозначение узла", 24), ("Наименование узла", 30), ("Состав", 50), ("Кол. на 1 шт", 11),
                    ("Кол. всего", 11), (note, 30)],
        "Покрасить": [("Обозначение", 24), ("Наименование", 30), ("Покрытие", 24), ("RAL", 12), ("Поверхность", 14),
                      ("Площадь 1 шт, м²", 14), ("Кол. всего", 11), ("Площадь всего, м²", 16), (note, 30)],
        "Комплектация": [("Наименование", 40), ("Обозначение / код", 24), ("Поставщик", 24), ("Кол. на 1 шт", 11),
                         ("Кол. всего", 11), (note, 30)],
        "Списать": [("Материал", 44), ("Ед.", 8), ("Норма без отхода", 16), ("Коэффициент отхода", 16), ("К списанию", 14), (note, 30)],
    }
    for name, columns in sections.items():
        s = wb.create_sheet(name)
        header_row(s, 1, columns, note_columns=(note,))
        body_rows(s, 2, 25, len(columns))
    return wb


def change_log():
    wb = Workbook()
    ws = wb.active
    ws.title = "Изменения"
    title(ws, "Журнал изменений изделия", [("Изделие (шифр):", ""), ("Папка:", "")])
    columns = [("№", 6), ("Ревизия", 9), ("Дата", 12), ("Кто", 20), ("Документ", 34), ("Что изменено", 44), ("Причина", 34),
               ("Код причины", 12), ("Задел", 14), ("Применяемость", 30)]
    header_row(ws, 5, columns)
    body_rows(ws, 6, 30, len(columns))
    dv = DataValidation(type="list", formula1='"использовать,доработать,в брак"', allow_blank=True)
    dv.add("I6:I500")
    ws.add_data_validation(dv)
    ws["A37"] = "Строки дописывают кнопки «Новая ревизия» и «Снимок эталона»; вручную журнал не ведётся."
    ws["A37"].font = Font(name=FONT, italic=True, size=9)
    return wb


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, build in (("СЗ_заказа_макет.xlsx", order_note), ("СЗ_на_производство.xlsx", prod_note), ("Изменения.xlsx", change_log)):
        path = OUT / name
        build().save(path)
        print("записан", path)
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
