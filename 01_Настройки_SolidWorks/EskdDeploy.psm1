<#
.SYNOPSIS
    Развёртывание инструментария ЕСКД из общей папки (сетевая схема, решение владельца 14.09.2026).
.DESCRIPTION
    Источник — папка инструментария, из которой запущен установщик (сетевая или локальная, путь не зашит).
    Данные SolidWorks читает прямо из источника: шаблоны, основные надписи, библиотека материалов, профили.
    Макросы SWPlus и надстройка ЕСКД копируются в профиль пользователя (%LOCALAPPDATA%\ESKD\Toolkit): макросы
    пишут настройки рядом с собой, DLL надстройки SolidWorks держит открытой. В источник установка не пишет.
    Функции модуля проверяются автотестом без SolidWorks (09_Тесты/tools/check_deploy_engine.ps1).
#>

$script:Rel = @{
    Templates     = "02_Шаблоны_и_Форматки"
    Addin         = "03_Макросы_и_Плагины\ESKD_Material_Sync_Addin"
    SwPlus        = "03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0"
    SheetFormats  = "02_Шаблоны_и_Форматки\Основные надписи"
    Libraries     = "04_Библиотеки_Материалов_и_Профилей"
    Fonts         = "05_Шрифты"
    Setup         = "01_Настройки_SolidWorks"
    SwTools       = "03_Макросы_и_Плагины\SWTools_Установщик"
}

# Файлы надстройки, которые нужны для работы (исходники и скрипты сборки не копируются).
$script:AddinFiles = @("ESKD_Material_Sync_v5.dll", "ESKD.exe", "ESKD_Sync.exe", "ESKD.ico",
    "SolidWorks.Interop.sldworks.dll", "SolidWorks.Interop.swconst.dll", "SolidWorks.Interop.swpublished.dll")

# Файлы, в которые макросы SWPlus пишут настройки пользователя (выгрузка VBA: FrmMProp:2437,2480; *_Pref: CmdSave_Click;
# MyStandard() пяти макросов). Если файл уже есть в локальной копии, обновление его не заменяет.
$script:SwPlusStateFiles = @(
    "MProp\MProp_Prof.txt", "MProp\MProp_Project.txt", "MProp\MProp.ini",
    # Общие списки фамилий и организаций пусты (решение владельца 15.09.2026): каждый вписывает своё при установке,
    # коллег добавляет в MProp сам — обновление эти списки не перезаписывает.
    "MProp\MProp_Fam.txt", "MProp\MProp_Firm.txt",
    "DProp\DProp.ini", "SProp\SProp.ini",
    "SpecEditor\SpecEditor.ini", "SpecEditor\MyProperties_2.ini",
    "Master\Master.ini",
    "ТТ\TT.ini", "ТТ\TT.TXT", "ТТ\TT_Prof.txt",
    "SaveAsPDF\SaveAsPDF.ini", "SaveAsPDF\PDFCreator.dat"
)

function Get-EskdRelativePaths { return $script:Rel.Clone() }

function Test-EskdSourceRoot {
    # Папка инструментария: есть шаблоны, макросы SWPlus, библиотеки.
    param([Parameter(Mandatory = $true)][string]$Path)
    foreach ($rel in @($script:Rel.Templates, $script:Rel.SwPlus, $script:Rel.Libraries)) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $rel))) { return $false }
    }
    return $true
}

