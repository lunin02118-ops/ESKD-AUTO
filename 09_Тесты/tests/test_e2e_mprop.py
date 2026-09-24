# -*- coding: utf-8 -*-
"""E2E, группа M (продолжение) — совместимость с MProp: критерии К-1 и К-2 плана согласования с SWPlus.

К-1: после сохранения с надстройкой «Применить без правок» MProp (так же его запускает «Перезагрузка форматки» DProp)
не меняет ни одного значения: свойства по уровням, «Сводка → Автор», единицы массы документа; добавляться могут только
служебные имена MProp; окон-вопросов нет. К-2: следующее сохранение ничего не пишет.
Расхождения каждого случая сохраняются в каталоге теста (mprop_differences.json) — это рабочий список шага 3.
"""
import json
import unittest
from pathlib import Path

from eskd_e2e import build, com, mprop, oracles, paths
from eskd_e2e.testing import SwTestCase, known_defect

A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A03 = "ПРТИ.468211.103 Планка.sldprt"
A04 = "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt"
A06 = "ПРТИ.468211.104 Кронштейн направляющий удлинённый.sldprt"
A08 = "ПРТИ.468211.110 СБ Узел опоры.sldasm"
A15 = "ПРТИ.468211.111 Стойка трубная.sldprt"
A17 = "ПРТИ.468211.113 Прокладка.sldprt"
A18 = "ПРТИ.468211.114 Планка регулировочная.sldprt"
A20 = "ПРТИ.468211.116-01 Упор.sldprt"


# Реквизиты, которые пишут MProp и надстройка; «Формат» приходит из чертежа (З-1), служебные свойства фикстур не
# восстанавливаются — их после «Удалить все свойства» нет ни у кого.
REQUISITES = ("Обозначение", "Наименование", "Наименование_ФБ", "Конструктор", "Контора", "Раздел", "Материал_ФБ",
              "Материал_Таблица", "Материал_Строка", "Масса_ФБ", "Масса_Таблица", "Сборка1_ФБ", "Сборка2_ФБ")


