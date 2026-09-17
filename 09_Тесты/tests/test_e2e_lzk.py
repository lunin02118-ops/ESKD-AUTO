# -*- coding: utf-8 -*-
"""E2E, группа L — кнопка «Ведомость ЛЗК» (ТЗ-02 Т-35…Т-37): операции, габарит, выгрузка SWTools без окна, листы."""
import time
import unittest
import winreg
from pathlib import Path

from eskd_e2e import com, oracles, paths
from eskd_e2e.testing import SwTestCase, tags

V = oracles.value
PRODUCT = "И01_ПРТИ.468211.100_Кондуктор"
ASM = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
SHEET_PART = "ПРТИ.468211.101 Пластина опорная.sldprt"
SUBASSEMBLY = "ПРТИ.468211.110 СБ Узел опоры.sldasm"
TIMEOUT = 600


def swtools_installed():
    for view in (winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\SWTools\InstallRoot", 0,
                                winreg.KEY_READ | view) as key:
                root = winreg.QueryValueEx(key, "")[0]
                if (Path(root) / "SWTools.exe").is_file():
                    return True
        except OSError:
            continue
    return False


@unittest.skipUnless(swtools_installed(), "SWTools не установлен")
class Lzk(SwTestCase):

    def _product(self):
        """Копия изделия в структуре заказа: И01_<шифр>_<имя>\\01_3D со всеми моделями фикстуры A."""
        models = self.case_dir / PRODUCT / "01_3D"
        models.mkdir(parents=True, exist_ok=True)
        for src in sorted(Path(paths.FIXTURES_A).iterdir()):
            if src.suffix.lower() in (".sldprt", ".sldasm"):
                self.s.workspace_copy(src, subdir=f"{self._case_name()}/{PRODUCT}/01_3D")
        return self.case_dir / PRODUCT, models / ASM

    def _build(self):
        com.call(self.s.eskd(), "BuildLzkSilent")
        deadline = time.time() + TIMEOUT
        status = "running"
        while time.time() < deadline:
            status = str(com.call(self.s.eskd(), "LzkStatus"))
            if status != "running":
                break
            time.sleep(1)
        return status

    @tags("smoke")
    def test_L01_builds_workbook_with_sheets(self):
        """L01: ведомость Ведомость_<шифр>.xlsx в папке изделия — три листа, шапка, отчёт; «Операции» и «Габарит» в модели."""
        import openpyxl

        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._build()
        self.assertTrue(status.startswith("ok|"), status)
        _, path, row_count, issues = status.split("|")
        workbook = product / "Ведомость_ПРТИ.468211.100.xlsx"
        self.assertEqual(str(workbook).lower(), path.lower(), "имя и место ведомости")
        self.assertTrue(workbook.is_file(), "файл ведомости")
        self.assertTrue((product / "_Ведомость.txt").is_file(), "отчёт")
        self.assertGreater(int(row_count), 5, "строк в ведомости")

        wb = openpyxl.load_workbook(workbook)
        self.assertEqual(["Ведомость", "Покраска", "Покупные"], wb.sheetnames)
        main = wb["Ведомость"]
        self.assertTrue(str(main["B2"].value or "").startswith("ПРТИ.468211.100"), main["B2"].value)
        self.assertEqual(ASM, main["B3"].value, "сборка в шапке")
        self.assertTrue(str(main["B4"].value or "").startswith("SHA-256 "), "контрольная сумма")
        rows = [r for r in range(7, main.max_row + 1) if main.cell(r, 10).value]
        self.assertEqual(int(row_count), len(rows), "строк в итоге кнопки и в книге")
        designations = [main.cell(r, 3).value for r in rows]
        self.assertIn("ПРТИ.468211.101", designations, "обозначение из свойства модели, без наименования")
        names = {main.cell(r, 3).value: main.cell(r, 4).value for r in rows}
        self.assertEqual("Пластина опорная", names["ПРТИ.468211.101"], "наименование")
        ops = {main.cell(r, 3).value: main.cell(r, 9).value for r in rows}
        self.assertEqual("Механическая сборка", ops["ПРТИ.468211.110"], "операции подсборки по признакам модели")
        self.assertTrue(all(ops.values()), f"у каждой строки операции или «?»: {ops}")
        # З-1: красится прокат; лист из стали — прокат, значит и резка, и покраска.
        self.assertIn("Покраска", ops["ПРТИ.468211.101"], f"стальной лист красится: {ops['ПРТИ.468211.101']}")
        self.assertNotIn("Покраска", ops["ПРТИ.468211.110"], "механическая сборка целиком не красится")
        paint = wb["Покраска"]
        self.assertEqual("Покраска (на одно изделие)", paint["A1"].value, "название листа")
        self.assertEqual("№", paint["A3"].value, "шапка таблицы")
        painted = [paint.cell(r, 2).value for r in range(4, paint.max_row + 1) if paint.cell(r, 1).value]
        self.assertIn("ПРТИ.468211.101", painted, f"лист в покраске: {painted}")
        areas = [paint.cell(r, 4).value for r in range(4, paint.max_row + 1) if paint.cell(r, 1).value]
        self.assertTrue(all(isinstance(a, (int, float)) and a > 0 for a in areas), f"площадь каждой единицы: {areas}")
        self.assertEqual(len(set(areas)), len(areas), f"площади разных деталей различаются: {areas}")
        bought = wb["Покупные"]
        codes = [bought.cell(r, 4).value for r in range(4, bought.max_row + 1) if bought.cell(r, 1).value]
        self.assertNotIn("?", codes, f"нет кода — ячейка пустая, а не «?»: {codes}")
        sizes = [main.cell(r, 6).value for r in rows]
        self.assertTrue(all(sizes), f"габарит у каждой строки: {sizes}")
        self.assertEqual(int(issues), int(str(main["G4"].value).split()[0]) if main["G4"].value != "нет" else 0,
                         "число замечаний в шапке")

        self.s.close_all()
        self.assertEqual("Механическая сборка", V(self.persisted(asm.parent / SUBASSEMBLY), "Операции"), "«Операции» в подсборке")
        self.assertEqual("200×100×4", V(self.persisted(asm.parent / SHEET_PART), "Габарит"), "«Габарит» в детали")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_L02_second_run_archives_previous(self):
        """L02: повторное формирование переносит прежнюю ведомость в _Аннулировано и не спрашивает об операциях."""
        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        self.assertTrue(self._build().startswith("ok|"))
        self.assertTrue(self._build().startswith("ok|"))
        archived = list((product / "_Аннулировано").glob("Ведомость_ПРТИ.468211.100_*.xlsx"))
        self.assertEqual(1, len(archived), archived)
        self.assertTrue((product / "Ведомость_ПРТИ.468211.100.xlsx").is_file())

    def test_L03_refuses_non_assembly(self):
        """L03: у детали кнопка отказывает без изменений файлов."""
        path, doc = self.open_copy(SHEET_PART)
        self.s.activate(doc)
        status = self._build()
        self.assertTrue(status.startswith("error|"), status)
        self.assertNoPropertyWrites()


if __name__ == "__main__":
    unittest.main()
