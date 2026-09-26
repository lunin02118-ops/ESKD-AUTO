<#
.SYNOPSIS
    Настройка рабочего места SolidWorks из папки инструментария ЕСКД (сетевая схема).
.DESCRIPTION
    Источник — папка инструментария, из которой запущен сценарий; путь нигде не зашит. Если папку перенесли,
    достаточно запустить установку из нового места.

    Что делает:
      * пути SolidWorks (шаблоны, основные надписи, библиотека материалов, профили, шаблоны свойств) — на источник;
      * макросы SWPlus и надстройку ЕСКД копирует в профиль пользователя (%LOCALAPPDATA%\ESKD\Toolkit): макросы пишут
        настройки рядом с собой, DLL надстройки SolidWorks держит открытой. Настройки макросов при повторной
        установке сохраняются; кнопки SWPlus и надстройка — на локальную копию; в Master.ini — основные надписи источника;
      * корпоративный профиль реестра, 9 кнопок SWPlus, вкладка ЕСКД, фамилия и организация, избранные материалы;
      * шрифты ГОСТ (без прав администратора — в профиль пользователя), модуль Drew;
      * SWTools из общей папки (тихая установка с запросом прав администратора; лицензию активирует конструктор).
    В источник не пишет ничего: папка инструментария может быть только для чтения. Надстройку не собирает — сборку
    кладёт в источник публикация (Publish-EskdToolkit.ps1). Права администратора не нужны.

    Повторный запуск — обновление: файлы выпуска заменяются, настройки макросов пользователя остаются.
    Каждый запуск пишет журнал в %LOCALAPPDATA%\ESKD\Logs (хранятся последние 20).
.PARAMETER Mode
    Install — установка или обновление (по умолчанию). Check — сверка выпуска, установленной версии и локальной копии
    по SHA-256 без изменений; код выхода 0 — актуально, 10 — нужно обновление, 20 — повреждено, 40 — выпуск не
    опубликован или источник не совпадает с хешами (ТЗ-01 Т-18). Uninstall — снять регистрацию надстройки, кнопки SWPlus
    и вкладку ЕСКД, удалить локальную копию и сведения об установке; фамилия и настройки ЕСКД, шрифты, Drew остаются.
.PARAMETER Author
    Фамилия и инициалы для штампа. По умолчанию — из ESKD_Settings, иначе полное имя учётной записи Windows.
.PARAMETER Firm
    Организация для основной надписи. По умолчанию — из ESKD_Settings.
.PARAMETER CloseMode
    Ask — спросить в консоли; Graceful — закрыть SolidWorks через COM без вопроса (согласие получено в окне);
    Force — завершить без сохранения; Skip — не закрывать. -KillSolidWorks и -SkipClose — прежние синонимы.
.PARAMETER NonInteractive
    Без вопросов в консоли: недостающая фамилия — ошибка.
.PARAMETER Graphics
    Auto — аппаратный конвейер графики включается только на дискретной видеокарте NVIDIA или AMD Radeon Pro
    (по умолчанию); Safe — конвейер выключен, предупреждение о программном OpenGL остаётся: этот режим нужен, когда
    после настройки SolidWorks не запускается или окно чёрное; Hardware — включить конвейер на любой видеокарте.
.PARAMETER Language
    Язык интерфейса SolidWorks и Drew: Russian или English. По умолчанию — прежний выбор (ESKD_Install\Language),
    иначе русский. SolidWorks берёт язык из регионального формата пользователя Windows: для русского на ПК с другим
    форматом установщик переключает формат пользователя на «Русский (Россия)» (Set-Culture, без прав администратора),
    если в SolidWorks установлен русский язык. Системную кодовую страницу для макросов SWPlus только проверяет.
    -DrewRussian — прежний синоним -Language Russian. Если русский интерфейс не включится (в SolidWorks нет русского
    языка той же версии, формат не переключился), установка завершается кодом 4: всё остальное настроено, причина —
    в шаге [9/9] и в ESKD_Install\LanguageIssue. Запуск под чужой учётной записью (не той, что вошла в Windows) —
    отказ до изменений, код 1.
.PARAMETER LocalRoot
    Локальная копия (по умолчанию %LOCALAPPDATA%\ESKD\Toolkit).
.PARAMETER RegistryRoot
    Корень HKCU\Software для записи (по умолчанию HKCU:\Software). Автотест передаёт временный раздел
    HKCU:\Software\ESKD_DeployTest_*: тогда HKLM, шрифты, Drew и SWTools не трогаются.
.EXAMPLE
    .\Setup_Workstation_SolidWorks.ps1
.EXAMPLE
    .\Setup_Workstation_SolidWorks.ps1 -Mode Check
.EXAMPLE
    .\Setup_Workstation_SolidWorks.ps1 -Author "Иванов И.И." -Firm "ТОО «Троя»" -CloseMode Graceful -NonInteractive
#>

[CmdletBinding()]
param (
    [ValidateSet("Install", "Check", "Uninstall")][string]$Mode = "Install",
    [string]$Author = "",
    [string]$Firm = "",
    [string]$SwVersion = "",
    [ValidateSet("Ask", "Graceful", "Force", "Skip")][string]$CloseMode = "Ask",
    [switch]$SkipClose,
    [switch]$KillSolidWorks,
    [switch]$NonInteractive,
    [switch]$SkipFonts,
    [switch]$SkipDrew,
    [switch]$SkipSwTools,
    [switch]$SkipProfileReset,
    [switch]$AllowUnpublished,
    [switch]$SwInternetBlock,
    [switch]$DrewRussian,
    [ValidateSet("Russian", "English", "")][string]$Language = "",
    [switch]$Utf8Output,
    [ValidateSet("Auto", "Safe", "Hardware")][string]$Graphics = "Auto",
    [string]$LocalRoot = "",
    [string]$RegistryRoot = "HKCU:\Software"
)

if ($Utf8Output) { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) }
if ($SkipClose) { $CloseMode = "Skip" }
if ($KillSolidWorks) { $CloseMode = "Force" }

$sandbox = $RegistryRoot -ne "HKCU:\Software"
if ($sandbox -and $RegistryRoot -notmatch '^HKCU:\\Software\\ESKD_DeployTest_') {
    Write-Host "[ОШИБКА] Тестовый корень реестра должен быть HKCU:\Software\ESKD_DeployTest_*: $RegistryRoot" -ForegroundColor Red
    exit 1
}
$U = $RegistryRoot

Import-Module (Join-Path $PSScriptRoot "EskdDeploy.psm1") -Force -DisableNameChecking
# Запуск из PowerShell 7 (или из программы, запущенной из него): чужие пути модулей — до первой команды из модулей.
$psModulePathFixed = Reset-EskdPowerShellEnvironment

#region Вспомогательные функции

function Write-Step($text) { Write-Host "`n$text" -ForegroundColor Gray }
function Write-Ok($text) { Write-Host "  [OK] $text" -ForegroundColor Green }
function Write-Info($text) { Write-Host "  [ИНФО] $text" -ForegroundColor DarkGray }
function Write-Warn($text) { Write-Host "  [ВНИМАНИЕ] $text" -ForegroundColor Yellow }
function Write-Fail($text) { Write-Host "  [ОШИБКА] $text" -ForegroundColor Red }

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SolidWorksRegistryVersion {
    # Явно заданная версия («SOLIDWORKS 2024» или «2024»), иначе самая новая в HKCU, иначе SOLIDWORKS 2025.
    param([string]$Requested = "")
    if ($Requested -and $Requested.Trim()) {
        $trimmed = $Requested.Trim()
        if ($trimmed -match '^\d{4}$') { return "SOLIDWORKS $trimmed" }
        return $trimmed
    }
    $bestName = ""; $bestYear = 0
    foreach ($child in @(Get-ChildItem "HKCU:\Software\SolidWorks" -ErrorAction SilentlyContinue)) {
        if ($child.PSChildName -match '^SOLIDWORKS (\d{4})$' -and [int]$Matches[1] -gt $bestYear) {
            $bestYear = [int]$Matches[1]; $bestName = $child.PSChildName
        }
    }
    if ($bestName) { return $bestName }
    return "SOLIDWORKS 2025"
}

function Close-SolidWorks {
    # Ask: подтверждение в консоли; Graceful: без вопроса. Оба — ExitApp через COM, ожидание 30 с, принудительно —
    # последней мерой. Force — сразу принудительно. Skip — не закрывать.
    param([string]$Mode)
    $procs = Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue
    if (-not $procs) { return $true }
    if ($Mode -eq "Skip") { Write-Warn "SolidWorks запущен, закрытие пропущено (-CloseMode Skip)."; return $true }
    if ($Mode -eq "Ask") {
        Write-Warn "SolidWorks запущен. При закрытии несохранённые изменения будут потеряны."
        if ($NonInteractive -or -not [Environment]::UserInteractive) {
            Write-Fail "Закройте SolidWorks и повторите установку."
            return $false
        }
        $answer = Read-Host "  Закрыть SolidWorks сейчас? [Д - да / Н - прервать]"
        if ($answer -notmatch '^(y|yes|д|да)$') { return $false }
    }
    if ($Mode -ne "Force") {
        try {
            $swApp = [Runtime.InteropServices.Marshal]::GetActiveObject('SldWorks.Application')
            if ($swApp) {
                # Несохранённая работа не закрывается: ExitApp её не спасёт, а через таймаут процесс снимается силой.
                $unsaved = @()
                $doc = $swApp.GetFirstDocument()
                while ($doc) {
                    if ($doc.GetSaveFlag()) { $unsaved += [IO.Path]::GetFileName([string]$doc.GetPathName()) }
                    $doc = $doc.GetNext()
                }
                if ($unsaved.Count -gt 0) {
                    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($swApp)
                    Write-Fail ("В SolidWorks есть несохранённые документы: " + (($unsaved | ForEach-Object { if ($_) { $_ } else { "(новый, без имени)" } }) -join ", ") +
                                ". Сохраните или закройте их и повторите установку.")
                    return $false
                }
                [void]$swApp.ExitApp()
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($swApp)
                Write-Info "SolidWorks закрывается (до 2 минут: большая сборка закрывается долго)..."
            }
        } catch {
            Write-Warn "SolidWorks не отвечает по COM: $($_.Exception.Message)"
        }
        $deadline = (Get-Date).AddSeconds(120)
        while ((Get-Date) -lt $deadline -and (Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
        if (Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue) {
            # Не закрылся сам — силой не снимаем: он может дописывать файл на NAS. Снятие — только -CloseMode Force.
            Write-Fail "SolidWorks не закрылся за 2 минуты. Закройте его вручную и повторите установку."
            return $false
        }
    }
    if (Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue) {
        Stop-Process -Name "SLDWORKS" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    if (Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue) {
        Write-Fail "Не удалось закрыть SolidWorks."
        return $false
    }
    Write-Ok "SolidWorks закрыт."
    return $true
}

function Backup-SolidWorksRegistryKeys {
    # Резервная копия веток реестра перед импортом профиля — в профиль пользователя; хранятся последние 5.
    param([string]$BackupRoot, [string[]]$RegistryKeys)
    $backupDir = Join-Path $BackupRoot ("Backup_" + (Get-Date -Format "yyyyMMdd_HHmmss"))
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    # Возвращает пути ветвей, для которых копия не создана. Ключи — в форме HKEY_CURRENT_USER\…: прежняя проверка
    # переводила только «HKCU\…», поэтому ни одна ветка не копировалась (аудит 15.09.2026).
    $failed = @()
    foreach ($key in $RegistryKeys) {
        if (-not (Test-Path -LiteralPath ("Registry::" + $key))) { continue }
        $outFile = Join-Path $backupDir ((($key -replace '[\\/:*?"<>|]', '_')) + ".reg")
        $null = & reg.exe export "$key" "$outFile" /y 2>&1
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $outFile) -and (Get-Item -LiteralPath $outFile).Length -gt 0) {
            Write-Ok "Резервная копия: $outFile"
        } else {
            $failed += $key
            Write-Warn "Резервная копия ветки '$key' не создана (код $LASTEXITCODE)."
        }
    }
    Get-ChildItem -LiteralPath $BackupRoot -Directory -Filter "Backup_*" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -Skip 5 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    return ,$failed
}

function Set-Reg($Path, $Name, $Value, $Type = "String") {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    if ($Type -eq "DWord") { Set-ItemProperty -LiteralPath $Path -Name $Name -Value ([int]$Value) -Type DWord }
    else { Set-ItemProperty -LiteralPath $Path -Name $Name -Value ([string]$Value) }
}

function Get-RegValue($Path, $Name) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $item = Get-ItemProperty -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $item -or $null -eq $item.PSObject.Properties[$Name]) { return $null }
    return $item.PSObject.Properties[$Name].Value
}

