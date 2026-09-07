<#
.SYNOPSIS
    Unregisters the ESKD Material Sync Add-In from SolidWorks and Windows COM.
#>
[CmdletBinding()]
param()

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
$outputDll = Join-Path $ScriptDir "ESKD_Material_Sync_v5.dll"
$guid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
$progId = "ESKD.MaterialSync.SwAddin_v5"

# 1. RegAsm unregister if elevated
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin -and (Test-Path $outputDll) -and (Test-Path $regasm)) {
    try { & $regasm /u $outputDll 2>$null } catch { }
}

# 2. Clean HKCU Classes
Remove-Item -Path "HKCU:\Software\Classes\CLSID\$guid" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Classes\$progId" -Recurse -Force -ErrorAction SilentlyContinue

# 3. Clean HKLM if elevated
if ($isAdmin) {
    Remove-Item -Path "HKLM:\Software\Classes\CLSID\$guid" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "HKLM:\Software\Classes\$progId" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "HKLM:\Software\SolidWorks\AddIns\$guid" -Recurse -Force -ErrorAction SilentlyContinue
}

# 4. Clean SolidWorks AddIns in HKCU
Remove-Item -Path "HKCU:\Software\SolidWorks\AddIns\$guid" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\SolidWorks\AddinsStartup\$guid" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "ESKD Add-In successfully unregistered." -ForegroundColor Yellow
