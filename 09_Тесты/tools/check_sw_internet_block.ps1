<#
.SYNOPSIS
    Проверка отучения SolidWorks от сети без брандмауэра, hosts и прав администратора (T0; решение владельца 26.09.2026).
.DESCRIPTION
    Функции пакета Set-SwInternetBlock.ps1 берутся разбором файла (сам пакет не запускается) и проверяются на данных:
      1. Адреса: «интернет» — всё, кроме частных сетей и самого ПК; сервер лицензий с публичным адресом остаётся
         открытым; прокси в локальной сети закрывается.
      2. hosts: каждый домен ровно один раз, одна метка; прежние ошибки (мусорная строка «`r`n», двойная метка,
         поиск подстрокой: online. внутри backuponline., закомментированная строка считалась заглушкой) исправлены;
         повторный запуск ничего не меняет; снятие не трогает чужие строки.
      3. Папки: системные (диск, Windows, Program Files, профиль) целиком не закрываются; папка программы поднимается до
         папки SOLIDWORKS («Менеджер установки SOLIDWORKS» русской установки).
      4. Программы: SLDWORKS.exe закрывается в обе стороны (прежний пакет его пропускал), имена правил уникальны.
      5. Сверка правила и код итога: 0 — всё закрыто; 3 — нужно применить; 4 — правила не действуют.
      6. Шаг [8/9] установщика: текст ветки «if ($SwInternetBlock)» выполняется с подставным Invoke-EskdSwBlock —
         применение только когда проверка нашла нехватку; неполный итог — [ВНИМАНИЕ], ошибкой установки не считается
         (решение владельца 25.09.2026); сетевые функции SolidWorks выключаются в реестре; в тестовом корне реестра
         брандмауэр не трогается.
    Вывод — JSON; код выхода 0 при успехе.
#>
param([Parameter(Mandatory = $true)][string]$RepoRoot)

$ErrorActionPreference = "Stop"
$problems = New-Object System.Collections.Generic.List[string]
$blockDir = Join-Path $RepoRoot "01_Настройки_SolidWorks\SwInternetBlock"
$setupDir = Join-Path $RepoRoot "01_Настройки_SolidWorks\_Служебное"
$temp = Join-Path ([IO.Path]::GetTempPath()) ("eskd_swblock_" + [guid]::NewGuid().ToString("N"))
$facts = [ordered]@{}

function Find-Ast($path, [scriptblock]$predicate) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors) { throw "$path не разобран: $($errors[0].Message)" }
    return @($ast.FindAll($predicate, $true))
}
function Check([bool]$ok, [string]$text) { if (-not $ok) { $problems.Add($text) } }

