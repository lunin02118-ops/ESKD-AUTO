# -*- coding: utf-8 -*-
"""Спайк: запись БЧ в спецификации строго по ГОСТ Р 2.105-2019 п. 7.4–7.5 и рисунку 15 проекта ГОСТ Р 2.109.

Каждая строка записи — отдельная строка таблицы 8 мм; дробь — объединённая ячейка «Наименование» на две строки, черта на
границе строк; «БЧ», позиция, обозначение — в первой строке записи, количество и масса — в последней. Надстройка не
загружается; результат — PDF, снимок и замеры (черта дроби против границы строк, текст внутри своих строк).

    python 09_Тесты/tools/spike_bch_gost.py
"""
import json
import re
import sys
import time
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from eskd_e2e import build, com, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402
import spikes_step0 as sp  # noqa: E402
from spike_bch_dense import FONT, cell_text, put, set_fmt  # noqa: E402

RUN = paths.RUNS / time.strftime("bch_gost_%Y%m%d_%H%M%S")
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
RECORDS = [
    ("ПРТИ.468211.131 Пластина опорная.sldprt",
     "Пластина опорная\n<STACK size=1>Лист Б-ПН-НО-4,0 ГОСТ 19903-2015<OVER>Ст3сп ГОСТ 14637-89</STACK>\n100×200 мм", "0.63 кг"),
    ("ПРТИ.468211.132 Стойка.sldprt",
     "Стойка\n<STACK size=1>Труба 80×80×4,0 ГОСТ 8639-82<OVER>В10 ГОСТ 13663-86</STACK>\nL = 300 мм", "2.86 кг"),
    ("ПРТИ.468211.134 Прокладка.sldprt", "Прокладка\nПаронит ПОН-Б 2 ГОСТ 481-80\n30×20 мм", "1.8 г"),
]
COL_FORMAT, COL_ZONE, COL_POS, COL_DESIGNATION, COL_NAME, COL_QTY, COL_REMARK = range(7)
ROW = 0.008
res = {"run": str(RUN), "font": FONT}


def fmt_cell(ann, r, c, left, middle=True, spacing_mm=4.4, width=1.0):
    put(ann, "CellTextHorizontalJustification", (r, c), 1 if left else 2)
    put(ann, "CellTextVerticalJustification", (r, c), 1 if middle else 0)
    set_fmt(ann, r, c, TypeFaceName=FONT, CharHeight=0.0035, WidthFactor=width, LineSpacing=spacing_mm / 1000.0)


def fit_width(ann, r, c, line):
    """Сжатие ширины 1,0…0,8 для строки записи: самая длинная часть (числитель или знаменатель дроби, иначе сама строка)
    пишется в однострочную ячейку простым текстом; если высота больше 8 мм — строка переносится, ширина уменьшается."""
    parts = re.split(r"<STACK[^>]*>|<OVER>|</STACK>", line)
    plain = max((s.strip() for s in parts if s.strip()), key=len, default="")
    stack_prefix = line.split("<STACK")[0]
    probe = (stack_prefix + plain) if "<STACK" in line and len(plain) > 0 else plain
    put(ann, "Text", (r, c), probe)
    chosen = 0.8
    for wf in (1.0, 0.95, 0.9, 0.85, 0.8):
        set_fmt(ann, r, c, WidthFactor=wf)
        if float(ann.SetRowHeight(r, ROW, 0)) <= ROW + 0.0001:
            chosen = wf
            break
    put(ann, "Text", (r, c), line)
    set_fmt(ann, r, c, WidthFactor=chosen)
    return chosen


def gost_layout(ann, stack_spacing_mm):
    rows, cols = int(ann.RowCount), int(ann.ColumnCount)
    col_width = float(ann.GetColumnWidth(COL_NAME))
    log = []
    for i in range(rows - 1, 0, -1):
        text = cell_text(ann, i, COL_NAME).replace("\r\n", "\n")
        lines = text.split("\n")
        plan = []  # (текст, строк таблицы)
        for line in lines:
            plan.append((line, 2 if "<STACK" in line else 1))
        total = sum(n for _, n in plan)
        qty, remark = cell_text(ann, i, COL_QTY), cell_text(ann, i, COL_REMARK)
        for _ in range(total - 1):
            ann.InsertRow(3, i)  # swTableItemInsertPosition_After
        r = i
        entry = {"row": i, "lines": len(lines), "table_rows": total, "cells": []}
        for line, n in plan:
            for k in range(n):
                ann.SetRowVerticalGap(r + k, 0.0)
                for c in range(cols):
                    fmt_cell(ann, r + k, c, left=c in (COL_DESIGNATION, COL_NAME, COL_REMARK))
                if r + k != i:
                    put(ann, "Text", (r + k, COL_POS), " ")
            wf = fit_width(ann, r, COL_NAME, line)
            if n == 2:
                ok = ann.MergeCells(r, COL_NAME, r + 1, COL_NAME)
                fmt_cell(ann, r, COL_NAME, left=True, middle=True, spacing_mm=stack_spacing_mm, width=wf)
                entry["merged"] = bool(ok)
            for k in range(n):
                ann.SetRowHeight(r + k, ROW, 0)
            entry["cells"].append({"text": line[:40], "rows": n, "width_factor": wf})
            r += n
        last = i + total - 1
        if total > 1:
            put(ann, "Text", (last, COL_QTY), qty)
            put(ann, "Text", (last, COL_REMARK), remark)
            put(ann, "Text", (i, COL_QTY), " ")
            put(ann, "Text", (i, COL_REMARK), " ")
        entry["heights_mm"] = [round(float(ann.GetRowHeight(x)) * 1000, 2) for x in range(i, i + total)]
        log.append(entry)
    # позиции подряд по первым строкам записей (как нумерует SpecEditor в режиме перезаписи ячеек)
    pos = 0
    for x in range(1, int(ann.RowCount)):
        if cell_text(ann, x, COL_FORMAT).strip():
            pos += 1
            put(ann, "Text", (x, COL_POS), str(pos))
    return list(reversed(log))


