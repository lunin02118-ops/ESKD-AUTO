# -*- coding: utf-8 -*-
"""E2E, группы R и Y — кнопки «Готово к производству» (ТЗ-04 Р4-8) и «Закрыть заказ» (ТЗ-02 Т-44…Т-47).

Окна «Выдать в производство» на заказ больше нет: изделие отмечается готовым по одному, отчёт _Выдано лежит
в папке изделия, PDF листов участков делает Excel из книги ЛЗК. Книги здесь собираются вручную (openpyxl) —
кнопка читает их как файлы.
"""
import shutil
import unittest
from pathlib import Path

import openpyxl

from eskd_e2e import com, paths
from eskd_e2e.testing import SwTestCase

PRODUCT = "И01_ПРТИ.468211.100"
CIPHER = "ПРТИ.468211.100"
ASM = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
ORDER = "2026-010 Стенд"
DOCS = "04_Сопроводительная документация"


class Issue(SwTestCase):

    # ------------------------------------------------------------------ фикстура заказа
    def _order(self, issued=False):
        """Заказ с одним изделием; возвращает (заказ, изделие, сборка)."""
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
        if issued:
            (product / "_Выдано_2026-09-10_0900.txt").write_text(
                "Готово к производству\nИзделие: " + CIPHER + "\n", encoding="utf-8")
        return order, product, models / ASM

    def _book(self, path):
        """Книга ЛЗК нового образца: паспорт с именем «Тираж» и лист участка."""
        from openpyxl.workbook.defined_name import DefinedName

        path.parent.mkdir(parents=True, exist_ok=True)
        book = openpyxl.Workbook()
        passport = book.active
        passport.title = "Паспорт"
        passport["A9"] = "Изделий в заказе, шт."
        passport["B9"] = 1
        book.defined_names.add(DefinedName("Тираж", attr_text="'Паспорт'!$B$9"))
        blank = book.create_sheet("Заготовительный")
        blank["A1"] = "Заготовительный участок"
        blank["A6"] = 1
        book.save(path)
        return path

    def _ready(self, asm):
        doc = self.s.open(asm)
        self.s.activate(doc)
        com.call(self.s.eskd(), "ReadyForProductionSilent")
        return str(com.call(self.s.eskd(), "ReadyStatus"))

    def _close(self, order, archive):
        com.call(self.s.eskd(), "CloseOrderSilent", str(order), str(archive))
        return str(com.call(self.s.eskd(), "CloseOrderStatus"))

    def _nothing_issued(self, product):
        self.assertEqual([], list(product.glob("_Выдано_*.txt")), "отметка «готово» не написана")
        self.assertEqual([], list((product / "02_PDF").glob("ЛЗК_*.pdf")) if (product / "02_PDF").exists() else [],
                         "PDF листов участков не сделаны")

    # ------------------------------------------------------------------ R: готово к производству
    def test_R01_without_book_asks_for_lzk(self):
        """R01: у изделия нет книги ЛЗК — кнопка просит сначала «Ведомость ЛЗК» и ничего не пишет."""
        _, product, asm = self._order()
        status = self._ready(asm)
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("Ведомость ЛЗК", status, status)
        self._nothing_issued(product)

    def test_R02_legacy_book_must_be_rebuilt(self):
        """R02: ведомость старого образца (в корне изделия, без участков) — кнопка просит пересобрать книгу."""
        _, product, asm = self._order()
        openpyxl.Workbook().save(product / f"Ведомость_{CIPHER}.xlsx")
        status = self._ready(asm)
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("старого образца", status, status)
        self._nothing_issued(product)

    def test_R03_unchecked_product_is_not_ready(self):
        """R03: книга есть, но проверка изделия не «ГОТОВО» — отказ с причиной, PDF и отметки нет."""
        _, product, asm = self._order()
        self._book(product / DOCS / f"ЛЗК_{CIPHER}.xlsx")
        status = self._ready(asm)
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("ГОТОВО", status, f"названа причина отказа: {status}")
        self._nothing_issued(product)
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_R04_product_ready_report_protects_its_documents(self):
        """R04: отметка «готово» лежит в папке изделия — выгрузка видит документ выданным (Т-30)."""
        _, product, asm = self._order()
        (product / "_Выдано_2026-09-11_1200.txt").write_text(
            "\n".join(["Готово к производству", "", "Документы изделия:", "  ab12  " + ASM, ""]), encoding="utf-8")
        doc = self.s.open(asm)
        self.s.activate(doc)
        com.call(self.s.eskd(), "ExportProductSilent")
        status = str(com.call(self.s.eskd(), "ExportStatus"))
        self.assertTrue(status.startswith("ok|"), status)
        text = Path(status.split("|")[3]).read_text(encoding="utf-8-sig")
        self.assertIn("документ выдан в производство, оформите новую ревизию", text,
                      "отметка в папке изделия защищает выданный документ")

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

        (product / "_Выдано_2026-09-10_0900.txt").write_text("Готово к производству\n", encoding="utf-8")
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
