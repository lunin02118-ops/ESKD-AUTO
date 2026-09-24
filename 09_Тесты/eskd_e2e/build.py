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
    """material=None — материал не назначается: так деталь приходит от конструктора, пока он его не выбрал (Р-8)."""
    if material is None:
        return
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


def sheet_metal_flange(doc, rect_mm, thickness_mm, tab=False):
    """Прямоугольник листа (x1, y1, x2, y2, мм) на фронтальной плоскости: на детали без тел — базовая кромка (SheetMetal
    и развёртка, выгрузка DXF, Т-28), на уже листовой детали (tab=True) — язычок, слитый с листом: так конструктор
    удлиняет лист в исполнении. Merge = False у базовой кромки: с объединением SolidWorks кромку на пустой детали не строит."""
    x1, y1, x2, y2 = rect_mm
    sketch_rectangles(doc, [(x1 / 1000.0, y1 / 1000.0, x2 / 1000.0, y2 / 1000.0)])
    t = thickness_mm / 1000.0
    feat = doc.FeatureManager.InsertSheetMetalBaseFlange2(t, False, t, 0.0254, 0.01, False, 0, 0, 1, com.null_dispatch(),
                                                           False, 0, 0.0001, 0.0001, 0.5, True, tab, True, True)
    if feat is None:
        raise RuntimeError("Язычок листовой детали не построен" if tab else "Базовая кромка листовой детали не построена")
    return com.dyn(feat)


def sheet_metal_plate(session, length_mm, width_mm, thickness_mm, material):
    """Листовая деталь — базовая кромка из прямоугольника: есть SheetMetal и развёртка (выгрузка DXF, Т-28)."""
    doc = session.new_doc(paths.PART_TEMPLATE)
    sheet_metal_flange(doc, (-length_mm / 2, -width_mm / 2, length_mm / 2, width_mm / 2), thickness_mm)
    set_material(doc, material)
    doc.ForceRebuild3(False)
    return doc


def sheet_metal_two_bodies(session, t1_mm, t2_mm, material):
    """Многотельная листовая деталь: две базовые кромки на непересекающихся прямоугольниках, у каждой свой «Листовой
    металл» и своя толщина (сверка SW API 23.09.2026, №39). Толщина второй кромки сама по себе не держится: SolidWorks
    ведёт все тела по параметрам листа детали, и оба тела выходили толщиной последней кромки (проба 23.09.2026). Своя
    толщина — только у «Листового металла» со своими параметрами (SetOverrideDefaultParameter2)."""
    doc = session.new_doc(paths.PART_TEMPLATE)
    for x0, thickness_mm in ((0.0, t1_mm), (0.3, t2_mm)):
        sketch_rectangles(doc, [(x0 - 0.1, -0.05, x0 + 0.1, 0.05)])
        t = thickness_mm / 1000.0
        feat = doc.FeatureManager.InsertSheetMetalBaseFlange2(t, False, t, 0.0254, 0.01, False, 0, 0, 1, com.null_dispatch(),
                                                               False, 0, 0.0001, 0.0001, 0.5, True, False, True, True)
        if feat is None:
            raise RuntimeError(f"Базовая кромка {thickness_mm} мм не построена")
    sheets = []
    feat = com.call(doc, "FirstFeature")
    while feat is not None:
        if str(com.call(feat, "GetTypeName2")) == "SheetMetal":
            sheets.append(feat)
        feat = com.call(feat, "GetNextFeature")
    for feat, thickness_mm in zip(sheets, (t1_mm, t2_mm)):
        data = com.call(feat, "GetDefinition")
        com.call(data, "AccessSelections", doc, com.null_dispatch())
        com.call(data, "SetOverrideDefaultParameter", True)
        for parameter in (0, 1, 2):  # swSheetMetalOverrideDefaultParameters_e: гибка, допуск на изгиб, разгрузка
            com.call(data, "SetOverrideDefaultParameter2", parameter, True)
        com.dyn(data).Thickness = thickness_mm / 1000.0
        if not com.call(feat, "ModifyDefinition", data, doc, com.null_dispatch()):
            raise RuntimeError(f"Толщина {thickness_mm} мм «Листового металла» не задана")
    set_material(doc, material)
    doc.ForceRebuild3(False)
    got = []
    for body in com.as_list(com.dyn(doc).GetBodies2(0, False)) or []:
        box = com.as_list(com.call(body, "GetBodyBox"))
        got.append(round(min(abs(box[k + 3] - box[k]) for k in range(3)) * 1000.0, 3))
    if sorted(got) != sorted(float(v) for v in (t1_mm, t2_mm)):
        raise RuntimeError(f"Тела листовой детали вышли толщиной {got}, а нужно {t1_mm} и {t2_mm} мм")
    return doc


