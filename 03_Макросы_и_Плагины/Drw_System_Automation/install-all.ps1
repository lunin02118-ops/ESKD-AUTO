# install-all.ps1 - адаптивная установка Drew Gov-издания (Admin / User dual-mode).
# 1) Определение прав (Admin -> C:\Program Files, User -> %LOCALAPPDATA%).
# 2) MSI тихо (в Admin-режиме) или прямое развёртывание комплекта.
# 3) Настройка .NET TLS и регистрация COM/SolidWorks в HKCU (+ HKLM для Admin).
# 4) Окно активации (если не -NoActivate и не -Silent).
param(
  [Alias('NoAct')][switch]$NoActivate,
  [Alias('PerUser', 'NoAdmin')][switch]$User,
  [Alias('AllUsers')][switch]$Machine,
  [Alias('Quiet', 'q', 's')][switch]$Silent
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$msi  = Join-Path $root 'Drew_4.3.0.0.msi'
$bin  = Join-Path $root '2_комплект_издания\bin'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($User -and $Machine) {
  Write-Host 'FATAL: Cannot specify both -User and -Machine.' -ForegroundColor Red
  exit 1
}

if ($User) {
  $isMachine = $false
} elseif ($Machine) {
  if (-not $isAdmin) {
    Write-Host 'FATAL: -Machine requested but current session is NOT Administrator.' -ForegroundColor Red
    exit 1
  }
  $isMachine = $true
} else {
  $isMachine = $isAdmin
}

if ($isMachine) {
  $dst = Join-Path $env:ProgramFiles 'CAD Booster\Drew'
  Write-Host '=== Drew Gov Setup: ДЛЯ ВСЕХ ПОЛЬЗОВАТЕЛЕЙ (Администратор) ===' -ForegroundColor Cyan
} else {
  $dst = Join-Path $env:LOCALAPPDATA 'CAD Booster\Drew'
  Write-Host '=== Drew Gov Setup: ДЛЯ ТЕКУЩЕГО ПОЛЬЗОВАТЕЛЯ (Без прав администратора) ===' -ForegroundColor Yellow
}
Write-Host "Целевой каталог: $dst" -ForegroundColor Gray

if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }

