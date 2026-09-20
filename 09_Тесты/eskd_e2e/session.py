# -*- coding: utf-8 -*-
"""Сессия SolidWorks для автотестов.

Правила безопасности:
  * сессия поднимается только если SLDWORKS.exe не запущен — документы пользователя
    не бывают открыты в тестовой сессии;
  * надстройка ЕСКД грузится явно (LoadAddIn), зонд событий — отдельный процесс;
  * все сохранения должны оказаться внутри каталога прогона: запись вне его запрещена
    харнессом до вызова API, а зонд фиксирует любое фактическое сохранение за пределами;
  * ESKD_Settings пользователя сохраняются до прогона (и в файл %LOCALAPPDATA%\\ESKD_Tests, один для всех рабочих
    копий) и восстанавливаются после; прерванный прогон восстанавливается при старте следующего, а если настройки
    после прерывания меняли вручную — прогон отказывается стартовать;
  * при остановке завершается только свой процесс SolidWorks.
"""
import contextlib
import ctypes
import os
import shutil
import subprocess
import sys
import time
import winreg
from pathlib import Path

import pythoncom
import win32com.client

from . import com, paths
from .guards import DialogWatchdog, RegistryConflict, RegistrySnapshot, settings_backup_path, solidworks_processes
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


def solidworks_exe():
    """SLDWORKS.exe из регистрации SldWorks.Application или None."""
    try:
        with winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, r"SldWorks.Application\CLSID") as key:
            clsid = winreg.QueryValueEx(key, "")[0]
        with winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, rf"CLSID\{clsid}\LocalServer32") as key:
            command = winreg.QueryValueEx(key, "")[0].strip()
    except OSError:
        return None
    exe = command[1:command.index('"', 1)] if command.startswith('"') else command.split(" /")[0]
    return exe if Path(exe).is_file() else None


def rot_solidworks(pid):
    """SolidWorks из таблицы запущенных объектов по моникеру SolidWorks_PID_<pid> или None."""
    try:
        rot = pythoncom.GetRunningObjectTable()
        # SolidWorks регистрирует моникер без разделителя: с «!» он не находится.
        obj = rot.GetObject(pythoncom.CreateItemMoniker(None, f"SolidWorks_PID_{pid}"))
        return win32com.client.Dispatch(obj.QueryInterface(pythoncom.IID_IDispatch))
    except pythoncom.com_error:
        return None


