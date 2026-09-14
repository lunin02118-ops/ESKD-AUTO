# -*- coding: utf-8 -*-
"""E2E, группа I — установка, шаблоны и интерфейс (план, §4.7). Сценарии I01, I02, I04, I05 добавляются в пакетах фаз 3–4."""
import re
import unittest
from pathlib import Path

from eskd_e2e import com, oracles, paths
from eskd_e2e.testing import SwTestCase, known_defect

LEGACY = {"Разраб.", "Разработал", "Автор", "п_Разраб", "DrawnBy", "п_Разраб_Дата", "DrawnDate", "Пров.", "п_Пров",
          "CheckedBy", "п_Пров_Дата", "Организация", "Организация_ФБ", "Компания", "Firm", "Organization", "PartNo",
          "Сортамент", "ГОСТ_Сортамент", "ГОСТ_Материал", "БЧ"}
PERSONAL_MARKERS = ("Лунин", "Home Made")


class Templates(SwTestCase):

    def _template_problems(self, template):
        with self.s.eskd_muted():
            doc = self.s.new_doc(template)
            dump = oracles.dump_properties(doc)
            self.s.close(doc)
        problems = []
        for level, props in [("общие", dump["general"])] + sorted(dump["configs"].items()):
            for name, item in props.items():
                raw = item["raw"]
                where = f"{level}/{name}"
                if name in LEGACY or name.startswith(("п_", "а_")):
                    problems.append(f"{where}: имя вне словаря")
                if any(marker in raw for marker in PERSONAL_MARKERS):
                    problems.append(f"{where}: личные данные «{raw}»")
                if name == "Формат" and re.search(r"[A-Za-z]", raw):
                    problems.append(f"{where}: латиница в «{raw}»")
                if name == "Масса" and "SW-Mass" not in raw:
                    problems.append(f"{where}: статичное значение «{raw}» вместо SW-Mass")
                if name == "Материал" and "SW-Material" not in raw:
                    problems.append(f"{where}: статичное значение «{raw}» вместо SW-Material")
                if name == "Обозначение" and "SW-File Name" in raw:
                    problems.append(f"{where}: «{raw}» — в спецификацию и штамп попадёт имя файла с наименованием")
        return problems

    @known_defect("Д-18")
    def test_I03_templates_have_dictionary_names_without_personal_data(self):
        """I03: новый документ из каждого шаблона — без алиасов v5, п_*/а_*, личных данных; «Формат» кириллицей; живые масса и
        материал; «Обозначение» не выражение имени файла."""
        report = {}
        for template in (paths.PART_TEMPLATE, paths.ASSEMBLY_TEMPLATE, paths.DRAWING_TEMPLATE):
            problems = self._template_problems(template)
            if problems:
                report[template.name] = problems
        self.assertEqual({}, report)