#endregion

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " НАСТРОЙКА РАБОЧЕГО МЕСТА SOLIDWORKS — ИНСТРУМЕНТАРИЙ ЕСКД" -ForegroundColor Yellow
Write-Host "=================================================================" -ForegroundColor Cyan

# 0. Источник и локальная копия
$SourceRoot = Find-EskdSourceRoot -StartPath $PSScriptRoot
if (-not $SourceRoot) {
    Write-Fail "Сценарий запущен не из папки инструментария (нет 02_Шаблоны_и_Форматки, 03_Макросы_и_Плагины, 04_Библиотеки_Материалов_и_Профилей рядом с ним): $PSScriptRoot"
    exit 2
}
if (-not $LocalRoot) { $LocalRoot = Get-EskdDefaultLocalRoot }
# Журнал запуска: рядом с локальной копией (%LOCALAPPDATA%\ESKD\Logs), последние 20 — администратору для разбора.
$logDir = Join-Path (Split-Path -Path $LocalRoot -Parent) "Logs"
$logPath = Join-Path $logDir ("{0}_{1}.log" -f $Mode.ToLowerInvariant(), (Get-Date -Format "yyyyMMdd_HHmmss"))
try {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    Start-Transcript -LiteralPath $logPath -Force | Out-Null
    Get-ChildItem -LiteralPath $logDir -Filter "*.log" | Sort-Object LastWriteTime -Descending | Select-Object -Skip 20 |
        Remove-Item -Force -ErrorAction SilentlyContinue
} catch {
    Write-Host "[ВНИМАНИЕ] Журнал не ведётся: $($_.Exception.Message)" -ForegroundColor Yellow
    $logPath = ""
}
$layout = Get-EskdLayout -SourceRoot $SourceRoot -LocalRoot $LocalRoot
$release = Get-EskdRelease -Path $layout.Release
$SwVersion = Get-SolidWorksRegistryVersion -Requested $SwVersion
$swRoot = "$U\SolidWorks\$SwVersion"
$isAdmin = Test-IsAdmin
$machine = $isAdmin -and -not $sandbox

Write-Host "Папка инструментария: $SourceRoot" -ForegroundColor White
Write-Host "Выпуск:               $($release.Version) $($release.Date)" -ForegroundColor White
Write-Host "Локальная копия:      $LocalRoot" -ForegroundColor White
Write-Host "SolidWorks:           $SwVersion" -ForegroundColor White
if ($sandbox) { Write-Host "Тестовый корень реестра: $U" -ForegroundColor Magenta }
if ($logPath) { Write-Host "Журнал:               $logPath" -ForegroundColor White }
if ($psModulePathFixed) { Write-Info "Запуск из PowerShell 7: его пути модулей убраны из окружения установщика." }
$install = "$U\SolidWorks\ESKD_Install"

if ($Mode -eq "Check") {
    Write-Step "Проверка установки (ничего не меняется)..."
    $check = Test-EskdInstall -Layout $layout -InstalledVersion ([string](Get-RegValue $install "ReleaseVersion"))
    foreach ($line in $check.Problems | Select-Object -First 30) { Write-Info $line }
    if ($check.Problems.Count -gt 30) { Write-Info "… и ещё $($check.Problems.Count - 30)." }
    if ($check.Code -eq 0) { Write-Ok "Актуально: выпуск $($release.Version), локальная копия совпадает с выпуском." }
    else { Write-Warn "$($check.State) (код $($check.Code))." }
    # Отучение от сети (если его ставили): после обновления SolidWorks появляются новые программы — их видно здесь.
    # Код выхода не меняется: он про выпуск инструментария; следующая установка с галочкой догоняет правила сама.
    $sbCheck = Join-Path (Split-Path -Path $PSScriptRoot -Parent) "SwInternetBlock\Set-SwInternetBlock.ps1"
    if ([string](Get-RegValue $install "SwInternetBlock") -eq "1" -and -not $sandbox -and (Test-Path -LiteralPath $sbCheck)) {
        $sbAudit = Invoke-EskdSwBlock -Script $sbCheck -Mode audit
        if ($sbAudit.Code -eq 0) {
            foreach ($l in $sbAudit.Lines | Where-Object { $_ -match 'ВНИМАНИЕ' }) { Write-Info "  $($l.Trim())" }
            Write-Ok $(if ($sbAudit.Verdict) { $sbAudit.Verdict -replace '^ИТОГ:\s*', '' } else { "SolidWorks отучен от интернета." })
        } else {
            foreach ($l in $sbAudit.Lines | Select-Object -First 25) { Write-Info "  $l" }
            if ($sbAudit.Code -eq 4) { Write-Warn "Правила отучения от сети на месте, но не действуют или обходятся (сообщения выше): SolidWorks может выходить в интернет." }
            else { Write-Warn "Отучение SolidWorks от сети неполное (код $($sbAudit.Code)): запустите установку с галочкой «Отучение SolidWorks от сети»." }
        }
    }
    exit $check.Code
}

# Без toolkit_release.json ставится только клон репозитория (разработчик) или тестовый корень реестра. В общей папке его
# нет в одном случае — идёт публикация (Publish снимает выпуск на время копирования): ставить сейчас — получить
# половину новых файлов и половину старых без сверки по хешам.
if ($Mode -eq "Install" -and $release.Version -eq "рабочая копия" -and -not $sandbox -and -not $AllowUnpublished -and
    -not (Test-Path -LiteralPath (Join-Path $SourceRoot ".git"))) {
    Write-Fail "Выпуск инструментария не опубликован или публикуется прямо сейчас (нет toolkit_release.json в $SourceRoot). Повторите через несколько минут; не проходит — сообщите администратору."
    exit 2
}

