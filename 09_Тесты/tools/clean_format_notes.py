# -*- coding: utf-8 -*-
"""WP-2.4 / Д-17: служебная заметка «Файл: $PRP:"SW- Имя файла(File Name)"» из дистрибутива SWPlus в форматках.

В основных надписях репозитория заметка уже пустая (« »); в форматках SpecEditor (SP-1 и др.) она печатает имя
файла на листе спецификации. Инструмент очищает её так же — текст « ». SolidWorks без надстройки правит копию
форматки в каталоге прогона (EditTemplate → SetText → EditSheet → ISheet.SaveFormat), затем сохранённая форматка
загружается заново и сверяется с исходной: те же заметки с теми же текстами и координатами (кроме очищенных),
те же привязки таблиц и число элементов эскиза рамки. В репозиторий файлы копируются только при полном совпадении
и только с ключом --apply.

    python tools/clean_format_notes.py            # проверка
    python tools/clean_format_notes.py --apply    # замена форматок
"""
import argparse
import json
import re
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eskd_e2e import com, oracles, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

SERVICE_NOTE = re.compile(r"^\s*Файл\b")
SIZES = {"A0": (1189, 841), "A1": (841, 594), "A2": (594, 420), "A3": (420, 297), "A4": (297, 210)}


def formats():
    items = []
    for fmt in sorted(paths.SHEET_FORMATS.glob("*.slddrt")) + sorted((paths.SWPLUS / "SpecEditor").glob("*.slddrt")):
        m = re.match(r"^(A\d)-([AP])-", fmt.name)
        if m:
            long_side, short_side = SIZES[m.group(1)]
            size = (long_side, short_side) if m.group(2) == "A" else (short_side, long_side)
        else:
            size = (210, 297)  # формы SpecEditor — А4 книжный
        items.append((fmt, size))
    return items


def load(session, fmt, size):
    drw = session.new_doc(paths.DRAWING_TEMPLATE)
    sheet = com.dyn(drw.GetCurrentSheet)
    if not drw.SetupSheet5(str(sheet.GetName), 12, 12, 1.0, 1.0, True, str(fmt), size[0] / 1000.0, size[1] / 1000.0,
                           "По умолчанию", True):
        session.close(drw)
        raise RuntimeError(f"форматка не применена: {fmt}")
    drw.ForceRebuild3(False)
    return drw


def snapshot(drw):
    sheet = com.dyn(drw.GetCurrentSheet)
    notes = {}
    for note in oracles.notes_of_view(com.dyn(drw.GetFirstView)):
        notes[note["name"]] = {"text": note.get("linked") or note["text"],
                               "position": [round(v, 3) for v in note.get("position_mm", [])]}
    anchors = {}
    for kind in range(12):
        try:
            anchor = sheet.TableAnchor(kind)
            if anchor is not None:
                anchors[kind] = [round(v * 1000.0, 3) for v in com.as_list(com.dyn(anchor).Position)]
        except Exception:
            pass
    sketch = com.dyn(sheet.GetTemplateSketch)
    segments = len(com.as_list(sketch.GetSketchSegments)) if sketch is not None else 0
    return {"notes": notes, "anchors": anchors, "segments": segments}


def clean(session, fmt, size, target):
    drw = load(session, fmt, size)
    try:
        before = snapshot(drw)
        service = [name for name, n in before["notes"].items() if SERVICE_NOTE.match(n["text"])]
        if not service:
            return before, service, None
        drw.EditTemplate()
        note = com.dyn(drw.GetFirstView).GetFirstNote
        while note is not None:
            note = com.dyn(note)
            if str(note.GetName) in service and not note.SetText(" "):
                raise RuntimeError(f"{fmt.name}: SetText не выполнен для {note.GetName}")
            note = note.GetNext
        drw.EditSheet()
        if not com.dyn(drw.GetCurrentSheet).SaveFormat(str(target)):
            raise RuntimeError(f"{fmt.name}: SaveFormat не выполнен")
        return before, service, target
    finally:
        session.close(drw)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--apply", action="store_true", help="заменить форматки в репозитории проверенными копиями")
    args = ap.parse_args()
    run = paths.RUNS / ("format_notes_" + time.strftime("%Y%m%d_%H%M%S"))
    report = {}
    with SwSession(run, load_eskd=False, use_probe=False) as session:
        for fmt, size in formats():
            source = session.workspace_copy(fmt, subdir="original")
            target = run / "cleaned" / fmt.name
            target.parent.mkdir(parents=True, exist_ok=True)
            before, service, saved = clean(session, source, size, target)
            item = {"source": str(fmt), "service_notes": service, "changed": bool(saved)}
            if saved:
                drw = load(session, saved, size)
                after = json.loads(json.dumps(snapshot(drw)))  # ключи привязок — строки, как у ожидаемого состояния
                session.close(drw)
                expected = json.loads(json.dumps(before))
                for name in service:
                    expected["notes"][name]["text"] = " "
                item["match"] = after == expected
                if not item["match"]:
                    item["expected"], item["actual"] = expected, after
                item["cleaned"] = str(saved)
            report[fmt.name] = item
    (run / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    changed = {name: item for name, item in report.items() if item["changed"]}
    for name, item in report.items():
        status = ("OK " if item.get("match") else "РАСХОЖДЕНИЕ") if item["changed"] else "без служебной заметки"
        print(f"{status:22} {name} {item['service_notes']}")
    if any(not item.get("match") for item in changed.values()):
        print(f"Форматки в репозитории не изменены. Подробности: {run / 'report.json'}")
        return 1
    if args.apply:
        for item in changed.values():
            shutil.copy2(item["cleaned"], item["source"])
            print("заменена", item["source"])
    else:
        print(f"Проверка пройдена ({len(changed)} форматок к очистке); для замены запустите с --apply. Каталог: {run}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
