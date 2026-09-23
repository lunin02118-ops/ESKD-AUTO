<#
.SYNOPSIS
    Единый модуль регистрации надстройки ЕСКД в SolidWorks (план, WP-4.2).

.DESCRIPTION
    Подключается dot-source и используется установщиком Setup_Workstation_SolidWorks.ps1, register_eskd.ps1,
    build_and_register.ps1, unregister.ps1 и конфигуратором рабочего места:

        . .\Register-EskdAddin.ps1
        Register-EskdAddin -DllPath .\ESKD_Material_Sync_v5.dll [-SystemWide]
        Get-EskdAddinRegistration
        Unregister-EskdAddin [-SystemWide]

    Регистрация пишет COM-класс, ProgId, запись SolidWorks AddIns и автозагрузку для текущего пользователя (HKCU).
    С -SystemWide и правами администратора то же пишется в HKLM — для SolidWorks, запущенного от администратора,
    который активирует COM через HKLM. RegAsm не используется: он записывает CodeBase в %-кодированной форме,
    а CLR не активирует такую сборку при кириллице в пути (DEP-11). CodeBase всегда пишется в сырой форме.

    Корни реестра задаются параметрами -UserRoot и -MachineRoot: автотест регистрирует надстройку
    во временном разделе и проверяет структуру, не касаясь настоящей регистрации.
#>

$script:EskdAddin = @{
    Guid                = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
    ProgId              = "ESKD.MaterialSync.SwAddin_v5"
    ClassName           = "ESKD.MaterialSync.SwAddin"
    # Полное имя и версия сборки берутся из самой DLL (Get-EskdAssemblyIdentity), здесь — только простое имя.
    AssemblySimpleName  = "ESKD_Material_Sync_v5"
    RuntimeVersion      = "v4.0.30319"
    ManagedCategory     = "{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}"
    Title               = "ЕСКД: Синхронизация материалов и реквизитов"
    Description         = "Реквизиты основной надписи по имени файла и словарю SWPlus, дробь материала и масса, «Деталь БЧ»"
    ObsoleteGuids       = @(
        "{B64E6875-B101-4D5C-B245-FF8D50772E21}",
        "{B64E6875-B101-4D5C-B245-FF8D50772E23}",
        "{B64E6875-B101-4D5C-B245-FF8D50772E24}"
    )
}

function Test-EskdAdministrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-EskdSessionUserSid {
    # Хозяин рабочего стола — пользователь, вошедший в этот сеанс Windows (WTSQuerySessionInformation: прав не требует и
    # не зависит от того, чей процесс спрашивает). Раньше — владелец explorer.exe: процесс чужой учётки без повышения прав
    # («Запуск от имени другого пользователя») владельца не прочтёт, и проверка молча пропускала (ревью 23.09.2026).
    # Пусто, если определить нельзя (служебный сеанс, сбой): тогда проверка учётки регистрации не мешает.
    try {
        if (-not ('Eskd.SessionUser' -as [type])) {
            Add-Type -Namespace Eskd -Name SessionUser -MemberDefinition @"
[DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
[DllImport("wtsapi32.dll")]
static extern void WTSFreeMemory(IntPtr memory);
public static string Query(int infoClass) {
    IntPtr buffer; int bytes;
    if (!WTSQuerySessionInformation(IntPtr.Zero, -1, infoClass, out buffer, out bytes)) return "";
    try { return Marshal.PtrToStringUni(buffer) ?? ""; } finally { WTSFreeMemory(buffer); }
}
"@
        }
        $user = [Eskd.SessionUser]::Query(5)     # WTSUserName
        $domain = [Eskd.SessionUser]::Query(7)   # WTSDomainName
        if (-not $user) { return "" }
        $account = if ($domain) { "$domain\$user" } else { $user }
        return (New-Object Security.Principal.NTAccount $account).Translate([Security.Principal.SecurityIdentifier]).Value
    } catch { }
    return ""
}

function Get-EskdAccountName {
    param([string]$Sid)
    try { return (New-Object Security.Principal.SecurityIdentifier $Sid).Translate([Security.Principal.NTAccount]).Value } catch { return $Sid }
}

function Get-EskdForeignAccountMessage {
    # Сверка SW API 23.09.2026 (№2): автозагрузка AddInsStartup пишется в HKCU того, кто запустил. Под чужой учётной
    # записью («Запуск от имени администратора» с паролем ИТ) конструктор остался бы без неё, а скрипт писал «[OK]».
    # Сравниваются SID: повышение прав под своей учёткой SID не меняет.
    param([string]$CurrentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
          [string]$SessionSid = (Get-EskdSessionUserSid),
          [ValidateSet("Register", "Unregister")][string]$Action = "Register",
          [string]$ScriptPath = "unregister.ps1")
    if (-not $SessionSid -or $SessionSid -eq $CurrentSid) { return "" }
    $me = Get-EskdAccountName $CurrentSid
    $owner = Get-EskdAccountName $SessionSid
    if ($Action -eq "Unregister") {
        # Команда целиком: двойной щелчок по .ps1 открывает Блокнот, а по умолчанию сценарии в PowerShell запрещены.
        # С правами администратора под своей же учётной записью можно — тогда снимется и регистрация для всех.
        return ("Снятие регистрации запущено от имени {0}, а в Windows сейчас вошёл {1}: снялась бы регистрация {0}, а не " +
                "того, кто работает в SolidWorks. Войдите в Windows под учётной записью конструктора и выполните в окне " +
                "PowerShell: powershell -NoProfile -ExecutionPolicy Bypass -File ""{2}""") -f $me, $owner, $ScriptPath
    }
    return ("Регистрация запущена от имени {0}, а в Windows сейчас вошёл {1}. Автозагрузка надстройки пишется в профиль " +
            "того, кто запустил, а SolidWorks читает профиль того, кто вошёл. Войдите в Windows под учётной записью " +
            "конструктора и запустите «Регистрация_ЕСКД_на_этом_компьютере.cmd» двойным щелчком.") -f $me, $owner
}

function Assert-EskdNativeProcess {
    # Сверка SW API 23.09.2026 (№4): 32-битный PowerShell на 64-битной Windows пишет HKCU\Software\Classes\CLSID и
    # HKLM\Software в Wow6432Node — 64-битный SolidWorks такую регистрацию не видит, а проверка читает то же 32-битное
    # представление реестра и сообщает, что всё в порядке.
    param([ValidateSet("Register", "Unregister")][string]$Action = "Register")
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
        if ($Action -eq "Unregister") {
            throw ("Снятие регистрации запущено в 32-битном PowerShell: оно сняло бы не ту регистрацию, которую видит 64-битный " +
                   "SolidWorks. Выполните в обычном (64-битном) окне PowerShell: powershell -NoProfile -ExecutionPolicy Bypass " +
                   "-File unregister.ps1")
        }
        throw "Регистрация запущена в 32-битном PowerShell: 64-битный SolidWorks её не увидит. Запустите файл двойным щелчком из Проводника."
    }
}

