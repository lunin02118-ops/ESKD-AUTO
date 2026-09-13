# -*- coding: utf-8 -*-
"""E2E, группа P — персистентность реквизитов и события сохранения (план, §4.7).

Оракул — состояние файла на диске, прочитанное с выключенной службой надстройки.
"""
import unittest
from pathlib import Path

from eskd_e2e import build, com, oracles, paths, testing
from eskd_e2e.testing import SwTestCase, known_defect, tags

SHEET4 = "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
SHEET6 = "Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
TUBE80 = "Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86"
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A04 = "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt"
A08 = "ПРТИ.468211.110 СБ Узел опоры.sldasm"
A10 = "ПРТИ.468211.101 Пластина опорная.slddrw"
A13 = "ПРТИ.468211.106 Крышка.sldprt"
V = oracles.value


class PersistenceNewDocuments(SwTestCase):

    def _assert_named_plate(self, disk, designation, title, mass="<FONT size=3.5>0,63"):
        self.assertEqual(designation, V(disk, "Обозначение"), "обозначение (общие)")
        self.assertEqual(title, V(disk, "Наименование"), "наименование (общие)")
        self.assertEqual(title, V(disk, "Наименование_ФБ"), "наименование для штампа")
        self.assertEqual(designation, V(disk, "Обозначение", "00"), "обозначение (конфигурация)")
        self.assertIn("<STACK size=1>", V(disk, "Материал_ФБ", "00") or "", "дробь материала в конфигурации")
        self.assertEqual(mass, V(disk, "Масса_ФБ", "00"), "масса в конфигурации")

    @tags("smoke")
    @known_defect("Д-01")
    def test_P01_new_part_api_save_as_writes_names_to_disk(self):
        """P01: новая деталь → API «Сохранить как» → на диске обозначение и наименование из имени файла."""
        doc, _ = build.plate(self.s, 200, 100, 4, SHEET4)
        target = self.path("ПРТИ.468211.121 Пластина новая.sldprt")
        self.s.save_as(doc, target)
        self.wait_idle()
        self.assertEqual([2, 1], [t for t, _ in self.saves()],
                         "ожидалось «Сохранить как» и одно отложенное пересохранение")
        self.s.close(doc)
        self._assert_named_plate(self.persisted(target), "ПРТИ.468211.121", "Пластина новая")

    @known_defect("Д-01")
    def test_P02_new_part_ui_save_writes_names_to_disk(self):
        """P02: новая деталь → команда «Сохранить» (путь команды интерфейса) → реквизиты на диске."""
        doc, _ = build.plate(self.s, 200, 100, 4, SHEET4)
        target = self.path("ПРТИ.468211.123 Пластина через команду.sldprt")
        self.s.ui_save_as(doc, target, command=2)
        self.wait_idle()
        self.s.close(doc)
        self._assert_named_plate(self.persisted(target), "ПРТИ.468211.123", "Пластина через команду")


