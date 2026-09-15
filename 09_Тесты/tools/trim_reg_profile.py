# -*- coding: utf-8 -*-
"""Корпоративный профиль SolidWorks — только настройки отдела (решение владельца 15.09.2026, аудит B5).

Установщик перед импортом сбрасывает раздел пользователя SOLIDWORKS <версия>, поэтому профиль — единственный источник
настроек и не должен нести снимок интерфейса конкретного ПК: панели и вкладки, раскладку окон, данные производительности,
последние файлы, графику. Остаются параметры системы и документов, пути, материал, импорт/экспорт, кнопки макросов
SWPlus. Вкладку ЕСКД, кнопки быстрого доступа, видеокарту, надстройки и фамилию пишет сам установщик.

    python 09_Тесты/tools/trim_reg_profile.py            — что будет убрано (пробный прогон)
    python 09_Тесты/tools/trim_reg_profile.py --apply    — запись; прежний профиль — в 08_Результаты_Тестирования/runs/
"""
import re
import shutil
import sys
import time
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROFILE = ROOT / "01_Настройки_SolidWorks" / "Реестровые_Профили" / "01_SW2025_Корпоративный_Стандарт_ЕСКД.reg"
VERSION = "HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025"

# Разделы версии, которые остаются вместе с подразделами: параметры системы и документов, пути, импорт и экспорт.
KEEP_TREE = {
    "Assemblies", "Auto Dimension Drawing", "Auto Dimension Sketch", "AutoFix", "Colors", "Compression", "ContentManager",
    "Convert view to sketch", "Crosshatch", "DesignCheck", "Dimensions", "Direct Edit", "Document Templates", "Drawings",
    "Edges", "eDrawings", "Export Settings", "ExtFolder", "ExtReferences", "Feature Colors", "FeatureWorks",
    "File Utilities", "GhostMissingRefs", "IgesSettings", "ImportSettings", "LineFont", "LineFontWeight", "Material",
    "Menu Customizations", "Page", "Planes", "PlasticsMode", "Reference Triad", "Regeneration", "SheetMetal",
    "SW on ACIS", "TriadConsistency", "User Defined Macros", "Viewpoint", "Weldments",
}
# Разделы, от которых остаются только значения самого раздела (параметры), без подразделов с накопленными данными.
KEEP_ROOT_VALUES = {"General", "Performance", "Hole Wizard"}
# Значения, которые описывают конкретный ПК или сеанс, а не настройку отдела.
DROP_VALUES = re.compile(r'^"(Last Run SolidWorks|Import Electrical Excel names|JumpListFileName|Last user path|'
                         r'AutoCenterMass|EULA Accepted[^"]*|document\d+|Recent[^"]*|Last[^"]*Folder[^"]*)"=', re.I)

SECTION = re.compile(r"^\[(-?)(HKEY_[^\]]+)\]\s*$")


def parse(text):
    """Разделы в порядке файла: (удаление?, ключ, строки значений с продолжениями)."""
    sections, current = [], None
    for line in text.splitlines():
        m = SECTION.match(line)
        if m:
            current = [m.group(1) == "-", m.group(2), []]
            sections.append(current)
        elif current is not None and line.strip():
            current[2].append(line)
    return sections


def value_groups(lines):
    """Значение и его строки-продолжения (hex: … \\)."""
    group = []
    for line in lines:
        if group and group[-1].rstrip().endswith("\\"):
            group.append(line)
            continue
        if group:
            yield group
        group = [line]
    if group:
        yield group


def decide(delete, key):
    if delete:
        return False, "удаление раздела"
    if not key.startswith(VERSION + "\\"):
        return (key == VERSION), "вне раздела версии"
    rest = key[len(VERSION) + 1:]
    top = rest.split("\\", 1)[0]
    if top in KEEP_TREE:
        return True, ""
    if top in KEEP_ROOT_VALUES:
        return ("\\" not in rest), top + ": подраздел"
    return False, top


def main():
    apply = "--apply" in sys.argv
    sys.stdout.reconfigure(encoding="utf-8")
    raw = PROFILE.read_bytes()
    text = raw.decode("utf-16")
    sections = parse(text)
    kept, removed, dropped_values = [], Counter(), Counter()
    for delete, key, lines in sections:
        keep, why = decide(delete, key)
        if not keep:
            removed[why] += 1
            continue
        groups = []
        for g in value_groups(lines):
            if DROP_VALUES.match(g[0]):
                dropped_values[g[0].split("=", 1)[0]] += 1
            else:
                groups.append(g)
        kept.append((key, groups))
    values_before = sum(len(list(value_groups(s[2]))) for s in sections)
    values_after = sum(len(g) for _, g in kept)
    print(f"разделов: было {len(sections)}, осталось {len(kept)}; значений: было {values_before}, осталось {values_after}")
    print("убрано разделов:", ", ".join(f"{k} {n}" for k, n in removed.most_common()))
    print("убрано значений ПК:", ", ".join(f"{k} {n}" for k, n in dropped_values.most_common()))
    out = ["Windows Registry Editor Version 5.00", ""]
    for key, groups in kept:
        out.append(f"[{key}]")
        for g in groups:
            out.extend(g)
        out.append("")
    result = "\r\n".join(out) + "\r\n"
    if not apply:
        print("пробный прогон; запись — ключ --apply")
        return 0
    backup = ROOT / "08_Результаты_Тестирования" / "runs" / ("reg_profile_" + time.strftime("%Y%m%d_%H%M%S"))
    backup.mkdir(parents=True, exist_ok=True)
    shutil.copy2(PROFILE, backup / PROFILE.name)
    PROFILE.write_bytes(b"\xff\xfe" + result.encode("utf-16-le"))
    print(f"записано: {len(result.encode('utf-16-le')) // 1024} КБ; прежний профиль — {backup}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
