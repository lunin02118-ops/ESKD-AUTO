<#
.SYNOPSIS
    Сборка архива выпуска инструментария ЕСКД и публикация его на странице Releases в GitHub.
.DESCRIPTION
    Готовит ровно тот же комплект, который раскладывается в общую папку, но одним zip-архивом:

      1. Собирает надстройку и окно настройки, гоняет автотесты и складывает выпуск во временную
         папку — тем же Publish-EskdToolkit.ps1, которым публикуется общая папка. Архив и общая
         папка получаются из одного источника, разойтись они не могут.
      2. Кладёт в корень «ЧИТАТЬ_ПЕРВЫМ.txt»: куда распаковать и что запустить.
      3. Пакует в ESKD-AUTO-<версия>.zip и считает SHA-256 — по нему видно, что архив скачался целиком.
      4. С ключом -Publish выкладывает архив на страницу Releases в GitHub под меткой v<версия>,
         с перечнем изменений с прошлого выпуска.

    Кому это нужно: тому, кто выпускает версии. Остальным достаточно зайти на страницу Releases,
    скачать zip и распаковать куда угодно — путь внутри архива ни к чему не привязан.

    Ничего не выкладывается, если автотесты не прошли.

    Пароли и токены скрипт не хранит и не спрашивает. Выкладывает через gh (GitHub CLI) — он один раз
    настраивается командой gh auth login и дальше помнит вход сам.
.PARAMETER OutDir
    Куда складывать архивы. По умолчанию %LOCALAPPDATA%\ESKD\Выпуски.
.PARAMETER Publish
    Выложить архив на страницу Releases в GitHub. Без ключа архив только собирается.
.PARAMETER Draft
    Вместе с -Publish: выпуск-черновик, виден только владельцу репозитория. Публикуется вручную.
.PARAMETER SkipTests
    Собрать без автотестов. Только для отладки: так выпуск не выкладывают.
.EXAMPLE
    .\Собрать_архив_выпуска.ps1
.EXAMPLE
    .\Собрать_архив_выпуска.ps1 -Publish -Draft
#>
[CmdletBinding()]
param(
    [string]$OutDir = "",
    [switch]$Publish,
    [switch]$Draft,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -Parent  # сценарий в 01_Настройки_SolidWorks\_Служебное
Import-Module (Join-Path $PSScriptRoot "EskdDeploy.psm1") -Force -DisableNameChecking

function Say($text)  { Write-Host $text -ForegroundColor Gray }
function Ok($text)   { Write-Host "  [OK] $text" -ForegroundColor Green }
function Info($text) { Write-Host "  [ИНФО] $text" -ForegroundColor DarkGray }
function Stop-Release($text) {
    Write-Host "[ОШИБКА] $text" -ForegroundColor Red
    Write-Host "Выпуск не собран: на странице Releases ничего не изменилось." -ForegroundColor Yellow
    exit 1
}

# Сборка, автотесты и git пишут ход работы в stderr. Для PowerShell это не ошибка, но при
# ErrorActionPreference=Stop первая же такая строка обрывает сборку — поэтому режим снимается,
# а код возврата проверяется явно.
function Invoke-Native([scriptblock]$Command) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $Command } finally { $ErrorActionPreference = $old }
}

if (-not (Test-EskdSourceRoot -Path $repo)) { Stop-Release "Скрипт должен лежать в 01_Настройки_SolidWorks репозитория: $repo" }
if (-not $OutDir)  { $OutDir  = Join-Path $env:LOCALAPPDATA "ESKD\Выпуски" }
# Версию ставит публикатор в toolkit_release.json — здесь она читается после сборки. Своя отметка
# времени разошлась бы с той, что лежит внутри архива, и выпуск врал бы о самом себе (20.09.2026).
$staging = Join-Path $OutDir "_сборка"

$commit = (Invoke-Native { & git -C $repo rev-parse --short HEAD }) | Select-Object -First 1
$head   = (Invoke-Native { & git -C $repo rev-parse HEAD }) | Select-Object -First 1
$branch = (Invoke-Native { & git -C $repo rev-parse --abbrev-ref HEAD }) | Select-Object -First 1

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " АРХИВ ВЫПУСКА ИНСТРУМЕНТАРИЯ ЕСКД" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "Коммит:  $commit, ветка $branch"
Write-Host "Папка:   $OutDir"
Write-Host ""

# 1. Комплект выпуска — тем же публикатором, что и общая папка
Say "[1/4] Сборка, автотесты и комплектование..."
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$publisher = Join-Path $PSScriptRoot "Publish-EskdToolkit.ps1"
if (-not (Test-Path -LiteralPath $publisher)) { Stop-Release "Нет Publish-EskdToolkit.ps1 рядом: $publisher" }

