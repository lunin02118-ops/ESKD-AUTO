<#
.SYNOPSIS
    Автоматическая настройка рабочего места SolidWorks (Корпоративный стандарт ЕСКД)
.DESCRIPTION
    Импортирует полный корпоративный профиль (Сборки, Чертежи, Оформление, Цвета, Панели инструментов, Макросы SWPlus),
    настраивает нативную надстройку ЕСКД v5 (реквизиты, масса, центрирование штампа),
    настраивает пути к библиотекам и шаблонам, Master.ini, MProp, шрифты ГОСТ и аппаратный RealView.
    Версия SolidWorks определяется автоматически (поддержка 2018-2025) либо задаётся параметром -SwVersion.
    Перед импортом .reg-профиля создаётся резервная копия веток реестра SolidWorks.
.PARAMETER ToolsRoot
    Корень каталога _Инструменты_Конструктора (по умолчанию определяется автоматически).
.PARAMETER Author
    Фамилия и инициалы конструктора для штампа чертежа (по умолчанию "Лунин В.И.").
.PARAMETER Firm
    Организация для основной надписи (по умолчанию "123").
.PARAMETER SwVersion
    Имя корневой ветки версии SolidWorks в реестре, например "SOLIDWORKS 2024".
    По умолчанию — автоопределение самой высокой установленной версии; при отсутствии данных — "SOLIDWORKS 2025".
.PARAMETER SkipClose
    Не закрывать SolidWorks, даже если он запущен (настройка выполняется на риск пользователя).
.PARAMETER KillSolidWorks
    Немедленное принудительное завершение SLDWORKS без подтверждения (режим автоматизации).
    Без ключа: подтверждение пользователя -> корректное закрытие через COM (SldWorks.Application -> ExitApp,
    ожидание до 30 секунд) -> принудительное завершение только как последняя мера.
.EXAMPLE
    .\Setup_Workstation_SolidWorks.ps1
.EXAMPLE
    .\Setup_Workstation_SolidWorks.ps1 -SwVersion "SOLIDWORKS 2024" -KillSolidWorks
#>

[CmdletBinding()]
param (
    [string]$ToolsRoot = "",
    [string]$Author = "",
    [string]$Firm = "",
    [string]$SwVersion = "",
    [switch]$SkipClose,
    [switch]$KillSolidWorks
)

#region Вспомогательные функции