def sheet_metal_outline(session, outline, thickness_mm, material):
    """Листовая деталь из замкнутого контура на фронтальной плоскости: outline — список отрезков ((x1, y1), (x2, y2))
    и дуг ((cx, cy), (x1, y1), (x2, y2)) против часовой, мм; («круг», (cx, cy), r) — окружность. Для замера рамки
    развёртки: круг, дуги, контур под углом к осям."""
    doc = session.new_doc(paths.PART_TEMPLATE)
    doc.ClearSelection2(True)
    select_plane(doc, "front")
    sk = doc.SketchManager
    sk.InsertSketch(True)
    sk.AddToDB = True
    for item in outline:
        if item[0] == "круг":
            (cx, cy), r = item[1], item[2]
            sk.CreateCircleByRadius(cx / 1000.0, cy / 1000.0, 0.0, r / 1000.0)
        elif len(item) == 2:
            (x1, y1), (x2, y2) = item
            sk.CreateLine(x1 / 1000.0, y1 / 1000.0, 0.0, x2 / 1000.0, y2 / 1000.0, 0.0)
        else:
            (cx, cy), (x1, y1), (x2, y2) = item
            sk.CreateArc(cx / 1000.0, cy / 1000.0, 0.0, x1 / 1000.0, y1 / 1000.0, 0.0, x2 / 1000.0, y2 / 1000.0, 0.0, 1)
    sk.AddToDB = False
    sk.InsertSketch(True)
    t = thickness_mm / 1000.0
    feat = doc.FeatureManager.InsertSheetMetalBaseFlange2(t, False, t, 0.0254, 0.01, False, 0, 0, 1, com.null_dispatch(),
                                                          False, 0, 0.0001, 0.0001, 0.5, True, False, True, True)
    if feat is None:
        raise RuntimeError("Базовая кромка листовой детали из контура не построена")
    set_material(doc, material)
    doc.ForceRebuild3(False)
    return doc


def features_of_type(doc, type_name):
    """Элементы дерева верхнего уровня с GetTypeName2 == type_name, по порядку дерева."""
    out = []
    feat = doc.FirstFeature
    while feat is not None:
        feat = com.dyn(feat)
        if str(feat.GetTypeName2) == type_name:
            out.append(feat)
        feat = feat.GetNextFeature
    return out


def sheet_thickness_mm(doc):
    """Толщина листовой детали по элементу «Листовой металл», мм; None — деталь не листовая. Так её читает надстройка."""
    for feat in features_of_type(doc, "SheetMetal"):
        data = com.dyn(feat.GetDefinition)
        if data is not None:
            return round(float(data.Thickness) * 1000.0, 4)
    return None


def _delete_feature(doc, feat):
    """DeleteSelection2(swDelete_Children | swDelete_Absorbed) — без вопросов."""
    doc.ClearSelection2(True)
    name = str(feat.Name)
    if not feat.Select2(False, 0) or not doc.Extension.DeleteSelection2(1 | 2):
        raise RuntimeError(f"Элемент «{name}» не удалён")
    doc.ClearSelection2(True)


def delete_extrusions(doc):
    """Снять с детали все вытягивания и их эскизы — с последнего: тел не остаётся, а сам документ, его конфигурации,
    свойства и материал те же, поэтому сборки и чертежи, которые на него ссылаются, остаются с ним связаны.
    Эскиз SolidWorks 2025 оставляет в дереве и с флагом «поглощённые» — он удаляется отдельно (в фикстурах других
    эскизов нет)."""
    extrusions = features_of_type(doc, "Extrusion")
    if not extrusions:
        raise RuntimeError("В детали нет вытягиваний")
    for feat in reversed(extrusions):
        _delete_feature(doc, feat)
    for sketch in reversed(features_of_type(doc, "ProfileFeature")):
        _delete_feature(doc, sketch)
    if features_of_type(doc, "Extrusion") or features_of_type(doc, "ProfileFeature") or com.as_list(doc.GetBodies2(0, True)):
        raise RuntimeError("После удаления вытягиваний в детали остались эскизы или тела")


