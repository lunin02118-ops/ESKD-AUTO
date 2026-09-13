# -*- coding: utf-8 -*-
"""E2E, группа I — установка, шаблоны и интерфейс (план, §4.7). Сценарии I01, I02, I04, I05 добавляются в пакетах фаз 3–4."""
import re
import unittest

from eskd_e2e import oracles, paths
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


if __name__ == "__main__":
    unittest.main()
