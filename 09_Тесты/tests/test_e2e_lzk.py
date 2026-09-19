# -*- coding: utf-8 -*-
"""E2E, группа L — кнопка «Ведомость ЛЗК» (ТЗ-02 Т-35…Т-37, ТЗ-04): операции, габарит, выгрузка SWTools без окна,
живая книга ЛЗК — паспорт, участки, расход, нормы — в «04_Сопроводительная документация» изделия."""
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
DOCS = "04_Сопроводительная документация"
BOOK = "ЛЗК_ПРТИ.468211.100.xlsx"
SHEETS = ["Паспорт", "Ведомость", "Заготовительный", "Сварочный", "Покрасочный", "Комплектовочный", "Расход", "Нормы"]
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
        # справочник нормативов «на заказ» над изделием: встроенных нормативов у надстройки нет (Т-13)
        norms = paths.ROOT / "02_Шаблоны_и_Форматки" / "Справочники" / "Нормативы_производства.xlsx"
        (self.case_dir / norms.name).write_bytes(norms.read_bytes())
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
        """L01: книга ЛЗК_<шифр>.xlsx в «04_Сопроводительная документация» — паспорт, участки, расход, нормы; «Операции» и «Габарит» в модели."""
        import openpyxl

        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._build()
        self.assertTrue(status.startswith("ok|"), status)
        _, path, row_count, issues = status.split("|")
        workbook = product / DOCS / BOOK
        self.assertEqual(str(workbook).lower(), path.lower(), "имя и место ведомости")
        self.assertTrue(workbook.is_file(), "файл ведомости")
        self.assertFalse((product / "_Ведомость.txt").exists(), "отчёта-текстовика больше нет — замечания в окне")
        notices = str(com.call(self.s.eskd(), "LastNotices"))
        self.assertEqual(int(issues), notices.count("ЗАМЕЧАНИЕ — "), f"пометки «?» — замечания окна: {notices}")
        self.assertGreater(int(row_count), 5, "строк в ведомости")

        wb = openpyxl.load_workbook(workbook)
        self.assertEqual(SHEETS, wb.sheetnames)
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
        paint = wb["Покрасочный"]
        self.assertEqual("Покрасочный участок", paint["A1"].value, "название листа")
        self.assertEqual("№", paint["A5"].value, "шапка таблицы")
        self.assertEqual("=Тираж", paint["C3"].value, "тираж в шапке — из паспорта")
        data = [r for r in range(6, paint.max_row + 1) if isinstance(paint.cell(r, 1).value, (int, float))]
        painted = [paint.cell(r, 2).value for r in data]
        self.assertIn("ПРТИ.468211.101", painted, f"лист в покраске: {painted}")
        areas = [paint.cell(r, 4).value for r in data]
        self.assertTrue(all(isinstance(a, (int, float)) and a > 0 for a in areas), f"площадь каждой единицы: {areas}")
        self.assertEqual(len(set(areas)), len(areas), f"площади разных деталей различаются: {areas}")
        self.assertTrue(all(str(paint.cell(r, 6).value).endswith("*Тираж") for r in data), "всего на заказ — формулой")
        kit = wb["Комплектовочный"]
        codes = [kit.cell(r, 4).value for r in range(6, kit.max_row + 1) if isinstance(kit.cell(r, 1).value, (int, float))]
        self.assertNotIn("?", codes, f"нет кода — ячейка пустая, а не «?»: {codes}")
        self.assertTrue(str(main.auto_filter.ref or "").startswith("A6:M"), f"фильтр по таблице с категорией: {main.auto_filter.ref}")
        self.assertTrue(all(main.cell(r, 11).value for r in rows), "категория у каждой строки")
        self.assertIn("Тираж", wb.defined_names, "имя «Тираж»")
        self.assertIn("Хлыст", wb.defined_names, "норматив «Хлыст»")
        self.assertFalse((product / "Ведомость_ПРТИ.468211.100.xlsx").exists(), "в корне изделия ведомости нет")
        sizes = [main.cell(r, 6).value for r in rows]
        self.assertTrue(all(sizes), f"габарит у каждой строки: {sizes}")
        self.assertEqual(int(issues), int(str(main["G4"].value).split()[0]) if main["G4"].value != "нет" else 0,
                         "число замечаний в шапке")

        self.s.close_all()
        self.assertEqual("Механическая сборка", V(self.persisted(asm.parent / SUBASSEMBLY), "Операции"), "«Операции» в подсборке")
        self.assertEqual("200×100×4", V(self.persisted(asm.parent / SHEET_PART), "Габарит"), "«Габарит» в детали")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_L02_second_run_archives_previous(self):
        """L02: повторное формирование переносит прежнюю книгу в _Аннулировано, введённый тираж и нормы сохраняет;
        ведомость старого образца из корня изделия тоже уходит в _Аннулировано."""
        import openpyxl

        product, asm = self._product()
        legacy = product / "Ведомость_ПРТИ.468211.100.xlsx"
        openpyxl.Workbook().save(legacy)
        doc = self.s.open(asm)
        self.s.activate(doc)
        self.assertTrue(self._build().startswith("ok|"))
        self.assertFalse(legacy.exists(), "ведомость старого образца убрана из корня")
        self.assertEqual(1, len(list((product / "_Аннулировано").glob("Ведомость_ПРТИ.468211.100_*.xlsx"))), "старый образец в архиве")

        # Начальник производства ввёл тираж и правку нормы — повторная К-4 их не теряет.
        book = product / DOCS / BOOK
        wb = openpyxl.load_workbook(book)
        sheet, cell = next(iter(wb.defined_names["Тираж"].destinations))
        wb[sheet][cell.replace("$", "")] = 41
        sheet, cell = next(iter(wb.defined_names["Захват"].destinations))
        wb[sheet][cell.replace("$", "")] = 600
        wb.save(book)
        self.assertTrue(self._build().startswith("ok|"))
        archived = list((product / "_Аннулировано").glob("ЛЗК_ПРТИ.468211.100_*.xlsx"))
        self.assertEqual(1, len(archived), archived)
        wb = openpyxl.load_workbook(book)
        sheet, cell = next(iter(wb.defined_names["Тираж"].destinations))
        self.assertEqual(41, wb[sheet][cell.replace("$", "")].value, "тираж сохранён")
        sheet, cell = next(iter(wb.defined_names["Захват"].destinations))
        self.assertEqual(600, wb[sheet][cell.replace("$", "")].value, "норма сохранена")

    def test_L03_refuses_non_assembly(self):
        """L03: у детали кнопка отказывает без изменений файлов."""
        path, doc = self.open_copy(SHEET_PART)
        self.s.activate(doc)
        status = self._build()
        self.assertTrue(status.startswith("error|"), status)
        self.assertNoPropertyWrites()


if __name__ == "__main__":
    unittest.main()