if ($Mode -eq "Uninstall") {
    Write-Step "Удаление инструментария ЕСКД с рабочего места..."
    if (-not (Close-SolidWorks -Mode $CloseMode)) {
        Write-Fail "Удаление прервано: SolidWorks не закрыт. Изменения не вносились."
        exit 3
    }
    try {
        . (Join-Path $layout.SourceAddin "Register-EskdAddin.ps1")
        Unregister-EskdAddin -UserRoot $U -MachineRoot "HKLM:\Software" -SystemWide:((Test-IsAdmin) -and -not $sandbox)
        Write-Ok "Регистрация надстройки ЕСКД снята."
    } catch {
        Write-Fail "Регистрация надстройки: $($_.Exception.Message)"
    }
    $swRootU = "$U\SolidWorks\$SwVersion"
    $qatU = "$swRootU\User Interface\CommandManager\QAT\GB0"
    # Кнопки SWPlus Btn11..Btn19 = 1,33639..1,33647 (шаг [5/9]); базовые кнопки SolidWorks и кнопки пользователя остаются.
    for ($i = 11; $i -le 19; $i++) {
        if ([string](Get-RegValue $qatU "Btn$i") -eq ("1,{0}" -f (33628 + $i))) { Remove-ItemProperty -LiteralPath $qatU -Name "Btn$i" -ErrorAction SilentlyContinue }
    }
    for ($cid = 33639; $cid -le 33647; $cid++) { Remove-ItemProperty -LiteralPath "$swRootU\Menu Customizations" -Name "$cid" -ErrorAction SilentlyContinue }
    foreach ($macro in @(Get-ChildItem -LiteralPath "$swRootU\User Defined Macros" -ErrorAction SilentlyContinue)) {
        $macroPath = [string](Get-RegValue $macro.PSPath "Source Path")
        if ($macroPath -and $macroPath.StartsWith($LocalRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $macro.PSPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Ok "Кнопки SWPlus убраны."
    foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
        foreach ($tab in @(Get-ChildItem -LiteralPath "$swRootU\User Interface\CommandManager\$ctx" -ErrorAction SilentlyContinue)) {
            if ([string](Get-RegValue $tab.PSPath "RefName") -eq "ЕСКД" -or
                [string](Get-RegValue $tab.PSPath "ModuleName") -eq "{B64E6875-B101-4D5C-B245-FF8D50772E25}") {
                Remove-Item -LiteralPath $tab.PSPath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    Write-Ok "Вкладка ЕСКД убрана."
    # Локальная копия удаляется, только если это она: в папке лежит надстройка или SWPlus (защита от ошибочного -LocalRoot).
    if ((Test-Path -LiteralPath $layout.LocalAddinDll) -or (Test-Path -LiteralPath $layout.LocalSwPlus)) {
        Remove-Item -LiteralPath $LocalRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $LocalRoot) { Write-Warn "Локальная копия удалена не полностью (файлы заняты): $LocalRoot" }
        else { Write-Ok "Локальная копия удалена: $LocalRoot" }
    } elseif (Test-Path -LiteralPath $LocalRoot) {
        Write-Warn "Папка $LocalRoot не похожа на локальную копию ЕСКД — не удалена."
    }
    # Отпечаток установщика Drew остаётся: Drew не удаляется, и следующая установка не должна переставлять его заново.
    $keepDrewInstaller = Get-RegValue $install "DrewInstaller"
    Remove-Item -LiteralPath $install -Recurse -Force -ErrorAction SilentlyContinue
    if ($keepDrewInstaller) { Set-Reg $install "DrewInstaller" $keepDrewInstaller }
    Write-Ok "Сведения об установке удалены. Фамилия и настройки ЕСКД, шрифты, Drew оставлены."
    exit 0
}

# Фамилия и организация
$settingsKey = "$U\SolidWorks\ESKD_Settings"
if (-not $Author) { $Author = [string](Get-RegValue $settingsKey "Author") }
if (-not $Author -and -not $sandbox) {
    try {
        $account = Get-CimInstance Win32_UserAccount -Filter "Name='$env:USERNAME'" -ErrorAction SilentlyContinue
        if ($account -and $account.FullName) { $Author = $account.FullName }
    } catch { }
}
if (-not $Author -and -not $NonInteractive -and [Environment]::UserInteractive) {
    $Author = Read-Host "Фамилия и инициалы конструктора для штампа (например, Иванов И.И.)"
}
$Author = "$Author".Trim()
if (-not $Author) {
    Write-Fail "Не указана фамилия конструктора (-Author)."
    exit 1
}
if (-not $Firm) { $Firm = [string](Get-RegValue $settingsKey "Organization") }
if (-not $Firm -and -not $NonInteractive -and [Environment]::UserInteractive) {
    $Firm = Read-Host "Организация для основной надписи (например, ТОО «Троя»)"
}
$Firm = "$Firm".Trim()
if (-not $Firm) {
    # Общий список организаций MProp пуст; без своей организации в локальном списке MProp падает на модели без «Конторы».
    Write-Fail "Не указана организация (-Firm)."
    exit 1
}
# Язык интерфейса: ключ, иначе прежний выбор, иначе русский (решение владельца 25.09.2026). В переменную с ValidateSet —
# только допустимые значения.
if (-not $Language -and $DrewRussian) { $Language = "Russian" }
if (-not $Language) { $Language = if ([string](Get-RegValue $install "Language") -eq "English") { "English" } else { "Russian" } }
Write-Host "Конструктор:          $Author" -ForegroundColor White
Write-Host "Организация:          $Firm" -ForegroundColor White
Write-Host "Язык интерфейса:      $(if ($Language -eq 'English') { 'английский' } else { 'русский' })" -ForegroundColor White

$failures = 0
# Для русского интерфейса, который не удалось включить, — причина: итог тогда не «готово», а код выхода 4 (разбор 25.09.2026:
# шаг [9/9] писал [ВНИМАНИЕ] в середине журнала, а окно — зелёное «Готово»).
$languageIssue = ""

# Чужая учётная запись («Запуск от имени администратора» с паролем ИТ): всё ниже ушло бы в её HKCU и формат Windows, а
# SolidWorks конструктора, который вошёл в Windows, остался бы прежним (разбор 25.09.2026). Модуль не прочитался — отказ,
# а не установка без проверки.
try {
    . (Join-Path $layout.SourceAddin "Register-EskdAddin.ps1")
    $foreignAccount = Get-EskdForeignAccountMessage -Action Setup
} catch {
    Write-Fail "Папка инструментария неполная (модуль регистрации надстройки): $($_.Exception.Message). Изменения не вносились — сообщите администратору."
    exit 2
}
if ($foreignAccount) {
    Write-Fail $foreignAccount
    exit 1
}

# 1. SolidWorks
Write-Step "[1/9] Проверка SolidWorks..."
if (-not (Close-SolidWorks -Mode $CloseMode)) {
    Write-Fail "Установка прервана: SolidWorks не закрыт. Изменения не вносились."
    exit 3
}

# 2. Локальная копия макросов SWPlus и надстройки
Write-Step "[2/9] Копирование макросов SWPlus и надстройки ЕСКД в профиль пользователя..."
try {
    $copy = Copy-EskdLocalInstance -Layout $layout
    Write-Ok ("Локальная копия: обновлено файлов {0}, без изменений {1}, настройки пользователя сохранены {2}." -f $copy.Copied.Count, $copy.Same, $copy.Kept.Count)
    # Сверка копии с хешами выпуска (ТЗ-01 Т-28): файл, испорченный при копировании по сети, не должен остаться незамеченным.
    $releaseCheck = Test-EskdInstall -Layout $layout -InstalledVersion $release.Version
    if ($releaseCheck.Code -eq 0) { Write-Ok "Локальная копия совпадает с выпуском $($release.Version) по SHA-256." }
    elseif ($releaseCheck.Code -eq 40 -and $release.Version -eq "рабочая копия") { Write-Info "Выпуск не опубликован (рабочая копия) — сверка по хешам пропущена." }
    else {
        $failures++
        Write-Fail "$($releaseCheck.State): $(@($releaseCheck.Problems | Select-Object -First 5) -join '; ')"
    }
} catch {
    Write-Fail $_.Exception.Message
    exit 2
}
if (Set-EskdMasterIniFormats -MasterIni (Join-Path $layout.LocalSwPlus "Master\Master.ini") -SheetFormats $layout.SheetFormats) {
    Write-Ok "Master.ini: основные надписи — $($layout.SheetFormats)"
}
$mprop = Join-Path $layout.LocalSwPlus "MProp"
if (Add-SwPlusFamily -Path (Join-Path $mprop "MProp_Fam.txt") -Name $Author) {
    Write-Info "Фамилия «$Author» добавлена в список MProp этого пользователя."
}
if (Add-SwPlusFirm -Path (Join-Path $mprop "MProp_Firm.txt") -Name $Firm) {
    Write-Info "Организация «$Firm» добавлена в список MProp этого пользователя."
}

# 3. Профиль реестра
Write-Step "[3/9] Корпоративный профиль SolidWorks..."
$regKeyUser = "HKEY_CURRENT_USER\" + $U.Substring("HKCU:\".Length)
$backupRoot = Join-Path (Split-Path -Path $LocalRoot -Parent) "Backups"
$backupFailed = Backup-SolidWorksRegistryKeys -BackupRoot $backupRoot -RegistryKeys @("$regKeyUser\SolidWorks\$SwVersion", "$regKeyUser\SolidWorks\AddInsStartup")
# Сброс к стандартным перед профилем: результат установки не зависит от прежних настроек ПК (решение владельца 15.09.2026).
# Без удачной резервной копии раздела версии сброс не выполняется — настройки удаляются только с возможностью вернуть.
# При указании -SkipProfileReset сброс пропускается, чтобы сохранить индивидуальные настройки конструктора.
if (-not $SkipProfileReset) {
    try {
        if ($backupFailed -contains "$regKeyUser\SolidWorks\$SwVersion") { throw "нет резервной копии $regKeyUser\SolidWorks\$SwVersion — сброс отменён" }
        $reset = Reset-EskdSolidWorksProfile -UserRoot $U -SwVersion $SwVersion
        if ($reset.Existed) {
            Write-Ok ("Настройки $SwVersion сброшены к стандартным; сохранены: " + $(if ($reset.Preserved) { $reset.Preserved -join ", " } else { "нечего" }) +
                      ". Прежние — в $backupRoot")
        } else {
            Write-Info "Настроек $SwVersion у пользователя ещё нет — сбрасывать нечего."
        }
    } catch {
        $failures++
        Write-Fail "Сброс настроек SolidWorks: $($_.Exception.Message)"
    }
} else {
    Write-Info "Сброс профиля SolidWorks пропущен (-SkipProfileReset)."
}
if (Test-Path -LiteralPath $layout.RegProfile) {
    $regText = [System.IO.File]::ReadAllText($layout.RegProfile, [System.Text.Encoding]::Unicode)
    $adapted = Convert-EskdRegProfile -Text $regText -SourceRoot $SourceRoot -LocalRoot $LocalRoot -SwVersion $SwVersion `
        -KeepMachineSections:$machine -RegistryRoot $U
    $foreign = @(Find-EskdForeignPaths -Text $adapted -Allowed @($SourceRoot, $LocalRoot))
    if ($foreign.Count) {
        $failures++
        Write-Fail "В профиле реестра пути чужого компьютера: $($foreign -join '; '). Профиль импортирован, но эти пути на этом ПК не работают — переэкспортируйте .reg без них."
    }
    $tempReg = Join-Path ([System.IO.Path]::GetTempPath()) ("eskd_profile_" + [guid]::NewGuid().ToString("N") + ".reg")
    [System.IO.File]::WriteAllText($tempReg, $adapted, [System.Text.Encoding]::Unicode)
    $importOutput = @(& reg.exe import "$tempReg" 2>&1)
    $code = $LASTEXITCODE
    Remove-Item -LiteralPath $tempReg -Force -ErrorAction SilentlyContinue
    if ($code -eq 0) {
        Write-Ok "Профиль импортирован: шаблоны и библиотеки — папка инструментария, макросы — локальная копия."
    } else {
        $failures++
        Write-Fail "Импорт профиля завершился с кодом $code. $((@($importOutput) -join ' ').Trim())"
    }
} else {
    $failures++
    Write-Fail "Нет профиля реестра: $($layout.RegProfile)"
}

# Пути, которых нет в профиле или которые должны быть заданы наверняка
foreach ($folderKey in @("$swRoot\ExtReferences", "$swRoot\ExtFolder")) {
    Set-Reg $folderKey "Custom Property Folders" $layout.PropertyTemplates
    Set-Reg $folderKey "Custom Property File" (Join-Path $layout.PropertyTemplates "default.prtprp")
    Set-Reg $folderKey "Material Database Folders" $layout.MaterialFolder
}
Write-Ok "Шаблоны свойств и библиотека материалов: $SourceRoot"

# Toolbox — только если найден рядом с инструментарием или в стандартной папке и у пользователя пути ещё нет
# (Р-4: Toolbox не используется); иначе прежнее значение не трогается
# Выпуск лежит либо рядом с папкой «_Библиотека проектирования» (локальная схема), либо внутри неё
# (сетевая схема: ...\_Библиотека проектирования\_инструменты_конструктора) — проверяем оба варианта.
$sourceParent = Split-Path -Path $SourceRoot -Parent
$toolboxCandidates = @(
    (Join-Path $sourceParent "_Toolbox"),
    (Join-Path $sourceParent "_Библиотека проектирования\_Toolbox")
)
if (-not $sandbox) { $toolboxCandidates += @("C:\SOLIDWORKS Data", "C:\SOLIDWORKS Data 2025") }
$currentToolbox = [string](Get-RegValue "$swRoot\General" "Toolbox Data Location")
$toolbox = $toolboxCandidates | Where-Object { (Test-Path (Join-Path $_ "lang\russian\swbrowser.sldedb")) -or (Test-Path (Join-Path $_ "lang\english\swbrowser.sldedb")) } |
    Select-Object -First 1
if (-not $currentToolbox -and $toolbox) {
    Set-Reg "$swRoot\General" "Toolbox Data Location" $toolbox
    if ($machine) { Set-ItemProperty -Path "HKLM:\SOFTWARE\SolidWorks\$SwVersion\General" -Name "Toolbox Data Location" -Value $toolbox -ErrorAction SilentlyContinue }
    Write-Ok "Toolbox: $toolbox"
} elseif ($currentToolbox) {
    Write-Info "Toolbox пользователя сохранён: $currentToolbox"
} else {
    Write-Info "Toolbox рядом с папкой инструментария не найден — путь Toolbox не меняется."
}
# Библиотека проектирования — крепёж и фурнитура с NAS (замечание владельца 18.09.2026). В «Расположении файлов»
# это пункт «Библиотека проектирования»: API swFileLocationsDesignLibrary (38), в реестре — «Content Manager Folders».
# Ищется так же, как Toolbox: рядом с инструментарием или внутри «_Библиотека проектирования».
$designLibrary = @(
    (Join-Path $sourceParent "_ крепеж и фурнитура"),
    (Join-Path $sourceParent "_Библиотека проектирования\_ крепеж и фурнитура")
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($designLibrary) {
    foreach ($folderKey in @("$swRoot\ExtReferences", "$swRoot\ExtFolder")) {
        Set-Reg $folderKey "Content Manager Folders" $designLibrary
    }
    Write-Ok "Библиотека проектирования: $designLibrary"
} else {
    Write-Info "Папка «_ крепеж и фурнитура» рядом с инструментарием не найдена — библиотека проектирования не меняется."
}
# Папки поиска ссылочных документов (ТЗ-02 Т-57): от места запуска — стандартные изделия (02_БАЗА), крепёж и фурнитура,
# профили сварных деталей инструментария; поиск по папкам включён. Режим совместной работы (Т-56) — в профиле, раздел Collab.
$referenceFolders = @(
    (Join-Path $sourceParent "_стандартные изделия"),
    (Join-Path $sourceParent "_Библиотека проектирования\_стандартные изделия"),
    $designLibrary,
    (Join-Path $SourceRoot "04_Библиотеки_Материалов_и_Профилей\Профили сварных деталей")
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
if ($referenceFolders) {
    foreach ($folderKey in @("$swRoot\ExtReferences", "$swRoot\ExtFolder")) {
        Set-Reg $folderKey "Document Folders" (@($referenceFolders) -join ";")
        Set-Reg $folderKey "Use Search Rules" 1 "DWord"
    }
    Write-Ok "Папки поиска ссылок: $(@($referenceFolders) -join '; ')"
}
# Графика. Аппаратный конвейер SolidWorks 2025 работает не на всякой видеокарте: на встроенной Intel/AMD, на старом
# драйвере и в виртуальной машине SolidWorks с ним не запускается вовсе, а отключённый программный OpenGL не даёт
# ему подняться запасным путём (замечание владельца 20.09.2026 — на другом ПК SolidWorks не стартовал после настройки).
# Поэтому конвейер включается только на дискретной видеокарте NVIDIA или AMD Radeon Pro; «-Graphics Safe» выключает его
# на любой машине (лечение уже настроенного ПК), «-Graphics Hardware» включает принудительно.
$videoCards = @()
try { $videoCards = @(Get-CimInstance Win32_VideoController -ErrorAction Stop) }
catch { Write-Info "Видеокарту определить не удалось — графика настраивается в безопасном режиме." }
$nvidia = @($videoCards | Where-Object { $_.Name -and ("$($_.AdapterCompatibility) $($_.Name)" -match 'NVIDIA|GeForce|Quadro|RTX') } |
    ForEach-Object { $_.Name })
$proGpu = @($videoCards | Where-Object { $_.Name -and ("$($_.AdapterCompatibility) $($_.Name)" -match 'Radeon Pro|FirePro') })
$hardwareGraphics = switch ($Graphics) {
    "Hardware" { $true }
    "Safe"     { $false }
    default    { [bool]($nvidia.Count -or $proGpu.Count) }
}
$cardNames = if ($videoCards.Count) { (@($videoCards | ForEach-Object { $_.Name }) -join ", ") } else { "не определена" }
if ($hardwareGraphics) {
    Set-Reg "$swRoot\Performance" "Use Performance Pipeline 2020" 1 "DWord"
    Set-Reg "$swRoot\Performance" "Use GPU Silhouette Edges" 1 "DWord"
    Set-Reg "$swRoot\General" "Software OGL Alarm" 0 "DWord"
    Set-Reg "$swRoot\General" "Use Software OGL" 0 "DWord"
    Write-Ok "Графика ($cardNames): аппаратный конвейер и кромки силуэта включены."
} else {
    # Значения по умолчанию SolidWorks: конвейер выключен, программный OpenGL разрешён и о нём предупреждают.
    Set-Reg "$swRoot\Performance" "Use Performance Pipeline 2020" 0 "DWord"
    Set-Reg "$swRoot\Performance" "Use GPU Silhouette Edges" 0 "DWord"
    Set-Reg "$swRoot\General" "Software OGL Alarm" 1 "DWord"
    Set-Reg "$swRoot\General" "Use Software OGL" 0 "DWord"
    Write-Ok "Графика ($cardNames): аппаратный конвейер не включается — SolidWorks выберет режим сам."
}
# «Saved OGL Settings» — возможности OpenGL, которые SolidWorks запомнил при прошлом запуске. Раздел Performance переживает
# сброс профиля, и если хоть один запуск прошёл без аппаратного OpenGL (0x02110211 вместо 0x021102F7 на RTX 2080 Ti),
# SolidWorks держит «Использовать программу OpenGL» серой и отмеченной, а «Повышенную производительность» — серой
# (замечание владельца 21.09.2026: вернулось только перезагрузкой ПК). Значение снимается: SolidWorks определит видеокарту
# заново при следующем запуске, как на чистом профиле, — своё значение мы не пишем (история с маской AllowList).
Remove-ItemProperty -LiteralPath "$swRoot\Performance" -Name "Saved OGL Settings" -ErrorAction SilentlyContinue

# AllowList — собственная база видеокарт SolidWorks: при первом запуске он пишет в HKCU около 1200 разделов
# (Gl2Shaders\NV25…NV40\<карта>\<версия драйвера>, разделы вендоров, DATE — версия базы). По ней он решает, можно ли
# рисовать на видеокарте. Раньше установщик вырезал ветки Gl2Shaders и NVIDIA Corporation целиком: база оставалась
# без NVIDIA, но с DATE — SolidWorks считал её целой и не восстанавливал, а для карты без записи брал осторожную маску
# (Current\Workarounds = 16) и включал программный OpenGL: «Использовать программу OpenGL» серая и отмеченная,
# «Повышенная производительность» серая (замечания владельца 21–22.09.2026; кэш OpenGL NVIDIA не обновлялся с 21.09
# 16:08 — с установки 16:40). Своя маска в неполной базе роняла SolidWorks при старте (0xC0000005, 20.09 и 22.09).
# Правило: база не правится по частям. Нет ветки Gl2Shaders — база повреждена, раздел AllowList снимается целиком,
# и SolidWorks при следующем запуске записывает её заново, полную и под свою версию. Целая база не трогается.
$allowRoot = $U.Substring("HKCU:\".Length) + "\SolidWorks\AllowList"  # в песочнице автотеста — её раздел
$cu = [Microsoft.Win32.Registry]::CurrentUser
$allowKey = $cu.OpenSubKey($allowRoot)
if ($allowKey -ne $null) {
    $complete = @($allowKey.GetSubKeyNames()) -contains "Gl2Shaders"
    $allowKey.Close()
    if ($complete) {
        Write-Info "AllowList: база видеокарт SolidWorks целая — не трогается."
    } else {
        $cu.DeleteSubKeyTree($allowRoot, $false)
        Write-Ok "AllowList: база видеокарт SolidWorks была неполной — снята, SolidWorks запишет её заново при запуске."
    }
}

# 4. Очистка устаревших надстроек и вкладок
Write-Step "[4/9] Очистка устаревших надстроек и вкладок..."
$unwanted = @("{03412ba8-10f6-4d51-ac38-4937ce7bea5f}", "{7a2f5c31-9e44-4b0d-8c21-5f0e9a4b77c2}",
              "{B64E6875-B101-4D5C-B245-FF8D50772E21}", "{B64E6875-B101-4D5C-B245-FF8D50772E23}", "{B64E6875-B101-4D5C-B245-FF8D50772E24}")
foreach ($g in $unwanted) {
    foreach ($p in @("$U\SolidWorks\AddIns\$g", "$U\SolidWorks\AddInsStartup\$g", "$U\SolidWorks\AddInsEntitlement\$g", "$U\Classes\CLSID\$g")) {
        Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($machine) {
        foreach ($p in @("HKLM:\SOFTWARE\SolidWorks\AddIns\$g", "HKLM:\SOFTWARE\SolidWorks\AddInsStartup\$g", "HKLM:\SOFTWARE\Classes\CLSID\$g")) {
            Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
$activeGuid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
$swToolsGuid = "{59959DFA-3229-4B86-852E-52ABF2BDB8C0}"
$drewGuid = "{08C4BC0B-C36C-470E-A0EA-02232F023333}"
foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
    $ctxPath = "$swRoot\User Interface\CommandManager\$ctx"
    if (-not (Test-Path -LiteralPath $ctxPath)) { continue }
    foreach ($gb in @("AssyContext", "DrwContext", "EditPartContext", "LAVContext", "PartContext", "QAT")) {
        Remove-Item -LiteralPath (Join-Path $ctxPath $gb) -Recurse -Force -ErrorAction SilentlyContinue
    }
    $seen = @{}
    $tabs = @(Get-ChildItem -LiteralPath $ctxPath -ErrorAction SilentlyContinue) | Sort-Object {
        if ($_.PSChildName -match '^Tab(\d+)$') { [int]$Matches[1] } else { 9999 }
    }
    foreach ($tab in $tabs) {
        $ref = [string](Get-RegValue $tab.PSPath "RefName")
        $props = [string](Get-RegValue $tab.PSPath "Tab Props")
        $mod = [string](Get-RegValue $tab.PSPath "ModuleName")
        $garbage = ($ref -match "OnCad|Ounan|Semantic") -or ($props -match "OnCad|Ounan|Semantic") -or ($mod -match "03412ba8|7A2F5C31|FF8D50772E2[134]") -or
            ($tab.PSChildName -like "Tab*" -and -not $ref.Trim() -and (-not $props.Trim() -or $props.StartsWith("0,")))
        if ($garbage) { Remove-Item -LiteralPath $tab.PSPath -Recurse -Force -ErrorAction SilentlyContinue; continue }

        $dedupKey = $null
        if ($ref -match "ЕСКД" -or $mod -eq $activeGuid) {
            $dedupKey = "ЕСКД"
        } elseif ($ref -match "SWTools" -or $mod -eq $swToolsGuid) {
            $dedupKey = "SWTools"
        } elseif ($ref -match "Drew" -or $mod -eq $drewGuid) {
            $dedupKey = "Drew"
        } elseif ($ref.Trim() -ne "") {
            $dedupKey = $ref.Trim()
        }

        if ($dedupKey) {
            if ($seen.ContainsKey($dedupKey)) {
                Remove-Item -LiteralPath $tab.PSPath -Recurse -Force -ErrorAction SilentlyContinue
                continue
            }
            $seen[$dedupKey] = $true
        }

        if ($dedupKey -eq "ЕСКД") {
            Set-Reg $tab.PSPath "RefName" "ЕСКД"
            Set-Reg $tab.PSPath "ModuleName" $activeGuid
            Set-Reg $tab.PSPath "Tab Props" "ЕСКД,1,1,-1"
        }
    }
}
foreach ($flyout in @(Get-ChildItem -LiteralPath "$swRoot\User Interface\Custom API Flyouts" -ErrorAction SilentlyContinue)) {
    if ([string](Get-RegValue $flyout.PSPath "ModuleName") -match "03412ba8|7A2F5C31|B64E6875") {
        Remove-Item -LiteralPath $flyout.PSPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Remove-ItemProperty -LiteralPath "$swRoot\User Interface\Toolbars" -Name "ToolbarChangesOnUpgrade" -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "$swRoot\User Interface\Toolbars\ToolbarChangesOnUpgrade" -Recurse -Force -ErrorAction SilentlyContinue
foreach ($name in @("OnCadTools", "Semantic", "Semantic MDM")) {
    Remove-ItemProperty -LiteralPath "$swRoot\General\Addin Performance" -Name $name -ErrorAction SilentlyContinue
}
Write-Ok "Устаревшие надстройки и пустые вкладки убраны."

# 5. Кнопки SWPlus в панели быстрого доступа
Write-Step "[5/9] Кнопки SWPlus..."
$qat = "$swRoot\User Interface\CommandManager\QAT\GB0"
# Базовые кнопки QAT SolidWorks (0..10): при их отсутствии SolidWorks считает панель невалидной и сбрасывает её
$defaultQat = [ordered]@{
    "Btn0"  = "1,21781"; "Btn1"  = "1,54312"; "Btn2"  = "1,54416"; "Btn3"  = "1,54302"; "Btn4"  = "1,54303";
    "Btn5"  = "1,57643"; "Btn6"  = "1,57644"; "Btn7"  = "1,34128"; "Btn8"  = "1,32805"; "Btn9"  = "1,33040"; "Btn10" = "1,54325"
}
foreach ($b in $defaultQat.Keys) {
    if (-not (Get-RegValue $qat $b)) { Set-Reg $qat $b $defaultQat[$b] }
}
$buttons = [ordered]@{ "Btn11" = "1,33639"; "Btn12" = "1,33640"; "Btn13" = "1,33641"; "Btn14" = "1,33642"; "Btn15" = "1,33643";
                       "Btn16" = "1,33644"; "Btn17" = "1,33645"; "Btn18" = "1,33646"; "Btn19" = "1,33647" }
foreach ($b in $buttons.Keys) { Set-Reg $qat $b $buttons[$b] }
for ($cid = 33639; $cid -le 33647; $cid++) { Set-Reg "$swRoot\Menu Customizations" "$cid" 0 "DWord" }
Write-Ok "9 кнопок SWPlus: MProp, SProp, DProp, SpecEditor, RecordDimM, Roughness, TT, Master, SaveAsPDF."

# 6. Надстройка ЕСКД
Write-Step "[6/9] Надстройка ЕСКД..."
$registration = $null
try {
    . (Join-Path $layout.SourceAddin "Register-EskdAddin.ps1")
    # С правами администратора — и в HKLM. Процессы администратора при отключённом UAC (EnableLUA = 0) и SolidWorks,
    # запущенный от имени администратора, не видят регистрацию COM из HKCU: без HKLM вкладка ЕСКД не загрузится.
    $registration = Register-EskdAddin -DllPath $layout.LocalAddinDll -UserRoot $U -MachineRoot "HKLM:\Software" -SystemWide:$machine
    if ($registration.UserDllExists -and $registration.UserAddIn -and $registration.UserStartup) {
        Write-Ok "Надстройка зарегистрирована из локальной копии: $($layout.LocalAddinDll)"
    } else {
        $failures++
        Write-Fail "Регистрация надстройки неполная: $($registration | ConvertTo-Json -Compress)"
    }
} catch {
    $failures++
    Write-Fail "Регистрация надстройки: $($_.Exception.Message)"
}
# Регистрация надстройки в HKLM, указывающая не на локальную копию (прежняя установка, клон репозитория): без прав
# администратора её не переписать, а SolidWorks с правами администратора загрузит по ней чужую DLL (аудит 15.09.2026, B10).
if (-not $sandbox -and -not $machine) {
    $machineDll = [string](Get-RegValue "HKLM:\SOFTWARE\Classes\CLSID\$activeGuid\InprocServer32" "CodeBase")
    $localUri = "file:///" + ([string]$layout.LocalAddinDll).Replace('\', '/')
    if ($machineDll -and $machineDll -ne $localUri) {
        Write-Warn ("Регистрация надстройки ЕСКД в HKLM указывает на другую DLL ($machineDll): SolidWorks, запущенный от имени " +
                    "администратора, загрузит её. Переписать её может администратор этого ПК: войти в Windows под своей " +
                    "учётной записью и запустить установку от имени администратора. Запуск «от имени администратора» с " +
                    "паролем ИТ, когда в Windows вошёл конструктор, установка отклоняет.")
    }
}
$current = if (Test-Path -LiteralPath $settingsKey) { Get-ItemProperty -LiteralPath $settingsKey } else { $null }
Set-Reg $settingsKey "Author" $Author
if ($Firm) { Set-Reg $settingsKey "Organization" $Firm }
foreach ($default in @(@{ Name = "Checker"; Value = ""; Type = "String" }, @{ Name = "AutoMass"; Value = 1; Type = "DWord" },
                       @{ Name = "AutoSplitName"; Value = 1; Type = "DWord" }, @{ Name = "AuthorList"; Value = $Author; Type = "String" })) {
    if (-not $current -or -not $current.PSObject.Properties[$default.Name]) { Set-Reg $settingsKey $default.Name $default.Value $default.Type }
}
foreach ($dead in @("AutoCenterMass", "MassDecimals")) { Remove-ItemProperty -LiteralPath $settingsKey -Name $dead -ErrorAction SilentlyContinue }

# Избранные материалы: имена и matid сверены с библиотекой (при неверном matid SolidWorks молча подставляет другой материал)
$favList = @(
    "Библиотека_Материалов_ГОСТ|Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024|1003",
    "Библиотека_Материалов_ГОСТ|Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024|1108",
    "Библиотека_Материалов_ГОСТ|Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86|1024",
    "Библиотека_Материалов_ГОСТ|Труба 57х3,5 ГОСТ 8732-78 / В 10 ГОСТ 8731-74|1109",
    "Библиотека_Материалов_ГОСТ|Труба 102х4,0 ГОСТ 8732-78 / В 20 ГОСТ 8731-74|1110",
    "Библиотека_Материалов_ГОСТ|Сталь 3сп (ГОСТ 380-2005)|1111",
    "Библиотека_Материалов_ГОСТ|Сталь 20 (ГОСТ 1050-2013)|1112",
    "Библиотека_Материалов_ГОСТ|Сталь 45 (ГОСТ 1050-2013)|1113",
    "Библиотека_Материалов_ГОСТ|Сталь 09Г2С (ГОСТ 19281-2014)|1114"
)
for ($i = 0; $i -lt $favList.Count; $i++) {
    Set-Reg "$swRoot\Material" "Favorite Material $($i + 1)" $favList[$i]
    Set-Reg "$swRoot\Material" "_FavMaterial$($i + 1)" $favList[$i]
}
Set-Reg "$swRoot\Material" "__NumOfFavs" $favList.Count "DWord"
Write-Ok "Фамилия, организация и избранные материалы записаны."

# 7. Шрифты, Drew и SWTools
Write-Step "[7/9] Шрифты ГОСТ, модуль Drew и SWTools..."
if ($sandbox -or $SkipFonts) {
    Write-Info "Шрифты пропущены."
} elseif (Test-Path -LiteralPath $layout.Fonts) {
    if (-not ([System.Management.Automation.PSTypeName]'GostFonts.NativeMethods').Type) {
        Add-Type -Namespace GostFonts -Name NativeMethods -MemberDefinition @'
[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "AddFontResourceW")]
public static extern int AddFontResource(string lpFileName);
[DllImport("user32.dll", SetLastError = true)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
'@
    }
    $hklmFonts = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"
    $hkcuFonts = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Fonts"
    $fontDir = if ($isAdmin) { Join-Path $env:SystemRoot "Fonts" } else { Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Fonts" }
    $fontReg = if ($isAdmin) { $hklmFonts } else { $hkcuFonts }
    if (-not (Test-Path -LiteralPath $fontDir)) { New-Item -ItemType Directory -Path $fontDir -Force | Out-Null }
    if (-not (Test-Path -LiteralPath $fontReg)) { New-Item -Path $fontReg -Force | Out-Null }
    $installed = 0; $present = 0
    foreach ($font in @(Get-ChildItem -LiteralPath $layout.Fonts -File | Where-Object { $_.Extension -match '^\.(ttf|otf|fon)$' })) {
        $valueName = "$([System.IO.Path]::GetFileNameWithoutExtension($font.Name)) (TrueType)"
        if ((Get-RegValue $hklmFonts $valueName) -or (Get-RegValue $hkcuFonts $valueName)) { $present++; continue }
        try {
            $dst = Join-Path $fontDir $font.Name
            if (-not (Test-Path -LiteralPath $dst)) { Copy-Item -LiteralPath $font.FullName -Destination $dst -Force }
            New-ItemProperty -Path $fontReg -Name $valueName -Value $dst -PropertyType String -Force | Out-Null
            [void][GostFonts.NativeMethods]::AddFontResource($dst)
            $installed++
        } catch {
            $failures++
            Write-Fail "Шрифт $($font.Name): $($_.Exception.Message)"
        }
    }
    if ($installed) {
        $r = [IntPtr]::Zero
        [void][GostFonts.NativeMethods]::SendMessageTimeout([IntPtr]0xFFFF, 0x001D, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 5000, [ref]$r)
    }
    Write-Ok "Шрифты ГОСТ: установлено $installed, уже были $present$(if (-not $isAdmin) { ' (в профиль пользователя)' })."
}

# Хвосты прежних переустановок Drew во временной папке (копия установщика и его распаковка) — и при снятой галочке Drew.
if (-not $sandbox) {
    $drewTempLeft = Clear-EskdDrewTempLeftovers
    if ($drewTempLeft.Removed) {
        Write-Info ("Убраны временные файлы прежних установок Drew: папок {0}, {1:N0} МБ." -f $drewTempLeft.Removed, ($drewTempLeft.Bytes / 1MB))
    } elseif ($drewTempLeft.Busy) {
        Write-Info "Временные файлы прежних установок Drew остаются: установщик Drew ещё работает. Их уберёт следующая настройка."
    }
}
if ($sandbox -or $SkipDrew) {
    Write-Info "Drew пропущен."
} else {
    $drewGuid = "{08c4bc0b-c36c-470e-a0ea-02232f023333}"
    $drewCandidates = @((Join-Path $env:ProgramFiles "CAD Booster\Drew\CADBooster.Drew.Drawing.dll"),
                        (Join-Path $env:LOCALAPPDATA "CAD Booster\Drew\CADBooster.Drew.Drawing.dll"))
    $licDll = Join-Path $env:ProgramFiles "CAD Booster\Drew\CADBooster.Common.Licensing.dll"
    # Хэш CADBooster.Common.Licensing.dll, которую ставит УСТАНОВЩИК_Drew_AUTO.exe инструментария (замер 24.09.2026, установщик
    # без слёта лицензии, SHA-256 6BD50418…; у установщиков 26.09.2026 — A9CEE78D…, 60273B0E…, FFAFF2D1… — сборка та же).
    # Новый установщик в Drw_System_Automation — новый замер и этот хэш: по нему рабочий Drew отличается от чужой сборки
    # (Get-EskdDrewPlan) и видно, что установка закончилась. Не обновили — установка всё равно засчитывается (сборка
    # лицензии появилась заново), а итог предупреждает, что замер устарел.
    $licHash = "0D31E06D6AC7F6F560745E8797BF09004BAC6FC576C937E36072AAC88831F365"
    # Какой установщик ставил Drew: запись «<время UTC>_<SHA-256 AUTO.exe>» после подтверждённой установки — метка на весь
    # ПК (файл в %ProgramData%\ESKD\DrewInstaller: Drew стоит на весь ПК, другая учётная запись не переставляет его заново)
    # и отпечаток учётной записи (HKCU, с именем ПК; -Mode Uninstall его сохраняет; нужен, если метку записать не дали).
    # Решает последняя запись (Get-EskdDrewRecordedInstaller). Пока в инструментарии тот же установщик, Drew не переустанавливается, даже если
    # хэш лицензионной сборки не совпал с замером (другая ОС) — иначе удаление и установка с запросом прав повторялись бы
    # при каждом обновлении. Сменился установщик (и возврат прежнего) — Drew переставляется один раз (решение владельца 26.09.2026).
    $drewInstallKey = "$U\SolidWorks\ESKD_Install"
    $drewMarkDir = Join-Path $env:ProgramData "ESKD\DrewInstaller"
    $drewDir = Join-Path $SourceRoot "03_Макросы_и_Плагины\Drw_System_Automation"
    $drewExe = @(Get-ChildItem -LiteralPath $drewDir -Filter "*AUTO.exe" -File -ErrorAction SilentlyContinue | Select-Object -First 1)
    # Хэши — только через Get-EskdFileSha256 (.NET): пустой хэш из-за пропавшего Get-FileHash означал «не совпало» и
    # удаление с переустановкой Drew (20.09.2026). Хэш сборки лицензии не вычислился — Drew не трогаем вовсе.
    $autoHash = if ($drewExe.Count) { Get-EskdFileSha256OrNull -Path $drewExe[0].FullName } else { $null }
    $drewRecords = @(Get-ChildItem -LiteralPath $drewMarkDir -File -Name -ErrorAction SilentlyContinue) +
                   @([string](Get-RegValue $drewInstallKey "DrewInstaller"))
    $recordedAuto = Get-EskdDrewRecordedInstaller -Records $drewRecords -Computer $env:COMPUTERNAME
    $licPresent = Test-Path -LiteralPath $licDll
    $licNow = if ($licPresent) { Get-EskdFileSha256OrNull -Path $licDll } else { $null }
    $drewPlan = Get-EskdDrewPlan -LicPresent $licPresent -LicNow $licNow -LicHash $licHash -AutoHash $autoHash -RecordedAuto $recordedAuto
    $drewOk = @("Keep", "Unverified") -contains $drewPlan
    # Метка на весь ПК. Не создалась (политика, права) — другие учётные записи переставят Drew ещё раз, это безопасно.
    $markDrew = {
        param([string]$Stamp)
        try {
            New-Item -ItemType Directory -Path $drewMarkDir -Force -ErrorAction Stop | Out-Null
            New-Item -ItemType File -Path (Join-Path $drewMarkDir $Stamp) -Force -ErrorAction Stop | Out-Null
        } catch { Write-Info "Метка установки Drew для других учётных записей не записана: $($_.Exception.Message)" }
    }
    # Установка подтверждена: отпечаток учётной записи (с именем ПК) и метка на весь ПК — одна и та же запись.
    $recordDrew = {
        $stamp = New-EskdDrewStamp -AutoHash $autoHash -Records $drewRecords
        Set-Reg $drewInstallKey "DrewInstaller" "${stamp}_$env:COMPUTERNAME"
        & $markDrew $stamp
    }
    # Записи «Drew» в списке установленных программ (установщик Windows): по ним удаляется прежняя сборка. Скриптблок отдаёт
    # одну запись без массива, а у одиночного объекта в PowerShell 5.1 нет .Count — считать только через @(& $findDrewEntries).
    $uninstallRoots = @("HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall")
    $findDrewEntries = {
        foreach ($root in $uninstallRoots) {
            foreach ($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
                $entry = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
                if ($entry -and "$($entry.DisplayName)" -eq "Drew" -and "$($entry.Publisher)" -match "CAD Booster" -and $key.PSChildName -match '^\{[0-9A-Fa-f\-]{36}\}$') {
                    [pscustomobject]@{ Code = $key.PSChildName; Version = "$($entry.DisplayVersion)" }
                }
            }
        }
    }
    if ($drewPlan -eq "Unverified") {
        Write-Warn "Drew установлен, но сверить его сборку не удалось (файл занят или недоступен): $licDll. Переустановка не выполняется."
    } elseif ($drewPlan -eq "Keep") {
        Write-Info "Drew уже установлен $(if ($autoHash) { 'этим же установщиком' } else { '(сборка верная, хэш совпал)' })."
    } elseif ($drewPlan -eq "Update" -and $drewExe.Count) {
        Write-Info "Drew поставлен прежним установщиком (или вручную), в инструментарии новый ($($drewExe[0].Name)): Drew будет переставлен один раз."
    } elseif ($drewPlan -eq "Replace" -and $drewExe.Count) {
        Write-Info "Сборка Drew не та, что ставит инструментарий (хэш лицензии $licNow): Drew будет переставлен."
    }
    if (-not $drewOk) {
        if ($drewExe.Count -eq 0) {
            Write-Warn "Установщик Drew не найден: $drewDir"
        } elseif (Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue) {
            Write-Warn "Установка Drew отложена: SolidWorks открыт (установщик закрывает его сам)."
        } else {
            # Установщик просит права администратора, а у повышенного процесса нет подключённых сетевых дисков: с буквы
            # диска NAS он тихо не стартует. Как у SWTools — запуск из локальной копии (аудит 19.09, У-К4). Копия — до
            # удаления прежнего Drew: не скопировалась (публикация, антивирус) — рабочий Drew не трогается. Отпечаток —
            # хэш именно копии: общую папку могли обновить посреди настройки.
            $drewTemp = Join-Path $env:TEMP ("ESKD_Drew_" + [guid]::NewGuid().ToString("N"))
            $drewLocalExe = Join-Path $drewTemp $drewExe[0].Name
            $copied = $false
            try {
                New-Item -ItemType Directory -Path $drewTemp -Force | Out-Null
                Copy-Item -LiteralPath $drewExe[0].FullName -Destination $drewLocalExe -Force -ErrorAction Stop
                $runHash = Get-EskdFileSha256OrNull -Path $drewLocalExe
                if (-not $runHash) { throw "копия установщика не читается (антивирус?): $drewLocalExe" }
                $autoHash = $runHash
                $copied = $true
            } catch {
                if ($drewPlan -eq "Update") {
                    Write-Warn "Drew не обновлён: установщик не скопирован ($($_.Exception.Message)). Прежний Drew работает; обновление предложит следующая установка."
                } else {
                    $failures++
                    Write-Fail "Установщик Drew не скопирован: $($_.Exception.Message) Запустите $($drewExe[0].Name) вручную."
                }
            }
            # Прежняя сборка Drew той же версии 4.3.0.0: установщик Windows её только «перенастроит» и файлы не заменит.
            # Поэтому сначала штатное удаление (msiexec /x по коду продукта; Program Files — нужны права администратора).
            # Отказ в правах или сбой удаления при обновлении рабочего Drew (Update) — предупреждение: прежний Drew цел,
            # обновление предложит следующая установка. Чужая сборка (Replace) — ошибка. После удаления (в том числе
            # отложенного до перезагрузки, код 3010) неудачная установка — ошибка: следующая настройка поставит Drew с нуля.
            $removeOk = $copied
            $uninstalled = $false
            if ($copied) {
                foreach ($old in @(& $findDrewEntries)) {
                    $current = if (Test-Path -LiteralPath $licDll) { Get-EskdFileSha256OrNull -Path $licDll } else { "нет файла" }
                    Write-Info "Удаление прежней сборки Drew $($old.Version) $($old.Code) (хэш лицензии $current). Подтвердите запрос прав администратора."
                    try {
                        $msi = Start-Process -FilePath "msiexec.exe" -ArgumentList "/x", $old.Code, "/qn", "/norestart" -Verb RunAs -Wait -PassThru
                        # 0 — удалено, 3010 — удалено, нужна перезагрузка, 1605 — продукт уже не установлен
                        if (@(0, 3010, 1605) -contains $msi.ExitCode) {
                            if ($msi.ExitCode -ne 1605) { $uninstalled = $true }
                            Write-Ok "Прежняя сборка Drew удалена (код $($msi.ExitCode))."
                        } else {
                            $removeOk = $false
                            if ($drewPlan -eq "Update" -and -not $uninstalled) {
                                Write-Warn "Drew не обновлён: удаление прежней сборки завершилось с кодом $($msi.ExitCode). Прежний Drew работает; обновление предложит следующая установка."
                            } else {
                                $failures++
                                Write-Fail "Удаление прежней сборки Drew завершилось с кодом $($msi.ExitCode)."
                            }
                        }
                    } catch {
                        $removeOk = $false
                        if ($drewPlan -eq "Update" -and -not $uninstalled) {
                            Write-Warn "Drew не обновлён: запрос прав администратора отклонён. Новая сборка не ставится, прежний Drew работает; обновление предложит следующая установка."
                        } else {
                            $failures++
                            Write-Fail "Прежняя сборка Drew не удалена: запрос прав администратора отклонён. Новая сборка не ставится."
                        }
                    }
                }
            }
            $setup = $null
            if ($removeOk) {
                Write-Info "Установка Drew (Gov-издание, лицензия встроена): $($drewExe[0].Name). Когда установщик сообщит итог, закройте его окно."
                # Признак, что установщик действительно поставил Drew (код выхода AUTO.exe всегда 0, даты файлов — даты
                # сборки): сборки лицензии перед запуском не было, а после — есть. Прежние файлы остались (удаление ждёт
                # перезагрузки, Drew копировали вручную) — установщик Windows мог их не заменить: запись Drew в списке
                # программ появилась, но сборку не проверить, установка не засчитывается (26.09.2026).
                $licLeft = Test-Path -LiteralPath $licDll
                $entryLeft = @(& $findDrewEntries).Count -gt 0
                try {
                    $setup = Start-Process -FilePath $drewLocalExe -WorkingDirectory $drewTemp -PassThru -ErrorAction Stop
                } catch {
                    # Блокировка файла SmartScreen и т. п.: ждать нечего. Права установщик запрашивает сам, уже после запуска.
                    if ($drewPlan -eq "Update" -and -not $uninstalled) {
                        Write-Warn "Drew не обновлён: установщик не запустился ($($_.Exception.Message)). Прежний Drew работает; обновление предложит следующая установка."
                    } else {
                        $failures++
                        Write-Fail "Установщик Drew не запустился: $($_.Exception.Message) Запустите $($drewExe[0].Name) вручную."
                    }
                }
                if ($setup) {
                    $deadline = (Get-Date).AddMinutes(6)
                    $exitedAt = $null
                    while ((Get-Date) -lt $deadline) {
                        Start-Sleep -Seconds 3
                        $newFiles = -not $licLeft -and (Test-Path -LiteralPath $licDll)
                        # Сборка лицензии появилась с верным хэшем — установка закончилась, окно установщика ещё может быть открыто.
                        if ($newFiles -and (Get-EskdFileSha256OrNull -Path $licDll) -eq $licHash) { break }
                        if ($setup.HasExited) {
                            # Установщик мог передать работу установщику Windows и выйти раньше — даём ему 20 секунд.
                            if ($newFiles -or (-not $entryLeft -and @(& $findDrewEntries).Count -gt 0)) { break }
                            if (-not $exitedAt) { $exitedAt = Get-Date } elseif (((Get-Date) - $exitedAt).TotalSeconds -gt 20) { break }
                        }
                    }
                    $newFiles = -not $licLeft -and (Test-Path -LiteralPath $licDll)
                    $registered = -not $entryLeft -and @(& $findDrewEntries).Count -gt 0
                    $licAfter = if (Test-Path -LiteralPath $licDll) { Get-EskdFileSha256OrNull -Path $licDll } else { $null }
                    if ($newFiles -and $licAfter -eq $licHash) {
                        & $recordDrew
                        $drewOk = $true
                        Write-Ok "Drew установлен (контроль хэша пройден)."
                    }
                    elseif ($newFiles) {
                        # Установка прошла, а сборка лицензии не та, что в замере: установщик в инструментарии новее замера.
                        # Отпечаток пишется — иначе удаление и установка повторялись бы при каждом обновлении.
                        & $recordDrew
                        $drewOk = $true
                        Write-Ok "Drew установлен."
                        Write-Warn "Хэш сборки лицензии Drew ($(if ($licAfter) { $licAfter } else { 'не прочитан' })) не совпал с замером в сценарии настройки: установщик в инструментарии новее замера — администратору обновить `$licHash."
                    }
                    elseif ($registered) {
                        $overText = "Drew поставлен поверх оставшихся файлов прежней сборки (их удаление ждёт перезагрузки, или Drew копировали вручную): сборку проверить нельзя. Перезагрузите ПК и запустите настройку снова."
                        if ($drewPlan -eq "Update" -and -not $uninstalled) { Write-Warn $overText } else { $failures++; Write-Fail $overText }
                    }
                    elseif ($setup.HasExited -and $drewPlan -eq "Update" -and -not $uninstalled -and (Test-Path -LiteralPath $licDll)) {
                        # Удалять было нечего, прежний Drew на месте, новый не поставлен (отказ в правах в окне установщика).
                        Write-Warn "Drew не обновлён: установщик закрылся, не поставив новую сборку. Прежний Drew работает; обновление предложит следующая установка."
                    }
                    elseif ($setup.HasExited) { $failures++; Write-Fail "Установщик Drew завершился, а Drew не установлен - запустите настройку снова или $($drewExe[0].Name) вручную." }
                    else { $failures++; Write-Fail "Drew не установился за 6 минут - проверьте окно установщика." }
                    if ($drewOk -and -not $setup.HasExited) { Write-Info "Окно установщика Drew ещё открыто - закройте его кнопкой «Готово»." }
                }
            }
            # Копию убираем, только когда установщик закончил: работающему процессу она ещё нужна.
            if (-not $setup -or $setup.HasExited) { Remove-Item -LiteralPath $drewTemp -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
    $drewDll = $drewCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($drewDll) {
        $drewClass = "CADBooster.Drew.Drawing.SolidWorks.Integration.DrewAddin"
        $drewAssembly = "CADBooster.Drew.Drawing, Version=4.3.0.0, Culture=neutral, PublicKeyToken=null"
        $inproc = "$U\Classes\CLSID\$drewGuid\InprocServer32"
        Set-Reg "$U\Classes\CLSID\$drewGuid" "(Default)" $drewClass
        Set-Reg $inproc "(Default)" "mscoree.dll"
        Set-Reg $inproc "ThreadingModel" "Both"
        Set-Reg $inproc "Class" $drewClass
        Set-Reg $inproc "Assembly" $drewAssembly
        Set-Reg $inproc "RuntimeVersion" "v4.0.30319"
        Set-Reg $inproc "CodeBase" ("file:///" + $drewDll.Replace('\', '/'))
        Set-Reg "$U\SolidWorks\AddIns\$drewGuid" "(Default)" 1 "DWord"
        Set-Reg "$U\SolidWorks\AddIns\$drewGuid" "Title" "Drew"
        Set-Reg "$U\SolidWorks\AddinsStartup\$drewGuid" "(Default)" 1 "DWord"
        Write-Ok "Модуль Drew подключён: $drewDll"
        $marker = Join-Path $env:APPDATA "CAD Booster\Drew\LicenseKey.skm"
        if (Test-Path -LiteralPath $marker) { Write-Ok "Лицензия Drew: встроенная, активация не требуется." }
        else { Write-Warn "Лицензия Drew: нет файла-маркера ($marker) - переустановите Drew галочкой." }

        # Профиль оформления Drew (форматки ГОСТ, шаблон чертежа) — всегда из инструментария, чтобы результат не зависел от
        # того, что было на ПК. В эталоне пути записаны через %TOOLKIT% и заменяются папкой инструментария (источником);
        # прежний файл пользователя сохраняется в резервную копию.
        $drewConfigDir = Join-Path $env:APPDATA "CAD Booster\Drew"
        $drewBlueprintsTarget = Join-Path $drewConfigDir "Drew-Blueprints.xml"
        $drewBlueprintsMaster = Join-Path $SourceRoot "03_Макросы_и_Плагины\Drw_System_Automation\Drew-Blueprints.xml"
        if (Test-Path -LiteralPath $drewBlueprintsMaster) {
            try {
                New-Item -ItemType Directory -Path $drewConfigDir -Force | Out-Null
                if (Test-Path -LiteralPath $drewBlueprintsTarget) {
                    $drewBackup = Join-Path $env:LOCALAPPDATA ("ESKD\Backups\Drew-Blueprints_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".xml")
                    New-Item -ItemType Directory -Path (Split-Path -Parent $drewBackup) -Force | Out-Null
                    Copy-Item -LiteralPath $drewBlueprintsTarget -Destination $drewBackup -Force -ErrorAction Stop
                }
                $xmlContent = [System.IO.File]::ReadAllText($drewBlueprintsMaster, [System.Text.Encoding]::UTF8)
                $toolkit = [System.Security.SecurityElement]::Escape($SourceRoot.TrimEnd('\'))
                $updated = $xmlContent.Replace("%TOOLKIT%", $toolkit)
                [xml]$updated | Out-Null
                [System.IO.File]::WriteAllText($drewBlueprintsTarget, $updated, (New-Object System.Text.UTF8Encoding($true)))
                Write-Ok "Профиль оформления Drew: форматки и шаблон чертежа из $SourceRoot"
            } catch {
                Write-Warn "Профиль оформления Drew не записан: $($_.Exception.Message)"
            }
        } else {
            Write-Warn "Эталон профиля Drew не найден: $drewBlueprintsMaster"
        }
    } else {
        Write-Warn "Модуль Drew не найден и не установлен."
    }
}

# SWTools: выгрузка спецификаций, в том числе для кнопки «Ведомость ЛЗК». Установщик из общей папки (Publish-EskdToolkit
# -SwToolsSetup), тихая установка с запросом прав администратора; лицензию конструктор активирует сам в окне SWTools.
if ($sandbox -or $SkipSwTools) {
    Write-Info "SWTools пропущен."
} else {
    $swToolsRelease = $null
    try { $swToolsRelease = Get-EskdSwToolsRelease -SourceRoot $SourceRoot } catch { $failures++; Write-Fail "Описание выпуска SWTools: $($_.Exception.Message)" }
    $swToolsInstalled = Get-EskdSwToolsInstalledVersion
    if (-not $swToolsRelease) {
        if ($swToolsInstalled) { Write-Info "SWTools $swToolsInstalled установлен; в общей папке выпуска SWTools нет." }
        else { Write-Warn "SWTools не установлен, а в общей папке нет его установщика ($($layout.SwTools)). Кнопка «Ведомость ЛЗК» не будет работать." }
    } elseif (-not (Test-EskdSwToolsUpdateNeeded -Release $swToolsRelease.Version -Installed $swToolsInstalled)) {
        Write-Ok "SWTools $swToolsInstalled уже установлен (выпуск $($swToolsRelease.Version))."
    } elseif (Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue) {
        Write-Warn "Установка SWTools $($swToolsRelease.Version) отложена: SolidWorks открыт. Закройте его и запустите настройку ещё раз."
    } elseif (-not (Test-Path -LiteralPath $swToolsRelease.SetupPath)) {
        $failures++
        Write-Fail "Нет установщика SWTools: $($swToolsRelease.SetupPath)"
    } else {
        # Копия во временной папке: установщик не запускается с сетевого ресурса и сверяется с описанием выпуска.
        $swToolsTemp = Join-Path $env:TEMP ("ESKD_SWTools_" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $swToolsTemp -Force | Out-Null
        $swToolsSetup = Join-Path $swToolsTemp (Split-Path -Leaf $swToolsRelease.SetupPath)
        try {
            Copy-Item -LiteralPath $swToolsRelease.SetupPath -Destination $swToolsSetup -Force -ErrorAction Stop
            if ((Get-EskdFileSha256 -Path $swToolsSetup) -ne $swToolsRelease.Sha256) {
                $failures++
                Write-Fail "Установщик SWTools не совпадает с выпуском (SHA-256) — не запускается: $($swToolsRelease.SetupPath)"
            } else {
                $from = if ($swToolsInstalled) { "обновление с $swToolsInstalled" } else { "установка" }
                Write-Info "SWTools $($swToolsRelease.Version): $from. Подтвердите запрос прав администратора."
                $swToolsProc = $null
                try {
                    $swToolsProc = Start-Process -FilePath $swToolsSetup -Verb RunAs -PassThru -ErrorAction Stop `
                        -ArgumentList "/S", "/SWTOOLS_EULA_SHA256=$($swToolsRelease.EulaSha256)"
                } catch {
                    $failures++
                    Write-Fail "Установщик SWTools не запущен (запрос прав отклонён?): $($_.Exception.Message)"
                }
                if ($swToolsProc) {
                    if (-not $swToolsProc.WaitForExit(600000)) {
                        $failures++
                        Write-Fail "Установка SWTools не завершилась за 10 минут."
                    } else {
                        $after = Get-EskdSwToolsInstalledVersion
                        if ($swToolsProc.ExitCode -eq 0 -and $after -and $after -ge $swToolsRelease.Version) {
                            Write-Ok "SWTools $after установлен."
                        } else {
                            $failures++
                            Write-Fail "Установщик SWTools завершился с кодом $($swToolsProc.ExitCode); установлена версия: $(if ($after) { $after } else { 'нет' })."
                        }
                    }
                }
            }
        } catch {
            $failures++
            Write-Fail "SWTools: $($_.Exception.Message)"
        } finally {
            Remove-Item -LiteralPath $swToolsTemp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (Get-EskdSwToolsInstalledVersion) {
        if (Test-Path -LiteralPath (Get-EskdSwToolsLicensePath)) { Write-Ok "Лицензия SWTools: файл лицензии есть." }
        else { Write-Warn "Лицензия SWTools не активирована: откройте SWTools из SolidWorks и активируйте лицензию. До этого кнопка «Ведомость ЛЗК» не работает." }
    }
}

# 8. Отучение SolidWorks от сети (галочка в окне). Решение владельца 26.09.2026: SolidWorks не отправляет в интернет ничего —
# закрыт и сам SLDWORKS.exe со всеми надстройками (Drew «Поделиться настройками» не работает — принято), SWTools, все
# программы SolidWorks этого ПК. Пакет находит их сам (любая версия, диск, язык установки); компьютер и локальная сеть
# (сервер лицензий, общие папки) остаются доступны. Права администратора запрашиваются, только если чего-то не хватает.
Write-Step "[8/9] Отучение SolidWorks от сети..."
if ($SwInternetBlock) {
    $sbDir = Join-Path (Split-Path -Path $PSScriptRoot -Parent) "SwInternetBlock"  # пакет лежит в 01_Настройки_SolidWorks, сценарий — в _Служебное
    $sb = Join-Path $sbDir "Set-SwInternetBlock.ps1"
    # Сетевые функции самого SolidWorks — в настройках пользователя, без прав администратора (после профиля [3/9]).
    foreach ($v in Get-EskdSwOfflineSettings) { Set-Reg "$swRoot\$($v.Key)" $v.Name $v.Value "DWord" }
    Write-Ok "SolidWorks: новости, проверка исправлений сбоев, онлайн-учебник, отправка отзывов и 3DEXPERIENCE выключены."
    Set-Reg $install "SwInternetBlock" "1"
    if ($sandbox) {
        Write-Info "Тестовый корень реестра: брандмауэр и hosts не меняются."
    } elseif (-not (Test-Path -LiteralPath $sb)) {
        Write-Warn "Пакет SwInternetBlock не найден: $sb"
    } else {
        # Сбой — примечание [ВНИМАНИЕ], установка ошибкой не считается (решение владельца 25.09.2026).
        $sbAudit = Invoke-EskdSwBlock -Script $sb -Mode audit
        if ($sbAudit.Code -ne 0 -and $sbAudit.Code -ne 4) {
            if ($machine) {
                $sbRun = Invoke-EskdSwBlock -Script $sb -Mode apply
                foreach ($l in $sbRun.Lines | Where-Object { $_ -match '^(apply|hosts|ОШИБКА)' }) { Write-Info "  $l" }
            } else {
                Write-Info "Нужны права администратора - откроется запрос UAC..."
                # У повышенного процесса нет подключённых сетевых дисков: пакет запускается из локальной копии (аудит 19.09,
                # У-К4). Администратор не видит надстроек этого пользователя (HKCU) — их папки передаются файлом. Права
                # обычно подтверждает администратор своим паролем: HKCU окна — его, поэтому прокси и сервер лицензий пакет
                # берёт из раздела конструктора по SID (иначе отпечаток адресов не сойдётся с проверкой и UAC будет всегда).
                $sbTemp = Join-Path $env:TEMP ("ESKD_SwInternetBlock_" + [guid]::NewGuid().ToString("N"))
                $sbCopy = $sb
                $sbRoots = ""
                try {
                    Copy-Item -LiteralPath $sbDir -Destination $sbTemp -Recurse -Force -ErrorAction Stop
                    $sbCopy = Join-Path $sbTemp "Set-SwInternetBlock.ps1"
                    $sbRoots = Join-Path $sbTemp "roots.txt"
                    [System.IO.File]::WriteAllText($sbRoots, (@((Invoke-EskdSwBlock -Script $sb -Mode roots).Lines) -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
                } catch { Write-Warn "Копия SwInternetBlock в %TEMP% не сделана ($($_.Exception.Message)) - запуск из $sbDir." }
                $sbCmd = "& '{0}' -Mode apply -UserSid '{1}'" -f $sbCopy.Replace("'", "''"), [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
                if ($sbRoots) { $sbCmd += " -ExtraRootFile '{0}'" -f $sbRoots.Replace("'", "''") }
                # Ожидание ограничено, чтобы окно установки не висело бесконечно: первое применение создаёт ~330 правил
                # и на новом ПК идёт 2-3 минуты. По истечении срока процесс администратора продолжает работу сам.
                $uacState = "declined"
                try {
                    $uacProc = Start-Process powershell.exe -Verb RunAs -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command',$sbCmd
                    $uacState = "done"
                    if ($uacProc -and -not $uacProc.WaitForExit(600000)) { $uacState = "timeout" }
                } catch { Write-Warn "UAC отклонён - отучение от сети пропущено." }
                if ($uacState -ne "timeout" -and $sbCopy -ne $sb) { Remove-Item -LiteralPath $sbTemp -Recurse -Force -ErrorAction SilentlyContinue }
                if ($uacState -eq "timeout") { Write-Warn "Отучение от сети не завершилось за 10 минут и продолжается в отдельном окне. Проверьте позже: установщик с -Mode Check." }
            }
            $sbAudit = Invoke-EskdSwBlock -Script $sb -Mode audit
        }
        if ($sbAudit.Code -eq 0) {
            # Примечания пакета (прокси из сценария автонастройки, прокси на сервере лицензий) видны и при успехе.
            foreach ($l in $sbAudit.Lines | Where-Object { $_ -match 'ВНИМАНИЕ' }) { Write-Info "  $($l.Trim())" }
            Write-Ok $(if ($sbAudit.Verdict) { $sbAudit.Verdict -replace '^ИТОГ:\s*', '' } else { "SolidWorks отучен от интернета." })
        } else {
            foreach ($l in $sbAudit.Lines | Select-Object -First 25) { Write-Info "  $l" }
            if ($sbAudit.Code -eq 4) { Write-Warn "Правила брандмауэра на месте, но не действуют или обходятся (сообщения выше): SolidWorks может выходить в интернет." }
            else { Write-Warn "Отучение от сети неполное (сообщения выше, код $($sbAudit.Code)): SolidWorks может выходить в интернет." }
        }
    }
} else {
    Write-Info "Пропущено (галочка снята)."
    if ([string](Get-RegValue $install "SwInternetBlock") -eq "1") {
        Write-Info "Правила прежнего отучения от сети остаются. Снять: SwInternetBlock\Set-SwInternetBlock.ps1 -Mode remove (администратор)."
    }
    # Выбор запоминается: окно при следующем открытии не ставит галочку снова.
    Set-Reg $install "SwInternetBlock" "0"
}

# 9. Язык интерфейса SolidWorks и Drew (выбор в окне; решение владельца 25.09.2026). После профиля [3/9]: сброс и .reg
# ставят «Use English language» = 0, здесь пишется итоговое значение. Правило выбора языка — Get-EskdLanguagePlan.
Write-Step "[9/9] Язык интерфейса SolidWorks и Drew: $(if ($Language -eq 'English') { 'английский' } else { 'русский' })..."
$swFolder = [string](Get-RegValue "HKLM:\SOFTWARE\SolidWorks\$SwVersion\Setup" "SolidWorks Folder")
$ruLangDir = if ($swFolder) { Join-Path $swFolder "lang\russian" } else { "" }
# Русский язык SolidWorks — главная библиотека ресурсов lang\russian\sldresu.dll той же версии, что SLDWORKS.exe. Прежде
# хватало любой DLL в папке: неполная копия или папка от другой версии проходили проверку (разбор 25.09.2026).
$ruMain = if ($ruLangDir) { Join-Path $ruLangDir "sldresu.dll" } else { "" }
# SolidWorks установлен, если есть SLDWORKS.exe: папка из реестра (и скопированный в неё русский язык) может остаться после
# удаления SolidWorks (проверка изменений 25.09.2026).
$swExe = if ($swFolder) { Join-Path $swFolder "SLDWORKS.exe" } else { "" }
$swFound = [bool]$swExe -and (Test-Path -LiteralPath $swExe)
$ruPack = $swFound -and (Test-Path -LiteralPath $ruMain)
$ruOtherVersion = ""
if ($ruPack) {
    $packMajor = (Get-Item -LiteralPath $ruMain).VersionInfo.FileMajorPart
    $swMajor = (Get-Item -LiteralPath $swExe).VersionInfo.FileMajorPart
    if ($packMajor -ne $swMajor) {
        $ruOtherVersion = "русский язык в $ruLangDir от другой версии SolidWorks (sldresu.dll $packMajor, SLDWORKS.exe $swMajor)"
        $ruPack = $false
    }
}
$culture = Get-Culture
$acp = [string](Get-RegValue "HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage" "ACP")
$plan = Get-EskdLanguagePlan -Language $Language -UserLcid $culture.LCID -RussianPack $ruPack -Acp $acp
Set-Reg "$swRoot\General" "Use English language" $plan.UseEnglish "DWord"
if ($Language -eq "English") {
    Write-Ok "SolidWorks: английский интерфейс («Use English language» = 1). Формат Windows не меняется."
} elseif (-not $swFound) {
    # Нет SLDWORKS.exe — SolidWorks не установлен: это не «нет русского пакета».
    Write-Warn ("SolidWorks $SwVersion не найден на этом ПК (HKLM\SOFTWARE\SolidWorks\$SwVersion\Setup, " +
                "«SolidWorks Folder» = «$swFolder») - русский язык не проверен, формат Windows не меняется.")
    $languageIssue = "$SwVersion не найден на этом ПК"
} elseif (-not $ruPack) {
    $ruProblem = if ($ruOtherVersion) { $ruOtherVersion } else { "в SolidWorks не установлен русский язык (нет $ruMain)" }
    Write-Warn ("$ruProblem`: интерфейс останется английским, формат Windows не меняется. Администратору: Панель управления -> " +
                "Программы и компоненты -> SOLIDWORKS -> Изменить -> Языки SOLIDWORKS -> Русский (нужен дистрибутив той же версии).")
    $languageIssue = $ruProblem
} elseif ($plan.RussianFormat) {
    Write-Ok "SolidWorks: русский интерфейс (формат Windows «$($culture.Name)» русский, «Use English language» = 0)."
} elseif ($sandbox) {
    Write-Info "Тестовый корень реестра: формат Windows не меняется (сейчас «$($culture.Name)»)."
} else {
    # SolidWorks берёт язык из регионального формата пользователя, а не из языка Windows: без смены формата меню
    # останется английским. Set-Culture меняет формат только у этого пользователя, права администратора не нужны.
    try {
        Set-Culture -CultureInfo ru-RU -ErrorAction Stop
        Write-Ok ("Формат Windows: «$($culture.Name)» -> «ru-RU» (Русский, Россия) - SolidWorks будет на русском. " +
                  "Во всех программах пользователя теперь десятичная запятая, даты ДД.ММ.ГГГГ, рубль в денежном формате.")
        $ps51 = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
        $lcid = "$(& $ps51 -NoProfile -Command '[Globalization.CultureInfo]::CurrentCulture.LCID' 2>$null)".Trim()
        if ($lcid -ne "1049") {
            Write-Warn "Новые программы формат ещё не видят - выйдите из учётной записи Windows и войдите снова."
            $languageIssue = "формат Windows «ru-RU» новые программы увидят только после выхода из учётной записи Windows и входа снова"
        }
    } catch {
        Write-Warn ("Формат Windows не переключён: $($_.Exception.Message) Вручную (Windows 10 и 11): Win+R -> intl.cpl -> " +
                    "вкладка «Форматы» -> Русский (Россия) -> ОК, затем перезапустить SolidWorks.")
        $languageIssue = "формат Windows «$($culture.Name)» не переключён на «Русский (Россия)»"
    }
}
if ($sandbox) {
    Write-Info "Тестовый корень реестра: DREW_LANG не меняется."
} else {
    [Environment]::SetEnvironmentVariable('DREW_LANG', $plan.DrewLang, 'User')
    Write-Ok "Drew: DREW_LANG=$($plan.DrewLang) для пользователя $env:USERNAME."
}
if (Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue) {
    Write-Warn "SolidWorks открыт: язык сменится после его перезапуска."
}
# Макросы SWPlus — VBA с русскими строками в кодовой странице 1251: при другой системной кодовой странице они показывают и
# пишут в свойства «кракозябры». Системную локаль меняет только администратор с перезагрузкой — здесь проверка.
$acpNote = ""
if (-not $plan.AcpOk) {
    $acpNote = ("Язык программ, не поддерживающих Юникод, - не русский (кодовая страница $(if ($acp) { $acp } else { 'не прочитана' })): " +
                "макросы SWPlus (MProp, DProp, SpecEditor) покажут и запишут в свойства нечитаемые символы вместо русских букв. " +
                "Администратору: Панель управления -> Региональные стандарты -> Дополнительно -> Изменить язык системы -> " +
                "Русский (Россия), флажок «Бета-версия: Юникод (UTF-8)» снять, перезагрузить ПК.")
} elseif ([System.Text.Encoding]::Default.CodePage -ne 1251) {
    $acpNote = "Язык системы уже русский, но ПК после смены не перезагружен: перезагрузите ПК, иначе макросы SWPlus покажут нечитаемые символы."
}
if ($acpNote) { Write-Warn $acpNote } else { Write-Info "Язык программ без Юникода - русский (1251): макросы SWPlus работают." }

# Сведения об установке
Set-Reg $install "SourceRoot" $SourceRoot
Set-Reg $install "LocalRoot" $LocalRoot
Set-Reg $install "ReleaseVersion" $release.Version
Set-Reg $install "ReleaseCommit" $release.Commit
Set-Reg $install "SwVersion" $SwVersion
Set-Reg $install "Author" $Author
Set-Reg $install "Language" $Language
Set-Reg $install "Graphics" $Graphics   # окно помнит «Безопасную графику»: следующее обновление не включает конвейер снова
Set-Reg $install "InstalledAt" (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Set-Reg $install "LastResult" $(if ($failures) { "FAILED" } else { "OK" })
Set-Reg $install "LanguageIssue" $languageIssue
Set-Reg $install "LastLog" $logPath

Write-Host ""
# Предупреждение о языке системы повторяется перед итогом: в середине журнала его легко пропустить.
if ($acpNote) { Write-Warn $acpNote }
if ($failures) {
    Write-Host "=================================================================" -ForegroundColor Red
    Write-Host " НАСТРОЙКА ЗАВЕРШЕНА С ОШИБКАМИ: $failures. Сообщения выше." -ForegroundColor Red
    Write-Host "=================================================================" -ForegroundColor Red
    exit 1
}
if ($languageIssue) {
    # Код 4: всё настроено, но выбранный русский интерфейс не включится — окно показывает это жёлтым, а не «Готово».
    Write-Host "=================================================================" -ForegroundColor Yellow
    Write-Host " НАСТРОЙКА ЗАВЕРШЕНА, НО SOLIDWORKS ОСТАНЕТСЯ АНГЛИЙСКИМ:" -ForegroundColor Yellow
    Write-Host " $languageIssue. Что сделать — в строке [ВНИМАНИЕ] шага [9/9]." -ForegroundColor Yellow
    Write-Host "=================================================================" -ForegroundColor Yellow
    exit 4
}
Write-Host "=================================================================" -ForegroundColor Green
Write-Host " НАСТРОЙКА ЗАВЕРШЕНА. Запустите SolidWorks." -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
exit 0
