# -*- coding: utf-8 -*-
"""WP-4.5: библиотека материалов по таблице Д-2 плана согласования (ред. 4) и ответу владельца «О-7 как советуешь».

Правятся только поля записи обозначения (Обозначение_ГОСТ, Обозначение_Строка, атрибут description, Сортамент,
ГОСТ_Сортамент, Марка_Материала, ГОСТ_Материал) и плотность. Имена материалов и matid не меняются: на них ссылаются
модели, избранные материалы и тесты. Разделители («х», «-2,5», «4,0») не меняются ни в одной группе (вопрос 12).

    python 09_Тесты/tools/library_wp45.py            — перечень правок (пробный прогон)
    python 09_Тесты/tools/library_wp45.py --apply    — запись; прежний файл — в 08_Результаты_Тестирования/runs/library_wp45_*

Класс А (без вопросов): лист t ≥ 4 — знаменатель «Ст3сп ГОСТ 14637-2024»; лист х/к 19904 — «Б» → «БТ»; плотность 7820 →
7850 у профильных труб; «Сталь 3сп» → «Ст3сп»; ABS 1380 → 1050; полиамид «ПА 6-210/311 ОСТ 6-06-С9-93».
Класс В (О-7): (а) кромка без документа одной строкой; (б) лист t < 4 — «Ст3сп ГОСТ 16523-97»; (в) ЛДСП без знаменателя
ГОСТ 10632-2014; (г) оцинкованный лист — год 14918-2020; (д) профиль АД31Т1 — год 22233-2018; (е) рифлёный лист,
труба ВГП, лист АМг2.М, полиэтилен, полипропилен — одна строка.
"""
import html
import re
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LIBRARY = ROOT / "04_Библиотеки_Материалов_и_Профилей" / "Библиотека материалов" / "Библиотека_Материалов_ГОСТ.sldmat"
RUNS = ROOT / "08_Результаты_Тестирования" / "runs"

MATERIAL = re.compile(r'(<material name=")([^"]*)(" description=")([^"]*)(" matid=")(\d+)(".*?</material>)', re.S)
PROP = re.compile(r'(<prop name="([^"]*)" description="[^"]*" value=")([^"]*)(")')
DENS = re.compile(r'(<DENS [^>]*value=")([^"]*)(")')

FIELDS = ("Сортамент", "ГОСТ_Сортамент", "Марка_Материала", "ГОСТ_Материал", "Обозначение_ГОСТ", "Обозначение_Строка")


def fraction(shape, numerator, denominator):
    """Запись дробью в форме библиотеки: вид перед дробью (как MProp «Сортамент»)."""
    prefix = (shape + " ") if shape else " "
    return f"{prefix}<STACK size=1>{numerator}<OVER>{denominator}</STACK>", f"{(shape + ' ') if shape else ''}{numerator} / {denominator}"


def one_line(text):
    return text, text


def thickness(fields):
    try:
        return float(fields["Типоразмер"].replace(",", "."))
    except (KeyError, ValueError):
        return None


