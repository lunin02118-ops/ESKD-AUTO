# -*- coding: utf-8 -*-
"""Р-14: шрифт «GOST 2.304 type A» вместо «GOST type A» в надписях форматок, шаблона чертежа, шаблонов Master и форм
SpecEditor, а также в .ini SWPlus (шрифт таблиц SpecEditor и новых форматок Master).

«GOST type A» (ASCON) рисует знак «×» буквой «Ч» и не имеет длинного тире; «GOST 2.304 type A» (05_Шрифты/GOST2304A_1.ttf,
ставится установщиком) — настоящие «×» и «—» (спайк font_x_spike_20260913_225141, решение владельца Р-14).
Правятся копии в каталоге прогона; проверка — матрица К-12 по форматкам листа 1 и шаблону чертежа; с --apply — замена
файлов в репозитории. Высота и интервал надписей не меняются; положение обозначения и ширина наименования формы 1 — по FORM1_ADJUST.

    python 09_Тесты/tools/switch_font.py            — копии и проверка
    python 09_Тесты/tools/switch_font.py --apply    — то же и замена
"""
import json
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eskd_e2e import build, com, paths, stamp_matrix as sm  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

OLD, NEW = "GOST type A", "GOST 2.304 type A"
# Подбор под метрики нового шрифта матрицей К-12 (прогон switch_font_20260914_031146): обозначение 7 мм поднималось на
# 1,4 мм (до верхней линии графы 2 оставалось 0,07 мм) — якорь 56,8 → 55,4; наименование 5 мм в 22 знака шире графы 1
# на 0,07 мм и смещено вправо на 0,7 мм (наклон шрифта) — ширина надписи 0,95 и якорь левее на 0,7 мм. Только форма 1 (есть MYPRP4).
FORM1_ADJUST = {"MYPRP0": {"y": 55.4}, "MYPRP4": {"width": 0.95, "dx": -0.7}}
RUN = paths.RUNS / time.strftime("switch_font_%Y%m%d_%H%M%S")
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
INI = [paths.SWPLUS / "Master" / "Master.ini", paths.SWPLUS / "SpecEditor" / "MyProperties_2.ini",
       paths.SWPLUS / "SpecEditor" / "SpecEditor.ini"]


def targets():
    files = sorted(paths.SHEET_FORMATS.glob("*.slddrt")) + sorted(paths.SPEC_FORMATS.glob("*.slddrt"))
    files += sorted((paths.SWPLUS / "Master").glob("Master_Template_*.SLDDRW"))
    files.append(paths.DRAWING_TEMPLATE)
    return files


def switch_notes(doc):
    """Все надписи форматки каждого листа со шрифтом OLD → NEW; возвращает число изменённых надписей."""
    changed = 0
    for sheet in [str(n) for n in com.as_list(doc.GetSheetNames)]:
        doc.ActivateSheet(sheet)
        doc.EditTemplate()
        try:
            view = com.dyn(doc.GetFirstView)
            note = view.GetFirstNote
            notes = {}
            while note is not None:
                n = com.dyn(note)
                fmt = com.dyn(n.GetTextFormat)
                if str(fmt.TypeFaceName) == OLD:
                    fmt.TypeFaceName = NEW
                    if not n.SetTextFormat(False, fmt._oleobj_ if hasattr(fmt, "_oleobj_") else fmt):
                        raise RuntimeError(f"SetTextFormat {n.GetName}")
                    changed += 1
                notes[str(n.GetName or "")] = n
                note = n.GetNext
            if "MYPRP4" in notes:
                for name, change in FORM1_ADJUST.items():
                    n = notes.get(name)
                    if n is None:
                        continue
                    if "y" in change or "dx" in change:
                        ann = com.dyn(n.GetAnnotation)
                        pos = com.as_list(ann.GetPosition)
                        x = float(pos[0]) + change.get("dx", 0.0) / 1000.0
                        y = change["y"] / 1000.0 if "y" in change else float(pos[1])
                        if not ann.SetPosition2(x, y, 0.0):
                            raise RuntimeError(f"SetPosition2 {name}")
                    if "width" in change:
                        fmt = com.dyn(n.GetTextFormat)
                        fmt.WidthFactor = change["width"]
                        if not n.SetTextFormat(False, fmt._oleobj_ if hasattr(fmt, "_oleobj_") else fmt):
                            raise RuntimeError(f"SetTextFormat {name}")
        finally:
            doc.EditSheet()
    doc.ForceRebuild3(False)
    return changed


