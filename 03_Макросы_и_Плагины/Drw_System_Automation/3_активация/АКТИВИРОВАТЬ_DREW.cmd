@echo off
powershell -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Client-Activate-Drew.ps1"
if errorlevel 1 pause
