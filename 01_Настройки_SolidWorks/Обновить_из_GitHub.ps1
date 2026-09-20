<#
.SYNOPSIS
    Обновление инструментария ЕСКД в общей папке из официального репозитория GitHub (администратор).
.DESCRIPTION
    Одно действие вместо ручной цепочки «выкачать — собрать — проверить — разложить»:

      1. Берёт из GitHub выбранную ветку или метку в служебную рабочую копию (по умолчанию
         %LOCALAPPDATA%\ESKD\Источник). Копия служебная и чистая: локальные правки в ней стираются,
         чтобы в цех уходило ровно то, что лежит в репозитории.
      2. Показывает, что нового по сравнению с выпуском, который уже лежит в общей папке.
      3. Вызывает Publish-EskdToolkit.ps1: собирает надстройку, гоняет автотесты и только после них
         раскладывает выпуск в общую папку и пишет toolkit_release.json.

    Конструкторы после этого запускают у себя «Настройка_Рабочего_Места_SolidWorks.exe» из общей папки —
    как обычно. Их порядок работы не меняется.

    Ничего не публикуется, если автотесты не прошли: в общей папке остаётся прежний выпуск.

    Пароли и токены этот скрипт не хранит и не спрашивает. Открытый репозиторий читается без них;
    если репозиторий закрыть, доступ настраивается один раз штатным Git Credential Manager
    (`git config --global credential.helper manager`) — при первом обращении Windows спросит сам.
.PARAMETER Target
    Общая папка инструментария, например \\Synology_TR\Конструкторский отдел\...\_инструменты_конструктора
.PARAMETER Ref
    Ветка или метка репозитория. По умолчанию main.
.PARAMETER Repo
    Адрес репозитория. По умолчанию — официальный.
.PARAMETER WorkDir
    Служебная рабочая копия. По умолчанию %LOCALAPPDATA%\ESKD\Источник.
.PARAMETER Check
    Только показать, что изменилось бы. Ничего не собирается и не публикуется.
.PARAMETER SkipTests
    Опубликовать без автотестов. Только для отладки: в цех так не раздают.
.EXAMPLE
    .\Обновить_из_GitHub.ps1 -Target "\\Synology_TR\Конструкторский отдел\_Библиотека проектирования\_инструменты_конструктора"
.EXAMPLE
    .\Обновить_из_GitHub.ps1 -Target "Z:\Инструменты" -Ref v2026.09 -Check
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$Ref = "main",
    [string]$Repo = "https://github.com/lunin02118-ops/ESKD-AUTO.git",
    [string]$WorkDir = "",
    [switch]$Check,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$Target = $Target.TrimEnd('\')
if (-not $WorkDir) { $WorkDir = Join-Path $env:LOCALAPPDATA "ESKD\Источник" }

function Say($text)  { Write-Host $text -ForegroundColor Gray }
function Ok($text)   { Write-Host "  [OK] $text" -ForegroundColor Green }
function Info($text) { Write-Host "  [ИНФО] $text" -ForegroundColor DarkGray }
function Stop-Update($text) {
    Write-Host "[ОШИБКА] $text" -ForegroundColor Red
    Write-Host "Общая папка не тронута: у конструкторов остаётся прежний выпуск." -ForegroundColor Yellow
    exit 1
}

# Вывод git идёт в stderr (прогресс, подсказки) — для PowerShell это не ошибка, поэтому вызовы обёрнуты.
# Необязательная проверка: неудача — обычный ответ, а не сбой. Без снятия Stop вывод git в stderr валит скрипт.
function Git-Try([string[]]$Arguments) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $output = & git @Arguments 2>&1 } finally { $ErrorActionPreference = $old }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Lines = @($output | ForEach-Object { "$_" }) }
}

function Git-Run([string[]]$Arguments, [string]$What) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $output = & git @Arguments 2>&1 } finally { $ErrorActionPreference = $old }
    if ($LASTEXITCODE -ne 0) {
        $text = ($output | ForEach-Object { "$_" }) -join "`n"
        if ($text -match "Authentication|could not read Username|403|401") {
            Stop-Update ("${What}: репозиторий не отдал данные без входа. Настройте доступ один раз — " +
                         "git config --global credential.helper manager — и повторите: Windows спросит логин сам.")
        }
        Stop-Update "${What}: $text"
    }
    return $output
}

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " ОБНОВЛЕНИЕ ИНСТРУМЕНТАРИЯ ЕСКД ИЗ GITHUB" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "Репозиторий:   $Repo"
Write-Host "Ветка/метка:   $Ref"
Write-Host "Общая папка:   $Target"
Write-Host "Рабочая копия: $WorkDir"
Write-Host ""

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Stop-Update "Не найден git. Поставьте Git для Windows (https://git-scm.com/download/win) и повторите."
}

# 1. Свежая копия репозитория
Say "[1/4] Получение из GitHub..."
if (Test-Path -LiteralPath (Join-Path $WorkDir ".git")) {
    Git-Run @("-C", $WorkDir, "remote", "set-url", "origin", $Repo) "смена адреса репозитория" | Out-Null
    Git-Run @("-C", $WorkDir, "fetch", "--prune", "--tags", "origin") "получение из GitHub" | Out-Null
} else {
    if (Test-Path -LiteralPath $WorkDir) {
        $busy = @(Get-ChildItem -LiteralPath $WorkDir -Force | Select-Object -First 1).Count -gt 0
        if ($busy) { Stop-Update "Папка рабочей копии занята чем-то другим: $WorkDir. Укажите -WorkDir или очистите её." }
    }
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    Git-Run @("clone", "--no-checkout", $Repo, $WorkDir) "клонирование репозитория" | Out-Null
    Git-Run @("-C", $WorkDir, "fetch", "--tags", "origin") "получение меток" | Out-Null
}

