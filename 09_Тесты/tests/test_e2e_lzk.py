# -*- coding: utf-8 -*-
"""E2E, группа L — кнопка «Ведомость ЛЗК» (ТЗ-02 Т-35…Т-37, ТЗ-04): операции, габарит, выгрузка SWTools без окна,
живая книга ЛЗК — паспорт, участки, расход, нормы — в «04_Сопроводительная документация» изделия."""
import os
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
        # справочник бланков — тоже из репозитория: иначе надстройка возьмёт опубликованный в инструментарии на NAS
        for name in ("Нормативы_производства.xlsx", "ЛЗК_бланки.xlsx"):
            ref = paths.ROOT / "02_Шаблоны_и_Форматки" / "Справочники" / name
            (self.case_dir / ref.name).write_bytes(ref.read_bytes())
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

    def _outside(self, folder):
        """Сборка в папке изделия, её детали — в папке folder вне изделия (относительно каталога теста). Возвращает
        (папка изделия, пути деталей, итог кнопки, замечания)."""
        from eskd_e2e import build

        product = self.case_dir / PRODUCT
        (product / "01_3D").mkdir(parents=True, exist_ok=True)
        for name in ("Нормативы_производства.xlsx", "ЛЗК_бланки.xlsx"):
            ref = paths.ROOT / "02_Шаблоны_и_Форматки" / "Справочники" / name
            (self.case_dir / ref.name).write_bytes(ref.read_bytes())
        parts = [self.s.workspace_copy(Path(paths.FIXTURES_A) / n, subdir=f"{self._case_name()}/{folder}")
                 for n in (SHEET_PART, "ПРТИ.468211.111 Стойка трубная.sldprt")]
        asm, opened = build.assembly(self.s, [(parts[0], 0, 0, 0), (parts[1], 0, 0.15, 0)])
        self.s.save_as(asm, product / "01_3D" / ASM)
        for doc in opened:
            self.s.close(doc)
        self.s.activate(asm)
        status = self._build()
        notices = str(com.call(self.s.eskd(), "LastNotices"))
        self.path("outcome.txt").write_text(status + "\n\n" + notices, encoding="utf-8")
        self.assertTrue(status.startswith("ok|"), f"{status}\n{notices}")
        return product, parts, status, notices

    def _sections(self, workbook):
        """{обозначение: операции} ведомости и обозначения на листах участков."""
        import openpyxl
        wb = openpyxl.load_workbook(workbook)
        main = wb["Ведомость"]
        ops = {main.cell(r, 3).value: main.cell(r, 9).value for r in range(7, main.max_row + 1) if main.cell(r, 10).value}
        on_sections = {wb[s].cell(r, 2).value for s in ("Заготовительный", "Сварочный", "Покрасочный", "Комплектовочный")
                       for r in range(6, wb[s].max_row + 1)}
        return ops, on_sections

    def test_L04_parts_outside_product_folder(self):
        """L04 (замечание владельца 19.09.2026): сборка в папке изделия, её детали — в другой папке. Книга ЛЗК — на всю
        сборку: у деталей из другой папки операции (не «?»), записанные в их модели, и они есть на участках."""
        product, parts, _, notices = self._outside("Детали_общие")
        workbook = product / DOCS / BOOK
        self.assertTrue(workbook.is_file(), "книга ЛЗК изделия")
        ops, on_sections = self._sections(workbook)
        for designation, path in (("ПРТИ.468211.101", parts[0]), ("ПРТИ.468211.111", parts[1])):
            with self.subTest(part=designation):
                self.assertNotIn(ops.get(designation) or "?", ("?", ""), f"операции детали из другой папки: {ops}\n{notices}")
                self.assertIn(designation, on_sections, f"деталь из другой папки на участках: {on_sections}")
        self.s.close_all()
        for designation, path in (("ПРТИ.468211.101", parts[0]), ("ПРТИ.468211.111", parts[1])):
            with self.subTest(disk=designation):
                self.assertEqual(ops[designation], V(self.persisted(path), "Операции"), "операции записаны в модель")

    def test_L06_assembly_in_any_folder(self):
        """L06 (замечание владельца 19.09.2026): главная сборка — в любой папке, вне заказа и без «01_3D»; детали — рядом и
        в другой папке. Книга ЛЗК — прямо рядом со сборкой, новых папок нет; повторный запуск заменяет книгу."""
        from eskd_e2e import build

        folder = self.case_dir / "Рабочая папка" / "Новый стол"
        folder.mkdir(parents=True, exist_ok=True)
        near = self.s.workspace_copy(Path(paths.FIXTURES_A) / SHEET_PART, subdir=f"{self._case_name()}/Рабочая папка/Новый стол")
        other = self.s.workspace_copy(Path(paths.FIXTURES_A) / "ПРТИ.468211.111 Стойка трубная.sldprt",
                                      subdir=f"{self._case_name()}/Детали где-то ещё")
        asm, opened = build.assembly(self.s, [(near, 0, 0, 0), (other, 0, 0.15, 0)])
        self.s.save_as(asm, folder / "Стол письменный.sldasm")
        for doc in opened:
            self.s.close(doc)
        self.s.activate(asm)
        status = self._build()
        notices = str(com.call(self.s.eskd(), "LastNotices"))
        self.path("outcome.txt").write_text(status + "\n\n" + notices, encoding="utf-8")
        self.assertTrue(status.startswith("ok|"), f"{status}\n{notices}")
        workbook = Path(status.split("|")[1])
        self.assertEqual(folder.resolve(), workbook.parent.resolve(), "книга прямо рядом со сборкой")
        self.assertEqual("ЛЗК_Стол письменный.xlsx", workbook.name)
        self.assertTrue(workbook.is_file(), "книга ЛЗК")
        ops, on_sections = self._sections(workbook)
        for designation in ("ПРТИ.468211.101", "ПРТИ.468211.111"):
            with self.subTest(part=designation):
                self.assertNotIn(ops.get(designation) or "?", ("?", ""), f"операции: {ops}\n{notices}")
                self.assertIn(designation, on_sections, f"на участках: {on_sections}")
        self.assertEqual([], [d.name for d in folder.iterdir() if d.is_dir()], "рядом со сборкой новых папок нет")
        status = self._build()
        self.assertTrue(status.startswith("ok|"), status)
        self.assertEqual([], [d.name for d in folder.iterdir() if d.is_dir()], "и после повторного запуска")
        self.assertEqual(["ЛЗК_Стол письменный.xlsx"], sorted(f.name for f in folder.glob("*.xlsx")))
        notices = str(com.call(self.s.eskd(), "LastNotices"))
        backup = next((line.split("её копия — ", 1)[1].strip() for line in notices.splitlines() if "её копия — " in line), "")
        self.assertTrue(backup and Path(backup).is_file(), f"копия прежней книги вне папки сборки\n{notices}")
        self.addCleanup(Path(backup).unlink, True)
        self.assertNotEqual(folder.resolve(), Path(backup).parent.resolve())

    def test_L08_unreadable_previous_book_is_not_overwritten(self):
        """L08: прежняя книга рядом со сборкой не читается — кнопка отказывает и её не трогает: введённое в ней
        (тираж, срок, нормы) не должно пропасть молча."""
        from eskd_e2e import build

        folder = self.case_dir / "Где угодно"
        folder.mkdir(parents=True, exist_ok=True)
        part = self.s.workspace_copy(Path(paths.FIXTURES_A) / SHEET_PART, subdir=f"{self._case_name()}/Где угодно")
        asm, _ = build.assembly(self.s, [(part, 0, 0, 0)])
        self.s.save_as(asm, folder / "Тумба.sldasm")
        broken = folder / "ЛЗК_Тумба.xlsx"
        broken.write_bytes(b"not a zip workbook")
        self.s.activate(asm)
        status = self._build()
        self.path("outcome.txt").write_text(status, encoding="utf-8")
        self.assertTrue(status.startswith("error|") and "не читается" in status, status)
        self.assertEqual(b"not a zip workbook", broken.read_bytes(), "прежняя книга не тронута")

    def test_L07_readonly_models_and_denied_folder_still_give_book(self):
        """L07: чужая сборка — деталь занята (только для чтения), в папку сборки писать нельзя. Раньше кнопка обрывалась
        «Ведомость не сформирована»; теперь книга — в «Документы», операции детали — в книге."""
        import subprocess
        from eskd_e2e import build

        stamp = time.strftime("%H%M%S")
        name = f"Стол чужой L07 {stamp}"
        folder = self.case_dir / "Чужая папка"
        folder.mkdir(parents=True, exist_ok=True)
        part = self.s.workspace_copy(Path(paths.FIXTURES_A) / SHEET_PART, subdir=f"{self._case_name()}/Чужая папка")
        asm, opened = build.assembly(self.s, [(part, 0, 0, 0)])
        asm_path = folder / f"{name}.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        before = part.read_bytes()

        user = os.environ["USERNAME"]
        subprocess.run(["icacls", str(folder), "/deny", f"{user}:(AD,WD)"], check=True, capture_output=True)
        self.addCleanup(subprocess.run, ["icacls", str(folder), "/remove:d", user], capture_output=True)

        self.s.open(part, readonly=True)
        doc = self.s.open(asm_path)
        self.s.activate(doc)
        status = self._build()
        notices = str(com.call(self.s.eskd(), "LastNotices"))
        self.path("outcome.txt").write_text(status + "\n\n" + notices, encoding="utf-8")
        self.assertTrue(status.startswith("ok|"), f"{status}\n{notices}")
        workbook = Path(status.split("|")[1])
        self.assertEqual(f"ЛЗК_{name}.xlsx", workbook.name, "запасное место — «Документы», без папок")
        self.addCleanup(workbook.unlink, True)
        self.assertTrue(workbook.is_file(), "книга ЛЗК")
        self.assertIn("записать нельзя", notices)
        self.assertIn("только для чтения", notices)
        ops, on_sections = self._sections(workbook)
        self.assertNotIn(ops.get("ПРТИ.468211.101") or "?", ("?", ""), f"операции: {ops}\n{notices}")
        self.assertIn("ПРТИ.468211.101", on_sections)
        self.s.close_all()
        self.assertEqual(before, part.read_bytes(), "деталь только для чтения не изменена")

    def test_L05_base_parts_get_operations_only_in_book(self):
        """L05: детали из базы («_Библиотека проектирования») — операции по модели в книге и на участках, файл базы не меняется."""
        base = "_Библиотека проектирования/Общие детали"
        before = {}
        for n in (SHEET_PART, "ПРТИ.468211.111 Стойка трубная.sldprt"):
            src = Path(paths.FIXTURES_A) / n
            before[n] = src.read_bytes()
        product, parts, _, notices = self._outside(base)
        ops, on_sections = self._sections(product / DOCS / BOOK)
        for designation, path in (("ПРТИ.468211.101", parts[0]), ("ПРТИ.468211.111", parts[1])):
            with self.subTest(part=designation):
                self.assertNotIn(ops.get(designation) or "?", ("?", ""), f"операции детали базы: {ops}")
                self.assertIn(designation, on_sections, f"деталь базы на участках: {on_sections}")
                self.assertEqual(before[path.name], path.read_bytes(), "файл базы не изменён")
        self.assertIn("модель базы", notices, "пометка, что операции только в книге")

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
        # В-3 (решение владельца 19.09.2026): подписей на листах участков нет
        for section in ("Заготовительный", "Сварочный", "Покрасочный", "Комплектовочный"):
            texts = [str(c.value) for row in wb[section].iter_rows() for c in row if isinstance(c.value, str)]
            self.assertFalse([t for t in texts if "_____ /" in t], f"{section}: строки подписей")
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
