@echo off
chcp 65001 >nul
title Установка и регистрация надстройки ЕСКД для SolidWorks

echo =======================================================================
echo    РЕГИСТРАЦИЯ НАДСТРОЙКИ ЕСКД В SOLIDWORKS (1-КЛИК ДЛЯ ЛЮБОГО ПК)
echo =======================================================================
echo.

:: Check Admin Rights
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [ВНИМАНИЕ] Требуются права Администратора для регистрации COM-библиотеки.
    echo Запуск от имени Администратора...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

set SCRIPT_DIR=%~dp0
set REGASM=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe

:: Detect DLL location (in same folder or in 03_Макросы_и_Плагины)
set DLL_PATH=%SCRIPT_DIR%ESKD_Material_Sync_v5.dll
if not exist "%DLL_PATH%" (
    set DLL_PATH=%SCRIPT_DIR%03_Макросы_и_Плагины\ESKD_Material_Sync_Addin\ESKD_Material_Sync_v5.dll
)

if not exist "%DLL_PATH%" (
    echo [ОШИБКА] Файл библиотеки не найден: "%DLL_PATH%"
    pause
    exit /b 1
)

if not exist "%REGASM%" (
    echo [ОШИБКА] Не найден RegAsm.exe в Microsoft .NET Framework 64-bit!
    pause
    exit /b 1
)

echo [1/4] Регистрация библиотеки ESKD_Material_Sync_v5.dll в COM-реестре...
echo Путь к DLL: "%DLL_PATH%"
"%REGASM%" /codebase "%DLL_PATH%"
if %errorlevel% neq 0 (
    echo [ОШИБКА] Ошибка при вызове RegAsm.exe!
    pause
    exit /b %errorlevel%
)

echo.
echo [2/4] Включение надстройки в SolidWorks...
set GUID={B64E6875-B101-4D5C-B245-FF8D50772E25}
set TITLE=ЕСКД: Синхронизация материалов и реквизитов
set DESC=Панель инструментов ЕСКД: настройки реквизитов (фамилии, контора, масса), автоматическая синхронизация материалов и центрирование штампа по ГОСТ 2.104

:: HKCU AddIns
reg add "HKCU\Software\SolidWorks\AddIns\%GUID%" /ve /t REG_DWORD /d 1 /f >nul
reg add "HKCU\Software\SolidWorks\AddIns\%GUID%" /v "Title" /t REG_SZ /d "%TITLE%" /f >nul
reg add "HKCU\Software\SolidWorks\AddIns\%GUID%" /v "Description" /t REG_SZ /d "%DESC%" /f >nul

:: HKCU AddinsStartup
reg add "HKCU\Software\SolidWorks\AddinsStartup\%GUID%" /ve /t REG_DWORD /d 1 /f >nul

:: HKLM AddIns
reg add "HKLM\Software\SolidWorks\AddIns\%GUID%" /ve /t REG_DWORD /d 1 /f >nul
reg add "HKLM\Software\SolidWorks\AddIns\%GUID%" /v "Title" /t REG_SZ /d "%TITLE%" /f >nul
reg add "HKLM\Software\SolidWorks\AddIns\%GUID%" /v "Description" /t REG_SZ /d "%DESC%" /f >nul

echo.
echo [3/4] Настройка общих параметров по умолчанию...
reg add "HKCU\Software\SolidWorks\ESKD_Settings" /v "AutoMass" /t REG_DWORD /d 1 /f >nul
reg add "HKCU\Software\SolidWorks\ESKD_Settings" /v "MassDecimals" /t REG_DWORD /d 2 /f >nul
reg add "HKCU\Software\SolidWorks\ESKD_Settings" /v "AutoCenterMass" /t REG_DWORD /d 1 /f >nul
reg add "HKCU\Software\SolidWorks\ESKD_Settings" /v "AutoSplitName" /t REG_DWORD /d 1 /f >nul

echo.
echo [4/4] Проверка реквизитов конструктора текущего пользователя Windows...
for /f "tokens=2*" %%A in ('reg query "HKCU\Software\SolidWorks\ESKD_Settings" /v "Author" 2^>nul ^| findstr /i "Author"') do set CUR_AUTH=%%B
if not "%CUR_AUTH%"=="" (
    echo   Текущий конструктор: %CUR_AUTH%
) else (
    echo   Фамилия конструктора для штампа не задана.
    echo   Вы сможете указать её в окне "Настройки ЕСКД" или ввести сейчас.
    set /p NEW_AUTH="  Введите Фамилию И.О. [или нажмите Enter для пропуска]: "
    if not "%NEW_AUTH%"=="" (
        reg add "HKCU\Software\SolidWorks\ESKD_Settings" /v "Author" /t REG_SZ /d "%NEW_AUTH%" /f >nul
        echo   [OK] Записано: %NEW_AUTH%
    )
)

echo.
echo =======================================================================
echo    [УСПЕХ] НАДСТРОЙКА ЕСКД УСПЕШНО ЗАРЕГИСТРИРОВАНА И ГОТОВА К РАБОТЕ!
echo =======================================================================
echo При следующем запуске SolidWorks на панели инструментов появится
echo меню "ЕСКД" и кнопки "Синхронизировать" и "Настройки ЕСКД".
echo.
pause
