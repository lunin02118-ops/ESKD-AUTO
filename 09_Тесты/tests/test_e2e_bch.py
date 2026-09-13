# -*- coding: utf-8 -*-
"""E2E, группа B — безчертёжные детали (ГОСТ 2.106, ГОСТ 2.109-73, черт. 40)."""
import unittest

from eskd_e2e import com, oracles
from eskd_e2e.testing import SwTestCase, known_defect, tags

V = oracles.value
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A02 = "ПРТИ.468211.102 Стойка.sldprt"
TUBE_RECORD = "Стойка\n<STACK size=1>Труба 80х80х4,0 ГОСТ 8639-82<OVER>В 10 ГОСТ 13663-86</STACK>\nL = 300 мм"


class Bch(SwTestCase):

    def _toggle(self, doc):
        self.s.activate(doc)
        return int(com.call(self.s.eskd(), "ToggleDrawinglessSilent"))

    @tags("smoke")
    @known_defect("Д-14")
    def test_B01_enable_bch_for_tube(self):
        """B01: «Деталь БЧ» для трубы — Формат БЧ, масса 2,86 кг в «Примечании», запись черт. 40."""
        path, doc = self.open_copy(A02)
        self.assertEqual(1, self._toggle(doc))
        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("БЧ", V(disk, "Формат", "00"), "Формат в конфигурации")
        self.assertEqual("БЧ", V(disk, "Формат"), "Формат в общих (одна конфигурация, как у MProp)")
        self.assertEqual("2,86 кг", V(disk, "Примечание", "00"), "масса детали из модели (Д-14)")
        self.assertEqual(TUBE_RECORD, (V(disk, "Наименование") or "").replace("\r\n", "\n"), "запись для спецификации")
        self.assertEqual("Стойка", V(disk, "Наименование_ФБ"), "штамп без разметки (Д-13)")
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
        self.assertEqual("Стойка", V(self.persisted(path), "Наименование_ФБ"))

    def test_B05_sheet_bch_record_with_width_and_length(self):
        """B05: БЧ из листа — размеры «B×L» по габариту."""
        path, doc = self.open_copy(A01)
        self._toggle(doc)
        self.s.save(doc)
        self.s.close(doc)
        record = (V(self.persisted(path), "Наименование") or "").replace("\r\n", "\n")
        self.assertTrue(record.startswith("Пластина опорная\n<STACK size=1>"), record)
        self.assertTrue(record.endswith("\n100х200 мм"), record)


if __name__ == "__main__":
    unittest.main()
