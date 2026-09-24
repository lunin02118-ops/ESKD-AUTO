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
# swUserPreferenceIntegerValue_e
UNITS_MASS, UNITS_MASS_DECIMALS, UNIT_SYSTEM = 259, 261, 263


def events_of(journal, mark, names):
    return [e for e in journal.events(mark) if e["event"] in names]


class ContractSaveEvents(SwTestCase):
    doc_events = True
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
    doc_events = True
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


class ContractDestroyEvents(SwTestCase):
    """C08 (сверка SW API 23.09.2026, №9): что приходит, когда закрывают окно детали, а сама деталь остаётся в памяти
    как компонент открытой сборки. Надстройка раньше слушала только DestroyNotify без типа и по нему забывала документ."""
    doc_events = True
    load_eskd = False

    def test_C08_hidden_document_gets_destroy_notify2_hidden(self):
        """C08: окно детали закрыто, деталь осталась в сборке — DestroyNotify2(1, «скрыт»); при закрытии сборки —
        DestroyNotify2(0, «разрушен»). Приходит ли при скрытии старый DestroyNotify, записывается в C08.txt."""
        part_path = self.path("КОНТР.000008.001 Пластина.sldprt")
        doc, _ = build.plate(self.s, 100, 50, 4, SHEET4)
        self.assertTrue(self.s.save_as(doc, part_path)[0], "деталь сохранена")
        asm, _ = build.assembly(self.s, [(part_path, 0, 0, 0)])
        self.assertTrue(self.s.save_as(asm, self.path("КОНТР.000008.000 Сборка.sldasm"))[0], "сборка сохранена")

        mark = self.mark("C08-hide")
        self.s.sw.CloseDoc(doc.GetTitle)
        self.s._opened[:] = [d for d in self.s._opened if d is not doc]
        self.assertIsNotNone(self.s.sw.GetOpenDocumentByName(str(part_path)), "деталь осталась в памяти")
        hidden = [(e["event"], e.get("destroyType")) for e in self.s.journal.events(mark)
                  if e["event"] in ("DestroyNotify", "DestroyNotify2") and e.get("title", "").lower().startswith("контр.000008.001")]

        mark = self.mark("C08-destroy")
        self.s.close_all()
        destroyed = [(e["event"], e.get("destroyType")) for e in self.s.journal.events(mark)
                     if e["event"] in ("DestroyNotify", "DestroyNotify2") and e.get("title", "").lower().startswith("контр.000008.001")]
        self.path("C08.txt").write_text(f"скрытие: {hidden}\nзакрытие сборки: {destroyed}\n", encoding="utf-8")
        self.assertIn(("DestroyNotify2", 1), hidden, "скрытие — DestroyNotify2 с типом «скрыт»")
        self.assertIn(("DestroyNotify2", 0), destroyed, "закрытие сборки — DestroyNotify2 с типом «разрушен»")


class ContractOpenEvents(SwTestCase):
    """C07 (сверка SW API 23.09.2026, №11): FileOpenPostNotify приходит только на документ, который открыли, — не на
    каждую модель сборки (для компонентов SolidWorks шлёт DocumentLoadNotify2). Тяжёлой работы на каждую модель нет."""
    load_eskd = False

    def test_C07_file_open_post_notify_only_for_top_document(self):
        """C07: открытие сборки из двух деталей — один FileOpenPostNotify (сборка), DocumentLoadNotify2 — на каждую модель."""
        parts = []
        for number, length in (("001", 100), ("002", 140)):
            doc, _ = build.plate(self.s, length, 50, 4, SHEET4)
            path = self.path(f"КОНТР.000007.{number} Пластина.sldprt")
            self.assertTrue(self.s.save_as(doc, path)[0], "деталь сохранена")
            parts.append(path)
        asm, _ = build.assembly(self.s, [(parts[0], 0, 0, 0), (parts[1], 0, 0.1, 0)])
        asm_path = self.path("КОНТР.000007.000 Сборка.sldasm")
        self.assertTrue(self.s.save_as(asm, asm_path)[0], "сборка сохранена")
        self.s.close_all()

        mark = self.mark("C07-open")
        self.s.open(asm_path)
        opened = [e.get("fileName", "") for e in self.s.journal.of("FileOpenPostNotify", mark)]
        loaded = self.s.journal.of("DocumentLoadNotify2", mark)
        self.path("C07.txt").write_text(f"FileOpenPostNotify: {opened}\nDocumentLoadNotify2: {len(loaded)}\n", encoding="utf-8")
        self.assertEqual([str(asm_path).lower()], [o.lower() for o in opened], "FileOpenPostNotify — только сборка")
        self.assertGreaterEqual(len(loaded), 3, "DocumentLoadNotify2 — сборка и обе детали")
        self.s.close_all()


