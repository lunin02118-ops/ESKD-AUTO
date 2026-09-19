<#
.SYNOPSIS
    Проверка установки из папки инструментария «только для чтения» (T0, сетевая схема 14.09.2026).
.DESCRIPTION
    Собирает во временном каталоге папку инструментария с непривычным именем (пробелы, кириллица, не
    «_Инструменты_Конструктора»), ставит всем файлам атрибут «только чтение» и запускает установщик из неё с
    временной локальной копией и временным разделом реестра HKCU:\Software\ESKD_DeployTest_*. Настоящий реестр,
    шрифты, Drew и SWTools не затрагиваются, SolidWorks не нужен.

    Проверяется: источник не изменился (ни одного нового или изменённого файла); пути SolidWorks — на источник,
    кнопки и папка макросов — на локальную копию; надстройка зарегистрирована из локальной копии; Master.ini указывает
    на основные надписи источника; фамилия и организация дописаны в локальные списки MProp; повторная установка
    сохраняет настройки макросов пользователя и возвращает изменённые файлы выпуска. Вывод — JSON.
#>
param([Parameter(Mandatory = $true)][string]$RepoRoot)

$ErrorActionPreference = "Stop"
$problems = New-Object System.Collections.Generic.List[string]
function Expect($label, $actual, $expected) {
    if ("$actual" -ne "$expected") { $problems.Add("${label}: ожидалось «$expected», получено «$actual»") }
}
function Read-Value($path, $name) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $item = Get-ItemProperty -LiteralPath $path
    if ($null -eq $item.PSObject.Properties[$name]) { return $null }
    return $item.PSObject.Properties[$name].Value
}
function Snapshot($root) {
    $map = @{}
    foreach ($f in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        $map[$f.FullName.Substring($root.Length)] = "{0}|{1}" -f $f.Length, $f.LastWriteTimeUtc.Ticks
    }
    return $map
}

$cp1251 = [System.Text.Encoding]::GetEncoding(1251)
# Короткие имена: самый длинный файл инструментария — 154 знака, путь во %TEMP% должен уложиться в 260 (MAX_PATH)
$id = [guid]::NewGuid().ToString("N").Substring(0, 8)
$temp = Join-Path ([System.IO.Path]::GetTempPath()) "eskd_d_$id"
$source = Join-Path $temp "Сеть КТО\Инструменты КТО"
$local = Join-Path $temp "Профиль\ESKD\Toolkit"
$registryName = "ESKD_DeployTest_$id"
$sandbox = "HKCU:\Software\$registryName"
$swKey = "$sandbox\SolidWorks\SOLIDWORKS 2025"
$liveInstallBefore = Test-Path "HKCU:\Software\SolidWorks\ESKD_Install"
$liveAuthorBefore = Read-Value "HKCU:\Software\SolidWorks\ESKD_Settings" "Author"
$output = ""

