<#
.SYNOPSIS
    Публикация инструментария ЕСКД в общую папку (администратор).
.DESCRIPTION
    1. Собирает надстройку ЕСКД (build.ps1) и окно настройки (PyInstaller) — пропуск ключом -SkipBuild.
    2. Проверяет автотесты static (без SolidWorks) на собранной надстройке — пропуск ключом -SkipTests.
    3. Копирует репозиторий в папку -Target зеркалом robocopy без служебных папок разработки.
    4. С ключом -SwToolsSetup кладёт установщик SWTools в 03_Макросы_и_Плагины\SWTools_Установщик (в репозитории
       его нет; зеркало эту папку не трогает, прежний выпуск SWTools без ключа остаётся).
    5. Последним пишет toolkit_release.json: версия, коммит, дата, кто опубликовал.

    Конструкторы после публикации запускают 01_Настройки_SolidWorks\Настройка_Рабочего_Места_SolidWorks.exe из
    общей папки: шаблоны, форматки и библиотеки у всех обновляются сразу, макросы и надстройка — после запуска окна.

    Зеркало удаляет в папке назначения всё, чего нет в репозитории. Поэтому папка назначения должна быть пустой или уже
    содержать опубликованный инструментарий (toolkit_release.json). У конструкторов на неё — право только на чтение.
.PARAMETER Target
    Общая папка инструментария, например Z:\00_ИНСТРУМЕНТЫ\Инструменты_Конструктора или \\сервер\КТО\Инструменты.
.EXAMPLE
    .\Publish-EskdToolkit.ps1 -Target "Z:\00_ИНСТРУМЕНТЫ\Инструменты_Конструктора"
.EXAMPLE
    .\Publish-EskdToolkit.ps1 -Target "Z:\00_ИНСТРУМЕНТЫ\Инструменты_Конструктора" -SwToolsSetup "D:\сборки\SWTools-1.1.110-Setup.exe"
.PARAMETER SwToolsSetup
    Установщик SWTools-<версия>-Setup.exe из сборки SWTools; рядом должен лежать его .manifest.json.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [switch]$SkipTests,
    [switch]$SkipBuild,
    [switch]$SkipGuiBuild,
    [string]$SwToolsSetup = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Path $PSScriptRoot -Parent
$Target = $Target.TrimEnd('\')
Import-Module (Join-Path $PSScriptRoot "EskdDeploy.psm1") -Force -DisableNameChecking

# Сборка и автотесты пишут ход работы в stderr; при ErrorActionPreference=Stop PowerShell считает это ошибкой
# и обрывает публикацию на первой же строке. Внешние программы запускаются со снятым режимом, а код возврата
# проверяется явно (аудит 20.09.2026).
function Invoke-Native([scriptblock]$Command) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $Command } finally { $ErrorActionPreference = $old }
}

function Stop-Publish($message) {
    Write-Host "[ОШИБКА] $message" -ForegroundColor Red
    exit 1
}

if (-not (Test-EskdSourceRoot -Path $repo)) { Stop-Publish "Скрипт должен лежать в 01_Настройки_SolidWorks репозитория: $repo" }
if ([System.IO.Path]::GetFullPath($Target).StartsWith([System.IO.Path]::GetFullPath($repo), [StringComparison]::OrdinalIgnoreCase)) {
    Stop-Publish "Папка назначения внутри репозитория: $Target"
}
if (Test-Path -LiteralPath $Target) {
    $hasItems = @(Get-ChildItem -LiteralPath $Target -Force | Select-Object -First 1).Count -gt 0
    if ($hasItems -and -not (Test-Path -LiteralPath (Join-Path $Target "toolkit_release.json")) -and -not (Test-EskdSourceRoot -Path $Target)) {
        Stop-Publish "Папка назначения не пуста и не содержит опубликованный инструментарий (toolkit_release.json). Зеркало удалило бы в ней чужие файлы: $Target"
    }
} else {
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
}

$commit = (& git -C $repo rev-parse --short HEAD 2>$null)
$dirty = @(& git -C $repo status --porcelain --untracked-files=no 2>$null).Count -gt 0
if ($dirty) { Write-Host "[ВНИМАНИЕ] В репозитории есть незакоммиченные изменения — они тоже будут опубликованы." -ForegroundColor Yellow }

