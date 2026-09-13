# -*- coding: utf-8 -*-
"""Снимок поведения надстройки (golden master) на корпусах А и Б.

    python 09_Тесты/baseline/capture.py v5                 # до рефакторинга, DLL из репозитория
    python 09_Тесты/baseline/capture.py v6 --dll <путь>     # после, для сравнения

Каждый сценарий работает с копиями фикстур и записывает JSON: состояние документа после
открытия (признак изменения, записи свойств по журналу зонда, свойства в памяти) и после
сохранения (свойства на диске, прочитанные без надстройки), для чертежей — тексты штампа.
Сравнение снимков — baseline/compare.py.
"""
import argparse
import json
import re
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from eskd_e2e import build, com, oracles, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

A = paths.FIXTURES_A
F = {
    "A01": "ПРТИ.468211.101 Пластина опорная.sldprt",
    "A02": "ПРТИ.468211.102 Стойка.sldprt",
    "A03": "ПРТИ.468211.103 Планка.sldprt",
    "A04": "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt",
    "A05": "Электродвигатель АИР71А4.sldprt",
    "A06": "ПРТИ.468211.104 Кронштейн направляющий удлинённый.sldprt",
    "A07": "ПРТИ.468211.105 Рама сварная.sldprt",
    "A08": "ПРТИ.468211.110 СБ Узел опоры.sldasm",
    "A09": "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm",
    "A10": "ПРТИ.468211.101 Пластина опорная.slddrw",
    "A11": "ПРТИ.468211.100 СБ Кондуктор сварочный.slddrw",
    "A13": "ПРТИ.468211.106 Крышка.sldprt",
}
SHEET4 = "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
DATE_RE = re.compile(r"\b(\d{2}\.\d{2}\.\d{2,4}|\d{4}-\d{2}-\d{2})\b")


def normalize(obj, run_dir):
    root = str(run_dir)
    if isinstance(obj, dict):
        return {k: normalize(v, run_dir) for k, v in obj.items()}
    if isinstance(obj, list):
        return [normalize(v, run_dir) for v in obj]
    if isinstance(obj, str):
        s = obj.replace(root, "<RUN>").replace(root.replace("\\", "/"), "<RUN>")
        return DATE_RE.sub("<ДАТА>", s)
    return obj


def copy_set(s, case, *names, source=A):
    out = []
    for n in names:
        out.append(s.workspace_copy(Path(source) / n, subdir=case))
    return out


def after_open(s, doc, mark):
    return {
        "dirty": bool(doc.GetSaveFlag),
        "property_writes": sorted({(e["event"], e.get("name", ""), e.get("cfg", "")) for e in s.journal.property_writes(mark)}),
        "memory": oracles.dump_properties(doc),
    }


# --------------------------------------------------------------------------- сценарии
def sc_open_only(name, source=A):
    def run(s, case):
        path, = copy_set(s, case, name, source=source)
        mark = s.mark(case + ":open")
        doc = s.open(path)
        time.sleep(1.0)
        snap = {"after_open": after_open(s, doc, mark)}
        s.close(doc)
        return snap
    return run


def sc_open_save(name, source=A, extra=()):
    def run(s, case):
        paths_ = copy_set(s, case, name, *extra, source=source)
        path = paths_[0]
        mark = s.mark(case + ":open")
        doc = s.open(path)
        time.sleep(1.0)
        snap = {"after_open": after_open(s, doc, mark)}
        mark = s.mark(case + ":save")
        ok, err, warn = s.save(doc)
        snap["save"] = {"ok": ok, "err": err, "property_writes": len(s.journal.property_writes(mark))}
        s.close(doc)
        snap["persisted"] = oracles.read_persisted(s, path)
        return snap
    return run


def sc_new_part_api_saveas(s, case):
    doc, _ = build.plate(s, 200, 100, 4, SHEET4)
    target = s.ws(case, "ПРТИ.468211.121 Пластина новая.sldprt")
    mark = s.mark(case + ":saveas")
    ok, err, _ = s.save_as(doc, target)
    snap = {"save": {"ok": ok, "err": err, "property_writes": len(s.journal.property_writes(mark))},
            "memory_after_saveas": oracles.dump_properties(doc), "dirty_after_saveas": bool(doc.GetSaveFlag)}
    s.close(doc)
    snap["persisted"] = oracles.read_persisted(s, target)
    return snap