function Get-EskdAssemblyIdentity {
    # Сверка SW API 23.09.2026 (№6): полное имя и версия сборки для COM-активации — из самой DLL (манифест читается без
    # загрузки сборки), а не из константы скрипта: версия, поднятая в AssemblyInfo.cs, не разойдётся с регистрацией.
    param([Parameter(Mandatory = $true)][string]$DllPath)
    $identity = [Reflection.AssemblyName]::GetAssemblyName($DllPath)
    if ($identity.Name -ne $script:EskdAddin.AssemblySimpleName) {
        throw "Это не сборка надстройки ЕСКД: $DllPath ($($identity.FullName))"
    }
    return $identity
}

function Get-EskdCodeBase {
    param([Parameter(Mandatory = $true)][string]$DllPath)
    # Сырая форма file:///D:/… — именно её активирует CLR при кириллице в пути (DEP-11). Не экранировать.
    return "file:///" + $DllPath.Replace('\', '/')
}

function Set-EskdRegistryValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Value,
        [ValidateSet("String", "DWord")][string]$Type = "String"
    )
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    if ($Type -eq "DWord") {
        Set-ItemProperty -LiteralPath $Path -Name $Name -Value ([int]$Value) -Type DWord
    } else {
        Set-ItemProperty -LiteralPath $Path -Name $Name -Value ([string]$Value)
    }
}

function Remove-EskdRegistryKey {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}

function Get-EskdRoots {
    param([string]$UserRoot, [string]$MachineRoot, [switch]$SystemWide)
    $roots = @($UserRoot)
    if ($SystemWide) {
        if ($MachineRoot -notlike "HKLM:*" -or (Test-EskdAdministrator)) {
            $roots += $MachineRoot
        } else {
            Write-Warning "Регистрация в HKLM пропущена: нужны права администратора. Надстройка зарегистрирована для текущего пользователя."
        }
    }
    return $roots
}

