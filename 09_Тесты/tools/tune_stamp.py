# -*- coding: utf-8 -*-
"""WP-4.6: подбор положения и интервала надписей штампа по матрице К-12 (eskd_e2e/stamp_matrix.py).

Надписи форматки листа 1 правятся в чертеже (EditTemplate, SetPosition2, SetTextFormat) — копия в каталоге прогона,
надстройка не загружается. Для каждого варианта геометрии прогоняется последовательность состояний матрицы (дважды —
положение массы не должно зависеть от предыдущего состояния, Н-32), замеры — по PDF.

    python 09_Тесты/tools/tune_stamp.py spike            — варианты на A3-A-1, результат в runs/tune_stamp_*/result.json
"""
import json
import sys
import time
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eskd_e2e import build, com, oracles, paths, stamp_matrix as sm  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

RUN = paths.RUNS / time.strftime("tune_stamp_%Y%m%d_%H%M%S")
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
TUNED = ("MYPRP4", "MYPRP3", "MYPRP15", "MYPRP16")


def format_notes(drw):
    """{имя: заметка} надписей форматки текущего листа (заметки берутся в режиме правки форматки)."""
    view = com.dyn(drw.GetFirstView)
    out = {}
    note = view.GetFirstNote
    while note is not None:
        n = com.dyn(note)
        out[str(n.GetName or "")] = n
        note = n.GetNext
    return out


def read_geometry(drw):
    notes = format_notes(drw)
    geo = {}
    for name in TUNED:
        n = notes.get(name)
        if n is None:
            continue
        ann = com.dyn(n.GetAnnotation)
        pos = com.as_list(ann.GetPosition)
        fmt = com.dyn(n.GetTextFormat)
        geo[name] = {"x": round(float(pos[0]) * 1000, 3), "y": round(float(pos[1]) * 1000, 3),
                     "line_spacing": round(float(fmt.LineSpacing) * 1000, 3), "char_height": round(float(fmt.CharHeight) * 1000, 3),
                     "font": str(fmt.TypeFaceName)}
    return geo


def set_geometry(drw, changes):
    """changes: {имя: {"y": мм, "line_spacing": мм}} — остальное как было."""
    drw.EditTemplate()
    try:
        notes = format_notes(drw)
        for name, change in changes.items():
            n = notes[name]
            ann = com.dyn(n.GetAnnotation)
            pos = com.as_list(ann.GetPosition)
            if "y" in change:
                if not ann.SetPosition2(float(pos[0]), change["y"] / 1000.0, 0.0):
                    raise RuntimeError(f"SetPosition2 {name}")
            if "line_spacing" in change:
                fmt = com.dyn(n.GetTextFormat)
                fmt.LineSpacing = change["line_spacing"] / 1000.0
                if not n.SetTextFormat(False, fmt._oleobj_ if hasattr(fmt, "_oleobj_") else fmt):
                    raise RuntimeError(f"SetTextFormat {name}")
    finally:
        drw.EditSheet()
    drw.ForceRebuild3(False)


def sequence(session, drw, model, width, pdf_dir, tag):
    """Состояния матрицы дважды подряд: замеры и разброс положения каждой графы между повторами."""
    runs = []
    for rep in range(2):
        for state in sm.STATES:
            sm.apply_state(model, state)
            drw.ForceRebuild3(False)
            pdf = pdf_dir / f"{tag}__{rep}__{state[0]}.pdf"
            session.save_as(drw, pdf)
            measured, problems = sm.check(sm.pdf_words(pdf, width), width, False, state[1])
            runs.append({"rep": rep, "state": state[0], "stamp": measured, "problems": problems})
    spread = {}
    for key in ("g1_title", "g1_doc", "g3", "g5"):
        by_state = {}
        for r in runs:
            if key in r["stamp"]:
                by_state.setdefault(r["state"], []).append(r["stamp"][key]["v_offset"])
        spread[key] = max((max(v) - min(v) for v in by_state.values()), default=0.0)
    return runs, spread


