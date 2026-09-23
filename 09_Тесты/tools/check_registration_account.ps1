<#
.SYNOPSIS
    Регистрация надстройки под чужой учётной записью (T0, сверка SW API 23.09.2026, №2).
.DESCRIPTION
    Проверяет функцию модуля, которая отказывает, если скрипт регистрации запущен не от имени того, кто работает за
    компьютером («Запуск от имени администратора» с паролем ИТ): автозагрузка надстройки ушла бы в профиль
    администратора, а скрипт писал «[OK]». Вторую учётку автотест не заведёт — SID подставляются параметрами. Вывод — JSON.
#>
param([Parameter(Mandatory = $true)][string]$ModulePath)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. $ModulePath
[pscustomobject]@{
    foreign = Get-EskdForeignAccountMessage -CurrentSid 'S-1-5-21-1-2-3-500' -SessionSid 'S-1-5-21-1-2-3-1001'
    same    = Get-EskdForeignAccountMessage -CurrentSid 'S-1-5-21-1-2-3-1001' -SessionSid 'S-1-5-21-1-2-3-1001'
    unknown = Get-EskdForeignAccountMessage -CurrentSid 'S-1-5-21-1-2-3-500' -SessionSid ''
    here    = Get-EskdForeignAccountMessage
    foreign_unreg = Get-EskdForeignAccountMessage -CurrentSid 'S-1-5-21-1-2-3-500' -SessionSid 'S-1-5-21-1-2-3-1001' -Action Unregister -ScriptPath 'C:\ЕСКД\unregister.ps1'
    session_is_me = ((Get-EskdSessionUserSid) -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
} | ConvertTo-Json -Compress
