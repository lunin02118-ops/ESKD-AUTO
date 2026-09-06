<#
.SYNOPSIS
    Builds and registers the ESKD Material Sync Zero-Click Add-In for SolidWorks 2025.
#>
[CmdletBinding()]
param()

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
$swDir = "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS"

Write-Host "=== Building ESKD Material Sync Add-In ===" -ForegroundColor Cyan

# 1. Copy required interop assemblies next to Add-In
foreach ($dll in @("SolidWorks.Interop.sldworks.dll", "SolidWorks.Interop.swconst.dll", "SolidWorks.Interop.swpublished.dll")) {
    $src = Join-Path $swDir $dll
    $dst = Join-Path $ScriptDir $dll
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $dst -Force
    }
}

# 2. Compile DLL with UTF-8 codepage
$outputDll = Join-Path $ScriptDir "ESKD_Material_Sync_v5.dll"
$sources = @(
    (Join-Path $ScriptDir "AssemblyInfo.cs"),
    (Join-Path $ScriptDir "MaterialSyncEngine.cs"),
    (Join-Path $ScriptDir "SettingsForm.cs"),
    (Join-Path $ScriptDir "SwAddin.cs")
)

$args = @(
    "/target:library",
    "/platform:anycpu",
    "/optimize+",
    "/codepage:65001",
    "/out:$outputDll",
    "/r:System.dll",
    "/r:System.Drawing.dll",
    "/r:System.Windows.Forms.dll",
    "/r:System.Xml.dll",
    "/r:$swDir\SolidWorks.Interop.sldworks.dll",
    "/r:$swDir\SolidWorks.Interop.swconst.dll",
    "/r:$swDir\SolidWorks.Interop.swpublished.dll"
) + $sources

& $csc $args
if ($LASTEXITCODE -ne 0) {
    Write-Error "CSC compilation failed with code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "Compilation successful: $outputDll" -ForegroundColor Green

# 3. Register with RegAsm
& $regasm /codebase $outputDll
if ($LASTEXITCODE -ne 0) {
    Write-Error "RegAsm registration failed with code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# 4. Set Registry Keys in HKLM and HKCU
$guid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
$title = "ЕСКД: Синхронизация материалов и реквизитов"
$desc = "Панель инструментов ЕСКД: настройки реквизитов (фамилии, контора, масса), автоматическая синхронизация материалов и центрирование штампа по ГОСТ 2.104"

# HKLM\Software\SolidWorks\AddIns (Displays in Tools -> Add-ins dialog!)
try {
    $hklmPath = "HKLM:\Software\SolidWorks\AddIns\$guid"
    if (-not (Test-Path $hklmPath)) {
        New-Item -Path $hklmPath -Force | Out-Null
    }
    Set-ItemProperty -Path $hklmPath -Name "(Default)" -Value 1 -Type DWord
    Set-ItemProperty -Path $hklmPath -Name "Title" -Value $title -Type String
    Set-ItemProperty -Path $hklmPath -Name "Description" -Value $desc -Type String
} catch {
    Write-Warning "Could not write to HKLM (may require admin privileges): $_"
}

# HKCU\Software\SolidWorks\AddIns
$keyPath = "HKCU:\Software\SolidWorks\AddIns\$guid"
if (-not (Test-Path $keyPath)) {
    New-Item -Path $keyPath -Force | Out-Null
}
Set-ItemProperty -Path $keyPath -Name "(Default)" -Value 1 -Type DWord
Set-ItemProperty -Path $keyPath -Name "Title" -Value $title -Type String
Set-ItemProperty -Path $keyPath -Name "Description" -Value $desc -Type String

# HKCU\Software\SolidWorks\AddInsStartup (Ensures cold-start autoload!)
$startupPath = "HKCU:\Software\SolidWorks\AddInsStartup\$guid"
if (-not (Test-Path $startupPath)) {
    New-Item -Path $startupPath -Force | Out-Null
}
Set-ItemProperty -Path $startupPath -Name "(Default)" -Value 1 -Type DWord

Write-Host "Add-In successfully registered in SolidWorks Add-Ins and AddInsStartup!" -ForegroundColor Green