function Test-IsAdmin {
    # Возвращает $true, если процесс запущен с правами администратора (B9).
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SolidWorksRegistryVersion {
    # B7 (DEP-5): определение целевой версии SolidWorks в реестре.
    # Возвращает имя корневой ветки вида "SOLIDWORKS 2024":
    #   1) явно заданное значение (допускается как "SOLIDWORKS 2024", так и "2024");
    #   2) самая высокая установленная версия (HKCU\Software\SolidWorks\SOLIDWORKS \d{4});
    #   3) "SOLIDWORKS 2025" — если установленых версий не найдено.
    param (
        [string]$Requested = ""
    )
    if ($Requested -and $Requested.Trim()) {
        $trimmed = $Requested.Trim()
        if ($trimmed -match '^\d{4}$') { return "SOLIDWORKS $trimmed" }
        return $trimmed
    }
    $bestName = ""
    $bestYear = 0
    if (Test-Path "HKCU:\Software\SolidWorks") {
        $children = Get-ChildItem "HKCU:\Software\SolidWorks" -ErrorAction SilentlyContinue
        foreach ($child in $children) {
            if ($child.PSChildName -match '^SOLIDWORKS (\d{4})$') {
                $year = [int]$Matches[1]
                if ($year -gt $bestYear) {
                    $bestYear = $year
                    $bestName = $child.PSChildName
                }
            }
        }
    }
    if ($bestName) { return $bestName }
    return "SOLIDWORKS 2025"
}

function Close-SolidWorks {
    # B2 (DEP-2): безопасное закрытие SolidWorks вместо безусловного Stop-Process -Force.
    #   -Force     : немедленное принудительное завершение (ключ -KillSolidWorks, для автоматизации);
    #   -SkipClose : не закрывать вообще (приоритет над -Force);
    #   по умолчанию: перечень процессов + предупреждение о несохранённых данных + подтверждение (Read-Host),
    #                 затем корректное закрытие через COM, ожидание до 30 секунд, Stop-Process как последняя мера.
    # Возвращает $true, если процессы SLDWORKS отсутствуют/завершены либо закрытие осознанно пропущено;
    # $false — пользователь отказался закрыть SolidWorks или завершить процессы не удалось.
    param (
        [switch]$Force,
        [switch]$SkipClose
    )

    $procs = Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue
    if (-not $procs) { return $true }

    if ($SkipClose) {
        Write-Host "  SolidWorks запущен (пропуск закрытия по ключу -SkipClose). Настройка продолжается." -ForegroundColor Yellow
        return $true
    }

    if (-not $Force) {
        Write-Host "  Обнаружены запущенные процессы SolidWorks:" -ForegroundColor Yellow
        foreach ($p in $procs) {
            $title = ""
            try { $title = $p.MainWindowTitle } catch { }
            $started = ""
            try { $started = $p.StartTime.ToString("yyyy-MM-dd HH:mm:ss") } catch { }
            Write-Host ("    PID {0}  запущен {1}  {2}" -f $p.Id, $started, $title) -ForegroundColor Gray
        }
        Write-Host "  ВНИМАНИЕ: при закрытии несохранённые изменения в открытых документах будут ПОТЕРЯНЫ." -ForegroundColor Red
        if (-not [Environment]::UserInteractive) {
            Write-Host "  Сессия неинтерактивна: подтверждение невозможно. Закройте SolidWorks вручную" -ForegroundColor Yellow
            Write-Host "  или перезапустите сценарий с ключом -KillSolidWorks." -ForegroundColor Yellow
            return $false
        }
        $answer = Read-Host "  Закрыть SolidWorks сейчас? [Y - да (сначала корректное закрытие через COM) / N - прервать настройку]"
        if ($answer -notmatch '^(y|yes|д|да)$') {
            Write-Host "  Отказ пользователя: настройка прервана (SolidWorks перезапишет часть настроек реестра при закрытии)." -ForegroundColor Yellow
            return $false
        }
    } else {
        Write-Host "  Режим -KillSolidWorks: принудительное завершение SLDWORKS без сохранения (автоматизация)..." -ForegroundColor Yellow
    }

    if (-not $Force) {
        # Корректное закрытие через COM: даёт SolidWorks шанс сохранить документы и корректно выгрузить надстройки.
        # ISldWorks::ExitApp — единственный документированный метод завершения (метода Quit в API нет).
        try {
            $swApp = [Runtime.InteropServices.Marshal]::GetActiveObject('SldWorks.Application')
            if ($swApp) {
                [void]$swApp.ExitApp()
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($swApp)
                Write-Host "  Команда ExitApp отправлена через COM, ожидание завершения (до 30 секунд)..." -ForegroundColor Gray
            }
        } catch {
            Write-Host "  COM-автоматизация недоступна ($($_.Exception.Message)) — переход к принудительному завершению." -ForegroundColor Yellow
        }
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            if (-not (Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 500
        }
    }

    $remaining = Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue
    if ($remaining) {
        Write-Host "  Процессы SLDWORKS не завершились корректно — принудительное завершение (последняя мера)." -ForegroundColor Yellow
        Stop-Process -Name "SLDWORKS" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    } else {
        Write-Host "  SolidWorks корректно закрыт." -ForegroundColor Green
    }

    $finalCheck = Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue
    if ($finalCheck) {
        Write-Host "  [ОШИБКА] Не удалось завершить процессы SLDWORKS. Дальнейшая настройка бессмысленна." -ForegroundColor Red
        return $false
    }
    return $true
}

function Backup-SolidWorksRegistryKeys {
    # B3 (DEP-3): резервная копия веток реестра (reg.exe export) перед импортом .reg-профиля.
    # Возвращает путь к каталогу резервной копии.
    param (
        [string]$BackupRoot,
        [string[]]$RegistryKeys
    )
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupDir = Join-Path $BackupRoot "Backup_$stamp"
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    $okCount = 0
    foreach ($key in $RegistryKeys) {
        $psDrivePath = $key -replace '^HKCU\\', 'HKCU:\'
        if (-not (Test-Path $psDrivePath)) {
            Write-Host "  [ИНФО] Ветка '$key' отсутствует — резервная копия не требуется (первая установка)." -ForegroundColor DarkGray
            continue
        }
        $safeName = ($key -replace '[\\/:*?"<>|]', '_') + ".reg"
        $outFile = Join-Path $backupDir $safeName
        $null = & reg.exe export "$key" "$outFile" /y 2>&1
        if (($LASTEXITCODE -eq 0) -and (Test-Path $outFile)) {
            $okCount++
            Write-Host "    + $key -> $safeName" -ForegroundColor DarkGray
        } else {
            Write-Host "  [ПРЕДУПРЕЖДЕНИЕ] Не удалось создать резервную копию ветки '$key' (код reg.exe: $LASTEXITCODE)." -ForegroundColor Yellow
        }
    }
    if ($okCount -gt 0) {
        Write-Host "  [OK] Резервная копия реестра создана: $backupDir" -ForegroundColor Green
    } else {
        Write-Host "  [ИНФО] Резервные копии не созданы (исходные ветки отсутствуют)." -ForegroundColor DarkGray
    }
    return $backupDir
}

#endregion

if (-not $ToolsRoot) {
    $cand = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
    if (Test-Path (Join-Path $cand "04_Библиотеки_Материалов_и_Профилей")) {
        $ToolsRoot = $cand
    } elseif (Test-Path (Join-Path (Split-Path $cand -Parent) "04_Библиотеки_Материалов_и_Профилей")) {
        $ToolsRoot = Split-Path $cand -Parent
    } else {
        $ToolsRoot = $cand
    }
}
$ToolsRoot = (Resolve-Path $ToolsRoot).Path

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " НАСТРОЙКА РАБОЧЕГО МЕСТА SOLIDWORKS (КОРПОРАТИВНЫЙ СТАНДАРТ) " -ForegroundColor Yellow
Write-Host "=================================================================" -ForegroundColor Cyan

# B7 (DEP-5): целевая версия SolidWorks — единая точка определения вместо хардкода "SOLIDWORKS 2025".
$SwVersion = Get-SolidWorksRegistryVersion -Requested $SwVersion
$swRegRoot = "HKCU:\Software\SolidWorks\$SwVersion"
$swRegRootHklm = "HKLM:\SOFTWARE\SolidWorks\$SwVersion"
Write-Host "Целевая версия SolidWorks: $SwVersion ($swRegRoot)" -ForegroundColor White

# B9: диагностика прав — при запуске без администратора явно перечисляем пропускаемые операции
# (вместо молчаливых SilentlyContinue). Авто-повышение прав НЕ выполняется.
$isAdmin = Test-IsAdmin
if (-not $isAdmin) {
    Write-Host "`nВНИМАНИЕ: сценарий запущен БЕЗ прав администратора." -ForegroundColor Yellow
    Write-Host "Будут ПРОПУЩЕНЫ или выполнены частично следующие операции:" -ForegroundColor Yellow
    Write-Host "  - HKLM-регистрация надстройки ЕСКД (выполняется только HKCU-регистрация);"
    Write-Host "  - HKLM-секции .reg-профиля (импорт частичный: HKCU-секция OK, HKLM пропущена);"
    Write-Host "  - запись пути Toolbox в HKLM (выполняется только HKCU);"
    Write-Host "  - RegAsm-регистрация в HKLM (HKCU COM-записи создаются вручную);"
    Write-Host "  - системная установка шрифтов ГОСТ в C:\Windows\Fonts (выполняется per-user установка"
    Write-Host "    в %LOCALAPPDATA%\Microsoft\Windows\Fonts, требуется Windows 10 1709+);"
    Write-Host "  - удаление HKLM-ключей устаревших надстроек (OnCadTools и др.) и HKLM-веток Classes."
    Write-Host "Для полного применения запустите сценарий от имени администратора." -ForegroundColor Yellow
}

# 0. Определение автора и организации по умолчанию (B11: канонические значения)
if (-not $Author) {
    try {
        $regAuth = (Get-ItemProperty -Path "HKCU:\Software\SolidWorks\ESKD_Settings" -Name "Author" -ErrorAction SilentlyContinue).Author
        if ($regAuth -and $regAuth.Trim()) { $Author = $regAuth.Trim() }
    } catch { }
}

# Попытка определить имя пользователя из учетной записи Windows
if (-not $Author) {
    try {
        $userObj = Get-CimInstance Win32_UserAccount -Filter "Name='$env:USERNAME'" -ErrorAction SilentlyContinue
        if ($userObj -and $userObj.FullName -and $userObj.FullName.Trim()) {
            $Author = $userObj.FullName.Trim()
        }
    } catch { }
}

# Если не определено, используем значение по умолчанию или запрашиваем в интерактивном режиме
$defaultAuthor = "Лунин В.И."
if (-not $Author) {
    if ([Environment]::UserInteractive -and -not $ToolsRoot.Contains("NonInteractive")) {
        $prompt = Read-Host "Введите фамилию и инициалы конструктора для штампа чертежа [Enter для '$defaultAuthor']"
        if ($prompt -and $prompt.Trim()) {
            $Author = $prompt.Trim()
        } else {
            $Author = $defaultAuthor
        }
    } else {
        $Author = $defaultAuthor
    }
}

if (-not $Firm) {
    try {
        $regFirm = (Get-ItemProperty -Path "HKCU:\Software\SolidWorks\ESKD_Settings" -Name "Organization" -ErrorAction SilentlyContinue).Organization
        if ($regFirm -and $regFirm.Trim()) { $Firm = $regFirm.Trim() }
    } catch { }
}
if (-not $Firm) { $Firm = "123" }

Write-Host "Конструктор (Разраб.): $Author" -ForegroundColor White
Write-Host "Организация (Контора): $Firm" -ForegroundColor White

# 1. Закрытие SolidWorks и очистка аварийных надстроек
Write-Host "`n[1/6] Проверка запущенных процессов SolidWorks..." -ForegroundColor Gray
# B2 (DEP-2): подтверждение + graceful-закрытие через COM; -KillSolidWorks — только для автоматизации.
$swClosed = Close-SolidWorks -Force:$KillSolidWorks -SkipClose:$SkipClose
if (-not $swClosed) {
    Write-Host "  Настройка прервана: изменения в реестр не вносились." -ForegroundColor Yellow
    exit 1
}

# Блокировка и удаление устаревших сторонних надстроек (OnCadTools и устаревшие версии)
$unwantedAddinGuids = @(
    "{03412ba8-10f6-4d51-ac38-4937ce7bea5f}", # OnCadTools
    "{7a2f5c31-9e44-4b0d-8c21-5f0e9a4b77c2}"  # OnCadTools Shim
)
foreach ($ug in $unwantedAddinGuids) {
    Remove-Item "HKLM:\SOFTWARE\SolidWorks\AddIns\$ug" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKLM:\SOFTWARE\SolidWorks\AddInsStartup\$ug" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKLM:\SOFTWARE\WOW6432Node\SolidWorks\AddIns\$ug" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKLM:\SOFTWARE\WOW6432Node\SolidWorks\AddInsStartup\$ug" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\SOFTWARE\SolidWorks\AddIns\$ug" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\SolidWorks\AddInsStartup\$ug" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\SolidWorks\AddInsEntitlement\$ug" -Recurse -Force -ErrorAction SilentlyContinue
}

# Очистка COM-классов и ProgID OnCadTools в Classes
# B10 (DEP-14): вместо полного перебора HKLM:\SOFTWARE\Classes — прямой wildcard-запрос "OnCadTools*"
# (разрешение имён на уровне провайдера реестра, без рекурсии и без чтения значений каждого ключа).
# HKLM-ветки очищаются только с правами администратора (без них удаление заведомо бесплодно).
foreach ($rootClass in @("HKCU:\Software\Classes", "HKLM:\SOFTWARE\Classes")) {
    $rootIsHklm = ($rootClass -eq "HKLM:\SOFTWARE\Classes")
    if ($rootIsHklm -and -not $isAdmin) { continue }
    Get-ChildItem -Path (Join-Path $rootClass "OnCadTools*") -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item -Path $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($ug in $unwantedAddinGuids) {
        Remove-Item (Join-Path $rootClass "CLSID\$ug") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $rootClass "WOW6432Node\CLSID\$ug") -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Очистка вкладок CommandManager и TaskPane от OnCadTools, Ounan
foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
    $ctxPath = "$swRegRoot\User Interface\CommandManager\$ctx"
    if (Test-Path $ctxPath) {
        Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "Tab*" } | ForEach-Object {
            $ref = (Get-ItemProperty -Path $_.PSPath -Name "RefName" -ErrorAction SilentlyContinue).RefName
            $props = (Get-ItemProperty -Path $_.PSPath -Name "Tab Props" -ErrorAction SilentlyContinue)."Tab Props"
            $mod = (Get-ItemProperty -Path $_.PSPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName
            if (($ref -and ($ref -match "OnCad|Ounan")) -or 
                ($props -and ($props -match "OnCad|Ounan")) -or
                ($mod -and ($mod -match "03412ba8|7A2F5C31"))) {
                Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
$flyoutsPath = "$swRegRoot\User Interface\Custom API Flyouts"
if (Test-Path $flyoutsPath) {
    Get-ChildItem $flyoutsPath -ErrorAction SilentlyContinue | ForEach-Object {
        $mod = (Get-ItemProperty $_.PSPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName
        if ($mod -and ($mod -match "03412ba8|7A2F5C31")) {
            Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
Remove-Item "$swRegRoot\User Interface\TaskPane\Инструменты Ounan (OnCadTools)" -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem "$swRegRoot\User Interface\TaskPane" -ErrorAction SilentlyContinue | Where-Object { 
    $_.Name -match "OnCad|Ounan" 
} | ForEach-Object {
    Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-ItemProperty -Path "$swRegRoot\General\Addin Performance" -Name "OnCadTools" -ErrorAction SilentlyContinue


# Очистка устаревших версий надстройки ЕСКД во избежание дубликатов
$oldGuids = @(
    "{B64E6875-B101-4D5C-B245-FF8D50772E21}",
    "{B64E6875-B101-4D5C-B245-FF8D50772E23}",
    "{B64E6875-B101-4D5C-B245-FF8D50772E24}"
)
foreach ($og in $oldGuids) {
    Remove-Item "HKCU:\Software\SolidWorks\AddIns\$og" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\SolidWorks\AddInsStartup\$og" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKLM:\Software\SolidWorks\AddIns\$og" -Recurse -Force -ErrorAction SilentlyContinue
}

# Удаление остатков и вкладок Semantic / Semantic MDM.
# B1 (DEP-1): блок безусловного удаления вкладок по жёстко зашитым номерам (Tab19/Tab20/Tab14/Tab15/Tab7/Tab8)
# УДАЛЁН — на чужом рабочем месте он сносил пользовательские вкладки. Осталась только идентификация
# по содержимому ключа (RefName / "Tab Props" содержат "Semantic") — чужие вкладки не затрагиваются.
foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
    $ctxPath = "$swRegRoot\User Interface\CommandManager\$ctx"
    if (Test-Path $ctxPath) {
        Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "Tab*" } | ForEach-Object {
            $ref = (Get-ItemProperty -Path $_.PSPath -Name "RefName" -ErrorAction SilentlyContinue).RefName
            $props = (Get-ItemProperty -Path $_.PSPath -Name "Tab Props" -ErrorAction SilentlyContinue)."Tab Props"
            if (($ref -and ($ref -like "*Semantic*")) -or ($props -and ($props -like "*Semantic*"))) {
                Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
Remove-ItemProperty -Path "$swRegRoot\General\Addin Performance" -Name "Semantic" -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "$swRegRoot\General\Addin Performance" -Name "Semantic MDM" -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\SDI Solution" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "HKLM:\SOFTWARE\SDI Solution" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "HKLM:\SOFTWARE\WOW6432Node\SDI Solution" -Recurse -Force -ErrorAction SilentlyContinue

# 2. Определение путей
$ToolsRoot = (Resolve-Path $ToolsRoot).Path
Write-Host "`n[2/6] Проверка путей в: $ToolsRoot" -ForegroundColor Gray

$regProfilesDir = Join-Path $ToolsRoot "01_Настройки_SolidWorks\Реестровые_Профили"
$regProfile = Join-Path $regProfilesDir "01_SW2025_Корпоративный_Стандарт_ЕСКД.reg"
# B7: если целевая версия отличается от 2025 — предпочитаем профиль этой версии, если он существует
$swYear = ""
if ($SwVersion -match 'SOLIDWORKS (\d{4})$') { $swYear = $Matches[1] }
if ($swYear -and ($swYear -ne "2025")) {
    $versionedProfile = Join-Path $regProfilesDir "01_SW${swYear}_Корпоративный_Стандарт_ЕСКД.reg"
    if (Test-Path $versionedProfile) { $regProfile = $versionedProfile }
}
$swplusRoots = @(
    (Join-Path $ToolsRoot "03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0")
)
$activeSwplus = $swplusRoots | Where-Object { Test-Path $_ } | Select-Object -First 1
$sheetFormats = if ($activeSwplus) { Join-Path $activeSwplus "Основные надписи" } else { Join-Path $swplusRoots[0] "Основные надписи" }
if (-not (Test-Path $sheetFormats)) {
    New-Item -ItemType Directory -Path $sheetFormats -Force | Out-Null
}

# 3. Master.ini, MProp и база ТТ
Write-Host "`n[3/6] Синхронизация Master.ini, MProp и базы ТТ..." -ForegroundColor Gray
foreach ($spRoot in $swplusRoots) {
    if (Test-Path $spRoot) {
        $masterIni = Join-Path $spRoot "Master\Master.ini"
        if (Test-Path $masterIni) {
            $lines = [System.IO.File]::ReadAllLines($masterIni, [System.Text.Encoding]::GetEncoding(1251))
            if ($lines.Count -ge 4) {
                $lines[3] = $sheetFormats.TrimEnd('\') + '\'
                [System.IO.File]::WriteAllLines($masterIni, $lines, [System.Text.Encoding]::GetEncoding(1251))
            }
        }

        $mpropDir = Join-Path $spRoot "MProp"
        if (Test-Path $mpropDir) {
            # Синхронизация списка фамилий (накопительно, не затирая коллег по отделу)
            $famFile = Join-Path $mpropDir "MProp_Fam.txt"
            $famList = @()
            if (Test-Path $famFile) {
                $famList = [System.IO.File]::ReadAllLines($famFile, [System.Text.Encoding]::GetEncoding(1251)) | Where-Object { $_.Trim() -ne "" }
            }
            if ($Author -and ($famList -notcontains $Author)) {
                $famList = @($Author) + $famList
            }
            if ($famList.Count -gt 0) {
                [System.IO.File]::WriteAllLines($famFile, $famList, [System.Text.Encoding]::GetEncoding(1251))
            }

            # Синхронизация списка организаций (формат MProp: четная/нечетная строка)
            $firmFile = Join-Path $mpropDir "MProp_Firm.txt"
            $firmPairs = @()
            $existingFirmNames = @()
            if (Test-Path $firmFile) {
                $rawFirms = [System.IO.File]::ReadAllLines($firmFile, [System.Text.Encoding]::GetEncoding(1251))
                for ($i = 0; $i -lt $rawFirms.Count; $i += 2) {
                    $fName = $rawFirms[$i].Trim()
                    $fCode = if ($i + 1 -lt $rawFirms.Count) { $rawFirms[$i + 1].Trim() } else { "" }
                    if ($fName) {
                        $firmPairs += ,@($fName, $fCode)
                        $existingFirmNames += $fName
                    }
                }
            }
            if ($Firm -and ($existingFirmNames -notcontains $Firm)) {
                $firmPairs = ,@($Firm, "") + $firmPairs
            }
            $outLines = @()
            foreach ($pair in $firmPairs) {
                $outLines += $pair[0]
                $outLines += $pair[1]
            }
            if ($outLines.Count -gt 0) {
                [System.IO.File]::WriteAllLines($firmFile, $outLines, [System.Text.Encoding]::GetEncoding(1251))
            }
        }
    }
}
Write-Host "  [OK] Master.ini и MProp ($Author / $Firm) синхронизированы в SWPlus SP0.1 и SP0.0." -ForegroundColor Green

# Очистка базы ТТ от пустых строк
$ttFiles = @(
    (Join-Path $ToolsRoot "03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\ТТ\TT_Prof.txt"),
    (Join-Path $ToolsRoot "03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\ТТ\TT.TXT")
)
foreach ($ttf in $ttFiles) {
    if (Test-Path $ttf) {
        $nonEmpty = [System.IO.File]::ReadAllLines($ttf, [System.Text.Encoding]::GetEncoding(1251)) | Where-Object { $_.Trim() -ne "" }
        [System.IO.File]::WriteAllLines($ttf, $nonEmpty, [System.Text.Encoding]::GetEncoding(1251))
    }
}
Write-Host "  [OK] База технических требований (ТТ) проверена и очищена от пустых строк." -ForegroundColor Green

# 4. Импорт полного корпоративного реестрового профиля с адаптацией путей
Write-Host "`n[4/6] Импорт полного реестрового профиля $SwVersion..." -ForegroundColor Gray
if (Test-Path $regProfile) {
    # B3 (DEP-3): резервная копия текущих настроек SolidWorks ДО импорта профиля.
    Write-Host "  Резервное копирование реестра перед импортом профиля..." -ForegroundColor Gray
    $backupKeys = @(
        "HKCU\Software\SolidWorks\$SwVersion",
        "HKCU\Software\SolidWorks\AddInsStartup"
    )
    [void](Backup-SolidWorksRegistryKeys -BackupRoot (Join-Path $regProfilesDir "Backups") -RegistryKeys $backupKeys)

    # Динамическая адаптация всех путей реестра под текущее размещение _Инструменты_Конструктора
    $escDouble = $ToolsRoot.Replace('\', '\\')
    $regText = [System.IO.File]::ReadAllText($regProfile, [System.Text.Encoding]::Unicode)
    $pattern = '[A-Za-z]:(?:\\\\+|/)[^"\r\n;]*?(?:\\\\+|/)_Инструменты_Конструктора'
    $adaptedText = [System.Text.RegularExpressions.Regex]::Replace(
        $regText,
        $pattern,
        [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $escDouble },
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    )
    # B7: профиль собран под "SOLIDWORKS 2025" — при другой целевой версии переписываем корневую ветку версии
    if ($swYear -and ($SwVersion -ne "SOLIDWORKS 2025")) {
        $adaptedText = [System.Text.RegularExpressions.Regex]::Replace($adaptedText, 'SOLIDWORKS 20\d{2}', $SwVersion)
    }
    $tempReg = Join-Path $ToolsRoot "01_Настройки_SolidWorks\_temp_import.reg"
    [System.IO.File]::WriteAllText($tempReg, $adaptedText, [System.Text.Encoding]::Unicode)

    # B5 (DEP-7): импорт с явной проверкой кода возврата reg.exe (раньше результат не проверялся)
    $importOutput = @(& reg.exe import "$tempReg" 2>&1)
    $importExitCode = $LASTEXITCODE
    if (Test-Path $tempReg) { Remove-Item $tempReg -Force -ErrorAction SilentlyContinue }
    if ($importExitCode -eq 0) {
        Write-Host "  [OK] Полный корпоративный профиль $SwVersion успешно импортирован (пути адаптированы)." -ForegroundColor Green
    } else {
        Write-Host "  [ОШИБКА] Импорт реестрового профиля завершился с ошибкой (код reg.exe: $importExitCode)." -ForegroundColor Red
        foreach ($line in $importOutput) {
            $msg = "$line"
            if ($msg.Trim()) { Write-Host "    reg.exe: $msg" -ForegroundColor DarkRed }
        }
        Write-Host "  Наиболее вероятные причины:" -ForegroundColor Yellow
        Write-Host "    - HKLM-секции профиля требуют прав администратора;"
        Write-Host "    - часть веток реестра заблокирована запущенным SolidWorks или другими процессами;"
        Write-Host "    - файл профиля повреждён или недоступен для чтения."
        if (-not $isAdmin) {
            Write-Host "  Скрипт запущен без прав администратора: профиль импортирован ЧАСТИЧНО (HKCU-секция OK, HKLM пропущена)." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  [ОШИБКА] Файл профиля не найден: $regProfile" -ForegroundColor Red
}

# 4.0. Очистка устаревших надстроек и фантомных вкладок CommandManager
$oldGuids = @(
    "{B64E6875-B101-4D5C-B245-FF8D50772E21}",
    "{B64E6875-B101-4D5C-B245-FF8D50772E23}",
    "{B64E6875-B101-4D5C-B245-FF8D50772E24}"
)
foreach ($og in $oldGuids) {
    Remove-Item "HKCU:\Software\SolidWorks\AddIns\$og" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\SolidWorks\AddInsStartup\$og" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKLM:\Software\SolidWorks\AddIns\$og" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKLM:\Software\SolidWorks\AddInsStartup\$og" -Recurse -Force -ErrorAction SilentlyContinue
}

$contexts = @("PartContext", "AssyContext", "DrwContext")
$activeGuid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
$garbageSubkeys = @("AssyContext", "DrwContext", "EditPartContext", "LAVContext", "PartContext", "QAT")

foreach ($ctx in $contexts) {
    $ctxPath = "$swRegRoot\User Interface\CommandManager\$ctx"
    if (-not (Test-Path $ctxPath)) { New-Item -Path $ctxPath -Force | Out-Null }

    # 1. Удаление ошибочно вложенных папок контекстов (порождали пустые строки в меню вкладок)
    foreach ($gb in $garbageSubkeys) {
        $gbPath = Join-Path $ctxPath $gb
        if (Test-Path $gbPath) {
            Remove-Item -Path $gbPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # 2. Поиск вкладки ЕСКД и удаление безымянных/пустых фантомных вкладок Tab* (с Tab Props = "0,1,1,-1" и пустым RefName)
    $tabItems = Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue
    if ($tabItems) {
        foreach ($item in $tabItems) {
            $tabPath = $item.PSPath
            $refName = (Get-ItemProperty -Path $tabPath -Name "RefName" -ErrorAction SilentlyContinue).RefName
            $tabProps = (Get-ItemProperty -Path $tabPath -Name "Tab Props" -ErrorAction SilentlyContinue)."Tab Props"
            $modName = (Get-ItemProperty -Path $tabPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName

            # Удаление устаревших надстроек
            if ($modName -and ($oldGuids -contains $modName.ToUpper())) {
                Remove-Item -Path $tabPath -Recurse -Force -ErrorAction SilentlyContinue
                continue
            }

            # Удаление пустых фантомных вкладок (порождают пустые пункты с галочками в меню вкладок)
            if (($item.PSChildName -like "Tab*") -and 
                (-not $refName -or $refName.Trim() -eq "") -and 
                (-not $tabProps -or $tabProps.StartsWith("0,") -or $tabProps.Trim() -eq "")) {
                Remove-Item -Path $tabPath -Recurse -Force -ErrorAction SilentlyContinue
                continue
            }

            # Фиксация корпоративной вкладки ЕСКД
            if (($refName -and ($refName -match "ЕСКД")) -or ($modName -and ($modName.ToUpper() -eq $activeGuid.ToUpper()))) {
                Set-ItemProperty -Path $tabPath -Name "RefName" -Value "ЕСКД" -Force -ErrorAction SilentlyContinue
                Set-ItemProperty -Path $tabPath -Name "ModuleName" -Value $activeGuid -Force -ErrorAction SilentlyContinue
                Set-ItemProperty -Path $tabPath -Name "Tab Props" -Value "ЕСКД,1,1,-1" -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

# 4.0.1. Гарантированное закрепление 9 кнопок макросов SWPlus в верхней панели QAT (Quick Access Toolbar)
$qatGb0Path = "$swRegRoot\User Interface\CommandManager\QAT\GB0"
if (-not (Test-Path $qatGb0Path)) { New-Item -Path $qatGb0Path -Force | Out-Null }
$qatButtons = [ordered]@{
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
foreach ($btn in $qatButtons.Keys) {
    Set-ItemProperty -Path $qatGb0Path -Name $btn -Value $qatButtons[$btn] -Force -ErrorAction SilentlyContinue
}

$menuCustPath = "$swRegRoot\Menu Customizations"
if (-not (Test-Path $menuCustPath)) { New-Item -Path $menuCustPath -Force | Out-Null }
for ($cid = 33639; $cid -le 33647; $cid++) {
    Set-ItemProperty -Path $menuCustPath -Name "$cid" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
}

# B8 (DEP-12): точечная очистка вместо удаления ВСЕГО дерева Custom API Flyouts
# (безусловное удаление сносило панели Drew и других надстроек). Удаляются только:
#   1) значение/подключ ToolbarChangesOnUpgrade (предотвращает диалог сброса тулбаров при старте);
#   2) flyout-ключи, достоверно относящиеся к семейству ЕСКД (ModuleName содержит B64E6875).
# Flyouts сторонних надстроек (Drew и др.) не затрагиваются.
$toolbarsPath = "$swRegRoot\User Interface\Toolbars"
if (Test-Path $toolbarsPath) {
    Remove-ItemProperty -Path $toolbarsPath -Name "ToolbarChangesOnUpgrade" -ErrorAction SilentlyContinue
}
$toolbarUpgradeSubkey = "$toolbarsPath\ToolbarChangesOnUpgrade"
if (Test-Path $toolbarUpgradeSubkey) {
    Remove-Item -Path $toolbarUpgradeSubkey -Recurse -Force -ErrorAction SilentlyContinue
}
$eskdFlyoutsPath = "$swRegRoot\User Interface\Custom API Flyouts"
if (Test-Path $eskdFlyoutsPath) {
    Get-ChildItem $eskdFlyoutsPath -ErrorAction SilentlyContinue | ForEach-Object {
        $mod = (Get-ItemProperty $_.PSPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName
        if ($mod -and ($mod -match "B64E6875")) {
            Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
Write-Host "  [OK] 9 кнопок макросов SWPlus зафиксированы в верхней панели быстрого доступа (QAT: 33639-33647)." -ForegroundColor Green

# 4.1. Автоматическая привязка Toolbox и фиксация стабильной графики
$toolboxCandidates = @(
    (Join-Path (Split-Path $ToolsRoot -Parent) "_Библиотека проектирования\_Toolbox"),
    "D:\Work\_Библиотека проектирования\_Toolbox",
    "C:\SOLIDWORKS Data",
    "C:\SOLIDWORKS Data 2025"
)
$toolboxPath = $null
foreach ($tb in $toolboxCandidates) {
    if ((Test-Path (Join-Path $tb "lang\english\swbrowser.sldedb")) -or (Test-Path (Join-Path $tb "lang\russian\swbrowser.sldedb"))) {
        $toolboxPath = $tb
        break
    }
}
if ($toolboxPath) {
    Set-ItemProperty -Path "$swRegRoot\General" -Name "Toolbox Data Location" -Value $toolboxPath -ErrorAction SilentlyContinue
    if ($isAdmin) {
        Set-ItemProperty -Path $swRegRootHklm -Name "Toolbox Data Location" -Value $toolboxPath -ErrorAction SilentlyContinue
    }
    Write-Host "  [OK] База данных стандартов Toolbox зафиксирована: $toolboxPath" -ForegroundColor Green
}
Set-ItemProperty -Path "$swRegRoot\Performance" -Name "Use Performance Pipeline 2020" -Value 0 -ErrorAction SilentlyContinue
Write-Host "  [OK] Графический режим переведен в безопасный режим (черный экран устранен)." -ForegroundColor Green

# 5. Регистрация нативной надстройки ЕСКД v5 (CommandManager, Настройки, Центрирование массы)
Write-Host "`n[5/6] Регистрация нативной надстройки ЕСКД и панели управления..." -ForegroundColor Gray
$addinDll = Join-Path $ToolsRoot "03_Макросы_и_Плагины\ESKD_Material_Sync_Addin\ESKD_Material_Sync_v5.dll"
$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

# Автокомпиляция надстройки из исходников, если DLL отсутствует (чистый clone репозитория).
# Без DLL ниже пропускались бы не только COM-регистрация, но и Избранные материалы,
# ESKD_Settings и вкладки CommandManager — теперь гарантированно выполняется весь блок.
if (-not (Test-Path $addinDll)) {
    $buildScript = Join-Path $ToolsRoot "03_Макросы_и_Плагины\ESKD_Material_Sync_Addin\build_and_register.ps1"
    if (Test-Path $buildScript) {
        Write-Host "  [ИНФО] DLL надстройки не найдена — автоматическая сборка из исходников..." -ForegroundColor Yellow
        & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript
        if (Test-Path $addinDll) {
            Write-Host "  [OK] Надстройка скомпилирована: $addinDll" -ForegroundColor Green
        } else {
            Write-Host "  [ОШИБКА] Автокомпиляция не удалась — блок [5/6] будет пропущен." -ForegroundColor Red
        }
    }
}

if ((Test-Path $addinDll) -and (Test-Path $regasm)) {
    # DEP-11 (эмпирически 2026-09-12): CLR не активирует сборку по percent-encoded file:///URI
    # с кириллицей — работает только сырая форма (как пишет RegAsm). Не экранировать!
    $codebase = "file:///" + $addinDll.Replace('\', '/')
    # CR#12: проверка кода возврата RegAsm вместо молчаливого пропуска сбоя
    $regasmProc = Start-Process -FilePath $regasm -ArgumentList "/codebase `"$addinDll`"" -Wait -NoNewWindow -PassThru
    if ($regasmProc.ExitCode -ne 0) {
        Write-Host "  [ВНИМАНИЕ] RegAsm завершился с кодом $($regasmProc.ExitCode) — HKLM-регистрация может быть неполной (HKCU-регистрация ниже это компенсирует)." -ForegroundColor Yellow
    }
    $guid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
    $title = "ЕСКД: Синхронизация материалов и реквизитов"
    $desc = "Панель инструментов ЕСКД: настройки реквизитов (фамилии, контора, масса), автоматическая синхронизация материалов и центрирование штампа по ГОСТ 2.104"

    # HKLM (только с правами администратора; без них работает HKCU-регистрация ниже)
    if ($isAdmin) {
        try {
            $hklmPath = "HKLM:\Software\SolidWorks\AddIns\$guid"
            if (-not (Test-Path $hklmPath -ErrorAction SilentlyContinue)) { New-Item -Path $hklmPath -Force -ErrorAction SilentlyContinue | Out-Null }
            Set-ItemProperty -Path $hklmPath -Name "(Default)" -Value 1 -Type DWord -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $hklmPath -Name "Title" -Value $title -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $hklmPath -Name "Description" -Value $desc -ErrorAction SilentlyContinue
            # DEP-11: RegAsm записывает CodeBase в percent-escaped форме ("%D0%98..."),
            # CLR НЕ активирует сборку по такому URI с кириллицей в пути. Прошиваем сырую
            # (RAW) форму в HKLM вручную — иначе SolidWorks, запущенный от администратора,
            # не загрузит надстройку (он активирует COM через HKLM, минуя HKCU).
            foreach ($hive in @("HKLM:\Software\Classes\CLSID\$guid\InprocServer32",
                                "HKLM:\Software\Classes\CLSID\$guid\InprocServer32\1.0.0.0")) {
                if (Test-Path $hive) {
                    Set-ItemProperty -Path $hive -Name "CodeBase" -Value $codebase -ErrorAction SilentlyContinue
                }
            }
        } catch { }
    } else {
        Write-Host "  [ИНФО] Без прав администратора: регистрация ЕСКД выполняется только в HKCU." -ForegroundColor DarkGray
    }

    $propFolders = Join-Path $ToolsRoot "02_Шаблоны_и_Форматки\Шаблоны свойств"
    if (Test-Path $propFolders) {
        Set-ItemProperty -Path "$swRegRoot\ExtReferences" -Name "Custom Property Folders" -Value $propFolders -ErrorAction SilentlyContinue
        Set-ItemProperty -Path "$swRegRoot\ExtFolder" -Name "Custom Property Folders" -Value $propFolders -ErrorAction SilentlyContinue
    }

    # Прямая привязка библиотеки материалов ГОСТ (дубль .reg-импорта: если reg.exe
    # завершился с ошибкой, база материалов всё равно подключится).
    # Корпоративный стандарт: в путях системы ТОЛЬКО наша библиотека; стандартные
    # папки SW (sldmaterials, Custom Materials) не подключаются — пользователь
    # добавит их сам при необходимости.
    $matLibDir = Join-Path $ToolsRoot "04_Библиотеки_Материалов_и_Профилей\Библиотека материалов"
    if (Test-Path $matLibDir) {
        Set-ItemProperty -Path "$swRegRoot\ExtReferences" -Name "Material Database Folders" -Value $matLibDir -ErrorAction SilentlyContinue
        Set-ItemProperty -Path "$swRegRoot\ExtFolder" -Name "Material Database Folders" -Value $matLibDir -ErrorAction SilentlyContinue
        Write-Host "  [OK] Библиотека материалов ГОСТ подключена: $matLibDir" -ForegroundColor Green
    }

    # Регистрация COM-сервера в HKCU (для гарантированной работы без прав Администратора)
    # $codebase (сырая форма) сформирован выше, до вызова RegAsm.
    $clsidPath = "HKCU:\Software\Classes\CLSID\$guid"
    if (-not (Test-Path $clsidPath)) { New-Item -Path $clsidPath -Force | Out-Null }
    Set-ItemProperty -Path $clsidPath -Name "(Default)" -Value "ESKD.MaterialSync.SwAddin"

    $inprocPath = "$clsidPath\InprocServer32"
    if (-not (Test-Path $inprocPath)) { New-Item -Path $inprocPath -Force | Out-Null }
    Set-ItemProperty -Path $inprocPath -Name "(Default)" -Value "mscoree.dll"
    Set-ItemProperty -Path $inprocPath -Name "ThreadingModel" -Value "Both"
    Set-ItemProperty -Path $inprocPath -Name "Class" -Value "ESKD.MaterialSync.SwAddin"
    Set-ItemProperty -Path $inprocPath -Name "Assembly" -Value "ESKD_Material_Sync_v5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
    Set-ItemProperty -Path $inprocPath -Name "RuntimeVersion" -Value "v4.0.30319"
    Set-ItemProperty -Path $inprocPath -Name "CodeBase" -Value $codebase

    $inprocVerPath = "$inprocPath\1.0.0.0"
    if (-not (Test-Path $inprocVerPath)) { New-Item -Path $inprocVerPath -Force | Out-Null }
    Set-ItemProperty -Path $inprocVerPath -Name "Class" -Value "ESKD.MaterialSync.SwAddin"
    Set-ItemProperty -Path $inprocVerPath -Name "Assembly" -Value "ESKD_Material_Sync_v5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
    Set-ItemProperty -Path $inprocVerPath -Name "RuntimeVersion" -Value "v4.0.30319"
    Set-ItemProperty -Path $inprocVerPath -Name "CodeBase" -Value $codebase

    $catPath = "$clsidPath\Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}"
    if (-not (Test-Path $catPath)) { New-Item -Path $catPath -Force | Out-Null }

    $progIdPath = "$clsidPath\ProgId"
    if (-not (Test-Path $progIdPath)) { New-Item -Path $progIdPath -Force | Out-Null }
    Set-ItemProperty -Path $progIdPath -Name "(Default)" -Value "ESKD.MaterialSync.SwAddin_v5"

    $progIdRoot = "HKCU:\Software\Classes\ESKD.MaterialSync.SwAddin_v5\CLSID"
    if (-not (Test-Path $progIdRoot)) { New-Item -Path $progIdRoot -Force | Out-Null }
    Set-ItemProperty -Path $progIdRoot -Name "(Default)" -Value $guid

    $progIdMain = "HKCU:\Software\Classes\ESKD.MaterialSync.SwAddin_v5"
    if (-not (Test-Path $progIdMain)) { New-Item -Path $progIdMain -Force | Out-Null }
    Set-ItemProperty -Path $progIdMain -Name "(Default)" -Value "ESKD.MaterialSync.SwAddin"

    # HKCU
    $keyPath = "HKCU:\Software\SolidWorks\AddIns\$guid"
    if (-not (Test-Path $keyPath)) { New-Item -Path $keyPath -Force | Out-Null }
    Set-ItemProperty -Path $keyPath -Name "(Default)" -Value 1 -Type DWord
    Set-ItemProperty -Path $keyPath -Name "Title" -Value $title
    Set-ItemProperty -Path $keyPath -Name "Description" -Value $desc

    # AddinsStartup
    $startupPath = "HKCU:\Software\SolidWorks\AddinsStartup\$guid"
    if (-not (Test-Path $startupPath)) { New-Item -Path $startupPath -Force | Out-Null }
    Set-ItemProperty -Path $startupPath -Name "(Default)" -Value 1 -Type DWord

    # Параметры ЕСКД (HKCU\Software\SolidWorks\ESKD_Settings)
    $eskdSettingsPath = "HKCU:\Software\SolidWorks\ESKD_Settings"
    if (-not (Test-Path $eskdSettingsPath)) { New-Item -Path $eskdSettingsPath -Force | Out-Null }
    Set-ItemProperty -Path $eskdSettingsPath -Name "Author" -Value $Author
    Set-ItemProperty -Path $eskdSettingsPath -Name "Checker" -Value ""
    Set-ItemProperty -Path $eskdSettingsPath -Name "Organization" -Value $Firm
    Set-ItemProperty -Path $eskdSettingsPath -Name "AutoMass" -Value 1 -Type DWord
    Set-ItemProperty -Path $eskdSettingsPath -Name "MassDecimals" -Value 2 -Type DWord
    Set-ItemProperty -Path $eskdSettingsPath -Name "AutoCenterMass" -Value 1 -Type DWord
    Set-ItemProperty -Path $eskdSettingsPath -Name "AutoSplitName" -Value 1 -Type DWord
    Set-ItemProperty -Path $eskdSettingsPath -Name "AuthorList" -Value $Author

    # Избранные материалы с дробным слэшем '/'
    # Имена и matid СВЕРЕНЫ с фактическим составом библиотеки (2026-09-12): при
    # несовпадении имени SW молча подставляет материал по matid — получался баг
    # «выбрал Лист 6,0 — прописался Лист 3,0» (id 1002 = Лист 3,0).
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
    $matKey = "$swRegRoot\Material"
    if (-not (Test-Path $matKey)) { New-Item -Path $matKey -Force | Out-Null }
    for ($i = 0; $i -lt $favList.Count; $i++) {
        $idx = $i + 1
        Set-ItemProperty -Path $matKey -Name "Favorite Material $idx" -Value $favList[$i]
        Set-ItemProperty -Path $matKey -Name "_FavMaterial$idx" -Value $favList[$i]
    }
    Set-ItemProperty -Path $matKey -Name "__NumOfFavs" -Value $favList.Count -Type DWord

    # Повторная фиксация видимости вкладки ЕСКД
    foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
        $ctxPath = "$swRegRoot\User Interface\CommandManager\$ctx"
        if (Test-Path $ctxPath) {
            Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | ForEach-Object {
                $tabPath = $_.PSPath
                $refName = (Get-ItemProperty -Path $tabPath -Name "RefName" -ErrorAction SilentlyContinue).RefName
                if ($refName -eq "ЕСКД") {
                    Set-ItemProperty -Path $tabPath -Name "Tab Props" -Value "ЕСКД,1,1,-1" -ErrorAction SilentlyContinue
                }
            }
        }
    }

    Write-Host "  [OK] Нативная надстройка ЕСКД v5, панель инструментов и Избранные материалы настроены." -ForegroundColor Green
} else {
    if (-not (Test-Path $addinDll)) {
        Write-Host "  [ПРЕДУПРЕЖДЕНИЕ] Не найдена DLL надстройки ЕСКД: $addinDll" -ForegroundColor Yellow
    }
    if (-not (Test-Path $regasm)) {
        Write-Host "  [ПРЕДУПРЕЖДЕНИЕ] Не найден RegAsm .NET 4: $regasm" -ForegroundColor Yellow
    }
}

# 5.1. Настройка и интеграция модуля автоматизации черчения Drw (CAD Booster Drew)
$drewGuid = "{08c4bc0b-c36c-470e-a0ea-02232f023333}"
$drewInstalled = @(
    (Join-Path $env:ProgramFiles "CAD Booster\Drew\CADBooster.Drew.Drawing.dll"),
    (Join-Path $env:LOCALAPPDATA "CAD Booster\Drew\CADBooster.Drew.Drawing.dll")
) | Where-Object { Test-Path $_ } | Select-Object -First 1

# Если модуль ещё не установлен в Program Files / AppData (чистая машина), обязательно запускаем дистрибутив
if (-not $drewInstalled) {
    $drewInstaller = Join-Path $ToolsRoot "03_Макросы_и_Плагины\Drw_System_Automation\install-all.ps1"
    if (Test-Path $drewInstaller) {
        Write-Host "  [ИНФО] Установка модуля Drw (CAD Booster Drew 4.3.0) из комплекта поставки..." -ForegroundColor Yellow
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $drewInstaller -Silent -NoActivate
        $drewInstalled = @(
            (Join-Path $env:ProgramFiles "CAD Booster\Drew\CADBooster.Drew.Drawing.dll"),
            (Join-Path $env:LOCALAPPDATA "CAD Booster\Drew\CADBooster.Drew.Drawing.dll")
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}

$drewDll = if ($drewInstalled) { (Resolve-Path $drewInstalled).Path } else { $null }

if ($drewDll) {
    $drewTitle = "Drew"
    $drewDesc = "Drew Drawing Automation"
    $drewCodeBase = "file:///" + $drewDll.Replace('\', '/')

    # HKCU COM-регистрация (для гарантированной работы без прав администратора)
    $drewClsidPath = "HKCU:\Software\Classes\CLSID\$drewGuid"
    if (-not (Test-Path $drewClsidPath)) { New-Item -Path $drewClsidPath -Force | Out-Null }
    Set-ItemProperty -Path $drewClsidPath -Name "(Default)" -Value "CADBooster.Drew.Drawing.SolidWorks.Integration.DrewAddin" -ErrorAction SilentlyContinue

    $drewInprocPath = "$drewClsidPath\InprocServer32"
    if (-not (Test-Path $drewInprocPath)) { New-Item -Path $drewInprocPath -Force | Out-Null }
    Set-ItemProperty -Path $drewInprocPath -Name "(Default)" -Value "mscoree.dll" -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $drewInprocPath -Name "ThreadingModel" -Value "Both" -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $drewInprocPath -Name "Class" -Value "CADBooster.Drew.Drawing.SolidWorks.Integration.DrewAddin" -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $drewInprocPath -Name "Assembly" -Value "CADBooster.Drew.Drawing, Version=4.3.0.0, Culture=neutral, PublicKeyToken=null" -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $drewInprocPath -Name "RuntimeVersion" -Value "v4.0.30319" -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $drewInprocPath -Name "CodeBase" -Value $drewCodeBase -ErrorAction SilentlyContinue

    # HKCU AddIns и автозагрузка
    $drewHkcuKey = "HKCU:\Software\SolidWorks\AddIns\$drewGuid"
    if (-not (Test-Path $drewHkcuKey)) { New-Item -Path $drewHkcuKey -Force | Out-Null }
    Set-ItemProperty -Path $drewHkcuKey -Name "(Default)" -Value 1 -Type DWord -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $drewHkcuKey -Name "Title" -Value $drewTitle -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $drewHkcuKey -Name "Description" -Value $drewDesc -ErrorAction SilentlyContinue

    $drewStartupKey = "HKCU:\Software\SolidWorks\AddinsStartup\$drewGuid"
    if (-not (Test-Path $drewStartupKey)) { New-Item -Path $drewStartupKey -Force | Out-Null }
    Set-ItemProperty -Path $drewStartupKey -Name "(Default)" -Value 1 -Type DWord -ErrorAction SilentlyContinue

    # HKLM регистрация для администраторов
    if ($isAdmin) {
        try {
            $drewHklmAddin = "HKLM:\Software\SolidWorks\AddIns\$drewGuid"
            if (-not (Test-Path $drewHklmAddin)) { New-Item -Path $drewHklmAddin -Force -ErrorAction SilentlyContinue | Out-Null }
            Set-ItemProperty -Path $drewHklmAddin -Name "(Default)" -Value 1 -Type DWord -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $drewHklmAddin -Name "Title" -Value $drewTitle -ErrorAction SilentlyContinue

            $drewHklmStartup = "HKLM:\Software\SolidWorks\AddinsStartup\$drewGuid"
            if (-not (Test-Path $drewHklmStartup)) { New-Item -Path $drewHklmStartup -Force -ErrorAction SilentlyContinue | Out-Null }
            Set-ItemProperty -Path $drewHklmStartup -Name "(Default)" -Value 1 -Type DWord -ErrorAction SilentlyContinue
        } catch { }
    }

    Write-Host "  [OK] Модуль автоматизации чертежей Drw (Drew 4.3.0) интегрирован и активирован." -ForegroundColor Green
} else {
    Write-Host "  [ПРЕДУПРЕЖДЕНИЕ] Модуль Drw не найден и не может быть установлен." -ForegroundColor Yellow
}

# 6. Шрифты ГОСТ
# B4 (DEP-4): настоящая установка с регистрацией в реестре шрифтов и обновлением кэша
# (раньше было только Copy-Item в C:\Windows\Fonts с ложным "[OK] Зарегистрировано").
Write-Host "`n[6/6] Установка шрифтов ГОСТ..." -ForegroundColor Gray
$fontsDir = Join-Path $ToolsRoot "05_Шрифты"
if (Test-Path $fontsDir) {
    if (-not ([System.Management.Automation.PSTypeName]'GostFonts.NativeMethods').Type) {
        Add-Type -Namespace GostFonts -Name NativeMethods -MemberDefinition @'
[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "AddFontResourceW")]
public static extern int AddFontResource(string lpFileName);

[DllImport("user32.dll", SetLastError = true)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
'@
    }

    $hklmFontsKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"
    $hkcuFontsKey = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Fonts"
    if ($isAdmin) {
        # Системная установка: C:\Windows\Fonts + HKLM
        $targetFontsDir = Join-Path $env:SystemRoot "Fonts"
        $targetFontsReg = $hklmFontsKey
        Write-Host "  Режим установки: системный ($targetFontsDir, HKLM)." -ForegroundColor Gray
    } else {
        # Per-user установка (Windows 10 1709+): %LOCALAPPDATA%\Microsoft\Windows\Fonts + HKCU
        $targetFontsDir = Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Fonts"
        $targetFontsReg = $hkcuFontsKey
        Write-Host "  Режим установки: пользовательский ($targetFontsDir, HKCU; требуется Windows 10 1709+)." -ForegroundColor Gray
    }
    if (-not (Test-Path $targetFontsDir)) { New-Item -ItemType Directory -Path $targetFontsDir -Force | Out-Null }
    if (-not (Test-Path $targetFontsReg)) { New-Item -Path $targetFontsReg -Force | Out-Null }

    $copiedCount = 0
    $registeredCount = 0
    $skippedCount = 0
    $failedCount = 0
    $fontFiles = @(Get-ChildItem -Path $fontsDir -Include *.ttf,*.fon,*.otf -Recurse -ErrorAction SilentlyContinue)
    foreach ($fontFile in $fontFiles) {
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($fontFile.Name)
        $valueName = "$baseName (TrueType)"

        # Уже установлен? Определяем по наличию значения в реестре шрифтов (HKLM или HKCU).
        $alreadyInstalled = $false
        foreach ($fontRegKey in @($hklmFontsKey, $hkcuFontsKey)) {
            if (Test-Path $fontRegKey) {
                $existing = (Get-ItemProperty -Path $fontRegKey -Name $valueName -ErrorAction SilentlyContinue).$valueName
                if ($existing) { $alreadyInstalled = $true; break }
            }
        }
        if ($alreadyInstalled) {
            $skippedCount++
            continue
        }

        $dstFont = Join-Path $targetFontsDir $fontFile.Name
        try {
            if (-not (Test-Path -LiteralPath $dstFont)) {
                Copy-Item -LiteralPath $fontFile.FullName -Destination $dstFont -Force -ErrorAction Stop
                $copiedCount++
            }
            New-ItemProperty -Path $targetFontsReg -Name $valueName -Value $dstFont -PropertyType String -Force -ErrorAction Stop | Out-Null
            $registeredCount++
            [void][GostFonts.NativeMethods]::AddFontResource($dstFont)
        } catch {
            $failedCount++
            Write-Host "    [ОШИБКА] Шрифт '$($fontFile.Name)': $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    if ($registeredCount -gt 0) {
        # Рассылка WM_FONTCHANGE (HWND_BROADCAST, SMTO_ABORTIFHUNG) — обновление кэша шрифтов без перезагрузки.
        $fontChangeResult = [IntPtr]::Zero
        [void][GostFonts.NativeMethods]::SendMessageTimeout([IntPtr]0xFFFF, 0x001D, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 5000, [ref]$fontChangeResult)
    }

    Write-Host "  [OK] Шрифты ГОСТ: скопировано $copiedCount, зарегистрировано $registeredCount, пропущено (уже установлены) $skippedCount, ошибок $failedCount." -ForegroundColor Green
    if (-not $isAdmin) {
        Write-Host "  [ИНФО] Шрифты установлены для текущего пользователя. Для системовой установки запустите сценарий от имени администратора." -ForegroundColor DarkGray
    }
} else {
    Write-Host "  [ПРЕДУПРЕЖДЕНИЕ] Каталог шрифтов не найден: $fontsDir" -ForegroundColor Yellow
}

Write-Host "`n=================================================================" -ForegroundColor Green
Write-Host " НАСТРОЙКА УСПЕШНО ЗАВЕРШЕНА! КОРПОРАТИВНЫЙ СТАНДАРТ ЕСКД ПРИМЕНЁН. " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
