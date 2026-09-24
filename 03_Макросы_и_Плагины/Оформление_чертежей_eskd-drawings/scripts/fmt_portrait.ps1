param([string]$Drw)
Set-Location $PSScriptRoot
# Макросы SW+ — из инструментария: ESKD_TOOLKIT, локальная копия установщика, NAS, копия разработчика (аудит 24.09.2026)
$rel = '03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\DProp\DProp.swp'
$m = @($env:ESKD_TOOLKIT, (Join-Path $env:LOCALAPPDATA 'ESKD\Toolkit'),
       '\\Synology_TR\Конструкторский отдел\_Библиотека проектирования\_инструменты_конструктора',
       'D:\Work\_Инструменты_Конструктора') | Where-Object { $_ } |
     ForEach-Object { Join-Path $_ $rel } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $m) { throw "Не найден макрос SW+: $rel" }
Start-Job -ScriptBlock {
    param($p, $d, $m)
    Set-Location $p
    python run_macro.py $d $m DProp_run main
} -ArgumentList $PSScriptRoot, $Drw, $m | Out-Null
$ok = $false
for ($i = 0; $i -lt 30 -and -not $ok; $i++) { Start-Sleep 1; $ok = (python macro_ui.py list | Select-String 'DProp') -ne $null }
if (-not $ok) { Write-Output 'окно DProp не появилось'; exit 1 }
python macro_ui.py click 486 85 0            # Изменить основную надпись
$ok = $false
for ($i = 0; $i -lt 30 -and -not $ok; $i++) { Start-Sleep 1; $ok = (python macro_ui.py list | Select-String 'Master') -ne $null }
if (-not $ok) { Write-Output 'окно Master не появилось'; exit 1 }
python macro_ui.py click 36 137 0            # Ориентация: Книжная
Start-Sleep 1
python macro_ui.py shot fmt_check.png | Out-Null
python macro_ui.py click 313 147 0           # Ok
Start-Sleep 8
python macro_ui.py list
python macro_ui.py click 537 185 0           # Закрыть (DProp)
Start-Sleep 2
python sheet_info.py $Drw
