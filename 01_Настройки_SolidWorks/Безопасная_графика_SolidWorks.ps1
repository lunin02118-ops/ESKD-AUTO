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

# AllowList — база видеокарт самого SolidWorks (больше тысячи ключей). Снимаем только маску настройки рабочего места
# (Workarounds = 0x32408: RealView + аппаратный конвейер в обход проверки SolidWorks) — из-за неё SolidWorks 2025 SP3
# на игровой GeForce падает при запуске в sldappu.dll. Базу видеокарт не трогаем.
$allowList = "HKCU:\Software\SolidWorks\AllowList"
$mask = 0x32408
$cleared = 0
if (Test-Path -LiteralPath $allowList) {
    foreach ($key in (Get-ChildItem -LiteralPath $allowList -Recurse -ErrorAction SilentlyContinue)) {
        if ($key.SubKeyCount -gt 0) { continue }
        $value = $key.GetValue("Workarounds", $null)
        if ($null -eq $value -or [int]$value -ne $mask) { continue }
        Remove-Item -LiteralPath $key.PSPath -Force -ErrorAction SilentlyContinue
        $cleared++
    }
    $current = Join-Path $allowList "Current"
    if ((Test-Path -LiteralPath $current) -and [int](Get-ItemProperty -LiteralPath $current -Name "Workarounds" -ErrorAction SilentlyContinue).Workarounds -eq $mask) {
        Remove-ItemProperty -LiteralPath $current -Name "Workarounds" -Force -ErrorAction SilentlyContinue
        $cleared++
    }
}
if ($cleared) { Write-Host "  маска RealView (AllowList) снята: $cleared записей" }
else { Write-Host "  маски RealView (AllowList) нет — ничего снимать не нужно" }

Write-Host ""
Write-Host "Готово. Запустите SolidWorks." -ForegroundColor Green
Write-Host "Если он всё равно не стартует, дело не в графике: пришлите журнал из %LOCALAPPDATA%\ESKD\Logs."