def rule(matid, name, f, dens):
    """(новые поля, новая плотность, основание) или None — запись не меняется."""
    num, den = f.get("Сортамент", ""), None
    m = re.match(r"^(\S*) ?<STACK size=1>(.*)<OVER>(.*)</STACK>$", f.get("Обозначение_ГОСТ", ""))
    shape, numer, denom = (m.group(1), m.group(2), m.group(3)) if m else (None, None, None)
    g = dict(f)
    new_dens = dens
    why = []

    if "ГОСТ 19903-2015" in num and denom == "Ст3сп ГОСТ 14637-89":
        t = thickness(f)
        if t is not None and t >= 4:
            denom, why = "Ст3сп ГОСТ 14637-2024", ["А: лист t ≥ 4 — ГОСТ 14637-2024"]
            g["ГОСТ_Материал"] = "ГОСТ 14637-2024"
        else:
            denom, why = "Ст3сп ГОСТ 16523-97", ["В О-7б: лист t < 4 — ГОСТ 16523-97"]
            g["ГОСТ_Материал"] = "ГОСТ 16523-97"
        g["Обозначение_ГОСТ"], g["Обозначение_Строка"] = fraction(shape, numer, denom)
    elif "ГОСТ 19904-90" in num and re.search(r"Б-ПО-", num):
        numer = numer.replace("Б-ПО-", "БТ-ПО-")
        g["Сортамент"] = num.replace("Б-ПО-", "БТ-ПО-")
        g["Обозначение_ГОСТ"], g["Обозначение_Строка"] = fraction(shape, numer, denom)
        why = ["А: ГОСТ 19904-90 — точность «БТ»"]
    elif "ГОСТ 14918-80" in num:
        for k in FIELDS:
            g[k] = g.get(k, "").replace("ГОСТ 14918-80", "ГОСТ 14918-2020")
        why = ["В О-7г: ГОСТ 14918-2020 (только год)"]
    elif "ГОСТ 22233-2001" in num:
        for k in FIELDS:
            g[k] = g.get(k, "").replace("ГОСТ 22233-2001", "ГОСТ 22233-2018")
        why = ["В О-7д: ГОСТ 22233-2018 (только год)"]
    elif f.get("Обозначение_ГОСТ") == "Сталь 3сп ГОСТ 380-2005":
        g["Обозначение_ГОСТ"], g["Обозначение_Строка"] = one_line("Ст3сп ГОСТ 380-2005")
        g["Марка_Материала"] = "Ст3сп"
        why = ["А: «Ст3сп ГОСТ 380-2005» (грубая ошибка 3)"]
    elif num.startswith("Кромка ") and "ГОСТ 34359-2017" in num:
        line = re.sub(r"\s*ГОСТ 34359-2017", "", num)
        g["Сортамент"] = line
        g["ГОСТ_Сортамент"] = g["ГОСТ_Материал"] = ""
        g["Обозначение_ГОСТ"], g["Обозначение_Строка"] = one_line(line)
        why = ["В О-7а: кромка без документа (ГОСТ 34359-2017 — сейфы)"]
        if "ABS" in num and abs(dens - 1380.0) < 0.1:
            new_dens = 1050.0
            why.append("А: плотность ABS 1050")
    elif num.startswith("Полиамид ПА 6-210-311"):
        line = "Полиамид ПА 6-210/311 ОСТ 6-06-С9-93"
        g.update({"Сортамент": line, "ГОСТ_Сортамент": "ОСТ 6-06-С9-93", "Марка_Материала": "ПА 6-210/311",
                  "ГОСТ_Материал": "ОСТ 6-06-С9-93"})
        g["Обозначение_ГОСТ"], g["Обозначение_Строка"] = one_line(line)
        why = ["А: полиамид ПА 6 по ОСТ 6-06-С9-93 (грубая ошибка 2)"]
    elif "ГОСТ 32289-2013" in num and denom == "ГОСТ 10632-2014":
        g["ГОСТ_Материал"] = ""
        g["Обозначение_ГОСТ"], g["Обозначение_Строка"] = one_line(num)
        why = ["В О-7в: ЛДСП без знаменателя ГОСТ 10632-2014"]
    else:
        single = {
            "ГОСТ 8568-77": lambda: f"{num.replace(' ГОСТ 8568-77', '')} Ст3сп ГОСТ 8568-77",
            "ГОСТ 3262-75": lambda: num,
            "ГОСТ 16338-85": lambda: num,
            "ГОСТ 26996-86": lambda: num,
        }
        for std, make in single.items():
            if std in num and denom is not None:
                line = make()
                g["Сортамент"] = line
                g["Обозначение_ГОСТ"], g["Обозначение_Строка"] = one_line(line)
                why = [f"В О-7е: одна строка ({std})"]
                break
        if "ГОСТ 21631-76" in num and denom is not None:
            line = re.sub(r"^Лист АМг2М-(\S+) ГОСТ 21631-76$", r"Лист АМг2.М \1 ГОСТ 21631-76", num)
            g["Сортамент"] = line
            g["Марка_Материала"] = "АМг2.М"
            g["Обозначение_ГОСТ"], g["Обозначение_Строка"] = one_line(line)
            why = ["В О-7е: одна строка «АМг2.М» (ГОСТ 21631-76)"]
    if abs(dens - 7820.0) < 0.1 and ("ГОСТ 13663-86" in f.get("ГОСТ_Материал", "")):
        new_dens = 7850.0
        why.append("А: плотность профильной трубы 7850 (таблицы сортамента при 7,85 г/см³)")
    if not why:
        return None
    return g, new_dens, why


def plan(text):
    changes = []
    for m in MATERIAL.finditer(text):
        body = m.group(7)
        fields = {k: html.unescape(v) for _, k, v, _ in [(p.group(1), p.group(2), p.group(3), p.group(4)) for p in PROP.finditer(body)]}
        dens = float(DENS.search(body).group(2))
        res = rule(m.group(6), html.unescape(m.group(2)), fields, dens)
        if res:
            changes.append((m, fields, dens) + res)
    return changes


def escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def apply(text, changes):
    out, last = [], 0
    for m, fields, dens, new, new_dens, why in changes:
        body = m.group(7)

        def prop(p):
            name = p.group(2)
            if name in new and new[name] != fields.get(name):
                return p.group(1) + escape(new[name]) + p.group(4)
            return p.group(0)

        body = PROP.sub(prop, body)
        if new_dens != dens:
            body = DENS.sub(lambda d: d.group(1) + repr(new_dens) + d.group(3), body, count=1)
        description = escape(new["Обозначение_ГОСТ"].strip()) if new["Обозначение_ГОСТ"] != fields["Обозначение_ГОСТ"] else m.group(4)
        out.append(text[last:m.start()])
        out.append(m.group(1) + m.group(2) + m.group(3) + description + m.group(5) + m.group(6) + body)
        last = m.end()
    out.append(text[last:])
    return "".join(out)


def main():
    raw = LIBRARY.read_bytes()
    text = raw.decode("utf-16")
    changes = plan(text)
    for m, fields, dens, new, new_dens, why in changes:
        print(f"{m.group(6)} {html.unescape(m.group(2))}")
        print(f"    {'; '.join(why)}")
        for k in FIELDS:
            if new.get(k) != fields.get(k):
                print(f"    {k}: «{fields.get(k)}» → «{new.get(k)}»")
        if new_dens != dens:
            print(f"    плотность: {dens} → {new_dens}")
    print(f"записей к правке: {len(changes)} из {len(MATERIAL.findall(text))}")
    if "--apply" not in sys.argv:
        return 0
    backup = RUNS / time.strftime("library_wp45_%Y%m%d_%H%M%S")
    backup.mkdir(parents=True)
    shutil.copy2(LIBRARY, backup / LIBRARY.name)
    result = apply(text, changes)
    LIBRARY.write_bytes(result.encode("utf-16"))
    again = plan(LIBRARY.read_bytes().decode("utf-16"))
    print("повторный план:", len(again), "правок; копия прежнего файла:", backup)
    return 0 if not again else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
