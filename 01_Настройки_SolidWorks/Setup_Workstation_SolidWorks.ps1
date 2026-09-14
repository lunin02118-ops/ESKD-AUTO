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
      * шрифты ГОСТ (без прав администратора — в профиль пользователя), модуль Drew.
    В источник не пишет ничего: папка инструментария может быть только для чтения. Надстройку не собирает — сборку
    кладёт в источник публикация (Publish-EskdToolkit.ps1). Права администратора не нужны.

    Повторный запуск — обновление: файлы выпуска заменяются, настройки макросов пользователя остаются.
.PARAMETER Author
    Фамилия и инициалы для штампа. По умолчанию — из ESKD_Settings, иначе полное имя учётной записи Windows.
.PARAMETER Firm
    Организация для основной надписи. По умолчанию — из ESKD_Settings.
.PARAMETER CloseMode
    Ask — спросить в консоли; Graceful — закрыть SolidWorks через COM без вопроса (согласие получено в окне);
    Force — завершить без сохранения; Skip — не закрывать. -KillSolidWorks и -SkipClose — прежние синонимы.
.PARAMETER NonInteractive
    Без вопросов в консоли: недостающая фамилия — ошибка.
.PARAMETER LocalRoot
    Локальная копия (по умолчанию %LOCALAPPDATA%\ESKD\Toolkit).
.PARAMETER RegistryRoot
    Корень HKCU\Software для записи (по умолчанию HKCU:\Software). Автотест передаёт временный раздел
    HKCU:\Software\ESKD_DeployTest_*: тогда HKLM, шрифты и Drew не трогаются.
.EXAMPLE
    .\Setup_Workstation_SolidWorks.ps1
.EXAMPLE
    .\Setup_Workstation_SolidWorks.ps1 -Author "Иванов И.И." -Firm "ТОО «Троя»" -CloseMode Graceful -NonInteractive
#>

