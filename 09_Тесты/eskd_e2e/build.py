# -*- coding: utf-8 -*-
"""Построение деталей, сборок и чертежей через API — для фикстур и сценариев тестов."""
import math
import re
import time
import xml.etree.ElementTree as ET
from functools import lru_cache
from pathlib import Path

from . import com, paths

PLANE_NAMES = {"front": ("Спереди", "Front Plane", "Front"), "top": ("Сверху", "Top Plane", "Top"),
               "right": ("Справа", "Right Plane", "Right")}


@lru_cache(maxsize=1)
def material_library():
    """Материалы корпоративной библиотеки: имя → {density, custom: {...}}."""
    raw = Path(paths.MATERIAL_DB).read_bytes().decode("utf-16")
    raw = raw.replace('encoding="UTF-16"', 'encoding="UTF-8"')
    root = ET.fromstring(raw.encode("utf-8"))
    out = {}
    for m in root.iter("material"):
        dens = m.find("physicalproperties/DENS")
        custom = {p.get("name"): p.get("value") for p in m.findall("custom/prop")}
        out[m.get("name")] = {"density": float(dens.get("value")) if dens is not None else None, "custom": custom}
    return out


def select_plane(doc, which="front"):
    for name in PLANE_NAMES[which]:
        try:
            if doc.Extension.SelectByID2(name, "PLANE", 0, 0, 0, False, 0, com.null_dispatch(), 0):
                return True
        except Exception:
            pass
    index = {"front": 0, "top": 1, "right": 2}[which]
    feat = doc.FirstFeature
    seen = 0
    while feat is not None:
        feat = com.dyn(feat)
        if str(feat.GetTypeName2) == "RefPlane":
            if seen == index:
                return bool(feat.Select2(False, 0))
            seen += 1
        feat = feat.GetNextFeature
    raise RuntimeError("Не найдена базовая плоскость")


def rectangle(sketch_manager, x1, y1, x2, y2):
    sketch_manager.CreateCornerRectangle(x1, y1, 0.0, x2, y2, 0.0)


def extrude(doc, depth_m, merge=True):
    feat = doc.FeatureManager.FeatureExtrusion3(
        True, False, False, 0, 0, depth_m, 0.0, False, False, False, False, 0, 0,
        False, False, False, False, merge, True, True, 0, 0, False)
    if feat is None:
        raise RuntimeError("Вытягивание не построено")
    return com.dyn(feat)


def sketch_rectangles(doc, rects, plane="front"):
    doc.ClearSelection2(True)
    select_plane(doc, plane)
    sk = doc.SketchManager
    sk.InsertSketch(True)
    # AddToDB: геометрия пишется напрямую, без привязок и автоматических взаимосвязей —
    # иначе близкие контуры (стенка трубы 2 мм) слипаются при текущем масштабе вида.
    sk.AddToDB = True
    try:
        for r in rects:
            rectangle(sk, *r)
    finally:
        sk.AddToDB = False


def set_material(doc, material, config=""):
    if material not in material_library():
        raise KeyError(f"Материала нет в корпоративной библиотеке: {material}")
    doc.SetMaterialPropertyName2(config, str(paths.MATERIAL_DB), material)


def set_other_material(doc, database, material, config=""):
    """Материал из базы SolidWorks вне корпоративной библиотеки — для изделий, которые не делаются из сортамента."""
    doc.SetMaterialPropertyName2(config, database, material)


def material_of(doc, config):
    """(материал, база) конфигурации; пустые строки — материала нет."""
    db = com.ref_str("")
    name = doc.GetMaterialPropertyName2(config, db)
    return str(name or ""), str(db.value or "")


def mass_kg(doc):
    mp = com.dyn(doc.Extension.CreateMassProperty)
    return float(mp.Mass)


def add_configuration(doc, name):
    ok = doc.AddConfiguration3(name, "", "", 0)
    if not ok:
        raise RuntimeError(f"Не создана конфигурация {name}")


