# -*- coding: utf-8 -*-
"""E2E, группа D — чертежи: штамп по ГОСТ 2.104, нулевое смещение, чертёж не меняет модель."""
import unittest

from eskd_e2e import oracles
from eskd_e2e.testing import SwTestCase, known_defect, tags

A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A10 = "ПРТИ.468211.101 Пластина опорная.slddrw"


class Drawing(SwTestCase):

    def _prepare_model(self):
        """Модель сохраняется с надстройкой — так реквизиты попадают в файл перед открытием чертежа."""
        model = self.copy_fixture(A01)
        doc = self.s.open(model)
        self.s.save(doc)
        self.s.close(doc)
        return model

    @tags("smoke")
    def test_D01_stamp_sheet1_texts_and_cells(self):
        """D01: штамп листа 1 (форма 1): тексты граф и положение каждой строки внутри своей графы."""
        self._prepare_model()
        drawing = self.copy_fixture(A10)
        drw = self.s.open(drawing)
        notes = oracles.stamp(drw)
        sheet = next(iter(notes))
        stamp = notes[sheet]
        text = {k: v["text"].replace("\r\n", "\n") for k, v in stamp.items()}
        self.assertEqual("ПРТИ.468211.101", text.get("MYPRP0"), "графа 2 — обозначение")
        self.assertEqual("Пластина опорная", text.get("MYPRP4"), "графа 1 — наименование")
        self.assertEqual("Тестов Т.Т.", text.get("MYPRP8"), "разработал")
        self.assertEqual("ООО «Испытание»", text.get("MYPRP7"), "графа 8 — организация")
        self.assertIn("0,63", text.get("MYPRP15", ""), "графа 5 — масса")
        self.assertIn("Лист", text.get("MYPRP16", ""), "графа 3 — материал")
        cells = oracles.form1_cells(420)
        for note, cell in (("MYPRP0", "g2_designation"), ("MYPRP4", "g1_title"), ("MYPRP16", "g3_material"),
                           ("MYPRP15", "g5_mass"), ("MYPRP7", "g8_firm")):
            with self.subTest(note=note):
                self.assertTrue(oracles.inside(stamp[note]["extent_mm"], cells[cell], tol=1.0),
                                f"{note} {stamp[note]['extent_mm']} вне графы {cell} {cells[cell]}")
        self.s.close(drw)

    def test_D04_zero_drift_on_drawing_save(self):
        """D04: сохранение чертежа не смещает заметки."""
        self._prepare_model()
        drawing = self.copy_fixture(A10)
        drw = self.s.open(drawing)
        before = oracles.note_positions(drw)
        self.s.save(drw)
        worst, moved = oracles.max_drift(before, oracles.note_positions(drw))
        self.assertLess(worst, 0.001, f"смещены заметки: {moved}")
        self.s.close(drw)

    def test_D05_stamp_filled_without_addin(self):
        """D05: штамп заполнен и при выключенной службе надстройки — реквизиты лежат в файле модели."""
        self._prepare_model()
        drawing = self.copy_fixture(A10)
        with self.s.eskd_muted():
            drw = self.s.open(drawing)
            text = {k: v["text"] for k, v in next(iter(oracles.stamp(drw).values())).items()}
            self.s.close(drw)
        self.assertEqual("ПРТИ.468211.101", text.get("MYPRP0"))
        self.assertEqual("Пластина опорная", text.get("MYPRP4"))

    @known_defect("Д-06")
    def test_D06_drawing_save_does_not_modify_model(self):
        """D06: сохранение чертежа не пишет в модель."""
        model = self._prepare_model()
        before = self.persisted(model)
        drawing = self.copy_fixture(A10)
        drw = self.s.open(drawing)
        self.s.save(drw)
        self.s.close(drw)
        self.s.close_all()
        self.assertEqual(before, self.persisted(model))


if __name__ == "__main__":
    unittest.main()