def sc_new_part_ui_save(s, case):
    doc, _ = build.plate(s, 200, 100, 4, SHEET4)
    target = s.ws(case, "ПРТИ.468211.123 Пластина через диалог.sldprt")
    s.ui_save_as(doc, target, command=2)
    snap = {"memory_after_save": oracles.dump_properties(doc), "dirty_after_save": bool(doc.GetSaveFlag)}
    s.close(doc)
    snap["persisted"] = oracles.read_persisted(s, target)
    return snap


def sc_saveas_rename(s, case):
    path, = copy_set(s, case, F["A01"])
    doc = s.open(path)
    s.save(doc)
    target = s.ws(case, "ПРТИ.468211.122 Пластина копия.sldprt")
    ok, err, _ = s.save_as(doc, target)
    snap = {"save_as": {"ok": ok, "err": err}, "memory_after_saveas": oracles.dump_properties(doc)}
    s.close(doc)
    snap["persisted_new"] = oracles.read_persisted(s, target)
    snap["persisted_original"] = oracles.read_persisted(s, path)
    return snap


def sc_bch_toggle(s, case):
    path, = copy_set(s, case, F["A02"])
    doc = s.open(path)
    s.activate(doc)
    addin = s.eskd()
    snap = {}
    r1 = com.call(addin, "ToggleDrawinglessSilent")
    s.save(doc)
    snap["toggle_on"] = {"result": r1, "memory": oracles.dump_properties(doc)}
    r2 = com.call(addin, "ToggleDrawinglessSilent")
    s.save(doc)
    snap["toggle_off"] = {"result": r2, "memory": oracles.dump_properties(doc)}
    s.close(doc)
    snap["persisted"] = oracles.read_persisted(s, path)
    return snap


def sc_drawing(drawing, model, extra=()):
    def run(s, case):
        dpath, mpath, *_ = copy_set(s, case, drawing, model, *extra)
        mark = s.mark(case + ":open")
        drw = s.open(dpath)
        time.sleep(2.0)
        snap = {"after_open": {"dirty": bool(drw.GetSaveFlag),
                               "property_writes": sorted({(e["event"], e.get("name", ""), e.get("title", ""))
                                                          for e in s.journal.property_writes(mark)})},
                "stamp": {sheet: {n: {"text": v["text"], "linked": v["linked"]} for n, v in notes.items()
                                  if n.startswith("MYPRP") or v["text"].startswith("Файл")}
                          for sheet, notes in oracles.stamp(drw).items()},
                "drawing_memory": oracles.dump_properties(drw)}
        before = oracles.note_positions(drw)
        mark = s.mark(case + ":save")
        ok, err, _ = s.save(drw)
        after = oracles.note_positions(drw)
        worst, moved = oracles.max_drift(before, after)
        snap["save"] = {"ok": ok, "err": err, "max_drift_mm": worst, "moved": moved,
                        "property_writes": sorted({(e["event"], e.get("name", ""), e.get("title", ""))
                                                   for e in s.journal.property_writes(mark)})}
        s.close_all()
        snap["persisted_drawing"] = oracles.read_persisted(s, dpath)
        snap["persisted_model"] = oracles.read_persisted(s, mpath)
        return snap
    return run


A08_PARTS = (F["A01"], F["A04"])
A09_PARTS = (F["A08"], F["A01"], F["A04"], F["A02"], F["A03"], F["A05"], F["A06"], F["A07"])
B01 = paths.CORPUS_B["B-01"]
B02 = paths.CORPUS_B["B-02"]
B03 = paths.CORPUS_B["B-03"]

