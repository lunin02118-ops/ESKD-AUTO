# -*- coding: utf-8 -*-
"""E2E, группа S — спецификация и формы SpecEditor (план, §4.7). S01–S06 требуют автоматизации SWPlus (WP-2.3)."""
import re
import sys
import unittest
from pathlib import Path

from eskd_e2e import com, oracles, paths
from eskd_e2e.testing import SwTestCase, known_defect

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from clean_format_notes import SERVICE_NOTE, formats  # noqa: E402


class SpecForms(SwTestCase):

    @known_defect("Д-17")
    def test_S07_formats_have_no_file_name_service_note(self):
        """S07: ни одна форматка комплекта (основные надписи и формы SpecEditor) не печатает «Файл: <имя файла>»."""
        found = {}
        for fmt, size in formats():
            drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
            sheet = com.dyn(drw.GetCurrentSheet)
            self.assertTrue(drw.SetupSheet5(str(sheet.GetName), 12, 12, 1.0, 1.0, True, str(fmt), size[0] / 1000.0,
                                            size[1] / 1000.0, "По умолчанию", True), fmt.name)
            drw.ForceRebuild3(False)
            service = [n["name"] for n in oracles.notes_of_view(com.dyn(drw.GetFirstView))
                       if SERVICE_NOTE.match(n.get("linked") or n["text"])]
            self.s.close(drw)
            if service:
                found[fmt.name] = service
        self.assertEqual({}, found)


def cell_text(ann, r, c):
    ole = com.dyn(ann)._oleobj_
    import pythoncom
    return str(ole.InvokeTypes(ole.GetIDsOfNames("Text2"), 0, pythoncom.DISPATCH_PROPERTYGET, (8, 0),
                               ((3, 1), (3, 1), (11, 1)), r, c, True))


class SpecBchLayout(SwTestCase):
    COL_FORMAT, COL_POS, COL_NAME, COL_QTY = 0, 2, 4, 5

    def test_S08_bch_record_one_table_row_per_line(self):
        """S08: запись детали БЧ в спецификации строго по ГОСТ Р 2.105-2019 п. 7.4–7.5 и рис. 15 проекта ГОСТ Р 2.109
        (Р-15, S-2f): SpecEditor (SwpLayoutRecords) даёт каждой строке записи строку 8 мм, дробь — объединённой ячейке на
        две строки, «Кол.» и «Примечание» — последней строке записи, позиция — первой."""
        from eskd_e2e import build, mprop
        parts = []
        for name in ("ПРТИ.468211.101 Пластина опорная.sldprt", "ПРТИ.468211.111 Стойка трубная.sldprt"):
            path, doc = self.open_copy(name)
            self.s.activate(doc)
            self.assertEqual(1, int(com.call(self.s.eskd(), "ToggleDrawinglessSilent")), name)
            self.s.save(doc)
            self.s.close(doc)
            parts.append(path)
        bolt = self.copy_fixture("Болт М6-6gх20.58 ГОСТ 7798-70.sldprt")
        asm, _ = build.assembly(self.s, [(parts[0], 0, 0, 0), (parts[1], 0, 0.15, 0), (bolt, 0.3, 0, 0)])
        asm_path = self.path("ПРТИ.468211.130 СБ Узел.sldasm")
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        self.s.open(asm_path)
        drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
        build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
        view = build.model_view(drw, asm_path, 60, 60)
        drw.ActivateView(str(view.GetName2))
        template = paths.SWPLUS / "SpecEditor" / "SpecEditor_sp.sldbomtbt"
        ann = com.dyn(view.InsertBomTable4(False, 0.2, 0.285, 1, 2, "", str(template), False, 0, False))
        macro = mprop.swplus_copy(self.s.run_dir).parent.parent / "SpecEditor" / "SpecEditor.swp"
        err = com.ref_int()
        ok = bool(self.s.sw.RunMacro2(str(macro), "SpecEditor_run", "swp_layout_active_table", 1, err))
        self.assertEqual([], self.s.watchdog.pop_unexpected(), "окна SpecEditor")
        self.assertTrue(ok, f"оформление не выполнено: err={int(err.value)}")
        self.assertEqual("", str(drw.GetCustomInfoValue("", "SwpLayoutError") or ""), "ошибка процедуры оформления")
        rows = int(ann.RowCount)
        table = [[cell_text(ann, r, c) for c in range(int(ann.ColumnCount))] for r in range(rows)]
        heights = [round(float(ann.GetRowHeight(r)) * 1000, 2) for r in range(rows)]
        self.path("table.json").write_text(__import__("json").dumps({"cells": table, "heights": heights}, ensure_ascii=False,
                                                                    indent=1), encoding="utf-8")
        for title, size_prefix in (("Пластина опорная", ""), ("Стойка трубная", "L = ")):
            with self.subTest(record=title):
                first = next(r for r in range(1, rows) if table[r][self.COL_NAME].strip() == title)
                self.assertEqual("БЧ", table[first][self.COL_FORMAT].strip(), "«БЧ» в первой строке записи")
                self.assertTrue(table[first][self.COL_POS].strip(), "позиция в первой строке записи")
                self.assertIn("ГОСТ", table[first + 1][self.COL_NAME], "дробь — со второй строки записи")
                self.assertTrue(table[first + 3][self.COL_NAME].strip().startswith(size_prefix) and "мм" in table[first + 3][self.COL_NAME],
                                f"размер — в четвёртой строке: {table[first + 3][self.COL_NAME]!r}")
                for r in range(first, first + 4):
                    self.assertAlmostEqual(8.0, heights[r], delta=0.1, msg=f"строка {r} записи — 8 мм")
                for r in range(first + 1, first + 4):
                    self.assertFalse(table[r][self.COL_POS].strip(), f"позиция только в первой строке (строка {r})")
                self.assertEqual("1", table[first + 3][self.COL_QTY].strip(), "«Кол.» в последней строке записи")
                self.assertIn("кг", table[first + 3][-1], "масса в «Примечании» последней строки записи")
                self.assertFalse(table[first][self.COL_QTY].strip(), "в первой строке «Кол.» пусто")


if __name__ == "__main__":
    unittest.main()
