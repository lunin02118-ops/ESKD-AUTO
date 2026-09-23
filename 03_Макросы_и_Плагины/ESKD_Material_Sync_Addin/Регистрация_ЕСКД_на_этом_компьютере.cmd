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

rem Started from a 32-bit program, System32 is the 32-bit PowerShell and the registration would go to Wow6432Node,
rem where 64-bit SolidWorks does not look. Sysnative is visible to 32-bit programs only and leads to the 64-bit one.
set "WINPS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "WINPS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%WINPS%" set "WINPS=powershell.exe"
"%WINPS%" -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%"
set "RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %RESULT%
