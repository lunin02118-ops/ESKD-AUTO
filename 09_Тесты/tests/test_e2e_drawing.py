# -*- coding: utf-8 -*-
"""E2E, группа D — чертежи: штамп по ГОСТ Р 2.104-2023, нулевое смещение, чертёж не меняет модель."""
import re
import sys
import unittest

from eskd_e2e import build, com, oracles, paths
from eskd_e2e.testing import SwTestCase, known_defect, tags

A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A10 = "ПРТИ.468211.101 Пластина опорная.slddrw"
A09 = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
A09_COMPONENTS = ("ПРТИ.468211.110 СБ Узел опоры.sldasm", A01, "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt",
                  "ПРТИ.468211.102 Стойка.sldprt", "ПРТИ.468211.103 Планка.sldprt", "Электродвигатель АИР71А4.sldprt",
                  "ПРТИ.468211.104 Кронштейн направляющий удлинённый.sldprt", "ПРТИ.468211.105 Рама сварная.sldprt")
A11 = "ПРТИ.468211.100 СБ Кондуктор сварочный.slddrw"
A03 = "ПРТИ.468211.103 Планка.sldprt"


def plain(note_text):
    """Текст заметки штампа без разметки SolidWorks (<FONT …>) и переводов строк — как его читает человек."""
    return " ".join(re.sub(r"<[^>]*>", " ", note_text or "").split())