if ($isMachine) {
  Write-Host '=== 1/4 Установка Drew 4.3.0.0 (MSI, тихо) ===' -ForegroundColor Cyan
  if (Test-Path $msi) {
    $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$msi`"", '/qn', '/norestart' -Wait -PassThru
    if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) {
      Write-Host ('MSI exit ' + $p.ExitCode + ' (продолжаем прямое развёртывание комплекта)') -ForegroundColor Yellow
    } else {
      Write-Host 'MSI OK' -ForegroundColor Green
    }
  }
} else {
  Write-Host '=== 1/4 Пропуск MSI (режим пользователя, прямое развёртывание) ===' -ForegroundColor Cyan
}

Write-Host '=== 2/4 Применение комплекта Gov-издания ===' -ForegroundColor Cyan

# .NET TLS
if ($isMachine) {
  try {
    foreach ($kp in 'HKLM:\SOFTWARE\Microsoft\.NETFramework\v4.0.30319', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\.NETFramework\v4.0.30319') {
      if (-not (Test-Path $kp)) { New-Item $kp -Force | Out-Null }
      Set-ItemProperty $kp -Name 'SchUseStrongCrypto' -Value 1 -Type DWord -ErrorAction SilentlyContinue
      Set-ItemProperty $kp -Name 'SystemDefaultTlsVersions' -Value 1 -Type DWord -ErrorAction SilentlyContinue
    }
  } catch { }
}
try {
  foreach ($kp in 'HKCU:\SOFTWARE\Microsoft\.NETFramework\v4.0.30319', 'HKCU:\SOFTWARE\WOW6432Node\Microsoft\.NETFramework\v4.0.30319') {
    if (-not (Test-Path $kp)) { New-Item $kp -Force | Out-Null }
    Set-ItemProperty $kp -Name 'SchUseStrongCrypto' -Value 1 -Type DWord -ErrorAction SilentlyContinue
    Set-ItemProperty $kp -Name 'SystemDefaultTlsVersions' -Value 1 -Type DWord -ErrorAction SilentlyContinue
  }
} catch { }

Get-Process SLDWORKS -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 2

robocopy $bin $dst /E /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Host 'FATAL: robocopy не смог скопировать комплект.' -ForegroundColor Red; exit 1 }

function Get-Sha256([string]$filePath) {
  if (-not (Test-Path $filePath)) { return $null }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $fs = [System.IO.File]::OpenRead($filePath)
  try {
    return ([BitConverter]::ToString($sha.ComputeHash($fs))).Replace('-', '')
  } finally {
    $fs.Dispose()
    $sha.Dispose()
  }
}

$checkFiles = @(
  'DrewAirGap.Activation.dll',
  'CADBooster.Common.Licensing.dll',
  'Cryptolens.Licensing.dll',
  'CADBooster.Common.Core.dll',
  'CADBooster.Drew.Drawing.dll',
  'ru\CADBooster.Drew.Drawing.resources.dll'
)
$bad = @()
foreach ($f in $checkFiles) {
  $sf = Join-Path $bin $f
  $df = Join-Path $dst $f
  if (Test-Path $sf) {
    $sh = Get-Sha256 $sf
    $dh = Get-Sha256 $df
    if (-not $dh -or ($dh -ne $sh)) { $bad += $f }
  }
}
if ($bad.Count -gt 0) {
  Write-Host ('FATAL: не удалось скопировать (заблокированы?): ' + ($bad -join ', ')) -ForegroundColor Red
  exit 1
}
Write-Host 'Комплект применён (контрольные сборки проверены)' -ForegroundColor Green

Write-Host '=== 3/4 Регистрация COM и SOLIDWORKS Add-in ===' -ForegroundColor Cyan
$clsid      = '{08c4bc0b-c36c-470e-a0ea-02232f023333}'
$dllPath    = Join-Path $dst 'CADBooster.Drew.Drawing.dll'
$fullClass  = 'CADBooster.Drew.Drawing.SolidWorks.Integration.DrewAddin'
$assemblyNm = 'CADBooster.Drew.Drawing, Version=4.3.0.0, Culture=neutral, PublicKeyToken=null'
$title      = 'Drew'
$desc       = 'Drew Drawing Automation'
$iconPath   = Join-Path $dst 'Assets\DrewIcon-AddinList.png'

# HKCU COM
$inproc = "HKCU:\Software\Classes\CLSID\$clsid\InprocServer32"
New-Item -Path $inproc -Force | Out-Null
Set-ItemProperty -Path $inproc -Name '(default)'      -Value 'mscoree.dll' -Type String -Force
Set-ItemProperty -Path $inproc -Name 'Assembly'       -Value $assemblyNm  -Type String -Force
Set-ItemProperty -Path $inproc -Name 'Class'          -Value $fullClass   -Type String -Force
Set-ItemProperty -Path $inproc -Name 'RuntimeVersion' -Value 'v4.0.30319' -Type String -Force
Set-ItemProperty -Path $inproc -Name 'CodeBase'       -Value $dllPath     -Type String -Force
Set-ItemProperty -Path $inproc -Name 'ThreadingModel' -Value 'Both'       -Type String -Force

$vsub = "$inproc\4.3.0.0"
New-Item -Path $vsub -Force | Out-Null
Set-ItemProperty -Path $vsub -Name 'Assembly'       -Value $assemblyNm -Type String -Force
Set-ItemProperty -Path $vsub -Name 'Class'          -Value $fullClass  -Type String -Force
Set-ItemProperty -Path $vsub -Name 'RuntimeVersion' -Value 'v4.0.30319' -Type String -Force
Set-ItemProperty -Path $vsub -Name 'CodeBase'       -Value $dllPath    -Type String -Force

$cat = "HKCU:\Software\Classes\CLSID\$clsid\Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}"
New-Item -Path $cat -Force | Out-Null

$progIdStr = $fullClass -replace '\.[^.]+$',''
$progId = "HKCU:\Software\Classes\CLSID\$clsid\ProgId"
New-Item -Path $progId -Force | Out-Null
Set-ItemProperty -Path $progId -Name '(default)' -Value $progIdStr -Type String -Force

$progIdRoot = "HKCU:\Software\Classes\$progIdStr\CLSID"
New-Item -Path $progIdRoot -Force | Out-Null
Set-ItemProperty -Path $progIdRoot -Name '(default)' -Value $clsid -Type String -Force

# HKCU SolidWorks AddIns
$addin = "HKCU:\Software\SolidWorks\AddIns\$clsid"
New-Item -Path $addin -Force | Out-Null
Set-ItemProperty -Path $addin -Name '(default)'   -Value 1 -Type DWord -Force
Set-ItemProperty -Path $addin -Name 'Title'       -Value $title -Type String -Force
Set-ItemProperty -Path $addin -Name 'Description' -Value $desc  -Type String -Force
if (Test-Path $iconPath) {
  Set-ItemProperty -Path $addin -Name 'Icon path' -Value $iconPath -Type String -Force
}
Set-ItemProperty -Path $addin -Name 'Startup'     -Value 1 -Type DWord -Force

$addinStartup = "HKCU:\Software\SolidWorks\AddInsStartup\$clsid"
New-Item -Path $addinStartup -Force | Out-Null
Set-ItemProperty -Path $addinStartup -Name '(default)' -Value 1 -Type DWord -Force

if ($isMachine) {
  try {
    $hklmInproc = "HKLM:\Software\Classes\CLSID\$clsid\InprocServer32"
    if (-not (Test-Path $hklmInproc)) { New-Item -Path $hklmInproc -Force | Out-Null }
    Set-ItemProperty -Path $hklmInproc -Name '(default)'      -Value 'mscoree.dll' -Type String -Force
    Set-ItemProperty -Path $hklmInproc -Name 'Assembly'       -Value $assemblyNm  -Type String -Force
    Set-ItemProperty -Path $hklmInproc -Name 'Class'          -Value $fullClass   -Type String -Force
    Set-ItemProperty -Path $hklmInproc -Name 'RuntimeVersion' -Value 'v4.0.30319' -Type String -Force
    Set-ItemProperty -Path $hklmInproc -Name 'CodeBase'       -Value $dllPath     -Type String -Force
    Set-ItemProperty -Path $hklmInproc -Name 'ThreadingModel' -Value 'Both'       -Type String -Force

    $hklmSub = "$hklmInproc\4.3.0.0"
    if (-not (Test-Path $hklmSub)) { New-Item -Path $hklmSub -Force | Out-Null }
    Set-ItemProperty -Path $hklmSub -Name 'Assembly'       -Value $assemblyNm -Type String -Force
    Set-ItemProperty -Path $hklmSub -Name 'Class'          -Value $fullClass  -Type String -Force
    Set-ItemProperty -Path $hklmSub -Name 'RuntimeVersion' -Value 'v4.0.30319' -Type String -Force
    Set-ItemProperty -Path $hklmSub -Name 'CodeBase'       -Value $dllPath    -Type String -Force

    $hklmCat = "HKLM:\Software\Classes\CLSID\$clsid\Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}"
    if (-not (Test-Path $hklmCat)) { New-Item -Path $hklmCat -Force | Out-Null }

    $hklmProgId = "HKLM:\Software\Classes\CLSID\$clsid\ProgId"
    if (-not (Test-Path $hklmProgId)) { New-Item -Path $hklmProgId -Force | Out-Null }
    Set-ItemProperty -Path $hklmProgId -Name '(default)' -Value $progIdStr -Type String -Force

    $hklmProgIdRoot = "HKLM:\Software\Classes\$progIdStr\CLSID"
    if (-not (Test-Path $hklmProgIdRoot)) { New-Item -Path $hklmProgIdRoot -Force | Out-Null }
    Set-ItemProperty -Path $hklmProgIdRoot -Name '(default)' -Value $clsid -Type String -Force

    $hklmAddin = "HKLM:\Software\SolidWorks\AddIns\$clsid"
    if (-not (Test-Path $hklmAddin)) { New-Item -Path $hklmAddin -Force | Out-Null }
    Set-ItemProperty -Path $hklmAddin -Name '(default)'   -Value 1 -Type DWord -Force
    Set-ItemProperty -Path $hklmAddin -Name 'Title'       -Value $title -Type String -Force
    Set-ItemProperty -Path $hklmAddin -Name 'Description' -Value $desc  -Type String -Force
    if (Test-Path $iconPath) {
      Set-ItemProperty -Path $hklmAddin -Name 'Icon path' -Value $iconPath -Type String -Force
    }
    Set-ItemProperty -Path $hklmAddin -Name 'Startup'     -Value 1 -Type DWord -Force

    $hklmAddinStartup = "HKLM:\Software\SolidWorks\AddInsStartup\$clsid"
    if (-not (Test-Path $hklmAddinStartup)) { New-Item -Path $hklmAddinStartup -Force | Out-Null }
    Set-ItemProperty -Path $hklmAddinStartup -Name '(default)' -Value 1 -Type DWord -Force
  } catch { }
}
Write-Host 'Регистрация выполнена успешно' -ForegroundColor Green

Write-Host '=== 4/4 Готово ===' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Drew установлен как закрытое (offline) издание.' -ForegroundColor Green
if ($NoActivate -or $Silent) { Write-Host 'Активация пропущена (-NoActivate или -Silent).'; exit 0 }
Write-Host 'Сейчас откроется ОКНО АКТИВАЦИИ:' -ForegroundColor Yellow
Write-Host '  1) «Скопировать» рядом с ключом железа -> переслать офицеру;'
Write-Host '  2) получить код DREW-XXXXX-... и вставить его -> «АКТИВИРОВАТЬ».'
& powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $root '3_активация\Client-Activate-Drew.ps1')
exit 0