try {
    # Папка инструментария: только то, что читает установка и SolidWorks
    $swplusRel = "03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0"
    $addinRel = "03_Макросы_и_Плагины\ESKD_Material_Sync_Addin"
    foreach ($rel in @("02_Шаблоны_и_Форматки", $swplusRel, "04_Библиотеки_Материалов_и_Профилей\Библиотека материалов",
                       "04_Библиотеки_Материалов_и_Профилей\Профили резьбы", "01_Настройки_SolidWorks\Реестровые_Профили")) {
        $dst = Join-Path $source $rel
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Copy-Item -Path (Join-Path (Join-Path $RepoRoot $rel) "*") -Destination $dst -Recurse -Force -Exclude "Backups"
    }
    Remove-Item -LiteralPath (Join-Path $source "01_Настройки_SolidWorks\Реестровые_Профили\Backups") -Recurse -Force -ErrorAction SilentlyContinue
    # Соседняя папка библиотеки проектирования, как «_Библиотека проектирования\_ крепеж и фурнитура» на NAS
    $fasteners = Join-Path (Split-Path -Path $source -Parent) "_ крепеж и фурнитура"
    New-Item -ItemType Directory -Path $fasteners -Force | Out-Null
    foreach ($name in @("Setup_Workstation_SolidWorks.ps1", "EskdDeploy.psm1")) {
        Copy-Item -LiteralPath (Join-Path $RepoRoot "01_Настройки_SolidWorks\$name") -Destination (Join-Path $source "01_Настройки_SolidWorks\$name")
    }
    $addinDst = Join-Path $source $addinRel
    New-Item -ItemType Directory -Path $addinDst -Force | Out-Null
    foreach ($name in @("ESKD_Material_Sync_v5.dll", "ESKD.exe", "ESKD_Sync.exe", "ESKD.ico", "Register-EskdAddin.ps1",
                        "SolidWorks.Interop.sldworks.dll", "SolidWorks.Interop.swconst.dll", "SolidWorks.Interop.swpublished.dll")) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $RepoRoot $addinRel) $name) -Destination $addinDst
    }
    Copy-Item -LiteralPath (Join-Path (Join-Path $RepoRoot $addinRel) "Icons") -Destination $addinDst -Recurse
    New-Item -ItemType Directory -Path (Join-Path $source "04_Библиотеки_Материалов_и_Профилей\Профили сварных деталей") -Force | Out-Null
    Get-ChildItem -LiteralPath $source -File -Recurse | ForEach-Object { $_.IsReadOnly = $true }
    $before = Snapshot $source

    # Разбор профиля отдельно: при тестовом корне нет HKLM и все разделы — в тестовом корне
    Import-Module (Join-Path $source "01_Настройки_SolidWorks\EskdDeploy.psm1") -Force -DisableNameChecking
    $regText = [System.IO.File]::ReadAllText((Join-Path $source "01_Настройки_SolidWorks\Реестровые_Профили\01_SW2025_Корпоративный_Стандарт_ЕСКД.reg"), [System.Text.Encoding]::Unicode)
    $adapted = Convert-EskdRegProfile -Text $regText -SourceRoot $source -LocalRoot $local -RegistryRoot $sandbox
    Expect "профиль: разделы HKLM" ([regex]::Matches($adapted, '(?m)^\[-?HKEY_LOCAL_MACHINE').Count) 0
    Expect "профиль: разделы вне тестового корня" ([regex]::Matches($adapted, "(?m)^\[-?HKEY_CURRENT_USER\\Software\\(?!$registryName\\)").Count) 0
    Expect "профиль: следы d:\Work" ([regex]::Matches($adapted, '(?i)[a-z]:\\\\+work\\\\+').Count) 0
    Expect "профиль: Toolbox" ([regex]::Matches($adapted, '"Toolbox Data Location"').Count) 0
    Expect "класс: основные надписи" (Resolve-EskdProfilePath -Relative "02_Шаблоны_и_Форматки\Основные надписи" -SourceRoot "S" -LocalRoot "L") "S\02_Шаблоны_и_Форматки\Основные надписи"
    Expect "класс: макрос" (Resolve-EskdProfilePath -Relative "$swplusRel\MProp\MProp.swp" -SourceRoot "S" -LocalRoot "L") "L\$swplusRel\MProp\MProp.swp"
    Expect "класс: надстройка" (Resolve-EskdProfilePath -Relative "$addinRel\x.dll" -SourceRoot "S" -LocalRoot "L") "L\$addinRel\x.dll"
    Expect "класс: шаблоны" (Resolve-EskdProfilePath -Relative "02_Шаблоны_и_Форматки\Шаблоны документов" -SourceRoot "S" -LocalRoot "L") "S\02_Шаблоны_и_Форматки\Шаблоны документов"
    Expect "класс: папка «SWPlusMacro_v_2018_SP0.0 2» не путается с папкой SWPlus" (Resolve-EskdProfilePath -Relative "${swplusRel} 2\x.swp" -SourceRoot "S" -LocalRoot "L") "S\${swplusRel} 2\x.swp"

    $setup = Join-Path $source "01_Настройки_SolidWorks\Setup_Workstation_SolidWorks.ps1"
    $setupArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $setup, "-Author", "Тестов Т.Т.", "-Firm", "ООО «Проверка»",
              "-CloseMode", "Skip", "-NonInteractive", "-LocalRoot", $local, "-RegistryRoot", $sandbox, "-SwVersion", "SOLIDWORKS 2025", "-Utf8Output")
    $run = {
        param([string[]]$extra = @())
        $psi = New-Object System.Diagnostics.ProcessStartInfo "powershell.exe"
        $psi.Arguments = (@($setupArgs) + $extra | ForEach-Object { if ($_ -match '[\s«»"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ } }) -join " "
        $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $p = [System.Diagnostics.Process]::Start($psi)
        $out = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd()
        $p.WaitForExit()
        return @($p.ExitCode, $out)
    }
    $code, $output = & $run
    Expect "код выхода установки" $code 0

    $sheetFormats = Join-Path $source "02_Шаблоны_и_Форматки\Основные надписи"
    $localSwPlus = Join-Path $local $swplusRel
    $ext = "$swKey\ExtReferences"
    Expect "шаблоны документов — источник" (Read-Value $ext "Document Template Folders") (Join-Path $source "02_Шаблоны_и_Форматки\Шаблоны документов")
    Expect "основные надписи — источник" (Read-Value $ext "Sheet Format Folders") $sheetFormats
    Expect "библиотека материалов — источник" (Read-Value $ext "Material Database Folders") (Join-Path $source "04_Библиотеки_Материалов_и_Профилей\Библиотека материалов")
    Expect "шаблоны свойств — источник" (Read-Value $ext "Custom Property Folders") (Join-Path $source "02_Шаблоны_и_Форматки\Шаблоны свойств")
    Expect "профили — источник" (Read-Value $ext "Weldment Profile Folders") (Join-Path $source "04_Библиотеки_Материалов_и_Профилей\Профили сварных деталей")
    Expect "папка макросов — локальная копия" (Read-Value $ext "Macro Folders") $localSwPlus
    Expect "стандарт оформления — локальная копия SpecEditor" (Read-Value $ext "Drafting Standard Folder") (Join-Path $localSwPlus "SpecEditor")
    Expect "кнопка MProp — локальная копия" (Read-Value "$swKey\User Defined Macros\01 - Macro Folder" "Source Path") (Join-Path $localSwPlus "MProp\MProp.swp")
    Expect "шаблон детали по умолчанию — источник" (Read-Value "$swKey\Document Templates" "Default Part template") (Join-Path $source "02_Шаблоны_и_Форматки\Шаблоны документов\Деталь.prtdot")
    Expect "Toolbox не подставлен из профиля" (Read-Value "$swKey\General" "Toolbox Data Location") $null
    # Библиотека проектирования: папка крепежа рядом с инструментарием (как на NAS) — в «Расположение файлов»
    Expect "библиотека проектирования — крепёж и фурнитура" (Read-Value $ext "Content Manager Folders") $fasteners
    # Т-57: папки поиска ссылок от места запуска; Т-56: режим совместной работы из профиля
    Expect "папки поиска ссылок" (Read-Value $ext "Document Folders") ($fasteners + ";" + (Join-Path $source "04_Библиотеки_Материалов_и_Профилей\Профили сварных деталей"))
    Expect "поиск по папкам включён" (Read-Value $ext "Use Search Rules") 1
    Expect "многопользовательская среда" (Read-Value "$swKey\Collab" "Enable Collab") 1
    Expect "проверка чужих изменений" (Read-Value "$swKey\Collab" "Ping Files") 1
    # З-2: тип отображения по умолчанию на чертеже — «Невидимые линии отображаются» (swHiddenEdgeDisplayDefault = 1)
    Expect "чертёж: невидимые линии отображаются" (Read-Value "$swKey\Drawings" "Display Mode") 1
    $dll = Join-Path $local "$addinRel\ESKD_Material_Sync_v5.dll"
    Expect "CodeBase надстройки — локальная копия" (Read-Value "$sandbox\Classes\CLSID\{B64E6875-B101-4D5C-B245-FF8D50772E25}\InprocServer32" "CodeBase") ("file:///" + $dll.Replace('\', '/'))
    Expect "автозагрузка надстройки" (Read-Value "$sandbox\SolidWorks\AddInsStartup\{B64E6875-B101-4D5C-B245-FF8D50772E25}" "(default)") 1
    Expect "фамилия" (Read-Value "$sandbox\SolidWorks\ESKD_Settings" "Author") "Тестов Т.Т."
    Expect "организация" (Read-Value "$sandbox\SolidWorks\ESKD_Settings" "Organization") "ООО «Проверка»"
    Expect "ESKD_Install: источник" (Read-Value "$sandbox\SolidWorks\ESKD_Install" "SourceRoot") $source
    Expect "ESKD_Install: итог" (Read-Value "$sandbox\SolidWorks\ESKD_Install" "LastResult") "OK"
    # Панель быстрого доступа: без базовых Btn0..Btn10 SolidWorks 2025 при запуске стирает её вместе с кнопками SWPlus
    $qat = "$swKey\User Interface\CommandManager\QAT\GB0"
    Expect "QAT: кнопок Btn0..Btn19" (@(0..19 | Where-Object { Read-Value $qat "Btn$_" }).Count) 20
    Expect "QAT: базовая кнопка SolidWorks Btn0" (Read-Value $qat "Btn0") "1,21781"
    Expect "QAT: базовая кнопка SolidWorks Btn10" (Read-Value $qat "Btn10") "1,54325"
    Expect "QAT: кнопка MProp Btn11" (Read-Value $qat "Btn11") "1,33639"
    Expect "QAT: кнопка SaveAsPDF Btn19" (Read-Value $qat "Btn19") "1,33647"

    $exported = Join-Path $temp "sandbox.reg"
    $null = & reg.exe export "HKCU\Software\$registryName" $exported /y 2>&1
    $dump = [System.IO.File]::ReadAllText($exported, [System.Text.Encoding]::Unicode)
    Expect "в реестре нет путей d:\Work" ([regex]::Matches($dump, '(?i)[a-z]:\\\\+work\\\\+').Count) 0
    Expect "в реестре нет «_Инструменты_Конструктора»" ([regex]::Matches($dump, '_Инструменты_Конструктора').Count) 0

    Expect "DLL в локальной копии" (Test-Path -LiteralPath $dll) $true
    Expect "иконки надстройки" (Test-Path -LiteralPath (Join-Path $local "$addinRel\Icons\icons_small.bmp")) $true
    Expect "MProp в локальной копии" (Test-Path -LiteralPath (Join-Path $localSwPlus "MProp\MProp.swp")) $true
    Expect "основных надписей в локальной копии нет" (@(Get-ChildItem -LiteralPath $local -Recurse -Filter *.slddrt | Where-Object { $_.DirectoryName -notlike "*\SpecEditor" }).Count) 0
    Expect "библиотеки материалов в локальной копии нет — только общая папка" (Test-Path -LiteralPath (Join-Path $local "04_Библиотеки_Материалов_и_Профилей")) $false
    Expect "локальные файлы без «только чтение»" (@(Get-ChildItem -LiteralPath $local -File -Recurse | Where-Object IsReadOnly).Count) 0
    $master = @([System.IO.File]::ReadAllLines((Join-Path $localSwPlus "Master\Master.ini"), $cp1251))
    Expect "Master.ini: основные надписи источника" $master[3] ($sheetFormats + "\")
    $fam = @([System.IO.File]::ReadAllLines((Join-Path $localSwPlus "MProp\MProp_Fam.txt"), $cp1251))
    Expect "MProp_Fam.txt: фамилия первой (З-3)" $fam[0] "Тестов Т.Т."
    $firm = @([System.IO.File]::ReadAllLines((Join-Path $localSwPlus "MProp\MProp_Firm.txt"), $cp1251))
    Expect "MProp_Firm.txt: пара первой (З-3)" ($firm[0] + "|" + $firm[1]) "ООО «Проверка»|"

    $after = Snapshot $source
    $changed = @($after.Keys | Where-Object { $before[$_] -ne $after[$_] }) + @($before.Keys | Where-Object { -not $after.ContainsKey($_) })
    Expect "источник не изменён" ($changed -join ", ") ""

    # Повторная установка: настройки макросов пользователя остаются, изменённые файлы выпуска возвращаются
    $prof = Join-Path $localSwPlus "MProp\MProp_Prof.txt"
    [System.IO.File]::AppendAllText($prof, "`$`$`$Профиль Тестов`r`n", $cp1251)
    $profText = [System.IO.File]::ReadAllText($prof, $cp1251)
    $masterLines = @($master); $masterLines[0] = "Arial"
    [System.IO.File]::WriteAllText((Join-Path $localSwPlus "Master\Master.ini"), (($masterLines -join "`r`n") + "`r`n"), $cp1251)
    $sort = Join-Path $localSwPlus "MProp\MProp_Sort.txt"
    [System.IO.File]::WriteAllText($sort, "испорчено", $cp1251)
    # Сброс перед профилем: личные настройки пропадают, данные пользователя (соглашение, последние файлы, Toolbox) остаются
    New-Item -Path "$swKey\Toolbars\Моя панель" -Force | Out-Null
    Set-ItemProperty -LiteralPath "$swKey\Toolbars\Моя панель" -Name "Visible" -Value 1
    Set-ItemProperty -LiteralPath "$swKey\ExtReferences" -Name "Sheet Format Folders" -Value "C:\Чужие форматки"
    New-Item -Path "$swKey\Security" -Force | Out-Null
    Set-ItemProperty -LiteralPath "$swKey\Security" -Name "EULA Accepted 2025 TEST" -Value "Yes"
    New-Item -Path "$swKey\Recent File List" -Force | Out-Null
    Set-ItemProperty -LiteralPath "$swKey\Recent File List" -Name "File1" -Value "C:\Проект\Деталь.sldprt"
    Set-ItemProperty -LiteralPath "$swKey\General" -Name "Toolbox Data Location" -Value "C:\Мой Toolbox"
    Set-ItemProperty -LiteralPath $qat -Name "Btn20" -Value "1,40001"
    $code2, $output2 = & $run
    $backups = @(Get-ChildItem -LiteralPath (Join-Path (Split-Path -Path $local -Parent) "Backups") -Recurse -Filter "*SOLIDWORKS 2025*.reg" -ErrorAction SilentlyContinue |
                 Where-Object { $_.Length -gt 0 -and [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::Unicode).Contains("Моя панель") })
    Expect "сброс: резервная копия раздела версии с прежними настройками создана" ($backups.Count -ge 1) $true
    Expect "сброс: ответы «Больше не показывать» из профиля" ((Get-Item -LiteralPath "$swKey\General\DontAskAgainOptions" -ErrorAction SilentlyContinue).ValueCount -gt 0) $true
    Expect "сброс: личная панель убрана" (Test-Path -LiteralPath "$swKey\Toolbars\Моя панель") $false
    Expect "сброс: чужой путь форматок заменён профилем" (Read-Value "$swKey\ExtReferences" "Sheet Format Folders") $sheetFormats
    Expect "сброс: принятие соглашения сохранено" (Read-Value "$swKey\Security" "EULA Accepted 2025 TEST") "Yes"
    Expect "сброс: последние файлы сохранены" (Read-Value "$swKey\Recent File List" "File1") "C:\Проект\Деталь.sldprt"
    Expect "сброс: путь Toolbox сохранён" (Read-Value "$swKey\General" "Toolbox Data Location") "C:\Мой Toolbox"
    Expect "сброс: кнопка пользователя в QAT сохранена" (Read-Value $qat "Btn20") "1,40001"
    Expect "сброс: QAT после повторной установки — Btn0..Btn19" (@(0..19 | Where-Object { Read-Value $qat "Btn$_" }).Count) 20
    Expect "сброс: фамилия вне раздела версии не тронута" (Read-Value "$sandbox\SolidWorks\ESKD_Settings" "Author") "Тестов Т.Т."
    $output += "`n--- повторная установка ---`n" + $output2
    Expect "код выхода повторной установки" $code2 0
    Expect "профили MProp пользователя сохранены" ([System.IO.File]::ReadAllText($prof, $cp1251)) $profText
    $master2 = @([System.IO.File]::ReadAllLines((Join-Path $localSwPlus "Master\Master.ini"), $cp1251))
    Expect "Master.ini: шрифт пользователя сохранён" $master2[0] "Arial"
    Expect "Master.ini: путь к основным надписям — источник" $master2[3] ($sheetFormats + "\")
    Expect "файл выпуска возвращён" ([System.IO.File]::ReadAllText($sort, $cp1251)) ([System.IO.File]::ReadAllText((Join-Path $source "$swplusRel\MProp\MProp_Sort.txt"), $cp1251))
    $fam2 = @([System.IO.File]::ReadAllLines((Join-Path $localSwPlus "MProp\MProp_Fam.txt"), $cp1251))
    Expect "фамилия не задвоена" (@($fam2 | Where-Object { $_ -eq "Тестов Т.Т." }).Count) 1
    $after2 = Snapshot $source
    Expect "источник не изменён повторной установкой" (@($after2.Keys | Where-Object { $before[$_] -ne $after2[$_] }) -join ", ") ""
    $logs = @(Get-ChildItem -LiteralPath (Join-Path (Split-Path -Path $local -Parent) "Logs") -Filter "install_*.log" -ErrorAction SilentlyContinue)
    Expect "журнал установки записан" ($logs.Count -ge 1) $true
    Expect "журнал установки — в ESKD_Install" ([bool](Read-Value "$sandbox\SolidWorks\ESKD_Install" "LastLog")) $true
    if ($logs.Count) { Expect "в журнале — итог установки" ([System.IO.File]::ReadAllText($logs[0].FullName).Contains("НАСТРОЙКА ЗАВЕРШЕНА")) $true }

    # ТЗ-01 -Mode Check: выпуск без хешей — 40; опубликованный выпуск, установлена рабочая копия — 10; после установки
    # выпуска — 0; испорченный файл локальной копии — 20; изменённый файл источника — 40. Check ничего не меняет.
    $code, $out = & $run @("-Mode", "Check")
    $output += "`n--- проверка без выпуска ---`n" + $out
    Expect "Check: выпуск не опубликован" $code 40
    $releaseFile = Join-Path $source "toolkit_release.json"
    $releaseJson = [ordered]@{ version = "2026.09.19.1200"; date = "19.09.2026 12:00"; commit = "test"; publisher = "test";
                              files = @(New-EskdReleaseFiles -SourceRoot $source) }
    [System.IO.File]::WriteAllText($releaseFile, ($releaseJson | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
    Expect "выпуск: хеши файлов надстройки и SWPlus" (@($releaseJson.files | Where-Object { $_.path -like "*ESKD_Material_Sync_v5.dll" -or $_.path -like "*MProp.swp" }).Count) 2
    Expect "выпуск: файлы настроек помечены" (@($releaseJson.files | Where-Object { $_.state -and $_.path -like "*MProp_Fam.txt" }).Count) 1
    $code, $out = & $run @("-Mode", "Check")
    Expect "Check: установлена рабочая копия, опубликован выпуск — нужно обновление" $code 10
    $code, $out = & $run
    $output += "`n--- установка выпуска ---`n" + $out
    Expect "установка выпуска" $code 0
    Expect "установка сверила копию с выпуском" ($out.Contains("совпадает с выпуском 2026.09.19.1200")) $true
    Expect "ESKD_Install: версия выпуска" (Read-Value "$sandbox\SolidWorks\ESKD_Install" "ReleaseVersion") "2026.09.19.1200"
    $code, $out = & $run @("-Mode", "Check")
    Expect "Check: актуально" $code 0
    $localBefore = Snapshot $local
    [System.IO.File]::AppendAllText((Join-Path $localSwPlus "MProp\MProp_Fam.txt"), "Коллега К.К.`r`n", $cp1251)
    $code, $out = & $run @("-Mode", "Check")
    Expect "Check: правка файла настроек пользователя — не повреждение" $code 0
    [System.IO.File]::WriteAllText($sort, "испорчено", $cp1251)
    $code, $out = & $run @("-Mode", "Check")
    Expect "Check: испорченный файл локальной копии" $code 20
    Expect "Check: назван испорченный файл" ($out.Contains("MProp_Sort.txt")) $true
    $srcFile = Get-Item -LiteralPath (Join-Path $source "$swplusRel\MProp\MProp_Sort.txt")
    $srcFile.IsReadOnly = $false
    $srcBytes = [System.IO.File]::ReadAllBytes($srcFile.FullName)
    [System.IO.File]::AppendAllText($srcFile.FullName, "x", $cp1251)
    $code, $out = & $run @("-Mode", "Check")
    Expect "Check: источник не совпадает с выпуском" $code 40
    Expect "Check: локальная копия не тронута" ((Snapshot $local).Keys.Count) $localBefore.Keys.Count
    [System.IO.File]::WriteAllBytes($srcFile.FullName, $srcBytes)

    # ТЗ-01 -Mode Uninstall: регистрация, кнопки SWPlus, вкладка, локальная копия и ESKD_Install — долой; фамилия,
    # базовые кнопки SolidWorks и кнопка пользователя в панели быстрого доступа остаются.
    $code, $out = & $run @("-Mode", "Uninstall")
    $output += "`n--- удаление ---`n" + $out
    Expect "Uninstall: код выхода" $code 0
    Expect "Uninstall: регистрация COM снята" (Test-Path -LiteralPath "$sandbox\Classes\CLSID\{B64E6875-B101-4D5C-B245-FF8D50772E25}") $false
    Expect "Uninstall: автозагрузка снята" (Test-Path -LiteralPath "$sandbox\SolidWorks\AddInsStartup\{B64E6875-B101-4D5C-B245-FF8D50772E25}") $false
    Expect "Uninstall: кнопки SWPlus убраны" (@(11..19 | Where-Object { Read-Value $qat "Btn$_" }).Count) 0
    Expect "Uninstall: базовая кнопка SolidWorks осталась" (Read-Value $qat "Btn0") "1,21781"
    Expect "Uninstall: кнопка пользователя осталась" (Read-Value $qat "Btn20") "1,40001"
    Expect "Uninstall: макросы на локальную копию убраны" (Read-Value "$swKey\User Defined Macros\01 - Macro Folder" "Source Path") $null
    Expect "Uninstall: локальная копия удалена" (Test-Path -LiteralPath $local) $false
    Expect "Uninstall: ESKD_Install удалён" (Test-Path -LiteralPath "$sandbox\SolidWorks\ESKD_Install") $false
    Expect "Uninstall: фамилия осталась" (Read-Value "$sandbox\SolidWorks\ESKD_Settings" "Author") "Тестов Т.Т."
    $code, $out = & $run @("-Mode", "Check")
    Expect "Check после удаления: не установлено" $code 10
} catch {
    $problems.Add("исключение: $($_.Exception.Message) @ $($_.InvocationInfo.ScriptLineNumber)")
} finally {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    if (Test-Path -LiteralPath $temp) {
        Get-ChildItem -LiteralPath $temp -File -Recurse -Force | ForEach-Object { $_.IsReadOnly = $false }
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
# Выпуск SWTools: публикация установщика по манифесту сборки, описание выпуска, решение об установке (без запуска установщика)
$swtTemp = Join-Path ([System.IO.Path]::GetTempPath()) "eskd_swt_$id"
try {
    Import-Module (Join-Path $RepoRoot "01_Настройки_SolidWorks\EskdDeploy.psm1") -Force -DisableNameChecking
    $build = Join-Path $swtTemp "сборка"
    $share = Join-Path $swtTemp "общая папка"
    New-Item -ItemType Directory -Path $build, $share -Force | Out-Null
    $setup = Join-Path $build "SWTools-1.1.109-LOCAL-TEST-Setup.exe"
    [System.IO.File]::WriteAllBytes($setup, [byte[]](1..64))
    $sha = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    $eula = "ab" * 32
    $manifest = @{ product_version = "1.1.109"; source_commit = "72ba847"; artifact_kind = "local-test-installer";
                   setup = @{ sha256 = $sha }; installer_behavior = @{ silent_install_eula_sha256 = $eula } }
    [System.IO.File]::WriteAllText([System.IO.Path]::ChangeExtension($setup, ".manifest.json"), ($manifest | ConvertTo-Json -Depth 3))
    Expect "без выпуска SWTools — нет описания" (Get-EskdSwToolsRelease -SourceRoot $share) ""
    $old = Join-Path $share "03_Макросы_и_Плагины\SWTools_Установщик\SWTools-1.1.100-Setup.exe"
    New-Item -ItemType Directory -Path (Split-Path -Parent $old) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($old, [byte[]](1..4))
    $rel = Publish-EskdSwToolsSetup -SetupPath $setup -TargetRoot $share
    Expect "версия выпуска SWTools" $rel.Version "1.1.109"
    Expect "SHA-256 установщика SWTools" $rel.Sha256 $sha
    Expect "SHA-256 EULA SWTools" $rel.EulaSha256 $eula
    Expect "установщик SWTools в общей папке" (Test-Path -LiteralPath $rel.SetupPath) $true
    Expect "прежний установщик SWTools удалён" (Test-Path -LiteralPath $old) $false
    Expect "описание перечитывается" (Get-EskdSwToolsRelease -SourceRoot $share).SetupPath $rel.SetupPath
    Expect "не установлен — ставить" (Test-EskdSwToolsUpdateNeeded -Release $rel.Version) $true
    Expect "старее выпуска — ставить" (Test-EskdSwToolsUpdateNeeded -Release $rel.Version -Installed ([version]"1.1.102")) $true
    Expect "та же версия — не ставить" (Test-EskdSwToolsUpdateNeeded -Release $rel.Version -Installed ([version]"1.1.109")) $false
    Expect "новее выпуска — не понижать" (Test-EskdSwToolsUpdateNeeded -Release $rel.Version -Installed ([version]"1.2.0")) $false
    [System.IO.File]::WriteAllBytes($setup, [byte[]](1..65))
    $refused = $false
    try { Publish-EskdSwToolsSetup -SetupPath $setup -TargetRoot $share | Out-Null } catch { $refused = $true }
    Expect "установщик, не совпадающий с манифестом, не публикуется" $refused $true
    Expect "после отказа прежнее описание цело" (Get-EskdSwToolsRelease -SourceRoot $share).Sha256 $sha
} catch {
    $problems.Add("SWTools: исключение: $($_.Exception.Message) @ $($_.InvocationInfo.ScriptLineNumber)")
} finally {
    Remove-Item -LiteralPath $swtTemp -Recurse -Force -ErrorAction SilentlyContinue
}
Expect "настоящая ветка ESKD_Install не создана тестом" (Test-Path "HKCU:\Software\SolidWorks\ESKD_Install") $liveInstallBefore
Expect "настоящая фамилия не изменена" (Read-Value "HKCU:\Software\SolidWorks\ESKD_Settings" "Author") $liveAuthorBefore
$tail = if ($problems.Count) { ($output -split "`n" | Select-Object -Last 40) -join "`n" } else { "" }
[pscustomobject]@{ ok = ($problems.Count -eq 0); problems = @($problems); sandboxRemoved = -not (Test-Path -LiteralPath $sandbox); output = $tail } |
    ConvertTo-Json -Compress
if ($problems.Count) { exit 1 }
