# -*- coding: utf-8 -*-
"""E2E, группы X и Y — кнопки «Выдать в производство» (ТЗ-02 Т-38…Т-43) и «Закрыть заказ» (Т-44…Т-47).

Ведомость изделия здесь собирается вручную (openpyxl): SWTools на стенде может быть не установлен,
а К-5 читает ведомость как файл — ей всё равно, кто её написал, лишь бы были именованные диапазоны
шаблона ЛЗК. Нормативы кладём рядом с корнем заказов, как на NAS.
"""
import shutil
import unittest
from pathlib import Path

import openpyxl
from openpyxl.workbook.defined_name import DefinedName

from eskd_e2e import com, paths
from eskd_e2e.testing import SwTestCase

PRODUCT = "И01_ПРТИ.468211.100"
CIPHER = "ПРТИ.468211.100"
ASM = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
ORDER = "2026-010 Стенд"

NAMES = ["Номер", "Обозначение", "Наименование", "Материал_Строка", "Габарит", "МассаЕдКг",
         "Количество", "Операции", "Путь"]
NORMS = [("Труба.Хлыст", "6000"), ("Труба.Захват", "200"), ("Труба.Торцовка", "20"), ("Труба.Рез", "0,5"),
         ("Труба.Деловой", "500"), ("Лист.Формат", "1250x2500"), ("Лист.Отход", "1,15"),
         ("Краска.Норма", "140"), ("Краска.Потери", "15"), ("Краска.Тара", "25")]

# Строки ведомости: обозначение, наименование, материал, габарит, масса, кол-во, операции, файл.
ROWS = [
    ("ПРТИ.468211.101", "Пластина опорная", "Ст3сп ГОСТ 380-2005", "200x120x6", 1.13, 2,
     "Лазерный раскрой; Покраска", "01_3D/ПРТИ.468211.101 Пластина опорная.sldprt"),
    ("ПРТИ.468211.102", "Стойка", "Труба 50х25х1.5 ГОСТ 8645-68", "L=1996", 2.4, 4,
     "Труборез; Сварка; Покраска", "01_3D/ПРТИ.468211.102 Стойка.sldprt"),
    ("ПРТИ.468211.100", "Кондуктор сварочный", "", "", 12.5, 1, "Сварка; Покраска",
     "01_3D/" + ASM),
]


def numbers(row):
    """Числа строки книги: openpyxl отдаёт их то int, то float — сравниваем по целой части."""
    return [int(c) for c in row if isinstance(c, (int, float)) and float(c).is_integer()]


