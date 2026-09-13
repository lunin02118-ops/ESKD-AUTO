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


if __name__ == "__main__":
    unittest.main()
