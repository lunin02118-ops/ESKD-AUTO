@echo off
chcp 65001 >nul
title Setup ESKD Addin

set SCRIPT_DIR=%~dp0
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%register_addin.ps1"
if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Registration failed with code: %errorlevel%
)
echo.
pause