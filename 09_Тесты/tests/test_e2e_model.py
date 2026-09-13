# -*- coding: utf-8 -*-
"""E2E, группа M — модель свойств: имена, уровни хранения, владение значениями (план, §3.2)."""
import re
import unittest

from eskd_e2e import build, com, oracles, paths
from eskd_e2e.testing import SwTestCase, known_defect, tags

V = oracles.value
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A02 = "ПРТИ.468211.102 Стойка.sldprt"
A03 = "ПРТИ.468211.103 Планка.sldprt"
A04 = "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt"
A05 = "Электродвигатель АИР71А4.sldprt"
A06 = "ПРТИ.468211.104 Кронштейн направляющий удлинённый.sldprt"
A07 = "ПРТИ.468211.105 Рама сварная.sldprt"
A08 = "ПРТИ.468211.110 СБ Узел опоры.sldasm"
A13 = "ПРТИ.468211.106 Крышка.sldprt"
A16 = "ПРТИ.468211.112 Панель монтажная.sldprt"
SHEET4 = "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
SHEET6 = "Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
# Дробь, набранная в MProp в режиме «Сортамент», — не материал детали
MPROP_FRACTION = "<FONT size=1.8> <FONT size=3.5>Лист <STACK size=1>Б-ПН-НО-5,0 ГОСТ 19903-2015<OVER>09Г2С-12 ГОСТ 19281-2014</STACK>"
MPROP_TABLE = "Лист <STACK size=1>Б-ПН-НО-5,0 ГОСТ 19903-2015<OVER>09Г2С-12 ГОСТ 19281-2014</STACK>"

ALLOWED_NEW = {"Обозначение", "Наименование", "Наименование_ФБ", "Сборка1_ФБ", "Сборка2_ФБ", "Конструктор",
               "Проверил", "Контора", "Масса_ФБ", "Масса_Таблица", "Материал_ФБ", "Материал_Таблица", "Материал_Строка",
               "Исполнение", "Формат", "Примечание", "Раздел"}
LEGACY = {"Разраб.", "Разработал", "Автор", "п_Разраб", "DrawnBy", "п_Разраб_Дата", "DrawnDate", "Пров.", "п_Пров",
          "CheckedBy", "п_Пров_Дата", "Организация", "Организация_ФБ", "Компания", "Firm", "Organization", "PartNo",
          "Сортамент", "ГОСТ_Сортамент", "ГОСТ_Материал", "БЧ"}


def names_by_level(dump):
    out = {"": set(dump["general"])}
    for cfg, props in dump["configs"].items():
        out[cfg] = set(props)
    return out


def legacy_values(dump):
    """Сырые значения алиасов v5 по уровням: {(уровень, имя): значение}."""
    out = {("", n): p["raw"] for n, p in dump["general"].items() if n in LEGACY}
    for cfg, props in dump["configs"].items():
        out.update({(cfg, n): p["raw"] for n, p in props.items() if n in LEGACY})
    return out


