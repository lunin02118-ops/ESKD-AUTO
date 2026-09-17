# -*- coding: utf-8 -*-
"""Запуск автотестов системы ЕСКД.

    python 09_Тесты/run_tests.py static            # T0: без SolidWorks
    python 09_Тесты/run_tests.py unit              # T1: юнит-тесты C# (ESKD.Tests.exe)
    python 09_Тесты/run_tests.py contract          # T2: контракт событий SolidWorks
    python 09_Тесты/run_tests.py e2e               # T3: сквозные сценарии
    python 09_Тесты/run_tests.py smoke             # быстрый набор перед коммитом
    python 09_Тесты/run_tests.py full              # всё
    python 09_Тесты/run_tests.py e2e -k P0         # фильтр по части имени теста
    python 09_Тесты/run_tests.py e2e -k M12,P11    # несколько фильтров через запятую

Сценарии с SolidWorks запускаются только при закрытом SolidWorks: тесты поднимают
собственную сессию и никогда не трогают документы пользователя.
Отчёты: 08_Результаты_Тестирования/runs/<дата-время>/report.md и junit.xml.
"""
import argparse
import os
import sys
import time
import traceback
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from eskd_e2e import paths, testing  # noqa: E402

SUITES = {
    "static": ["tests.test_static"],
    "unit": ["tests.test_unit"],
    "contract": ["tests.test_contract"],
    "e2e": ["tests.test_e2e_persistence", "tests.test_e2e_model", "tests.test_e2e_bch",
            "tests.test_e2e_drawing", "tests.test_e2e_spec", "tests.test_e2e_real", "tests.test_e2e_install", "tests.test_e2e_mprop",
            "tests.test_e2e_stamp", "tests.test_e2e_order_structure", "tests.test_e2e_lzk"],
}
SUITES["full"] = SUITES["static"] + SUITES["unit"] + SUITES["contract"] + SUITES["e2e"]
SUITES["smoke"] = SUITES["static"] + SUITES["unit"] + SUITES["contract"]
SMOKE_TAG = "smoke"


class Recorder(unittest.TextTestResult):
    def __init__(self, *a, **kw):
        super().__init__(*a, **kw)
        self.records = []
        self._t0 = {}

    def startTest(self, test):
        self._t0[test.id()] = time.time()
        super().startTest(test)

    def _rec(self, test, status, detail=""):
        # ошибка setUpClass приходит объектом _ErrorHolder без имени метода
        method = getattr(test, getattr(test, "_testMethodName", ""), None)
        doc = getattr(method, "__doc__", "") or ""
        defect = getattr(method, "__eskd_defect__", "")
        self.records.append({"id": test.id(), "status": status, "detail": detail,
                             "seconds": round(time.time() - self._t0.get(test.id(), time.time()), 2),
                             "doc": doc.strip().splitlines()[0] if doc.strip() else "", "defect": defect})

    def addSuccess(self, test):
        super().addSuccess(test)
        self._rec(test, "pass")

    def addFailure(self, test, err):
        super().addFailure(test, err)
        self._rec(test, "fail", self._exc_info_to_string(err, test))

    def addError(self, test, err):
        super().addError(test, err)
        self._rec(test, "error", self._exc_info_to_string(err, test))

    def addSkip(self, test, reason):
        super().addSkip(test, reason)
        self._rec(test, "skip", reason)

    def addExpectedFailure(self, test, err):
        super().addExpectedFailure(test, err)
        self._rec(test, "xfail", self._exc_info_to_string(err, test).strip().splitlines()[-1])

    def addUnexpectedSuccess(self, test):
        super().addUnexpectedSuccess(test)
        self._rec(test, "xpass", "дефект, по-видимому, исправлен — обновите defects.json")


def load(names, pattern):
    loader = unittest.TestLoader()
    suite = unittest.TestSuite()
    for name in names:
        try:
            mod_suite = loader.loadTestsFromName(name)
        except Exception:
            print(f"  [пропуск] модуль {name} не загружен:\n{traceback.format_exc()}")
            continue
        for test in iter_tests(mod_suite):
            if pattern and not any(part.strip().lower() in test.id().lower() for part in pattern.split(",") if part.strip()):
                continue
            suite.addTest(test)
    return suite


