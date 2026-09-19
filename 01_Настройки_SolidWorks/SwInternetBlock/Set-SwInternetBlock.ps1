# Set-SwInternetBlock.ps1 - "отучение SOLIDWORKS от интернета": пакет файрвол-правил
# (исходящие+входящие блокировки всех EXE SolidWorks - телеметрия/облако/лицензионные звонки).
# Основа - проверенный живой пакет с рабочей машины (манифест SWInternetBlock.manifest.json, 258 записей).
#
# ВАЖНО (аудит 07.09.2026): правило для SLDWORKS.exe (OUT 096) ЛОМАЕТ функцию
# "Поделиться настройками" аддона Drew (аддон работает внутри процесса SLDWORKS).
# По умолчанию apply ПРОПУСКАЕТ его; создать сознательно: -BlockSldWorks.
#
# Режимы:
#   apply  - создать отсутствующие правила (файл должен существовать на машине; идемпотентно)
#   audit  - сверка: что есть, чего нет, мёртвые пути, конфликты
#   remove - удалить весь пакет (-BlockSldWorks удаляет и правило SLDWORKS)
#
# Примеры:
#   powershell -File Set-SwInternetBlock.ps1 -Mode audit
#   powershell -File Set-SwInternetBlock.ps1 -Mode apply     (администратор)
#   powershell -File Set-SwInternetBlock.ps1 -Mode remove

param(
  [string]$Mode = 'audit',
  [string]$ManifestPath = (Join-Path $PSScriptRoot 'SWInternetBlock.manifest.json'),
  [switch]$BlockSldWorks
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ManifestPath)) { Write-Output "FATAL: манифест не найден: $ManifestPath"; exit 1 }
$entries = Get-Content $ManifestPath -Raw | ConvertFrom-Json
Write-Output ("манифест: {0} записей" -f @($entries).Count)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Mode -ne 'audit' -and -not $isAdmin) { Write-Output 'FATAL: apply/remove требуют прав администратора.'; exit 1 }

function Test-SldWorksConflict([string]$path, [string]$dir) {
  # Ни одного правила на SLDWORKS.exe: входящее тоже пропускаем — оно мешало лицензированию на новом ПК (аудит 20.09.2026).
  return ($path -match 'SLDWORKS\.exe$')
}

$script:SwDomains = @(
  'activate.solidworks.com','activate-se.solidworks.com','backupactivate.solidworks.com',
  'backupactivate-se.solidworks.com','online.solidworks.com','online-se.solidworks.com',
  'backuponline.solidworks.com','backuponline-se.solidworks.com','backoffice.solidworks.com',
  'api.solidworks.com','performance.solidworks.com','iam.3ds.com','my.solidworks.com',
  'customerportal.solidworks.com','webapps.solidworks.com','dslauncher.3ds.com','companion.3ds.com'
)
$script:HostsMarker = 'SW-Internet-Block'

function Invoke-HostsApply {
  $hp = 'C:\Windows\System32\drivers\etc\hosts'
  $cur = Get-Content $hp -Raw -EA SilentlyContinue
  $add = @()
  foreach ($d in $script:SwDomains) { if ($cur -notmatch [regex]::Escape($d)) { $add += ('0.0.0.0 ' + $d) } }
  if ($add.Count -gt 0) {
    # hosts постоянно читается службами - пишем через FileShare.ReadWrite (Add-Content конфликтует)
    $hp2 = 'C:\Windows\System32\drivers\etc\hosts'
    $text = ''
    foreach ($l in $add) { $text += $l + "`r`n" }
    $marker = '# ' + $script:HostsMarker + ' (SOLIDWORKS cloud/telemetry isolation - Drew safe: AWS untouched)'
    if (-not (Get-Content $hp2 -Raw).Contains($script:HostsMarker)) { $text = "`r`n" + $marker + "`r`n" + $text }
    $done = $false
    for ($try = 1; $try -le 8 -and -not $done; $try++) {
      try {
        $fs = [IO.File]::Open($hp2, 'Append', 'Write', [IO.FileShare]::ReadWrite)
        $b = [Text.Encoding]::ASCII.GetBytes($text)
        $fs.Write($b, 0, $b.Length); $fs.Close()
        $done = $true
      } catch { Start-Sleep -Milliseconds 700 }
    }
    if (-not $done) { Write-Output 'FATAL: hosts недоступен для записи (8 попыток).'; exit 1 }
  }
  Write-Output ('hosts: добавлено доменов {0}, всего под маркером {1}' -f $add.Count, $script:SwDomains.Count)
}

