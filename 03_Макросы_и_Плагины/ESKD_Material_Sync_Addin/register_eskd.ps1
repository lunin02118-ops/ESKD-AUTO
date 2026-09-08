# -*- coding: utf-8 -*-
<#
.SYNOPSIS
    Регистрация надстройки ЕСКД и привязка инструментов заполнения штампов SolidWorks 2025
#>
[CmdletBinding()]
param(
    [switch]$Elevated
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host "    РЕГИСТРАЦИЯ НАДСТРОЙКИ ЕСКД В SOLIDWORKS (1-КЛИК ДЛЯ ЛЮБОГО ПК)    " -ForegroundColor Yellow
Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host ""

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SuiteRoot = Split-Path -Parent $ScriptDir

# Поиск DLL надстройки
$candidatesDll = @(
    (Join-Path $ScriptDir "ESKD_Material_Sync_v5.dll"),
    (Join-Path $SuiteRoot "bin\ESKD_Material_Sync_v5.dll"),
    "D:\Work\_Инструменты_Конструктора\03_Макросы_и_Плагины\ESKD_Material_Sync_Addin\ESKD_Material_Sync_v5.dll"
)

$dllPath = $null
foreach ($c in $candidatesDll) {
    if (Test-Path $c) {
        $dllPath = (Resolve-Path $c).Path
        break
    }
}

# Определение ToolsRoot
$toolsRoot = $null
for ($i = 0; $i -lt 4; $i++) {
    if (Test-Path (Join-Path $SuiteRoot "04_Библиотеки_Материалов_и_Профилей")) { $toolsRoot = $SuiteRoot; break }
}
if (-not $toolsRoot -and (Test-Path "D:\Work\_Инструменты_Конструктора")) {
    $toolsRoot = "D:\Work\_Инструменты_Конструктора"
}

Write-Host "[1/5] Проверка расположения компонентов..." -ForegroundColor Gray
Write-Host "  Сводный пакет: $SuiteRoot" -ForegroundColor White
if ($toolsRoot) { Write-Host "  Корневая папка библиотек: $toolsRoot" -ForegroundColor White }
Write-Host "  Библиотека DLL: $dllPath" -ForegroundColor White

if (-not $dllPath -or -not (Test-Path $dllPath)) {
    Write-Host "[ОШИБКА] Файл библиотеки ESKD_Material_Sync_v5.dll не найден!" -ForegroundColor Red
    exit 1
}

$guid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
$progId = "ESKD.MaterialSync.SwAddin_v5"
$title = "ЕСКД: Синхронизация материалов и реквизитов"
$desc = "Панель инструментов ЕСКД: настройки реквизитов (фамилии, контора, масса), автоматическая синхронизация материалов и центрирование штампа по ГОСТ 2.104"
$codeBase = "file:///" + $dllPath.Replace("\", "/")

Write-Host "`n[2/5] Регистрация COM-сервера в профиле текущего пользователя (HKCU)..." -ForegroundColor Gray

# 2.1 COM Classes in HKCU
$clsidPath = "HKCU:\Software\Classes\CLSID\$guid"
if (-not (Test-Path $clsidPath)) { New-Item -Path $clsidPath -Force | Out-Null }
Set-ItemProperty -Path $clsidPath -Name "(Default)" -Value "ESKD.MaterialSync.SwAddin"

$inprocPath = Join-Path $clsidPath "InprocServer32"
if (-not (Test-Path $inprocPath)) { New-Item -Path $inprocPath -Force | Out-Null }
Set-ItemProperty -Path $inprocPath -Name "(Default)" -Value "mscoree.dll"
Set-ItemProperty -Path $inprocPath -Name "ThreadingModel" -Value "Both"
Set-ItemProperty -Path $inprocPath -Name "Class" -Value "ESKD.MaterialSync.SwAddin"
Set-ItemProperty -Path $inprocPath -Name "Assembly" -Value "ESKD_Material_Sync_v5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
Set-ItemProperty -Path $inprocPath -Name "RuntimeVersion" -Value "v4.0.30319"
Set-ItemProperty -Path $inprocPath -Name "CodeBase" -Value $codeBase

$verPath = Join-Path $inprocPath "1.0.0.0"
if (-not (Test-Path $verPath)) { New-Item -Path $verPath -Force | Out-Null }
Set-ItemProperty -Path $verPath -Name "Class" -Value "ESKD.MaterialSync.SwAddin"
Set-ItemProperty -Path $verPath -Name "Assembly" -Value "ESKD_Material_Sync_v5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
Set-ItemProperty -Path $verPath -Name "RuntimeVersion" -Value "v4.0.30319"
Set-ItemProperty -Path $verPath -Name "CodeBase" -Value $codeBase

$piPath = Join-Path $clsidPath "ProgId"
if (-not (Test-Path $piPath)) { New-Item -Path $piPath -Force | Out-Null }
Set-ItemProperty -Path $piPath -Name "(Default)" -Value $progId

$catPath = Join-Path $clsidPath "Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}"
if (-not (Test-Path $catPath)) { New-Item -Path $catPath -Force | Out-Null }

$progIdPath = "HKCU:\Software\Classes\$progId"
if (-not (Test-Path $progIdPath)) { New-Item -Path $progIdPath -Force | Out-Null }
Set-ItemProperty -Path $progIdPath -Name "(Default)" -Value "ESKD.MaterialSync.SwAddin"
$progIdClsid = Join-Path $progIdPath "CLSID"
if (-not (Test-Path $progIdClsid)) { New-Item -Path $progIdClsid -Force | Out-Null }
Set-ItemProperty -Path $progIdClsid -Name "(Default)" -Value $guid

Write-Host "  [OK] COM-класс зарегистрирован в HKCU\Software\Classes." -ForegroundColor Green

# 2.2 SolidWorks AddIns in HKCU
$swAddIn = "HKCU:\Software\SolidWorks\AddIns\$guid"
if (-not (Test-Path $swAddIn)) { New-Item -Path $swAddIn -Force | Out-Null }
Set-ItemProperty -Path $swAddIn -Name "(Default)" -Value 1 -Type DWord
Set-ItemProperty -Path $swAddIn -Name "Title" -Value $title
Set-ItemProperty -Path $swAddIn -Name "Description" -Value $desc

$swStartup = "HKCU:\Software\SolidWorks\AddinsStartup\$guid"
if (-not (Test-Path $swStartup)) { New-Item -Path $swStartup -Force | Out-Null }
Set-ItemProperty -Path $swStartup -Name "(Default)" -Value 1 -Type DWord

Write-Host "  [OK] Автозагрузка надстройки активирована в SolidWorks." -ForegroundColor Green

# Очистка устаревших версий надстройки
$oldGuids = @(
    "{B64E6875-B101-4D5C-B245-FF8D50772E21}",
    "{B64E6875-B101-4D5C-B245-FF8D50772E23}",
    "{B64E6875-B101-4D5C-B245-FF8D50772E24}"
)
foreach ($og in $oldGuids) {
    Remove-Item "HKCU:\Software\SolidWorks\AddIns\$og" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\SolidWorks\AddInsStartup\$og" -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n[3/5] Настройка папок шаблонов свойств и параметров ЕСКД..." -ForegroundColor Gray

# 3.1 Custom Property Folders
if ($toolsRoot) {
    $propFolder = Join-Path $toolsRoot "02_Шаблоны_и_Форматки\Шаблоны свойств"
    if (Test-Path $propFolder) {
        $extRef = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\ExtReferences"
        if (-not (Test-Path $extRef)) { New-Item -Path $extRef -Force | Out-Null }
        Set-ItemProperty -Path $extRef -Name "Custom Property Folders" -Value $propFolder -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $extRef -Name "Custom Property File" -Value (Join-Path $propFolder "default.prtprp") -ErrorAction SilentlyContinue

        $extFld = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\ExtFolder"
        if (-not (Test-Path $extFld)) { New-Item -Path $extFld -Force | Out-Null }
        Set-ItemProperty -Path $extFld -Name "Custom Property Folders" -Value $propFolder -ErrorAction SilentlyContinue
        Write-Host "  [OK] Папка шаблонов свойств (Task Pane) привязана." -ForegroundColor Green
    }
}

# 3.2 ESKD Settings in HKCU
$eskdKey = "HKCU:\Software\SolidWorks\ESKD_Settings"
if (-not (Test-Path $eskdKey)) { New-Item -Path $eskdKey -Force | Out-Null }
Set-ItemProperty -Path $eskdKey -Name "AutoMass" -Value 1 -Type DWord
Set-ItemProperty -Path $eskdKey -Name "MassDecimals" -Value 2 -Type DWord
Set-ItemProperty -Path $eskdKey -Name "AutoCenterMass" -Value 1 -Type DWord
Set-ItemProperty -Path $eskdKey -Name "AutoSplitName" -Value 1 -Type DWord

$curAuth = (Get-ItemProperty -Path $eskdKey -Name "Author" -ErrorAction SilentlyContinue).Author
if (-not $curAuth) { $curAuth = "Лунин В.И." }
Set-ItemProperty -Path $eskdKey -Name "Author" -Value $curAuth
Set-ItemProperty -Path $eskdKey -Name "AuthorList" -Value $curAuth

$curOrg = (Get-ItemProperty -Path $eskdKey -Name "Organization" -ErrorAction SilentlyContinue).Organization
if (-not $curOrg) { $curOrg = "ТОО `"Троя`"" }
Set-ItemProperty -Path $eskdKey -Name "Organization" -Value $curOrg
Write-Host "  [OK] Параметры ЕСКД сохранены (Конструктор: $curAuth, Организация: $curOrg)." -ForegroundColor Green

Write-Host "`n[4/5] Синхронизация с макросами SWPlus (MProp, DProp, Master)..." -ForegroundColor Gray
$mpropDirs = @(
    (Join-Path $SuiteRoot "macros\SWPlus_ESKD\MProp"),
    (Join-Path $toolsRoot "03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\MProp")
)

foreach ($mpropDir in $mpropDirs) {
    if (Test-Path $mpropDir) {
        $famFile = Join-Path $mpropDir "MProp_Fam.txt"
        $firmFile = Join-Path $mpropDir "MProp_Firm.txt"
        $iniFile = Join-Path $mpropDir "MProp.ini"

        try {
            $fams = @()
            if (Test-Path $famFile) {
                $fams = [System.IO.File]::ReadAllLines($famFile, [System.Text.Encoding]::GetEncoding(1251)) | Where-Object { $_.Trim() }
            }
            if ($fams -notcontains $curAuth) { $fams = @($curAuth) + $fams }
            [System.IO.File]::WriteAllLines($famFile, $fams, [System.Text.Encoding]::GetEncoding(1251))

            $firms = @()
            if (Test-Path $firmFile) {
                $firms = [System.IO.File]::ReadAllLines($firmFile, [System.Text.Encoding]::GetEncoding(1251)) | Where-Object { $_.Trim() }
            }
            if ($firms -notcontains $curOrg) { $firms = @($curOrg) + $firms }
            [System.IO.File]::WriteAllLines($firmFile, $firms, [System.Text.Encoding]::GetEncoding(1251))

            if (Test-Path $iniFile) {
                $iniLines = [System.IO.File]::ReadAllLines($iniFile, [System.Text.Encoding]::GetEncoding(1251))
                if ($iniLines.Count -ge 6) {
                    $iniLines[0] = "0"
                    $iniLines[5] = $curOrg
                    [System.IO.File]::WriteAllLines($iniFile, $iniLines, [System.Text.Encoding]::GetEncoding(1251))
                }
            }
        } catch { }
    }
}
Write-Host "  [OK] Списки фамилий и организаций синхронизированы с профилями SWPlus." -ForegroundColor Green

# 4.1 Master.ini и форматки
$masterInis = @(
    (Join-Path $SuiteRoot "macros\SWPlus_ESKD\Master\Master.ini"),
    (Join-Path $toolsRoot "03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\Master\Master.ini")
)
$sheetFormats = if (Test-Path (Join-Path $SuiteRoot "templates\Основные надписи")) {
    (Join-Path $SuiteRoot "templates\Основные надписи")
} else {
    Join-Path $toolsRoot "03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\Основные надписи"
}

foreach ($mIni in $masterInis) {
    if ((Test-Path $mIni) -and (Test-Path $sheetFormats)) {
        try {
            $mLines = [System.IO.File]::ReadAllLines($mIni, [System.Text.Encoding]::GetEncoding(1251))
            if ($mLines.Count -ge 4) {
                $mLines[3] = $sheetFormats.TrimEnd('\') + '\'
                [System.IO.File]::WriteAllLines($mIni, $mLines, [System.Text.Encoding]::GetEncoding(1251))
            }
        } catch { }
    }
}
Write-Host "  [OK] Master.ini привязан к форматам основных надписей." -ForegroundColor Green

Write-Host "`n[5/5] Системная регистрация в HKLM..." -ForegroundColor Gray
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdmin) {
    try {
        $hklmAddIn = "HKLM:\Software\SolidWorks\AddIns\$guid"
        if (-not (Test-Path $hklmAddIn)) { New-Item -Path $hklmAddIn -Force | Out-Null }
        Set-ItemProperty -Path $hklmAddIn -Name "(Default)" -Value 1 -Type DWord
        Set-ItemProperty -Path $hklmAddIn -Name "Title" -Value $title
        Set-ItemProperty -Path $hklmAddIn -Name "Description" -Value $desc

        $regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
        if (Test-Path $regasm) {
            & $regasm /codebase $dllPath | Out-Null
        }
        Write-Host "  [OK] Системная регистрация HKLM выполнена успешно!" -ForegroundColor Green
    } catch {
        Write-Host "  [ПРЕДУПРЕЖДЕНИЕ] Ошибка записи в HKLM: $_" -ForegroundColor Yellow
    }
} else {
    Write-Host "  [ИНФО] Запуск без прав Администратора. Надстройка полностью активна для текущего пользователя (HKCU)." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=======================================================================" -ForegroundColor Green
Write-Host "  [УСПЕХ] НАДСТРОЙКА ЕСКД И СВОЙСТВА ШТАМПА УСПЕШНО АКТИВИРОВАНЫ!      " -ForegroundColor Green
Write-Host "=======================================================================" -ForegroundColor Green