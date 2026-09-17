# -*- coding: utf-8 -*-
"""E2E, группа X — кнопка «Выгрузить в производство» (ТЗ-02 Т-25…Т-31): PDF, DXF развёрток, IGS и отчёт."""
import shutil
import time
import unittest
from pathlib import Path

from eskd_e2e import com, paths
from eskd_e2e.testing import SwTestCase, tags

PRODUCT = "И01_ПРТИ.468211.100"
ASM = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
SHEET_PART = "ПРТИ.468211.101 Пластина опорная.sldprt"
TIMEOUT = 900


class Export(SwTestCase):

    def _product(self):
        """Изделие в структуре заказа со всеми моделями и чертежами фикстуры A."""
        short = self._case_name().split("_")[1]
        subdir = f"{short}/_Заявки/2026-001/02_Металл/{PRODUCT}/01_3D"
        models = self.s.run_dir / subdir
        if models.exists():
            shutil.rmtree(models, ignore_errors=True)
        for src in sorted(Path(paths.FIXTURES_A).iterdir()):
            if src.suffix.lower() in (".sldprt", ".sldasm", ".slddrw"):
                self.s.workspace_copy(src, subdir=subdir)
        return models.parent, models / ASM

    def _export(self):
        com.call(self.s.eskd(), "ExportProductSilent")
        deadline = time.time() + TIMEOUT
        status = ""
        while time.time() < deadline:
            status = str(com.call(self.s.eskd(), "ExportStatus"))
            if status:
                break
            time.sleep(1)
        return status

    @tags("smoke")
    def test_X01_exports_pdf_dxf_and_report(self):
        """X01: выгрузка изделия даёт PDF чертежей, DXF развёртки листовой детали и отчёт с контрольными суммами."""
        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        _, files, skipped, report_path = status.split("|")
        self.assertGreater(int(files), 0, "выгружены файлы")

        pdfs = sorted((product / "02_PDF").glob("*.pdf"))
        self.assertTrue(pdfs, "PDF чертежей")
        self.assertTrue(all(p.stat().st_size > 1000 for p in pdfs), f"PDF непустые: {[p.name for p in pdfs]}")
        self.assertTrue(any(p.name.startswith("ПРТИ.468211.100 ") for p in pdfs),
                        f"PDF назван по обозначению и наименованию: {[p.name for p in pdfs]}")

        # Фикстура A смоделирована обычными телами: ни листового металла, ни элементов сварной
        # конструкции в ней нет, поэтому развёрток и профиля здесь не бывает. Имена проверяет unit,
        # сам экспорт DXF и IGS — боевой прогон на заказе с трубой и листом.
        dxfs = sorted((product / "03_ЧПУ" / "Лазер_Лист").glob("*.dxf"))
        igs = sorted((product / "03_ЧПУ" / "Труборез").glob("*.igs"))
        self.assertTrue(all("_S" in p.name and "мм_" in p.name for p in dxfs),
                        f"в имени DXF толщина и рамка: {[p.name for p in dxfs]}")

        report = product / "_Экспорт.txt"
        self.assertEqual(str(report).lower(), report_path.lower(), "отчёт в папке изделия")
        text = report.read_text(encoding="utf-8-sig")
        self.assertIn("Выгружено (SHA-256):", text)
        for path in pdfs + dxfs + igs:
            self.assertIn(path.name, text, f"{path.name} в отчёте")
        self.assertIn("Файлов:   " + files, text, "число файлов в шапке отчёта")
        self.assertIn("пропущено: " + skipped, text, "число пропусков в шапке отчёта")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X02_reports_missing_drawings(self):
        """X02: у детали без чертежа выгрузка не падает — пропуск с причиной в отчёте."""
        product, asm = self._product()
        for drawing in (asm.parent).glob("*.slddrw"):
            drawing.unlink()
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        _, _, skipped, report_path = status.split("|")
        self.assertGreater(int(skipped), 0, "пропуски посчитаны")
        text = Path(report_path).read_text(encoding="utf-8-sig")
        self.assertIn("нет чертежа: PDF не сделан", text)

    def test_X04_issued_documents_are_not_overwritten(self):
        """X04: документ из _Выдано_*.txt не перевыгружается — в отчёте просьба оформить ревизию."""
        product, asm = self._product()
        (product / "_Выдано_2026-09-10.txt").write_text(
            "\n".join(["Выдано в производство", "", "ab12  " + ASM, ""]), encoding="utf-8")
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        text = Path(status.split("|")[3]).read_text(encoding="utf-8-sig")
        self.assertIn("документ выдан в производство, оформите новую ревизию", text)
        self.assertFalse(any(p.name.startswith("ПРТИ.468211.100 ") for p in (product / "02_PDF").glob("*.pdf")),
                         "PDF выданной сборки не перезаписан")

    def test_X03_refuses_drawing(self):
        """X03: на чертеже кнопка недоступна и выгрузка отказывает."""
        self.copy_fixture(SHEET_PART)
        path, doc = self.open_copy("ПРТИ.468211.101 Пластина опорная.slddrw")
        self.s.activate(doc)
        self.assertEqual(0, int(com.call(self.s.eskd(), "EnableExportCommand")), "кнопка серая на чертеже")
        self.assertTrue(self._export().startswith("error|"), "выгрузка отказала")


if __name__ == "__main__":
    unittest.main()