class ContractMassExpression(SwTestCase):
    """Спайки S-3 и S-4 (13.09.2026): на этом контракте стоит масса по конфигурациям (Д-36)."""
    load_eskd = False

    def _resolved(self, doc, cfg, name):
        raw, res = com.ref_str(""), com.ref_str("")
        doc.Extension.CustomPropertyManager(cfg).Get4(name, False, raw, res)
        return res.value

    @tags("smoke")
    def test_C05_sw_mass_expression_per_configuration(self):
        """C05: «SW-Mass@@конф@файл» даёт массу неактивной конфигурации без активации, после правки геометрии сразу новую, с точкой."""
        path = self.copy_fixture("ПРТИ.468211.103 Планка.sldprt")
        doc = self.s.open(path)
        for cfg in ("00", "01", "02"):
            com.prop_set(doc.Extension.CustomPropertyManager(cfg), "Проба_масса", f'"SW-Mass@@{cfg}@{path.name}"')
        self.assertEqual({"00": "0.13", "01": "0.19", "02": "0.25"},
                         {c: self._resolved(doc, c, "Проба_масса") for c in ("00", "01", "02")}, "эталон A-03, точка")
        build.sketch_rectangles(doc, [(-0.20, -0.02, -0.16, 0.02)])
        build.extrude(doc, 0.004)
        doc.ForceRebuild3(False)
        self.assertEqual({"00": "0.18", "01": "0.24", "02": "0.30"},
                         {c: self._resolved(doc, c, "Проба_масса") for c in ("00", "01", "02")},
                         "новая бобышка во всех конфигурациях — масса неактивных пересчитана без активации")
        self.s.close(doc)

    def test_C06_sw_mass_expression_units_precision_and_file_name(self):
        """C06: «SW-Mass» считается в единицах и с точностью массы документа (шаблон ЕСКД — кг, 2 знака; MMGS — граммы;
        IPS — фунты), единицу показывает swUnitsMassPropMass при любой системе единиц; имя файла в выражении SolidWorks
        ставит своё и меняет при «Сохранить как» (спайк S-4)."""
        path = self.copy_fixture("ПРТИ.468211.103 Планка.sldprt")
        doc = self.s.open(path)
        ext = doc.Extension
        com.prop_set(ext.CustomPropertyManager("01"), "Проба_масса", '"SW-Mass@@01@Другой файл.sldprt"')
        self.assertEqual(f'"SW-Mass@@01@{path.name}"', self._raw(doc, "01", "Проба_масса"), "имя файла — своё")

        def state():
            return (ext.GetUserPreferenceInteger(UNITS_MASS, 0), ext.GetUserPreferenceInteger(UNITS_MASS_DECIMALS, 0),
                    self._resolved(doc, "01", "Проба_масса"))
        self.assertEqual((3, 2, "0.19"), state(), "шаблон ЕСКД: кг, два знака")
        ext.SetUserPreferenceInteger(UNIT_SYSTEM, 0, 5)
        self.assertEqual((2, 2, "188.40"), state(), "MMGS: граммы")
        ext.SetUserPreferenceInteger(UNIT_SYSTEM, 0, 3)
        self.assertEqual((4, 2, "0.42"), state(), "IPS: фунты")
        ext.SetUserPreferenceInteger(UNIT_SYSTEM, 0, 4)
        ext.SetUserPreferenceInteger(UNITS_MASS, 0, 3)
        ext.SetUserPreferenceInteger(UNITS_MASS_DECIMALS, 0, 4)
        self.assertEqual((3, 4, "0.1884"), state(), "свои единицы: кг, четыре знака")
        target = self.path("КОНТР.000006.001 Планка.sldprt")
        ok, err, _ = self.s.save_as(doc, target)
        self.assertTrue(ok, f"SaveAs не выполнен, err={err}")
        self.assertEqual(f'"SW-Mass@@01@{target.name}"', self._raw(doc, "01", "Проба_масса"), "«Сохранить как» — новое имя")
        self.s.close(doc)

    def _raw(self, doc, cfg, name):
        raw, res = com.ref_str(""), com.ref_str("")
        doc.Extension.CustomPropertyManager(cfg).Get4(name, False, raw, res)
        return raw.value


if __name__ == "__main__":
    unittest.main()
