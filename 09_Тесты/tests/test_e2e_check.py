# -*- coding: utf-8 -*-
"""E2E, группа C — кнопка «Проверить изделие» (ТЗ-02 Т-32…Т-34): состав, реквизиты, отчёт и итог."""
import shutil
import time
import unittest
from pathlib import Path

from eskd_e2e import com, paths
from eskd_e2e.testing import SwTestCase, tags

PRODUCT = "И01_ПРТИ.468211.100"
ASM = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
SHEET_PART = "ПРТИ.468211.101 Пластина опорная.sldprt"
TIMEOUT = 600


class Check(SwTestCase):

    def _product(self):
        """Копия изделия в структуре заказа: _Заявки\\<заказ>\\02_Металл\\И01_…\\01_3D.

        Каталог теста назван коротко (K01, K02…): имена фикстур длинные, а Windows отказывает
        в копировании, когда путь переваливает за 260 знаков.
        """
        short = self._case_name().split("_")[1]
        subdir = f"{short}/_Заявки/2026-001/02_Металл/{PRODUCT}/01_3D"
        models = self.s.run_dir / subdir
        # Каталог теста — целиком: отчёт прежнего прогона в папке изделия (_Проверка.txt) иначе остаётся, и K05
        # «проверки ещё не было» падает при повторном прогоне в том же ESKD_RUN_DIR.
        if (self.s.run_dir / short).exists():
            shutil.rmtree(self.s.run_dir / short, ignore_errors=True)
        for src in sorted(Path(paths.FIXTURES_A).iterdir()):
            if src.suffix.lower() in (".sldprt", ".sldasm"):
                self.s.workspace_copy(src, subdir=subdir)
        return models.parent, models / ASM

    def _check(self):
        com.call(self.s.eskd(), "CheckProductSilent")
        deadline = time.time() + TIMEOUT
        status = ""
        while time.time() < deadline:
            status = str(com.call(self.s.eskd(), "CheckStatus"))
            if status:
                break
            time.sleep(1)
        return status

    @tags("smoke")
    def test_K01_report_lists_issues_and_outcome(self):
        """K01: проверка изделия без чертежей и выгрузки — «ЗАМЕЧАНИЯ», отчёт _Проверка.txt с правилами и суммами."""
        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        _, outcome, defects, issues, report_path = status.split("|")
        self.assertEqual("ЗАМЕЧАНИЯ", outcome, f"итог: {status}")
        self.assertEqual("0", defects, "брака нет: все компоненты на месте")
        self.assertGreater(int(issues), 0, "замечания есть: нет чертежей и выгрузки")

        report = product / "_Проверка.txt"
        self.assertEqual(str(report).lower(), report_path.lower(), "отчёт в папке изделия")
        text = report.read_text(encoding="utf-8-sig")
        self.assertIn("Итог:     ЗАМЕЧАНИЯ", text)
        self.assertIn("ЗАМЕЧАНИЕ — ", text, "строки замечаний")
        self.assertIn("нет книги ЛЗК изделия", text, "книга ЛЗК ещё не сформирована")
        self.assertIn("не выгружено для производства", text, "экспорта нет")
        self.assertIn("Контрольные суммы (SHA-256):", text)
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_K02_missing_component_is_defect(self):
        """K02: файла компонента нет на диске — итог БРАК."""
        product, asm = self._product()
        # Файла нет ещё до открытия: SolidWorks открывает сборку с потерянным компонентом
        # (диалог не выводится — открытие тихое), а проверка обязана назвать это браком.
        (asm.parent / SHEET_PART).unlink()
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        _, outcome, defects, _, report_path = status.split("|")
        self.assertEqual("БРАК", outcome, f"итог: {status}")
        self.assertGreater(int(defects), 0, "брак посчитан")
        text = Path(report_path).read_text(encoding="utf-8-sig")
        self.assertIn("БРАК — ", text, "строка брака")

    def test_K03_second_run_keeps_previous_report(self):
        """K03: повторная проверка сохраняет прежний отчёт в _Проверка_пред.txt."""
        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        self.assertTrue(self._check().startswith("ok|"))
        first = (product / "_Проверка.txt").read_text(encoding="utf-8-sig")
        self.assertTrue(self._check().startswith("ok|"))
        previous = product / "_Проверка_пред.txt"
        self.assertTrue(previous.is_file(), "прежний отчёт сохранён")
        self.assertEqual(first, previous.read_text(encoding="utf-8-sig"), "прежний отчёт — это первый")

    def test_K05_shows_last_report_without_checking(self):
        """K05: пункт «Отчёт проверки» отдаёт итог прежней проверки, а без отчёта — отказ."""
        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        com.call(self.s.eskd(), "ShowCheckReportSilent")
        self.assertTrue(str(com.call(self.s.eskd(), "CheckStatus")).startswith("error|"), "проверки ещё не было")

        self.assertTrue(self._check().startswith("ok|"))
        report = product / "_Проверка.txt"
        stamp = report.stat().st_mtime_ns
        com.call(self.s.eskd(), "ShowCheckReportSilent")
        status = str(com.call(self.s.eskd(), "CheckStatus"))
        _, outcome, _, _, path = status.split("|")
        self.assertEqual("ЗАМЕЧАНИЯ", outcome, status)
        self.assertEqual(str(report).lower(), path.lower(), "путь прежнего отчёта")
        self.assertEqual(stamp, report.stat().st_mtime_ns, "отчёт не переписан: проверка заново не запускалась")

    def test_K07_lightweight_report_changes_nothing(self):
        """K07 (ревью 23.09.2026): сборку открыли с облегчёнными компонентами — так регламент велит открывать большие
        изделия. Проверка без окна и тогда не помечает сборку изменённой и проверяет все детали."""
        product, asm = self._product()
        doc = self.s.open(asm, lightweight=True)
        self.s.activate(doc)
        self.assertGreater(int(com.dyn(doc).GetLightWeightComponentCount), 0, "компоненты облегчённые")
        self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "сборка открыта без изменений")
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "проверка не пометила сборку изменённой")
        report = (product / "_Проверка.txt").read_text(encoding="utf-8-sig")
        self.path("report.txt").write_text(report, encoding="utf-8")
        for name in ("ПРТИ.468211.101 Пластина опорная", "ПРТИ.468211.105 Рама сварная"):
            self.assertIn(name, report, "облегчённая деталь проверена")
        self.assertNotIn("модель не загрузилась", report, "облегчённые компоненты прочитаны, а не пропущены")
        self.assertNotIn("облегч", report.lower(), "облегчённые компоненты прочитаны, а не пропущены")
        self.s.close_all()

    def test_K06_report_changes_nothing(self):
        """K06 (аудит 23.09.2026): проверка без окна ничего не меняет — ни файлов, ни открытых документов. Раньше она
        перестраивала сборку (ForceRebuild3), и SolidWorks после проверки просил сохранить сборку и её детали."""
        product, asm = self._product()
        stamps = {p: p.stat().st_mtime_ns for p in asm.parent.iterdir() if p.suffix.lower() in (".sldprt", ".sldasm")}
        doc = self.s.open(asm)
        self.s.activate(doc)
        self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "сборка открыта без изменений")
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "проверка не пометила сборку изменённой")
        self.assertEqual("", str(com.call(self.s.eskd(), "CheckApplied")), "ничего не записано")
        self.s.close_all()
        self.assertEqual(stamps, {p: p.stat().st_mtime_ns for p in stamps}, "файлы изделия не переписаны")

    def test_K04_refuses_part_and_keeps_files(self):
        """K04: у детали кнопка недоступна и проверка отказывает без изменений файлов."""
        path, doc = self.open_copy(SHEET_PART)
        self.s.activate(doc)
        self.assertEqual(0, int(com.call(self.s.eskd(), "EnableCheckCommand")), "кнопка серая у детали")
        self.assertTrue(self._check().startswith("error|"), "проверка отказала")
        self.assertNoPropertyWrites()


if __name__ == "__main__":
    unittest.main()
