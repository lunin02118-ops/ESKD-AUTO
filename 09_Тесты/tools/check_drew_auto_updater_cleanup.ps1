<#
.SYNOPSIS
    Проверка установщика Drew AUTO (T0): удаление программы обновлений не задевает сам Drew.
.DESCRIPTION
    install-flat.ps1 берётся из ресурса SfxMain.payload.zip внутри УСТАНОВЩИК_Drew_AUTO.exe (сборка загружается
    только для чтения, код не выполняется). Блок «Delete updater files» выполняется во временном каталоге в обоих режимах
    установщика: для всех пользователей (Program Files) и для одного пользователя (%LOCALAPPDATA%\CAD Booster\Drew —
    это и есть папка установки). Пути ProgramData и LOCALAPPDATA подменяются временными, поэтому ПК не затрагивается.
    Ожидается: updater.exe и updater.ini удалены, сборки Drew на месте в обоих режимах; остатки в LOCALAPPDATA
    удаляются только при установке для всех пользователей. В скрипте нет личных путей C:\Users\<имя>.
    Вывод — JSON; код выхода 0 при успехе.
#>
param([Parameter(Mandatory = $true)][string]$AutoPath)

$ErrorActionPreference = "Stop"
$problems = New-Object System.Collections.Generic.List[string]
$cases = New-Object System.Collections.Generic.List[string]
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("eskd_drew_auto_check_" + [guid]::NewGuid().ToString("N"))
try {
    $asm = [System.Reflection.Assembly]::ReflectionOnlyLoad([System.IO.File]::ReadAllBytes($AutoPath))
    $stream = $asm.GetManifestResourceStream("SfxMain.payload.zip")
    if (-not $stream) { throw "в установщике нет ресурса SfxMain.payload.zip" }
    Add-Type -AssemblyName System.IO.Compression
    $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Read)
    $entry = $zip.GetEntry("install-flat.ps1")
    if (-not $entry) { throw "в комплекте установщика нет install-flat.ps1" }
    $reader = New-Object System.IO.StreamReader($entry.Open(), [System.Text.Encoding]::UTF8)
    $script = $reader.ReadToEnd(); $reader.Dispose(); $zip.Dispose()

    $tokens = $null; $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput($script, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { $problems.Add("install-flat.ps1 не разбирается: $($errors[0].Message)") }
    $personal = [regex]::Matches($script, "(?i)[A-Z]:\\Users\\(?!Default\\)[^\\'""\s]+\\")
    if ($personal.Count) { $problems.Add("личный путь в install-flat.ps1: $($personal[0].Value)") }

    $lines = $script -split "\r?\n"
    $from = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l -like "# Delete updater files*" })
    $to = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l -like "*updater files deleted*" })
    if ($from -lt 0 -or $to -lt $from) { throw "в install-flat.ps1 не найден блок «Delete updater files» … «updater files deleted»" }
    $block = $lines[$from..$to] -join "`r`n"

    foreach ($mode in "machine", "peruser") {
        $root = Join-Path $temp $mode
        $lad = Join-Path $root "LocalAppData"
        $updates = Join-Path $root "ProgramData\CAD Booster\Drew\updates"
        $userDrew = Join-Path $lad "CAD Booster\Drew"
        $isMachine = $mode -eq "machine"
        $dst = if ($isMachine) { Join-Path $root "ProgramFiles\CAD Booster\Drew" } else { $userDrew }
        New-Item -ItemType Directory -Force -Path $dst, $userDrew, $updates | Out-Null
        foreach ($f in "CADBooster.Drew.Drawing.dll", "updater.exe", "updater.ini") { [System.IO.File]::WriteAllText((Join-Path $dst $f), "x") }
        [System.IO.File]::WriteAllText((Join-Path $userDrew "updater.exe"), "x")

        $code = $block.Replace("'C:\ProgramData\CAD Booster\Drew\updates'", "'$updates'")
        $foreign = [regex]::Matches($code, "(?i)'[A-Z]:\\[^']*'") | Where-Object { -not $_.Value.StartsWith("'$temp", [System.StringComparison]::OrdinalIgnoreCase) }
        if (@($foreign).Count) { throw "в блоке удаления путь вне песочницы: $(@($foreign)[0].Value)" }
        $savedLad = $env:LOCALAPPDATA
        try { $env:LOCALAPPDATA = $lad; & ([scriptblock]::Create($code)) *> $null } finally { $env:LOCALAPPDATA = $savedLad }

        $label = if ($isMachine) { "для всех пользователей" } else { "для одного пользователя" }
        if (-not (Test-Path -LiteralPath (Join-Path $dst "CADBooster.Drew.Drawing.dll"))) { $problems.Add("${label}: удалён сам Drew ($dst)") }
        foreach ($f in "updater.exe", "updater.ini") {
            if (Test-Path -LiteralPath (Join-Path $dst $f)) { $problems.Add("${label}: $f не удалён") }
        }
        if (Test-Path -LiteralPath $updates) { $problems.Add("${label}: папка ProgramData\CAD Booster\Drew\updates не удалена") }
        if ($isMachine -and (Test-Path -LiteralPath $userDrew)) { $problems.Add("${label}: остатки в LOCALAPPDATA\CAD Booster\Drew не удалены") }
        $cases.Add($mode)
    }
} catch {
    $problems.Add("исключение: $($_.Exception.Message)")
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
[pscustomobject]@{ ok = ($problems.Count -eq 0); problems = @($problems); cases = @($cases); tempRemoved = -not (Test-Path -LiteralPath $temp) } | ConvertTo-Json -Compress
if ($problems.Count) { exit 1 }