class PersistenceSave(SwTestCase):

    def _grow_plate(self, doc):
        # Дополнительная площадка 100×100×4: масса 0,628 → 0,942 кг
        build.sketch_rectangles(doc, [(0.15, -0.05, 0.25, 0.05)])
        build.extrude(doc, 0.004)
        doc.ForceRebuild3(False)

    @tags("smoke")
    def test_P03_api_save_writes_new_mass(self):
        """P03: изменение геометрии → API Save3 → новая масса на диске."""
        path, doc = self.open_copy(A01)
        self._grow_plate(doc)
        ok, err, _ = self.s.save(doc)
        self.assertTrue(ok, f"Save3 err={err}")
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual("<FONT size=3.5>0,94", V(disk, "Масса_ФБ", "00"))

    def test_P04_command_save_writes_new_mass(self):
        """P04: то же через команду «Сохранить» (Ctrl+S)."""
        path, doc = self.open_copy(A01)
        self._grow_plate(doc)
        self.s.run_command(doc, 2)
        self.s.close(doc)
        self.assertEqual("<FONT size=3.5>0,94", V(self.persisted(path), "Масса_ФБ", "00"))

    @tags("smoke")
    @known_defect("Д-02")
    def test_P05_save_as_renames_part(self):
        """P05: «Сохранить как» (API) под новым именем — новый файл получает новые реквизиты, исходный не меняется."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        target = self.path("ПРТИ.468211.131 Пластина переименованная.sldprt")
        self.s.save_as(doc, target)
        self.wait_idle()
        self.s.close(doc)
        new = self.persisted(target)
        self.assertEqual("ПРТИ.468211.131", V(new, "Обозначение"))
        self.assertEqual("ПРТИ.468211.131", V(new, "Обозначение", "00"))
        self.assertEqual("Пластина переименованная", V(new, "Наименование"))
        self.assertEqual("Пластина переименованная", V(new, "Наименование_ФБ"))
        self.assertEqual("ПРТИ.468211.101", V(self.persisted(path), "Обозначение"), "исходный файл")

    @known_defect("Д-02")
    def test_P05_ui_save_as_renames_part(self):
        """P05: «Сохранить как» командой интерфейса — то же для пути UI."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        target = self.path("ПРТИ.468211.133 Пластина из диалога.sldprt")
        self.s.ui_save_as(doc, target)
        self.wait_idle()
        self.s.close(doc)
        self.assertEqual("ПРТИ.468211.133", V(self.persisted(target), "Обозначение"))

    @known_defect("Д-02")
    def test_P05_save_as_renames_assembly(self):
        """P05: сборка «Сохранить как» — новое обозначение и код СБ в конфигурации."""
        self.copy_fixtures(A01, A04)
        path, doc = self.open_copy(A08)
        self.s.save(doc)
        target = self.path("ПРТИ.468211.132 СБ Узел новый.sldasm")
        self.s.save_as(doc, target)
        self.wait_idle()
        self.s.close(doc)
        new = self.persisted(target)
        self.assertEqual("ПРТИ.468211.132", V(new, "Обозначение"))
        self.assertEqual("Узел новый", V(new, "Наименование"))
        self.assertEqual(" СБ", V(new, "Сборка1_ФБ", "00"))

    def test_P06_save_as_keeps_manual_designation(self):
        """P06: «Сохранить как» детали с ручным обозначением (RenameSWP = 1) — обозначение сохраняется."""
        path, doc = self.open_copy(A13)
        target = self.path("ПРТИ.468211.141 Крышка новая.sldprt")
        self.s.save_as(doc, target)
        self.wait_idle()
        self.s.close(doc)
        new = self.persisted(target)
        self.assertEqual("ПРТИ.468211.199", V(new, "Обозначение"))

    def test_P07_save_as_copy_keeps_document_and_warns(self):
        """P07: «Сохранить как копию» — документ в памяти не меняется, в журнале предупреждение."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        copy = self.path("ПРТИ.468211.151 Копия.sldprt")
        ok, err, _ = self.s.save_as(doc, copy, options=com.SAVE_SILENT | com.SAVE_COPY)
        self.assertTrue(ok, f"копия не сохранена: {err}")
        self.wait_idle()
        self.assertEqual(str(path).lower(), str(doc.GetPathName).lower())
        self.assertTrue(any("Копия" in ln and "реквизитами исходного" in ln for ln in self.addin_log.new_lines()),
                        "нет предупреждения о копии в журнале надстройки")
        self.s.close(doc)

    def test_P07_save_as_copy_with_fix_copies_updates_copy(self):
        """P07: «Сохранить как копию» при FixCopies = 1 — копия на диске получает свои реквизиты, документ и исходный файл прежние."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        copy = self.path("ПРТИ.468211.152 Копия исправленная.sldprt")
        self.s.set_settings(FixCopies=1)
        # Надстройка сама откроет и закроет копию: зонд не должен держать на ней подписку (закрытие чужой подписки роняет SolidWorks).
        self.s.probe.call("doc_events", "0")
        try:
            ok, err, _ = self.s.save_as(doc, copy, options=com.SAVE_SILENT | com.SAVE_COPY)
            self.assertTrue(ok, f"копия не сохранена: {err}")
            self.wait_idle(4.0)
        finally:
            self.s.probe.call("doc_events", "1")
            self.s.set_settings(FixCopies=0)
        self.assertEqual(str(path).lower(), str(doc.GetPathName).lower(), "документ в памяти сменил путь")
        self.assertEqual("ПРТИ.468211.101", V(oracles.dump_properties(doc), "Обозначение"), "реквизиты документа в памяти изменены")
        self.s.close(doc)
        new = self.persisted(copy)
        self.assertEqual("ПРТИ.468211.152", V(new, "Обозначение"))
        self.assertEqual("ПРТИ.468211.152", V(new, "Обозначение", "00"))
        self.assertEqual("Копия исправленная", V(new, "Наименование"))
        self.assertEqual("ПРТИ.468211.101", V(self.persisted(path), "Обозначение"), "исходный файл изменён")
        self.assertEqual([], self.addin_errors())


