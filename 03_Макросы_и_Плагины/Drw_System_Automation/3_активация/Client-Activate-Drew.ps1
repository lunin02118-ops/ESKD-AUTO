# Client-Activate-Drew.ps1 - окно активации Drew на машине клиента (полностью офлайн).
# Два поля: ключ железа ЭТОЙ машины (сообщить офицеру) и код активации (от офицера).
# Проверяет код (ECDSA, привязка к железу) и пишет %APPDATA%\CAD Booster\Drew\Activation.code.
# Требует Gov-издание с SimpleCode (DrewAirGap.Activation.dll в каталоге Drew).
#
# Окно:    powershell -STA -File Client-Activate-Drew.ps1
# CLI:     powershell -File Client-Activate-Drew.ps1 -Code "DREW-..."
# Только ключ железа: -ShowMidOnly

param(
  [string]$EcdsaPubB64 = 'RUNTMSAAAACmHodQ0v8L2BRTftNR8t9TD6WwCfP+z5FV4DdZcM1kqD2Ocfkk0DaLbOiteyM+6dAprZ0VlymjzD2HixkP/JYB',   # внедряется при генерации клиентского кита
  [string]$Code = '',
  [switch]$ShowMidOnly
)

$ErrorActionPreference = 'Stop'

# ================= общие функции =================

function Get-MachineCode {
  $uuid = ''
  try { $c = Get-CimInstance -Class Win32_ComputerSystemProduct -ErrorAction Stop; if ($c -and $c.UUID) { $uuid = ([string]$c.UUID).Trim() } } catch { }
  if ($uuid -ne '') {
    $s = 'UUID' + (' ' * 34) + $uuid + '  '
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $h = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::Unicode.GetBytes($s)))).Replace('-', '')
    $sha.Dispose(); return @{ Code = $h; Source = 'PI10' }
  }
  $mg = ''
  try { $mg = ([string](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography' -ErrorAction Stop).MachineGuid).Trim() } catch { }
  if ($mg -ne '') {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $h = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($mg)))).Replace('-', '').ToLowerInvariant()
    $sha.Dispose(); return @{ Code = $h; Source = 'PI2' }
  }
  return $null
}

$script:alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'
function ConvertFrom-Base32([string]$s) {
  # убрать префикс "DREW" ДО фильтра: буквы префикса сами входят в алфавит base32
  $dash = $s.IndexOf('-'); if ($dash -ge 0) { $s = $s.Substring($dash + 1) }
  $clean = ($s.ToUpperInvariant() -replace '[^0-9A-Z]', '')
  foreach ($c in $clean.ToCharArray()) { if ($script:alphabet.IndexOf($c) -lt 0) { return $null } }
  $bytes = New-Object System.Collections.Generic.List[byte]
  $bits = 0; $acc = [long]0
  foreach ($c in $clean.ToCharArray()) {
    $acc = ($acc -shl 5) -bor $script:alphabet.IndexOf($c); $bits += 5
    if ($bits -ge 8) { $bits -= 8; $bytes.Add([byte](($acc -shr $bits) -band 255)) }
  }
  return $bytes.ToArray()
}

