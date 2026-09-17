# -*- coding: utf-8 -*-
"""E2E, группа V — кнопка «Новая ревизия» (ТЗ-02 Т-48…Т-52): штамп, журнал изменений и выгрузка с `_ИзмN`.

Изделие «выдаётся» отчётом `_Выдано_<дата>.txt` — так же, как его пишет кнопка «Выдать в производство»:
до выдачи чертёж правят свободно и ревизия не нужна.
"""
import shutil
import unittest
from pathlib import Path

import openpyxl

from eskd_e2e import com, oracles, paths
from eskd_e2e.testing import SwTestCase

PRODUCT = "И01_ПРТИ.468211.100"
ASM = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
PART = "ПРТИ.468211.101 Пластина опорная.sldprt"
DRAWING = "ПРТИ.468211.101 Пластина опорная.slddrw"


class Revision(SwTestCase):

    def _product(self, issued=True):
        """Изделие заказа с чертежами; issued — изделие уже выдано в производство."""
        short = self._case_name().split("_")[1]
        subdir = f"{short}/_Заявки/2026-001/02_Металл/{PRODUCT}/01_3D"
        models = self.s.run_dir / subdir
        if models.exists():
            shutil.rmtree(models, ignore_errors=True)
        for src in sorted(Path(paths.FIXTURES_A).iterdir()):
            if src.suffix.lower() in (".sldprt", ".sldasm", ".slddrw"):
                self.s.workspace_copy(src, subdir=subdir)
        product = models.parent
        if issued:
            (product / "_Выдано_2026-09-10.txt").write_text(
                "Выдано в производство\n\nab12cd  " + PART + "\n", encoding="utf-8")
        return product, models

    def _revision(self, what="толщина 2 → 3 мм", code="4", backlog="доработать"):
        com.call(self.s.eskd(), "NewRevisionSilent", what, code, backlog)
        return str(com.call(self.s.eskd(), "RevisionStatus"))

    def test_V01_raises_revision_and_writes_journal(self):
        """V01: на чертеже выданной детали ревизия 0 → 1, строка в Изменения.xlsx, свойство Revision записано."""
        product, models = self._product()
        doc = self.s.open(models / DRAWING)
        self.s.activate(doc)
        status = self._revision()
        self.assertTrue(status.startswith("ok|"), status)
        _, revision, line, exported = status.split("|")
        self.assertEqual("1", revision, f"первая ревизия: {status}")
        self.assertEqual("1", line, f"первая строка журнала: {status}")

        journal = product / "Изменения.xlsx"
        self.assertTrue(journal.is_file(), "журнал изменений заведён")
        sheet = openpyxl.load_workbook(journal)["Изменения"]
        rows = list(sheet.iter_rows(values_only=True))
        self.assertEqual("№", rows[0][0], f"шапка журнала: {rows[0]}")
        self.assertEqual("1", str(rows[1][1]), f"ревизия в журнале: {rows[1]}")
        self.assertEqual(DRAWING, rows[1][4], f"документ в журнале: {rows[1]}")
        self.assertEqual("толщина 2 → 3 мм", rows[1][5], f"что изменено: {rows[1]}")
        self.assertEqual("устранение ошибок", rows[1][6], f"причина по коду 4: {rows[1]}")
        self.assertEqual("доработать", rows[1][8], f"задел: {rows[1]}")

        self.s.close_all()
        self.assertEqual("1", oracles.value(self.persisted(models / DRAWING), "Revision"),
                         "ревизия записана в свойство чертежа")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_V02_exports_with_revision_suffix_and_archives_previous(self):
        """V02: после ревизии PDF выгружается с суффиксом _Изм1, а прежний файл уходит в _Аннулировано (Т-30, Т-52)."""
        # Сначала черновик: выгрузка даёт PDF без суффикса — то, что уйдёт цеху…
        product, models = self._product(issued=False)
        doc = self.s.open(models / ASM)
        self.s.activate(doc)
        com.call(self.s.eskd(), "ExportProductSilent")
        self.assertTrue(str(com.call(self.s.eskd(), "ExportStatus")).startswith("ok|"), "черновая выгрузка")
        before = sorted(p.name for p in (product / "02_PDF").glob("ПРТИ.468211.101*.pdf"))
        self.assertTrue(before, "PDF детали до ревизии")
        self.s.close_all()
        # …и только потом выдача: выданное правят через ревизию.
        (product / "_Выдано_2026-09-10.txt").write_text(
            "Выдано в производство\n\nab12cd  " + PART + "\n", encoding="utf-8")

        drawing = self.s.open(models / DRAWING)
        self.s.activate(drawing)
        self.assertTrue(self._revision().startswith("ok|"))
        after = sorted(p.name for p in (product / "02_PDF").glob("ПРТИ.468211.101*.pdf"))
        self.assertTrue(any(name.endswith("_Изм1.pdf") for name in after), f"PDF новой ревизии: {after}")
        archived = sorted(p.name for p in (product / "02_PDF" / "_Аннулировано").glob("*.pdf"))
        self.assertTrue(archived, "прежний PDF перенесён в _Аннулировано")

    def test_V03_draft_document_needs_no_revision(self):
        """V03: изделие не выдано — кнопка серая, вызов отказывает и ничего не пишет (Т-48)."""
        product, models = self._product(issued=False)
        doc = self.s.open(models / DRAWING)
        self.s.activate(doc)
        self.assertEqual(0, int(com.call(self.s.eskd(), "EnableRevisionCommand")), "кнопка серая у черновика")
        self.assertIn("не выдан", str(com.call(self.s.eskd(), "RevisionUnavailable")), "подсказка объясняет почему")
        status = self._revision()
        self.assertTrue(status.startswith("error|"), status)
        self.assertFalse((product / "Изменения.xlsx").exists(), "журнал не заведён")

    def test_V04_assembly_has_no_revision(self):
        """V04: у сборки ревизии нет — ревизия принадлежит чертежу (Р0-8)."""
        product, models = self._product()
        doc = self.s.open(models / ASM)
        self.s.activate(doc)
        self.assertEqual(0, int(com.call(self.s.eskd(), "EnableRevisionCommand")), "кнопка серая у сборки")
        self.assertTrue(self._revision().startswith("error|"), "вызов отказал")
        self.assertNoPropertyWrites()


if __name__ == "__main__":
    unittest.main()
