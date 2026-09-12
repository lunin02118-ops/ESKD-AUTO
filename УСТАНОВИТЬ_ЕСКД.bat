@echo off
rem ============================================================
rem  ESKD AUTO - one-click workstation deployment
rem  (SolidWorks 2025 + GOST title blocks + ESKD add-in)
rem ============================================================
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights (UAC)...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
cd /d "%~dp0"
echo Root: %~dp0
echo.
rem The batch file stays pure ASCII: cmd reads it in the OEM code page, so the Cyrillic
rem folder name "01_..." is located by wildcard instead of being typed literally.
set "SETUP_PS1="
for /d %%D in ("%~dp001_*") do if exist "%%~fD\Setup_Workstation_SolidWorks.ps1" set "SETUP_PS1=%%~fD\Setup_Workstation_SolidWorks.ps1"
if not defined SETUP_PS1 (
    echo  Setup_Workstation_SolidWorks.ps1 was not found in "%~dp001_*"
    pause
    exit /b 2
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SETUP_PS1%"
set RC=%errorlevel%
echo.
echo ============================================================
if %RC% equ 0 (echo  DEPLOYMENT FINISHED SUCCESSFULLY) else (echo  DEPLOYMENT FINISHED WITH CODE %RC% - check messages above)
echo ============================================================
pause
