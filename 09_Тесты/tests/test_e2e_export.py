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


def igs_line_extents(path):
    """Размах координат X, Y, Z по отрезкам (сущность 110) файла IGES, мм."""
    lines = Path(path).read_text(encoding="ascii", errors="ignore").splitlines()
    parameters = "".join(line[:64] for line in lines if len(line) >= 73 and line[72] == "P")
    axes = ([], [], [])
    for record in parameters.split(";"):
        fields = record.split(",")
        if fields[0].strip() != "110":
            continue
        values = [float(v.replace("D", "E")) for v in fields[1:7]]
        for i in range(3):
            axes[i].extend((values[i], values[i + 3]))
    return [max(a) - min(a) if a else 0.0 for a in axes]


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
        # конструкции в ней нет, поэтому развёрток и профиля здесь не бывает — их выгрузку проверяет X05.
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

    def test_X05_exports_flat_pattern_dxf_and_tube_igs(self):
        """X05 (Т-28, Т-29): листовая деталь выгружается развёрткой DXF с толщиной и рамкой в имени, деталь сварной
        конструкции — IGS в «Труборез» в СК по оси трубы (труба наклонная, а в IGS лежит вдоль X); временная СК
        удалена, деталь сохранена без неё; обе строки — в отчёте с контрольными суммами."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.160/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        models.mkdir(parents=True)
        sheet = models / "ПРТИ.468211.161 Лист опорный.sldprt"
        tube = models / "ПРТИ.468211.162 Стойка трубная.sldprt"
        doc = build.sheet_metal_plate(self.s, 200, 100, 3, "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97")
        self.s.save_as(doc, sheet)
        doc = build.structural_tube(self.s, 500, build.tube_profile(), "Труба 30х30х1,5 ГОСТ 8639-82 / 08пс ГОСТ 13663-86",
                                    angle_deg=53.13)
        self.s.save_as(doc, tube)
        asm, _ = build.assembly(self.s, [(sheet, 0, 0, 0), (tube, 0, 0.2, 0)])
        asm_path = models / "ПРТИ.468211.160 СБ Рама.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        doc = self.s.open(asm_path)
        self.s.activate(doc)

        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        product = models.parent
        dxfs = sorted((product / "03_ЧПУ" / "Лазер_Лист").glob("*.dxf"))
        self.assertEqual(1, len(dxfs), f"одна развёртка: {[p.name for p in dxfs]}")
        self.assertTrue(dxfs[0].name.startswith("ПРТИ.468211.161 Лист опорный_S3мм_"), dxfs[0].name)
        self.assertRegex(dxfs[0].name, r"_S3мм_(200х100|100х200)\.dxf$", "рамка развёртки в имени")
        igs = sorted((product / "03_ЧПУ" / "Труборез").glob("*.igs"))
        self.assertEqual(["ПРТИ.468211.162 Стойка трубная.igs"], [p.name for p in igs], "IGS трубы")
        self.assertGreater(igs[0].stat().st_size, 1000, "IGS непустой")
        _, dy, dz = igs_line_extents(igs[0])
        self.assertLess(max(dy, dz), 41, f"сечение 40х40 поперёк оси X — IGS в СК по оси трубы: Y={dy:.1f}, Z={dz:.1f}")
        text = (product / "_Экспорт.txt").read_text(encoding="utf-8-sig")
        for path in dxfs + igs:
            self.assertIn(path.name, text, f"{path.name} в отчёте")
        self.assertNotIn("Замечания:", text, "СК по оси построена — замечаний нет")

        self.s.close_all()
        part = self.s.open(tube)
        names = []
        feat = com.call(part, "FirstFeature")
        while feat is not None:
            names.append(str(com.dyn(feat).Name))
            feat = com.call(feat, "GetNextFeature")
        self.assertNotIn("_ЕСКД_ось_трубы", names, "временная СК удалена")
        self.assertEqual("", str(part.Extension.GetUserPreferenceString(16, 0) or ""), "СК вывода возвращена")
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
