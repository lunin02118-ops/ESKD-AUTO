<#
.SYNOPSIS
    Удаление регистрации надстройки ЕСКД из SolidWorks.

.DESCRIPTION
    Снимает регистрацию текущей и прежних версий через общий модуль Register-EskdAddin.ps1 (с правами
    администратора — и в HKLM) и удаляет вкладку ЕСКД из сохранённой раскладки CommandManager.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot "Register-EskdAddin.ps1")

Unregister-EskdAddin -SystemWide:(Test-EskdAdministrator)

$guids = @($script:EskdAddin.Guid) + $script:EskdAddin.ObsoleteGuids
Get-ChildItem "HKCU:\Software\SolidWorks" -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -match '^SOLIDWORKS \d{4}$' } |
    ForEach-Object {
        foreach ($ctx in @("PartContext", "AssyContext", "DrwContext")) {
            $ctxPath = Join-Path $_.PSPath "User Interface\CommandManager\$ctx"
            if (-not (Test-Path -LiteralPath $ctxPath)) { continue }
            Get-ChildItem -LiteralPath $ctxPath | ForEach-Object {
                $tab = Get-ItemProperty -LiteralPath $_.PSPath
                $ref = if ($tab.PSObject.Properties["RefName"]) { $tab.RefName } else { "" }
                $module = if ($tab.PSObject.Properties["ModuleName"]) { $tab.ModuleName } else { "" }
                if ($ref -match "ЕСКД" -or ($module -and $guids -contains $module.ToUpper())) {
                    Remove-Item -LiteralPath $_.PSPath -Recurse -Force
                }
            }
        }
    }

$state = Get-EskdAddinRegistration
if ($state.UserAddIn -or $state.UserStartup -or $state.UserCodeBase) {
    Write-Host "[ОШИБКА] Регистрация для текущего пользователя осталась:" -ForegroundColor Red
    $state | Format-List | Out-String | Write-Host
    exit 1
}
Write-Host "Надстройка ЕСКД удалена из SolidWorks." -ForegroundColor Green
