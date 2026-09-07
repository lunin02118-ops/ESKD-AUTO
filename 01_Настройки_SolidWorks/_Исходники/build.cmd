@echo off
chcp 1251 >nul
title Сборка Настройка_Рабочего_Места_SolidWorks.exe
cd /d "%~dp0"

echo =======================================================================
echo    СБОРКА НАСТРОЙКА РАБОЧЕГО МЕСТА SOLIDWORKS (PyInstaller)
echo =======================================================================
echo.

pyinstaller --noconfirm --distpath ".." --workpath "..\build_temp" "Настройка_Рабочего_Места_SolidWorks.spec"
if %errorlevel% neq 0 (
    echo [ОШИБКА] Сборка завершилась с ошибкой!
    if "%1"=="" pause
    exit /b %errorlevel%
)

if exist "..\build_temp" rd /s /q "..\build_temp"

echo.
echo [УСПЕХ] Файл Настройка_Рабочего_Места_SolidWorks.exe успешно собран в папке:
echo         01_Настройки_SolidWorks\
echo.
if "%1"=="" pause