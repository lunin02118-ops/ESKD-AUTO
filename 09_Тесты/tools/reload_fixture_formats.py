# -*- coding: utf-8 -*-
"""Перезагрузка встроенных форматок в чертежах-фикстурах (WP-4.6.4, решение владельца 14.09.2026: «только фикстуры»).

Чертежи A-10, A-11 и копия B-01 (fixtures/B) получают форматки по эталону основной надписи через
ESKD_Sync.exe /reloadformats — копии в каталоге прогона вместе с моделями, проверка повторным пробным прогоном
(ни одной «устарела»), с --apply — замена чертежей в fixtures и SHA-256 в manifest.json. Исходники архива корпуса Б
в 08_Результаты_Тестирования не меняются.

    python 09_Тесты/tools/reload_fixture_formats.py            — копии и отчёт
    python 09_Тесты/tools/reload_fixture_formats.py --apply    — то же и замена фикстур
"""
import csv
import hashlib
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eskd_e2e import paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

RUN = paths.RUNS / time.strftime("reload_fixture_formats_%Y%m%d_%H%M%S")


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def reload(folder, apply, report):
    args = [str(paths.ESKD_SYNC_EXE), "/reloadformats", str(folder), "/report", str(report)] + (["/apply"] if apply else [])
    proc = subprocess.run(args, capture_output=True, timeout=900)
    output = (proc.stdout + proc.stderr).decode("utf-8", errors="replace")
    if proc.returncode != 0:
        raise RuntimeError(f"ESKD_Sync.exe /reloadformats: код {proc.returncode}: {output}")
    with open(report, encoding="utf-8-sig", newline="") as f:
        return output, list(csv.DictReader(f, delimiter=";"))


def main():
    apply = "--apply" in sys.argv
    RUN.mkdir(parents=True)
    work_a = RUN / "work" / "A"
    work_b = RUN / "work" / "B"
    shutil.copytree(paths.FIXTURES_A, work_a)
    shutil.copytree(paths.FIXTURES_B, work_b)
    drawings = {"A-10": (work_a, "ПРТИ.468211.101 Пластина опорная.slddrw"),
                "A-11": (work_a, "ПРТИ.468211.100 СБ Кондуктор сварочный.slddrw"),
                "B-01": (work_b, "ПРТИ.468211.010.SLDDRW")}
    result = {"run": str(RUN)}
    with SwSession(RUN / "work", load_eskd=False, use_probe=False):
        for folder in (work_a, work_b):
            out, rows = reload(folder, True, RUN / f"apply_{folder.name}.csv")
            result[f"apply_{folder.name}"] = {"output": out.strip(), "rows": rows}
            out, rows = reload(folder, False, RUN / f"check_{folder.name}.csv")
            result[f"check_{folder.name}"] = rows
    outdated = [r for key in ("check_A", "check_B") for r in result[key] if r["Состояние"] == "устарела"]
    reloaded = [r for key in ("apply_A", "apply_B") for r in result[key]["rows"] if r["Действие"] == "перезагружена"]
    (RUN / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    print("перезагружено листов:", len(reloaded), "; устаревших после перезагрузки:", len(outdated))
    for r in reloaded:
        print("  ", Path(r["Файл"]).name, r["Лист"], r["Форматка"])
    if outdated:
        print("НЕ ПРИВЕДЕНЫ:", outdated)
        return 1
    if not apply:
        print("Проверка пройдена; для замены фикстур — --apply.", RUN)
        return 0
    manifest = json.loads(paths.FIXTURE_MANIFEST.read_text(encoding="utf-8"))
    for fid, (folder, name) in drawings.items():
        target = (paths.FIXTURES_B if folder == work_b else paths.FIXTURES_A) / name
        shutil.copy2(folder / name, target)
        if fid == "B-01":
            manifest["corpus_b"]["B-01"]["drawing_sha256"] = sha256(target)
        else:
            manifest["fixtures"][fid]["sha256"] = sha256(target)
            manifest["fixtures"][fid]["formats_reloaded"] = "14.09.2026: эталон основной надписи Р-12, шрифт GOST 2.304 type A (tools/reload_fixture_formats.py)"
        print("заменён", target)
    if "B-01" in manifest.get("corpus_b", {}):
        manifest["corpus_b"]["B-01"]["formats_reloaded"] = "14.09.2026: эталон основной надписи Р-12, шрифт GOST 2.304 type A"
    paths.FIXTURE_MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("manifest.json обновлён", RUN)
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