# Ветка или метка: сначала пробуем ветку с origin, потом метку/коммит как есть.
$refSpec = "origin/$Ref"
if ((Git-Try @("-C", $WorkDir, "rev-parse", "--verify", "--quiet", "$refSpec^{commit}")).Code -ne 0) { $refSpec = $Ref }   # не ветка — значит метка или коммит
Git-Run @("-C", $WorkDir, "checkout", "--detach", $refSpec) "переключение на $Ref" | Out-Null
Git-Run @("-C", $WorkDir, "reset", "--hard", $refSpec) "сброс рабочей копии" | Out-Null
# Служебная копия чистится целиком: собранная надстройка соберётся заново, чужого в выпуск не попадёт.
Git-Run @("-C", $WorkDir, "clean", "-fdx") "очистка рабочей копии" | Out-Null

$new = (Git-Try @("-C", $WorkDir, "rev-parse", "--short", "HEAD")).Lines[0].Trim()
$when = (Git-Try @("-C", $WorkDir, "log", "-1", "--format=%ad", "--date=format:%d.%m.%Y %H:%M")).Lines[0].Trim()
Ok "Получено: $Ref = $new от $when"

# 2. Что нового по сравнению с опубликованным
Say "`n[2/4] Сравнение с выпуском в общей папке..."
$releaseFile = Join-Path $Target "toolkit_release.json"
$published = ""
if (Test-Path -LiteralPath $releaseFile) {
    try {
        $release = Get-Content -LiteralPath $releaseFile -Raw | ConvertFrom-Json
        $published = "$($release.commit)"
        Info "Сейчас опубликован выпуск $($release.version) (коммит $published, $($release.date))"
    } catch { Info "toolkit_release.json не разобран — сравнить не с чем." }
} else {
    Info "В общей папке выпуска ещё нет — будет первая публикация."
}

$same = $false
if ($published) {
    $publishedCommit = ($published -split '\+')[0]
    if ((Git-Try @("-C", $WorkDir, "cat-file", "-e", "$publishedCommit^{commit}")).Code -eq 0) {
        $behind = @((Git-Try @("-C", $WorkDir, "log", "--oneline", "$publishedCommit..HEAD")).Lines | Where-Object { $_ })
        if ($behind.Count -eq 0) {
            $same = $true
            Ok "Новых изменений нет — в общей папке уже последняя версия."
        } else {
            Write-Host "  Новых коммитов: $($behind.Count)" -ForegroundColor White
            $behind | Select-Object -First 15 | ForEach-Object { Write-Host "    $_" }
            if ($behind.Count -gt 15) { Write-Host "    … и ещё $($behind.Count - 15)" }
        }
    } else {
        Info "Опубликованного коммита $publishedCommit нет в репозитории — список изменений не построить."
    }
}

if ($Check) {
    Write-Host "`nПроверка без изменений (-Check): ничего не собрано и не опубликовано." -ForegroundColor Yellow
    exit $(if ($same) { 0 } else { 10 })
}
if ($same) { exit 0 }

# 3. Сборка, автотесты и раскладка — существующей публикацией из этой же копии
Say "`n[3/4] Сборка, автотесты и публикация..."
$publish = Join-Path $WorkDir "01_Настройки_SolidWorks\Publish-EskdToolkit.ps1"
if (-not (Test-Path -LiteralPath $publish)) { Stop-Update "В полученной копии нет Publish-EskdToolkit.ps1: $publish" }

# PyInstaller нужен, только если правили исходники окна настройки; собранное окно лежит в репозитории.
$hasPyInstaller = $false
if (Get-Command python -ErrorAction SilentlyContinue) {
    & python -c "import PyInstaller" 2>$null
    $hasPyInstaller = ($LASTEXITCODE -eq 0)
}
if (-not $hasPyInstaller) { Info "PyInstaller не найден — окно настройки берётся из репозитория." }

$arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $publish, "-Target", $Target)
if (-not $hasPyInstaller) { $arguments += "-SkipGuiBuild" }
if ($SkipTests) { $arguments += "-SkipTests" }
# Сборка и автотесты пишут ход работы в stderr (PyInstaller, unittest). Для PowerShell это не ошибка,
# но при ErrorActionPreference=Stop первая же такая строка обрывает обновление — поэтому режим снимается.
$old = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try { & powershell.exe @arguments } finally { $ErrorActionPreference = $old }
if ($LASTEXITCODE -ne 0) { Stop-Update "Публикация не выполнена (код $LASTEXITCODE)." }

# 4. Итог
Say "`n[4/4] Проверка результата..."
if (-not (Test-Path -LiteralPath $releaseFile)) { Stop-Update "Публикация прошла, но toolkit_release.json не появился." }
$result = Get-Content -LiteralPath $releaseFile -Raw | ConvertFrom-Json
Ok "В общей папке выпуск $($result.version), коммит $($result.commit)"
Write-Host ""
Write-Host "Конструкторам: запустить $Target\01_Настройки_SolidWorks\Настройка_Рабочего_Места_SolidWorks.exe" -ForegroundColor White
exit 0
