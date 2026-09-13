# -*- coding: utf-8 -*-
"""E2E, группа D — чертежи: штамп по ГОСТ 2.104, нулевое смещение, чертёж не меняет модель."""
import re
import sys
import unittest

from eskd_e2e import com, oracles, paths
from eskd_e2e.testing import SwTestCase, known_defect, tags

A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A10 = "ПРТИ.468211.101 Пластина опорная.slddrw"


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
        self.assertEqual("Пластина опорная", text.get("MYPRP4"), "графа 1 — наименование")
        self.assertEqual("Тестов Т.Т.", text.get("MYPRP8"), "разработал")
        self.assertEqual("ООО «Испытание»", text.get("MYPRP7"), "графа 8 — организация")
        self.assertIn("0,63", text.get("MYPRP15", ""), "графа 5 — масса")
        self.assertIn("Лист", text.get("MYPRP16", ""), "графа 3 — материал")
        cells = oracles.form1_cells(420)
        for note, cell in (("MYPRP0", "g2_designation"), ("MYPRP4", "g1_title"), ("MYPRP16", "g3_material"),
                           ("MYPRP15", "g5_mass"), ("MYPRP7", "g8_firm")):
            with self.subTest(note=note):
                self.assertTrue(oracles.inside(stamp[note]["extent_mm"], cells[cell], tol=1.0),
                                f"{note} {stamp[note]['extent_mm']} вне графы {cell} {cells[cell]}")
        self.s.close(drw)

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
        self.assertEqual("Пластина опорная", text.get("MYPRP4"))

    @known_defect("Д-06")
    def test_D06_drawing_save_does_not_modify_model(self):
        """D06: сохранение чертежа не пишет в модель."""
        model = self._prepare_model()
        before = self.persisted(model)
        drawing = self.copy_fixture(A10)
        drw = self.s.open(drawing)
        self.s.save(drw)
        self.s.close(drw)
        self.s.close_all()
        self.assertEqual(before, self.persisted(model))


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
        у правого нижнего угла, у А4 — вдоль короткой стороны, все линии в пределах листа."""
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
            self.s.close(drw)
            found = []
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
                found.append("А4 горизонтально: основная надпись вдоль длинной стороны (ГОСТ 2.104, п. 4.2)")
            if found:
                problems[fmt.name] = found
        self.assertEqual({}, problems)


if __name__ == "__main__":
    unittest.main()
