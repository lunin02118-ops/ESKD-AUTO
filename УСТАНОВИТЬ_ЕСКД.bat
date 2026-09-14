@echo off
rem ============================================================
rem  ESKD toolkit - workstation setup from the toolkit folder
rem  (console variant of the setup window, no admin rights needed)
rem ============================================================
rem pushd maps a network (UNC) folder to a temporary drive letter.
pushd "%~dp0"
echo Toolkit folder: %~dp0
echo.
rem The batch file stays pure ASCII: cmd reads it in the OEM code page, so the Cyrillic
rem folder name "01_..." is located by wildcard instead of being typed literally.
set "SETUP_PS1="
for /d %%D in ("%~dp001_*") do if exist "%%~fD\Setup_Workstation_SolidWorks.ps1" set "SETUP_PS1=%%~fD\Setup_Workstation_SolidWorks.ps1"
if not defined SETUP_PS1 (
    echo  Setup_Workstation_SolidWorks.ps1 was not found in "%~dp001_*"
    popd
    pause
    exit /b 2
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SETUP_PS1%" -CloseMode Ask
set RC=%errorlevel%
popd
echo.
echo ============================================================
if %RC% equ 0 (echo  SETUP FINISHED SUCCESSFULLY) else (echo  SETUP FINISHED WITH CODE %RC% - check messages above)
echo ============================================================
pause
