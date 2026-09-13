# -*- coding: utf-8 -*-
"""Спайки шага 0 плана согласования с SWPlus: S-2 (дробь в таблице BOM), S-6 (свойства форматки в чертеже),
S-8 (CustomPropertyView листа). Надстройка не загружается; все файлы — копии в каталоге прогона.

    python 09_Тесты/tools/spikes_step0.py   (SolidWorks должен быть закрыт)
Результат: 08_Результаты_Тестирования/runs/spikes_step0_*/result.json и снимки PDF.
"""
import json
import sys
import time
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eskd_e2e import build, com, oracles, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

RUN = paths.RUNS / time.strftime("spikes_step0_%Y%m%d_%H%M%S")
OWNER_DRAWING = (paths.RUNS / "20260913_143501" / "work" /
                 "test_R04_apply_removes_aliases_moves_signatures_and_is_idempotent" / "Болт М6-6gх20.58 ГОСТ 7798-70.SLDDRW")
BOM_TEMPLATE = paths.SWPLUS / "SpecEditor" / "SpecEditor_sp.sldbomtbt"
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A04 = "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt"
A08 = "ПРТИ.468211.110 СБ Узел опоры.sldasm"
A10 = "ПРТИ.468211.101 Пластина опорная.slddrw"
A11 = "ПРТИ.468211.100 СБ Кондуктор сварочный.slddrw"
BCH_RECORD = ("Пластина опорная\n<STACK size=1>Лист Б-ПН-НО 4 ГОСТ 19903-2015<OVER>Ст3сп ГОСТ 14637-2024</STACK>\n"
              "L = 200 мм")
result = {"run": str(RUN)}


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def save():
    (RUN / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=1, default=str), encoding="utf-8")


def sheet_views(drw):
    out = {}
    for name in com.as_list(drw.GetSheetNames):
        drw.ActivateSheet(str(name))
        sheet = com.dyn(drw.GetCurrentSheet)
        views = []
        view = com.dyn(drw.GetFirstView)
        view = view.GetNextView if view is not None else None
        while view is not None:
            view = com.dyn(view)
            views.append(str(view.GetName2))
            view = view.GetNextView
        out[str(name)] = {"CustomPropertyView": str(sheet.CustomPropertyView), "template": str(sheet.GetTemplateName),
                          "views": views}
    return out


def s8(s):
    rec = {}
    for label, source in (("owner_bolt", OWNER_DRAWING), ("A-10", paths.FIXTURES_A / A10), ("A-11", paths.FIXTURES_A / A11)):
        try:
            if not source.exists():
                rec[label] = "нет файла"
                continue
            sub = f"s8/{label}"
            if label == "owner_bolt":
                s.workspace_copy(source.with_name(A04), subdir=sub)
            elif label == "A-10":
                s.workspace_copy(paths.FIXTURES_A / A01, subdir=sub)
            drw = s.open(s.workspace_copy(source, subdir=sub), readonly=True)
            rec[label] = sheet_views(drw)
            s.close_all()
        except Exception:
            rec[label] = traceback.format_exc()
            s.close_all()
    try:
        drw = s.new_doc(paths.DRAWING_TEMPLATE)
        rec["new_from_template"] = sheet_views(drw)
        build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
        rec["after_SetupSheet5_По_умолчанию"] = sheet_views(drw)
        s.close_all()
    except Exception:
        rec["new_from_template"] = traceback.format_exc()
        s.close_all()
    result["S-8"] = rec


def s6(s):
    rec = {}
    try:
        drw = s.new_doc(paths.DRAWING_TEMPLATE)
        rec["template_general"] = com.prop_names(drw.Extension.CustomPropertyManager(""))
        build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
        rec["after_setup_A3"] = com.prop_names(drw.Extension.CustomPropertyManager(""))
        build.add_sheet(drw, "Лист2", build.sheet_format("A4-P-2"), 210, 297)
        rec["after_new_sheet_A4"] = com.prop_names(drw.Extension.CustomPropertyManager(""))
        build.set_sheet_format(drw, build.sheet_format("A2-A-1"), 594, 420, "Лист2")
        rec["after_replace_A2"] = com.prop_names(drw.Extension.CustomPropertyManager(""))
        s.close_all()
    except Exception:
        rec["error"] = traceback.format_exc()
        s.close_all()
    result["S-6"] = rec


def s2(s):
    import fitz
    rec = {}
    try:
        for name in (A01, A04, A08):
            s.workspace_copy(paths.FIXTURES_A / name, subdir="s2")
        part = s.open(s.ws("s2", A01))
        build.props(part, {"Наименование": BCH_RECORD, "Обозначение": "", "Формат": "БЧ", "Примечание": "0.63 кг"})
        s.save(part)
        asm = s.open(s.ws("s2", A08))
        drw = s.new_doc(paths.DRAWING_TEMPLATE)
        build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
        view = build.model_view(drw, s.ws("s2", A08), 150, 150)
        drw.ActivateView(str(view.GetName2))
        # InsertBomTable4(якорь, X, Y, привязка слева сверху=1, только верхний уровень=2, конфигурация, шаблон, скрыт, нумерация, вырезы)
        bom = view.InsertBomTable4(False, 0.25, 0.25, 1, 2, "", str(BOM_TEMPLATE), False, 0, False)
        rec["bom_inserted"] = bom is not None
        if bom is not None:
            ann = com.dyn(bom)  # InsertBomTable4 возвращает BomTableAnnotation — это и есть ITableAnnotation
            rows, cols = int(ann.RowCount), int(ann.ColumnCount)
            cells = []
            for r in range(rows):
                row = []
                for c in range(cols):
                    try:
                        row.append(str(ann._oleobj_.InvokeTypes(ann._oleobj_.GetIDsOfNames("Text2"), 0, 2, (8, 0), ((3, 1), (3, 1), (11, 1)), r, c, True)))
                    except Exception as exc:
                        row.append(f"<{exc!r}>")
                cells.append(row)
            rec["cells"] = cells
        drw.ForceRebuild3(False)
        pdf = RUN / "work" / "pdf" / "S2_bom.pdf"
        ok, err, warn = s.save_as(drw, pdf)
        rec["pdf"] = [ok, err, warn]
        if ok:
            page = fitz.open(str(pdf))[0]
            clip = fitz.Rect(0, 0, page.rect.width, page.rect.height * 0.6)
            page.get_pixmap(matrix=fitz.Matrix(150 / 72, 150 / 72), clip=clip).save(str(RUN / "S2_bom.png"))
            rec["pdf_text"] = page.get_text()[:3000]
        s.close_all()
    except Exception:
        rec["error"] = traceback.format_exc()
        s.close_all()
    result["S-2"] = rec


def main():
    RUN.mkdir(parents=True)
    (RUN / "work" / "pdf").mkdir(parents=True)
    s = SwSession(RUN / "work", load_eskd=False)
    try:
        s.start()
        chosen = [f for f in (s8, s6, s2) if not sys.argv[1:] or f.__name__ in sys.argv[1:]]
        for fn in chosen:
            log("спайк", fn.__name__)
            fn(s)
            save()
        result["unexpected_dialogs"] = s.watchdog.pop_unexpected()
    except Exception:
        result["error"] = traceback.format_exc()
    finally:
        try:
            s.stop()
        except Exception:
            pass
        save()
        log("готово", RUN)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