class Drawing(SwTestCase):

    def _prepare_model(self):
        """Модель сохраняется с надстройкой — так реквизиты попадают в файл перед открытием чертежа."""
        model = self.copy_fixture(A01)
        doc = self.s.open(model)
        self.s.save(doc)
        self.s.close(doc)
        return model

    @tags("smoke")
    def test_D01_stamp_sheet1_texts_and_cells(self):
        """D01: штамп листа 1 (форма 1): тексты граф и положение каждой строки внутри своей графы."""
        self._prepare_model()
        drawing = self.copy_fixture(A10)
        drw = self.s.open(drawing)
        notes = oracles.stamp(drw)
        sheet = next(iter(notes))
        stamp = notes[sheet]
        text = {k: v["text"].replace("\r\n", "\n") for k, v in stamp.items()}
        self.assertEqual("ПРТИ.468211.101", text.get("MYPRP0"), "графа 2 — обозначение")
        self.assertEqual("Пластина опорная", plain(text.get("MYPRP4")), "графа 1 — наименование")
        self.assertEqual("Тестов Т.Т.", text.get("MYPRP8"), "разработал")
        self.assertEqual("ООО «Испытание»", text.get("MYPRP7"), "графа 9 — организация")
        self.assertIn("0.63", text.get("MYPRP15", ""), "графа 5 — масса (точка, как у MProp, Р-1)")
        self.assertIn("Лист", text.get("MYPRP16", ""), "графа 3 — материал")
        cells = oracles.form1_cells(420)
        for note, cell in (("MYPRP0", "g2_designation"), ("MYPRP4", "g1_title"), ("MYPRP16", "g3_material"),
                           ("MYPRP15", "g5_mass"), ("MYPRP7", "g9_firm")):
            with self.subTest(note=note):
                self.assertTrue(oracles.inside(stamp[note]["extent_mm"], cells[cell], tol=1.0),
                                f"{note} {stamp[note]['extent_mm']} вне графы {cell} {cells[cell]}")
        self.s.close(drw)

    def test_D02_sheet2_form2a_designation_and_sheet_numbers(self):
        """D02: лист 2 (форма 2а) — обозначение в графе 2, номер листа «2» в графе 7; на листе 1 — заметки «Sheet1» и
        «Sheet2» в графах 7 и 8: в них DProp пишет «Лист 1» и «Листов N» (ГОСТ Р 2.104-2023 не заполняет номер у однолистового
        документа, поэтому это не системное свойство)."""
        self._prepare_model()
        drw = self.s.open(self.copy_fixture(A10))
        notes = oracles.stamp(drw)
        self.assertEqual(2, len(notes), f"листы A-10: {list(notes)}")
        first, second = (notes[name] for name in notes)
        texts = {k: v["text"].replace("\r\n", "\n") for k, v in second.items()}
        cells = oracles.form2a_cells(210)
        self.assertEqual("ПРТИ.468211.101", texts.get("MYPRP0"), "графа 2 листа 2 — обозначение")
        self.assertTrue(oracles.inside(second["MYPRP0"]["extent_mm"], cells["g2_designation"], tol=1.0),
                        f"обозначение {second['MYPRP0']['extent_mm']} вне графы 2 {cells['g2_designation']}")
        sheet_no = [k for k, v in second.items() if "Current Sheet" in v["linked"]]
        self.assertEqual(1, len(sheet_no), "заметка номера листа на форме 2а")
        self.assertEqual("2", texts[sheet_no[0]].strip(), f"графа 7 листа 2: {second[sheet_no[0]]}")
        self.assertTrue(oracles.inside(second[sheet_no[0]]["extent_mm"], cells["g7_sheet"], tol=1.0),
                        f"номер листа {second[sheet_no[0]]['extent_mm']} вне графы 7 {cells['g7_sheet']}")
        form1 = oracles.form1_cells(420)
        for note, cell in (("Sheet1", "g7_sheet"), ("Sheet2", "g8_sheets")):
            with self.subTest(note=note):
                self.assertIn(note, first, f"на форме 1 нет заметки {note}, которую заполняет DProp")
                self.assertTrue(oracles.inside(first[note]["extent_mm"], form1[cell], tol=1.0),
                                f"{note} {first[note]['extent_mm']} вне графы {cell} {form1[cell]}")
        self.s.close(drw)

    def test_D03_assembly_drawing_code_and_second_title_line(self):
        """D03: сборочный чертёж (A-11, форма 1 на А2) — графа 2 «ПРТИ.468211.100СБ», графа 1 — наименование и второй
        строкой «Сборочный чертёж»; тексты в своих графах."""
        for component in A09_COMPONENTS:
            self.copy_fixture(component)
        assembly = self.copy_fixture(A09)
        doc = self.s.open(assembly)
        with self.s.eskd_muted():
            build.props(doc, {"Сборка1_ФБ": "СБ"}, str(doc.GetActiveConfiguration.Name))  # как в шаблоне сборки и у MProp
        self.s.save(doc)
        self.s.close_all()
        drw = self.s.open(self.copy_fixture(A11))
        stamp = next(iter(oracles.stamp(drw).values()))
        text = {k: v["text"].replace("\r\n", "\n") for k, v in stamp.items()}
        self.assertEqual("ПРТИ.468211.100СБ", plain(text.get("MYPRP0")), "графа 2 — обозначение с кодом документа слитно, как у MProp и SpecEditor (Р-3)")
        self.assertEqual("Кондуктор сварочный", plain(text.get("MYPRP4")), "графа 1 — наименование")
        self.assertIn(plain(text.get("MYPRP3")), ("Сборочный чертёж", "Сборочный чертеж"), "графа 1 — вторая строка")
        cells = oracles.form1_cells(594)
        for note, cell in (("MYPRP0", "g2_designation"), ("MYPRP4", "g1_title"), ("MYPRP3", "g1_title")):
            with self.subTest(note=note):
                self.assertTrue(oracles.inside(stamp[note]["extent_mm"], cells[cell], tol=1.0),
                                f"{note} {stamp[note]['extent_mm']} вне графы {cell} {cells[cell]}")
        self.s.close(drw)

    @known_defect("Д-36")
    def test_D11_execution_drawing_shows_mass_of_its_configuration(self):
        """D11: чертёж исполнения «01» детали A-03 — в графе 5 PDF масса именно этой конфигурации (0.19, Р-1)."""
        model_path = self.copy_fixture(A03)
        doc = self.s.open(model_path)
        self.s.save(doc)
        self.s.close(doc)
        model = self.s.open(model_path)
        drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
        build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
        view = build.model_view(drw, model_path, 150, 180)
        view.ReferencedConfiguration = "01"
        drw.ForceRebuild3(False)
        build.wait(1.0)
        pdf = self.path("ПРТИ.468211.103-01 Планка.pdf")
        ok, err, _ = self.s.save_as(drw, pdf)
        self.s.close(drw)
        self.s.close(model)
        self.assertTrue(ok and pdf.exists(), f"PDF не выгружен, код {err}")
        self.assertEqual("ПРТИ.468211.103-01", oracles.pdf_cell_text(pdf, 420, oracles.form1_cells(420)["g2_designation"]),
                         "лист показывает исполнение 01")
        self.assertEqual("0.19", oracles.pdf_cell_text(pdf, 420, oracles.form1_cells(420)["g5_mass"]), "графа 5 — масса исполнения 01")

    def test_D04_zero_drift_on_drawing_save(self):
        """D04: сохранение чертежа не смещает заметки."""
        self._prepare_model()
        drawing = self.copy_fixture(A10)
        drw = self.s.open(drawing)
        before = oracles.note_positions(drw)
        self.s.save(drw)
        worst, moved = oracles.max_drift(before, oracles.note_positions(drw))
        self.assertLess(worst, 0.001, f"смещены заметки: {moved}")
        self.s.close(drw)

    def test_D05_stamp_filled_without_addin(self):
        """D05: штамп заполнен и при выключенной службе надстройки — реквизиты лежат в файле модели."""
        self._prepare_model()
        drawing = self.copy_fixture(A10)
        with self.s.eskd_muted():
            drw = self.s.open(drawing)
            text = {k: v["text"] for k, v in next(iter(oracles.stamp(drw).values())).items()}
            self.s.close(drw)
        self.assertEqual("ПРТИ.468211.101", text.get("MYPRP0"))
        self.assertEqual("Пластина опорная", plain(text.get("MYPRP4")))

    @known_defect("Д-06")
    def test_D06_drawing_save_does_not_modify_model(self):
        """D06: сохранение чертежа не пишет в модель ничего, кроме формата своих листов для спецификации (З-1, D13)."""
        model = self._prepare_model()
        before = self.persisted(model)
        drawing = self.copy_fixture(A10)
        drw = self.s.open(drawing)
        self.s.save(drw)
        self.wait_idle()
        self.s.close(drw)
        self.s.close_all()
        self.assertEqual(without_format(before), without_format(self.persisted(model)))

    def test_D13_drawing_save_fills_model_format_for_specification(self):
        """D13 (З-1): сохранение чертежа пишет формат его листов в «Формат» модели — графу «Формат» спецификации:
        у «Пластины» лист 1 — А3, лист 2 — А4, поэтому «*)» и «*) А4, А3» в «Примечании» (ГОСТ Р 2.106-2019, как SpecEditor);
        модель сохранена и не осталась «грязной», повторное сохранение чертежа ничего не пишет."""
        model = self._prepare_model()
        doc = self.s.open(model)
        for cfg in ("",) + tuple(com.as_list(doc.GetConfigurationNames)):
            com.dyn(doc.Extension.CustomPropertyManager(cfg)).Add3("Формат", 30, "A3", 1)
        self.s.save(doc)
        drw = self.s.open(self.copy_fixture(A10))
        sizes = [tuple(round(float(v) * 1000) for v in list(com.dyn(drw.Sheet(n)).GetProperties2)[5:7])
                 for n in com.as_list(drw.GetSheetNames)]
        self.assertEqual([(420, 297), (210, 297)], sizes, "листы фикстуры: А3 и А4")
        self.s.save(drw)
        self.wait_idle()
        self.assertFalse(bool(doc.GetSaveFlag), "модель сохранена после записи формата")
        mark = self.mark("D13-second-save")
        self.s.save(drw)
        self.wait_idle()
        self.assertNoPropertyWrites(mark, "повторное сохранение чертежа снова пишет в модель")
        self.s.close_all()
        props = self.persisted(model)
        levels = list(props["configs"]) + ([None] if len(props["configs"]) <= 1 else [])
        self.assertEqual({c: ("*)", "*) А4, А3") for c in levels},
                         {c: (oracles.value(props, "Формат", c), oracles.value(props, "Примечание", c)) for c in levels})

    def _close_now(self, doc):
        """Закрыть документ сразу, не дожидаясь простоя надстройки, — так закрывает чертёж конструктор после Ctrl+S."""
        title = doc.GetTitle
        if self.s.probe is not None:
            try:
                self.s.probe.call("detach", title)
            except Exception:
                pass
        self.s.sw.CloseDoc(title)
        self.s._opened[:] = [d for d in self.s._opened if d is not doc]

    def _formats(self, path):
        props = self.persisted(path)
        levels = list(props["configs"]) + ([None] if len(props["configs"]) <= 1 else [])
        return {c: oracles.value(props, "Формат", c) for c in levels}

    def test_D14_drawing_saved_and_closed_at_once_still_fills_model_format(self):
        """D14 (замечание владельца 21.09.2026 «формат не попадает в спецификацию»): чертёж сохранили и сразу закрыли.
        Раньше запись формата ждала простоя вместе с чертежом и отменялась его закрытием; теперь листы читаются при
        сохранении, а модель — выгруженная вместе с чертежом — открывается скрыто, записывается и закрывается."""
        model = self._prepare_model()
        doc = self.s.open(model)
        for cfg in ("",) + tuple(com.as_list(doc.GetConfigurationNames)):
            build.props(doc, {"Формат": "A3"}, cfg)
        self.s.save(doc)
        self.s.close(doc)
        drw = self.s.open(self.copy_fixture(A10))
        # «Сохранить?» → «Да» при закрытии: сохранение и закрытие идут подряд, SolidWorks между ними не простаивает.
        # Пока «идёт команда», простоя (OnIdleNotify) нет — так это и воспроизводится извне.
        self.s.sw.CommandInProgress = True
        try:
            self.s.save(drw)
            self._close_now(drw)
        finally:
            self.s.sw.CommandInProgress = False
        self.assertTrue(self.s.wait_addin_idle(timeout=60.0), "надстройка записала формат в простое")
        self.s.close_all()
        formats = self._formats(model)
        self.assertEqual({c: "*)" for c in formats}, formats, "«Формат» модели — из листов А3 и А4 закрытого чертежа")

    def test_D15_product_sync_backfills_empty_format_from_drawings(self):
        """D15: чертежи, сохранённые до исправления, формат в модель не записали. «Синхронизировать» на сборке
        дозаполняет пустой «Формат» деталей и сборки по чертежу рядом (тот же путь, .slddrw)."""
        for name in (A09,) + A09_COMPONENTS + (A10, A11):
            self.copy_fixture(name)
        plate, asm_path = self.path(A01), self.path(A09)
        with self.s.eskd_muted():
            for path in (plate, asm_path):
                doc = self.s.open(path)
                for cfg in ("",) + tuple(com.as_list(doc.GetConfigurationNames)):
                    build.props(doc, {"Формат": ""}, cfg)
                self.s.save(doc)
                self.s.close(doc)
        asm = self.s.open(asm_path)
        self.s.activate(asm)
        status = str(com.call(self.s.eskd(), "SyncProductSilent") or "")
        self.assertIn("формат из чертежа 2", status, status)
        self.s.save(asm)
        self.s.close_all()
        formats = self._formats(plate)
        self.assertEqual({c: "*)" for c in formats}, formats, "деталь: формат из её чертежа (А3 + А4)")
        formats = self._formats(asm_path)
        self.assertEqual({c: "А2" for c in formats}, formats, "сборка: формат чертежа СБ")


    def test_D16_flat_pattern_sheet_does_not_block_format(self):
        """D16 (живая проверка NC3-7R 22.09.2026): у листовой детали в чертеже есть лист развёртки Drew «DXF1»
        произвольного размера. Он служебный, как SP/VP, — в «Формат» не входит и не мешает записать формат остальных
        листов. Раньше из-за него формат не писался ни при сохранении чертежа, ни при «Синхронизировать»."""
        for name in (A09,) + A09_COMPONENTS + (A11,):
            self.copy_fixture(name)
        asm_path = self.path(A09)
        with self.s.eskd_muted():
            drw = self.s.open(self.path(A11))
            first = str(com.dyn(drw.GetCurrentSheet).GetName)
            # лист развёртки Drew — пользовательского размера и без форматки (12 — свой размер, 13 — без шаблона)
            self.assertTrue(drw.NewSheet3("DXF1", 12, 13, 1.0, 1.0, True, "", 0.0814, 0.0495, ""), "лист DXF1 добавлен")
            drw.ActivateSheet(first)
            self.s.save(drw)
            self.s.close(drw)
            doc = self.s.open(asm_path)
            for cfg in ("",) + tuple(com.as_list(doc.GetConfigurationNames)):
                build.props(doc, {"Формат": ""}, cfg)
            self.s.save(doc)
            self.s.close(doc)
        asm = self.s.open(asm_path)
        self.s.activate(asm)
        status = str(com.call(self.s.eskd(), "SyncProductSilent") or "")
        self.assertIn("формат из чертежа", status, status)
        self.s.save(asm)
        self.s.close_all()
        formats = self._formats(asm_path)
        self.assertEqual({c: "А2" for c in formats}, formats, "«Синхронизировать»: формат листа СБ, лист DXF1 не считается")

        doc = self.s.open(asm_path)
        for cfg in ("",) + tuple(com.as_list(doc.GetConfigurationNames)):
            build.props(doc, {"Формат": "A3"}, cfg)
        self.s.save(doc)
        self.s.close(doc)
        drw = self.s.open(self.path(A11))
        self.s.sw.CommandInProgress = True
        try:
            drw.SetSaveFlag()
            self.s.save(drw)
            self._close_now(drw)
        finally:
            self.s.sw.CommandInProgress = False
        self.assertTrue(self.s.wait_addin_idle(timeout=60.0), "надстройка записала формат в простое")
        self.s.close_all()
        formats = self._formats(asm_path)
        self.assertEqual({c: "А2" for c in formats}, formats, "сохранение чертежа: лист DXF1 не мешает записать А2")


