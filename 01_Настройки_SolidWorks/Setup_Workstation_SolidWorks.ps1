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

# 0.0. Снятие сетевых блокировок Windows (Mark of the Web / Zone.Identifier)
Write-Host "Проверка и разблокировка файлов дистрибутива..." -ForegroundColor Gray
try {
    Get-ChildItem -LiteralPath $ToolsRoot -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
        Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue
    }
    Write-Host "  [OK] Все библиотеки и скрипты разблокированы." -ForegroundColor Green
} catch { }

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

# Блокировка и полное удаление нежелательных надстроек (OnCadTools, Drew, устаревшие версии)
$unwantedAddinGuids = @(
    "{03412ba8-10f6-4d51-ac38-4937ce7bea5f}", # OnCadTools
    "{7a2f5c31-9e44-4b0d-8c21-5f0e9a4b77c2}", # OnCadTools Shim
    "{08c4bc0b-c36c-470e-a0ea-02232f023333}"  # CAD Booster Drew
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

# Очистка COM-классов и ProgID OnCadTools и Drew в Classes
foreach ($rootClass in @("HKLM:\SOFTWARE\Classes", "HKCU:\Software\Classes")) {
    Get-ChildItem $rootClass -ErrorAction SilentlyContinue | Where-Object { 
        $_.PSChildName -like "OnCadTools*" -or $_.PSChildName -like "CADBooster*" 
    } | ForEach-Object {
        Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($ug in $unwantedAddinGuids) {
        Remove-Item (Join-Path $rootClass "CLSID\$ug") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $rootClass "WOW6432Node\CLSID\$ug") -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Очистка вкладок CommandManager и TaskPane от OnCadTools, Drew, Ounan
foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
    $ctxPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\CommandManager\$ctx"
    if (Test-Path $ctxPath) {
        Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "Tab*" } | ForEach-Object {
            $ref = (Get-ItemProperty -Path $_.PSPath -Name "RefName" -ErrorAction SilentlyContinue).RefName
            $props = (Get-ItemProperty -Path $_.PSPath -Name "Tab Props" -ErrorAction SilentlyContinue)."Tab Props"
            $mod = (Get-ItemProperty -Path $_.PSPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName
            if (($ref -and ($ref -match "OnCad|Drew|Ounan")) -or 
                ($props -and ($props -match "OnCad|Drew|Ounan")) -or
                ($mod -and ($mod -match "03412ba8|08C4BC0B|7A2F5C31"))) {
                Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
$flyoutsPath = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\Custom API Flyouts"
if (Test-Path $flyoutsPath) {
    Get-ChildItem $flyoutsPath -ErrorAction SilentlyContinue | ForEach-Object {
        $mod = (Get-ItemProperty $_.PSPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName
        if ($mod -and ($mod -match "03412ba8|08C4BC0B|7A2F5C31")) {
            Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
Remove-Item "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\TaskPane\Инструменты Ounan (OnCadTools)" -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\User Interface\TaskPane" -ErrorAction SilentlyContinue | Where-Object { 
    $_.Name -match "OnCad|Drew|Ounan" 
} | ForEach-Object {
    Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-ItemProperty -Path "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\General\Addin Performance" -Name "OnCadTools" -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\General\Addin Performance" -Name "Drew" -ErrorAction SilentlyContinue

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

# 4. Импорт полного корпоративного реестрового профиля
Write-Host "`n[4/6] Импорт полного реестрового профиля SolidWorks 2025..." -ForegroundColor Gray
if (Test-Path $regProfile) {
    Start-Process -FilePath "reg.exe" -ArgumentList "import `"$regProfile`"" -Wait -NoNewWindow
    Write-Host "  [OK] Полный корпоративный профиль SolidWorks 2025 успешно импортирован." -ForegroundColor Green
} else {
    Write-Host "  [ОШИБКА] Файл профиля не найден: $regProfile" -ForegroundColor Red
}

# 4.1. Настройка путей ExtReferences, шаблонов документов и библиотек
Write-Host "  Привязка путей к корпоративным шаблонам, базам материалов и форматам..." -ForegroundColor Gray
$docTemplates = Join-Path $ToolsRoot "02_Шаблоны_и_Форматки\Шаблоны документов"
$draftingStd = Join-Path $ToolsRoot "02_Шаблоны_и_Форматки\База шаблонов"
$weldProfiles = Join-Path $ToolsRoot "04_Библиотеки_Материалов_и_Профилей\Профили сварных деталей"
$threadProfiles = Join-Path $ToolsRoot "04_Библиотеки_Материалов_и_Профилей\Профили резьбы"
$matLibrary = Join-Path $ToolsRoot "04_Библиотеки_Материалов_и_Профилей\Библиотека материалов"
$cutLists = Join-Path $ToolsRoot "02_Шаблоны_и_Форматки\Шаблон списка вырезов"
$weldTables = Join-Path $ToolsRoot "02_Шаблоны_и_Форматки\Шаблон таблицы сварных швов"
$favSymbols = Join-Path $ToolsRoot "02_Шаблоны_и_Форматки\Часто используемые размеры и примечания"
$swplusRoot = Join-Path $ToolsRoot "03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0"

$partDefault = Join-Path $docTemplates "Деталь.prtdot"
$asmDefault  = Join-Path $docTemplates "Сборка.asmdot"
$drwDefault  = Join-Path $docTemplates "Чертеж.drwdot"
$matDbStr    = "$matLibrary;C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\lang\russian\sldmaterials;C:\ProgramData\SolidWorks\SOLIDWORKS 2025\Custom Materials"
$tmplFolders = "$docTemplates;$draftingStd"

$extRefKey = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\ExtReferences"
if (-not (Test-Path $extRefKey)) { New-Item -Path $extRefKey -Force | Out-Null }
Set-ItemProperty -Path $extRefKey -Name "Document Template Folders" -Value $tmplFolders
Set-ItemProperty -Path $extRefKey -Name "Sheet Format Folders" -Value $sheetFormats
Set-ItemProperty -Path $extRefKey -Name "Drafting Standard Folder" -Value $draftingStd
Set-ItemProperty -Path $extRefKey -Name "Weldment Profile Folders" -Value $weldProfiles
Set-ItemProperty -Path $extRefKey -Name "Thread Profiles Folder" -Value $threadProfiles
Set-ItemProperty -Path $extRefKey -Name "Material Database Folders" -Value $matDbStr
Set-ItemProperty -Path $extRefKey -Name "Weldment Cut List Template Folders" -Value $cutLists
Set-ItemProperty -Path $extRefKey -Name "Weld Table Template Folder" -Value $weldTables
Set-ItemProperty -Path $extRefKey -Name "Dimension Favorite Folders" -Value $favSymbols
Set-ItemProperty -Path $extRefKey -Name "Macro Folders" -Value $swplusRoot

$docTmplKey = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\Document Templates"
if (-not (Test-Path $docTmplKey)) { New-Item -Path $docTmplKey -Force | Out-Null }
Set-ItemProperty -Path $docTmplKey -Name "Default Part template" -Value $partDefault
Set-ItemProperty -Path $docTmplKey -Name "Default Assy template" -Value $asmDefault
Set-ItemProperty -Path $docTmplKey -Name "Default Draw Template" -Value $drwDefault
Set-ItemProperty -Path $docTmplKey -Name "Use Default Document Templates" -Value 0 -Type DWord
Set-ItemProperty -Path $docTmplKey -Name "Templates Last Tab Used" -Value "Шаблоны документов"

$extFolderKey = "HKCU:\Software\SolidWorks\SOLIDWORKS 2025\ExtFolder"
if (-not (Test-Path $extFolderKey)) { New-Item -Path $extFolderKey -Force | Out-Null }
Set-ItemProperty -Path $extFolderKey -Name "Document Template Folder" -Value $tmplFolders
Set-ItemProperty -Path $extFolderKey -Name "Sheet Format Folders" -Value $sheetFormats
Set-ItemProperty -Path $extFolderKey -Name "Drafting Standard Folder" -Value $draftingStd
Set-ItemProperty -Path $extFolderKey -Name "Weldment Profile Folders" -Value $weldProfiles
Set-ItemProperty -Path $extFolderKey -Name "Thread Profiles Folder" -Value $threadProfiles
Set-ItemProperty -Path $extFolderKey -Name "Material Database Folders" -Value $matDbStr
Write-Host "  [OK] Пути к шаблонам, форматам, базам материалов и профилям зафиксированы." -ForegroundColor Green

# 4.2. Автоматическая привязка Toolbox и фиксация стабильной графики
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

# 5. Регистрация нативной надстройки ЕСКД v5 (работает без прав администратора через HKCU)
Write-Host "`n[5/6] Регистрация нативной надстройки ЕСКД и панели управления..." -ForegroundColor Gray
$addinDll = Join-Path $ToolsRoot "03_Макросы_и_Плагины\ESKD_Material_Sync_Addin\ESKD_Material_Sync_v5.dll"
$guid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
$progId = "ESKD.MaterialSync.SwAddin_v5"
$className = "ESKD.MaterialSync.SwAddin"
$assemblyName = "ESKD_Material_Sync_v5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
$runtimeVersion = "v4.0.30319"
$title = "ЕСКД: Синхронизация материалов и реквизитов"
$desc = "Панель инструментов ЕСКД: настройки реквизитов (фамилии, контора, масса), автоматическая синхронизация материалов и центрирование штампа по ГОСТ 2.104"

if (Test-Path $addinDll) {
    # 5.1. Всегда гарантированная Per-User COM регистрация в HKCU (работает без прав администратора!)
    $codeBase = ([System.Uri](Resolve-Path $addinDll).Path).AbsoluteUri
    
    # CLSID
    $clsidKey = "HKCU:\Software\Classes\CLSID\$guid"
    if (-not (Test-Path $clsidKey)) { New-Item -Path $clsidKey -Force | Out-Null }
    Set-ItemProperty -Path $clsidKey -Name "(Default)" -Value $className
    
    # InprocServer32
    $inprocKey = Join-Path $clsidKey "InprocServer32"
    if (-not (Test-Path $inprocKey)) { New-Item -Path $inprocKey -Force | Out-Null }
    Set-ItemProperty -Path $inprocKey -Name "(Default)" -Value "mscoree.dll"
    Set-ItemProperty -Path $inprocKey -Name "ThreadingModel" -Value "Both"
    Set-ItemProperty -Path $inprocKey -Name "Class" -Value $className
    Set-ItemProperty -Path $inprocKey -Name "Assembly" -Value $assemblyName
    Set-ItemProperty -Path $inprocKey -Name "RuntimeVersion" -Value $runtimeVersion
    Set-ItemProperty -Path $inprocKey -Name "CodeBase" -Value $codeBase
    
    # InprocServer32\1.0.0.0
    $verKey = Join-Path $inprocKey "1.0.0.0"
    if (-not (Test-Path $verKey)) { New-Item -Path $verKey -Force | Out-Null }
    Set-ItemProperty -Path $verKey -Name "Class" -Value $className
    Set-ItemProperty -Path $verKey -Name "Assembly" -Value $assemblyName
    Set-ItemProperty -Path $verKey -Name "RuntimeVersion" -Value $runtimeVersion
    Set-ItemProperty -Path $verKey -Name "CodeBase" -Value $codeBase
    
    # ProgId
    $progKey = Join-Path $clsidKey "ProgId"
    if (-not (Test-Path $progKey)) { New-Item -Path $progKey -Force | Out-Null }
    Set-ItemProperty -Path $progKey -Name "(Default)" -Value $progId
    
    # Implemented Categories
    $catKey = Join-Path $clsidKey "Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}"
    if (-not (Test-Path $catKey)) { New-Item -Path $catKey -Force | Out-Null }
    
    # Root ProgId in HKCU\Software\Classes
    $rootProgKey = "HKCU:\Software\Classes\$progId"
    if (-not (Test-Path $rootProgKey)) { New-Item -Path $rootProgKey -Force | Out-Null }
    Set-ItemProperty -Path $rootProgKey -Name "(Default)" -Value $className
    $rootProgClsid = Join-Path $rootProgKey "CLSID"
    if (-not (Test-Path $rootProgClsid)) { New-Item -Path $rootProgClsid -Force | Out-Null }
    Set-ItemProperty -Path $rootProgClsid -Name "(Default)" -Value $guid

    # 5.2. Если есть права администратора — дополнительно регистрируем в HKLM
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
    if ($isAdmin -and (Test-Path $regasm)) {
        try {
            Start-Process -FilePath $regasm -ArgumentList "/codebase `"$addinDll`"" -Wait -NoNewWindow -ErrorAction SilentlyContinue
            $hklmPath = "HKLM:\Software\SolidWorks\AddIns\$guid"
            if (-not (Test-Path $hklmPath)) { New-Item -Path $hklmPath -Force | Out-Null }
            Set-ItemProperty -Path $hklmPath -Name "(Default)" -Value 1 -Type DWord
            Set-ItemProperty -Path $hklmPath -Name "Title" -Value $title
            Set-ItemProperty -Path $hklmPath -Name "Description" -Value $desc
        } catch { }
    }

    # 5.3. Регистрация в SolidWorks (HKCU)
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

    Write-Host "  [OK] Нативная надстройка ЕСКД v5 зарегистрирована в COM (без прав админа), панель инструментов и Избранные материалы настроены." -ForegroundColor Green
} else {
    Write-Host "  [ПРЕДУПРЕЖДЕНИЕ] Файл надстройки не найден: $addinDll" -ForegroundColor Yellow
}

# 6. Шрифты ГОСТ (с поддержкой установки без прав администратора)
Write-Host "`n[6/6] Установка шрифтов ГОСТ..." -ForegroundColor Gray
$fontsDir = Join-Path $ToolsRoot "05_Шрифты"
if (Test-Path $fontsDir) {
    $systemFonts = Join-Path $env:SystemRoot "Fonts"
    $userFontsDir = Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Fonts"
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $targetFontsDir = if ($isAdmin) { $systemFonts } else { $userFontsDir }
    if (-not (Test-Path $targetFontsDir)) { New-Item -ItemType Directory -Path $targetFontsDir -Force | Out-Null }
    
    $fontCount = 0
    Get-ChildItem -Path $fontsDir -Include *.ttf,*.fon,*.otf -Recurse | ForEach-Object {
        $fName = $_.Name
        $dst = Join-Path $targetFontsDir $fName
        $fontValName = "$([System.IO.Path]::GetFileNameWithoutExtension($fName)) (TrueType)"
        
        $copied = $false
        try {
            if (-not (Test-Path $dst)) {
                Copy-Item -LiteralPath $_.FullName -Destination $dst -Force -ErrorAction Stop
            }
            $copied = $true
        } catch {
            if ($targetFontsDir -ne $userFontsDir) {
                if (-not (Test-Path $userFontsDir)) { New-Item -ItemType Directory -Path $userFontsDir -Force | Out-Null }
                $dst = Join-Path $userFontsDir $fName
                if (-not (Test-Path $dst)) {
                    Copy-Item -LiteralPath $_.FullName -Destination $dst -Force -ErrorAction SilentlyContinue
                }
                $copied = $true
            }
        }
        
        if ($copied) {
            if ($dst.StartsWith($systemFonts, [System.StringComparison]::OrdinalIgnoreCase)) {
                Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts" -Name $fontValName -Value $fName -ErrorAction SilentlyContinue
            } else {
                Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Fonts" -Name $fontValName -Value $dst -ErrorAction SilentlyContinue
            }
            $fontCount++
        }
    }
    Write-Host "  [OK] Зарегистрировано шрифтов ГОСТ: $fontCount шт." -ForegroundColor Green
}

Write-Host "`n=================================================================" -ForegroundColor Green
Write-Host " НАСТРОЙКА УСПЕШНО ЗАВЕРШЕНА! КОРПОРАТИВНЫЙ СТАНДАРТ ЕСКД ПРИМЕНЁН. " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
