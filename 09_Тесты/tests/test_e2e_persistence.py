# -*- coding: utf-8 -*-
"""E2E, группа P — персистентность реквизитов и события сохранения (план, §4.7).

Оракул — состояние файла на диске, прочитанное с выключенной службой надстройки.
"""
import re
import unittest
from pathlib import Path

from eskd_e2e import build, com, oracles, paths, testing
from eskd_e2e.testing import SwTestCase, known_defect, tags, with_doc_events

SHEET4 = "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
SHEET6 = "Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
TUBE80 = "Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86"
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A04 = "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt"
A08 = "ПРТИ.468211.110 СБ Узел опоры.sldasm"
A10 = "ПРТИ.468211.101 Пластина опорная.slddrw"
A13 = "ПРТИ.468211.106 Крышка.sldprt"
V = oracles.value



def mprop_title(text):
    """«Наименование_ФБ» в разметке MProp (FrmMProp:2640–2652): строки разделены LF."""
    lines = text.count("\n") + 1
    head = {1: "<FONT size=4> \n<FONT size=5>", 2: "<FONT size=2> \n<FONT size=5>"}.get(lines, "<FONT size=3.5>")
    return head + text


def mprop_mass(cfg, file_stem, grams=False, small_font=True, assembly=False):
    """«Масса_ФБ» в формате MProp (Правила записи свойств SWPlus, раздел 1; FrmMProp:2850–2888)."""
    ext = ".SLDASM" if assembly else ".SLDPRT"
    return ("<FONT size=1> \n<FONT size=3.5>" if small_font else "") + f'"SW-Mass@@{cfg}@{file_stem}{ext}"' + (" г" if grams else "")


class PersistenceNewDocuments(SwTestCase):

    def _assert_named_plate(self, disk, designation, title):
        self.assertEqual(designation, V(disk, "Обозначение"), "обозначение (общие)")
        self.assertEqual(title, V(disk, "Наименование"), "наименование (общие)")
        self.assertEqual(mprop_title(title), (V(disk, "Наименование_ФБ") or "").replace("\r\n", "\n"), "наименование для штампа в разметке MProp")
        self.assertEqual(designation, V(disk, "Обозначение", "00"), "обозначение (конфигурация)")
        self.assertIn("<STACK size=1>", V(disk, "Материал_ФБ", "00") or "", "дробь материала в конфигурации")
        self.assertEqual(mprop_mass("00", f"{designation} {title}"), (V(disk, "Масса_ФБ", "00") or "").replace("\r\n", "\n"),
                         "масса в конфигурации — выражение MProp")
        self.assertEqual("0.63", re.sub(r"<[^>]*>", "", V(disk, "Масса_ФБ", "00", resolved=True) or "").strip(),
                         "графа 5 — 0.63 кг")

    @tags("smoke")
    @known_defect("Д-01")
    @with_doc_events
    def test_P01_new_part_api_save_as_writes_names_to_disk(self):
        """P01: новая деталь → API «Сохранить как» → на диске обозначение и наименование из имени файла."""
        doc, _ = build.plate(self.s, 200, 100, 4, SHEET4)
        target = self.path("ПРТИ.468211.121 Пластина новая.sldprt")
        self.s.save_as(doc, target)
        self.wait_idle()
        self.assertEqual([2, 1], [t for t, _ in self.saves()],
                         "ожидалось «Сохранить как» и одно отложенное пересохранение")
        self.s.close(doc)
        self._assert_named_plate(self.persisted(target), "ПРТИ.468211.121", "Пластина новая")

    @known_defect("Д-01")
    @with_doc_events
    def test_P02_new_part_ui_save_writes_names_to_disk(self):
        """P02: новая деталь → команда «Сохранить» (путь команды интерфейса) → реквизиты на диске.
        Путь в диалог подставляет зонд из FileSaveAsNotify2 — событие документа, поэтому они включены."""
        doc, _ = build.plate(self.s, 200, 100, 4, SHEET4)
        target = self.path("ПРТИ.468211.123 Пластина через команду.sldprt")
        self.s.ui_save_as(doc, target, command=2)
        self.wait_idle()
        self.s.close(doc)
        self._assert_named_plate(self.persisted(target), "ПРТИ.468211.123", "Пластина через команду")


