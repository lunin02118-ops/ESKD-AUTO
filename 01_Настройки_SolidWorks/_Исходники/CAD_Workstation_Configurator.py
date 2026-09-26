# -*- coding: utf-8 -*-
"""
Настройка рабочего места SolidWorks — инструментарий ЕСКД.

Запускается двойным щелчком из папки инструментария (сетевой или локальной). Папка, из которой запущена программа,
и есть источник: путь нигде не зашит и не вводится. Окно берёт фамилию и организацию, а всю работу делает
установщик Setup_Workstation_SolidWorks.ps1 из той же папки: пути SolidWorks на папку инструментария, макросы
SWPlus и надстройка ЕСКД — в профиль пользователя, кнопки, шрифты, Drew (лицензия встроена - активация
не нужна). Галочки: Drew, отучение SolidWorks от сети (включена по умолчанию; права администратора — только
когда правил не хватает);
выбор языка интерфейса SolidWorks и Drew (русский или английский).

Повторный запуск — обновление. Окно само ничего не запускает: настройка и обновление начинаются только по кнопке
«Установить / Обновить» (решение владельца 26.09.2026); при открытии окно лишь подсказывает, есть ли новый выпуск.

Само окно реестр и файлы ПК не меняет — только читает (текущие фамилия, организация, сведения об установке).
"""
import ctypes
import json
import os
import queue
import subprocess
import sys
import threading

TOOLKIT_MARKERS = (
    "02_Шаблоны_и_Форматки",
    os.path.join("03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0"),
    "04_Библиотеки_Материалов_и_Профилей",
)
SWPLUS = os.path.join("03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0")
ENGINE = "Setup_Workstation_SolidWorks.ps1"
EXIT_MESSAGES = {
    0: "Готово. Запустите SolidWorks.",
    1: "Настройка завершена с ошибками — см. сообщения выше.",
    2: "Папка инструментария неполная или выпуск не опубликован — сообщите администратору.",
    3: "SolidWorks не закрыт — настройка не выполнялась.",
    4: "Готово, но SolidWorks останется английским — см. [ВНИМАНИЕ] в шаге [9/9].",
}
# Код 4 — не ошибка настройки, а невключённый русский интерфейс (разбор 25.09.2026): жёлтым, а не зелёным «Готово».
EXIT_TONES = {0: "ok", 4: "warn"}


# ---------------------------------------------------------------- логика без окна (проверяется автотестом)
def program_dir():
    return os.path.dirname(os.path.abspath(sys.executable if getattr(sys, "frozen", False) else __file__))


def is_toolkit_root(path):
    return bool(path) and all(os.path.isdir(os.path.join(path, m)) for m in TOOLKIT_MARKERS)


def find_source_root(start):
    """Папка инструментария — папка программы или её родители (не выше трёх уровней). Запасных путей нет."""
    cur = os.path.abspath(start)
    for _ in range(3):
        if is_toolkit_root(cur):
            return cur
        parent = os.path.dirname(cur)
        if parent == cur:
            break
        cur = parent
    return None


def _engine_in(folder):
    """
    Движок в подпапке одним уровнем ниже (_Служебное, нынешняя раскладка) или, если его там нет, в самой папке
    (прежняя раскладка). Подпапка — первой: выпуск, распакованный поверх старой папки, оставляет рядом с окном старый
    движок, и окно запускало бы его вместо нового (аудит 23.09.2026).
    """
    try:
        names = sorted(os.listdir(folder))
    except OSError:
        names = []
    for name in names:
        candidate = os.path.join(folder, name, ENGINE)
        if os.path.isfile(candidate):
            return candidate
    near = os.path.join(folder, ENGINE)
    if os.path.isfile(near):
        return near
    return None


