# -*- coding: utf-8 -*-
"""
Tier 2: Модульные тесты алгоритмического ядра надстройки ЕСКД и парсинга свойств.
Тестирует математику, регулярные выражения, форматирование дроби сортамента и разбор ЕСКД.
Не требует запуска SolidWorks GUI.
"""

import sys
import re
import unittest

# Configure console output for UTF-8
try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

class EskdAlgorithmEmulator:
    """
    Python-эмуляция алгоритмов C# класса MaterialSyncEngine.
    Позволяет изолированно тестировать регулярные выражения и бизнес-правила ГОСТ 2.104/2.113.

    ПРИМЕЧАНИЕ (2026-09-12): эмулятор синхронизирован с семантикой MaterialSyncEngine.cs
    по итогам аудита (R-1..R-6): суффикс исполнения допускает до 4 цифр; ветка «хвостовых
    двух цифр» (Case 2) удалена — её нет в C#; код документа матчится после цифры без пробела
    («АБВГ.123СБ»); суффикс исполнения из имени конфигурации извлекается только при дефисе/
    подчёркивании перед цифрами или цифрах в начале; IsFastener — признак только при истинном
    значении ("1", "true", "да", "yes").
    """
    DOC_CODES = ["СБ", "ГЧ", "МЧ", "ВО", "ТУ", "ТБ", "ПЭ", "Э1", "Э2", "Э3", "Э4", "Э5", "СХ", "СЭ", "ВП", "СП"]
    BLANK_SHAPES = ["Лист", "Труба", "Круг", "Уголок", "Швеллер", "Двутавр", "Шестигранник", "Полоса", "Квадрат", "Профиль"]

    @classmethod
    def clean_document_name(cls, raw_name):
        if not raw_name:
            return ""
        # Strip directory and extension
        clean = raw_name.replace("/", "\\")
        if "\\" in clean:
            clean = clean.split("\\")[-1]
        for ext in [".sldprt", ".sldasm", ".slddrw", ".prt", ".asm", ".drw"]:
            if clean.lower().endswith(ext):
                clean = clean[:-len(ext)]
                break
        # Strip sheet suffixes
        clean = re.sub(r"\s*-\s*(Лист|Sheet)\s*\d*$", "", clean, flags=re.IGNORECASE)
        return clean.strip()

    @classmethod
    def strip_execution_suffix(cls, desig):
        if not desig:
            return "", "", ""
        d = desig.strip()
        doc_code = ""

        # Extract document code at end (e.g. " СБ"); per C# the code may also directly
        # follow a digit without any space (e.g. "АБВГ.123СБ").
        for dc in cls.DOC_CODES:
            pattern = rf"(?:^|\s+|(?<=[0-9]))({dc})$"
            m = re.search(pattern, d, flags=re.IGNORECASE)
            if m:
                doc_code = " " + m.group(1).upper()
                d = d[:m.start()].strip()
                break

        exec_suffix = ""
        # Dash + up to 4 digits (e.g. "-01", "-1", "-0001") — synced with C# MaterialSyncEngine.
        # NOTE: the former "Case 2" (two trailing digits glued to a numeric root) was removed:
        # that branch does not exist in the C# engine.
        m_dash = re.search(r"-(\d{1,4})$", d)
        if m_dash:
            exec_suffix = "-" + m_dash.group(1).zfill(2)
            root = d[:m_dash.start()].strip()
            return root, exec_suffix, doc_code

        return d, "", doc_code

    @classmethod
    def build_execution_designation(cls, root_desig, config_name, doc_code="", is_assembly=False):
        if not root_desig:
            return ""
        root = root_desig.strip()
        code = doc_code.strip()
        # ЕСКД-разделение: у сборок шифр «СБ» — код сборочного ЧЕРТЕЖА (ГОСТ 2.102),
        # в «Обозначение» изделия не входит (спецификация по ГОСТ 2.106 — без шифра);
        # шифр живёт в свойстве «Сборка1_ФБ» и склеивается форматкой в графе 1 и 26.
        if is_assembly:
            code = ""
        code_str = (" " + code) if code else ""

        if not config_name or config_name.lower() in ("default", "по умолчанию", "основная", "main"):
            return root + code_str

        # Check if config specifies execution suffix: the digits must either start the
        # config name or be preceded by a dash/underscore (per C# MaterialSyncEngine).
        # Plain trailing digits of a long part name ("Длинная деталь 100") are NOT an execution.
        m = re.search(r"(?:^|[-_])(\d{1,4})$", config_name.strip())
        if m:
            exec_num = m.group(1).zfill(2)
            return f"{root}-{exec_num}{code_str}"

        return root + code_str

    @classmethod
    def format_eskd_material_fb(cls, top, bottom):
        if not top or not bottom:
            return (top or "") + (bottom or "")
        top = top.strip()
        bottom = bottom.strip()
        # Scale fraction font to fit 15mm title block cell per SWPlus / GOST standard.
        # Guaranteed leading space ensures MProp InStr("<") == 2, Left$(strTemp, 0) == "" (never crashes with Left$(..., -1)).
        return f" <FONT size=1.8><FONT size=3.5><STACK size=1>{top}<OVER>{bottom}</STACK>"

    @classmethod
    def format_eskd_mass_fb(cls, mass_kg, decimals=2):
        if mass_kg <= 0:
            return ""
        mass_str = f"{mass_kg:.{decimals}f}".replace(".", ",")
        # Однострочный формат: прежний двухстрочный ("<FONT size=1> \n<FONT size=3.5>X")
        # прижимал число к нижней кромке ячейки; центрирование по ГОСТ 2.104 (графа 5).
        return f"<FONT size=3.5>{mass_str}"

    @classmethod
    def format_eskd_title_fb(cls, title):
        """Перенос длинного наименования по словам максимум на 2 строки (графа 2: 70 мм, ~24 симв.)."""
        if not title or not title.strip():
            return ""
        t = title.strip()
        limit = 24
        if len(t) <= limit:
            return t
        line1, line2 = "", ""
        to_second = False
        for word in t.split(" "):
            if not word:
                continue
            if not to_second:
                cand = word if not line1 else line1 + " " + word
                if len(cand) <= limit or not line1:
                    line1 = cand
                    continue
                to_second = True
            line2 = word if not line2 else line2 + " " + word
        return (line1 + "\n" + line2) if line2 else line1

    @classmethod
    def parse_filename_desig_and_title(cls, base_name):
        is_default = bool(re.match(r"^(Деталь|Part|Сборка|Assem|Чертеж|Draw)\s*\d*$", base_name, re.IGNORECASE))
        if is_default or not base_name:
            return "", ""

        space_idx = base_name.find(" ")
        if space_idx > 0:
            desig = base_name[:space_idx].strip()
            title = base_name[space_idx + 1:].strip()

            # Check if title starts with doc code
            doc_pattern = r"^(СБ|ГЧ|МЧ|ВО|ТУ|ТБ|ПЭ|Э\d|СХ|СЭ|ВП|СП)(\s+.*|$)"
            m_doc = re.match(doc_pattern, title, re.IGNORECASE)
            if m_doc:
                desig = f"{desig} {m_doc.group(1).upper()}"
                title = m_doc.group(2).strip()
            return desig, title
        else:
            if re.search(r"\d", base_name):
                return base_name, ""
            else:
                return "", base_name

    @classmethod
    def is_standard_or_purchased_part(cls, props):
        if not props:
            return False
        section = (props.get("Раздел") or "").lower()
        if any(w in section for w in ["стандартн", "прочи", "покупн", "материал", "эм-"]):
            return True
        # IsFastener counts as a marker ONLY for truthy values (per C# MaterialSyncEngine);
        # "0", "false", "нет", "no" and an empty value explicitly do NOT mark the part.
        fastener_flag = str(props.get("IsFastener") or "").strip().lower()
        if fastener_flag in ("1", "true", "да", "yes"):
            return True
        if props.get("Наименование_ВП"):
            return True
        if props.get("Поставщик"):
            return True
        if props.get("Код_Продукции"):
            return True
        if props.get("Обозначение_ДНП"):
            return True
        if props.get("Справочный_номер_ВП"):
            return True
        return False

    @classmethod
    def mark_drawingless(cls, title, sortament, length_mm, mass_str, is_bch):
        """Канонический БЧ по ГОСТ 2.109-73 п. 3.3: формат 'БЧ', краткое имя + сортамент + длина L."""
        if not is_bch:
            short_name = (title or "").split()[0] if title else "Деталь"
            bch_title = (short_name + "\n" + sortament + ", L = " + str(int(length_mm)) + " мм") if sortament else (short_name + ", L = " + str(int(length_mm)) + " мм")
            bch_note = (mass_str + " кг") if mass_str else ""
            return 1, "БЧ", bch_title, bch_note
        else:
            return 2, "А3", title, ""

    @classmethod
    def parse_mprop_firm_file(cls, lines):
        firms = []
        for i in range(0, len(lines), 2):
            f = lines[i].strip()
            if f:
                firms.append(f)
        return firms


