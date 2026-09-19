# -*- coding: utf-8 -*-
"""E2E, группа S — спецификация и формы SpecEditor (план, §4.7). S01–S06 требуют автоматизации SWPlus (WP-2.3)."""
import re
import sys
import unittest
from pathlib import Path

from eskd_e2e import com, oracles, paths
from eskd_e2e.testing import SwTestCase, known_defect

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from clean_format_notes import SERVICE_NOTE, formats  # noqa: E402


class SpecForms(SwTestCase):

    @known_defect("Д-17")
    def test_S07_formats_have_no_file_name_service_note(self):
        """S07: ни одна форматка комплекта (основные надписи и формы SpecEditor) не печатает «Файл: <имя файла>»."""
        found = {}
        for fmt, size in formats():
            drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
            sheet = com.dyn(drw.GetCurrentSheet)
            self.assertTrue(drw.SetupSheet5(str(sheet.GetName), 12, 12, 1.0, 1.0, True, str(fmt), size[0] / 1000.0,
                                            size[1] / 1000.0, "По умолчанию", True), fmt.name)
            drw.ForceRebuild3(False)
            service = [n["name"] for n in oracles.notes_of_view(com.dyn(drw.GetFirstView))
                       if SERVICE_NOTE.match(n.get("linked") or n["text"])]
            self.s.close(drw)
            if service:
                found[fmt.name] = service
        self.assertEqual({}, found)


def cell_text(ann, r, c):
    ole = com.dyn(ann)._oleobj_
    import pythoncom
    return str(ole.InvokeTypes(ole.GetIDsOfNames("Text2"), 0, pythoncom.DISPATCH_PROPERTYGET, (8, 0),
                               ((3, 1), (3, 1), (11, 1)), r, c, True))


def read_table(ann):
    rows = int(ann.RowCount)
    table = [[cell_text(ann, r, c) for c in range(int(ann.ColumnCount))] for r in range(rows)]
    heights = [round(float(ann.GetRowHeight(r)) * 1000, 2) for r in range(rows)]
    return table, heights


def bom_anchor(drw):
    """Точка привязки BOM (swTableAnnotation_BillOfMaterials) текущего листа, мм."""
    a = com.dyn(drw.GetCurrentSheet).TableAnchor(2)
    return [round(float(x) * 1000, 1) for x in com.dyn(a).Position] if a is not None else None


def set_bom_anchor(drw, x, y):
    """Точка привязки BOM листа — точка эскиза форматки (ISheet.SetAsTableAnchor), как у прежних форматок."""
    drw.EditTemplate()
    sketch = com.dyn(drw.SketchManager)
    sketch.AddToDB = True
    point = com.dyn(sketch.CreatePoint(x, y, 0))
    sketch.AddToDB = False
    drw.ClearSelection2(True)
    point.Select4(False, com.null_dispatch())
    com.dyn(drw.GetCurrentSheet).SetAsTableAnchor(2)
    drw.ClearSelection2(True)
    drw.EditSheet()


def first_table(drw):
    view = drw.GetFirstView
    while view is not None:
        view = com.dyn(view)
        for ann in view.GetTableAnnotations or []:
            return com.dyn(ann)
        view = view.GetNextView
    return None


