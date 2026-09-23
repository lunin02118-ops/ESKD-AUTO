<#
.SYNOPSIS
    Проверка модуля Register-EskdAddin.ps1 во временном разделе реестра (T0, WP-4.2).
.DESCRIPTION
    Регистрирует надстройку в HKCU:\Software\ESKD_RegistrationTest_<id>\{User,Machine}, проверяет каждое значение,
    снимает регистрацию и удаляет временный раздел. Настоящая регистрация надстройки не затрагивается.
    Вывод — JSON с результатом; код выхода 0 при успехе.
#>
param([Parameter(Mandatory = $true)][string]$ModulePath, [Parameter(Mandatory = $true)][string]$DllPath)

$ErrorActionPreference = "Stop"
. $ModulePath
$sandbox = "HKCU:\Software\ESKD_RegistrationTest_" + [guid]::NewGuid().ToString("N")
$user = "$sandbox\User"
$machine = "$sandbox\Machine"
$problems = New-Object System.Collections.Generic.List[string]
function Expect($label, $actual, $expected) {
    if ($actual -ne $expected) { $problems.Add("${label}: ожидалось «$expected», получено «$actual»") }
}
$fake = Join-Path ([IO.Path]::GetTempPath()) ("eskd_version_" + [guid]::NewGuid().ToString("N"))
try {
    $guid = "{B64E6875-B101-4D5C-B245-FF8D50772E25}"
    # следы прежних версий должны удаляться
    New-Item -Path "$user\SolidWorks\AddIns\{B64E6875-B101-4D5C-B245-FF8D50772E24}" -Force | Out-Null
    # подраздел версии прежней сборки — тоже (сверка SW API 23.09.2026, №6)
    New-Item -Path "$user\Classes\CLSID\$guid\InprocServer32\0.9.0.0" -Force | Out-Null
    $state = Register-EskdAddin -DllPath $DllPath -SystemWide -UserRoot $user -MachineRoot $machine
    $dll = (Resolve-Path -LiteralPath $DllPath).ProviderPath
    $codeBase = "file:///" + $dll.Replace('\', '/')
    # Имя и версия сборки — из самой DLL, а не из константы скрипта (№6).
    $identity = [Reflection.AssemblyName]::GetAssemblyName($dll)
    $version = $identity.Version.ToString()
    foreach ($root in @($user, $machine)) {
        $inproc = Get-ItemProperty -LiteralPath "$root\Classes\CLSID\$guid\InprocServer32"
        Expect "$root InprocServer32 (default)" $inproc."(default)" "mscoree.dll"
        Expect "$root ThreadingModel" $inproc.ThreadingModel "Both"
        Expect "$root Class" $inproc.Class "ESKD.MaterialSync.SwAddin"
        Expect "$root CodeBase" $inproc.CodeBase $codeBase
        Expect "$root CodeBase $version" (Get-ItemProperty -LiteralPath "$root\Classes\CLSID\$guid\InprocServer32\$version").CodeBase $codeBase
        Expect "$root Assembly" $inproc.Assembly $identity.FullName
        Expect "$root Assembly $version" (Get-ItemProperty -LiteralPath "$root\Classes\CLSID\$guid\InprocServer32\$version").Assembly $identity.FullName
        Expect "$root ProgId" (Get-ItemProperty -LiteralPath "$root\Classes\CLSID\$guid\ProgId")."(default)" "ESKD.MaterialSync.SwAddin_v5"
        Expect "$root категория .NET" (Test-Path -LiteralPath "$root\Classes\CLSID\$guid\Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}") $true
        $addIn = Get-ItemProperty -LiteralPath "$root\SolidWorks\AddIns\$guid"
        Expect "$root AddIns (default)" $addIn."(default)" 1
        Expect "$root AddIns Title" $addIn.Title "ЕСКД: Синхронизация материалов и реквизитов"
    }
    Expect "автозагрузка" (Get-ItemProperty -LiteralPath "$user\SolidWorks\AddInsStartup\$guid")."(default)" 1
    Expect "прежний GUID удалён" (Test-Path -LiteralPath "$user\SolidWorks\AddIns\{B64E6875-B101-4D5C-B245-FF8D50772E24}") $false
    Expect "подраздел прежней версии удалён" (Test-Path -LiteralPath "$user\Classes\CLSID\$guid\InprocServer32\0.9.0.0") $false
    Expect "CodeBase без %-кодирования" ($codeBase -match '%[0-9A-F]{2}') $false
    Expect "состояние: CodeBase" $state.UserCodeBase $codeBase
    Expect "состояние: DLL существует" $state.UserDllExists $true
    Expect "состояние: AddIn" $state.UserAddIn $true
    Expect "состояние: автозагрузка" $state.UserStartup $true
    Expect "состояние: ProgId → CLSID" $state.ProgIdClsid $guid

    # Сборка с другой версией: регистрация следует за ней, подраздел прежней версии не остаётся (№6).
    New-Item -ItemType Directory -Path $fake -Force | Out-Null
    $fakeDll = Join-Path $fake "ESKD_Material_Sync_v5.dll"
    Add-Type -TypeDefinition '[assembly: System.Reflection.AssemblyVersion("9.9.9.9")] namespace EskdFake { public class C { } }' `
        -OutputAssembly $fakeDll -OutputType Library
    Register-EskdAddin -DllPath $fakeDll -UserRoot $user -MachineRoot $machine | Out-Null
    $fakeInproc = "$user\Classes\CLSID\$guid\InprocServer32"
    Expect "другая версия: Assembly" (Get-ItemProperty -LiteralPath $fakeInproc).Assembly "ESKD_Material_Sync_v5, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null"
    Expect "другая версия: подраздел 9.9.9.9" (Test-Path -LiteralPath "$fakeInproc\9.9.9.9") $true
    if ($version -ne "9.9.9.9") { Expect "другая версия: подраздел $version удалён" (Test-Path -LiteralPath "$fakeInproc\$version") $false }
    $foreign = Join-Path $fake "Other.dll"
    Add-Type -TypeDefinition 'namespace EskdOther { public class C { } }' -OutputAssembly $foreign -OutputType Library
    $refused = ""
    try { Register-EskdAddin -DllPath $foreign -UserRoot $user -MachineRoot $machine | Out-Null } catch { $refused = $_.Exception.Message }
    Expect "чужая сборка не регистрируется" ($refused -ne "") $true

    Unregister-EskdAddin -SystemWide -UserRoot $user -MachineRoot $machine
    $after = Get-EskdAddinRegistration -UserRoot $user -MachineRoot $machine
    Expect "после снятия: CodeBase" $after.UserCodeBase $null
    Expect "после снятия: AddIn" $after.UserAddIn $false
    Expect "после снятия: HKLM-аналог" $after.MachineAddIn $false
    Expect "после снятия: автозагрузка" $after.UserStartup $false
} catch {
    $problems.Add("исключение: $($_.Exception.Message)")
} finally {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    Remove-Item -LiteralPath $fake -Recurse -Force -ErrorAction SilentlyContinue
}
[pscustomobject]@{ ok = ($problems.Count -eq 0); problems = @($problems); sandboxRemoved = -not (Test-Path -LiteralPath $sandbox) } | ConvertTo-Json -Compress
if ($problems.Count) { exit 1 }
