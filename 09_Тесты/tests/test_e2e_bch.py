# -*- coding: utf-8 -*-
"""E2E, группа B — безчертёжные детали (ГОСТ Р 2.106-2019, ГОСТ Р 2.109-2023)."""
import unittest

from eskd_e2e import build, com, oracles, paths
from eskd_e2e.testing import SwTestCase, known_defect, tags

V = oracles.value
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A02 = "ПРТИ.468211.102 Стойка.sldprt"
A03 = "ПРТИ.468211.103 Планка.sldprt"
SHEET4 = "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
TUBE_RECORD = "Стойка\n<STACK size=1>Труба 80х80х4,0 ГОСТ 8639-82<OVER>В 10 ГОСТ 13663-86</STACK>\nL = 300 мм"


def record(disk, cfg=None):
    """Запись БЧ целиком (З-9): «Наименование» — одно название (общие свойства), строки после него — «Запись_БЧ»."""
    name = ((V(disk, "Наименование", cfg) if cfg else None) or V(disk, "Наименование") or "").replace("\r\n", "\n")
    lines = (V(disk, "Запись_БЧ", cfg) or "").replace("\r\n", "\n")
    return name + ("\n" + lines if lines else "")


class Bch(SwTestCase):

    def _toggle(self, doc):
        self.s.activate(doc)
        return int(com.call(self.s.eskd(), "ToggleDrawinglessSilent"))

    @tags("smoke")
    @known_defect("Д-14")
    def test_B01_enable_bch_for_tube(self):
        """B01: «Деталь БЧ» для трубы — Формат БЧ, масса выражением MProp с «кг» в «Примечании» (FrmMProp:3033), запись черт. 40."""
        path, doc = self.open_copy(A02)
        self.assertEqual(1, self._toggle(doc))
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("БЧ", V(disk, "Формат", "00"), "Формат в конфигурации")
        self.assertEqual("БЧ", V(disk, "Формат"), "Формат в общих (одна конфигурация, как у MProp)")
        self.assertEqual('"SW-Mass@@00@ПРТИ.468211.102 Стойка.SLDPRT" кг', V(disk, "Примечание", "00"), "масса детали как у MProp (Д-14)")
        self.assertEqual("2.86 кг", V(disk, "Примечание", "00", resolved=True), "значение массы в «Примечании»")
        self.assertEqual("Стойка", V(disk, "Наименование"), "«Наименование» — одно название (З-9)")
        self.assertEqual(TUBE_RECORD, record(disk), "запись для спецификации: название и «Запись_БЧ»")
        self.assertEqual("<FONT size=4> \n<FONT size=5>Стойка", (V(disk, "Наименование_ФБ") or "").replace("\r\n", "\n"),
                         "штамп — первая строка записи в разметке MProp (Д-13)")
        self.assertNotIn("БЧ", oracles.all_names(disk), "отдельного свойства «БЧ» нет")

    @known_defect("Д-15")
    def test_B02_disable_bch_restores_previous_values(self):
        """B02: снятие БЧ возвращает прежние «Формат» и «Примечание» и наименование из имени файла."""
        path = self.copy_fixture(A02)
        before = self.persisted(path)
        doc = self.s.open(path)
        self.assertEqual(1, self._toggle(doc))
        self.assertEqual(2, self._toggle(doc))
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual(V(before, "Формат"), V(disk, "Формат"), "Формат восстановлен (не жёсткое «А3»)")
        self.assertIsNone(V(disk, "Примечание", "00"), "масса удалена из «Примечания»")
        self.assertIsNone(V(disk, "Формат_до_БЧ"), "служебное свойство удалено")
        self.assertIsNone(V(disk, "Формат_до_БЧ", "00"), "служебное свойство удалено из конфигурации")
        self.assertIsNone(V(disk, "Запись_БЧ"), "строки записи БЧ удалены")
        self.assertEqual("Стойка", V(disk, "Наименование"))

    @known_defect("Д-13")
    def test_B03_repeated_saves_are_stable(self):
        """B03: после включения БЧ повторные сохранения ничего не меняют, разметка цела."""
        path, doc = self.open_copy(A02)
        self._toggle(doc)
        self.s.save(doc)
        mark = self.mark("B03-resave")
        self.s.save(doc)
        self.s.save(doc)
        self.assertNoPropertyWrites(mark)
        self.s.close(doc)
        self.assertEqual("<FONT size=4> \n<FONT size=5>Стойка", (V(self.persisted(path), "Наименование_ФБ") or "").replace("\r\n", "\n"))

    def test_B05_sheet_bch_record_with_width_and_length(self):
        """B05: БЧ из листа — размеры «B×L» по габариту."""
        path, doc = self.open_copy(A01)
        self._toggle(doc)
        self.s.save(doc)
        self.s.close(doc)
        text = record(self.persisted(path))
        self.assertTrue(text.startswith("Пластина опорная\n<STACK size=1>"), text)
        self.assertTrue(text.endswith("\n100\u00d7200 мм"), text)

    def _set_and_resave(self, path, lines):
        with self.s.eskd_muted():
            doc = self.s.open(path)
            build.props(doc, {"Запись_БЧ": lines}, "")
            self.s.save(doc)
            self.s.close(doc)
        doc = self.s.open(path)
        self.s.save(doc)
        self.s.close(doc)
        return record(self.persisted(path))

    def test_B07_own_record_follows_model_on_save(self):
        """B07: своя запись БЧ пересобирается при сохранении по модели (WP-2.7); запись, исправленная вручную, остаётся."""
        path, doc = self.open_copy(A01)
        self.assertEqual(1, self._toggle(doc))
        self.s.save(doc)
        self.s.close(doc)
        text = self._set_and_resave(path, "<STACK size=1>Лист 4,0<OVER>Ст3сп</STACK>\n90х190 мм")
        self.assertTrue(text.endswith("\n100\u00d7200 мм"), f"размер по модели: {text!r}")
        self.assertIn("ГОСТ 19903-2015", text, "дробь из библиотеки")
        manual = "<STACK size=1>Лист 4,0<OVER>Ст3сп</STACK>\n100\u00d7200±1 мм"
        self.assertEqual("Пластина опорная\n" + manual, self._set_and_resave(path, manual), "ручная запись остаётся")

    def _set_type(self, doc, bch):
        self.s.activate(doc)
        return int(com.call(self.s.eskd(), "SetDrawinglessSilent", 1 if bch else 0))

    def test_B08_switch_drawing_part_to_bch_and_back(self):
        """B08 (замечание владельца 14.09): деталь, уже оформленная чертёжной (Формат А3), переключается в БЧ и обратно
        сколько угодно раз; повторная команда того же типа ничего не меняет (двойное нажатие не отменяет переключение);
        запись БЧ прежних версий (вся в «Наименовании») при сохранении делится на название и «Запись_БЧ» (З-9)."""
        path = self.copy_fixture(A02)
        with self.s.eskd_muted():
            doc = self.s.open(path)
            build.props(doc, {"Формат": "А3"}, "")
            build.props(doc, {"Формат": "А3"}, "00")
            self.s.save(doc)
            self.s.close(doc)
        for round_no in (1, 2):
            with self.subTest(round=round_no):
                doc = self.s.open(path)
                self.assertEqual(1, self._set_type(doc, True), "стала БЧ")
                self.assertEqual(1, self._set_type(doc, True), "повторная команда «БЧ» — без изменений")
                self.s.save(doc)
                self.s.close(doc)
                disk = self.persisted(path)
                self.assertEqual("БЧ", V(disk, "Формат", "00"))
                self.assertEqual(TUBE_RECORD, record(disk), "запись БЧ после сохранения")
                doc = self.s.open(path)
                self.assertEqual(2, self._set_type(doc, False), "стала чертёжной")
                self.assertEqual(2, self._set_type(doc, False), "повторная команда «чертёжная» — без изменений")
                self.s.save(doc)
                self.s.close(doc)
                disk = self.persisted(path)
                self.assertEqual("А3", V(disk, "Формат"), "прежний формат вернулся")
                self.assertEqual("А3", V(disk, "Формат", "00"), "прежний формат в конфигурации")
                self.assertEqual("Стойка", V(disk, "Наименование"))
                self.assertIsNone(V(disk, "Примечание", "00"), "масса убрана из «Примечания»")
        doc = self.s.open(path)
        self.assertEqual(1, self._set_type(doc, True))
        self.s.save(doc)
        with self.s.eskd_muted():
            build.props(doc, {"Наименование": TUBE_RECORD}, "")  # запись прежней версии надстройки
            com.dyn(doc.Extension.CustomPropertyManager("")).Delete2("Запись_БЧ")
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("Стойка", V(disk, "Наименование"), "в «Наименовании» — одно название")
        self.assertEqual(TUBE_RECORD, record(disk), "запись БЧ разделена без потерь")
        self.assertEqual("БЧ", V(disk, "Формат", "00"))

    def test_B09_bch_in_file_name_keeps_record_on_save(self):
        """B09 (замечание владельца 14.09): у файла «… Стойка БЧ» сохранение не заменяет запись БЧ именем файла."""
        path = self.s.workspace_copy(paths.FIXTURES_A / A02, subdir=self._case_name(), name="ПРТИ.468211.102 Стойка БЧ.sldprt")
        doc = self.s.open(path)
        self.assertEqual(1, self._set_type(doc, True))
        self.s.save(doc)
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        text = record(disk)
        self.assertTrue(text.startswith("Стойка\n<STACK size=1>Труба"), f"запись БЧ: {text!r}")
        self.assertEqual("<FONT size=4> \n<FONT size=5>Стойка", (V(disk, "Наименование_ФБ") or "").replace("\r\n", "\n"))

    @known_defect("Д-41")
    def test_B06_bch_takes_material_of_its_own_configuration(self):
        """B06: «Деталь БЧ» у исполнения без материала (A-03, активна «02») не берёт материал другой конфигурации."""
        path = self.copy_fixture(A03)
        doc = self.s.open(path)
        doc.SetMaterialPropertyName2("02", "", "")  # материал фикстуры задан на все конфигурации — снимается со всех
        for cfg in ("00", "01"):
            build.set_material(doc, SHEET4, cfg)
        build.show_configuration(doc, "02")
        self.assertEqual(1, self._toggle(doc))
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        text = record(disk, "02")
        self.assertTrue(text.startswith("Планка"), f"запись БЧ исполнения 02: {text!r}")
        self.assertNotIn("<STACK", text, "у исполнения без материала нет дроби сортамента")
        self.assertIn("Лист", V(disk, "Материал_Строка", "00") or "", "у 00 материал свой")


if __name__ == "__main__":
    unittest.main()