class ModelNames(SwTestCase):

    def _save_and_diff(self, name, *extra):
        for n in extra:
            self.copy_fixture(n)
        path = self.copy_fixture(name)
        before = self.persisted(path)
        doc = self.s.open(path)
        self.s.save(doc)
        self.s.close(doc)
        after = self.persisted(path)
        added = {}
        b, a = names_by_level(before), names_by_level(after)
        for level, props in a.items():
            new = props - b.get(level, set())
            if new:
                added[level or "общие"] = sorted(new)
        return before, after, added

    @tags("smoke")
    @known_defect("Д-07")
    def test_M01_save_adds_only_dictionary_names(self):
        """M01: сохранение добавляет только словарные имена; ни одного из 21 лишнего."""
        for fixture, extra in ((A01, ()), (A02, ()), (A03, ()), (A06, ()), (A07, ()), (A08, (A01, A04))):
            with self.subTest(fixture=fixture):
                before, after, added = self._save_and_diff(fixture, *extra)
                unexpected = {lvl: [n for n in names if n not in ALLOWED_NEW] for lvl, names in added.items()}
                unexpected = {k: v for k, v in unexpected.items() if v}
                self.assertEqual({}, unexpected, "надстройка добавила имена вне словаря")
                # Алиасы, пришедшие из шаблона (Д-18, тест I03), надстройка не заполняет и не меняет.
                self.assertEqual(legacy_values(before), legacy_values(after), "надстройка изменила алиасы v5")

    @known_defect("Д-08")
    def test_M02_levels_follow_mprop(self):
        """M02: уровни хранения как у MProp."""
        _, after, added = self._save_and_diff(A01)
        general_new = set(added.get("общие", []))
        config_new = set(added.get("00", []))
        self.assertTrue({"Обозначение", "Наименование", "Наименование_ФБ", "Конструктор"} <= set(after["general"]),
                        "общие: обозначение, наименование, конструктор")
        self.assertTrue({"Обозначение", "Материал_ФБ", "Масса_ФБ", "Проверил", "Контора"} <= set(after["configs"]["00"]),
                        "конфигурация: обозначение, материал, масса, проверил, контора")
        self.assertFalse(general_new & {"Масса_ФБ", "Материал_ФБ", "Проверил", "Контора"},
                         f"в общих не должны появляться конфигурационные свойства: {sorted(general_new)}")
        self.assertNotIn("Конструктор", config_new, "конструктор хранится в общих свойствах")

    @tags("smoke")
    def test_M03_second_save_writes_nothing(self):
        """M03: повторное сохранение без изменений не пишет свойства (нет «пинг-понга»)."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        mark = self.mark("M03-second-save")
        self.s.save(doc)
        self.assertNoPropertyWrites(mark, "второе сохранение изменило свойства")
        self.s.close(doc)

    @known_defect("Д-03")
    def test_M05_foreign_part_keeps_signatures_and_designation(self):
        """M05: «чужая» деталь — подписи, организация и ручное обозначение не меняются."""
        path, doc = self.open_copy(A13)
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("Петров П.П.", V(disk, "Конструктор"))
        self.assertEqual("ООО «Вектор»", V(disk, "Контора", "00"))
        self.assertEqual("Сидоров С.С.", V(disk, "Проверил", "00"))
        self.assertEqual("ПРТИ.468211.199", V(disk, "Обозначение"))
        self.assertNotEqual("Тестов Т.Т.", V(disk, "Конструктор", "00"), "подпись из настроек затенила ручную")

    def test_M06_executions_by_configuration(self):
        """M06: исполнения по конфигурациям (ГОСТ 2.113)."""
        path, doc = self.open_copy(A03)
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("ПРТИ.468211.103", V(disk, "Обозначение", "00"))
        self.assertEqual("ПРТИ.468211.103-01", V(disk, "Обозначение", "01"))
        self.assertEqual("ПРТИ.468211.103-02", V(disk, "Обозначение", "02"))
        self.assertEqual("2", V(disk, "Исполнение", "01"))

    def test_M06_mass_in_every_configuration(self):
        """M06: масса для графы 5 у каждого исполнения — «Масса_ФБ» в «00», «01», «02» выражением MProp (эталон A-03: 0.13; 0.19; 0.25 кг)."""
        path, doc = self.open_copy(A03)
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        shown = {cfg: re.sub(r"<[^>]*>", "", V(disk, "Масса_ФБ", cfg, resolved=True) or "").strip() for cfg in ("00", "01", "02")}
        self.assertEqual({"00": "0.13", "01": "0.19", "02": "0.25"}, shown, "графа 5 по конфигурациям")
        for cfg in ("00", "01", "02"):
            self.assertEqual('<FONT size=1> \n<FONT size=3.5>"SW-Mass@@%s@ПРТИ.468211.103 Планка.SLDPRT"' % cfg,
                             (V(disk, "Масса_ФБ", cfg) or "").replace("\r\n", "\n"), f"выражение MProp «{cfg}»")

    @tags("smoke")
    def test_M07_standard_and_purchased_parts_untouched(self):
        """M07: стандартное и покупное изделия — надстройка не пишет ни одного свойства."""
        for fixture in (A04, A05):
            with self.subTest(fixture=fixture):
                path = self.copy_fixture(fixture)
                before = self.persisted(path)
                doc = self.s.open(path)
                mark = self.mark("M07-" + fixture)
                self.s.save(doc)
                self.assertNoPropertyWrites(mark)
                self.s.close(doc)
                self.assertEqual(before, self.persisted(path))

    def test_M18_em_sections_filled_like_ordinary_parts(self):
        """M18: деталь раздела «ЭМ-Детали» (A-16) заполняется как обычная; «ЭМ-Стандартные изделия» и «ЭМ-Прочие изделия»
        по-прежнему не трогаются (Н-15)."""
        path, doc = self.open_copy(A16)
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("ЭМ-Детали", V(disk, "Раздел", "00"))
        self.assertEqual("ПРТИ.468211.112", V(disk, "Обозначение", "00"))
        self.assertEqual("Панель монтажная", V(disk, "Наименование"))
        self.assertIn("STACK", V(disk, "Материал_ФБ", "00") or "", "дробь материала из библиотеки")
        self.assertIsNotNone(V(disk, "Масса_ФБ", "00"), "масса для графы 5")
        for section in ("ЭМ-Стандартные изделия", "ЭМ-Прочие изделия"):
            with self.subTest(section=section):
                path = self.s.workspace_copy(paths.FIXTURES_A / A16, name=f"ПРТИ.468211.112 Панель {section[3:7].lower()}.sldprt",
                                             subdir=self._case_name())
                doc = self.s.open(path)
                build.props(doc, {"Раздел": section}, str(doc.GetActiveConfiguration.Name))
                before = oracles.dump_properties(doc)
                mark = self.mark("M18-" + section)
                self.s.save(doc)
                self.assertNoPropertyWrites(mark, f"раздел «{section}» защищён")
                self.assertEqual(before, oracles.dump_properties(doc))
                self.s.close(doc)

    def test_M08_long_title_two_lines_for_stamp(self):
        """M08: длинное наименование — две строки для штампа, слова не режутся."""
        path, doc = self.open_copy(A06)
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("Кронштейн направляющий удлинённый", V(disk, "Наименование"))
        self.assertEqual("<FONT size=2> \n<FONT size=5>Кронштейн направляющий\nудлинённый", V(disk, "Наименование_ФБ").replace("\r\n", "\n"),
                         "две строки в разметке MProp (FrmMProp:2645)")

    def test_M09_weldment_material_in_configurations(self):
        """M09: сварная деталь — дробь материала в конфигурациях."""
        path, doc = self.open_copy(A07)
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        for cfg in disk["configs"]:
            self.assertIn("40х40х2,0", V(disk, "Материал_ФБ", cfg) or "", f"конфигурация {cfg}")

    def test_M10_non_library_material_plain_text(self):
        """M10: материал вне корпоративной библиотеки — строка без дроби, без ошибок."""
        path, doc = self.open_copy(A01)
        doc.SetMaterialPropertyName2("00", "SOLIDWORKS Materials", "Простая углеродистая сталь")
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("<FONT size=1.8> \n<FONT size=3.5>Простая углеродистая сталь", (V(disk, "Материал_ФБ", "00") or "").replace("\r\n", "\n"),
                         "одна строка в разметке MProp")
        self.assertEqual("Простая углеродистая сталь", V(disk, "Материал_Таблица", "00"), "таблица")
        self.assertEqual("Простая углеродистая сталь", V(disk, "Материал_Строка", "00"), "сводная ведомость")
        self.assertEqual([], self.addin_errors())

    @known_defect("Д-46")
    def test_M12_mprop_fraction_gives_way_to_library_material(self):
        """M12: дробь сортамента, записанная раньше в MProp, при материале SolidWorks из библиотеки ЕСКД заменяется дробью
        библиотеки (решение владельца 13.09.2026: библиотека — единственный источник графы 3)."""
        path, doc = self.open_copy(A01)
        cpm = doc.Extension.CustomPropertyManager("00")
        self.assertEqual(0, com.prop_set(cpm, "Материал_ФБ", MPROP_FRACTION))
        self.assertEqual(0, com.prop_set(cpm, "Материал_Таблица", MPROP_TABLE))
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        custom = build.material_library()[SHEET4]["custom"]
        self.assertEqual("<FONT size=1.8> <FONT size=3.5>" + custom["Обозначение_ГОСТ"], V(disk, "Материал_ФБ", "00"), "графа 3 — дробь библиотеки")
        self.assertEqual(custom["Обозначение_ГОСТ"], V(disk, "Материал_Таблица", "00"), "таблица — дробь библиотеки")
        self.assertEqual(custom["Обозначение_Строка"], V(disk, "Материал_Строка", "00"), "сводная ведомость — строка библиотеки")
        self.assertFalse([ln for ln in self.addin_log.new_lines() if "введён вручную" in ln], "при материале из библиотеки предупреждения нет")
        self.assertIn("заменено записью библиотеки", str(com.call(self.s.eskd(), "LastSyncWarnings") or ""),
                      "замена чужой дроби видна в строке состояния (Н-23, К-7)")
        self.assertEqual([], self.addin_errors())

    @known_defect("Д-32")
    def test_M12_mprop_fraction_kept_for_material_outside_library(self):
        """M12: у материала вне библиотеки ЕСКД («Простая углеродистая сталь») дробь MProp переживает сохранение, в журнале —
        предупреждение о расхождении (Д-32)."""
        path, doc = self.open_copy(A01)
        doc.SetMaterialPropertyName2("00", "SOLIDWORKS Materials", "Простая углеродистая сталь")
        cpm = doc.Extension.CustomPropertyManager("00")
        self.assertEqual(0, com.prop_set(cpm, "Материал_ФБ", MPROP_FRACTION))
        self.assertEqual(0, com.prop_set(cpm, "Материал_Таблица", MPROP_TABLE))
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual(MPROP_FRACTION, V(disk, "Материал_ФБ", "00"), "дробь MProp в графе 3")
        self.assertEqual(MPROP_TABLE, V(disk, "Материал_Таблица", "00"), "дробь MProp для таблиц")
        warnings = [ln for ln in self.addin_log.new_lines() if "введён вручную" in ln and "Материал_ФБ" in ln]
        self.assertTrue(warnings, "расхождение с материалом SolidWorks не записано в журнал")
        self.assertEqual([], self.addin_errors())

    @known_defect("Д-37")
    def test_M15_typed_text_kept_for_material_outside_library(self):
        """M15: текст «Бронза БрАЖ9-4», набранный в «Материал_ФБ», при материале вне библиотеки остаётся; «Материал_Строка»
        повторяет его; расхождение видно в «Диагностике документа», в строке состояния и в журнале."""
        path, doc = self.open_copy(A01)
        doc.SetMaterialPropertyName2("00", "SOLIDWORKS Materials", "Простая углеродистая сталь")
        self.assertEqual(0, com.prop_set(doc.Extension.CustomPropertyManager("00"), "Материал_ФБ", "Бронза БрАЖ9-4"))
        self.s.activate(doc)
        diagnosis = str(com.call(self.s.eskd(), "DiagnoseActiveDocument") or "")
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("Бронза БрАЖ9-4", V(disk, "Материал_ФБ", "00"), "ручной текст остался")
        self.assertEqual("Бронза БрАЖ9-4", V(disk, "Материал_Строка", "00"), "сводная ведомость повторяет графу 3")
        self.assertIn("Бронза БрАЖ9-4", diagnosis, "предупреждение в «Диагностике документа»")
        self.assertIn("Бронза БрАЖ9-4", str(com.call(self.s.eskd(), "LastSyncWarnings") or ""),
                      "предупреждение сохранения — то, что показано в строке состояния (Д-38)")
        self.assertTrue(any("введён вручную" in ln for ln in self.addin_log.new_lines()), "предупреждение в журнале")

    @known_defect("Д-46")
    def test_M15_typed_text_replaced_by_library_material(self):
        """M15: текст, набранный в «Материал_ФБ», при материале из библиотеки ЕСКД заменяется дробью библиотеки; «-» и
        «См. таблицу» остаются за пользователем при любом материале."""
        path, doc = self.open_copy(A03)
        texts = {"00": "Бронза БрАЖ9-4", "01": "-", "02": "<FONT size=1.8> \n<FONT size=3.5>См. таблицу"}
        for cfg, text in texts.items():
            self.assertEqual(0, com.prop_set(doc.Extension.CustomPropertyManager(cfg), "Материал_ФБ", text))
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        custom = build.material_library()[SHEET4]["custom"]
        self.assertEqual("<FONT size=1.8> <FONT size=3.5>" + custom["Обозначение_ГОСТ"], V(disk, "Материал_ФБ", "00"), "набранный текст заменён")
        self.assertEqual("-", V(disk, "Материал_ФБ", "01"), "прочерк остался")
        self.assertEqual(texts["02"], (V(disk, "Материал_ФБ", "02") or "").replace("\r\n", "\n"), "«См. таблицу» осталось")
        self.assertEqual(custom["Обозначение_Строка"], V(disk, "Материал_Строка", "02"), "сводная ведомость — материал конфигурации")

    @known_defect("Д-40")
    def test_M17_dictionary_font_flag_zero_writes_without_font_tags(self):
        """M17: флаг словаря prpFontSize = 0 (строка 50) — «Материал_ФБ» и «Масса_ФБ» без тегов FONT, как у MProp."""
        lines = paths.SWPLUS_DICTIONARY.read_bytes().decode("cp1251").splitlines()
        self.assertEqual("1", lines[49].strip(), "в корпоративном словаре prpFontSize = 1")
        lines[49] = "0"
        dictionary = self.path("MyProperties_1_prpFontSize_0.ini")
        dictionary.write_bytes("".join(line + "\r\n" for line in lines).encode("cp1251"))
        path, doc = self.open_copy(A01)
        self.s.set_settings(DictionaryPath=str(dictionary))
        try:
            self.s.save(doc)
        finally:
            self.s.set_settings(DictionaryPath="")
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual(build.material_library()[SHEET4]["custom"]["Обозначение_ГОСТ"], V(disk, "Материал_ФБ", "00"), "графа 3 без FONT")
        self.assertEqual('"SW-Mass@@00@ПРТИ.468211.101 Пластина опорная.SLDPRT"', V(disk, "Масса_ФБ", "00"), "графа 5 без FONT (2852)")
        self.assertEqual([], self.addin_errors())

    def test_M16_plain_library_material_name_is_system_value(self):
        """M16: строка без разметки, равная имени материала библиотеки (так писала v5), — запись системы: заменяется графой 3 текущего материала."""
        path, doc = self.open_copy(A01)
        self.assertEqual(0, com.prop_set(doc.Extension.CustomPropertyManager("00"), "Материал_ФБ", SHEET6))
        self.s.save(doc)
        self.s.close(doc)
        designation = build.material_library()[SHEET4]["custom"]["Обозначение_ГОСТ"]
        self.assertEqual("<FONT size=1.8> <FONT size=3.5>" + designation, V(self.persisted(path), "Материал_ФБ", "00"))

    @known_defect("Д-33")
    def test_M13_configuration_without_material_gets_no_material(self):
        """M13: конфигурация без материала не получает материал другой конфигурации (у B-01 он попадал в 78 конфигураций)."""
        path, doc = self.open_copy(A03)
        # Материал фикстуры задан на все конфигурации: удаление в одной снимает его со всех, поэтому назначаем заново в 00 и 01.
        doc.SetMaterialPropertyName2("02", "", "")
        for cfg in ("00", "01"):
            build.set_material(doc, "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", cfg)
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("Лист Б-ПН-НО-4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", V(disk, "Материал_Строка", "01"),
                         "конфигурация со своим материалом")
        self.assertIsNone(V(disk, "Материал_Строка", "02"), "конфигурация без материала — нет записи для ведомости")
        self.assertNotIn("Лист", V(disk, "Материал_ФБ", "02") or "", "конфигурация без материала — нет чужой дроби в графе 3")

    @known_defect("Д-35")
    def test_M14_reopened_document_keeps_multiline_values(self):
        """M14: документ, открытый заново, при сохранении не переписывает наименование в две строки (перевод строки CR LF)."""
        path, doc = self.open_copy(A06)
        self.s.save(doc)
        self.s.close(doc)
        doc = self.s.open(path)
        mark = self.mark("M14-reopened-save")
        self.s.save(doc)
        self.assertNoPropertyWrites(mark, "сохранение заново открытого документа переписало значения")
        self.s.close(doc)

    @known_defect("Д-09")
    def test_M11_assembly_code_and_second_line_in_configuration(self):
        """M11: сборка — «Сборка1_ФБ» ставят шаблон и MProp: прежнее « СБ» v6.1 → «СБ», вторая строка графы 1 в формате MProp;
        без кода в конфигурации надстройка код не придумывает (Р-3), обозначение без кода."""
        self.copy_fixtures(A01, A04)
        path, doc = self.open_copy(A08)
        with self.s.eskd_muted():
            build.props(doc, {"Сборка1_ФБ": " СБ"}, "00")
            build.props(doc, {"Сборка1_ФБ": " СБ"})
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("ПРТИ.468211.110", V(disk, "Обозначение"))
        self.assertEqual("СБ", V(disk, "Сборка1_ФБ", "00"), "« СБ» v6.1 → «СБ» (Р-3)")
        self.assertEqual("<FONT size=1> \n<FONT size=2.5>Сборочный чертеж", (V(disk, "Сборка2_ФБ", "00") or "").replace("\r\n", "\n"),
                         "вторая строка графы 1 в формате MProp (FrmMProp:3066)")
        self.assertIsNone(V(disk, "Сборка1_ФБ"), "общей копии кода нет — MProp её удаляет")
        self.assertIn('"SW-Mass@@00@ПРТИ.468211.110 СБ Узел опоры.SLDASM"', V(disk, "Масса_ФБ", "00") or "", "масса сборки выражением MProp")
        bare_path, bare = self.s.workspace_copy(paths.FIXTURES_A / A08, name="ПРТИ.468211.111 СБ Узел без кода.sldasm",
                                                subdir=self._case_name()), None
        bare = self.s.open(bare_path)
        self.s.save(bare)
        self.s.close(bare)
        bare_disk = self.persisted(bare_path)
        self.assertIsNone(V(bare_disk, "Сборка1_ФБ", "00"), "код из имени файла не пишется (Р-3, Р-9)")
        self.assertIsNone(V(bare_disk, "Сборка2_ФБ", "00"), "без «СБ» вторая строка не добавляется")

    def test_M01_live_mass_kept_and_sw_material_replaced_by_library(self):
        """M01: выражение массы MProp остаётся; выражение SW-Material режима «материал SW» уступает записи библиотеки (Р-6, Н-07)."""
        path = self.copy_fixture(A01)
        live_mass = '<FONT size=1> \n<FONT size=3.5>"SW-Mass@@00@ПРТИ.468211.101 Пластина опорная.SLDPRT"'
        live_material = '"SW-Material@@00@ПРТИ.468211.101 Пластина опорная.sldprt"'
        with self.s.eskd_muted():
            doc = self.s.open(path)
            build.props(doc, {"Масса_ФБ": live_mass, "Материал_ФБ": live_material}, "00")
            self.s.save(doc)
            self.s.close(doc)
        doc = self.s.open(path)
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual(live_mass, (V(disk, "Масса_ФБ", "00") or "").replace("\r\n", "\n"), "масса")
        material = V(disk, "Материал_ФБ", "00") or ""
        self.assertNotIn("SW-Material", material, "выражение заменено записью библиотеки")
        self.assertIn("<STACK size=1>", material, "дробь сортамента из библиотеки")
        self.assertNotIn("SW-Material", V(disk, "Материал_Таблица", "00") or "", "таблица — тоже запись библиотеки")


if __name__ == "__main__":
    unittest.main()
