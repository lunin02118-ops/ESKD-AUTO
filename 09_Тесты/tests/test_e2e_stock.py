# -*- coding: utf-8 -*-
"""E2E, группа T — материал по геометрии: типоразмер профиля и толщина листа против библиотеки ЕСКД (Р-8).

Проверяется правило владельца от 20.09.2026 целиком, на настоящих деталях в настоящем SolidWorks:

    материала нет, подходящий один      -> подставить молча;
    материала нет, подходящих несколько -> спросить (в прогоне окно выключено, вердикт читается из надстройки);
    материала нет, подходящих нет       -> замечание, ничего не менять;
    материал выбран и совпадает         -> ничего;
    материал выбран и не совпадает      -> уведомление, выбор конструктора не переписывать.
"""
import unittest

from eskd_e2e import build, com, oracles, paths
from eskd_e2e.testing import SwTestCase, tags

V = oracles.value

PROFILES = paths.ROOT / "04_Библиотеки_Материалов_и_Профилей" / "Профили сварных деталей" / "Сортамент ГОСТ"
FLAT_OVAL = PROFILES / "Труба плоскоовальная ГОСТ 8644-68" / "30х15х1,5.SLDLFP"
RECTANGLE = PROFILES / "Прямоугольная труба ГОСТ 8645-68" / "30х15х1,5.sldlfp"

TUBE_MATERIAL = "Труба ПО 30х15х1,5 ГОСТ 8644-68 / 08пс ГОСТ 13663-86"
TUBE_LINE = "Труба ПО 30х15х1,5 ГОСТ 8644-68 / 08пс ГОСТ 13663-86"
SHEET8 = "Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024"
SHEET8_LINE = "Лист Б-ПН-НО-8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024"


