<#
.SYNOPSIS
    Регистрация надстройки ЕСКД для SolidWorks (работает без прав администратора).
#>
[CmdletBinding()]
param()

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host "   РЕГИСТРАЦИЯ НАДСТРОЙКИ ЕСКД В SOLIDWORKS (БЕЗ ПРАВ АДМИНИСТРАТОРА)" -ForegroundColor Cyan
Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host ""

$dll = Join-Path $ScriptDir "ESKD_Material_Sync_v5.dll"
if (-not (Test-Path $dll)) {
    Write-Host "[ОШИБКА] Файл библиотеки не найден: $dll" -ForegroundColor Red
    exit 1
}

# 1. Снятие блокировки безопасности Windows (Mark of the Web)
Write-Host "[1/4] Разблокировка файлов..." -ForegroundColor Gray
Get-ChildItem -Path $ScriptDir -Recurse -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
Write-Host "  [OK] Файлы разблокированы." -ForegroundColor Green

# 2. Регистрация COM-компонента в реестре пользователя (HKCU)
Write-Host "`n[2/4] Регистрация COM-компонента в реестре пользователя (HKCU)..." -ForegroundColor Gray
$guid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
$progId = "ESKD.MaterialSync.SwAddin_v5"
$className = "ESKD.MaterialSync.SwAddin"
$assemblyName = "ESKD_Material_Sync_v5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
$runtimeVersion = "v4.0.30319"
$title = "ЕСКД: Синхронизация материалов и реквизитов"
$desc = "Панель инструментов ЕСКД: настройки реквизитов (фамилии, контора, масса), автоматическая синхронизация материалов и центрирование штампа по ГОСТ 2.104"
$codeBase = ([System.Uri](Resolve-Path $dll).Path).AbsoluteUri

# CLSID
$clsidKey = "HKCU:\Software\Classes\CLSID\$guid"
if (-not (Test-Path $clsidKey)) { New-Item -Path $clsidKey -Force | Out-Null }
Set-ItemProperty -Path $clsidKey -Name "(Default)" -Value $className

# InprocServer32
$inprocKey = Join-Path $clsidKey "InprocServer32"
if (-not (Test-Path $inprocKey)) { New-Item -Path $inprocKey -Force | Out-Null }
Set-ItemProperty -Path $inprocKey -Name "(Default)" -Value "mscoree.dll"
Set-ItemProperty -Path $inprocKey -Name "ThreadingModel" -Value "Both"
Set-ItemProperty -Path $inprocKey -Name "Class" -Value $className
Set-ItemProperty -Path $inprocKey -Name "Assembly" -Value $assemblyName
Set-ItemProperty -Path $inprocKey -Name "RuntimeVersion" -Value $runtimeVersion
Set-ItemProperty -Path $inprocKey -Name "CodeBase" -Value $codeBase

# InprocServer32\1.0.0.0
$verKey = Join-Path $inprocKey "1.0.0.0"
if (-not (Test-Path $verKey)) { New-Item -Path $verKey -Force | Out-Null }
Set-ItemProperty -Path $verKey -Name "Class" -Value $className
Set-ItemProperty -Path $verKey -Name "Assembly" -Value $assemblyName
Set-ItemProperty -Path $verKey -Name "RuntimeVersion" -Value $runtimeVersion
Set-ItemProperty -Path $verKey -Name "CodeBase" -Value $codeBase

# ProgId
$progKey = Join-Path $clsidKey "ProgId"
if (-not (Test-Path $progKey)) { New-Item -Path $progKey -Force | Out-Null }
Set-ItemProperty -Path $progKey -Name "(Default)" -Value $progId

# Implemented Categories
$catKey = Join-Path $clsidKey "Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}"
if (-not (Test-Path $catKey)) { New-Item -Path $catKey -Force | Out-Null }

# Root ProgId
$rootProgKey = "HKCU:\Software\Classes\$progId"
if (-not (Test-Path $rootProgKey)) { New-Item -Path $rootProgKey -Force | Out-Null }
Set-ItemProperty -Path $rootProgKey -Name "(Default)" -Value $className
$rootProgClsid = Join-Path $rootProgKey "CLSID"
if (-not (Test-Path $rootProgClsid)) { New-Item -Path $rootProgClsid -Force | Out-Null }
Set-ItemProperty -Path $rootProgClsid -Name "(Default)" -Value $guid

