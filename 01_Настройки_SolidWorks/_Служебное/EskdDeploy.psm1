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

function Find-EskdForeignPaths {
    <#
    Пути с буквой диска в адаптированном профиле .reg, которые не ведут ни в инструментарий, ни в локальную копию, ни в
    профиль пользователя, ни в папки Windows/Program Files. Новый экспорт .reg с ПК разработчика (его диск D:) иначе молча
    разнёсся бы на все рабочие места (аудит 19.09, У-В1). Возвращает список различных путей (до трёх уровней).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [string[]]$Allowed = @()
    )
    $roots = @($Allowed + @($env:USERPROFILE, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:windir) |
        Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\').ToLowerInvariant() + '\' })
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($Text, '(?i)(?<![A-Za-z])[A-Za-z]:(?:\\\\)+[^"\r\n;]*')) {
        $path = ($m.Value -replace '(\\)+', '\')
        $low = $path.ToLowerInvariant() + '\'
        if (@($roots | Where-Object { $low.StartsWith($_) }).Count) { continue }
        $short = (($path -split '\\') | Select-Object -First 3) -join '\'
        if (-not $found.Contains($short)) { $found.Add($short) }
    }
    return @($found)
}

function Test-EskdFileSame {
    <#
    Файл локальной копии совпадает с выпуском. Размер и время сравниваются первыми (быстро); при разнице во времени
    решает SHA-256: у Synology и NTFS разная точность времени, и по одному времени перекопировалось бы всё (аудит 19.09, У-В9).
    #>
    param([string]$A, [string]$B)
    if (-not (Test-Path -LiteralPath $B)) { return $false }
    $fa = Get-Item -LiteralPath $A; $fb = Get-Item -LiteralPath $B
    if ($fa.Length -ne $fb.Length) { return $false }
    if ($fa.LastWriteTimeUtc -eq $fb.LastWriteTimeUtc) { return $true }
    return (Get-EskdFileSha256 -Path $A) -eq (Get-EskdFileSha256 -Path $B)
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

function Get-EskdInstanceFiles {
    <#
    Файлы выпуска, которые попадают в локальную копию: надстройка (файлы $AddinFiles и Icons) и вся папка SWPlus.
    Relative — путь от корня инструментария (он же — от корня локальной копии), State — файл настроек макросов
    ($SwPlusStateFiles): пользователь его меняет, поэтому обновление и проверка его не сравнивают.
    #>
    param([Parameter(Mandatory = $true)][pscustomobject]$Layout)
    $root = $Layout.SourceRoot.TrimEnd('\')
    $sources = New-Object System.Collections.Generic.List[string]
    foreach ($name in $script:AddinFiles) {
        $src = Join-Path $Layout.SourceAddin $name
        if (Test-Path -LiteralPath $src) { $sources.Add($src) }
    }
    $icons = Join-Path $Layout.SourceAddin "Icons"
    if (Test-Path -LiteralPath $icons) { foreach ($f in Get-ChildItem -LiteralPath $icons -File) { $sources.Add($f.FullName) } }
    if (Test-Path -LiteralPath $Layout.SourceSwPlus) {
        foreach ($f in Get-ChildItem -LiteralPath $Layout.SourceSwPlus -File -Recurse) { $sources.Add($f.FullName) }
    }
    $swplusPrefix = $script:Rel.SwPlus.ToLowerInvariant() + "\"
    $stateLow = @($script:SwPlusStateFiles | ForEach-Object { $swplusPrefix + $_.ToLowerInvariant() })
    foreach ($src in $sources) {
        $rel = $src.Substring($root.Length + 1)
        [pscustomobject]@{
            Source   = $src
            Local    = Join-Path $Layout.LocalRoot $rel
            Relative = $rel
            State    = $stateLow -contains $rel.ToLowerInvariant()
        }
    }
}

function Get-EskdFileSha256 {
    <#
    SHA-256 файла средствами .NET, без Get-FileHash. Windows PowerShell 5.1, запущенный из PowerShell 7, наследует его
    PSModulePath: модуль Microsoft.PowerShell.Utility 7-й версии в 5.1 не грузится, Get-FileHash пропадает, и выражение
    «(Get-FileHash …).Hash» молча давало $null. Два $null сравнивались как «файлы совпадают» (обновление не копировало
    файлы), а $null против замера — как «не совпадают» (Drew переустанавливался, 20.09.2026). Ошибка чтения — исключение,
    а не пустой хэш.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-EskdFileSha256OrNull {
    # То же, но нет файла или он занят — $null (сверка, где отсутствие файла — законный ответ).
    param([string]$Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return Get-EskdFileSha256 -Path $Path } catch { return $null }
}

function Reset-EskdPowerShellEnvironment {
    <#
    Пути модулей Windows PowerShell 5.1 — свои, а не унаследованные. Установщик, запущенный из PowerShell 7 (pwsh —
    по умолчанию в Windows Terminal) или из программы, запущенной из него, получает его PSModulePath: 5.1 находит модули
    7-й версии раньше своих, они не загружаются, и пропадают команды (Get-FileHash и др.). Убираются пути PowerShell 7
    (…\PowerShell\…, но не …\WindowsPowerShell\…), свои пути 5.1 ставятся первыми. Дочерние процессы (установщики,
    msiexec) получают уже чистое окружение. Возвращает $true, если путь пришлось исправить.
    #>
    if ($PSVersionTable.PSEdition -eq "Core") { return $false }
    $own = @((Join-Path $env:ProgramFiles "WindowsPowerShell\Modules"),
             (Join-Path $env:SystemRoot "system32\WindowsPowerShell\v1.0\Modules"))
    $ownLow = @($own | ForEach-Object { $_.TrimEnd('\').ToLowerInvariant() })
    $rest = @(foreach ($item in ("$env:PSModulePath" -split ';')) {
        if (-not $item) { continue }
        $low = $item.TrimEnd('\').ToLowerInvariant()
        if ($ownLow -contains $low) { continue }
        if ($low -match '\\powershell\\' -and $low -notmatch '\\windowspowershell\\') { continue }
        $item
    })
    $clean = (@($own) + $rest) -join ';'
    if ($clean -eq $env:PSModulePath) { return $false }
    $env:PSModulePath = $clean
    return $true
}

function New-EskdReleaseFiles {
    # Для toolkit_release.json (ТЗ-01 Т-39): относительный путь и SHA-256 каждого файла локальной копии.
    param([Parameter(Mandatory = $true)][string]$SourceRoot)
    $layout = Get-EskdLayout -SourceRoot $SourceRoot -LocalRoot $SourceRoot
    foreach ($f in Get-EskdInstanceFiles -Layout $layout) {
        [ordered]@{ path = $f.Relative; sha256 = (Get-EskdFileSha256 -Path $f.Source); state = [bool]$f.State }
    }
}

function Copy-EskdLocalInstance {
    <#
    Локальная копия: файлы Get-EskdInstanceFiles. Основные надписи (02) и библиотека материалов не копируются:
    SolidWorks и надстройка берут их только из общей папки (решение владельца 14.09.2026). Файлы выпуска заменяются
    версией источника; файлы настроек макросов, уже существующие локально, не трогаются.
    Возвращает сводку: Copied, Kept, Same.
    #>
    param([Parameter(Mandatory = $true)][pscustomobject]$Layout)
    $copied = New-Object System.Collections.Generic.List[string]
    $kept = New-Object System.Collections.Generic.List[string]
    $same = 0

    $sourceDll = Join-Path $Layout.SourceAddin "ESKD_Material_Sync_v5.dll"
    if (-not (Test-Path -LiteralPath $sourceDll)) {
        throw "В папке инструментария нет собранной надстройки ($sourceDll). Опубликуйте выпуск: 01_Настройки_SolidWorks\Publish-EskdToolkit.ps1"
    }
    foreach ($f in Get-EskdInstanceFiles -Layout $Layout) {
        if ($f.State -and (Test-Path -LiteralPath $f.Local)) { $kept.Add($f.Local); continue }
        if (Test-EskdFileSame -A $f.Source -B $f.Local) { $same++; continue }
        Copy-EskdFile -Source $f.Source -Target $f.Local
        $copied.Add($f.Local)
    }
    return [pscustomobject]@{ Copied = @($copied); Kept = @($kept); Same = $same }
}

function Test-EskdInstall {
    <#
    Режим Check (ТЗ-01 Т-13, Т-18): сверка выпуска, установленной версии и локальной копии. Ничего не меняет.
    Code: 0 — актуально; 10 — нужно обновление (не установлено или установлен другой выпуск); 20 — повреждено (файл
    локальной копии пропал или изменён); 40 — выпуск не опубликован или файлы источника не совпадают с его хешами.
    Файлы настроек макросов не сравниваются: их меняет пользователь.
    #>
    param([Parameter(Mandatory = $true)][pscustomobject]$Layout, [string]$InstalledVersion = "")
    $problems = New-Object System.Collections.Generic.List[string]
    $result = { param($code, $state) [pscustomobject]@{ Code = $code; State = $state; Problems = @($problems) } }
    if (-not (Test-Path -LiteralPath $Layout.Release)) {
        $problems.Add("Нет toolkit_release.json: выпуск не опубликован (Publish-EskdToolkit.ps1).")
        return & $result 40 "Выпуск не опубликован"
    }
    $json = [System.IO.File]::ReadAllText($Layout.Release, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $files = @($json.files)
    if (-not $files.Count) {
        $problems.Add("В toolkit_release.json нет хешей файлов: выпуск опубликован прежней версией публикации — опубликуйте заново.")
        return & $result 40 "Выпуск без хешей"
    }
    foreach ($f in $files) {
        $src = Join-Path $Layout.SourceRoot $f.path
        if (-not (Test-Path -LiteralPath $src)) { $problems.Add("В источнике нет файла выпуска: $($f.path)"); continue }
        if ((Get-EskdFileSha256 -Path $src) -ne $f.sha256) { $problems.Add("Файл источника не совпадает с выпуском: $($f.path)") }
    }
    if ($problems.Count) { return & $result 40 "Источник не совпадает с выпуском" }
    if (-not $InstalledVersion) {
        $problems.Add("Инструментарий на этом рабочем месте не установлен.")
        return & $result 10 "Не установлено"
    }
    if ($InstalledVersion -ne [string]$json.version) {
        $problems.Add("Установлен выпуск $InstalledVersion, опубликован $($json.version).")
        return & $result 10 "Требуется обновление"
    }
    foreach ($f in $files) {
        if ($f.state) { continue }
        $local = Join-Path $Layout.LocalRoot $f.path
        if (-not (Test-Path -LiteralPath $local)) { $problems.Add("В локальной копии нет файла: $($f.path)"); continue }
        if ((Get-EskdFileSha256 -Path $local) -ne $f.sha256) { $problems.Add("Файл локальной копии изменён: $($f.path)") }
    }
    if ($problems.Count) { return & $result 20 "Повреждено" }
    return & $result 0 "Актуально"
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
    # MProp_Fam.txt: фамилия на строке. Фамилия этого рабочего места — первой: после «Удалить все свойства» MProp
    # берёт «Разработал» из первой строки списка (З-3). Остальные строки — по порядку, без пустых и повторов.
    param([Parameter(Mandatory = $true)][string]$Path, [string]$Name)
    if (-not $Name -or -not $Name.Trim()) { return $false }
    $name = $Name.Trim()
    $lines = @()
    if (Test-Path -LiteralPath $Path) { $lines = @([System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::GetEncoding(1251))) }
    $rest = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        $t = $line.Trim()
        if ($t -and $t -ne $name -and -not ($rest | Where-Object { $_ -eq $t })) { $rest.Add($t) }
    }
    $new = @($name) + @($rest)
    if (($new -join "`n") -ceq ($lines -join "`n")) { return $false }
    return Write-SwPlusLines -Path $Path -Lines $new
}

function Add-SwPlusFirm {
    # MProp_Firm.txt: пары строк «организация / код». Организация этого рабочего места — первой парой (со своим кодом):
    # при пустой «Конторе» MProp берёт первую (FrmMProp: CboFirm.ListIndex = 0, З-3).
    param([Parameter(Mandatory = $true)][string]$Path, [string]$Name)
    if (-not $Name -or -not $Name.Trim()) { return $false }
    $name = $Name.Trim()
    $lines = @()
    if (Test-Path -LiteralPath $Path) { $lines = @([System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::GetEncoding(1251))) }
    $code = ""
    $rest = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    for ($i = 0; $i -lt $lines.Count; $i += 2) {
        $firm = $lines[$i].Trim()
        $c = if ($i + 1 -lt $lines.Count) { $lines[$i + 1].Trim() } else { "" }
        if (-not $firm) { continue }
        if ($firm -eq $name) { $code = $c; continue }
        if ($seen.ContainsKey($firm)) { continue }
        $seen[$firm] = $true
        $rest.Add($firm); $rest.Add($c)
    }
    $new = @($name, $code) + @($rest)
    if (($new -join "`n") -ceq ($lines -join "`n")) { return $false }
    return Write-SwPlusLines -Path $Path -Lines $new
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
    соглашения (Security), списки последних файлов и папок, путь Toolbox, панель быстрого доступа (QAT): SolidWorks 2025
    при запуске стирает всю панель, если в ней нет его базовых кнопок Btn0..Btn10, и кнопки SWPlus пропадают,
    а также раздел графики и конвейера производительности (Performance), чтобы пользовательские настройки OpenGL не сбрасывались.
    Лицензии, надстройки при запуске и ESKD_Settings лежат вне раздела версии и не затрагиваются. Возвращает сводку: Existed, Preserved.
    #>
    param([Parameter(Mandatory = $true)][string]$UserRoot, [Parameter(Mandatory = $true)][string]$SwVersion)
    if ($UserRoot -notmatch '^HKCU:\\(.+)$') { throw "Сброс только в HKCU: $UserRoot" }
    $sub = $Matches[1].TrimEnd('\') + "\SolidWorks\$SwVersion"
    $hkcu = [Microsoft.Win32.Registry]::CurrentUser
    $keepTrees = @("Security", "Recent File List", "Recent Folder List", "Recent Macro File List", "User Interface\CommandManager\QAT", "Performance")
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

# ------------------------------------------------------------------ Язык интерфейса
# Правило SolidWorks 2025 (sldutu.dll, LangUtils::GetLangSubdir; разбор 25.09.2026): «Use English language» = 1 —
# английский; иначе основной язык регионального формата пользователя (GetUserDefaultLangID & 0x3FF) = 0x19 при DLL
# в lang\russian — русский; всё прочее — английский. Русскими считаются только ru-RU (0x0419) и ru-MD (0x0819):
# у ru-KZ, ru-UA, ru-BY LCID 0x1000, у kk-KZ — 0x043F. Язык интерфейса Windows и системная локаль на выбор не влияют.
# Макросам SWPlus (VBA, кодовая страница проекта 1251) нужна системная кодовая страница 1251 — её меняет только
# администратор с перезагрузкой, установщик лишь предупреждает.
function Get-EskdLanguagePlan {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("Russian", "English")][string]$Language,
        [int]$UserLcid,
        [bool]$RussianPack,
        [string]$Acp
    )
    $russianFormat = ($UserLcid -band 0x3FF) -eq 0x19
    return [pscustomobject]@{
        UseEnglish    = [int]($Language -eq "English")
        RussianFormat = $russianFormat
        SetCulture    = ($Language -eq "Russian") -and $RussianPack -and -not $russianFormat
        DrewLang      = $(if ($Language -eq "English") { "en" } else { "ru" })
        AcpOk         = ("$Acp".Trim() -eq "1251")
    }
}

# ------------------------------------------------------------------ Drew

function Get-EskdDrewPlan {
    # Что делать с Drew на шаге [7/9]. Отпечаток установщика — SHA-256 файла AUTO.exe инструментария, записанный после
    # подтверждённой установки (ESKD_Install\DrewInstaller). Решение владельца 26.09.2026: сменился установщик в
    # инструментарии — Drew переставляется один раз, даже если сборка лицензии совпала с замером: у установщиков 26.09.2026
    # она та же, и прежнее правило «хэш совпал — Drew верный» оставляло старый Drew навсегда.
    #   Install — Drew нет; Keep — стоит этим же установщиком; Adopt — отпечатка нет (другая учётная запись этого ПК, или
    #   Drew ставили не окном настройки), но Drew с верной сборкой лицензии поставлен ПОЗЖЕ, чем появился этот установщик:
    #   не трогается, отпечаток записывается; Update — стоит Drew с верной сборкой лицензии, но от прежнего (или
    #   неизвестного) установщика: переставляется, отказ в правах — только предупреждение, прежний Drew работает;
    #   Replace — сборка лицензии чужая (пробная производителя, со «слётом» лицензии): переставляется, отказ — ошибка;
    #   Unverified — сборка лицензии есть, но не читается (файл занят): Drew не трогается.
    # DrewSince — когда создана папка Drew в Program Files (удаление её убирает, установка создаёт заново); AutoSince —
    # дата изменения AUTO.exe. Даты файлов внутри папки не годятся: установщик оставляет им даты сборки.
    param(
        [bool]$LicPresent,
        [string]$LicNow,
        [string]$LicHash,
        [string]$AutoHash,
        [string]$RecordedAuto,
        [Nullable[datetime]]$DrewSince,
        [Nullable[datetime]]$AutoSince
    )
    if (-not $LicPresent) { return "Install" }
    if (-not $LicNow) { return "Unverified" }
    $licOk = $LicNow -eq $LicHash
    # Установщика в инструментарии нет или он не читается: сравнить не с чем — остаётся проверка по сборке лицензии.
    if (-not $AutoHash) { return $(if ($licOk) { "Keep" } else { "Install" }) }
    if ($RecordedAuto -eq $AutoHash) { return "Keep" }
    if (-not $RecordedAuto -and $licOk -and $DrewSince -and $AutoSince -and $DrewSince -gt $AutoSince) { return "Adopt" }
    if ($licOk) { return "Update" }
    return "Replace"
}

# ------------------------------------------------------------------ Отучение SolidWorks от сети

function Get-EskdSwBlockExpectedRules {
    # Записи манифеста SwInternetBlock, по которым Set-SwInternetBlock.ps1 -Mode apply создаёт правила на этом ПК:
    # программа есть, и это не SLDWORKS.exe. SLDWORKS.exe пакет пропускает в обе стороны (Test-SldWorksConflict,
    # 20.09.2026): наружу — ради «Поделиться настройками» Drew, внутрь — мешало лицензированию. Правила держит вместе
    # тест T0 test_T0_sw_block_expected_matches_package.
    param([Parameter(Mandatory = $true)][string]$ManifestPath)
    $entries = [System.IO.File]::ReadAllText($ManifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($e in @($entries)) {
        if ((Test-Path -LiteralPath $e.path) -and ($e.path -notmatch 'SLDWORKS\.exe$')) { $e }
    }
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
    $sha = Get-EskdFileSha256 -Path $setup.FullName
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
    if ((Get-EskdFileSha256 -Path $copy) -ne $sha) {
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
