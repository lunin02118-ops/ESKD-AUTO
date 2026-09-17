# -*- coding: utf-8 -*-
"""E2E, группа W — кнопка «Снимок эталона» (ТЗ-02 Т-53…Т-55): папка `_Версии`, суммы и применяемость."""
import shutil
import unittest
from pathlib import Path

import openpyxl

from eskd_e2e import com, paths
from eskd_e2e.testing import SwTestCase

ETALON = "И01_ПРТИ.468211.100"
ASM = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
PART = "ПРТИ.468211.101 Пластина опорная.sldprt"


class Etalon(SwTestCase):

    def _etalon(self, order=None):
        """Эталон в «02_БАЗА» и, если просят, открытый заказ, взявший его в производство."""
        short = self._case_name().split("_")[1]
        subdir = f"{short}/02_БАЗА/{ETALON}/01_3D"
        models = self.s.run_dir / subdir
        if models.exists():
            shutil.rmtree(models, ignore_errors=True)
        for src in sorted(Path(paths.FIXTURES_A).iterdir()):
            if src.suffix.lower() in (".sldprt", ".sldasm", ".slddrw"):
                self.s.workspace_copy(src, subdir=subdir)
        if order:
            issued = self.s.run_dir / short / "_Заявки" / order / "02_Металл" / "И01_Заказ" / "_Выдано_2026-09-10.txt"
            issued.parent.mkdir(parents=True, exist_ok=True)
            issued.write_text("Выдано в производство\n\nab12cd  " + ASM + "\n", encoding="utf-8")
        return models.parent, models / ASM

    def _snapshot(self, what="состояние после правки", code="1"):
        com.call(self.s.eskd(), "EtalonSnapshotSilent", what, code)
        return str(com.call(self.s.eskd(), "EtalonStatus"))

    def test_W01_snapshot_copies_state_and_writes_journal(self):
        """W01: снимок кладёт модели и чертежи в _Версии\\<дата>_ИзмN и пишет строку в Изменения.xlsx."""
        product, asm = self._etalon(order="2026-001 Школа")
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._snapshot()
        self.assertTrue(status.startswith("ok|"), status)
        _, folder, files, applicability = status.split("|")
        self.assertGreater(int(files), 0, f"файлы скопированы: {status}")

        snapshot = Path(folder)
        self.assertTrue(snapshot.is_dir(), "папка снимка")
        self.assertIn("_Изм0", snapshot.name, f"в имени — наибольшая ревизия: {snapshot.name}")
        copied = sorted(p.name for p in (snapshot / "01_3D").glob("*"))
        self.assertIn(PART, copied, f"модели в снимке: {copied}")
        self.assertEqual((product / "01_3D" / PART).stat().st_size, (snapshot / "01_3D" / PART).stat().st_size,
                         "файл скопирован целиком")
        self.assertIn("2026-001 Школа", applicability, f"применяемость: {status}")

        rows = list(openpyxl.load_workbook(product / "Изменения.xlsx")["Изменения"].iter_rows(values_only=True))
        self.assertEqual("снимок " + snapshot.name, rows[1][4], f"документ в журнале: {rows[1]}")
        self.assertIn("2026-001 Школа", str(rows[1][9]), f"применяемость в журнале: {rows[1]}")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_W02_second_snapshot_without_changes_is_refused(self):
        """W02: эталон не изменился — второй снимок не делается (Т-54)."""
        product, asm = self._etalon()
        doc = self.s.open(asm)
        self.s.activate(doc)
        self.assertTrue(self._snapshot().startswith("ok|"), "первый снимок")
        status = self._snapshot()
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("не изменился", status, status)
        self.assertEqual(1, len(list((product / "_Версии").iterdir())), "снимок остался один")

    def test_W03_changed_etalon_gets_new_snapshot(self):
        """W03: после правки файла снимок делается снова — состояние другое."""
        product, asm = self._etalon()
        doc = self.s.open(asm)
        self.s.activate(doc)
        self.assertTrue(self._snapshot().startswith("ok|"), "первый снимок")
        self.s.close_all()
        (product / "01_3D" / "ПРТИ.468211.199 Новая деталь.sldprt").write_text(
            "не модель, но файл эталона", encoding="utf-8")
        doc = self.s.open(asm)
        self.s.activate(doc)
        self.assertTrue(self._snapshot().startswith("ok|"), "второй снимок после правки")
        self.assertEqual(2, len(list((product / "_Версии").iterdir())), "снимков два")

    def test_W04_order_product_is_not_an_etalon(self):
        """W04: изделие заказа — не эталон, снимок там не делается (Т-53)."""
        short = self._case_name().split("_")[1]
        subdir = f"{short}/_Заявки/2026-001/02_Металл/{ETALON}/01_3D"
        for src in sorted(Path(paths.FIXTURES_A).iterdir()):
            if src.suffix.lower() in (".sldprt", ".sldasm"):
                self.s.workspace_copy(src, subdir=subdir)
        doc = self.s.open(self.s.run_dir / subdir / ASM)
        self.s.activate(doc)
        self.assertEqual(0, int(com.call(self.s.eskd(), "EnableEtalonCommand")), "кнопка серая вне базы")
        status = self._snapshot()
        self.assertTrue(status.startswith("error|"), status)
        self.assertIn("вне базы", status, status)


if __name__ == "__main__":
    unittest.main()