def measure(pdf, x_from_mm=250, x_to_mm=355):
    """Черты дробей против ближайшей границы строк и слова, выходящие за свою строку 8 мм."""
    import fitz
    page = fitz.open(str(pdf))[0]
    k = page.rect.width / 420.0
    table_lines, bars = set(), []
    for d in page.get_drawings():
        for it in d["items"]:
            if it[0] == "l" and abs(it[1].y - it[2].y) < 0.2:
                x1, x2 = sorted((it[1].x / k, it[2].x / k))
                y = round(it[1].y / k, 2)
                if x1 < 230 and x2 > x_to_mm:
                    table_lines.add(y)
                elif x_from_mm < x1 and x2 - x1 < 62:
                    bars.append(y)
    lines = sorted(table_lines)
    words = [(w[1] / k, w[3] / k, w[4]) for w in page.get_text("words") if x_from_mm < w[0] / k < x_to_mm]
    bar_info = [{"bar": y, "nearest_row_line_mm": round(min((abs(y - L) for L in lines), default=99), 2)} for y in bars]
    # слово считается в своей строке, если его середина по высоте между соседними линиями таблицы или границами 8 мм
    return {"row_lines": lines, "bars": bar_info, "words": len(words)}


def main():
    import fitz
    RUN.mkdir(parents=True)
    (RUN / "work" / "pdf").mkdir(parents=True)
    s = SwSession(RUN / "work", load_eskd=False, use_probe=False)
    try:
        s.start()
        comps = []
        for n, (name, record, remark) in enumerate(RECORDS):
            path = s.workspace_copy(paths.FIXTURES_A / A01, name=name)
            doc = s.open(path)
            build.props(doc, {"Обозначение": name.split(" ")[0], "Наименование": record, "Формат": "БЧ", "Примечание": remark})
            s.save(doc)
            s.close(doc)
            comps.append((path, 0, 0.12 * n, 0))
        comps.append((s.workspace_copy(paths.FIXTURES_A / "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt"), 0.3, 0, 0))
        asm, _ = build.assembly(s, comps)
        asm_path = s.ws("ПРТИ.468211.130 СБ Узел.sldasm")
        s.save_as(asm, asm_path)
        s.close_all()
        res["variants"] = {}
        for spacing in (2.9,):
            key = f"ГОСТ_дробь_{spacing}"
            v = res["variants"][key] = {}
            try:
                s.open(asm_path)
                drw = s.new_doc(paths.DRAWING_TEMPLATE)
                build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
                view = build.model_view(drw, asm_path, 60, 60)
                drw.ActivateView(str(view.GetName2))
                ann = com.dyn(view.InsertBomTable4(False, 0.2, 0.285, 1, 2, "", str(sp.BOM_TEMPLATE), False, 0, False))
                v["layout"] = gost_layout(ann, spacing)
                drw.ForceRebuild3(False)
                pdf = RUN / "work" / "pdf" / f"{key}.pdf"
                v["pdf"] = s.save_as(drw, pdf)
                v["measure"] = measure(pdf)
                page = fitz.open(str(pdf))[0]
                k = page.rect.width / 420.0
                page.get_pixmap(matrix=fitz.Matrix(200 / 72, 200 / 72), clip=fitz.Rect(195 * k, 5 * k, 418 * k, 130 * k)).save(
                    str(RUN / f"{key}.png"))
            except Exception:
                v["error"] = traceback.format_exc()
            s.close_all()
        res["unexpected_dialogs"] = s.watchdog.pop_unexpected()
    except Exception:
        res["error"] = traceback.format_exc()
    finally:
        try:
            s.stop()
        except Exception:
            pass
        (RUN / "result.json").write_text(json.dumps(res, ensure_ascii=False, indent=1, default=str), encoding="utf-8")
        print(RUN)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