if (-not $SkipBuild) {
    Write-Host "`nСборка надстройки ЕСКД..." -ForegroundColor Gray
    Invoke-Native { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo "03_Макросы_и_Плагины\ESKD_Material_Sync_Addin\build.ps1") }
    if ($LASTEXITCODE -ne 0) { Stop-Publish "Надстройка не собрана." }
    # Окно настройки лежит в репозитории собранным, PyInstaller нужен только когда правили его исходники.
    # На машине без Python (обновление из GitHub у администратора) сборка пропускается — берётся файл из репозитория.
    $gui = Join-Path $PSScriptRoot "Настройка_Рабочего_Места_SolidWorks.exe"
    if ($SkipGuiBuild) {
        if (-not (Test-Path -LiteralPath $gui)) { Stop-Publish "Окно настройки не собрано и его нет в репозитории: $gui" }
        Write-Host "`nОкно настройки: из репозитория (-SkipGuiBuild)." -ForegroundColor Gray
    } else {
        Write-Host "`nСборка окна настройки..." -ForegroundColor Gray
        $sources = Join-Path $PSScriptRoot "_Исходники"
        Invoke-Native { & python -m PyInstaller --noconfirm --distpath $PSScriptRoot --workpath (Join-Path $PSScriptRoot "build_temp") `
            (Join-Path $sources "Настройка_Рабочего_Места_SolidWorks.spec") }
        $code = $LASTEXITCODE
        Remove-Item -LiteralPath (Join-Path $PSScriptRoot "build_temp") -Recurse -Force -ErrorAction SilentlyContinue
        if ($code -ne 0) { Stop-Publish "Окно настройки не собрано (PyInstaller)." }
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $repo "03_Макросы_и_Плагины\ESKD_Material_Sync_Addin\ESKD_Material_Sync_v5.dll"))) {
    Stop-Publish "Нет собранной надстройки ESKD_Material_Sync_v5.dll."
}

if (-not $SkipTests) {
    # После сборки: проверки собранной надстройки обязаны выполниться, пропуск из-за отсутствия DLL — провал.
    Write-Host "`nАвтотесты static..." -ForegroundColor Gray
    $env:ESKD_REQUIRE_BUILD = "1"
    try { Invoke-Native { & python (Join-Path $repo "09_Тесты\run_tests.py") static } } finally { Remove-Item Env:\ESKD_REQUIRE_BUILD -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { Stop-Publish "Автотесты не прошли — публикация отменена." }
}

Write-Host "`nКопирование в $Target ..." -ForegroundColor Gray
$excludeDirs = @(".git", ".claude", "08_Результаты_Тестирования", "09_Тесты", "99_Архив", "07_Драйверы_NVIDIA", "build_temp",
                 "Backups", "Legacy_Builds", "_VBA_выгрузка", "__pycache__",
                 "swtools",             # клон репозитория SWTools у разработчика (ТЗ-02 огр. 4а) не публикуется
                 "SWTools_Установщик")  # установщик SWTools — только в общей папке (-SwToolsSetup), зеркало его не удаляет
$excludeFiles = @(".git", "*_old", "*.clean_old", "*.f40_old", '~$*', "*.tmp", "toolkit_release.json")  # .git — файл-ссылка worktree
$releaseFile = Join-Path $Target "toolkit_release.json"
Remove-Item -LiteralPath $releaseFile -Force -ErrorAction SilentlyContinue  # на время копирования выпуск не считается опубликованным
& robocopy.exe $repo $Target /MIR /R:2 /W:5 /NP /NFL /NDL /XD @excludeDirs /XF @excludeFiles | Out-Host
$rc = $LASTEXITCODE
if ($rc -ge 8) { Stop-Publish "robocopy завершился с кодом ${rc}: часть файлов не скопирована (занятые файлы — закройте окно настройки у пользователей)." }

if ($SwToolsSetup) {
    try {
        $swTools = Publish-EskdSwToolsSetup -SetupPath $SwToolsSetup -TargetRoot $Target
        Write-Host "[OK] SWTools $($swTools.Version): $($swTools.SetupPath)" -ForegroundColor Green
    } catch {
        Stop-Publish "SWTools не опубликован: $($_.Exception.Message)"
    }
} else {
    $swTools = $null
    try { $swTools = Get-EskdSwToolsRelease -SourceRoot $Target } catch { Write-Host "[ВНИМАНИЕ] $($_.Exception.Message)" -ForegroundColor Yellow }
    if ($swTools) { Write-Host "SWTools в общей папке: $($swTools.Version) (без изменений)." -ForegroundColor Gray }
    else { Write-Host "[ВНИМАНИЕ] В общей папке нет установщика SWTools — укажите -SwToolsSetup." -ForegroundColor Yellow }
}

$date = Get-Date
$release = [ordered]@{
    version   = $date.ToString("yyyy.MM.dd.HHmm")
    date      = $date.ToString("dd.MM.yyyy HH:mm")
    commit    = "$commit$(if ($dirty) { '+изменения' })"
    publisher = "$env:USERDOMAIN\$env:USERNAME"
    # SHA-256 опубликованных файлов локальной копии: по ним установщик проверяет источник и копию (ТЗ-01 Т-39, -Mode Check)
    files     = @(New-EskdReleaseFiles -SourceRoot $Target)
}
[System.IO.File]::WriteAllText($releaseFile, ($release | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "`n[OK] Опубликован выпуск $($release.version) (коммит $($release.commit)) в $Target" -ForegroundColor Green
Write-Host "Конструкторам: запустить $Target\01_Настройки_SolidWorks\Настройка_Рабочего_Места_SolidWorks.exe" -ForegroundColor Green
exit 0