# PyInstaller нужен, только если правили исходники окна настройки; собранное окно лежит в репозитории.
$hasPyInstaller = $false
if (Get-Command python -ErrorAction SilentlyContinue) {
    Invoke-Native { & python -c "import PyInstaller" 2>$null }
    $hasPyInstaller = ($LASTEXITCODE -eq 0)
}
if (-not $hasPyInstaller) { Info "PyInstaller не найден — окно настройки берётся из репозитория." }

$arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $publisher, "-Target", $staging)
if (-not $hasPyInstaller) { $arguments += "-SkipGuiBuild" }
if ($SkipTests) { $arguments += "-SkipTests" }
Invoke-Native { & powershell.exe @arguments }
if ($LASTEXITCODE -ne 0) { Stop-Release "Комплект не собран (код $LASTEXITCODE)." }

$releaseFile = Join-Path $staging "toolkit_release.json"
if (-not (Test-Path -LiteralPath $releaseFile)) { Stop-Release "Комплект собран, но toolkit_release.json не появился." }
$Version = "$(([System.IO.File]::ReadAllText($releaseFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json).version)"
if (-not $Version) { Stop-Release "В toolkit_release.json нет версии — выпуску нечем назваться." }

# Внутри архива одна папка верхнего уровня — иначе распаковка рассыпает файлы по текущему каталогу.
# Имя у неё короткое: Проводник при «Извлечь всё» добавляет ещё и свою папку по имени архива, а самый
# длинный путь внутри выпуска — 154 знака при потолке Windows в 260 (20.09.2026).
$tag   = "v$Version"
$name  = "ESKD-AUTO-$Version"
$inner = "ESKD-AUTO"
$zip   = Join-Path $OutDir "$name.zip"
$release = Join-Path $OutDir $inner
if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
Rename-Item -LiteralPath $staging -NewName $inner
$staging = $release
$releaseFile = Join-Path $staging "toolkit_release.json"
Ok "Комплект $Version (коммит $commit)"

# 2. Памятка в корень архива: что это и что с этим делать
Say "`n[2/4] Памятка «ЧИТАТЬ_ПЕРВЫМ.txt»..."
$readme = @"
ИНСТРУМЕНТАРИЙ ЕСКД — выпуск $Version (коммит $commit)

КАК РАЗВЕРНУТЬ

  1. Распакуйте эту папку куда угодно: на диск, в общую папку отдела, на флешку.
     Путь может быть любым — внутри ничего к нему не привязано.

     Одно ограничение не наше, а Windows: полный путь к файлу не длиннее 260 знаков.
     Внутри выпуска самый длинный путь — около 155 знаков, поэтому распаковывайте
     не слишком глубоко: C:\ESKD-AUTO или D:\Инструменты подойдут, а вот
     «Загрузки\Новая папка\Новая папка (2)» уже рискованно — часть файлов
     (шаблоны допусков, профили сортамента) просто не распакуется.

  2. Закройте SolidWorks.
  3. Запустите 01_Настройки_SolidWorks\Настройка_Рабочего_Места_SolidWorks.exe
     Окно само разложит шаблоны, форматки, библиотеки материалов, макросы SWPlus
     и надстройку ЕСКД, а затем проверит, что всё встало.

  Повторный запуск безопасен: окно переписывает только то, что отличается.

ЧЕГО В АРХИВЕ НЕТ

  - Установщика SWTools: у него свои выпуски и своя лицензия. Ставится отдельно.
  - Драйверов видеокарты, автотестов, архива разработки и результатов прогонов —
    они нужны только разработчику и в работе не участвуют.

ПРОВЕРИТЬ, ЧТО АРХИВ СКАЧАЛСЯ ЦЕЛИКОМ

  Рядом с архивом на странице выпуска лежит файл $name.zip.sha256.
  В PowerShell:

      Get-FileHash .\$name.zip -Algorithm SHA256

  Значение Hash должно совпасть со строкой в .sha256 (регистр не важен).

ЧТО ВНУТРИ

  toolkit_release.json — версия, коммит, дата и SHA-256 каждого файла выпуска.
  По нему окно настройки сверяет источник и развёрнутую копию.

  06_Документация — регламент, руководства и технические задания.
"@
[System.IO.File]::WriteAllText((Join-Path $staging "ЧИТАТЬ_ПЕРВЫМ.txt"), ($readme -replace "`r?`n", "`r`n"),
                               (New-Object System.Text.UTF8Encoding($true)))
