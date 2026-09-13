@echo off
rem ESKD add-in registration for SolidWorks on this computer.
rem ASCII only: cmd.exe reads batch files in the OEM code page, Cyrillic text would be garbled.
title ESKD add-in registration

set "PS_SCRIPT=%~dp0register_eskd.ps1"
if not exist "%PS_SCRIPT%" (
    echo [ERROR] register_eskd.ps1 not found next to this file.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%"
set "RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %RESULT%
