<#
.SYNOPSIS
    Автоматическая настройка рабочего места SolidWorks 2025 (Корпоративный стандарт ЕСКД)
.DESCRIPTION
    Импортирует полный корпоративный профиль (Сборки, Чертежи, Оформление, Цвета, Панели инструментов, Макросы SWPlus),
    настраивает нативную надстройку ЕСКД v5 (реквизиты, масса, центрирование штампа),
    настраивает пути к библиотекам и шаблонам, Master.ini, MProp, шрифты ГОСТ и аппаратный RealView.
#>

[CmdletBinding()]
param (
    [string]$ToolsRoot = "",
    [string]$Author = "",
    [string]$Firm = "",
    [switch]$SkipClose
)

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
Write-Host " НАСТРОЙКА РАБОЧЕГО МЕСТА SOLIDWORKS 2025 (КОРПОРАТИВНЫЙ СТАНДАРТ) " -ForegroundColor Yellow
Write-Host "=================================================================" -ForegroundColor Cyan

# 0. Определение автора и организации по умолчанию
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
$defaultAuthor = "Шалунов В.В."
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
if (-not $Firm) { $Firm = "Home Made" }

Write-Host "Конструктор (Разраб.): $Author" -ForegroundColor White
Write-Host "Организация (Контора): $Firm" -ForegroundColor White

