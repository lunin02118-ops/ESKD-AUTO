# -*- coding: utf-8 -*-
"""E2E, группа R — golden master на корпусах А и Б и миграция файлов v5 (план, §4.7). R02 добавляется с корпусом В."""
import csv
import hashlib
import json
import subprocess
import unittest
from pathlib import Path

from eskd_e2e import oracles, paths
from eskd_e2e.testing import SwTestCase

V = oracles.value
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A04 = "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt"
A14_PART = "ПРТИ.468211.107 Кожух.sldprt"
A14_ASSEMBLY = "ПРТИ.468211.108 СБ Узел.sldasm"
LEGACY = {"Разраб.", "Разработал", "Автор", "п_Разраб", "DrawnBy", "п_Разраб_Дата", "DrawnDate", "Пров.", "п_Пров",
          "CheckedBy", "п_Пров_Дата", "Организация", "Организация_ФБ", "Компания", "Firm", "Organization", "PartNo",
          "Сортамент", "ГОСТ_Сортамент", "ГОСТ_Материал", "БЧ"}
# Что ещё вправе трогать миграция: копии на чужих уровнях MProp, живые «Масса» и «Материал», «Формат» кириллицей.
DELETABLE = LEGACY | {"Проверил", "Контора", "Конструктор", "Наименование", "Наименование_ФБ", "Масса_ФБ", "Масса_Таблица",
                      "Материал_ФБ", "Материал_Таблица", "Масса", "Материал", "Сборка2_ФБ"}
WRITABLE = {"Конструктор", "Проверил", "Контора", "Масса", "Материал", "Формат", "Сборка1_ФБ",
            # правила формата MProp, как при сохранении (WP-2.10)
            "Масса_ФБ", "Масса_Таблица", "Примечание", "Сборка2_ФБ"}


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class Migration(SwTestCase):

    def _copy_a14(self):
        for component in (A01, A04):
            self.copy_fixture(component)
        return self.copy_fixture(A14_PART), self.copy_fixture(A14_ASSEMBLY)

    def _clean(self, apply, report):
        """ESKD_Sync.exe /clean — отдельный процесс подключается к SolidWorks сессии тестов и сам открывает и закрывает файлы."""
        args = [str(paths.ESKD_SYNC_EXE), "/clean", str(self.case_dir), "/report", str(report)] + (["/apply"] if apply else [])
        backups_before = set(self.case_dir.glob("_ESKD_backup_*"))
        # зонд не должен держать подписку на документы, которые закроет утилита
        self.s.probe.call("doc_events", "0")
        try:
            proc = subprocess.run(args, capture_output=True, timeout=600)
        finally:
            self.s.probe.call("doc_events", "1")
        output = (proc.stdout + proc.stderr).decode("utf-8", errors="replace")
        self.assertEqual(0, proc.returncode, f"ESKD_Sync.exe /clean завершился с кодом {proc.returncode}: {output}")
        self.assertTrue(Path(report).exists(), f"нет отчёта: {output}")
        backups = sorted(set(self.case_dir.glob("_ESKD_backup_*")) - backups_before)
        return output, Path(report), str(backups[-1]) if backups else ""

    @staticmethod
    def _rows(report):
        with open(report, encoding="utf-8-sig", newline="") as f:
            return list(csv.DictReader(f, delimiter=";"))

    def _models(self):
        return {p.name: sha256(p) for p in self.case_dir.iterdir() if p.suffix.lower() in (".sldprt", ".sldasm")}

    def test_R03_dry_run_reports_only_legacy_cleanup(self):
        """R03: пробный прогон — отчёт CSV перечисляет только лишние имена v5, перенос подписей и уровни MProp; файлы не меняются."""
        part, assembly = self._copy_a14()
        legacy_in_part = LEGACY & oracles.all_names(self.persisted(part))
        self.assertTrue(legacy_in_part, "фикстура A-14 должна содержать алиасы v5")
        before = self._models()
        summary, report, backup = self._clean(False, self.path("report_dry.csv"))
        rows = [r for r in self._rows(report) if r["Действие"] != "пропуск"]
        deleted = {r["Имя"] for r in rows if r["Действие"] == "удалить"}
        written = {r["Имя"] for r in rows if r["Действие"] == "записать"}
        self.assertTrue(legacy_in_part <= deleted, f"в отчёте нет удаления {sorted(legacy_in_part - deleted)}")
        self.assertEqual(set(), deleted - DELETABLE, "миграция удаляет словарные или служебные имена")
        self.assertEqual(set(), written - WRITABLE, "миграция пишет имена вне своей задачи")
        for r in rows:
            if r["Действие"] == "удалить" and r["Имя"] in ("Масса_ФБ", "Материал_ФБ", "Проверил", "Контора", "Сборка2_ФБ"):
                self.assertEqual("общие", r["Уровень"], f"удаление конфигурационного значения: {r}")
            if r["Действие"] == "удалить" and r["Имя"] in ("Конструктор", "Наименование", "Наименование_ФБ"):
                self.assertNotEqual("общие", r["Уровень"], f"удаление общего значения: {r}")
        files = {r["Файл"] for r in rows}
        self.assertTrue({str(part), str(assembly)} <= files, f"в отчёте нет моделей A-14: {sorted(files)}")
        self.assertTrue(all(Path(f).parent == self.case_dir for f in files), "в отчёте файлы вне каталога")
        self.assertEqual(before, self._models(), "пробный прогон изменил файлы")
        self.assertEqual("", backup, "резервные копии при пробном прогоне")

    def test_R04_apply_removes_aliases_moves_signatures_and_is_idempotent(self):
        """R04: применение — лишних имён нет, подписи на уровнях MProp, резервные копии исходников, повторный прогон без изменений."""
        part, assembly = self._copy_a14()
        originals = {p.name: sha256(p) for p in (part, assembly)}
        summary, report, backup = self._clean(True, self.path("report_apply.csv"))
        self.assertTrue(backup and Path(backup).is_dir(), f"нет каталога резервных копий: {summary}")
        for name, digest in originals.items():
            self.assertEqual(digest, sha256(Path(backup) / name), f"резервная копия {name} не совпадает с исходником")
        for path in (part, assembly):
            with self.subTest(file=path.name):
                disk = self.persisted(path)
                self.assertEqual(set(), LEGACY & oracles.all_names(disk), "лишние имена v5 остались")
                self.assertEqual("Тестов Т.Т.", V(disk, "Конструктор"), "конструктор в общих")
                self.assertIsNone(V(disk, "Конструктор", "00"), "копия конструктора в конфигурации")
                self.assertEqual("Проверкин П.П.", V(disk, "Проверил", "00"), "проверил в конфигурации")
                self.assertEqual("ООО «Испытание»", V(disk, "Контора", "00"), "контора в конфигурации")
                self.assertIsNone(V(disk, "Проверил"), "общая копия «Проверил»")
                self.assertIn("SW-Mass", V(disk, "Масса") or "", "живая масса")
                self.assertNotRegex(V(disk, "Формат") or "", "[A-Za-z]", "«Формат» кириллицей")
        again, report_again, _ = self._clean(False, self.path("report_again.csv"))
        self.assertEqual([], [r for r in self._rows(report_again) if r["Действие"] != "пропуск"],
                         f"повторный прогон нашёл изменения: {again}")


