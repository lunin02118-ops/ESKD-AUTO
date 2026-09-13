# -*- coding: utf-8 -*-
"""E2E, группа D (продолжение) — эталон основной надписи, критерий К-12 плана согласования (WP-4.6, Р-12)."""
import json
import unittest

from eskd_e2e import build, paths, stamp_matrix
from eskd_e2e.testing import SwTestCase, known_defect

A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"


class StampEtalon(SwTestCase):

    @known_defect("Д-82")
    def test_D12_stamp_matrix_all_formats(self):
        """D12 (К-12): все 18 форматок «Основные надписи» — деталь и сборка, наименование в 1–3 строки, масса в кг и в г,
        материал дробью и строкой в разметке MProp: текст внутри своей графы, обозначение и масса по центру графы,
        наименование не налезает на «Сборочный чертеж». Замеры — stamp_matrix.json в каталоге теста."""
        model_path = self.copy_fixture(A01)
        with self.s.eskd_muted():
            model = self.s.open(model_path)
            drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
            build.model_view(drw, model_path, 150, 170)
            formats = sorted(paths.SHEET_FORMATS.glob("*.slddrt"))
            result = stamp_matrix.run(self.s, drw, model, formats, self.path("pdf"))
            self.s.close(drw)
            self.s.close(model)
        self.path("stamp_matrix.json").write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
        self.assertEqual(18, len(result), "форматок «Основные надписи»")
        self.assertEqual({}, stamp_matrix.problems_of(result), "нарушения эталона штампа")

    def test_D12_stamp_matrix_drawing_template(self):
        """D12 (К-12): встроенная форматка шаблона «Чертеж.drwdot» — новый чертёж без замены форматки по той же матрице."""
        model_path = self.copy_fixture(A01)
        with self.s.eskd_muted():
            model = self.s.open(model_path)
            drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
            build.model_view(drw, model_path, 150, 170)
            result = stamp_matrix.run_current(self.s, drw, model, 420, self.path("pdf"), "Чертеж")
            self.s.close(drw)
            self.s.close(model)
        self.path("stamp_matrix.json").write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
        self.assertEqual({}, stamp_matrix.problems_of(result), "нарушения эталона штампа в шаблоне чертежа")


if __name__ == "__main__":
    unittest.main()