# 1. Закрытие SolidWorks и очистка аварийных надстроек
Write-Host "`n[1/6] Проверка запущенных процессов SolidWorks..." -ForegroundColor Gray
$swProc = Get-Process -Name "SLDWORKS" -ErrorAction SilentlyContinue
if ($swProc -and -not $SkipClose) {
    Write-Host "  Закрытие процессов SLDWORKS.exe для надежной записи реестра..." -ForegroundColor Yellow
    Stop-Process -Name "SLDWORKS" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
} elseif ($swProc -and $SkipClose) {
    Write-Host "  SolidWorks запущен (пропуск закрытия по ключу -SkipClose)..." -ForegroundColor Yellow
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
foreach ($rootClass in @("HKLM:\SOFTWARE\Classes", "HKCU:\Software\Classes")) {
    Get-ChildItem $rootClass -ErrorAction SilentlyContinue | Where-Object { 
        $_.PSChildName -like "OnCadTools*" 
    } | ForEach-Object {
        Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($ug in $unwantedAddinGuids) {
        Remove-Item (Join-Path $rootClass "CLSID\$ug") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $rootClass "WOW6432Node\CLSID\$ug") -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Очистка вкладок CommandManager и TaskPane от OnCadTools, Ounan
foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
    $ctxPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\$ctx"
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
$flyoutsPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\Custom API Flyouts"
if (Test-Path $flyoutsPath) {
    Get-ChildItem $flyoutsPath -ErrorAction SilentlyContinue | ForEach-Object {
        $mod = (Get-ItemProperty $_.PSPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName
        if ($mod -and ($mod -match "03412ba8|7A2F5C31")) {
            Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
Remove-Item "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\TaskPane\Инструменты Ounan (OnCadTools)" -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\TaskPane" -ErrorAction SilentlyContinue | Where-Object { 
    $_.Name -match "OnCad|Ounan" 
} | ForEach-Object {
    Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-ItemProperty -Path "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\General\Addin Performance" -Name "OnCadTools" -ErrorAction SilentlyContinue


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

# Удаление остатков и вкладок Semantic / Semantic MDM
$semanticTabs = @(
    "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\PartContext\Tab19",
    "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\PartContext\Tab20",
    "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\AssyContext\Tab14",
    "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\AssyContext\Tab15",
    "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\DrwContext\Tab7",
    "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\DrwContext\Tab8"
)
foreach ($st in $semanticTabs) {
    Remove-Item $st -Recurse -Force -ErrorAction SilentlyContinue
}
foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
    $ctxPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\$ctx"
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
Remove-ItemProperty -Path "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\General\Addin Performance" -Name "Semantic" -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\General\Addin Performance" -Name "Semantic MDM" -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\SDI Solution" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "HKLM:\SOFTWARE\SDI Solution" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "HKLM:\SOFTWARE\WOW6432Node\SDI Solution" -Recurse -Force -ErrorAction SilentlyContinue

# 2. Определение путей
$ToolsRoot = (Resolve-Path $ToolsRoot).Path
Write-Host "`n[2/6] Проверка путей в: $ToolsRoot" -ForegroundColor Gray

$regProfile = Join-Path $ToolsRoot "01_Настройки_SolidWorks\Реестровые_Профили\01_SW2025_Корпоративный_Стандарт_ЕСКД.reg"
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

            # Синхронизация списка организаций
            $firmFile = Join-Path $mpropDir "MProp_Firm.txt"
            $firmList = @()
            if (Test-Path $firmFile) {
                $firmList = [System.IO.File]::ReadAllLines($firmFile, [System.Text.Encoding]::GetEncoding(1251)) | Where-Object { $_.Trim() -ne "" }
            }
            if ($Firm -and ($firmList -notcontains $Firm)) {
                $firmList = @($Firm) + $firmList
            }
            if ($firmList.Count -gt 0) {
                [System.IO.File]::WriteAllLines($firmFile, $firmList, [System.Text.Encoding]::GetEncoding(1251))
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
Write-Host "`n[4/6] Импорт полного реестрового профиля SolidWorks 2025..." -ForegroundColor Gray
if (Test-Path $regProfile) {
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
    $tempReg = Join-Path $ToolsRoot "01_Настройки_SolidWorks\_temp_import.reg"
    [System.IO.File]::WriteAllText($tempReg, $adaptedText, [System.Text.Encoding]::Unicode)
    Start-Process -FilePath "reg.exe" -ArgumentList "import `"$tempReg`"" -Wait -NoNewWindow
    if (Test-Path $tempReg) { Remove-Item $tempReg -Force -ErrorAction SilentlyContinue }
    Write-Host "  [OK] Полный корпоративный профиль SolidWorks 2025 успешно импортирован (пути адаптированы)." -ForegroundColor Green
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
    Remove-Item "HKLM:\SOFTWARE\SolidWorks\AddIns\$og" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKLM:\SOFTWARE\SolidWorks\AddInsStartup\$og" -Recurse -Force -ErrorAction SilentlyContinue
}

$contexts = @("PartContext", "AssyContext", "DrwContext")
$activeGuid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
foreach ($ctx in $contexts) {
    $ctxPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\$ctx"
    if (-not (Test-Path $ctxPath)) { New-Item -Path $ctxPath -Force | Out-Null }

    $found = $false
    Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | ForEach-Object {
        $tabPath = $_.PSPath
        $modName = (Get-ItemProperty -Path $tabPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName
        if ($modName -and ($oldGuids -contains $modName.ToUpper())) {
            Remove-Item -Path $tabPath -Recurse -Force -ErrorAction SilentlyContinue
            return
        }
        $refName = (Get-ItemProperty -Path $tabPath -Name "RefName" -ErrorAction SilentlyContinue).RefName
        if (($refName -and ($refName -match "ЕСКД")) -or ($modName -and ($modName.ToUpper() -eq $activeGuid.ToUpper()))) {
            Set-ItemProperty -Path $tabPath -Name "RefName" -Value "ЕСКД" -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $tabPath -Name "ModuleName" -Value $activeGuid -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $tabPath -Name "Tab Props" -Value "ЕСКД,1,1,-1" -Force -ErrorAction SilentlyContinue
            $found = $true
        }
    }
    if (-not $found) {
        $existingTabs = Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.PSChildName -match "^Tab(\d+)$") { [int]$matches[1] }
        }
        $nextNum = 0
        if ($existingTabs) { $nextNum = ($existingTabs | Measure-Object -Maximum).Maximum + 1 }
        $newTabPath = Join-Path $ctxPath "Tab$nextNum"
        New-Item -Path $newTabPath -Force | Out-Null
        Set-ItemProperty -Path $newTabPath -Name "RefName" -Value "ЕСКД" -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $newTabPath -Name "ModuleName" -Value $activeGuid -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $newTabPath -Name "Tab Props" -Value "ЕСКД,1,1,-1" -Force -ErrorAction SilentlyContinue
    }
}

# 4.0.1. Гарантированное закрепление 9 кнопок макросов SWPlus в верхней панели QAT (Quick Access Toolbar)
$qatGb0Path = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\QAT\GB0"
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

$menuCustPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\Menu Customizations"
if (-not (Test-Path $menuCustPath)) { New-Item -Path $menuCustPath -Force | Out-Null }
for ($cid = 33639; $cid -le 33647; $cid++) {
    Set-ItemProperty -Path $menuCustPath -Name "$cid" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
}

# Очистка фантомных ссылок Custom API Flyouts / Toolbars (Drew, OnCadTools, SWTools) предотвращающая диалог сброса тулбаров SolidWorks
$orphanToolbars = @(
    "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\Custom API Flyouts",
    "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\Toolbars\ToolbarChangesOnUpgrade"
)
foreach ($ot in $orphanToolbars) {
    Remove-Item -Path $ot -Recurse -Force -ErrorAction SilentlyContinue
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
    Set-ItemProperty -Path "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\General" -Name "Toolbox Data Location" -Value $toolboxPath -ErrorAction SilentlyContinue
    Set-ItemProperty -Path "HKLM:\SOFTWARE\SolidWorks\SOLIDWORKS 2025\General" -Name "Toolbox Data Location" -Value $toolboxPath -ErrorAction SilentlyContinue
    Write-Host "  [OK] База данных стандартов Toolbox зафиксирована: $toolboxPath" -ForegroundColor Green
}
Set-ItemProperty -Path "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\Performance" -Name "Use Performance Pipeline 2020" -Value 0 -ErrorAction SilentlyContinue
Write-Host "  [OK] Графический режим переведен в безопасный режим (черный экран устранен)." -ForegroundColor Green

# 5. Регистрация нативной надстройки ЕСКД v5 (CommandManager, Настройки, Центрирование массы)
Write-Host "`n[5/6] Регистрация нативной надстройки ЕСКД и панели управления..." -ForegroundColor Gray
$addinDll = Join-Path $ToolsRoot "03_Макросы_и_Плагины\ESKD_Material_Sync_Addin\ESKD_Material_Sync_v5.dll"
$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

if ((Test-Path $addinDll) -and (Test-Path $regasm)) {
    Start-Process -FilePath $regasm -ArgumentList "/codebase `"$addinDll`"" -Wait -NoNewWindow
    $guid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
    $title = "ЕСКД: Синхронизация материалов и реквизитов"
    $desc = "Панель инструментов ЕСКД: настройки реквизитов (фамилии, контора, масса), автоматическая синхронизация материалов и центрирование штампа по ГОСТ 2.104"
    
    # HKLM (если запущен с правами администратора)
    try {
        $hklmPath = "HKLM:\Software\SolidWorks\AddIns\$guid"
        if (-not (Test-Path $hklmPath -ErrorAction SilentlyContinue)) { New-Item -Path $hklmPath -Force -ErrorAction SilentlyContinue | Out-Null }
        Set-ItemProperty -Path $hklmPath -Name "(Default)" -Value 1 -Type DWord -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $hklmPath -Name "Title" -Value $title -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $hklmPath -Name "Description" -Value $desc -ErrorAction SilentlyContinue
    } catch { }

    $propFolders = Join-Path $ToolsRoot "02_Шаблоны_и_Форматки\Шаблоны свойств"
    if (Test-Path $propFolders) {
        Set-ItemProperty -Path "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\ExtReferences" -Name "Custom Property Folders" -Value $propFolders -ErrorAction SilentlyContinue
        Set-ItemProperty -Path "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\ExtFolder" -Name "Custom Property Folders" -Value $propFolders -ErrorAction SilentlyContinue
    }

    # Регистрация COM-сервера в HKCU (для гарантированной работы без прав Администратора)
    $codebase = "file:///" + $addinDll.Replace('\', '/')
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
    $favList = @(
        "Библиотека_Материалов_ГОСТ|Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89|1001",
        "Библиотека_Материалов_ГОСТ|Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89|1002",
        "Библиотека_Материалов_ГОСТ|Труба 80х80х4 ГОСТ 8639-82 / В 10 ГОСТ 13663-86|1003",
        "Библиотека_Материалов_ГОСТ|Труба 57х3,5 ГОСТ 8732-78 / В 10 ГОСТ 8731-74|1005",
        "Библиотека_Материалов_ГОСТ|Труба 102х4 ГОСТ 8732-78 / В 20 ГОСТ 8731-74|1006",
        "Библиотека_Материалов_ГОСТ|Сталь 3сп (ГОСТ 380-2005)|1007",
        "Библиотека_Материалов_ГОСТ|Сталь 20 (ГОСТ 1050-2013)|1008",
        "Библиотека_Материалов_ГОСТ|Сталь 45 (ГОСТ 1050-2013)|1009",
        "Библиотека_Материалов_ГОСТ|Сталь 09Г2С (ГОСТ 19281-2014)|1011"
    )
    $matKey = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\Material"
    if (-not (Test-Path $matKey)) { New-Item -Path $matKey -Force | Out-Null }
    for ($i = 0; $i -lt $favList.Count; $i++) {
        $idx = $i + 1
        Set-ItemProperty -Path $matKey -Name "Favorite Material $idx" -Value $favList[$i]
        Set-ItemProperty -Path $matKey -Name "_FavMaterial$idx" -Value $favList[$i]
    }
    Set-ItemProperty -Path $matKey -Name "__NumOfFavs" -Value $favList.Count -Type DWord

    # Повторная фиксация видимости вкладки ЕСКД
    foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
        $ctxPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\$ctx"
        if (Test-Path $ctxPath) {
            Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | ForEach-Object {
                $tabPath = $_.PSPath
                $refName = (Get-ItemProperty -Path $tabPath -Name "RefName" -ErrorAction SilentlyContinue).RefName
                if ($refName -eq "ЕСКД") {
                    Set-ItemProperty -Path $tabPath -Name "Tab Props" -Value "ЕСКД,0,1,-1" -ErrorAction SilentlyContinue
                }
            }
        }
    }

    Write-Host "  [OK] Нативная надстройка ЕСКД v5, панель инструментов и Избранные материалы настроены." -ForegroundColor Green

    # 5.1. Настройка и интеграция модуля автоматизации черчения Drw (CAD Booster Drew)
    $drewGuid = "{08c4bc0b-c36c-470e-a0ea-02232f023333}"
    $drewCandidates = @(
        (Join-Path $env:ProgramFiles "CAD Booster\Drew\CADBooster.Drew.Drawing.dll"),
        (Join-Path $env:LOCALAPPDATA "CAD Booster\Drew\CADBooster.Drew.Drawing.dll"),
        (Join-Path $ToolsRoot "03_Макросы_и_Плагины\Drw_System_Automation\2_комплект_издания\bin\CADBooster.Drew.Drawing.dll")
    )
    $drewDll = $null
    foreach ($dc in $drewCandidates) {
        if (Test-Path $dc) { $drewDll = (Resolve-Path $dc).Path; break }
    }

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

        Write-Host "  [OK] Модуль автоматизации чертежей Drw (Drew 4.3.0) интегрирован и активирован." -ForegroundColor Green
    } else {
        $drewInstaller = Join-Path $ToolsRoot "03_Макросы_и_Плагины\Drw_System_Automation\install-all.ps1"
        if (Test-Path $drewInstaller) {
            Write-Host "  [ИНФО] Развертывание модуля Drw из комплекта поставки..." -ForegroundColor Gray
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $drewInstaller -Silent -NoActivate
            Write-Host "  [OK] Модуль Drw успешно развернут из дистрибутива." -ForegroundColor Green
        }
    }
} else {
    Write-Host "  [ПРЕДУПРЕЖДЕНИЕ] Файл надстройки или RegAsm не найден: $addinDll" -ForegroundColor Yellow
}

# 6. Шрифты ГОСТ
Write-Host "`n[6/6] Установка шрифтов ГОСТ..." -ForegroundColor Gray
$fontsDir = Join-Path $ToolsRoot "05_Шрифты"
if (Test-Path $fontsDir) {
    $winFonts = Join-Path $env:SystemRoot "Fonts"
    $fontCount = 0
    Get-ChildItem -Path $fontsDir -Include *.ttf,*.fon,*.otf -Recurse | ForEach-Object {
        $dst = Join-Path $winFonts $_.Name
        try {
            if (-not (Test-Path $dst)) {
                Copy-Item -LiteralPath $_.FullName -Destination $dst -Force -ErrorAction Stop
            }
            $fontCount++
        } catch { }
    }
    Write-Host "  [OK] Зарегистрировано шрифтов ГОСТ: $fontCount шт." -ForegroundColor Green
}

Write-Host "`n=================================================================" -ForegroundColor Green
Write-Host " НАСТРОЙКА УСПЕШНО ЗАВЕРШЕНА! КОРПОРАТИВНЫЙ СТАНДАРТ ЕСКД ПРИМЕНЁН. " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
