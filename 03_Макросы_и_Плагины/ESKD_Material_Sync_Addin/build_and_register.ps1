<#
.SYNOPSIS
    Сборка надстройки ЕСКД из исходников и её регистрация в SolidWorks.

.DESCRIPTION
    Последовательно запускает build.ps1 (DLL, ESKD.exe, ESKD_Sync.exe и build_manifest.json) и register_eskd.ps1
    (регистрация через общий модуль Register-EskdAddin.ps1).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "build.ps1")
& (Join-Path $PSScriptRoot "register_eskd.ps1")
