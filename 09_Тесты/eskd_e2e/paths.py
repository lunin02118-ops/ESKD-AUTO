# -*- coding: utf-8 -*-
"""Пути репозитория и компонентов, которые проверяет харнесс."""
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TESTS = ROOT / "09_Тесты"

ADDIN_DIR = ROOT / "03_Макросы_и_Плагины" / "ESKD_Material_Sync_Addin"
ADDIN_DLL = ADDIN_DIR / "ESKD_Material_Sync_v5.dll"
ESKD_SYNC_EXE = ADDIN_DIR / "ESKD_Sync.exe"
ADDIN_PROGID = "ESKD.MaterialSync.SwAddin_v5"
ADDIN_LOG = Path(os.environ.get("TEMP", r"C:\Temp")) / "eskd_material_sync.log"

SWPLUS = ROOT / "03_Макросы_и_Плагины" / "Макросы_SW_ZTool" / "SWPlusMacro_v_2018_SP0.0"
SWPLUS_DICTIONARY = SWPLUS / "SpecEditor" / "MyProperties_1.ini"
SHEET_FORMATS = SWPLUS / "Основные надписи"
SPEC_FORMATS = SWPLUS / "SpecEditor"

TEMPLATES = ROOT / "02_Шаблоны_и_Форматки" / "Шаблоны документов"
PART_TEMPLATE = TEMPLATES / "Деталь.prtdot"
ASSEMBLY_TEMPLATE = TEMPLATES / "Сборка.asmdot"
DRAWING_TEMPLATE = TEMPLATES / "Чертеж.drwdot"
PROPERTY_TAB_TEMPLATES = ROOT / "02_Шаблоны_и_Форматки" / "Шаблоны свойств"

MATERIAL_DB = ROOT / "04_Библиотеки_Материалов_и_Профилей" / "Библиотека материалов" / "Библиотека_Материалов_ГОСТ.sldmat"

PROBE_DIR = TESTS / "probe"
PROBE_SOURCE = PROBE_DIR / "ESKD_ProbeHost.cs"
PROBE_BIN = PROBE_DIR / "bin"
PROBE_EXE = PROBE_BIN / "ESKD_ProbeHost.exe"

FIXTURES = TESTS / "fixtures"
FIXTURES_A = FIXTURES / "A"
FIXTURE_MANIFEST = FIXTURES / "manifest.json"
BASELINE = TESTS / "baseline"

RUNS = ROOT / "08_Результаты_Тестирования" / "runs"

SW_INSTALL = Path(r"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS")
SW_INTEROP_DIR = SW_INSTALL / "api" / "redist"
CSC = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"

# Корпус Б — реальные файлы архива как есть (только чтение). Материалы у них прежние: у трубы B-01 «Сталь 20кп» из старой
# базы, у B-02 материала нет, у B-03 марка из «Марочника». Эти исходники открывает только R01: сравнение с поведением v5
# возможно лишь на тех же файлах. Рабочие сценарии идут на копиях с сортаментом из библиотеки (CORPUS_B_LIBRARY).
CORPUS_B = {
    "B-01": [
        ROOT / "08_Результаты_Тестирования" / "ПРТИ.468211.010_Стойка_Труба_80х80х4" / "ПРТИ.468211.010.SLDPRT",
        ROOT / "08_Результаты_Тестирования" / "ПРТИ.468211.010_Стойка_Труба_80х80х4" / "ПРТИ.468211.010.SLDDRW",
    ],
    "B-02": [
        ROOT / "08_Результаты_Тестирования" / "E2E_Execution_Test" / "АБВГ.123456.001 Кронштейн.SLDPRT",
        ROOT / "08_Результаты_Тестирования" / "E2E_Execution_Test" / "АБВГ.123456.000 СБ Сборка рамы.SLDASM",
    ],
    "B-03": [
        ROOT / "08_Результаты_Тестирования" / "AutoDrawing_Test" / "Тест_Многотельная_Деталь.SLDPRT",
    ],
}

# Копии корпуса Б, в которых конструктор штатно назначил сортамент из корпоративной библиотеки по геометрии
# (fixtures/build_corpus_b.py, решение владельца 13.09.2026). Материалы и массы — в manifest.json, раздел corpus_b.
FIXTURES_B = FIXTURES / "B"
CORPUS_B_LIBRARY = {
    "B-01": [FIXTURES_B / "ПРТИ.468211.010.SLDPRT", FIXTURES_B / "ПРТИ.468211.010.SLDDRW"],
}

# Корпус В — рабочие проекты владельца; путь задаётся переменной окружения.
CORPUS_C_ENV = "ESKD_REAL_CORPUS"