def add_derived_configuration(doc, name, parent):
    """Производная конфигурация name от parent (как «01» под «00» у «Укосины» NC3-7R)."""
    manager = com.dyn(doc.ConfigurationManager)
    cfg = manager.AddConfiguration2(name, "", "", 0, parent, "", True)
    if cfg is None:
        raise RuntimeError(f"Не создана производная конфигурация {name} от {parent}")


def show_configuration(doc, name):
    doc.ShowConfiguration2(name)


def set_dimension(doc, full_name, value_m, config=None):
    dim = com.dyn(doc.Parameter(full_name))
    if dim is None:
        raise RuntimeError(f"Размер {full_name} не найден")
    if config is None:
        return dim.SetSystemValue3(value_m, 2, None)
    return dim.SetSystemValue3(value_m, 3, [config])


def props(doc, values, config=""):
    cpm = doc.Extension.CustomPropertyManager(config)
    for name, value in values.items():
        rc = com.prop_set(cpm, name, value)
        if rc != 0:
            raise RuntimeError(f"Add3({name}) вернул {rc}")


# --------------------------------------------------------------------------- типовые детали
def plate(session, length_mm, width_mm, thickness_mm, material):
    """Прямоугольная пластина: эскиз на фронтальной плоскости, вытягивание на толщину; material=None — без материала."""
    doc = session.new_doc(paths.PART_TEMPLATE)
    sketch_rectangles(doc, [(-length_mm / 2000, -width_mm / 2000, length_mm / 2000, width_mm / 2000)])
    feat = extrude(doc, thickness_mm / 1000.0)
    if material is not None:
        set_material(doc, material)
    doc.ForceRebuild3(False)
    return doc, feat


def square_tube(session, outer_mm, wall_mm, length_mm, material):
    doc = session.new_doc(paths.PART_TEMPLATE)
    o = outer_mm / 2000.0
    i = (outer_mm - 2 * wall_mm) / 2000.0
    sketch_rectangles(doc, [(-o, -o, o, o), (-i, -i, i, i)])
    feat = extrude(doc, length_mm / 1000.0)
    set_material(doc, material)
    doc.ForceRebuild3(False)
    return doc, feat


def sheet_metal_plate(session, length_mm, width_mm, thickness_mm, material):
    """Листовая деталь — базовая кромка из прямоугольника: есть SheetMetal и развёртка (выгрузка DXF, Т-28).
    Merge = False: с объединением SolidWorks кромку на пустой детали не строит."""
    doc = session.new_doc(paths.PART_TEMPLATE)
    sketch_rectangles(doc, [(-length_mm / 2000, -width_mm / 2000, length_mm / 2000, width_mm / 2000)])
    t = thickness_mm / 1000.0
    feat = doc.FeatureManager.InsertSheetMetalBaseFlange2(t, False, t, 0.0254, 0.01, False, 0, 0, 1, com.null_dispatch(),
                                                           False, 0, 0.0001, 0.0001, 0.5, True, False, True, True)
    if feat is None:
        raise RuntimeError("Базовая кромка листовой детали не построена")
    set_material(doc, material)
    doc.ForceRebuild3(False)
    return doc


def structural_tube(session, length_mm, profile, material, angle_deg=0.0):
    """Деталь сварной конструкции: элемент конструкции (WeldMemberFeat) по отрезку вдоль X из профиля .sldlfp
    (выгрузка IGS, Т-29).
    angle_deg — наклон отрезка во фронтальной плоскости: ось трубы не совпадает с осями детали. Массивы объектов API передаются VARIANT с VT_DISPATCH — иначе SolidWorks их не принимает."""
    import pythoncom
    import win32com.client
    doc = session.new_doc(paths.PART_TEMPLATE)
    doc.ClearSelection2(True)
    select_plane(doc, "front")
    sk = doc.SketchManager
    sk.InsertSketch(True)
    a = math.radians(angle_deg)
    segment = sk.CreateLine(0.0, 0.0, 0.0, length_mm / 1000.0 * math.cos(a), length_mm / 1000.0 * math.sin(a), 0.0)
    sk.InsertSketch(True)
    fm = doc.FeatureManager
    group = win32com.client.Dispatch(com.call(fm, "CreateStructuralMemberGroup")._oleobj_)
    group.Segments = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_DISPATCH, [segment._oleobj_])
    feat = fm.InsertStructuralWeldment4(str(profile), 1, True,
                                        win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_DISPATCH, [group._oleobj_]))
    if feat is None:
        raise RuntimeError(f"Элемент конструкции не построен: {profile}")
    set_material(doc, material)
    doc.ForceRebuild3(False)
    return doc