function Test-ActivationCode([string]$code, [string]$machineCode) {
  # Возвращает @{ Ok; Reason }: ECDSA-подпись + привязка MidHash16 к этой машине.
  if (-not $code) { return @{ Ok = $false; Reason = 'код пуст' } }
  if (-not $code.Trim().ToUpperInvariant().StartsWith('DREW-')) { return @{ Ok = $false; Reason = 'код не начинается с DREW-' } }
  $code85 = ConvertFrom-Base32 $code
  if ($null -eq $code85 -or $code85.Count -ne 85) { return @{ Ok = $false; Reason = 'неверная длина кода' } }
  $payload = New-Object 'byte[]' 21; $sig = New-Object 'byte[]' 64
  [Array]::Copy($code85, 0, $payload, 0, 21); [Array]::Copy($code85, 21, $sig, 0, 64)
  try {
    $key = [System.Security.Cryptography.CngKey]::Import([Convert]::FromBase64String($EcdsaPubB64), [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $ecdsa = New-Object System.Security.Cryptography.ECDsaCng($key)
    $sigOk = $ecdsa.VerifyData($payload, $sig)
    $ecdsa.Dispose()
  } catch { return @{ Ok = $false; Reason = ('ошибка ECDSA: ' + $_.Exception.Message) } }
  if (-not $sigOk) { return @{ Ok = $false; Reason = 'подпись кода недействительна' } }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $midHash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($machineCode.ToUpperInvariant()))
  $sha.Dispose()
  for ($i = 0; $i -lt 16; $i++) { if ($payload[$i] -ne $midHash[$i]) { return @{ Ok = $false; Reason = 'код выдан для ДРУГОЙ машины' } } }
  return @{ Ok = $true; Reason = '' }
}

function Save-ActivationFile([string]$code, [string]$machineCode) {
  $dir = Join-Path $env:APPDATA 'CAD Booster\Drew'
  New-Item -ItemType Directory -Path $dir -Force | Out-Null
  $path = Join-Path $dir 'Activation.code'
  [IO.File]::WriteAllLines($path, @($code.Trim(), $machineCode.ToUpperInvariant(), [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')))
  # MARKER .skm: старт Drew заходит в лицензионную ветку ТОЛЬКО при наличии LicenseKey.skm;
  # внутри ветки префикс A.E::a возьмёт лицензию из Activation.code (содержимое .skm не парсится).
  [IO.File]::WriteAllText((Join-Path $dir 'LicenseKey.skm'), "AIRGAP-CODE-ACTIVATION`r`n" + [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') + "`r`n")
  return $path
}

$mc = Get-MachineCode
if ($null -eq $mc) { Write-Output 'MACHINE_CODE|UnknownMachineCode'; exit 1 }

# ================= CLI-режимы =================

if ($ShowMidOnly) { Write-Output ('MACHINE_CODE|' + $mc.Code); exit 0 }

if ($Code -ne '') {
  $r = Test-ActivationCode $Code $mc.Code
  if (-not $r.Ok) { Write-Output ('ERROR|' + $r.Reason); exit 1 }
  $p = Save-ActivationFile $Code $mc.Code
  Write-Output ('ACTIVATED|' + $p)
  exit 0
}

# ================= окно =================

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Активация Drew (офлайн)'
$form.Size = New-Object System.Drawing.Size(560, 430)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$font = New-Object System.Drawing.Font('Segoe UI', 9)

$l1 = New-Object System.Windows.Forms.Label
$l1.Text = '1. Сообщите офицеру КЛЮЧ ЖЕЛЕЗА этой машины:'
$l1.Location = New-Object System.Drawing.Point(14, 14); $l1.Size = New-Object System.Drawing.Size(520, 20); $l1.Font = $font

$tbMid = New-Object System.Windows.Forms.TextBox
$tbMid.Text = $mc.Code; $tbMid.ReadOnly = $true; $tbMid.Font = New-Object System.Drawing.Font('Consolas', 9)
$tbMid.Location = New-Object System.Drawing.Point(14, 36); $tbMid.Size = New-Object System.Drawing.Size(430, 24)

$btnCopy = New-Object System.Windows.Forms.Button
$btnCopy.Text = 'Скопировать'; $btnCopy.Location = New-Object System.Drawing.Point(452, 34); $btnCopy.Size = New-Object System.Drawing.Size(88, 26); $btnCopy.Font = $font
$btnCopy.Add_Click({ Set-Clipboard -Value $tbMid.Text; $btnCopy.Text = 'Скопировано' })

$l2 = New-Object System.Windows.Forms.Label
$l2.Text = '2. Вставьте КОД АКТИВАЦИИ, полученный от офицера:'
$l2.Location = New-Object System.Drawing.Point(14, 74); $l2.Size = New-Object System.Drawing.Size(520, 20); $l2.Font = $font

$tbCode = New-Object System.Windows.Forms.TextBox
$tbCode.Multiline = $true; $tbCode.ScrollBars = 'Vertical'; $tbCode.Font = New-Object System.Drawing.Font('Consolas', 9)
$tbCode.Location = New-Object System.Drawing.Point(14, 96); $tbCode.Size = New-Object System.Drawing.Size(526, 180)

$l3 = New-Object System.Windows.Forms.Label
$l3.Location = New-Object System.Drawing.Point(14, 282); $l3.Size = New-Object System.Drawing.Size(526, 40); $l3.Font = $font; $l3.ForeColor = [System.Drawing.Color]::DarkRed

$btnAct = New-Object System.Windows.Forms.Button
$btnAct.Text = 'АКТИВИРОВАТЬ'; $btnAct.Font = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)
$btnAct.Location = New-Object System.Drawing.Point(14, 330); $btnAct.Size = New-Object System.Drawing.Size(526, 40)
$btnAct.Add_Click({
  try {
    $r = Test-ActivationCode $tbCode.Text $mc.Code
    if (-not $r.Ok) { $l3.Text = 'ОТКАЗ: ' + $r.Reason; return }
    $p = Save-ActivationFile $tbCode.Text $mc.Code
    $l3.ForeColor = [System.Drawing.Color]::DarkGreen
    $l3.Text = 'ЛИЦЕНЗИЯ АКТИВИРОВАНА (бессрочно). Запустите SOLIDWORKS.'
    [void][System.Windows.Forms.MessageBox]::Show($form, "Активация выполнена.`n`nФайл: $p`nЗапустите SOLIDWORKS - лицензия будет подобрана автоматически.", 'Активация Drew', 'OK', 'Information')
    $form.Close()
  } catch { $l3.Text = 'ОШИБКА: ' + $_.Exception.Message }
})

$form.Controls.AddRange(@($l1, $tbMid, $btnCopy, $l2, $tbCode, $l3, $btnAct))
[void]$form.ShowDialog()
exit 0
