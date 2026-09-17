# -*- coding: utf-8 -*-
"""Защита рабочего места во время прогона: реестр, диалоги, файлы, процессы."""
import ctypes
import ctypes.wintypes as wt
import json
import os
import threading
import time
import winreg
from pathlib import Path

import psutil

ESKD_SETTINGS_KEY = r"Software\SolidWorks\ESKD_Settings"


def solidworks_processes():
    out = []
    for p in psutil.process_iter(["name", "pid"]):
        try:
            if (p.info["name"] or "").lower() == "sldworks.exe":
                out.append(p)
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    return out


class RegistryConflict(RuntimeError):
    """Резервная копия прерванного прогона не совпадает с тем, что сейчас в реестре, — решать человеку."""


def settings_backup_path(subkey=ESKD_SETTINGS_KEY):
    """Один файл снимка на ключ реестра для всех рабочих копий репозитория, вне очищаемой папки прогонов."""
    base = Path(os.environ.get("LOCALAPPDATA") or Path.home()) / "ESKD_Tests"
    return base / ("settings_backup_" + subkey.replace("\\", "_") + ".json")


class RegistrySnapshot:
    """Снимок значений ключа HKCU; restore() возвращает ключ в исходное состояние.

    С backup_path снимок до первой записи сохраняется в файл и удаляется только после restore(). Если прогон прерван
    (процесс убит, таймаут), следующий capture() сначала возвращает ключ из файла — иначе он снял бы как «исходные»
    тестовые значения, и фамилии из тестов остались бы в настройках пользователя навсегда.

    С test_values ключ из файла возвращается, только если в реестре остались тестовые подписи. Если их нет и ключ не
    совпадает со снимком, человек менял настройки после прерванного прогона: capture() бросает RegistryConflict и ничего
    не трогает, а не откатывает его правки к старому снимку.
    """

    SIGNATURES = ("Author", "Checker", "Organization")

    def __init__(self, subkey=ESKD_SETTINGS_KEY, backup_path=None, test_values=None, signatures=None,
                 hive=winreg.HKEY_CURRENT_USER):
        self.subkey = subkey
        self.hive = hive
        self.signatures = tuple(signatures or self.SIGNATURES)
        self.backup_path = Path(backup_path) if backup_path else None
        self.test_values = dict(test_values or {})
        self.existed = False
        self.values = {}
        self.recovered = False

    def _read(self):
        values = {}
        try:
            with winreg.OpenKey(self.hive, self.subkey, 0, winreg.KEY_READ) as key:
                i = 0
                while True:
                    try:
                        name, value, kind = winreg.EnumValue(key, i)
                    except OSError:
                        break
                    values[name] = (value, kind)
                    i += 1
            return True, values
        except FileNotFoundError:
            return False, values

    def capture(self):
        if self.backup_path is not None and self.backup_path.exists():
            existed, current = self._read()
            self._load_backup()
            if self.test_values:
                signatures = [n for n in self.signatures if isinstance(self.test_values.get(n), str)]
                leftover = signatures and all(current.get(n, (None,))[0] == self.test_values[n] for n in signatures)
                if not leftover:
                    if existed == self.existed and current == self.values:
                        self.backup_path.unlink()
                    else:
                        raise RegistryConflict(
                            f"Найден снимок настроек прерванного прогона {self.backup_path}, но в HKCU\\{self.subkey} уже не "
                            "тестовые подписи и не значения снимка: настройки меняли после прерывания. Ничего не изменено. "
                            "Проверьте настройки ЕСКД; если они верны — удалите файл снимка и запустите тесты снова.")
                else:
                    self.restore(keep_backup=True)
                    self.recovered = True
                    return self
            else:
                self.restore(keep_backup=True)
                self.recovered = True
                return self
        self.existed, self.values = self._read()
        self._save_backup()
        return self

    def _save_backup(self):
        if self.backup_path is None:
            return
        values = {name: {"kind": kind, "hex": value.hex()} if isinstance(value, bytes) else {"kind": kind, "value": value}
                  for name, (value, kind) in self.values.items()}
        self.backup_path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.backup_path.with_suffix(".tmp")
        tmp.write_text(json.dumps({"subkey": self.subkey, "existed": self.existed, "values": values},
                                  ensure_ascii=False, indent=1), encoding="utf-8")
        os.replace(tmp, self.backup_path)

    def _load_backup(self):
        data = json.loads(self.backup_path.read_text(encoding="utf-8"))
        if data.get("subkey") != self.subkey:
            raise RuntimeError(f"Резервная копия {self.backup_path} относится к ключу {data.get('subkey')}, а не {self.subkey}")
        self.existed = bool(data["existed"])
        self.values = {name: (bytes.fromhex(item["hex"]) if "hex" in item else item["value"], item["kind"])
                       for name, item in data["values"].items()}

    def apply(self, values):
        """values: {имя: значение}; int → REG_DWORD, остальное → REG_SZ."""
        with winreg.CreateKeyEx(self.hive, self.subkey, 0, winreg.KEY_WRITE) as key:
            for name, value in values.items():
                if isinstance(value, int):
                    winreg.SetValueEx(key, name, 0, winreg.REG_DWORD, value)
                else:
                    winreg.SetValueEx(key, name, 0, winreg.REG_SZ, str(value))

    def restore(self, keep_backup=False):
        if not self.existed:
            try:
                winreg.DeleteKey(self.hive, self.subkey)
            except FileNotFoundError:
                pass
        else:
            with winreg.CreateKeyEx(self.hive, self.subkey, 0,
                                    winreg.KEY_READ | winreg.KEY_WRITE) as key:
                current = []
                i = 0
                while True:
                    try:
                        current.append(winreg.EnumValue(key, i)[0])
                    except OSError:
                        break
                    i += 1
                for name in current:
                    if name not in self.values:
                        winreg.DeleteValue(key, name)
                for name, (value, kind) in self.values.items():
                    winreg.SetValueEx(key, name, 0, kind, value)
        if not keep_backup and self.backup_path is not None:
            try:
                self.backup_path.unlink()
            except FileNotFoundError:
                pass