def tube_profile(name="40х40х2"):
    """Профиль сварных деталей из библиотеки инструментария: квадратная труба ГОСТ 8639-82."""
    return (paths.ROOT / "04_Библиотеки_Материалов_и_Профилей" / "Профили сварных деталей" / "Сортамент ГОСТ" /
            "Труба квадратная ГОСТ 8639-82" / f"{name}.sldlfp")


def analytic_mass_plate(length_mm, width_mm, thickness_mm, material):
    return length_mm * width_mm * thickness_mm * 1e-9 * material_library()[material]["density"]


def analytic_mass_square_tube(outer_mm, wall_mm, length_mm, material):
    inner = outer_mm - 2 * wall_mm
    return (outer_mm ** 2 - inner ** 2) * length_mm * 1e-9 * material_library()[material]["density"]


# --------------------------------------------------------------------------- сборки и чертежи
def assembly(session, components):
    """components: [(путь к модели, x_m, y_m, z_m)] — модели открываются на время вставки."""
    opened = {}
    for path, *_ in components:
        key = str(path).lower()
        if key not in opened and session.sw.GetOpenDocumentByName(str(path)) is None:
            opened[key] = session.open(path)
    asm = session.new_doc(paths.ASSEMBLY_TEMPLATE)
    for path, x, y, z in components:
        comp = asm.AddComponent5(str(path), 0, "", False, "", x, y, z)
        if comp is None:
            raise RuntimeError(f"Компонент не вставлен: {path}")
    return asm, list(opened.values())


def set_sheet_format(drw, format_path, width_mm, height_mm, sheet_name=None):
    sheet = com.dyn(drw.GetCurrentSheet)
    name = sheet_name or str(sheet.GetName)
    ok = drw.SetupSheet5(name, 12, 12, 1.0, 1.0, True, str(format_path), width_mm / 1000.0, height_mm / 1000.0,
                         "По умолчанию", True)
    if not ok:
        raise RuntimeError(f"Форматка не применена: {format_path}")


def add_sheet(drw, name, format_path, width_mm, height_mm):
    ok = drw.NewSheet3(name, 12, 12, 1.0, 1.0, True, str(format_path), width_mm / 1000.0, height_mm / 1000.0, "")
    if not ok:
        raise RuntimeError(f"Лист {name} не добавлен")
    drw.ActivateSheet(name)


def model_view(drw, model_path, x_mm, y_mm, orientation="*Спереди"):
    for name in (orientation, "*Front", "*Изометрия", "*Isometric"):
        try:
            view = drw.CreateDrawViewFromModelView3(str(model_path), name, x_mm / 1000.0, y_mm / 1000.0, 0.0)
            if view is not None:
                return com.dyn(view)
        except Exception:
            pass
    raise RuntimeError(f"Вид модели не вставлен: {model_path}")


def wait(seconds=1.0):
    time.sleep(seconds)


def sheet_format(name):
    return paths.SHEET_FORMATS / f"{name}.slddrt"


FILE_NAME_RE = re.compile(r"^(?P<desig>[^ ]+)(?: (?P<code>СБ|ГЧ|МЧ|ВО|ТУ|ТБ|ПЭ|Э\d|СХ|СЭ|ВП|СП))?(?: (?P<title>.+))?$")
