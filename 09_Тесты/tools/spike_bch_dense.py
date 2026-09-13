# -*- coding: utf-8 -*-
"""Спайк плотного оформления записей БЧ в спецификации (Р-15): алгоритм для правки SpecEditor, проверка по PDF.

Для каждой строки таблицы: шрифт и выравнивание как у SpecEditor; если запись не помещается в одну строку 8 мм —
межстрочный интервал 3,4 мм (у записи с дробью) или 4,4 мм, подбор сжатия ширины 1,0…0,8 по высоте, которую возвращает
SetRowHeight (перенос строки внутри ячейки увеличивает высоту), и высота k × 8 мм. Надстройка не загружается.

    python 09_Тесты/tools/spike_bch_dense.py
"""
import json
import math
import sys
import time
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from eskd_e2e import build, com, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402
import spikes_step0 as sp  # noqa: E402

RUN = paths.RUNS / time.strftime("bch_dense_%Y%m%d_%H%M%S")
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
FONT = "GOST 2.304 type A"
RECORDS = [
    ("ПРТИ.468211.131 Пластина опорная.sldprt",
     "Пластина опорная\n<STACK size=1>Лист Б-ПН-НО-4,0 ГОСТ 19903-2015<OVER>Ст3сп ГОСТ 14637-89</STACK>\n100×200 мм", "0.63 кг"),
    ("ПРТИ.468211.132 Стойка.sldprt",
     "Стойка\n<STACK size=1>Труба 80×80×4,0 ГОСТ 8639-82<OVER>В10 ГОСТ 13663-86</STACK>\nL = 300 мм", "2.86 кг"),
    ("ПРТИ.468211.133 Кронштейн.sldprt",
     "Кронштейн крепления направляющей\nрамы сварочного кондуктора\n"
     "<STACK size=1>Лист Б-ПН-НО-6,0 ГОСТ 19903-2015<OVER>09Г2С ГОСТ 19281-2014</STACK>\n180×90 мм", "0.76 кг"),
    ("ПРТИ.468211.134 Прокладка.sldprt", "Прокладка\nПаронит ПОН-Б 2 ГОСТ 481-80\n30×20 мм", "1.8 г"),
]
res = {"run": str(RUN), "font": FONT, "rows": []}


def put(obj, name, args, value):
    import pythoncom
    ole = com.dyn(obj)._oleobj_
    ole.Invoke(ole.GetIDsOfNames(name), 0, pythoncom.DISPATCH_PROPERTYPUT, 0, *args, value)


def cell_text(ann, r, c):
    ole = ann._oleobj_
    return str(ole.InvokeTypes(ole.GetIDsOfNames("Text2"), 0, 2, (8, 0), ((3, 1), (3, 1), (11, 1)), r, c, False))


def set_fmt(ann, r, c, **values):
    fmt = com.dyn(ann.GetCellTextFormat(r, c))
    for k, v in values.items():
        setattr(fmt, k, v)
    ann.SetCellTextFormat(r, c, False, fmt._oleobj_ if hasattr(fmt, "_oleobj_") else fmt)


def dense_format(ann, font_mm=3.5, gap_mm=None, stack_spacing_mm=None):
    """gap_mm — вертикальный отступ строки (SetRowVerticalGap), None — как в шаблоне; stack_spacing_mm — интервал у записи
    с дробью, None — как у всех (шаг 8 мм). Перенос определяется сравнением с высотой при временно широкой графе."""
    rows, cols = int(ann.RowCount), int(ann.ColumnCount)
    text_cols = (3, 4, cols - 1)
    col_width = float(ann.GetColumnWidth(4))
    log = []
    for i in range(1, rows):
        if gap_mm is not None:
            ann.SetRowVerticalGap(i, gap_mm / 1000.0)
        for j in range(cols):
            put(ann, "CellTextHorizontalJustification", (i, j), 1 if j in text_cols else 2)
            put(ann, "CellTextVerticalJustification", (i, j), 0)
            set_fmt(ann, i, j, TypeFaceName=FONT, CharHeight=font_mm / 1000.0, WidthFactor=1.0,
                    LineSpacing=(8 - font_mm - 0.1) / 1000.0)
        stack = "<STACK" in cell_text(ann, i, 4)
        if stack and stack_spacing_mm:
            for j in text_cols:
                set_fmt(ann, i, j, LineSpacing=stack_spacing_mm / 1000.0)
        ann.SetColumnWidth(4, 0.4, 0)
        reference = float(ann.SetRowHeight(i, 0.008, 0))
        ann.SetColumnWidth(4, col_width, 0)
        trials, chosen = [], None
        for wf in (1.0, 0.95, 0.9, 0.85, 0.8):
            set_fmt(ann, i, 4, WidthFactor=wf)
            h = float(ann.SetRowHeight(i, 0.008, 0))
            trials.append((wf, round(h * 1000, 2)))
            if abs(h - reference) <= 0.0002:
                chosen = wf
                break
        if chosen is None:
            chosen = 0.8
        set_fmt(ann, i, 4, WidthFactor=chosen)
        need = float(ann.SetRowHeight(i, 0.008, 0))
        k = max(1, math.ceil((need - 0.001) / 0.008))
        ann.SetRowHeight(i, k * 0.008, 0)
        log.append({"row": i, "stack": stack, "reference_mm": round(reference * 1000, 2), "trials": trials,
                    "width_factor": chosen, "need_mm": round(need * 1000, 2), "rows": k,
                    "height_mm": round(float(ann.GetRowHeight(i)) * 1000, 2),
                    "gap_mm": round(float(ann.GetRowVerticalGap(i)) * 1000, 2)})
    return log


