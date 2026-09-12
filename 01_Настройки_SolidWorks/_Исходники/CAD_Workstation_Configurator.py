# -*- coding: utf-8 -*-
"""
Универсальный конфигуратор рабочего места SolidWorks 2025
Корпоративный стандарт ЕСКД и миграция настроек
- Автоматическая привязка сетевых библиотек, макросов SWPlus, шаблонов документов и стилей ЕСКД
- Полный перенос всех параметров корпоративного стандарта (Сборки, Чертежи, Оформление, Производительность, Цвета)
- Полная интеграция всех 9 кнопок макросов SWPlus в верхнюю панель быстрого доступа (QAT)
- Совместимость с официальным «Мастером копирования настроек SolidWorks» (.sldreg)
- Постоянная регистрация шрифтов ГОСТ (тип А, тип В) в Windows
- Аппаратный RealView с динамическим определением видеокарты (WMI)
"""
import os
import sys
import subprocess
import winreg
import time
import ctypes
import shutil
import re
from pathlib import Path
import tkinter as tk
from tkinter import ttk, messagebox, filedialog

def find_tools_root():
    d = os.path.abspath(os.path.dirname(sys.executable if getattr(sys, 'frozen', False) else __file__))
    if os.path.exists(os.path.join(d, '04_Библиотеки_Материалов_и_Профилей')):
        return d
    p = os.path.dirname(d)
    if os.path.exists(os.path.join(p, '04_Библиотеки_Материалов_и_Профилей')):
        return p
    fallback = r'D:\Work\_Инструменты_Конструктора'
    if os.path.exists(fallback):
        return fallback
    return d

DEFAULT_ROOT = find_tools_root()

def get_installed_gpus():
    gpus = []
    try:
        cmd = ['powershell', '-NoProfile', '-Command', '(Get-CimInstance Win32_VideoController).Name']
        res = subprocess.run(cmd, capture_output=True, text=True, timeout=5)
        for line in res.stdout.splitlines():
            line = line.strip()
            if line and line not in gpus:
                gpus.append(line)
    except Exception:
        pass
    if not gpus:
        gpus = ['NVIDIA GeForce RTX 5070 Ti/PCIe/SSE2']
    return gpus

class CADConfiguratorApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Настройка рабочего места SolidWorks 2025 — Корпоративный стандарт ЕСКД")
        self.root.geometry("860x720")
        self.root.minsize(800, 640)
        
        self.style = ttk.Style()
        try:
            self.style.theme_use('vista')
        except Exception:
            pass
            
        self.setup_ui()
        self.validate_paths()
        
    def setup_ui(self):
        header_frame = ttk.Frame(self.root, padding="15 10 15 10")
        header_frame.pack(fill=tk.X)
        
        lbl_title = ttk.Label(header_frame, text="Конфигуратор рабочего места SolidWorks 2025", font=("Segoe UI", 14, "bold"))
        lbl_title.pack(anchor=tk.W)
        lbl_desc = ttk.Label(header_frame, text="Перенос и фиксация корпоративного стандарта: шаблоны документов, форматки, 9 кнопок SWPlus в верхней панели, шрифты ГОСТ, стили ЕСКД и RealView.", font=("Segoe UI", 9))
        lbl_desc.pack(anchor=tk.W, pady=(2, 0))
        
        ttk.Separator(self.root, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=10)
        
        main_frame = ttk.Frame(self.root, padding="15 10 15 10")
        main_frame.pack(fill=tk.BOTH, expand=True)
        
        # 1. Paths Group
        grp_paths = ttk.LabelFrame(main_frame, text=" 1. Расположение корпоративных инструментов и библиотек ", padding="10")
        grp_paths.pack(fill=tk.X, pady=(0, 10))
        
        lbl_net = ttk.Label(grp_paths, text="Корневая папка инструментов (_Инструменты_Конструктора):")
        lbl_net.pack(anchor=tk.W)
        
        frame_net = ttk.Frame(grp_paths)
        frame_net.pack(fill=tk.X, pady=(3, 6))
        
        self.var_root_path = tk.StringVar(value=DEFAULT_ROOT)
        self.entry_root = ttk.Entry(frame_net, textvariable=self.var_root_path, font=("Segoe UI", 9))
        self.entry_root.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 5))
        
        btn_browse_net = ttk.Button(frame_net, text="Обзор...", command=self.browse_root_path)
        btn_browse_net.pack(side=tk.RIGHT)
        
        self.lbl_path_status = ttk.Label(grp_paths, text="Проверка путей...", font=("Segoe UI", 8, "italic"))
        self.lbl_path_status.pack(anchor=tk.W)
        
        # 2. Personal Data Group
        grp_user = ttk.LabelFrame(main_frame, text=" 2. Данные конструктора (ЕСКД / SWPlus) ", padding="10")
        grp_user.pack(fill=tk.X, pady=(0, 10))
        
        frame_user_grid = ttk.Frame(grp_user)
        frame_user_grid.pack(fill=tk.X)
        
        ttk.Label(frame_user_grid, text="Фамилия (Разработал):").grid(row=0, column=0, sticky=tk.W, pady=3)
        self.var_author = tk.StringVar(value="Лунин В.И.")
        ttk.Entry(frame_user_grid, textvariable=self.var_author, width=25).grid(row=0, column=1, sticky=tk.W, padx=10, pady=3)
        
        ttk.Label(frame_user_grid, text="Организация (Контора):").grid(row=0, column=2, sticky=tk.W, pady=3)
        self.var_firm = tk.StringVar(value="123")
        ttk.Entry(frame_user_grid, textvariable=self.var_firm, width=25).grid(row=0, column=3, sticky=tk.W, padx=10, pady=3)
        
        # 3. Options Group
        grp_opts = ttk.LabelFrame(main_frame, text=" 3. Компоненты настройки корпоративного стандарта ", padding="10")
        grp_opts.pack(fill=tk.X, pady=(0, 10))
        
        self.var_opt_profile = tk.BooleanVar(value=True)
        ttk.Checkbutton(grp_opts, text="Полный корпоративный профиль (Сборки, Чертежи, Оформление, Размеры, Цвета, Панели инструментов)", variable=self.var_opt_profile).pack(anchor=tk.W, pady=2)

        self.var_opt_templates = tk.BooleanVar(value=True)
        ttk.Checkbutton(grp_opts, text="Шаблоны документов (Деталь, Сборка, Чертеж) и корпоративная База форматок", variable=self.var_opt_templates).pack(anchor=tk.W, pady=2)

        self.var_opt_macros = tk.BooleanVar(value=True)
        ttk.Checkbutton(grp_opts, text="9 кнопок SWPlus в верхней панели быстрого доступа (QAT: MProp, SProp, DProp, SpecEditor, RecordDimM, Roughness, TT, Master, SaveAsPDF)", variable=self.var_opt_macros).pack(anchor=tk.W, pady=2)
        
        self.var_opt_lang = tk.BooleanVar(value=True)
        ttk.Checkbutton(grp_opts, text="Русский язык интерфейса и дерева конструирования", variable=self.var_opt_lang).pack(anchor=tk.W, pady=2)
        
        self.var_opt_realview = tk.BooleanVar(value=True)
        ttk.Checkbutton(grp_opts, text="Аппаратный RealView (автоопределение видеокарты WMI)", variable=self.var_opt_realview).pack(anchor=tk.W, pady=2)
        
        self.var_opt_fonts = tk.BooleanVar(value=True)
        ttk.Checkbutton(grp_opts, text="Постоянная системная регистрация шрифтов ГОСТ (тип А, тип В) в Windows", variable=self.var_opt_fonts).pack(anchor=tk.W, pady=2)
        
        self.var_opt_highlight = tk.BooleanVar(value=True)
        ttk.Checkbutton(grp_opts, text="Динамическая подсветка кромок и граней при наведении курсора (Dynamic Highlight)", variable=self.var_opt_highlight).pack(anchor=tk.W, pady=2)
        
        # 4. Log Group
        grp_log = ttk.LabelFrame(main_frame, text=" Журнал выполнения ", padding="5")
        grp_log.pack(fill=tk.BOTH, expand=True, pady=(0, 10))
        
        self.txt_log = tk.Text(grp_log, height=7, font=("Consolas", 8), bg="#F8F9FA", wrap=tk.WORD)
        self.txt_log.pack(fill=tk.BOTH, expand=True)
        
        # Action Buttons
        btn_frame = ttk.Frame(self.root, padding="15 0 15 15")
        btn_frame.pack(fill=tk.X)
        
        btn_reset = ttk.Button(btn_frame, text="СБРОС ДО ЗАВОДСКИХ + ЧИСТАЯ НАСТРОЙКА", command=self.full_factory_reset_and_apply)
        btn_reset.pack(side=tk.LEFT, padx=(0, 8))
        
        btn_exp_sldreg = ttk.Button(btn_frame, text="Экспорт в .sldreg (Мастер SW)", command=self.export_sldreg_file)
        btn_exp_sldreg.pack(side=tk.LEFT, padx=(0, 5))
        
        btn_export = ttk.Button(btn_frame, text="Экспорт в .reg", command=self.export_reg_file)
        btn_export.pack(side=tk.LEFT)
        
        btn_apply = ttk.Button(btn_frame, text="ПРИМЕНИТЬ НАСТРОЙКИ", command=self.apply_configuration)
        btn_apply.pack(side=tk.RIGHT)
        
    def log(self, msg, level="INFO"):
        prefix = {"INFO": "[ИНФО] ", "SUCCESS": "[УСПЕХ] ", "WARN": "[ВНИМАНИЕ] ", "ERROR": "[ОШИБКА] "}.get(level, "")
        self.txt_log.insert(tk.END, f"{prefix}{msg}\n")
        self.txt_log.see(tk.END)
        self.root.update_idletasks()
        
    def browse_root_path(self):
        d = filedialog.askdirectory(initialdir=self.var_root_path.get(), title="Выберите корневую папку _Инструменты_Конструктора")
        if d:
            self.var_root_path.set(os.path.abspath(d))
            self.validate_paths()
            
    def validate_paths(self):
        root_p = self.var_root_path.get().strip()
        self.txt_log.delete("1.0", tk.END)
        self.log(f"Проверка структуры каталогов в: {root_p}")
        
        required_dirs = [
            ("Шаблоны документов", os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблоны документов")),
            ("База шаблонов", os.path.join(root_p, "02_Шаблоны_и_Форматки", "База шаблонов")),
            ("Основные надписи (SWPlus)", os.path.join(root_p, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "Основные надписи")),
            ("Макросы SWPlus", os.path.join(root_p, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0")),
            ("Профили сварных деталей", os.path.join(root_p, "04_Библиотеки_Материалов_и_Профилей", "Профили сварных деталей")),
            ("Профили резьбы", os.path.join(root_p, "04_Библиотеки_Материалов_и_Профилей", "Профили резьбы")),
            ("Библиотека материалов", os.path.join(root_p, "04_Библиотеки_Материалов_и_Профилей", "Библиотека материалов")),
            ("Шаблон списка вырезов", os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблон списка вырезов")),
            ("Шаблон таблицы сварных швов", os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблон таблицы сварных швов")),
            ("Избранные размеры и примечания", os.path.join(root_p, "02_Шаблоны_и_Форматки", "Часто используемые размеры и примечания")),
            ("Шрифты ГОСТ", os.path.join(root_p, "05_Шрифты")),
        ]
        
        all_ok = True
        for name, path in required_dirs:
            if os.path.exists(path):
                cnt = len(os.listdir(path)) if os.path.isdir(path) else 1
                self.log(f"[OK] {name} -> найдено ({cnt} эл.)", "SUCCESS")
            else:
                self.log(f"[НЕ НАЙДЕНО] {name} -> {path}", "ERROR")
                all_ok = False
                
        if all_ok:
            self.lbl_path_status.config(text="Все необходимые библиотеки, шаблоны и макросы найдены!", foreground="green")
        else:
            self.lbl_path_status.config(text="Внимание: некоторые папки не найдены! Проверьте путь.", foreground="red")
        return all_ok

    def build_full_profile(self, root_p, as_sldreg=True):
        candidate_2025 = os.path.join(root_p, "01_Настройки_SolidWorks", "Реестровые_Профили", "01_SW2025_Корпоративный_Стандарт_ЕСКД.sldreg")
        if os.path.exists(candidate_2025):
            src_profile = candidate_2025
        else:
            candidates = [
                os.path.join(root_p, "01_Настройки_SolidWorks", "SW 2021 12-12-2021.sldreg"),
                os.path.join(root_p, "99_Архив", "SW 2021 12-12-2021.sldreg"),
                os.path.join(DEFAULT_ROOT, "01_Настройки_SolidWorks", "SW 2021 12-12-2021.sldreg"),
                os.path.join(DEFAULT_ROOT, "99_Архив", "SW 2021 12-12-2021.sldreg")
            ]
            src_profile = next((c for c in candidates if os.path.exists(c)), candidates[0])
            
        with open(src_profile, "rb") as f:
            text = f.read().decode("cp1251", errors="replace")
            
        # 1. Upgrade Version 2021 -> 2025
        text = text.replace(r"Software\SolidWorks\SOLIDWORKS 2021", r"Software\SolidWorks\SOLIDWORKS 2025")
        
        # 2. Exclude personal / temporary MRU and broken addins
        excluded_patterns = [
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\Recent Folder List\].*?(?=\r?\n\[|\Z)',
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\Recent Macro File List\].*?(?=\r?\n\[|\Z)',
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\SW Event Log\].*?(?=\r?\n\[|\Z)',
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\PDMWorks Workgroup\].*?(?=\r?\n\[|\Z)',
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\AddInsStartup\\\{03412BA8-10F6-4D51-AC38-4937CE7BEA5F\}\].*?(?=\r?\n\[|\Z)',
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\AddIns\\\{03412BA8-10F6-4D51-AC38-4937CE7BEA5F\}\].*?(?=\r?\n\[|\Z)',
            r'\[HKEY_LOCAL_MACHINE\\SOFTWARE\\SolidWorks\\AddIns\\\{03412BA8-10F6-4D51-AC38-4937CE7BEA5F\}\].*?(?=\r?\n\[|\Z)',
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Interface\\CommandManager\\[^\\]+\\Tab\d+\].*?(OnCadTools|Ounan|03412BA8).*?(?=\r?\n\[|\Z)',
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Interface\\TaskPane\\.*?(OnCadTools|Ounan).*?\].*?(?=\r?\n\[|\Z)',
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Interface\\Custom API Flyouts\\.*?\].*?03412BA8.*?(?=\r?\n\[|\Z)',
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Interface\\CommandManager\\[^\\]+\\Tab\d+\].*?Semantic.*?(?=\r?\n\[|\Z)',
            r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Interface\\TaskPane\\.*?Semantic.*?\].*?(?=\r?\n\[|\Z)',
        ]
        for ep in excluded_patterns:
            text = re.sub(ep, '', text, flags=re.DOTALL | re.IGNORECASE)
            
        # Strip Semantic from Addin Performance
        text = re.sub(r'"Semantic"="[^"]*"\r?\n', '', text, flags=re.IGNORECASE)
        text = re.sub(r'"Semantic MDM"="[^"]*"\r?\n', '', text, flags=re.IGNORECASE)
            
        # 3. Path mappings
        doc_templates   = os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблоны документов")
        drafting_std    = os.path.join(root_p, "02_Шаблоны_и_Форматки", "База шаблонов")
        sheet_formats   = os.path.join(root_p, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "Основные надписи")
        weld_profiles   = os.path.join(root_p, "04_Библиотеки_Материалов_и_Профилей", "Профили сварных деталей")
        thread_profiles = os.path.join(root_p, "04_Библиотеки_Материалов_и_Профилей", "Профили резьбы")
        mat_library     = os.path.join(root_p, "04_Библиотеки_Материалов_и_Профилей", "Библиотека материалов")
        cut_lists       = os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблон списка вырезов")
        weld_tables     = os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблон таблицы сварных швов")
        fav_symbols     = os.path.join(root_p, "02_Шаблоны_и_Форматки", "Часто используемые размеры и примечания")
        swplus_root     = os.path.join(root_p, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0")

        # Определение системной папки библиотеки обозначений SolidWorks (gtol.sym)
        sym_candidates = [
            r"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2025\lang\russian",
            r"C:\ProgramData\SolidWorks\SOLIDWORKS 2025\lang\russian",
            r"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2025\lang\english",
            r"C:\ProgramData\SolidWorks\SOLIDWORKS 2025\lang\english",
            r"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\lang\russian",
            r"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\lang\english",
        ]
        sym_lib_folder = r"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2025\lang\russian"
        for sc in sym_candidates:
            if os.path.exists(os.path.join(sc, "gtol.sym")):
                sym_lib_folder = sc
                break

        part_default = os.path.join(doc_templates, "Деталь.prtdot")
        asm_default  = os.path.join(doc_templates, "Сборка.asmdot")
        drw_default  = os.path.join(doc_templates, "Чертеж.drwdot")
        mat_db_str   = f"{mat_library};C:\\Program Files\\SOLIDWORKS Corp\\SOLIDWORKS\\lang\\russian\\sldmaterials;C:\\ProgramData\\SolidWorks\\SOLIDWORKS 2025\\Custom Materials"
        tmpl_folders = f"{doc_templates};{drafting_std}"

        def esc(s): return s.replace('\\', '\\\\')

        replacements = [
            (r"D:\\_Работа Solidworks рабочая!!!!\\_Шаблоны SW 2020\\Шаблоны", esc(doc_templates)),
            (r"D:\\_Основные надписи Solidworks Не трогать!!!!\\_Всё для SW 2020\\Шаблоны", esc(doc_templates)),
            (r"D:\\_Работа Solidworks рабочая!!!!\\_Шаблоны SW 2020\\Форматки", esc(sheet_formats)),
            (r"D:\\_Основные надписи Solidworks Не трогать!!!!\\_Всё для SW 2020\\Основные надписи", esc(sheet_formats)),
            (r"D:\\_Работа Solidworks рабочая!!!!\\_Шаблоны SW 2020\\SpecEditor", esc(drafting_std)),
            (r"D:\\_Основные надписи Solidworks Не трогать!!!!\\_Всё для SW 2020\\SpecEditor", esc(drafting_std)),
            (r"D:\\_3.Материалы и профили\\Профили сварных деталей\\weldment profiles", esc(weld_profiles)),
            (r"D:\\_3.Библиотека проектирования\\Сварные профили\\weldment profiles", esc(weld_profiles)),
            (r"D:\\_3.Материалы и профили", esc(os.path.join(root_p, "04_Библиотеки_Материалов_и_Профилей"))),
            (r"C:\\Users\\OneSmiLe\\", r"C:\\Users\\Vladimir\\"),
            (r"C:\\Users\\Владимир\\", r"C:\\Users\\Vladimir\\"),
            (r"C:\\Users\\Юрий\\", r"C:\\Users\\Vladimir\\"),
            (r"SOLIDWORKS 2020", r"SOLIDWORKS 2025"),
            (r"SOLIDWORKS 2021", r"SOLIDWORKS 2025"),
        ]

        for old, new in replacements:
            text = re.sub(re.escape(old), new, text, flags=re.IGNORECASE)

        # Dynamic replacement of any previous drive/path to _Инструменты_Конструктора with current root_p
        pattern_root = r'[A-Za-z]:(?:\\\\+|/)[^"\r\n;]*?(?:\\\\+|/)_Инструменты_Конструктора'
        text = re.sub(pattern_root, esc(root_p), text, flags=re.IGNORECASE)

        # Toolbox location - auto-detect real location with swbrowser.sldedb
        def find_toolbox_path():
            candidates = [
                os.path.join(os.path.dirname(root_p), "_Библиотека проектирования", "_Toolbox"),
                r"D:\Work\_Библиотека проектирования\_Toolbox",
                os.path.join(root_p, "_Библиотека проектирования", "_Toolbox"),
                r"C:\SOLIDWORKS Data",
                r"C:\SOLIDWORKS Data 2025",
                r"D:\Work\_Toolbox",
            ]
            for c in candidates:
                if os.path.exists(os.path.join(c, "lang", "english", "swbrowser.sldedb")) or \
                   os.path.exists(os.path.join(c, "lang", "russian", "swbrowser.sldedb")):
                    return c
            return candidates[1]

        toolbox_location = find_toolbox_path()
        text = re.sub(r'"Toolbox Data Location"="[^"]*"', f'"Toolbox Data Location"="{esc(toolbox_location)}"', text)

        # 4. Clean and inject accurate ExtReferences, Document Templates, ExtFolder
        ext_ref_block = f'''
[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\ExtReferences]
"Document Template Folders"="{esc(tmpl_folders)}"
"Sheet Format Folders"="{esc(sheet_formats)}"
"Drafting Standard Folder"="{esc(drafting_std)}"
"Weldment Profile Folders"="{esc(weld_profiles)}"
"Thread Profiles Folder"="{esc(thread_profiles)}"
"Material Database Folders"="{esc(mat_db_str)}"
"Weldment Cut List Template Folders"="{esc(cut_lists)}"
"Weld Table Template Folder"="{esc(weld_tables)}"
"Symbol Library Folders"="{esc(sym_lib_folder)}"
"Dimension Favorite Folders"="{esc(fav_symbols)}"
"Macro Folders"="{esc(swplus_root)}"
"Custom Property Folders"="{esc(os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблоны свойств"))}"
"Custom Property File"="{esc(os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблоны свойств", "default.prtprp"))}"

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\Document Templates]
"Default Part template"="{esc(part_default)}"
"Default Assy template"="{esc(asm_default)}"
"Default Draw Template"="{esc(drw_default)}"
"Use Default Document Templates"=dword:00000000
"Templates Last Tab Used"="Шаблоны документов"

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\ExtFolder]
"Document Template Folder"="{esc(tmpl_folders)}"
"Sheet Format Folders"="{esc(sheet_formats)}"
"Drafting Standard Folder"="{esc(drafting_std)}"
"Weldment Profile Folders"="{esc(weld_profiles)}"
"Thread Profiles Folder"="{esc(thread_profiles)}"
"Material Database Folders"="{esc(mat_db_str)}"
"Weldment Cut List Template Folders"="{esc(cut_lists)}"
"Weld Table Template Folder"="{esc(weld_tables)}"
"Symbol Library Folder"="{esc(sym_lib_folder)}"
"Dimension Favorite Folders"="{esc(fav_symbols)}"
"Macro Folder"="{esc(swplus_root)}"
"Custom Property Folders"="{esc(os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблоны свойств"))}"
"Custom Property File"="{esc(os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблоны свойств", "default.prtprp"))}"
"Default Template Part"="{esc(part_default)}"
"Default Template Assembly"="{esc(asm_default)}"
"Default Template Drawing"="{esc(drw_default)}"
'''
        text = re.sub(r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\ExtReferences\].*?(?=\r?\n\[|\Z)', '', text, flags=re.DOTALL)
        text = re.sub(r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\Document Templates\].*?(?=\r?\n\[|\Z)', '', text, flags=re.DOTALL)
        text = re.sub(r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\ExtFolder\].*?(?=\r?\n\[|\Z)', '', text, flags=re.DOTALL)
        text += "\r\n" + ext_ref_block.strip() + "\r\n"

        # 5. Inject exact 9 SWPlus macros
        macro_block = f'''
[-HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros]

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros]
"Macro Count"=dword:00000009

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros\\01 - Macro Folder]
"Command Offset"=dword:00000000
"Project"="MProp_run"
"MacroMethod"="main"
"ToolTip"="MProp"
"Prompt Msg"="{esc(os.path.join(swplus_root, "MProp", "MProp.swp"))}"
"Source Path"="{esc(os.path.join(swplus_root, "MProp", "MProp.swp"))}"
"Bitmap Path"="{esc(os.path.join(swplus_root, "MProp", "MProp.bmp"))}"
"Mouse Gesture Part"=dword:00000000
"Mouse Gesture Assembly"=dword:00000000
"Mouse Gesture Drawing"=dword:00000000
"Mouse Gesture Sketch"=dword:00000000
"Accelerator"=""

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros\\02 - Macro Folder]
"Command Offset"=dword:00000001
"Project"="SProp_run"
"MacroMethod"="main"
"ToolTip"="SProp"
"Prompt Msg"="{esc(os.path.join(swplus_root, "SProp", "SProp.swp"))}"
"Source Path"="{esc(os.path.join(swplus_root, "SProp", "SProp.swp"))}"
"Bitmap Path"="{esc(os.path.join(swplus_root, "SProp", "SProp.bmp"))}"
"Mouse Gesture Part"=dword:00000000
"Mouse Gesture Assembly"=dword:00000000
"Mouse Gesture Drawing"=dword:00000000
"Mouse Gesture Sketch"=dword:00000000
"Accelerator"=""

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros\\03 - Macro Folder]
"Command Offset"=dword:00000002
"Project"="DProp_run"
"MacroMethod"="main"
"ToolTip"="DProp"
"Prompt Msg"="{esc(os.path.join(swplus_root, "DProp", "DProp.swp"))}"
"Source Path"="{esc(os.path.join(swplus_root, "DProp", "DProp.swp"))}"
"Bitmap Path"="{esc(os.path.join(swplus_root, "DProp", "DProp.bmp"))}"
"Mouse Gesture Part"=dword:00000000
"Mouse Gesture Assembly"=dword:00000000
"Mouse Gesture Drawing"=dword:00000000
"Mouse Gesture Sketch"=dword:00000000
"Accelerator"=""

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros\\04 - Macro Folder]
"Command Offset"=dword:00000003
"Project"="SpecEditor_run"
"MacroMethod"="main"
"ToolTip"="SpecEditor"
"Prompt Msg"="{esc(os.path.join(swplus_root, "SpecEditor", "SpecEditor.swp"))}"
"Source Path"="{esc(os.path.join(swplus_root, "SpecEditor", "SpecEditor.swp"))}"
"Bitmap Path"="{esc(os.path.join(swplus_root, "SpecEditor", "SpecEditor.bmp"))}"
"Mouse Gesture Part"=dword:00000000
"Mouse Gesture Assembly"=dword:00000000
"Mouse Gesture Drawing"=dword:00000000
"Mouse Gesture Sketch"=dword:00000000
"Accelerator"=""

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros\\05 - Macro Folder]
"Command Offset"=dword:00000004
"Project"="RecordDimM_run"
"MacroMethod"="main"
"ToolTip"="RecordDimM"
"Prompt Msg"="{esc(os.path.join(swplus_root, "RecordDimM", "RecordDimM.swp"))}"
"Source Path"="{esc(os.path.join(swplus_root, "RecordDimM", "RecordDimM.swp"))}"
"Bitmap Path"="{esc(os.path.join(swplus_root, "RecordDimM", "RecordDimM.bmp"))}"
"Mouse Gesture Part"=dword:00000000
"Mouse Gesture Assembly"=dword:00000000
"Mouse Gesture Drawing"=dword:00000000
"Mouse Gesture Sketch"=dword:00000000
"Accelerator"=""

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros\\06 - Macro Folder]
"Command Offset"=dword:00000005
"Project"="Roughness_run"
"MacroMethod"="main"
"ToolTip"="Roughness"
"Prompt Msg"="{esc(os.path.join(swplus_root, "Roughness", "Roughness.swp"))}"
"Source Path"="{esc(os.path.join(swplus_root, "Roughness", "Roughness.swp"))}"
"Bitmap Path"="{esc(os.path.join(swplus_root, "Roughness", "Roughness.bmp"))}"
"Mouse Gesture Part"=dword:00000000
"Mouse Gesture Assembly"=dword:00000000
"Mouse Gesture Drawing"=dword:00000000
"Mouse Gesture Sketch"=dword:00000000
"Accelerator"=""

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros\\07 - Macro Folder]
"Command Offset"=dword:00000006
"Project"="Run_Program"
"MacroMethod"="main"
"ToolTip"="TT"
"Prompt Msg"="{esc(os.path.join(swplus_root, "ТТ", "TT.SWP"))}"
"Source Path"="{esc(os.path.join(swplus_root, "ТТ", "TT.SWP"))}"
"Bitmap Path"="{esc(os.path.join(swplus_root, "ТТ", "TT.BMP"))}"
"Mouse Gesture Part"=dword:00000000
"Mouse Gesture Assembly"=dword:00000000
"Mouse Gesture Drawing"=dword:00000000
"Mouse Gesture Sketch"=dword:00000000
"Accelerator"=""

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros\\08 - Macro Folder]
"Command Offset"=dword:00000007
"Project"="Master_run"
"MacroMethod"="main"
"ToolTip"="Master"
"Prompt Msg"="{esc(os.path.join(swplus_root, "Master", "Master.swp"))}"
"Source Path"="{esc(os.path.join(swplus_root, "Master", "Master.swp"))}"
"Bitmap Path"="{esc(os.path.join(swplus_root, "Master", "Master.bmp"))}"
"Mouse Gesture Part"=dword:00000000
"Mouse Gesture Assembly"=dword:00000000
"Mouse Gesture Drawing"=dword:00000000
"Mouse Gesture Sketch"=dword:00000000
"Accelerator"=""

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros\\09 - Macro Folder]
"Command Offset"=dword:00000008
"Project"="PDFCreatorPrint_run"
"MacroMethod"="main"
"ToolTip"="SaveAsPDF"
"Prompt Msg"="{esc(os.path.join(swplus_root, "SaveAsPDF", "PDFCreator.swp"))}"
"Source Path"="{esc(os.path.join(swplus_root, "SaveAsPDF", "PDFCreator.swp"))}"
"Bitmap Path"="{esc(os.path.join(swplus_root, "SaveAsPDF", "SaveAsPDF.bmp"))}"
"Mouse Gesture Part"=dword:00000000
"Mouse Gesture Assembly"=dword:00000000
"Mouse Gesture Drawing"=dword:00000000
"Mouse Gesture Sketch"=dword:00000000
"Accelerator"=""
'''
        text = re.sub(r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Defined Macros.*?\].*?(?=\r?\n\[|\Z)', '', text, flags=re.DOTALL)
        text += "\r\n" + macro_block.strip() + "\r\n"

        # 6. Inject QAT GB0 with all 9 macros
        qat_block = '''
[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Interface\\CommandManager\\QAT\\GB0]
"Btn0"="1,21781"
"Btn1"="1,54312"
"Btn2"="1,54416"
"Btn3"="1,54302"
"Btn4"="1,54303"
"Btn5"="1,57643"
"Btn6"="1,57644"
"Btn7"="1,34128"
"Btn8"="1,32805"
"Btn9"="1,33040"
"Btn10"="1,54325"
"Btn11"="1,33639"
"Btn12"="1,33640"
"Btn13"="1,33641"
"Btn14"="1,33642"
"Btn15"="1,33643"
"Btn16"="1,33644"
"Btn17"="1,33645"
"Btn18"="1,33646"
"Btn19"="1,33647"
'''
        text = re.sub(r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\User Interface\\CommandManager\\QAT\\GB0\].*?(?=\r?\n\[|\Z)', '', text, flags=re.DOTALL)
        text += "\r\n" + qat_block.strip() + "\r\n"

        # 7. Menu Customizations for 33639..33647
        menu_custom_lines = "\r\n".join([f'"{cid}"=dword:00000000' for cid in range(33639, 33648)])
        text = re.sub(r'\[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\Menu Customizations\].*?(?=\r?\n\[|\Z)', '', text, flags=re.DOTALL)
        text += f"\r\n[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\Menu Customizations]\r\n{menu_custom_lines}\r\n"

        # 8. RealView AllowList entries
        rv_block = "\r\n".join([
            f'[HKEY_CURRENT_USER\\Software\\SolidWorks\\AllowList\\Gl2Shaders\\NV40\\{esc(gpu)}]\r\n"Workarounds"=dword:00030408'
            for gpu in get_installed_gpus()
        ])
        text += f"\r\n{rv_block}\r\n"

        # 9. Dynamic Highlight (Динамическая подсветка кромок и граней при наведении курсора)
        is_highlight_on = getattr(self, "var_opt_highlight", None) is None or self.var_opt_highlight.get()
        hl_dword = "dword:00000001" if is_highlight_on else "dword:00000000"
        
        text = re.sub(r'"Dynamic Highlight"=dword:[0-9a-fA-F]+', f'"Dynamic Highlight"={hl_dword}', text)
        text = re.sub(r'"Dynamic Highlight from Browser"=dword:[0-9a-fA-F]+', f'"Dynamic Highlight from Browser"={hl_dword}', text)
        
        edges_highlight_block = f'''
[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\Edges]
"Dynamic Highlight"={hl_dword}
"Show Shaded Edges"=dword:00000001
"Highlight new or modified faces"=dword:00000001
"Tangent Edge Display With Font"=dword:00000001

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\General]
"Dynamic Highlight from Browser"={hl_dword}
'''
        text += "\r\n" + edges_highlight_block.strip() + "\r\n"

        # 10. Fix graphics performance on Intel Arc / hybrid GPUs (disable buggy Enhanced Performance Pipeline)
        text = re.sub(r'"Use Performance Pipeline 2020"=dword:[0-9a-fA-F]+', '"Use Performance Pipeline 2020"=dword:00000000', text)
        perf_fix_block = f'''
[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\Performance]
"Use Performance Pipeline 2020"=dword:00000000

[HKEY_CURRENT_USER\\Software\\SolidWorks\\SOLIDWORKS 2025\\General]
"Toolbox Data Location"="{esc(toolbox_location)}"

[HKEY_CURRENT_USER\\Software\\SolidWorks\\AddIns\\{{B64E6875-B101-4D5C-B245-FF8D50772E25}}]
@=dword:00000001
"Title"="ЕСКД: Синхронизация материалов и реквизитов"
"Description"="Автоматическая синхронизация материалов, реквизитов ГОСТ, центрирование массы в штампе чертежа"
'''
        text += "\r\n" + perf_fix_block.strip() + "\r\n"

        if as_sldreg:
            if not text.startswith("REGEDIT4"):
                text = "REGEDIT4\r\n;SolidWorks Copy Settings Wizard\r\n\r\n" + text.lstrip()
            return text
        else:
            prefix = "Windows Registry Editor Version 5.00\r\n\r\n"
            clean_body = re.sub(r'^(REGEDIT4\r?\n)?(;[^\r\n]*\r?\n)*', '', text, flags=re.DOTALL)
            return prefix + clean_body.lstrip()

    def delete_key_recursive(self, root_h, sub_path):
        try:
            with winreg.OpenKey(root_h, sub_path, 0, winreg.KEY_ALL_ACCESS) as k:
                sub_cnt = winreg.QueryInfoKey(k)[0]
                sub_names = [winreg.EnumKey(k, i) for i in range(sub_cnt)]
                for sub_name in sub_names:
                    self.delete_key_recursive(root_h, f"{sub_path}\\{sub_name}")
            winreg.DeleteKey(root_h, sub_path)
        except Exception:
            pass

    def full_factory_reset_and_apply(self):
        if not messagebox.askyesno("Подтверждение полного сброса", "Вы действительно хотите выполнить полный сброс SolidWorks 2025?\n\nВся ветка реестра SolidWorks 2025 будет очищена от мусора и сбоев, а затем чисто накатятся полные настройки корпоративного стандарта ЕСКД.\n\nУбедитесь, что все открытые документы SolidWorks сохранены!"):
            return
            
        self.txt_log.delete("1.0", tk.END)
        self.log("=== НАЧАЛО ПОЛНОГО СБРОСА И НАСТРОЙКИ ===", "INFO")
        
        self.log("1. Закрытие процессов SolidWorks...", "INFO")
        subprocess.run(["taskkill", "/F", "/IM", "SLDWORKS.exe"], capture_output=True)
        subprocess.run(["taskkill", "/F", "/IM", "sldworks.exe"], capture_output=True)
        time.sleep(2)
        
        self.log("2. Полная очистка реестра от следов OnCadTools...", "INFO")
        hkcu = winreg.HKEY_CURRENT_USER
        hklm = winreg.HKEY_LOCAL_MACHINE
        unwanted_guids = [
            "{03412BA8-10F6-4D51-AC38-4937CE7BEA5F}".lower(), # OnCadTools
            "{7A2F5C31-9E44-4B0D-8C21-5F0E9A4B77C2}".lower(), # OnCadTools Shim
        ]

        # Удаление из автозагрузки и списков надстроек
        for root_k, base_p in [
            (hkcu, r"Software\SolidWorks\AddInsStartup"),
            (hkcu, r"Software\SolidWorks\AddIns"),
            (hkcu, r"Software\SolidWorks\AddInsEntitlement"),
            (hklm, r"SOFTWARE\SolidWorks\AddIns"),
            (hklm, r"SOFTWARE\SolidWorks"),
            (hklm, r"SOFTWARE\WOW6432Node\SolidWorks\AddIns"),
            (hkcu, r"Software\Classes\CLSID"),
            (hklm, r"SOFTWARE\Classes\CLSID"),
            (hklm, r"SOFTWARE\Classes\WOW6432Node\CLSID"),
        ]:
            try:
                with winreg.OpenKey(root_k, base_p, 0, winreg.KEY_ALL_ACCESS) as k:
                    sub_cnt = winreg.QueryInfoKey(k)[0]
                    sub_keys = [winreg.EnumKey(k, i) for i in range(sub_cnt)]
                    for sub in sub_keys:
                        if any(ug in sub.lower() for ug in unwanted_guids):
                            self.delete_key_recursive(root_k, f"{base_p}\\{sub}")
                            self.log(f"Удален ключ: {base_p}\\{sub}", "SUCCESS")
            except Exception:
                pass

        # Удаление ProgID OnCadTools в Classes
        for root_k, base_p in [(hklm, r"SOFTWARE\Classes"), (hkcu, r"Software\Classes")]:
            try:
                with winreg.OpenKey(root_k, base_p, 0, winreg.KEY_ALL_ACCESS) as k:
                    sub_cnt = winreg.QueryInfoKey(k)[0]
                    sub_keys = [winreg.EnumKey(k, i) for i in range(sub_cnt)]
                    for sub in sub_keys:
                        sub_low = sub.lower()
                        if sub_low.startswith("oncadtools"):
                            self.delete_key_recursive(root_k, f"{base_p}\\{sub}")
            except Exception:
                pass
            
        self.log("3. Сброс профиля SolidWorks 2025 в реестре...", "INFO")
        self.delete_key_recursive(hkcu, r"Software\SolidWorks\SOLIDWORKS 2025")
        self.log("Профиль SolidWorks 2025 очищен.", "SUCCESS")
        
        self.log("4. Применение чистой корпоративной конфигурации...", "INFO")
        self.apply_configuration(show_msgbox=False, skip_close_check=True)
        
        self.log("\nСБРОС И ЧИСТАЯ НАСТРОЙКА УСПЕШНО ЗАВЕРШЕНЫ!", "SUCCESS")
        messagebox.showinfo("Успех", "SolidWorks 2025 полностью настроен с нуля!\n\nПолный корпоративный профиль ЕСКД, шаблоны документов, 9 кнопок SWPlus в верхней панели и шрифты ГОСТ успешно привязаны.")

    def export_sldreg_file(self):
        root_p = self.var_root_path.get().strip()
        save_path = filedialog.asksaveasfilename(
            defaultextension=".sldreg",
            filetypes=[("Профиль SolidWorks (*.sldreg)", "*.sldreg")],
            initialfile="01_SW2025_Корпоративный_Стандарт_ЕСКД.sldreg",
            title="Сохранить файл профиля настроек SolidWorks (.sldreg)"
        )
        if save_path:
            content = self.build_full_profile(root_p, as_sldreg=True)
            with open(save_path, "wb") as f:
                f.write(content.encode("cp1251", errors="replace"))
            self.log(f"Экспорт .sldreg завершен: {save_path}", "SUCCESS")
            messagebox.showinfo("Экспорт завершен", f"Файл профиля настроек сохранен в:\n{save_path}\n\nЕго можно импортировать в любое время двойным кликом через стандартный «Мастер копирования настроек SolidWorks».")

    def export_reg_file(self):
        root_p = self.var_root_path.get().strip()
        save_path = filedialog.asksaveasfilename(
            defaultextension=".reg",
            filetypes=[("Файлы реестра (*.reg)", "*.reg")],
            initialfile="01_SW2025_Корпоративный_Стандарт_ЕСКД.reg",
            title="Сохранить файл настроек реестра (.reg)"
        )
        if save_path:
            content = self.build_full_profile(root_p, as_sldreg=False)
            with open(save_path, "w", encoding="utf-16") as f:
                f.write(content)
            self.log(f"Экспорт .reg завершен: {save_path}", "SUCCESS")
            messagebox.showinfo("Экспорт завершен", f"Файл настроек сохранен в:\n{save_path}")

    def apply_configuration(self, show_msgbox=True, skip_close_check=False):
        root_p = self.var_root_path.get().strip()
        if not self.validate_paths():
            if not messagebox.askyesno("Предупреждение", "Некоторые пути не найдены. Продолжить применение?"):
                return
                
        if not skip_close_check:
            try:
                chk = subprocess.run(["tasklist", "/FI", "IMAGENAME eq SLDWORKS.exe"], capture_output=True, text=True)
                if "SLDWORKS.exe" in chk.stdout or "sldworks.exe" in chk.stdout:
                    if not messagebox.askyesno("SolidWorks запущен", "Для надежной записи реестра процесс SolidWorks должен быть закрыт.\n\nСохранить документы и закрыть SolidWorks?"):
                        return
                    self.log("Закрытие запущенного процесса SolidWorks...", "INFO")
                    subprocess.run(["taskkill", "/F", "/IM", "SLDWORKS.exe"], capture_output=True)
                    subprocess.run(["taskkill", "/F", "/IM", "sldworks.exe"], capture_output=True)
                    time.sleep(2)
            except Exception:
                pass

        self.log("\n=== Импорт полного корпоративного профиля SolidWorks 2025 ===", "INFO")
        
        # 1. Generate and import full registry profile
        try:
            reg_content = self.build_full_profile(root_p, as_sldreg=False)
            temp_reg = os.path.join(root_p, "01_Настройки_SolidWorks", "_temp_import.reg")
            with open(temp_reg, "w", encoding="utf-16") as f:
                f.write(reg_content)
                
            res = subprocess.run(["reg", "import", temp_reg], capture_output=True, text=True)
            if os.path.exists(temp_reg):
                os.remove(temp_reg)
                
            if res.returncode == 0:
                self.log("Полный профиль корпоративного стандарта успешно импортирован в реестр!", "SUCCESS")
            else:
                self.log(f"Ошибка reg import: {res.stderr}", "WARN")

            # Прямая запись Toolbox и исправления графики в HKCU и HKLM
            try:
                tb_path = os.path.join(os.path.dirname(root_p), "_Библиотека проектирования", "_Toolbox")
                if not os.path.exists(tb_path):
                    tb_path = r"D:\Work\_Библиотека проектирования\_Toolbox"
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\SolidWorks\SOLIDWORKS 2025\General", 0, winreg.KEY_SET_VALUE) as k:
                    winreg.SetValueEx(k, "Toolbox Data Location", 0, winreg.REG_SZ, tb_path)
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\SolidWorks\SOLIDWORKS 2025\Performance", 0, winreg.KEY_SET_VALUE) as k:
                    winreg.SetValueEx(k, "Use Performance Pipeline 2020", 0, winreg.REG_DWORD, 0)
                self.log(f"База данных стандартов Toolbox зафиксирована: '{tb_path}'", "SUCCESS")
                self.log("Графический конвейер зафиксирован в безопасном режиме (черный экран устранен)", "SUCCESS")
            except Exception as e:
                self.log(f"Предупреждение при записи ключей Toolbox/Graphics: {e}", "WARN")

            try:
                with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\SolidWorks\SOLIDWORKS 2025\General", 0, winreg.KEY_SET_VALUE) as k:
                    winreg.SetValueEx(k, "Toolbox Data Location", 0, winreg.REG_SZ, tb_path)
            except Exception:
                pass

            # Гарантированное закрепление 9 кнопок макросов SWPlus в верхней панели QAT и Menu Customizations
            try:
                qat_path = r"Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\QAT\GB0"
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, qat_path) as k_qat:
                    qat_btns = {
                        "Btn11": "1,33639",
                        "Btn12": "1,33640",
                        "Btn13": "1,33641",
                        "Btn14": "1,33642",
                        "Btn15": "1,33643",
                        "Btn16": "1,33644",
                        "Btn17": "1,33645",
                        "Btn18": "1,33646",
                        "Btn19": "1,33647",
                    }
                    for b_name, b_val in qat_btns.items():
                        winreg.SetValueEx(k_qat, b_name, 0, winreg.REG_SZ, b_val)

                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\SolidWorks\SOLIDWORKS 2025\Menu Customizations") as k_mc:
                    for cid in range(33639, 33648):
                        winreg.SetValueEx(k_mc, str(cid), 0, winreg.REG_DWORD, 0)

                # Очистка фантомных ссылок Toolbars (OnCadTools, SWTools) предотвращающая диалог сброса тулбаров SolidWorks
                self.delete_key_recursive(winreg.HKEY_CURRENT_USER, r"Software\SolidWorks\SOLIDWORKS 2025\User Interface\Toolbars\ToolbarChangesOnUpgrade")
                self.log("9 кнопок макросов SWPlus зафиксированы в верхней панели быстрого доступа (QAT: 33639-33647)!", "SUCCESS")
            except Exception as e_qat:
                self.log(f"Предупреждение при фиксации кнопок QAT: {e_qat}", "WARN")
        except Exception as e:
            self.log(f"Ошибка при импорте профиля: {e}", "ERROR")

        # 2. Master.ini
        master_ini = os.path.join(root_p, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "Master", "Master.ini")
        sheet_formats = os.path.join(root_p, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "Основные надписи")
        os.makedirs(sheet_formats, exist_ok=True)
        if os.path.exists(master_ini):
            try:
                with open(master_ini, 'r', encoding='cp1251', errors='ignore') as f:
                    lines = f.readlines()
                if len(lines) >= 4:
                    sf_norm = sheet_formats.rstrip("\\") + "\\"
                    lines[3] = f"{sf_norm}\n"
                    with open(master_ini, 'w', encoding='cp1251') as f:
                        f.writelines(lines)
                    self.log(f"Обновлен Master.ini -> '{sf_norm}'", "SUCCESS")
            except Exception as e:
                self.log(f"Ошибка обновления Master.ini: {e}", "WARN")

        # 3. Personal Author / Firm
        author = self.var_author.get().strip()
        firm = self.var_firm.get().strip()
        mprop_dir = os.path.join(root_p, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "MProp")
        if os.path.exists(mprop_dir):
            if author:
                fam_file = os.path.join(mprop_dir, "MProp_Fam.txt")
                try:
                    with open(fam_file, "w", encoding="cp1251") as f:
                        f.write(f"{author}\n")
                    self.log(f"Записана фамилия конструктора: {author}", "SUCCESS")
                except Exception: pass
            if firm:
                firm_file = os.path.join(mprop_dir, "MProp_Firm.txt")
                try:
                    with open(firm_file, "w", encoding="cp1251") as f:
                        f.write(f"{firm}\n")
                    self.log(f"Записана организация: {firm}", "SUCCESS")
                except Exception: pass

        # 4. Fonts Registration (Permanent in HKLM/HKCU Fonts + GDI AddFontResourceW)
        if self.var_opt_fonts.get():
            fonts_dir = os.path.join(root_p, "05_Шрифты")
            if os.path.exists(fonts_dir):
                win_fonts = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "Fonts")
                installed_cnt = 0
                for f_name in os.listdir(fonts_dir):
                    if f_name.lower().endswith(('.ttf', '.fon', '.otf')):
                        src_font = os.path.join(fonts_dir, f_name)
                        dst_font = os.path.join(win_fonts, f_name)
                        try:
                            if not os.path.exists(dst_font):
                                shutil.copy2(src_font, dst_font)
                            ctypes.windll.gdi32.AddFontResourceW(dst_font)
                            
                            font_val_name = f"{os.path.splitext(f_name)[0]} (TrueType)"
                            try:
                                with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", 0, winreg.KEY_ALL_ACCESS) as k_fnt:
                                    winreg.SetValueEx(k_fnt, font_val_name, 0, winreg.REG_SZ, f_name)
                            except Exception:
                                try:
                                    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", 0, winreg.KEY_ALL_ACCESS) as k_fnt_u:
                                        winreg.SetValueEx(k_fnt_u, font_val_name, 0, winreg.REG_SZ, dst_font)
                                except Exception:
                                    pass
                            installed_cnt += 1
                        except Exception:
                            try:
                                ctypes.windll.gdi32.AddFontResourceW(src_font)
                                installed_cnt += 1
                            except Exception: pass
                if installed_cnt > 0:
                    try:
                        ctypes.windll.user32.PostMessageW(0xFFFF, 0x001D, 0, 0)
                    except Exception: pass
                    self.log(f"Шрифты ГОСТ зарегистрированы в Windows ({installed_cnt} шт.)!", "SUCCESS")

                # 5. Register Native ESKD Material Sync Add-In v5, Toolbar & Favorites
        addin_dll = os.path.join(root_p, "03_Макросы_и_Плагины", "ESKD_Material_Sync_Addin", "ESKD_Material_Sync_v5.dll")
        regasm = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

        # Clean legacy addin GUIDs to avoid duplicates
        old_guids = [
            "{B64E6875-B101-4D5C-B245-FF8D50772E21}",
            "{B64E6875-B101-4D5C-B245-FF8D50772E23}",
            "{B64E6875-B101-4D5C-B245-FF8D50772E24}"
        ]
        for og in old_guids:
            for base_p in [r"Software\SolidWorks\AddIns", r"Software\SolidWorks\AddInsStartup"]:
                try:
                    self.delete_key_recursive(winreg.HKEY_CURRENT_USER, f"{base_p}\\{og}")
                    self.delete_key_recursive(winreg.HKEY_LOCAL_MACHINE, f"{base_p}\\{og}")
                except Exception: pass

        # Clean legacy tabs from CommandManager and guarantee ESKD tab visibility
        active_guid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
        for ctx in ['PartContext', 'AssyContext', 'DrwContext']:
            base_ctx = rf"Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\{ctx}"
            try:
                found_eskd = False
                highest_tab = -1
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, base_ctx) as k_ctx:
                    sub_keys = []
                    i = 0
                    while True:
                        try:
                            sub_keys.append(winreg.EnumKey(k_ctx, i))
                            i += 1
                        except OSError:
                            break
                    for sk in sub_keys:
                        m = re.match(r"^Tab(\d+)$", sk, re.IGNORECASE)
                        if m:
                            highest_tab = max(highest_tab, int(m.group(1)))
                        try:
                            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, rf"{base_ctx}\{sk}", 0, winreg.KEY_ALL_ACCESS) as k_tab:
                                mod = ""
                                try: mod = winreg.QueryValueEx(k_tab, "ModuleName")[0]
                                except: pass
                                if any(og.lower() == mod.lower() for og in old_guids):
                                    self.delete_key_recursive(winreg.HKEY_CURRENT_USER, rf"{base_ctx}\{sk}")
                                    continue
                                ref = ""
                                try: ref = winreg.QueryValueEx(k_tab, "RefName")[0]
                                except: pass
                                if (ref and "ЕСКД" in ref) or (mod and mod.upper() == active_guid.upper()):
                                    winreg.SetValueEx(k_tab, "RefName", 0, winreg.REG_SZ, "ЕСКД")
                                    winreg.SetValueEx(k_tab, "ModuleName", 0, winreg.REG_SZ, active_guid)
                                    winreg.SetValueEx(k_tab, "Tab Props", 0, winreg.REG_SZ, "ЕСКД,1,1,-1")
                                    found_eskd = True
                        except Exception: pass
                if not found_eskd:
                    next_tab_name = f"Tab{highest_tab + 1}"
                    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"{base_ctx}\{next_tab_name}") as k_new_tab:
                        winreg.SetValueEx(k_new_tab, "RefName", 0, winreg.REG_SZ, "ЕСКД")
                        winreg.SetValueEx(k_new_tab, "ModuleName", 0, winreg.REG_SZ, active_guid)
                        winreg.SetValueEx(k_new_tab, "Tab Props", 0, winreg.REG_SZ, "ЕСКД,1,1,-1")
            except Exception: pass

        if os.path.exists(addin_dll):
            try:
                if os.path.exists(regasm):
                    subprocess.run([regasm, "/codebase", addin_dll], capture_output=True)

                # DEP-11: RegAsm пишет percent-escaped CodeBase — CLR не активирует
                # кириллический URI. Прошиваем RAW-форму в HKLM (для SW от администратора).
                try:
                    import winreg as _wr
                    _raw_cb = "file:///" + addin_dll.replace(chr(92), "/")
                    for _hive in (r"SOFTWARE\Classes\CLSID\{B64E6875-B101-4D5C-B245-FF8D50772E25}\InprocServer32",
                                  r"SOFTWARE\Classes\CLSID\{B64E6875-B101-4D5C-B245-FF8D50772E25}\InprocServer32\1.0.0.0"):
                        try:
                            with _wr.OpenKey(_wr.HKEY_LOCAL_MACHINE, _hive, 0, _wr.KEY_SET_VALUE) as _k:
                                _wr.SetValueEx(_k, "CodeBase", 0, _wr.REG_SZ, _raw_cb)
                        except OSError:
                            pass
                except Exception:
                    pass

                guid_str = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
                title_str = "ЕСКД: Синхронизация материалов и реквизитов"
                desc_str = "Панель инструментов ЕСКД: настройки реквизитов (фамилии, контора, масса), автоматическая синхронизация материалов и центрирование штампа по ГОСТ 2.104"

                # Direct Custom Property Folders
                try:
                    prop_f = os.path.join(root_p, "02_Шаблоны_и_Форматки", "Шаблоны свойств")
                    prop_default = os.path.join(prop_f, "default.prtprp")
                    for subk in [r"Software\SolidWorks\SOLIDWORKS 2025\ExtReferences", r"Software\SolidWorks\SOLIDWORKS 2025\ExtFolder"]:
                        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, subk) as k_prop:
                            winreg.SetValueEx(k_prop, "Custom Property Folders", 0, winreg.REG_SZ, prop_f)
                            winreg.SetValueEx(k_prop, "Custom Property File", 0, winreg.REG_SZ, prop_default)
                except Exception: pass

                # HKCU Software\Classes COM registration (guarantees add-in loads without admin rights)
                try:
                    codebase_url = "file:///" + addin_dll.replace(chr(92), "/")
                    clsid_root = rf"Software\Classes\CLSID\{guid_str}"
                    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, clsid_root) as k_c:
                        winreg.SetValueEx(k_c, "", 0, winreg.REG_SZ, "ESKD.MaterialSync.SwAddin")

                    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"{clsid_root}\InprocServer32") as k_inproc:
                        winreg.SetValueEx(k_inproc, "", 0, winreg.REG_SZ, "mscoree.dll")
                        winreg.SetValueEx(k_inproc, "ThreadingModel", 0, winreg.REG_SZ, "Both")
                        winreg.SetValueEx(k_inproc, "Class", 0, winreg.REG_SZ, "ESKD.MaterialSync.SwAddin")
                        winreg.SetValueEx(k_inproc, "Assembly", 0, winreg.REG_SZ, "ESKD_Material_Sync_v5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")
                        winreg.SetValueEx(k_inproc, "RuntimeVersion", 0, winreg.REG_SZ, "v4.0.30319")
                        winreg.SetValueEx(k_inproc, "CodeBase", 0, winreg.REG_SZ, codebase_url)

                    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"{clsid_root}\InprocServer32\1.0.0.0") as k_inver:
                        winreg.SetValueEx(k_inver, "Class", 0, winreg.REG_SZ, "ESKD.MaterialSync.SwAddin")
                        winreg.SetValueEx(k_inver, "Assembly", 0, winreg.REG_SZ, "ESKD_Material_Sync_v5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")
                        winreg.SetValueEx(k_inver, "RuntimeVersion", 0, winreg.REG_SZ, "v4.0.30319")
                        winreg.SetValueEx(k_inver, "CodeBase", 0, winreg.REG_SZ, codebase_url)

                    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"{clsid_root}\Implemented Categories\{{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}}") as k_cat:
                        pass

                    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"{clsid_root}\ProgId") as k_prg:
                        winreg.SetValueEx(k_prg, "", 0, winreg.REG_SZ, "ESKD.MaterialSync.SwAddin_v5")

                    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\Classes\ESKD.MaterialSync.SwAddin_v5") as k_p:
                        winreg.SetValueEx(k_p, "", 0, winreg.REG_SZ, "ESKD.MaterialSync.SwAddin")

                    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\Classes\ESKD.MaterialSync.SwAddin_v5\CLSID") as k_prog_clsid:
                        winreg.SetValueEx(k_prog_clsid, "", 0, winreg.REG_SZ, guid_str)
                except Exception: pass

                # HKLM
                try:
                    with winreg.CreateKey(winreg.HKEY_LOCAL_MACHINE, f"Software\\SolidWorks\\AddIns\\{guid_str}") as k_hklm:
                        winreg.SetValueEx(k_hklm, "", 0, winreg.REG_DWORD, 1)
                        winreg.SetValueEx(k_hklm, "Title", 0, winreg.REG_SZ, title_str)
                        winreg.SetValueEx(k_hklm, "Description", 0, winreg.REG_SZ, desc_str)
                except Exception: pass

                # HKCU
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, f"Software\\SolidWorks\\AddIns\\{guid_str}") as k_add:
                    winreg.SetValueEx(k_add, "", 0, winreg.REG_DWORD, 1)
                    winreg.SetValueEx(k_add, "Title", 0, winreg.REG_SZ, title_str)
                    winreg.SetValueEx(k_add, "Description", 0, winreg.REG_SZ, desc_str)

                # AddinsStartup
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, f"Software\\SolidWorks\\AddinsStartup\\{guid_str}") as k_start:
                    winreg.SetValueEx(k_start, "", 0, winreg.REG_DWORD, 1)

                # ESKD_Settings in HKCU
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\SolidWorks\ESKD_Settings") as k_eskd:
                    winreg.SetValueEx(k_eskd, "Author", 0, winreg.REG_SZ, author if author else "Лунин В.И.")
                    winreg.SetValueEx(k_eskd, "Checker", 0, winreg.REG_SZ, "")
                    winreg.SetValueEx(k_eskd, "Organization", 0, winreg.REG_SZ, firm if firm else "123")
                    winreg.SetValueEx(k_eskd, "AutoMass", 0, winreg.REG_DWORD, 1)
                    winreg.SetValueEx(k_eskd, "MassDecimals", 0, winreg.REG_DWORD, 2)
                    winreg.SetValueEx(k_eskd, "AutoCenterMass", 0, winreg.REG_DWORD, 1)
                    winreg.SetValueEx(k_eskd, "AuthorList", 0, winreg.REG_SZ, author if author else "Лунин В.И.")

                # Favorite Materials with '/'
                fav_list = [
                    "Библиотека_Материалов_ГОСТ|Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89|1001",
                    "Библиотека_Материалов_ГОСТ|Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89|1002",
                    "Библиотека_Материалов_ГОСТ|Труба 80х80х4 ГОСТ 8639-82 / В 10 ГОСТ 13663-86|1003",
                    "Библиотека_Материалов_ГОСТ|Труба 57х3,5 ГОСТ 8732-78 / В 10 ГОСТ 8731-74|1005",
                    "Библиотека_Материалов_ГОСТ|Труба 102х4 ГОСТ 8732-78 / В 20 ГОСТ 8731-74|1006",
                    "Библиотека_Материалов_ГОСТ|Сталь 3сп (ГОСТ 380-2005)|1007",
                    "Библиотека_Материалов_ГОСТ|Сталь 20 (ГОСТ 1050-2013)|1008",
                    "Библиотека_Материалов_ГОСТ|Сталь 45 (ГОСТ 1050-2013)|1009",
                    "Библиотека_Материалов_ГОСТ|Сталь 09Г2С (ГОСТ 19281-2014)|1011"
                ]
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\SolidWorks\SOLIDWORKS 2025\Material") as k_mat:
                    for i, fv in enumerate(fav_list, start=1):
                        winreg.SetValueEx(k_mat, f"Favorite Material {i}", 0, winreg.REG_SZ, fv)
                        winreg.SetValueEx(k_mat, f"_FavMaterial{i}", 0, winreg.REG_SZ, fv)
                    winreg.SetValueEx(k_mat, "__NumOfFavs", 0, winreg.REG_DWORD, len(fav_list))

                self.log("Нативная надстройка ЕСКД v5, панель CommandManager и Избранные материалы успешно настроены!", "SUCCESS")
            except Exception as e:
                self.log(f"Предупреждение при настройке надстройки: {e}", "WARN")

        # 6. Проверка и регистрация надстройки Drw (CAD Booster Drew)
        drew_guid = "{08C4BC0B-C36C-470E-A0EA-02232F023333}"
        drew_candidates = [
            r"C:\Program Files\CAD Booster\Drew\CADBooster.Drew.Drawing.dll",
            os.path.join(os.environ.get("LOCALAPPDATA", ""), r"CAD Booster\Drew\CADBooster.Drew.Drawing.dll"),
            os.path.join(root_p, "03_Макросы_и_Плагины", "Drw_System_Automation", "2_комплект_издания", "bin", "CADBooster.Drew.Drawing.dll")
        ]
        drew_dll = next((p for p in drew_candidates if os.path.exists(p)), None)
        if drew_dll:
            try:
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"Software\SolidWorks\AddIns\{drew_guid}") as k_drew:
                    winreg.SetValueEx(k_drew, "", 0, winreg.REG_DWORD, 1)
                    winreg.SetValueEx(k_drew, "Title", 0, winreg.REG_SZ, "Drew")
                    winreg.SetValueEx(k_drew, "Description", 0, winreg.REG_SZ, "Drew Drawing Automation")
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"Software\SolidWorks\AddinsStartup\{drew_guid}") as k_drew_st:
                    winreg.SetValueEx(k_drew_st, "", 0, winreg.REG_DWORD, 1)

                drew_codebase = "file:///" + drew_dll.replace(chr(92), "/")
                drew_clsid = rf"Software\Classes\CLSID\{drew_guid}"
                full_class = "CADBooster.Drew.Drawing.SolidWorks.Integration.DrewAddin"
                assembly_nm = "CADBooster.Drew.Drawing, Version=4.3.0.0, Culture=neutral, PublicKeyToken=null"

                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, drew_clsid) as k_dc:
                    winreg.SetValueEx(k_dc, "", 0, winreg.REG_SZ, full_class)
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"{drew_clsid}\InprocServer32") as k_din:
                    winreg.SetValueEx(k_din, "", 0, winreg.REG_SZ, "mscoree.dll")
                    winreg.SetValueEx(k_din, "ThreadingModel", 0, winreg.REG_SZ, "Both")
                    winreg.SetValueEx(k_din, "Class", 0, winreg.REG_SZ, full_class)
                    winreg.SetValueEx(k_din, "Assembly", 0, winreg.REG_SZ, assembly_nm)
                    winreg.SetValueEx(k_din, "RuntimeVersion", 0, winreg.REG_SZ, "v4.0.30319")
                    winreg.SetValueEx(k_din, "CodeBase", 0, winreg.REG_SZ, drew_codebase)
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"{drew_clsid}\InprocServer32\4.3.0.0") as k_din_v:
                    winreg.SetValueEx(k_din_v, "Class", 0, winreg.REG_SZ, full_class)
                    winreg.SetValueEx(k_din_v, "Assembly", 0, winreg.REG_SZ, assembly_nm)
                    winreg.SetValueEx(k_din_v, "RuntimeVersion", 0, winreg.REG_SZ, "v4.0.30319")
                    winreg.SetValueEx(k_din_v, "CodeBase", 0, winreg.REG_SZ, drew_codebase)
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"{drew_clsid}\Implemented Categories\{{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}}") as _:
                    pass

                try:
                    with winreg.CreateKey(winreg.HKEY_LOCAL_MACHINE, rf"SOFTWARE\SolidWorks\AddIns\{drew_guid}") as k_drew_m:
                        winreg.SetValueEx(k_drew_m, "", 0, winreg.REG_DWORD, 1)
                        winreg.SetValueEx(k_drew_m, "Title", 0, winreg.REG_SZ, "Drew")
                        winreg.SetValueEx(k_drew_m, "Description", 0, winreg.REG_SZ, "Drew Drawing Automation")
                except Exception: pass

                self.log(f"Надстройка черчения Drw (CAD Booster Drew) успешно активирована ({drew_dll})!", "SUCCESS")
            except Exception as e_drew:
                self.log(f"Предупреждение при регистрации Drew: {e_drew}", "WARN")

        if show_msgbox:
            self.log("\nНАСТРОЙКА РАБОЧЕГО МЕСТА ЗАВЕРШЕНА!", "SUCCESS")
            messagebox.showinfo(
                "Успех", 
                "Рабочее место SolidWorks 2025 успешно настроено!\n\n"
                "• Полный корпоративный профиль ЕСКД перенесён и применён.\n"
                "• Плагин Zero-Click автосинхронизации материалов ЕСКД активирован.\n"
                "• Надстройка автоматизации черчения Drw (Drew) зарегистрирована.\n"
                "• Шаблоны документов (Деталь, Сборка, Чертеж) и База форматок подключены.\n"
                "• 9 кнопок SWPlus встроены в верхнюю панель быстрого доступа (QAT).\n"
                "• Динамическая подсветка кромок и граней (Dynamic Highlight) включена.\n"
                "• Шрифты ГОСТ установлены."
            )

if __name__ == "__main__":
    tk_root = tk.Tk()
    app = CADConfiguratorApp(tk_root)
    tk_root.mainloop()
