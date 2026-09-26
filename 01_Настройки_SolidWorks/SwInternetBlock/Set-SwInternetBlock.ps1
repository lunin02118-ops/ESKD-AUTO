<#
.SYNOPSIS
    Отучение SolidWorks от интернета: правила брандмауэра Windows для всех программ SolidWorks и надстроек и заглушки
    доменов SolidWorks в hosts. Работает на любом ПК: программы находятся на месте, а не берутся из готового списка.
.DESCRIPTION
    Решение владельца 26.09.2026: SolidWorks не должен отправлять в интернет ничего — ни телеметрию, ни данные.
    Поэтому закрывается и сам SLDWORKS.exe (с ним — все надстройки внутри него: Drew с его аналитикой и «Поделиться
    настройками», SWTools, ЕСКД), и все вспомогательные программы. Компьютер и локальная сеть остаются доступными:
    сервер лицензий на этом ПК или в сети, общие папки, сетевые принтеры работают как прежде.

    Исключение одно: SWTools.exe открыт его сервер лицензий license.vizbuka.ru (активация, продление, перенос).

    Где искать программы (сведения самой Windows, любая версия, любой диск, любой язык установки):
      * HKLM\SOFTWARE\SolidWorks\SOLIDWORKS <версия>\Setup «SolidWorks Folder» и папка над ней (SOLIDWORKS Corp);
      * записи Менеджера установки (HKLM\SOFTWARE\SolidWorks\IM, в том числе WOW6432Node): папки установки, проверка
        обновлений и фоновая загрузка — у русской установки это «Менеджер установки SOLIDWORKS»;
      * программы из «Программы и компоненты» издателей SolidWorks и Dassault Systemes;
      * папки SOLIDWORKS и Dassault в Common Files, %WINDIR%\SolidWorks, FlexNet Publisher;
      * службы лицензий SolidWorks (папка с sw_d.exe) — сервер лицензий на этом ПК;
      * все надстройки SolidWorks (HKLM и HKCU \SolidWorks\AddIns): папки их DLL — Drew, SWTools, ЕСКД и любые другие;
      * папки CAD Booster в профилях пользователей (прежние копии Drew и его программы обновления).
    В этих папках закрываются все .exe.

    Что считается интернетом: всё, кроме адресов самого ПК и частных сетей — 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16,
    127.0.0.0/8, 169.254.0.0/16, 100.64.0.0/10 и групповых. Диапазоны заданы явно, а не словом «Internet» брандмауэра:
    его смысл зависит от настроек изоляции сети Windows. Прокси-сервер в локальной сети (через него программа вышла бы
    в интернет) закрывается отдельно: его адрес берётся из настроек прокси Windows. Сервер лицензий с публичным адресом
    (port@сервер в SW_D_LICENSE_FILE) остаётся открытым.

    Правила — группа брандмауэра «ESKD-SW-Internet-Block», по два на программу (исходящее и входящее). Прежний пакет
    (правила «Block SW Internet*» по списку с одной машины) при apply удаляется.

    Режимы:
      audit        — проверка без изменений (права администратора не нужны). Код 0 — всё закрыто и действует;
                     3 — чего-то не хватает, нужен apply; 4 — правила на месте, но не действуют (брандмауэр выключен,
                     политика домена запрещает локальные правила); 1 — сбой.
      apply        — создать и поправить правила, убрать устаревшие, записать hosts (администратор). Код — как у audit
                     после применения.
      remove       — снять все правила пакета (и прежнего) и заглушки hosts (администратор).
      list         — показать найденные программы и почему каждая попала в список.
      roots        — только папки (строки «путь<TAB>причина»): установщик передаёт их окну администратора в
                     -ExtraRootFile — надстройки, зарегистрированные у пользователя (HKCU), администратор иначе не видит.
      hosts-apply, hosts-remove — только hosts (администратор).
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Set-SwInternetBlock.ps1 -Mode audit
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Set-SwInternetBlock.ps1 -Mode apply
#>
param(
    [ValidateSet('audit', 'apply', 'remove', 'list', 'roots', 'hosts-apply', 'hosts-remove')][string]$Mode = 'audit',
    [string]$HostsPath = (Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'),
    [string]$ExtraRootFile = ''
)

$ErrorActionPreference = 'Stop'
$script:Group = 'ESKD-SW-Internet-Block'
$script:NamePrefix = 'ESKD SW Block'
$script:LegacyPrefix = 'Block SW Internet'
$script:HostsMarker = 'SW-Internet-Block'
# Домены SolidWorks и Dassault: второй слой защиты для SLDWORKS.exe и его надстроек. Проверенные на ПК обращения
# (кэш DNS 26.09.2026) и адрес Менеджера установки (HKLM\...\SolidWorks\IM «WebServer2025»).
$script:SwDomains = @(
    'activate.solidworks.com', 'activate-se.solidworks.com', 'backupactivate.solidworks.com',
    'backupactivate-se.solidworks.com', 'online.solidworks.com', 'online-se.solidworks.com',
    'backuponline.solidworks.com', 'backuponline-se.solidworks.com', 'backoffice.solidworks.com',
    'api.solidworks.com', 'performance.solidworks.com', 'iam.3ds.com', 'my.solidworks.com',
    'customerportal.solidworks.com', 'webapps.solidworks.com', 'dslauncher.3ds.com', 'companion.3ds.com',
    'im.solidworks.com'
)
# Адреса, которые не интернет: частные сети, сам ПК, служебные и групповые (IPv4). В IPv6 интернет — 2000::/3.
$script:LocalIpv4 = @(
    '0.0.0.0/8', '10.0.0.0/8', '100.64.0.0/10', '127.0.0.0/8', '169.254.0.0/16', '172.16.0.0/12', '192.168.0.0/16',
    '224.0.0.0/3'
)
$script:InternetIpv6 = '2000::-3fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff'
# Единственные исключения — сервер лицензий SWTools для самой программы SWTools.exe (решение владельца 26.09.2026):
# активация на новом ПК, продление аренды лицензии и перенос идут только через него («Активация онлайн»). Сетевой код
# лицензии есть только в SWTools.exe; надстройка SWTools внутри SolidWorks закрыта вместе с SLDWORKS.exe.
$script:ProgramAllowHosts = @{ 'swtools.exe' = @('license.vizbuka.ru') }

# ------------------------------------------------------------------ адреса

function ConvertTo-Ipv4Number([string]$Ip) {
    $b = ([System.Net.IPAddress]::Parse($Ip)).GetAddressBytes()
    return ([int64]$b[0] * 16777216) + ([int64]$b[1] * 65536) + ([int64]$b[2] * 256) + [int64]$b[3]
}

function ConvertFrom-Ipv4Number([int64]$N) {
    return '{0}.{1}.{2}.{3}' -f (($N -shr 24) -band 255), (($N -shr 16) -band 255), (($N -shr 8) -band 255), ($N -band 255)
}

function ConvertTo-Ipv4Interval([string]$Cidr) {
    # «a.b.c.d/n» или один адрес -> @(начало, конец).
    $parts = $Cidr.Split('/')
    $start = ConvertTo-Ipv4Number $parts[0]
    $bits = 32
    if ($parts.Count -gt 1) { $bits = [int]$parts[1] }
    $size = [int64][math]::Pow(2, 32 - $bits)
    $start = $start - ($start % $size)
    return , @($start, ($start + $size - 1))
}

function Get-SwBlockIpv4Ranges {
    # Диапазоны IPv4 для правил: всё, кроме $script:LocalIpv4 и адресов из -Allow (сервер лицензий или сам ПК с
    # публичным адресом), плюс отдельные адреса из -Block (прокси в локальной сети). Вывод — строки «a-b» или «a».
    param([string[]]$Allow = @(), [string[]]$Block = @())
    $intervals = New-Object System.Collections.Generic.List[object]
    foreach ($c in @($script:LocalIpv4) + @($Allow | Where-Object { $_ })) { $intervals.Add((ConvertTo-Ipv4Interval $c)) }
    $sorted = @($intervals | Sort-Object { $_[0] })
    $ranges = New-Object System.Collections.Generic.List[string]
    $next = [int64]0
    foreach ($iv in $sorted) {
        if ($iv[0] -gt $next) { $ranges.Add(('{0}-{1}' -f (ConvertFrom-Ipv4Number $next), (ConvertFrom-Ipv4Number ($iv[0] - 1)))) }
        if ($iv[1] + 1 -gt $next) { $next = $iv[1] + 1 }
    }
    if ($next -le 4294967295) { $ranges.Add(('{0}-{1}' -f (ConvertFrom-Ipv4Number $next), (ConvertFrom-Ipv4Number 4294967295))) }
    foreach ($b in @($Block | Where-Object { $_ })) { if (-not $ranges.Contains($b)) { $ranges.Add($b) } }
    return $ranges.ToArray()
}

function Test-PublicIpv4([string]$Ip) {
    $n = ConvertTo-Ipv4Number $Ip
    foreach ($c in $script:LocalIpv4) { $iv = ConvertTo-Ipv4Interval $c; if ($n -ge $iv[0] -and $n -le $iv[1]) { return $false } }
    return $true
}

function Resolve-SwBlockIpv4([string]$HostName) {
    $h = $HostName.Trim().Trim('[', ']')
    if (-not $h) { return @() }
    $ip = $null
    if ([System.Net.IPAddress]::TryParse($h, [ref]$ip)) {
        if ($ip.AddressFamily -eq 'InterNetwork') { return @($ip.ToString()) } else { return @() }
    }
    try {
        return @([System.Net.Dns]::GetHostAddresses($h) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | ForEach-Object { $_.ToString() })
    } catch { return @() }
}

function Get-SwBlockLicenseServers {
    # Серверы лицензий SolidWorks: «port@host;port@host» из SW_D_LICENSE_FILE (переменная и HKLM/HKCU FLEXlm).
    $values = @(
        [Environment]::GetEnvironmentVariable('SW_D_LICENSE_FILE', 'Machine'),
        [Environment]::GetEnvironmentVariable('SW_D_LICENSE_FILE', 'User'),
        [Environment]::GetEnvironmentVariable('SW_D_LICENSE_FILE', 'Process')
    )
    foreach ($key in 'HKLM:\SOFTWARE\FLEXlm License Manager', 'HKCU:\Software\FLEXlm License Manager') {
        $p = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($p) { $values += $p.SW_D_LICENSE_FILE }
    }
    $hosts = @()
    foreach ($v in @($values | Where-Object { $_ })) {
        foreach ($part in ("$v" -split '[;,]')) {
            if ($part -match '@(.+)$') { $hosts += $Matches[1].Trim() }
        }
    }
    return @($hosts | Sort-Object -Unique)
}

function Get-SwBlockProxyHosts {
    # Прокси из настроек Windows: WinINet (HKCU и политика HKLM), WinHTTP, переменные окружения. Автонастройка (PAC,
    # WPAD) адреса не называет — о ней отдельное предупреждение.
    $result = [pscustomobject]@{ Hosts = @(); AutoConfig = @() }
    $servers = @()
    foreach ($key in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings',
                     'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings') {
        $p = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        if ($p.ProxyEnable -eq 1 -and $p.ProxyServer) { $servers += "$($p.ProxyServer)" }
        if ($p.AutoConfigURL) { $result.AutoConfig += "$($p.AutoConfigURL)" }
    }
    try {
        $winhttp = (& netsh.exe winhttp show proxy 2>$null) -join "`n"
        if ($winhttp -match '(?im)^\s*Proxy Server\(s\)\s*:\s*(\S+)' -or $winhttp -match '(?im)^\s*Прокси-сервер\S*\s*:\s*(\S+)') { $servers += $Matches[1] }
    } catch { }
    foreach ($name in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY') {
        foreach ($scope in 'Machine', 'User') {
            $v = [Environment]::GetEnvironmentVariable($name, $scope)
            if ($v) { $servers += $v }
        }
    }
    $hosts = @()
    foreach ($s in $servers) {
        foreach ($item in ("$s" -split '[;\s]+')) {
            $x = $item -replace '^[a-z]+=', '' -replace '^[a-z]+://', '' -replace '^[^@/]*@', ''
            $x = ($x -split '/')[0]
            if ($x -match '^\[(.+)\](:\d+)?$') { $x = $Matches[1] } elseif ($x -match '^([^:]+):\d+$') { $x = $Matches[1] }
            if ($x -and $x -ne '<local>' -and $x -notmatch '^(direct|none)$') { $hosts += $x }
        }
    }
    $result.Hosts = @($hosts | Sort-Object -Unique)
    $result.AutoConfig = @($result.AutoConfig | Sort-Object -Unique)
    return $result
}

function Get-SwBlockFingerprint([string]$Text) {
    $sha = [System.Security.Cryptography.SHA1]::Create()
    return -join ($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text.ToLowerInvariant())) | Select-Object -First 4 | ForEach-Object { $_.ToString('x2') })
}