def find_engine(source_root, start):
    """
    Рядом с программой или в её подпапке, затем в 01_* корня инструментария и в подпапках такой папки.
    Служебные сценарии убраны в 01_Настройки_SolidWorks\\_Служебное, чтобы на виду у конструктора
    осталось только окно настройки; прежняя раскладка, где движок лежал рядом, тоже работает.
    """
    found = _engine_in(start)
    if found:
        return found
    for name in sorted(os.listdir(source_root)):
        if not name.startswith("01_"):
            continue
        found = _engine_in(os.path.join(source_root, name))
        if found:
            return found
    return None


def _read_cp1251_lines(path):
    try:
        with open(path, "r", encoding="cp1251") as f:
            return [line.rstrip("\r\n") for line in f]
    except OSError:
        return []


def read_families(source_root):
    """Фамилии из общего списка MProp (MProp_Fam.txt, по строке)."""
    names = []
    for line in _read_cp1251_lines(os.path.join(source_root, SWPLUS, "MProp", "MProp_Fam.txt")):
        if line.strip() and line.strip() not in names:
            names.append(line.strip())
    return names


def read_firms(source_root):
    """Организации из MProp_Firm.txt: пары строк «организация / код»."""
    lines = _read_cp1251_lines(os.path.join(source_root, SWPLUS, "MProp", "MProp_Firm.txt"))
    return [lines[i].strip() for i in range(0, len(lines), 2) if lines[i].strip()]


def read_release(source_root):
    path = os.path.join(source_root, "toolkit_release.json")
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            return "рабочая копия (не опубликована)"
        return "{} от {}".format(data.get("version", "?"), data.get("date", "?"))
    except (OSError, ValueError):
        return "рабочая копия (не опубликована)"


def release_version(source_root):
    """Номер выпуска в папке инструментария (toolkit_release.json) или пустая строка."""
    try:
        with open(os.path.join(source_root, "toolkit_release.json"), "r", encoding="utf-8-sig") as f:
            data = json.load(f)
        return str(data.get("version") or "") if isinstance(data, dict) else ""
    except (OSError, ValueError):
        return ""


def _release_key(version):
    """Номер выпуска «ГГГГ.ММ.ДД.ЧЧММ» (Publish-EskdToolkit) — кортеж для сравнения, иначе None."""
    parts = (version or "").split(".")
    return tuple(int(p) for p in parts) if len(parts) == 4 and all(p.isdigit() for p in parts) else None


def startup_status(ready, installed_version, folder_version, author, firm, last_result=""):
    """Подсказка при открытии окна. Окно само ничего не запускает: настройка и обновление — только по кнопке
    «Установить / Обновить» (решение владельца 26.09.2026; прежде при готовой установке через 5 с начиналось само)."""
    installed_version, folder_version = (installed_version or "").strip(), (folder_version or "").strip()
    if not ready:
        return "Запустите программу из папки инструментария на сетевом диске.", "error"
    if not (author and firm):
        return "Впишите свою фамилию и организацию и нажмите «Установить / Обновить».", "text"
    if installed_version and folder_version and installed_version != folder_version:
        old, new = _release_key(installed_version), _release_key(folder_version)
        if old and new and new < old:
            return ("В папке более ранний выпуск {} (на компьютере — {}): «Установить / Обновить» вернёт его."
                    .format(folder_version, installed_version), "warn")
        return "Есть новый выпуск {} — нажмите «Установить / Обновить».".format(folder_version), "warn"
    # Установщик пишет номер выпуска и при ошибках (LastResult = FAILED): «текущий выпуск» тогда не утешение.
    if installed_version and (last_result or "").strip().upper() == "FAILED":
        return "Прошлая настройка завершилась с ошибками — нажмите «Установить / Обновить».", "warn"
    if installed_version and installed_version == folder_version:
        return "Установлен текущий выпуск. «Установить / Обновить» — настроить заново.", "text"
    return "Проверьте данные и нажмите «Установить / Обновить».", "text"