SCENARIOS = [
    ("A01_open", sc_open_only(F["A01"])),
    ("A01_open_save", sc_open_save(F["A01"])),
    ("A02_open_save", sc_open_save(F["A02"])),
    ("A03_open_save", sc_open_save(F["A03"])),
    ("A04_open_save", sc_open_save(F["A04"])),
    ("A05_open_save", sc_open_save(F["A05"])),
    ("A06_open_save", sc_open_save(F["A06"])),
    ("A07_open_save", sc_open_save(F["A07"])),
    ("A13_open_save", sc_open_save(F["A13"])),
    ("A08_open_save", sc_open_save(F["A08"], extra=A08_PARTS)),
    ("A09_open_save", sc_open_save(F["A09"], extra=A09_PARTS)),
    ("new_part_api_saveas", sc_new_part_api_saveas),
    ("new_part_ui_save", sc_new_part_ui_save),
    ("A01_saveas_rename", sc_saveas_rename),
    ("A02_bch_toggle", sc_bch_toggle),
    ("A10_drawing", sc_drawing(F["A10"], F["A01"])),
    ("A11_drawing", sc_drawing(F["A11"], F["A09"], extra=A09_PARTS)),
    ("B01_part_open_save", sc_open_save(B01[0].name, source=B01[0].parent)),
    ("B01_drawing", None),
    ("B02_assembly_open_save", sc_open_save(B02[1].name, source=B02[1].parent, extra=(B02[0].name,))),
    ("B03_multibody_open_save", sc_open_save(B03[0].name, source=B03[0].parent)),
]


def sc_b01_drawing(s, case):
    dpath = s.workspace_copy(B01[1], subdir=case)
    mpath = s.workspace_copy(B01[0], subdir=case)
    return sc_drawing_paths(s, case, dpath, mpath)


def sc_drawing_paths(s, case, dpath, mpath):
    mark = s.mark(case + ":open")
    drw = s.open(dpath)
    time.sleep(2.0)
    snap = {"after_open": {"dirty": bool(drw.GetSaveFlag),
                           "property_writes": sorted({(e["event"], e.get("name", ""), e.get("title", ""))
                                                      for e in s.journal.property_writes(mark)})},
            "stamp": {sheet: {n: {"text": v["text"], "linked": v["linked"]} for n, v in notes.items()
                              if n.startswith("MYPRP") or v["text"].startswith("Файл")}
                      for sheet, notes in oracles.stamp(drw).items()}}
    s.close_all()
    snap["persisted_model_after_open"] = oracles.read_persisted(s, mpath)
    return snap


SCENARIOS = [(n, sc_b01_drawing if n == "B01_drawing" else f) for n, f in SCENARIOS]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("label", help="имя снимка: v5, v6 …")
    ap.add_argument("--dll", default=None)
    ap.add_argument("-k", dest="only", default="")
    ap.add_argument("--out", default=None, help="каталог снимка (по умолчанию baseline/<label>)")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    out_dir = Path(args.out) if args.out else HERE / args.label
    out_dir.mkdir(parents=True, exist_ok=True)
    run_dir = paths.RUNS / f"baseline_{args.label}_{time.strftime('%Y%m%d_%H%M%S')}"
    failures = []
    crashes = []
    session = None
    meta = {"label": args.label, "captured": time.strftime("%Y-%m-%d %H:%M:%S")}
    try:
        for name, fn in SCENARIOS:
            if args.only and args.only not in name:
                continue
            if session is None or not session.alive():
                if session is not None:
                    crashes.append(name)
                    session.stop()
                # Каждая сессия — отдельный каталог: зонд нельзя переиспользовать после падения.
                session = SwSession(run_dir / f"s{len(crashes)}", load_eskd=True, eskd_dll=args.dll).start()
                meta.update({"sw_revision": str(session.sw.RevisionNumber()), "dll": str(session.eskd_dll)})
            print(f"-- {name}", flush=True)
            try:
                log = oracles.AddinLog()
                snap = fn(session, name)
                snap["addin_log_errors"] = log.errors()
                snap["unexpected_dialogs"] = session.watchdog.pop_unexpected()
                snap = normalize(snap, session.run_dir)
                (out_dir / f"{name}.json").write_text(json.dumps(snap, ensure_ascii=False, indent=1, sort_keys=True),
                                                     encoding="utf-8")
            except Exception as exc:
                failures.append((name, repr(exc)))
                print(f"   ОШИБКА: {exc!r}", flush=True)
                try:
                    if session.alive():
                        session.close_all()
                except Exception:
                    pass
    finally:
        if session is not None:
            session.stop()
    meta["failures"] = failures
    meta["solidworks_crashes_before"] = crashes
    (out_dir / "_meta.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1), encoding="utf-8")
    print("готово; ошибок:", len(failures), "перезапусков SolidWorks:", len(crashes))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
