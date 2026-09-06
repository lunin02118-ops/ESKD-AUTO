# -*- mode: python ; coding: utf-8 -*-


a = Analysis(
    ['D:\\Work\\_Инструменты_Конструктора\\01_Настройки_SolidWorks\\CAD_Workstation_Configurator.py'],
    pathex=[],
    binaries=[],
    datas=[],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='Настройка_Рабочего_Места_SolidWorks',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=['D:\\Work\\_Инструменты_Конструктора\\01_Настройки_SolidWorks\\app_icon.ico'],
)