function Get-SwBlockAddressPlan {
    # Итоговые адреса для правил и пояснения к ним.
    $notes = New-Object System.Collections.Generic.List[string]
    $allow = @()
    foreach ($h in Get-SwBlockLicenseServers) {
        foreach ($ip in Resolve-SwBlockIpv4 $h) {
            if (Test-PublicIpv4 $ip) { $allow += $ip; $notes.Add("сервер лицензий $h ($ip) — публичный адрес, оставлен открытым") }
        }
    }
    try {
        foreach ($a in Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop) {
            if ((Test-PublicIpv4 $a.IPAddress)) { $allow += $a.IPAddress; $notes.Add("адрес этого ПК $($a.IPAddress) — публичный, оставлен открытым") }
        }
    } catch { }
    $block = @()
    $proxy = Get-SwBlockProxyHosts
    foreach ($h in $proxy.Hosts) {
        foreach ($ip in Resolve-SwBlockIpv4 $h) {
            if (-not (Test-PublicIpv4 $ip) -and -not $ip.StartsWith('127.')) { $block += $ip; $notes.Add("прокси $h ($ip) в локальной сети — закрыт для программ SolidWorks") }
        }
    }
    foreach ($u in $proxy.AutoConfig) {
        $notes.Add("ВНИМАНИЕ: прокси задан сценарием автонастройки ($u) — его адрес неизвестен; если прокси в локальной сети, через него SolidWorks может выйти в интернет")
    }
    return New-SwBlockPlan -Allow $allow -Block $block -Notes $notes.ToArray()
}

