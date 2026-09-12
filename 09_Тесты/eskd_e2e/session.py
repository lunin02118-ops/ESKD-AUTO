# -*- coding: utf-8 -*-
"""Сессия SolidWorks для автотестов.

Правила безопасности:
  * сессия поднимается только если SLDWORKS.exe не запущен — документы пользователя
    не бывают открыты в тестовой сессии;
  * надстройка ЕСКД грузится явно (LoadAddIn), зонд событий — отдельный процесс;
  * все сохранения должны оказаться внутри каталога прогона: запись вне его запрещена
    харнессом до вызова API, а зонд фиксирует любое фактическое сохранение за пределами;
  * ESKD_Settings пользователя сохраняются до прогона и восстанавливаются после.
"""
import contextlib
import os
import shutil
import time
from pathlib import Path

import pythoncom
import win32com.client

from . import com, paths
from .guards import DialogWatchdog, RegistrySnapshot, solidworks_processes
from .probe import Journal, ProbeClient

TEST_SETTINGS = {
    "ServiceEnabled": 1,
    "AutoSyncMaterials": 1,
    "AutoMass": 1,
    "MassDecimals": 2,
    "AutoSplitName": 1,
    "Author": "Тестов Т.Т.",
    "Checker": "Проверкин П.П.",
    "Organization": "ООО «Испытание»",
}


class SessionRefused(RuntimeError):
    pass