[CmdletBinding()]
param (
    [string]$Author = "",
    [string]$Firm = "",
    [string]$SwVersion = "",
    [ValidateSet("Ask", "Graceful", "Force", "Skip")][string]$CloseMode = "Ask",
    [switch]$SkipClose,
    [switch]$KillSolidWorks,
    [switch]$NonInteractive,
    [switch]$SkipFonts,
    [switch]$SkipDrew,
    [switch]$Utf8Output,
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
                [void]$swApp.ExitApp()
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($swApp)
                Write-Info "SolidWorks закрывается (до 30 секунд)..."
            }
        } catch {
            Write-Warn "SolidWorks не отвечает по COM: $($_.Exception.Message)"
        }
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and (Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
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
    $ok = 0
    foreach ($key in $RegistryKeys) {
        if (-not (Test-Path ($key -replace '^HKCU\\', 'HKCU:\'))) { continue }
        $outFile = Join-Path $backupDir ((($key -replace '[\\/:*?"<>|]', '_')) + ".reg")
        $null = & reg.exe export "$key" "$outFile" /y 2>&1
        if ($LASTEXITCODE -eq 0) { $ok++ } else { Write-Warn "Резервная копия ветки '$key' не создана (код $LASTEXITCODE)." }
    }
    Get-ChildItem -LiteralPath $BackupRoot -Directory -Filter "Backup_*" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -Skip 5 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    if ($ok) { Write-Ok "Резервная копия реестра: $backupDir" }
    return $backupDir
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
$Firm = "$Firm".Trim()
Write-Host "Конструктор:          $Author" -ForegroundColor White
Write-Host "Организация:          $(if ($Firm) { $Firm } else { '(не задана)' })" -ForegroundColor White

$failures = 0

# 1. SolidWorks
Write-Step "[1/7] Проверка SolidWorks..."
if (-not (Close-SolidWorks -Mode $CloseMode)) {
    Write-Fail "Установка прервана: SolidWorks не закрыт. Изменения не вносились."
    exit 3
}

# 2. Локальная копия макросов SWPlus и надстройки
Write-Step "[2/7] Копирование макросов SWPlus и надстройки ЕСКД в профиль пользователя..."
try {
    $copy = Copy-EskdLocalInstance -Layout $layout
    Write-Ok ("Локальная копия: обновлено файлов {0}, без изменений {1}, настройки пользователя сохранены {2}." -f $copy.Copied.Count, $copy.Same, $copy.Kept.Count)
} catch {
    Write-Fail $_.Exception.Message
    exit 2
}
if (Set-EskdMasterIniFormats -MasterIni (Join-Path $layout.LocalSwPlus "Master\Master.ini") -SheetFormats $layout.SheetFormats) {
    Write-Ok "Master.ini: основные надписи — $($layout.SheetFormats)"
}
$mprop = Join-Path $layout.LocalSwPlus "MProp"
if (Add-SwPlusFamily -Path (Join-Path $mprop "MProp_Fam.txt") -Name $Author) {
    Write-Warn "Фамилии «$Author» нет в общем списке MProp — добавлена в локальный. Передайте администратору для общего списка."
}
if ($Firm -and (Add-SwPlusFirm -Path (Join-Path $mprop "MProp_Firm.txt") -Name $Firm)) {
    Write-Warn "Организации «$Firm» нет в общем списке MProp — добавлена в локальный. Передайте администратору."
}

# 3. Профиль реестра
Write-Step "[3/7] Корпоративный профиль SolidWorks..."
$regKeyUser = "HKEY_CURRENT_USER\" + $U.Substring("HKCU:\".Length)
$backupRoot = Join-Path (Split-Path -Path $LocalRoot -Parent) "Backups"
[void](Backup-SolidWorksRegistryKeys -BackupRoot $backupRoot -RegistryKeys @("$regKeyUser\SolidWorks\$SwVersion", "$regKeyUser\SolidWorks\AddInsStartup"))
if (Test-Path -LiteralPath $layout.RegProfile) {
    $regText = [System.IO.File]::ReadAllText($layout.RegProfile, [System.Text.Encoding]::Unicode)
    $adapted = Convert-EskdRegProfile -Text $regText -SourceRoot $SourceRoot -LocalRoot $LocalRoot -SwVersion $SwVersion `
        -KeepMachineSections:$machine -RegistryRoot $U
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

# Toolbox — только если найден рядом с инструментарием или в стандартной папке; иначе прежнее значение не трогается
$toolbox = @(
    (Join-Path (Split-Path -Path $SourceRoot -Parent) "_Библиотека проектирования\_Toolbox"),
    "C:\SOLIDWORKS Data", "C:\SOLIDWORKS Data 2025"
) | Where-Object { (Test-Path (Join-Path $_ "lang\russian\swbrowser.sldedb")) -or (Test-Path (Join-Path $_ "lang\english\swbrowser.sldedb")) } |
    Select-Object -First 1
if ($toolbox) {
    Set-Reg "$swRoot\General" "Toolbox Data Location" $toolbox
    if ($machine) { Set-ItemProperty -Path "HKLM:\SOFTWARE\SolidWorks\$SwVersion\General" -Name "Toolbox Data Location" -Value $toolbox -ErrorAction SilentlyContinue }
    Write-Ok "Toolbox: $toolbox"
} else {
    Write-Info "Toolbox рядом с папкой инструментария не найден — путь Toolbox не меняется."
}
Set-Reg "$swRoot\Performance" "Use Performance Pipeline 2020" 0 "DWord"

# RealView для видеокарт этого ПК
try {
    foreach ($gpu in @(Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object { $_.Name } | Where-Object { $_ })) {
        Set-Reg "$U\SolidWorks\AllowList\Gl2Shaders\NV40\$gpu" "Workarounds" 0x30408 "DWord"
    }
} catch { Write-Info "Видеокарта не определена — RealView не настраивается." }

# 4. Очистка устаревших надстроек и вкладок
Write-Step "[4/7] Очистка устаревших надстроек и вкладок..."
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
foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
    $ctxPath = "$swRoot\User Interface\CommandManager\$ctx"
    if (-not (Test-Path -LiteralPath $ctxPath)) { continue }
    foreach ($gb in @("AssyContext", "DrwContext", "EditPartContext", "LAVContext", "PartContext", "QAT")) {
        Remove-Item -LiteralPath (Join-Path $ctxPath $gb) -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($tab in @(Get-ChildItem -LiteralPath $ctxPath -ErrorAction SilentlyContinue)) {
        $ref = [string](Get-RegValue $tab.PSPath "RefName")
        $props = [string](Get-RegValue $tab.PSPath "Tab Props")
        $mod = [string](Get-RegValue $tab.PSPath "ModuleName")
        $garbage = ($ref -match "OnCad|Ounan|Semantic") -or ($props -match "OnCad|Ounan|Semantic") -or ($mod -match "03412ba8|7A2F5C31|FF8D50772E2[134]") -or
            ($tab.PSChildName -like "Tab*" -and -not $ref.Trim() -and (-not $props.Trim() -or $props.StartsWith("0,")))
        if ($garbage) { Remove-Item -LiteralPath $tab.PSPath -Recurse -Force -ErrorAction SilentlyContinue; continue }
        if ($ref -match "ЕСКД" -or $mod -eq $activeGuid) {
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
Write-Step "[5/7] Кнопки SWPlus..."
$qat = "$swRoot\User Interface\CommandManager\QAT\GB0"
$buttons = [ordered]@{ "Btn11" = "1,33639"; "Btn12" = "1,33640"; "Btn13" = "1,33641"; "Btn14" = "1,33642"; "Btn15" = "1,33643";
                       "Btn16" = "1,33644"; "Btn17" = "1,33645"; "Btn18" = "1,33646"; "Btn19" = "1,33647" }
foreach ($b in $buttons.Keys) { Set-Reg $qat $b $buttons[$b] }
for ($cid = 33639; $cid -le 33647; $cid++) { Set-Reg "$swRoot\Menu Customizations" "$cid" 0 "DWord" }
Write-Ok "9 кнопок SWPlus: MProp, SProp, DProp, SpecEditor, RecordDimM, Roughness, TT, Master, SaveAsPDF."

# 6. Надстройка ЕСКД
Write-Step "[6/7] Надстройка ЕСКД..."
$registration = $null
try {
    . (Join-Path $layout.SourceAddin "Register-EskdAddin.ps1")
    $registration = Register-EskdAddin -DllPath $layout.LocalAddinDll -UserRoot $U -MachineRoot "HKLM:\Software"
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
    "Библиотека_Материалов_ГОСТ|Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89|1003",
    "Библиотека_Материалов_ГОСТ|Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89|1108",
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

# 7. Шрифты и Drew
Write-Step "[7/7] Шрифты ГОСТ и модуль Drew..."
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

if ($sandbox -or $SkipDrew) {
    Write-Info "Drew пропущен."
} else {
    $drewGuid = "{08c4bc0b-c36c-470e-a0ea-02232f023333}"
    $drewCandidates = @((Join-Path $env:ProgramFiles "CAD Booster\Drew\CADBooster.Drew.Drawing.dll"),
                        (Join-Path $env:LOCALAPPDATA "CAD Booster\Drew\CADBooster.Drew.Drawing.dll"))
    $drewDll = $drewCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    $drewDir = Join-Path $SourceRoot "03_Макросы_и_Плагины\Drw_System_Automation"
    $drewInstaller = Join-Path $drewDir "install-all.ps1"
    if (-not $drewDll -and (Test-Path -LiteralPath $drewInstaller)) {
        # install-all.ps1 завершает SolidWorks принудительно — запускается только при закрытом SolidWorks
        if (Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue) {
            Write-Warn "Модуль Drew не установлен: SolidWorks открыт. Закройте SolidWorks и запустите настройку ещё раз."
        } else {
            Write-Info "Установка модуля Drew (издание с активацией)..."
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $drewInstaller -Silent -NoActivate | Out-Null
            $drewDll = $drewCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        }
    }
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
        # Лицензия Drew — код активации по ключу железа этого ПК (3_активация\Client-Activate-Drew.ps1 пишет
        # %APPDATA%\CAD Booster\Drew\Activation.code). Без него Drew работает без лицензии.
        $activation = Join-Path $env:APPDATA "CAD Booster\Drew\Activation.code"
        if (Test-Path -LiteralPath $activation) {
            Write-Ok "Drew активирован (код от $((Get-Content -LiteralPath $activation -TotalCount 3)[2]))."
        } else {
            Write-Warn ("Drew не активирован: запустите " + (Join-Path $drewDir "3_активация\АКТИВИРОВАТЬ_DREW.cmd") +
                ", скопируйте ключ железа и передайте администратору; полученный код вставьте в то же окно.")
        }
    } else {
        Write-Warn "Модуль Drew не найден и не установлен."
    }
}

# Сведения об установке
$install = "$U\SolidWorks\ESKD_Install"
Set-Reg $install "SourceRoot" $SourceRoot
Set-Reg $install "LocalRoot" $LocalRoot
Set-Reg $install "ReleaseVersion" $release.Version
Set-Reg $install "ReleaseCommit" $release.Commit
Set-Reg $install "SwVersion" $SwVersion
Set-Reg $install "Author" $Author
Set-Reg $install "InstalledAt" (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Set-Reg $install "LastResult" $(if ($failures) { "FAILED" } else { "OK" })

Write-Host ""
if ($failures) {
    Write-Host "=================================================================" -ForegroundColor Red
    Write-Host " НАСТРОЙКА ЗАВЕРШЕНА С ОШИБКАМИ: $failures. Сообщения выше." -ForegroundColor Red
    Write-Host "=================================================================" -ForegroundColor Red
    exit 1
}
Write-Host "=================================================================" -ForegroundColor Green
Write-Host " НАСТРОЙКА ЗАВЕРШЕНА. Запустите SolidWorks." -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
exit 0
