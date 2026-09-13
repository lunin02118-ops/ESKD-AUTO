# -*- coding: utf-8 -*-
"""Сравнение снимков поведения надстройки (golden master).

    python 09_Тесты/baseline/compare.py baseline/v5 <каталог снимка v6>            # все различия
    python 09_Тесты/baseline/compare.py baseline/v5 <снимок> --rules allowed_diffs.json   # только не объяснённые

Снимок сводится к фактам «сценарий · раздел · уровень · имя → значение»: свойства в памяти после открытия и на
диске после сохранения, признак изменения и записи свойств по журналу зонда, тексты заметок основной надписи,
смещение заметок, ошибки журнала надстройки и неожиданные диалоги. Различие объяснено, если подходит под правило
из allowed_diffs.json; правило ссылается на дефект, который это различие устраняет. Правило без
срабатываний тоже провал, если у него нет поля «optional» с причиной (Д-43).
"""
import argparse
import fnmatch
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROPERTY_SECTIONS = ("after_open.memory", "persisted", "persisted_new", "persisted_original", "persisted_drawing",
                     "persisted_model", "persisted_model_after_open", "memory_after_saveas", "memory_after_save",
                     "toggle_on.memory", "toggle_off.memory", "drawing_memory")


def _get(snapshot, dotted):
    node = snapshot
    for part in dotted.split("."):
        if not isinstance(node, dict) or part not in node:
            return None
        node = node[part]
    return node


def facts(snapshot):
    """{(раздел, уровень, имя): значение} — уровень «общие», имя конфигурации или лист чертежа."""
    out = {}
    for section in PROPERTY_SECTIONS:
        dump = _get(snapshot, section)
        if not isinstance(dump, dict) or "general" not in dump:
            continue
        for name, item in dump["general"].items():
            out[(section, "общие", name)] = item.get("raw")
        for cfg, props in dump.get("configs", {}).items():
            for name, item in props.items():
                out[(section, cfg, name)] = item.get("raw")
    for sheet, notes in (_get(snapshot, "stamp") or {}).items():
        for note, item in notes.items():
            out[("stamp", sheet, note)] = (item.get("text") or "").replace("\r\n", "\n")
    for dotted in ("after_open.dirty", "dirty_after_saveas", "dirty_after_save", "save.ok", "save_as.ok",
                   "toggle_on.result", "toggle_off.result"):
        value = _get(snapshot, dotted)
        if value is not None:
            out[(dotted, "", "")] = value
    for dotted in ("after_open.property_writes", "save.property_writes"):
        value = _get(snapshot, dotted)
        if isinstance(value, list):
            out[(dotted, "", "")] = len(value)
        elif value is not None:
            out[(dotted, "", "")] = value
    drift = _get(snapshot, "save.max_drift_mm")
    if drift is not None:
        out[("save.max_drift_mm", "", "")] = round(float(drift), 3)
    out[("addin_log_errors", "", "")] = len(snapshot.get("addin_log_errors") or [])
    out[("unexpected_dialogs", "", "")] = len(snapshot.get("unexpected_dialogs") or [])
    return out


def load(directory):
    return {p.stem: json.loads(p.read_text(encoding="utf-8")) for p in sorted(Path(directory).glob("*.json"))
            if not p.name.startswith("_")}


def diffs(old_dir, new_dir):
    """Различия по сценариям, которые есть в обоих снимках: [{scenario, section, level, name, old, new}]."""
    old, new = load(old_dir), load(new_dir)
    out = []
    for scenario in sorted(set(old) & set(new)):
        a, b = facts(old[scenario]), facts(new[scenario])
        for key in sorted(set(a) | set(b), key=lambda k: tuple(str(x) for x in k)):
            if a.get(key) != b.get(key):
                section, level, name = key
                out.append({"scenario": scenario, "section": section, "level": level, "name": name,
                            "old": a.get(key), "new": b.get(key)})
    missing = sorted(set(old) - set(new))
    return out, missing


def _change(d):
    if d["old"] is None:
        return "added"
    if d["new"] is None:
        return "removed"
    return "changed"


def matches(rule, d):
    if not fnmatch.fnmatchcase(d["scenario"], rule.get("scenario", "*")):
        return False
    if not any(fnmatch.fnmatchcase(d["section"], s) for s in rule.get("sections", ["*"])):
        return False
    level = rule.get("level", "any")
    if level == "general" and d["level"] != "общие" or level == "config" and d["level"] in ("общие", ""):
        return False
    if level not in ("any", "general", "config") and not fnmatch.fnmatchcase(d["level"], level):
        return False
    if "names" in rule and not any(fnmatch.fnmatchcase(d["name"], n) for n in rule["names"]):
        return False
    if rule.get("change", "any") not in ("any", _change(d)):
        return False
    for side in ("old", "new"):
        if side in rule and not re.fullmatch(rule[side], "" if d[side] is None else str(d[side]), flags=re.S):
            return False
    return True


def describe(d):
    return f"{d['scenario']} · {d['section']} · {d['level']} · {d['name']}: {d['old']!r} → {d['new']!r}"


def explain(all_diffs, rules):
    unexplained, used = [], {r["id"]: 0 for r in rules}
    for d in all_diffs:
        hit = [r["id"] for r in rules if matches(r, d)]
        for rid in hit:
            used[rid] += 1
        if not hit:
            unexplained.append(d)
    return unexplained, used


def unused(used, rules):
    """Правила без единого срабатывания и без поля «optional» с причиной (Д-43): правило, которому нечего объяснять,
    устарело или шире своего дефекта — R01 проваливается."""
    optional = {r["id"] for r in rules if str(r.get("optional") or "").strip()}
    return sorted(rid for rid, n in used.items() if n == 0 and rid not in optional)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("old")
    ap.add_argument("new")
    ap.add_argument("--rules", default=None)
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    all_diffs, missing = diffs(args.old, args.new)
    shown, stale = all_diffs, []
    if args.rules:
        rules = json.loads(Path(args.rules).read_text(encoding="utf-8"))["rules"]
        shown, used = explain(all_diffs, rules)
        for rid, n in used.items():
            print(f"{rid}: {n}")
        stale = unused(used, rules)
    for d in shown:
        print(describe(d))
    for rid in stale:
        print(f"правило без срабатываний: {rid}")
    print(f"различий: {len(all_diffs)}, показано: {len(shown)}, сценариев нет в новом снимке: {missing}")
    return 1 if shown or missing or stale else 0


if __name__ == "__main__":
    sys.exit(main())
