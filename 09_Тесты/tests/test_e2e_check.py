# -*- coding: utf-8 -*-
"""E2E, группа C — кнопка «Проверить изделие» (ТЗ-02 Т-32…Т-34): состав, реквизиты, отчёт и итог."""
import shutil
import time
import unittest
from pathlib import Path

from eskd_e2e import com, paths
from eskd_e2e.testing import SwTestCase, tags

PRODUCT = "И01_ПРТИ.468211.100"
ASM = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
SHEET_PART = "ПРТИ.468211.101 Пластина опорная.sldprt"
TIMEOUT = 600


class Check(SwTestCase):

    def _product(self):
        """Копия изделия в структуре заказа: _Заявки\\<заказ>\\02_Металл\\И01_…\\01_3D.

        Каталог теста назван коротко (K01, K02…): имена фикстур длинные, а Windows отказывает
        в копировании, когда путь переваливает за 260 знаков.
        """
        short = self._case_name().split("_")[1]
        subdir = f"{short}/_Заявки/2026-001/02_Металл/{PRODUCT}/01_3D"
        models = self.s.run_dir / subdir
        # Каталог теста — целиком: отчёт прежнего прогона в папке изделия (_Проверка.txt) иначе остаётся, и K05
        # «проверки ещё не было» падает при повторном прогоне в том же ESKD_RUN_DIR.
        if (self.s.run_dir / short).exists():
            shutil.rmtree(self.s.run_dir / short, ignore_errors=True)
        for src in sorted(Path(paths.FIXTURES_A).iterdir()):
            if src.suffix.lower() in (".sldprt", ".sldasm"):
                self.s.workspace_copy(src, subdir=subdir)
        return models.parent, models / ASM

    def _check(self):
        com.call(self.s.eskd(), "CheckProductSilent")
        deadline = time.time() + TIMEOUT
        status = ""
        while time.time() < deadline:
            status = str(com.call(self.s.eskd(), "CheckStatus"))
            if status:
                break
            time.sleep(1)
        return status

    def _export(self):
        com.call(self.s.eskd(), "ExportProductSilent")
        deadline = time.time() + 900
        status = ""
        while time.time() < deadline:
            status = str(com.call(self.s.eskd(), "ExportStatus"))
            if status:
                break
            time.sleep(1)
        return status

    def test_K10_multibody_tube_part_is_flagged_until_resolved(self):
        """K10 (решение владельца 24.09.2026, З-51): многотельная деталь в IGS не идёт. С «Лазерная резка трубы» ведомость
        ЛЗК обещает цеху IGS, которого нет, — проверка изделия ставит замечание правила «е» по отчёту выгрузки. Сама
        проверка тел не считает и исполнений не переключает (Т-6). Конструктор разбивает деталь на однотельные или
        снимает резку трубы — после новой выгрузки замечания нет, выгрузка только предупреждает, не советуя поставить
        резку трубы."""
        from eskd_e2e import build
        product, asm = self._product()
        frame_name = "ПРТИ.468211.105 Рама сварная.sldprt"
        doc = self.s.open(asm)
        self.s.activate(doc)
        export = self._export()
        self.assertTrue(export.startswith("ok|"), export)
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        text = (product / "_Проверка.txt").read_text(encoding="utf-8-sig")
        # Имя документа в отчётах — как у файла модели: после сохранения SolidWorks пишет «.SLDPRT».
        flagged = [ln.strip().lower() for ln in text.splitlines() if "многотельная" in ln]
        self.assertEqual(1, len(flagged), "\n".join(flagged) or text)
        self.assertIn(("ЗАМЕЧАНИЕ — " + frame_name + " — многотельная деталь в IGS не идёт (тел 3)").lower(), flagged[0])
        self.assertIn("ЗАМЕЧАНИЯ", status, "замечание не даёт «Готово к производству»")

        # Рама загружена сборкой — конструктор снимает резку трубы в ней самой и сохраняет.
        frame = com.dyn(self.s.sw.GetOpenDocumentByName(str(asm.parent / frame_name)))
        build.props(frame, {"Операции": "Покраска"})
        self.assertTrue(self.s.save(frame)[0], "рама сохранена")
        self.s.activate(doc)
        export = self._export()
        self.assertTrue(export.startswith("ok|"), export)
        report = (product / "_Экспорт.txt").read_text(encoding="utf-8-sig").lower()
        notes = [ln.strip() for ln in report.splitlines() if ln.strip().startswith(frame_name.lower())]
        self.assertEqual([(frame_name + " — многотельная деталь из трубы (тел 3), «Лазерная резка трубы» в «Операциях» нет: "
                           "IGS не делается — проверьте чертёж и сборку").lower()],
                         [n for n in notes if "igs" in n], "предупреждение без совета поставить резку трубы")
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        text = (product / "_Проверка.txt").read_text(encoding="utf-8-sig")
        self.assertEqual([], [ln.strip() for ln in text.splitlines() if "многотельная" in ln], "замечания нет")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    @tags("smoke")
    def test_K01_report_lists_issues_and_outcome(self):
        """K01: проверка изделия без чертежей и выгрузки — «ЗАМЕЧАНИЯ», отчёт _Проверка.txt с правилами и суммами."""
        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        _, outcome, defects, issues, report_path = status.split("|")
        self.assertEqual("ЗАМЕЧАНИЯ", outcome, f"итог: {status}")
        self.assertEqual("0", defects, "брака нет: все компоненты на месте")
        self.assertGreater(int(issues), 0, "замечания есть: нет чертежей и выгрузки")

        report = product / "_Проверка.txt"
        self.assertEqual(str(report).lower(), report_path.lower(), "отчёт в папке изделия")
        text = report.read_text(encoding="utf-8-sig")
        self.assertIn("Итог:     ЗАМЕЧАНИЯ", text)
        self.assertIn("ЗАМЕЧАНИЕ — ", text, "строки замечаний")
        self.assertIn("нет книги ЛЗК изделия", text, "книга ЛЗК ещё не сформирована")
        self.assertIn("не выгружено для производства", text, "экспорта нет")
        self.assertIn("Контрольные суммы (SHA-256):", text)
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_K02_missing_component_is_defect(self):
        """K02: файла компонента нет на диске — итог БРАК."""
        product, asm = self._product()
        # Файла нет ещё до открытия: SolidWorks открывает сборку с потерянным компонентом
        # (диалог не выводится — открытие тихое), а проверка обязана назвать это браком.
        (asm.parent / SHEET_PART).unlink()
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        _, outcome, defects, _, report_path = status.split("|")
        self.assertEqual("БРАК", outcome, f"итог: {status}")
        self.assertGreater(int(defects), 0, "брак посчитан")
        text = Path(report_path).read_text(encoding="utf-8-sig")
        self.assertIn("БРАК — ", text, "строка брака")

    def test_K09_foreign_file_in_issue_folder_is_flagged(self):
        """K09 (замечание владельца 24.09.2026, З-48): в папках выдачи — только выгруженное. Развёртка, которой нет в отчёте
        выгрузки (прежняя развёртка убранной детали, чужой файл), — замечание: «Готово к производству» вписало бы её в
        отчёт выдачи, и цех получил бы её вместе с изделием. PDF листа ЛЗК, выгруженный файл и заметка — не замечание.
        Выданный файл документа, которого в изделии больше нет (выгрузка оставила его с замечанием), — тоже замечание:
        без новой ревизии сборки его выдали бы снова."""
        import hashlib
        product, asm = self._product()
        pdf = product / "02_PDF"
        laser = product / "03_ЧПУ" / "Лазер_Лист"
        pdf.mkdir(parents=True)
        laser.mkdir(parents=True)
        exported = pdf / "ПРТИ.468211.101 Пластина опорная.pdf"
        exported.write_bytes(b"%PDF-1.4 exported")
        orphan = pdf / "ПРТИ.468211.198 Ребро.pdf"
        orphan.write_bytes(b"%PDF-1.4 issued")
        (pdf / "ЛЗК_ПРТИ.468211.100_Расход.pdf").write_bytes(b"%PDF-1.4 lzk")
        (pdf / "Для цеха.txt").write_text("заметка", encoding="utf-8")
        (laser / "ПРТИ.468211.199 Косынка_S3мм_1шт_80х150.dxf").write_text("0\nEOF\n", encoding="ascii")
        (product / "_Экспорт.txt").write_text("\n".join([
            "Выгрузка для производства", "Изделие:  ПРТИ.468211.100", "Файлов:   2, пропущено: 0", "",
            "Выгружено (SHA-256):",
            "  " + hashlib.sha256(exported.read_bytes()).hexdigest() + "  " + exported.name,
            "  " + hashlib.sha256(orphan.read_bytes()).hexdigest() + "  " + orphan.name, "",
            "Замечания:",
            "  " + orphan.name + " — выдан в производство, а его документа в изделии больше нет (убран или переименован " +
            "после выдачи): оформите новую ревизию сборочного чертежа — выгрузка уберёт файл в «_Аннулировано»", ""]),
            encoding="utf-8-sig")
        doc = self.s.open(asm)
        self.s.activate(doc)
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        text = (product / "_Проверка.txt").read_text(encoding="utf-8-sig")
        flagged = [line for line in text.splitlines() if "лежит в папке выдачи" in line]
        self.assertEqual(1, len(flagged), "\n".join(flagged) or text)
        self.assertIn("ПРТИ.468211.199 Косынка_S3мм_1шт_80х150.dxf", flagged[0])
        orphans = [line for line in text.splitlines() if "а его документа в изделии больше нет" in line]
        self.assertEqual(1, len(orphans), "\n".join(orphans) or text)
        self.assertIn(orphan.name, orphans[0])
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_K03_second_run_keeps_previous_report(self):
        """K03: повторная проверка сохраняет прежний отчёт в _Проверка_пред.txt."""
        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        self.assertTrue(self._check().startswith("ok|"))
        first = (product / "_Проверка.txt").read_text(encoding="utf-8-sig")
        self.assertTrue(self._check().startswith("ok|"))
        previous = product / "_Проверка_пред.txt"
        self.assertTrue(previous.is_file(), "прежний отчёт сохранён")
        self.assertEqual(first, previous.read_text(encoding="utf-8-sig"), "прежний отчёт — это первый")

    def test_K05_shows_last_report_without_checking(self):
        """K05: пункт «Отчёт проверки» отдаёт итог прежней проверки, а без отчёта — отказ."""
        product, asm = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        com.call(self.s.eskd(), "ShowCheckReportSilent")
        self.assertTrue(str(com.call(self.s.eskd(), "CheckStatus")).startswith("error|"), "проверки ещё не было")

        self.assertTrue(self._check().startswith("ok|"))
        report = product / "_Проверка.txt"
        stamp = report.stat().st_mtime_ns
        com.call(self.s.eskd(), "ShowCheckReportSilent")
        status = str(com.call(self.s.eskd(), "CheckStatus"))
        _, outcome, _, _, path = status.split("|")
        self.assertEqual("ЗАМЕЧАНИЯ", outcome, status)
        self.assertEqual(str(report).lower(), path.lower(), "путь прежнего отчёта")
        self.assertEqual(stamp, report.stat().st_mtime_ns, "отчёт не переписан: проверка заново не запускалась")

    def test_K07_lightweight_report_changes_nothing(self):
        """K07 (ревью 23.09.2026): сборку открыли с облегчёнными компонентами — так регламент велит открывать большие
        изделия. Проверка без окна и тогда не помечает сборку изменённой и проверяет все детали."""
        product, asm = self._product()
        doc = self.s.open(asm, lightweight=True)
        self.s.activate(doc)
        self.assertGreater(int(com.dyn(doc).GetLightWeightComponentCount), 0, "компоненты облегчённые")
        self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "сборка открыта без изменений")
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "проверка не пометила сборку изменённой")
        report = (product / "_Проверка.txt").read_text(encoding="utf-8-sig")
        self.path("report.txt").write_text(report, encoding="utf-8")
        for name in ("ПРТИ.468211.101 Пластина опорная", "ПРТИ.468211.105 Рама сварная"):
            self.assertIn(name, report, "облегчённая деталь проверена")
        self.assertNotIn("модель не загрузилась", report, "облегчённые компоненты прочитаны, а не пропущены")
        self.assertNotIn("облегч", report.lower(), "облегчённые компоненты прочитаны, а не пропущены")
        self.s.close_all()

    def test_K06_report_changes_nothing(self):
        """K06 (аудит 23.09.2026): проверка без окна ничего не меняет — ни файлов, ни открытых документов. Раньше она
        перестраивала сборку (ForceRebuild3), и SolidWorks после проверки просил сохранить сборку и её детали."""
        product, asm = self._product()
        stamps = {p: p.stat().st_mtime_ns for p in asm.parent.iterdir() if p.suffix.lower() in (".sldprt", ".sldasm")}
        doc = self.s.open(asm)
        self.s.activate(doc)
        self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "сборка открыта без изменений")
        status = self._check()
        self.assertTrue(status.startswith("ok|"), status)
        self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "проверка не пометила сборку изменённой")
        self.assertEqual("", str(com.call(self.s.eskd(), "CheckApplied")), "ничего не записано")
        self.s.close_all()
        self.assertEqual(stamps, {p: p.stat().st_mtime_ns for p in stamps}, "файлы изделия не переписаны")

    def test_K08_every_execution_in_product_needs_material(self):
        """K08 (сверка SW API 23.09.2026, находка 14): у пластины два исполнения — «00» с материалом и «01» без него.
        Если в изделии стоят оба, проверка сверяет каждое, а не только активное в файле: у «01» — брак «материал не
        назначен». Если стоит только «00», про «01» проверка молчит: сверяются исполнения изделия. Исполнения не
        переключаются — проверка не помечает ни деталь, ни сборку изменёнными."""
        from eskd_e2e import build
        short = self._case_name().split("_")[1]
        models = self.s.run_dir / f"{short}/_Заявки/2026-001/02_Металл/И01_ПРТИ.468211.210/01_3D"
        if (self.s.run_dir / short).exists():
            shutil.rmtree(self.s.run_dir / short, ignore_errors=True)
        models.mkdir(parents=True)
        doc, _ = build.plate(self.s, 150, 80, 4, None)
        active = str(doc.GetActiveConfiguration.Name)
        build.add_configuration(doc, "01")
        build.show_configuration(doc, active)
        build.set_material(doc, "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", active)
        self.assertEqual("", build.material_of(doc, "01")[0], "у «01» материала нет")
        part = models / "ПРТИ.468211.211 Пластина.sldprt"
        self.s.save_as(doc, part)
        self.s.wait_addin_idle(timeout=60.0)
        self.s.close_all()
        for configs, expected in (([active, "01"], True), ([active], False)):
            asm, _ = build.assembly(self.s, [(part, 0, 0.2 * i, 0) for i in range(len(configs))])
            for comp, cfg in zip(com.as_list(asm.GetComponents(True)), configs):
                com.dyn(comp).ReferencedConfiguration = cfg
            asm.ForceRebuild3(False)
            asm_path = models / "ПРТИ.468211.210 СБ Опора.sldasm"
            self.s.save_as(asm, asm_path)
            self.s.close_all()
            doc = self.s.open(asm_path)
            self.s.activate(doc)
            part_doc = com.call(self.s.sw, "GetOpenDocumentByName", str(part))
            self.assertIsNotNone(part_doc, "деталь открыта со сборкой")
            # Сборку, где деталь стоит не в активном исполнении файла, SolidWorks при открытии перестраивает и помечает
            # изменённой — это не проверка. Такие документы сохраняются до неё: дальше флаг ставит только проверка.
            for opened in (doc, part_doc):
                if bool(com.dyn(opened).GetSaveFlag):
                    self.s.save(com.dyn(opened))
                    self.s.wait_addin_idle(timeout=60.0)
                self.assertFalse(bool(com.dyn(opened).GetSaveFlag), "документ без изменений перед проверкой")
            status = self._check()
            self.assertTrue(status.startswith("ok|"), status)
            text = (models.parent / "_Проверка.txt").read_text(encoding="utf-8-sig")
            lines = [line for line in text.splitlines() if "исполнение «01»" in line]
            if expected:
                self.assertTrue(any(line.lstrip().startswith("БРАК") and "ПРТИ.468211.211" in line and "материал не назначен" in line
                                    for line in lines), f"брак у «01», стоящего в изделии:\n{text}")
            else:
                self.assertEqual([], lines, f"«01» в изделии нет — про него ни слова:\n{text}")
            self.assertFalse(bool(com.dyn(doc).GetSaveFlag), "сборка не помечена изменённой")
            self.assertFalse(bool(com.dyn(part_doc).GetSaveFlag), "деталь не помечена изменённой: исполнения не переключались")
            self.s.close_all()
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_K04_refuses_part_and_keeps_files(self):
        """K04: у детали кнопка недоступна и проверка отказывает без изменений файлов."""
        path, doc = self.open_copy(SHEET_PART)
        self.s.activate(doc)
        self.assertEqual(0, int(com.call(self.s.eskd(), "EnableCheckCommand")), "кнопка серая у детали")
        self.assertTrue(self._check().startswith("error|"), "проверка отказала")
        self.assertNoPropertyWrites()


if __name__ == "__main__":
    unittest.main()