class SwSession:
    def __init__(self, run_dir, load_eskd=True, eskd_dll=None, settings=None, visible=True, use_probe=True, probe_flags=None,
                 launch="exe"):
        self.run_dir = Path(run_dir)
        #: "exe" — как у пользователя (по умолчанию), "com_only" — прежний запуск через COM, для разбора расхождений.
        self.launch_mode = launch
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
        self.registry = RegistrySnapshot(backup_path=settings_backup_path(), test_values=self.settings)
        # Регистрация надстройки указывает на установленную локальную копию. На время прогона CodeBase направляется на
        # проверяемую DLL и после прогона возвращается — снимком с защитой от прерывания, как ESKD_Settings. Ключи HKLM
        # нужны там, где регистрация машинная: процессы администратора при отключённом UAC (EnableLUA = 0) не видят
        # регистрацию COM из HKCU. Ключ, которого нет, не создаётся.
        self.addin_uri = "file:///" + str(Path(self.eskd_dll).resolve()).replace("\\", "/")
        inproc = "Software\\Classes\\CLSID\\" + paths.ADDIN_CLSID + "\\InprocServer32"
        keys = [(winreg.HKEY_CURRENT_USER, "HKCU", inproc), (winreg.HKEY_CURRENT_USER, "HKCU", inproc + "\\1.0.0.0"),
                (winreg.HKEY_LOCAL_MACHINE, "HKLM", inproc), (winreg.HKEY_LOCAL_MACHINE, "HKLM", inproc + "\\1.0.0.0")]
        self.addin_com = [RegistrySnapshot(key, settings_backup_path(prefix + "\\" + key), {"CodeBase": self.addin_uri},
                                           ("CodeBase",), hive) for hive, prefix, key in keys
                          if prefix == "HKCU" or ctypes.windll.shell32.IsUserAnAdmin()]
        self.eskd_loaded = False
        self._opened = []
        self._com_initialized = False

    # ------------------------------------------------------------------ жизненный цикл
    def start(self):
        if solidworks_processes():
            raise SessionRefused("SolidWorks уже запущен. Автотесты работают только в собственной сессии: "
                                 "сохраните работу и закройте SolidWorks.")
        self.run_dir.mkdir(parents=True, exist_ok=True)
        try:
            self.registry.capture()
            for snapshot in self.addin_com:
                snapshot.capture()
        except RegistryConflict as exc:
            raise SessionRefused(str(exc)) from exc
        if not any(snapshot.existed for snapshot in self.addin_com):
            self.registry.restore()
            for snapshot in self.addin_com:
                snapshot.restore()
            raise SessionRefused("Надстройка ЕСКД не зарегистрирована: запустите окно установки или "
                                 "03_Макросы_и_Плагины/ESKD_Material_Sync_Addin/register_eskd.ps1.")
        for snapshot in self.addin_com:
            if snapshot.existed:
                snapshot.apply({"CodeBase": self.addin_uri})
        if self.registry.recovered:
            print(f"ESKD_Settings восстановлены из {self.registry.backup_path}: предыдущий прогон был прерван "
                  "до восстановления настроек пользователя", file=sys.stderr)
        self.registry.apply(self.settings)
        pythoncom.CoInitialize()
        self._com_initialized = True
        try:
            raw, self.pid = self._launch()
            self.sw = com.flag_methods(com.dyn(raw), com.APP_METHODS)
            self.sw.Visible = self.visible
            # Экземпляр, запущенный автоматизацией, завершается, когда другой процесс (ESKD_Sync.exe) закрывает
            # последний документ. Под управлением «пользователя» сессия ведёт себя как на рабочем месте;
            # закрывает её stop() через ExitApp.
            self.sw.UserControl = True
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

    def _launch(self, timeout=180):
        """SolidWorks запускается как у пользователя — SLDWORKS.exe — и берётся из таблицы запущенных объектов.

        Только такой экземпляр видят другие программы: SWTools ищет SolidWorks по моникеру SolidWorks_PID_<pid>,
        а запущенный через COM SolidWorks моникер не регистрирует. Заодно это ближе к рабочему месту — в сессии
        через COM расходятся MProp и личные данные шаблонов (D01, I03, M04). Не вышло — прежний запуск через COM."""
        exe = solidworks_exe() if self.launch_mode != "com_only" else None
        if exe:
            proc = subprocess.Popen([exe])
            deadline = time.time() + timeout
            while time.time() < deadline and proc.poll() is None:
                app = rot_solidworks(proc.pid)
                if app is not None:
                    return app, proc.pid
                time.sleep(1)
            if proc.poll() is None:
                proc.kill()
                proc.wait(30)
            print(f"SolidWorks {exe} не появился в таблице запущенных объектов — запуск через COM", file=sys.stderr)
        raw = win32com.client.Dispatch("SldWorks.Application")
        procs = solidworks_processes()
        return raw, (procs[0].pid if procs else None)

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
            # Ждём и при необходимости завершаем только свой процесс: SolidWorks, запущенный человеком во время
            # прогона, не трогаем.
            own = [p for p in solidworks_processes() if self.pid is not None and p.pid == self.pid]
            deadline = time.time() + 60
            while own and time.time() < deadline:
                own = [p for p in own if p.is_running()]
                time.sleep(0.5)
            for p in own:
                try:
                    p.kill()
                except Exception:
                    pass
            foreign = [p.pid for p in solidworks_processes() if p.pid != self.pid]
            if foreign:
                print(f"SolidWorks с PID {foreign} запущен не тестами и оставлен работать", file=sys.stderr)
            self.registry.restore()
            for snapshot in self.addin_com:
                snapshot.restore()
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

    def wait_addin_idle(self, timeout=15.0):
        """Ждёт, пока надстройка выполнит отложенные задачи (пересохранение после «Сохранить как» в простое).
        Закрыть документ или снять с него подписки зонда раньше нельзя: SolidWorks принимает внешний COM-вызов
        посреди Save3 и падает (R01, 19.09.2026). Каждый опрос отдаёт SolidWorks простой."""
        deadline = time.time() + timeout
        refused = 0
        while time.time() < deadline:
            try:
                addin = self.eskd() if self.eskd_loaded else None
                if addin is None or int(com.call(addin, "PendingIdleTasks")) == 0:
                    return True
                refused = 0
            except Exception:
                # Занятый SolidWorks отклоняет вызов — это «ещё не простаивает», а не «ждать больше не надо».
                # Прежний мгновенный выход и давал закрытие документа посреди Save3 (R01, аудит 20.09.2026).
                refused += 1
                if refused >= 5:
                    return False
            time.sleep(0.2)
        return False

    def close(self, doc):
        if not self.wait_addin_idle():
            # Закрывать документ, пока надстройка не отработала отложенные задачи, нельзя: SolidWorks падает
            # на внешнем COM-вызове посреди Save3. Даём ему ещё простоя и пишем это в вывод теста.
            print("ВНИМАНИЕ: надстройка не отчиталась о простое перед закрытием документа — дополнительная пауза")
            time.sleep(2.0)
            self.wait_addin_idle(timeout=30.0)
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
        if not self.wait_addin_idle():
            time.sleep(2.0)
            self.wait_addin_idle(timeout=30.0)
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
