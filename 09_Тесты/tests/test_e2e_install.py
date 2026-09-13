# -*- coding: utf-8 -*-
"""E2E, группа I — установка, шаблоны и интерфейс (план, §4.7). Сценарии I01, I02, I04, I05 добавляются в пакетах фаз 3–4."""
import re
import unittest

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
        return problems

    @known_defect("Д-18")
    def test_I03_templates_have_dictionary_names_without_personal_data(self):
        """I03: новый документ из каждого шаблона — без алиасов v5, п_*/а_*, личных данных; «Формат» кириллицей; живые масса и материал."""
        report = {}
        for template in (paths.PART_TEMPLATE, paths.ASSEMBLY_TEMPLATE, paths.DRAWING_TEMPLATE):
            problems = self._template_problems(template)
            if problems:
                report[template.name] = problems
        self.assertEqual({}, report)


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
