<#
.SYNOPSIS
    Проверка шага 8 установщика «Отучение SolidWorks от сети» без брандмауэра и прав администратора (T0, ревью 24.09.2026).
.DESCRIPTION
    1. Ожидаемое число правил (Get-EskdSwBlockExpectedRules модуля EskdDeploy.psm1) совпадает с тем, что создаёт пакет:
       правило Test-SldWorksConflict берётся из самого Set-SwInternetBlock.ps1, записи — из настоящего манифеста, а
       программы подменяются пустыми файлами во временной папке (часть — нарочно без файла).
    2. Ветка установщика «уже администратор» при сбое пакета или нехватке правил пишет [ВНИМАНИЕ], а не [OK]: её текст
       берётся из Setup_Workstation_SolidWorks.ps1 и выполняется с поддельным пакетом (только выводит строку и выходит с
       заданным кодом) и заглушкой Get-NetFirewallRule. Настоящий пакет не запускается, брандмауэр и hosts не меняются.
    Вывод — JSON с результатом; код выхода 0 при успехе.
#>
param([Parameter(Mandatory = $true)][string]$RepoRoot)

$ErrorActionPreference = "Stop"
$problems = New-Object System.Collections.Generic.List[string]
$blockDir = Join-Path $RepoRoot "01_Настройки_SolidWorks\SwInternetBlock"
$setupDir = Join-Path $RepoRoot "01_Настройки_SolidWorks\_Служебное"
Import-Module (Join-Path $setupDir "EskdDeploy.psm1") -Force -DisableNameChecking
$temp = Join-Path ([IO.Path]::GetTempPath()) ("eskd_swblock_" + [guid]::NewGuid().ToString("N"))

function Find-Ast($path, [scriptblock]$predicate) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors) { throw "$path не разобран: $($errors[0].Message)" }
    return @($ast.FindAll($predicate, $true))
}

