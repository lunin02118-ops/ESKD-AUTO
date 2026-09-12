<#
.SYNOPSIS
    Полное удаление и деактивация надстройки ЕСКД из SolidWorks.
#>
[CmdletBinding()]
param()


# Автодетект установленной версии SolidWorks (унифицировано с Setup_Workstation_SolidWorks.ps1):
# самая высокая версия в HKCU\Software\SolidWorks\SOLIDWORKS \d{4}, дефолт — SOLIDWORKS 2025.
function Get-SolidWorksRegistryVersion {
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
$SwVersion = Get-SolidWorksRegistryVersion
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

$guids = @(
    "{B64E6875-B101-4D5C-B245-FF8D50772E25}", # v5 (актуальная)
    "{B64E6875-B101-4D5C-B245-FF8D50772E24}",
    "{B64E6875-B101-4D5C-B245-FF8D50772E23}",
    "{B64E6875-B101-4D5C-B245-FF8D50772E21}"
)

# 1. HKLM RegAsm unregister if admin
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$candidateDlls = @(
    (Join-Path $ScriptDir "ESKD_Material_Sync_v5.dll"),
    (Join-Path $ScriptDir "ESKD_Material_Sync_Addin.dll")
)
if ($isAdmin -and (Test-Path $regasm)) {
    foreach ($d in $candidateDlls) {
        if (Test-Path $d) {
            try { & $regasm /u $d 2>$null } catch { }
        }
    }
}

# 2. Cleanup HKCU and HKLM registries
foreach ($g in $guids) {
    Remove-Item "HKCU:\Software\Classes\CLSID\$g" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\SolidWorks\AddIns\$g" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\SolidWorks\AddinsStartup\$g" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKLM:\SOFTWARE\SolidWorks\AddIns\$g" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKLM:\SOFTWARE\SolidWorks\AddinsStartup\$g" -Recurse -Force -ErrorAction SilentlyContinue
}

Remove-Item "HKCU:\Software\Classes\ESKD.MaterialSync.SwAddin_v5" -Recurse -Force -ErrorAction SilentlyContinue

# 3. Remove CommandManager tabs
$contexts = @("PartContext", "AssyContext", "DrwContext")
foreach ($ctx in $contexts) {
    $ctxPath = "HKCU:\Software\SolidWorks\$SwVersion\User Interface\CommandManager\$ctx"
    if (Test-Path $ctxPath) {
        Get-ChildItem -Path $ctxPath -ErrorAction SilentlyContinue | ForEach-Object {
            $tPath = $_.PSPath
            $ref = (Get-ItemProperty -Path $tPath -Name "RefName" -ErrorAction SilentlyContinue).RefName
            $mod = (Get-ItemProperty -Path $tPath -Name "ModuleName" -ErrorAction SilentlyContinue).ModuleName
            if (($ref -and ($ref -match "ЕСКД")) -or ($mod -and ($guids -contains $mod.ToUpper()))) {
                Remove-Item -Path $tPath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

Write-Host "Надстройка ЕСКД успешно удалена из SolidWorks." -ForegroundColor Green
