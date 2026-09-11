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

# 2.1 Compile EXEs (ESKD.exe and ESKD_Sync.exe)
$outputExe = Join-Path $ScriptDir "ESKD.exe"
$exeSources = @(
    (Join-Path $ScriptDir "Program.cs"),
    (Join-Path $ScriptDir "SettingsForm.cs"),
    (Join-Path $ScriptDir "MaterialSyncEngine.cs")
)
$exeArgs = @(
    "/target:winexe",
    "/platform:anycpu",
    "/optimize+",
    "/codepage:65001",
    "/out:$outputExe",
    "/r:System.dll",
    "/r:System.Drawing.dll",
    "/r:System.Windows.Forms.dll",
    "/r:System.Xml.dll",
    "/r:$swDir\SolidWorks.Interop.sldworks.dll",
    "/r:$swDir\SolidWorks.Interop.swconst.dll"
) + $exeSources

& $csc $exeArgs
if ($LASTEXITCODE -eq 0) {
    Write-Host "Compilation successful: $outputExe" -ForegroundColor Green
    $syncExe = Join-Path $ScriptDir "ESKD_Sync.exe"
    Copy-Item $outputExe $syncExe -Force
    Write-Host "Copied to: $syncExe" -ForegroundColor Green
}

# 3. Register COM: direct HKCU registration (works without Admin) + RegAsm/HKLM if elevated
$guid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
$progId = "ESKD.MaterialSync.SwAddin_v5"
$className = "ESKD.MaterialSync.SwAddin"
$assemblyName = "ESKD_Material_Sync_v5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
$runtimeVersion = "v4.0.30319"
$title = "ЕСКД: Синхронизация материалов и реквизитов"
$codeBase = "file:///" + ((Resolve-Path $outputDll).Path -replace '\\', '/')

# 3.1. HKCU COM Registration
$clsidKey = "HKCU:\Software\Classes\CLSID\$guid"
if (-not (Test-Path $clsidKey)) { New-Item -Path $clsidKey -Force | Out-Null }
Set-ItemProperty -Path $clsidKey -Name "(Default)" -Value $className

$inprocKey = Join-Path $clsidKey "InprocServer32"
if (-not (Test-Path $inprocKey)) { New-Item -Path $inprocKey -Force | Out-Null }
Set-ItemProperty -Path $inprocKey -Name "(Default)" -Value "mscoree.dll"
Set-ItemProperty -Path $inprocKey -Name "ThreadingModel" -Value "Both"
Set-ItemProperty -Path $inprocKey -Name "Class" -Value $className
Set-ItemProperty -Path $inprocKey -Name "Assembly" -Value $assemblyName
Set-ItemProperty -Path $inprocKey -Name "RuntimeVersion" -Value $runtimeVersion
Set-ItemProperty -Path $inprocKey -Name "CodeBase" -Value $codeBase

$verKey = Join-Path $inprocKey "1.0.0.0"
if (-not (Test-Path $verKey)) { New-Item -Path $verKey -Force | Out-Null }
Set-ItemProperty -Path $verKey -Name "Class" -Value $className
Set-ItemProperty -Path $verKey -Name "Assembly" -Value $assemblyName
Set-ItemProperty -Path $verKey -Name "RuntimeVersion" -Value $runtimeVersion
Set-ItemProperty -Path $verKey -Name "CodeBase" -Value $codeBase

$progKey = Join-Path $clsidKey "ProgId"
if (-not (Test-Path $progKey)) { New-Item -Path $progKey -Force | Out-Null }
Set-ItemProperty -Path $progKey -Name "(Default)" -Value $progId

$catKey = Join-Path $clsidKey "Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}"
if (-not (Test-Path $catKey)) { New-Item -Path $catKey -Force | Out-Null }

$rootProgKey = "HKCU:\Software\Classes\$progId"
if (-not (Test-Path $rootProgKey)) { New-Item -Path $rootProgKey -Force | Out-Null }
Set-ItemProperty -Path $rootProgKey -Name "(Default)" -Value $className
$rootProgClsid = Join-Path $rootProgKey "CLSID"
if (-not (Test-Path $rootProgClsid)) { New-Item -Path $rootProgClsid -Force | Out-Null }
Set-ItemProperty -Path $rootProgClsid -Name "(Default)" -Value $guid

# 3.2. RegAsm / HKLM if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    try {
        & $regasm /codebase $outputDll 2>$null
        $hklmPath = "HKLM:\Software\SolidWorks\AddIns\$guid"
        if (-not (Test-Path $hklmPath)) { New-Item -Path $hklmPath -Force | Out-Null }
        Set-ItemProperty -Path $hklmPath -Name "(Default)" -Value 1 -Type DWord
        Set-ItemProperty -Path $hklmPath -Name "Title" -Value $title -Type String
        Set-ItemProperty -Path $hklmPath -Name "Description" -Value $desc -Type String
    } catch { }
}

# 4. Set SolidWorks Add-in registration in HKCU
$keyPath = "HKCU:\Software\SolidWorks\AddIns\$guid"
if (-not (Test-Path $keyPath)) { New-Item -Path $keyPath -Force | Out-Null }
Set-ItemProperty -Path $keyPath -Name "(Default)" -Value 1 -Type DWord
Set-ItemProperty -Path $keyPath -Name "Title" -Value $title -Type String
Set-ItemProperty -Path $keyPath -Name "Description" -Value $desc -Type String

$startupPath = "HKCU:\Software\SolidWorks\AddInsStartup\$guid"
if (-not (Test-Path $startupPath)) { New-Item -Path $startupPath -Force | Out-Null }
Set-ItemProperty -Path $startupPath -Name "(Default)" -Value 1 -Type DWord

# 5. Ensure CommandManager tabs are visible in SolidWorks
$contexts = @("PartContext", "AssyContext", "DrwContext")
foreach ($ctx in $contexts) {
    $ctxPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\$ctx"
    if (-not (Test-Path $ctxPath)) { New-Item -Path $ctxPath -Force | Out-Null }

    $found = $false
    Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | ForEach-Object {
        $tabPath = $_.PSPath
        $modName = (Get-ItemProperty -Path $tabPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName
        $refName = (Get-ItemProperty -Path $tabPath -Name "RefName" -ErrorAction SilentlyContinue).RefName
        if (($refName -and ($refName -match "ЕСКД")) -or ($modName -and ($modName.ToUpper() -eq $guid.ToUpper()))) {
            Set-ItemProperty -Path $tabPath -Name "RefName" -Value "ЕСКД" -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $tabPath -Name "ModuleName" -Value $guid -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $tabPath -Name "Tab Props" -Value "ЕСКД,1,1,-1" -Force -ErrorAction SilentlyContinue
            $found = $true
        }
    }
    if (-not $found) {
        $existingTabs = Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.PSChildName -match "^Tab(\d+)$") { [int]$matches[1] }
        }
        $nextNum = 0
        if ($existingTabs) { $nextNum = ($existingTabs | Measure-Object -Maximum).Maximum + 1 }
        $newTabPath = Join-Path $ctxPath "Tab$nextNum"
        New-Item -Path $newTabPath -Force | Out-Null
        Set-ItemProperty -Path $newTabPath -Name "RefName" -Value "ЕСКД" -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $newTabPath -Name "ModuleName" -Value $guid -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $newTabPath -Name "Tab Props" -Value "ЕСКД,1,1,-1" -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Add-In successfully registered in SolidWorks Add-Ins, Startup, and CommandManager tabs (HKCU)!" -ForegroundColor Green

