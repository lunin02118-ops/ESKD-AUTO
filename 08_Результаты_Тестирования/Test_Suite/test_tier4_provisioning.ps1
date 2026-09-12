# test_tier4_provisioning.ps1
# Tier 4: Проверка окружения и развертывания рабочей станции SolidWorks 2025.
# Проверяет реестр, видимость вкладок ЕСКД, кнопки QAT, шрифты, регистрацию надстроек,
# валидность CodeBase (URI, существование файла) и живую загрузку
# надстройки в сессии SLDWORKS.

$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$passed = 0
$failed = 0
$warnings = 0

function Assert-Check {
    param(
        [bool]$Condition,
        [string]$TestName,
        [string]$ErrorMsg = ""
    )
    if ($Condition) {
        $global:passed++
        Write-Host "  [PASS] $TestName" -ForegroundColor Green
    } else {
        $global:failed++
        Write-Host "  [FAIL] $TestName : $ErrorMsg" -ForegroundColor Red
    }
}

function Warn-Check {
    param(
        [string]$TestName,
        [string]$WarnMsg
    )
    $global:warnings++
    Write-Host "  [WARN] $TestName : $WarnMsg" -ForegroundColor Yellow
}

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "  Tier 4: ПРОВЕРКА РАЗВЕРТЫВАНИЯ РАБОЧЕЙ СТАНЦИИ (PROVISIONING TESTS)" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

# 1. Проверка регистрации COM надстройки ЕСКД
Write-Host "`n--- 1. Регистрация COM-надстройки ЕСКД ---" -ForegroundColor Gray
$activeGuid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
$clsidPathUser = "HKCU:\Software\Classes\CLSID\$activeGuid"
$clsidPathRoot = "Registry::HKEY_CLASSES_ROOT\CLSID\$activeGuid"

$hasClsid = (Test-Path $clsidPathUser) -or (Test-Path $clsidPathRoot)
Assert-Check $hasClsid "COM CLSID надстройки ЕСКД зарегистрирован" "CLSID $activeGuid не найден в реестре!"

# Проверка автозапуска надстройки в SolidWorks
$startupKey = "HKCU:\Software\SolidWorks\AddInsStartup\$activeGuid"
$startupVal = -1
if (Test-Path $startupKey) {
    $startupVal = (Get-ItemProperty -Path $startupKey -Name "(Default)" -ErrorAction SilentlyContinue)."(Default)"
    if ($null -eq $startupVal) {
        $startupVal = (Get-ItemProperty -Path $startupKey -ErrorAction SilentlyContinue).$activeGuid
    }
}
Assert-Check (Test-Path $startupKey) "Запись автозапуска AddInsStartup присутствует"
Assert-Check ($startupVal -eq 1) "Автозапуск надстройки включен (значение = 1)" "Текущее значение автозапуска: $startupVal"

# Живая проба надстройки через python (PowerShell Marshal.GetActiveObject падает с TYPE_E
# на ранней привязке SldWorks — известная особенность; win32com работает корректно).
$eskdLiveLoaded = $false
$swProcEarly = Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue
if ($swProcEarly) {
    $pyOut = & python -c "import win32com.client; sw = win32com.client.GetActiveObject('SldWorks.Application'); print('LOADED' if sw.GetAddInObject('ESKD.MaterialSync.SwAddin_v5') else 'MISSING')" 2>$null
    $eskdLiveLoaded = ($pyOut -is [string] -and $pyOut -match 'LOADED')
}

# 2. Проверка видимости вкладок ЕСКД в CommandManager
# Кэш CommandManager в реестре стирается каждым выходом SolidWorks (FAQ 1): надстройка
# создаёт вкладки ПРОГРАММНО при каждой загрузке. Если в живой сессии надстройка
# загружена — вкладки существуют в UI независимо от реестра (учёт через [LIVE]).
Write-Host "`n--- 2. Видимость вкладок «ЕСКД» в CommandManager ---" -ForegroundColor Gray
$contexts = @("PartContext", "AssyContext", "DrwContext")

