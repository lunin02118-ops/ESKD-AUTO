# -*- coding: utf-8 -*-
"""Оракулы: свойства на диске, штамп, положение заметок, журнал надстройки, геометрия ГОСТ."""
import re
from pathlib import Path

from . import com, paths


# --------------------------------------------------------------------------- свойства
def dump_properties(doc):
    """Все пользовательские свойства документа: общие и по конфигурациям, сырые и разрешённые."""
    def level(cpm):
        out = {}
        for name in com.prop_names(cpm):
            raw, resolved = com.ref_str(""), com.ref_str("")
            try:
                cpm.Get4(name, False, raw, resolved)
            except Exception:
                pass
            out[name] = {"raw": str(raw.value or ""), "resolved": str(resolved.value or "")}
        return out

    ext = doc.Extension
    data = {"general": level(ext.CustomPropertyManager("")), "configs": {}}
    doc_type = int(doc.GetType)
    if doc_type in (com.SW_DOC_PART, com.SW_DOC_ASSEMBLY):
        for cfg in com.as_list(doc.GetConfigurationNames):
            data["configs"][str(cfg)] = level(ext.CustomPropertyManager(str(cfg)))
        try:
            data["active_config"] = str(doc.GetActiveConfiguration.Name)
        except Exception:
            data["active_config"] = ""
    return data


def read_persisted(session, path):
    """Состояние файла на диске: надстройка ЕСКД выгружена, документ открыт только для чтения."""
    with session.eskd_unloaded():
        doc = session.open(path, readonly=True)
        try:
            return dump_properties(doc)
        finally:
            session.close(doc)


def value(dump, name, cfg=None, resolved=False):
    """Значение свойства из дампа или None, если свойства нет на указанном уровне."""
    level = dump["general"] if cfg is None else dump["configs"].get(cfg, {})
    item = level.get(name)
    if item is None:
        return None
    return item["resolved" if resolved else "raw"]


def names(dump, cfg=None):
    level = dump["general"] if cfg is None else dump["configs"].get(cfg, {})
    return set(level)


def all_names(dump):
    out = set(dump["general"])
    for cfg in dump["configs"].values():
        out |= set(cfg)
    return out


# --------------------------------------------------------------------------- чертёж
def sheet_views(drw):
    """[(имя листа, вид листа, [виды модели])] для всех листов."""
    out = []
    names = [str(n) for n in com.as_list(drw.GetSheetNames)]
    views = com.as_list(drw.GetViews)
    for i, per_sheet in enumerate(views):
        items = [com.dyn(v) for v in com.as_list(per_sheet)]
        if not items:
            continue
        out.append((names[i] if i < len(names) else f"#{i}", items[0], items[1:]))
    return out


def notes_of_view(view):
    out = []
    note = view.GetFirstNote
    while note is not None:
        note = com.dyn(note)
        rec = {"name": str(note.GetName or ""), "text": str(note.GetText or "")}
        try:
            rec["linked"] = str(note.PropertyLinkedText or "")
        except Exception:
            rec["linked"] = ""
        try:
            ann = com.dyn(note.GetAnnotation)
            pos = com.as_list(ann.GetPosition)
            rec["position_mm"] = [round(float(c) * 1000.0, 4) for c in pos[:2]] if pos else None
        except Exception:
            rec["position_mm"] = None
        try:
            ext = com.as_list(note.GetExtent)
            rec["extent_mm"] = [round(float(c) * 1000.0, 3) for c in ext] if ext else None
        except Exception:
            rec["extent_mm"] = None
        out.append(rec)
        note = note.GetNext
    return out


def stamp(drw):
    """Заметки форматок всех листов: {лист: {имя заметки: запись}}."""
    result = {}
    for sheet_name, sheet_view, _ in sheet_views(drw):
        result[sheet_name] = {n["name"]: n for n in notes_of_view(sheet_view)}
    return result


def note_positions(drw):
    """Координаты всех заметок всех видов в мм — для проверки нулевого смещения."""
    out = {}
    for sheet_name, sheet_view, model_views in sheet_views(drw):
        for view in [sheet_view] + model_views:
            vname = str(view.GetName2 or "")
            for n in notes_of_view(view):
                out[f"{sheet_name}/{vname}/{n['name']}"] = n["position_mm"]
    return out


def max_drift(before, after):
    worst = 0.0
    moved = []
    for key, p0 in before.items():
        p1 = after.get(key)
        if not p0 or not p1:
            continue
        d = max(abs(p1[0] - p0[0]), abs(p1[1] - p0[1]))
        if d > worst:
            worst = d
        if d > 0.001:
            moved.append((key, round(d, 4)))
    return worst, moved


# --------------------------------------------------------------------------- журнал надстройки
LOG_ERROR = re.compile(r"(exception|error|ошибк|fail|код возврата\s*[1-9])", re.IGNORECASE)


class AddinLog:
    def __init__(self, path=paths.ADDIN_LOG):
        self.path = Path(path)
        self.offset = self.path.stat().st_size if self.path.exists() else 0

    def new_lines(self):
        if not self.path.exists():
            return []
        with open(self.path, "rb") as fh:
            fh.seek(self.offset)
            data = fh.read()
        return data.decode("utf-8", errors="replace").splitlines()

    def errors(self):
        return [ln for ln in self.new_lines() if LOG_ERROR.search(ln)]


# --------------------------------------------------------------------------- геометрия ГОСТ
def form1_cells(sheet_width_mm):
    """Графы основной надписи формы 1 (ГОСТ 2.104), мм от левого нижнего угла листа."""
    r = sheet_width_mm - 5.0
    return {
        "stamp": (r - 185.0, r, 5.0, 60.0),
        "g2_designation": (r - 120.0, r, 45.0, 60.0),
        "g1_title": (r - 120.0, r - 50.0, 20.0, 45.0),
        "g3_material": (r - 120.0, r - 50.0, 5.0, 20.0),
        "g5_mass": (r - 35.0, r - 18.0, 25.0, 40.0),
        "g8_firm": (r - 50.0, r, 5.0, 20.0),
    }


def form2a_cells(sheet_width_mm):
    r = sheet_width_mm - 5.0
    return {
        "stamp": (r - 185.0, r, 5.0, 20.0),
        "g2_designation": (r - 120.0, r - 10.0, 5.0, 20.0),
        "g7_sheet": (r - 10.0, r, 5.0, 20.0),
    }


SPEC_COLUMNS_MM = (("Формат", 6), ("Зона", 6), ("Поз.", 8), ("Обозначение", 70), ("Наименование", 63),
                   ("Кол.", 10), ("Примечание", 22))


def inside(extent_mm, cell, tol=0.5):
    """Габарит текста заметки [x1,y1,z1,x2,y2,z2] целиком внутри графы (x1,x2,y1,y2)."""
    if not extent_mm:
        return False
    x1, y1, x2, y2 = extent_mm[0], extent_mm[1], extent_mm[3], extent_mm[4]
    cx1, cx2, cy1, cy2 = cell
    return (min(x1, x2) >= cx1 - tol and max(x1, x2) <= cx2 + tol and
            min(y1, y2) >= cy1 - tol and max(y1, y2) <= cy2 + tol)
