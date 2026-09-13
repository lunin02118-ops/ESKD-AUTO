# -*- coding: utf-8 -*-
"""Матрица штампа К-12 (план согласования, WP-4.6): каждая форматка листа 1 и листа 2 × деталь и сборка × наименование
в 1, 2, 3 строки × масса в кг и в г × материал дробью и строкой. Свойства пишутся в разметке MProp — её же пишет
надстройка (Правила записи свойств SWPlus), поэтому ветки «запись надстройки» и «запись MProp» совпадают.

Проверки по словам штампа в PDF (габариты заметок API у дроби и массы недостоверны, аудит штампа и Н-32) относительно
граф формы 1 и формы 2а (ГОСТ 2.104):
текст внутри графы, центр по вертикали у эталонного места с допуском, наименование не налезает на «Сборочный чертеж».
Используется автотестом D12 и инструментом подбора координат tools/tune_stamp.py.
"""
from . import build, com, oracles

FILE = "ПРТИ.468211.101 Пластина опорная"
TITLES = {
    1: "<FONT size=4> \n<FONT size=5>Пластина опорная",
    2: "<FONT size=2> \r\n<FONT size=5>Кронштейн направляющий\nудлинённый",
    3: "<FONT size=3.5>Стойка сварная опорная\nкондуктора для сборки\nузла крепления",
}
MASS_KG = '<FONT size=1> \n<FONT size=3.5>"SW-Mass@@00@' + FILE + '.SLDPRT"'
MATERIALS = {
    "дробь": "<FONT size=1.8> <FONT size=3.5>Лист <STACK size=1>Б-ПН-НО-4,0 ГОСТ 19903-2015<OVER>Ст3сп ГОСТ 14637-89</STACK>",
    "строка": "<FONT size=1.8> \n<FONT size=3.5>Паронит ПОН-Б 2 ГОСТ 481-80",
}
DOC_EMPTY = "<FONT size=1> \n<FONT size=2.5>"
DOC_ASSEMBLY = "<FONT size=1> \n<FONT size=2.5>Сборочный чертеж"

# (ключ, сборка, строк наименования, граммы, материал). Дробь не идёт сразу после материала строкой: в открытом сеансе
# SolidWorks после такой смены рисует дробь графы 3 со сдвигом вправо на 33 мм до переоткрытия чертежа
# (tune_stamp fraction, 14.09.2026) — у документа, открытого заново, дробь на месте.
STATES = [
    ("деталь_1стр_кг_дробь", False, 1, False, "дробь"),
    ("деталь_3стр_кг_дробь", False, 3, False, "дробь"),
    ("деталь_2стр_г_строка", False, 2, True, "строка"),
    ("сборка_1стр_кг", True, 1, False, None),
    ("сборка_2стр_г", True, 2, True, None),
    ("сборка_3стр_кг", True, 3, False, None),
]
SIZES = {"A0": (1189, 841), "A1": (841, 594), "A2": (594, 420), "A3": (420, 297), "A4": (297, 210)}

INSIDE_TOL = 0.5
CENTER_TOL = {"g2": 1.0, "g5": 1.0}
MIN_TITLE_GAP = 0.3


def sheet_size(fmt):
    w, h = SIZES[fmt.stem[:2]]
    return (h, w) if fmt.stem.split("-")[1] == "P" else (w, h)


def apply_state(model, state):
    key, assembly, lines, grams, material = state
    build.props(model, {"Обозначение": "ПРТИ.468211.101", "Наименование_ФБ": TITLES[lines]})
    build.props(model, {
        "Обозначение": "ПРТИ.468211.101",
        "Сборка1_ФБ": "СБ" if assembly else "",
        "Сборка2_ФБ": DOC_ASSEMBLY if assembly else DOC_EMPTY,
        "Масса_ФБ": MASS_KG + (" г" if grams else ""),
        "Материал_ФБ": MATERIALS[material] if material else "",
    }, "00")
    model.ForceRebuild3(False)


def _decode(word):
    try:
        return word.encode("latin-1").decode("cp1251")
    except UnicodeError:
        return word


def pdf_words(pdf, width):
    """Слова первой страницы PDF: (x1, y1, x2, y2 в мм от левого нижнего угла листа, текст). Габарит слова — объединение
    габаритов его знаков: у слов PyMuPDF габарит строки, а он зависит от пустой служебной строки разметки MProp."""
    import fitz
    page = fitz.open(str(pdf))[0]
    k = page.rect.width / width
    height = page.rect.height / k
    words = []
    for block in page.get_text("rawdict")["blocks"]:
        for line in block.get("lines", []):
            for span in line["spans"]:
                current = []
                for ch in span["chars"] + [{"c": " ", "bbox": None}]:
                    if ch["c"].strip():
                        current.append(ch)
                        continue
                    if current:
                        x1 = min(c["bbox"][0] for c in current)
                        y1 = min(c["bbox"][1] for c in current)
                        x2 = max(c["bbox"][2] for c in current)
                        y2 = max(c["bbox"][3] for c in current)
                        text = _decode("".join(c["c"] for c in current))
                        words.append((x1 / k, height - y2 / k, x2 / k, height - y1 / k, text))
                        current = []
    return words