class MPropCompatibility(SwTestCase):

    def _mprop_round_trip(self, doc):
        """Сохранение с надстройкой → снимок → MProp «Применить» → снимок; возвращает расхождения и пишет их в файл."""
        self.s.save(doc)
        before = mprop.snapshot(doc)
        run = mprop.apply_without_edits(self.s, doc)
        after = mprop.snapshot(doc)
        dialogs = self.s.watchdog.pop_unexpected()
        found = mprop.differences(before, after)
        if dialogs:
            found.append(f"окна MProp: {dialogs}")
        if not run.get("ok"):
            found.append(f"MProp не выполнен: {run}")
        record = self.path("mprop_differences.json")
        record.write_text(json.dumps({"run": run, "differences": found, "before": before, "after": after},
                                     ensure_ascii=False, indent=1), encoding="utf-8")
        return found

    def _assert_k1(self, name, *extra):
        path, doc = self.open_copy(name, *extra)
        found = self._mprop_round_trip(doc)
        self.assertEqual([], found, f"MProp «Применить без правок» изменил документ {name}")

    @known_defect("Д-68")
    def test_M04_mprop_apply_changes_nothing_plate(self):
        """M04 (К-1): пластина A-01 — масса, наименование, подписи, материал после MProp те же (Н-02, Н-03, Н-04, Н-05)."""
        self._assert_k1(A01)

    @known_defect("Д-68")
    def test_M04_mprop_apply_changes_nothing_executions(self):
        """M04 (К-1): планка A-03 с исполнениями 00/01/02 — обозначения исполнений и «Исполнение» те же (Н-06)."""
        self._assert_k1(A03)

    @known_defect("Д-49")
    def test_M04_mprop_apply_changes_nothing_long_title(self):
        """M04 (К-1): A-06 «Кронштейн направляющий удлинённый» — перенос наименования графы 1 как у MProp (Н-03)."""
        self._assert_k1(A06)

    @known_defect("Д-78")
    def test_M04_mprop_apply_changes_nothing_light_part(self):
        """M04 (К-1): прокладка A-17 (18,8 г) — масса в граммах « г» и единицы документа как у MProp (Н-32, Р-7)."""
        self._assert_k1(A17)

    @known_defect("Д-68")
    def test_M04_mprop_apply_changes_nothing_mass_threshold(self):
        """M04 (К-1): A-18 — исполнения 50,2 г и 175,8 г по обе стороны порога 100 г; единица одна на документ (И-20)."""
        self._assert_k1(A18)

    @known_defect("Д-51")
    def test_M04_mprop_apply_changes_nothing_assembly(self):
        """M04 (К-1): сборка A-08 «… СБ …» — «Сборка1_ФБ», «Сборка2_ФБ», обозначение, вопросы MProp (Н-09, И-21)."""
        self._assert_k1(A08, A01, A04)

    @known_defect("Д-52")
    def test_M04_mprop_apply_changes_nothing_execution_in_file_name(self):
        """M04 (К-1): A-20 «ПРТИ.468211.116-01 Упор» — исполнение в имени файла не удваивается (Н-06)."""
        self._assert_k1(A20)

    @known_defect("Д-54")
    def test_M04_mprop_apply_keeps_bch_record(self):
        """M04 (К-1): A-15 после «Деталь БЧ» — MProp не стирает запись БЧ и «Примечание» (Н-08)."""
        from eskd_e2e import com
        path, doc = self.open_copy(A15)
        self.s.activate(doc)
        self.assertEqual(1, int(com.call(self.s.eskd(), "ToggleDrawinglessSilent")))
        found = self._mprop_round_trip(doc)
        self.assertEqual([], found, "MProp «Применить без правок» изменил деталь БЧ")

    @known_defect("Д-67")
    def test_M04_mprop_apply_changes_nothing_new_part_without_material(self):
        """M04 (К-1): новая деталь из шаблона без материала, сохранённая как «ПРТИ.468211.140 Втулка» (Н-21)."""
        doc = self.s.new_doc(paths.PART_TEMPLATE)
        build.sketch_rectangles(doc, [(-0.01, -0.01, 0.01, 0.01)])
        build.extrude(doc, 0.01)
        ok, err, warn = self.s.save_as(doc, self.path("ПРТИ.468211.140 Втулка.sldprt"))
        self.assertTrue(ok, f"сохранение не удалось: {err} {warn}")
        self.wait_idle(4.0)
        found = self._mprop_round_trip(doc)
        self.assertEqual([], found, "MProp «Применить без правок» изменил новую деталь без материала")

    def test_M04_mprop_apply_changes_nothing_name_without_designation(self):
        """M04 (К-1): деталь «Кронштейн сварной» без обозначения в имени файла — всё имя идёт в наименование,
        обозначение пустое, как в надстройке; MProp ничего не меняет и не спрашивает (WP-3.6, Р-11, Н-30)."""
        path = self.s.workspace_copy(paths.FIXTURES_A / A01, name="Кронштейн сварной.sldprt", subdir=self._case_name())
        doc = self.s.open(path)
        found = self._mprop_round_trip(doc)
        self.assertEqual([], found, "MProp «Применить без правок» изменил деталь без обозначения")
        from eskd_e2e import oracles
        self.assertEqual("Кронштейн сварной", oracles.value(oracles.dump_properties(doc), "Наименование"), "наименование — всё имя файла")

    def test_M04_mprop_from_drawing_with_empty_sheet_view_asks_nothing(self):
        """M04: MProp из чертежа, у первого листа которого пустой или чужой «Вид для свойств» (у чертежей владельца — пустой), берёт
        первый вид без окна «Не удалось определить вид» (WP-3.5, S-8, Н-29)."""
        model = self.copy_fixture(A01)
        doc = self.s.open(model)
        self.s.save(doc)
        self.s.close(doc)
        drawing_path = self.copy_fixture("ПРТИ.468211.101 Пластина опорная.slddrw")
        drawing = self.s.open(drawing_path)
        from eskd_e2e import com
        sheet = com.dyn(drawing.GetCurrentSheet)
        p = list(sheet.GetProperties2)
        # «Вид для свойств» задаётся только параметрами листа (SetupSheet5, аргумент propertyViewName); чертёж помнит путь
        # форматки на момент её загрузки — форматка берётся по имени из каталога поставки (02_Шаблоны_и_Форматки)
        template = paths.SHEET_FORMATS / Path(str(sheet.GetTemplateName)).name
        self.assertTrue(template.exists(), f"форматка поставки {template}")
        self.assertTrue(drawing.SetupSheet5(str(sheet.GetName), int(p[0]), int(p[1]), p[2], p[3], bool(p[4]),
                                            str(template), p[5], p[6], "", True), "параметры листа")
        sheet = com.dyn(drawing.GetCurrentSheet)
        self.assertEqual("", str(sheet.CustomPropertyView), "вид для свойств листа пуст")
        run = mprop.apply_without_edits(self.s, drawing)
        dialogs = self.s.watchdog.pop_unexpected()
        self.assertEqual([], dialogs, "окна MProp")
        self.assertTrue(run.get("ok"), f"MProp не выполнен: {run}")

    def test_M04_dprop_loads_after_edit(self):
        """M04: правленый DProp.swp (без кэша P-code, WP-3.5) загружается и исполняется SolidWorks — служебная процедура
        DProp_top.HWNDActiveWindow без окон (порядок правки макросов, спайк S-9)."""
        from eskd_e2e import com
        macro = mprop.swplus_copy(self.s.run_dir).parent.parent / "DProp" / "DProp.swp"
        err = com.ref_int()
        ok = bool(self.s.sw.RunMacro2(str(macro), "DProp_top", "HWNDActiveWindow", 1, err))
        self.assertEqual([], self.s.watchdog.pop_unexpected(), "окна DProp")
        self.assertTrue(ok, f"DProp не выполнен: err={int(err.value)}")

    def _designer_save(self, doc):
        """Ctrl+S конструктора: сохранение, в котором надстройка наводит единый порядок свойств."""
        doc.SetSaveFlag()
        self.s.run_command(doc, 2)
        self.assertTrue(self.s.wait_addin_idle(timeout=60.0), self.s.last_idle_state)

    @staticmethod
    def _orders(doc):
        """{уровень: [имена в порядке SolidWorks]} — только GetNames, без пересчёта значений."""
        out = {"": com.prop_names(doc.Extension.CustomPropertyManager(""))}
        for cfg in com.as_list(doc.GetConfigurationNames):
            out[str(cfg)] = com.prop_names(doc.Extension.CustomPropertyManager(str(cfg)))
        return out

    def _apply(self, doc, cfg):
        doc.ShowConfiguration2(cfg)
        run = mprop.apply_without_edits(self.s, doc)
        self.assertEqual([], self.s.watchdog.pop_unexpected(), "окна MProp")
        self.assertTrue(run.get("ok") and not run.get("timeout"), f"MProp не выполнен: {run}")

    def _assert_mprop_and_save_keep_order(self, name, executions):
        """«Применить» MProp и сохранение надстройки не переставляют свойства друг за другом: MProp удаляет и дописывает
        в конец «Примечание», «Формат», «Раздел», а в едином порядке они и так последние. Один круг разогрева:
        первое «Применить» дописывает служебные имена MProp, которых у файла ещё нет, и следующее сохранение ставит их
        на место — это один раз."""
        path, doc = self.open_copy(name)
        master, tail = oracles.property_master(), oracles.property_tail()
        self._designer_save(doc)
        for cfg in executions:
            self._apply(doc, cfg)
        self._designer_save(doc)
        stable = self._orders(doc)
        for level, names in stable.items():
            self.assertEqual(oracles.canonical(names, master, tail)[0], names, f"уровень «{level or 'общие'}» в едином порядке")
        for cfg in executions:
            self.assertEqual(tail, stable[cfg][-3:], f"в «{cfg}» последние — «Примечание», «Формат», «Раздел»")
        for cycle in (1, 2):
            for cfg in executions:
                self._apply(doc, cfg)
                self.assertEqual(stable, self._orders(doc), f"круг {cycle}: «Применить» MProp в «{cfg}» порядок не изменил")
            self._designer_save(doc)
            self.assertEqual(stable, self._orders(doc), f"круг {cycle}: сохранение после MProp ничего не переставило")
        self.s.close(doc)
        self.assertEqual(stable, oracles.property_orders(self.persisted(path)), "на диске тот же порядок")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_M23_mprop_and_save_keep_one_order_plate(self):
        """M23 (решение владельца 24.09.2026, «чтобы один раз как было, и она не менялась»): пластина A-01 с одним
        исполнением — «Применить» MProp и Ctrl+S по кругу не переставляют свойства ни в общих, ни в «00». Раньше
        MProp уносил «Примечание», «Формат», «Раздел» в конец, а следующее сохранение возвращало их на строки словаря."""
        self._assert_mprop_and_save_keep_order(A01, ("00",))

    def test_M23_mprop_and_save_keep_one_order_executions(self):
        """M23: планка A-03 с исполнениями 00/01/02 — «Применить» MProp в каждом исполнении и Ctrl+S по кругу порядок
        не меняют ни на одном уровне."""
        self._assert_mprop_and_save_keep_order(A03, ("00", "01", "02"))

    @known_defect("Д-50")
    def test_M19_save_after_mprop_writes_nothing(self):
        """M19 (К-2): после «Применить» MProp сохранение ничего не пишет — надстройка не возвращает свои форматы (Н-04)."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        mprop.apply_without_edits(self.s, doc)
        self.s.watchdog.pop_unexpected()
        mark = self.mark("M19-after-mprop")
        self.s.save(doc)
        self.assertNoPropertyWrites(mark, "сохранение после MProp изменило свойства")

    def test_M20_refill_after_mprop_delete_all_properties(self):
        """M20 (З-3): «Удалить все свойства» MProp (свойства всех уровней и «Сводка») → «Применить» MProp → сохранение
        возвращает реквизиты, как были: обозначение, наименование, фамилия, организация, материал. Списки MProp — как их
        пишет установщик (своя фамилия и организация первыми): сразу после MProp «Разработал» и «Контора» — свои; пустые
        списки не обрывают MProp ошибкой 380."""
        lists = mprop.swplus_copy(self.s.run_dir).parent
        saved = {n: (lists / n).read_bytes() for n in ("MProp_Fam.txt", "MProp_Firm.txt")}
        cases = {"списки установщика": ("Тестов Т.Т.\r\nИванов И.И.\r\n", "ООО «Испытание»\r\n\r\nТОО «Троя»\r\nТР\r\n"),
                 "пустые списки": ("", "")}
        try:
            for case, (fam, firm) in cases.items():
                with self.subTest(case=case):
                    (lists / "MProp_Fam.txt").write_bytes(fam.encode("cp1251"))
                    (lists / "MProp_Firm.txt").write_bytes(firm.encode("cp1251"))
                    path = self.s.workspace_copy(paths.FIXTURES_A / A01, subdir=f"{self._case_name()}/{len(fam)}")
                    doc = self.s.open(path)
                    self.addCleanup(self.s.close_all)
                    self.s.save(doc)
                    before = mprop.snapshot(doc)
                    for cfg in [""] + list(com.as_list(doc.GetConfigurationNames)):
                        cpm = com.dyn(doc.Extension.CustomPropertyManager(cfg))
                        for name in list(com.prop_names(cpm)):
                            cpm.Delete2(name)
                    for info in range(5):  # как CmdDelete_Click: заголовок, тема, автор, ключевые слова, комментарий
                        o = doc._oleobj_
                        o.Invoke(o.GetIDsOfNames("SummaryInfo"), 0, 4, 0, info, "")
                    run = mprop.apply_without_edits(self.s, doc)
                    self.assertEqual([], self.s.watchdog.pop_unexpected(), "окна MProp")
                    self.assertTrue(run.get("ok"), run)
                    if fam:
                        self.assertEqual("Тестов Т.Т.", str(doc.SummaryInfo(2)), "«Разработал» MProp — своя фамилия")
                        self.assertEqual("ООО «Испытание»", mprop.snapshot(doc)["levels"]["00"].get("Контора"), "«Контора» MProp")
                    self.s.save(doc)
                    after = mprop.snapshot(doc)
                    lost = {f"{level} · {name}": (value, after["levels"].get(level, {}).get(name))
                            for level, props in before["levels"].items() for name, value in props.items()
                            if name in REQUISITES and after["levels"].get(level, {}).get(name) != value}
                    self.assertEqual({}, lost, "реквизиты после повторного заполнения: было → стало")
                    self.assertEqual(before["author"], after["author"], "Сводка → Автор")
                    self.s.close_all()
        finally:
            for name, data in saved.items():
                (lists / name).write_bytes(data)


if __name__ == "__main__":
    unittest.main()