# --------------------------------------------------------------------------- диалоги
user32 = ctypes.WinDLL("user32", use_last_error=True)
WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user32.EnumWindows.argtypes = [WNDENUMPROC, wt.LPARAM]
user32.EnumChildWindows.argtypes = [wt.HWND, WNDENUMPROC, wt.LPARAM]
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.IsWindowVisible.argtypes = [wt.HWND]
user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
WM_CLOSE = 0x0010


def _class_name(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def _text(hwnd):
    buf = ctypes.create_unicode_buffer(1024)
    user32.GetWindowTextW(hwnd, buf, 1024)
    return buf.value


def dialogs_of(pid):
    """Видимые окна-диалоги (#32770) процесса: [(hwnd, заголовок, тексты)]."""
    found = []

    def on_window(hwnd, _):
        proc = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(proc))
        if proc.value == pid and user32.IsWindowVisible(hwnd) and _class_name(hwnd) == "#32770":
            texts = []

            def on_child(child, __):
                cls = _class_name(child)
                if cls in ("Static", "Button") and _text(child).strip():
                    texts.append(_text(child).strip())
                return True

            user32.EnumChildWindows(hwnd, WNDENUMPROC(on_child), 0)
            found.append((hwnd, _text(hwnd), texts))
        return True

    user32.EnumWindows(WNDENUMPROC(on_window), 0)
    return found


def is_startup_notice(title, texts):
    """Безымянная заготовка уведомления, которую SolidWorks, запущенный через SLDWORKS.exe, создаёт при старте
    (подписи шаблона «Hyperlink Text 1/2», «Button1/2»). К проверяемому поведению отношения не имеет."""
    return not title and "Hyperlink Text 1" in texts and "Button1" in texts


class DialogWatchdog(threading.Thread):
    """Следит за модальными диалогами SolidWorks.

    Неожиданный диалог фиксируется (заголовок, тексты), закрывается WM_CLOSE и считается
    провалом текущего теста. Тест может заранее объявить ожидаемый диалог через expect().
    """

    def __init__(self, pid, interval=0.5):
        super().__init__(daemon=True)
        self.pid = pid
        self.interval = interval
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._expected = []
        self.unexpected = []
        self.handled = []

    def expect(self, title_part, close=True):
        with self._lock:
            self._expected.append((title_part, close))

    def pop_unexpected(self):
        with self._lock:
            items, self.unexpected = self.unexpected, []
        return items

    def stop(self):
        self._stop.set()

    def run(self):
        seen = set()
        while not self._stop.is_set():
            try:
                for hwnd, title, texts in dialogs_of(self.pid):
                    if hwnd in seen:
                        continue
                    time.sleep(0.3)
                    seen.add(hwnd)
                    record = {"title": title, "texts": texts, "time": time.strftime("%H:%M:%S")}
                    with self._lock:
                        match = next((e for e in self._expected if e[0] in title or any(e[0] in t for t in texts)), None)
                        if match:
                            self._expected.remove(match)
                            self.handled.append(record)
                        elif is_startup_notice(title, texts):
                            self.handled.append(record)
                        else:
                            self.unexpected.append(record)
                    user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
            except Exception:
                pass
            self._stop.wait(self.interval)


# --------------------------------------------------------------------------- файлы репозитория
PROTECTED_EXTENSIONS = (".sldprt", ".sldasm", ".slddrw", ".prtdot", ".asmdot", ".drwdot", ".slddrt",
                        ".sldmat", ".sldbomtbt", ".swp", ".ini", ".txt")


def snapshot_tree(root, exclude):
    """Время изменения и размер файлов SolidWorks и справочников в дереве root, кроме exclude."""
    root = Path(root)
    exclude = [Path(e).resolve() for e in exclude]
    state = {}
    for dirpath, dirnames, filenames in os.walk(root):
        d = Path(dirpath).resolve()
        if any(str(d).lower().startswith(str(e).lower()) for e in exclude):
            dirnames[:] = []
            continue
        dirnames[:] = [n for n in dirnames if n not in (".git", "__pycache__", "bin", "obj")]
        for name in filenames:
            if name.lower().endswith(PROTECTED_EXTENSIONS):
                p = d / name
                try:
                    st = p.stat()
                    state[str(p)] = (st.st_mtime_ns, st.st_size)
                except OSError:
                    pass
    return state


def diff_trees(before, after):
    changed = [p for p in after if p in before and after[p] != before[p]]
    created = [p for p in after if p not in before]
    deleted = [p for p in before if p not in after]
    return {"changed": sorted(changed), "created": sorted(created), "deleted": sorted(deleted)}