def without_format(dump):
    """Дамп свойств без «Формата» и «Примечания» — их сохранение чертежа пишет по З-1."""
    strip = lambda level: {k: v for k, v in level.items() if k not in ("Формат", "Примечание")}
    return {"general": strip(dump["general"]), "configs": {c: strip(v) for c, v in dump["configs"].items()}}


GOST_2_301 = {"A0": (1189, 841), "A1": (841, 594), "A2": (594, 420), "A3": (420, 297), "A4": (297, 210)}
TOL = 0.2


def template_lines(drw):
    """Отрезки эскиза форматки текущего листа в мм: (x1, y1, x2, y2)."""
    sketch = com.dyn(com.dyn(drw.GetCurrentSheet).GetTemplateSketch)
    lines = []
    for segment in com.as_list(sketch.GetSketchSegments):
        segment = com.dyn(segment)
        if int(segment.GetType) != 0:  # swSketchLINE
            continue
        p1, p2 = com.dyn(segment.GetStartPoint2), com.dyn(segment.GetEndPoint2)
        lines.append(tuple(v * 1000.0 for v in (p1.X, p1.Y, p2.X, p2.Y)))
    return lines


def covered(lines, horizontal, level, start, end):
    """Покрыт ли отрезок [start, end] на прямой y = level (или x = level) коллинеарными отрезками эскиза."""
    spans = []
    for x1, y1, x2, y2 in lines:
        if horizontal and abs(y1 - level) <= TOL and abs(y2 - level) <= TOL:
            spans.append(sorted((x1, x2)))
        if not horizontal and abs(x1 - level) <= TOL and abs(x2 - level) <= TOL:
            spans.append(sorted((y1, y2)))
    reach = start
    for a, b in sorted(spans):
        if a > reach + TOL:
            break
        reach = max(reach, b)
    return reach >= end - TOL


