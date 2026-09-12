# -*- coding: utf-8 -*-
"""T2 — контракт событий SolidWorks, на который опирается надстройка ЕСКД.

Сценарии повторяют измерение 13.09.2026 (план, §1). Если сервис-пак SolidWorks изменит
поведение событий, эти тесты упадут первыми — до того, как сломается синхронизация реквизитов.
Надстройка ЕСКД в этих тестах выгружена: проверяется сам SolidWorks.
"""
import unittest

from eskd_e2e import build, com, paths
from eskd_e2e.testing import SwTestCase, tags

SHEET4 = "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"


def events_of(journal, mark, names):
    return [e for e in journal.events(mark) if e["event"] in names]


class ContractSaveEvents(SwTestCase):
    load_eskd = False

    def _new_part(self):
        doc, _ = build.plate(self.s, 100, 50, 4, SHEET4)
        return doc

    @tags("smoke")
    def test_C01_api_save_as_fires_only_post_notify_with_new_name(self):
        """C01: API SaveAs нового документа — только FileSavePostNotify(2) с новым именем."""
        doc = self._new_part()
        target = self.path("КОНТР.000001.001 Пластина.sldprt")
        mark = self.mark("C01-saveas")
        ok, err, _ = self.s.save_as(doc, target)
        self.assertTrue(ok, f"SaveAs4 не выполнен, err={err}")
        evs = events_of(self.s.journal, mark, ("FileSaveNotify", "FileSaveAsNotify2", "FileSavePostNotify"))
        self.assertEqual(["FileSavePostNotify"], [e["event"] for e in evs])
        self.assertEqual(2, evs[0]["saveType"])
        self.assertEqual(str(target).lower(), evs[0]["fileName"].lower())

    @tags("smoke")
    def test_C01_api_save_fires_pre_and_post_notify(self):
        """C01: API Save3 — FileSaveNotify до записи, затем FileSavePostNotify(1)."""
        doc = self._new_part()
        target = self.path("КОНТР.000001.002 Пластина.sldprt")
        self.s.save_as(doc, target)
        com.prop_set(doc.Extension.CustomPropertyManager(""), "КОНТРАКТ", "1")
        mark = self.mark("C01-save")
        ok, err, _ = self.s.save(doc)
        self.assertTrue(ok, f"Save3 не выполнен, err={err}")
        evs = events_of(self.s.journal, mark, ("FileSaveNotify", "FileSaveAsNotify2", "FileSavePostNotify",
                                               "CommandOpenPreNotify"))
        self.assertEqual(["FileSaveNotify", "FileSavePostNotify"], [e["event"] for e in evs])
        self.assertEqual(1, evs[1]["saveType"])

    def test_C01_api_save_as_copy_reports_copy_name_and_keeps_document_path(self):
        """C01: SaveAs с флагом Copy — FileSavePostNotify(3), документ остаётся со старым путём."""
        doc = self._new_part()
        original = self.path("КОНТР.000001.003 Пластина.sldprt")
        copy = self.path("КОНТР.000001.004 Копия.sldprt")
        self.s.save_as(doc, original)
        mark = self.mark("C01-copy")
        ok, err, _ = self.s.save_as(doc, copy, options=com.SAVE_SILENT | com.SAVE_COPY)
        self.assertTrue(ok, f"копия не сохранена, err={err}")
        evs = events_of(self.s.journal, mark, ("FileSavePostNotify",))
        self.assertEqual([3], [e["saveType"] for e in evs])
        self.assertEqual(str(copy).lower(), evs[0]["fileName"].lower())
        self.assertEqual(str(original).lower(), str(doc.GetPathName).lower())

    @tags("smoke")
    def test_C02_ui_save_of_new_document_goes_through_save_as_notify(self):
        """C02: команда «Сохранить» нового документа — FileSaveAsNotify2 со старым именем, затем Post(2)."""
        doc = self._new_part()
        title = str(doc.GetTitle)
        target = self.path("КОНТР.000002.001 Пластина.sldprt")
        mark = self.mark("C02-ui-save")
        self.s.ui_save_as(doc, target, command=2)
        evs = events_of(self.s.journal, mark, ("CommandOpenPreNotify", "FileSaveNotify", "FileSaveAsNotify2",
                                               "FileSavePostNotify"))
        names = [e["event"] for e in evs]
        self.assertEqual(["CommandOpenPreNotify", "FileSaveAsNotify2", "FileSavePostNotify"], names)
        self.assertEqual(2, evs[0]["command"])
        self.assertTrue(evs[1]["fileName"].lower().startswith(title.lower().split(".")[0]),
                        f"FileSaveAsNotify2 пришёл с именем {evs[1]['fileName']!r}, ожидалось прежнее {title!r}")
        self.assertEqual(2, evs[2]["saveType"])
        self.assertEqual(str(target).lower(), evs[2]["fileName"].lower())

    def test_C02_ui_save_as_of_saved_document_reports_old_path_before_dialog(self):
        """C02: команда «Сохранить как» — FileSaveAsNotify2 с прежним полным путём."""
        doc = self._new_part()
        first = self.path("КОНТР.000002.002 Пластина.sldprt")
        second = self.path("КОНТР.000002.003 Плита.sldprt")
        self.s.save_as(doc, first)
        mark = self.mark("C02-ui-saveas")
        self.s.ui_save_as(doc, second, command=620)
        evs = events_of(self.s.journal, mark, ("FileSaveAsNotify2", "FileSavePostNotify"))
        self.assertEqual(["FileSaveAsNotify2", "FileSavePostNotify"], [e["event"] for e in evs])
        self.assertEqual(str(first).lower(), evs[0]["fileName"].lower())
        self.assertEqual(str(second).lower(), evs[1]["fileName"].lower())


class ContractPropertyEvents(SwTestCase):
    load_eskd = False

    def test_C03_property_notifications_follow_api_writes(self):
        """C03: запись свойства через API видна зонду — основа оракула «документ не менялся»."""
        doc, _ = build.plate(self.s, 50, 50, 4, SHEET4)
        cpm = doc.Extension.CustomPropertyManager("")
        mark = self.mark("C03")
        self.assertEqual(0, com.prop_set(cpm, "КОНТРАКТ", "1"))
        self.assertEqual(0, cpm.Add3("КОНТРАКТ", 30, "2", 2))
        self.assertEqual(0, cpm.Delete2("КОНТРАКТ"))
        kinds = [e["event"] for e in self.s.journal.property_writes(mark)]
        self.assertIn("AddCustomPropertyNotify", kinds)
        self.assertIn("ChangeCustomPropertyNotify", kinds)
        self.assertIn("DeleteCustomPropertyNotify", kinds)

    def test_C04_favourite_material_command_is_bracketed_by_command_events(self):
        """C04: команда избранного материала 2007 даёт CommandOpenPreNotify и CommandCloseNotify."""
        doc, _ = build.plate(self.s, 50, 50, 4, SHEET4)
        mark = self.mark("C04")
        self.s.run_command(doc, 2007)
        cmds = [(e["event"], e["command"]) for e in self.s.journal.events(mark)
                if e["event"] in ("CommandOpenPreNotify", "CommandCloseNotify")]
        self.assertIn(("CommandOpenPreNotify", 2007), cmds)
        self.assertIn(("CommandCloseNotify", 2007), cmds)


if __name__ == "__main__":
    unittest.main()
