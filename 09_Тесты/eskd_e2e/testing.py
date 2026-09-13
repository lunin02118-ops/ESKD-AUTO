# -*- coding: utf-8 -*-
"""Базовые классы тестов, общая сессия SolidWorks и учёт известных дефектов."""
import functools
import json
import os
import shutil
import time
import unittest
from pathlib import Path

from . import oracles, paths
from .session import SwSession

DEFECTS_FILE = paths.TESTS / "defects.json"

_run_dir = None
_session = None
_session_options = {"eskd_dll": None}


# --------------------------------------------------------------------------- прогон
def run_dir():
    global _run_dir
    if _run_dir is None:
        base = os.environ.get("ESKD_RUN_DIR")
        _run_dir = Path(base) if base else paths.RUNS / time.strftime("%Y%m%d_%H%M%S")
        _run_dir.mkdir(parents=True, exist_ok=True)
    return _run_dir


def configure(eskd_dll=None):
    _session_options["eskd_dll"] = eskd_dll


def session():
    """Общая сессия на весь прогон; после падения SolidWorks поднимается заново."""
    global _session
    if _session is not None and not _session_alive(_session):
        try:
            _session.stop()
        except Exception:
            pass
        _session = None
    if _session is None:
        _session = SwSession(run_dir() / "work", load_eskd=True, eskd_dll=_session_options["eskd_dll"]).start()
    return _session


def _session_alive(s):
    try:
        s.sw.RevisionNumber()
        return True
    except Exception:
        return False


def shutdown():
    global _session
    if _session is not None:
        _session.stop()
        _session = None


# --------------------------------------------------------------------------- известные дефекты
def defects():
    if DEFECTS_FILE.exists():
        return json.loads(DEFECTS_FILE.read_text(encoding="utf-8"))
    return {}


def known_defect(defect_id):
    """Тест, закрывающий дефект: пока дефект открыт (defects.json), провал ожидаем.

    Когда тест неожиданно проходит, отчёт показывает «unexpected success» — пора закрыть дефект.
    """
    def decorate(fn):
        state = defects().get(defect_id, {}).get("status", "open")
        if state == "open":
            wrapped = unittest.expectedFailure(fn)
            wrapped.__eskd_defect__ = defect_id
            return wrapped
        fn.__eskd_defect__ = defect_id
        return fn
    return decorate


def tags(*names):
    def decorate(fn):
        fn.__eskd_tags__ = set(names)
        return fn
    return decorate


# --------------------------------------------------------------------------- базовые классы
class StaticTestCase(unittest.TestCase):
    """Тесты без SolidWorks."""
    maxDiff = None


class SwTestCase(unittest.TestCase):
    """Тест с общей сессией SolidWorks и зондом событий."""
    maxDiff = None
    load_eskd = True

    @classmethod
    def setUpClass(cls):
        cls.s = session()

    def setUp(self):
        self.s = session()
        if self.load_eskd:
            self.s.load_eskd()
        else:
            self.s.unload_eskd()
        self.addin_log = oracles.AddinLog()
        self.case_dir = self.s.run_dir / self._case_name()
        if self.case_dir.exists():
            shutil.rmtree(self.case_dir, ignore_errors=True)
        self.case_dir.mkdir(parents=True, exist_ok=True)
        self.mark(self.id())

    def tearDown(self):
        problems = []
        try:
            self.s.close_all()
        except Exception as exc:
            problems.append(f"закрытие документов: {exc}")
        dialogs = self.s.watchdog.pop_unexpected() if self.s.watchdog else []
        if dialogs:
            problems.append(f"неожиданные диалоги SolidWorks: {dialogs}")
        try:
            if self.s.violations():
                problems.append("сохранение вне каталога прогона")
        except Exception:
            pass
        if problems:
            self.fail("; ".join(problems))

    # помощники
    def _case_name(self):
        return self.id().split(".")[-1]

    def mark(self, label):
        self._mark = self.s.mark(label)
        return self._mark

    def copy_fixture(self, name, fixture_dir=None):
        src = Path(fixture_dir or paths.FIXTURES_A) / name
        return self.s.workspace_copy(src, subdir=self._case_name())

    def copy_fixtures(self, *names):
        return [self.copy_fixture(n) for n in names]

    def path(self, name):
        return self.case_dir / name

    def persisted(self, path):
        return oracles.read_persisted(self.s, path)

    def property_writes(self, mark=None):
        return self.s.journal.property_writes(mark or self._mark)

    def assertNoPropertyWrites(self, mark=None, msg=""):
        writes = self.property_writes(mark)
        self.assertEqual([], [(w["event"], w.get("name"), w.get("cfg")) for w in writes],
                         msg or "документ изменён без действия пользователя")

    def wait_idle(self, seconds=2.5):
        """Даёт SolidWorks простоять: надстройка выполняет отложенные задачи в OnIdleNotify."""
        deadline = time.time() + seconds
        while time.time() < deadline:
            try:
                self.s.sw.RevisionNumber()
            except Exception:
                break
            time.sleep(0.25)

    def saves(self, mark=None):
        return [(e["saveType"], Path(e["fileName"]).name) for e in self.s.journal.saves(mark or self._mark)]

    def addin_errors(self):
        return [ln for ln in self.addin_log.new_lines() if "] ERROR " in ln]

    def open_copy(self, name, *extra):
        for n in extra:
            self.copy_fixture(n)
        path = self.copy_fixture(name)
        return path, self.s.open(path)

    def memory_equals_disk(self, doc, path):
        """Состояние в памяти после открытия совпадает с файлом на диске (надстройка ничего не записала)."""
        memory = oracles.dump_properties(doc)
        self.s.close(doc)
        disk = oracles.read_persisted(self.s, path)
        return memory, disk


def manifest():
    return json.loads(paths.FIXTURE_MANIFEST.read_text(encoding="utf-8"))
