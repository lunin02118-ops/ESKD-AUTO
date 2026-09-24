<#
.SYNOPSIS
    Регистрация надстройки из 32-битного PowerShell (T0, сверка SW API 23.09.2026, №4).
.DESCRIPTION
    Запускается 32-битным PowerShell (SysWOW64). Регистрирует и снимает надстройку во временном разделе
    HKCU:\Software\ESKD_RegistrationTest_<id>: этот раздел не перенаправляется, поэтому запись видна. Модуль должен
    отказать обоим вызовам и ничего не записать — из 32-битного процесса регистрация ушла бы в Wow6432Node, и 64-битный
    SolidWorks её не увидел бы. Настоящая регистрация не затрагивается. Вывод — JSON.
#>
param([Parameter(Mandatory = $true)][string]$ModulePath)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. $ModulePath
$sandbox = "HKCU:\Software\ESKD_RegistrationTest_" + [guid]::NewGuid().ToString("N")
$dll = Join-Path ([IO.Path]::GetTempPath()) ("eskd_bitness_" + [guid]::NewGuid().ToString("N") + ".dll")
$reg = ""
$unreg = ""
$written = $false
try {
    Set-Content -LiteralPath $dll -Value "not a dll"
    try {
        Register-EskdAddin -DllPath $dll -SystemWide -UserRoot "$sandbox\User" -MachineRoot "$sandbox\Machine" | Out-Null
    } catch {
        $reg = $_.Exception.Message
    }
    $written = Test-Path -LiteralPath $sandbox
    try {
        Unregister-EskdAddin -SystemWide -UserRoot "$sandbox\User" -MachineRoot "$sandbox\Machine"
    } catch {
        $unreg = $_.Exception.Message
    }
} finally {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    Remove-Item -LiteralPath $dll -Force -ErrorAction SilentlyContinue
}
[pscustomobject]@{ is64 = [Environment]::Is64BitProcess; reg = $reg; unreg = $unreg; written = $written } | ConvertTo-Json -Compress