def iter_tests(suite):
    for item in suite:
        if isinstance(item, unittest.TestSuite):
            yield from iter_tests(item)
        else:
            yield item


def write_reports(records, out_dir, title, started):
    out_dir.mkdir(parents=True, exist_ok=True)
    counts = {}
    for r in records:
        counts[r["status"]] = counts.get(r["status"], 0) + 1
    suite = ET.Element("testsuite", name=title, tests=str(len(records)),
                       failures=str(counts.get("fail", 0) + counts.get("xpass", 0)), errors=str(counts.get("error", 0)),
                       skipped=str(counts.get("skip", 0) + counts.get("xfail", 0)))
    for r in records:
        case = ET.SubElement(suite, "testcase", classname=r["id"].rsplit(".", 1)[0], name=r["id"].rsplit(".", 1)[-1],
                             time=str(r["seconds"]))
        if r["status"] in ("fail", "xpass"):
            ET.SubElement(case, "failure", message=r["status"]).text = r["detail"]
        elif r["status"] == "error":
            ET.SubElement(case, "error", message="error").text = r["detail"]
        elif r["status"] in ("skip", "xfail"):
            ET.SubElement(case, "skipped", message=r["status"]).text = r["detail"]
    ET.ElementTree(suite).write(out_dir / "junit.xml", encoding="utf-8", xml_declaration=True)

    labels = {"pass": "✅ пройден", "fail": "❌ провален", "error": "💥 ошибка", "skip": "⏭ пропущен",
              "xfail": "🟡 известный дефект", "xpass": "⚠ дефект больше не воспроизводится"}
    lines = [f"# Отчёт автотестов ЕСКД — {title}", "",
             f"Начало: {started}  ·  длительность: {round(time.time() - time.mktime(time.strptime(started, '%Y-%m-%d %H:%M:%S')))} с",
             f"DLL надстройки: `{testing._session_options['eskd_dll'] or paths.ADDIN_DLL}`", "",
             "| Итог | Количество |", "|---|---:|"]
    for key in ("pass", "xfail", "fail", "error", "xpass", "skip"):
        if counts.get(key):
            lines.append(f"| {labels[key]} | {counts[key]} |")
    lines += ["", "| Тест | Итог | Дефект | с | Описание |", "|---|---|---|---:|---|"]
    for r in records:
        lines.append(f"| `{r['id'].split('tests.')[-1]}` | {labels[r['status']]} | {r['defect']} | {r['seconds']} | {r['doc']} |")
    problems = [r for r in records if r["status"] in ("fail", "error", "xpass")]
    if problems:
        lines += ["", "## Подробности"]
        for r in problems:
            lines += ["", f"### {r['id']}", "```", r["detail"].strip()[-4000:], "```"]
    (out_dir / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return counts


def main():
    ap = argparse.ArgumentParser(description="Автотесты системы ЕСКД")
    ap.add_argument("suite", choices=sorted(SUITES))
    ap.add_argument("-k", dest="pattern", default="", help="фильтр по части идентификатора теста; несколько — через запятую")
    ap.add_argument("--eskd-dll", default=None, help="проверять другую сборку надстройки")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    started = time.strftime("%Y-%m-%d %H:%M:%S")
    testing.configure(eskd_dll=args.eskd_dll)
    names = SUITES[args.suite]
    suite = load(names, args.pattern)
    if args.suite == "smoke":
        extra = load(SUITES["e2e"], args.pattern)
        for t in iter_tests(extra):
            fn = getattr(t, t._testMethodName, None)
            if SMOKE_TAG in getattr(fn, "__eskd_tags__", set()):
                suite.addTest(t)
    runner = unittest.TextTestRunner(verbosity=2, resultclass=Recorder, stream=sys.stdout)
    try:
        result = runner.run(suite)
    finally:
        testing.shutdown()
    out = testing.run_dir()
    counts = write_reports(result.records, out, args.suite, started)
    print(f"\nОтчёт: {out / 'report.md'}")
    print("Итог:", ", ".join(f"{k}={v}" for k, v in sorted(counts.items())))
    bad = counts.get("fail", 0) + counts.get("error", 0) + counts.get("xpass", 0)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