class SheetFormats(SwTestCase):

    FORMAT_KEEP = {"SWFormatSize"}

    def test_I08_sheet_formats_and_master_templates_carry_no_properties(self):
        """I08: новый лист с каждой форматкой SWPlus (18 основных надписей и 9 форм SpecEditor) не приносит в чертёж
        пользовательских свойств — ни алиасов v5, ни «Лунин В.И.», ни «123» (спайк S-6); шаблоны Master без свойств (WP-4.2)."""
        formats = sorted(paths.SHEET_FORMATS.glob("*.slddrt")) + sorted(paths.SPEC_FORMATS.glob("*.slddrt"))
        self.assertEqual(27, len(formats), [f.name for f in formats])
        report = {}
        with self.s.eskd_muted():
            drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
            try:
                cpm = drw.Extension.CustomPropertyManager("")
                for n, fmt in enumerate(formats):
                    before = set(com.prop_names(cpm))
                    ok = drw.NewSheet3(f"Проверка{n}", 12, 12, 1.0, 1.0, True, str(fmt), 0.42, 0.297, "")
                    if not ok:
                        report[fmt.name] = "лист не добавлен"
                        continue
                    added = {name: com.prop_get(cpm, name)[0] for name in set(com.prop_names(cpm)) - before - self.FORMAT_KEEP}
                    for name in list(added):
                        cpm.Delete2(name)
                    if added:
                        report[fmt.name] = added
            finally:
                self.s.close(drw)
        for template in sorted((paths.SWPLUS / "Master").glob("Master_Template_*.SLDDRW")):
            dump = oracles.read_persisted(self.s, self.s.workspace_copy(template, subdir=self._case_name()))
            extra = {name: item["raw"] for name, item in dump["general"].items() if name not in self.FORMAT_KEEP}
            if extra:
                report[template.name] = extra
        self.assertEqual({}, report, "свойства, которые форматки и шаблоны Master приносят в чертёж")

    def test_I04_property_tab_values_reach_stamp(self):
        """I04 (Д-19): значения, введённые во вкладке свойств по шаблону «Деталь_ГОСТ.prtprp» (имя и уровень каждого поля берутся
        из шаблона), после сохранения детали выводятся в основной надписи чертежа A-10."""
        import xml.etree.ElementTree as ET
        template = paths.PROPERTY_TAB_TEMPLATES / "Деталь_ГОСТ.prtprp"
        controls = {c.get("PropName"): c.get("ApplyTo") for c in ET.fromstring(template.read_bytes().decode("utf-8-sig")).iter("Control")}
        values = {"Наименование": "Пластина проверочная", "Конструктор": "Проба К.К.", "Проверил": "Проба П.П.",
                  "Контора": "ООО «Проба»"}
        model = self.copy_fixture("ПРТИ.468211.101 Пластина опорная.sldprt")
        drawing = self.copy_fixture("ПРТИ.468211.101 Пластина опорная.slddrw")
        doc = self.s.open(model)
        for name, value in values.items():
            self.assertIn(name, controls, f"поле «{name}» в шаблоне вкладки свойств")
            level = "" if controls[name] == "Global" else "00"
            self.assertEqual(0, com.prop_set(doc.Extension.CustomPropertyManager(level), name, value), name)
        self.s.save(doc)
        self.s.close(doc)
        drw = self.s.open(drawing)
        notes = next(iter(oracles.stamp(drw).values()))
        self.s.close(drw)
        shown = " | ".join(" ".join(re.sub(r"<[^>]*>", " ", (n.get("text") or "")).split()) for n in notes.values())
        missing = [f"{name} = {value}" for name, value in values.items() if value not in shown]
        self.assertEqual([], missing, f"значения вкладки свойств, которых нет в штампе: {shown[:600]}")

    def test_I09_master_templates_match_sheet_formats(self):
        """I09 (К-9, WP-4.1): надписи шаблонов Master совпадают с форматками A4 — имя, шрифт, высота, ширина, интервал и
        положение от правого нижнего угла; иначе «Сменить формат» в Master пересоздаёт форматку со старым штампом (Н-34)."""
        def geometry(doc):
            sheet = com.dyn(doc.GetCurrentSheet)
            width = float(com.as_list(sheet.GetProperties2)[5]) * 1000
            doc.EditTemplate()
            try:
                out = {}
                note = com.dyn(doc.GetFirstView).GetFirstNote
                while note is not None:
                    n = com.dyn(note)
                    name = str(n.GetName or "")
                    if name.startswith("MYPRP"):
                        fmt = com.dyn(n.GetTextFormat)
                        pos = com.as_list(com.dyn(n.GetAnnotation).GetPosition)
                        out[name] = (str(fmt.TypeFaceName), round(float(fmt.CharHeight) * 1000, 2), round(float(fmt.WidthFactor), 3),
                                     round(float(fmt.LineSpacing) * 1000, 2), round(width - float(pos[0]) * 1000, 2), round(float(pos[1]) * 1000, 2))
                    note = n.GetNext
                return out
            finally:
                doc.EditSheet()

        pairs = (("Master_Template_Sheet1.SLDDRW", "A4-P-1.slddrt"), ("Master_Template_Sheet2.SLDDRW", "A4-P-2.slddrt"))
        report = {}
        with self.s.eskd_muted():
            for template, fmt in pairs:
                master = self.s.open(self.s.workspace_copy(paths.SWPLUS / "Master" / template, subdir=self._case_name()))
                expected = geometry(master)
                self.s.close(master)
                sheet = self.s.open(self.s.workspace_copy(paths.SHEET_FORMATS / fmt, subdir=self._case_name(), name=Path(fmt).stem + ".SLDDRW"))
                actual = geometry(sheet)
                self.s.close(sheet)
                # надпись только в шаблоне Master (MYPRP1 — повёрнутое обозначение графы 26) не мешает эталону: сравниваются общие
                diff = {k: (expected[k], actual[k]) for k in set(expected) & set(actual) if expected[k] != actual[k]}
                missing = sorted(set(actual) - set(expected))
                if missing:
                    diff["нет в шаблоне Master"] = missing
                if diff:
                    report[template] = diff
        self.assertEqual({}, report, "надписи шаблона Master (первое) и форматки (второе)")