Ok "Памятка добавлена"

# 3. Упаковка
Say "`n[3/4] Упаковка..."
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Имена внутри архива — в UTF-8 (кириллица в путях), папка верхнего уровня сохраняется,
# чтобы распаковка не рассыпала файлы по текущему каталогу.
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $staging, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true, [System.Text.Encoding]::UTF8)

$size = [Math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$zip.sha256", "$hash *$name.zip`r`n", (New-Object System.Text.UTF8Encoding($false)))
Ok "$name.zip — $size МБ"
Info "SHA-256: $hash"

# Потолок пути в Windows — 260 знаков. Если самый длинный файл выпуска в него не влезет, распаковка
# молча оборвётся на середине, и человек получит неполный комплект. Запас считается и называется вслух.
$deepest = (Get-ChildItem -LiteralPath $staging -Recurse -File |
            ForEach-Object { $_.FullName.Substring($staging.Length + 1) } |
            Sort-Object Length -Descending | Select-Object -First 1)
$budget = 259 - $deepest.Length - $inner.Length - 1
Info "Самый длинный путь внутри: $($deepest.Length) знаков — на папку распаковки остаётся $budget"
if ($budget -lt 60) { Write-Host "  [ВНИМАНИЕ] Запас по длине пути мал: распаковка в глубокую папку оборвётся." -ForegroundColor Yellow }

if (-not $Publish) {
    Say "`n[4/4] Публикация не запрошена."
    Write-Host "`nАрхив готов: $zip" -ForegroundColor Green
    Write-Host "Выложить на GitHub: тот же запуск с ключом -Publish" -ForegroundColor Gray
    exit 0
}

# 4. Публикация на странице Releases
Say "`n[4/4] Публикация на GitHub..."
$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    foreach ($candidate in @("$env:ProgramFiles\GitHub CLI\gh.exe", "${env:ProgramFiles(x86)}\GitHub CLI\gh.exe")) {
        if (Test-Path -LiteralPath $candidate) { $gh = $candidate; break }
    }
} else { $gh = $gh.Source }
if (-not $gh) {
    Stop-Release "Не найден gh (GitHub CLI). Поставьте https://cli.github.com и войдите один раз: gh auth login"
}

Invoke-Native { & $gh auth status 2>&1 | Out-Null }
if ($LASTEXITCODE -ne 0) { Stop-Release "gh не выполнил вход. Один раз: gh auth login" }

# Изменения с прошлого выпуска: метки выпусков называются v<версия>
$previous = @(Invoke-Native { & git -C $repo tag --list "v20*" --sort=-creatordate } | Where-Object { $_ -and $_ -ne $tag }) |
            Select-Object -First 1
if ($previous) {
    $changes = @(Invoke-Native { & git -C $repo log --oneline --no-decorate "$previous..$head" } | Where-Object { $_ })
    $intro = "Изменения с выпуска $previous"
} else {
    $changes = @(Invoke-Native { & git -C $repo log --oneline --no-decorate -20 $head } | Where-Object { $_ })
    $intro = "Последние изменения (первый выпуск архивом)"
}

$notes = @("**Коммит:** ``$commit`` (ветка ``$branch``)", "",
           "## Как развернуть", "",
           "1. Скачайте ``$name.zip`` ниже и распакуйте куда угодно.",
           "2. Закройте SolidWorks.",
           "3. Запустите ``01_Настройки_SolidWorks\Настройка_Рабочего_Места_SolidWorks.exe``.",
           "",
           "Целостность архива проверяется файлом ``$name.zip.sha256``:", "",
           '```', "$hash", '```', "",
           "Установщик SWTools в архив не входит — у него свои выпуски.", "",
           "## $intro", "") + ($changes | ForEach-Object { "- $_" })
$notesFile = Join-Path $OutDir "$name.notes.md"
[System.IO.File]::WriteAllText($notesFile, (($notes -join "`r`n") + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))

$ghArgs = @("release", "create", $tag, $zip, "$zip.sha256",
            "--title", "Инструментарий ЕСКД $Version",
            "--notes-file", $notesFile,
            "--target", $head)
if ($Draft) { $ghArgs += "--draft" }
Invoke-Native { & $gh @ghArgs }
if ($LASTEXITCODE -ne 0) { Stop-Release "gh не создал выпуск (код $LASTEXITCODE). Метка $tag уже занята?" }

Ok "Выпуск $tag выложен$(if ($Draft) { ' черновиком' })"
Invoke-Native { & $gh release view $tag --json url --jq .url } | ForEach-Object { Write-Host "`nСтраница выпуска: $_" -ForegroundColor White }
exit 0
