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
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp001_\230\235\241\340\256\351\352\250_\221\356\253\250\244\240\236\242\221\250\351\250\225_\221\250\253\250\256\242_SolidWorks\Setup_Workstation_SolidWorks.ps1"
set RC=%errorlevel%
echo.
echo ============================================================
if %RC% equ 0 (echo  DEPLOYMENT FINISHED SUCCESSFULLY) else (echo  DEPLOYMENT FINISHED WITH CODE %RC% - check messages above)
echo ============================================================
pause
