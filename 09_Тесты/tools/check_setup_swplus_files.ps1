<#
.SYNOPSIS
    Проверка записи файлов SWPlus установщиком (T0, WP-3.4): без изменений — без записи, своя фамилия и организация — первыми (З-3).
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
        # Общие списки MProp пусты (решение владельца 15.09.2026) — проверка на заполненных локальных списках пользователя;
        # остальные файлы — реальные. Существующие фамилия и организация — файлы не переписываются.
        foreach ($file in @("Master\Master.ini", "ТТ\TT.TXT", "ТТ\TT_Prof.txt")) {
            Copy-Item -LiteralPath (Join-Path $SwPlusRoot $file) -Destination (Join-Path $temp ([System.IO.Path]::GetFileName($file)))
        }
        $fam = Join-Path $temp "MProp_Fam.txt"; $firm = Join-Path $temp "MProp_Firm.txt"
        [System.IO.File]::WriteAllText($fam, "Петров П.П.`r`nСидоров С.С.`r`n", $cp1251)
        [System.IO.File]::WriteAllText($firm, "ТОО «Троя»`r`nТР`r`nАО «Завод»`r`n`r`n", $cp1251)
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

        # своя фамилия и организация — первыми (З-3: MProp при пустом свойстве берёт первую строку), остальные по порядку
        $famLines = @([System.IO.File]::ReadAllLines($fam, $cp1251))
        Expect "новая фамилия записана" (Add-SwPlusFamily -Path $fam -Name "Новиков Н.Н.") $true
        $after = @([System.IO.File]::ReadAllLines($fam, $cp1251))
        Expect "новая фамилия первой" $after[0] "Новиков Н.Н."
        Expect "прежние фамилии по порядку" ($after[1..($after.Count - 1)] -join "|") ($famLines -join "|")
        Expect "кодировка cp1251 и CRLF" ([System.IO.File]::ReadAllText($fam, $cp1251).StartsWith("Новиков Н.Н.`r`nПетров П.П.`r`n")) $true
        Expect "существующая фамилия поднимается первой" (Add-SwPlusFamily -Path $fam -Name "Сидоров С.С.") $true
        Expect "порядок после подъёма" (@([System.IO.File]::ReadAllLines($fam, $cp1251)) -join "|") "Сидоров С.С.|Новиков Н.Н.|Петров П.П."
        $firmLines = @([System.IO.File]::ReadAllLines($firm, $cp1251))
        Expect "новая организация записана" (Add-SwPlusFirm -Path $firm -Name "ООО «Новая»") $true
        $afterFirm = @([System.IO.File]::ReadAllLines($firm, $cp1251))
        Expect "пары сохранены" ($afterFirm.Count % 2) 0
        Expect "новая пара первой" ($afterFirm[0] + "|" + $afterFirm[1]) "ООО «Новая»|"
        Expect "прежние пары по порядку" ($afterFirm[2..($afterFirm.Count - 1)] -join "|") ($firmLines -join "|")
        Expect "существующая организация поднимается со своим кодом" (Add-SwPlusFirm -Path $firm -Name "ТОО «Троя»") $true
        Expect "порядок пар после подъёма" (@([System.IO.File]::ReadAllLines($firm, $cp1251)) -join "|") "ТОО «Троя»|ТР|ООО «Новая»||АО «Завод»|"

        # нечётный файл организаций дополняется до пар
        $odd = Join-Path $temp "odd_firm.txt"
        [System.IO.File]::WriteAllText($odd, "А`r`n", $cp1251)
        [void](Add-SwPlusFirm -Path $odd -Name "Б")
        Expect "нечётный файл" ([System.IO.File]::ReadAllText($odd, $cp1251)) "Б`r`n`r`nА`r`n`r`n"
    }
} catch {
    $problems.Add("исключение: $($_.Exception.Message)")
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
[pscustomobject]@{ ok = ($problems.Count -eq 0); problems = @($problems) } | ConvertTo-Json -Compress
if ($problems.Count) { exit 1 }
