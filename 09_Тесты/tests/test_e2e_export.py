# -*- coding: utf-8 -*-
"""E2E, группа X — кнопка «Выгрузить в производство» (ТЗ-02 Т-25…Т-31): PDF, DXF развёрток, IGS и отчёт."""
import math
import re
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


def igs_edge_extents(path):
    """Размах X, Y, Z по отрезкам (110) — рёбрам граней тела файла IGES, мм: отрезки, на которые ссылаются границы
    обрезанных граней (144/142, через составные кривые 102). Оси поверхностей вращения (120) — скруглений углов трубы —
    SolidWorks пишет отрезками произвольной длины (у трубы 400 мм — 1000 мм), и по всем отрезкам длина трубы неверна."""
    records = {}
    for line in Path(path).read_text(encoding="ascii", errors="ignore").splitlines():
        if len(line) >= 73 and line[72] == "P":
            de = int(line[64:72])
            records[de] = records.get(de, "") + line[:64]
    entities = {de: [f.strip() for f in text.split(";")[0].split(",")] for de, text in records.items()}
    boundary = {int(f[4]) for f in entities.values() if f[0] == "142"}
    for de in list(boundary):
        f = entities.get(de)
        if f and f[0] == "102":
            boundary.update(int(x) for x in f[2:2 + int(f[1])])
    axes = ([], [], [])
    for de, f in entities.items():
        if f[0] != "110" or de not in boundary:
            continue
        values = [float(v.replace("D", "E")) for v in f[1:7]]
        for i in range(3):
            axes[i].extend((values[i], values[i + 3]))
    return [max(a) - min(a) if a else 0.0 for a in axes]