function Invoke-HostsRemove {
  $hp = 'C:\Windows\System32\drivers\etc\hosts'
  $lines = Get-Content $hp | Where-Object { ($_ -notmatch $script:HostsMarker) -and -not ($_ -match '0\.0\.0\.0\s+\S' -and $_ -match 'solidworks|3ds\.com') }
  Set-Content $hp $lines -Encoding ASCII
  Write-Output 'hosts: домены SW удалены'
}

if ($Mode -ceq 'apply') {
  $created = 0; $skippedExist = 0; $skippedPolicy = 0; $skippedNoFile = 0
  foreach ($e in $entries) {
    if (-not (Test-Path -LiteralPath $e.path)) { $skippedNoFile++; continue }
    if ((Test-SldWorksConflict $e.path $e.direction) -and -not $BlockSldWorks) { $skippedPolicy++; continue }
    $existing = Get-NetFirewallRule -DisplayName $e.name -EA SilentlyContinue
    if ($existing) { $skippedExist++; continue }
    New-NetFirewallRule -DisplayName $e.name -Direction $e.direction -Action Block -Enabled True -Profile Any -Program $e.path -RemoteAddress Internet | Out-Null
    $created++
  }
  Write-Output ("apply: создано {0}, уже были {1}, нет файла {2}, пропущено по политике (SLDWORKS/Share) {3}" -f $created, $skippedExist, $skippedNoFile, $skippedPolicy)
  if ($skippedPolicy -gt 0 -and -not $BlockSldWorks) {
    Write-Output '  SLDWORKS.exe не заблокирован: функция Drew "Поделиться настройками" осталась рабочей.'
    Write-Output '  Чтобы заблокировать и его: -BlockSldWorks (сломает Share!).'
  }
  exit 0
}

if ($Mode -ceq 'audit') {
  $exist = 0; $missing = 0; $dead = 0; $conflict = 0
  foreach ($e in $entries) {
    $fileOk = Test-Path -LiteralPath $e.path
    $ruleOk = [bool](Get-NetFirewallRule -DisplayName $e.name -EA SilentlyContinue)
    if ($fileOk -and $ruleOk) { $exist++ } elseif (-not $fileOk) { $dead++ } else { $missing++ }
    if (Test-SldWorksConflict $e.path $e.direction) { $conflict++; Write-Output ("  КОНФЛИКТ (ломает Drew Share): {0} [{1}]" -f $e.name, $e.direction) }
  }
  Write-Output ("audit: активных пар файл+правило {0}, правил не хватает {1}, файлов нет на машине {2}, конфликтов с Drew Share {3}" -f $exist, $missing, $dead, $conflict)
  exit 0
}

if ($Mode -ceq 'hosts-apply') {
  if (-not $isAdmin) { Write-Output 'FATAL: hosts-apply требует прав администратора.'; exit 1 }
  Invoke-HostsApply
  exit 0
}
if ($Mode -ceq 'hosts-remove') {
  if (-not $isAdmin) { Write-Output 'FATAL: hosts-remove требует прав администратора.'; exit 1 }
  Invoke-HostsRemove
  exit 0
}

if ($Mode -ceq 'remove') {
  $removed = 0
  foreach ($e in $entries) {
    if ((Test-SldWorksConflict $e.path $e.direction) -and -not $BlockSldWorks) { continue }
    $r = Get-NetFirewallRule -DisplayName $e.name -EA SilentlyContinue
    if ($r) { $r | Remove-NetFirewallRule; $removed++ }
  }
  Write-Output ("remove: удалено правил {0}" -f $removed)
  exit 0
}

Write-Output "FATAL: неизвестный режим: $Mode (apply | audit | remove)"
exit 1
