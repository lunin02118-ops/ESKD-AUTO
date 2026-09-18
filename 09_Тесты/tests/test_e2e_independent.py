# -*- coding: utf-8 -*-
"""E2E, группа N — кнопка «Сделать независимым с чертежом» (ТЗ-02 Т-19…Т-24).

Фикстура A лежит одной папкой, а кнопке нужен компонент ИЗВНЕ изделия. Поэтому деталь переносится
в соседнюю «02_БАЗА», а ссылка закрытой сборки переставляется на неё (`ReplaceReferencedDocument`) —
так же, как это выглядит у конструктора, взявшего эталон из базы.
"""
import hashlib
import shutil
import unittest
from pathlib import Path

import pythoncom
import win32com.client

from eskd_e2e import com, paths
from eskd_e2e.testing import SwTestCase

PRODUCT = "И01_ПРТИ.468211.100"
ASM = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
# Деталь верхнего уровня сборки: «Пластина опорная» сидит внутри узла опоры, и ссылку на неё
# главная сборка не хранит — заменять в ней было бы нечего.
ETALON = "ПРТИ.468211.102 Стойка.sldprt"


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class Independent(SwTestCase):

    def _product(self, etalon=ETALON):
        """Изделие заказа и эталон в «02_БАЗА» рядом; возвращает (папка изделия, сборка, эталон)."""
        short = self._case_name().split("_")[1]
        subdir = f"{short}/_Заявки/2026-001/02_Металл/{PRODUCT}/01_3D"
        models = self.s.run_dir / subdir
        if models.exists():
            shutil.rmtree(models, ignore_errors=True)
        for src in sorted(Path(paths.FIXTURES_A).iterdir()):
            if src.suffix.lower() in (".sldprt", ".sldasm"):
                self.s.workspace_copy(src, subdir=subdir)
        base = self.s.run_dir / short / "02_БАЗА"
        base.mkdir(parents=True, exist_ok=True)
        moved = base / etalon
        shutil.move(str(models / etalon), str(moved))
        asm = models / ASM
        # Путь ссылки сборка хранит тот, по которому файл лежал при сборке фикстуры, — по нему и заменяем.
        deps = list(self.s.sw.GetDocumentDependencies2(str(asm), True, False, False) or [])
        stored = next(p for p in deps[1::2] if Path(p).name.lower() == etalon.lower())
        self.assertTrue(self.s.sw.ReplaceReferencedDocument(str(asm), stored, str(moved)),
                        "ссылка сборки не переставлена на базу")
        return models.parent, asm, moved

    def _select(self, doc, path):
        """Выделить в дереве все экземпляры компонента; возвращает их число."""
        doc.ClearSelection2(True)
        # Select4 ждёт SelectData: обычный None позднее связывание передаёт как «несовпадение типов».
        null = win32com.client.VARIANT(pythoncom.VT_DISPATCH, None)
        count, seen = 0, []
        for obj in com.dyn(doc).GetComponents(False) or []:
            comp = com.dyn(obj)
            current = str(comp.GetPathName or "")
            seen.append(current)
            if current.lower() == str(path).lower() and comp.Select4(True, null, False):
                count += 1
        self.assertGreater(count, 0, f"компонент не выделен: {path}\nв сборке: {seen}")
        return count

    def _make(self, designation="", name="", with_drawing=-1):
        com.call(self.s.eskd(), "MakeIndependentSilent", designation, name, with_drawing)
        return str(com.call(self.s.eskd(), "IndependentStatus"))

    def test_N01_etalon_becomes_own_part_with_report(self):
        """N01: эталон из базы становится своей деталью в 01_3D, эталон не изменён, отчёт перечисляет созданное."""
        product, asm, etalon = self._product()
        before = sha256(etalon)
        doc = self.s.open(asm)
        self.s.activate(doc)
        instances = self._select(doc, etalon)

        status = self._make("ПРТИ.468211.150", "Пластина своя")
        self.assertTrue(status.startswith("ok|"), status)
        _, created, skipped, dangling, report_path = status.split("|")
        self.assertEqual("", report_path, "отчёта _Независимые.txt нет — итог в окне замечаний")
        self.assertEqual("1", created, f"создана одна деталь: {status}")
        self.assertEqual("0", skipped, f"пропусков нет: {status}")

        target = product / "01_3D" / "ПРТИ.468211.150 Пластина своя.sldprt"
        self.assertTrue(target.is_file(), "новая деталь лежит в 01_3D изделия")
        self.assertEqual(before, sha256(etalon), "эталон в базе не изменён (Т-24)")

        text = str(com.call(self.s.eskd(), "LastNotices"))
        self.assertIn("ПРТИ.468211.150 Пластина своя.sldprt", text, text)
        self.assertIn(f"экземпляров перепривязано: {instances}", text, text)
        self.assertNotIn("исходная модель изменилась", text, "эталон не изменён — критичного нет")
        self.assertFalse((product / "_Независимые.txt").exists(), "текстовый отчёт не пишется")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_N02_assembly_points_at_the_new_part(self):
        """N02: после отвязки сборка ссылается на новую деталь, а реквизиты взяты из её имени (Т-21, Т-23)."""
        product, asm, etalon = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        self._select(doc, etalon)
        self.assertTrue(self._make("ПРТИ.468211.151", "Пластина своя").startswith("ok|"))

        target = str(product / "01_3D" / "ПРТИ.468211.151 Пластина своя.sldprt")
        paths_now = [str(com.dyn(o).GetPathName or "") for o in com.dyn(doc).GetComponents(False) or []]
        self.assertIn(target.lower(), [p.lower() for p in paths_now], "сборка ссылается на новую деталь")
        self.assertNotIn(str(etalon).lower(), [p.lower() for p in paths_now], "ссылки на эталон не осталось")

        # Новая деталь загружена как компонент сборки: реквизиты читаются, когда изделие закрыто.
        from eskd_e2e import oracles
        self.s.close_all()
        self.assertEqual("ПРТИ.468211.151", oracles.value(self.persisted(Path(target)), "Обозначение", "00"),
                         "обозначение новой детали — из её имени файла")
        # «Наименование» надстройка держит общим свойством документа, «Обозначение» — по конфигурациям.
        self.assertEqual("Пластина своя", oracles.value(self.persisted(Path(target)), "Наименование"),
                         "наименование новой детали — из её имени файла")

    def test_N03_own_and_purchased_parts_are_skipped(self):
        """N03: деталь этого же изделия независимой не делают — кнопка объясняет отказ, файлы не трогает."""
        product, asm, etalon = self._product()
        doc = self.s.open(asm)
        self.s.activate(doc)
        # «~$…» — служебный файл блокировки SolidWorks, компонентом он не бывает.
        own = next(p for p in sorted((product / "01_3D").glob("*.sldprt"))
                   if p.name != ETALON and not p.name.startswith("~$"))
        self._select(doc, own)
        status = self._make()
        self.assertTrue(status.startswith("error|"), f"отказ: {status}")
        self.assertIn("деталь уже своя", status, "причина названа")
        self.assertNoPropertyWrites()

    def test_N04_refuses_part_document(self):
        """N04: на детали кнопка недоступна, вызов отказывает."""
        path, doc = self.open_copy(ETALON)
        self.s.activate(doc)
        self.assertEqual(0, int(com.call(self.s.eskd(), "EnableIndependentCommand")), "кнопка серая у детали")
        self.assertTrue(self._make().startswith("error|"), "вызов отказал")
        self.assertNoPropertyWrites()


if __name__ == "__main__":
    unittest.main()