class TestEskdUnitAlgorithms(unittest.TestCase):

    def test_mark_drawingless_gost(self):
        # 1. Установка БЧ: формат 'БЧ', наименование по ГОСТ 2.109, масса в примечании
        code, fmt, title, note = EskdAlgorithmEmulator.mark_drawingless(
            "Стойка направляющая", "80х80х4,0 ГОСТ 8639-82", 300.0, "2,86", False)
        self.assertEqual(code, 1)
        self.assertEqual(fmt, "БЧ")
        self.assertIn("Стойка", title)
        self.assertIn("80х80х4,0 ГОСТ 8639-82", title)
        self.assertIn("L = 300 мм", title)
        self.assertEqual(note, "2,86 кг")

        # 2. Снятие БЧ: формат А3, очистка примечания
        code, fmt, title, note = EskdAlgorithmEmulator.mark_drawingless(
            "Стойка направляющая", "80х80х4,0 ГОСТ 8639-82", 300.0, "2,86", True)
        self.assertEqual(code, 2)
        self.assertEqual(fmt, "А3")
        self.assertEqual(title, "Стойка направляющая")
        self.assertEqual(note, "")

    def test_clean_document_name(self):
        self.assertEqual(EskdAlgorithmEmulator.clean_document_name("C:\\CAD\\ПРТИ.468211.010 Стойка.sldprt"), "ПРТИ.468211.010 Стойка")
        self.assertEqual(EskdAlgorithmEmulator.clean_document_name("Чертеж1 - Лист1.slddrw"), "Чертеж1")
        self.assertEqual(EskdAlgorithmEmulator.clean_document_name("ПРТИ.001 - Sheet2"), "ПРТИ.001")
        self.assertEqual(EskdAlgorithmEmulator.clean_document_name("Сборка рамы.sldasm"), "Сборка рамы")

    def test_strip_execution_suffix(self):
        # Base with no execution
        root, ex, dc = EskdAlgorithmEmulator.strip_execution_suffix("ПРТИ.468211.010")
        self.assertEqual(root, "ПРТИ.468211.010")
        self.assertEqual(ex, "")
        self.assertEqual(dc, "")

        # Execution -01
        root, ex, dc = EskdAlgorithmEmulator.strip_execution_suffix("ПРТИ.468211.010-01")
        self.assertEqual(root, "ПРТИ.468211.010")
        self.assertEqual(ex, "-01")
        self.assertEqual(dc, "")

        # Execution -02 with Assembly doc code СБ
        root, ex, dc = EskdAlgorithmEmulator.strip_execution_suffix("ПРТИ.468211.010-02 СБ")
        self.assertEqual(root, "ПРТИ.468211.010")
        self.assertEqual(ex, "-02")
        self.assertEqual(dc, " СБ")

        # Base assembly with doc code СБ
        root, ex, dc = EskdAlgorithmEmulator.strip_execution_suffix("ПРТИ.468211.010 СБ")
        self.assertEqual(root, "ПРТИ.468211.010")
        self.assertEqual(ex, "")
        self.assertEqual(dc, " СБ")

    def test_strip_execution_suffix_four_digit_execution(self):
        # C# engine allows up to 4 digits in the execution suffix (e.g. "-0001")
        root, ex, dc = EskdAlgorithmEmulator.strip_execution_suffix("ПРТИ.468211.010-0001")
        self.assertEqual(root, "ПРТИ.468211.010")
        self.assertEqual(ex, "-0001")
        self.assertEqual(dc, "")

    def test_strip_execution_suffix_doc_code_after_digit(self):
        # Document code may directly follow a digit without a space (per C#): "АБВГ.123СБ"
        root, ex, dc = EskdAlgorithmEmulator.strip_execution_suffix("АБВГ.123СБ")
        self.assertEqual(root, "АБВГ.123")
        self.assertEqual(ex, "")
        self.assertEqual(dc, " СБ")

        # Removed "Case 2" (trailing two digits glued to a numeric root) must NOT
        # be treated as an execution suffix anymore — this branch is absent in C#.
        root, ex, dc = EskdAlgorithmEmulator.strip_execution_suffix("ПРТИ.468211.01001")
        self.assertEqual(root, "ПРТИ.468211.01001")
        self.assertEqual(ex, "")
        self.assertEqual(dc, "")

    def test_build_execution_designation(self):
        # Default config
        res = EskdAlgorithmEmulator.build_execution_designation("ПРТИ.468211.010", "Default", "")
        self.assertEqual(res, "ПРТИ.468211.010")

        # Config -01
        res = EskdAlgorithmEmulator.build_execution_designation("ПРТИ.468211.010", "01", "")
        self.assertEqual(res, "ПРТИ.468211.010-01")

        # Config -02 with СБ (деталь: doc-код остаётся в обозначении)
        res = EskdAlgorithmEmulator.build_execution_designation("ПРТИ.468211.010", "-02", "СБ")
        self.assertEqual(res, "ПРТИ.468211.010-02 СБ")

        # СБОРКА: шифр живёт в «Сборка1_ФБ», обозначение чистое (ГОСТ 2.102/2.106)
        res = EskdAlgorithmEmulator.build_execution_designation("ПРТИ.468211.010", "Default", " СБ", is_assembly=True)
        self.assertEqual(res, "ПРТИ.468211.010")

        res = EskdAlgorithmEmulator.build_execution_designation("ПРТИ.468211.010", "-02", " СБ", is_assembly=True)
        self.assertEqual(res, "ПРТИ.468211.010-02")

    def test_assembly_code_fb_scheme(self):
        # Штамп склеивает: $PRPSHEET:"Обозначение" + $PRPSHEET:"Сборка1_ФБ" (без разделителя),
        # поэтому шифр обязан храниться с ведущим пробелом.
        root, ex, dc = EskdAlgorithmEmulator.strip_execution_suffix("ПРТИ.468211.010 СБ")
        self.assertEqual(dc, " СБ")
        assembly_code_fb = " " + dc.strip()
        self.assertEqual("ПРТИ.468211.010" + assembly_code_fb, "ПРТИ.468211.010 СБ")

    def test_build_execution_designation_long_name_no_execution(self):
        # Trailing digits of a long part name (no dash/underscore before them) are NOT
        # an execution suffix — synced with C# MaterialSyncEngine.
        res = EskdAlgorithmEmulator.build_execution_designation("ПРТИ.468211.010", "Длинная деталь 100", "")
        self.assertEqual(res, "ПРТИ.468211.010")

        # Explicit dash/underscore before the digits still yields an execution suffix
        res = EskdAlgorithmEmulator.build_execution_designation("ПРТИ.468211.010", "Длинная деталь-100", "")
        self.assertEqual(res, "ПРТИ.468211.010-100")

        res = EskdAlgorithmEmulator.build_execution_designation("ПРТИ.468211.010", "Config_05", "СБ")
        self.assertEqual(res, "ПРТИ.468211.010-05 СБ")

    def test_format_eskd_material_fb(self):
        top = "Труба 80х80х4 ГОСТ 8639-82"
        bottom = "Ст3сп ГОСТ 380-2005"
        res = EskdAlgorithmEmulator.format_eskd_material_fb(top, bottom)

        # Must start with space for MProp InStr compatibility
        self.assertTrue(res.startswith(" <FONT"))
        # Must contain stack and over tags
        self.assertIn("<STACK size=1>", res)
        self.assertIn("<OVER>", res)
        self.assertIn("</STACK>", res)
        self.assertIn(top, res)
        self.assertIn(bottom, res)

    def test_format_eskd_mass_fb(self):
        res = EskdAlgorithmEmulator.format_eskd_mass_fb(14.526, 2)
        # Однострочный формат: без микро-строки, центрирование в ячейке (ГОСТ 2.104, графа 5)
        self.assertTrue(res.startswith("<FONT size=3.5>"))
        self.assertNotIn("\n", res)
        self.assertIn("14,53", res) # Comma decimal separator per GOST

        res_zero = EskdAlgorithmEmulator.format_eskd_mass_fb(0.0)
        self.assertEqual(res_zero, "")

    def test_format_eskd_mass_fb_fixed_zeros(self):
        # GOST requires fixed decimal places: 14.5 kg at decimals=2 must render as "14,50"
        # (fixed trailing zeros, comma as decimal separator).
        res = EskdAlgorithmEmulator.format_eskd_mass_fb(14.5, 2)
        self.assertTrue(res.startswith("<FONT size=3.5>"))
        self.assertIn("14,50", res)

    def test_format_eskd_title_fb(self):
        # Короткое наименование — одна строка
        self.assertEqual(EskdAlgorithmEmulator.format_eskd_title_fb("Стойка опорная"), "Стойка опорная")
        self.assertEqual(EskdAlgorithmEmulator.format_eskd_title_fb("  Рама сварная кондуктора  "), "Рама сварная кондуктора")

        # Длинное наименование — перенос по словам максимум на 2 строки
        res = EskdAlgorithmEmulator.format_eskd_title_fb("Втулка направляющая опорная длинная")
        self.assertIn("\n", res)
        l1, l2 = res.split("\n")
        self.assertEqual(l1, "Втулка направляющая")
        self.assertEqual(l2, "опорная длинная")
        self.assertLessEqual(len(l1), 24)

        # Очень длинное — всё равно максимум 2 строки, слова не режем
        res = EskdAlgorithmEmulator.format_eskd_title_fb("Кондуктор для сварки продольных рёбер жёсткости корпуса")
        self.assertEqual(len(res.split("\n")), 2)

        # Одно длинное слово — остаётся целиком на первой строке
        res = EskdAlgorithmEmulator.format_eskd_title_fb("Гидроцилиндрдвустороннегодействия")
        self.assertNotIn("\n", res)

    def test_parse_filename_desig_and_title(self):
        # Normal designation and title
        d, t = EskdAlgorithmEmulator.parse_filename_desig_and_title("ПРТИ.468211.010 Стойка опорная")
        self.assertEqual(d, "ПРТИ.468211.010")
        self.assertEqual(t, "Стойка опорная")

        # File name with document code
        d, t = EskdAlgorithmEmulator.parse_filename_desig_and_title("ПРТИ.468211.010 СБ Рама сварная")
        self.assertEqual(d, "ПРТИ.468211.010 СБ")
        self.assertEqual(t, "Рама сварная")

        # Default template name should be ignored
        d, t = EskdAlgorithmEmulator.parse_filename_desig_and_title("Деталь1")
        self.assertEqual(d, "")
        self.assertEqual(t, "")

        d, t = EskdAlgorithmEmulator.parse_filename_desig_and_title("Сборка2")
        self.assertEqual(d, "")
        self.assertEqual(t, "")

    def test_is_standard_or_purchased_part(self):
        # 1. Standard part marked with Раздел
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"Раздел": "Стандартные изделия"}))
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"Раздел": "Прочие изделия"}))
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"Раздел": "ЭМ-Стандартные изделия"}))

        # 2. Fastener or supplier part
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": "1"}))
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"Наименование_ВП": "Винт М6х20"}))
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"Поставщик": "Wurth"}))
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"Код_Продукции": "A2-70"}))
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"Обозначение_ДНП": "DIN 912"}))

        # 3. Regular manufactured part should NOT be detected as standard
        self.assertFalse(EskdAlgorithmEmulator.is_standard_or_purchased_part({"Раздел": "Детали", "Обозначение": "ПРТИ.468211.010"}))
        self.assertFalse(EskdAlgorithmEmulator.is_standard_or_purchased_part({}))

    def test_is_standard_or_purchased_part_fastener_flag_semantics(self):
        # IsFastener is a marker ONLY for truthy values, case-insensitive
        # (synced with C# MaterialSyncEngine).
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": "1"}))
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": "true"}))
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": "TRUE"}))
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": "Да"}))
        self.assertTrue(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": "Yes"}))

        # Falsy and empty values must NOT mark the part as standard/purchased
        self.assertFalse(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": "false"}))
        self.assertFalse(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": "0"}))
        self.assertFalse(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": "нет"}))
        self.assertFalse(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": "no"}))
        self.assertFalse(EskdAlgorithmEmulator.is_standard_or_purchased_part({"IsFastener": ""}))

    def test_mprop_firm_alternating_lines(self):
        # MProp_Firm.txt format: Line 1 = Firm, Line 2 = Classifier
        raw_lines = ["123\n", "\n", "АО Тест\n", "АБВГ\n", "BSL-Lab\n", "\n", "Home Made\n", "\n"]
        firms = EskdAlgorithmEmulator.parse_mprop_firm_file(raw_lines)
        self.assertEqual(firms, ["123", "АО Тест", "BSL-Lab", "Home Made"])
        # Classifiers should NOT appear as firms
        self.assertNotIn("АБВГ", firms)


def run_tier2_tests():
    print("=" * 70)
    print("  Tier 2: МОДУЛЬНЫЕ ТЕСТЫ АЛГОРИТМОВ ЕСКД (UNIT TESTS)")
    print("=" * 70)

    suite = unittest.TestLoader().loadTestsFromTestCase(TestEskdUnitAlgorithms)
    runner = unittest.TextTestRunner(verbosity=2)
    res = runner.run(suite)

    print("=" * 70)
    print(f"  ИТОГ TIER 2: Запущено: {res.testsRun}, Ошибок: {len(res.errors)}, Провалов: {len(res.failures)}")
    print("=" * 70)

    return len(res.errors) == 0 and len(res.failures) == 0


if __name__ == "__main__":
    success = run_tier2_tests()
    sys.exit(0 if success else 1)
