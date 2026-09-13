<#
.SYNOPSIS
    Регистрация надстройки ЕСКД в SolidWorks на этом компьютере.

.DESCRIPTION
    Регистрирует ESKD_Material_Sync_v5.dll, лежащую рядом со скриптом, через общий модуль Register-EskdAddin.ps1:
    для текущего пользователя, а при запуске от администратора — и в HKLM. Если сборки нет (чистый клон
    репозитория), она собирается build.ps1. Пути шаблонов, справочники SWPlus, профиль реестра и избранные
    материалы настраивает установщик 01_Настройки_SolidWorks\Setup_Workstation_SolidWorks.ps1.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File register_eskd.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot "Register-EskdAddin.ps1")

$dll = Join-Path $PSScriptRoot "ESKD_Material_Sync_v5.dll"
if (-not (Test-Path -LiteralPath $dll)) {
    Write-Host "Сборка надстройки не найдена — сборка из исходников (build.ps1)..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "build.ps1")
}

$state = Register-EskdAddin -DllPath $dll -SystemWide:(Test-EskdAdministrator)
$state | Format-List | Out-String | Write-Host
if (-not ($state.UserDllExists -and $state.UserAddIn -and $state.UserStartup)) {
    Write-Host "[ОШИБКА] Регистрация неполная — см. значения выше." -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Надстройка ЕСКД зарегистрирована. Запустите SolidWorks заново, чтобы она загрузилась." -ForegroundColor Green
