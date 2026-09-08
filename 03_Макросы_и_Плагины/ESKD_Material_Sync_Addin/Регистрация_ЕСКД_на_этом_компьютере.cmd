@echo off
chcp 65001 >nul
title “αβ ­®Άª  ¨ ΰ¥£¨αβΰ ζ¨ο …‘„ ¤«ο SolidWorks
echo =======================================================================
echo          “‘’€‚€ ‘‚„ƒ €…’€ …‘„ „‹ SOLIDWORKS 2025
echo =======================================================================
echo.

set SCRIPT_DIR=%~dp0
set PS_SCRIPT=%SCRIPT_DIR%register_eskd.ps1
if not exist "%PS_SCRIPT%" (
    set PS_SCRIPT=%SCRIPT_DIR%scripts\register_eskd.ps1
)

if not exist "%PS_SCRIPT%" (
    echo [€] ‘ªΰ¨―β ΰ¥£¨αβΰ ζ¨¨ ­¥ ­ ©¤¥­!
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%"

echo.
pause