class SpecBchLayout(SwTestCase):
    COL_FORMAT, COL_POS, COL_NAME, COL_QTY = 0, 2, 4, 5
    PARTS = ("ПРТИ.468211.101 Пластина опорная.sldprt", "ПРТИ.468211.111 Стойка трубная.sldprt")

    def _macro(self, proc, error_property):
        from eskd_e2e import mprop
        drw = com.dyn(self.s.sw.ActiveDoc)
        macro = mprop.swplus_copy(self.s.run_dir).parent.parent / "SpecEditor" / "SpecEditor.swp"
        err = com.ref_int()
        ok = bool(self.s.sw.RunMacro2(str(macro), "SpecEditor_run", proc, 1, err))
        self.assertEqual([], self.s.watchdog.pop_unexpected(), "окна SpecEditor")
        self.assertTrue(ok, f"{proc} не выполнен: err={int(err.value)}")
        self.assertEqual("", str(drw.GetCustomInfoValue("", error_property) or ""), f"ошибка {proc}")

    def _layout(self, anchored=False):
        """Сборка из двух деталей БЧ и болта, чертёж с таблицей SpecEditor, оформление записей БЧ (SwpLayoutRecords).
        anchored — таблица вставляется, как SpecEditor на лист сборки (FrmSpecEditor:1708): к точке привязки BOM листа,
        правым нижним углом; перед этим у листа ставится точка прежних форматок (205; 68), и SpecEditor её исправляет.
        Возвращает чертёж, таблицу, пути деталей и свойства деталей в памяти до оформления."""
        from eskd_e2e import build
        parts = []
        for name in self.PARTS:
            path, doc = self.open_copy(name)
            self.s.activate(doc)
            self.assertEqual(1, int(com.call(self.s.eskd(), "ToggleDrawinglessSilent")), name)
            self.s.save(doc)
            self.s.close(doc)
            parts.append(path)
        bolt = self.copy_fixture("Болт М6-6gх20.58 ГОСТ 7798-70.sldprt")
        asm, _ = build.assembly(self.s, [(parts[0], 0, 0, 0), (parts[1], 0, 0.15, 0), (bolt, 0.3, 0, 0)])
        asm_path = self.path("ПРТИ.468211.130 СБ Узел.sldasm")
        self.s.save_as(asm, asm_path)
        self.s.close_all()
        self.s.open(asm_path)
        drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
        build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
        view = build.model_view(drw, asm_path, 60, 60)
        drw.ActivateView(str(view.GetName2))
        template = paths.SWPLUS / "SpecEditor" / "SpecEditor_sp.sldbomtbt"
        if anchored:
            set_bom_anchor(drw, 0.205, 0.068)
            self.assertEqual([205.0, 68.0], bom_anchor(drw), "точка прежних форматок")
            self._macro("swp_bom_anchor_active_sheet", "SwpAnchorError")
            drw.ActivateView(str(view.GetName2))
            ann = com.dyn(view.InsertBomTable4(True, 0.415, 0.068, 4, 1, "", str(template), False, 0, False))
        else:
            ann = com.dyn(view.InsertBomTable4(False, 0.2, 0.285, 1, 2, "", str(template), False, 0, False))
        before = {p: oracles.dump_properties(com.dyn(self.s.sw.GetOpenDocumentByName(str(p)))) for p in parts}
        self._macro("swp_layout_active_table", "SwpLayoutError")
        return drw, ann, parts, before

    def test_S10_spec_sits_in_bottom_left_corner_of_sheet(self):
        """S10 (замечание владельца 19.09.2026): спецификация на листе A3 сборочного чертежа — в левом нижнем углу рамки:
        правый нижний угол в точке привязки BOM (205; 5) мм, левый — в (20; 5), таблица привязана и растёт вверх.
        Лист с точкой прежних форматок (205; 68) SpecEditor исправляет перед вставкой; форматки A3–A0 несут точку сами."""
        from eskd_e2e import build
        # Форматки — до чертежа со старой точкой: SolidWorks держит загруженную форматку в памяти сеанса.
        for name in ("A3-A-1", "A2-A-1", "A1-A-1", "A0-A-1"):
            with self.subTest(format=name):
                doc = self.s.new_doc(paths.DRAWING_TEMPLATE)
                size = {"A3": (420, 297), "A2": (594, 420), "A1": (841, 594), "A0": (1189, 841)}[name[:2]]
                build.set_sheet_format(doc, build.sheet_format(name), *size)
                self.assertEqual([205.0, 5.0], bom_anchor(doc), "точка привязки BOM форматки")
                self.s.close(doc)
        drw, ann, parts, before = self._layout(anchored=True)
        # Ширины граф спецификации ставит SpecEditor (FrmSpecEditor:2949–2955): 6+6+8+70+63+10+22 = 185 мм.
        for c, w in enumerate((0.006, 0.006, 0.008, 0.07, 0.063, 0.01, 0.022)):
            ann.SetColumnWidth(c, w, 0)  # swTableRowColChange_TableSizeCanChange
        drw.ForceRebuild3(False)
        self.assertEqual([205.0, 5.0], bom_anchor(drw), "точка привязки BOM листа")
        self.assertTrue(bool(drw.GetEditSheet), "после правки основной надписи лист вернулся в режим листа")
        self.assertTrue(bool(ann.Anchored), "таблица привязана к точке")
        self.assertEqual([205.0, 5.0], [round(float(x) * 1000, 1) for x in list(com.dyn(ann.GetAnnotation).GetPosition)[:2]],
                         "правый нижний угол таблицы — в точке привязки, низ — на рамке")
        width = sum(float(ann.GetColumnWidth(c)) for c in range(int(ann.ColumnCount))) * 1000
        self.assertAlmostEqual(20.0, 205.0 - width, delta=0.5, msg=f"левый край — на рамке 20 мм (ширина {width:.1f} мм)")

    def test_S11_deliberate_bom_anchor_is_kept(self):
        """S11 (аудит 19.09, М-В4): точку привязки BOM, поставленную не форматкой SWPlus (205; 68), SpecEditor не переставляет;
        лист после проверки не остаётся в режиме редактирования основной надписи."""
        from eskd_e2e import build
        drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
        build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
        set_bom_anchor(drw, 0.1, 0.05)
        self.assertEqual([100.0, 50.0], bom_anchor(drw), "точка поставлена намеренно")
        self._macro("swp_bom_anchor_active_sheet", "SwpAnchorError")
        self.assertEqual([100.0, 50.0], bom_anchor(drw), "SpecEditor точку не тронул")
        self.assertTrue(bool(drw.GetEditSheet), "лист в режиме листа, не основной надписи")
        self.s.close(drw)

    def test_S09_spec_layout_does_not_rewrite_models(self):
        """S09 (З-9): SpecEditor не переписывает свойства моделей — у деталей БЧ «Наименование», «Запись_БЧ» и «Примечание»
        после оформления те же; после сохранения чертежа с деталями (как при ЛЗК и синхронизации) и повторного открытия
        таблица та же — запись БЧ не двоится, «L = …» не наползает."""
        drw, ann, parts, before = self._layout()
        for path in parts:
            with self.subTest(part=path.name):
                after = oracles.dump_properties(com.dyn(self.s.sw.GetOpenDocumentByName(str(path))))
                for name, cfg in (("Наименование", None), ("Запись_БЧ", None), ("Примечание", "00")):
                    self.assertEqual(oracles.value(before[path], name, cfg), oracles.value(after, name, cfg),
                                     f"«{name}» не изменилось оформлением")
                self.assertNotIn("\n", oracles.value(after, "Наименование") or "", "«Наименование» — одно название")
                self.assertIn("мм", oracles.value(after, "Запись_БЧ") or "", "строки записи — в «Запись_БЧ»")
        table, heights = read_table(ann)
        drw_path = self.path("ПРТИ.468211.130 СБ Узел.slddrw")
        self.s.save_as(drw, drw_path)
        err, warn = com.ref_int(), com.ref_int()
        self.assertTrue(drw.Save3(com.SAVE_SILENT | 4, err, warn), f"сохранение с деталями: {int(err.value)}")  # swSaveAsOptions_SaveReferenced
        self.s.close_all()
        for path in parts:
            with self.subTest(disk=path.name):
                disk = self.persisted(path)
                self.assertNotIn("\n", oracles.value(disk, "Наименование") or "", "на диске «Наименование» — одно название")
                self.assertIn("мм", oracles.value(disk, "Запись_БЧ") or "", "на диске строки записи — в «Запись_БЧ»")
        drw = self.s.open(drw_path)
        drw.ForceRebuild3(False)
        again, again_heights = read_table(first_table(drw))
        self.path("table.json").write_text(__import__("json").dumps({"after_layout": table, "after_reopen": again},
                                                                    ensure_ascii=False, indent=1), encoding="utf-8")
        self.assertEqual(table, again, "таблица после сохранения и повторного открытия — та же")
        self.assertEqual(heights, again_heights, "высоты строк те же")

    def test_S08_bch_record_one_table_row_per_line(self):
        """S08: запись детали БЧ в спецификации строго по ГОСТ Р 2.105-2019 п. 7.4–7.5 и рис. 15 проекта ГОСТ Р 2.109
        (Р-15, S-2f): SpecEditor (SwpLayoutRecords) даёт каждой строке записи строку 8 мм, дробь — объединённой ячейке на
        две строки, «Кол.» и «Примечание» — последней строке записи, позиция — первой."""
        drw, ann, parts, before = self._layout()
        table, heights = read_table(ann)
        rows = len(table)
        self.path("table.json").write_text(__import__("json").dumps({"cells": table, "heights": heights}, ensure_ascii=False,
                                                                    indent=1), encoding="utf-8")
        for title, size_prefix in (("Пластина опорная", ""), ("Стойка трубная", "L = ")):
            with self.subTest(record=title):
                first = next(r for r in range(1, rows) if table[r][self.COL_NAME].strip() == title)
                self.assertEqual("БЧ", table[first][self.COL_FORMAT].strip(), "«БЧ» в первой строке записи")
                self.assertTrue(table[first][self.COL_POS].strip(), "позиция в первой строке записи")
                self.assertIn("ГОСТ", table[first + 1][self.COL_NAME], "дробь — со второй строки записи")
                self.assertTrue(table[first + 3][self.COL_NAME].strip().startswith(size_prefix) and "мм" in table[first + 3][self.COL_NAME],
                                f"размер — в четвёртой строке: {table[first + 3][self.COL_NAME]!r}")
                for r in range(first, first + 4):
                    self.assertAlmostEqual(8.0, heights[r], delta=0.1, msg=f"строка {r} записи — 8 мм")
                for r in range(first + 1, first + 4):
                    self.assertFalse(table[r][self.COL_POS].strip(), f"позиция только в первой строке (строка {r})")
                self.assertEqual("1", table[first + 3][self.COL_QTY].strip(), "«Кол.» в последней строке записи")
                self.assertIn("кг", table[first + 3][-1], "масса в «Примечании» последней строки записи")
                self.assertFalse(table[first][self.COL_QTY].strip(), "в первой строке «Кол.» пусто")


if __name__ == "__main__":
    unittest.main()
