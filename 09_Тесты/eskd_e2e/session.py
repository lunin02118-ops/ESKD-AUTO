# -*- coding: utf-8 -*-
"""Сессия SolidWorks для автотестов.

Правила безопасности:
  * сессия поднимается только если SLDWORKS.exe не запущен — документы пользователя
    не бывают открыты в тестовой сессии;
  * надстройка ЕСКД грузится явно (LoadAddIn), а в сессии без неё служба надстройки из автозагрузки SolidWorks
    выключена (ServiceEnabled = 0); зонд событий — отдельный процесс;
  * все сохранения должны оказаться внутри каталога прогона: запись вне его запрещена
    харнессом до вызова API, а в контрактных тестах (события документа включены) зонд фиксирует
    и фактическое сохранение за пределами;
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

import psutil
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
    "AutoStockMaterial": 1,
    "Author": "Тестов Т.Т.",
    "Checker": "Проверкин П.П.",
    "Organization": "ООО «Испытание»",
}

#: Сколько секунд ждать подключения надстройки после LoadAddIn (SolidWorks может подключить её уже после возврата).
ADDIN_CONNECT_TIMEOUT = 30


class SessionRefused(RuntimeError):
    pass


#: SolidWorks, запущенные харнессом в этом процессе Python: PID → время создания (Windows переиспользует PID, и
#: SolidWorks, запущенный человеком позже, может получить номер нашего завершённого процесса).
_OWN_SOLIDWORKS = {}


def remember_own(pid):
    """Запомнить процесс SolidWorks, запущенный харнессом: только такой start() дожидается, а не отказывается."""
    try:
        _OWN_SOLIDWORKS[pid] = psutil.Process(pid).create_time()
    except psutil.Error:
        pass


def wait_own_exit(processes, timeout=60):
    """Перед стартом сессии: SolidWorks, запущенный не тестами, — отказ; свой, ещё не завершившийся после stop(), —
    дождаться.

    23.09.2026 (T05): зависший SolidWorks stop() завершил через kill, но процесс ещё оставался в списке, и следующая
    сессия отказалась стартовать с «SolidWorks уже запущен» — все остальные тесты прогона стали ошибками."""
    foreign, own = [], []
    for p in processes:
        try:
            created = p.create_time()
        except psutil.NoSuchProcess:
            continue  # уже завершился
        except psutil.Error:
            created = None  # не прочитать — считаем чужим
        (own if created is not None and _OWN_SOLIDWORKS.get(p.pid) == created else foreign).append(p)
    if foreign:
        raise SessionRefused("SolidWorks уже запущен. Автотесты работают только в собственной сессии: "
                             "сохраните работу и закройте SolidWorks.")
    _, alive = psutil.wait_procs(own, timeout=timeout)
    if alive:
        raise SessionRefused(f"SolidWorks, запущенный автотестами (PID {[p.pid for p in alive]}), не завершился за "
                             f"{timeout} с после остановки сессии: закройте его в диспетчере задач.")


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


def inproc_keys(hive, inproc):
    """InprocServer32 и его подразделы версий: версию сборки регистрация берёт из DLL (сверка SW API 23.09.2026, №6),
    и стенд направляет на проверяемую DLL каждый, а не только «1.0.0.0». Раздела нет — только он сам (снимок его не
    создаст)."""
    keys = [inproc]
    try:
        with winreg.OpenKey(hive, inproc) as key:
            i = 0
            while True:
                try:
                    keys.append(inproc + "\\" + winreg.EnumKey(key, i))
                except OSError:
                    break
                i += 1
    except FileNotFoundError:
        pass
    return keys


class SwSession:
    def __init__(self, run_dir, load_eskd=True, eskd_dll=None, settings=None, visible=True, use_probe=True, probe_flags=None,
                 launch="exe"):
        self.run_dir = Path(run_dir)
        #: "exe" — как у пользователя (по умолчанию), "com_only" — прежний запуск через COM, для разбора расхождений.
        self.launch_mode = launch
        self.load_eskd_on_start = load_eskd
        self.eskd_dll = Path(eskd_dll or paths.ADDIN_DLL)
        self.settings = dict(TEST_SETTINGS if settings is None else settings)
        if not load_eskd:
            # Сессия без надстройки: LoadAddIn не вызывается, но установка включает надстройку в автозагрузку
            # SolidWorks, и она поднимается сама — из проверяемой DLL, с тестовыми настройками — и пишет свои свойства
            # в каждый сохраняемый документ. Так 23.09.2026 сборщик фикстур записал в детали корпуса А «Контору»,
            # «Проверил» и дробь материала. Служба выключается, как в eskd_muted; выгружать надстройку нельзя —
            # SolidWorks при выходе запомнил бы её выключенной и у конструктора она перестала бы запускаться.
            self.settings["ServiceEnabled"] = 0
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
        keys = [(hive, prefix, key) for hive, prefix in ((winreg.HKEY_CURRENT_USER, "HKCU"), (winreg.HKEY_LOCAL_MACHINE, "HKLM"))
                for key in inproc_keys(hive, inproc)]
        self.addin_com = [RegistrySnapshot(key, settings_backup_path(prefix + "\\" + key), {"CodeBase": self.addin_uri},
                                           ("CodeBase",), hive) for hive, prefix, key in keys
                          if prefix == "HKCU" or ctypes.windll.shell32.IsUserAnAdmin()]
        # SolidWorks при выходе записывает, была ли надстройка загружена: AddinsStartup\{CLSID} = 1 — запускать её вместе
        # с SolidWorks. Тест выгрузил надстройку (I06) — при выходе записывался 0: у конструктора она переставала
        # запускаться сама, а следующие прогоны не могли её загрузить (23.09.2026). На время прогона флаг — 1, после
        # выхода SolidWorks возвращается прежний.
        startup = "Software\\SolidWorks\\AddinsStartup\\" + paths.ADDIN_CLSID
        self.addin_startup = RegistrySnapshot(startup, settings_backup_path("HKCU\\" + startup))
        self.eskd_loaded = False
        self._opened = []
        #: Вызывается для каждого открытого или созданного документа (SwTestCase снимает базовый дамп свойств).
        self.on_open = None
        self.last_idle_state = ""
        self._com_initialized = False

    # ------------------------------------------------------------------ жизненный цикл
    def start(self):
        wait_own_exit(solidworks_processes())
        self.run_dir.mkdir(parents=True, exist_ok=True)
        try:
            self.registry.capture()
            for snapshot in self.addin_com:
                snapshot.capture()
            self.addin_startup.capture()
        except RegistryConflict as exc:
            raise SessionRefused(str(exc)) from exc
        if not any(snapshot.existed for snapshot in self.addin_com):
            self.registry.restore()
            for snapshot in self.addin_com:
                snapshot.restore()
            self.addin_startup.restore()
            raise SessionRefused("Надстройка ЕСКД не зарегистрирована: запустите окно установки или "
                                 "03_Макросы_и_Плагины/ESKD_Material_Sync_Addin/register_eskd.ps1.")
        for snapshot in self.addin_com:
            if snapshot.existed:
                snapshot.apply({"CodeBase": self.addin_uri})
        self.addin_startup.apply({"": 1})
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
            self._silence_pdf_viewer()
            if self.pid:
                self.watchdog = DialogWatchdog(self.pid)
                self.watchdog.start()
            if self.use_probe:
                # Подписки внешнего процесса на события документа роняют SolidWorks при закрытии детали после
                # сохранения (21.09.2026: цикл «открыть → сохранить → закрыть» падал за 1–4 повтора при любой
                # одной такой подписке, даже снятой до закрытия; без них — 30 повторов чисто). Поэтому зонд слушает
                # только события приложения, а события документа включают лишь тесты, которым они нужны
                # (SwTestCase.doc_events = True), на время теста.
                flags = {"doc-events": "0"} if self.probe_flags is None else self.probe_flags
                self.probe = ProbeClient(self.run_dir, flags).start()
                self.journal = Journal(self.probe.journal_path)
            if self.load_eskd_on_start:
                self.load_eskd()
        except BaseException:
            self.stop()
            raise
        return self

    def _silence_pdf_viewer(self):
        """Выключить «просмотр PDF после сохранения» (swPDFViewOnSave = 617).

        Прогон делает PDF десятками, и SolidWorks открывает по окну просмотрщика на каждый. 21.09.2026 так
        набралось 152 процесса PDF-XChange на 27 ГБ: SolidWorks предупредил о нехватке памяти окном «Да/Нет»
        (сторожевой пёс засчитал это провалом X05) и в другом прогоне упал совсем. Значение сессии, не файла
        пользователя: SolidWorks поднимается заново в каждом прогоне."""
        try:
            self.sw.SetUserPreferenceToggle(617, False)
        except Exception as exc:  # старая версия SolidWorks без этого параметра — не повод валить прогон
            print(f"не удалось выключить просмотр PDF после сохранения: {exc}", file=sys.stderr)

    def _launch(self, timeout=180):
        """SolidWorks запускается как у пользователя — SLDWORKS.exe — и берётся из таблицы запущенных объектов.

        Только такой экземпляр видят другие программы: SWTools ищет SolidWorks по моникеру SolidWorks_PID_<pid>,
        а запущенный через COM SolidWorks моникер не регистрирует. Заодно это ближе к рабочему месту — в сессии
        через COM расходятся MProp и личные данные шаблонов (D01, I03, M04). Не вышло — прежний запуск через COM."""
        exe = solidworks_exe() if self.launch_mode != "com_only" else None
        if exe:
            proc = subprocess.Popen([exe])
            remember_own(proc.pid)
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
        # Не свой прежний процесс, который мог ещё не исчезнуть из списка после kill выше.
        procs = [p for p in solidworks_processes() if p.pid not in _OWN_SOLIDWORKS]
        pid = procs[0].pid if procs else None
        if pid:
            remember_own(pid)
        return raw, pid

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
            # kill только начинает завершение: не дождавшись, следующая сессия видит свой же процесс как чужой.
            if own:
                _, alive = psutil.wait_procs(own, timeout=30)
                if alive:
                    print(f"SolidWorks автотестов (PID {[p.pid for p in alive]}) не завершился после kill", file=sys.stderr)
            foreign = [p.pid for p in solidworks_processes() if p.pid not in _OWN_SOLIDWORKS]
            if foreign:
                print(f"SolidWorks с PID {foreign} запущен не тестами и оставлен работать", file=sys.stderr)
            self.registry.restore()
            for snapshot in self.addin_com:
                snapshot.restore()
            self.addin_startup.restore()
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
        # SolidWorks может подключить надстройку не внутри LoadAddIn, а через 2–3 с после возврата: 25.09.2026
        # LoadAddIn = 0, ConnectToSW — через 2 с, объект надстройки — на следующем опросе. Без ожидания все классы
        # e2e падали в setUpClass (прогон r59). Ждём до ADDIN_CONNECT_TIMEOUT.
        deadline = time.monotonic() + ADDIN_CONNECT_TIMEOUT
        while self.sw.GetAddInObject(paths.ADDIN_PROGID) is None:
            if time.monotonic() > deadline:
                raise RuntimeError(f"Надстройка ЕСКД не загрузилась из {self.eskd_dll} за {ADDIN_CONNECT_TIMEOUT} с "
                                   f"(LoadAddIn={rc})")
            time.sleep(0.5)
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
        if self.on_open:
            self.on_open(doc)
        return doc

    def open(self, path, readonly=False, lightweight=False, extra_options=0):
        """extra_options — другие флаги swOpenDocOptions_e, например com.OPEN_DONT_LOAD_HIDDEN."""
        path = str(path)
        if self.sw.GetOpenDocumentByName(path) is not None:
            raise RuntimeError(f"Документ уже открыт в сессии: {path}")
        err, warn = com.ref_int(), com.ref_int()
        options = com.OPEN_SILENT | (com.OPEN_READONLY if readonly else 0) | (com.OPEN_LIGHTWEIGHT if lightweight else 0) | \
            extra_options
        doc = self.sw.OpenDoc6(path, com.doc_type_for(path), options, "", err, warn)
        if doc is None:
            raise RuntimeError(f"OpenDoc6 не открыл {path}: errors={err.value} warnings={warn.value}")
        doc = com.dyn(doc)
        self._opened.append(doc)
        if self.on_open:
            self.on_open(doc)
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
        self.last_idle_state = ""
        while time.time() < deadline:
            try:
                addin = self.eskd() if self.eskd_loaded else None
                pending = 0 if addin is None else int(com.call(addin, "PendingIdleTasks"))
                if pending == 0:
                    return True
                self.last_idle_state = f"отложенных задач: {pending}"
                refused = 0
            except Exception as exc:
                # Занятый SolidWorks отклоняет вызов — это «ещё не простаивает», а не «ждать больше не надо».
                # Прежний мгновенный выход и давал закрытие документа посреди Save3 (R01, аудит 20.09.2026).
                refused += 1
                self.last_idle_state = f"вызов отклонён {refused} раз: {exc!r}"[:200]
                if refused >= 5:
                    return False
            time.sleep(0.2)
        return False

    def close(self, doc):
        if not self.wait_addin_idle():
            # Закрывать документ, пока надстройка не отработала отложенные задачи, нельзя: SolidWorks падает
            # на внешнем COM-вызове посреди Save3. Даём ему ещё простоя и пишем это в вывод теста.
            print("ВНИМАНИЕ: надстройка не отчиталась о простое перед закрытием документа — дополнительная пауза"
                  f" ({self.last_idle_state})")
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
        """«Сохранить как» через команду интерфейса; зонд подставляет путь из FileSaveAsNotify2, диалог не
        показывается. Нужны события документа у зонда (@with_doc_events / doc_events = True в тесте)."""
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
