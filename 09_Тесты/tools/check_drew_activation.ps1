<#
.SYNOPSIS
    Проверка офлайн-активации Drew (T0): код, выданный кейгеном администратора, принимается клиентским окном только
    на той машине, для которой выдан; чужой и испорченный код отклоняются.
.DESCRIPTION
    Функции проверки берутся из 3_активация\Client-Activate-Drew.ps1 разбором AST (окно не открывается, файлы не
    пишутся). Эталонный код выдан кейгеном администратора для вымышленного ключа железа A1B2C3D4×8 ключом подписи
    объекта; приватного ключа в репозитории нет — только публичный внутри клиентского скрипта. Вывод — JSON.
#>
param([Parameter(Mandatory = $true)][string]$ClientScript)

$ErrorActionPreference = "Stop"
$problems = New-Object System.Collections.Generic.List[string]
function Expect($label, $actual, $expected) {
    if ("$actual" -ne "$expected") { $problems.Add("${label}: ожидалось «$expected», получено «$actual»") }
}

$goldenMid = "A1B2C3D4" * 8
$goldenCode = "DREW-TB9ND-NAN74-DX9RP-BXGS3-J97Y5-G0PNA-23FS2-155NF-NZ24Z-1B1DW-GBW18-SZE6F-E0MY9-W4K69-HB367-YVTQF-1WSTN-D3MVH-248XH-NF4YR-NH9EY-KEHRM-HKZ2Y-08QCN-2RP8H-J62QZ-WZMZP-M"

try {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($ClientScript, [ref]$tokens, [ref]$errors)
    Expect "клиентский скрипт разбирается" $errors.Count 0
    $EcdsaPubB64 = ($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq "EcdsaPubB64" }).DefaultValue.Value
    Expect "в клиентском скрипте есть публичный ключ" ([string]::IsNullOrEmpty($EcdsaPubB64)) $false
    foreach ($name in @("ConvertFrom-Base32", "Test-ActivationCode")) {
        $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if (-not $definition) { $problems.Add("в клиентском скрипте нет функции $name"); continue }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $script:alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    if ($problems.Count -eq 0) {
        $ok = Test-ActivationCode $goldenCode $goldenMid
        Expect "код принят на своей машине" $ok.Ok $true
        $foreign = Test-ActivationCode $goldenCode ("0F0F0F0F" * 8)
        Expect "код отклонён на другой машине" $foreign.Ok $false
        Expect "причина: другая машина" $foreign.Reason "код выдан для ДРУГОЙ машины"
        $broken = Test-ActivationCode ($goldenCode.Substring(0, $goldenCode.Length - 1) + "A") $goldenMid
        Expect "испорченный код отклонён" $broken.Ok $false
        Expect "пустой код отклонён" (Test-ActivationCode "" $goldenMid).Ok $false
        Expect "код без префикса отклонён" (Test-ActivationCode ($goldenCode.Substring(5)) $goldenMid).Ok $false
        # Save-ActivationFile пишет только в %APPDATA% пользователя
        $save = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "Save-ActivationFile" }, $true)
        Expect "файл активации — в профиле пользователя" ($save -and $save.Extent.Text.Contains('$env:APPDATA')) $true
    }
} catch {
    $problems.Add("исключение: $($_.Exception.Message)")
}
[pscustomobject]@{ ok = ($problems.Count -eq 0); problems = @($problems) } | ConvertTo-Json -Compress
if ($problems.Count) { exit 1 }