class GoldenMaster(SwTestCase):

    def test_R01_behaviour_differs_from_v5_only_by_explained_changes(self):
        """R01: сценарии golden master (корпуса А и Б) на текущей надстройке — каждое отличие от снимка baseline/v5
        объяснено правилом baseline/allowed_diffs.json со ссылкой на устранённый дефект."""
        from baseline import capture, compare

        snapshot = self.path("snapshot_v6")
        snapshot.mkdir(parents=True, exist_ok=True)
        failures = {}
        for name, scenario in capture.SCENARIOS:
            log = oracles.AddinLog()
            try:
                snap = scenario(self.s, name)
            except Exception as exc:  # сценарий упал — фиксируем и идём дальше, итог проверяется ниже
                failures[name] = repr(exc)
                self.s.close_all()
                continue
            snap["addin_log_errors"] = log.errors()
            snap["unexpected_dialogs"] = self.s.watchdog.pop_unexpected()
            snap = capture.normalize(snap, self.s.run_dir)
            (snapshot / f"{name}.json").write_text(json.dumps(snap, ensure_ascii=False, indent=1, sort_keys=True),
                                                  encoding="utf-8")
        self.assertEqual({}, failures, "сценарии golden master упали")
        rules = json.loads((paths.TESTS / "baseline" / "allowed_diffs.json").read_text(encoding="utf-8"))["rules"]
        all_diffs, missing = compare.diffs(paths.TESTS / "baseline" / "v5", snapshot)
        self.assertEqual([], missing, "сценарии снимка v5 без пары")
        unexplained, used = compare.explain(all_diffs, rules)
        self.assertEqual([], [compare.describe(d) for d in unexplained], "отличия от v5 без правила в allowed_diffs.json")
        self.assertEqual([], compare.unused(used, rules), "правила allowed_diffs.json, которым нечего объяснять (Д-43)")


if __name__ == "__main__":
    unittest.main()
