# -*- coding: utf-8 -*-
"""E2E, группа T — материал по геометрии: типоразмер профиля и толщина листа против библиотеки ЕСКД (Р-8).

Проверяется правило владельца от 20.09.2026 целиком, на настоящих деталях в настоящем SolidWorks:

    материала нет, подходящий один      -> подставить молча;
    материала нет, подходящих несколько -> спросить (в прогоне окно выключено, вердикт читается из надстройки);
    материала нет, подходящих нет       -> замечание, ничего не менять;
    материал выбран и совпадает         -> ничего;
    материал выбран и не совпадает      -> уведомление, выбор конструктора не переписывать.
"""
import shutil
import time
import unittest

from eskd_e2e import build, com, oracles, paths
from eskd_e2e.testing import SwTestCase, tags

V = oracles.value

PROFILES = paths.ROOT / "04_Библиотеки_Материалов_и_Профилей" / "Профили сварных деталей" / "Сортамент ГОСТ"
FLAT_OVAL = PROFILES / "Труба плоскоовальная ГОСТ 8644-68" / "30х15х1,5.SLDLFP"
RECTANGLE = PROFILES / "Прямоугольная труба ГОСТ 8645-68" / "30х15х1,5.sldlfp"
SQUARE_40 = PROFILES / "Труба квадратная ГОСТ 8639-82" / "40х40х2.sldlfp"
SQUARE_40_MATERIAL = "Труба 40х40х2,0 ГОСТ 8639-82 / Ст3сп ГОСТ 13663-86"

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

    def _tube(self, name, profile=FLAT_OVAL, material=None, verdicts=None):
        doc = build.structural_tube(self.s, 400, profile, material)
        return self._save(doc, name, verdicts), doc

    def _sheet(self, name, thickness_mm, material=None, verdicts=None):
        doc = build.sheet_metal_plate(self.s, 200, 100, thickness_mm, material)
        return self._save(doc, name, verdicts), doc

    def _save(self, doc, name, verdicts):
        """Сохраняет новую деталь под именем; вердикт проверяется ДО сохранения: после него надстройка в простое
        уже назначает материал, и «Assign» превращается в «Ok» (гонка T02, 21.09.2026)."""
        if verdicts == ["Assign"]:
            self.assertEqual(("", ""), build.material_of(doc, ""), "исходно материала нет")
        if verdicts is not None:
            self.assertEqual(verdicts, self._verdicts(), "вердикт до сохранения")
        path = self.s.ws(self._case_name(), name)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.s.save_as(doc, path)
        return path

    # ------------------------------------------------------------------ подстановка молча
    @tags("smoke")
    def test_T01_flat_oval_tube_without_material_gets_it_from_library(self):
        """T01: случай владельца — плоскоовальная труба 30х15х1,5 без материала. В библиотеке она одна,
        значит спрашивать нечего: материал подставляется сам и доходит до свойств."""
        path, doc = self._tube("ПРТИ.301111.001 Распорка.sldprt", verdicts=["Assign"])

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
        path, doc = self._sheet("ПРТИ.301111.002 Косынка.sldprt", 8, verdicts=["Assign"])

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
        warnings = str(com.call(self.s.eskd(), "LastSyncWarnings") or "")
        self.assertIn("«Синхронизировать»", warnings, f"подсказка, где выбрать: {warnings}")

        self.assertEqual(0, int(com.call(self.s.eskd(), "ApplyStockMaterialSilent")),
                         "без выбора назначать нечего")
        status = str(com.call(self.s.eskd(), "ReviewActivePartSilent") or "")
        self.assertNotIn("материалов назначено 1", status, f"и «Синхронизировать» без ответа не выбирает: {status}")
        self.assertEqual(("", ""), build.material_of(doc, ""), "материал по-прежнему не выбран")

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
    def test_T05_wrong_material_waits_for_designer_answer(self):
        """T05: конструктор выбрал материал, потом сменил профиль; подходящий материал один. Ctrl+S его материал не
        меняет и ничего не спрашивает — в строке состояния подсказка, где ответить (решение владельца 23.09.2026, З-25 и
        З-27; до этого, по решению 22.09.2026, сохранение меняло материал само). «Синхронизировать» в детали — окно;
        без окна (ответ как без конструктора) единственный подходящий заменяет неподходящий, и деталь сохраняется."""
        doc = build.structural_tube(self.s, 400, FLAT_OVAL, SHEET8)
        rows = self._report()
        self.assertEqual(1, len(rows), rows)
        self.assertEqual("Replace", rows[0][1], f"расхождение материала и профиля: {rows[0]}")
        self.assertEqual(SHEET8, rows[0][4], "виден материал, который стоит сейчас")
        path = self._save(doc, "ПРТИ.301111.005 Стойка.sldprt", None)
        self.s.wait_addin_idle(timeout=60.0)
        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        name, _ = build.material_of(doc, "")
        self.assertEqual(SHEET8, name, "сохранение материал конструктора не меняет")
        warnings = str(com.call(self.s.eskd(), "LastSyncWarnings") or "")
        self.assertIn("не соответствует геометрии", warnings, warnings)
        self.assertIn("«Синхронизировать»", warnings, "подсказка, где ответить")
        self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "деталь сохранена и не изменена после сохранения")

        status = str(com.call(self.s.eskd(), "ReviewActivePartSilent") or "")
        self.path("outcome.txt").write_text(status + "\n" + warnings, encoding="utf-8")
        self.assertIn("материалов назначено 1", status, status)
        self.assertIn("сохранено 1", status, status)
        name, _ = build.material_of(doc, "")
        self.assertEqual(TUBE_MATERIAL, name, "материал заменён на подходящий профилю")
        self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "«Синхронизировать» сохранила деталь сама")
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertEqual(TUBE_LINE, V(disk, "Материал_Строка", "00") or V(disk, "Материал_Строка"), "в файле — новый материал")

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

    def test_T09_product_walk_saves_only_the_parts_it_changed(self):
        """T09: изделие целиком — это и уже согласованные детали. Обход переписывает только те файлы,
        в которых что-то изменил: иначе у всего заказа меняется дата, и потом не понять, что правили."""
        folder = self.s.ws(self._case_name())
        folder.mkdir(parents=True, exist_ok=True)
        ready = folder / "ПРТИ.301111.031 Готовая.sldprt"
        empty = folder / "ПРТИ.301111.032 Без материала.sldprt"

        # «Готовая» приходит с материалом и заполненными свойствами — обходу в ней делать нечего.
        doc = build.structural_tube(self.s, 400, FLAT_OVAL, TUBE_MATERIAL)
        self.s.save_as(doc, ready)
        self.s.wait_addin_idle(timeout=60.0)
        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc = build.structural_tube(self.s, 250, FLAT_OVAL, None)
            self.s.save_as(doc, empty)
            asm, _ = build.assembly(self.s, [(ready, 0, 0, 0), (empty, 0, 0.2, 0)])
            asm_path = folder / "ПРТИ.301111.030 СБ Ферма.sldasm"
            self.s.save_as(asm, asm_path)
            self.s.close_all()
        finally:
            self.s.set_settings(AutoStockMaterial=1)

        before = ready.stat().st_mtime_ns

        asm = self.s.open(asm_path)
        self.s.activate(asm)
        status = str(com.call(self.s.eskd(), "SyncProductSilent") or "")
        self.s.close_all()

        self.assertIn("деталей 2", status, status)
        self.assertIn("сохранено 1", status, f"сохранена только изменённая деталь: {status}")
        self.assertEqual(before, ready.stat().st_mtime_ns, "готовая деталь не переписана")
        self.assertEqual(TUBE_LINE, V(self.persisted(empty), "Материал_Строка", "00"),
                         "деталь без материала обходом заполнена")


    def test_T12_check_saves_changed_parts_but_not_designer_edits(self):
        """T12 (решение владельца 23.09.2026, З-27): «Проверить изделие» — один порядок работы. Без окна (ответы как без
        конструктора) проверка назначает материал деталям, которые отдельно не сохраняли, и сохраняет их. Деталь с
        несохранёнными правками конструктора материал получает, но не сохраняется: вместе с ним ушли бы и его правки."""
        folder = self.s.ws(self._case_name())
        folder.mkdir(parents=True, exist_ok=True)
        tube = folder / "ПРТИ.301111.061 Лежак.sldprt"
        edited = folder / "ПРТИ.301111.062 Стойка.sldprt"
        self.s.set_settings(AutoStockMaterial=0)
        try:
            for path in (tube, edited):
                doc = build.structural_tube(self.s, 300, FLAT_OVAL, None)
                self.s.save_as(doc, path)
            asm, _ = build.assembly(self.s, [(tube, 0, 0, 0), (edited, 0, 0.2, 0)])
            asm_path = folder / "ПРТИ.301111.060 СБ Опора.sldasm"
            self.s.save_as(asm, asm_path)
            self.s.close_all()
        finally:
            self.s.set_settings(AutoStockMaterial=1)
        before = edited.read_bytes()

        asm = self.s.open(asm_path)
        part = self.s.sw.GetOpenDocumentByName(str(edited))
        self.assertIsNotNone(part, "«Стойка» загружена сборкой")
        # Несохранённая правка конструктора: любая правка помечает документ изменённым — так же поступаем и здесь.
        com.dyn(part).SetSaveFlag()
        self.assertTrue(bool(com.dyn(part).GetSaveFlag), "у «Стойки» несохранённая правка")
        self.s.activate(asm)
        com.call(self.s.eskd(), "CheckProductApplySilent")
        status = str(com.call(self.s.eskd(), "CheckStatus"))
        applied = str(com.call(self.s.eskd(), "CheckApplied"))
        self.path("outcome.txt").write_text(status + "\n" + applied + "\n" + str(com.call(self.s.eskd(), "LastNotices")),
                                            encoding="utf-8")
        self.assertTrue(status.startswith("ok|"), status)
        self.assertIn("материалов назначено 2", applied, applied)
        self.assertIn("не сохранено (сохраните сами) 1", applied, applied)
        self.assertTrue(bool(com.dyn(part).GetSaveFlag), "«Стойка» осталась несохранённой — её сохраняет конструктор")
        self.s.close_all()

        self.assertEqual(TUBE_LINE, V(self.persisted(tube), "Материал_Строка", "00"), "«Лежак» заполнен и сохранён")
        self.assertEqual(before, edited.read_bytes(), "файл «Стойки» не тронут")

    def test_T11_plain_solid_is_left_alone(self):
        """T11: решение владельца 21.09.2026 — по геометрии не гадать. Плоская деталь, сделанная вытяжкой,
        а не листовым металлом, может быть и пластиком, и фанерой: толщина тела о прокате не говорит ничего.
        Такой детали надстройка не подбирает материал и ничего о нём не сообщает."""
        path = self.s.ws(self._case_name(), "ПРТИ.301111.051 Пластина точёная.sldprt")
        path.parent.mkdir(parents=True, exist_ok=True)

        doc, _ = build.plate(self.s, 150, 80, 6, None)   # 6 мм: в библиотеке такой лист есть — соблазн подобрать
        self.s.save_as(doc, path)
        self.s.wait_addin_idle(timeout=60.0)

        self.assertEqual([], self._report(), "по обычному телу вердиктов нет")
        self.assertEqual(("", ""), build.material_of(doc, ""), "материал за конструктора не выбран")
        self.s.close_all()
        self.assertIsNone(V(self.persisted(path), "Материал_Строка", "00"), "и в свойства ничего не записано")

    def _body_materials(self, doc, cfg):
        names = []
        for body in com.as_list(com.dyn(doc).GetBodies2(0, False)):
            db = com.ref_str("")
            got = com.dyn(body).GetMaterialPropertyName(cfg, db)
            # Позднее связывание отдаёт выходной параметр вместе с результатом: (имя, база).
            names.append(str((got[0] if isinstance(got, tuple) else got) or ""))
        return sorted(names)

    def test_T13_material_goes_to_every_execution_with_the_same_profile(self):
        """T13 (аудит 23.09.2026, MAT-5): материал SolidWorks у каждой конфигурации свой. Назначенный по типоразмеру
        только в активной, в других исполнениях он оставался прежним — у них «Материал_Строка», масса и книга ЛЗК
        расходились. Теперь в исполнении с тем же профилем и без материала он ставится сам — так его поставила бы и сама
        система в этом исполнении; исполнение, где конструктор выбрал своё, не трогается (в исполнениях бывают разные
        материалы — решение владельца 23.09.2026), активное исполнение остаётся прежним."""
        path = self.s.ws(self._case_name(), "ПРТИ.301111.061 Распорка.sldprt")
        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc = build.structural_tube(self.s, 400, FLAT_OVAL, None)
            first = str(doc.GetActiveConfiguration.Name)
            build.add_configuration(doc, "01")
            build.add_configuration(doc, "02")
            build.set_material(doc, SHEET8, "02")
            build.show_configuration(doc, first)
            self.s.save_as(doc, path)
            # Пересохранение после «Сохранить как» идёт в простое: до него подбор должен оставаться выключенным.
            self.s.wait_addin_idle(timeout=60.0)
        finally:
            self.s.set_settings(AutoStockMaterial=1)
        self.assertEqual(SHEET8, build.material_of(doc, "02")[0], "в «02» — свой материал конструктора")
        self.assertEqual("", build.material_of(doc, "01")[0], "в «01» материала нет")

        self.s.activate(doc)
        changed = int(com.call(self.s.eskd(), "ApplyStockMaterialSilent"))
        self.assertEqual(2, changed, "материал назначен в активном исполнении и в «01»")
        self.assertEqual(TUBE_MATERIAL, build.material_of(doc, first)[0], "активное исполнение")
        self.assertEqual(TUBE_MATERIAL, build.material_of(doc, "01")[0], "исполнение «01» получило тот же материал")
        self.assertEqual(SHEET8, build.material_of(doc, "02")[0], "выбор конструктора в «02» не тронут")
        self.assertEqual(first, str(doc.GetActiveConfiguration.Name), "активное исполнение прежнее")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")
        self.s.close_all()

    def test_T15_save_assigns_material_in_executions_without_it(self):
        """T15 (аудит 23.09.2026, MAT-5, решение владельца 23.09.2026): путь конструктора — Ctrl+S. Материала нет, подходящий
        один — надстройка ставит его в активном исполнении и в исполнении «01» с тем же профилем и без материала; в «02»
        конструктор выбрал своё — не трогается. Деталь не сохраняется, активным остаётся прежнее исполнение."""
        path = self.s.ws(self._case_name(), "ПРТИ.301111.063 Распорка.sldprt")
        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc = build.structural_tube(self.s, 400, FLAT_OVAL, None)
            first = str(doc.GetActiveConfiguration.Name)
            build.add_configuration(doc, "01")
            build.add_configuration(doc, "02")
            build.set_material(doc, SHEET8, "02")
            build.show_configuration(doc, first)
            self.s.save_as(doc, path)
            self.s.wait_addin_idle(timeout=60.0)
        finally:
            self.s.set_settings(AutoStockMaterial=1)

        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        warnings = [ln for ln in self.addin_log.new_lines() if "WARN" in ln or "ERROR" in ln]
        self.path("warnings.txt").write_text("\n".join(warnings), encoding="utf-8")
        self.assertEqual(TUBE_MATERIAL, build.material_of(doc, first)[0], "активное исполнение")
        self.assertEqual(TUBE_MATERIAL, build.material_of(doc, "01")[0], "исполнение «01» без материала получило его")
        self.assertEqual(SHEET8, build.material_of(doc, "02")[0], "выбор конструктора в «02» не тронут")
        self.assertEqual(first, str(doc.GetActiveConfiguration.Name), "активное исполнение прежнее")
        self.assertEqual([], [ln for ln in warnings if "не открылось" in ln or "не вернулось" in ln], "исполнения переключились")
        self.s.close_all()

    def test_T14_profile_material_goes_only_to_its_bodies(self):
        """T14 (аудит 23.09.2026, MAT-6): в детали труба и приваренная к ней пластина (второе тело, не прокат). Позиция
        проката одна, и раньше материал трубы ставился детали целиком — пластина становилась трубой. Теперь детали целиком
        он не ставится, у пластины материала по-прежнему нет. Список вырезов ещё не построен, а тела профиля — не вся
        деталь: какому телу какой материал, не понять — позиции нет, в журнале «обновите список вырезов» (ревью
        23.09.2026). Если позиция всё же найдена, телу по отдельности SolidWorks 2025 материал через API не ставит
        (отвечает «готово», а материал и масса прежние): тогда назначенным это не считается, а в журнале — «назначьте его
        этим телам вручную»."""
        path = self.s.ws(self._case_name(), "ПРТИ.301111.062 Кронштейн.sldprt")
        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc = build.structural_tube(self.s, 400, FLAT_OVAL, None)
            build.sketch_rectangles(doc, [(0.0, 0.05, 0.1, 0.1)])
            build.extrude(doc, 0.006, merge=False)
            doc.ForceRebuild3(False)
            self.assertEqual(2, len(com.as_list(com.dyn(doc).GetBodies2(0, False))), "два тела: труба и пластина")
            self.s.save_as(doc, path)
            self.s.wait_addin_idle(timeout=60.0)
        finally:
            self.s.set_settings(AutoStockMaterial=1)
        cfg = str(doc.GetActiveConfiguration.Name)

        self.s.activate(doc)
        self.addin_log.new_lines()
        changed = int(com.call(self.s.eskd(), "ApplyStockMaterialSilent"))
        log = self.addin_log.new_lines()
        self.path("log.txt").write_text("\n".join(log), encoding="utf-8")
        self.assertEqual("", build.material_of(doc, cfg)[0], "детали целиком материал трубы не назначен")
        bodies = self._body_materials(doc, cfg)
        self.assertEqual("", bodies[0], f"у пластины материала трубы нет: {bodies}")
        if bodies[1] == TUBE_MATERIAL:
            self.assertEqual(1, changed, "материал встал телу трубы")
        else:
            self.assertEqual(["", ""], bodies, bodies)
            self.assertEqual(0, changed, "не вставший материал назначенным не считается")
            self.assertTrue(any("назначьте его этим телам вручную" in ln or "обновите список вырезов" in ln for ln in log),
                            "\n".join(log))
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")
        self.s.close_all()

    def test_T16_hidden_folder_of_other_execution_is_ignored(self):
        """T16 (сверка SW API 23.09.2026, №32): «Укосина» — в «00» плоскоовальная труба 30х15х1,5, в «01» квадратная
        40х40х2; чужой элемент в каждом исполнении погашен. Его папка в списке вырезов остаётся, но без тел. Раньше такая
        папка считалась позицией «вся деталь»: в отчёте две позиции, и материал квадратной трубы ставился детали «00» —
        и через «ту же позицию» в «01». Теперь позиция одна — своя; «01» получает свой материал, когда станет активным."""
        path = self.s.ws(self._case_name(), "ПРТИ.301111.064 Укосина.sldprt")
        path.parent.mkdir(parents=True, exist_ok=True)
        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc, (base, other) = build.structural_tube_executions(
                self.s, [(400, 0, (0, 0), FLAT_OVAL), (300, 90, (-100, 0), SQUARE_40)], FLAT_OVAL, None, cut_list=True)
            self.s.save_as(doc, path)
            self.s.wait_addin_idle(timeout=60.0)
        finally:
            self.s.set_settings(AutoStockMaterial=1)
        folders = build.cut_list_folders(doc)
        self.path("folders.txt").write_text(repr(folders), encoding="utf-8")
        if not any(count == 0 for _, count in folders):
            self.skipTest(f"SolidWorks не держит папку погашенного элемента — состояние не воспроизводится: {folders}")

        self.s.activate(doc)
        rows = self._report()
        self.assertEqual(1, len(rows), rows)
        self.assertIn("8644", rows[0][3], f"позиция — своя плоскоовальная труба: {rows}")
        changed = int(com.call(self.s.eskd(), "ApplyStockMaterialSilent"))
        self.assertGreaterEqual(changed, 1, "материал назначен")
        self.assertEqual(TUBE_MATERIAL, build.material_of(doc, base)[0], "в «00» — материал своей трубы")
        self.assertEqual("", build.material_of(doc, other)[0], "в «01» другой профиль — не тронут")
        self.assertEqual(base, str(doc.GetActiveConfiguration.Name), "активное исполнение прежнее")
        self.assertEqual(["Ok"], self._verdicts(), "вопрос больше не появляется")

        build.show_configuration(doc, other)
        com.call(self.s.eskd(), "ApplyStockMaterialSilent")
        self.assertEqual(SQUARE_40_MATERIAL, build.material_of(doc, other)[0], "«01» получил материал своей трубы")
        self.assertEqual(TUBE_MATERIAL, build.material_of(doc, base)[0], "у «00» материал прежний")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")
        self.s.close_all()

    def test_T17_frame_of_one_tube_in_two_lengths_gets_part_material(self):
        """T17 (сверка SW API 23.09.2026, №41): рама из одной плоскоовальной трубы двух длин — в списке вырезов две папки,
        каждая покрывает часть тел. Раньше материал ставился телам каждой папки, а телу SolidWorks 2025 материал через API
        не ставит (T14): рама оставалась без материала, в журнале — «назначьте вручную». Позиции одного материала вместе
        покрывают все тела — это вся деталь: материал ставится детали целиком."""
        path = self.s.ws(self._case_name(), "ПРТИ.301111.065 Рама.sldprt")
        path.parent.mkdir(parents=True, exist_ok=True)
        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc = build.structural_tube(self.s, 400, FLAT_OVAL, None, weldment=True)
            build.add_structural_member(doc, 250, FLAT_OVAL, 90, (-100, 0))
            doc.ForceRebuild3(False)
            build.update_cut_list(doc)
            self.s.save_as(doc, path)
            self.s.wait_addin_idle(timeout=60.0)
        finally:
            self.s.set_settings(AutoStockMaterial=1)
        cfg = str(doc.GetActiveConfiguration.Name)
        folders = build.cut_list_folders(doc)
        self.path("folders.txt").write_text(repr(folders), encoding="utf-8")
        self.assertEqual([1, 1], sorted(count for _, count in folders), f"две папки по телу: {folders}")
        self.assertEqual(["Assign", "Assign"], self._verdicts(), "обе длины — без материала, подходящий один")

        self.s.activate(doc)
        changed = int(com.call(self.s.eskd(), "ApplyStockMaterialSilent"))
        self.assertGreaterEqual(changed, 1, "материал назначен")
        self.assertEqual(TUBE_MATERIAL, build.material_of(doc, cfg)[0], "материал трубы — детали целиком")
        self.assertEqual(["Ok", "Ok"], self._verdicts(), "обе длины получили материал")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")
        self.s.close_all()

    def test_T18_flat_pattern_configuration_passes_material_to_its_parent(self):
        """T18 (сверка SW API 23.09.2026, №40): у листа 8 мм материал трубы — не подходит, подходящий один (замена). Активна
        производная развёртки «…SM-FLAT-PATTERN». Раньше материал ставился в неё саму, а исполнение-родитель оставалось с
        материалом трубы — его графа 3 и масса не менялись. Развёртка — не исполнение: материал ставится родителю,
        активной остаётся развёртка. Развёртка, созданная при материале трубы, держит свою копию материала, и SolidWorks
        за родителем её не ведёт (проба 23.09.2026): копия прежнего материала родителя получает новый вместе с ним."""
        path = self.s.ws(self._case_name(), "ПРТИ.301111.066 Лист.sldprt")
        path.parent.mkdir(parents=True, exist_ok=True)
        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc = build.sheet_metal_plate(self.s, 200, 100, 8, TUBE_MATERIAL)
            first = str(doc.GetActiveConfiguration.Name)
            flat = first + "SM-FLAT-PATTERN"
            build.add_derived_configuration(doc, flat, first)
            build.show_configuration(doc, flat)
            doc.ForceRebuild3(False)
            self.s.save_as(doc, path)
            self.s.wait_addin_idle(timeout=60.0)
        finally:
            self.s.set_settings(AutoStockMaterial=1)
        self.assertEqual(flat, str(doc.GetActiveConfiguration.Name), "активна развёртка")
        self.assertEqual(TUBE_MATERIAL, build.material_of(doc, first)[0], "у исполнения — материал трубы")

        self.s.activate(doc)
        changed = int(com.call(self.s.eskd(), "ApplyStockMaterialSilent"))
        self.assertGreaterEqual(changed, 1, "материал заменён")
        self.assertEqual(SHEET8, build.material_of(doc, first)[0], "материал получило исполнение-родитель")
        self.assertEqual(SHEET8, build.material_of(doc, flat)[0], "копия материала трубы в развёртке заменена вместе с родителем")
        self.assertEqual(flat, str(doc.GetActiveConfiguration.Name), "активной осталась развёртка")
        build.show_configuration(doc, first)
        self.assertEqual(["Ok"], self._verdicts(), "в исполнении вопрос больше не появляется")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")
        self.s.close_all()

    def test_T19_multibody_sheet_of_different_thickness_is_not_guessed(self):
        """T19 (сверка SW API 23.09.2026, №39): листовая деталь из двух тел 3 и 8 мм. Раньше толщина первого «Листового
        металла» шла всей детали — молча «Лист 3» и телу 8 мм. Теперь материал по толщине не подбирается, конструктору —
        замечание «разной толщины». Два тела одной толщины (8 и 8) — как раньше, вся деталь получает «Лист 8»."""
        doc = build.sheet_metal_two_bodies(self.s, 3, 8, None)
        bodies = com.as_list(com.dyn(doc).GetBodies2(0, False))
        self.assertEqual(2, len(bodies), "два тела")
        self.assertTrue(all(bool(com.dyn(b).IsSheetMetal()) for b in bodies), "оба листовые")
        self.assertEqual(["Unclear"], self._verdicts(), "вердикт — неясно")
        path = self._save(doc, "ПРТИ.301111.067 Лист двойной.sldprt", None)
        self.s.wait_addin_idle(timeout=60.0)
        self.assertEqual(("", ""), build.material_of(doc, ""), "материал не назначен")
        warnings = str(com.call(self.s.eskd(), "LastSyncWarnings"))
        self.assertIn("разной толщины", warnings, warnings)
        self.s.close(doc)

        doc = build.sheet_metal_two_bodies(self.s, 8, 8, None)
        self._save(doc, "ПРТИ.301111.068 Лист двойной 8.sldprt", ["Assign"])
        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        self.assertEqual(SHEET8, build.material_of(doc, "")[0], "одна толщина — вся деталь, лист 8")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")
        self.s.close_all()

    def test_T10_product_walk_reaches_parts_three_levels_deep(self):
        """T10: изделие — это сборка сборок. Деталь лежит на третьем уровне дерева, а нажимают кнопку
        в корневой сборке: обход обязан дойти и до неё."""
        folder = self.s.ws(self._case_name())
        folder.mkdir(parents=True, exist_ok=True)
        deep = folder / "ПРТИ.301111.043 Глубокая.sldprt"

        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc = build.structural_tube(self.s, 300, FLAT_OVAL, None)
            self.s.save_as(doc, deep)

            level3, _ = build.assembly(self.s, [(deep, 0, 0, 0)])
            path3 = folder / "ПРТИ.301111.042 СБ Узел.sldasm"
            self.s.save_as(level3, path3)

            level2, _ = build.assembly(self.s, [(path3, 0, 0, 0)])
            path2 = folder / "ПРТИ.301111.041 СБ Секция.sldasm"
            self.s.save_as(level2, path2)

            top, _ = build.assembly(self.s, [(path2, 0, 0, 0)])
            path1 = folder / "ПРТИ.301111.040 СБ Изделие.sldasm"
            self.s.save_as(top, path1)
            self.s.close_all()
            self.assertIsNone(V(self.persisted(deep), "Материал_Строка", "00"), "материала ещё нет")
        finally:
            self.s.set_settings(AutoStockMaterial=1)

        asm = self.s.open(path1)
        self.s.activate(asm)
        status = str(com.call(self.s.eskd(), "SyncProductSilent") or "")
        self.s.close_all()

        self.assertIn("деталей 1", status, status)
        self.assertIn("материалов назначено 1", status, f"деталь третьего уровня обойдена: {status}")
        self.assertEqual(TUBE_LINE, V(self.persisted(deep), "Материал_Строка", "00"),
                         "материал записан детали на третьем уровне дерева")


    def _check_product(self):
        """«Проверить изделие» без окна; статус «ok|…» или «error|…» — когда проверка закончилась."""
        com.call(self.s.eskd(), "CheckProductSilent")
        deadline = time.time() + 600
        status = ""
        while time.time() < deadline:
            status = str(com.call(self.s.eskd(), "CheckStatus") or "")
            if status:
                break
            time.sleep(1)
        return status

    def test_T20_execution_in_product_is_checked_and_active_outside_is_not_asked(self):
        """T20 (№36; решение владельца 24.09.2026): «Укосина» — в «00» плоскоовальная труба 30х15х1,5, в «01» квадратная
        40х40х2. Файл сохранён активным в «00», а в изделии стоит только «01». У «01» материал плоскоовальной трубы — не
        по профилю: проверка изделия пишет замечание «исполнение «01» … сделайте «01» активным и нажмите
        «Синхронизировать»». У «00» материал листа — тоже не по профилю, но «00» в изделии нет: про него ни вопроса, ни
        замечания. Исполнения не переключаются, материалы не меняются, деталь не помечается изменённой. Раньше сверялось
        только активное «00»: вопрос про лист, а про «01» — ни слова."""
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.301111.110/01_3D"
        if (self.s.run_dir / short).exists():
            shutil.rmtree(self.s.run_dir / short, ignore_errors=True)
        models.mkdir(parents=True)
        part = models / "ПРТИ.301111.111 Укосина.sldprt"
        asm_path = models / "ПРТИ.301111.110 СБ Рама.sldasm"
        self.s.set_settings(AutoStockMaterial=0)
        try:
            doc, (base, other) = build.structural_tube_executions(
                self.s, [(400, 0, (0, 0), FLAT_OVAL), (300, 90, (-100, 0), SQUARE_40)], FLAT_OVAL, TUBE_MATERIAL)
            build.set_material(doc, SHEET8, base)
            build.set_material(doc, TUBE_MATERIAL, other)
            self.assertEqual(SHEET8, build.material_of(doc, base)[0], "у «00» — лист")
            self.assertEqual(TUBE_MATERIAL, build.material_of(doc, other)[0], "у «01» — плоскоовальная труба")
            self.assertEqual(base, str(doc.GetActiveConfiguration.Name), "активно «00»")
            self.s.save_as(doc, part)
            self.s.wait_addin_idle(timeout=60.0)
            self.s.close_all()
            asm, _ = build.assembly(self.s, [(part, 0, 0, 0)])
            comp = com.as_list(asm.GetComponents(True))[0]
            com.dyn(comp).ReferencedConfiguration = other
            asm.ForceRebuild3(False)
            self.s.save_as(asm, asm_path)
            self.s.close_all()
        finally:
            self.s.set_settings(AutoStockMaterial=1)

        asm = self.s.open(asm_path)
        self.s.activate(asm)
        part_doc = com.call(self.s.sw, "GetOpenDocumentByName", str(part))
        self.assertIsNotNone(part_doc, "деталь открыта со сборкой")
        pd = com.dyn(part_doc)
        # Сборку, где деталь стоит не в активном исполнении файла, SolidWorks при открытии перестраивает и помечает
        # изменённой — это не проверка (как в K08): такие документы сохраняются до неё.
        for opened in (asm, part_doc):
            if bool(com.dyn(opened).GetSaveFlag):
                self.s.save(com.dyn(opened))
                self.s.wait_addin_idle(timeout=60.0)
            self.assertFalse(bool(com.dyn(opened).GetSaveFlag), "документ без изменений перед проверкой")
        self.assertEqual(base, str(pd.GetActiveConfiguration.Name), "в файле активно «00»")

        status = self._check_product()
        self.assertTrue(status.startswith("ok|"), status)
        text = (models.parent / "_Проверка.txt").read_text(encoding="utf-8-sig")
        self.path("check.txt").write_text(text, encoding="utf-8")
        lines = [line for line in text.splitlines() if "ПРТИ.301111.111" in line]
        self.assertTrue(any("исполнение «01»" in line and TUBE_MATERIAL in line and "40х40х2" in line and "«01» активным" in line
                            for line in lines), f"замечание по «01», стоящему в изделии:\n{text}")
        self.assertFalse(any("30х15х1,5" in line and SHEET8 in line for line in lines),
                         f"про «00», которого в изделии нет, — ни вопроса, ни замечания:\n{text}")
        self.assertFalse(bool(pd.GetSaveFlag), "деталь не помечена изменённой")
        self.assertEqual(base, str(pd.GetActiveConfiguration.Name), "исполнение не переключено")
        self.assertEqual(SHEET8, build.material_of(pd, base)[0], "материал «00» не тронут")
        self.assertEqual(TUBE_MATERIAL, build.material_of(pd, other)[0], "материал «01» не тронут")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")
        self.s.close_all()


if __name__ == "__main__":
    unittest.main()