$expected = @(); $package = @(); $skippedByPolicy = @(); $verdicts = @()
try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null

    # --- 1. Ожидание установщика = правило пакета
    $conflict = Find-Ast (Join-Path $blockDir "Set-SwInternetBlock.ps1") {
        param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq "Test-SldWorksConflict" }
    if ($conflict.Count -ne 1) { throw "В Set-SwInternetBlock.ps1 нет функции Test-SldWorksConflict" }
    . ([scriptblock]::Create($conflict[0].Extent.Text))

    $entries = [System.IO.File]::ReadAllText((Join-Path $blockDir "SWInternetBlock.manifest.json"), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $fake = @()
    $i = 0
    foreach ($e in @($entries)) {
        $i++
        $path = Join-Path (Join-Path $temp "programs\$i") (Split-Path -Leaf $e.path)
        # Каждой 25-й программы «нет на этом ПК»; SLDWORKS.exe есть всегда — проверяется политика, а не наличие файла.
        if (($i % 25) -ne 0 -or $e.path -match 'SLDWORKS\.exe$') {
            New-Item -ItemType File -Path $path -Force | Out-Null
        }
        $fake += [pscustomobject]@{ name = $e.name; path = $path; direction = $e.direction; category = $e.category }
    }
    $manifest = Join-Path $temp "manifest.json"
    [System.IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject $fake -Depth 3), (New-Object System.Text.UTF8Encoding($false)))

    $expected = @(Get-EskdSwBlockExpectedRules -ManifestPath $manifest | ForEach-Object { $_.name })
    # Условия цикла apply пакета: программа есть; Test-SldWorksConflict без -BlockSldWorks — пропуск.
    foreach ($e in $fake) {
        if (-not (Test-Path -LiteralPath $e.path)) { continue }
        if (Test-SldWorksConflict $e.path $e.direction) { $skippedByPolicy += $e.name; continue }
        $package += $e.name
    }
    $missing = @($fake | Where-Object { -not (Test-Path -LiteralPath $_.path) }).Count
    if ($missing -eq 0) { $problems.Add("проверка без отсутствующих программ ничего не доказывает") }
    $extra = @($expected | Where-Object { $package -notcontains $_ })
    $lost = @($package | Where-Object { $expected -notcontains $_ })
    if ($extra) { $problems.Add("установщик ждёт правила, которых пакет не создаёт: " + ($extra -join "; ")) }
    if ($lost) { $problems.Add("пакет создаёт правила, которых установщик не ждёт: " + ($lost -join "; ")) }

    # --- 2. Ветка «уже администратор» шага 8
    $setupPath = Join-Path $setupDir "Setup_Workstation_SolidWorks.ps1"
    $step = Find-Ast $setupPath {
        param($a) $a -is [System.Management.Automation.Language.IfStatementAst] -and $a.Clauses[0].Item1.Extent.Text -eq '$SwInternetBlock' }
    if ($step.Count -ne 1) { throw "В установщике нет шага «if (`$SwInternetBlock)»" }
    $clause = @($step[0].FindAll({ param($a) $a -is [System.Management.Automation.Language.IfStatementAst] }, $true) |
        ForEach-Object { $_.Clauses } | Where-Object { $_.Item1.Extent.Text -eq '$machine' })
    if ($clause.Count -ne 1) { throw "В шаге 8 нет ветки «elseif (`$machine)»" }
    $adminBranch = $clause[0].Item2.Extent.Text
    $adminBranch = $adminBranch.Substring(1, $adminBranch.Length - 2)   # тело без внешних { }

    $sb = Join-Path $temp "Set-SwInternetBlock.ps1"
    [System.IO.File]::WriteAllText($sb, 'param([string]$Mode) "fake $Mode"; if ($env:ESKD_SWBLOCK_FAIL -eq $Mode) { "FATAL: fake"; exit 1 }; exit 0')
    $script:log = New-Object System.Collections.Generic.List[string]
    function Write-Ok($text) { $script:log.Add("[OK] $text") }
    function Write-Info($text) { $script:log.Add("[ИНФО] $text") }
    function Write-Warn($text) { $script:log.Add("[ВНИМАНИЕ] $text") }
    function Write-Fail($text) { $script:log.Add("[ОШИБКА] $text") }
    function Get-NetFirewallRule { [CmdletBinding()] param([string]$DisplayName) 1..$script:rules | ForEach-Object { [pscustomobject]@{ DisplayName = $DisplayName } } }

    $cases = @(
        @{ label = "всё создано"; fail = ""; rules = 10; ok = $true },
        @{ label = "apply упал"; fail = "apply"; rules = 10; ok = $false; text = "apply (код 1)" },
        @{ label = "hosts-apply упал"; fail = "hosts-apply"; rules = 10; ok = $false; text = "hosts-apply (код 1)" },
        @{ label = "правил меньше"; fail = ""; rules = 9; ok = $false; text = "9 из 10" }
    )
    foreach ($case in $cases) {
        $script:log.Clear()
        $script:rules = $case.rules
        $sbExpected = 10
        $env:ESKD_SWBLOCK_FAIL = $case.fail
        . ([scriptblock]::Create($adminBranch))
        $okLines = @($script:log | Where-Object { $_.StartsWith("[OK]") })
        $warnLines = @($script:log | Where-Object { $_.StartsWith("[ВНИМАНИЕ]") })
        $verdicts += [pscustomobject]@{ case = $case.label; ok = $okLines; warn = $warnLines }
        if ($case.ok -and ($okLines.Count -ne 1 -or $warnLines.Count)) { $problems.Add("$($case.label): ожидался один [OK] без [ВНИМАНИЕ]: " + ($script:log -join " | ")) }
        if (-not $case.ok) {
            if ($okLines.Count) { $problems.Add("$($case.label): сбой закончился [OK]: " + ($okLines -join " | ")) }
            if (-not @($warnLines | Where-Object { $_.Contains($case.text) }).Count) {
                $problems.Add("$($case.label): нет [ВНИМАНИЕ] с «$($case.text)»: " + ($script:log -join " | "))
            }
        }
    }
} catch {
    $problems.Add("сбой проверки: $($_.Exception.Message)")
} finally {
    Remove-Item Env:\ESKD_SWBLOCK_FAIL -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

$json = [pscustomobject]@{
    ok = ($problems.Count -eq 0); problems = @($problems); expected = $expected.Count; package = $package.Count
    skippedByPolicy = @($skippedByPolicy); verdicts = @($verdicts); tempRemoved = -not (Test-Path -LiteralPath $temp)
} | ConvertTo-Json -Compress -Depth 4
# Кириллица — последовательностями \uXXXX: вывод PowerShell 5.1 в канал идёт в кодировке консоли.
[regex]::Replace($json, '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
if ($problems.Count) { exit 1 }
