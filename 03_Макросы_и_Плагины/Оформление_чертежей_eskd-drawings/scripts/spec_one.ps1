param([string]$Drw)
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
$m = 'D:\Work\_Инструменты_Конструктора\03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\SpecEditor\SpecEditor.swp'
python run_macro.py $Drw $m SpecEditor_run main --drop-sheet SP1
$h = $null
for ($i = 0; $i -lt 20 -and -not $h; $i++) {
    Start-Sleep 1
    $h = python macro_ui.py list | Select-String 'SpecEditor'
}
if (-not $h) { Write-Output 'окно SpecEditor не появилось'; exit 1 }
python macro_ui.py shot se_form.png | Out-Null
python macro_ui.py click 470 305
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep 2
    if (-not (python macro_ui.py list | Select-String 'SpecEditor')) { break }
}
python macro_ui.py list
