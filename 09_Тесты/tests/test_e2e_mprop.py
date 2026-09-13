# -*- coding: utf-8 -*-
"""E2E, группа M (продолжение) — совместимость с MProp: критерии К-1 и К-2 плана согласования с SWPlus.

К-1: после сохранения с надстройкой «Применить без правок» MProp (так же его запускает «Перезагрузка форматки» DProp)
не меняет ни одного значения: свойства по уровням, «Сводка → Автор», единицы массы документа; добавляться могут только
служебные имена MProp; окон-вопросов нет. К-2: следующее сохранение ничего не пишет.
Расхождения каждого случая сохраняются в каталоге теста (mprop_differences.json) — это рабочий список шага 3.
"""
import json
import unittest

from eskd_e2e import build, mprop, paths
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

    @known_defect("Д-48")
    def test_M04_mprop_apply_changes_nothing_plate(self):
        """M04 (К-1): пластина A-01 — масса, наименование, подписи, материал после MProp те же (Н-02, Н-03, Н-04, Н-05)."""
        self._assert_k1(A01)

    @known_defect("Д-52")
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

    @known_defect("Д-48")
    def test_M04_mprop_apply_changes_nothing_mass_threshold(self):
        """M04 (К-1): A-18 — исполнения 50,2 г и 175,8 г по обе стороны порога 100 г; единица одна на документ (И-20)."""
        self._assert_k1(A18)

    @known_defect("Д-55")
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

    @known_defect("Д-50")
    def test_M03_save_after_mprop_writes_nothing(self):
        """M03 (К-2): после «Применить» MProp сохранение ничего не пишет — надстройка не возвращает свои форматы (Н-04)."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        mprop.apply_without_edits(self.s, doc)
        self.s.watchdog.pop_unexpected()
        mark = self.mark("M03-after-mprop")
        self.s.save(doc)
        self.assertNoPropertyWrites(mark, "сохранение после MProp изменило свойства")


if __name__ == "__main__":
    unittest.main()