def _union(words):
    return (min(w[0] for w in words), min(w[1] for w in words), max(w[2] for w in words), max(w[3] for w in words))


def _measure(box, cell):
    x1, y1, x2, y2 = box
    return {"left": round(x1 - cell[0], 2), "right": round(cell[1] - x2, 2), "bottom": round(y1 - cell[2], 2),
            "top": round(cell[3] - y2, 2), "v_offset": round((y1 + y2) / 2 - (cell[2] + cell[3]) / 2, 2)}


DOC_WORDS = {"Сборочный", "чертеж"}
# Надписи самой формы 1 в полосах поиска: «Лит.» над графой 4, «Лист», «Листов» и номер листа — графы 7 и 8.
FORM_LABELS = {"Лит.", "Лист", "Листов", "1"}


def check(words, width, form2, assembly):
    """Замеры и нарушения эталона по словам штампа в PDF."""
    cells = oracles.form2a_cells(width) if form2 else oracles.form1_cells(width)

    def in_cell(name, right=None):
        """Слова с центром в графе; right — продолжить поиск вправо до этой границы: текст, вылезший за графу,
        попадает центром в соседнюю графу и иначе не был бы виден проверке."""
        cx1, cx2, cy1, cy2 = cells[name]
        cx2 = right if right is not None else cx2
        return [w for w in words if cx1 <= (w[0] + w[2]) / 2 <= cx2 and cy1 <= (w[1] + w[3]) / 2 <= cy2
                and w[4] not in FORM_LABELS]

    out, problems = {}, []
    groups = {"g2": in_cell("g2_designation")}
    if not form2:
        r = width - 5.0
        title = in_cell("g1_title", right=r - 35.0)
        groups["g1_title"] = [w for w in title if w[4] not in DOC_WORDS]
        groups["g1_doc"] = [w for w in title if w[4] in DOC_WORDS]
        groups["g3"] = in_cell("g3_material", right=r)
        groups["g5"] = in_cell("g5_mass")
    cell_of = {"g2": "g2_designation", "g1_title": "g1_title", "g1_doc": "g1_title", "g3": "g3_material", "g5": "g5_mass"}
    for key, group in groups.items():
        if not group:
            if key in ("g2", "g1_title", "g5") or (key == "g1_doc" and assembly) or (key == "g3" and not assembly):
                problems.append(f"{key}: текста нет")
            continue
        rec = _measure(_union(group), cells[cell_of[key]])
        rec["text"] = " ".join(w[4] for w in group)[:60]
        out[key] = rec
        if min(rec["left"], rec["right"], rec["bottom"], rec["top"]) < -INSIDE_TOL:
            problems.append(f"{key}: вне графы (слева {rec['left']}, справа {rec['right']}, снизу {rec['bottom']}, сверху {rec['top']})")
        if key in CENTER_TOL and abs(rec["v_offset"]) > CENTER_TOL[key]:
            problems.append(f"{key}: центр смещён на {rec['v_offset']} мм")
    if "g1_title" in out and "g1_doc" in out:
        gap = round(_union(groups["g1_title"])[1] - _union(groups["g1_doc"])[3], 2)
        out["title_gap"] = gap
        if gap < MIN_TITLE_GAP:
            problems.append(f"наименование налезает на «Сборочный чертеж»: зазор {gap} мм")
    return out, problems


def run_current(session, drw, model, width, pdf_dir, tag, states=STATES):
    """Матрица на текущей (встроенной) форматке листа 1: шаблон чертежа, шаблоны Master."""
    result = {}
    for state in states:
        apply_state(model, state)
        drw.ForceRebuild3(False)
        pdf = pdf_dir / f"{tag}__{state[0]}.pdf"
        ok, err, warn = session.save_as(drw, pdf)
        if not ok:
            result[state[0]] = {"problems": [f"PDF не сохранён: {err} {warn}"]}
            continue
        measured, problems = check(pdf_words(pdf, width), width, False, state[1])
        result[state[0]] = {"stamp": measured, "problems": problems}
    return {tag: result}


def run(session, drw, model, formats, pdf_dir, states=STATES):
    """{форматка: {состояние: {"stamp": замеры по PDF, "problems": […]}}} — форматка ставится на лист 1 чертежа."""
    result = {}
    for fmt in formats:
        w, h = sheet_size(fmt)
        build.set_sheet_format(drw, fmt, w, h)
        form2 = fmt.stem.endswith("-2")
        result[fmt.name] = {}
        for state in (states[:1] if form2 else states):
            apply_state(model, state)
            drw.ForceRebuild3(False)
            pdf = pdf_dir / f"{fmt.stem}__{state[0]}.pdf"
            ok, err, warn = session.save_as(drw, pdf)
            if not ok:
                result[fmt.name][state[0]] = {"problems": [f"PDF не сохранён: {err} {warn}"]}
                continue
            measured, problems = check(pdf_words(pdf, w), w, form2, state[1])
            result[fmt.name][state[0]] = {"stamp": measured, "problems": problems}
    return result


def problems_of(result):
    return {f"{fmt} / {state}": rec["problems"] for fmt, states in result.items() for state, rec in states.items()
            if rec["problems"]}