function New-SwBlockPlan([string[]]$Allow = @(), [string[]]$Block = @(), [string[]]$Notes = @()) {
    $Allow = @($Allow | Where-Object { $_ } | Sort-Object -Unique)
    $Block = @($Block | Where-Object { $_ } | Sort-Object -Unique)
    $v4 = Get-SwBlockIpv4Ranges -Allow $Allow -Block $Block
    $addresses = @($v4) + @($script:InternetIpv6)
    return [pscustomobject]@{ Addresses = $addresses; Fingerprint = (Get-SwBlockFingerprint ($addresses -join ',')); Allow = $Allow; Block = $Block; Notes = @($Notes) }
}

# ------------------------------------------------------------------ программы

function Get-SwBlockForbiddenRoots {
    # Папки, которые нельзя закрывать целиком (ошибочная запись реестра не должна закрыть пол-Windows).
    $list = @()
    foreach ($v in @(($env:SystemDrive + '\'), $env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432,
                     $env:CommonProgramFiles, ${env:CommonProgramFiles(x86)}, $env:CommonProgramW6432, $env:ProgramData,
                     $env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, (Split-Path -Parent $env:USERPROFILE))) {
        if ($v) { $list += $v.TrimEnd('\').ToLowerInvariant() }
    }
    return @($list | Sort-Object -Unique)
}

function Test-SwBlockSafeRoot([string]$Path, [string[]]$Forbidden = (Get-SwBlockForbiddenRoots)) {
    $p = $Path.TrimEnd('\').ToLowerInvariant()
    if (-not $p -or $p -match '^[a-z]:$' -or $p.StartsWith('\\')) { return $false }
    if ($Forbidden -contains $p) { return $false }
    $win = "$env:SystemRoot".TrimEnd('\').ToLowerInvariant()
    if ($win -and $p.StartsWith($win + '\') -and -not $p.StartsWith($win + '\solidworks')) { return $false }
    return $true
}

function Add-SwBlockRoot($List, [string]$Path, [string]$Reason) {
    if (-not $Path) { return }
    $p = [Environment]::ExpandEnvironmentVariables("$Path".Trim().Trim('"'))
    if (-not $p) { return }
    try { $p = [System.IO.Path]::GetFullPath($p).TrimEnd('\') } catch { return }
    if (-not (Test-Path -LiteralPath $p -PathType Container)) { return }
    if (-not (Test-SwBlockSafeRoot $p)) { return }
    $List.Add([pscustomobject]@{ Path = $p; Reason = $Reason })
}

function Add-SwBlockRootFromFile($List, [string]$File, [string]$Reason) {
    # Папка программы; если выше неё (до двух уровней) есть папка с «SOLIDWORKS» или «Dassault» в имени — она.
    if (-not $File) { return }
    $f = [Environment]::ExpandEnvironmentVariables("$File".Trim())
    if ($f -match '^"([^"]+)"') { $f = $Matches[1] } elseif ($f -match '^(.+?\.(exe|dll))\b') { $f = $Matches[1] }
    $f = $f -replace '^file:///', '' -replace '/', '\'
    $dir = Split-Path -Parent $f
    if (-not $dir) { return }
    $pick = $dir
    $cur = $dir
    for ($i = 0; $i -lt 3 -and $cur; $i++) {
        if ((Split-Path -Leaf $cur) -match '(?i)solidworks|dassault') { $pick = $cur; break }
        $cur = Split-Path -Parent $cur
    }
    Add-SwBlockRoot $List $pick $Reason
}

function Get-SwBlockRoots {
    $roots = New-Object System.Collections.Generic.List[object]
    # SolidWorks всех версий
    foreach ($base in 'HKLM:\SOFTWARE\SolidWorks', 'HKLM:\SOFTWARE\WOW6432Node\SolidWorks') {
        foreach ($k in @(Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue)) {
            $setup = Get-ItemProperty -LiteralPath (Join-Path $k.PSPath 'Setup') -ErrorAction SilentlyContinue
            if ($setup -and $setup.'SolidWorks Folder') {
                $folder = "$($setup.'SolidWorks Folder')".TrimEnd('\')
                Add-SwBlockRoot $roots $folder "$($k.PSChildName)"
                $parent = Split-Path -Parent $folder
                if ($parent -and (Split-Path -Leaf $parent) -match '(?i)solidworks|dassault') { Add-SwBlockRoot $roots $parent "$($k.PSChildName): папка продуктов" }
            }
        }
        # Менеджер установки: папки установки, проверка обновлений, фоновая загрузка
        $im = Get-ItemProperty -LiteralPath (Join-Path $base 'IM') -ErrorAction SilentlyContinue
        if ($im) {
            foreach ($prop in $im.PSObject.Properties) {
                if ($prop.Name -like 'PS*' -or $prop.Value -isnot [string]) { continue }
                if ($prop.Name -like 'InstallDir*') { Add-SwBlockRoot $roots $prop.Value "Менеджер установки: $($prop.Name)" }
                elseif ($prop.Value -match '(?i)\.exe"?$') { Add-SwBlockRootFromFile $roots $prop.Value "Менеджер установки: $($prop.Name)" }
                elseif ($prop.Name -like 'IMInstallDir*') { Add-SwBlockRootFromFile $roots (Join-Path $prop.Value 'x.exe') "Менеджер установки: $($prop.Name)" }
            }
        }
    }
    # «Программы и компоненты»
    foreach ($u in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
        foreach ($k in @(Get-ChildItem -LiteralPath $u -ErrorAction SilentlyContinue)) {
            $p = Get-ItemProperty -LiteralPath $k.PSPath -ErrorAction SilentlyContinue
            if (-not $p -or -not $p.InstallLocation) { continue }
            if ("$($p.Publisher)|$($p.DisplayName)" -match '(?i)solidworks|dassault|edrawings') {
                Add-SwBlockRoot $roots $p.InstallLocation "установлено: $($p.DisplayName)"
            }
        }
    }
    # Common Files, Windows\SolidWorks, FlexNet
    foreach ($cf in @($env:CommonProgramFiles, ${env:CommonProgramFiles(x86)}, $env:CommonProgramW6432) | Where-Object { $_ } | Sort-Object -Unique) {
        foreach ($d in @(Get-ChildItem -LiteralPath $cf -Directory -ErrorAction SilentlyContinue)) {
            if ($d.Name -match '(?i)solidworks|dassault') { Add-SwBlockRoot $roots $d.FullName 'общие файлы SolidWorks' }
        }
        Add-SwBlockRoot $roots (Join-Path $cf 'Macrovision Shared\FlexNet Publisher') 'лицензирование FlexNet'
    }
    Add-SwBlockRoot $roots (Join-Path $env:SystemRoot 'SolidWorks') 'Менеджер установки (Windows\SolidWorks)'
    # Службы лицензий SolidWorks (сервер лицензий на этом ПК)
    try {
        foreach ($s in @(Get-CimInstance Win32_Service -ErrorAction Stop)) {
            if (-not $s.PathName) { continue }
            $exe = "$($s.PathName)"
            if ($exe -match '^"([^"]+)"') { $exe = $Matches[1] } elseif ($exe -match '^(.+?\.exe)') { $exe = $Matches[1] }
            $dir = Split-Path -Parent $exe
            $isSw = "$($s.Name)|$($s.DisplayName)" -match '(?i)solidworks'
            if (-not $isSw -and $dir) { $isSw = Test-Path -LiteralPath (Join-Path $dir 'sw_d.exe') }
            if ($isSw) { Add-SwBlockRootFromFile $roots $exe "служба: $($s.DisplayName)" }
        }
    } catch { }
    # Надстройки SolidWorks: папки их DLL
    foreach ($base in 'HKLM:\SOFTWARE\SolidWorks\AddIns', 'HKCU:\Software\SolidWorks\AddIns') {
        foreach ($k in @(Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue)) {
            $title = "$((Get-ItemProperty -LiteralPath $k.PSPath -ErrorAction SilentlyContinue).Title)"
            $clsid = $k.PSChildName
            $dll = ''
            foreach ($inproc in "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid\InprocServer32", "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\$clsid\InprocServer32",
                                "Registry::HKEY_CURRENT_USER\Software\Classes\CLSID\$clsid\InprocServer32") {
                $keys = @(Get-Item -LiteralPath $inproc -ErrorAction SilentlyContinue) + @(Get-ChildItem -LiteralPath $inproc -ErrorAction SilentlyContinue)
                foreach ($ik in $keys) {
                    if (-not $ik) { continue }
                    $cb = $ik.GetValue('CodeBase')
                    $def = $ik.GetValue('')
                    if ($cb) { $dll = "$cb"; break }
                    if ($def -and "$def" -notmatch '(?i)mscoree\.dll$') { $dll = "$def" }
                }
                if ($dll) { break }
            }
            if ($dll) {
                $f = ("$dll" -replace '^file:///', '') -replace '/', '\'
                Add-SwBlockRoot $roots (Split-Path -Parent $f) "надстройка SolidWorks: $(if ($title) { $title } else { $clsid })"
            }
        }
    }
    # Прежние копии Drew и его программы обновления в профилях пользователей
    $usersDir = Split-Path -Parent $env:USERPROFILE
    foreach ($prof in @(Get-ChildItem -LiteralPath $usersDir -Directory -ErrorAction SilentlyContinue)) {
        foreach ($sub in 'AppData\Local\CAD Booster', 'AppData\Roaming\CAD Booster') {
            Add-SwBlockRoot $roots (Join-Path $prof.FullName $sub) "Drew в профиле $($prof.Name)"
        }
    }
    # Папки, найденные установщиком под учётной записью пользователя (надстройки в HKCU)
    if ($ExtraRootFile -and (Test-Path -LiteralPath $ExtraRootFile)) {
        foreach ($line in [System.IO.File]::ReadAllLines($ExtraRootFile, [System.Text.Encoding]::UTF8)) {
            $parts = $line.Split("`t")
            if ($parts[0].Trim()) { Add-SwBlockRoot $roots $parts[0] $(if ($parts.Count -gt 1) { $parts[1] } else { 'папка пользователя' }) }
        }
    }
    # Без вложенных дублей: папка внутри уже найденной не нужна
    $unique = @($roots | Sort-Object { $_.Path.Length })
    $kept = New-Object System.Collections.Generic.List[object]
    foreach ($r in $unique) {
        $low = $r.Path.ToLowerInvariant()
        $inside = $false
        foreach ($k in $kept) { $kl = $k.Path.ToLowerInvariant(); if ($low -eq $kl -or $low.StartsWith($kl + '\')) { $inside = $true; break } }
        if (-not $inside) { $kept.Add($r) }
    }
    return $kept.ToArray()
}

function Get-SwBlockPrograms {
    param([object[]]$Roots = (Get-SwBlockRoots))
    $seen = @{}
    foreach ($r in $Roots) {
        foreach ($f in @(Get-ChildItem -LiteralPath $r.Path -Recurse -File -Filter '*.exe' -ErrorAction SilentlyContinue)) {
            if ($f.Extension -ne '.exe') { continue }
            $key = $f.FullName.ToLowerInvariant()
            if ($seen.ContainsKey($key)) { continue }
            $seen[$key] = $true
            [pscustomobject]@{ Path = $f.FullName; Reason = $r.Reason }
        }
    }
}

function Get-SwBlockRuleName([string]$Program, [string]$Direction) {
    $hash = Get-SwBlockFingerprint $Program
    $dir = if ($Direction -eq 'Outbound') { 'OUT' } else { 'IN' }
    return '{0} {1} - {2} - {3}' -f $script:NamePrefix, $dir, [System.IO.Path]::GetFileName($Program), $hash
}

function Get-SwBlockDesiredRules {
    # Правила по программам. Программе из $script:ProgramAllowHosts открыт её сервер (адреса по DNS). Сервер не
    # найден (нет сети) — Fingerprint пустой: имеющееся правило программы не трогается, нового — полное закрытие.
    param([object[]]$Programs, $Plan, [scriptblock]$Resolver = { param($h) Resolve-SwBlockIpv4 $h })
    foreach ($p in $Programs) {
        $addresses = $Plan.Addresses; $fp = $Plan.Fingerprint; $reason = $p.Reason
        $leaf = ([System.IO.Path]::GetFileName($p.Path)).ToLowerInvariant()
        if ($script:ProgramAllowHosts.ContainsKey($leaf)) {
            $hosts = $script:ProgramAllowHosts[$leaf]
            $ips = @(foreach ($h in $hosts) { & $Resolver $h } )
            $ips = @($ips | Where-Object { $_ -and (Test-PublicIpv4 $_) } | Sort-Object -Unique)
            if ($ips.Count) {
                $own = New-SwBlockPlan -Allow (@($Plan.Allow) + $ips) -Block $Plan.Block
                $addresses = $own.Addresses; $fp = $own.Fingerprint
                $reason = "$reason; открыт только сервер $($hosts -join ', ') ($($ips -join ', '))"
            } else {
                $fp = ''
                $reason = "$reason; сервер $($hosts -join ', ') не найден — закрыто полностью"
            }
        }
        foreach ($d in 'Outbound', 'Inbound') {
            [pscustomobject]@{ Name = Get-SwBlockRuleName $p.Path $d; Program = $p.Path; Direction = $d; Reason = $reason
                               Addresses = $addresses; Fingerprint = $fp }
        }
    }
}

# ------------------------------------------------------------------ состояние брандмауэра

function Get-SwBlockExistingRules {
    # Правила группы и прежнего пакета с программой (одним проходом по конвейеру — быстро и на 400 правилах). Адреса
    # сверяются по отпечатку в описании правила: брандмауэр может вернуть диапазон в другой записи (подсеть, маска).
    $rules = @(Get-NetFirewallRule -Group $script:Group -ErrorAction SilentlyContinue)
    $legacy = @(Get-NetFirewallRule -DisplayName ($script:LegacyPrefix + '*') -ErrorAction SilentlyContinue)
    $apps = @{}
    if ($rules.Count) {
        foreach ($a in @($rules | Get-NetFirewallApplicationFilter)) { $apps[$a.InstanceID] = "$($a.Program)" }
    }
    $list = foreach ($r in $rules) {
        [pscustomobject]@{
            Id = $r.Name; Name = $r.DisplayName; Direction = "$($r.Direction)"; Action = "$($r.Action)"; Enabled = "$($r.Enabled)"
            Profile = "$($r.Profile)"; Program = $apps[$r.Name]; Description = "$($r.Description)"
        }
    }
    return [pscustomobject]@{ Rules = @($list); Legacy = @($legacy) }
}

function Get-SwBlockDescription([string]$Reason, [string]$Fingerprint) {
    return "ESKD: SolidWorks без интернета. $Reason [addr:$Fingerprint]"
}

function Test-SwBlockRuleOk($Rule, $Desired, [string]$Fingerprint) {
    # Пустой отпечаток — адреса не сверяются (сервер исключения не найден: правило, созданное раньше, остаётся).
    if (-not $Rule) { return $false }
    if ($Rule.Direction -ne $Desired.Direction -or $Rule.Action -ne 'Block' -or $Rule.Enabled -ne 'True' -or $Rule.Profile -ne 'Any') { return $false }
    if ("$($Rule.Program)".ToLowerInvariant() -ne $Desired.Program.ToLowerInvariant()) { return $false }
    if (-not $Fingerprint) { return "$($Rule.Description)" -match '\[addr:[0-9a-f]+\]$' }
    return "$($Rule.Description)".EndsWith("[addr:$Fingerprint]")
}

function Get-SwBlockFirewallState {
    # Действуют ли правила: служба брандмауэра, профили, политика домена «применять локальные правила», сторонний брандмауэр.
    $problems = New-Object System.Collections.Generic.List[string]
    $notes = New-Object System.Collections.Generic.List[string]
    $svc = Get-Service -Name mpssvc -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -ne 'Running') { $problems.Add('служба «Брандмауэр Защитника Windows» (mpssvc) не работает — правила не действуют') }
    try {
        foreach ($p in @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)) {
            if ("$($p.Enabled)" -ne 'True') { $problems.Add("брандмауэр выключен для профиля сети «$($p.Name)» — правила в нём не действуют") }
            if ("$($p.AllowLocalFirewallRules)" -eq 'False') { $problems.Add("политика домена запрещает локальные правила брандмауэра (профиль «$($p.Name)») — правила пакета не действуют; нужны правила в групповой политике") }
        }
    } catch { $notes.Add("профили брандмауэра не прочитаны: $($_.Exception.Message)") }
    try {
        foreach ($fw in @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName FirewallProduct -ErrorAction Stop)) {
            $notes.Add("ВНИМАНИЕ: установлен сторонний брандмауэр «$($fw.displayName)» — если он заменяет брандмауэр Windows, правила пакета не действуют; закройте SolidWorks от интернета и в нём")
        }
    } catch { }
    return [pscustomobject]@{ Problems = $problems.ToArray(); Notes = $notes.ToArray() }
}

# ------------------------------------------------------------------ hosts

function Test-SwBlockHostsLineOurs([string]$Line, [string[]]$Domains) {
    # Строка пакета: метка или активная заглушка 0.0.0.0 / 127.0.0.1, все имена которой — домены пакета.
    if ($Line.Contains($script:HostsMarker)) { return $true }
    $body = ($Line -split '#', 2)[0].Trim()
    if (-not $body) { return $false }
    $tokens = @($body -split '\s+')
    if ($tokens.Count -lt 2 -or $tokens[0] -notin '0.0.0.0', '127.0.0.1') { return $false }
    foreach ($t in $tokens[1..($tokens.Count - 1)]) { if ($Domains -notcontains $t.ToLowerInvariant()) { return $false } }
    return $true
}

function Get-SwBlockHostsText([string]$Text, [string[]]$Domains, [switch]$Remove) {
    # Новый текст hosts: строки пакета убраны, при установке — одна метка и заглушки в конце. $null — менять нечего.
    $lines = @($Text -split "`r?`n")
    $kept = New-Object System.Collections.Generic.List[string]
    foreach ($l in $lines) { if (-not (Test-SwBlockHostsLineOurs $l $Domains)) { $kept.Add($l) } }
    while ($kept.Count -and -not $kept[$kept.Count - 1].Trim()) { $kept.RemoveAt($kept.Count - 1) }
    if (-not $Remove) {
        $kept.Add('')
        $kept.Add("# $($script:HostsMarker) (ESKD: SolidWorks без интернета; снять — Set-SwInternetBlock.ps1 -Mode remove)")
        foreach ($d in $Domains) { $kept.Add("0.0.0.0 $d") }
    }
    $new = ($kept -join "`r`n") + "`r`n"
    $old = (($lines -join "`r`n").TrimEnd("`r", "`n")) + "`r`n"
    if ($new -ceq $old) { return $null }
    return $new
}

function Test-SwBlockHostsComplete([string]$Text, [string[]]$Domains) {
    $have = @{}
    foreach ($l in @($Text -split "`r?`n")) {
        $body = ($l -split '#', 2)[0].Trim()
        $tokens = @($body -split '\s+')
        if ($tokens.Count -ge 2 -and $tokens[0] -in '0.0.0.0', '127.0.0.1') { foreach ($t in $tokens[1..($tokens.Count - 1)]) { $have[$t.ToLowerInvariant()] = $true } }
    }
    return @($Domains | Where-Object { -not $have.ContainsKey($_) })
}

function Read-SwBlockHosts([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return [pscustomobject]@{ Text = ''; Encoding = [System.Text.Encoding]::ASCII } }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        return [pscustomobject]@{ Text = [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3); Encoding = (New-Object System.Text.UTF8Encoding($true)) }
    }
    return [pscustomobject]@{ Text = [System.Text.Encoding]::Default.GetString($bytes); Encoding = [System.Text.Encoding]::Default }
}

function Write-SwBlockHosts([string]$Path, [string]$Text, $Encoding) {
    # hosts постоянно читает служба DNS: запись с общим доступом и повторами.
    $bytes = $Encoding.GetPreamble() + $Encoding.GetBytes($Text)
    for ($try = 1; $try -le 8; $try++) {
        try {
            $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
            try { $fs.Write($bytes, 0, $bytes.Length) } finally { $fs.Close() }
            try { Clear-DnsClientCache -ErrorAction Stop } catch { & ipconfig.exe /flushdns | Out-Null }
            return $true
        } catch { Start-Sleep -Milliseconds 700 }
    }
    return $false
}

function Invoke-SwBlockHosts([switch]$Remove) {
    $h = Read-SwBlockHosts $HostsPath
    $new = Get-SwBlockHostsText $h.Text $script:SwDomains -Remove:$Remove
    if ($null -eq $new) { Write-Output ('hosts: без изменений ({0} доменов SolidWorks)' -f $(if ($Remove) { 0 } else { $script:SwDomains.Count })); return $true }
    if (-not (Write-SwBlockHosts $HostsPath $new $h.Encoding)) { Write-Output 'ОШИБКА: hosts недоступен для записи (8 попыток).'; return $false }
    if ($Remove) { Write-Output 'hosts: заглушки доменов SolidWorks сняты' }
    else { Write-Output ('hosts: заглушено доменов SolidWorks {0}' -f $script:SwDomains.Count) }
    return $true
}

# ------------------------------------------------------------------ проверка и применение

function Get-SwBlockAudit {
    $programs = @(Get-SwBlockPrograms)
    $plan = Get-SwBlockAddressPlan
    $desired = @(Get-SwBlockDesiredRules -Programs $programs -Plan $plan)
    $existing = Get-SwBlockExistingRules
    $byName = @{}
    foreach ($r in $existing.Rules) { $byName[$r.Name] = $r }
    $missing = @($desired | Where-Object { -not (Test-SwBlockRuleOk $byName[$_.Name] $_ $_.Fingerprint) })
    $desiredNames = @{}
    foreach ($d in $desired) { $desiredNames[$d.Name] = $true }
    # Правила группы для программ, которых нет на диске (удалённая версия SolidWorks): лишние.
    $stale = @($existing.Rules | Where-Object { -not $desiredNames.ContainsKey($_.Name) -and -not ($_.Program -and (Test-Path -LiteralPath $_.Program)) })
    # Правила группы для программ, найденных под другой учётной записью: остаются, но адреса должны быть актуальны.
    $foreign = @($existing.Rules | Where-Object { -not $desiredNames.ContainsKey($_.Name) -and $_.Program -and (Test-Path -LiteralPath $_.Program) })
    $foreignWrong = @($foreign | Where-Object { -not (Test-SwBlockRuleOk $_ ([pscustomobject]@{ Direction = $_.Direction; Program = $_.Program }) $plan.Fingerprint) })
    $hostsMissing = @(Test-SwBlockHostsComplete (Read-SwBlockHosts $HostsPath).Text $script:SwDomains)
    $fw = Get-SwBlockFirewallState
    $sld = @($programs | Where-Object { $_.Path -match '(?i)\\sldworks\.exe$' })
    return [pscustomobject]@{
        Programs = $programs; Plan = $plan; Desired = $desired; Missing = $missing; Stale = $stale; ForeignWrong = $foreignWrong
        Legacy = @($existing.Legacy); HostsMissing = $hostsMissing; Firewall = $fw; SldWorks = $sld
    }
}

function Write-SwBlockAudit($A) {
    Write-Output ('Найдено программ SolidWorks и надстроек: {0}; SLDWORKS.exe: {1}' -f $A.Programs.Count, $(if ($A.SldWorks.Count) { ($A.SldWorks.Path -join '; ') } else { 'не найден' }))
    foreach ($n in $A.Plan.Notes) { Write-Output "  $n" }
    Write-Output ('Правил нужно: {0}; не хватает или неверных: {1}; лишних (программы нет): {2}; прежнего пакета: {3}' -f $A.Desired.Count, $A.Missing.Count, $A.Stale.Count, $A.Legacy.Count)
    foreach ($m in @($A.Missing | Select-Object -First 15)) { Write-Output "  нет: $($m.Direction) $($m.Program)" }
    if ($A.Missing.Count -gt 15) { Write-Output "  … и ещё $($A.Missing.Count - 15)" }
    if ($A.HostsMissing.Count) { Write-Output ('hosts: не заглушено {0}: {1}' -f $A.HostsMissing.Count, ($A.HostsMissing -join ', ')) }
    else { Write-Output ('hosts: заглушено доменов SolidWorks {0}' -f $script:SwDomains.Count) }
    foreach ($p in $A.Firewall.Problems) { Write-Output "ОШИБКА: $p" }
    foreach ($n in $A.Firewall.Notes) { Write-Output "  $n" }
}

function Get-SwBlockAuditCode($A) {
    if (-not $A.Programs.Count) { return 3 }
    if ($A.Missing.Count -or $A.Stale.Count -or $A.ForeignWrong.Count -or $A.Legacy.Count -or $A.HostsMissing.Count) { return 3 }
    if ($A.Firewall.Problems.Count) { return 4 }
    return 0
}

function Write-SwBlockVerdict([int]$Code, $A) {
    switch ($Code) {
        0 { Write-Output ('ИТОГ: SolidWorks отучен от интернета: {0} программ, {1} правил, hosts — {2} доменов.' -f $A.Programs.Count, $A.Desired.Count, $script:SwDomains.Count) }
        3 { if ($A.Programs.Count) { Write-Output 'ИТОГ: отучение неполное — нужен запуск с -Mode apply (администратор).' } else { Write-Output 'ИТОГ: программы SolidWorks на этом ПК не найдены — закрывать нечего.' } }
        4 { Write-Output 'ИТОГ: правила на месте, но брандмауэр их не применяет — см. ОШИБКА выше.' }
    }
}

function Test-SwBlockAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-SwBlockApply {
    $a = Get-SwBlockAudit
    $existing = Get-SwBlockExistingRules
    $byName = @{}
    foreach ($r in $existing.Rules) { $byName[$r.Name] = $r }
    $created = 0; $fixed = 0; $removed = 0; $errors = 0
    foreach ($d in $a.Missing) {
        try {
            if ($byName.ContainsKey($d.Name)) { Remove-NetFirewallRule -Name $byName[$d.Name].Id -ErrorAction Stop; $fixed++ } else { $created++ }
            $fp = $d.Fingerprint
            if (-not $fp) { $fp = $a.Plan.Fingerprint }
            New-NetFirewallRule -DisplayName $d.Name -Group $script:Group -Description (Get-SwBlockDescription $d.Reason $fp) `
                -Direction $d.Direction -Action Block -Enabled True -Profile Any -Program $d.Program `
                -RemoteAddress $d.Addresses -ErrorAction Stop | Out-Null
        } catch { $errors++; Write-Output "ОШИБКА: правило для $($d.Program) ($($d.Direction)): $($_.Exception.Message)" }
    }
    foreach ($r in $a.ForeignWrong) {
        try {
            $reason = ("$($r.Description)" -replace '^ESKD: SolidWorks без интернета\. ', '' -replace ' \[addr:[0-9a-f]+\]$', '')
            Set-NetFirewallRule -Name $r.Id -RemoteAddress $a.Plan.Addresses -Action Block -Enabled True -Profile Any `
                -Description (Get-SwBlockDescription $reason $a.Plan.Fingerprint) -ErrorAction Stop
            $fixed++
        }
        catch { $errors++; Write-Output "ОШИБКА: правило $($r.Name): $($_.Exception.Message)" }
    }
    foreach ($r in $a.Stale) {
        try { Remove-NetFirewallRule -Name $r.Id -ErrorAction Stop; $removed++ } catch { $errors++; Write-Output "ОШИБКА: $($r.Name): $($_.Exception.Message)" }
    }
    # Прежний пакет (список с одной машины, без SLDWORKS.exe) — после того, как новые правила созданы.
    $legacyRemoved = 0
    if (-not $errors) {
        foreach ($r in $a.Legacy) { try { $r | Remove-NetFirewallRule -ErrorAction Stop; $legacyRemoved++ } catch { $errors++ } }
    }
    Write-Output ('apply: создано {0}, исправлено {1}, удалено лишних {2}, удалено правил прежнего пакета {3}, ошибок {4}' -f $created, $fixed, $removed, $legacyRemoved, $errors)
    if (-not (Invoke-SwBlockHosts)) { $errors++ }
    return $errors
}

function Invoke-SwBlockRemove {
    $n = 0
    foreach ($r in @(Get-NetFirewallRule -Group $script:Group -ErrorAction SilentlyContinue) + @(Get-NetFirewallRule -DisplayName ($script:LegacyPrefix + '*') -ErrorAction SilentlyContinue)) {
        if ($r) { $r | Remove-NetFirewallRule; $n++ }
    }
    Write-Output "remove: удалено правил $n"
    [void](Invoke-SwBlockHosts -Remove)
}

# ------------------------------------------------------------------ запуск

# Точечный вызов (. .\Set-SwInternetBlock.ps1 -Mode list из автотеста) не нужен: тест берёт функции разбором файла.
if ($Mode -notin 'audit', 'list', 'roots' -and -not (Test-SwBlockAdmin)) {
    Write-Output "ОШИБКА: режим $Mode требует прав администратора."
    exit 1
}
try {
    switch ($Mode) {
        'list' {
            foreach ($r in Get-SwBlockRoots) { Write-Output "папка: $($r.Path)  [$($r.Reason)]" }
            foreach ($p in Get-SwBlockPrograms) { Write-Output "  $($p.Path)" }
            exit 0
        }
        'roots' {
            foreach ($r in Get-SwBlockRoots) { Write-Output ("{0}`t{1}" -f $r.Path, $r.Reason) }
            exit 0
        }
        'audit' {
            $a = Get-SwBlockAudit
            Write-SwBlockAudit $a
            $code = Get-SwBlockAuditCode $a
            Write-SwBlockVerdict $code $a
            exit $code
        }
        'apply' {
            $errors = Invoke-SwBlockApply
            $a = Get-SwBlockAudit
            Write-SwBlockAudit $a
            $code = Get-SwBlockAuditCode $a
            Write-SwBlockVerdict $code $a
            if ($errors -and $code -eq 0) { $code = 3 }
            exit $code
        }
        'remove' { Invoke-SwBlockRemove; exit 0 }
        'hosts-apply' { if (Invoke-SwBlockHosts) { exit 0 } else { exit 1 } }
        'hosts-remove' { if (Invoke-SwBlockHosts -Remove) { exit 0 } else { exit 1 } }
    }
} catch {
    Write-Output "ОШИБКА: $($_.Exception.Message)"
    exit 1
}
