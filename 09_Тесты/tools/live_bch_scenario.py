# -*- coding: utf-8 -*-
"""Живой сценарий владельца: новая деталь из шаблона → Сохранить → «Деталь БЧ» → Сохранить → сборка → спецификация →
MProp «применить без правок» → Сохранить. Надстройка текущей ветки загружена; всё в каталоге прогона."""
import json, shutil, subprocess, sys, threading, time, traceback
from pathlib import Path
sys.path.insert(0, r"D:\Work\_Инструменты_Конструктора\09_Тесты")
sys.path.insert(0, r"D:\Work\_Инструменты_Конструктора\09_Тесты\tools")
from eskd_e2e import build, com, oracles, paths
from eskd_e2e.session import SwSession
import spikes_step0 as sp

RUN = paths.RUNS / time.strftime("live_bch_%Y%m%d_%H%M%S")
WORK = RUN / "work"
SW = WORK / "SWPlus"
PART = "ПРТИ.468211.120 Пластина опорная.sldprt"
ASM = "ПРТИ.468211.121 СБ Узел проверочный.sldasm"
SHEET4 = "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
KEYS = ("Обозначение", "Наименование", "Наименование_ФБ", "Формат", "Примечание", "Масса_ФБ", "Материал_ФБ",
        "Материал_Таблица", "Сборка1_ФБ", "Сборка2_ФБ", "RenameSWP", "Раздел", "Конструктор")
res = {"run": str(RUN)}


def props(doc):
    d = oracles.dump_properties(doc)
    out = {"общие": {k: d["general"][k]["raw"] for k in KEYS if k in d["general"]}}
    for cfg, p in d["configs"].items():
        out[cfg] = {k: p[k]["raw"] for k in KEYS if k in p}
    return out


def save():
    (RUN / "result.json").write_text(json.dumps(res, ensure_ascii=False, indent=1, default=str), encoding="utf-8")


def run_mprop(s, doc):
    s.activate(doc)
    err = com.ref_int()
    out = {}
    done = threading.Event()

    def killer():
        if not done.wait(120):
            out["timeout"] = True
            subprocess.run(["taskkill", "/PID", str(s.pid), "/F"], capture_output=True)
    threading.Thread(target=killer, daemon=True).start()
    try:
        out["ok"] = bool(s.sw.RunMacro2(str(SW / "MProp" / "MProp.swp"), "MProp_run", "main_reload", 1, err))
        out["err"] = int(err.value)
    except Exception as exc:
        out["exception"] = repr(exc)
    finally:
        done.set()
    out["dialogs"] = s.watchdog.pop_unexpected()
    return out


def spec(s, tag):
    import fitz
    s.open(s.ws(ASM))
    drw = s.new_doc(paths.DRAWING_TEMPLATE)
    build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
    view = build.model_view(drw, s.ws(ASM), 80, 80)
    drw.ActivateView(str(view.GetName2))
    ann = com.dyn(view.InsertBomTable4(False, 0.2, 0.28, 1, 2, "", str(sp.BOM_TEMPLATE), False, 0, False))
    sp.specedit_format(ann)
    ole = ann._oleobj_; did = ole.GetIDsOfNames("Text2")
    cells = [[str(ole.InvokeTypes(did, 0, 2, (8, 0), ((3, 1), (3, 1), (11, 1)), r, c, True)) for c in range(int(ann.ColumnCount))]
             for r in range(int(ann.RowCount))]
    pdf = WORK / "pdf" / f"{tag}.pdf"
    ok, err, warn = s.save_as(drw, pdf)
    page = fitz.open(str(pdf))[0]; k = page.rect.width / 420
    page.get_pixmap(matrix=fitz.Matrix(220 / 72, 220 / 72), clip=fitz.Rect(195 * k, 5 * k, 418 * k, 80 * k)).save(str(RUN / f"{tag}.png"))
    s.close_all()
    return cells


RUN.mkdir(parents=True); (WORK / "pdf").mkdir(parents=True)
for sub in ("MProp", "SpecEditor", "SProp"):
    shutil.copytree(paths.SWPLUS / sub, SW / sub)
s = SwSession(WORK, load_eskd=True)
try:
    s.start()
    doc, _ = build.plate(s, 200, 100, 4, SHEET4)
    res["1_новая_из_шаблона"] = props(doc)
    ok, err, warn = s.save_as(doc, s.ws(PART))
    time.sleep(5)
    res["2_после_сохранить_как"] = props(doc)
    res["3_бч"] = int(com.call(s.eskd(), "ToggleDrawinglessSilent"))
    s.save(doc)
    res["4_после_БЧ_и_сохранить"] = props(doc)
    s.close_all()
    part = s.open(s.ws(PART))
    asm, opened = build.assembly(s, [(s.ws(PART), 0, 0, 0), (s.ws(PART), 0, 0.2, 0)])
    s.save_as(asm, s.ws(ASM))
    time.sleep(5)
    res["5_сборка"] = props(asm)
    s.save(asm)
    s.close_all()
    res["6_спецификация_после_надстройки"] = spec(s, "spec_1_addin")
    save()
    part = s.open(s.ws(PART))
    before = props(part)
    res["7_mprop"] = run_mprop(s, part)
    s.save(part)
    res["8_после_MProp_и_сохранить"] = props(part)
    res["8_изменил_MProp"] = {lvl: {k: [before.get(lvl, {}).get(k), v.get(k)] for k in set(v) | set(before.get(lvl, {}))
                                     if before.get(lvl, {}).get(k) != v.get(k)} for lvl, v in res["8_после_MProp_и_сохранить"].items()}
    s.close_all()
    res["9_спецификация_после_MProp"] = spec(s, "spec_2_after_mprop")
except Exception:
    res["error"] = traceback.format_exc()
finally:
    try:
        s.stop()
    except Exception:
        pass
    save()
    print(RUN)
