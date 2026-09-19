# -*- coding: utf-8 -*-
"""Все правки ЕСКД в макросах SWPlus одной командой — из исходного SWPlus, в записанном порядке (аудит 19.09, М-К2).

Макросы в репозитории и на NAS — уже правленые; рабочие места получают их установщиком как есть, ничего не
накладывая. Этот скрипт нужен тому, кто меняет правки или ставит другой выпуск SWPlus: каждая правка описана
в своём файле swplus_*_edits.py (было → стало), здесь — только их порядок.

    python 09_Тесты/tools/swplus_apply_all.py                  — проверка: исходный SWPlus + все правки = макросы в репозитории
    python 09_Тесты/tools/swplus_apply_all.py --apply          — пересобрать .swp из исходного SWPlus и обновить выгрузку
    python 09_Тесты/tools/swplus_apply_all.py --source <папка> — исходный SWPlus из папки (новый выпуск), а не из git

Исходный SWPlus по умолчанию — из git (коммит ORIGINAL, первая запись макросов в репозиторий). Правка, которая
не находит своего места в новом выпуске, останавливает сборку с именем модуля и текстом «было».
"""
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import export_vba  # noqa: E402
import swplus_audit_edits  # noqa: E402
import swplus_d64_edits  # noqa: E402
import swplus_spec_anchor_edits  # noqa: E402
import swplus_step4_edits  # noqa: E402
import swplus_sw2025_edits  # noqa: E402
import swplus_wp43_edits  # noqa: E402
import swplus_z3_edits  # noqa: E402
import swplus_z9_edits  # noqa: E402
import vba_patch  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
SWPLUS = export_vba.SWPLUS
ORIGINAL = "11a6d98"

# Порядок важен: поздние правки ищут текст, который оставили ранние.
STEPS = (
    ("SW2025 (2652461)", swplus_sw2025_edits.EDITS),
    ("шаг 4, WP-3.x", {rel: swplus_step4_edits.with_helpers(e) for rel, e in swplus_step4_edits.EDITS.items()}),
    ("WP-4.3", swplus_wp43_edits.EDITS),
    ("З-9", swplus_z9_edits.EDITS),
    ("З-10", swplus_spec_anchor_edits.EDITS),
    ("З-3", swplus_z3_edits.EDITS),
    ("Д-64", swplus_d64_edits.EDITS),
    ("аудит 19.09 (М-В2…М-В6)", swplus_audit_edits.EDITS),
)


def macros():
    """Пути .swp (относительно папки SWPlus), которые правит хотя бы один шаг."""
    found = []
    for _, edits in STEPS:
        found += [rel for rel in edits if rel not in found]
    return found


def original(rel, source, work):
    """Файл исходного макроса: из папки source или из git (коммит ORIGINAL)."""
    if source:
        return Path(source) / rel
    path = work / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    git = SWPLUS.relative_to(ROOT).as_posix() + "/" + rel
    data = subprocess.run(["git", "-C", str(ROOT), "show", f"{ORIGINAL}:{git}"], capture_output=True, check=True).stdout
    path.write_bytes(data)
    return path


def build(source, work):
    """{rel: (путь исходного .swp, {модуль: текст после всех правок})}."""
    result = {}
    for rel in macros():
        src = original(rel, source, work)
        texts = vba_patch.read_modules(src)[4]
        for title, edits in STEPS:
            if rel in edits:
                texts = vba_patch.apply_edits(texts, edits[rel], f"{rel} [{title}]: ")
        result[rel] = (src, texts)
    return result


def check(source=None):
    """Отличия макросов в репозитории от «исходный SWPlus + все правки» (пустой список — совпадают)."""
    with tempfile.TemporaryDirectory() as work:
        built = build(source, Path(work))
    problems = []
    for rel, (_, texts) in built.items():
        current = vba_patch.read_modules(SWPLUS / rel)[4]
        for module in sorted(set(texts) | set(current)):
            if texts.get(module) != current.get(module):
                problems.append(f"{rel}/{module}: макрос в репозитории не равен исходному SWPlus с правками")
    return problems


def apply(source=None):
    with tempfile.TemporaryDirectory() as work:
        for rel, (src, texts) in build(source, Path(work)).items():
            target = SWPLUS / rel
            tmp = target.with_name(target.stem + ".сборка.swp")
            vba_patch.write_texts(src, tmp, texts)
            tmp.replace(target)
            print(f"{rel}: собран")
    export_vba.write()


def main(argv):
    source = argv[argv.index("--source") + 1] if "--source" in argv else None
    if "--apply" in argv:
        apply(source)
        return 0
    problems = check(source)
    print("\n".join(problems) or "макросы = исходный SWPlus + все правки ЕСКД")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