class SwSession:
    def __init__(self, run_dir, load_eskd=True, eskd_dll=None, settings=None, visible=True, use_probe=True, probe_flags=None):
        self.run_dir = Path(run_dir)
        self.load_eskd_on_start = load_eskd
        self.eskd_dll = Path(eskd_dll or paths.ADDIN_DLL)
        self.settings = dict(TEST_SETTINGS if settings is None else settings)
        self.visible = visible
        self.use_probe = use_probe
        self.probe_flags = probe_flags
        self.sw = None
        self.pid = None
        self.watchdog = None
        self.probe = None
        self.journal = None
        self.registry = RegistrySnapshot()
        self.eskd_loaded = False
        self._opened = []
        self._com_initialized = False

    # ------------------------------------------------------------------ жизненный цикл
    def start(self):
        if solidworks_processes():
            raise SessionRefused("SolidWorks уже запущен. Автотесты работают только в собственной сессии: "
                                 "сохраните работу и закройте SolidWorks.")
        self.run_dir.mkdir(parents=True, exist_ok=True)
        self.registry.capture()
        self.registry.apply(self.settings)
        pythoncom.CoInitialize()
        self._com_initialized = True
        try:
            raw = win32com.client.Dispatch("SldWorks.Application")
            self.sw = com.flag_methods(com.dyn(raw), com.APP_METHODS)
            self.sw.Visible = self.visible
            procs = solidworks_processes()
            self.pid = procs[0].pid if procs else None
            self._wait_startup()
            if self.pid:
                self.watchdog = DialogWatchdog(self.pid)
                self.watchdog.start()
            if self.use_probe:
                self.probe = ProbeClient(self.run_dir, self.probe_flags).start()
                self.journal = Journal(self.probe.journal_path)
            if self.load_eskd_on_start:
                self.load_eskd()
        except BaseException:
            self.stop()
            raise
        return self

    def _wait_startup(self, timeout=120):
        t0 = time.time()
        while time.time() - t0 < timeout:
            try:
                if self.sw.StartupProcessCompleted:
                    return
            except Exception:
                return
            time.sleep(0.5)

    def stop(self):
        try:
            if self.probe is not None:
                self.probe.call("detach", "*", timeout=10)
                self.probe.stop()
        except Exception:
            pass
        try:
            if self.sw is not None:
                for doc in list(self._opened):
                    self.close(doc)
                try:
                    self.sw.CloseAllDocuments(True)
                except Exception:
                    pass
                self.sw.ExitApp()
        except Exception:
            pass
        finally:
            self.sw = None
            if self.watchdog:
                self.watchdog.stop()
            deadline = time.time() + 60
            while solidworks_processes() and time.time() < deadline:
                time.sleep(0.5)
            for p in solidworks_processes():
                try:
                    p.kill()
                except Exception:
                    pass
            self.registry.restore()
            if self._com_initialized:
                pythoncom.CoUninitialize()
                self._com_initialized = False

    def __enter__(self):
        return self.start()

    def __exit__(self, *exc):
        self.stop()

    # ------------------------------------------------------------------ надстройка ЕСКД
    def load_eskd(self):
        if self.eskd_loaded:
            return
        rc = self.sw.LoadAddIn(str(self.eskd_dll))
        if self.sw.GetAddInObject(paths.ADDIN_PROGID) is None:
            raise RuntimeError(f"Надстройка ЕСКД не загрузилась из {self.eskd_dll} (LoadAddIn={rc})")
        self.eskd_loaded = True

    def unload_eskd(self):
        if not self.eskd_loaded:
            return
        self.sw.UnloadAddIn(str(self.eskd_dll))
        self.eskd_loaded = False

    @contextlib.contextmanager
    def eskd_unloaded(self):
        was = self.eskd_loaded
        self.unload_eskd()
        try:
            yield
        finally:
            if was:
                self.load_eskd()

    @contextlib.contextmanager
    def eskd_muted(self):
        """Надстройка загружена, но служба выключена (ServiceEnabled = 0): документы открываются
        без её записи. Циклы UnloadAddIn/LoadAddIn не используются — после нескольких повторов
        надстройка v5 роняет SolidWorks при повторном создании вкладки (Д-26)."""
        self.registry.apply({"ServiceEnabled": 0})
        try:
            yield
        finally:
            self.registry.apply({"ServiceEnabled": int(self.settings.get("ServiceEnabled", 1))})

    def alive(self):
        try:
            self.sw.RevisionNumber()
            return True
        except Exception:
            return False

    def eskd(self):
        obj = self.sw.GetAddInObject(paths.ADDIN_PROGID)
        return com.dyn(obj) if obj is not None else None

    def mark(self, label):
        return self.probe.mark(label)

    def set_settings(self, **values):
        self.registry.apply(values)

    # ------------------------------------------------------------------ документы
    def workspace_copy(self, source, name=None, subdir=""):
        """Копия файла в каталоге прогона; исходные фикстуры не открываются никогда."""
        target_dir = self.run_dir / subdir if subdir else self.run_dir
        target_dir.mkdir(parents=True, exist_ok=True)
        target = target_dir / (name or Path(source).name)
        shutil.copy2(source, target)
        os.chmod(target, 0o666)
        return target

    def ws(self, *parts):
        p = self.run_dir.joinpath(*parts)
        p.parent.mkdir(parents=True, exist_ok=True)
        return p

    def new_doc(self, template):
        doc = self.sw.NewDocument(str(template), 0, 0, 0)
        if doc is None:
            raise RuntimeError(f"NewDocument не создал документ из {template}")
        doc = com.dyn(doc)
        self._opened.append(doc)
        return doc

    def open(self, path, readonly=False):
        path = str(path)
        if self.sw.GetOpenDocumentByName(path) is not None:
            raise RuntimeError(f"Документ уже открыт в сессии: {path}")
        err, warn = com.ref_int(), com.ref_int()
        options = com.OPEN_SILENT | (com.OPEN_READONLY if readonly else 0)
        doc = self.sw.OpenDoc6(path, com.doc_type_for(path), options, "", err, warn)
        if doc is None:
            raise RuntimeError(f"OpenDoc6 не открыл {path}: errors={err.value} warnings={warn.value}")
        doc = com.dyn(doc)
        self._opened.append(doc)
        return doc

    def activate(self, doc):
        err = com.ref_int()
        self.sw.ActivateDoc3(doc.GetTitle, False, 0, err)

    def close(self, doc):
        try:
            title = doc.GetTitle
        except Exception:
            title = None
        if title and self.probe is not None:
            try:
                self.probe.call("detach", title)
            except Exception:
                pass
        try:
            if title:
                self.sw.CloseDoc(title)
        except Exception:
            pass
        for i, d in enumerate(self._opened):
            if d is doc:
                del self._opened[i]
                break
        time.sleep(0.3)

    def close_all(self):
        for doc in list(self._opened):
            self.close(doc)
        try:
            if self.probe is not None:
                self.probe.call("detach", "*")
            self.sw.CloseAllDocuments(True)
        except Exception:
            pass
        self._opened = []

    def save(self, doc):
        err, warn = com.ref_int(), com.ref_int()
        ok = doc.Save3(com.SAVE_SILENT, err, warn)
        return bool(ok), int(err.value), int(warn.value)

    def save_as(self, doc, path, options=com.SAVE_SILENT):
        path = Path(path)
        self._assert_inside(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        err, warn = com.ref_int(), com.ref_int()
        ok = doc.SaveAs4(str(path), 0, options, err, warn)
        return bool(ok), int(err.value), int(warn.value)

    def ui_save_as(self, doc, path, command=620):
        """«Сохранить как» через команду интерфейса; зонд подставляет путь, диалог не показывается."""
        path = Path(path)
        self._assert_inside(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.activate(doc)
        self.probe.queue_save_as(str(path))
        ok = self.sw.RunCommand(command, "")
        if self.probe.pending_save_as():
            self.probe.clear_save_as()
            raise RuntimeError("Команда сохранения не дошла до FileSaveAsNotify2: путь из очереди не использован")
        return bool(ok)

    def run_command(self, doc, command):
        self.activate(doc)
        return bool(self.sw.RunCommand(command, ""))

    def violations(self):
        return self.probe.violations() if self.probe else 0

    def _assert_inside(self, path):
        root = str(self.run_dir.resolve()).lower().rstrip("\\") + "\\"
        if not str(Path(path).resolve()).lower().startswith(root):
            raise RuntimeError(f"Запись вне каталога прогона запрещена: {path}")