class Stock(SwTestCase):

    # ------------------------------------------------------------------ помощники
    def _report(self):
        """Строки «папка|вердикт|типоразмер|ГОСТ|материал сейчас|кандидаты» активного документа."""
        raw = str(com.call(self.s.eskd(), "StockReport") or "")
        return [line.split("|") for line in raw.splitlines() if line.strip()]

    def _verdicts(self):
        return [row[1] for row in self._report()]

    def _tube(self, name, profile=FLAT_OVAL, material=None):
        doc = build.structural_tube(self.s, 400, profile, material)
        path = self.s.ws(self._case_name(), name)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.s.save_as(doc, path)
        return path, doc

    def _sheet(self, name, thickness_mm, material=None):
        doc = build.sheet_metal_plate(self.s, 200, 100, thickness_mm, material)
        path = self.s.ws(self._case_name(), name)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.s.save_as(doc, path)
        return path, doc

    # ------------------------------------------------------------------ подстановка молча
    @tags("smoke")
    def test_T01_flat_oval_tube_without_material_gets_it_from_library(self):
        """T01: случай владельца — плоскоовальная труба 30х15х1,5 без материала. В библиотеке она одна,
        значит спрашивать нечего: материал подставляется сам и доходит до свойств."""
        path, doc = self._tube("ПРТИ.301111.001 Распорка.sldprt")
        self.assertEqual(("", ""), build.material_of(doc, ""), "исходно материала нет")
        self.assertEqual(["Assign"], self._verdicts(), "надстройка берётся назначить материал")

        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        name, _ = build.material_of(doc, "")
        self.assertEqual(TUBE_MATERIAL, name, "материал назначен детали по типоразмеру профиля")

        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual(TUBE_LINE, V(disk, "Материал_Строка", "00") or V(disk, "Материал_Строка"),
                         "материал доехал до свойств — в книге ЛЗК не будет «?»")

    @tags("smoke")
    def test_T02_sheet_8mm_without_material_gets_it_silently(self):
        """T02: лист 8 мм. В библиотеке две записи, но свойства у них одинаковые — для конструктора это
        один материал, поэтому окна выбора быть не должно."""
        path, doc = self._sheet("ПРТИ.301111.002 Косынка.sldprt", 8)
        self.assertEqual(["Assign"], self._verdicts(), "однозначный выбор: редакции ГОСТа схлопнуты")

        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        name, _ = build.material_of(doc, "")
        self.assertEqual(SHEET8, name, "назначен лист 8 мм свежей редакции")

        self.s.save(doc)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual(SHEET8_LINE, V(disk, "Материал_Строка", "00") or V(disk, "Материал_Строка"), "графа 3")

    # ------------------------------------------------------------------ спросить
    def test_T03_sheet_6mm_offers_choice_and_assigns_nothing(self):
        """T03: лист 6 мм бывает из Ст3сп и из 09Г2С — выбрать может только конструктор.
        Сама надстройка не назначает ничего."""
        path, doc = self._sheet("ПРТИ.301111.003 Пластина.sldprt", 6)
        rows = self._report()
        self.assertEqual(1, len(rows), rows)
        self.assertEqual("Choose", rows[0][1], "нужен выбор конструктора")
        candidates = rows[0][5].split(";")
        self.assertEqual(2, len(candidates), f"два варианта по марке стали: {candidates}")
        self.assertTrue(any("09Г2С" in c for c in candidates), candidates)
        self.assertTrue(any("Ст3сп" in c for c in candidates), candidates)

        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        self.assertEqual(("", ""), build.material_of(doc, ""), "за конструктора материал не выбран")

        self.assertEqual(0, int(com.call(self.s.eskd(), "ApplyStockMaterialSilent")),
                         "без выбора назначать нечего")

    # ------------------------------------------------------------------ нет в библиотеке
    def test_T04_size_outside_library_is_reported_not_guessed(self):
        """T04: прямоугольная труба 30х15х1,5 по ГОСТ 8645-68. Такой же типоразмер есть у плоскоовальной,
        но это другой сортамент — подставлять её материал нельзя. Именно так ошиблись в NC3-7R.02.001."""
        path, doc = self._tube("ПРТИ.301111.004 Перемычка.sldprt", profile=RECTANGLE)
        rows = self._report()
        self.assertEqual(1, len(rows), rows)
        self.assertEqual("NotInLibrary", rows[0][1], f"типоразмера нет в этом сортаменте: {rows[0]}")
        self.assertEqual("", rows[0][5], "кандидатов нет — чужой сортамент не предлагается")

        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        self.assertEqual(("", ""), build.material_of(doc, ""), "материал не выдуман")
        warnings = str(com.call(self.s.eskd(), "LastSyncWarnings") or "")
        self.assertIn("не найден в библиотеке", warnings, warnings)

    # ------------------------------------------------------------------ расхождение
    def test_T05_wrong_material_is_reported_and_kept(self):
        """T05: конструктор выбрал материал, потом сменил профиль. Уведомляем, но его выбор не переписываем —
        ошибка может быть и в модели, решать конструктору."""
        path, doc = self._tube("ПРТИ.301111.005 Стойка.sldprt", material=SHEET8)
        rows = self._report()
        self.assertEqual(1, len(rows), rows)
        self.assertEqual("Mismatch", rows[0][1], f"расхождение материала и профиля: {rows[0]}")
        self.assertEqual(SHEET8, rows[0][4], "виден материал, который стоит сейчас")

        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        name, _ = build.material_of(doc, "")
        self.assertEqual(SHEET8, name, "материал конструктора остался нетронутым")
        warnings = str(com.call(self.s.eskd(), "LastSyncWarnings") or "")
        self.assertIn("не соответствует геометрии", warnings, warnings)

    # ------------------------------------------------------------------ всё сходится
    def test_T06_matching_material_causes_no_changes(self):
        """T06: материал стоит и профилю соответствует — надстройке говорить нечего и трогать нечего."""
        path, doc = self._tube("ПРТИ.301111.006 Труба.sldprt", material=TUBE_MATERIAL)
        self.assertEqual(["Ok"], self._verdicts(), "расхождения нет")
        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        warnings = str(com.call(self.s.eskd(), "LastSyncWarnings") or "")
        self.assertNotIn("не соответствует", warnings, warnings)
        self.assertNotIn("не найден в библиотеке", warnings, warnings)
        name, _ = build.material_of(doc, "")
        self.assertEqual(TUBE_MATERIAL, name, "материал не тронут")

    # ------------------------------------------------------------------ обход изделия
    @tags("smoke")
    def test_T07_product_walk_fills_parts_assigned_in_assembly_context(self):
        """T07: главная жалоба владельца. Материал назначают в дереве сборки, детали отдельно не сохраняют —
        и свойства остаются пустыми. Обход изделия по кнопке заполняет их все за один раз."""
        folder = self.s.ws(self._case_name())
        folder.mkdir(parents=True, exist_ok=True)
        tube = folder / "ПРТИ.301111.011 Лежак.sldprt"
        sheet = folder / "ПРТИ.301111.012 Закладная.sldprt"

        # Детали заводятся так, как они приходят от конструктора: материала нет, подбор при сохранении выключен —
        # это и есть жалоба владельца, когда деталь отдельно никто не сохранял.
        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc = build.structural_tube(self.s, 500, FLAT_OVAL, None)
            self.s.save_as(doc, tube)
            doc = build.sheet_metal_plate(self.s, 150, 80, 8, None)
            self.s.save_as(doc, sheet)
            asm, _ = build.assembly(self.s, [(tube, 0, 0, 0), (sheet, 0, 0.2, 0)])
            asm_path = folder / "ПРТИ.301111.010 СБ Рама.sldasm"
            self.s.save_as(asm, asm_path)
            self.s.close_all()
            for path in (tube, sheet):
                self.assertIsNone(V(self.persisted(path), "Материал_Строка", "00"), f"{path.name}: материала ещё нет")
        finally:
            self.s.set_settings(AutoStockMaterial=1)

        asm = self.s.open(asm_path)
        self.s.activate(asm)
        status = str(com.call(self.s.eskd(), "SyncProductSilent") or "")
        self.assertIn("деталей 2", status, status)
        self.assertIn("материалов назначено 2", status, status)
        self.s.close_all()

        for path, expected in ((tube, TUBE_LINE), (sheet, SHEET8_LINE)):
            disk = self.persisted(path)
            got = V(disk, "Материал_Строка", "00") or V(disk, "Материал_Строка")
            self.assertEqual(expected, got, f"{path.name}: материал записан обходом изделия")

    def test_T08_product_walk_skips_parts_outside_the_order_folder(self):
        """T08: чужие файлы обход не трогает. Покупные и крепёж из библиотеки лежат вне папки изделия —
        переписывать их нельзя."""
        folder = self.s.ws(self._case_name(), "изделие")
        outside = self.s.ws(self._case_name(), "чужое")
        folder.mkdir(parents=True, exist_ok=True)
        outside.mkdir(parents=True, exist_ok=True)
        own = folder / "ПРТИ.301111.021 Стойка.sldprt"
        alien = outside / "ПРТИ.301111.022 Втулка.sldprt"

        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc = build.structural_tube(self.s, 300, FLAT_OVAL, None)
            self.s.save_as(doc, own)
            doc = build.sheet_metal_plate(self.s, 100, 60, 8, None)
            self.s.save_as(doc, alien)
            asm, _ = build.assembly(self.s, [(own, 0, 0, 0), (alien, 0, 0.2, 0)])
            asm_path = folder / "ПРТИ.301111.020 СБ Узел.sldasm"
            self.s.save_as(asm, asm_path)
            self.s.close_all()
        finally:
            self.s.set_settings(AutoStockMaterial=1)

        asm = self.s.open(asm_path)
        self.s.activate(asm)
        status = str(com.call(self.s.eskd(), "SyncProductSilent") or "")
        self.assertIn("деталей 1", status, status)
        self.s.close_all()

        self.assertIsNone(V(self.persisted(alien), "Материал_Строка", "00"), "чужой файл не тронут")


if __name__ == "__main__":
    unittest.main()
