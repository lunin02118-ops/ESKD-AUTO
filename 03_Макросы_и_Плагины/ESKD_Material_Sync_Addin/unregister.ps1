<#
.SYNOPSIS
    Unregisters the ESKD Material Sync Zero-Click Add-In from SolidWorks 2025.
#>
[CmdletBinding()]
param()

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
$outputDll = Join-Path $ScriptDir "ESKD_Material_Sync_Addin.dll"

if (Test-Path $outputDll) {
    & $regasm /u $outputDll
}

$guid = "{B64E6875-B101-4D5C-B245-FF8D50772E21}"
$keyPath = "HKCU:\Software\SolidWorks\AddIns\$guid"
if (Test-Path $keyPath) {
    Remove-Item -Path $keyPath -Recurse -Force
}

Write-Host "Add-In successfully unregistered." -ForegroundColor Yellow