def remaining_old(doc):
    left = []
    for sheet in [str(n) for n in com.as_list(doc.GetSheetNames)]:
        doc.ActivateSheet(sheet)
        doc.EditTemplate()
        try:
            note = com.dyn(doc.GetFirstView).GetFirstNote
            while note is not None:
                n = com.dyn(note)
                if str(com.dyn(n.GetTextFormat).TypeFaceName) == OLD:
                    left.append(f"{sheet}/{n.GetName}")
                note = n.GetNext
        finally:
            doc.EditSheet()
    return left


def main():
    apply = "--apply" in sys.argv
    RUN.mkdir(parents=True)
    report = {"run": str(RUN), "files": {}}
    edited = {}
    with SwSession(RUN / "work", load_eskd=False, use_probe=False) as s:
        for src in targets():
            as_drawing = src.suffix.lower() == ".slddrt"
            work = s.workspace_copy(src, subdir="edit/" + src.parent.name, name=(src.stem + ".SLDDRW") if as_drawing else None)
            doc = s.open(work)
            try:
                rec = report["files"][str(src.relative_to(paths.ROOT))] = {"changed": switch_notes(doc)}
                rec["saved"] = list(s.save(doc))
                rec["left"] = remaining_old(doc)
            finally:
                s.close(doc)
            out = RUN / "work" / "edited" / src.parent.name / src.name
            out.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(work, out)
            edited[src] = out
            print(src.name, rec, flush=True)
        model_path = s.workspace_copy(paths.FIXTURES_A / A01)
        model = s.open(model_path)
        drw = s.new_doc(paths.DRAWING_TEMPLATE)
        build.model_view(drw, model_path, 150, 170)
        formats = [edited[f] for f in sorted(paths.SHEET_FORMATS.glob("*.slddrt"))]
        matrix = sm.run(s, drw, model, formats, RUN / "work" / "pdf")
        s.close(drw)
        drw = s.new_doc(edited[paths.DRAWING_TEMPLATE])
        build.model_view(drw, model_path, 150, 170)
        matrix.update(sm.run_current(s, drw, model, 420, RUN / "work" / "pdf", "Чертеж"))
        s.close(drw)
        s.close(model)
    report["problems"] = sm.problems_of(matrix)
    bad_files = {k: v for k, v in report["files"].items() if v["left"] or not v["saved"][0]}
    (RUN / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    (RUN / "stamp_matrix.json").write_text(json.dumps(matrix, ensure_ascii=False, indent=1), encoding="utf-8")
    if report["problems"] or bad_files:
        print("НАРУШЕНИЯ:", json.dumps({"matrix": report["problems"], "files": bad_files}, ensure_ascii=False)[:4000])
        print("Репозиторий не изменён.", RUN)
        return 1
    if apply:
        for src, out in edited.items():
            shutil.copy2(out, src)
        for ini in INI:
            raw = ini.read_bytes().decode("cp1251")
            lines = raw.split("\r\n") if "\r\n" in raw else raw.split("\n")
            if lines[0].strip() == OLD:
                lines[0] = NEW
                ini.write_bytes(("\r\n" if "\r\n" in raw else "\n").join(lines).encode("cp1251"))
        print("Заменено файлов:", len(edited), "и .ini:", len(INI), RUN)
    else:
        print("Проверка пройдена; для замены — --apply.", RUN)
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
