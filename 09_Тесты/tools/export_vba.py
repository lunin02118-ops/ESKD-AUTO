# -*- coding: utf-8 -*-
"""Текстовая выгрузка модулей VBA макросов SWPlus и SHA-256 их .swp (WP-0.2).

Выгрузка лежит в git рядом с макросами: ссылки плана вида «FrmMProp:2850» указывают на строки этих файлов,
а правки макросов видны в diff. Исходные .swp только читаются (oletools).

    python 09_Тесты/tools/export_vba.py          — переписать выгрузку и manifest.json
    python 09_Тесты/tools/export_vba.py --check  — сравнить выгрузку в git с .swp, код выхода 1 при отличии
"""
import hashlib
import json
import re
import sys
from pathlib import Path

from oletools.olevba import VBA_Parser

ROOT = Path(__file__).resolve().parents[2]
SWPLUS = ROOT / "03_Макросы_и_Плагины" / "Макросы_SW_ZTool" / "SWPlusMacro_v_2018_SP0.0"
EXPORT = ROOT / "03_Макросы_и_Плагины" / "Макросы_SW_ZTool" / "_VBA_выгрузка"
MACROS = ("MProp/MProp.swp", "SProp/SProp.swp", "SpecEditor/SpecEditor.swp", "DProp/DProp.swp", "Master/Master.swp",
          "SaveAsPDF/SaveAsPDF.swp", "SaveAsPDF/PDFCreator.swp", "SaveDRW/SaveDRW.swp")


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def extract(swp):
    """{имя файла модуля: текст с переводами строк LF}; первый поток модуля выигрывает (формы дублируются в .swp)."""
    modules = {}
    parser = VBA_Parser(str(swp))
    try:
        for _, _, vba_filename, code in parser.extract_macros():
            if vba_filename.startswith("VBA_P-code"):  # псевдомодуль oletools (дамп p-code), не исходный текст
                continue
            name = re.sub(r"[^\w.\-]+", "_", vba_filename) + ".txt"
            if name in modules:
                continue
            text = code if isinstance(code, str) else code.decode("cp1251", "replace")
            modules[name] = text.replace("\r\n", "\n")
    finally:
        parser.close()
    return modules


def build():
    """Ожидаемое состояние: manifest и файлы выгрузки по текущим .swp."""
    manifest, files = {}, {}
    for rel in MACROS:
        swp = SWPLUS / rel
        modules = extract(swp)
        project = swp.stem
        manifest[rel] = {"sha256": sha256(swp), "modules": {n: t.count("\n") + 1 for n, t in sorted(modules.items())}}
        for name, text in modules.items():
            files[f"{project}/{name}"] = text
    return manifest, files


def check():
    """Список отличий выгрузки в git от .swp (пустой — совпадает)."""
    manifest, files = build()
    problems = []
    stored = json.loads((EXPORT / "manifest.json").read_text(encoding="utf-8"))
    if stored != manifest:
        for rel in MACROS:
            if stored.get(rel) != manifest[rel]:
                problems.append(f"manifest.json: {rel} не совпадает с .swp")
    present = {p.relative_to(EXPORT).as_posix() for p in EXPORT.rglob("*.txt")}
    for rel in sorted(present - set(files)):
        problems.append(f"лишний файл выгрузки {rel}")
    for rel, text in sorted(files.items()):
        path = EXPORT / rel
        if not path.exists():
            problems.append(f"нет файла выгрузки {rel}")
        elif path.read_text(encoding="utf-8").replace("\r\n", "\n") != text:
            problems.append(f"выгрузка {rel} отличается от .swp")
    return problems


def write():
    manifest, files = build()
    for old in EXPORT.rglob("*.txt"):
        old.unlink()
    for rel, text in files.items():
        path = EXPORT / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="\n")
    (EXPORT / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")
    for rel, item in manifest.items():
        print(f"{rel}: {item['sha256']} ({len(item['modules'])} модулей)")


if __name__ == "__main__":
    if "--check" in sys.argv:
        found = check()
        print("\n".join(found) or "выгрузка совпадает с .swp")
        sys.exit(1 if found else 0)
    write()