class PersistenceSave(SwTestCase):

    def _grow_plate(self, doc):
        # Дополнительная площадка 100×100×4: масса 0,628 → 0,942 кг
        build.sketch_rectangles(doc, [(0.15, -0.05, 0.25, 0.05)])
        build.extrude(doc, 0.004)
        doc.ForceRebuild3(False)

    @tags("smoke")
    def test_P03_api_save_writes_new_mass(self):
        """P03: изменение геометрии → API Save3 → новая масса на диске."""
        path, doc = self.open_copy(A01)
        self._grow_plate(doc)
        ok, err, _ = self.s.save(doc)
        self.assertTrue(ok, f"Save3 err={err}")
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertIn("0.94", V(disk, "Масса_ФБ", "00", resolved=True) or "", "графа 5 — новая масса 0.94 кг")

    def test_P04_command_save_writes_new_mass(self):
        """P04: то же через команду «Сохранить» (Ctrl+S)."""
        path, doc = self.open_copy(A01)
        self._grow_plate(doc)
        self.s.run_command(doc, 2)
        self.s.close(doc)
        self.assertIn("0.94", V(self.persisted(path), "Масса_ФБ", "00", resolved=True) or "", "графа 5 — новая масса 0.94 кг")

    @tags("smoke")
    @known_defect("Д-02")
    def test_P05_save_as_renames_part(self):
        """P05: «Сохранить как» (API) под новым именем — новый файл получает новые реквизиты, исходный не меняется."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        target = self.path("ПРТИ.468211.131 Пластина переименованная.sldprt")
        self.s.save_as(doc, target)
        self.wait_idle()
        self.s.close(doc)
        new = self.persisted(target)
        self.assertEqual("ПРТИ.468211.131", V(new, "Обозначение"))
        self.assertEqual("ПРТИ.468211.131", V(new, "Обозначение", "00"))
        self.assertEqual("Пластина переименованная", V(new, "Наименование"))
        self.assertEqual(mprop_title("Пластина\nпереименованная"), (V(new, "Наименование_ФБ") or "").replace("\r\n", "\n"))
        self.assertEqual("ПРТИ.468211.101", V(self.persisted(path), "Обозначение"), "исходный файл")

    @known_defect("Д-02")
    @with_doc_events
    def test_P05_ui_save_as_renames_part(self):
        """P05: «Сохранить как» командой интерфейса — то же для пути UI (путь подставляет зонд, см. P02)."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        target = self.path("ПРТИ.468211.133 Пластина из диалога.sldprt")
        self.s.ui_save_as(doc, target)
        self.wait_idle()
        self.s.close(doc)
        self.assertEqual("ПРТИ.468211.133", V(self.persisted(target), "Обозначение"))

    @known_defect("Д-02")
    def test_P05_save_as_renames_assembly(self):
        """P05: сборка «Сохранить как» — новое обозначение и код СБ в конфигурации."""
        self.copy_fixtures(A01, A04)
        path, doc = self.open_copy(A08)
        with self.s.eskd_muted():
            build.props(doc, {"Сборка1_ФБ": "СБ"}, "00")  # как в шаблоне сборки и у MProp
        self.s.save(doc)
        target = self.path("ПРТИ.468211.132 СБ Узел новый.sldasm")
        self.s.save_as(doc, target)
        self.wait_idle()
        self.s.close(doc)
        new = self.persisted(target)
        self.assertEqual("ПРТИ.468211.132", V(new, "Обозначение"))
        self.assertEqual("Узел новый", V(new, "Наименование"))
        self.assertEqual("СБ", V(new, "Сборка1_ФБ", "00"), "код без пробела (Р-3)")

    def test_P06_save_as_keeps_manual_designation(self):
        """P06: «Сохранить как» детали с ручным обозначением (RenameSWP = 1) — обозначение сохраняется."""
        path, doc = self.open_copy(A13)
        target = self.path("ПРТИ.468211.141 Крышка новая.sldprt")
        self.s.save_as(doc, target)
        self.wait_idle()
        self.s.close(doc)
        new = self.persisted(target)
        self.assertEqual("ПРТИ.468211.199", V(new, "Обозначение"))

    def test_P07_save_as_copy_keeps_document_and_warns(self):
        """P07: «Сохранить как копию» — документ в памяти не меняется, в журнале предупреждение."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        copy = self.path("ПРТИ.468211.151 Копия.sldprt")
        ok, err, _ = self.s.save_as(doc, copy, options=com.SAVE_SILENT | com.SAVE_COPY)
        self.assertTrue(ok, f"копия не сохранена: {err}")
        self.wait_idle()
        self.assertEqual(str(path).lower(), str(doc.GetPathName).lower())
        self.assertTrue(any("Копия" in ln and "реквизитами исходного" in ln for ln in self.addin_log.new_lines()),
                        "нет предупреждения о копии в журнале надстройки")
        self.s.close(doc)

    def test_P07_save_as_copy_with_fix_copies_updates_copy(self):
        """P07: «Сохранить как копию» при FixCopies = 1 — копия на диске получает свои реквизиты, документ и исходный файл прежние."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        copy = self.path("ПРТИ.468211.152 Копия исправленная.sldprt")
        self.s.set_settings(FixCopies=1)
        # Надстройка сама откроет и закроет копию; подписок зонда на документах нет (SwTestCase.doc_events).
        try:
            ok, err, _ = self.s.save_as(doc, copy, options=com.SAVE_SILENT | com.SAVE_COPY)
            self.assertTrue(ok, f"копия не сохранена: {err}")
            self.wait_idle(4.0)
        finally:
            self.s.set_settings(FixCopies=0)
        self.assertEqual(str(path).lower(), str(doc.GetPathName).lower(), "документ в памяти сменил путь")
        self.assertEqual("ПРТИ.468211.101", V(oracles.dump_properties(doc), "Обозначение"), "реквизиты документа в памяти изменены")
        self.s.close(doc)
        new = self.persisted(copy)
        self.assertEqual("ПРТИ.468211.152", V(new, "Обозначение"))
        self.assertEqual("ПРТИ.468211.152", V(new, "Обозначение", "00"))
        self.assertEqual("Копия исправленная", V(new, "Наименование"))
        self.assertEqual("ПРТИ.468211.101", V(self.persisted(path), "Обозначение"), "исходный файл изменён")
        self.assertEqual([], self.addin_errors())


    def test_P08_save_as_copy_and_open_fixes_opened_copy(self):
        """P08: «Сохранить как копию и открыть» — открытая копия получает реквизиты по своему имени и сохраняется;
        исходный документ и его файл прежние (FileSavePostNotify(4), EventHub)."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        copy = self.path("ПРТИ.468211.153 Копия открытая.sldprt")
        ok, err, _ = self.s.save_as(doc, copy, options=com.SAVE_SILENT | com.SAVE_COPY_AND_OPEN)
        self.assertTrue(ok, f"копия не сохранена: {err}")
        self.wait_idle(4.0)
        opened = self.s.sw.GetOpenDocumentByName(str(copy))
        self.assertIsNotNone(opened, "копия не открыта")
        opened = com.dyn(opened)
        self.assertFalse(bool(opened.GetSaveFlag), "открытая копия осталась несохранённой")
        # после «копии с открытием» прежняя ссылка на документ SolidWorks указывает на открытую копию — исходный ищется по пути
        original = self.s.sw.GetOpenDocumentByName(str(path))
        if original is not None:
            original = com.dyn(original)
            self.assertEqual("ПРТИ.468211.101", V(oracles.dump_properties(original), "Обозначение"), "реквизиты исходного документа изменены")
        self.s.close_all()
        new = self.persisted(copy)
        self.assertEqual("ПРТИ.468211.153", V(new, "Обозначение"))
        self.assertEqual("ПРТИ.468211.153", V(new, "Обозначение", "00"))
        self.assertEqual("Копия открытая", V(new, "Наименование"))
        self.assertEqual("ПРТИ.468211.101", V(self.persisted(path), "Обозначение"), "исходный файл изменён")
        self.assertEqual([], self.addin_errors())


class Performance(SwTestCase):
    A09 = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
    A09_COMPONENTS = ("ПРТИ.468211.110 СБ Узел опоры.sldasm", A01, A04, "ПРТИ.468211.102 Стойка.sldprt",
                      "ПРТИ.468211.103 Планка.sldprt", "Электродвигатель АИР71А4.sldprt",
                      "ПРТИ.468211.104 Кронштейн направляющий удлинённый.sldprt", "ПРТИ.468211.105 Рама сварная.sldprt")

    @staticmethod
    def _median(values):
        values = sorted(values)
        return values[len(values) // 2]

    def _save_times(self, doc, n):
        import time
        out = []
        cpm = doc.Extension.CustomPropertyManager("")
        for i in range(n):
            # документ меняется по-настоящему, иначе Save3 ничего не записывает (0,05 с)
            com.prop_set(cpm, "P14_проба", str(time.time()) + str(i))
            started = time.perf_counter()
            ok, err, _ = self.s.save(doc)
            out.append(time.perf_counter() - started)
            self.assertTrue(ok, f"Save3 err={err}")
        return out

    def _switch_times(self, docs, n):
        import time
        out = []
        for _ in range(n):
            for d in docs:
                started = time.perf_counter()
                self.s.activate(d)
                out.append(time.perf_counter() - started)
        return out

    def test_P14_save_and_window_switch_overhead(self):
        """P14 (Д-04, Д-11): сохранение сборки A-09 с надстройкой дольше, чем без неё, не больше чем на 2 с (медиана трёх
        сохранений); переключение окна добавляет не больше 50 мс (медиана) и не пишет свойств."""
        self.copy_fixtures(*self.A09_COMPONENTS)
        path, asm = self.open_copy(self.A09)
        part = self.s.open(self.case_dir / A01) if self.s.sw.GetOpenDocumentByName(str(self.case_dir / A01)) is None             else com.dyn(self.s.sw.GetOpenDocumentByName(str(self.case_dir / A01)))
        self.s.save(asm)
        stamp = path.stat().st_mtime_ns
        with_addin = self._median(self._save_times(asm, 3))
        self.assertNotEqual(stamp, path.stat().st_mtime_ns, "сохранения не записали файл")
        mark = self.mark("P14-switch")
        switch_addin = self._median(self._switch_times([asm, part], 5))
        self.assertNoPropertyWrites(mark, "переключение окон записало свойства")
        with self.s.eskd_muted():
            without = self._median(self._save_times(asm, 3))
            switch_plain = self._median(self._switch_times([asm, part], 5))
        report = {"save_with_addin_s": round(with_addin, 3), "save_without_s": round(without, 3),
                  "switch_with_addin_ms": round(switch_addin * 1000, 1), "switch_without_ms": round(switch_plain * 1000, 1)}
        self.path("performance.json").write_text(__import__("json").dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
        cpm = asm.Extension.CustomPropertyManager("")
        cpm.Delete2("P14_проба")
        self.assertLessEqual(with_addin - without, 2.0, f"сохранение A-09: {report}")
        self.assertLessEqual(switch_addin - switch_plain, 0.05, f"переключение окна: {report}")

    def _reopen_save_times(self, path, n):
        """Открыть, сразу пометить изменённым и сохранить, дождаться простоя надстройки, закрыть — n раз, с."""
        import time
        out = []
        for _ in range(n):
            doc = self.s.open(path)
            doc.SetSaveFlag()
            started = time.perf_counter()
            ok, err, _ = self.s.save(doc)
            self.s.wait_addin_idle(timeout=180.0)
            out.append(time.perf_counter() - started)
            self.assertTrue(ok, f"Save3 err={err}")
            self.s.close(doc)
        return out

    def test_P20_library_part_save_overhead(self):
        """P20 (сверка SW API 23.09.2026, №24): библиотечная деталь B-01 — сотня исполнений-типоразмеров, у каждого масса
        ссылкой «SW-Mass@@исполнение@…». Ctrl+S сразу после открытия длился ~29 с против 0,15 с без надстройки: свойства
        каждого исполнения читались с пересчётом (Get4, UseCached = false), и SolidWorks заново считал массу всех
        исполнений. Для сравнения с нужным значением пересчёт не нужен. Надстройка добавляет не больше 3 с (медиана трёх
        открытий с сохранением)."""
        path = [self.s.workspace_copy(src, subdir=self._case_name()) for src in paths.CORPUS_B_LIBRARY["B-01"]][0]
        doc = self.s.open(path)
        self.s.wait_addin_idle(timeout=180.0)
        doc.SetSaveFlag()
        self.s.save(doc)  # первое сохранение: надстройка дописывает свои реквизиты, в том числе массу, во все исполнения
        self.s.wait_addin_idle(timeout=180.0)
        self.s.close(doc)
        with_addin = self._median(self._reopen_save_times(path, 3))
        with self.s.eskd_muted():
            without = self._median(self._reopen_save_times(path, 3))
        # Этапы долгих синхронизаций (надстройка пишет их от 1 с) — чтобы при провале было видно, что тормозит.
        slow = [ln for ln in self.addin_log.new_lines() if " мс — " in ln]
        report = {"reopen_save_with_addin_s": round(with_addin, 3), "reopen_save_without_s": round(without, 3),
                  "slow_sync": slow[-8:]}
        self.path("performance_b01.json").write_text(__import__("json").dumps(report, ensure_ascii=False, indent=1),
                                                     encoding="utf-8")
        self.assertLessEqual(with_addin - without, 3.0, f"сохранение B-01 после открытия: {report}")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")


class PropertyOrderOnSave(SwTestCase):
    """Единый порядок пользовательских свойств (сверка SW API 23.09.2026, №23; решение владельца 24.09.2026): при
    сохранении по команде конструктора — словарь SWPlus по порядку строк, затем служебные, затем свои свойства
    конструктора в прежнем порядке, последними — «Примечание», «Формат», «Раздел», как их оставляет «Применить» MProp.
    Кнопки и открытие порядок не трогают; покупные и файлы других заказов — тоже."""

    def assertCanonical(self, dump, what):
        master, tail = oracles.property_master(), oracles.property_tail()
        for level, names in oracles.property_orders(dump).items():
            self.assertEqual(oracles.canonical(names, master, tail)[0], names, f"{what}: уровень «{level or 'общие'}»")

    @staticmethod
    def _orders(doc):
        """{уровень: [имена в порядке SolidWorks]} — только GetNames, без пересчёта значений."""
        out = {"": com.prop_names(doc.Extension.CustomPropertyManager(""))}
        for cfg in com.as_list(doc.GetConfigurationNames):
            out[str(cfg)] = com.prop_names(doc.Extension.CustomPropertyManager(str(cfg)))
        return out

    def _types(self, path, level, names):
        with self.s.eskd_muted():
            doc = self.s.open(path, readonly=True)
            try:
                cpm = doc.Extension.CustomPropertyManager(level)
                return {n: (int(cpm.GetType2(n)), (com.prop_get(cpm, n)[0] or "").replace("\r\n", "\n")) for n in names}
            finally:
                self.s.close(doc)

    @staticmethod
    def _to_end(cpm, name):
        """Перенести свойство в конец, как это делает MProp: удалить и добавить заново тем же текстом."""
        raw, _ = com.prop_get(cpm, name)
        rc = com.prop_set(cpm, name, raw)
        if rc != 0:
            raise RuntimeError(f"Add3({name}) вернул {rc}")

    def test_P21_sync_button_writes_in_place(self):
        """P21: кнопка «Синхронизировать» пишет существующее свойство на его строке. Раньше каждое записанное свойство
        уезжало в конец списка «Свойств файла» (Add3 DeleteAndAdd) — «Масса_Таблица» вставала за свойство конструктора.
        Сама кнопка порядок не наводит: без сохранения это лишняя правка."""
        path, doc = self.open_copy(A01)
        self.s.activate(doc)
        cpm = doc.Extension.CustomPropertyManager("00")
        self.assertEqual(0, com.prop_set(cpm, "Я_P21_проба", "x"), "своё свойство — в конец")
        self.assertEqual(0, int(cpm.Add3("Масса_Таблица", com.CUSTOM_INFO_TEXT, "", com.PROP_ADD_REPLACE)), "масса стёрта на месте")
        levels = {"00": cpm, "": doc.Extension.CustomPropertyManager("")}
        before = {level: com.prop_names(m) for level, m in levels.items()}
        self.assertGreater(int(com.call(self.s.eskd(), "SyncActiveDocumentSilent")), 0, "кнопка записала массу")
        for level, m in levels.items():
            after = com.prop_names(m)
            # Кнопка вправе удалить прежнюю общую копию (как MProp) и дописать новое в конец; остальное — на своих строках.
            kept = [n for n in before[level] if n in after]
            self.assertEqual(kept, after[:len(kept)], f"уровень «{level or 'общие'}»: записанное — на своих строках, новое — в конце")
        self.assertIn("SW-Mass@@", com.prop_get(cpm, "Масса_Таблица")[0] or "", "масса записана")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_P22_ctrl_s_puts_properties_in_single_order(self):
        """P22: «Обозначение» (общие) и первое свойство исполнения «00» перенесены в конец, как это делает MProp, и
        добавлены свои свойства конструктора трёх типов. После Ctrl+S каждый уровень на диске в едином порядке, свои
        свойства — в прежнем порядке сразу перед «Примечание», «Формат», «Раздел», с прежними типом и значением; масса
        осталась выражением."""
        path, doc = self.open_copy(A01)
        general = doc.Extension.CustomPropertyManager("")
        execution = doc.Extension.CustomPropertyManager("00")
        self._to_end(general, "Обозначение")
        self._to_end(execution, com.prop_names(execution)[0])
        own = (("P22_моё", 30, "а\nб"), ("P22_дата", 64, "24.09.2026"), ("P22_число", 3, "7"))
        for name, kind, value in own:
            self.assertEqual(0, int(general.Add3(name, kind, value, com.PROP_DELETE_AND_ADD)), name)
        types = {n: (int(general.GetType2(n)), (com.prop_get(general, n)[0] or "").replace("\r\n", "\n")) for n, _, _ in own}
        doc.SetSaveFlag()
        self.s.run_command(doc, 2)
        self.s.wait_addin_idle(timeout=60.0)
        self.s.close(doc)
        disk = self.persisted(path)
        self.assertCanonical(disk, "после Ctrl+S")
        general_names = list(disk["general"])
        tail = [n for n in oracles.property_tail() if n in general_names]
        self.assertEqual(["P22_моё", "P22_дата", "P22_число"] + tail, general_names[-3 - len(tail):],
                         "свои — как завёл конструктор, за ними «Примечание», «Формат», «Раздел»")
        self.assertEqual(types, self._types(path, "", [n for n, _, _ in own]), "тип и значение своих свойств")
        self.assertIn("SW-Mass@@00@", V(disk, "Масса_Таблица", "00") or "", "масса — выражение")
        self.assertRegex(re.sub(r"<[^>]*>", "", V(disk, "Масса_Таблица", "00", resolved=True) or ""), r"^\s*0\.\d+", "масса — число")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_P23_new_part_is_saved_in_single_order(self):
        """P23: новая деталь из шаблона после «Сохранить как» (пересохранение надстройки) — все уровни на диске в едином
        порядке. Раньше свойства шли в порядке записи: шаблон «Материал, Масса, Формат», «Масса_Таблица» раньше «Масса_ФБ»."""
        doc, _ = build.plate(self.s, 200, 100, 4, SHEET4)
        target = self.path("ПРТИ.468211.141 Пластина порядок.sldprt")
        self.s.save_as(doc, target)
        self.s.wait_addin_idle(timeout=60.0)
        self.s.close(doc)
        self.assertCanonical(self.persisted(target), "новая деталь")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_P24_purchased_and_foreign_order_keep_their_order(self):
        """P24: покупное изделие (свойства ВП) и скрытая деталь другого заказа, сохранённая вместе со сборкой, порядок
        свойств не получают: надстройка не переставляет то, что ей не принадлежит. Своя скрытая деталь той же сборки —
        получает."""
        path, doc = self.open_copy("Электродвигатель АИР71А4.sldprt")
        general = doc.Extension.CustomPropertyManager("")
        self._to_end(general, com.prop_names(general)[0])
        before = com.prop_names(general)
        doc.SetSaveFlag()
        self.s.run_command(doc, 2)
        self.s.wait_addin_idle(timeout=60.0)
        self.s.close(doc)
        self.assertEqual(before, list(self.persisted(path)["general"]), "покупное — порядок прежний")

        root = self.path("_Заявки")
        own_path = root / "111 Свой" / "02_Металл" / "И01_ПРТИ.468211.150" / "01_3D" / "ПРТИ.468211.151 Пластина своя.sldprt"
        foreign_path = root / "222 Чужой" / "02_Металл" / "И01_ПРТИ.468211.160" / "01_3D" / "ПРТИ.468211.161 Пластина чужая.sldprt"
        parts = []
        for part_path in (own_path, foreign_path):
            part, _ = build.plate(self.s, 120, 60, 3, None)
            self.s.save_as(part, part_path)
            self.s.wait_addin_idle(timeout=60.0)
            parts.append(part)
        asm, _ = build.assembly(self.s, [(own_path, 0, 0, 0), (foreign_path, 0.3, 0, 0)])
        asm_path = own_path.parent / "ПРТИ.468211.150 СБ Узел.sldasm"
        self.s.save_as(asm, asm_path)
        self.s.wait_addin_idle(timeout=60.0)
        for part in parts:
            self.s.sw.CloseDoc(part.GetTitle)
        self.s._opened[:] = [d for d in self.s._opened if all(d is not p for p in parts)]
        self.wait_idle(2.0)
        orders = {}
        for part_path in (own_path, foreign_path):
            hidden = self.s.sw.GetOpenDocumentByName(str(part_path))
            self.assertIsNotNone(hidden, f"деталь в памяти сборки: {part_path.name}")
            cpm = hidden.Extension.CustomPropertyManager("")
            self._to_end(cpm, "Обозначение")
            orders[part_path] = com.prop_names(cpm)
            hidden.SetSaveFlag()
        self.s.activate(asm)
        err, warn = com.ref_int(), com.ref_int()
        asm.Save3(com.SAVE_SILENT | 4, err, warn)  # swSaveAsOptions_SaveReferenced
        self.s.wait_addin_idle(timeout=60.0)
        self.s.close_all()
        own = self.persisted(own_path)
        self.assertCanonical(own, "своя скрытая деталь")
        foreign = list(self.persisted(foreign_path)["general"])
        self.assertEqual(orders[foreign_path], foreign[:len(orders[foreign_path])], "деталь другого заказа — порядок прежний")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")


    def test_P25_save_after_mprop_like_tail_moves_nothing(self):
        """P25 (решение владельца 24.09.2026): после Ctrl+S «Примечание», «Формат», «Раздел» уже последние. Их удаляют и
        дописывают в конец, как «Применить» MProp (FrmMProp 3021–3053), — порядок тот же, и следующий Ctrl+S ничего не
        переставляет. Раньше сохранение возвращало их на строки словаря, а MProp снова уносил в конец — по кругу."""
        path, doc = self.open_copy(A01)
        own = doc.Extension.CustomPropertyManager("00")
        self.assertEqual(0, int(own.Add3("P25_моё", com.CUSTOM_INFO_TEXT, "x", com.PROP_DELETE_AND_ADD)), "своё свойство")
        doc.SetSaveFlag()
        self.s.run_command(doc, 2)
        self.s.wait_addin_idle(timeout=60.0)
        stable = self._orders(doc)
        # У A01 «Формат» — в общих свойствах, в «00» хвоста нет: проверяются все уровни, где он есть.
        tails = {level: [n for n in oracles.property_tail() if n in names] for level, names in stable.items()}
        self.assertTrue(any(tails.values()), f"«Примечание», «Формат» или «Раздел» есть хотя бы на одном уровне: {stable}")
        for level, tail in tails.items():
            if tail:
                self.assertEqual(tail, stable[level][-len(tail):], f"на уровне «{level or 'общие'}» они последние")
        self.assertEqual(len(stable["00"]) - len(tails["00"]) - 1, stable["00"].index("P25_моё"),
                         f"своё свойство в «00» — последним перед хвостом: {stable['00']}")
        for level in ("", "00"):
            cpm = doc.Extension.CustomPropertyManager(level)
            for name in oracles.property_tail():
                if name in stable[level]:
                    self._to_end(cpm, name)
        self.assertEqual(stable, self._orders(doc), "как после MProp: порядок тот же")
        doc.SetSaveFlag()
        self.s.run_command(doc, 2)
        self.s.wait_addin_idle(timeout=60.0)
        self.assertEqual(stable, self._orders(doc), "сохранение ничего не переставило")
        self.s.close(doc)
        self.assertEqual(stable, oracles.property_orders(self.persisted(path)), "на диске тот же порядок")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")


class PersistenceOpen(SwTestCase):

    def _assert_open_is_read_only(self, path, doc):
        self.assertFalse(bool(doc.GetSaveFlag), "документ помечен изменённым сразу после открытия")
        memory, disk = self.memory_equals_disk(doc, path)
        self.assertEqual(disk, memory, "свойства в памяти отличаются от файла — надстройка писала при открытии")

    @tags("smoke")
    @known_defect("Д-03")
    def test_P09_open_part_does_not_modify(self):
        """P09: открытие детали ничего не меняет."""
        path, doc = self.open_copy(A01)
        self._assert_open_is_read_only(path, doc)

    @known_defect("Д-03")
    def test_P09_open_assembly_does_not_modify(self):
        """P09: открытие сборки ничего не меняет."""
        self.copy_fixtures(A01, A04)
        path, doc = self.open_copy(A08)
        self._assert_open_is_read_only(path, doc)

    @known_defect("Д-06")
    def test_P09_open_drawing_does_not_modify(self):
        """P09: открытие чертежа не меняет ни чертёж, ни модель."""
        self.copy_fixture(A01)
        path, doc = self.open_copy(A10)
        self._assert_open_is_read_only(path, doc)

    @known_defect("Д-03")
    def test_P09_open_real_part_does_not_modify(self):
        """P09: открытие реальной детали корпуса Б (B-01 с сортаментом из библиотеки) ничего не меняет."""
        src = paths.CORPUS_B_LIBRARY["B-01"][0]
        path = self.s.workspace_copy(src, subdir=self._case_name())
        doc = self.s.open(path)
        self._assert_open_is_read_only(path, doc)

    @known_defect("Д-04")
    def test_P09_switching_windows_writes_nothing(self):
        """P09: переключение между открытыми документами не пишет свойства и не помечает их изменёнными."""
        _, part = self.open_copy(A01)
        _, assembly = self.open_copy(A08, A04)
        mark = self.mark("P09-switch")
        for doc in (part, assembly, part, assembly):
            self.s.activate(doc)
            self.wait_idle(1.0)
        self.assertNoPropertyWrites(mark, "переключение окон изменило свойства")
        self.assertFalse(bool(part.GetSaveFlag), "деталь помечена изменённой")
        self.assertFalse(bool(assembly.GetSaveFlag), "сборка помечена изменённой")

    def test_P10_no_errors_in_addin_log_during_save_cycle(self):
        """P10: полный цикл открытие → сохранение → «Сохранить как» без ошибок в журнале надстройки."""
        path, doc = self.open_copy(A01)
        self.s.save(doc)
        self.s.save_as(doc, self.path("ПРТИ.468211.161 Пластина журнал.sldprt"))
        self.wait_idle()
        self.s.close(doc)
        self.assertEqual([], self.addin_errors())

    def test_P11_material_command_updates_stamp_before_save(self):
        """P11: закрытие команды материала (swCommands_Favorite_Material_2 = 2008) — дробь в памяти до сохранения, на диске после (Д-05).

        RunCommand избранного без выделения в интерфейсе материал не меняет, поэтому материал назначается через API,
        а команда проверяет перехват CommandCloseNotify надстройкой.
        """
        path, doc = self.open_copy(A01)
        build.set_material(doc, SHEET6, "00")
        self.wait_idle()
        before = V(oracles.dump_properties(doc), "Материал_ФБ", "00") or ""
        self.assertNotIn("6,0", before, "без команды и сохранения надстройка не должна была обновить дробь")
        self.assertTrue(self.s.run_command(doc, 2008), "команда не выполнена")
        self.wait_idle()
        self.assertIn("6,0", V(oracles.dump_properties(doc), "Материал_ФБ", "00") or "", "Материал_ФБ в памяти после команды")
        self.s.save(doc)
        self.s.close(doc)
        self.assertIn("6,0", V(self.persisted(path), "Материал_ФБ", "00") or "", "Материал_ФБ на диске")

    def test_P12_api_material_change_reaches_disk_on_save(self):
        """P12: смена материала через API → после сохранения новая дробь на диске."""
        path, doc = self.open_copy(A01)
        build.set_material(doc, SHEET6, "00")
        self.s.save(doc)
        self.s.close(doc)
        self.assertIn("6,0", V(self.persisted(path), "Материал_ФБ", "00") or "")


class HiddenDocument(SwTestCase):
    """Деталь, чьё окно закрыто, а сама она осталась в памяти как компонент открытой сборки (сверка SW API 23.09.2026,
    №9). Раньше надстройка по DestroyNotify без типа считала такую деталь закрытой и больше её не синхронизировала."""

    def _hidden_part(self, name):
        from eskd_e2e import build

        part_path = self.path(name)
        doc, _ = build.plate(self.s, 120, 60, 3, None)
        self.s.save_as(doc, part_path)
        self.s.wait_addin_idle()
        asm, _ = build.assembly(self.s, [(part_path, 0, 0, 0)])
        asm_path = self.path("ПРТИ.468211.320 СБ Сборка скрытая.sldasm")
        self.s.save_as(asm, asm_path)
        self.s.wait_addin_idle()
        self.s.sw.CloseDoc(doc.GetTitle)
        self.s._opened[:] = [d for d in self.s._opened if d is not doc]
        self.wait_idle(2.0)
        hidden = self.s.sw.GetOpenDocumentByName(str(part_path))
        self.assertIsNotNone(hidden, "деталь осталась в памяти сборки")
        return part_path, hidden, asm

    def test_P17_hidden_part_saved_from_assembly_is_synced(self):
        """P17: окно детали закрыто, сборка открыта; обозначение детали стёрто и деталь сохранена — надстройка
        восстанавливает обозначение, как у любой сохранённой детали."""
        from eskd_e2e import build

        part_path, hidden, asm = self._hidden_part("ПРТИ.468211.321 Пластина скрытая.sldprt")
        build.props(hidden, {"Обозначение": ""})
        err, warn = com.ref_int(), com.ref_int()
        hidden.Save3(com.SAVE_SILENT, err, warn)
        self.s.wait_addin_idle()
        self.wait_idle(2.0)
        self.s.close_all()
        self.assertEqual("ПРТИ.468211.321", V(self.persisted(part_path), "Обозначение"), "обозначение восстановлено")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")

    def test_P18_hidden_then_closed_part_is_tracked_again(self):
        """P18: деталь скрыта, затем сборка закрыта (деталь разрушена), деталь открыта снова — учёт новый, без мёртвой
        записи: стёртое обозначение восстанавливается при сохранении, ошибок в журнале нет."""
        from eskd_e2e import build

        part_path, hidden, asm = self._hidden_part("ПРТИ.468211.322 Пластина скрытая.sldprt")
        hidden = None
        self.s.close_all()
        self.wait_idle(2.0)
        doc = self.s.open(part_path)
        build.props(doc, {"Обозначение": ""})
        self.s.save(doc)
        self.s.close(doc)
        self.assertEqual("ПРТИ.468211.322", V(self.persisted(part_path), "Обозначение"), "обозначение восстановлено")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")


class OpenDiagnostics(SwTestCase):

    def test_P19_open_diagnostics_line_comes_in_idle(self):
        """P19 (сверка SW API 23.09.2026, №11): строка «Реквизиты … будут обновлены при сохранении» при открытии документа
        с устаревшими реквизитами по-прежнему пишется — теперь в простое, открытие её не ждёт; документ не меняется."""
        from eskd_e2e import build

        path = self.path("ПРТИ.468211.341 Пластина диагностика.sldprt")
        doc, _ = build.plate(self.s, 100, 50, 3, None)
        self.s.save_as(doc, path)
        self.s.wait_addin_idle()
        with self.s.eskd_muted():
            build.props(doc, {"Обозначение": ""})
            self.s.save(doc)
            self.s.close(doc)
        self.addin_log = oracles.AddinLog()
        doc = self.s.open(path)
        self.s.wait_addin_idle()
        self.wait_idle(2.0)
        lines = self.addin_log.new_lines()
        self.assertTrue(any("Реквизиты «" in line and "будут обновлены при сохранении" in line for line in lines),
                        "строка диагностики при открытии")
        self.assertFalse(bool(doc.GetSaveFlag), "открытие не пометило документ изменённым")
        memory, disk = self.memory_equals_disk(doc, path)
        self.assertEqual(disk, memory, "свойства в памяти отличаются от файла — надстройка писала при открытии")
        self.assertEqual([], self.addin_errors(), "ошибки в журнале надстройки")


class BasicMaterialScenario(SwTestCase):
    """Базовый сценарий конструктора: деталь из шаблона → материал из библиотеки → «Сохранить как» → чертёж."""

    def _check(self, doc, file_name, material, shape):
        custom = build.material_library()[material]["custom"]
        designation, line = custom["Обозначение_ГОСТ"], custom["Обозначение_Строка"]
        self.assertTrue(designation.startswith(shape + " <STACK size=1>"), f"библиотека: {designation}")
        numerator, denominator = designation.split("<STACK size=1>")[1].split("</STACK>")[0].split("<OVER>")
        target = self.path(file_name)
        self.s.save_as(doc, target)
        self.wait_idle()
        self.s.close(doc)
        disk = self.persisted(target)
        self.assertEqual("<FONT size=1.8> <FONT size=3.5>" + designation, V(disk, "Материал_ФБ", "00"),
                         "графа 3 — «Обозначение_ГОСТ» библиотеки в разметке MProp")
        self.assertEqual(designation, V(disk, "Материал_Таблица", "00"), "таблица — «Обозначение_ГОСТ», как пишет MProp")
        self.assertEqual(line, V(disk, "Материал_Строка", "00"), "одна строка для сводной ведомости — «Обозначение_Строка»")

        model = self.s.open(target)
        drw = self.s.new_doc(paths.DRAWING_TEMPLATE)
        build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
        build.model_view(drw, target, 150, 180)
        build.wait(1.0)
        pdf = self.path(target.stem + ".pdf")
        ok, err, _ = self.s.save_as(drw, pdf)
        self.s.close(drw)
        self.s.close(model)
        self.assertTrue(ok and pdf.exists(), f"PDF не выгружен, код {err}")
        rows = oracles.pdf_rows(pdf, 420, oracles.form1_cells(420)["g3_material"])
        self.assertEqual([numerator, shape, denominator], [text for _, _, text in rows],
                         f"графа 3 в PDF: над чертой сортамент, форма по центру, под чертой марка: {rows}")

    @known_defect("Д-34")
    def test_P15_sheet_material_fraction_in_stamp_and_one_line_record(self):
        """P15: пластина из «Лист 4,0 … / Ст3сп …» — в графе 3 чертежа «Лист» и дробь в две строки, одна строка для ведомости."""
        doc, _ = build.plate(self.s, 200, 100, 4, SHEET4)
        self._check(doc, "ПРТИ.468211.131 Пластина.sldprt", SHEET4, "Лист")

    @known_defect("Д-34")
    def test_P15_tube_material_fraction_in_stamp_and_one_line_record(self):
        """P15: стойка из «Труба 80х80х4,0 … / В 10 …» — в графе 3 чертежа «Труба» и дробь в две строки, одна строка для ведомости."""
        doc, _ = build.square_tube(self.s, 80, 4, 300, TUBE80)
        self._check(doc, "ПРТИ.468211.132 Стойка.sldprt", TUBE80, "Труба")


class RealPartLibraryMaterial(SwTestCase):
    """Корпус Б: реальная труба B-01, в которой конструктор назначил сортамент из библиотеки (fixtures/build_corpus_b.py)."""

    @known_defect("Д-46")
    def test_P16_real_tube_with_library_sortament_gets_library_fraction(self):
        """P16: реальная деталь B-01, конфигурация «Труба 80х80х4»: материал — «Труба 80х80х4,0 ГОСТ 8639-82 / В 10 …» из
        библиотеки, в графе 3 осталась старая дробь MProp → сохранение: графа 3 и таблица — дробь библиотеки, «Материал_Строка»
        — строка библиотеки, масса графы 5 — по материалу; PDF чертежа B-01: «Труба» и дробь в две строки в графе 3."""
        part_src, drawing_src = paths.CORPUS_B_LIBRARY["B-01"]
        fixture = testing.manifest()["corpus_b"]["B-01"]
        cfg = "Труба 80х80х4"
        part = self.s.workspace_copy(part_src, subdir=self._case_name())
        drawing = self.s.workspace_copy(drawing_src, subdir=self._case_name())
        self.assertIn("<STACK size=1>Труба 80х80х4 ГОСТ 8639-82<OVER>", V(self.persisted(part), "Материал_ФБ", cfg) or "",
                      "в копии реальной детали — старая дробь MProp")
        doc = self.s.open(part)
        self.assertEqual((TUBE80, "Библиотека_Материалов_ГОСТ"), build.material_of(doc, cfg), "в детали назначен сортамент из библиотеки")
        self.s.save(doc)
        custom = build.material_library()[TUBE80]["custom"]
        drw = self.s.open(drawing)
        width = round(float(com.as_list(com.dyn(drw.GetCurrentSheet).GetProperties2)[5]) * 1000.0)
        pdf = self.path("ПРТИ.468211.010.pdf")
        ok, err, _ = self.s.save_as(drw, pdf)
        self.s.close(drw)
        self.s.close(doc)
        disk = self.persisted(part)
        self.assertEqual("<FONT size=1.8> <FONT size=3.5>" + custom["Обозначение_ГОСТ"], V(disk, "Материал_ФБ", cfg), "графа 3 — дробь библиотеки")
        self.assertEqual(custom["Обозначение_ГОСТ"], V(disk, "Материал_Таблица", cfg), "таблица — дробь библиотеки")
        self.assertEqual(custom["Обозначение_Строка"], V(disk, "Материал_Строка", cfg), "сводная ведомость — строка библиотеки")
        self.assertEqual(mprop_mass(cfg, part.stem), (V(disk, "Масса_ФБ", cfg) or "").replace("\r\n", "\n"), "графа 5 — выражение MProp")
        self.assertIn("%.2f" % fixture["mass_kg"][cfg], V(disk, "Масса_ФБ", cfg, resolved=True) or "", "графа 5 — масса трубы из материала библиотеки")
        self.assertTrue(ok and pdf.exists(), f"PDF не выгружен, код {err}")
        numerator, denominator = custom["Обозначение_ГОСТ"].split("<STACK size=1>")[1].split("</STACK>")[0].split("<OVER>")
        rows = [text for _, _, text in oracles.pdf_rows(pdf, width, oracles.form1_cells(width)["g3_material"])]
        self.assertEqual([numerator, "Труба", denominator], rows, f"графа 3 в PDF чертежа B-01: {rows}")


if __name__ == "__main__":
    unittest.main()