def fold_sheet_metal(doc):
    """Лист согнут во всех конфигурациях: «Развёртка» (FlatPattern) погашена в каждой явно — и в исполнениях, созданных
    раньше листа (деталь перестроена на месте). IsSuppressed2 по чужой конфигурации отвечает для развёртки неверно;
    состояние видно по IsSuppressed, когда конфигурация активна."""
    names = com.str_array([str(c) for c in com.as_list(doc.GetConfigurationNames)])
    for flat in features_of_type(doc, "FlatPattern"):
        if not flat.SetSuppression2(0, 3, names):
            raise RuntimeError("Развёртка не погашена во всех конфигурациях")
    doc.ForceRebuild3(False)


def sheet_metal_angle(session, leg_mm, flange_mm, depth_mm, thickness_mm, material):
    """Гнутая листовая деталь — уголок: базовая кромка из открытого L-эскиза (полка leg_mm, отгиб flange_mm)
    глубиной depth_mm. Развёртка длиннее полки, но короче суммы полки и отгиба; габарит согнутой детали —
    leg×depth×flange."""
    doc = session.new_doc(paths.PART_TEMPLATE)
    doc.ClearSelection2(True)
    select_plane(doc, "front")
    sk = doc.SketchManager
    sk.InsertSketch(True)
    # Без AddToDB: концы отрезков должны слиться в одну цепочку, иначе SolidWorks строит из открытого
    # контура плоский «тонкий» элемент, а не гнутую кромку.
    sk.CreateLine(0.0, 0.0, 0.0, leg_mm / 1000.0, 0.0, 0.0)
    sk.CreateLine(leg_mm / 1000.0, 0.0, 0.0, leg_mm / 1000.0, flange_mm / 1000.0, 0.0)
    sk.InsertSketch(True)
    t = thickness_mm / 1000.0
    # InsertSheetMetalBaseFlange2(Thickness, ThickenDir, Radius, ExtrudeDist1, ExtrudeDist2, FlipExtruDir, EndCondition1,
    # EndCondition2, DirToUse, PCBA, UseDefaultRelief, ReliefType, ReliefWidth, ReliefDepth, ReliefRatio, UseReliefRatio, …):
    # для открытого контура толщина — первый параметр, глубина вытяжки — четвёртый (по библиотеке типов SolidWorks).
    feat = doc.FeatureManager.InsertSheetMetalBaseFlange2(t, False, t, depth_mm / 1000.0, 0.0, False, 0, 0, 1,
                                                           com.null_dispatch(), True, 0, 0.0001, 0.0001, 0.5, True, False, True, True)
    if feat is None:
        raise RuntimeError("Базовая кромка уголка не построена")
    set_material(doc, material)
    doc.ForceRebuild3(False)
    return doc


def weldment_feature(doc):
    """Элемент «Сварная деталь» — его SolidWorks ставит сам, когда конструктор добавляет первый элемент конструкции.
    InsertStructuralWeldment4 его не добавляет, и у такой детали нет списка вырезов — только «Твердые тела»:
    SetAutomaticCutList и UpdateCutList отвечают false, папок элементов нет (проба 23.09.2026)."""
    # Метод без аргументов позднее связывание выполнило бы как чтение свойства, а скобки вызвали бы найденный элемент.
    feat = com.call(doc.FeatureManager, "InsertWeldmentFeature")
    if feat is None:
        raise RuntimeError("Элемент «Сварная деталь» не вставлен")
    return feat


def structural_tube(session, length_mm, profile, material, angle_deg=0.0, weldment=False):
    """Деталь сварной конструкции: элемент конструкции (WeldMemberFeat) по отрезку вдоль X из профиля .sldlfp
    (выгрузка IGS, Т-29).
    angle_deg — наклон отрезка во фронтальной плоскости: ось трубы не совпадает с осями детали.
    weldment — сначала элемент «Сварная деталь», как у детали из окна SolidWorks: со списком вырезов."""
    doc = session.new_doc(paths.PART_TEMPLATE)
    if weldment:
        weldment_feature(doc)
    add_structural_member(doc, length_mm, profile, angle_deg)
    set_material(doc, material)
    doc.ForceRebuild3(False)
    return doc