function Find-EskdSourceRoot {
    # Корень — папка, из которой запущен установщик (01_Настройки_SolidWorks) или её родитель.
    param([Parameter(Mandatory = $true)][string]$StartPath)
    $cur = $StartPath
    for ($i = 0; $i -lt 3 -and $cur; $i++) {
        if (Test-EskdSourceRoot -Path $cur) { return $cur.TrimEnd('\') }
        $cur = Split-Path -Path $cur -Parent
    }
    return $null
}

function Get-EskdDefaultLocalRoot { return (Join-Path $env:LOCALAPPDATA "ESKD\Toolkit") }

function Get-EskdLayout {
    param([Parameter(Mandatory = $true)][string]$SourceRoot, [Parameter(Mandatory = $true)][string]$LocalRoot)
    $r = $script:Rel
    return [pscustomobject]@{
        SourceRoot       = $SourceRoot
        LocalRoot        = $LocalRoot
        SourceAddin      = Join-Path $SourceRoot $r.Addin
        SourceSwPlus     = Join-Path $SourceRoot $r.SwPlus
        SheetFormats     = Join-Path $SourceRoot $r.SheetFormats
        Fonts            = Join-Path $SourceRoot $r.Fonts
        SwTools          = Join-Path $SourceRoot $r.SwTools
        LocalAddin       = Join-Path $LocalRoot $r.Addin
        LocalAddinDll    = Join-Path (Join-Path $LocalRoot $r.Addin) "ESKD_Material_Sync_v5.dll"
        LocalSwPlus      = Join-Path $LocalRoot $r.SwPlus
        PropertyTemplates = Join-Path $SourceRoot "$($r.Templates)\Шаблоны свойств"
        MaterialFolder   = Join-Path $SourceRoot "$($r.Libraries)\Библиотека материалов"
        RegProfile       = Join-Path $SourceRoot "$($r.Setup)\Реестровые_Профили\01_SW2025_Корпоративный_Стандарт_ЕСКД.reg"
        Release          = Join-Path $SourceRoot "toolkit_release.json"
    }
}

function Resolve-EskdProfilePath {
    # Класс пути профиля по подпути после корня инструментария: макросы SWPlus и надстройка — локальная копия,
    # всё остальное (шаблоны и основные надписи в 02, библиотеки в 04) — источник.
    param([Parameter(Mandatory = $true)][string]$Relative, [Parameter(Mandatory = $true)][string]$SourceRoot,
          [Parameter(Mandatory = $true)][string]$LocalRoot)
    $rel = $Relative.TrimStart('\')
    $low = $rel.ToLowerInvariant()
    $isUnder = { param($prefix) $p = $prefix.ToLowerInvariant(); $low -eq $p -or $low.StartsWith($p + "\") }
    if ((& $isUnder $script:Rel.SwPlus) -or (& $isUnder $script:Rel.Addin)) { $base = $LocalRoot }
    else { $base = $SourceRoot }
    if (-not $rel) { return $base }
    return (Join-Path $base $rel)
}

function Convert-EskdRegProfile {
    <#
    Адаптация текста профиля .reg: корень инструментария (любая буква диска, путь до «_Инструменты_Конструктора»)
    заменяется по классу пути; %USERPROFILE% раскрывается; значения Toolbox убираются (их задаёт установщик по факту);
    разделы HKLM убираются без прав администратора; ветка версии SolidWorks переименовывается; при тестовом корне
    реестра все разделы HKCU\Software переносятся в него.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$LocalRoot,
        [string]$UserProfile = $env:USERPROFILE,
        [string]$SwVersion = "SOLIDWORKS 2025",
        [switch]$KeepMachineSections,
        [string]$RegistryRoot = "HKCU:\Software"
    )
    $pattern = '(?i)[A-Za-z]:(?:\\\\+|/)(?:[^"\r\n;\\/]+(?:\\\\+|/))*?_Инструменты_Конструктора((?:(?:\\\\+|/)[^"\r\n;\\/]+)*)'
    $evaluator = [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        $rel = ($m.Groups[1].Value -replace '(\\\\+|/)', '\')
        (Resolve-EskdProfilePath -Relative $rel -SourceRoot $SourceRoot -LocalRoot $LocalRoot).Replace('\', '\\')
    }
    $out = [regex]::Replace($Text, $pattern, $evaluator)
    $out = $out.Replace('%USERPROFILE%', $UserProfile.Replace('\', '\\'))
    $out = [regex]::Replace($out, '(?m)^"Toolbox Data Location"="[^"\r\n]*"\r?\n', '')
    if ($SwVersion -ne "SOLIDWORKS 2025") { $out = [regex]::Replace($out, 'SOLIDWORKS 20\d{2}', $SwVersion) }
    if (-not $KeepMachineSections) {
        $out = [regex]::Replace($out, '(?ms)^\[-?HKEY_LOCAL_MACHINE\\[^\]]*\]\r?\n.*?(?=^\[|\z)', '')
    }
    if ($RegistryRoot -ne "HKCU:\Software") {
        if ($RegistryRoot -notmatch '^HKCU:\\Software\\(.+)$') { throw "Тестовый корень реестра должен быть в HKCU:\Software: $RegistryRoot" }
        $sub = $Matches[1]
        $out = [regex]::Replace($out, '(?m)^\[(-?)HKEY_CURRENT_USER\\Software\\', "[`$1HKEY_CURRENT_USER\Software\$sub\")
    }
    return $out
}

function Test-EskdFileSame {
    param([string]$A, [string]$B)
    if (-not (Test-Path -LiteralPath $B)) { return $false }
    $fa = Get-Item -LiteralPath $A; $fb = Get-Item -LiteralPath $B
    return ($fa.Length -eq $fb.Length) -and ($fa.LastWriteTimeUtc -eq $fb.LastWriteTimeUtc)
}

function Copy-EskdFile {
    # Копия с сохранением времени изменения; атрибут «только чтение» источника снимается у копии.
    param([string]$Source, [string]$Target)
    $dir = Split-Path -Path $Target -Parent
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    if (Test-Path -LiteralPath $Target) {
        $existing = Get-Item -LiteralPath $Target
        if ($existing.IsReadOnly) { $existing.IsReadOnly = $false }
    }
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    $copy = Get-Item -LiteralPath $Target
    if ($copy.IsReadOnly) { $copy.IsReadOnly = $false }
}

function Copy-EskdLocalInstance {
    <#
    Локальная копия: надстройка (файлы $AddinFiles и Icons) и вся папка SWPlus. Основные надписи (02) и библиотека
    материалов не копируется: SolidWorks и надстройка берут её только из общей папки (решение владельца 14.09.2026). Файлы выпуска заменяются
    версией источника; файлы настроек макросов ($SwPlusStateFiles), уже существующие локально, не трогаются.
    Возвращает сводку: Copied, Kept, Skipped.
    #>
    param([Parameter(Mandatory = $true)][pscustomobject]$Layout)
    $copied = New-Object System.Collections.Generic.List[string]
    $kept = New-Object System.Collections.Generic.List[string]
    $same = 0

    $sourceDll = Join-Path $Layout.SourceAddin "ESKD_Material_Sync_v5.dll"
    if (-not (Test-Path -LiteralPath $sourceDll)) {
        throw "В папке инструментария нет собранной надстройки ($sourceDll). Опубликуйте выпуск: 01_Настройки_SolidWorks\Publish-EskdToolkit.ps1"
    }
    $pairs = New-Object System.Collections.Generic.List[object]
    foreach ($name in $script:AddinFiles) {
        $src = Join-Path $Layout.SourceAddin $name
        if (Test-Path -LiteralPath $src) { $pairs.Add(@($src, (Join-Path $Layout.LocalAddin $name), $false)) }
    }
    $icons = Join-Path $Layout.SourceAddin "Icons"
    if (Test-Path -LiteralPath $icons) {
        foreach ($f in Get-ChildItem -LiteralPath $icons -File) { $pairs.Add(@($f.FullName, (Join-Path $Layout.LocalAddin "Icons\$($f.Name)"), $false)) }
    }
    $stateLow = @($script:SwPlusStateFiles | ForEach-Object { $_.ToLowerInvariant() })
    foreach ($f in Get-ChildItem -LiteralPath $Layout.SourceSwPlus -File -Recurse) {
        $rel = $f.FullName.Substring($Layout.SourceSwPlus.TrimEnd('\').Length + 1)
        $pairs.Add(@($f.FullName, (Join-Path $Layout.LocalSwPlus $rel), ($stateLow -contains $rel.ToLowerInvariant())))
    }

    foreach ($p in $pairs) {
        $src, $dst, $isState = $p
        if ($isState -and (Test-Path -LiteralPath $dst)) { $kept.Add($dst); continue }
        if (Test-EskdFileSame -A $src -B $dst) { $same++; continue }
        Copy-EskdFile -Source $src -Target $dst
        $copied.Add($dst)
    }
    return [pscustomobject]@{ Copied = @($copied); Kept = @($kept); Same = $same }
}

# WP-3.4: справочники SWPlus записываются, только если содержимое меняется; фамилии и организации дописываются в конец.
function Write-SwPlusLines {
    param([Parameter(Mandatory = $true)][string]$Path, [string[]]$Lines = @())
    $encoding = [System.Text.Encoding]::GetEncoding(1251)
    $text = if ($Lines.Count) { ($Lines -join "`r`n") + "`r`n" } else { "" }
    if ((Test-Path -LiteralPath $Path) -and ([System.IO.File]::ReadAllText($Path, $encoding) -ceq $text)) { return $false }
    [System.IO.File]::WriteAllText($Path, $text, $encoding)
    return $true
}

function Add-SwPlusFamily {
    # MProp_Fam.txt: фамилия на строке; новая — в конец.
    param([Parameter(Mandatory = $true)][string]$Path, [string]$Name)
    if (-not $Name -or -not $Name.Trim()) { return $false }
    $lines = @()
    if (Test-Path -LiteralPath $Path) { $lines = @([System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::GetEncoding(1251))) }
    if (@($lines | Where-Object { $_.Trim() -eq $Name.Trim() }).Count) { return $false }
    return Write-SwPlusLines -Path $Path -Lines (@($lines | Where-Object { $_.Trim() -ne "" }) + @($Name.Trim()))
}

function Add-SwPlusFirm {
    # MProp_Firm.txt: пары строк «организация / код»; новая пара — в конец.
    param([Parameter(Mandatory = $true)][string]$Path, [string]$Name)
    if (-not $Name -or -not $Name.Trim()) { return $false }
    $lines = @()
    if (Test-Path -LiteralPath $Path) { $lines = @([System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::GetEncoding(1251))) }
    for ($i = 0; $i -lt $lines.Count; $i += 2) {
        if ($lines[$i].Trim() -eq $Name.Trim()) { return $false }
    }
    if ($lines.Count % 2 -eq 1) { $lines += "" }
    return Write-SwPlusLines -Path $Path -Lines ($lines + @($Name.Trim(), ""))
}

function Set-EskdMasterIniFormats {
    # Master.ini строка 4 — папка основных надписей (FrmMaster:147, FrmDProp:250); в локальной копии — папка источника.
    param([Parameter(Mandatory = $true)][string]$MasterIni, [Parameter(Mandatory = $true)][string]$SheetFormats)
    if (-not (Test-Path -LiteralPath $MasterIni)) { return $false }
    $lines = @([System.IO.File]::ReadAllLines($MasterIni, [System.Text.Encoding]::GetEncoding(1251)))
    while ($lines.Count -lt 4) { $lines += "" }
    $lines[3] = $SheetFormats.TrimEnd('\') + '\'
    return Write-SwPlusLines -Path $MasterIni -Lines $lines
}

function Get-EskdRelease {
    # toolkit_release.json пишет публикация; в клоне репозитория его нет — выпуск «рабочая копия».
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return [pscustomobject]@{ Version = "рабочая копия"; Commit = ""; Date = "" } }
    $json = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    return [pscustomobject]@{ Version = [string]$json.version; Commit = [string]$json.commit; Date = [string]$json.date }
}

function Read-EskdRegTree {
    # Значения и подразделы ключа HKCU (RegistryKey .NET: имена и типы без раскрытия %переменных%).
    param([Microsoft.Win32.RegistryKey]$Key)
    $values = foreach ($name in $Key.GetValueNames()) {
        [pscustomobject]@{ Name = $name; Kind = $Key.GetValueKind($name)
                           Value = $Key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    }
    $children = foreach ($childName in $Key.GetSubKeyNames()) {
        $child = $Key.OpenSubKey($childName)
        try { [pscustomobject]@{ Name = $childName; Tree = (Read-EskdRegTree -Key $child) } } finally { $child.Close() }
    }
    return [pscustomobject]@{ Values = @($values); Children = @($children) }
}

function Write-EskdRegTree {
    param([Microsoft.Win32.RegistryKey]$Key, $Tree)
    foreach ($v in $Tree.Values) { $Key.SetValue($v.Name, $v.Value, $v.Kind) }
    foreach ($c in $Tree.Children) {
        $child = $Key.CreateSubKey($c.Name)
        try { Write-EskdRegTree -Key $child -Tree $c.Tree } finally { $child.Close() }
    }
}

function Reset-EskdSolidWorksProfile {
    <#
    Сброс настроек пользователя SolidWorks перед импортом корпоративного профиля (решение владельца 15.09.2026): раздел
    HKCU\...\SolidWorks\<версия> удаляется целиком, как «Сброс настроек» SolidWorks Rx, чтобы результат установки не
    зависел от прежнего состояния ПК. Сохраняются только данные пользователя, а не настройки: принятие лицензионного
    соглашения (Security), списки последних файлов и папок, путь Toolbox и панель быстрого доступа (QAT): SolidWorks 2025
    при запуске стирает всю панель, если в ней нет его базовых кнопок Btn0..Btn10, и кнопки SWPlus пропадают.
    Лицензии, надстройки при запуске и ESKD_Settings лежат вне раздела версии и не затрагиваются. Возвращает сводку: Existed, Preserved.
    #>
    param([Parameter(Mandatory = $true)][string]$UserRoot, [Parameter(Mandatory = $true)][string]$SwVersion)
    if ($UserRoot -notmatch '^HKCU:\\(.+)$') { throw "Сброс только в HKCU: $UserRoot" }
    $sub = $Matches[1].TrimEnd('\') + "\SolidWorks\$SwVersion"
    $hkcu = [Microsoft.Win32.Registry]::CurrentUser
    $keepTrees = @("Security", "Recent File List", "Recent Folder List", "Recent Macro File List", "User Interface\CommandManager\QAT")
    $keepValues = @(@{ Key = "General"; Name = "Toolbox Data Location" })
    $version = $hkcu.OpenSubKey($sub)
    if ($null -eq $version) { return [pscustomobject]@{ Existed = $false; Preserved = @() } }
    $saved = @(); $savedValues = @()
    try {
        foreach ($name in $keepTrees) {
            $k = $version.OpenSubKey($name)
            if ($k) { try { $saved += [pscustomobject]@{ Name = $name; Tree = (Read-EskdRegTree -Key $k) } } finally { $k.Close() } }
        }
        foreach ($item in $keepValues) {
            $k = $version.OpenSubKey($item.Key)
            if ($k) {
                try {
                    if ($k.GetValueNames() -contains $item.Name) {
                        $savedValues += [pscustomobject]@{ Key = $item.Key; Name = $item.Name; Kind = $k.GetValueKind($item.Name)
                                                           Value = $k.GetValue($item.Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
                    }
                } finally { $k.Close() }
            }
        }
    } finally { $version.Close() }

    $hkcu.DeleteSubKeyTree($sub, $false)
    $restored = $hkcu.CreateSubKey($sub)
    try {
        foreach ($s in $saved) {
            $k = $restored.CreateSubKey($s.Name)
            try { Write-EskdRegTree -Key $k -Tree $s.Tree } finally { $k.Close() }
        }
        foreach ($v in $savedValues) {
            $k = $restored.CreateSubKey($v.Key)
            try { $k.SetValue($v.Name, $v.Value, $v.Kind) } finally { $k.Close() }
        }
    } finally { $restored.Close() }
    return [pscustomobject]@{ Existed = $true; Preserved = @($saved.Name) + @($savedValues | ForEach-Object { "$($_.Key)\$($_.Name)" }) }
}

# ------------------------------------------------------------------ SWTools
# Установщик SWTools не хранится в репозитории: Publish-EskdToolkit -SwToolsSetup кладёт его в общую папку
# (03_Макросы_и_Плагины\SWTools_Установщик) вместе с описанием swtools_release.json, установщик рабочего места ставит его оттуда.

function Get-EskdSwToolsRelease {
    # Описание выпуска SWTools в папке инструментария или $null.
    param([Parameter(Mandatory = $true)][string]$SourceRoot)
    $dir = Join-Path $SourceRoot $script:Rel.SwTools
    $file = Join-Path $dir "swtools_release.json"
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    $json = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($field in "version", "setup", "sha256", "eula_sha256") {
        if (-not "$($json.$field)".Trim()) { throw "В $file нет поля $field" }
    }
    if ("$($json.setup)" -match '[\\/]') { throw "В $file поле setup должно быть именем файла: $($json.setup)" }
    return [pscustomobject]@{
        Version    = [version]"$($json.version)"
        SetupPath  = Join-Path $dir "$($json.setup)"
        Sha256     = "$($json.sha256)".ToLowerInvariant()
        EulaSha256 = "$($json.eula_sha256)".ToLowerInvariant()
        Commit     = "$($json.source_commit)"
    }
}

function Get-EskdSwToolsInstalledVersion {
    # Версия установленного SWTools (запись удаления программ) или $null.
    foreach ($root in "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") {
        foreach ($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
            $entry = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            if ($entry -and "$($entry.DisplayName)" -eq "SWTools" -and "$($entry.DisplayVersion)" -match '^\d+(\.\d+){1,3}$') {
                return [version]"$($entry.DisplayVersion)"
            }
        }
    }
    return $null
}

function Test-EskdSwToolsUpdateNeeded {
    # Ставить ли SWTools: не установлен или установлен старее выпуска. Более новая версия на ПК не понижается.
    param([Parameter(Mandatory = $true)][version]$Release, [version]$Installed)
    return (-not $Installed) -or ($Installed -lt $Release)
}

function Get-EskdSwToolsLicensePath {
    return (Join-Path $env:LOCALAPPDATA "Lunin V\SWTools\license-v2.bin")
}

function Publish-EskdSwToolsSetup {
    # Кладёт установщик SWTools в папку выпуска и пишет swtools_release.json по манифесту сборки SWTools
    # (<Setup>.manifest.json рядом с установщиком: версия, коммит, SHA-256 EULA для тихой установки).
    param([Parameter(Mandatory = $true)][string]$SetupPath, [Parameter(Mandatory = $true)][string]$TargetRoot)
    $setup = Get-Item -LiteralPath $SetupPath -ErrorAction Stop
    $manifestPath = [System.IO.Path]::ChangeExtension($setup.FullName, ".manifest.json")
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Нет манифеста сборки SWTools: $manifestPath" }
    $manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $sha = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ("$($manifest.setup.sha256)".ToLowerInvariant() -ne $sha) { throw "SHA-256 установщика SWTools не совпадает с манифестом: $($setup.FullName)" }
    $eula = "$($manifest.installer_behavior.silent_install_eula_sha256)".ToLowerInvariant()
    if ($eula -notmatch '^[0-9a-f]{64}$') { throw "В манифесте SWTools нет SHA-256 EULA для тихой установки" }
    $dir = Join-Path $TargetRoot $script:Rel.SwTools
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $releaseFile = Join-Path $dir "swtools_release.json"
    Remove-Item -LiteralPath $releaseFile -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $dir -Filter "SWTools-*Setup.exe" -File | Where-Object { $_.Name -ne $setup.Name } | Remove-Item -Force
    $copy = Join-Path $dir $setup.Name
    Copy-Item -LiteralPath $setup.FullName -Destination $copy -Force
    if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha) {
        throw "Установщик SWTools скопирован с ошибкой: $copy"
    }
    $release = [ordered]@{
        version       = "$($manifest.product_version)"
        setup         = $setup.Name
        sha256        = $sha
        eula_sha256   = $eula
        source_commit = "$($manifest.source_commit)"
        artifact_kind = "$($manifest.artifact_kind)"
    }
    [System.IO.File]::WriteAllText($releaseFile, ($release | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
    return Get-EskdSwToolsRelease -SourceRoot $TargetRoot
}

Export-ModuleMember -Function *-Eskd*, Write-SwPlusLines, Add-SwPlusFamily, Add-SwPlusFirm
