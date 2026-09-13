<#
.SYNOPSIS
    Проверка записи файлов SWPlus установщиком (T0, WP-3.4): без изменений — без записи, справочники только дополняются.
.DESCRIPTION
    Функции Write-SwPlusLines, Add-SwPlusFamily и Add-SwPlusFirm извлекаются из Setup_Workstation_SolidWorks.ps1
    разбором AST (сам установщик не запускается) и проверяются на копиях справочников во временном каталоге.
    Вывод — JSON; код выхода 0 при успехе.
#>
param([Parameter(Mandatory = $true)][string]$SetupPath, [Parameter(Mandatory = $true)][string]$SwPlusRoot)

$ErrorActionPreference = "Stop"
$problems = New-Object System.Collections.Generic.List[string]
function Expect($label, $actual, $expected) {
    if ("$actual" -cne "$expected") { $problems.Add("${label}: ожидалось «$expected», получено «$actual»") }
}

$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($SetupPath, [ref]$tokens, [ref]$errors)
foreach ($name in @("Write-SwPlusLines", "Add-SwPlusFamily", "Add-SwPlusFirm")) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { $problems.Add("в Setup нет функции $name"); continue }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$cp1251 = [System.Text.Encoding]::GetEncoding(1251)
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("eskd_setup_check_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    if ($problems.Count -eq 0) {
        # реальные справочники: существующие фамилия и организация — файлы не переписываются
        foreach ($file in @("MProp\MProp_Fam.txt", "MProp\MProp_Firm.txt", "Master\Master.ini", "ТТ\TT.TXT", "ТТ\TT_Prof.txt")) {
            Copy-Item -LiteralPath (Join-Path $SwPlusRoot $file) -Destination (Join-Path $temp ([System.IO.Path]::GetFileName($file)))
        }
        $fam = Join-Path $temp "MProp_Fam.txt"; $firm = Join-Path $temp "MProp_Firm.txt"
        $famBefore = [System.IO.File]::ReadAllBytes($fam); $firmBefore = [System.IO.File]::ReadAllBytes($firm)
        $firstFamily = ([System.IO.File]::ReadAllLines($fam, $cp1251) | Where-Object { $_.Trim() } | Select-Object -First 1)
        $firstFirm = ([System.IO.File]::ReadAllLines($firm, $cp1251) | Select-Object -First 1)
        Expect "существующая фамилия не пишется" (Add-SwPlusFamily -Path $fam -Name $firstFamily) $false
        Expect "существующая организация не пишется" (Add-SwPlusFirm -Path $firm -Name $firstFirm) $false
        Expect "MProp_Fam.txt не изменён" ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($fam))) ([Convert]::ToBase64String($famBefore))
        Expect "MProp_Firm.txt не изменён" ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($firm))) ([Convert]::ToBase64String($firmBefore))
        foreach ($tt in @("TT.TXT", "TT_Prof.txt", "Master.ini")) {
            $path = Join-Path $temp $tt
            $lines = @([System.IO.File]::ReadAllLines($path, $cp1251))
            if ($tt -ne "Master.ini") { $lines = @($lines | Where-Object { $_.Trim() -ne "" }) }
            Expect "$tt без изменений не переписывается" (Write-SwPlusLines -Path $path -Lines $lines) $false
        }

        # новая фамилия и организация — в конец, индексы прежних записей не сдвигаются
        $famLines = @([System.IO.File]::ReadAllLines($fam, $cp1251))
        Expect "новая фамилия записана" (Add-SwPlusFamily -Path $fam -Name "Новиков Н.Н.") $true
        $after = @([System.IO.File]::ReadAllLines($fam, $cp1251))
        Expect "первая фамилия на месте" $after[0] $famLines[0]
        Expect "новая фамилия в конце" $after[-1] "Новиков Н.Н."
        Expect "кодировка cp1251 и CRLF" ([System.IO.File]::ReadAllText($fam, $cp1251).EndsWith("Новиков Н.Н.`r`n")) $true
        $firmLines = @([System.IO.File]::ReadAllLines($firm, $cp1251))
        Expect "новая организация записана" (Add-SwPlusFirm -Path $firm -Name "ООО «Новая»") $true
        $afterFirm = @([System.IO.File]::ReadAllLines($firm, $cp1251))
        Expect "пары сохранены" ($afterFirm.Count % 2) 0
        Expect "новая пара в конце" $afterFirm[-2] "ООО «Новая»"
        Expect "первая организация на месте" $afterFirm[0] $firmLines[0]

        # нечётный файл организаций дополняется до пар
        $odd = Join-Path $temp "odd_firm.txt"
        [System.IO.File]::WriteAllText($odd, "А`r`n", $cp1251)
        [void](Add-SwPlusFirm -Path $odd -Name "Б")
        Expect "нечётный файл" ([System.IO.File]::ReadAllText($odd, $cp1251)) "А`r`n`r`nБ`r`n`r`n"
    }
} catch {
    $problems.Add("исключение: $($_.Exception.Message)")
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
[pscustomobject]@{ ok = ($problems.Count -eq 0); problems = @($problems) } | ConvertTo-Json -Compress
if ($problems.Count) { exit 1 }