class Export(SwTestCase):

    def _product(self):
        """Изделие в структуре заказа со всеми моделями и чертежами фикстуры A."""
        short = self._case_name().split("_")[1]
        subdir = f"{short}/_Заявки/2026-001/02_Металл/{PRODUCT}/01_3D"
        models = self.s.run_dir / subdir
        # Каталог теста — целиком: отчёты прежнего прогона в папке изделия (_Проверка.txt, _Экспорт.txt, _Выдано_…)
        # иначе остаются при повторном прогоне в том же ESKD_RUN_DIR.
        if (self.s.run_dir / short).exists():
            shutil.rmtree(self.s.run_dir / short, ignore_errors=True)
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

        # Детали из листа фикстуры A построены листовым металлом (Д-84) — у них есть развёртки; трубы уходят в IGS
        # по материалу (X06). Имена и количество развёрток проверяют X05 и X07.
        dxfs = sorted((product / "03_ЧПУ" / "Лазер_Лист").glob("*.dxf"))
        igs = sorted((product / "03_ЧПУ" / "Труборез").glob("*.igs"))
        self.assertTrue(dxfs, "DXF развёрток листовых деталей")
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
        self.assertRegex(dxfs[0].name, r"_S3мм_1шт_(200х100|100х200)\.dxf$", "количество на изделие и рамка развёртки в имени")
        igs = sorted((product / "03_ЧПУ" / "Труборез").glob("*.igs"))
        self.assertEqual(["ПРТИ.468211.162 Стойка трубная.igs"], [p.name for p in igs], "IGS трубы")
        self.assertGreater(igs[0].stat().st_size, 1000, "IGS непустой")
        _, dy, dz = igs_line_extents(igs[0])
        self.assertLess(max(dy, dz), 41, f"сечение 40х40 поперёк оси X — IGS в СК по оси трубы: Y={dy:.1f}, Z={dz:.1f}")
        text = (product / "_Экспорт.txt").read_text(encoding="utf-8-sig")
        for path in dxfs + igs:
            self.assertIn(path.name, text, f"{path.name} в отчёте")
        # Изделие здесь не проверялось — об этом одно замечание (решение владельца 23.09.2026, З-27); других нет.
        remarks = [line.strip() for line in text.split("Замечания:", 1)[-1].splitlines()[1:] if line.startswith("  ")] \
            if "Замечания:" in text else []
        self.assertEqual([], [r for r in remarks if "непроверенному изделию" not in r], f"СК по оси построена — замечаний нет:\n{text}")

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

    def test_X09_export_own_saves_keep_version(self):
        """X09 (решение владельца 23.09.2026, З-27): выгрузка проверенного изделия сама сохраняет трубу — временная СК по оси
        убрана, деталь сохранена снова. Это сохранение версию изделия не меняет: новая сумма трубы вписана в отчёт
        проверки («Суммы обновлены: выгрузка для производства»), повторная проверка оставляет прежнюю версию и не находит
        выгрузку «по другой версии»."""
        import re
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.190/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        models.mkdir(parents=True)
        sheet = models / "ПРТИ.468211.191 Лист опорный.sldprt"
        tube = models / "ПРТИ.468211.192 Стойка трубная.sldprt"
        doc = build.sheet_metal_plate(self.s, 200, 100, 3, "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97")
        self.s.save_as(doc, sheet)
        doc = build.structural_tube(self.s, 500, build.tube_profile(), "Труба 30х30х1,5 ГОСТ 8639-82 / 08пс ГОСТ 13663-86",
                                    angle_deg=53.13)
        self.s.save_as(doc, tube)
        asm, _ = build.assembly(self.s, [(sheet, 0, 0, 0), (tube, 0, 0.2, 0)])
        asm_path = models / "ПРТИ.468211.190 СБ Рама.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        doc = self.s.open(asm_path)
        self.s.activate(doc)
        product = models.parent

        def checked():
            com.call(self.s.eskd(), "CheckProductSilent")
            status = str(com.call(self.s.eskd(), "CheckStatus"))
            self.assertTrue(status.startswith("ok|"), status)
            text = (product / "_Проверка.txt").read_text(encoding="utf-8-sig")
            found = re.search(r"^Версия:\s+(.+)$", text, re.M)
            self.assertIsNotNone(found, text)
            return text, found.group(1).strip()

        _, version = checked()
        before = tube.read_bytes()
        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        exported = (product / "_Экспорт.txt").read_text(encoding="utf-8-sig")
        self.path("export.txt").write_text(exported, encoding="utf-8")
        self.assertIn("Версия:   " + version, exported, "изделие проверено и не менялось — выгрузка помнит версию")
        self.assertNotIn("не убрана", exported, "временная СК трубы убрана")
        self.assertNotEqual(before, tube.read_bytes(), "труба сохранена выгрузкой (СК по оси построена и убрана)")
        report = (product / "_Проверка.txt").read_text(encoding="utf-8-sig")
        self.assertIn("Суммы обновлены: выгрузка для производства", report, report)

        text, again = checked()
        self.path("check2.txt").write_text(text, encoding="utf-8")
        self.assertEqual(version, again, "сохранение самой выгрузки версию не меняет")
        self.assertNotIn("другой версии", text, text)
        self.assertNotIn("непроверенному", text, text)
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X06_tube_without_weldment_goes_to_igs_by_material(self):
        """X06 (заказ 778, 22.09.2026): труба, построенная без элемента сварной конструкции и без списка вырезов
        (так выглядят тела, выделенные из многотельной детали «Разделить»), уходит в IGS по материалу «Труба …» —
        тем же правилом ЛЗК ставит ей «Лазерная резка трубы». Пластина с материалом «Лист …», построенная не листовым
        металлом, развёртки не даёт, но в отчёте сказано почему — раньше выгрузка молчала."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.170/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        models.mkdir(parents=True)
        tube = models / "ПРТИ.468211.171 Стойка.sldprt"
        plate = models / "ПРТИ.468211.172 Накладка.sldprt"
        doc, _ = build.square_tube(self.s, 30, 1.5, 600, "Труба 30х30х1,5 ГОСТ 8639-82 / 08пс ГОСТ 13663-86")
        self.s.save_as(doc, tube)
        doc, _ = build.plate(self.s, 200, 100, 3, "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97")
        self.s.save_as(doc, plate)
        asm, _ = build.assembly(self.s, [(tube, 0, 0, 0), (plate, 0, 0.2, 0)])
        asm_path = models / "ПРТИ.468211.170 СБ Стойка.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        doc = self.s.open(asm_path)
        self.s.activate(doc)

        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        product = models.parent
        igs = sorted((product / "03_ЧПУ" / "Труборез").glob("*.igs"))
        self.assertEqual(["ПРТИ.468211.171 Стойка.igs"], [p.name for p in igs], "IGS трубы без сварной конструкции")
        self.assertGreater(igs[0].stat().st_size, 1000, "IGS непустой")
        self.assertEqual([], list((product / "03_ЧПУ" / "Лазер_Лист").glob("*.dxf")) if (product / "03_ЧПУ" / "Лазер_Лист").exists() else [],
                         "развёртки у не листовой детали нет")
        text = (product / "_Экспорт.txt").read_text(encoding="utf-8-sig")
        self.assertIn("построена не листовым металлом", text, "причина отсутствия DXF в отчёте")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X08_igs_follows_tube_cutting_checkbox(self):
        """X08 (решение владельца 22.09.2026): IGS на труборез выгружается, когда в операциях детали стоит
        «Лазерная резка трубы». Труба, у которой галочку сняли, в «Труборез» не попадает; деталь с галочкой — попадает."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.190/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        models.mkdir(parents=True)
        unchecked = models / "ПРТИ.468211.191 Стойка.sldprt"
        checked = models / "ПРТИ.468211.192 Вставка.sldprt"
        doc, _ = build.square_tube(self.s, 30, 1.5, 600, "Труба 30х30х1,5 ГОСТ 8639-82 / 08пс ГОСТ 13663-86")
        build.props(doc, {"Операции": "Сварочная сборка"}, "")
        self.s.save_as(doc, unchecked)
        doc, _ = build.plate(self.s, 200, 100, 3, "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97")
        build.props(doc, {"Операции": "Лазерная резка трубы"}, "")
        self.s.save_as(doc, checked)
        asm, _ = build.assembly(self.s, [(unchecked, 0, 0, 0), (checked, 0, 0.2, 0)])
        asm_path = models / "ПРТИ.468211.190 СБ Рама.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        doc = self.s.open(asm_path)
        self.s.activate(doc)

        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        igs = sorted(p.name for p in (models.parent / "03_ЧПУ" / "Труборез").glob("*.igs"))
        self.assertEqual(["ПРТИ.468211.192 Вставка.igs"], igs, "IGS — только у детали с галочкой")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X07_every_sheet_execution_gets_its_dxf_with_quantity(self):
        """X07 (заказ 778, 22.09.2026): листовая деталь в двух исполнениях — «00» дважды и «01» один раз — даёт две
        развёртки, каждая со своим обозначением и количеством на изделие в имени. Деталь лежит в «Стандартные изделия
        и фурнитура», но обозначение у неё из серии изделия — это своя деталь, а не покупная. Конфигурация детали
        после выгрузки прежняя."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.180/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        (models / "Стандартные изделия и фурнитура").mkdir(parents=True)
        part = models / "Стандартные изделия и фурнитура" / "ПРТИ.468211.181 Кронштейн.sldprt"
        doc = build.sheet_metal_plate(self.s, 120, 80, 3, "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97")
        active = str(doc.GetActiveConfiguration.Name)
        build.add_configuration(doc, "01")
        build.show_configuration(doc, active)
        self.s.save_as(doc, part)
        self.s.wait_addin_idle(timeout=60.0)
        # Обозначения исполнений («…-01») надстройка пишет при сохранении — второе сохранение кладёт их в файл
        # до сборки изделия, как у настоящей детали заказа.
        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        self.assertEqual("ПРТИ.468211.181-01", com.prop_get(doc.Extension.CustomPropertyManager("01"), "Обозначение")[0],
                         "у исполнения своё обозначение")
        asm, opened = build.assembly(self.s, [(part, 0, 0, 0), (part, 0, 0.2, 0), (part, 0, 0.4, 0)])
        comps = com.as_list(asm.GetComponents(True))
        com.dyn(comps[2]).ReferencedConfiguration = "01"
        asm.ForceRebuild3(False)
        asm_path = models / "ПРТИ.468211.180 СБ Опора.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        doc = self.s.open(asm_path)
        self.s.activate(doc)

        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        product = models.parent
        names = sorted(p.name for p in (product / "03_ЧПУ" / "Лазер_Лист").glob("*.dxf"))
        self.assertEqual(2, len(names), f"по развёртке на исполнение: {names}")
        self.assertRegex(names[0], r"^ПРТИ\.468211\.181 Кронштейн_S3мм_2шт_(120х80|80х120)\.dxf$", names)
        self.assertRegex(names[1], r"^ПРТИ\.468211\.181-01 Кронштейн_S3мм_1шт_(120х80|80х120)\.dxf$", names)
        self.s.close_all()
        part_doc = self.s.open(part)
        self.assertEqual(active, str(part_doc.GetActiveConfiguration.Name), "конфигурация детали возвращена")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X10_every_tube_execution_gets_its_igs(self):
        """X10 (сверка SW API 23.09.2026): труба в двух исполнениях, в сборке стоят оба — на труборез уходит IGS каждого
        исполнения со своим обозначением, как DXF у листовой детали (X07). Раньше IGS делался один — активного в файле
        исполнения, и исполнение из сборки, не активное в файле, цех не получал вовсе. Конфигурация детали после
        выгрузки прежняя."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.200/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        models.mkdir(parents=True)
        part = models / "ПРТИ.468211.201 Укосина.sldprt"
        doc = build.structural_tube(self.s, 400, build.tube_profile(), "Труба 30х30х1,5 ГОСТ 8639-82 / 08пс ГОСТ 13663-86")
        active = str(doc.GetActiveConfiguration.Name)
        build.add_configuration(doc, "01")
        build.show_configuration(doc, active)
        self.s.save_as(doc, part)
        self.s.wait_addin_idle(timeout=60.0)
        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        self.assertEqual("ПРТИ.468211.201-01", com.prop_get(doc.Extension.CustomPropertyManager("01"), "Обозначение")[0],
                         "у исполнения своё обозначение")
        asm, _ = build.assembly(self.s, [(part, 0, 0, 0), (part, 0, 0.2, 0)])
        comps = com.as_list(asm.GetComponents(True))
        com.dyn(comps[1]).ReferencedConfiguration = "01"
        asm.ForceRebuild3(False)
        asm_path = models / "ПРТИ.468211.200 СБ Опора.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        doc = self.s.open(asm_path)
        self.s.activate(doc)

        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        product = models.parent
        names = sorted(p.name for p in (product / "03_ЧПУ" / "Труборез").glob("*.igs"))
        self.assertEqual(["ПРТИ.468211.201 Укосина.igs", "ПРТИ.468211.201-01 Укосина.igs"], names,
                         "по IGS на каждое исполнение из сборки")
        text = (product / "_Экспорт.txt").read_text(encoding="utf-8-sig")
        for name in names:
            self.assertIn(name, text, f"{name} в отчёте")
        self.s.close_all()
        part_doc = self.s.open(part)
        self.assertEqual(active, str(part_doc.GetActiveConfiguration.Name), "конфигурация детали возвращена")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X11_dxf_frame_is_flat_pattern_bounding_box(self):
        """X11 (Т-28, сверка SW API 23.09.2026, находка 13; замечание владельца 23.09.2026): рамка в имени DXF — граничная
        рамка развёртки, как её считает SolidWorks («Длина/Ширина граничной рамки» списка вырезов): по внешнему контуру
        с дугами и окружностями, наименьшая, даже если деталь построена под углом. Раньше диск не выгружался вовсе
        («развёртка пустая»), у планки с полукруглыми торцами было 80х200, у пластины под 30° — 187х223."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.300/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        models.mkdir(parents=True)
        a = math.radians(30)
        corners = [(x * math.cos(a) - y * math.sin(a), x * math.sin(a) + y * math.cos(a))
                   for x, y in ((-100, -50), (100, -50), (100, 50), (-100, 50))]
        parts = {
            "ПРТИ.468211.301 Диск": ([("круг", (0, 0), 75)], "150х150"),
            "ПРТИ.468211.302 Планка": ([((-100, -40), (100, -40)), ((100, 0), (100, -40), (100, 40)),
                                        ((100, 40), (-100, 40)), ((-100, 0), (-100, 40), (-100, -40))], "80х280"),
            "ПРТИ.468211.303 Пластина": ([(corners[i], corners[(i + 1) % 4]) for i in range(4)], "100х200"),
            "ПРТИ.468211.304 Косынка": ([((0, 0), (200, 0)), ((200, 0), (0, 100)), ((0, 100), (0, 0))], "100х200"),
        }
        placed = []
        for i, (name, (outline, _)) in enumerate(parts.items()):
            doc = build.sheet_metal_outline(self.s, outline, 3, "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97")
            path = models / (name + ".sldprt")
            self.s.save_as(doc, path)
            placed.append((path, 0, 0.3 * i, 0))
        asm, _ = build.assembly(self.s, placed)
        asm_path = models / "ПРТИ.468211.300 СБ Набор.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        doc = self.s.open(asm_path)
        self.s.activate(doc)

        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        names = sorted(p.name for p in (models.parent / "03_ЧПУ" / "Лазер_Лист").glob("*.dxf"))
        expected = sorted(f"{name}_S3мм_1шт_{frame}.dxf" for name, (_, frame) in parts.items())
        self.assertEqual(expected, names, "рамка каждой развёртки — граничная рамка SolidWorks")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X12_igs_of_execution_in_assembly_has_its_own_length(self):
        """X12 (сверка SW API 23.09.2026, находка 12): в изделии стоит только исполнение «-01», а в файле активно «00»;
        труба «-01» короче — 300 мм против 600. На труборез уходит один IGS — «-01» длиной 300. Раньше выгружалось
        активное в файле «00»: чужое обозначение и чужая длина. После выгрузки в файле снова активно «00»."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.220/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        models.mkdir(parents=True)
        part = models / "ПРТИ.468211.221 Стойка.sldprt"
        doc, feat = build.square_tube(self.s, 30, 1.5, 600, "Труба 30х30х1,5 ГОСТ 8639-82 / 08пс ГОСТ 13663-86")
        active = str(doc.GetActiveConfiguration.Name)
        build.add_configuration(doc, "01")
        build.show_configuration(doc, "01")
        # 1 — только в этой конфигурации: у «00» длина остаётся 600.
        com.dyn(doc.Parameter("D1@" + str(feat.Name))).SetSystemValue3(0.3, 1, None)
        doc.ForceRebuild3(False)
        build.show_configuration(doc, active)
        doc.ForceRebuild3(False)
        self.s.save_as(doc, part)
        self.s.wait_addin_idle(timeout=60.0)
        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        self.assertEqual("ПРТИ.468211.221-01", com.prop_get(doc.Extension.CustomPropertyManager("01"), "Обозначение")[0],
                         "у исполнения своё обозначение")
        asm, _ = build.assembly(self.s, [(part, 0, 0, 0)])
        comps = com.as_list(asm.GetComponents(True))
        com.dyn(comps[0]).ReferencedConfiguration = "01"
        asm.ForceRebuild3(False)
        asm_path = models / "ПРТИ.468211.220 СБ Опора.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        doc = self.s.open(asm_path)
        self.s.activate(doc)

        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        igs = sorted((models.parent / "03_ЧПУ" / "Труборез").glob("*.igs"))
        self.assertEqual(["ПРТИ.468211.221-01 Стойка.igs"], [p.name for p in igs], "IGS исполнения из изделия, не активного в файле")
        self.assertAlmostEqual(300, max(igs_line_extents(igs[0])), delta=1, msg="длина трубы — исполнения «-01»")
        self.s.close_all()
        part_doc = self.s.open(part)
        self.assertEqual(active, str(part_doc.GetActiveConfiguration.Name), "конфигурация детали возвращена")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X13_igs_of_execution_follows_its_own_member(self):
        """X13 (сверка SW API 23.09.2026, критик): у «Укосины» в каждом исполнении свой элемент конструкции — в «00»
        труба 400 вдоль X, в «01» труба 300 вдоль Y, чужой элемент погашен. IGS каждого исполнения лежит по оси своей
        трубы. Раньше ось бралась по первому элементу в дереве, даже погашенному: у «-01» труба в IGS шла поперёк оси X,
        и труборез получал бы её боком."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.230/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        models.mkdir(parents=True)
        part = models / "ПРТИ.468211.231 Укосина.sldprt"
        doc, configs = build.structural_tube_executions(
            self.s, [(400, 0, (0, 0)), (300, 90, (-100, 0))], build.tube_profile(),
            "Труба 30х30х1,5 ГОСТ 8639-82 / 08пс ГОСТ 13663-86")
        self.assertEqual(2, len(configs))
        self.s.save_as(doc, part)
        self.s.wait_addin_idle(timeout=60.0)
        self.s.save(doc)
        self.s.wait_addin_idle(timeout=60.0)
        self.assertEqual("ПРТИ.468211.231-01", com.prop_get(doc.Extension.CustomPropertyManager("01"), "Обозначение")[0],
                         "у исполнения своё обозначение")
        asm, _ = build.assembly(self.s, [(part, 0, 0, 0), (part, 0, 0.5, 0)])
        comps = com.as_list(asm.GetComponents(True))
        com.dyn(comps[1]).ReferencedConfiguration = "01"
        asm.ForceRebuild3(False)
        asm_path = models / "ПРТИ.468211.230 СБ Опора.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        doc = self.s.open(asm_path)
        self.s.activate(doc)

        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        product = models.parent
        igs = {p.name: p for p in (product / "03_ЧПУ" / "Труборез").glob("*.igs")}
        self.assertEqual(["ПРТИ.468211.231 Укосина.igs", "ПРТИ.468211.231-01 Укосина.igs"], sorted(igs),
                         "по IGS на каждое исполнение из сборки")
        for name, length in (("ПРТИ.468211.231 Укосина.igs", 400), ("ПРТИ.468211.231-01 Укосина.igs", 300)):
            dx, dy, dz = igs_edge_extents(igs[name])
            self.assertAlmostEqual(length, dx, delta=1, msg=f"{name}: труба вдоль оси X своей СК, X={dx:.1f}")
            self.assertLess(max(dy, dz), 41, f"{name}: сечение 40х40 поперёк оси, Y={dy:.1f}, Z={dz:.1f}")
        text = (product / "_Экспорт.txt").read_text(encoding="utf-8-sig")
        self.assertNotIn("не убрана", text, "временная СК убрана")
        self.s.close_all()
        part_doc = self.s.open(part)
        self.assertEqual(configs[0], str(part_doc.GetActiveConfiguration.Name), "конфигурация детали возвращена")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X14_reexport_retires_previous_dxf_of_same_part(self):
        """X14 (сверка SW API 23.09.2026): в имени DXF — количество и рамка. Изделие выгрузили, потом поставили вторую
        такую же пластину и выгрузили снова: в «Лазер_Лист» одна развёртка пластины — «…_2шт_…», прежняя «…_1шт_…»
        убрана в «_Аннулировано», в отчёте о ней замечание. Раньше обе лежали рядом, и цех мог резать по старой. Так же
        меняются имена прежних выгрузок и от нового округления рамки — вверх."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.240/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        models.mkdir(parents=True)
        part = models / "ПРТИ.468211.241 Пластина.sldprt"
        doc = build.sheet_metal_plate(self.s, 200, 100, 3, "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97")
        self.s.save_as(doc, part)
        asm_path = models / "ПРТИ.468211.240 СБ Опора.sldasm"
        laser = models.parent / "03_ЧПУ" / "Лазер_Лист"
        for count in (1, 2):
            asm, _ = build.assembly(self.s, [(part, 0, 0.3 * i, 0) for i in range(count)])
            self.s.save_as(asm, asm_path)
            self.s.close_all()
            doc = self.s.open(asm_path)
            self.s.activate(doc)
            status = self._export()
            self.assertTrue(status.startswith("ok|"), status)
            self.assertEqual([f"ПРТИ.468211.241 Пластина_S3мм_{count}шт_100х200.dxf"], sorted(p.name for p in laser.glob("*.dxf")),
                             f"в «Лазер_Лист» одна развёртка пластины, {count} шт")
            self.s.close_all()
        archived = sorted(p.name for p in (laser / "_Аннулировано").glob("*.dxf"))
        self.assertEqual(1, len(archived), archived)
        self.assertTrue(archived[0].startswith("ПРТИ.468211.241 Пластина_S3мм_1шт_100х200_"), archived)
        text = (models.parent / "_Экспорт.txt").read_text(encoding="utf-8-sig")
        self.assertIn("прежняя развёртка «ПРТИ.468211.241 Пластина_S3мм_1шт_100х200.dxf» убрана в _Аннулировано", text, text)
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def _sheet_and_tube(self, number):
        """Изделие ПРТИ.468211.<number> из листовой пластины и наклонной трубы, как в X05; сборка сохранена и закрыта."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.{number}/01_3D"
        if models.exists():
            shutil.rmtree(models.parent, ignore_errors=True)
        models.mkdir(parents=True)
        sheet = models / f"ПРТИ.468211.{number + 1} Лист опорный.sldprt"
        tube = models / f"ПРТИ.468211.{number + 2} Стойка трубная.sldprt"
        doc = build.sheet_metal_plate(self.s, 200, 100, 3, "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97")
        self.s.save_as(doc, sheet)
        doc = build.structural_tube(self.s, 500, build.tube_profile(), "Труба 30х30х1,5 ГОСТ 8639-82 / 08пс ГОСТ 13663-86",
                                    angle_deg=53.13)
        self.s.save_as(doc, tube)
        asm, _ = build.assembly(self.s, [(sheet, 0, 0, 0), (tube, 0, 0.2, 0)])
        asm_path = models / f"ПРТИ.468211.{number} СБ Рама.sldasm"
        return models.parent, asm, asm_path, sheet, tube

    def test_X16_blocked_folder_does_not_abort_export(self):
        """X16 (сверка SW API 23.09.2026, №29): на месте папок «Лазер_Лист» и «Труборез» лежат файлы — папку не создать.
        Выгрузка доходит до конца: отчёт записан, DXF листа и IGS трубы — в «Пропущено» с причиной, активна снова
        сборка. Раньше исключение уходило во внешний обработчик — «Выгрузка не выполнена», отчёт не писался, а уже
        сделанные PDF лежали без него."""
        product, asm, asm_path, sheet, tube = self._sheet_and_tube(250)
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        cnc = product / "03_ЧПУ"
        cnc.mkdir(parents=True, exist_ok=True)
        (cnc / "Лазер_Лист").write_text("не папка", encoding="utf-8")
        (cnc / "Труборез").write_text("не папка", encoding="utf-8")
        doc = self.s.open(asm_path)
        self.s.activate(doc)

        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        # Имя файла в отчёте — как его отдаёт SolidWorks (расширение бывает заглавным): сверка без учёта регистра.
        text = (product / "_Экспорт.txt").read_text(encoding="utf-8-sig")
        self.assertIn((sheet.name + " — DXF не сделан").lower(), text.lower(), text)
        self.assertIn((tube.name + " — IGS не сделан").lower(), text.lower(), text)
        active = com.dyn(self.s.sw.ActiveDoc)
        self.assertEqual(str(asm_path).lower(), str(active.GetPathName).lower(), "активна снова сборка")

    def test_X19_export_closes_windows_it_opened(self):
        """X19 (сверка SW API 23.09.2026, №15): развёртку и ось трубы SolidWorks делает только в активном окне, и выгрузка
        открывала окно каждой детали — после изделия с десятками деталей у конструктора оставались десятки окон.
        Теперь окна, которые открыла сама выгрузка, закрываются (модель остаётся загруженной в сборке); окно, открытое
        конструктором, остаётся."""
        from eskd_e2e import build
        product, asm, asm_path, sheet, tube = self._sheet_and_tube(260)
        own = sheet.parent / "ПРТИ.468211.263 Лист верхний.sldprt"
        doc = build.sheet_metal_plate(self.s, 150, 90, 3, "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97")
        self.s.save_as(doc, own)
        asm.AddComponent5(str(own), 0, "", False, "", 0, 0.4, 0)
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        self.s.open(own)
        doc = self.s.open(asm_path)
        self.s.activate(doc)

        def visible(path):
            model = self.s.sw.GetOpenDocumentByName(str(path))
            self.assertIsNotNone(model, f"модель загружена: {path.name}")
            return bool(com.dyn(model).Visible)

        self.assertFalse(visible(sheet), "до выгрузки у листа окна нет")
        self.assertFalse(visible(tube), "до выгрузки у трубы окна нет")
        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        self.assertFalse(visible(sheet), "окно листа, открытое выгрузкой, закрыто")
        self.assertFalse(visible(tube), "окно трубы, открытое выгрузкой, закрыто")
        self.assertTrue(visible(own), "окно конструктора осталось")
        active = com.dyn(self.s.sw.ActiveDoc)
        self.assertEqual(str(asm_path).lower(), str(active.GetPathName).lower(), "активна снова сборка")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X20_drawings_open_without_window(self):
        """X20 (сверка SW API 23.09.2026, №28, шаг 2): выгрузка открывает чертежи без окна, как «Формат» чертежа. Раньше
        каждый чертёж изделия открывался в своём окне и становился активным — у изделия с десятками чертежей окна мелькали,
        и конструктор, вернувшийся в SolidWorks, мог застать чужое окно. PDF те же: страница на каждый лист, текст
        основной надписи на месте. Видимость новых чертежей после выгрузки прежняя."""
        import fitz

        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        mark = self.mark("x20")
        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        activated = [e["path"] for e in self.s.journal.of("ActiveDocChangeNotify", mark)
                     if str(e.get("path", "")).lower().endswith(".slddrw")]
        self.assertEqual([], activated, "чертёж становился активным окном")
        self.assertTrue(bool(com.call(self.s.sw, "GetDocumentVisible", 3)), "видимость новых чертежей возвращена")
        pdfs = sorted((product / "02_PDF").glob("*.pdf"))
        self.assertTrue(pdfs, "PDF чертежей")
        for pdf in pdfs:
            with fitz.open(pdf) as book:
                self.assertGreater(book.page_count, 0, pdf.name)
                for page in book:
                    self.assertTrue(page.get_text().strip(), f"{pdf.name}: на странице есть текст")
                    self.assertGreater(len(page.get_drawings()), 20, f"{pdf.name}: на странице есть графика")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_X17_unloaded_component_is_named_in_report(self):
        """X17 (сверка SW API 23.09.2026, №20): сборка открыта без скрытых компонентов — у скрытой пластины нет модели в
        памяти. Выгрузка называет её в «Пропущено»: «модель не загружена». Раньше деталь молча выпадала — ни в
        «Выгружено», ни в «Пропущено», а окно говорило «выгружено всё»."""
        product, asm, asm_path, sheet, tube = self._sheet_and_tube(260)
        plate = next(c for c in com.as_list(asm.GetComponents(True))
                     if str(com.dyn(c).GetPathName or "").lower() == str(sheet).lower())
        com.dyn(plate).Visible = 0  # swComponentHidden
        asm.ForceRebuild3(False)
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        doc = self.s.open(asm_path, extra_options=com.OPEN_DONT_LOAD_HIDDEN)
        self.s.activate(doc)
        doc.ResolveAllLightWeightComponents(False)
        plate = next(c for c in com.as_list(doc.GetComponents(True))
                     if str(com.dyn(c).GetPathName or "").lower() == str(sheet).lower())
        # SolidWorks 2025 отдаёт такой компонент погашенным (GetSuppression2 = 0, IsSuppressed), но не загруженным
        # (IsLoaded = false); у погашенного IsLoaded = true (проба 23.09.2026). Раньше тест принимал его за погашенный
        # и пропускался, а выгрузка так же молча его отбрасывала.
        if com.dyn(plate).GetModelDoc2 is not None or bool(com.call(plate, "IsLoaded")):
            self.skipTest("SolidWorks загрузил скрытый компонент — состояние не воспроизводится")

        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        text = (product / "_Экспорт.txt").read_text(encoding="utf-8-sig")
        self.assertIn((sheet.name + " — модель не загружена").lower(), text.lower(), text)
        self.assertIn(tube.stem + ".igs", text, "труба выгружена")
        # Проверка изделия тем же путём: незагруженная деталь — замечание, а не молчаливый пропуск.
        com.call(self.s.eskd(), "CheckProductSilent")
        deadline = time.time() + TIMEOUT
        while time.time() < deadline and not str(com.call(self.s.eskd(), "CheckStatus")):
            time.sleep(1)
        report = (product / "_Проверка.txt").read_text(encoding="utf-8-sig")
        self.assertRegex(report.lower(), re.escape(sheet.name.lower()) + r".*модель не загрузилась", report)

    def test_X18_lightweight_assembly_exports_every_part(self):
        """X18 (сверка SW API 23.09.2026, №20, регрессия): сборка фикстуры A открыта облегчённой — выгрузка сама разрешает
        компоненты: строки «модель не загружена» нет, PDF пластины сделан."""
        product, asm = self._product()
        doc = self.s.open(asm, lightweight=True)
        self.s.activate(doc)
        status = self._export()
        self.assertTrue(status.startswith("ok|"), status)
        text = (product / "_Экспорт.txt").read_text(encoding="utf-8-sig")
        self.assertNotIn("модель не загружена", text, text)
        pdfs = [p.name for p in (product / "02_PDF").glob("*.pdf")]
        self.assertTrue(any(n.startswith("ПРТИ.468211.101 ") for n in pdfs), pdfs)

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
