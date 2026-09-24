# -*- coding: utf-8 -*-
"""WP-3.1: приведение корпоративных шаблонов документов к модели свойств плана (§3.2, §3.6).

Эталон TARGET — полный набор пользовательских свойств на каждом уровне: всё, чего нет в эталоне, удаляется,
значения выставляются дословно. SolidWorks работает только с копиями в каталоге прогона и без надстройки;
результат читается с диска и сравнивается с эталоном. В репозиторий файл копируется после выхода из SolidWorks,
только если все шаблоны совпали с эталоном, и только с ключом --apply.

    python tools/normalize_templates.py            # проверка: собрать и сравнить, репозиторий не меняется
    python tools/normalize_templates.py --apply    # то же и замена шаблонов в 02_Шаблоны_и_Форматки
"""
import argparse
import json
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eskd_e2e import com, oracles, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

# «Формат» — кириллица (Д-16): «А» U+0410.
# Порядок имён на уровне — единый порядок свойств (№23, решение владельца 24.09.2026): словарь SWPlus, затем служебные,
# последними «Примечание», «Формат», «Раздел». Имена переписываются по одному в этом порядке, и так они ложатся в файл:
# у новой детали первое сохранение ничего не переставляет.
# «Обозначение» пустое: выражение $PRP:"SW-File Name" давало имя файла целиком («ПРТИ.468211.101 Пластина опорная») в графе
# «Обозначение» спецификации и штампа у документа, который не сохранялся с надстройкой и не проходил MProp. Пустое значение
# надстройка (PropertyWriter.IsEmptyOrTemplate) и MProp (FrmMProp:1526, 1550–1557) заполняют по имени файла так же.
TARGET = {
    paths.PART_TEMPLATE: {
        "": {"Обозначение": "", "Наименование": "", "Масса": '"SW-Mass"',
             "Материал": '"SW-Material"', "Формат": "А3"},
        # «Материал_ФБ» в шаблоне нет (WP-4.4, Н-21): MProp принимал $PRP:"Материал" за текст пользователя и переписывал его
        # в своей разметке; графу 3 заполняют надстройка по библиотеке материалов и MProp.
        "00": {"UNIT_OF_MEASURE": " - нет -"},
    },
    paths.ASSEMBLY_TEMPLATE: {
        "": {"Обозначение": "", "Наименование": "", "Масса": '"SW-Mass"', "Формат": "А3"},
        # Код «СБ» — в конфигурации (§3.2) и в форме MProp (D-8); «Раздел» — как пишет MProp.
        "00": {"Сборка1_ФБ": "СБ", "UNIT_OF_MEASURE": " - нет -", "Раздел": "Сборочные единицы"},
    },
    # Штамп чертежа читает свойства модели ($PRPSHEET): собственные подписи и организация чертежу не нужны.
    paths.DRAWING_TEMPLATE: {
        "": {"SWFormatSize": "297мм*420мм"},
    },
}


def raw_levels(dump):
    levels = {"": {name: item["raw"] for name, item in dump["general"].items()}}
    for cfg, props in dump["configs"].items():
        levels[cfg] = {name: item["raw"] for name, item in props.items()}
    return levels


def normalize(session, path, target):
    doc = session.open(path)
    operations = []
    try:
        for level, wanted in target.items():
            cpm = doc.Extension.CustomPropertyManager(level)
            names = list(com.prop_names(cpm))
            # Запись значения оставляет свойство на прежнем месте: порядок чинится только удалением и записью заново
            # (новое свойство SolidWorks ставит последним). Оставшиеся должны быть началом эталона: недостающие допишутся за ними.
            present = [n for n in names if n in wanted]
            reorder = present != list(wanted)[:len(present)]
            for name in names:
                if name not in wanted or reorder:
                    rc = int(cpm.Delete2(name))
                    if rc != 0:
                        raise RuntimeError(f"{path.name} [{level or 'общие'}] Delete2({name}) = {rc}")
                    operations.append(f"[{level or 'общие'}] {'удалено' if name not in wanted else 'переставлено'} {name}")
            for name, value in wanted.items():
                rc = com.prop_set(cpm, name, value)
                if rc != 0:
                    raise RuntimeError(f"{path.name} [{level or 'общие'}] Add3({name}) = {rc}")
        ok, err, warn = session.save(doc)
        if not ok:
            raise RuntimeError(f"{path.name}: Save3 errors={err} warnings={warn}")
    finally:
        session.close(doc)
    return operations


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--apply", action="store_true", help="заменить шаблоны в репозитории проверенными копиями")
    ap.add_argument("--only", default="", help="только эти шаблоны, имена через запятую: «Деталь.prtdot,Сборка.asmdot»")
    ap.add_argument("--run", default="", help="каталог прогона; по умолчанию 08_Результаты_Тестирования/runs")
    args = ap.parse_args()
    only = {n.strip().lower() for n in args.only.split(",") if n.strip()}
    targets = {t: v for t, v in TARGET.items() if not only or t.name.lower() in only}
    if only and len(targets) != len(only):
        print("Нет таких шаблонов:", sorted(only - {t.name.lower() for t in targets}))
        return 2
    run = Path(args.run) if args.run else paths.RUNS / ("templates_" + time.strftime("%Y%m%d_%H%M%S"))
    report = {}
    master, tail = oracles.property_master(), oracles.property_tail()
    with SwSession(run, load_eskd=False, use_probe=False) as session:
        for template, target in targets.items():
            session.workspace_copy(template, subdir="original")
            work = session.workspace_copy(template, subdir="normalized")
            operations = normalize(session, work, target)
            persisted = oracles.read_persisted(session, work)
            actual = {level: raw_levels(persisted).get(level, {}) for level in target}
            resolved = {level: {n: i["resolved"] for n, i in (persisted["general"] if level == "" else persisted["configs"].get(level, {})).items()}
                        for level in target}
            # Порядок тоже: словари сравниваются без порядка, списки пар — с ним. Эталон сам в едином порядке — иначе
            # правка TARGET молча вернула бы шаблонам чужой порядок.
            ordered = {level: list(values.items()) for level, values in actual.items()}
            match = ordered == {level: list(values.items()) for level, values in target.items()} and all(
                oracles.canonical(list(values), master, tail)[0] == list(values) for values in target.values())
            report[template.name] = {"work": str(work), "match": match, "operations": operations,
                                     "actual": ordered, "expected": {k: list(v.items()) for k, v in target.items()},
                                     "resolved": resolved}
    (run / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    for name, item in report.items():
        print(f"{'OK ' if item['match'] else 'РАСХОЖДЕНИЕ'} {name}: операций {len(item['operations'])}")
        for op in item["operations"]:
            print("    ", op)
        if not item["match"]:
            print("     получено:", item["actual"])
            print("     ожидалось:", item["expected"])
        print("     разрешённые значения:", item["resolved"])
    if not all(item["match"] for item in report.values()):
        print("Шаблоны в репозитории не изменены.")
        return 1
    if args.apply:
        for template in targets:
            shutil.copy2(report[template.name]["work"], template)
            print("заменён", template)
    else:
        print(f"Проверка пройдена; для замены шаблонов запустите с --apply. Каталог: {run}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