def read_registry(subkey, names):
    """Чтение значений HKCU\\Software\\SolidWorks\\<subkey>; отсутствующие — пустая строка."""
    result = {n: "" for n in names}
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Software\\SolidWorks\\" + subkey) as key:
            for n in names:
                try:
                    result[n] = str(winreg.QueryValueEx(key, n)[0])
                except OSError:
                    pass
    except (OSError, ImportError):
        pass
    return result


def windows_display_name():
    """Полное имя учётной записи Windows (GetUserNameExW, NameDisplay) или пусто."""
    try:
        size = ctypes.c_ulong(0)
        ctypes.windll.secur32.GetUserNameExW(3, None, ctypes.byref(size))
        buffer = ctypes.create_unicode_buffer(size.value)
        if ctypes.windll.secur32.GetUserNameExW(3, buffer, ctypes.byref(size)):
            return buffer.value.strip()
    except (AttributeError, OSError):
        pass
    return ""


def windows_powershell(environ=None):
    """Windows PowerShell 5.1 по полному пути: «powershell.exe» из PATH может оказаться чем угодно."""
    env = os.environ if environ is None else environ
    root = _env_get(env, "SYSTEMROOT") or r"C:\Windows"
    exe = os.path.join(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    return exe if os.path.isfile(exe) else "powershell.exe"


def _env_get(env, name):
    for key, value in env.items():
        if key.upper() == name:
            return value
    return ""


def engine_env(environ=None):
    """
    Окружение движка: пути модулей Windows PowerShell 5.1, а не унаследованные. Окно, запущенное из PowerShell 7
    (pwsh), передавало бы движку его PSModulePath: 5.1 подхватывал модули 7-й версии, Get-FileHash пропадал, сверка
    хешей давала пусто, и установка переставляла Drew (20.09.2026). Ключ заменяется без учёта регистра — в Windows
    у переменной одно имя, а два ключа в окружении дочернего процесса дали бы случайное значение.
    """
    env = dict(os.environ if environ is None else environ)
    for key in [k for k in env if k.upper() == "PSMODULEPATH"]:
        del env[key]
    root = _env_get(env, "SYSTEMROOT") or r"C:\Windows"
    program_files = _env_get(env, "PROGRAMFILES") or r"C:\Program Files"
    env["PSModulePath"] = ";".join([os.path.join(program_files, "WindowsPowerShell", "Modules"),
                                    os.path.join(root, "system32", "WindowsPowerShell", "v1.0", "Modules")])
    return env


def build_command(engine, author, firm, close_mode, drew=True, block=False, language="Russian", safe_graphics=False):
    """Командная строка установщика: без вопросов в консоли, вывод в UTF-8; флаги чекбоксов и язык интерфейса."""
    cmd = [windows_powershell(), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", engine,
           "-Author", author, "-CloseMode", close_mode, "-NonInteractive", "-Utf8Output",
           "-Language", "English" if language == "English" else "Russian"]
    if firm:
        cmd += ["-Firm", firm]
    if not drew:
        cmd += ["-SkipDrew"]
    if block:
        cmd += ["-SwInternetBlock"]
    if safe_graphics:
        # Аппаратный конвейер графики не включается: лечение ПК, на котором SolidWorks после настройки не стартует.
        cmd += ["-Graphics", "Safe"]
    return cmd


def line_level(line):
    text = line.strip()
    if text.startswith("[OK]"):
        return "ok"
    if text.startswith("[ОШИБКА]") or "С ОШИБКАМИ" in text:
        return "error"
    if text.startswith("[ВНИМАНИЕ]") or "ОСТАНЕТСЯ АНГЛИЙСКИМ" in text:
        return "warn"
    if text.startswith("[ИНФО]"):
        return "info"
    return "text"


def solidworks_running():
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq SLDWORKS.exe", "/NH"], capture_output=True, text=True,
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0), timeout=15)
        return "sldworks.exe" in out.stdout.lower()
    except (OSError, subprocess.SubprocessError):
        return False