def overlaps(pdf, x_from_mm, x_to_mm):
    """Пары слов таблицы с пересечением рамок больше 0,3 мм по обеим осям и слова, пересекающие линии строк."""
    import fitz
    page = fitz.open(str(pdf))[0]
    k = page.rect.width / 420.0
    words = [(w[0] / k, w[1] / k, w[2] / k, w[3] / k, w[4]) for w in page.get_text("words") if x_from_mm < w[0] / k < x_to_mm]
    hlines = sorted({round(it[1].y / k, 2) for d in page.get_drawings() for it in d["items"]
                     if it[0] == "l" and abs(it[1].y - it[2].y) < 0.2 and x_from_mm - 5 < it[1].x / k < x_to_mm})
    bad = []
    for a in range(len(words)):
        for b in range(a + 1, len(words)):
            wa, wb = words[a], words[b]
            dx = min(wa[2], wb[2]) - max(wa[0], wb[0])
            dy = min(wa[3], wb[3]) - max(wa[1], wb[1])
            if dx > 0.3 and dy > 1.0:
                bad.append(f"«{wa[4]}» × «{wb[4]}»")
    crossing = [f"«{w[4]}» через линию {h}" for w in words for h in hlines if w[1] + 0.6 < h < w[3] - 0.6]
    return {"overlaps": bad, "crossing_lines": crossing, "words": len(words)}


def fraction_gaps(pdf, x_from_mm=250, x_to_mm=355):
    """Для каждой дроби: черта (короткая горизонтальная линия внутри графы), низ числителя и верх знаменателя, мм."""
    import fitz
    page = fitz.open(str(pdf))[0]
    k = page.rect.width / 420.0
    words = [(w[0] / k, w[1] / k, w[2] / k, w[3] / k, w[4]) for w in page.get_text("words") if x_from_mm < w[0] / k < x_to_mm]
    bars = []
    for d in page.get_drawings():
        for it in d["items"]:
            if it[0] == "l" and abs(it[1].y - it[2].y) < 0.2:
                x1, x2 = sorted((it[1].x / k, it[2].x / k))
                if x_from_mm < x1 and x2 - x1 < 62:
                    bars.append(round(it[1].y / k, 2))
    out = []
    for y in sorted(set(bars)):
        above = [w for w in words if w[3] <= y + 0.5 and w[3] > y - 6]
        below = [w for w in words if w[1] >= y - 0.5 and w[1] < y + 8]
        num_bottom = max((w[3] for w in above), default=None)
        den_top = min((w[1] for w in below), default=None)
        out.append({"bar_mm": y, "numerator_to_bar_mm": round(y - num_bottom, 2) if num_bottom else None,
                    "bar_to_denominator_mm": round(den_top - y, 2) if den_top else None,
                    "denominator": " ".join(w[4] for w in below if abs(w[1] - den_top) < 1.0) if den_top else None})
    return out


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
        bolt = s.workspace_copy(paths.FIXTURES_A / "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt")
        comps.append((bolt, 0.3, 0, 0))
        asm, opened = build.assembly(s, comps)
        asm_path = s.ws("ПРТИ.468211.130 СБ Узел.sldasm")
        s.save_as(asm, asm_path)
        s.close_all()
        s.open(asm_path)
        drw = s.new_doc(paths.DRAWING_TEMPLATE)
        build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
        view = build.model_view(drw, asm_path, 60, 60)
        drw.ActivateView(str(view.GetName2))
        variants = {"S39_дробь_3.9": (0.0, 3.9), "S29_дробь_2.9": (0.0, 2.9), "S19_дробь_1.9": (0.0, 1.9), "S09_дробь_0.9": (0.0, 0.9)}
        s.close_all()
        res["variants"] = {}
        for key, (gap, spacing) in variants.items():
            s.open(asm_path)
            drw = s.new_doc(paths.DRAWING_TEMPLATE)
            build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
            view = build.model_view(drw, asm_path, 60, 60)
            drw.ActivateView(str(view.GetName2))
            ann = com.dyn(view.InsertBomTable4(False, 0.2, 0.285, 1, 2, "", str(sp.BOM_TEMPLATE), False, 0, False))
            v = res["variants"][key] = {"rows": dense_format(ann, gap_mm=gap, stack_spacing_mm=spacing)}
            drw.ForceRebuild3(False)
            pdf = RUN / "work" / "pdf" / f"{key}.pdf"
            ok, err, warn = s.save_as(drw, pdf)
            v["pdf"] = [ok, err, warn]
            v["check"] = overlaps(pdf, 250, 355)
            v["fractions"] = fraction_gaps(pdf)
            page = fitz.open(str(pdf))[0]
            k = page.rect.width / 420.0
            page.get_pixmap(matrix=fitz.Matrix(200 / 72, 200 / 72), clip=fitz.Rect(195 * k, 5 * k, 418 * k, 160 * k)).save(
                str(RUN / f"{key}.png"))
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