def spike():
    RUN.mkdir(parents=True)
    result = {"run": str(RUN), "variants": {}}
    variants = {
        "G0_текущая": {},
        "G2": {"MYPRP4": {"y": 44.5}, "MYPRP3": {"y": 25.5}, "MYPRP15": {"y": 38.0}},
        "G2_масса_интервал_0": {"MYPRP4": {"y": 44.5}, "MYPRP3": {"y": 25.5}, "MYPRP15": {"y": 38.0, "line_spacing": 0.0}},
        "G2_масса_интервал_1": {"MYPRP4": {"y": 44.5}, "MYPRP3": {"y": 25.5}, "MYPRP15": {"y": 38.0, "line_spacing": 1.0}},
        "G2_масса_интервал_2": {"MYPRP4": {"y": 44.5}, "MYPRP3": {"y": 25.5}, "MYPRP15": {"y": 38.0, "line_spacing": 2.0}},
    }
    with SwSession(RUN / "work", load_eskd=False, use_probe=False) as s:
        model_path = s.workspace_copy(paths.FIXTURES_A / A01)
        model = s.open(model_path)
        fmt = paths.SHEET_FORMATS / "A3-A-1.slddrt"
        for name, changes in variants.items():
            rec = result["variants"][name] = {}
            try:
                drw = s.new_doc(paths.DRAWING_TEMPLATE)
                build.set_sheet_format(drw, fmt, 420, 297)
                build.model_view(drw, model_path, 150, 170)
                drw.EditTemplate()
                rec["before"] = read_geometry(drw)
                drw.EditSheet()
                set_geometry(drw, changes)
                drw.EditTemplate()
                rec["after"] = read_geometry(drw)
                drw.EditSheet()
                runs, spread = sequence(s, drw, model, 420, RUN / "work" / "pdf", name)
                rec["spread"] = spread
                rec["problems"] = {f"{r['rep']}/{r['state']}": r["problems"] for r in runs if r["problems"]}
                rec["runs"] = runs
                s.close(drw)
            except Exception:
                rec["error"] = traceback.format_exc()
            (RUN / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
            print(name, rec.get("spread"), len(rec.get("problems", {})), rec.get("error", "")[:300], flush=True)
    print(RUN)


def fraction_spike():
    """Дробь графы 3 после материала строкой: смещение вправо остаётся после перестроения и после переоткрытия?"""
    RUN.mkdir(parents=True)
    out = {}
    fraction = [s for s in sm.STATES if s[0] == "деталь_1стр_кг_дробь"][0]
    line = [s for s in sm.STATES if s[0] == "деталь_2стр_г_строка"][0]
    pdf_dir = RUN / "work" / "pdf"

    def measure(tag, drw):
        pdf = pdf_dir / f"{tag}.pdf"
        s.save_as(drw, pdf)
        rec, problems = sm.check(sm.pdf_words(pdf, 420), 420, False, False)
        out[tag] = {"g3": rec.get("g3"), "problems": problems}
        print(tag, rec.get("g3"), problems, flush=True)

    with SwSession(RUN / "work", load_eskd=False, use_probe=False) as s:
        model_path = s.workspace_copy(paths.FIXTURES_A / A01)
        model = s.open(model_path)
        drw = s.new_doc(paths.DRAWING_TEMPLATE)
        build.set_sheet_format(drw, paths.SHEET_FORMATS / "A3-A-1.slddrt", 420, 297)
        build.model_view(drw, model_path, 150, 170)
        drawing_path = RUN / "work" / "fraction.SLDDRW"
        s.save_as(drw, drawing_path)
        sm.apply_state(model, fraction)
        drw.ForceRebuild3(False)
        measure("1_дробь", drw)
        sm.apply_state(model, line)
        drw.ForceRebuild3(False)
        measure("2_строка", drw)
        sm.apply_state(model, fraction)
        drw.ForceRebuild3(False)
        measure("3_дробь_после_строки", drw)
        drw.ForceRebuild3(True)
        drw.GraphicsRedraw2()
        measure("4_после_полного_перестроения", drw)
        s.save(model)
        s.save(drw)
        s.close(drw)
        drw = s.open(drawing_path)
        drw.ForceRebuild3(False)
        measure("5_после_переоткрытия_чертежа", drw)
    (RUN / "result.json").write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")


# Эталон Р-12 (формы 1, мм над низом листа; одинаков для всех форматок — штамп привязан к правому нижнему углу):
# спайк tune_stamp_20260914_015952 — G2 и интервал массы 2 мм, все состояния К-12 без нарушений на A3-A-1;
# «Сборочный чертеж» поднят с 25,5 до 26,0 (у нижней линии графы 1 было 0,08 мм).
ETALON = {"MYPRP4": {"y": 44.5}, "MYPRP3": {"y": 26.0}, "MYPRP15": {"y": 38.0, "line_spacing": 2.0}}


def apply_etalon(argv):
    """Эталон во все форматки листа 1 «Основные надписи»: правка копий, проверка матрицей К-12 по копиям, с --apply —
    замена файлов в репозитории (прежние — в каталоге прогона)."""
    import shutil
    RUN.mkdir(parents=True)
    formats = sorted(paths.SHEET_FORMATS.glob("*-1.slddrt"))
    report = {"run": str(RUN), "etalon": ETALON, "formats": {}}
    edited = RUN / "work" / "edited"
    with SwSession(RUN / "work", load_eskd=False, use_probe=False) as s:
        for fmt in formats:
            rec = report["formats"][fmt.name] = {}
            s.workspace_copy(fmt, subdir="original")
            work = s.workspace_copy(fmt, subdir="edit", name=fmt.stem + ".SLDDRW")
            drw = s.open(work)
            try:
                drw.EditTemplate()
                rec["before"] = read_geometry(drw)
                drw.EditSheet()
                set_geometry(drw, ETALON)
                drw.EditTemplate()
                rec["after"] = read_geometry(drw)
                drw.EditSheet()
                ok, err, warn = s.save(drw)
                rec["saved"] = [ok, err, warn]
            finally:
                s.close(drw)
            edited.mkdir(parents=True, exist_ok=True)
            shutil.copy2(work, edited / fmt.name)
        model_path = s.workspace_copy(paths.FIXTURES_A / A01)
        model = s.open(model_path)
        drw = s.new_doc(paths.DRAWING_TEMPLATE)
        build.model_view(drw, model_path, 150, 170)
        matrix = sm.run(s, drw, model, sorted(edited.glob("*.slddrt")), RUN / "work" / "pdf")
        s.close(drw)
        s.close(model)
    report["problems"] = sm.problems_of(matrix)
    (RUN / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    (RUN / "stamp_matrix.json").write_text(json.dumps(matrix, ensure_ascii=False, indent=1), encoding="utf-8")
    print(json.dumps({k: v.get("after") for k, v in report["formats"].items()}, ensure_ascii=False)[:1500])
    if report["problems"]:
        print("НАРУШЕНИЯ:", json.dumps(report["problems"], ensure_ascii=False)[:3000])
        print("Репозиторий не изменён.", RUN)
        return 1
    if "--apply" in argv:
        for fmt in formats:
            shutil.copy2(edited / fmt.name, fmt)
        print("Заменено форматок:", len(formats), RUN)
    else:
        print("Матрица К-12 чистая на копиях; для замены — --apply.", RUN)
    return 0


def apply_embedded(argv):
    """Эталон во встроенную форматку шаблона «Чертеж.drwdot» (WP-4.6.3) и шаблона Master листа 1 (WP-4.1): правка копий,
    проверка матрицей К-12 на чертеже из правленой копии, с --apply — замена в репозитории."""
    import shutil
    RUN.mkdir(parents=True)
    targets = [paths.DRAWING_TEMPLATE, paths.SWPLUS / "Master" / "Master_Template_Sheet1.SLDDRW"]
    report = {"run": str(RUN), "etalon": ETALON, "targets": {}}
    edited = {}
    with SwSession(RUN / "work", load_eskd=False, use_probe=False) as s:
        model_path = s.workspace_copy(paths.FIXTURES_A / A01)
        for src in targets:
            rec = report["targets"][src.name] = {}
            s.workspace_copy(src, subdir="original")
            work = s.workspace_copy(src, subdir="edit")
            doc = s.open(work)
            try:
                doc.EditTemplate()
                rec["before"] = read_geometry(doc)
                doc.EditSheet()
                set_geometry(doc, ETALON)
                doc.EditTemplate()
                rec["after"] = read_geometry(doc)
                doc.EditSheet()
                rec["saved"] = list(s.save(doc))
            finally:
                s.close(doc)
            edited[src] = work
        model = s.open(model_path)
        matrix = {}
        for src, work in edited.items():
            if src.suffix.lower() == ".drwdot":
                drw = s.new_doc(work)
            else:
                check_copy = s.workspace_copy(work, subdir="check")
                drw = s.open(check_copy)
            sheet = com.dyn(drw.GetCurrentSheet)
            width = round(float(com.as_list(sheet.GetProperties2)[5]) * 1000)
            build.model_view(drw, model_path, width * 0.35, 170 if width > 300 else 200)
            matrix.update(sm.run_current(s, drw, model, width, RUN / "work" / "pdf", src.stem))
            s.close(drw)
        s.close(model)
    problems = {f"{tag} / {state}": rec["problems"] for tag, states in matrix.items() for state, rec in states.items()
                if rec["problems"]}
    report["problems"] = problems
    (RUN / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    (RUN / "stamp_matrix.json").write_text(json.dumps(matrix, ensure_ascii=False, indent=1), encoding="utf-8")
    print(json.dumps({k: v.get("after") for k, v in report["targets"].items()}, ensure_ascii=False)[:1200])
    if problems:
        print("НАРУШЕНИЯ:", json.dumps(problems, ensure_ascii=False)[:3000])
        print("Репозиторий не изменён.", RUN)
        return 1
    if "--apply" in argv:
        for src, work in edited.items():
            shutil.copy2(work, src)
        print("Заменено:", [p.name for p in edited], RUN)
    else:
        print("Матрица К-12 чистая; для замены — --apply.", RUN)
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    if sys.argv[1:] == ["spike"]:
        spike()
    elif sys.argv[1:] == ["fraction"]:
        fraction_spike()
    elif sys.argv[1:2] == ["embedded"]:
        sys.exit(apply_embedded(sys.argv[2:]))
    elif sys.argv[1:2] == ["apply"]:
        sys.exit(apply_etalon(sys.argv[2:]))