# ---------------------------------------------------------------- окно
class ConfiguratorApp:
    def __init__(self, root):
        import tkinter as tk
        from tkinter import ttk
        self.tk, self.ttk, self.root = tk, ttk, root
        self.start_dir = program_dir()
        self.source = find_source_root(self.start_dir)
        self.engine = find_engine(self.source, self.start_dir) if self.source else None
        self.events = queue.Queue()
        self.process = None

        root.title("Настройка рабочего места SolidWorks — ЕСКД")
        root.geometry("820x640")
        root.minsize(640, 460)
        if getattr(sys, "frozen", False):
            try:
                root.iconbitmap(default=sys.executable)
            except tk.TclError:
                pass
        try:
            ttk.Style().theme_use("vista")
        except tk.TclError:
            pass

        top = ttk.Frame(root, padding="16 12 16 6")
        top.pack(fill=tk.X)
        ttk.Label(top, text="Настройка рабочего места SolidWorks", font=("Segoe UI", 14, "bold")).pack(anchor=tk.W)
        ttk.Label(top, text="Шаблоны, форматки и библиотеки — из папки инструментария; макросы SWPlus и надстройка ЕСКД — "
                            "на этот компьютер.", font=("Segoe UI", 9)).pack(anchor=tk.W, pady=(2, 8))

        info = ttk.Frame(top)
        info.pack(fill=tk.X)
        installed = read_registry("ESKD_Install", ("ReleaseVersion", "InstalledAt", "SourceRoot", "LastResult", "Language",
                                                   "SwInternetBlock", "Graphics"))
        settings = read_registry("ESKD_Settings", ("Author", "Organization"))
        self.installed = bool(installed["InstalledAt"])
        rows = [
            ("Папка инструментария:", self.source or "не найдена — программа запущена не из папки инструментария"),
            ("Выпуск в папке:", read_release(self.source) if self.source else "—"),
            ("На этом компьютере:", "установлено {} ({})".format(installed["InstalledAt"], installed["ReleaseVersion"])
             if self.installed else "не установлено"),
        ]
        for i, (label, value) in enumerate(rows):
            ttk.Label(info, text=label).grid(row=i, column=0, sticky=tk.W, padx=(0, 10), pady=1)
            ttk.Label(info, text=value, font=("Segoe UI", 9, "bold")).grid(row=i, column=1, sticky=tk.W, pady=1)
        if self.installed and installed["SourceRoot"] and self.source and \
                os.path.normcase(installed["SourceRoot"]) != os.path.normcase(self.source):
            ttk.Label(info, text="Прежняя установка была из другой папки ({}) — пути будут переписаны на текущую."
                      .format(installed["SourceRoot"]), foreground="#9a6a12").grid(row=3, column=0, columnspan=2, sticky=tk.W)

        form = ttk.LabelFrame(root, text=" Данные для основной надписи ", padding="12 8 12 10")
        form.pack(fill=tk.X, padx=16, pady=(6, 6))
        ttk.Label(form, text="Фамилия И.О.:").grid(row=0, column=0, sticky=tk.W, pady=3)
        families = read_families(self.source) if self.source else []
        author = settings["Author"] or windows_display_name()
        self.var_author = tk.StringVar(value=author)
        ttk.Combobox(form, textvariable=self.var_author, values=families, width=34).grid(row=0, column=1, sticky=tk.W, padx=8)
        ttk.Label(form, text="Организация:").grid(row=0, column=2, sticky=tk.W, padx=(16, 0))
        firms = read_firms(self.source) if self.source else []
        self.var_firm = tk.StringVar(value=settings["Organization"])
        ttk.Combobox(form, textvariable=self.var_firm, values=firms, width=34).grid(row=0, column=3, sticky=tk.W, padx=8)

        comp = ttk.LabelFrame(root, text=" Компоненты ", padding="12 6 12 8")
        comp.pack(fill=tk.X, padx=16, pady=(6, 6))
        self.var_drew = tk.BooleanVar(value=True)
        # Отучение от сети включено по умолчанию (решение владельца 26.09.2026: телеметрия и данные SolidWorks не должны
        # уходить в интернет). Повторная установка лишь проверяет правила: права администратора — только если чего-то нет.
        # Снятая галочка запоминается (ESKD_Install\SwInternetBlock = 0): следующее обновление не ставит правила снова.
        self.var_block = tk.BooleanVar(value=installed["SwInternetBlock"] != "0")
        # Язык: прежний выбор этого ПК, иначе русский (решение владельца 25.09.2026) — следующее обновление его не меняет.
        self.var_lang = tk.StringVar(value="English" if (installed["Language"] or "").lower() == "english" else "Russian")
        # Безопасная графика — тоже прежний выбор этого ПК: следующее обновление не включает аппаратный конвейер снова.
        self.var_safe_gfx = tk.BooleanVar(value=(installed["Graphics"] or "").lower() == "safe")
        ttk.Checkbutton(comp, text="Drew — Gov-издание (лицензия встроена, без активации и кейгена)",
                        variable=self.var_drew).pack(anchor=tk.W)
        ttk.Checkbutton(comp, text="Отучение SolidWorks от сети — SolidWorks, Drew, SWTools без интернета "
                                   "(брандмауэр + hosts; права администратора — если правил не хватает)",
                        variable=self.var_block).pack(anchor=tk.W)
        lang = ttk.Frame(comp)
        lang.pack(anchor=tk.W, pady=(4, 0))
        ttk.Label(lang, text="Язык интерфейса SolidWorks и Drew:").pack(side=tk.LEFT)
        ttk.Radiobutton(lang, text="Русский", value="Russian", variable=self.var_lang).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Radiobutton(lang, text="Английский", value="English", variable=self.var_lang).pack(side=tk.LEFT, padx=(8, 0))
        # SolidWorks берёт язык из формата Windows пользователя, а не из языка Windows (разбор 25.09.2026).
        ttk.Label(comp, text="Русский: нужен русский язык в самом SolidWorks (его установщик: Изменить → Языки → Русский). "
                             "Если формат Windows не «Русский (Россия)», он будет переключён. Язык сменится после "
                             "перезапуска SolidWorks, иногда — после выхода из Windows и входа снова.",
                  foreground="#6b7682", font=("Segoe UI", 8), wraplength=760, justify=tk.LEFT).pack(anchor=tk.W)
        # Замечание владельца 22.09.2026: из подписи не было понятно, что галочка выключает аппаратное ускорение.
        ttk.Checkbutton(comp, text="Безопасная графика — выключает аппаратное ускорение "
                                   "(только если SolidWorks не запускается или окно чёрное)",
                        variable=self.var_safe_gfx).pack(anchor=tk.W)

        log_frame = ttk.LabelFrame(root, text=" Ход настройки ", padding=6)
        log_frame.pack(fill=tk.BOTH, expand=True, padx=16, pady=(0, 6))
        self.log = tk.Text(log_frame, height=12, font=("Consolas", 9), wrap=tk.WORD, relief=tk.FLAT, background="#f7f8fa")
        scroll = ttk.Scrollbar(log_frame, command=self.log.yview)
        self.log.configure(yscrollcommand=scroll.set, state=tk.DISABLED)
        scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self.log.pack(fill=tk.BOTH, expand=True)
        for tag, color in (("ok", "#2b7a4b"), ("error", "#a83a3a"), ("warn", "#9a6a12"), ("info", "#6b7682"), ("text", "#1b232c")):
            self.log.tag_configure(tag, foreground=color)

        bottom = ttk.Frame(root, padding="16 4 16 14")
        bottom.pack(fill=tk.X)
        # Кнопки размещаются первыми, итог переносится по строкам: длинный итог не сжимает кнопки (проверка 25.09.2026).
        self.status = ttk.Label(bottom, text="", font=("Segoe UI", 10, "bold"), wraplength=380)
        self.btn_close = ttk.Button(bottom, text="Закрыть", command=self.on_close)
        self.btn_close.pack(side=tk.RIGHT)
        self.btn_install = ttk.Button(bottom, text="Установить / Обновить", command=self.start)
        self.btn_install.pack(side=tk.RIGHT, padx=(0, 8))
        self.status.pack(side=tk.LEFT, fill=tk.X, expand=True)
        root.protocol("WM_DELETE_WINDOW", self.on_close)
        ready = bool(self.source and self.engine)
        if not ready:
            self.btn_install.state(["disabled"])
        # Только подсказка: настройка начинается лишь по кнопке (решение владельца 26.09.2026).
        self.set_status(*startup_status(ready, installed["ReleaseVersion"] if self.installed else "",
                                        release_version(self.source) if self.source else "",
                                        self.var_author.get().strip(), self.var_firm.get().strip(), installed["LastResult"]))
        root.after(100, self.pump)

    def set_status(self, text, level):
        colors = {"ok": "#2b7a4b", "error": "#a83a3a", "warn": "#9a6a12", "text": "#1b232c"}
        self.status.configure(text=text, foreground=colors.get(level, "#1b232c"))

    def write(self, line, level=None):
        self.log.configure(state=self.tk.NORMAL)
        self.log.insert(self.tk.END, line + "\n", level or line_level(line))
        self.log.see(self.tk.END)
        self.log.configure(state=self.tk.DISABLED)

    def start(self):
        from tkinter import messagebox
        author = self.var_author.get().strip()
        if not author:
            messagebox.showwarning("Фамилия", "Укажите фамилию и инициалы — они пишутся в основную надпись.")
            return
        if not self.var_firm.get().strip():
            messagebox.showwarning("Организация", "Укажите организацию — она пишется в основную надпись.")
            return
        close_mode = "Skip"
        if solidworks_running():
            if not messagebox.askyesno("SolidWorks открыт",
                                       "Для настройки SolidWorks нужно закрыть.\n\nСохраните открытые документы. "
                                       "Закрыть SolidWorks и продолжить?"):
                self.set_status("Отменено: SolidWorks открыт.", "warn")
                return
            close_mode = "Graceful"
        self.btn_install.state(["disabled"])
        self.log.configure(state=self.tk.NORMAL)
        self.log.delete("1.0", self.tk.END)
        self.log.configure(state=self.tk.DISABLED)
        self.set_status("Идёт настройка…", "text")
        cmd = build_command(self.engine, author, self.var_firm.get().strip(), close_mode,
                             drew=self.var_drew.get(), block=self.var_block.get(), language=self.var_lang.get(),
                             safe_graphics=self.var_safe_gfx.get())
        threading.Thread(target=self.run_engine, args=(cmd,), daemon=True).start()

    def run_engine(self, cmd):
        try:
            self.process = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                            env=engine_env(), creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            for raw in self.process.stdout:
                self.events.put(("line", raw.decode("utf-8", errors="replace").rstrip()))
            code = self.process.wait()
        except OSError as exc:
            self.events.put(("line", "[ОШИБКА] Не удалось запустить установщик: {}".format(exc)))
            code = 1
        self.events.put(("done", code))

    def pump(self):
        try:
            while True:
                kind, value = self.events.get_nowait()
                if kind == "line":
                    if value.strip():
                        self.write(value)
                else:
                    self.process = None
                    self.btn_install.state(["!disabled"])
                    self.set_status(EXIT_MESSAGES.get(value, "Установщик завершился с кодом {}.".format(value)),
                                    EXIT_TONES.get(value, "error"))
        except queue.Empty:
            pass
        self.root.after(100, self.pump)

    def on_close(self):
        if self.process is not None:
            from tkinter import messagebox
            if not messagebox.askyesno("Идёт настройка", "Настройка ещё не закончена. Закрыть окно? Установщик доработает сам."):
                return
        self.root.destroy()


if __name__ == "__main__":
    import tkinter
    window = tkinter.Tk()
    ConfiguratorApp(window)
    window.mainloop()
