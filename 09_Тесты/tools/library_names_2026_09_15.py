# -*- coding: utf-8 -*-
"""Аудит 15.09.2026, C1–C2: имена в дереве библиотеки материалов — по действующим ГОСТ, как уже записаны поля (WP-4.5).

Действующие редакции (решение владельца «сам определи», проверено 15.09.2026): ГОСТ 14637-2024 вместо 14637-89 (с
01.03.2025), ГОСТ 14918-2020 вместо 14918-80 (с 01.12.2020), ГОСТ 16523-97 — действующий, лист до 3,9 мм, ГОСТ 22233-2018.

SolidWorks находит материал модели по имени. Поэтому прежние имена не исчезают: их копии (те же поля, новые matid)
лежат в группе «99. Прежние наименования — не выбирать». Старые модели открываются со своим материалом, а в дереве
конструктор выбирает новое имя. Трубы ГОСТ 8732-78 и марки стали переносятся из группы листового проката в свои группы.
Ограничительный перечень ТРОЯ (CSV) переписывается из библиотеки для строк, которые в нём есть.

    python 09_Тесты/tools/library_names_2026_09_15.py            — пробный прогон
    python 09_Тесты/tools/library_names_2026_09_15.py --apply    — запись; прежние файлы — в 08_Результаты_Тестирования/runs/
"""
import csv
import html
import io
import re
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LIBRARY = ROOT / "04_Библиотеки_Материалов_и_Профилей" / "Библиотека материалов" / "Библиотека_Материалов_ГОСТ.sldmat"
RESTRICTION = ROOT / "06_Документация" / "Библиотека_материалов" / "Ограничительный_перечень_ТРОЯ.csv"
RUNS = ROOT / "08_Результаты_Тестирования" / "runs"

RENAMES = {
    "1001": ("Лист 2,5 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", "Лист 2,5 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97"),
    "1002": ("Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97"),
    "1003": ("Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024"),
    "1107": ("Лист 5,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", "Лист 5,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024"),
    "1108": ("Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", "Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024"),
    "1005": ("Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", "Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024"),
    "1006": ("Лист 10,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", "Лист 10,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024"),
    "1012": ("Лист оцинкованный 0,95 ГОСТ 14918-80 / 08пс", "Лист оцинкованный 0,95 ГОСТ 14918-2020 / 08пс"),
    "1013": ("Лист оцинкованный 1,2 ГОСТ 14918-80 / 08пс", "Лист оцинкованный 1,2 ГОСТ 14918-2020 / 08пс"),
    "1104": ("Профиль АД31Т1 ГОСТ 22233-2001", "Профиль АД31Т1 ГОСТ 22233-2018"),
}
LEGACY_CLASS = "99. Прежние наименования — не выбирать (открытие старых моделей)"
LEGACY_MATID_START = 1201
MOVES = {
    "16. Трубы стальные бесшовные горячедеформированные (ГОСТ 8732-78)": ("1109", "1110"),
    "17. Сталь сортовая и поковки — марки без сортамента": ("1111", "1112", "1113", "1114"),
}
CLASS_RENAMES = {"15. Алюминиевый прокат и профили (ГОСТ 22233-2001)": "15. Алюминиевый прокат и профили (ГОСТ 22233-2018)"}

CLASS = re.compile(r'(\t*)<classification name="([^"]*)">\r?\n(.*?)\r?\n\1</classification>', re.S)
MATERIAL = re.compile(r'(\t*)<material name="([^"]*)" description="[^"]*" matid="(\d+)".*?</material>', re.S)
PROP = re.compile(r'<prop name="([^"]*)" description="[^"]*" value="([^"]*)"')
XHATCH = re.compile(r'<xhatch name="([^"]*)"')


def attr(text):
    return html.escape(text, quote=True)