class Issue(SwTestCase):

    # ------------------------------------------------------------------ фикстура заказа
    def _order(self, marks=False, norms=True, issued=False):
        """Заказ с одним изделием, ведомостью и справочником нормативов; возвращает (заказ, изделие, сборка)."""
        short = self._case_name().split("_")[1]
        subdir = f"{short}/_Заявки/{ORDER}/02_Металл/{PRODUCT}/01_3D"
        models = self.s.run_dir / subdir
        if models.exists():
            shutil.rmtree(models, ignore_errors=True)
        for src in sorted(Path(paths.FIXTURES_A).iterdir()):
            if src.suffix.lower() in (".sldprt", ".sldasm", ".slddrw"):
                self.s.workspace_copy(src, subdir=subdir)
        product = models.parent
        order = product.parent.parent
        self._workbook(product / f"Ведомость_{CIPHER}.xlsx", marks=marks)
        if norms:
            self._norms(self.s.run_dir / short / "Нормативы_производства.xlsx")
        if issued:
            (order / "_Выдано_2026-09-10_0900.txt").write_text(
                "Выдано в производство\nЗаказ: " + ORDER + "\n", encoding="utf-8")
        return order, product, models / ASM

    def _workbook(self, path, marks=False):
        """Ведомость по шаблону ЛЗК: главный лист с именованными диапазонами, «Покраска» и «Покупные»."""
        book = openpyxl.Workbook()
        main = book.active
        main.title = "Ведомость"
        main.append(["Ведомость ЛЗК"])
        main.append([])
        main.append(["№", "Обозначение", "Наименование", "Материал", "Габарит", "Масса, кг",
                     "Кол-во", "Операции", "Путь"])
        for n, row in enumerate(ROWS, start=1):
            designation, name, material, size, mass, quantity, operations, file = row
            main.append([n, designation, name, "?" if marks and n == 1 else material,
                         size, mass, quantity, operations, file])
        for column, named in zip("ABCDEFGHI", NAMES):
            book.defined_names.add(DefinedName(named, attr_text=f"'Ведомость'!${column}$3"))

        paint = book.create_sheet("Покраска")
        paint.append(["Покраска (на одно изделие)"])
        paint.append([])
        paint.append(["№", "Обозначение", "Наименование", "Площадь, м²", "Кол-во"])
        paint.append([1, "ПРТИ.468211.101", "Пластина опорная", 0.5, 2])
        paint.append([2, "ПРТИ.468211.102", "Стойка", 0.42, 4])

        purchased = book.create_sheet("Покупные")
        purchased.append(["Покупные изделия"])
        purchased.append([])
        purchased.append(["№", "Наименование", "Обозначение", "Код 1С", "Ед.", "ОКЕИ", "Кол-во"])
        purchased.append([1, "Болт М10х40", "ГОСТ 7798-70", "00-00012345", "шт", "796", 8])
        book.save(path)
        return path

    def _norms(self, path):
        book = openpyxl.Workbook()
        sheet = book.active
        sheet.title = "Нормативы"
        sheet.append(["Ключ", "Значение"])
        for key, value in NORMS:
            sheet.append([key, value])
        book.save(path)
        return path

    def _issue(self, order, number="2026-010", quantities="", color="RAL 7035 шагрень", draft=1):
        com.call(self.s.eskd(), "IssueProductionSilent", str(order), number, quantities, color, draft)
        return str(com.call(self.s.eskd(), "IssueStatus"))

    def _close(self, order, archive):
        com.call(self.s.eskd(), "CloseOrderSilent", str(order), str(archive))
        return str(com.call(self.s.eskd(), "CloseOrderStatus"))

    # ------------------------------------------------------------------ X: выдача в производство
    def test_X01_draft_builds_workbook_and_touches_nothing_else(self):
        """X01: черновик сводной собирает книгу в папке заказа; в производство ничего не уходит (Т-38)."""
        order, product, _ = self._order()
        status = self._issue(order)
        self.assertTrue(status.startswith("ok|"), status)
        _, path, products, files, mode = status.split("|")
        self.assertEqual("черновик", mode, status)
        self.assertEqual("1", products, status)
        self.assertEqual("0", files, "в черновике ничего не копируется")

        book = Path(path)
        self.assertTrue(book.is_file(), f"сводная собрана: {path}")
        self.assertEqual(order, book.parent, "книга лежит в папке заказа")
        sheets = openpyxl.load_workbook(book).sheetnames
        self.assertEqual(["Лист 1 Заготовки", "Лист 2 Сварка", "Лист 3 Покраска", "Лист 4 Покупные",
                          "Лист 5 Расход", "Цвета", "Комплект"], sheets, f"листы сводной: {sheets}")
        self.assertEqual([], list(order.glob("_Выдано_*.txt")), "черновик не пишет отчёт выдачи")
        for name in ("_Производство", "04_ПРОИЗВОДСТВО"):
            self.assertFalse((order.parent.parent / name).exists(), f"папка производства не создана: {name}")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X02_summary_counts_stock_and_paint_by_norms(self):
        """X02: тираж умножается, заготовки сводятся по сортаменту, хлысты и краска — по нормативам (Т-40)."""
        order, _, _ = self._order()
        status = self._issue(order, quantities=f"{PRODUCT}=2")
        self.assertTrue(status.startswith("ok|"), status)
        book = openpyxl.load_workbook(Path(status.split("|")[1]))

        blanks = [[c.value for c in row] for row in book["Лист 1 Заготовки"].iter_rows()]
        stoika = next(r for r in blanks if any(str(c or "").startswith("ПРТИ.468211.102") for c in r))
        self.assertIn(8, numbers(stoika), f"4 стойки × 2 изделия = 8: {stoika}")

        consumption = [[c.value for c in row] for row in book["Лист 5 Расход"].iter_rows()]
        tube = next(r for r in consumption if any("50х25" in str(c or "") for c in r))
        # 8 стоек по 1996 мм: по две на хлыст зоны реза 5800 мм — 4 хлыста.
        self.assertIn(4, numbers(tube), f"хлысты по CutPlan: {tube}")
        self.assertTrue(any("%" in str(c or "") for c in tube), f"КИМ в строке расхода: {tube}")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X03_missing_norms_stop_issue_with_explanation(self):
        """X03: без справочника нормативов кнопка отказывает и ничего не собирает (Т-13)."""
        order, _, _ = self._order(norms=False)
        status = self._issue(order)
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("Нормативы_производства.xlsx", status, status)
        self.assertIn("встроенных нормативов", status, "объяснено, почему нельзя продолжать")
        self.assertEqual([], list(order.glob("*Сводная_заявка*.xlsx")), "книга не собрана")

    def test_X04_marked_workbook_and_unchecked_product_stop_full_issue(self):
        """X04: полная выдача требует ведомость без пометок и К-3 = ГОТОВО; иначе отказ без копирования (Т-39)."""
        order, product, _ = self._order(marks=True)
        status = self._issue(order, draft=0)
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("пометками", status, status)
        self.assertEqual([], list(order.glob("_Выдано_*.txt")), "отчёт выдачи не написан")
        for name in ("_Производство", "04_ПРОИЗВОДСТВО"):
            self.assertFalse((order.parent.parent / name).exists(), f"в производство ничего не скопировано: {name}")

        # Ведомость без пометок — остаётся проверка изделия, и она у сырой фикстуры даёт «ЗАМЕЧАНИЯ».
        self._workbook(product / f"Ведомость_{CIPHER}.xlsx")
        status = self._issue(order, draft=0)
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("ГОТОВО", status, f"названа причина отказа: {status}")
        self.assertEqual([], list(order.glob("_Выдано_*.txt")), "отчёт выдачи не написан")

    def test_X05_order_issue_report_protects_documents_of_every_product(self):
        """X05: отчёт выдачи лежит в папке заказа (Т-42) — выгрузка изделия видит документ выданным (Т-30)."""
        order, product, asm = self._order()
        (order / "_Выдано_2026-09-11_1200.txt").write_text(
            "\n".join(["Выдано в производство", "", "Документы изделий:", "  ab12  " + ASM, ""]), encoding="utf-8")
        doc = self.s.open(asm)
        self.s.activate(doc)
        com.call(self.s.eskd(), "ExportProductSilent")
        status = str(com.call(self.s.eskd(), "ExportStatus"))
        self.assertTrue(status.startswith("ok|"), status)
        text = Path(status.split("|")[3]).read_text(encoding="utf-8-sig")
        self.assertIn("документ выдан в производство, оформите новую ревизию", text,
                      "отчёт заказа читается так же, как отчёт в папке изделия")

    # ------------------------------------------------------------------ Y: закрытие заказа
    def test_Y01_close_packs_order_and_moves_it_to_archive(self):
        """Y01: комплекты Pack and Go, сверка, архив Y:\\<год>\\<заказ> и папка в _Сдано с _Архив.txt (Т-45…Т-47)."""
        order, product, asm = self._order(issued=True)
        archive = self.s.run_dir / self._case_name() / "archive"
        archive.mkdir(parents=True, exist_ok=True)
        self.s.close_all()

        status = self._close(order, archive)
        self.assertTrue(status.startswith("ok|"), status)
        _, target, files, done = status.split("|")
        self.assertGreater(int(files), 0, status)

        target = Path(target)
        self.assertTrue(target.is_dir(), f"папка архива: {target}")
        self.assertEqual(archive.name, target.parent.parent.name, "архив в указанном корне")
        kit = target / "Комплекты" / CIPHER
        self.assertTrue(kit.is_dir(), f"комплект изделия: {sorted(p.name for p in target.iterdir())}")
        packed = sorted(p.name for p in kit.iterdir())
        self.assertTrue(any(p.lower().endswith(".sldasm") for p in packed), f"сборка в комплекте: {packed}")
        self.assertTrue(any(p.lower().endswith(".slddrw") for p in packed), f"чертежи в комплекте: {packed}")
        self.assertTrue((target / "02_Металл" / PRODUCT / "01_3D" / ASM).is_file(), "файлы заказа скопированы")

        done = Path(done)
        self.assertFalse(order.exists(), "папка заказа убрана из рабочего каталога")
        self.assertTrue(done.is_dir(), f"заказ в «_Сдано»: {done}")
        note = (done / "_Архив.txt").read_text(encoding="utf-8-sig")
        self.assertIn(str(target), note, f"записка ссылается на архив: {note}")
        self.assertFalse((order.parent / "_tmp" / ORDER).exists(), "временная папка убрана")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_Y02_open_document_and_missing_issue_report_stop_closing(self):
        """Y02: заказ не выдавался или его документ открыт — отказ без единого движения файлов (Т-44)."""
        order, product, asm = self._order()
        archive = self.s.run_dir / self._case_name() / "archive"
        archive.mkdir(parents=True, exist_ok=True)

        status = self._close(order, archive)
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("не выдавался", status, status)

        (order / "_Выдано_2026-09-10_0900.txt").write_text("Выдано в производство\n", encoding="utf-8")
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._close(order, archive)
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("открыт", status, status)
        self.assertTrue(order.is_dir(), "папка заказа на месте")
        self.assertEqual([], list(archive.iterdir()), "в архиве пусто")

    def test_Y03_unavailable_archive_keeps_order_untouched(self):
        """Y03: архивный диск недоступен — заказ не тронут, объяснение понятное (Т-46)."""
        order, _, _ = self._order(issued=True)
        self.s.close_all()
        status = self._close(order, self.s.run_dir / self._case_name() / "нет-такого-диска")
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("недоступен", status, status)
        self.assertTrue((order / "02_Металл" / PRODUCT / "01_3D" / ASM).is_file(), "файлы заказа на месте")
        self.assertFalse((order.parent / "_Сдано" / ORDER).exists(), "заказ не сдан")


if __name__ == "__main__":
    unittest.main()
