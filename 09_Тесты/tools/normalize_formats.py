# -*- coding: utf-8 -*-
"""WP-4.2: форматки SWPlus, формы SpecEditor и шаблоны Master без пользовательских свойств v5 и личных данных.

Форматка переносит свои пользовательские свойства в чертёж при добавлении листа (спайк S-6): алиасы v5 и фамилии
попадали в каждый новый лист. Штамп читает реквизиты модели через $PRPSHEET, собственные свойства форматке не нужны.
SolidWorks работает только с копиями в каталоге прогона и без надстройки; в репозиторий файлы копируются после выхода
из SolidWorks, только если у всех копий на диске не осталось лишних свойств, и только с ключом --apply.

    python tools/normalize_formats.py            # опись и проверка на копиях, репозиторий не меняется
    python tools/normalize_formats.py --apply    # то же и замена файлов в репозитории
"""
import argparse
import hashlib
import json
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eskd_e2e import com, oracles, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

# Служебное свойство SolidWorks, которое он пишет сам.
KEEP = {"SWFormatSize"}


def targets():
    files = sorted(paths.SHEET_FORMATS.glob("*.slddrt"))
    files += sorted(paths.SPEC_FORMATS.glob("*.slddrt"))
    files += sorted((paths.SWPLUS / "Master").glob("Master_Template_*.SLDDRW"))
    return files


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def levels(dump):
    out = {"": {n: i["raw"] for n, i in dump["general"].items()}}
    for cfg, props in dump["configs"].items():
        out[cfg] = {n: i["raw"] for n, i in props.items()}
    return out


def clean(session, path):
    doc = session.open(path)
    removed = []
    try:
        names = [""] + [str(c) for c in com.as_list(doc.GetConfigurationNames)]
        for level in names:
            cpm = doc.Extension.CustomPropertyManager(level)
            for name in com.prop_names(cpm):
                if name in KEEP:
                    continue
                value = com.prop_get(cpm, name)[0]
                rc = int(cpm.Delete2(name))
                if rc != 0:
                    raise RuntimeError(f"{path.name} [{level or 'общие'}] Delete2({name}) = {rc}")
                removed.append({"level": level or "общие", "name": name, "value": value})
        ok, err, warn = session.save(doc)
        if not ok:
            raise RuntimeError(f"{path.name}: Save3 errors={err} warnings={warn}")
    finally:
        session.close(doc)
    return removed


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--apply", action="store_true", help="заменить файлы в репозитории проверенными копиями")
    args = ap.parse_args()
    run = paths.RUNS / ("formats_clean_" + time.strftime("%Y%m%d_%H%M%S"))
    report = {}
    with SwSession(run, load_eskd=False, use_probe=False) as session:
        for source in targets():
            rel = source.relative_to(paths.SWPLUS).as_posix()
            session.workspace_copy(source, subdir="original/" + source.parent.name)
            # .slddrt SolidWorks открывает только как чертёж: как Master (FrmMaster_Pref:159–162) — переименование на время правки
            as_drawing = source.suffix.lower() == ".slddrt"
            work = session.workspace_copy(source, subdir="clean/" + source.parent.name,
                                          name=(source.stem + ".SLDDRW") if as_drawing else None)
            item = report[rel] = {"work": str(work), "as_drawing": as_drawing, "sha256_before": sha256(source)}
            try:
                item["removed"] = clean(session, work)
                persisted = levels(oracles.read_persisted(session, work))
                item["left"] = {lvl: sorted(set(v) - KEEP) for lvl, v in persisted.items() if set(v) - KEEP}
            except Exception as exc:
                item["error"] = repr(exc)
    (run / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    bad = {k: v.get("error") or v.get("left") for k, v in report.items() if v.get("error") or v.get("left")}
    for rel, item in report.items():
        names = sorted({r["name"] for r in item.get("removed", [])})
        print(f"{rel}: удалено {len(item.get('removed', []))} {names}")
    if bad:
        print("НЕ ОЧИЩЕНЫ:", json.dumps(bad, ensure_ascii=False))
        print("Файлы в репозитории не изменены.")
        return 1
    if args.apply:
        for rel, item in report.items():
            shutil.copy2(item["work"], paths.SWPLUS / rel)  # целевое имя с исходным расширением .slddrt
        print(f"Заменено файлов: {len(report)}. Каталог: {run}")
    else:
        print(f"Проверка пройдена; для замены запустите с --apply. Каталог: {run}")
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
