<#
.SYNOPSIS
    Сборка надстройки ЕСКД из исходников системным компилятором csc (.NET Framework 4, C# 5).

.DESCRIPTION
    Собирает ESKD_Material_Sync_v5.dll, ESKD.exe и ESKD_Sync.exe. Реестр не трогает —
    регистрацию выполняет модуль Register-EskdAddin.ps1 (register_eskd.ps1, build_and_register.ps1, установщик). Рядом со сборкой пишется
    build_manifest.json: SHA-256 исходников и результатов, чтобы тесты и установщик могли
    проверить, что DLL собрана из текущих исходников.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipExe
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "Не найден компилятор C#: $csc" }

if (Get-Process -Name SLDWORKS -ErrorAction SilentlyContinue) {
    $dll = Join-Path $ScriptDir "ESKD_Material_Sync_v5.dll"
    # Нет файла — держать нечего: так выглядит первая сборка в свежей копии репозитория (обновление из GitHub).
    # Раньше отсутствие файла тоже попадало в catch, и сборка отказывала с чужой причиной (20.09.2026).
    if (Test-Path -LiteralPath $dll) {
        try {
            $fs = [System.IO.File]::Open($dll, 'Open', 'ReadWrite', 'None'); $fs.Close()
        } catch {
            throw "SolidWorks держит ESKD_Material_Sync_v5.dll открытой. Закройте SolidWorks и повторите сборку."
        }
    }
}

# Сборки интеропа: из установленного SolidWorks, иначе — уже лежащие рядом с надстройкой.
$interopNames = @("SolidWorks.Interop.sldworks.dll", "SolidWorks.Interop.swconst.dll", "SolidWorks.Interop.swpublished.dll")
$swDirs = @(
    "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist",
    "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS"
)
foreach ($name in $interopNames) {
    $src = $swDirs | ForEach-Object { Join-Path $_ $name } | Where-Object { Test-Path $_ } | Select-Object -First 1
    $dst = Join-Path $ScriptDir $name
    if ($src) { Copy-Item -Path $src -Destination $dst -Force }
    if (-not (Test-Path $dst)) { throw "Нет сборки интеропа $name (установлен ли SolidWorks?)" }
}
$refs = @("/r:System.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:System.Xml.dll",
          "/r:System.Core.dll", "/r:System.Xml.Linq.dll", "/r:System.IO.Compression.dll") +
        ($interopNames | ForEach-Object { "/r:" + (Join-Path $ScriptDir $_) })

$core = @(Get-ChildItem (Join-Path $ScriptDir "Core") -Filter *.cs | Sort-Object Name | ForEach-Object FullName)
$sw = @(Get-ChildItem (Join-Path $ScriptDir "Sw") -Filter *.cs | Sort-Object Name | ForEach-Object FullName)
$dllSources = @((Join-Path $ScriptDir "AssemblyInfo.cs"), (Join-Path $ScriptDir "SwAddin.cs"), (Join-Path $ScriptDir "SettingsForm.cs")) + $core + $sw
$exeSources = @((Join-Path $ScriptDir "Program.cs"), (Join-Path $ScriptDir "SettingsForm.cs")) + $core + $sw

function Invoke-Csc([string]$target, [string]$out, [string[]]$sources) {
    $cscArgs = @("/nologo", "/target:$target", "/platform:anycpu", "/optimize+", "/codepage:65001", "/warn:4", "/out:$out") + $refs + $sources
    $output = & $csc $cscArgs 2>&1
    $code = $LASTEXITCODE
    $output | Where-Object { $_ -match "error" } | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    if ($code -ne 0) { throw "csc завершился с кодом $code при сборке $out" }
}

$outDll = Join-Path $ScriptDir "ESKD_Material_Sync_v5.dll"
Invoke-Csc "library" $outDll $dllSources
Write-Host "Собрано: $outDll" -ForegroundColor Green

if (-not $SkipExe) {
    $outExe = Join-Path $ScriptDir "ESKD.exe"
    Invoke-Csc "winexe" $outExe $exeSources
    Copy-Item $outExe (Join-Path $ScriptDir "ESKD_Sync.exe") -Force
    Write-Host "Собрано: ESKD.exe, ESKD_Sync.exe" -ForegroundColor Green
}

# Манифест сборки
# SHA-256 средствами .NET: Windows PowerShell 5.1, запущенный из PowerShell 7, теряет Get-FileHash (аудит 23.09.2026)
function Get-Sha([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}
$allSources = @($dllSources + $exeSources) | Sort-Object -Unique
$manifest = [ordered]@{
    built   = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    csc     = $csc
    sources = [ordered]@{}
    outputs = [ordered]@{}
}
foreach ($s in $allSources) {
    $rel = $s.Substring($ScriptDir.Length + 1)
    $manifest.sources[$rel] = Get-Sha $s
}
foreach ($o in @("ESKD_Material_Sync_v5.dll", "ESKD.exe", "ESKD_Sync.exe")) {
    $p = Join-Path $ScriptDir $o
    if (Test-Path $p) { $manifest.outputs[$o] = Get-Sha $p }
}
$json = $manifest | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path $ScriptDir "build_manifest.json"), $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Манифест: build_manifest.json" -ForegroundColor Gray