function Register-EskdAddin {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$DllPath,
        [switch]$SystemWide,
        [string]$UserRoot = "HKCU:\Software",
        [string]$MachineRoot = "HKLM:\Software"
    )
    Assert-EskdNativeProcess
    if (-not (Test-Path -LiteralPath $DllPath)) { throw "Не найдена сборка надстройки: $DllPath" }
    $dll = (Resolve-Path -LiteralPath $DllPath).ProviderPath
    $codeBase = Get-EskdCodeBase -DllPath $dll
    $identity = Get-EskdAssemblyIdentity -DllPath $dll
    $a = $script:EskdAddin

    foreach ($root in (Get-EskdRoots -UserRoot $UserRoot -MachineRoot $MachineRoot -SystemWide:$SystemWide)) {
        $clsid = "$root\Classes\CLSID\$($a.Guid)"
        # Подраздел InprocServer32\<версия> прежней сборки остался бы со старым именем — класс пишется заново (№6).
        Remove-EskdRegistryKey -Path $clsid
        Set-EskdRegistryValue -Path $clsid -Name "(Default)" -Value $a.ClassName
        foreach ($inproc in @("$clsid\InprocServer32", "$clsid\InprocServer32\$($identity.Version)")) {
            Set-EskdRegistryValue -Path $inproc -Name "Class" -Value $a.ClassName
            Set-EskdRegistryValue -Path $inproc -Name "Assembly" -Value $identity.FullName
            Set-EskdRegistryValue -Path $inproc -Name "RuntimeVersion" -Value $a.RuntimeVersion
            Set-EskdRegistryValue -Path $inproc -Name "CodeBase" -Value $codeBase
        }
        Set-EskdRegistryValue -Path "$clsid\InprocServer32" -Name "(Default)" -Value "mscoree.dll"
        Set-EskdRegistryValue -Path "$clsid\InprocServer32" -Name "ThreadingModel" -Value "Both"
        Set-EskdRegistryValue -Path "$clsid\ProgId" -Name "(Default)" -Value $a.ProgId
        if (-not (Test-Path -LiteralPath "$clsid\Implemented Categories\$($a.ManagedCategory)")) {
            New-Item -Path "$clsid\Implemented Categories\$($a.ManagedCategory)" -Force | Out-Null
        }
        Set-EskdRegistryValue -Path "$root\Classes\$($a.ProgId)" -Name "(Default)" -Value $a.ClassName
        Set-EskdRegistryValue -Path "$root\Classes\$($a.ProgId)\CLSID" -Name "(Default)" -Value $a.Guid

        $addIn = "$root\SolidWorks\AddIns\$($a.Guid)"
        Set-EskdRegistryValue -Path $addIn -Name "(Default)" -Value 1 -Type DWord
        Set-EskdRegistryValue -Path $addIn -Name "Title" -Value $a.Title
        Set-EskdRegistryValue -Path $addIn -Name "Description" -Value $a.Description

        foreach ($old in $a.ObsoleteGuids) {
            Remove-EskdRegistryKey -Path "$root\SolidWorks\AddIns\$old"
            Remove-EskdRegistryKey -Path "$root\SolidWorks\AddInsStartup\$old"
            Remove-EskdRegistryKey -Path "$root\Classes\CLSID\$old"
        }
    }
    # Автозагрузку SolidWorks читает только из профиля пользователя.
    Set-EskdRegistryValue -Path "$UserRoot\SolidWorks\AddInsStartup\$($a.Guid)" -Name "(Default)" -Value 1 -Type DWord
    return Get-EskdAddinRegistration -UserRoot $UserRoot -MachineRoot $MachineRoot
}

function Unregister-EskdAddin {
    [CmdletBinding()]
    param(
        [switch]$SystemWide,
        [string]$UserRoot = "HKCU:\Software",
        [string]$MachineRoot = "HKLM:\Software"
    )
    Assert-EskdNativeProcess -Action Unregister
    $a = $script:EskdAddin
    foreach ($root in (Get-EskdRoots -UserRoot $UserRoot -MachineRoot $MachineRoot -SystemWide:$SystemWide)) {
        foreach ($guid in @($a.Guid) + $a.ObsoleteGuids) {
            Remove-EskdRegistryKey -Path "$root\Classes\CLSID\$guid"
            Remove-EskdRegistryKey -Path "$root\SolidWorks\AddIns\$guid"
            Remove-EskdRegistryKey -Path "$root\SolidWorks\AddInsStartup\$guid"
        }
        Remove-EskdRegistryKey -Path "$root\Classes\$($a.ProgId)"
    }
}

function Get-EskdAddinRegistration {
    [CmdletBinding()]
    param(
        [string]$UserRoot = "HKCU:\Software",
        [string]$MachineRoot = "HKLM:\Software"
    )
    $a = $script:EskdAddin
    $read = {
        param($path, $name)
        if (-not (Test-Path -LiteralPath $path)) { return $null }
        $item = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
        if ($null -eq $item) { return $null }
        $property = $item.PSObject.Properties[$name]
        if ($null -eq $property) { return $null }
        return $property.Value
    }
    $state = [ordered]@{ Guid = $a.Guid; ProgId = $a.ProgId }
    foreach ($scope in @(@{ Name = "User"; Root = $UserRoot }, @{ Name = "Machine"; Root = $MachineRoot })) {
        $codeBase = & $read "$($scope.Root)\Classes\CLSID\$($a.Guid)\InprocServer32" "CodeBase"
        $dllExists = $false
        if ($codeBase -and $codeBase.StartsWith("file:///")) {
            $dllExists = Test-Path -LiteralPath ($codeBase.Substring(8).Replace('/', '\'))
        }
        $state["$($scope.Name)CodeBase"] = $codeBase
        $state["$($scope.Name)DllExists"] = $dllExists
        $state["$($scope.Name)AddIn"] = (& $read "$($scope.Root)\SolidWorks\AddIns\$($a.Guid)" "(default)") -eq 1
    }
    $state["UserStartup"] = (& $read "$UserRoot\SolidWorks\AddInsStartup\$($a.Guid)" "(default)") -eq 1
    $state["ProgIdClsid"] = & $read "$UserRoot\Classes\$($a.ProgId)\CLSID" "(default)"
    return [pscustomobject]$state
}