def transform(text):
    changes = []
    nl = "\r\n" if "\r\n" in text else "\n"
    blocks = {}  # matid -> (класс, текст материала)
    for cls in CLASS.finditer(text):
        for mat in MATERIAL.finditer(cls.group(3)):
            blocks[mat.group(3)] = (cls.group(2), mat.group(0))
    missing = [m for m in list(RENAMES) + [x for ids in MOVES.values() for x in ids] if m not in blocks]
    if missing:
        raise SystemExit(f"в библиотеке нет matid {missing}")
    if any(html.unescape(blocks[m][1].split('name="', 1)[1].split('"', 1)[0]) != old for m, (old, _) in RENAMES.items()):
        raise SystemExit("имена не совпадают с ожидаемыми прежними — библиотека уже изменена?")
    if LEGACY_CLASS in text:
        raise SystemExit("группа прежних наименований уже есть — повторный прогон не нужен")

    legacy = []
    for i, (matid, (old, new)) in enumerate(RENAMES.items()):
        block = blocks[matid][1]
        renamed = block.replace(f'<material name="{attr(old)}"', f'<material name="{attr(new)}"', 1)
        text = text.replace(block, renamed, 1)
        blocks[matid] = (blocks[matid][0], renamed)
        copy = block.replace(f'matid="{matid}"', f'matid="{LEGACY_MATID_START + i}"', 1)
        legacy.append(copy)
        changes.append(f"{matid}: «{old}» → «{new}»; прежнее имя — копия matid {LEGACY_MATID_START + i}")

    # переносы в новые группы
    indent_cls = re.search(r'(\t*)<classification name=', text).group(1)
    moved_classes = []
    for cls_name, ids in MOVES.items():
        mats = []
        for matid in ids:
            block = blocks[matid][1]
            text = re.sub(r"\r?\n?" + re.escape(block), "", text, count=1)
            mats.append(block)
            changes.append(f"{matid}: из «{blocks[matid][0]}» в «{cls_name}»")
        moved_classes.append((cls_name, mats))
    moved_classes.append((LEGACY_CLASS, legacy))

    last_close = text.rindex("</classification>") + len("</classification>")
    addition = "".join(f"{nl}{indent_cls}<classification name=\"{attr(name)}\">{nl}" + nl.join(mats) + f"{nl}{indent_cls}</classification>"
                       for name, mats in moved_classes)
    text = text[:last_close] + addition + text[last_close:]

    for old, new in CLASS_RENAMES.items():
        if f'<classification name="{attr(old)}">' in text:
            text = text.replace(f'<classification name="{attr(old)}">', f'<classification name="{attr(new)}">', 1)
            changes.append(f"группа «{old}» → «{new}»")
    return text, changes


def restriction_rows(library_text):
    """Строки перечня из библиотеки: те же столбцы, что у файла ТРОЯ."""
    by_name = {}
    for cls in CLASS.finditer(library_text):
        if cls.group(2) == LEGACY_CLASS:
            continue
        for mat in MATERIAL.finditer(cls.group(3)):
            props = {k: html.unescape(v) for k, v in PROP.findall(mat.group(0))}
            line = props.get("Обозначение_Строка", "")
            numerator, _, denominator = line.partition(" / ")
            hatch = XHATCH.search(mat.group(0))
            by_name[mat.group(3)] = {
                "Категория": html.unescape(cls.group(2)), "Имя_в_дереве": html.unescape(mat.group(2)),
                "Числитель_Сортамент": numerator, "Знаменатель_Материал": denominator,
                "Типоразмер": props.get("Типоразмер", ""), "ГОСТ_Сортамент": props.get("ГОСТ_Сортамент", ""),
                "Марка_Материала": props.get("Марка_Материала", ""), "ГОСТ_Материал": props.get("ГОСТ_Материал", ""),
                "Штриховка": html.unescape(hatch.group(1)) if hatch else "",
            }
    return by_name


def main():
    apply = "--apply" in sys.argv
    sys.stdout.reconfigure(encoding="utf-8")
    raw = LIBRARY.read_bytes()
    text = raw.decode("utf-16")
    new_text, changes = transform(text)
    print("\n".join(changes))

    old_names = {old: matid for matid, (old, _) in RENAMES.items()}
    lib_rows = restriction_rows(new_text)
    old_rows = {html.unescape(m.group(2)): m.group(3) for cls in CLASS.finditer(text) for m in MATERIAL.finditer(cls.group(3))}
    csv_text = RESTRICTION.read_bytes().decode("utf-8-sig")
    reader = csv.DictReader(io.StringIO(csv_text), delimiter=";")
    fields = reader.fieldnames
    out_rows, csv_changes = [], 0
    for row in reader:
        matid = old_names.get(row["Имя_в_дереве"]) or old_rows.get(row["Имя_в_дереве"])
        fresh = lib_rows.get(matid) if matid else None
        if fresh is None:
            out_rows.append(row)
            continue
        if any(row[k] != fresh[k] for k in fields):
            csv_changes += 1
        out_rows.append({k: fresh[k] for k in fields})
    print(f"перечень ТРОЯ: строк {len(out_rows)}, обновлено {csv_changes}")

    if not apply:
        print("пробный прогон; запись — ключ --apply")
        return 0
    backup = RUNS / ("library_names_" + time.strftime("%Y%m%d_%H%M%S"))
    backup.mkdir(parents=True, exist_ok=True)
    shutil.copy2(LIBRARY, backup / LIBRARY.name)
    shutil.copy2(RESTRICTION, backup / RESTRICTION.name)
    LIBRARY.write_bytes(b"\xff\xfe" + new_text.encode("utf-16-le") if raw.startswith(b"\xff\xfe") else new_text.encode("utf-16"))
    buf = io.StringIO(newline="")
    writer = csv.DictWriter(buf, fieldnames=fields, delimiter=";", lineterminator="\r\n")
    writer.writeheader()
    writer.writerows(out_rows)
    RESTRICTION.write_bytes(b"\xef\xbb\xbf" + buf.getvalue().encode("utf-8"))
    print(f"записано; прежние файлы — {backup}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
