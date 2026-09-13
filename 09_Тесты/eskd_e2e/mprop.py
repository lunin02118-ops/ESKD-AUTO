# -*- coding: utf-8 -*-
"""MProp в автотестах: «Применить без правок» и снимок того, что MProp может изменить (критерий К-1 плана согласования).

MProp запускается из копии папок SWPlus в каталоге прогона (он пишет MProp_Prof.txt и MProp_Project.txt рядом с собой,
спайк S-1): SolidWorks.RunMacro2(<копия MProp.swp>, "MProp_run", "main_reload") выполняет чтение формы и «Применить и
закрыть» без нажатий. Окна-вопросы MProp закрывает сторож диалогов сессии, тест их видит как неожиданные диалоги.
"""
import shutil
import subprocess
import threading
import time
from pathlib import Path

from . import com, oracles, paths

SWPLUS_FOLDERS = ("MProp", "SpecEditor", "SProp", "DProp")
UNIT_PREFERENCES = {"swUnitSystem": 263, "swUnitsMassPropLength": 258, "swUnitsMassPropMass": 259,
                    "swUnitsMassPropVolume": 260, "swUnitsMassPropDecimalPlaces": 261}

EMPTY_DOC_DESCRIPTION = "<FONT size=1> \n<FONT size=2.5>"
EMPTY_LITERA = "<FONT size=1> \n<FONT size=3.5>"


def _is(*values):
    return lambda value, before: value in values


def _empty(value, before):
    return value == ""


# Что MProp вправе добавить при первом применении (Правила записи свойств SWPlus, раздел 9): имя → проверка значения.
FIRST_APPLY_GENERAL = {
    "Number": lambda v, b: v == b["levels"]["общие"].get("Обозначение", ""),
    "Description": lambda v, b: v == b["levels"]["общие"].get("Наименование", "").replace("\n", " "),
    "RenameSWP": _is("0"), "Доп.свойство_1": _empty, "Доп.свойство_2": _empty, "Заимствование": _empty,
    "Классификатор": _is("False"), "Наименование2": _empty, "Обозначение2": _empty, "Примечание": _empty,
    "Проект_ФБ": _empty, "Раздел": _is("Детали", "Сборочные единицы"), "Сборка": _is("False", "True"), "Удален": _is("НЕТ"),
}
FIRST_APPLY_CONFIG = {
    "Единицы": _is("True"), "Заготовка": _empty, "Код_ФБ": _empty, "Литера_Таблица": _empty, "Литера_ФБ": _is(EMPTY_LITERA),
    "Начальник": _empty, "Нормоконтроль": _empty, "Плотность_ФБ": lambda v, b: v.startswith('"SW-Density@@'),
    "Применение2": _empty, "Примечание": _empty, "Проект": _empty, "Раздел": _is("Детали", "Сборочные единицы"),
    "Сборка1_ФБ": _empty, "Сборка2_ФБ": _is(EMPTY_DOC_DESCRIPTION), "Справочный_номер": _empty, "Техконтроль": _empty,
    "Утвердил": _empty, "Формат": lambda v, b: v == b["levels"]["общие"].get("Формат"), "Формат_ФБ": _is("False"),
    "Характер_работы": _empty, "Материал_ФБ": _empty, "Материал_Таблица": _empty,
}


def swplus_copy(run_dir):
    """Копия папок SWPlus в каталоге прогона (один раз на прогон); путь к копии MProp.swp."""
    root = Path(run_dir) / "_SWPlus"
    for folder in SWPLUS_FOLDERS:
        target = root / folder
        if not target.exists():
            shutil.copytree(paths.SWPLUS / folder, target)
    return root / "MProp" / "MProp.swp"


def apply_without_edits(session, doc, timeout=120):
    """MProp «Применить без правок» для активного документа. Возвращает {"ok", "err", "seconds", "timeout"}."""
    macro = swplus_copy(session.run_dir)
    session.activate(doc)
    err = com.ref_int()
    result = {}
    done = threading.Event()

    def killer():
        if not done.wait(timeout):
            result["timeout"] = True
            subprocess.run(["taskkill", "/PID", str(session.pid), "/F"], capture_output=True)

    threading.Thread(target=killer, daemon=True).start()
    started = time.time()
    try:
        result["ok"] = bool(session.sw.RunMacro2(str(macro), "MProp_run", "main_reload", 1, err))
        result["err"] = int(err.value)
    finally:
        done.set()
    result["seconds"] = round(time.time() - started, 1)
    return result


def snapshot(doc):
    """Всё, что MProp может изменить: сырые значения свойств по уровням, «Сводка → Автор», единицы массы документа."""
    dump = oracles.dump_properties(doc)
    levels = {"общие": {n: (i["raw"] or "").replace("\r\n", "\n") for n, i in dump["general"].items()}}
    for cfg, props in dump["configs"].items():
        levels[cfg] = {n: (i["raw"] or "").replace("\r\n", "\n") for n, i in props.items()}
    units = {}
    for name, pref in UNIT_PREFERENCES.items():
        try:
            units[name] = int(doc.Extension.GetUserPreferenceInteger(pref, 0))
        except Exception as exc:
            units[name] = repr(exc)
    try:
        author = str(doc.SummaryInfo(2))
    except Exception:
        author = None
    return {"levels": levels, "author": author, "units": units}


def differences(before, after):
    """Изменения после MProp: изменённые и удалённые значения, добавления вне перечня первого применения, автор, единицы."""
    out = []
    for level in sorted(set(before["levels"]) | set(after["levels"])):
        b, a = before["levels"].get(level, {}), after["levels"].get(level, {})
        allowed = FIRST_APPLY_GENERAL if level == "общие" else FIRST_APPLY_CONFIG
        for name in sorted(set(b) | set(a)):
            if name not in a:
                out.append(f"{level} · {name}: удалено «{b[name]}»")
            elif name not in b:
                check = allowed.get(name)
                if check is None or not check(a[name], before):
                    out.append(f"{level} · {name}: добавлено «{a[name]}»")
            elif a[name] != b[name]:
                out.append(f"{level} · {name}: «{b[name]}» → «{a[name]}»")
    if before["author"] != after["author"]:
        out.append(f"Сводка · Автор: «{before['author']}» → «{after['author']}»")
    for name in UNIT_PREFERENCES:
        if before["units"].get(name) != after["units"].get(name):
            out.append(f"единицы · {name}: {before['units'].get(name)} → {after['units'].get(name)}")
    return out
