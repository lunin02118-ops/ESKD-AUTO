<#
.SYNOPSIS
    Скорая помощь: SolidWorks не запускается после настройки рабочего места (графика).
.DESCRIPTION
    Выключает аппаратный конвейер графики SolidWorks 2025 и снимает маску RealView, оставленную настройкой рабочего
    места: на видеокарте, которая этот конвейер не тянет (встроенная Intel/AMD, старый драйвер, виртуальная машина),
    SolidWorks с ним не стартует. Программный OpenGL остаётся разрешённым — SolidWorks выберет режим сам.

    Права администратора не нужны: пишется только раздел текущего пользователя. SolidWorks должен быть закрыт.
    После запуска настройку рабочего места можно повторять: в окне есть галочка «Безопасная графика».
.PARAMETER SwVersion
    Раздел версии («SOLIDWORKS 2025» по умолчанию, можно «SOLIDWORKS 2024»).
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Безопасная_графика_SolidWorks.ps1
#>

[CmdletBinding()]
param([string]$SwVersion = "SOLIDWORKS 2025")

$ErrorActionPreference = "Stop"
$swRoot = "HKCU:\Software\SolidWorks\$SwVersion"

function Set-Value([string]$key, [string]$name, [int]$value) {
    if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -LiteralPath $key -Name $name -Value $value -Type DWord
    Write-Host "  $name = $value"
}

Write-Host "Безопасная графика SolidWorks ($SwVersion)"
Set-Value "$swRoot\Performance" "Use Performance Pipeline 2020" 0
Set-Value "$swRoot\Performance" "Use GPU Silhouette Edges" 0
Set-Value "$swRoot\General" "Software OGL Alarm" 1
Set-Value "$swRoot\General" "Use Software OGL" 0

$allowList = "HKCU:\Software\SolidWorks\AllowList"
if (Test-Path -LiteralPath $allowList) {
    Remove-Item -LiteralPath $allowList -Recurse -Force
    Write-Host "  маска RealView (AllowList) снята"
}

Write-Host ""
Write-Host "Готово. Запустите SolidWorks." -ForegroundColor Green
Write-Host "Если он всё равно не стартует, дело не в графике: пришлите журнал из %LOCALAPPDATA%\ESKD\Logs."