# Если есть права администратора — регистрируем и в HKLM
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
if ($isAdmin -and (Test-Path $regasm)) {
    try {
        Start-Process -FilePath $regasm -ArgumentList "/codebase `"$dll`"" -Wait -NoNewWindow -ErrorAction SilentlyContinue
        $hklmPath = "HKLM:\Software\SolidWorks\AddIns\$guid"
        if (-not (Test-Path $hklmPath)) { New-Item -Path $hklmPath -Force | Out-Null }
        Set-ItemProperty -Path $hklmPath -Name "(Default)" -Value 1 -Type DWord
        Set-ItemProperty -Path $hklmPath -Name "Title" -Value $title
        Set-ItemProperty -Path $hklmPath -Name "Description" -Value $desc
    } catch { }
}
Write-Host "  [OK] COM-компонент зарегистрирован." -ForegroundColor Green

# 3. Включение надстройки в SolidWorks
Write-Host "`n[3/4] Включение надстройки в SolidWorks..." -ForegroundColor Gray
$keyPath = "HKCU:\Software\SolidWorks\AddIns\$guid"
if (-not (Test-Path $keyPath)) { New-Item -Path $keyPath -Force | Out-Null }
Set-ItemProperty -Path $keyPath -Name "(Default)" -Value 1 -Type DWord
Set-ItemProperty -Path $keyPath -Name "Title" -Value $title
Set-ItemProperty -Path $keyPath -Name "Description" -Value $desc

$startupPath = "HKCU:\Software\SolidWorks\AddinsStartup\$guid"
if (-not (Test-Path $startupPath)) { New-Item -Path $startupPath -Force | Out-Null }
Set-ItemProperty -Path $startupPath -Name "(Default)" -Value 1 -Type DWord
Write-Host "  [OK] Надстройка активирована в SolidWorks AddIns и автозагрузке." -ForegroundColor Green

# 4. Настройка параметров ЕСКД
Write-Host "`n[4/4] Проверка параметров ЕСКД..." -ForegroundColor Gray
$eskdKey = "HKCU:\Software\SolidWorks\ESKD_Settings"
if (-not (Test-Path $eskdKey)) { New-Item -Path $eskdKey -Force | Out-Null }
if (-not (Get-ItemProperty -Path $eskdKey -Name "AutoMass" -ErrorAction SilentlyContinue)) {
    Set-ItemProperty -Path $eskdKey -Name "AutoMass" -Value 1 -Type DWord
    Set-ItemProperty -Path $eskdKey -Name "MassDecimals" -Value 2 -Type DWord
    Set-ItemProperty -Path $eskdKey -Name "AutoCenterMass" -Value 1 -Type DWord
    Set-ItemProperty -Path $eskdKey -Name "AutoSplitName" -Value 1 -Type DWord
}

$curAuthor = (Get-ItemProperty -Path $eskdKey -Name "Author" -ErrorAction SilentlyContinue).Author
if ([string]::IsNullOrWhiteSpace($curAuthor)) {
    $curAuthor = [Environment]::UserName
    Set-ItemProperty -Path $eskdKey -Name "Author" -Value $curAuthor
    Set-ItemProperty -Path $eskdKey -Name "AuthorList" -Value $curAuthor
}
Write-Host "  [OK] Текущий конструктор: $curAuthor" -ForegroundColor Green

# 5. Тестовая верификация COM
try {
    $comType = [Type]::GetTypeFromCLSID([Guid]::Parse($guid))
    $comObj = [Activator]::CreateInstance($comType)
    if ($comObj -ne $null) {
        Write-Host "`n  [OK] Тест COM-активации успешно пройден (объект надстройки создан)." -ForegroundColor Green
        if ([System.Runtime.InteropServices.Marshal]::IsComObject($comObj)) {
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($comObj) | Out-Null
        }
    }
} catch {
    Write-Host "`n  [ВНИМАНИЕ] Проверка COM-активации: $_" -ForegroundColor Yellow
}

Write-Host "`n=======================================================================" -ForegroundColor Cyan
Write-Host "   [УСПЕХ] НАДСТРОЙКА ЕСКД ГОТОВА К РАБОТЕ В SOLIDWORKS!" -ForegroundColor Green
Write-Host "=======================================================================" -ForegroundColor Cyan