class AddinLifecycle(SwTestCase):

    @known_defect("Д-26")
    def test_I06_repeated_unload_and_load_keep_solidworks_alive(self):
        """I06: пять циклов UnloadAddIn/LoadAddIn — SolidWorks жив, надстройка отвечает, реквизиты по-прежнему пишутся при сохранении."""
        for cycle in range(5):
            with self.subTest(cycle=cycle):
                self.s.unload_eskd()
                self.s.load_eskd()
                self.assertTrue(self.s.alive(), "SolidWorks не отвечает после перезагрузки надстройки")
        self.assertTrue(str(com.call(self.s.eskd(), "GetVersion")).startswith("6."), "надстройка не отвечает")
        path, doc = self.open_copy("ПРТИ.468211.101 Пластина опорная.sldprt")
        self.s.save(doc)
        self.s.close(doc)
        self.assertEqual("ПРТИ.468211.101", oracles.value(self.persisted(path), "Обозначение", "00"), "запись при сохранении")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки после перезагрузок")


class FixtureMaterials(SwTestCase):

    def test_I07_fixture_materials_match_manifest(self):
        """I07: материалы в файлах фикстур как в manifest.json — у деталей из проката корпуса А и у копии трубы B-01 сортамент
        из библиотеки ЕСКД, у стандартного болта — сталь вне библиотеки, у покупного двигателя материала нет."""
        from eskd_e2e import build, testing
        manifest = testing.manifest()
        expected = {}
        for fid, item in manifest["fixtures"].items():
            if item.get("kind") in ("part", "weldment", "bch"):
                expected[(fid, paths.FIXTURES_A / item["file"])] = {None: item["material"]}
            elif item.get("kind") in ("standard", "purchased"):
                expected[(fid, paths.FIXTURES_A / item["file"])] = {None: item.get("material_sw") or ""}
        for fid, item in manifest["corpus_b"].items():
            expected[(fid, paths.FIXTURES_B / item["file"])] = dict(item["materials"])
        wrong = {}
        for (fid, source), materials in sorted(expected.items(), key=lambda kv: kv[0][0]):
            doc = self.s.open(self.s.workspace_copy(source, subdir=f"{self._case_name()}/{fid}"), readonly=True)
            try:
                configurations = [str(c) for c in com.as_list(doc.GetConfigurationNames)]
                for cfg, material in materials.items():
                    for name in (configurations if cfg is None else [cfg]):
                        got = build.material_of(doc, name)[0]
                        if got != material:
                            wrong[f"{fid} «{name}»"] = {"ожидался": material, "назначен": got}
            finally:
                self.s.close(doc)
        self.assertEqual({}, wrong, "материалы фикстур расходятся с манифестом")


if __name__ == "__main__":
    unittest.main()