foreach ($ctx in $contexts) {
    $ctxPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\$ctx"
    $foundTab = $false
    $tabVisible = $false
    $tabPropsVal = ""

    if (Test-Path $ctxPath) {
        Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | ForEach-Object {
            $tPath = $_.PSPath
            $ref = (Get-ItemProperty -Path $tPath -Name "RefName" -ErrorAction SilentlyContinue).RefName
            $mod = (Get-ItemProperty -Path $tPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName
            $props = (Get-ItemProperty -Path $tPath -Name "Tab Props" -ErrorAction SilentlyContinue)."Tab Props"

            if (($ref -and ($ref -match "ЕСКД")) -or ($mod -and ($mod.ToUpper() -eq $activeGuid.ToUpper()))) {
                $foundTab = $true
                $tabPropsVal = $props
                if ($props -and ($props -match ",\s*1\s*,")) {
                    $tabVisible = $true
                }
            }
        }
    }

    if (-not $foundTab -and $eskdLiveLoaded) {
        Write-Host "  [LIVE] $ctx : вкладка ЕСКД отсутствует в кэше реестра, но создана надстройкой в живой сессии." -ForegroundColor DarkCyan
        $foundTab = $true
        $tabVisible = $true
    }
    Assert-Check $foundTab "Контекст $ctx : вкладка ЕСКД найдена в CommandManager" "Вкладка отсутствует!"
    Assert-Check $tabVisible "Контекст $ctx : вкладка ЕСКД видима (Visible = 1)" "Tab Props = '$tabPropsVal' (вкладка скрыта!)"
}

# 3. Проверка кнопок макросов SWPlus в верхней панели QAT (Quick Access Toolbar)
Write-Host "`n--- 3. Закрепление 9 кнопок макросов SWPlus в панели QAT ---" -ForegroundColor Gray
$qatPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\QAT\GB0"
$qatButtons = @{
    "Btn11" = "1,33639" # MProp
    "Btn12" = "1,33640" # SProp
    "Btn13" = "1,33641" # DProp
    "Btn14" = "1,33642" # SpecEditor
    "Btn15" = "1,33643" # RecordDimM
    "Btn16" = "1,33644" # Roughness
    "Btn17" = "1,33645" # TT
    "Btn18" = "1,33646" # Master
    "Btn19" = "1,33647" # SaveAsPDF
}

$hasQat = Test-Path $qatPath
Assert-Check $hasQat "Панель QAT (GB0) существует в реестре"

if ($hasQat) {
    $qatProps = Get-ItemProperty -Path $qatPath -ErrorAction SilentlyContinue
    foreach ($btn in ($qatButtons.Keys | Sort-Object)) {
        $expected = $qatButtons[$btn]
        $actual = $qatProps.$btn
        Assert-Check ($actual -eq $expected) "Кнопка $btn ($expected)" "Фактическое значение в реестре: '$actual'"
    }
}

# 4. Проверка установки шрифтов ЕСКД по ГОСТ 2.304 ---
# B2/CR#2: Setup ставит шрифты с оригинальными кириллическими именами («ГОСТ тип А.ttf» и т.д.)
# и в двух режимах: системная установка (HKLM + %SystemRoot%\Fonts) или пользовательская
# (HKCU + %LOCALAPPDATA%\Microsoft\Windows\Fonts, без прав администратора).
# Проверяем значения в реестре шрифтов по маскам ГОСТ/GOST и файлы в обоих каталогах.
Write-Host "`n--- 4. Установка шрифтов ЕСКД по ГОСТ 2.304 ---" -ForegroundColor Gray
$fontsFolder = Join-Path $env:SystemRoot "Fonts"
$userFontsFolder = Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Fonts"

$fontNames = @("GOST type A.ttf", "gost_a.ttf", "GOSTA.TTF", "GOST type B.ttf", "gost_b.ttf", "GOSTB.TTF",
               "ГОСТ тип А.ttf", "ГОСТ тип А наклонный.ttf", "ГОСТ тип В.ttf", "ГОСТ тип В наклонный.ttf", "GOST2304A_1.ttf")
$fontFiles = @()
foreach ($n in $fontNames) {
    if ((Test-Path -LiteralPath (Join-Path $fontsFolder $n)) -or (Test-Path -LiteralPath (Join-Path $userFontsFolder $n))) {
        $fontFiles += $n
    }
}

# Регистрация в реестре шрифтов (HKLM — системная, HKCU — пользовательская)
$fontRegValue = $null
foreach ($regBase in @("HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Fonts")) {
    if (Test-Path $regBase) {
        $props = Get-ItemProperty -Path $regBase -ErrorAction SilentlyContinue
        if ($props) {
            foreach ($p in $props.PSObject.Properties) {
                if ($p.Name -match 'ГОСТ тип|GOST') { $fontRegValue = $p.Name; break }
            }
        }
    }
    if ($fontRegValue) { break }
}

Assert-Check ($fontFiles.Count -ge 2) "Файлы шрифтов ГОСТ (тип A/B) присутствуют в каталогах шрифтов" "Найдено файлов: $($fontFiles.Count) в $fontsFolder / $userFontsFolder"
Assert-Check ($null -ne $fontRegValue) "Шрифты ГОСТ зарегистрированы в реестре шрифтов (HKLM/HKCU)" "Значение вида 'ГОСТ тип*' / 'GOST*' не найдено"

# 5. Проверка путей поиска в SolidWorks (File Locations)
Write-Host "`n--- 5. Пути поиска SolidWorks (File Locations) ---" -ForegroundColor Gray
$swExtRef = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\ExtReferences"
if (Test-Path $swExtRef) {
    $sheetFmt = (Get-ItemProperty -Path $swExtRef -Name "Sheet Format Folders" -ErrorAction SilentlyContinue)."Sheet Format Folders"
    $hasSheetFmt = ($sheetFmt -and ($sheetFmt -match "Основные надписи"))
    Assert-Check $hasSheetFmt "Настройка 'Sheet Format Folders' зарегистрирована в SolidWorks" "Путь: $sheetFmt"
} else {
    Warn-Check "SolidWorks ExtReferences" "Ветка реестра ExtReferences не найдена"
}

# 6. Валидация CodeBase надстройки и живая загрузка в SLDWORKS (инцидент автозагрузки)
Write-Host "`n--- 6. Валидность CodeBase надстройки (URI, файл) ---" -ForegroundColor Gray
$inprocPath = "HKCU:\Software\Classes\CLSID\$activeGuid\InprocServer32"
$codeBase = $null
if (Test-Path $inprocPath) {
    $codeBase = (Get-ItemProperty -Path $inprocPath -Name "CodeBase" -ErrorAction SilentlyContinue).CodeBase
}
Assert-Check (-not [string]::IsNullOrEmpty($codeBase)) "CodeBase надстройки присутствует и непуст" "Свойство CodeBase отсутствует или пусто в $inprocPath"

# CodeBase обязан парситься в абсолютный URI (сырая кириллица парсится валидно)
$cbUri = $null
$cbUriValid = $false
if ($codeBase) {
    try {
        $cbUri = [System.Uri]$codeBase
        if ($null -ne $cbUri) { $cbUriValid = $cbUri.IsAbsoluteUri }
    } catch {
        $cbUri = $null
        $cbUriValid = $false
    }
}
Assert-Check $cbUriValid "CodeBase парсится в валидный абсолютный URI ([System.Uri])" "URI не парсится как абсолютный: '$codeBase'"

# Файл надстройки по CodeBase обязан существовать на диске (LocalPath декодирует percent-encoding)
$cbLocalPath = ""
$cbFileExists = $false
if ($cbUri) {
    try {
        $cbLocalPath = $cbUri.LocalPath
        if ($cbLocalPath) { $cbFileExists = Test-Path -LiteralPath $cbLocalPath }
    } catch {
        $cbFileExists = $false
    }
}
Assert-Check $cbFileExists "Файл надстройки по CodeBase существует на диске" "LocalPath: '$cbLocalPath' — файл не найден"

# Эмпирически (2026-09-12) установлено: CLR на этой конфигурации активирует сборку
# ТОЛЬКО по сырой (unescaped) форме CodeBase, которую пишет RegAsm; percent-encoded
# форма с кириллицей автозагрузку ломает. Поэтому проверяем НЕ кодировку, а то,
# что CodeBase не пуст, парсится в URI и указывает на существующий файл (выше).
$cbHasNonAscii = $false
if ($codeBase) {
    $cbHasNonAscii = ($codeBase -match '[^\x00-\x7F]')
}
if ($cbHasNonAscii) {
    Write-Host "  [INFO] CodeBase в сырой (unescaped) форме с кириллицей — рабочая форма для этой конфигурации CLR." -ForegroundColor DarkCyan
}

# Живая проверка загрузки надстройки (только если SLDWORKS запущен).
# Выполняется python-пробой выше ($eskdLiveLoaded): PS Marshal.GetActiveObject падает
# с TYPE_E_ELEMENTNOTFOUND на ранней привязке SldWorks (особенность PowerShell).
$swProc = Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue
if ($swProc) {
    Assert-Check $eskdLiveLoaded "Живая сессия SLDWORKS: надстройка ЕСКД доступна (GetAddInObject)" "SLDWORKS запущен, но надстройка ЕСКД не получена через GetAddInObject"
} else {
    Write-Host "  [INFO] SLDWORKS не запущен: живая проверка надстройки пропущена (счётчики не изменяются)." -ForegroundColor DarkCyan
}

Write-Host "`n======================================================================" -ForegroundColor Cyan
$totalChecks = $passed + $failed
Write-Host "  ИТОГ TIER 4: Успешно: $passed, Провалено: $failed, Предупреждений: $warnings" -ForegroundColor Cyan
Write-Host "  Всего выполнено проверок: $totalChecks" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

if ($failed -eq 0) { exit 0 } else { exit 1 }
