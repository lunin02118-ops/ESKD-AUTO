# -*- coding: utf-8 -*-
"""
Tier 1: Статический аудит целостности и конфигураций экосистемы SolidWorks 2025.
Проверяет кодировки, целостность макросов, отсутствие битых путей и валидность XML.
"""

import os
import sys
import xml.etree.ElementTree as ET

# Configure console output for UTF-8
try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

# Base directories
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
SWPLUS_DIR = os.path.join(ROOT_DIR, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0")
MATERIALS_DIR = os.path.join(ROOT_DIR, "04_Библиотеки_Материалов_и_Профилей", "Библиотека материалов")
REG_PROFILES_DIR = os.path.join(ROOT_DIR, "01_Настройки_SolidWorks", "Реестровые_Профили")
ADDIN_DIR = os.path.join(ROOT_DIR, "03_Макросы_и_Плагины", "ESKD_Material_Sync_Addin")

class TestResult:
    def __init__(self):
        self.passed = 0
        self.failed = 0
        self.warnings = 0
        self.errors = []
        self.warn_msgs = []

    def assert_true(self, condition, test_name, error_msg=""):
        if condition:
            self.passed += 1
            print(f"  [PASS] {test_name}")
        else:
            self.failed += 1
            msg = f"  [FAIL] {test_name}: {error_msg}"
            print(msg)
            self.errors.append(msg)

    def warn(self, test_name, msg):
        self.warnings += 1
        w_msg = f"  [WARN] {test_name}: {msg}"
        print(w_msg)
        self.warn_msgs.append(w_msg)


def test_file_encodings(res):
    print("\n--- Проверка кодировок файлов конфигурации ---")
    cp1251_files = [
        os.path.join(SWPLUS_DIR, "DProp", "DProp.ini"),
        os.path.join(SWPLUS_DIR, "MProp", "MProp.ini"),
        os.path.join(SWPLUS_DIR, "MProp", "MProp_Fam.txt"),
        os.path.join(SWPLUS_DIR, "MProp", "MProp_Firm.txt"),
        os.path.join(SWPLUS_DIR, "MProp", "MProp_Prof.txt"),
        os.path.join(SWPLUS_DIR, "Master", "Master.ini"),
        os.path.join(SWPLUS_DIR, "SProp", "SProp.ini"),
        os.path.join(SWPLUS_DIR, "SaveAsPDF", "SaveAsPDF.ini"),
        os.path.join(SWPLUS_DIR, "SaveAsPDF", "PDFCreator.dat"),
        os.path.join(SWPLUS_DIR, "SpecEditor", "SpecEditor.ini"),
        os.path.join(SWPLUS_DIR, "SpecEditor", "MyProperties_1.ini"),
        os.path.join(SWPLUS_DIR, "SpecEditor", "MyProperties_2.ini"),
        os.path.join(SWPLUS_DIR, "ТТ", "TT.ini"),
        os.path.join(SWPLUS_DIR, "ТТ", "TT_Prof.txt"),
    ]

    for p in cp1251_files:
        fname = os.path.relpath(p, ROOT_DIR)
        if not os.path.exists(p):
            res.assert_true(False, f"Существование {fname}", "Файл отсутствует!")
            continue

        with open(p, "rb") as f:
            raw = f.read()

        has_utf8_bom = raw.startswith(b"\xef\xbb\xbf")
        res.assert_true(not has_utf8_bom, f"{fname} без UTF-8 BOM", "Обнаружен UTF-8 BOM, что ломает VBA 7.1!")

        try:
            raw.decode("cp1251")
            res.assert_true(True, f"{fname} валидный Windows-1251")
        except UnicodeDecodeError as e:
            res.assert_true(False, f"{fname} валидный Windows-1251", f"Ошибка декодирования: {e}")


def test_swplus_macros_exist(res):
    print("\n--- Проверка наличия 9 макросов SWPlus и иконок ---")
    macros = [
        ("01 - MProp", "MProp/MProp.swp", "MProp/MProp.bmp"),
        ("02 - SProp", "SProp/SProp.swp", "SProp/SProp.bmp"),
        ("03 - DProp", "DProp/DProp.swp", "DProp/DProp.bmp"),
        ("04 - SpecEditor", "SpecEditor/SpecEditor.swp", "SpecEditor/SpecEditor.bmp"),
        ("05 - RecordDimM", "RecordDimM/RecordDimM.swp", "RecordDimM/RecordDimM.bmp"),
        ("06 - Roughness", "Roughness/Roughness.swp", "Roughness/Roughness.bmp"),
        ("07 - TT", "ТТ/TT.SWP", "ТТ/TT.BMP"),
        ("08 - Master", "Master/Master.swp", "Master/Master.bmp"),
        ("09 - SaveAsPDF", "SaveAsPDF/PDFCreator.swp", "SaveAsPDF/SaveAsPDF.bmp"),
    ]

    for label, swp_rel, bmp_rel in macros:
        swp_path = os.path.join(SWPLUS_DIR, swp_rel.replace("/", os.sep))
        bmp_path = os.path.join(SWPLUS_DIR, bmp_rel.replace("/", os.sep))

        swp_ok = os.path.isfile(swp_path) and os.path.getsize(swp_path) > 1000
        bmp_ok = os.path.isfile(bmp_path) and os.path.getsize(bmp_path) > 100

        res.assert_true(swp_ok, f"{label}: макрос {swp_rel}", "Файл макроса отсутствует или пуст!")
        res.assert_true(bmp_ok, f"{label}: иконка {bmp_rel}", "Файл иконки отсутствует!")


def test_materials_xml(res):
    print("\n--- Валидация XML библиотеки материалов ГОСТ ---")
    mat_file = os.path.join(MATERIALS_DIR, "Библиотека_Материалов_ГОСТ.sldmat")
    if not os.path.exists(mat_file):
        candidates = [f for f in os.listdir(MATERIALS_DIR) if f.endswith(".sldmat")]
        if candidates:
            mat_file = os.path.join(MATERIALS_DIR, candidates[0])

    res.assert_true(os.path.isfile(mat_file), f"Наличие библиотеки материалов ({os.path.basename(mat_file)})")

    try:
        tree = ET.parse(mat_file)
        root = tree.getroot()
        res.assert_true(root is not None, "Синтаксический парсинг XML .sldmat")

        materials = root.findall(".//material")
        res.assert_true(len(materials) > 0, f"Количество материалов в библиотеке: {len(materials)}")

        # Check properties from parsed XML tree directly (agnostic to file encoding)
        has_stack = False
        has_sortament = False
        has_gost = False

        for m in materials:
            custom = m.find("custom")
            if custom is not None:
                for prop in custom.findall("prop"):
                    val = prop.get("value", "")
                    name = prop.get("name", "")
                    if "<STACK" in val and "<OVER>" in val:
                        has_stack = True
                    if name == "Сортамент":
                        has_sortament = True
                    if name == "ГОСТ_Материал":
                        has_gost = True
            if has_stack and has_sortament and has_gost:
                break

        res.assert_true(has_stack, "Библиотека содержит дробные теги <STACK>...<OVER>")
        res.assert_true(has_sortament, "Библиотека содержит свойство 'Сортамент'")
        res.assert_true(has_gost, "Библиотека содержит свойство 'ГОСТ_Материал'")

    except Exception as e:
        res.assert_true(False, "Парсинг XML .sldmat", str(e))




def test_favorites_materials_valid(res):
    """Часто используемые материалы (favorites) SW обязаны существовать в ГОСТ-библиотеке:
    favorite хранит 'библиотека|имя|matid'; при несовпадении имени SW подставляет материал
    по matid — пользователь выбирал «Лист 6,0», а прописывался «Лист 3,0» (реальный инцидент)."""
    print('\n--- Валидация часто используемых материалов (favorites <-> библиотека) ---')
    try:
        import winreg
    except ImportError:
        res.warn("favorites", "winreg недоступен")
        return
    mat_file = os.path.join(MATERIALS_DIR, "Библиотека_Материалов_ГОСТ.sldmat")
    if not os.path.isfile(mat_file):
        res.assert_true(False, "Библиотека материалов для сверки favorites")
        return
    try:
        raw = open(mat_file, "rb").read()
        text = raw.decode("utf-16", errors="ignore")
        import re as _re
        lib = set(_re.findall(r'<material name="([^"]+)"', text))
        fav_ok, fav_bad = 0, []
        key_path = None
        try:
            k = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\SolidWorks\SOLIDWORKS 2025\Material")
        except OSError:
            res.warn("favorites", "раздел реестра Material не найден (SW не запускался)")
            return
        i = 0
        while True:
            try:
                name, val, _ = winreg.EnumValue(k, i)
            except OSError:
                break
            if name.startswith("Favorite Material"):
                parts = str(val).split("|")
                if len(parts) >= 2:
                    dbname, matname = parts[0], parts[1]
                    if dbname == "Библиотека_Материалов_ГОСТ":
                        if matname in lib:
                            fav_ok += 1
                        else:
                            fav_bad.append(matname)
            i += 1
        winreg.CloseKey(k)
        res.assert_true(fav_ok > 0, f"Валидных записей favorites ГОСТ-библиотеки: {fav_ok}")
        res.assert_true(not fav_bad,
                        "Все favorites существуют в библиотеке",
                        "отсутствуют: " + "; ".join(fav_bad[:5]))
    except Exception as e:
        res.assert_true(False, "Сверка favorites с библиотекой", str(e))




def test_tt_drawingless_section(res):
    """База технических требований (TT.TXT) обязана содержать раздел безчертёжных
    деталей (БЧ) с указаниями по ГОСТ 2.109 (файл CP1251)."""
    print('\n--- ТТ: секция безчертёжных деталей (БЧ) ---')
    tt = os.path.join(ROOT_DIR, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "ТТ", "TT.TXT")
    try:
        text = open(tt, "rb").read().decode("cp1251", errors="ignore")
        res.assert_true("$$$12. Безчертёжные детали" in text, "Секция '$$$12. Безчертёжные детали (БЧ)' присутствует")
        res.assert_true("БЧ" in text and "ГОСТ 2.109" in text, "Указания БЧ со ссылкой на ГОСТ 2.109")
    except Exception as e:
        res.assert_true(False, "Чтение TT.TXT", str(e))


def test_saveaspdf_config(res):
    print("\n--- Проверка конфигурации SaveAsPDF ---")
    ini_path = os.path.join(SWPLUS_DIR, "SaveAsPDF", "SaveAsPDF.ini")
    dat_path = os.path.join(SWPLUS_DIR, "SaveAsPDF", "PDFCreator.dat")

    if os.path.exists(ini_path):
        with open(ini_path, "r", encoding="cp1251") as f:
            first_line = f.readline().strip()
        is_native_sw = first_line.startswith("0")
        res.assert_true(is_native_sw, "SaveAsPDF.ini настроен на нативный экспорт SolidWorks (0)", f"Текущее значение: '{first_line}'")

    if os.path.exists(dat_path):
        with open(dat_path, "r", encoding="cp1251") as f:
            dat_content = f.read()
        has_e_drive = "e:\\temp" in dat_content.lower()
        res.assert_true(not has_e_drive, "PDFCreator.dat не содержит жесткого пути E:\\Temp")


def test_master_config(res):
    print("\n--- Проверка конфигурации Master.ini ---")
    ini_path = os.path.join(SWPLUS_DIR, "Master", "Master.ini")
    if os.path.exists(ini_path):
        with open(ini_path, "r", encoding="cp1251") as f:
            lines = [l.strip() for l in f.readlines()]
        if len(lines) >= 4:
            fmt_path = lines[3]
            ends_correctly = fmt_path.endswith("Основные надписи\\") or fmt_path.endswith("Основные надписи/")
            res.assert_true(ends_correctly, "Master.ini строка 4 указывает на папку 'Основные надписи'", f"Текущий путь: '{fmt_path}'")
        else:
            res.assert_true(False, "Master.ini содержит как минимум 4 строки")


def test_no_prohibited_paths(res):
    print("\n--- Проверка отсутствия устаревших путей разработчиков ---")
    prohibited = ["c:\\users\\onesmile", "d:\\_work\\_основные надписи solidworks не трогать!!!!\\_всё для sw 2016"]
    
    files_to_check = [
        os.path.join(REG_PROFILES_DIR, "01_SW2025_Корпоративный_Стандарт_ЕСКД.reg"),
        os.path.join(SWPLUS_DIR, "SaveAsPDF", "SaveAsPDF.ini"),
        os.path.join(SWPLUS_DIR, "SaveAsPDF", "PDFCreator.dat"),
    ]

    for p in files_to_check:
        if not os.path.exists(p):
            continue
        fname = os.path.relpath(p, ROOT_DIR)
        
        content = ""
        for enc in ["utf-16", "cp1251", "utf-8"]:
            try:
                with open(p, "r", encoding=enc, errors="ignore") as f:
                    content = f.read().lower()
                    break
            except Exception:
                pass

        for pattern in prohibited:
            has_pattern = pattern in content
            res.assert_true(not has_pattern, f"{fname}: отсутствие '{pattern}'")


def run_tier1_tests():
    print("=" * 70)
    print("  Tier 1: СТАТИЧЕСКИЙ АУДИТ ЦЕЛОСТНОСТИ И КОНФИГУРАЦИЙ (STATIC SANITY)")
    print("=" * 70)

    res = TestResult()
    test_file_encodings(res)
    test_swplus_macros_exist(res)
    test_materials_xml(res)
    test_favorites_materials_valid(res)
    test_tt_drawingless_section(res)
    test_saveaspdf_config(res)
    test_master_config(res)
    test_no_prohibited_paths(res)

    print("\n" + "=" * 70)
    print(f"  ИТОГ TIER 1: Успешно: {res.passed}, Провалено: {res.failed}, Предупреждений: {res.warnings}")
    print("=" * 70)

    return res.failed == 0


if __name__ == "__main__":
    success = run_tier1_tests()
    sys.exit(0 if success else 1)