class SheetFormats(SwTestCase):

    @known_defect("Д-30")
    def test_D09_sheet_formats_follow_gost_2_301_and_2_104(self):
        """D09: форматки основных надписей — размер по ГОСТ 2.301, рамка 20/5/5/5, основная надпись формы 1 или 2а
        у правого нижнего угла, у А4 — вдоль короткой стороны, все линии в пределах листа; обозначение, номер листа и
        заметки «Sheet1»/«Sheet2», которые заполняет DProp, — в своих графах."""
        sys.path.insert(0, str(paths.TESTS / "tools"))
        from clean_format_notes import load
        problems = {}
        formats = sorted(paths.SHEET_FORMATS.glob("*.slddrt"))
        self.assertEqual(18, len(formats), "комплект SWPlus: A0–A3 горизонтальные и вертикальные, A4 вертикальные; формы 1 и 2а")
        for fmt in formats:
            m = re.match(r"^(A\d)-([AP])-([12])$", fmt.stem)
            self.assertIsNotNone(m, fmt.name)
            long_side, short_side = GOST_2_301[m.group(1)]
            width, height = (long_side, short_side) if m.group(2) == "A" else (short_side, long_side)
            drw = load(self.s, fmt, (width, height))
            props = com.as_list(com.dyn(drw.GetCurrentSheet).GetProperties2)
            lines = template_lines(drw)
            notes = next(iter(oracles.stamp(drw).values()))
            self.s.close(drw)
            found = []
            # Заметки, по которым пишут DProp («Лист 1», «Листов N») и системное свойство номера листа, — в своих графах.
            if m.group(3) == "1":
                cells = oracles.form1_cells(width)
                expected_notes = (("MYPRP0", "g2_designation"), ("Sheet1", "g7_sheet"), ("Sheet2", "g8_sheets"))
            else:
                cells = oracles.form2a_cells(width)
                current = [k for k, v in notes.items() if "Current Sheet" in v["linked"]]
                expected_notes = (("MYPRP0", "g2_designation"),) + tuple((k, "g7_sheet") for k in current[:1])
                if len(current) != 1:
                    found.append(f"заметок номера листа (Current Sheet): {len(current)}")
            for note, cell in expected_notes:
                if note not in notes:
                    found.append(f"нет заметки {note}")
                # Габарит текста включает надстрочные и подстрочные поля шрифта, а в A0-A-1 и A1-P-1 номер листа
                # набран крупнее, чем в остальных форматках, и выступает за 5-мм графу на 0,99 мм — допуск 1 мм стоял
                # на самой границе и зависел от того, как запущен SolidWorks. Графы отстоят друг от друга не меньше
                # чем на 5 мм, поэтому 1,5 мм по-прежнему ловит заметку не в своей графе.
                elif not oracles.inside(notes[note]["extent_mm"], cells[cell], tol=1.5):
                    found.append(f"{note} {notes[note]['extent_mm']} вне графы {cell}")
            if (round(props[5] * 1000), round(props[6] * 1000)) != (width, height):
                found.append(f"лист {props[5] * 1000:.0f}×{props[6] * 1000:.0f} вместо {width}×{height}")
            right, top = width - 5, height - 5
            frame = [covered(lines, True, 5, 20, right), covered(lines, True, top, 20, right),
                     covered(lines, False, 20, 5, top), covered(lines, False, right, 5, top)]
            if not all(frame):
                found.append("рамка не 20/5/5/5")
            block_top = 60 if m.group(3) == "1" else 20
            if not (covered(lines, True, block_top, right - 185, right) and covered(lines, False, right - 185, 5, block_top)):
                found.append(f"основная надпись 185×{block_top - 5} не у правого нижнего угла")
            outside = [l for l in lines if min(l[0], l[2]) < -TOL or max(l[0], l[2]) > width + TOL
                       or min(l[1], l[3]) < -TOL or max(l[1], l[3]) > height + TOL]
            if outside:
                found.append(f"{len(outside)} линий за пределами листа")
            if m.group(1) == "A4" and m.group(2) == "A":
                found.append("А4 горизонтально: основная надпись вдоль длинной стороны (ГОСТ Р 2.104-2023)")
            if found:
                problems[fmt.name] = found
        self.assertEqual({}, problems)


if __name__ == "__main__":
    unittest.main()