class PersistenceOpen(SwTestCase):

    def _assert_open_is_read_only(self, path, doc):
        self.assertFalse(bool(doc.GetSaveFlag), "документ помечен изменённым сразу после открытия")
        memory, disk = self.memory_equals_disk(doc, path)
        self.assertEqual(disk, memory, "свойства в памяти отличаются от файла — надстройка писала при открытии")

    @tags("smoke")
    @known_defect("Д-03")
    def test_P09_open_part_does_not_modify(self):
        """P09: открытие детали ничего не меняет."""
        path, doc = self.open_copy(A01)
        self._assert_open_is_read_only(path, doc)

    @known_defect("Д-03")
    def test_P09_open_assembly_does_not_modify(self):
        """P09: открытие сборки ничего не меняет."""
        self.copy_fixtures(A01, A04)
        path, doc = self.open_copy(A08)
        self._assert_open_is_read_only(path, doc)

    @known_defect("Д-06")
    def test_P09_open_drawing_does_not_modify(self):
        """P09: открытие чертежа не меняет ни чертёж, ни модель."""
        self.copy_fixture(A01)
        path, doc = self.open_copy(A10)
        self._assert_open_is_read_only(path, doc)

    @known_defect("Д-03")
    def test_P09_open_real_part_does_not_modify(self):
        """P09: открытие реальной детали корпуса Б (B-01 с сортаментом из библиотеки) ничего не меняет."""
        src = paths.CORPUS_B_LIBRARY["B-01"][0]
        path = self.s.workspace_copy(src, subdir=self._case_name())
        doc = self.s.open(path)
        self._assert_open_is_read_only(path, doc)

    @known_defect("Д-04")
    def test_P09_switching_windows_writes_nothing(self):
        """P09: переключение между открытыми документами не пишет свойства и не помечает их изменёнными."""
        _, part = self.open_copy(A01)
        _, assembly = self.open_copy(A08, A04)
        mark = self.mark("P09-switch")
        for doc in (part, assembly, part, assembly):
            self.s.activate(doc)
            self.wait_idle(1.0)
        self.assertNoPropertyWrites(mark, "переключение окон изменило свойства")
        self.assertFalse(bool(part.GetSaveFlag), "деталь помечена изменённой")
        self.assertFalse(bool(assembly.GetSaveFlag), "сборка помечена изменённой")

    def test_P10_no_errors_in_addin_log_during_save_cycle(self):
        """P10: полный цикл открытие → сохранение → «Сохранить как» без ошибок в журнале надстройки."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        self.s.save_as(doc, self.path("ПРТИ.468211.161 Пластина журнал.sldprt"))
        self.wait_idle()
        self.s.close(doc)
        self.assertEqual([], self.addin_errors())

    def test_P11_material_command_updates_stamp_before_save(self):
        """P11: закрытие команды материала (swCommands_Favorite_Material_2 = 2008) — дробь в памяти до сохранения, на диске после (Д-05).

        RunCommand избранного без выделения в интерфейсе материал не меняет, поэтому материал назначается через API,
        а команда проверяет перехват CommandCloseNotify надстройкой.
        """
        path, doc = self.open_copy(A01)
        build.set_material(doc, SHEET6, "00")
        self.wait_idle()
        before = V(oracles.dump_properties(doc), "Материал_ФБ", "00") or ""
        self.assertNotIn("6,0", before, "без команды и сохранения надстройка не должна была обновить дробь")
        self.assertTrue(self.s.run_command(doc, 2008), "команда не выполнена")
        self.wait_idle()
        self.assertIn("6,0", V(oracles.dump_properties(doc), "Материал_ФБ", "00") or "", "Материал_ФБ в памяти после команды")
        self.s.save(doc)
        self.s.close(doc)
        self.assertIn("6,0", V(self.persisted(path), "Материал_ФБ", "00") or "", "Материал_ФБ на диске")

    def test_P12_api_material_change_reaches_disk_on_save(self):
        """P12: смена материала через API → после сохранения новая дробь на диске."""
        path, doc = self.open_copy(A01)
        build.set_material(doc, SHEET6, "00")
        self.s.save(doc)
        self.s.close(doc)
        self.assertIn("6,0", V(self.persisted(path), "Материал_ФБ", "00") or "")


class BasicMaterialScenario(SwTestCase):
    """Базовый сценарий конструктора: деталь из шаблона → материал из библиотеки → «Сохранить как» → чертёж."""

    def _check(self, doc, file_name, material, shape):
        custom = build.material_library()[material]["custom"]
        designation, line = custom["Обозначение_ГОСТ"], custom["Обозначение_Строка"]
        self.assertTrue(designation.startswith(shape + " <STACK size=1>"), f"библиотека: {designation}")
        numerator, denominator = designation.split("<STACK size=1>")[1].split("</STACK>")[0].split("<OVER>")
        target = self.path(file_name)
        self.s.save_as(doc, target)
        self.wait_idle()
        self.s.close(doc)
        disk = self.persisted(target)
        self.assertEqual("<FONT size=1.8> <FONT size=3.5>" + designation, V(disk, "Материал_ФБ", "00"),
                         "графа 3 — «Обозначение_ГОСТ» библиотеки в разметке MProp")
        self.assertEqual(designation, V(disk, "Материал_Таблица", "00"), "таблица — «Обозначение_ГОСТ», как пишет MProp")
        self.assertEqual(line, V(disk, "Материал_Строка", "00"), "одна строка для сводной ведомости — «Обозначение_Строка»")

        model = self.s.open(target)
        drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
        build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
        build.model_view(drw, target, 150, 180)
        build.wait(1.0)
        pdf = self.path(target.stem + ".pdf")
        ok, err, _ = self.s.save_as(drw, pdf)
        self.s.close(drw)
        self.s.close(model)
        self.assertTrue(ok and pdf.exists(), f"PDF не выгружен, код {err}")
        rows = oracles.pdf_rows(pdf, 420, oracles.form1_cells(420)["g3_material"])
        self.assertEqual([numerator, shape, denominator], [text for _, _, text in rows],
                         f"графа 3 в PDF: над чертой сортамент, форма по центру, под чертой марка: {rows}")

    @known_defect("Д-34")
    def test_P15_sheet_material_fraction_in_stamp_and_one_line_record(self):
        """P15: пластина из «Лист 4,0 … / Ст3сп …» — в графе 3 чертежа «Лист» и дробь в две строки, одна строка для ведомости."""
        doc, _ = build.plate(self.s, 200, 100, 4, SHEET4)
        self._check(doc, "ПРТИ.468211.131 Пластина.sldprt", SHEET4, "Лист")

    @known_defect("Д-34")
    def test_P15_tube_material_fraction_in_stamp_and_one_line_record(self):
        """P15: стойка из «Труба 80х80х4,0 … / В 10 …» — в графе 3 чертежа «Труба» и дробь в две строки, одна строка для ведомости."""
        doc, _ = build.square_tube(self.s, 80, 4, 300, TUBE80)
        self._check(doc, "ПРТИ.468211.132 Стойка.sldprt", TUBE80, "Труба")


class RealPartLibraryMaterial(SwTestCase):
    """Корпус Б: реальная труба B-01, в которой конструктор назначил сортамент из библиотеки (fixtures/build_corpus_b.py)."""

    @known_defect("Д-46")
    def test_P16_real_tube_with_library_sortament_gets_library_fraction(self):
        """P16: реальная деталь B-01, конфигурация «Труба 80х80х4»: материал — «Труба 80х80х4,0 ГОСТ 8639-82 / В 10 …» из
        библиотеки, в графе 3 осталась старая дробь MProp → сохранение: графа 3 и таблица — дробь библиотеки, «Материал_Строка»
        — строка библиотеки, масса графы 5 — по материалу; PDF чертежа B-01: «Труба» и дробь в две строки в графе 3."""
        part_src, drawing_src = paths.CORPUS_B_LIBRARY["B-01"]
        fixture = testing.manifest()["corpus_b"]["B-01"]
        cfg = "Труба 80х80х4"
        part = self.s.workspace_copy(part_src, subdir=self._case_name())
        drawing = self.s.workspace_copy(drawing_src, subdir=self._case_name())
        self.assertIn("<STACK size=1>Труба 80х80х4 ГОСТ 8639-82<OVER>", V(self.persisted(part), "Материал_ФБ", cfg) or "",
                      "в копии реальной детали — старая дробь MProp")
        doc = self.s.open(part)
        self.assertEqual((TUBE80, "Библиотека_Материалов_ГОСТ"), build.material_of(doc, cfg), "в детали назначен сортамент из библиотеки")
        self.s.save(doc)
        custom = build.material_library()[TUBE80]["custom"]
        drw = self.s.open(drawing)
        width = round(float(com.as_list(com.dyn(drw.GetCurrentSheet).GetProperties2)[5]) * 1000.0)
        pdf = self.path("ПРТИ.468211.010.pdf")
        ok, err, _ = self.s.save_as(drw, pdf)
        self.s.close(drw)
        self.s.close(doc)
        disk = self.persisted(part)
        self.assertEqual("<FONT size=1.8> <FONT size=3.5>" + custom["Обозначение_ГОСТ"], V(disk, "Материал_ФБ", cfg), "графа 3 — дробь библиотеки")
        self.assertEqual(custom["Обозначение_ГОСТ"], V(disk, "Материал_Таблица", cfg), "таблица — дробь библиотеки")
        self.assertEqual(custom["Обозначение_Строка"], V(disk, "Материал_Строка", cfg), "сводная ведомость — строка библиотеки")
        mass = ("%.2f" % fixture["mass_kg"][cfg]).replace(".", ",")
        self.assertEqual("<FONT size=3.5>" + mass, V(disk, "Масса_ФБ", cfg), "графа 5 — масса трубы из материала библиотеки")
        self.assertTrue(ok and pdf.exists(), f"PDF не выгружен, код {err}")
        numerator, denominator = custom["Обозначение_ГОСТ"].split("<STACK size=1>")[1].split("</STACK>")[0].split("<OVER>")
        rows = [text for _, _, text in oracles.pdf_rows(pdf, width, oracles.form1_cells(width)["g3_material"])]
        self.assertEqual([numerator, "Труба", denominator], rows, f"графа 3 в PDF чертежа B-01: {rows}")


if __name__ == "__main__":
    unittest.main()
