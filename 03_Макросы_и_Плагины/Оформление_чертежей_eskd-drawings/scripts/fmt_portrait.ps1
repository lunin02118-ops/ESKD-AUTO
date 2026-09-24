param([string]$Drw)
Set-Location $PSScriptRoot
$m = 'D:\Work\_Инструменты_Конструктора\03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\DProp\DProp.swp'
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
