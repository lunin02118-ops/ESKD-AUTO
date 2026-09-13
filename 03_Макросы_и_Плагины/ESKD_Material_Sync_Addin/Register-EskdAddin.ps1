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
    AssemblyName        = "ESKD_Material_Sync_v5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
    AssemblyVersion     = "1.0.0.0"
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
    if (-not (Test-Path -LiteralPath $DllPath)) { throw "Не найдена сборка надстройки: $DllPath" }
    $dll = (Resolve-Path -LiteralPath $DllPath).ProviderPath
    $codeBase = Get-EskdCodeBase -DllPath $dll
    $a = $script:EskdAddin

    foreach ($root in (Get-EskdRoots -UserRoot $UserRoot -MachineRoot $MachineRoot -SystemWide:$SystemWide)) {
        $clsid = "$root\Classes\CLSID\$($a.Guid)"
        Set-EskdRegistryValue -Path $clsid -Name "(Default)" -Value $a.ClassName
        foreach ($inproc in @("$clsid\InprocServer32", "$clsid\InprocServer32\$($a.AssemblyVersion)")) {
            Set-EskdRegistryValue -Path $inproc -Name "Class" -Value $a.ClassName
            Set-EskdRegistryValue -Path $inproc -Name "Assembly" -Value $a.AssemblyName
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
