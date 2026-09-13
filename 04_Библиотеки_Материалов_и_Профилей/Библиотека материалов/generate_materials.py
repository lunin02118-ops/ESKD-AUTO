#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Генератор библиотеки материалов SolidWorks (.sldmat) по ГОСТ.
Поддерживает иерархическую структуру категорий, формирование дробей ЕСКД (<STACK>...<OVER>...</STACK>),
пользовательские свойства (<custom><prop...>) и физико-механические характеристики.
"""

import os
import sys
import csv
import json
from typing import List, Dict, Any, Optional

# Стандартные физические свойства основных марок сталей
DEFAULT_STEEL_PROPERTIES = {
    "Ст3сп": {
        "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
        "KX": 47.0, "C": 460.0, "SIGXT": 370.0e6, "SIGYLD": 245.0e6
    },
    "Сталь 10": {
        "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
        "KX": 48.0, "C": 460.0, "SIGXT": 353.0e6, "SIGYLD": 216.0e6
    },
    "В 10": {
        "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
        "KX": 48.0, "C": 460.0, "SIGXT": 353.0e6, "SIGYLD": 216.0e6
    },
    "Сталь 20": {
        "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
        "KX": 48.0, "C": 460.0, "SIGXT": 412.0e6, "SIGYLD": 245.0e6
    },
    "В 20": {
        "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
        "KX": 48.0, "C": 460.0, "SIGXT": 412.0e6, "SIGYLD": 245.0e6
    },
    "Сталь 45": {
        "EX": 2.05e11, "NUXY": 0.28, "GXY": 8.0e10, "ALPX": 1.15e-5, "DENS": 7850.0,
        "KX": 43.0, "C": 460.0, "SIGXT": 600.0e6, "SIGYLD": 355.0e6
    },
    "40Х": {
        "EX": 2.11e11, "NUXY": 0.28, "GXY": 8.1e10, "ALPX": 1.2e-5, "DENS": 7820.0,
        "KX": 44.0, "C": 460.0, "SIGXT": 980.0e6, "SIGYLD": 785.0e6
    },
    "09Г2С": {
        "EX": 2.06e11, "NUXY": 0.28, "GXY": 8.0e10, "ALPX": 1.2e-5, "DENS": 7850.0,
        "KX": 45.0, "C": 470.0, "SIGXT": 490.0e6, "SIGYLD": 345.0e6
    },
    "12Х18Н10Т": {
        "EX": 1.98e11, "NUXY": 0.28, "GXY": 7.7e10, "ALPX": 1.65e-5, "DENS": 7900.0,
        "KX": 15.0, "C": 500.0, "SIGXT": 540.0e6, "SIGYLD": 220.0e6
    }
}

class MaterialItem:
    def __init__(
        self,
        category: str,
        name: str,
        assortment_numerator: str,
        material_denominator: str,
        size: str = "",
        gost_assortment: str = "",
        steel_grade: str = "",
        gost_material: str = "",
        props: Optional[Dict[str, float]] = None,
        hatch: str = "DIN 201 сталь и отлитый из стали металл"
    ):
        self.category = category
        self.name = name
        self.assortment_numerator = assortment_numerator
        self.material_denominator = material_denominator
        self.size = size
        self.gost_assortment = gost_assortment
        self.steel_grade = steel_grade
        self.gost_material = gost_material
        self.hatch = hatch
        
        # Дробное обозначение для графы 3 штампа чертежа
        if assortment_numerator and material_denominator:
            self.fraction_eskd = f"<STACK size=1>{assortment_numerator}<OVER>{material_denominator}</STACK>"
            self.single_line_eskd = f"{assortment_numerator} / {material_denominator}"
        elif assortment_numerator:
            self.fraction_eskd = assortment_numerator
            self.single_line_eskd = assortment_numerator
        else:
            self.fraction_eskd = material_denominator
            self.single_line_eskd = material_denominator
            
        # Физические свойства
        if props:
            self.props = props
        else:
            matched_props = None
            for grade, p in DEFAULT_STEEL_PROPERTIES.items():
                if grade.lower() in steel_grade.lower() or grade.lower() in material_denominator.lower():
                    matched_props = p.copy()
                    break
            self.props = matched_props or DEFAULT_STEEL_PROPERTIES["Ст3сп"].copy()

def escape_xml(s: str) -> str:
    """Экранирует специальные символы XML."""
    if not s:
        return ""
    return (
        s.replace("&", "&amp;")
         .replace("<", "&lt;")
         .replace(">", "&gt;")
         .replace('"', "&quot;")
         .replace("'", "&apos;")
    )

def build_sldmat_xml(items: List[MaterialItem]) -> str:
    """Генерирует XML-строку библиотеки .sldmat по спецификации SolidWorks."""
    categories: Dict[str, List[MaterialItem]] = {}
    for item in items:
        categories.setdefault(item.category, []).append(item)

    lines = []
    lines.append('<?xml version="1.0" encoding="UTF-16" standalone="no"?>')
    lines.append('<mstns:materials xmlns:mstns="http://www.solidworks.com/sldmaterials" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:schemaLocation="http://matwebtest.matweb.com/sw/XSD/sldmaterials.xsd" xmlns:sldcolorswatch="http://www.solidworks.com/sldcolorswatch" version="2008.03">')
    lines.append('\t<curves id="curve0">')
    lines.append('\t\t<point x="1.0" y="1.0"/>')
    lines.append('\t\t<point x="2.0" y="1.0"/>')
    lines.append('\t\t<point x="3.0" y="1.0"/>')
    lines.append('\t</curves>')

    mat_id_counter = 1000

    for cat_name, cat_items in categories.items():
        lines.append(f'\t<classification name="{escape_xml(cat_name)}">')
        for item in cat_items:
            mat_id_counter += 1
            desc_attr = escape_xml(item.fraction_eskd)
            name_attr = escape_xml(item.name)
            lines.append(f'\t\t<material name="{name_attr}" description="{desc_attr}" matid="{mat_id_counter}" envdata="0" appdata="">')
            
            # Шейдеры и текстуры
            lines.append('\t\t\t<shaders>')
            lines.append('\t\t\t\t<pwshader2 path="\\metal\\steel\\polished steel.p2m" name="polished steel" isNewShader="1"/>')
            lines.append('\t\t\t\t<cgshader2 name="Polishedsteel"/>')
            lines.append('\t\t\t\t<pwshader name="сталь"/>')
            lines.append('\t\t\t\t<cgshader name="steel"/>')
            lines.append('\t\t\t\t<swtexture path="images\\textures\\metal\\cast\\cast_fine.jpg"/>')
            lines.append('\t\t\t</shaders>')
            
            # Цвет
            lines.append('\t\t\t<swatchcolor RGB="c0c0c0">')
            lines.append('\t\t\t\t<sldcolorswatch:Optical Ambient="1" Transparency="0" Diffuse="0.79" Specularity="0.88" Shininess="0.21" Emission="0.21"/>')
            lines.append('\t\t\t</swatchcolor>')
            
            # Штриховка
            lines.append(f'\t\t\t<xhatch name="{escape_xml(item.hatch)}" angle="0.0" scale="1.0"/>')
            
            # Физические свойства
            p = item.props
            lines.append('\t\t\t<physicalproperties>')
            lines.append(f'\t\t\t\t<EX displayname="Модуль упругости" value="{p.get("EX", 2.0e11)}" usepropertycurve="0"/>')
            lines.append(f'\t\t\t\t<NUXY displayname="Коэффициент Пуассона" value="{p.get("NUXY", 0.28)}" usepropertycurve="0"/>')
            lines.append(f'\t\t\t\t<GXY displayname="Модуль сдвига" value="{p.get("GXY", 7.9e10)}" usepropertycurve="0"/>')
            lines.append(f'\t\t\t\t<ALPX displayname="Коэффициент теплового расширения" value="{p.get("ALPX", 1.2e-5)}" usepropertycurve="0"/>')
            lines.append(f'\t\t\t\t<DENS displayname="Массовая плотность" value="{p.get("DENS", 7850.0)}" usepropertycurve="0"/>')
            lines.append(f'\t\t\t\t<KX displayname="Теплопроводность" value="{p.get("KX", 47.0)}" usepropertycurve="0"/>')
            lines.append(f'\t\t\t\t<C displayname="Удельная теплоемкость" value="{p.get("C", 460.0)}" usepropertycurve="0"/>')
            lines.append(f'\t\t\t\t<SIGXT displayname="Предел прочности при растяжении" value="{p.get("SIGXT", 370.0e6)}" usepropertycurve="0"/>')
            lines.append(f'\t\t\t\t<SIGYLD displayname="Предел текучести" value="{p.get("SIGYLD", 245.0e6)}" usepropertycurve="0"/>')
            lines.append('\t\t\t</physicalproperties>')
            
            # Кастомные свойства SolidWorks
            lines.append('\t\t\t<custom>')
            lines.append(f'\t\t\t\t<prop name="Сортамент" description="" value="{escape_xml(item.assortment_numerator)}" units=""/>')
            lines.append(f'\t\t\t\t<prop name="Типоразмер" description="" value="{escape_xml(item.size)}" units=""/>')
            lines.append(f'\t\t\t\t<prop name="ГОСТ_Сортамент" description="" value="{escape_xml(item.gost_assortment)}" units=""/>')
            lines.append(f'\t\t\t\t<prop name="Марка_Материала" description="" value="{escape_xml(item.steel_grade)}" units=""/>')
            lines.append(f'\t\t\t\t<prop name="ГОСТ_Материал" description="" value="{escape_xml(item.gost_material)}" units=""/>')
            lines.append(f'\t\t\t\t<prop name="Обозначение_ГОСТ" description="" value="{escape_xml(item.fraction_eskd)}" units=""/>')
            lines.append(f'\t\t\t\t<prop name="Обозначение_Строка" description="" value="{escape_xml(item.single_line_eskd)}" units=""/>')
            lines.append('\t\t\t</custom>')
            
            lines.append('\t\t</material>')
        lines.append('\t</classification>')

    lines.append('</mstns:materials>\n')
    return '\n'.join(lines)

def get_pilot_items() -> List[MaterialItem]:
    """Возвращает 6 пилотных позиций + базовые марки сталей."""
    items = [
        # --- 01. Прокат листовой горячекатаный (ГОСТ 19903-2015) ---
        MaterialItem(
            category="01. Прокат листовой горячекатаный (ГОСТ 19903-2015)",
            name="Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89",
            assortment_numerator="Лист Б-ПН-НО-4,0 ГОСТ 19903-2015",
            material_denominator="Ст3сп ГОСТ 14637-2024",
            size="4,0",
            gost_assortment="ГОСТ 19903-2015",
            steel_grade="Ст3сп",
            gost_material="ГОСТ 14637-2024",
            props={
                "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
                "KX": 47.0, "C": 460.0, "SIGXT": 370.0e6, "SIGYLD": 245.0e6
            }
        ),
        MaterialItem(
            category="01. Прокат листовой горячекатаный (ГОСТ 19903-2015)",
            name="Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89",
            assortment_numerator="Лист Б-ПН-НО-6,0 ГОСТ 19903-2015",
            material_denominator="Ст3сп ГОСТ 14637-2024",
            size="6,0",
            gost_assortment="ГОСТ 19903-2015",
            steel_grade="Ст3сп",
            gost_material="ГОСТ 14637-2024",
            props={
                "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
                "KX": 47.0, "C": 460.0, "SIGXT": 370.0e6, "SIGYLD": 235.0e6
            }
        ),

        # --- 02. Трубы стальные профильные квадратные (ГОСТ 8639-82) ---
        MaterialItem(
            category="02. Трубы стальные профильные квадратные (ГОСТ 8639-82)",
            name="Труба 80х80х4 ГОСТ 8639-82 / В 10 ГОСТ 13663-86",
            assortment_numerator="Труба 80х80х4 ГОСТ 8639-82",
            material_denominator="В 10 ГОСТ 13663-86",
            size="80х80х4",
            gost_assortment="ГОСТ 8639-82",
            steel_grade="В 10",
            gost_material="ГОСТ 13663-86",
            props={
                "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
                "KX": 48.0, "C": 460.0, "SIGXT": 353.0e6, "SIGYLD": 216.0e6
            }
        ),
        MaterialItem(
            category="02. Трубы стальные профильные квадратные (ГОСТ 8639-82)",
            name="Труба 100х100х4 ГОСТ 8639-82 / В 10 ГОСТ 13663-86",
            assortment_numerator="Труба 100х100х4 ГОСТ 8639-82",
            material_denominator="В 10 ГОСТ 13663-86",
            size="100х100х4",
            gost_assortment="ГОСТ 8639-82",
            steel_grade="В 10",
            gost_material="ГОСТ 13663-86",
            props={
                "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
                "KX": 48.0, "C": 460.0, "SIGXT": 353.0e6, "SIGYLD": 216.0e6
            }
        ),

        # --- 03. Трубы стальные бесшовные горячедеформированные (ГОСТ 8732-78) ---
        MaterialItem(
            category="03. Трубы стальные бесшовные горячедеформированные (ГОСТ 8732-78)",
            name="Труба 57х3,5 ГОСТ 8732-78 / В 10 ГОСТ 8731-74",
            assortment_numerator="Труба 57х3,5 ГОСТ 8732-78",
            material_denominator="В 10 ГОСТ 8731-74",
            size="57х3,5",
            gost_assortment="ГОСТ 8732-78",
            steel_grade="В 10",
            gost_material="ГОСТ 8731-74",
            props={
                "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
                "KX": 48.0, "C": 460.0, "SIGXT": 353.0e6, "SIGYLD": 216.0e6
            }
        ),
        MaterialItem(
            category="03. Трубы стальные бесшовные горячедеформированные (ГОСТ 8732-78)",
            name="Труба 102х4 ГОСТ 8732-78 / В 20 ГОСТ 8731-74",
            assortment_numerator="Труба 102х4 ГОСТ 8732-78",
            material_denominator="В 20 ГОСТ 8731-74",
            size="102х4",
            gost_assortment="ГОСТ 8732-78",
            steel_grade="В 20",
            gost_material="ГОСТ 8731-74",
            props={
                "EX": 2.0e11, "NUXY": 0.28, "GXY": 7.9e10, "ALPX": 1.2e-5, "DENS": 7850.0,
                "KX": 48.0, "C": 460.0, "SIGXT": 412.0e6, "SIGYLD": 245.0e6
            }
        ),

        # --- 04. Базовые марки сталей (для деталей мехобработки) ---
        MaterialItem(
            category="04. Марки сталей и сплавов (Базовые материалы)",
            name="Сталь 3сп (ГОСТ 380-2005)",
            assortment_numerator="",
            material_denominator="Ст3сп ГОСТ 380-2005",
            steel_grade="Ст3сп",
            gost_material="ГОСТ 380-2005",
            props=DEFAULT_STEEL_PROPERTIES["Ст3сп"]
        ),
        MaterialItem(
            category="04. Марки сталей и сплавов (Базовые материалы)",
            name="Сталь 20 (ГОСТ 1050-2013)",
            assortment_numerator="",
            material_denominator="Сталь 20 ГОСТ 1050-2013",
            steel_grade="Сталь 20",
            gost_material="ГОСТ 1050-2013",
            props=DEFAULT_STEEL_PROPERTIES["Сталь 20"]
        ),
        MaterialItem(
            category="04. Марки сталей и сплавов (Базовые материалы)",
            name="Сталь 45 (ГОСТ 1050-2013)",
            assortment_numerator="",
            material_denominator="Сталь 45 ГОСТ 1050-2013",
            steel_grade="Сталь 45",
            gost_material="ГОСТ 1050-2013",
            props=DEFAULT_STEEL_PROPERTIES["Сталь 45"]
        ),
        MaterialItem(
            category="04. Марки сталей и сплавов (Базовые материалы)",
            name="Сталь 40Х (ГОСТ 4543-2016)",
            assortment_numerator="",
            material_denominator="40Х ГОСТ 4543-2016",
            steel_grade="40Х",
            gost_material="ГОСТ 4543-2016",
            props=DEFAULT_STEEL_PROPERTIES["40Х"]
        ),
        MaterialItem(
            category="04. Марки сталей и сплавов (Базовые материалы)",
            name="Сталь 09Г2С (ГОСТ 19281-2014)",
            assortment_numerator="",
            material_denominator="09Г2С ГОСТ 19281-2014",
            steel_grade="09Г2С",
            gost_material="ГОСТ 19281-2014",
            props=DEFAULT_STEEL_PROPERTIES["09Г2С"]
        )
    ]
    return items

def load_items_from_csv(csv_path: str) -> List[MaterialItem]:
    """Загружает список материалов из CSV-файла ограничительного перечня."""
    items = []
    with open(csv_path, mode="r", encoding="utf-8-sig") as f:
        reader = csv.DictReader(f, delimiter=";")
        for row in reader:
            item = MaterialItem(
                category=row.get("Категория", "").strip(),
                name=row.get("Имя_в_дереве", "").strip(),
                assortment_numerator=row.get("Числитель_Сортамент", "").strip(),
                material_denominator=row.get("Знаменатель_Материал", "").strip(),
                size=row.get("Типоразмер", "").strip(),
                gost_assortment=row.get("ГОСТ_Сортамент", "").strip(),
                steel_grade=row.get("Марка_Материала", "").strip(),
                gost_material=row.get("ГОСТ_Материал", "").strip(),
                hatch=row.get("Штриховка", "DIN 201 сталь и отлитый из стали металл").strip()
            )
            items.append(item)
    return items

def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    output_path = os.path.join(script_dir, "Библиотека_Материалов_ГОСТ.sldmat")
    
    csv_file = None
    if len(sys.argv) > 1 and sys.argv[1].endswith(".csv"):
        csv_file = sys.argv[1]

    if csv_file and os.path.exists(csv_file):
        print(f"Загрузка позиций из CSV: {csv_file}")
        items = load_items_from_csv(csv_file)
    else:
        print("Используется стандартный набор (пилотные позиции + базовые стали)")
        items = get_pilot_items()

    print(f"Генерация файла базы материалов SolidWorks: {output_path}")
    xml_content = build_sldmat_xml(items)
    
    with open(output_path, "w", encoding="utf-16", newline="\r\n") as f:
        f.write(xml_content)
        
    print(f"Готово! Сгенерировано материалов: {len(items)}")

if __name__ == "__main__":
    main()