try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $package = Join-Path $blockDir "Set-SwInternetBlock.ps1"
    Check (-not (Test-Path -LiteralPath (Join-Path $blockDir "SWInternetBlock.manifest.json"))) "остался список программ с одной машины (манифест)"
    # Функции и переменные уровня сценария пакета, без блока запуска
    foreach ($f in Find-Ast $package { param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] }) {
        . ([scriptblock]::Create($f.Extent.Text))
    }
    foreach ($a in Find-Ast $package { param($a) $a -is [System.Management.Automation.Language.AssignmentStatementAst] -and $a.Left.Extent.Text -like '$script:*' }) {
        . ([scriptblock]::Create($a.Extent.Text))
    }

    # --- 1. Адреса
    $ranges = @(Get-SwBlockIpv4Ranges)
    $expectedRanges = @('1.0.0.0-9.255.255.255', '11.0.0.0-100.63.255.255', '100.128.0.0-126.255.255.255',
        '128.0.0.0-169.253.255.255', '169.255.0.0-172.15.255.255', '172.32.0.0-192.167.255.255', '192.169.0.0-223.255.255.255')
    Check (($ranges -join ',') -eq ($expectedRanges -join ',')) ("интернет IPv4 посчитан неверно: " + ($ranges -join ', '))
    $withServer = @(Get-SwBlockIpv4Ranges -Allow '8.8.8.8')
    Check ($withServer -contains '1.0.0.0-8.8.8.7' -and $withServer -contains '8.8.8.9-9.255.255.255') ("публичный сервер лицензий не вырезан: " + ($withServer -join ', '))
    $withProxy = @(Get-SwBlockIpv4Ranges -Block '192.168.1.10')
    Check ($withProxy -contains '192.168.1.10') "прокси в локальной сети не закрыт"
    Check (-not (Test-PublicIpv4 '192.168.10.8') -and -not (Test-PublicIpv4 '127.0.0.1') -and -not (Test-PublicIpv4 '10.1.2.3') -and (Test-PublicIpv4 '8.8.8.8')) "Test-PublicIpv4 ошибается"
    Check ($script:InternetIpv6 -eq '2000::-3fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff') "интернет IPv6 — не 2000::/3"
    Check ((Get-SwBlockFingerprint 'A') -eq (Get-SwBlockFingerprint 'a') -and (Get-SwBlockFingerprint 'a') -ne (Get-SwBlockFingerprint 'b')) "отпечаток не зависит от регистра или совпадает у разных строк"

    # --- 2. hosts
    $domains = $script:SwDomains
    Check ($domains -contains 'im.solidworks.com' -and $domains -contains 'performance.solidworks.com') "в hosts нет адреса Менеджера установки или сбора данных"
    $original = @(
        '# Copyright (c) Microsoft Corp.', '127.0.0.1 localhost', '0.0.0.0 api.cryptolens.io # DrewGov offline',
        '`r`n# SW-Internet-Block (SOLIDWORKS cloud/telemetry isolation - Drew safe: AWS untouched)',
        '# SW-Internet-Block (SOLIDWORKS cloud/telemetry isolation - Drew safe: AWS untouched)',
        '0.0.0.0 activate.solidworks.com', '0.0.0.0 backuponline.solidworks.com', '# 0.0.0.0 online.solidworks.com',
        '0.0.0.0 www.solidworks.com', ''
    ) -join "`r`n"
    $missingBefore = @(Test-SwBlockHostsComplete $original $domains)
    Check ($missingBefore -contains 'online.solidworks.com') "online.solidworks.com считается заглушённым (поиск подстрокой или закомментированная строка)"
    $applied = Get-SwBlockHostsText $original $domains
    Check ($null -ne $applied) "hosts не изменён, хотя доменов не хватало"
    if ($applied) {
        $lines = @($applied -split "`r`n")
        Check (@(Test-SwBlockHostsComplete $applied $domains).Count -eq 0) "после записи в hosts не хватает доменов"
        foreach ($d in $domains) { Check (@($lines | Where-Object { $_ -eq "0.0.0.0 $d" }).Count -eq 1) "домен $d в hosts не ровно один раз" }
        Check (@($lines | Where-Object { $_.Contains($script:HostsMarker) }).Count -eq 1) "метка пакета в hosts не одна"
        Check (-not ($applied.Contains('`r`n'))) "мусорная строка «`r`n» осталась в hosts"
        Check (-not ($applied -match '[^\x00-\x7F]')) "в hosts записаны не латинские символы"
        foreach ($keep in '127.0.0.1 localhost', '0.0.0.0 api.cryptolens.io # DrewGov offline', '# 0.0.0.0 online.solidworks.com', '0.0.0.0 www.solidworks.com', '# Copyright (c) Microsoft Corp.') {
            Check ($lines -contains $keep) "чужая строка hosts пропала: $keep"
        }
        Check ($null -eq (Get-SwBlockHostsText $applied $domains)) "повторная запись hosts снова меняет файл"
        $removed = Get-SwBlockHostsText $applied $domains -Remove
        $rl = @($removed -split "`r`n")
        Check (-not @($rl | Where-Object { $_.Contains($script:HostsMarker) }).Count) "после снятия осталась метка"
        Check (@(Test-SwBlockHostsComplete $removed $domains).Count -eq $domains.Count) "после снятия остались заглушки пакета"
        Check ($rl -contains '0.0.0.0 www.solidworks.com' -and $rl -contains '0.0.0.0 api.cryptolens.io # DrewGov offline') "снятие удалило чужие заглушки"
    }

    # --- 3. Папки
    $forbidden = Get-SwBlockForbiddenRoots
    foreach ($bad in @("$env:SystemDrive\", $env:SystemRoot, (Join-Path $env:SystemRoot 'System32'), $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA, $env:USERPROFILE, '\\server\share')) {
        if ($bad) { Check (-not (Test-SwBlockSafeRoot $bad $forbidden)) "системная папка закрывалась бы целиком: $bad" }
    }
    foreach ($good in @((Join-Path $env:SystemRoot 'SolidWorks'), (Join-Path $env:ProgramFiles 'SOLIDWORKS Corp'), 'D:\SW\SOLIDWORKS Corp')) {
        Check (Test-SwBlockSafeRoot $good $forbidden) "папка SolidWorks отвергнута: $good"
    }
    $im = Join-Path $temp 'CommonFiles\Менеджер установки SOLIDWORKS\CheckForUpdates'
    New-Item -ItemType Directory -Path $im -Force | Out-Null
    $roots = New-Object System.Collections.Generic.List[object]
    Add-SwBlockRootFromFile $roots (Join-Path $im 'sldCheckForUpdates.exe') 'IMPrototype'
    Check ($roots.Count -eq 1 -and $roots[0].Path -eq (Split-Path -Parent $im)) ("папка проверки обновлений не поднята до «Менеджер установки SOLIDWORKS»: " + (($roots | ForEach-Object { $_.Path }) -join '; '))

    # --- 4. Программы и правила
    $sw = Join-Path $temp 'SOLIDWORKS Corp'
    foreach ($rel in 'SOLIDWORKS\SLDWORKS.exe', 'SOLIDWORKS\sldProcMon.exe', 'SOLIDWORKS\swScheduler\swScheduler.exe', 'eDrawings\eDrawings.exe', 'SOLIDWORKS\readme.txt', 'SOLIDWORKS\x.exe_old') {
        $p = Join-Path $sw $rel
        New-Item -ItemType Directory -Path (Split-Path -Parent $p) -Force | Out-Null
        [IO.File]::WriteAllText($p, '')
    }
    $progs = @(Get-SwBlockPrograms -Roots @([pscustomobject]@{ Path = $sw; Reason = 'тест' }))
    Check ($progs.Count -eq 4) ("программ найдено не 4: " + (($progs | ForEach-Object { $_.Path }) -join '; '))
    $plan = New-SwBlockPlan
    $rules = @(Get-SwBlockDesiredRules -Programs $progs -Plan $plan)
    $sld = @($rules | Where-Object { $_.Program -match 'SLDWORKS\.exe$' })
    Check ($sld.Count -eq 2 -and ((@($sld.Direction | Sort-Object)) -join ',') -eq 'Inbound,Outbound') "SLDWORKS.exe не закрыт в обе стороны"
    Check (@($rules.Name | Sort-Object -Unique).Count -eq $rules.Count) "имена правил повторяются"
    Check ((Get-SwBlockRuleName 'C:\A\SLDWORKS.exe' 'Outbound') -eq (Get-SwBlockRuleName 'c:\a\sldworks.exe' 'Outbound')) "имя правила зависит от регистра пути"
    $facts.programs = $progs.Count; $facts.rules = $rules.Count
    Check (-not @($rules | Where-Object { $_.Fingerprint -ne $plan.Fingerprint }).Count) "у программ без исключения адреса отличаются от общих"

    # SWTools.exe: открыт только его сервер лицензий (решение владельца 26.09.2026)
    $swt = [pscustomobject]@{ Path = 'C:\Program Files\SWTools\SWTools.exe'; Reason = 'тест' }
    $online = @(Get-SwBlockDesiredRules -Programs @($swt, $progs[0]) -Plan $plan -Resolver { param($h) if ($h -eq 'license.vizbuka.ru') { '185.112.102.122' } })
    $sw1 = @($online | Where-Object { $_.Program -like '*SWTools.exe' })
    Check ($sw1.Count -eq 2 -and $sw1[0].Addresses -contains '172.32.0.0-185.112.102.121' -and $sw1[0].Addresses -contains '185.112.102.123-192.167.255.255') ("SWTools.exe: сервер лицензий не вырезан: " + ($sw1[0].Addresses -join ', '))
    Check ($sw1[0].Fingerprint -and $sw1[0].Fingerprint -ne $plan.Fingerprint) "SWTools.exe: отпечаток адресов не свой"
    $other = @($online | Where-Object { $_.Program -notlike '*SWTools.exe' })
    Check (-not @($other | Where-Object { $_.Addresses -contains '185.112.102.123-192.167.255.255' }).Count) "сервер лицензий SWTools открыт не только SWTools.exe"
    $offline = @(Get-SwBlockDesiredRules -Programs @($swt) -Plan $plan -Resolver { param($h) })
    Check ($offline[0].Fingerprint -eq '' -and (($offline[0].Addresses -join ',') -eq ($plan.Addresses -join ','))) "SWTools.exe без сети: новое правило не закрывает полностью"
    $kept = [pscustomobject]@{ Direction = 'Outbound'; Action = 'Block'; Enabled = 'True'; Profile = 'Any'; Program = $swt.Path; Description = (Get-SwBlockDescription 'x' '0badf00d') }
    Check (Test-SwBlockRuleOk $kept $offline[0] $offline[0].Fingerprint) "SWTools.exe без сети: прежнее правило с сервером лицензий не принимается"

    # --- 5. Сверка правила и код итога
    $d = $sld | Where-Object { $_.Direction -eq 'Outbound' }
    $good = [pscustomobject]@{ Direction = 'Outbound'; Action = 'Block'; Enabled = 'True'; Profile = 'Any'; Program = $d.Program.ToUpperInvariant(); Description = (Get-SwBlockDescription 'x' 'abcd1234') }
    Check (Test-SwBlockRuleOk $good $d 'abcd1234') "верное правило не признано"
    foreach ($bad in @(
        @{ k = 'Description'; v = (Get-SwBlockDescription 'x' 'ffff0000') }, @{ k = 'Enabled'; v = 'False' }, @{ k = 'Action'; v = 'Allow' },
        @{ k = 'Program'; v = 'C:\other.exe' }, @{ k = 'Direction'; v = 'Inbound' }, @{ k = 'Profile'; v = 'Domain' })) {
        $r = $good.PSObject.Copy(); $r.($bad.k) = $bad.v
        Check (-not (Test-SwBlockRuleOk $r $d 'abcd1234')) "неверное правило ($($bad.k) = $($bad.v)) признано верным"
    }
    $base = @{ Programs = @(1); Missing = @(); Stale = @(); ForeignWrong = @(); Legacy = @(); HostsMissing = @(); Firewall = [pscustomobject]@{ Problems = @(); Notes = @() } }
    $codes = [ordered]@{}
    $codes.ok = Get-SwBlockAuditCode ([pscustomobject]$base)
    $x = $base.Clone(); $x.Missing = @(1); $codes.missing = Get-SwBlockAuditCode ([pscustomobject]$x)
    $x = $base.Clone(); $x.Legacy = @(1); $codes.legacy = Get-SwBlockAuditCode ([pscustomobject]$x)
    $x = $base.Clone(); $x.HostsMissing = @('a'); $codes.hosts = Get-SwBlockAuditCode ([pscustomobject]$x)
    $x = $base.Clone(); $x.Firewall = [pscustomobject]@{ Problems = @('off'); Notes = @() }; $codes.firewall = Get-SwBlockAuditCode ([pscustomobject]$x)
    $x = $base.Clone(); $x.Programs = @(); $codes.none = Get-SwBlockAuditCode ([pscustomobject]$x)
    Check (($codes.Values -join ',') -eq '0,3,3,3,4,3') ("коды итога: " + (($codes.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))

    # --- 6. Шаг [8/9] установщика
    Import-Module (Join-Path $setupDir "EskdDeploy.psm1") -Force -DisableNameChecking
    $step = Find-Ast (Join-Path $setupDir "Setup_Workstation_SolidWorks.ps1") {
        param($a) $a -is [System.Management.Automation.Language.IfStatementAst] -and $a.Clauses[0].Item1.Extent.Text -eq '$SwInternetBlock' }
    if ($step.Count -ne 1) { throw "В установщике нет шага «if (`$SwInternetBlock)»" }
    $stepText = $step[0].Extent.Text
    Check (-not $stepText.Contains('Get-EskdSwBlockExpectedRules')) "шаг 8 считает правила по прежнему списку"
    $cases = @(
        @{ label = 'уже закрыто'; codes = @(0); machine = $true; sandbox = $false; calls = 'audit'; ok = $true },
        @{ label = 'не хватало, применено'; codes = @(3, 0, 0); machine = $true; sandbox = $false; calls = 'audit,apply,audit'; ok = $true },
        @{ label = 'не хватало, не вышло'; codes = @(3, 3, 3); machine = $true; sandbox = $false; calls = 'audit,apply,audit'; ok = $false; warn = 'неполное' },
        @{ label = 'брандмауэр не применяет'; codes = @(4); machine = $true; sandbox = $false; calls = 'audit'; ok = $false; warn = 'не действуют' },
        @{ label = 'тестовый корень'; codes = @(); machine = $false; sandbox = $true; calls = ''; ok = $false; warn = '' }
    )
    $verdicts = @()
    foreach ($case in $cases) {
        $script:calls = New-Object System.Collections.Generic.List[string]
        $script:codes = New-Object System.Collections.Generic.Queue[int]
        foreach ($c in $case.codes) { $script:codes.Enqueue($c) }
        $script:log = New-Object System.Collections.Generic.List[string]
        $script:regs = New-Object System.Collections.Generic.List[string]
        & {
            function Write-Ok($text) { $script:log.Add("[OK] $text") }
            function Write-Info($text) { $script:log.Add("[ИНФО] $text") }
            function Write-Warn($text) { $script:log.Add("[ВНИМАНИЕ] $text") }
            function Write-Fail($text) { $script:log.Add("[ОШИБКА] $text") }
            function Set-Reg($Path, $Name, $Value, $Type = "String") { $script:regs.Add("$Path|$Name=$Value") }
            function Get-RegValue($Path, $Name) { "" }
            function Test-Path { $true }
            function Invoke-EskdSwBlock([string]$Script, [string]$Mode, [string]$ExtraRootFile = "") {
                $script:calls.Add($Mode)
                $c = $script:codes.Dequeue()
                $v = if ($c -eq 0) { "ИТОГ: SolidWorks отучен от интернета: тест." } else { "ИТОГ: не всё." }
                [pscustomobject]@{ Code = $c; Lines = @("строка пакета", $v); Verdict = $v }
            }
            $testScriptRoot = $setupDir
            $SwInternetBlock = $true; $machine = $case.machine; $sandbox = $case.sandbox; $failures = 0
            $swRoot = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025"; $install = "HKCU:\Software\SolidWorks\ESKD_Install"
            . ([scriptblock]::Create($stepText.Replace('$PSScriptRoot', '$testScriptRoot')))
            if ($failures -ne 0) { $problems.Add("$($case.label): отучение от сети посчитано ошибкой установки") }
        }
        $calls = $script:calls -join ','
        Check ($calls -eq $case.calls) "$($case.label): вызовы пакета «$calls», ожидались «$($case.calls)»"
        $blockOk = @($script:log | Where-Object { $_.StartsWith('[OK]') -and $_.Contains('отучен от интернета') })
        $warns = @($script:log | Where-Object { $_.StartsWith('[ВНИМАНИЕ]') })
        if ($case.ok) { Check ($blockOk.Count -eq 1 -and -not $warns.Count) "$($case.label): ожидался [OK] без [ВНИМАНИЕ]: $($script:log -join ' | ')" }
        else {
            Check (-not $blockOk.Count) "$($case.label): неудача закончилась [OK]"
            if ($case.warn) { Check (@($warns | Where-Object { $_.Contains($case.warn) }).Count -eq 1) "$($case.label): нет [ВНИМАНИЕ] «$($case.warn)»: $($script:log -join ' | ')" }
        }
        foreach ($need in "ESKD_Install|SwInternetBlock=1", "General|Show Latest News feeds In task pane=0", "General|Check Crash Fixes=0", "SW Event Log|Send Feedback Enabled=0") {
            Check (@($script:regs | Where-Object { $_.EndsWith($need) }).Count -eq 1) "$($case.label): не записано $need"
        }
        $verdicts += [pscustomobject]@{ case = $case.label; calls = $calls; log = @($script:log) }
    }
    $facts.cases = $verdicts.Count
} catch {
    $problems.Add("сбой проверки: $($_.Exception.Message) $($_.ScriptStackTrace)")
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

$json = [pscustomobject]@{
    ok = ($problems.Count -eq 0); problems = $problems.ToArray(); facts = $facts; tempRemoved = -not (Test-Path -LiteralPath $temp)
} | ConvertTo-Json -Compress -Depth 4
# Кириллица — последовательностями \uXXXX: вывод PowerShell 5.1 в канал идёт в кодировке консоли.
[regex]::Replace($json, '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
if ($problems.Count) { exit 1 }