def add_structural_member(doc, length_mm, profile, angle_deg=0.0, start_mm=(0.0, 0.0)):
    """Элемент конструкции в деталь: свой эскиз на фронтальной плоскости с отрезком из start_mm под углом angle_deg.
    Массивы объектов API передаются VARIANT с VT_DISPATCH — иначе SolidWorks их не принимает."""
    import pythoncom
    import win32com.client
    doc.ClearSelection2(True)
    select_plane(doc, "front")
    sk = doc.SketchManager
    sk.InsertSketch(True)
    a = math.radians(angle_deg)
    x0, y0 = start_mm[0] / 1000.0, start_mm[1] / 1000.0
    segment = sk.CreateLine(x0, y0, 0.0, x0 + length_mm / 1000.0 * math.cos(a), y0 + length_mm / 1000.0 * math.sin(a), 0.0)
    sk.InsertSketch(True)
    fm = doc.FeatureManager
    group = win32com.client.Dispatch(com.call(fm, "CreateStructuralMemberGroup")._oleobj_)
    group.Segments = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_DISPATCH, [segment._oleobj_])
    feat = fm.InsertStructuralWeldment4(str(profile), 1, True,
                                        win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_DISPATCH, [group._oleobj_]))
    if feat is None:
        raise RuntimeError(f"Элемент конструкции не построен: {profile}")
    return feat


def structural_tube_executions(session, members, profile, material, cut_list=False):
    """Деталь сварной конструкции с исполнением на каждый элемент: members — [(длина, угол, (x, y) начала[, профиль])], мм и
    градусы; профиль элемента по умолчанию — profile. Первое исполнение — исходная конфигурация, дальше «01», «02»…;
    элемент i погашен во всех исполнениях, кроме своего (как «Укосина» с разной трубой в исполнениях). cut_list — список
    вырезов обновлён в каждом исполнении: у свежей детали папок элементов нет; тогда сначала ставится элемент «Сварная
    деталь» — без него списка вырезов нет вовсе. Возвращает (doc, имена исполнений по порядку элементов)."""
    doc = session.new_doc(paths.PART_TEMPLATE)
    if cut_list:
        weldment_feature(doc)
    features = [add_structural_member(doc, m[0], m[3] if len(m) > 3 else profile, m[1], m[2]) for m in members]
    set_material(doc, material)
    doc.ForceRebuild3(False)
    names = [str(doc.GetActiveConfiguration.Name)]
    for i in range(1, len(members)):
        names.append(f"{i:02d}")
        add_configuration(doc, names[-1])
    show_configuration(doc, names[0])
    for feat, own in zip(features, names):
        others = [name for name in names if name != own]
        # 0 — погасить, 3 — в указанных конфигурациях.
        if not com.dyn(feat).SetSuppression2(0, 3, com.str_array(others)):
            raise RuntimeError(f"Элемент не погашен в {others}")
    doc.ForceRebuild3(False)
    if cut_list:
        for name in reversed(names):
            show_configuration(doc, name)
            doc.ForceRebuild3(False)
            update_cut_list(doc)
    return doc, names


def cut_list_root(doc):
    """Папка «Список вырезов» (SolidBodyFolder); None — её нет."""
    feat = com.call(doc, "FirstFeature")
    while feat is not None:
        if str(com.call(feat, "GetTypeName2")) == "SolidBodyFolder":
            return feat
        feat = com.call(feat, "GetNextFeature")
    return None


def update_cut_list(doc):
    """Обновить список вырезов, как «Обновить» в дереве: автоматический список включается, папки элементов строятся."""
    root = cut_list_root(doc)
    if root is None:
        raise RuntimeError("В детали нет папки «Список вырезов»")
    folder = com.call(root, "GetSpecificFeature2")
    com.call(folder, "SetAutomaticCutList", True)
    ok = com.call(folder, "UpdateCutList")
    doc.ForceRebuild3(False)
    return ok


def cut_list_folders(doc):
    """[(имя папки, тел в активном исполнении)] списка вырезов — вместе с папками без тел и вложенными в подсварки."""
    found = []

    def walk(parent):
        sub = com.call(parent, "GetFirstSubFeature")
        while sub is not None:
            kind = str(com.call(sub, "GetTypeName2"))
            if kind == "SubWeldFolder":
                walk(sub)
            elif kind == "CutListFolder":
                found.append((str(com.dyn(sub).Name), int(com.call(com.call(sub, "GetSpecificFeature2"), "GetBodyCount"))))
            sub = com.call(sub, "GetNextSubFeature")

    root = cut_list_root(doc)
    if root is not None:
        walk(root)
    return found


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
