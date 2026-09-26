# -*- coding: utf-8 -*-
"""T0 — статические проверки репозитория без SolidWorks."""
import hashlib
import json
import os
import re
import subprocess
import unittest
from collections import Counter
from pathlib import Path

from PIL import Image

from eskd_e2e import paths
from eskd_e2e.testing import StaticTestCase, known_defect, tags

ROOT = paths.ROOT
ADDIN = paths.ADDIN_DIR
LEGACY_ALIASES = ("Разраб.", "Разработал", "п_Разраб", "DrawnBy", "DrawnDate", "Пров.", "п_Пров", "CheckedBy",
                  "Организация_ФБ", "\"Компания\"", "\"Firm\"", "\"Organization\"", "PartNo", "ГОСТ_Сортамент",
                  "ГОСТ_Материал")


def need_built_addin(case, reason):
    """Без собранной надстройки проверка пропускается; при публикации (ESKD_REQUIRE_BUILD=1) пропуск — провал,
    иначе «зелёный» прогон без трёх проверок выглядел бы полным (аудит 15.09.2026, D3)."""
    if os.environ.get("ESKD_REQUIRE_BUILD") == "1":
        case.fail(reason + " — публикация требует собранную надстройку")
    case.skipTest(reason)


def addin_sources():
    return [p for p in ADDIN.rglob("*.cs") if "Legacy" not in p.parts and "bin" not in p.parts]


def strip_csharp_literals(text):
    """Строки и символьные литералы заменяются пробелами той же длины; комментарии сохраняются (они — пояснение)."""
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        if text.startswith("//", i):
            i = text.find("\n", i) if text.find("\n", i) >= 0 else n
        elif text.startswith("/*", i):
            i = text.find("*/", i) + 2 if text.find("*/", i) >= 0 else n
        elif text.startswith('@"', i):
            j = i + 2
            while j < n and not (text[j] == '"' and not text.startswith('""', j)):
                j += 2 if text.startswith('""', j) else 1
            for k in range(i, min(j + 1, n)):
                if out[k] != "\n":
                    out[k] = " "
            i = j + 1
        elif text[i] in "\"'":
            quote, j = text[i], i + 1
            while j < n and text[j] != quote and text[j] != "\n":
                j += 2 if text[j] == "\\" else 1
            for k in range(i, min(j + 1, n)):
                out[k] = " "
            i = j + 1
        else:
            i += 1
    return "".join(out)


def matching_brace(text, open_index):
    depth = 0
    for i in range(open_index, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return i
    raise ValueError(f"нет парной скобки для позиции {open_index}")


class StaticRepository(StaticTestCase):

    @tags("smoke")
    def test_T0_powershell_scripts_with_cyrillic_have_bom(self):
        """T0 (PR №1, 15.09.2026): сценарии PowerShell с кириллицей — в UTF-8 с BOM. Windows PowerShell 5.1, которым
        запускают окно установки и батник, читает файл без BOM как ANSI: кириллица ломается, установщик не разбирается."""
        bad = []
        for script in list(ROOT.rglob("*.ps1")) + list(ROOT.rglob("*.psm1")):
            if any(part in (".git", "08_Результаты_Тестирования", "99_Архив") for part in script.parts):
                continue
            data = script.read_bytes()
            if any(b > 127 for b in data) and not data.startswith((b"\xef\xbb\xbf", b"\xff\xfe")):
                bad.append(str(script.relative_to(ROOT)))
        self.assertEqual([], bad, "сценарии с кириллицей без BOM")

    def test_T0_drew_blueprints_have_no_machine_paths(self):
        """T0 (PR №1): эталон профиля Drew — пути через %TOOLKIT% на форматки и шаблон инструментария, без путей ПК
        разработчика; каждая форматка из профиля существует; линии гибов на листе развёртки видны (решение по DXF)."""
        import xml.etree.ElementTree as ET
        path = ROOT / "03_Макросы_и_Плагины" / "Drw_System_Automation" / "Drew-Blueprints.xml"
        text = path.read_text(encoding="utf-8-sig")
        self.assertNotRegex(text, r"(?i)[a-z]:\\", "абсолютный путь в эталоне профиля Drew")
        root = ET.fromstring(text)
        paths_in_xml = [e.text for e in root.iter() if e.tag in ("Path", "FullPath") and e.text]
        self.assertTrue(paths_in_xml, "в профиле нет путей")
        missing = [p for p in paths_in_xml if not p.startswith("%TOOLKIT%\\") or not (ROOT / p[len("%TOOLKIT%\\"):]).exists()]
        self.assertEqual([], missing, "пути профиля Drew не на существующие файлы инструментария")
        self.assertEqual(["false"], [e.text for e in root.iter("HideBendLinesFlatPatternSheet")], "линии гибов скрыты")
        setup = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        self.assertIn('Replace("%TOOLKIT%", $toolkit)', setup, "установщик не подставляет папку инструментария")
        self.assertNotIn("$layout.DrwAutomation", setup, "поле раскладки, которого нет")

    def test_T0_update_from_github_publishes_only_checked_release(self):
        """T0 (замысел владельца 20.09.2026): обновление инструментария — одна кнопка, которая тянет репозиторий с
        GitHub и раздаёт его в общую папку. В цех попадает только то, что собралось и прошло автотесты; паролей и
        токенов скрипт не хранит; имя папки назначения не затирается служебной переменной (PowerShell не различает
        регистр — из-за этого выпуск однажды уехал мимо NAS)."""
        folder = ROOT / "01_Настройки_SolidWorks" / "_Служебное"
        script = folder / "Обновить_из_GitHub.ps1"
        self.assertTrue(script.is_file(), "нет скрипта обновления из GitHub")
        self.assertEqual(b"\xef\xbb\xbf", script.read_bytes()[:3],
                         "скрипт без BOM: Windows PowerShell 5.1 прочитает кириллицу как мусор")
        text = script.read_text(encoding="utf-8-sig")
        self.assertIn("Publish-EskdToolkit.ps1", text, "обновление не идёт через публикацию (сборка и автотесты)")
        self.assertIn('if ($SkipTests) { $arguments += "-SkipTests" }', text, "автотесты отключаются не только явным ключом")
        for secret in ("ghp_", "github_pat_", "-Password", "AccessToken", "PersonalAccessToken"):
            self.assertNotIn(secret, text, "в скрипте обновления хранится секрет: " + secret)
        shadow = [line for line in text.splitlines()
                  if re.match(r"\s*\$target\s*=", line, re.IGNORECASE) and "$Target = $Target.TrimEnd" not in line]
        self.assertEqual([], shadow, "служебная переменная затирает параметр -Target")
        # toolkit_release.json читается явным UTF-8: Get-Content без -Encoding в PowerShell 5.1 берёт
        # кодировку системы и молча портит кириллицу (20.09.2026).
        self.assertNotIn("Get-Content -LiteralPath $releaseFile -Raw | ConvertFrom-Json", text,
                         "выпуск читается без явной кодировки")
        launcher = folder / "Обновить_инструментарий_из_GitHub.cmd"
        self.assertTrue(launcher.is_file(), "нет ярлыка запуска обновления двойным щелчком")
        self.assertIn("Обновить_из_GitHub.ps1", launcher.read_text(encoding="utf-8"), "ярлык не запускает скрипт обновления")

    def test_T0_release_archive_matches_its_own_manifest(self):
        """T0 (решение владельца 20.09.2026): выпуск раздаётся архивом со страницы Releases в GitHub — скачал,
        распаковал куда угодно, запустил окно настройки. Архив собирается тем же публикатором, что и общая папка,
        поэтому разойтись они не могут; версию архив берёт из toolkit_release.json, а не ставит свою — иначе имя
        архива врёт о том, что внутри. Без автотестов на Releases ничего не уходит."""
        script = ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Собрать_архив_выпуска.ps1"
        self.assertTrue(script.is_file(), "нет сборщика архива выпуска")
        self.assertEqual(b"\xef\xbb\xbf", script.read_bytes()[:3],
                         "скрипт без BOM: Windows PowerShell 5.1 прочитает кириллицу как мусор")
        text = script.read_text(encoding="utf-8-sig")
        self.assertIn("Publish-EskdToolkit.ps1", text, "архив собирается мимо публикации (сборка и автотесты)")
        self.assertIn('if ($SkipTests) { $arguments += "-SkipTests" }', text, "автотесты отключаются не только явным ключом")
        # Версия — одна на архив, метку и toolkit_release.json внутри. Читается явным UTF-8: Get-Content без
        # -Encoding в PowerShell 5.1 берёт кодировку системы и превращает кириллицу в мусор молча.
        self.assertIn("$Version = \"$(([System.IO.File]::ReadAllText($releaseFile, [System.Text.Encoding]::UTF8) "
                      "| ConvertFrom-Json).version)\"", text, "версия архива не читается из toolkit_release.json в UTF-8")
        self.assertNotIn('$Version = (Get-Date)', text, "архив ставит свою отметку времени вместо версии выпуска")
        tag = re.search(r'\$tag\s*=\s*"v\$Version"', text)
        self.assertIsNotNone(tag, "метка выпуска строится не из версии")
        self.assertLess(text.index("$Version ="), tag.start(), "метка выпуска строится до версии")
        # Путь в Windows — не длиннее 260 знаков, внутри выпуска сидят пути под 155: имя папки в архиве
        # короткое, а остаток запаса называется вслух, иначе распаковка оборвётся на середине молча.
        self.assertIn('$inner = "ESKD-AUTO"', text, "папка внутри архива названа длинно — распаковка упрётся в предел пути")
        self.assertIn("$budget = 259", text, "запас по длине пути не считается")
        # Имена файлов в архиве кириллические: без UTF-8 они распакуются мусором.
        self.assertIn("[System.Text.Encoding]::UTF8", text, "имена внутри архива пакуются не в UTF-8")
        # Публикация — только по явному ключу, и только через gh: пароли и токены скрипт не хранит.
        self.assertIn("if (-not $Publish) {", text, "архив выкладывается на GitHub без явного ключа -Publish")
        for secret in ("ghp_", "github_pat_", "-Password", "AccessToken", "PersonalAccessToken"):
            self.assertNotIn(secret, text, "в сборщике архива хранится секрет: " + secret)
        # gh без -R ищет репозиторий в текущей папке, а не в папке скрипта: запуск из не-git папки обрывался на
        # «not a git repository», а сборщик сваливал это на занятую метку (выпуск 2026.09.24.1252, 24.09.2026).
        self.assertIn("git -C $repo remote get-url origin", text, "репозиторий для gh не берётся из origin копии")
        gh_release = [line.strip() for line in text.splitlines() if re.search(r'& \$gh release|"release", "create"', line)]
        self.assertTrue(gh_release, "вызовы gh release не найдены")
        self.assertEqual([], [line for line in gh_release if not re.search(r'-R"?,? \$ghRepo', line)],
                         "gh release зовётся без -R — зависит от текущей папки")
        self.assertNotIn("уже занята?", text, "отказ gh сваливается на занятую метку без проверки")
        self.assertLess(text.index("$existing = Get-ReleaseUrl"), text.index("& $gh @ghArgs"),
                        "занятость метки не проверяется до выкладки")

    def test_T0_graphics_settings_do_not_depend_on_developer_pc(self):
        """Замечание владельца 20.09.2026: на другом ПК SolidWorks не запускался после настройки. Аппаратный конвейер
        графики включается только на дискретной видеокарте (ключ -Graphics: Auto/Safe/Hardware), программный OpenGL
        остаётся запасным путём, а профиль реестра не несёт слепок видеокарты разработчика."""
        setup = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        self.assertIn('[ValidateSet("Auto", "Safe", "Hardware")][string]$Graphics', setup, "нет выбора режима графики")
        self.assertIn("$hardwareGraphics", setup, "конвейер включается без проверки видеокарты")
        pipeline = setup.index('"Use Performance Pipeline 2020" 1')
        self.assertLess(setup.index("$hardwareGraphics = switch"), pipeline, "конвейер включается до проверки видеокарты")
        # AllowList — база видеокарт SolidWorks (22.09.2026). Вырезанные ветки Gl2Shaders/NVIDIA Corporation оставляли GeForce
        # на программном OpenGL, своя маска в неполной базе роняла запуск. База не правится по частям: неполная (нет
        # Gl2Shaders) снимается целиком — SolidWorks запишет её заново; маски не пишутся.
        self.assertNotIn('Set-Reg "$U\\SolidWorks\\AllowList', setup, "установщик пишет маску AllowList")
        self.assertNotIn("CreateSubKey", setup, "установщик пишет маску AllowList через .NET")
        self.assertNotIn('"Workarounds"', setup, "установщик пишет маску обхода")
        self.assertNotIn('"$allowRoot\\Gl2Shaders"', setup, "установщик вырезает ветку базы видеокарт по частям")
        self.assertIn('-contains "Gl2Shaders"', setup, "нет проверки целостности базы видеокарт")
        self.assertIn('$cu.DeleteSubKeyTree($allowRoot, $false)', setup, "неполная база видеокарт не снимается целиком")
        # 21.09.2026: запомненные возможности OpenGL переживали сброс профиля — программный OpenGL «застревал» серым.
        forget = setup.index('-Name "Saved OGL Settings"')
        self.assertLess(pipeline, forget, "запомненный режим OpenGL снимается до настройки конвейера, а не после")
        self.assertNotIn('Set-Reg "$swRoot\\Performance" "Saved OGL Settings"', setup, "установщик пишет свой слепок OpenGL")
        gui = (ROOT / "01_Настройки_SolidWorks" / "_Исходники" / "CAD_Workstation_Configurator.py").read_text(encoding="utf-8")
        self.assertIn('"-Graphics", "Safe"', gui, "в окне настройки нет безопасной графики")
        self.assertTrue((ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Безопасная_графика_SolidWorks.ps1").is_file(),
                        "нет скорой помощи для ПК, где SolidWorks не стартует")
        reg = (ROOT / "01_Настройки_SolidWorks" / "Реестровые_Профили" / "01_SW2025_Корпоративный_Стандарт_ЕСКД.reg").read_bytes().decode("utf-16")
        for name in ("Saved OGL Settings", "OGL Display Shaders", "Use Performance Pipeline 2020", "Use GPU Silhouette Edges",
                     "Use Software OGL", "Software OGL Alarm", "Large Assembly Settings", "Open Documents On Startup"):
            with self.subTest(value=name):
                self.assertNotIn('"%s"=' % name, reg, "настройка конкретного ПК в корпоративном профиле")

    def test_T0_setup_loop_variables_do_not_shadow_parameters(self):
        """23.09.2026: шаг 8 «Отучение SolidWorks от сети» падал с ошибкой ValidateSet — цикл `foreach ($mode …)`
        писал в параметр сценария `$Mode` (у PowerShell имена переменных без учёта регистра, атрибут ValidateSet
        остаётся на переменной). Переменная цикла не должна совпадать с параметром сценария."""
        setup = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        start = re.search(r"(?im)^param\s*\(", setup).end()
        depth, i = 1, start
        while depth:
            depth += {"(": 1, ")": -1}.get(setup[i], 0)
            i += 1
        params = {m.lower() for m in re.findall(r"\$(\w+)\s*(?:=|,|\)|$)", setup[start:i - 1], re.M)}
        self.assertIn("mode", params, "не разобран блок параметров сценария")
        for loop_var in re.findall(r"(?i)foreach\s*\(\s*\$(\w+)\s+in\b", setup[i:]):
            with self.subTest(loop=loop_var):
                self.assertNotIn(loop_var.lower(), params, "переменная цикла совпадает с параметром сценария")

    def test_T0_export_paths_follow_order_structure(self):
        """ТЗ-03 ред. 8 §4.3, регламент §7.2: выгрузки из чертежа в папке 01_3D ложатся в папки изделия —
        PDF в 02_PDF (только из чертежей, без листов развёртки), DXF в 03_ЧПУ\\Лазер_Лист; линии сгиба в DXF не удаляются;
        резервные копии SolidWorks — в существующий каталог %TEMP%."""
        import xml.etree.ElementTree as ET
        path = ROOT / "03_Макросы_и_Плагины" / "Drw_System_Automation" / "Drew-Blueprints.xml"
        root = ET.fromstring(path.read_text(encoding="utf-8-sig"))
        presets = {}
        for e in root.iter("ExportSettings"):
            ft = e.findtext("FileType")
            if ft:
                presets.setdefault(ft, []).append(e)
        for pdf in presets["Pdf"]:
            self.assertEqual("<Directory>..\\02_PDF\\<Filename>", pdf.findtext("PathPattern"))
            self.assertEqual(("false", "true", "false"), (pdf.findtext("EnableForAssemblies"), pdf.findtext("EnableForDrawings"),
                                                          pdf.findtext("EnableForParts")), "PDF только из чертежей")
            self.assertEqual("true", pdf.findtext("SkipFlatPatternSheetsPdf"), "лист развёртки попадёт в PDF")
        for dxf in presets["Dxf"]:
            self.assertEqual("<Directory>..\\03_ЧПУ\\Лазер_Лист\\<Filename>", dxf.findtext("PathPattern"))
        reg = (ROOT / "01_Настройки_SolidWorks" / "Реестровые_Профили" / "01_SW2025_Корпоративный_Стандарт_ЕСКД.reg").read_bytes().decode("utf-16")
        self.assertIn('"DXF/DWG Remove Bend Lines For FlatpatternToDXF"=dword:00000000', reg, "линии сгиба удаляются из DXF")
        self.assertNotIn("TempSW", reg, "опечатка в пути резервных копий")
        tt = (ROOT / "03_Макросы_и_Плагины" / "Макросы_SW_ZTool" / "SWPlusMacro_v_2018_SP0.0" / "ТТ" / "apply_tt_profile.py").read_text(encoding="utf-8")
        self.assertNotRegex(tt, r"(?i)[a-z]:\\\\?Work", "зашитый путь ПК разработчика в apply_tt_profile.py")

    @tags("smoke")
    def test_T0_installer_batch_is_ascii_and_finds_setup(self):
        """T0: УСТАНОВИТЬ_ЕСКД.bat — только ASCII, путь к Setup находится маской (Д-23), права администратора не
        запрашиваются: у повышенного процесса нет подключённого сетевого диска и HKCU — другого пользователя."""
        data = (ROOT / "УСТАНОВИТЬ_ЕСКД.bat").read_bytes()
        self.assertTrue(all(b < 128 for b in data), "в батнике есть не-ASCII символы")
        self.assertIn(b"01_*", data)
        self.assertNotRegex(data.decode("ascii"), r"(?i)runas|net session", "батник повышает права")
        # Движок лежит в подпапке 01_*\_Служебное — батник ищет его и там: в 01_* у конструктора на виду
        # остаётся только окно настройки, а имя подпапки кириллическое и в ASCII-батник его не вписать.
        self.assertTrue(list(ROOT.glob("01_*/Setup_Workstation_SolidWorks.ps1")) +
                        list(ROOT.glob("01_*/*/Setup_Workstation_SolidWorks.ps1")), "Setup не найден по маске 01_*")
        self.assertIn(b'for /d %%S in ("%%~fD\\*")', data, "батник не ищет движок в подпапке 01_*")

    def test_T0_deploy_from_readonly_toolkit_folder(self):
        """T0 (сетевая схема, решение владельца 14.09.2026): установка из папки инструментария «только чтение» с
        непривычным именем — источник не меняется, пути SolidWorks на источник, макросы и надстройка — в локальную копию,
        Master.ini на основные надписи источника, повторная установка сохраняет настройки макросов. Временный раздел
        реестра и временные папки; настоящий реестр не затрагивается."""
        if not paths.ADDIN_DLL.exists():
            need_built_addin(self, "нет собранной DLL")
        out = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                              str(paths.TESTS / "tools" / "check_deploy_engine.ps1"), "-RepoRoot", str(ROOT)],
                             capture_output=True, timeout=600)
        lines = [ln for ln in out.stdout.decode("utf-8", errors="replace").splitlines() if ln.startswith("{")]
        self.assertTrue(lines, out.stdout.decode("cp866", errors="replace") + out.stderr.decode("cp866", errors="replace"))
        result = json.loads(lines[-1])
        self.assertEqual([], result["problems"], result.get("output", ""))
        self.assertTrue(result["sandboxRemoved"], "временный раздел реестра не удалён")

    def test_T0_deploy_scripts_write_nothing_into_toolkit(self):
        """T0: установщик не пишет в папку инструментария и не собирает надстройку; окно настройки — только окно над
        установщиком (реестр и файлы не пишет, личных умолчаний и зашитых путей нет); публикация исключает папки
        разработки и не зеркалит в чужую папку."""
        setup_dir = ROOT / "01_Настройки_SolidWorks" / "_Служебное"
        setup = (setup_dir / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        module = (setup_dir / "EskdDeploy.psm1").read_text(encoding="utf-8-sig")
        self.assertNotIn("build.ps1", setup, "установщик собирает надстройку — сборку кладёт в источник публикация")
        self.assertNotRegex(setup + module, r"(?i)[a-z]:\\+work\\+", "зашитый путь D:\\Work")
        self.assertNotIn("Лунин", setup + module)
        self.assertNotRegex(setup, r"Join-Path \$SourceRoot[^\n]*\n[^\n]*(WriteAllText|Set-Content|Out-File)", "запись в источник")
        self.assertIn("GetTempPath()", setup, "временный .reg — во временной папке пользователя")

        import ast
        configurator_path = setup_dir.parent / "_Исходники" / "CAD_Workstation_Configurator.py"
        text = configurator_path.read_text(encoding="utf-8")
        tree = ast.parse(text)
        calls = {node.func.attr if isinstance(node.func, ast.Attribute) else getattr(node.func, "id", "")
                 for node in ast.walk(tree) if isinstance(node, ast.Call)}
        self.assertEqual(set(), calls & {"SetValueEx", "CreateKey", "DeleteKey", "DeleteValue", "copy", "copy2", "copyfile",
                                         "rmtree", "remove", "makedirs"}, "окно пишет реестр или файлы")
        for node in ast.walk(tree):
            if isinstance(node, ast.Call) and getattr(node.func, "id", "") == "open" and len(node.args) > 1:
                self.assertEqual("r", getattr(node.args[1], "value", "r"), "окно открывает файл на запись")
        self.assertNotRegex(text, r"(?i)taskkill|reg import|[a-z]:\\+work|Лунин|\"123\"", "окно: принудительное закрытие, "
                                                                                        "импорт реестра, зашитые путь или фамилия")
        self.assertIn("Setup_Workstation_SolidWorks.ps1", text)

        import importlib.util
        import tempfile
        spec = importlib.util.spec_from_file_location("configurator", configurator_path)
        configurator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(configurator)
        self.assertEqual(str(ROOT), configurator.find_source_root(str(setup_dir)))
        with tempfile.TemporaryDirectory() as foreign:
            self.assertIsNone(configurator.find_source_root(foreign), "запасной путь к инструментарию")
        self.assertEqual([], configurator.read_firms(str(ROOT)), "общий список организаций должен быть пуст")
        self.assertEqual([], configurator.read_families(str(ROOT)), "общий список фамилий должен быть пуст")
        with tempfile.TemporaryDirectory() as filled:
            mprop_dir = Path(filled) / configurator.SWPLUS / "MProp"
            mprop_dir.mkdir(parents=True)
            (mprop_dir / "MProp_Firm.txt").write_bytes("ТОО «Троя»\r\nТР\r\nАО Завод\r\n\r\n".encode("cp1251"))
            self.assertEqual(["ТОО «Троя»", "АО Завод"], configurator.read_firms(filled), "пары «организация / код»")
        cmd = configurator.build_command("S.ps1", "Иванов И.И.", "", "Graceful")
        for flag in ("-NonInteractive", "-Utf8Output", "-CloseMode", "Graceful", "-Author", "Иванов И.И."):
            self.assertIn(flag, cmd)
        self.assertNotIn("-Firm", cmd, "пустая организация не передаётся")
        self.assertEqual(["ok", "error", "warn", "info"],
                         [configurator.line_level(s) for s in ("  [OK] x", "  [ОШИБКА] x", "  [ВНИМАНИЕ] x", "  [ИНФО] x")])

        publish = (setup_dir / "Publish-EskdToolkit.ps1").read_text(encoding="utf-8-sig")
        for excluded in ('".git"', '"08_Результаты_Тестирования"', '"09_Тесты"', '"99_Архив"', '"Backups"', '"_VBA_выгрузка"'):
            self.assertIn(excluded, publish, f"публикация копирует {excluded}")
        self.assertIn("build.ps1", publish, "надстройку собирает публикация")
        self.assertIn("Test-EskdSourceRoot -Path $Target", publish, "зеркало в чужую папку")
        spec_text = (setup_dir.parent / "_Исходники" / "Настройка_Рабочего_Места_SolidWorks.spec").read_text(encoding="utf-8")
        self.assertNotIn("uac_admin", spec_text, "окно запрашивает права администратора")

    def test_T0_drew_installed_with_builtin_license(self):
        """T0 (решение владельца 15.09.2026): Drew — Gov-издание со ВСТРОЕННОЙ лицензией (AUTO):
        в инструментарии один установщик AUTO.exe без MSI/комплекта/активации; движок ставит его
        только при закрытом SolidWorks, ждёт завершения по контрольному хэшу сборки и не требует
        активации; окно настройки предлагает чекбоксы Drew/отучение от сети/русский интерфейс."""
        drew = ROOT / "03_Макросы_и_Плагины" / "Drw_System_Automation"
        auto = list(drew.glob("УСТАНОВЩИК_Drew_*AUTO.exe"))
        self.assertTrue(auto, "нет AUTO-установщика Drew в Drw_System_Automation")
        for absent in ("Drew_4.3.0.0.msi", "install-all.ps1", "2_комплект_издания", "3_активация", "УСТАНОВИТЬ_DREW.cmd"):
            self.assertFalse((drew / absent).exists(), f"артефакт классического издания не должен входить: {absent}")
        self.assertTrue((drew / "1_УСТАНОВКА.txt").exists(), "нет инструкции 1_УСТАНОВКА.txt")
        setup = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        # замер 24.09.2026: установщик без слёта лицензии (SHA-256 6BD50418…), чистая установка на ПК владельца
        self.assertIn("0D31E06D6AC7F6F560745E8797BF09004BAC6FC576C937E36072AAC88831F365", setup,
                      "движок сверяет сборку Drew по контрольному хэшу")
        # 26.09.2026: установщик оставляет файлам Drew даты сборки (и создания), а AUTO.exe всегда выходит с кодом 0 —
        # установка засчитывается только по появлению сборки лицензии или записи Drew в списке программ
        self.assertNotIn("$licItem.CreationTime -ge $startedAt", setup, "дата файла Drew не показывает свежую установку")
        self.assertNotIn("$setup.ExitCode -eq 0", setup, "код выхода AUTO.exe всегда 0 — не признак установки")
        self.assertIn("$newFiles = -not $licLeft -and (Test-Path -LiteralPath $licDll)", setup, "появление сборки лицензии — признак установки")
        self.assertIn("if ($newFiles -and $licAfter -eq $licHash) {", setup, "прежние файлы Drew не засчитываются как установка")
        # одна запись Drew из скриптблока приходит без массива, а у одиночного объекта в PowerShell 5.1 нет .Count
        self.assertIn("$entryLeft = @(& $findDrewEntries).Count -gt 0", setup, "запись Drew в списке программ не видна")
        self.assertNotRegex(setup, r"[^@]\(& \$findDrewEntries\)\.Count", "счёт записей Drew без @() в PowerShell 5.1 пуст")
        self.assertIn("AddMinutes(6)", setup, "движок ждёт завершения установщика Drew с таймаутом")
        self.assertIn("Лицензия Drew: встроенная", setup, "активация больше не требуется — сообщается прямо")
        # решение владельца 15.09.2026: Drew другой сборки той же версии удаляется штатно и ставится заново —
        # иначе установщик Windows только «перенастраивает» продукт и прежние файлы остаются
        self.assertRegex(setup, r'Start-Process -FilePath "msiexec\.exe" -ArgumentList "/x", \$old\.Code, "/qn", "/norestart" -Verb RunAs',
                         "прежняя сборка Drew удаляется msiexec /x с запросом прав")
        self.assertIn("Новая сборка не ставится", setup, "при отказе в правах новая сборка не ставится поверх старой")
        self.assertLess(setup.index("msiexec.exe"), setup.index("$drewLocalExe -WorkingDirectory"), "удаление — до установки")
        # аудит 15.09.2026 B1, B2: сбой запуска установщика не ждёт 6 минут; установленный этим же установщиком Drew
        # не переустанавливается при каждом обновлении из-за расхождения хэша
        self.assertIn("-PassThru -ErrorAction Stop", setup, "сбой запуска установщика Drew перехватывается сразу")
        self.assertIn('Set-Reg $drewInstallKey "DrewInstaller" $autoHash', setup, "отпечаток установщика Drew не запоминается")
        # решение владельца 26.09.2026: сменился установщик в инструментарии — Drew переставляется один раз, даже если
        # сборка лицензии та же (правило — Get-EskdDrewPlan, случаи — check_deploy_engine.ps1)
        module = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "EskdDeploy.psm1").read_text(encoding="utf-8-sig")
        plan = module[module.index("function Get-EskdDrewPlan"):]
        plan = plan[:plan.index("\n}")]
        self.assertIn('if ($RecordedAuto -eq $AutoHash -or $MachineAuto) { return "Keep" }', plan,
                      "Drew от того же установщика (у этой или другой учётной записи ПК) не признаётся установленным")
        for outcome in ("Update", "Replace"):
            self.assertIn(f'return "{outcome}"', plan, f"правило Drew без исхода {outcome}")
        self.assertNotIn("Since", plan, "даты файлов не показывают, каким установщиком поставлен Drew")
        self.assertIn("$drewPlan = Get-EskdDrewPlan", setup, "шаг Drew не спрашивает правило")
        # план → действие: обновление не считается готовым Drew; метка установки — на весь ПК
        self.assertIn('$drewOk = @("Keep", "Unverified") -contains $drewPlan', setup, "рабочий Drew от прежнего установщика не переставляется")
        self.assertIn(r'Join-Path $env:ProgramData "ESKD\DrewInstaller"', setup, "метка установки Drew не на весь ПК")
        self.assertIn('if ($drewPlan -eq "Update" -and $licLeft) {', setup, "сбой запуска — предупреждение только при нетронутом рабочем Drew")
        self.assertIn('elseif ($setup.HasExited -and $drewPlan -eq "Update" -and $licLeft -and (Test-Path -LiteralPath $licDll)) {', setup,
                      "закрытое без установки окно при нетронутом рабочем Drew — предупреждение")
        self.assertLess(setup.index("Copy-Item -LiteralPath $drewExe[0].FullName"), setup.index("msiexec.exe"),
                        "установщик копируется до удаления рабочего Drew")
        self.assertIn("Drew не обновлён: запрос прав администратора отклонён", setup,
                      "отказ в правах при обновлении рабочего Drew — предупреждение, а не ошибка настройки")
        self.assertIn("$keepDrewInstaller = Get-RegValue $install \"DrewInstaller\"", setup, "-Mode Uninstall стирает отпечаток Drew")
        self.assertNotIn("-Silent -NoActivate", setup, "старый вызов классического установщика убран")
        self.assertNotIn("Drew не активирован", setup)
        self.assertNotIn("Activation.code", setup)
        self.assertIn("[switch]$SwInternetBlock", setup, "опция отучения от сети объявлена")
        self.assertIn("[switch]$DrewRussian", setup, "опция русского интерфейса объявлена")
        block = ROOT / "01_Настройки_SolidWorks" / "SwInternetBlock"
        self.assertTrue((block / "Set-SwInternetBlock.ps1").exists() and (block / "SWInternetBlock.manifest.json").exists(),
                        "нет пакета SwInternetBlock рядом с движком")
        configurator = (ROOT / "01_Настройки_SolidWorks" / "_Исходники" / "CAD_Workstation_Configurator.py").read_text(encoding="utf-8")
        for needed in ("Gov-издание (лицензия встроена", "Отучение SolidWorks от сети", "Язык интерфейса SolidWorks и Drew",
                       '"-SkipDrew"', '"-SwInternetBlock"', '"-Language"'):
            self.assertIn(needed, configurator, f"в окне настройки нет: {needed}")
        for gone in ("Активация Drew", "Client-Activate-Drew.ps1", "drew_needs_activation"):
            self.assertNotIn(gone, configurator, f"артефакт активации не должен остаться в окне: {gone}")

    def test_T0_language_choice_at_deploy(self):
        """T0 (решение владельца 25.09.2026): при развёртывании выбирается язык интерфейса SolidWorks и Drew.
        SolidWorks 2025 берёт язык из регионального формата пользователя (sldutu.dll LangUtils::GetLangSubdir:
        «Use English language» = 1 — английский, иначе основной язык формата 0x19 при DLL в lang/russian — русский).
        Установщик: ключ -Language Russian|English (прежний выбор, иначе русский; -DrewRussian — синоним), значение
        «Use English language» пишется ПОСЛЕ профиля .reg, формат пользователя — Set-Culture ru-RU (без прав
        администратора) только при установленном русском пакете; системную локаль, язык Windows, «Расположение»
        и папки lang не трогает — кодовую страницу для макросов SWPlus только проверяет."""
        service = ROOT / "01_Настройки_SolidWorks" / "_Служебное"
        setup = (service / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        module = (service / "EskdDeploy.psm1").read_text(encoding="utf-8-sig")
        self.assertIn('[ValidateSet("Russian", "English", "")][string]$Language', setup, "ключ -Language")
        self.assertIn('if (-not $Language -and $DrewRussian) { $Language = "Russian" }', setup, "-DrewRussian — синоним")
        self.assertIn('Get-RegValue $install "Language"', setup, "по умолчанию — прежний выбор ПК")
        self.assertIn('Set-Reg $install "Language" $Language', setup, "выбор запоминается в ESKD_Install")
        write = setup.index(r'Set-Reg "$swRoot\General" "Use English language" $plan.UseEnglish "DWord"')
        self.assertLess(setup.index("reg.exe import"), write, "язык пишется после импорта профиля .reg — иначе .reg его перекроет")
        for needed in ("Set-Culture -CultureInfo ru-RU", r'lang\russian', '"SolidWorks Folder"', r"Nls\CodePage",
                       "[Environment]::SetEnvironmentVariable('DREW_LANG', $plan.DrewLang, 'User')"):
            self.assertIn(needed, setup, f"в установщике нет: {needed}")
        for forbidden in ("Set-WinSystemLocale", "Set-WinUILanguageOverride", "Set-WinHomeLocation", "Set-WinUserLanguageList",
                          r"Control Panel\International"):
            self.assertNotIn(forbidden, setup, f"установщик не должен менять: {forbidden}")
        self.assertIn("function Get-EskdLanguagePlan", module)
        self.assertIn("-band 0x3FF", module, "основной язык формата — младшие 10 бит LCID")
        self.assertIn("-eq 0x19", module, "русский — основной язык 0x19")
        reg = (ROOT / "01_Настройки_SolidWorks" / "Реестровые_Профили" / "01_SW2025_Корпоративный_Стандарт_ЕСКД.reg").read_text(encoding="utf-16")
        self.assertIn('"Use English language feature names"=dword:00000000', reg, "имена элементов — по стандарту отдела")
        import importlib.util
        spec = importlib.util.spec_from_file_location("configurator", ROOT / "01_Настройки_SolidWorks" / "_Исходники" / "CAD_Workstation_Configurator.py")
        configurator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(configurator)
        cmd = configurator.build_command("S.ps1", "И", "", "Skip")
        self.assertEqual(["-Language", "Russian"], cmd[cmd.index("-Language"):cmd.index("-Language") + 2], "по умолчанию русский")
        cmd = configurator.build_command("S.ps1", "И", "", "Skip", language="English")
        self.assertEqual(["-Language", "English"], cmd[cmd.index("-Language"):cmd.index("-Language") + 2])
        self.assertNotIn("-DrewRussian", cmd, "прежний ключ окно не передаёт")
        window = (ROOT / "01_Настройки_SolidWorks" / "_Исходники" / "CAD_Workstation_Configurator.py").read_text(encoding="utf-8")
        self.assertIn('"LastResult", "Language")', window, "окно читает прежний выбор языка из ESKD_Install")
        guide = (ROOT / "06_Документация" / "РУКОВОДСТВО_ПОЛЬЗОВАТЕЛЯ_И_АДМИНИСТРАТОРА.md").read_text(encoding="utf-8")
        self.assertIn("`-Language`", guide, "ключ -Language описан в руководстве")

    def test_T0_language_not_switched_is_not_green(self):
        """T0 (замечание владельца 25.09.2026: «выбор языка установки не работает»): шаг [9/9] писал [ВНИМАНИЕ] в середине
        журнала, а итог — зелёное «Готово», код 0. Теперь невключённый русский интерфейс — код 4 и жёлтый итог с причиной;
        русский язык SolidWorks — lang\\russian\\sldresu.dll той же версии, что SLDWORKS.exe (прежде — любая DLL)."""
        service = ROOT / "01_Настройки_SolidWorks" / "_Служебное"
        setup = (service / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        step9 = setup[setup.index("[9/9] Язык интерфейса"):setup.index('Set-Reg $install "SourceRoot"')]
        self.assertIn('Join-Path $ruLangDir "sldresu.dll"', step9, "русский язык — главная библиотека ресурсов")
        self.assertIn("FileMajorPart", step9, "версия русского языка сверяется с SLDWORKS.exe")
        self.assertNotIn('-Filter "*.dll"', step9, "любая DLL в lang\\russian больше не считается русским языком")
        self.assertIn("$ruPack = $swFound -and", step9, "без SLDWORKS.exe русский язык не считается установленным")
        self.assertIn("} elseif (-not $swFound) {", step9, "папка без SLDWORKS.exe — «SolidWorks не найден»")
        # Каждая ветка, после которой SolidWorks останется английским, называет причину.
        for branch in ('$languageIssue = "$SwVersion не найден на этом ПК"', "$languageIssue = $ruProblem", "только после выхода из учётной записи",
                       "не переключён на «Русский (Россия)»"):
            self.assertIn(branch, step9, f"ветка без причины: {branch}")
        self.assertEqual(4, step9.count("$languageIssue = "), "четыре ветки, после которых интерфейс останется английским")
        self.assertIn('Set-Reg $install "LanguageIssue" $languageIssue', setup, "причина — в ESKD_Install")
        tail = setup[setup.rindex("if ($failures) {"):]
        self.assertLess(tail.index("exit 1"), tail.index("if ($languageIssue)"), "ошибки важнее языка")
        self.assertIn("exit 4", tail[tail.index("if ($languageIssue)"):tail.index("exit 0")], "невключённый язык — код 4")
        self.assertIn("ОСТАНЕТСЯ АНГЛИЙСКИМ", tail, "итог называет, что не так")
        import importlib.util
        spec = importlib.util.spec_from_file_location("configurator", ROOT / "01_Настройки_SolidWorks" / "_Исходники" / "CAD_Workstation_Configurator.py")
        configurator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(configurator)
        self.assertIn("английским", configurator.EXIT_MESSAGES[4], "окно: код 4 — не «Готово»")
        self.assertEqual({0: "ok", 4: "warn"}, configurator.EXIT_TONES, "код 4 — жёлтым, остальные ненулевые — красным")
        self.assertEqual("warn", configurator.line_level(" НАСТРОЙКА ЗАВЕРШЕНА, НО SOLIDWORKS ОСТАНЕТСЯ АНГЛИЙСКИМ:"))
        window = (ROOT / "01_Настройки_SolidWorks" / "_Исходники" / "CAD_Workstation_Configurator.py").read_text(encoding="utf-8")
        self.assertIn("EXIT_TONES.get(value", window, "цвет итога — по коду выхода")
        self.assertIn("Изменить → Языки → Русский", window, "подпись в окне: русский язык ставится в самом SolidWorks")
        bat = (ROOT / "УСТАНОВИТЬ_ЕСКД.bat").read_bytes()
        self.assertTrue(all(c < 128 for c in bat), "УСТАНОВИТЬ_ЕСКД.bat — только ASCII")
        self.assertIn(b"if %RC% equ 4", bat, "консольный запуск тоже различает код 4")
        guide = (ROOT / "06_Документация" / "РУКОВОДСТВО_ПОЛЬЗОВАТЕЛЯ_И_АДМИНИСТРАТОРА.md").read_text(encoding="utf-8")
        self.assertIn("4 — всё настроено, но выбранный русский интерфейс не включится", guide, "код 4 в руководстве")
        self.assertIn("`LanguageIssue`", guide)

    def test_T0_setup_refuses_foreign_account(self):
        """T0 (разбор 25.09.2026): установщик, запущенный «от имени администратора» под другой учётной записью, писал
        профиль SolidWorks, язык и формат Windows в её HKCU — у конструктора SolidWorks оставался прежним. Теперь отказ
        до изменений, как у register_eskd.ps1."""
        setup = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        check = setup.index("Get-EskdForeignAccountMessage -Action Setup")
        self.assertLess(check, setup.index('Write-Step "[1/9]'), "проверка учётки до первого шага")
        guard = setup[setup.index("$failures = 0"):setup.index('Write-Step "[1/9]')]
        self.assertLess(guard.index('. (Join-Path $layout.SourceAddin "Register-EskdAddin.ps1")'), guard.index("-Action Setup"), "модуль подключён до проверки")
        self.assertIn("exit 2", guard[guard.index("} catch {"):], "модуль не прочитался — отказ, а не установка без проверки")
        self.assertIn("exit 1", setup[check:setup.index('Write-Step "[1/9]')], "отказ — код 1")
        out = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                              str(paths.TESTS / "tools" / "check_registration_account.ps1"),
                              "-ModulePath", str(ADDIN / "Register-EskdAddin.ps1")], capture_output=True, timeout=120)
        lines = [ln for ln in out.stdout.decode("utf-8", errors="replace").splitlines() if ln.startswith("{")]
        self.assertTrue(lines, out.stdout.decode("utf-8", errors="replace") + out.stderr.decode("cp866", errors="replace"))
        result = json.loads(lines[-1])
        self.assertIn("Настройка запущена от имени", result["foreign_setup"] or "", "текст отказа — об установке")
        self.assertIn("Настройка не выполнялась", result["foreign_setup"] or "")
        self.assertFalse(result["same_setup"], "своя учётка — без отказа")

    def test_T0_mprop_firm_is_pairs(self):
        """T0: общие списки MProp пусты — фамилию и организацию каждый вписывает при установке (решение владельца
        15.09.2026); если в MProp_Firm.txt что-то есть, это пары «организация / код» с непустыми именами (Д-25)."""
        mprop = paths.SWPLUS / "MProp"
        for name in ("MProp_Fam.txt", "MProp_Firm.txt"):
            self.assertEqual(b"", (mprop / name).read_bytes().strip(), f"общий список {name} не пуст: чужие фамилии и организации уйдут всем")
        lines = (mprop / "MProp_Firm.txt").read_bytes().decode("cp1251").split("\r\n")
        if lines and lines[-1] == "":
            lines = lines[:-1]
        self.assertEqual(0, len(lines) % 2, f"нечётное число строк: {lines}")
        self.assertTrue(all(lines[i].strip() for i in range(0, len(lines), 2)), f"пустое имя организации: {lines}")

    @tags("smoke")
    def test_T0_dictionary_has_43_names_and_flags(self):
        """T0: словарь SWPlus читается: 43 имени, разбор имени файла включён, разделитель — пробел; строки 51/53 — «Операции» и
        «Ревизия» (ТЗ-02 Т-15, Т-17)."""
        lines = paths.SWPLUS_DICTIONARY.read_bytes().decode("cp1251").split("\r\n")
        self.assertGreaterEqual(len(lines), 50)
        self.assertEqual("Обозначение", lines[0])
        self.assertEqual("Количество", lines[42])
        self.assertEqual("1", lines[47].strip(), "prpFileName")
        self.assertEqual(" ", lines[48], "prpNameSep")
        self.assertEqual(["Операции", "0", "Ревизия", "0"], lines[50:54], "доп. свойства 1 и 2 — общие (ТЗ-02 Т-15)")

    @tags("smoke")
    def test_T0_addin_code_has_no_legacy_alias_writes(self):
        """T0: алиасы v5 упоминаются только в словаре и в ветке флага LegacyAliases (Д-07)."""
        offenders = {}
        # Запись свойства — это вызов PropertyWriter.Set/SetIfEmpty/Delete или Add3 с именем-литералом.
        write_call = re.compile(r'(?:Set|SetIfEmpty|Delete|Add3|Delete2)\(\s*[^;]*?"([^"]+)"')
        aliases = {a.strip('"') for a in LEGACY_ALIASES}
        for src in addin_sources():
            if src.name in ("PropertyDictionary.cs", "SyncService.cs"):
                continue
            text = src.read_text(encoding="utf-8", errors="replace")
            hits = sorted({name for name in write_call.findall(text) if name in aliases})
            if hits:
                offenders[src.name] = hits
        self.assertEqual({}, offenders)
        sync = (ADDIN / "Sw" / "SyncService.cs").read_text(encoding="utf-8")
        block = sync[sync.index("if (settings.LegacyAliases)"):]
        block_end = block.index("private static bool Empty")
        outside = sync.replace(block[:block_end], "")
        self.assertEqual([], [a for a in LEGACY_ALIASES if a in outside], "алиасы вне ветки LegacyAliases")

    @tags("smoke")
    def test_T0_command_ids_match_swcommands_enum(self):
        """T0: идентификаторы команд в коде совпадают с swCommands_e интеропа SolidWorks (Д-05)."""
        interop = paths.SW_INTEROP_DIR / "SolidWorks.Interop.swcommands.dll"
        if not interop.exists():
            self.skipTest("нет SolidWorks.Interop.swcommands.dll")
        ps = ("$a=[Reflection.Assembly]::LoadFile('%s'); $e=$a.GetType('SolidWorks.Interop.swcommands.swCommands_e'); "
              "$h=@{}; foreach($n in [Enum]::GetNames($e)){ $h[$n]=[int][Enum]::Parse($e,$n) }; $h | ConvertTo-Json -Compress") % interop
        out = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, timeout=120)
        enum = json.loads(out.stdout.decode("utf-8", errors="replace"))
        pattern = re.compile(r"=\s*(\d+);\s*//\s*swCommands_e\.(\w+)")
        found = 0
        for src in addin_sources():
            for value, name in pattern.findall(src.read_text(encoding="utf-8", errors="replace")):
                found += 1
                self.assertIn(name, enum, f"{src.name}: нет {name} в swCommands_e")
                self.assertEqual(enum[name], int(value), f"{src.name}: {name}")
        self.assertGreater(found, 0, "в коде нет ни одной константы команды с комментарием swCommands_e")

    def test_T0_icon_strips_match_command_buttons(self):
        """T0: в полосах иконок столько изображений, сколько кнопок на вкладке (Д-25)."""
        text = (ADDIN / "SwAddin.cs").read_text(encoding="utf-8")
        buttons = len(re.findall(r'AddCommandItem2\("[^"]+"', text))
        small = Image.open(ADDIN / "Icons" / "icons_small.bmp").size
        large = Image.open(ADDIN / "Icons" / "icons_large.bmp").size
        self.assertEqual(buttons, small[0] // small[1], "icons_small.bmp")
        self.assertEqual(buttons, large[0] // large[1], "icons_large.bmp")

    def test_T0_single_registration_module(self):
        """T0: COM-регистрацию надстройки пишет только Register-EskdAddin.ps1; RegAsm и жёстких путей D:\\Work нет (Д-28)."""
        scripts = [p for p in ROOT.rglob("*") if p.suffix.lower() in (".ps1", ".py", ".cmd", ".bat") and p.is_file()
                   and not any(part in p.parts for part in ("99_Архив", "08_Результаты_Тестирования", "Drw_System_Automation", "bin", "09_Тесты"))]
        module = ADDIN / "Register-EskdAddin.ps1"
        writers, regasm = [], []
        for script in scripts:
            text = script.read_bytes().decode("utf-8", errors="replace")
            if "ESKD_Material_Sync_v5, Version=" in text and script != module:
                writers.append(str(script.relative_to(ROOT)))
            if re.search(r"RegAsm\.exe", text, re.I) and "ESKD_Material_Sync" in text:
                regasm.append(str(script.relative_to(ROOT)))
        self.assertEqual([], writers, "COM-регистрация надстройки вне модуля")
        self.assertEqual([], regasm, "RegAsm портит CodeBase с кириллицей (DEP-11)")
        wrappers = {
            ADDIN / "register_eskd.ps1": "Register-EskdAddin.ps1",
            ADDIN / "unregister.ps1": "Register-EskdAddin.ps1",
            ADDIN / "build_and_register.ps1": "register_eskd.ps1",
            ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1": "Register-EskdAddin.ps1",
            ROOT / "01_Настройки_SolidWorks" / "_Исходники" / "CAD_Workstation_Configurator.py": "Setup_Workstation_SolidWorks.ps1",
        }
        for path, reference in wrappers.items():
            with self.subTest(script=path.name):
                text = path.read_bytes().decode("utf-8-sig")
                self.assertIn(reference, text)
                if path.parent == ADDIN:
                    self.assertNotRegex(text, r"(?i)[a-z]:\\+work\\+", "жёсткий путь к репозиторию")
        setup = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        self.assertNotIn("build_and_register.ps1", setup, "Setup должен собирать build.ps1")
        cmd = (ADDIN / "Регистрация_ЕСКД_на_этом_компьютере.cmd").read_bytes()
        self.assertTrue(all(b < 128 for b in cmd), "в .cmd не-ASCII символы")

    def test_T0_registration_module_in_sandbox(self):
        """T0: модуль регистрации во временном разделе HKCU пишет полную регистрацию с сырым CodeBase и снимает её (Д-28)."""
        if not paths.ADDIN_DLL.exists():
            need_built_addin(self, "нет собранной DLL")
        out = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                              str(paths.TESTS / "tools" / "check_registration_module.ps1"),
                              "-ModulePath", str(ADDIN / "Register-EskdAddin.ps1"), "-DllPath", str(paths.ADDIN_DLL)],
                             capture_output=True, timeout=120)
        lines = [ln for ln in out.stdout.decode("utf-8", errors="replace").splitlines() if ln.startswith("{")]
        self.assertTrue(lines, out.stdout.decode("cp866", errors="replace") + out.stderr.decode("cp866", errors="replace"))
        result = json.loads(lines[-1])
        self.assertEqual([], result["problems"])
        self.assertTrue(result["sandboxRemoved"], "временный раздел реестра не удалён")

    def test_T0_sw_block_expected_matches_package(self):
        """T0 (ревью 24.09.2026): шаг 8 установщика «Отучение SolidWorks от сети». Установщик ждёт столько правил, сколько
        создаёт пакет (SLDWORKS.exe пропускается в обе стороны, как в Test-SldWorksConflict), а сбой пакета или нехватка
        правил в ветке «уже администратор» дают [ВНИМАНИЕ], не [OK], и ошибкой установки не считаются (решение владельца
        25.09.2026: это примечание). Брандмауэр, hosts и реестр не затрагиваются:
        программы и пакет подменяются временными файлами, счёт правил — заглушкой."""
        out = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                              str(paths.TESTS / "tools" / "check_sw_internet_block.ps1"), "-RepoRoot", str(ROOT)],
                             capture_output=True, timeout=300)
        lines = [ln for ln in out.stdout.decode("utf-8", errors="replace").splitlines() if ln.startswith("{")]
        self.assertTrue(lines, out.stdout.decode("cp866", errors="replace") + out.stderr.decode("cp866", errors="replace"))
        result = json.loads(lines[-1])
        self.assertEqual([], result["problems"])
        self.assertEqual({"Block SW Internet 096 - SLDWORKS", "Block SW Internet IN 096 - SLDWORKS"},
                         set(result["skippedByPolicy"]), "пакет пропускает SLDWORKS.exe в обе стороны и только его")
        self.assertEqual(result["package"], result["expected"])
        self.assertTrue(result["tempRemoved"], "временная папка не удалена")

    def test_T0_drawing_scripts_refuse_without_product_folder(self):
        """T0 (ревью 24.09.2026): скрипты eskd-drawings без ESKD_DRW_ROOT отказываются работать (SKILL.md, «Порядок»),
        а не падают и не берут текущую папку. sm_group_drw строит путь к модели сам, без find_model: без папки изделия
        или без модели в ней он отказывает до обращения к SolidWorks. SolidWorks не нужен — COM подменяется заглушкой,
        которая сообщает о любом открытии документа или смене настройки."""
        import sys
        import tempfile
        scripts = ROOT / "03_Макросы_и_Плагины" / "Оформление_чертежей_eskd-drawings" / "scripts"
        child = r'''
import json, sys, types
from unittest import mock
sys.path.insert(0, sys.argv[1])
client = mock.MagicMock()
sw = client.GetActiveObject.return_value
for name in ("OpenDoc6", "GetOpenDocumentByName", "GetUserPreferenceToggle", "SetUserPreferenceToggle"):
    getattr(sw, name).side_effect = RuntimeError("обращение к SolidWorks: " + name)
package = types.ModuleType("win32com")
package.client = client
sys.modules.update({"win32com": package, "win32com.client": client, "pythoncom": mock.MagicMock()})
import sm_group_drw as G
result = {}
for name, call in (("sm_group_drw", lambda: G.run(False)), ("find_model", lambda: G.T.find_model("Кронштейн", ".SLDPRT"))):
    try:
        call()
        result[name] = "нет отказа"
    except SystemExit as e:
        result[name] = "SystemExit: %s" % e
    except Exception as e:
        result[name] = "%s: %s" % (type(e).__name__, e)
print(json.dumps(result))
'''

        def refusals(root):
            env = {k: v for k, v in os.environ.items() if k != "ESKD_DRW_ROOT"}
            if root is not None:
                env["ESKD_DRW_ROOT"] = root
            out = subprocess.run([sys.executable, "-c", child, str(scripts)], capture_output=True, timeout=120, env=env)
            lines = [ln for ln in out.stdout.decode("utf-8", errors="replace").splitlines() if ln.startswith("{")]
            self.assertTrue(lines, out.stdout.decode("utf-8", errors="replace") + out.stderr.decode("utf-8", errors="replace"))
            return json.loads(lines[-1])

        result = refusals(None)
        self.assertTrue(result["sm_group_drw"].startswith("SystemExit: Задайте ESKD_DRW_ROOT"), result)
        self.assertTrue(result["find_model"].startswith("SystemExit: Задайте ESKD_DRW_ROOT"), result)
        with tempfile.TemporaryDirectory() as empty:
            result = refusals(empty)
        self.assertTrue(result["sm_group_drw"].startswith("SystemExit: Нет модели " + empty), result)
        self.assertTrue(result["find_model"].startswith("SystemExit: «Кронштейн»: нет модели"), result)

    def test_T0_command_callbacks_are_guarded(self):
        """T0 (сверка SW API 23.09.2026, №7): тело каждой кнопки надстройки — в Guarded или try: исключение, ушедшее в
        SolidWorks, он проглатывает молча, и кнопка «ничего не сделала» без записи в журнал."""
        raw = (ADDIN / "SwAddin.cs").read_text(encoding="utf-8-sig")
        # Имя обработчика стоит перед именем проверки доступности: «…, "CheckProduct", "EnableCheckCommand", …».
        callbacks = re.findall(r'"(\w+)",\s*"Enable\w*"', raw)
        self.assertGreaterEqual(len(callbacks), 12, callbacks)
        text = strip_csharp_literals(raw)
        bare = []
        for name in callbacks:
            found = re.search(r"public void " + name + r"\(\)\s*\{", text)
            self.assertIsNotNone(found, name)
            body = text[found.end():matching_brace(text, found.end() - 1)]
            body = re.sub(r"//[^\n]*", "", body).strip()
            if not (body.startswith("Guarded(") or body.startswith("try")):
                bare.append(name)
        self.assertEqual([], bare, "кнопки без защиты от исключения")

    def test_T0_graph3_reads_body_materials(self):
        """T0 (сверка SW API 23.09.2026, №33): графа 3, «Материал_Строка», запись БЧ и проверка «материал не назначен»
        читают фактический материал — свой материал тела перекрывает материал детали (SyncService.ActualMaterial)."""
        sync = (ADDIN / "Sw" / "SyncService.cs").read_text(encoding="utf-8-sig")
        found = re.search(r"public static void SyncMaterials\([^)]*\)\s*\{", sync)
        body = sync[found.end():matching_brace(sync, found.end() - 1)]
        self.assertIn("ActualMaterial(", body, "графа 3 — по фактическому материалу")
        self.assertNotIn("MaterialName(", body, "не только материал детали")
        for name in ("CheckService.cs", "BchService.cs"):
            text = (ADDIN / "Sw" / name).read_text(encoding="utf-8-sig")
            self.assertNotIn("SyncService.MaterialName(", text, name + ": только материал детали")
            self.assertIn("SyncService.ActualMaterial(", text, name)

    def test_T0_open_event_takes_only_the_opened_document(self):
        """T0 (сверка SW API 23.09.2026, №11): документ события ищется только по имени — запасной ActiveDoc давал чужой
        документ (при SyncOnOpen = 1 запись ушла бы в него); пробный проход при открытии — в простое, открытие его не ждёт."""
        hub = (ADDIN / "Sw" / "EventHub.cs").read_text(encoding="utf-8-sig")
        code = re.sub(r"//[^\n]*", "", hub)
        self.assertNotIn("return _app.ActiveDoc as ModelDoc2;", code, "запасной ActiveDoc при поиске документа по имени")
        found = re.search(r"private int OnFileOpenPost\(string fileName\)\s*\{", code)
        body = code[found.end():matching_brace(code, found.end() - 1)]
        self.assertNotIn("DryRun = true", body, "пробный проход в самом событии открытия")
        self.assertIn('Kind = "opendiag"', body, "пробный проход — задача простоя")

    def test_T0_review2_windowless_and_opendiag(self):
        """T0 (ревью 23.09.2026): документ без окна (скрытый, остался в сборке) материал по геометрии в простое не
        получает — надстройка без окна документ не правит. Пробная задача открытия несёт путь и при закрытии документа
        не пишет ложного «задача не выполнена» (закрытие заказа и очистка открывают и закрывают файлы подряд)."""
        hub = (ADDIN / "Sw" / "EventHub.cs").read_text(encoding="utf-8-sig")
        code = re.sub(r"//[^\n]*", "", hub)
        found = re.search(r'if \(task\.Kind == "stock"\)\s*\{', code)
        stock = code[found.end():matching_brace(code, found.end() - 1)]
        self.assertIn("Windowless(task.Doc)", stock, "без окна — задача отменяется")
        self.assertLess(stock.find("Windowless(task.Doc)"), stock.find("ApplyStock("), "до назначения")
        self.assertRegex(code, r'Kind = "opendiag", TargetPath = SafePath\(doc\)', "задача открытия с путём")
        for name in (r"private void DropTasks\(", r"public void Detach\("):
            m = re.search(name + r"[^{]*\{", code)
            body = code[m.end():matching_brace(code, m.end() - 1)]
            self.assertIn('"opendiag"', body, name + ": пробная задача без предупреждения")

    def test_T0_review2_lzk_abort_waits_for_swtools(self):
        """T0 (ревью 23.09.2026): Abort ждёт завершения SWTools после Kill — иначе временные файлы ещё заняты и остаются;
        служба перестаёт быть текущей и при сбое уборки (finally), а уборка ловит и отказ в доступе."""
        lzk = (ADDIN / "Sw" / "LzkService.cs").read_text(encoding="utf-8-sig")
        code = re.sub(r"//[^\n]*", "", lzk)
        m = re.search(r"public static void Abort\(string reason\)\s*\{", code)
        abort = code[m.end():matching_brace(code, m.end() - 1)]
        self.assertIn("WaitForExit(", abort, "ждать SWTools после Kill")
        self.assertIn("finally", abort, "служба снимается и при сбое уборки")
        m = re.search(r"private void CleanupAttempt\(\)\s*\{", code)
        cleanup = code[m.end():matching_brace(code, m.end() - 1)]
        self.assertIn("UnauthorizedAccessException", cleanup, "отказ в доступе при удалении временного файла")

    def test_T0_unloaded_component_is_not_suppressed(self):
        """T0 (сверка SW API 23.09.2026, №20; e2e X17): скрытый незагруженный компонент SolidWorks отдаёт погашенным, но с
        IsLoaded = false. Выгрузка, проверка изделия и ЛЗК отбрасывают только погашенный (ComponentState.Suppressed), а
        незагруженный называют «модель не загружена»."""
        for name in ("ExportService.cs", "ProductReviewService.cs", "LzkService.cs"):
            code = re.sub(r"//[^\n]*", "", (ADDIN / "Sw" / name).read_text(encoding="utf-8-sig"))
            self.assertIn("ComponentState.Suppressed(", code, name)
            self.assertNotIn("swComponentSuppressionState_e.swComponentSuppressed", code, name + ": погашенный — через ComponentState")
            self.assertNotRegex(code, r"\bc(omp)?\.IsSuppressed\(\)", name + ": IsSuppressed компонента истинно и у незагруженного")
        state = (ADDIN / "Sw" / "ComponentState.cs").read_text(encoding="utf-8-sig")
        self.assertIn("IsLoaded()", state, "незагруженный — не погашенный")

    def test_T0_raw_property_read_without_recalculation(self):
        """T0 (сверка SW API 23.09.2026, №24; e2e P20): сырое значение свойства читается без пересчёта. Get4 с
        UseCached = false пересчитывал «SW-Mass» каждого исполнения библиотечной детали — Ctrl+S ~31 с; но и с
        UseCached = true первое чтение выражения массы после открытия файла пересчитывает её (проба 24.09.2026: 79
        исполнений — 8,8 с, этап «масса» — 4,5 с при Ctrl+S). Сырое значение отдаёт без пересчёта CustomInfo2 — так
        читает и пишет MProp (79 исполнений — 0,17 с; контракт C11). Get4/Get6 — только для вычисленного и для признака
        связи с родителем у производной конфигурации."""
        code = re.sub(r"//[^\n]*", "", (ADDIN / "Sw" / "PropertyWriter.cs").read_text(encoding="utf-8-sig"))
        self.assertRegex(code, r"public string Raw\([^)]*\)\s*\{\s*string\[\] pair = Values\(cfg, name, false\);", "сырое — из кэша")
        self.assertRegex(code, r"public string Resolved\([^)]*\)\s*\{\s*string\[\] pair = Values\(cfg, name, true\);",
                         "вычисленное — свежее")
        self.assertRegex(code, r"private string RawValue\(string cfg, string name\)\s*\{[^}]*get_CustomInfo2\(",
                         "сырое — CustomInfo2")
        self.assertEqual(["Get4(name, false,"], re.findall(r"Get4\(name, [^,]+,", code), "Get4 — только вычисленное, свежее")
        m = re.search(r"public string MoveRefusal\(", code)
        refusal = code[m.start():matching_brace(code, code.index("{", m.start()))]
        self.assertEqual(1, code.count(".Get6("), "Get6 — только признак связи в MoveRefusal")
        self.assertRegex(refusal, r"if \(derived\)\s*\{[^}]*\.Get6\(", "и только у производной конфигурации")
        self.assertEqual(4, len(re.findall(r"\bRawValue\(", code)) - 1,
                         "RawValue: Values, MoveRefusal, перенос — до и после записи")

    def test_T0_property_order_only_inside_commanded_saves(self):
        """T0 (сверка SW API 23.09.2026, №23; решение владельца 24.09.2026): существующее свойство пишется на своей строке
        (Add3 ReplaceValue), с заменой — только новое, запасной путь и перенос в конец. Единый порядок наводится только в
        сохранении по команде конструктора: OrderFor задают лишь OnDocSave и SyncAndResave, Restore зовут лишь SyncModel и
        «Применить и сохранить» окна «Проверить изделие»."""
        writer = re.sub(r"//[^\n]*", "", (ADDIN / "Sw" / "PropertyWriter.cs").read_text(encoding="utf-8-sig"))
        m = re.search(r"public bool Set\(string cfg, string name, string value\)\s*\{", writer)
        body = writer[m.end():matching_brace(writer, m.end() - 1)]
        self.assertLess(body.index("swCustomPropertyReplaceValue"), body.index("swCustomPropertyDeleteAndAdd"),
                        "Set: сначала запись на месте")
        self.assertEqual(3, writer.count("swCustomPropertyDeleteAndAdd"),
                         "DeleteAndAdd — новое свойство, запасной путь Set и перенос в конец")
        order_for, restore = {}, {}
        for src in addin_sources():
            code = re.sub(r"//[^\n]*", "", src.read_text(encoding="utf-8-sig"))
            for m in re.finditer(r"\bOrderFor\s*=\s*(\w+)", code):
                order_for.setdefault(src.name, []).append(m.group(1))
            n = len(re.findall(r"PropertyOrderService\.Restore\(", code))
            if n:
                restore[src.name] = n
        self.assertEqual({"EventHub.cs": ["targetPath", "by"]}, order_for, "кто просит порядок: SyncAndResave, OnDocSave")
        self.assertEqual({"SyncService.cs": 1, "ProductReviewService.cs": 1}, restore, "где наводится порядок")

    def test_T0_property_order_oracle_matches_addin(self):
        """T0 (№23): независимый оракул порядка (Python) совпадает с надстройкой — группы мастер-списка в том же порядке,
        случаи property_order_cases.txt проходит так же, как юнит-тест PropertyOrderTests."""
        from eskd_e2e import oracles

        code = (ADDIN / "Core" / "PropertyOrder.cs").read_text(encoding="utf-8-sig")
        m = re.search(r"IEnumerable<string>\[\] groups =\s*\{(.*?)\};", code, flags=re.S)
        self.assertEqual(["Names", *oracles.MASTER_GROUPS], re.findall(r"\.(\w+)", m.group(1)), "группы мастер-списка")
        master = oracles.property_master()
        self.assertEqual(93, len(master), master)
        self.assertEqual(["Операции", "Ревизия", "Габарит", "ЕСКД_Принято"], master[-4:], "поздние имена — последние")
        text = (paths.TESTS / "unit" / "data" / "property_order_cases.txt").read_text(encoding="utf-8")
        cases = re.findall(r"^current=(.*)\ncanonical=(.*)\nmove=(.*)$", text, flags=re.M)
        self.assertGreaterEqual(len(cases), 10)
        split = lambda v: v.split("|") if v else []  # noqa: E731
        for current, want, move in cases:
            with self.subTest(current=current):
                self.assertEqual((split(want), split(move)), oracles.canonical(split(current), master, oracles.property_tail()))

    def test_T0_property_order_tail_is_mprop_apply_order(self):
        """T0 (решение владельца 24.09.2026): последние в едином порядке — ровно те свойства, которые «Применить» MProp
        удаляет и дописывает заново в том же исполнении, и в том же порядке. Тогда MProp и сохранение надстройки не
        переставляют их друг за другом. Правка MProp или надстройки, которая это разведёт, ломает тест."""
        from eskd_e2e import oracles

        form = paths.ROOT / "03_Макросы_и_Плагины" / "Макросы_SW_ZTool" / "_VBA_выгрузка" / "MProp" / "FrmMProp.frm.txt"
        text = form.read_text(encoding="utf-8-sig")
        apply = re.search(r"Private Sub Внести_изменения_Click\(\)(.*?)^End Sub", text, flags=re.S | re.M).group(1)
        readded = []
        for m in re.finditer(r"AddCustomInfo3\(sConfigName, (prp\w+)", apply):
            deleted = re.search(r"DeleteCustomInfo2\(sConfigName, %s\)" % m.group(1), apply[:m.start()])
            if deleted and m.group(1) not in readded:
                readded.append(m.group(1))
        self.assertEqual(["prpRemark", "prpFormat", "prpSection"], readded, "MProp удаляет и дописывает в исполнении")
        # Строка словаря каждого prp — по порядку чтения в Sub MyProperties: «Line Input», затем «prpX = strTemp».
        reader = re.search(r"Private Sub MyProperties\(\)(.*?)^End Sub", text, flags=re.S | re.M).group(1)
        order = re.findall(r"^\s*(prp\w+) = strTemp", reader, flags=re.M)
        ini = paths.SWPLUS_DICTIONARY.read_text(encoding="cp1251").splitlines()
        self.assertEqual([ini[order.index(prp)].strip() for prp in readded], oracles.property_tail(),
                         "хвост оракула — имена этих строк словаря")
        code = (ADDIN / "Core" / "PropertyOrder.cs").read_text(encoding="utf-8-sig")
        tail = re.search(r"public static List<string> Tail\(.*?\{(.*?)^        \}", code, flags=re.S | re.M).group(1)
        self.assertEqual(["Remark", "Format", "Section"], re.findall(r"Role\.(\w+)", tail), "хвост надстройки — те же роли")

    def test_T0_export_opens_drawings_without_window(self):
        """T0 (сверка SW API 23.09.2026, №28, шаг 2; e2e X20): выгрузка открывает чертёж без окна и возвращает прежнюю
        видимость новых чертежей даже при сбое открытия (finally)."""
        code = re.sub(r"//[^\n]*", "", (ADDIN / "Sw" / "ExportService.cs").read_text(encoding="utf-8-sig"))
        m = re.search(r"private static ModelDoc2 OpenDrawing\([^)]*\)\s*\{", code)
        body = code[m.end():matching_brace(code, m.end() - 1)]
        self.assertIn("GetDocumentVisible((int)swDocumentTypes_e.swDocDRAWING)", body, "прежняя видимость запомнена")
        self.assertRegex(body, r"DocumentVisible\(false, \(int\)swDocumentTypes_e\.swDocDRAWING\);\s*try\s*\{[^}]*OpenDoc6",
                         "открытие — без окна")
        self.assertRegex(body, r"finally\s*\{\s*app\.DocumentVisible\(visible,", "видимость возвращается в finally")

    def test_T0_flat_frame_points_checked_by_edge_ends(self):
        """T0 (L15, 23.09.2026): рамка развёртки берёт прямые рёбра по концам (GetCurveParams2), а точки кривых — только
        сверенные с концами ребра (EdgePoints.Choose). Кривая развёрнутого тела бывает сдвинута: уголок 100+60 давал
        262 мм вместо 161."""
        lzk = (ADDIN / "Sw" / "LzkService.cs").read_text(encoding="utf-8-sig")
        code = re.sub(r"//[^\n]*", "", lzk)
        m = re.search(r"private static double\[\] FlatFrame\([^)]*\)\s*\{", code)
        frame = code[m.end():matching_brace(code, m.end() - 1)]
        self.assertIn("EdgePoints.Choose(", frame, "точки рёбер сверены с концами")
        self.assertNotIn("Evaluate2(", frame, "кривая не вычисляется в обход сверки")
        self.assertEqual(1, len(re.findall(r"curve\.Evaluate2\(", code)), "Curve.Evaluate2 — только в EdgeSamples")

    def test_T0_review2_materials_and_namesakes(self):
        """T0 (ревью 23.09.2026): материал детали, который встал, считается назначенным, а перекрывающие тела — отдельным
        замечанием (StockCatalog.WholePartResult); многотельный лист, где конструктор уже назначил телам материалы, не
        даёт замечания (SheetBodiesSettled); материал тел читается только у активного исполнения (BodiesBelongTo);
        отказ «Закрыть заказ» разделяет окна и детали сборок (NamesakeText)."""
        stock = (ADDIN / "Sw" / "StockService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("StockCatalog.WholePartResult(", stock, "итог назначения детали целиком")
        self.assertNotRegex(stock, r"WholePartProblem\([^;]*;\s*bool set = problem\.Length == 0", "«не назначен» при вставшем материале")
        self.assertIn("StockCatalog.SheetBodiesSettled(", stock, "решённый многотельный лист без замечания")
        sync = (ADDIN / "Sw" / "SyncService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("BodyMaterials.BodiesBelongTo(", sync, "тела — только своего исполнения")
        close = (ADDIN / "Sw" / "CloseOrderService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("OrderArchive.NamesakeText(", close, "отказ с окнами и деталями сборок раздельно")
        self.assertNotIn("Закройте их и повторите", close, "не просить закрыть документы без окна")
        self.assertIn("WholePartResult(before", stock, "назначением считается только смена материала детали")
        self.assertIn("propagate", stock, "материал чужой библиотеки в исполнения не переносится")
        self.assertIn("BodyMaterials.Actual(", sync, "материал другого исполнения — «не узнать», а не «нет»")
        hub = (ADDIN / "Sw" / "EventHub.cs").read_text(encoding="utf-8-sig")
        self.assertIn("applied.StatusLine()", hub, "замечания назначения по Ctrl+S видны в строке состояния")
        self.assertIn(".Visible", close, "есть ли у документа окно")

    def test_T0_hidden_document_stays_tracked(self):
        """T0 (сверка SW API 23.09.2026, №9): надстройка слушает DestroyNotify2 с типом закрытия. Скрытый документ (окно
        закрыто, документ остался в памяти сборки) остаётся в учёте, у него снимаются только отложенные задачи."""
        hub = (ADDIN / "Sw" / "EventHub.cs").read_text(encoding="utf-8-sig")
        code = re.sub(r"//[^\n]*", "", hub)
        self.assertNotRegex(code, r"\.DestroyNotify\s*[+-]=", "старый DestroyNotify без типа")
        self.assertEqual(3, len(re.findall(r"\.DestroyNotify2\s*\+=", code)), "деталь, сборка, чертёж")
        self.assertIn("swDestroyNotifyHidden", code, "скрытие различается")

    def test_T0_addin_release_takes_down_everything(self):
        """T0 (сверка SW API 23.09.2026, №0 и №3): сбой ConnectToSW и выгрузка надстройки идут одним путём Release —
        ведомость ЛЗК прерывается, подписки и вкладка снимаются. Каждая подписка Attach записана для отписки, Detach
        снимает их по одной и пишет в журнал отложенные задачи, которые не успели выполниться."""
        addin = strip_csharp_literals((ADDIN / "SwAddin.cs").read_text(encoding="utf-8-sig"))
        hub = strip_csharp_literals((ADDIN / "Sw" / "EventHub.cs").read_text(encoding="utf-8-sig"))
        lzk = (ADDIN / "Sw" / "LzkService.cs").read_text(encoding="utf-8-sig")

        def body(text, signature):
            found = re.search(signature + r"[^{]*\{", text)
            self.assertIsNotNone(found, signature)
            return text[found.end():matching_brace(text, found.end() - 1)]

        connect = body(addin, r"public bool ConnectToSW\(")
        self.assertRegex(connect, r"catch \(Exception ex\)\s*\{[^}]*Release\(", "сбой ConnectToSW снимает то, что успело встать")
        self.assertIn("Release(", body(addin, r"public bool DisconnectFromSW\("), "выгрузка — тем же путём")
        release = body(addin, r"private void Release\(")
        for step in ("LzkService.Abort(", "_hub.Detach()", "RemoveCommands()"):
            self.assertIn(step, release, "Release: " + step)
        attach = body(hub, r"public void Attach\(")
        self.assertEqual(attach.count("+="), attach.count("Undo("), "каждая подписка записана для отписки")
        detach = body(hub, r"public void Detach\(")
        self.assertIn("_unsubscribe", detach, "отписка по записанному списку")
        self.assertNotIn("-=", detach, "не одним блоком: сбой первой отписки оставлял остальные")
        self.assertIn("до выполнения отложенной задачи", (ADDIN / "Sw" / "EventHub.cs").read_text(encoding="utf-8-sig"),
                      "снятые задачи — в журнале")
        self.assertIn("public static void Abort(string reason)", lzk, "ведомость ЛЗК можно прервать снаружи")

    def test_T0_export_returns_to_product_after_closing_its_windows(self):
        """T0 (сверка SW API 23.09.2026, №15, №28; e2e X19, X20): выгрузка сначала закрывает окна, которые открыла сама,
        и только потом делает изделие активным — закрытое окно SolidWorks сменяет следующим по порядку, окном
        конструктора (прогон r34 24.09.2026). Чертёж без окна показывается только на время записи PDF и скрывается
        в finally: SolidWorks 2025 не пишет PDF из невидимого чертежа (проба 24.09.2026)."""
        code = re.sub(r"//[^\n]*", "", (ADDIN / "Sw" / "ExportService.cs").read_text(encoding="utf-8-sig"))
        self.assertRegex(code, r"CloseOwnWindows\(app, items\);\s*Activate\(app, path\);", "сначала закрыть, потом активировать")
        m = re.search(r"private static void Pdf\(", code)
        pdf = code[m.start():matching_brace(code, code.index("{", m.start()))]
        self.assertLess(pdf.index("drawing.Visible = true"), pdf.index("drawing.Extension.SaveAs("), "показать до записи PDF")
        self.assertRegex(pdf, r"finally\s*\{\s*if \(shown\)[\s\S]*drawing\.Visible = false", "скрыть в finally")

    def test_T0_export_survives_one_bad_document(self):
        """T0 (сверка SW API 23.09.2026, №29 и №20): сбой одного документа не обрывает выгрузку. Вызовы SolidWorks и
        создание папок в составе, PDF, DXF и IGS стоят внутри try с catch; каждый документ выгружается под своим catch;
        окно изделия возвращается в finally; незагруженная модель названа в отчёте, итог разрешения облегчённых
        компонентов проверяется."""
        raw = (ADDIN / "Sw" / "ExportService.cs").read_text(encoding="utf-8-sig")
        text = re.sub(r"//[^\n]*", lambda m: " " * len(m.group(0)), strip_csharp_literals(raw))

        def body(name):
            found = re.search(r"(?:private|public) static [\w<>\[\], ]+ " + name + r"\([^)]*\)\s*\{", text)
            self.assertIsNotNone(found, name)
            return found.end(), matching_brace(text, found.end() - 1)

        def guarded(kind):
            """Участки [начало, конец) блоков try, за которыми стоит catch (kind="try"), или блоков finally."""
            spans = []
            for m in re.finditer(r"\b" + kind + r"\s*\{", text):
                end = matching_brace(text, m.end() - 1)
                if kind == "finally" or re.match(r"\s*catch\b", text[end + 1:]):
                    spans.append((m.end(), end))
            return spans

        tries, finals = guarded("try"), guarded("finally")
        risky = ["Directory.CreateDirectory(", ".CloseDoc(", ".ShowConfiguration2(", ".GetSaveFlag()", ".GetSuppression2()",
                 ".GetModelDoc2()", ".ExcludeFromBOM", ".ReferencedConfiguration"]
        bare = []
        for method in ("Collect", "ExportItem", "Pdf", "PartFiles", "DxfOne", "IgsOne"):
            start, end = body(method)
            for call in risky:
                for m in re.finditer(re.escape(call), text[start:end]):
                    at = start + m.start()
                    if not any(a <= at < b for a, b in tries):
                        bare.append(f"{method}: {call}")
        self.assertEqual([], bare, "вызовы без catch: исключение оборвало бы всю выгрузку")

        start, end = body("Run")
        calls = [start + m.start() for m in re.finditer(r"ExportItem\(", text[start:end])]
        self.assertTrue(calls and all(any(a <= at < b for a, b in tries) for at in calls), "каждый документ — под своим catch")
        back = [start + m.start() for m in re.finditer(r"Activate\(app, path\)", text[start:end])]
        self.assertTrue(back and all(any(a <= at < b for a, b in finals) for at in back), "окно изделия возвращается в finally")

        start, end = body("Collect")
        self.assertIn("swResolveOk", text[start:end], "итог разрешения облегчённых компонентов проверяется")
        self.assertIn("модель не загружена", raw, "незагруженная модель названа в отчёте")

    def test_T0_cut_list_is_walked_in_one_place(self):
        """T0 (сверка SW API 23.09.2026, №32, №34, №41): список вырезов обходится только через CutListFolders — папки с
        телами активного исполнения, вместе с подсварками. Свой обход в сервисе брал и скрытые папки других исполнений
        (чужой профиль, вторая «заготовка»), а подсварки пропускал."""
        own = []
        for path in sorted((ADDIN / "Sw").glob("*.cs")):
            if path.name == "CutListFolders.cs":
                continue
            raw = path.read_text(encoding="utf-8-sig")
            if re.search(r'"(CutListFolder|SolidBodyFolder|SubWeldFolder)"', raw):
                own.append(path.name)
        self.assertEqual([], own, "свой обход списка вырезов")
        self.assertTrue((ADDIN / "Sw" / "CutListFolders.cs").exists())

    def test_T0_body_material_refusal_is_explained(self):
        """T0 (сверка SW API 23.09.2026, №38): отказ SolidWorks поставить материал телу объясняется словами — в замечании
        стояло «не назначен телу (код 4)», и конструктор не знал, что дерево откатано."""
        text = (ADDIN / "Sw" / "StockService.cs").read_text(encoding="utf-8-sig")
        # Назначение телам — через BodyAssignment (всем или ни одному, №38), код отказа словами — SwCodes.BodyMaterialProblem.
        self.assertRegex(text, r"(?s)BodyAssignment\.Apply\(.*?SwCodes\.BodyMaterialProblem\);", "код отказа словами")
        self.assertNotIn("не назначен телу (код", text)
        core = (ADDIN / "Core" / "BodyAssignment.cs").read_text(encoding="utf-8-sig")
        self.assertIn("problem(code)", core, "причина отказа в замечании")

    def test_T0_registration_refuses_32bit_powershell(self):
        """T0 (сверка SW API 23.09.2026, №4): из 32-битного PowerShell модуль регистрации отказывает с понятным текстом и
        ничего не пишет: HKCU\\Software\\Classes\\CLSID и HKLM\\Software ушли бы в Wow6432Node, 64-битный SolidWorks такую
        регистрацию не увидел бы, а проверка, читающая то же представление реестра, сообщала бы «[OK]»."""
        ps32 = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "SysWOW64" / "WindowsPowerShell" / "v1.0" / "powershell.exe"
        if not ps32.exists():
            self.skipTest("32-битного PowerShell нет (32-битная Windows)")
        out = subprocess.run([str(ps32), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                              str(paths.TESTS / "tools" / "check_registration_bitness.ps1"),
                              "-ModulePath", str(ADDIN / "Register-EskdAddin.ps1")], capture_output=True, timeout=120)
        lines = [ln for ln in out.stdout.decode("utf-8", errors="replace").splitlines() if ln.startswith("{")]
        self.assertTrue(lines, out.stdout.decode("utf-8", errors="replace") + out.stderr.decode("cp866", errors="replace"))
        result = json.loads(lines[-1])
        self.assertFalse(result["is64"], "проба запущена в 32-битном PowerShell")
        for call in ("reg", "unreg"):
            self.assertIn("32", result[call], f"{call}: отказ с причиной — 32-битный PowerShell")
        # Ревью 23.09.2026: снятие регистрации отказывает своим текстом, без «двойного щелчка» по unregister.ps1.
        self.assertIn("Снятие регистрации", result["unreg"], "отказ снятия — о снятии")
        self.assertNotIn("двойным щелчком", result["unreg"], "unregister.ps1 двойным щелчком не запускается")
        self.assertFalse(result["written"], "в реестр ничего не записано")

    def test_T0_registration_refuses_foreign_account(self):
        """T0 (сверка SW API 23.09.2026, №2): скрипт регистрации, запущенный не от имени того, кто работает за компьютером
        («Запуск от имени администратора» с паролем ИТ), отказывает до записи в реестр: автозагрузка надстройки ушла бы в
        профиль администратора, а скрипт писал «[OK]». «[OK]» называет учётную запись. Установщик проверяет учётку сам
        (test_T0_setup_refuses_foreign_account)."""
        out = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                              str(paths.TESTS / "tools" / "check_registration_account.ps1"),
                              "-ModulePath", str(ADDIN / "Register-EskdAddin.ps1")], capture_output=True, timeout=120)
        lines = [ln for ln in out.stdout.decode("utf-8", errors="replace").splitlines() if ln.startswith("{")]
        self.assertTrue(lines, out.stdout.decode("utf-8", errors="replace") + out.stderr.decode("cp866", errors="replace"))
        result = json.loads(lines[-1])
        self.assertIn("учётной записью конструктора", result["foreign"] or "", "чужая учётка — отказ с объяснением")
        self.assertEqual("", result["same"] or "", "та же учётка — можно")
        self.assertEqual("", result["unknown"] or "", "владелец сеанса не определён — проверка не мешает")
        self.assertEqual("", result["here"] or "", "тесты запускает сам владелец сеанса")
        # Ревью 23.09.2026: владелец сеанса — пользователь, вошедший в сеанс (WTS), а не владелец explorer.exe: чужой
        # процесс без повышения прав не читает владельца explorer, и проверка молча пропускала.
        self.assertTrue(result["session_is_me"], "владелец сеанса — тот, кто запускает тесты")
        module = (ADDIN / "Register-EskdAddin.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("WTSQuerySessionInformation", module, "владелец сеанса — по сеансу Windows")
        self.assertIn("Регистрация_ЕСКД_на_этом_компьютере.cmd", result["foreign"] or "", "отказ называет, что запустить")
        self.assertIn("Снятие регистрации", result["foreign_unreg"] or "", "отказ снятия — о снятии, не о регистрации")
        self.assertNotIn("двойным щелчком", result["foreign_unreg"] or "", "у unregister.ps1 нет ярлыка для двойного щелчка")
        self.assertIn("-ExecutionPolicy Bypass", result["foreign_unreg"] or "", "команда, которая запустится и при запрете сценариев")
        self.assertIn(r"C:\ЕСКД\unregister.ps1", result["foreign_unreg"] or "", "путь к скрипту")
        self.assertIn("Войдите в Windows", result["foreign"] or "", "что делать, если за компьютером не конструктор")
        register = (ADDIN / "register_eskd.ps1").read_text(encoding="utf-8-sig")
        check = register.find("Get-EskdForeignAccountMessage")
        self.assertGreater(check, 0, "register_eskd.ps1 проверяет учётку")
        self.assertLess(check, register.find('& (Join-Path $PSScriptRoot "build.ps1")'), "до сборки")
        self.assertLess(check, register.find("Register-EskdAddin -DllPath"), "до записи в реестр")
        self.assertRegex(register, r"\[OK\][^\n]*учётной записи", "«[OK]» называет учётку")
        unregister = (ADDIN / "unregister.ps1").read_text(encoding="utf-8-sig")
        check = unregister.find("Get-EskdForeignAccountMessage")
        self.assertGreater(check, 0, "unregister.ps1 проверяет учётку")
        self.assertLess(check, unregister.find("Unregister-EskdAddin -SystemWide"), "до снятия регистрации")
        self.assertIn("Get-EskdForeignAccountMessage -Action Unregister", unregister, "текст отказа — о снятии")

    def test_T0_launchers_start_64bit_powershell(self):
        """T0 (сверка SW API 23.09.2026, №4): УСТАНОВИТЬ_ЕСКД.bat и «Регистрация_ЕСКД_на_этом_компьютере.cmd», запущенные
        из 32-битной программы, всё равно зовут 64-битный PowerShell (Sysnative): System32 у 32-битного процесса — это
        SysWOW64. Проверяется копиями файлов с поддельными скриптами, которые записывают разрядность своего процесса."""
        import shutil
        import tempfile
        cmd32 = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "SysWOW64" / "cmd.exe"
        if not cmd32.exists():
            self.skipTest("32-битной подсистемы нет (32-битная Windows)")
        fake = "param([string]$CloseMode)\r\n[Environment]::Is64BitProcess | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'bitness.txt')\r\n"
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            (tmp / "01_x").mkdir()
            (tmp / "addin").mkdir()
            shutil.copy2(ROOT / "УСТАНОВИТЬ_ЕСКД.bat", tmp / "setup.bat")
            (tmp / "01_x" / "Setup_Workstation_SolidWorks.ps1").write_text(fake, encoding="ascii")
            shutil.copy2(ADDIN / "Регистрация_ЕСКД_на_этом_компьютере.cmd", tmp / "addin" / "reg.cmd")
            (tmp / "addin" / "register_eskd.ps1").write_text(fake, encoding="ascii")
            for launcher, result in ((tmp / "setup.bat", tmp / "01_x" / "bitness.txt"),
                                     (tmp / "addin" / "reg.cmd", tmp / "addin" / "bitness.txt")):
                with self.subTest(launcher=launcher.name):
                    subprocess.run([str(cmd32), "/c", str(launcher)], input=b"\r\n", capture_output=True, timeout=120,
                                   cwd=str(launcher.parent))
                    self.assertTrue(result.exists(), f"{launcher.name}: PowerShell не запустился")
                    self.assertEqual("True", result.read_text(encoding="utf-8-sig").strip(), f"{launcher.name}: 64-битный PowerShell")

    def test_T0_e2e_harness_follows_registered_versions(self):
        """T0 (сверка SW API 23.09.2026, №6): стенд e2e направляет на проверяемую DLL все подразделы версий
        InprocServer32, а не только «1.0.0.0»: версию регистрация берёт из самой DLL, и после её подъёма стенд незаметно
        проверял бы установленную копию надстройки."""
        import uuid
        import winreg
        from eskd_e2e import session
        base = "Software\\ESKD_RegistrationTest_" + uuid.uuid4().hex
        key = base + "\\InprocServer32"
        try:
            for version in ("1.0.0.0", "6.0.0.0"):
                winreg.CloseKey(winreg.CreateKey(winreg.HKEY_CURRENT_USER, key + "\\" + version))
            self.assertEqual(sorted([key, key + "\\1.0.0.0", key + "\\6.0.0.0"]),
                             sorted(session.inproc_keys(winreg.HKEY_CURRENT_USER, key)), "раздел и все подразделы версий")
            self.assertEqual([key + "_нет"], session.inproc_keys(winreg.HKEY_CURRENT_USER, key + "_нет"),
                             "раздела нет — только он сам, подразделы не выдумываются")
        finally:
            for sub in (key + "\\1.0.0.0", key + "\\6.0.0.0", key, base):
                try:
                    winreg.DeleteKey(winreg.HKEY_CURRENT_USER, sub)
                except OSError:
                    pass
        text = (paths.TESTS / "eskd_e2e" / "session.py").read_text(encoding="utf-8")
        self.assertNotIn('"\\\\1.0.0.0"', text, "версия сборки в стенде не прибита")

    def test_T0_setup_writes_swplus_files_only_when_changed(self):
        """T0: установщик переписывает файлы SWPlus только при отличии, свою фамилию и организацию ставит первыми — MProp берёт первую строку (WP-3.4, З-3);
        функции — в модуле EskdDeploy.psm1, запись идёт в локальную копию."""
        module = ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "EskdDeploy.psm1"
        out = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                              str(paths.TESTS / "tools" / "check_setup_swplus_files.ps1"),
                              "-SetupPath", str(module), "-SwPlusRoot", str(paths.SWPLUS)], capture_output=True, timeout=120)
        lines = [ln for ln in out.stdout.decode("utf-8", errors="replace").splitlines() if ln.startswith("{")]
        self.assertTrue(lines, out.stdout.decode("cp866", errors="replace") + out.stderr.decode("cp866", errors="replace"))
        self.assertEqual([], json.loads(lines[-1])["problems"])
        for script in (module, ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1"):
            self.assertNotIn("WriteAllLines", script.read_text(encoding="utf-8-sig"), "файлы SWPlus пишутся только через Write-SwPlusLines")

    def test_T0_installer_survives_powershell7_environment(self):
        """T0 (аудит 23.09.2026): установщик, запущенный из PowerShell 7, наследовал его PSModulePath — Windows PowerShell 5.1
        терял Get-FileHash, сверка хешей молча давала пусто, и Drew переустанавливался. Теперь хэш считается средствами .NET,
        движок сам чистит пути модулей, окно и .bat запускают 5.1 по полному пути с его собственными путями модулей."""
        import tempfile
        setup_dir = ROOT / "01_Настройки_SolidWorks" / "_Служебное"
        module = setup_dir / "EskdDeploy.psm1"
        setup = (setup_dir / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        code = setup + module.read_text(encoding="utf-8-sig")
        self.assertNotRegex(code, r"\(Get-FileHash\s+-", "хэш через Get-FileHash: без модуля Utility он молча пуст")
        self.assertIn("Reset-EskdPowerShellEnvironment", setup, "движок не чистит пути модулей PowerShell 7")
        self.assertLess(setup.index("Reset-EskdPowerShellEnvironment"), setup.index("Get-EskdLayout"), "чистка — до работы")
        bat = (ROOT / "УСТАНОВИТЬ_ЕСКД.bat").read_bytes()
        self.assertTrue(all(b < 128 for b in bat), "в .bat не-ASCII символы")
        self.assertIn(b"PSModulePath=", bat, ".bat передаёт 5.1 чужие пути модулей")
        self.assertIn(b"WindowsPowerShell\\v1.0\\powershell.exe", bat, ".bat запускает powershell из PATH")

        with tempfile.TemporaryDirectory() as tmp:
            sample = Path(tmp) / "sample.bin"
            sample.write_bytes(b"ESKD" * 1000)
            expected = hashlib.sha256(sample.read_bytes()).hexdigest()
            script = Path(tmp) / "probe.ps1"
            script.write_text(
                "param([string]$Module, [string]$File)\n"
                "Import-Module $Module -Force -DisableNameChecking\n"
                "$fixed = Reset-EskdPowerShellEnvironment\n"
                "$again = Reset-EskdPowerShellEnvironment\n"
                "[pscustomobject]@{ fixed = $fixed; again = $again; path = $env:PSModulePath;\n"
                "  sha = (Get-EskdFileSha256 -Path $File); missing = (Get-EskdFileSha256OrNull -Path ($File + '.нет')) } |\n"
                "  ConvertTo-Json -Compress\n", encoding="utf-8-sig")
            env = {k: v for k, v in os.environ.items() if k.upper() != "PSMODULEPATH"}
            env["PSModulePath"] = r"C:\Users\x\Documents\PowerShell\Modules;C:\Program Files\PowerShell\Modules;" \
                                  r"C:\Program Files\PowerShell\7\Modules;C:\Program Files\WindowsPowerShell\Modules;D:\Other\Modules"
            out = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
                                  "-Module", str(module), "-File", str(sample)], capture_output=True, timeout=120, env=env)
            lines = [ln for ln in out.stdout.decode("utf-8", errors="replace").splitlines() if ln.startswith("{")]
            self.assertTrue(lines, out.stdout.decode("cp866", errors="replace") + out.stderr.decode("cp866", errors="replace"))
            result = json.loads(lines[-1])
        self.assertTrue(result["fixed"], "пути PowerShell 7 не убраны")
        self.assertFalse(result["again"], "повторная чистка снова что-то меняет")
        parts = result["path"].split(";")
        self.assertFalse([p for p in parts if "\\PowerShell\\" in p and "WindowsPowerShell" not in p], parts)
        self.assertTrue(parts[0].endswith("WindowsPowerShell\\Modules"), parts)
        self.assertIn("D:\\Other\\Modules", parts, "посторонние пути 5.1 не выбрасываются")
        self.assertEqual(1, len([p for p in parts if p.rstrip("\\").lower().endswith("program files\\windowspowershell\\modules")]),
                         "свой путь не дублируется")
        self.assertEqual(expected, result["sha"], "SHA-256 средствами .NET")
        self.assertIsNone(result["missing"], "нет файла — пусто, а не исключение")

        import importlib.util
        spec = importlib.util.spec_from_file_location("configurator", setup_dir.parent / "_Исходники" / "CAD_Workstation_Configurator.py")
        configurator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(configurator)
        polluted = {"PSMODULEPATH": r"C:\Program Files\PowerShell\7\Modules", "SYSTEMROOT": r"C:\Windows",
                    "PROGRAMFILES": r"C:\Program Files", "OTHER": "1"}
        env = configurator.engine_env(polluted)
        keys = [k for k in env if k.upper() == "PSMODULEPATH"]
        self.assertEqual(["PSModulePath"], keys, "один ключ пути модулей")
        self.assertNotIn("PowerShell\\7", env["PSModulePath"])
        self.assertEqual("1", env["OTHER"], "прочее окружение сохраняется")
        ps51 = os.path.join(os.environ.get("SYSTEMROOT", r"C:\Windows"), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
        if os.path.isfile(ps51):
            self.assertEqual(os.path.normcase(ps51), os.path.normcase(configurator.build_command("S.ps1", "И", "", "Skip")[0]),
                             "окно запускает powershell из PATH, а не 5.1 по полному пути")
        import inspect
        self.assertRegex(inspect.getsource(configurator), r"Popen\([^)]*env=engine_env\(\)",
                         "окно передаёт движку PSModulePath PowerShell 7")
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / configurator.ENGINE).write_text("old", encoding="utf-8")
            (Path(tmp) / "_Служебное").mkdir()
            (Path(tmp) / "_Служебное" / configurator.ENGINE).write_text("new", encoding="utf-8")
            self.assertEqual(str(Path(tmp) / "_Служебное" / configurator.ENGINE), configurator.find_engine(tmp, tmp),
                             "выпуск поверх старой папки: окно запускает старый движок рядом с собой")

    def test_T0_mprop_ini_cleanup_flag_untouched(self):
        """T0: надстройка не пишет MProp.ini — первая строка там флаг MProp «Очистка свойств», в репозитории он выключен (Д-29)."""
        uses = {}
        for src in addin_sources():
            code = [ln.split("//")[0] for ln in src.read_text(encoding="utf-8").splitlines() if not ln.strip().startswith(("///", "//", "*"))]
            n = sum(1 for ln in code if "MProp.ini" in ln)
            if n:
                uses[src.name] = n
        self.assertEqual({"SettingsForm.cs": 1}, uses, "MProp.ini упоминается в коде только как маркёр каталога SWPlus")
        form = (ADDIN / "SettingsForm.cs").read_text(encoding="utf-8")
        self.assertRegex(form, r'File\.Exists\(markerIni\)', "маркёр только проверяется на существование")
        first_line = (paths.SWPLUS / "MProp" / "MProp.ini").read_bytes().decode("cp1251").split("\r\n")[0]
        self.assertEqual("0", first_line, "MProp.ini: флаг «Очистка свойств» (MIni1) включён")

    def test_T0_every_setting_is_used(self):
        """T0: каждое поле Core.Settings влияет на поведение — нет «фиктивных флажков» (Д-25)."""
        settings_cs = ADDIN / "Core" / "Settings.cs"
        fields = re.findall(r"^\s*public (?:bool|int|string) (\w+)\b(?!\s*\()", settings_cs.read_text(encoding="utf-8"), flags=re.M)
        self.assertGreater(len(fields), 10, "поля настроек не найдены")
        others = "\n".join(strip_csharp_literals(p.read_text(encoding="utf-8")) for p in addin_sources() if p != settings_cs)
        unused = [f for f in fields if not re.search(r"\.%s\b" % f, others)]
        self.assertEqual([], unused, "настройки читаются из реестра, но ни на что не влияют")

    def test_T0_addin_has_no_silent_catch_and_single_write_point(self):
        """T0: ни одного «глотающего» catch; Add3/Delete2 вызываются только в PropertyWriter (Д-12)."""
        silent = []
        for src in addin_sources():
            text = strip_csharp_literals(src.read_text(encoding="utf-8"))
            for m in re.finditer(r"\bcatch\b\s*(?:\(\s*([\w.]+)(?:\s+\w+)?\s*\))?\s*\{", text):
                body = text[m.end():matching_brace(text, m.end() - 1)]
                exception_type = m.group(1)
                broad = exception_type in (None, "Exception", "System.Exception")
                handled = re.search(r"\bLog\.|\bthrow\b|MessageBox\.Show", body)
                if (broad and not handled) or (not broad and not body.strip()):
                    line = text.count("\n", 0, m.start()) + 1
                    silent.append(f"{src.relative_to(ADDIN)}:{line} catch ({exception_type or '*'})")
        self.assertEqual([], silent, "catch без журнала, повторного выброса или сообщения")
        writers = sorted({src.name for src in addin_sources()
                          if re.search(r"\.(Add3|Delete2|Set2)\(", src.read_text(encoding="utf-8"))})
        self.assertEqual(["PropertyWriter.cs"], writers, "свойства пишутся в обход PropertyWriter")

    def test_T0_golden_master_rules_are_strict(self):
        """T0: правило golden master без срабатываний проваливает R01, если в нём нет «optional» с причиной; правила замены
        записи материала ограничивают прежнее значение, а не только новое (Д-43)."""
        from baseline import compare
        rules = [{"id": "R-used", "names": ["Масса_ФБ"]},
                 {"id": "R-unused", "names": ["Проверил"]},
                 {"id": "R-optional", "names": ["Контора"], "optional": "в снимке v5 этого различия нет"}]
        diffs = [{"scenario": "A01_open_save", "section": "persisted", "level": "00", "name": "Масса_ФБ",
                  "old": None, "new": "<FONT size=3.5>0,63"}]
        unexplained, used = compare.explain(diffs, rules)
        self.assertEqual([], unexplained)
        self.assertEqual(["R-unused"], compare.unused(used, rules), "правило без срабатываний и без причины")

        real = json.loads((paths.TESTS / "baseline" / "allowed_diffs.json").read_text(encoding="utf-8"))["rules"]
        by_id = {r["id"]: r for r in real}
        self.assertEqual(len(real), len(by_id), "повторяющиеся id правил")
        for rule in real:
            self.assertTrue(rule.get("defect") and rule.get("title"), f"{rule['id']}: нет дефекта или названия")
            if "optional" in rule:
                self.assertTrue(str(rule["optional"]).strip(), f"{rule['id']}: «optional» без причины")
        for rid in ("AD-17", "AD-21", "AD-22"):
            self.assertIn("old", by_id[rid], f"{rid} не ограничивает прежнее значение")
        mprop_fraction = "<FONT size=1.8> <FONT size=3.5>Лист <STACK size=1>Б-ПН-НО-5,0 ГОСТ 19903-2015<OVER>09Г2С-12 ГОСТ 19281-2014</STACK>"
        legacy_fraction = " <FONT size=1.8><FONT size=3.5><STACK size=1>Лист 4<OVER>Ст3</STACK>"
        for rid in ("AD-21", "AD-22"):
            self.assertIsNone(re.fullmatch(by_id[rid]["old"], mprop_fraction, flags=re.S), f"{rid} объяснил бы замену дроби MProp")
            self.assertIsNotNone(re.fullmatch(by_id[rid]["old"], legacy_fraction, flags=re.S), f"{rid}: запись прежней надстройки")
        self.assertIsNone(re.fullmatch(by_id["AD-17"]["old"], mprop_fraction, flags=re.S), "AD-17: прежнее значение — строка v5")

    def test_T0_property_name_literals_are_declared(self):
        """T0: имя свойства литералом в коде надстройки — из словаря SWPlus, служебных имён SWPlus, лишних имён v5, имён
        надстройки или шаблона (PropertyDictionary); белый список M01 — словарь, служебные имена и имена надстройки (А-5)."""
        dictionary_cs = (ADDIN / "Core" / "PropertyDictionary.cs").read_text(encoding="utf-8")
        consts = {}
        for src in addin_sources():
            consts.update(re.findall(r'const string (\w+) = "([^"]*)"', src.read_text(encoding="utf-8")))

        def declared(array):
            m = re.search(r"public static readonly string\[\] %s = new string\[\]\s*\{(.*?)\};" % array, dictionary_cs, flags=re.S)
            self.assertIsNotNone(m, f"PropertyDictionary.{array} не найден")
            body = m.group(1)
            return set(re.findall(r'"([^"]+)"', body)) | {consts[ref.split(".")[-1]] for ref in re.findall(r"\b[A-Z]\w*\.\w+", body)}

        groups = {a: declared(a) for a in ("DefaultNames", "SwPlusServiceNames", "LegacyExtraNames", "AddinNames",
                                           "TemplateNames", "ExtraNames", "CutListNames", "LateNames")}
        self.assertEqual({"Материал_Строка", "Формат_до_БЧ", "Примечание_до_БЧ", "Запись_БЧ"}, groups["AddinNames"])
        known = set().union(*groups.values())

        call = re.compile(r'\b(?:Set|SetIfEmpty|Raw|Resolved|Delete|Exists|Get)\(\s*(?:"[^"]*"|[^,()"]*)\s*,\s*"([^"]+)"')
        live = re.compile(r'\bRestoreLive\(\s*\w+\s*,\s*\w+\s*,\s*"([^"]+)"')
        loop = re.compile(r'foreach \(string (\w+) in new\[\] \{([^}]*)\}\)')
        used = {}
        for src in addin_sources():
            text = src.read_text(encoding="utf-8")
            names = set(call.findall(text)) | set(live.findall(text))
            names |= {v for k, v in re.findall(r'const string (\w+Property) = "([^"]*)"', text)}
            for m in loop.finditer(text):
                body = text[m.end():m.end() + 400]
                if re.search(r"\b(?:Set|SetIfEmpty|Raw|Resolved|Delete)\([^;]*?,\s*%s\b" % m.group(1), body):
                    names |= set(re.findall(r'"([^"]+)"', m.group(2)))
            for name in names:
                used.setdefault(name, set()).add(src.name)
        self.assertTrue({"Исполнение", "Материал_Строка", "Масса", "Наименование_ВП", "Разраб."} <= set(used), sorted(used))
        self.assertEqual({}, {n: sorted(f) for n, f in used.items() if n not in known}, "имена свойств вне PropertyDictionary")

        from tests import test_e2e_model
        allowed = groups["DefaultNames"] | groups["SwPlusServiceNames"] | groups["AddinNames"]
        self.assertEqual(set(), test_e2e_model.ALLOWED_NEW - allowed, "M01 разрешает имена вне словаря и имён надстройки")

    @tags("smoke")
    def test_T0_material_library_fields_consistent(self):
        """T0: в библиотеке материалов дробь «Обозначение_ГОСТ» согласована с «Сортаментом» и «Обозначением_Строкой» (Д-27)."""
        from eskd_e2e import build

        def plain(markup):
            text = re.sub(r"<OVER>", " / ", markup, flags=re.I)
            text = re.sub(r"</?(STACK|FONT)[^>]*>", "", text, flags=re.I)
            return re.sub(r"\s+", " ", text).strip()

        wrong = {}
        for name, material in build.material_library().items():
            custom = material["custom"]
            fraction = custom.get("Обозначение_ГОСТ") or ""
            sortament = (custom.get("Сортамент") or "").strip()
            line = (custom.get("Обозначение_Строка") or "").strip()
            size = (custom.get("Типоразмер") or "").strip()
            problems = []
            m = re.match(r"^(.*?)<STACK[^>]*>(.*?)<OVER>", fraction, flags=re.I)
            if m and sortament:
                numerator = (m.group(1).strip() + " " + m.group(2).strip()).strip()
                if numerator != sortament:
                    problems.append(f"числитель «{numerator}» ≠ Сортамент «{sortament}»")
            if fraction and line and plain(fraction) != line:
                problems.append(f"«{plain(fraction)}» ≠ Обозначение_Строка «{line}»")
            if fraction.upper().startswith("<STACK"):
                # Форма MProp берёт форму как Left$(s, InStr(s, "<") - 2): с дробью в начале строки длина отрицательна.
                problems.append("дробь с начала строки: перед <STACK нужна форма и пробел или хотя бы пробел (разбор MProp)")
            if size and size not in name:
                problems.append(f"Типоразмер «{size}» не входит в имя")
            if problems:
                wrong[name] = problems
        self.assertEqual({}, wrong)

    def test_T0_stock_material_follows_owner_rule(self):
        """T0 (Р-8, решение владельца 20.09.2026): материал по геометрии подставляется молча только тогда, когда
        подходящий один; выбор конструктора при расхождении не переписывается; назначение идёт из очереди простоя,
        а не из обработчика сохранения; модель не откатывается — в откаченном состоянии материал телу не назначить."""
        core = (ROOT / "03_Макросы_и_Плагины" / "ESKD_Material_Sync_Addin" / "Core" / "StockCatalog.cs").read_text(encoding="utf-8")
        service = (ROOT / "03_Макросы_и_Плагины" / "ESKD_Material_Sync_Addin" / "Sw" / "StockService.cs").read_text(encoding="utf-8")
        sync = (ROOT / "03_Макросы_и_Плагины" / "ESKD_Material_Sync_Addin" / "Sw" / "SyncService.cs").read_text(encoding="utf-8")
        hub = (ROOT / "03_Макросы_и_Плагины" / "ESKD_Material_Sync_Addin" / "Sw" / "EventHub.cs").read_text(encoding="utf-8")

        self.assertNotIn("SolidWorks.Interop", core, "подбор материала должен быть чистой логикой — иначе его не проверить юнит-тестом")
        # Упоминание в комментарии — это объяснение запрета; ищем именно вызов.
        code = "\n".join(ln.split("//")[0] for ln in service.splitlines() if not ln.strip().startswith(("///", "//", "*", "/*")))
        self.assertNotIn(".AccessSelections(", code, "откат модели запрещён: в откаченном состоянии материал телу не назначить")

        # Молча — только при единственном подходящем (и когда материала нет, и когда он не тот — решение 22.09.2026):
        # Chosen заполняется в ветке Single и больше нигде.
        self.assertEqual(1, service.count("finding.Chosen = "), "материал выбирается за конструктора не в одном месте")
        single = re.search(r"if \(finding\.Match\.Single\)\s*\{(.*?)\}", service, flags=re.S)
        self.assertIsNotNone(single, "ветка «подходящий один» не найдена")
        self.assertIn("finding.Chosen = finding.Match.First", single.group(1), "молча подставляется не единственный подходящий")

        # Расхождение: один подходящий — замена, несколько — вопрос; решение на месте, а не уведомление.
        self.assertIn("finding.Verdict = assigned ? StockVerdict.Replace : StockVerdict.Assign", service,
                      "при расхождении с единственным подходящим материал не заменяется")
        self.assertNotIn("Mismatch", service, "осталось уведомление без замены")

        # Запись документа — из очереди простоя, а не из обработчика сохранения.
        inspect = re.search(r"private static void InspectStock\(.*?\n        \}", sync, flags=re.S)
        self.assertIsNotNone(inspect, "InspectStock не найден")
        self.assertNotIn("StockService.Apply", inspect.group(0), "материал назначается прямо при сохранении")
        self.assertIn('Kind = "stock"', hub, "задача подбора не ставится в очередь простоя")

    def test_T0_fixture_materials_follow_library(self):
        """T0: по манифесту фикстур у деталей из проката корпуса А и у копий корпуса Б — сортамент из корпоративной библиотеки,
        у стандартных и покупных изделий сортамента нет; копии корпуса Б сделаны из текущих исходных файлов и не изменены
        (решение владельца 13.09.2026; материалы в самих файлах проверяет I07). Детали из листа построены листовым металлом:
        вытянутая пластина из «Лист …» не даёт DXF развёртки, и изделие с ней не проходит проверку (23.09.2026, G05)."""
        from eskd_e2e import build

        def digest(path):
            return hashlib.sha256(Path(path).read_bytes()).hexdigest()

        library = build.material_library()
        manifest = json.loads(paths.FIXTURE_MANIFEST.read_text(encoding="utf-8"))
        wrong = []
        for fid, item in manifest["fixtures"].items():
            kind = item.get("kind")
            if kind in ("part", "weldment") and item.get("material") not in library:
                wrong.append(f"{fid}: у детали из проката «{item.get('material')}» — не сортамент библиотеки")
            if str(item.get("material") or "").startswith("Лист ") and not item.get("sheet_metal"):
                wrong.append(f"{fid}: деталь из листа построена не листовым металлом — fixtures/build_fixtures.py --sheet-metal")
            if kind in ("standard", "purchased"):
                if "material_sw" not in item:
                    wrong.append(f"{fid}: материал изделия не записан в манифест")
                elif item["material_sw"] in library:
                    wrong.append(f"{fid}: у изделия «{kind}» сортамент библиотеки «{item['material_sw']}»")
        corpus = manifest.get("corpus_b") or {}
        self.assertIn("B-01", corpus, "нет копии реальной трубы B-01 с сортаментом из библиотеки")
        for fid, item in corpus.items():
            wrong += [f"{fid} «{cfg}»: «{m}» — не сортамент библиотеки" for cfg, m in item["materials"].items() if m not in library]
            if digest(paths.ROOT / item["source"]) != item["source_sha256"]:
                wrong.append(f"{fid}: исходный файл корпуса Б изменился после сборки копии — запустите fixtures/build_corpus_b.py")
            if digest(paths.FIXTURES_B / item["file"]) != item["sha256"]:
                wrong.append(f"{fid}: копия детали изменена после сборки")
            if item.get("drawing") and digest(paths.FIXTURES_B / item["drawing"]) != item["drawing_sha256"]:
                wrong.append(f"{fid}: копия чертежа изменена после сборки")
        self.assertEqual([], wrong)

    def test_T0_dprop_form_opening_does_not_change_drawing(self):
        """T0 (Д-64, решение владельца 19.09.2026): открытие формы DProp чертёж не меняет — в UserForm_Activate нет
        SheetsControl (переименование листов, ЛРИ, «Листов»); нумерацию делает «Исправить оформление чертежа»."""
        text = (ROOT / "03_Макросы_и_Плагины" / "Макросы_SW_ZTool" / "_VBA_выгрузка" / "DProp" / "FrmDProp.frm.txt"
                ).read_text(encoding="utf-8")
        activate = re.search(r"(?ms)^Public Sub UserForm_Activate\(\)$(.*?)^End Sub$", text).group(1)
        self.assertNotRegex(activate, r"(?m)^\s*SheetsControl\s*$", "открытие формы переименовывает листы")
        standard = re.search(r"(?ms)^Private Sub CmdStandard_Click\(\).*?$(.*?)^End Sub$", text).group(1)
        self.assertIn("SheetsControl", standard, "«Исправить оформление чертежа» нумерует листы")

    def test_T0_audit_wave1_guards(self):
        """T0 (глубокий аудит 19.09.2026, волна 1): правила, без которых данные теряются молча.
        Документ SolidWorks внутри процесса не освобождается ReleaseComObject (его RCW держит EventHub); ревизия
        проверяет «только чтение» и результат сохранения; книга ЛЗК и PDF листов участков заменяются только готовыми;
        установщик не ставит неопубликованный выпуск и не закрывает SolidWorks с несохранёнными документами."""
        allowed = {"ExcelPdf.cs", "ShellThumbnail.cs", "TubeAxis.cs"}  # чужие COM-объекты: Excel, оболочка, элемент дерева
        released = sorted(src.name for src in (ADDIN / "Sw").glob("*.cs")
                          if "ReleaseComObject" in src.read_text(encoding="utf-8") and src.name not in allowed)
        self.assertEqual([], released, "ReleaseComObject на документе SolidWorks внутри процесса")
        revision = (ADDIN / "Sw" / "RevisionService.cs").read_text(encoding="utf-8")
        self.assertIn("IsOpenedReadOnly()", revision, "ревизия отказывает документу только для чтения")
        self.assertRegex(revision, r"string saveProblem = Write\(", "ревизия проверяет, сохранился ли штамп")
        lzk = (ADDIN / "Sw" / "LzkService.cs").read_text(encoding="utf-8")
        self.assertLess(lzk.index("File.Copy(_tempWorkbook, fresh"), lzk.index("ReplacePrevious(fresh)"),
                        "новая книга ЛЗК записана до того, как прежняя уехала в архив")
        self.assertIn("File.Copy(previous, _workbookPath", lzk, "не встала новая книга — прежняя возвращается на место")
        self.assertIn("inputs.ReadError = ex.Message", (ADDIN / "Core" / "LzkBook.cs").read_text(encoding="utf-8"),
                      "нечитаемая прежняя книга не затирается молча")
        ready = (ADDIN / "Sw" / "ReadyService.cs").read_text(encoding="utf-8")
        self.assertLess(ready.index("ExcelPdf.Export(workbook, staged"), ready.index("File.Move(target, archive)"),
                        "PDF листов участков сделаны до того, как прежние уехали в архив")
        setup = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("GetSaveFlag()", setup, "установщик видит несохранённые документы")
        self.assertRegex(setup, r'\$release\.Version -eq "рабочая копия" -and -not \$sandbox -and -not \$AllowUnpublished',
                         "без опубликованного выпуска установка из общей папки не идёт")
        self.assertNotRegex(" ".join(src.read_text(encoding="utf-8") for src in addin_sources()),
                            r"Author \?\? Environment\.UserName", "Author пустой, а не null: нужен Settings.AuthorOrUser()")

    def test_T0_audit_wave2_guards(self):
        """T0 (глубокий аудит 19.09.2026, волны 2–3): скорость на NAS и одно правило «покупное».
        Словарь SWPlus и значения свойств не перечитываются на каждое обращение; опрос кнопки «Новая ревизия» не читает
        сеть в потоке SolidWorks; выгрузка открывает чертёж один раз и возвращает настройки DXF; «покупное» решает
        ComponentKind; установщик запускает повышенные процессы из %TEMP% и проверяет чужие пути в профиле."""
        sync = (ADDIN / "Sw" / "SyncService.cs").read_text(encoding="utf-8")
        self.assertIn("DictionaryRecheck", sync, "словарь SWPlus кешируется")
        writer = (ADDIN / "Sw" / "PropertyWriter.cs").read_text(encoding="utf-8")
        self.assertIn("_values.Clear()", writer, "кеш значений сбрасывается при записи")
        addin = (ADDIN / "SwAddin.cs").read_text(encoding="utf-8")
        self.assertIn("RevisionService.AvailableForButton(_app)", addin, "опрос кнопки ревизии — из памяти")
        revision = (ADDIN / "Sw" / "RevisionService.cs").read_text(encoding="utf-8")
        self.assertNotIn("IssuedQuick", revision, "опрос не читает отчёты выдачи в потоке SolidWorks")
        self.assertIn("IssuedFlags.Get(", revision, "признак «выдан» для опроса — фоновый")
        for name in ("ReadyService.cs", "CloseOrderService.cs"):
            self.assertIn("RevisionService.ForgetIssued()", (ADDIN / "Sw" / name).read_text(encoding="utf-8"),
                          name + ": после выдачи или закрытия заказа запомненный ответ кнопки сбрасывается")
        export = (ADDIN / "Sw" / "ExportService.cs").read_text(encoding="utf-8")
        self.assertEqual(1, export.count("swDocumentTypes_e.swDocDRAWING,"), "чертёж открывается в одном месте")
        self.assertIn("восстановление настройки DXF", export, "настройки DXF пользователя возвращаются")
        services = {name: (ADDIN / "Sw" / name).read_text(encoding="utf-8")
                    for name in ("ProductReviewService.cs", "ExportService.cs", "LzkService.cs")}
        self.assertEqual([], [n for n, text in services.items() if "ComponentKind.IsPurchased(" not in text],
                         "проверка (состав изделия собирает ProductReviewService), выгрузка и ЛЗК решают «покупное» одним правилом")
        etalon = (ADDIN / "Sw" / "EtalonService.cs").read_text(encoding="utf-8")
        self.assertIn("ReadManifest(snapshot) ?? State(snapshot)", etalon, "прежние снимки сравниваются по манифесту")
        setup = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("ESKD_Drew_", setup, "установщик Drew — из копии в %TEMP%")
        self.assertIn("ESKD_SwInternetBlock_", setup, "SwInternetBlock — из копии в %TEMP%")
        self.assertNotIn("$cnt -ge 300", setup, "порог правил — по файлам этого ПК, а не 300")
        self.assertIn("Find-EskdForeignPaths", setup, "профиль реестра проверяется на чужие пути")
        # R01 19.09: закрытие документа посреди отложенного пересохранения роняет SolidWorks
        session = (ROOT / "09_Тесты" / "eskd_e2e" / "session.py").read_text(encoding="utf-8")
        close = session[session.index("    def close(self, doc):"):session.index("    def close_all(self):")]
        self.assertIn("self.wait_addin_idle()", close, "сессия ждёт отложенные задачи надстройки перед закрытием")
        self.assertIn("public int PendingIdleTasks()", addin, "надстройка сообщает число отложенных задач")

    def test_T0_fixture_corpus_a_matches_manifest(self):
        """T0 (Д-44): файлы корпуса А совпадают с хешами манифеста, манифест помнит шаблоны, из которых корпус собран.
        Корпус А сознательно собран из шаблонов до нормализации (13.09.2026): так выглядят модели, уже лежащие в заказах,
        и на них держатся проверки прежних алиасов (Д-18, I03). Текущие шаблоны проверяют модели, которые тесты строят
        на лету (build.plate, X05, контракт C*). Изменённый файл корпуса — пересборка fixtures/build_fixtures.py."""

        def digest(path):
            return hashlib.sha256(Path(path).read_bytes()).hexdigest()

        manifest = json.loads(paths.FIXTURE_MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual({paths.PART_TEMPLATE.name, paths.ASSEMBLY_TEMPLATE.name, paths.DRAWING_TEMPLATE.name},
                         set(manifest["templates"]), "манифест помнит шаблоны сборки корпуса")
        wrong = []
        for fid, item in manifest["fixtures"].items():
            hashes = item["sha256"] if isinstance(item.get("sha256"), dict) else {item["file"]: item.get("sha256")}
            for name, expected in hashes.items():
                path = paths.FIXTURES_A / name
                if not path.is_file():
                    wrong.append(f"{fid}: нет файла {name}")
                elif digest(path) != expected:
                    wrong.append(f"{fid}: {name} изменён после сборки корпуса")
        self.assertEqual([], wrong)

    def test_T0_document_templates_in_single_order(self):
        """T0 (решение владельца 24.09.2026: «поставь тот же порядок свойств в шаблонах деталей и сборки»): свойства
        шаблонов детали и сборки на каждом уровне стоят в едином порядке — так, как их ставит надстройка при сохранении.
        Первое сохранение новой детали ничего не переставляет. Читается сам файл шаблона, без SolidWorks."""
        from eskd_e2e import offline_props, oracles

        master, tail = oracles.property_master(), oracles.property_tail()
        expected = {
            paths.PART_TEMPLATE: [["Обозначение", "Наименование", "Масса", "Материал", "Формат"], ["UNIT_OF_MEASURE"]],
            paths.ASSEMBLY_TEMPLATE: [["Обозначение", "Наименование", "Масса", "Формат"],
                                      ["Сборка1_ФБ", "UNIT_OF_MEASURE", "Раздел"]],
        }
        for template, levels in expected.items():
            props = offline_props.read(template)
            names = [[n for n, _ in level] for level in [props["general"]] + props["configs"]]
            self.assertEqual(levels, names, f"{template.name}: общие, затем исполнение «00»")
            for level in names:
                self.assertEqual(oracles.canonical(level, master, tail)[0], level, f"{template.name}: единый порядок")

    def test_T0_dll_built_from_current_sources(self):
        """T0: build_manifest.json — хеши исходников совпадают с текущими файлами (Д-24)."""
        if not paths.ADDIN_DLL.exists():
            need_built_addin(self, "надстройка не собрана: запустите build.ps1")
        manifest_path = ADDIN / "build_manifest.json"
        self.assertTrue(manifest_path.exists(), "нет build_manifest.json — DLL собрана не build.ps1")
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        stale = [rel for rel, sha in manifest["sources"].items()
                 if hashlib.sha256((ADDIN / rel).read_bytes()).hexdigest() != sha]
        self.assertEqual([], stale, "исходники изменены после сборки DLL")
        dll_sha = hashlib.sha256(paths.ADDIN_DLL.read_bytes()).hexdigest()
        self.assertEqual(manifest["outputs"]["ESKD_Material_Sync_v5.dll"], dll_sha, "DLL не из этой сборки")

    @known_defect("Д-23")
    def test_T0_reg_profile_has_no_unescaped_backslashes(self):
        """T0: профиль .reg — «\\» экранированы, нет EULA, личных каталогов и недавних файлов, избранные материалы из библиотеки ГОСТ (Д-23)."""
        from eskd_e2e import build
        reg = ROOT / "01_Настройки_SolidWorks" / "Реестровые_Профили" / "01_SW2025_Корпоративный_Стандарт_ЕСКД.reg"
        text = reg.read_bytes().decode("utf-16")
        problems, key, sections = [], "", Counter()
        favorites, favorites_declared, seen = [], None, {}
        for i, line in enumerate(text.splitlines(), 1):
            if line.startswith("["):
                key = line
                sections[key] += 1
                continue
            m = re.match(r'^"((?:[^"\\]|\\.)*)"=(.*)$', line)
            if not m:
                continue
            name, value = m.group(1), m.group(2)
            # повторный раздел в конце профиля допустим, но не должен молча переопределять значение другим
            if (key, name) in seen and seen[(key, name)] != value:
                problems.append(f"{i}: {name} в {key} переопределяет значение другим")
            seen[(key, name)] = value
            if value.startswith('"') and re.search(r'\\(?![\\"])', value[1:-1].replace("\\\\", "")):
                problems.append(f"{i}: одиночная «\\» в {name}")
            if name.startswith("EULA Accepted"):
                problems.append(f"{i}: принятие EULA чужой учётной записью")
            if re.search(r"(?i)[a-z]:\\\\users\\\\", value):
                problems.append(f"{i}: личный каталог в {name} (нужен %USERPROFILE%)")
            if name in ("JumpListFileName", "Last user path", "AutoCenterMass") or re.match(r"^document\d+$", name):
                problems.append(f"{i}: {name}")
            if key.endswith("SOLIDWORKS 2025\\Material]"):
                if name.startswith("Favorite Material "):
                    favorites.append(value.strip('"'))
                elif name == "__NumOfFavs":
                    favorites_declared = int(value.split(":")[1], 16)
        problems += [k for k in sections if "Recent Command Searches" in k]
        library = paths.MATERIAL_DB.read_bytes().decode("utf-16")
        known = set(build.material_library())
        for favorite in favorites:
            db, material, matid = favorite.split("|")
            if db != paths.MATERIAL_DB.stem or material not in known or \
                    not re.search(r'<material name="%s"[^>]*matid="%s"' % (re.escape(material), matid), library):
                problems.append(f"избранный материал не из библиотеки ГОСТ: {favorite}")
        if favorites_declared != len(favorites):
            problems.append(f"__NumOfFavs = {favorites_declared}, записей {len(favorites)}")
        setup = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "EskdDeploy.psm1").read_text(encoding="utf-8-sig")
        if "%USERPROFILE%" in text and "'%USERPROFILE%'" not in setup:
            problems.append("Setup не подставляет %USERPROFILE%")
        self.assertEqual([], problems[:30])

    @known_defect("Д-19")
    def test_T0_property_tab_templates_use_dictionary_names(self):
        """T0: шаблоны вкладки свойств — словарные имена SWPlus на уровнях MProp, живые масса и материал только для чтения, без личных данных (Д-19)."""
        import xml.etree.ElementTree as ET
        dictionary = set(paths.SWPLUS_DICTIONARY.read_bytes().decode("cp1251").split("\r\n")[:43])
        live = {"Материал": "SW-Material", "Масса": "SW-Mass"}
        general = {"Обозначение", "Наименование", "Наименование_ФБ", "Конструктор"} | set(live)
        configuration = {"Проверил", "Контора", "Литера_ФБ", "Техконтроль", "Нормоконтроль", "Утвердил", "Начальник",
                         "Масса_ФБ", "Материал_ФБ", "Сборка1_ФБ", "Сборка2_ФБ"}
        templates = sorted(paths.PROPERTY_TAB_TEMPLATES.glob("*.prtprp"))
        self.assertTrue(templates, "нет шаблонов вкладки свойств")
        wrong = {}
        for prp in templates:
            text = prp.read_bytes().decode("utf-8-sig")
            problems = [f"личные данные «{m}»" for m in ("Лунин", "Home Made") if m in text]
            for control in ET.fromstring(text).iter("Control"):
                name, apply_to = control.get("PropName"), control.get("ApplyTo")
                if name not in dictionary and name not in live:
                    problems.append(f"{name}: имя вне словаря")
                if name in general and apply_to != "Global" or name in configuration and apply_to != "Config":
                    problems.append(f"{name}: уровень {apply_to}")
                if name in live and (control.get("DefaultValue") != live[name] or control.get("ReadOnly") != "True"):
                    problems.append(f"{name}: живое {live[name]} должно быть только для чтения")
            if problems:
                wrong[prp.name] = problems
        self.assertEqual({}, wrong)

    @known_defect("Д-21")
    def test_T0_single_sheet_format_set(self):
        """T0: один комплект форматок — «02_…/База шаблонов» убран, профиль реестра и конфигуратор на него не ссылаются (Д-21)."""
        self.assertFalse((ROOT / "02_Шаблоны_и_Форматки" / "База шаблонов").exists())
        reg = (ROOT / "01_Настройки_SolidWorks" / "Реестровые_Профили" / "01_SW2025_Корпоративный_Стандарт_ЕСКД.reg").read_bytes().decode("utf-16")
        configurator = (ROOT / "01_Настройки_SolidWorks" / "_Исходники" / "CAD_Workstation_Configurator.py").read_text(encoding="utf-8")
        self.assertNotIn("База шаблонов", reg)
        self.assertNotIn("База шаблонов", configurator)
        sheet_formats = set(re.findall(r'^"Sheet Format Folders"="([^"]*)"', reg, flags=re.M))
        self.assertTrue(sheet_formats, "в профиле нет Sheet Format Folders")
        self.assertTrue(all(v.endswith("02_Шаблоны_и_Форматки\\\\Основные надписи") for v in sheet_formats), sheet_formats)
        self.assertFalse((paths.SWPLUS / "Основные надписи").exists(), "основные надписи — в 02 рядом с шаблонами, не в папке макросов")
        master_ini = (paths.SWPLUS / "Master" / "Master.ini").read_bytes().decode("cp1251").split("\r\n")
        self.assertTrue(master_ini[3].endswith("02_Шаблоны_и_Форматки\\Основные надписи\\"), f"Master.ini строка 4: {master_ini[3]}")
        self.assertEqual(18, len(list(paths.SHEET_FORMATS.glob("*.slddrt"))), "комплект основных надписей SWPlus (A4-A-1 в архиве, Д-30)")

    @known_defect("Д-20")
    def test_T0_mcp_server_writes_no_legacy_aliases(self):
        """T0: MCP-сервер не пишет реквизиты сам — ни алиасов v5, ни граф по именам-литералам; запись через надстройку (Д-20, D-10)."""
        text = (ROOT / "03_Макросы_и_Плагины" / "SolidWorks_MCP_Server" / "server.py").read_text(encoding="utf-8")
        aliases = {a.strip('"') for a in LEGACY_ALIASES} | {"Автор", "DrawnDate", "п_Разраб_Дата", "п_Пров_Дата", "ГОСТ_Сортамента"}
        literals = set(re.findall(r'"([^"\\\r\n]+)"', text))
        self.assertEqual([], sorted(aliases & literals), "алиасы v5 в коде сервера")
        self.assertEqual([], re.findall(r'\.(?:Add3|Set2|Delete2)\(\s*"[^"]*"', text), "запись свойств с именами-литералами")
        self.assertIn('"SyncActiveDocumentSilent"', text, "синхронизация делегирована надстройке")

    @known_defect("Д-24")
    def test_T0_no_build_leftovers_and_reports_in_git(self):
        """T0: в git нет собранной надстройки, копий DLL (*_old, Legacy_Builds), отчётов TEST_REPORT_* и архива (Д-24)."""
        out = subprocess.run(["git", "-C", str(ROOT), "-c", "core.quotepath=off", "ls-files"], capture_output=True)
        files = out.stdout.decode("utf-8", errors="replace").splitlines()
        addin = "03_Макросы_и_Плагины/ESKD_Material_Sync_Addin/"
        built = {addin + name for name in ("ESKD_Material_Sync_v5.dll", "ESKD_Material_Sync_v5.tlb", "ESKD.exe", "ESKD_Sync.exe",
                                          "build_manifest.json")}
        leftovers = [f for f in files if f.endswith("_old") or "Legacy_Builds/" in f or "/TEST_REPORT_2" in f
                     or f.startswith("99_Архив/") or f in built]
        self.assertEqual([], leftovers)
        publish = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Publish-EskdToolkit.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("ESKD_Material_Sync_Addin\\build.ps1", publish, "надстройку собирает из исходников публикация")

    @known_defect("Д-31")
    def test_T0_registry_snapshot_survives_interrupted_run(self):
        """T0: прерванный прогон не оставляет тестовые подписи в ESKD_Settings — следующий снимок сначала восстанавливает
        ключ из резервной копии (песочница HKCU\\Software\\ESKD_RegistrationTest_*)."""
        import tempfile
        import uuid
        import winreg
        from eskd_e2e.guards import RegistrySnapshot

        subkey = r"Software\ESKD_RegistrationTest_Snapshot_" + uuid.uuid4().hex
        backup = Path(tempfile.gettempdir()) / (subkey.rsplit("\\", 1)[1] + ".json")

        def read():
            try:
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey) as key:
                    return {winreg.EnumValue(key, i)[0]: winreg.EnumValue(key, i)[1] for i in range(winreg.QueryInfoKey(key)[1])}
            except FileNotFoundError:
                return None

        try:
            with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, subkey, 0, winreg.KEY_WRITE) as key:
                winreg.SetValueEx(key, "Author", 0, winreg.REG_SZ, "Лунин В.И.")
                winreg.SetValueEx(key, "AutoCenterMass", 0, winreg.REG_DWORD, 1)
            original = read()

            interrupted = RegistrySnapshot(subkey, backup).capture()
            interrupted.apply({"Author": "Тестов Т.Т.", "Checker": "Проверкин П.П."})
            self.assertTrue(backup.exists(), "снимок не сохранён до записи тестовых значений")

            next_run = RegistrySnapshot(subkey, backup).capture()
            self.assertTrue(next_run.recovered)
            self.assertEqual(original, read(), "прерванный прогон не восстановлен при старте следующего")
            next_run.apply({"Author": "Тестов Т.Т."})
            next_run.restore()
            self.assertEqual(original, read())
            self.assertFalse(backup.exists(), "резервная копия осталась после восстановления")

            winreg.DeleteKey(winreg.HKEY_CURRENT_USER, subkey)
            missing = RegistrySnapshot(subkey, backup).capture()
            missing.apply({"Author": "Тестов Т.Т."})
            RegistrySnapshot(subkey, backup).capture().restore()
            self.assertIsNone(read(), "ключ, которого не было до прогона, не удалён")

            # Прерванный прогон, после которого человек сам поправил настройки: снимок не откатывает его правки.
            from eskd_e2e.guards import RegistryConflict
            test_values = {"Author": "Тестов Т.Т.", "Checker": "Проверкин П.П.", "Organization": "ООО «Испытание»"}
            with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, subkey, 0, winreg.KEY_WRITE) as key:
                winreg.SetValueEx(key, "Author", 0, winreg.REG_SZ, "Лунин В.И.")
            RegistrySnapshot(subkey, backup, test_values).capture().apply(test_values)
            with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, subkey, 0, winreg.KEY_WRITE) as key:
                winreg.SetValueEx(key, "Author", 0, winreg.REG_SZ, "Шалунов В.В.")
                winreg.SetValueEx(key, "Organization", 0, winreg.REG_SZ, "ТОО «Троя»")
            edited = read()
            with self.assertRaises(RegistryConflict):
                RegistrySnapshot(subkey, backup, test_values).capture()
            self.assertEqual(edited, read(), "ручные правки откатились к старому снимку")
            self.assertTrue(backup.exists(), "снимок удалён без решения человека")

            # Тестовые подписи остались — снимок возвращается.
            snap = RegistrySnapshot(subkey, backup, test_values)
            snap._load_backup()
            snap.apply(test_values)
            recovered = RegistrySnapshot(subkey, backup, test_values).capture()
            self.assertTrue(recovered.recovered)
            self.assertEqual("Лунин В.И.", read()["Author"])
            recovered.restore()
            self.assertFalse(backup.exists())
        finally:
            try:
                winreg.DeleteKey(winreg.HKEY_CURRENT_USER, subkey)
            except FileNotFoundError:
                pass
            backup.unlink(missing_ok=True)

    def test_T0_session_waits_for_own_solidworks_only(self):
        """T0 (23.09.2026, T05): после зависания stop() завершает свой SolidWorks через kill, и процесс ещё виден
        какое-то время. Новая сессия его дожидается, а не отказывает — иначе все следующие тесты прогона становятся
        ошибками. SolidWorks, запущенный не тестами (в том числе получивший номер нашего завершённого процесса),
        по-прежнему — немедленный отказ, и его никто не трогает. Вместо SolidWorks — дочерние процессы Python."""
        import sys
        import time
        import psutil
        from eskd_e2e import session

        def child(seconds):
            return subprocess.Popen([sys.executable, "-c", f"import time; time.sleep({seconds})"])

        children = []
        try:
            # Свой, завершается за время ожидания: старт дожидается, а не отказывает.
            leaving = child(2)
            children.append(leaving)
            session.remember_own(leaving.pid)
            session.wait_own_exit([psutil.Process(leaving.pid)], timeout=30)
            self.assertIsNotNone(leaving.poll(), "старт не дождался своего процесса")

            # Свой, но не завершается: отказ после ожидания, с номером процесса.
            stuck = child(60)
            children.append(stuck)
            session.remember_own(stuck.pid)
            with self.assertRaisesRegex(session.SessionRefused, str(stuck.pid)):
                session.wait_own_exit([psutil.Process(stuck.pid)], timeout=1)

            # Чужой: отказ сразу, без ожидания, процесс жив.
            foreign = child(60)
            children.append(foreign)
            started = time.time()
            with self.assertRaisesRegex(session.SessionRefused, "уже запущен"):
                session.wait_own_exit([psutil.Process(foreign.pid)], timeout=30)
            self.assertLess(time.time() - started, 5, "чужой SolidWorks не повод ждать")
            self.assertIsNone(foreign.poll(), "чужой процесс завершён")

            # Номер нашего процесса достался другому: время создания не то — чужой.
            session._OWN_SOLIDWORKS[foreign.pid] = psutil.Process(foreign.pid).create_time() - 100
            with self.assertRaisesRegex(session.SessionRefused, "уже запущен"):
                session.wait_own_exit([psutil.Process(foreign.pid)], timeout=30)
        finally:
            for p in children:
                session._OWN_SOLIDWORKS.pop(p.pid, None)
                if p.poll() is None:
                    p.kill()
                p.wait(10)

    def test_T0_vba_export_matches_swp(self):
        """T0: текстовая выгрузка модулей VBA пяти макросов SWPlus и SHA-256 в manifest.json совпадают с .swp (WP-0.2);
        после правки макроса выгрузку обновляет tools/export_vba.py в том же коммите."""
        import importlib.util
        spec = importlib.util.spec_from_file_location("export_vba", paths.TESTS / "tools" / "export_vba.py")
        export_vba = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(export_vba)
        self.assertEqual([], export_vba.check())

    def test_T0_destroy_notify_does_not_unsubscribe(self):
        """T0 (19.09.2026, M07 в длинном прогоне): обработчик DestroyNotify надстройки не снимает подписки — отписка внутри
        события меняет список подписчиков, который перебирает SolidWorks, и изредка роняет его при закрытии детали."""
        hub = (ADDIN / "Sw" / "EventHub.cs").read_text(encoding="utf-8-sig")
        body = re.search(r"private int OnDocDestroy\(DocState s, int destroyType\)\s*\{(.*?)\n        \}", hub, re.S).group(1)
        # Снятие задач вынесено в DropTasks (№9) — в нём тоже ни одной отписки.
        body += re.search(r"private void DropTasks\(DocState s, string text\)\s*\{(.*?)\n        \}", hub, re.S).group(1)
        code = "\n".join(line for line in body.splitlines() if not line.strip().startswith("//"))
        self.assertNotIn("Untrack", code)
        self.assertNotIn("-=", code)
        self.assertIn("s.Destroyed = true", code)
        for handler in ("OnDocSave(DocState s, string fileName)", "OnDocSavePost(DocState s, int saveType, string fileName)"):
            start = hub.index(handler)
            self.assertIn("if (s.Destroyed) return 0;", hub[start:start + 200], handler)

    def test_T0_idle_queue_cannot_spin(self):
        """T0 (23.09.2026, T05): очередь простоя надстройки не крутится без конца. Замена материала, которая не прижилась
        (материал тела перекрывает материал детали), пересохраняла деталь, FileSaveNotify снова ставил подбор, и OnIdle
        не возвращал управление SolidWorks — тот переставал отвечать, прогон e2e обрывался на T06."""
        hub = (ADDIN / "Sw" / "EventHub.cs").read_text(encoding="utf-8-sig")
        idle = re.search(r"private int OnIdle\(\)\s*\{(.*?)\n        \}", hub, re.S).group(1)
        self.assertRegex(idle, r"int count = _idle\.Count;\s*(//[^\n]*\s*)*while \(count-- > 0",
                         "OnIdle выполняет только задачи, стоявшие в очереди на входе")
        remember = re.search(r"private void Remember\(ModelDoc2 doc, SyncReport report\)\s*\{(.*?)\n        \}", hub, re.S).group(1)
        self.assertIn("if (report.StockNeedsWork && doc != null && !_resaving)", remember,
                      "собственное пересохранение надстройки не ставит подбор материала заново")

    def test_T0_swplus_macros_rebuild_from_original(self):
        """T0 (аудит 19.09, М-К2): исходный SWPlus из git + все правки ЕСКД по порядку (tools/swplus_apply_all.py) дают
        ровно макросы репозитория — правки воспроизводимы на новом выпуске SWPlus одной командой, без редактора VBA."""
        import importlib.util
        import sys
        sys.path.insert(0, str(paths.TESTS / "tools"))
        spec = importlib.util.spec_from_file_location("swplus_apply_all", paths.TESTS / "tools" / "swplus_apply_all.py")
        apply_all = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(apply_all)
        self.assertEqual([], apply_all.check())

    def test_T0_swplus_audit_macro_guards(self):
        """T0 (аудит 19.09, М-В2…М-В6): временные графа и строка SpecEditor убираются и при ошибке; точка привязки BOM меняется
        только у листа без точки или с точкой прежних форматок; графы — по заголовку; коды документов MProp и имя «Запись_БЧ»
        совпадают с надстройкой."""
        export = ROOT / "03_Макросы_и_Плагины" / "Макросы_SW_ZTool" / "_VBA_выгрузка"
        run = (export / "SpecEditor" / "SpecEditor_run.bas.txt").read_text(encoding="utf-8")

        def proc(text, name):
            m = re.search(r"\n(?:Public |Private )?(?:Sub|Function) " + name + r"\b.*?\nEnd (?:Sub|Function)", text, re.S)
            self.assertIsNotNone(m, name)
            return m.group(0)

        for name, cleanup in (("SwpExtraLines", "If bAdded Then ok = swTable.DeleteColumn(n)"),
                              ("SwpFitWidth", "If bRow Then ok = swTable.DeleteRow(t)"),
                              ("SwpBomAnchor", "swDraw.EditSheet")):
            body = proc(run, name)
            self.assertIn("On Error GoTo Failed", body, name)
            self.assertIn("Resume Cleanup", body, name)
            self.assertIn(cleanup, body.split("Cleanup:")[1], f"{name}: уборка после метки Cleanup")
        self.assertNotIn("swTable.Text(r, c) = sProbe", proc(run, "SwpFitWidth"), "пробная строка — не в ячейку детали")
        self.assertIn("SwpAt(vPos, 0.205, 0.068)", proc(run, "SwpBomAnchor"), "намеренная точка привязки не трогается")
        layout = proc(run, "SwpLayoutRecords")
        for title in ("ОБОЗНАЧ", "НАИМЕН", "КОЛ", "ПРИМ", "ПОЗ"):
            self.assertIn(f'SwpColumn(swTable, "{title}"', layout, title)
        form = (export / "SpecEditor" / "FrmSpecEditor.frm.txt").read_text(encoding="utf-8")
        self.assertIn("SwpBomAnchorWarn swDraw, swSheet", form, "форма предупреждает о неудаче точки привязки")

        bch = (ADDIN / "Core" / "BchRecord.cs").read_text(encoding="utf-8")
        prop = re.search(r'LinesProperty = "([^"]+)"', bch).group(1)
        self.assertIn(f'Private Const prpRecordBch As String = "{prop}"', run)
        self.assertEqual(1, run.count(f'"{prop}"'), "«Запись_БЧ» — одной константой")

        mprop = (export / "MProp" / "FrmMProp.frm.txt").read_text(encoding="utf-8")
        codes = re.search(r'DocCodes = "([^"]+)"', (ADDIN / "Core" / "NameParsing.cs").read_text(encoding="utf-8")).group(1)
        addin_codes = {c for c in codes.split("|") if "\\" not in c}
        vba = proc(mprop, "SwpIsDocCode")
        vba_codes = set(re.findall(r'"([А-ЯЁ]{2})"', re.search(r"Case (\"[^\n]+)", vba).group(1)))
        self.assertEqual(addin_codes, vba_codes, "коды документов MProp = NameParsing.DocCodes")
        self.assertIn('Like "Э#"', vba, "Э1…Э9 — как «Э\\d» надстройки")
        self.assertEqual(2, mprop.count('If UCase$(Trim$('), "«БЧ» без учёта регистра")
        self.assertNotIn('If Формат.Value = "БЧ"', mprop)
        self.assertNotIn('If Trim$(sFormat) = "БЧ"', mprop)

        # Перенос графы 1 MProp — те же 22 и 31 знак, что у надстройки (М-В7).
        fmt = (ADDIN / "Core" / "SwPlusFormat.cs").read_text(encoding="utf-8")
        big = re.search(r"TitleLineLimit = (\d+)", fmt).group(1)
        small = re.search(r"TitleSmallLineLimit = (\d+)", fmt).group(1)
        wrap = proc(mprop, "SwpWrapTitle")
        for piece in (f"Len(t) <= {big}", f"SwpWrapLines(t, {big}, n)", f"SwpWrapLines(t, {small}, n)"):
            self.assertIn(piece, wrap, "перенос графы 1 MProp = SwPlusFormat")

    def test_T0_documentation_matches_code(self):
        """T0: руководство описывает все параметры ESKD_Settings и кнопки вкладки и ссылается только на существующие тесты;
        README, руководство и окно настроек не повторяют утверждений v5; устаревшие документы помечены (WP-5.1)."""
        guide_path = ROOT / "06_Документация" / "РУКОВОДСТВО_ПОЛЬЗОВАТЕЛЯ_И_АДМИНИСТРАТОРА.md"
        guide = guide_path.read_text(encoding="utf-8")
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
        form = (ADDIN / "SettingsForm.cs").read_text(encoding="utf-8")

        settings = set(re.findall(r'(?:Int|Str)\(key, "(\w+)"', (ADDIN / "Core" / "Settings.cs").read_text(encoding="utf-8")))
        self.assertGreater(len(settings), 10, "параметры реестра в Settings.cs не найдены")
        self.assertEqual([], sorted(n for n in settings if f"`{n}`" not in guide), "параметры ESKD_Settings без описания в руководстве")
        buttons = [b for b in re.findall(r'AddCommandItem2\("([^"]*)"', (ADDIN / "SwAddin.cs").read_text(encoding="utf-8")) if b]
        self.assertEqual(12, len(buttons), buttons)
        self.assertEqual([], [b for b in buttons if f"**{b}**" not in guide], "кнопки вкладки ЕСКД без описания в руководстве")

        existing = set()
        for src in (paths.TESTS / "tests").glob("test_*.py"):
            existing |= set(re.findall(r"def test_([A-Z]\d{2})_", src.read_text(encoding="utf-8")))
        ids = r"[PMBDSRICGK]\d{2}(?:,\s*[PMBDSRICGK]\d{2})*"
        cited = set()
        for text in (guide, readme):
            for group in re.findall(r"\((%s)\)|\|\s*(%s)\s*\||тест[а-я]*\s+(%s)" % (ids, ids, ids), text):
                cited |= set(re.findall(r"[A-Z]\d{2}", " ".join(group)))
        self.assertGreater(len(cited), 20, "ссылки на тесты в документации не найдены")
        self.assertEqual([], sorted(cited - existing), "документация ссылается на несуществующие тесты")

        v5_claims = [r'\$PRPSHEET:"(?:Разраб\.|Пров\.|Организация)"', r"MaterialSyncEngine", r"SyncAssembly",
                     r"IsStandardOrPurchasedPart", r"центрир", r"перестроени", r"Синхронизировать ЕСКД", r"Синхронизация ТТ"]
        stale = {name: [p for p in v5_claims if re.search(p, text, flags=re.I)]
                 for name, text in (("README.md", readme), (guide_path.name, guide), ("SettingsForm.cs", form))}
        self.assertEqual({}, {k: v for k, v in stale.items() if v}, "утверждения надстройки v5 в действующих документах")

        markers = re.compile(r'MaterialSyncEngine|home-pc|База шаблонов|Деталь ГОСТ\.prtdot|A4-A-1|Синхронизировать ЕСКД|\$PRPSHEET:"Разраб\."')
        unmarked = []
        for doc in sorted((ROOT / "06_Документация").glob("*.md")):
            text = doc.read_text(encoding="utf-8")
            if doc != guide_path and markers.search(text) and "**Исторический документ**" not in "\n".join(text.splitlines()[:8]):
                unmarked.append(doc.name)
        self.assertEqual([], unmarked, "документы с утверждениями v5 без пометки «Исторический документ»")

    def test_T0_library_matches_table_d2(self):
        """T0 (К-13, аудит 15.09.2026 C1): библиотека материалов исправлена по таблице Д-2 плана согласования и ответу «О-7 как советуешь»:
        имена в дереве по действующим ГОСТ, прежние имена сохранены копиями, заменённые и ошибочные стандарты убраны, однострочные ТУ без дроби, «БТ» у листа
        х/к, толщина листа Ст3сп определяет стандарт знаменателя, плотности стали 7850 и ABS 1050 (WP-4.5)."""
        import xml.etree.ElementTree as ET
        raw = Path(paths.MATERIAL_DB).read_bytes().decode("utf-16").replace('encoding="UTF-16"', 'encoding="UTF-8"')
        root = ET.fromstring(raw.encode("utf-8"))
        materials = list(root.iter("material"))
        self.assertEqual(128, len(materials), "число записей библиотеки: 118 и 10 копий прежних имён")
        self.assertEqual(len(materials), len({m.get("matid") for m in materials}), "matid уникальны")
        self.assertEqual(len(materials), len({m.get("name") for m in materials}), "имена уникальны — SolidWorks ищет материал по имени")

        # MProp (SWPlus) читает библиотеку построчно через Split(…, vbCrLf): при переводах строк LF весь файл для него
        # одна строка, и выбор базы падает с «Несовпадение типов» (З-4). Каждая классификация и материал — на своей строке CRLF.
        text = Path(paths.MATERIAL_DB).read_bytes().decode("utf-16")
        self.assertEqual(0, len(re.findall(r"(?<!\r)\n", text)), "в библиотеке переводы строк без CR — MProp не разберёт файл")
        lines = text.split("\r\n")
        self.assertEqual(len(materials), sum("material name" in s for s in lines), "каждый материал на отдельной строке")
        self.assertEqual(len(list(root.iter("classification"))), sum("classification name" in s for s in lines),
                         "каждая классификация на отдельной строке")

        # З-5: материал с внешним видом, которого нет в SolidWorks 2025, не назначается вовсе — «Применить» молча
        # ничего не делает (так было у всех ЛДСП, МДФ, фанеры, кромки, HPL и полимеров). Пути сверяются с установленным SW.
        appearances = Path(r"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\data\graphics\Materials")
        if appearances.is_dir():
            shaders = {s.get("path") for s in root.iter("pwshader2")}
            self.assertEqual(set(), {p for p in shaders if not (appearances / p.lstrip("\\")).is_file()},
                             "внешние виды материалов, которых нет в SolidWorks 2025")
        self.assertEqual([], [m.get("name") for m in materials if re.search(r"[\x00-\x08\x0b\x0c\x0e-\x1f]", ET.tostring(m, encoding="unicode"))],
                         "управляющие символы в записи материала (\\t, \\f в путях)")

        # Аудит 15.09.2026, C1: имена в дереве — по действующим ГОСТ; прежние имена — копиями в группе «99», чтобы старые
        # модели нашли свой материал. У копии те же поля, что у переименованной записи.
        custom_of = lambda m: {p.get("name"): p.get("value") or "" for p in m.findall("custom/prop")}
        legacy_classes = [c for c in root.iter("classification") if c.get("name").startswith("99. Прежние наименования")]
        self.assertEqual(1, len(legacy_classes), "нет группы прежних наименований")
        legacy = legacy_classes[0].findall("material")
        legacy_ids = {m.get("matid") for m in legacy}
        active = [m for m in materials if m.get("matid") not in legacy_ids]
        self.assertEqual(10, len(legacy), "копий прежних имён")
        active_by_fields = {tuple(sorted(custom_of(m).items())): m.get("name") for m in active}
        name_problems = []
        for m in legacy:
            if tuple(sorted(custom_of(m).items())) not in active_by_fields:
                name_problems.append(f"прежнее имя «{m.get('name')}» без действующей записи с теми же полями")
        for m in active:
            for obsolete in ("ГОСТ 14637-89", "ГОСТ 14918-80", "ГОСТ 22233-2001"):
                if obsolete in m.get("name"):
                    name_problems.append(f"имя в дереве «{m.get('name')}» ссылается на {obsolete}")
            c = custom_of(m)
            if "ГОСТ 19903-2015" in c.get("Обозначение_ГОСТ", "") and "Ст3сп" in m.get("name"):
                want = c.get("ГОСТ_Материал", "")
                if want and want not in m.get("name"):
                    name_problems.append(f"имя в дереве «{m.get('name')}» не по {want}")
        self.assertEqual([], name_problems)
        problems = []
        for m in materials:
            name = m.get("name")
            custom = {p.get("name"): p.get("value") or "" for p in m.findall("custom/prop")}
            gost, line = custom.get("Обозначение_ГОСТ", ""), custom.get("Обозначение_Строка", "")
            dens = float(m.find("physicalproperties/DENS").get("value"))
            text = gost + " " + line
            for wrong in ("ГОСТ 34359-2017", "ГОСТ 10589-87", "ГОСТ 14637-89", "ГОСТ 14918-80", "ГОСТ 22233-2001",
                          "ГОСТ 10632-2014", "Сталь 3сп", "ПА 6-210-311", "Б-ПО-"):
                if wrong in text:
                    problems.append(f"{name}: «{wrong}»")
            single = any(s in text for s in ("ГОСТ 8568-77", "ГОСТ 3262-75", "ГОСТ 21631-76", "ГОСТ 16338-85",
                                             "ГОСТ 26996-86", "ГОСТ 32289-2013", "ОСТ 6-06-С9-93")) or gost.startswith("Кромка")
            if single and "<STACK" in gost:
                problems.append(f"{name}: однострочное обозначение записано дробью")
            if "ГОСТ 19903-2015" in gost and "Ст3сп" in gost:
                t = float(custom.get("Типоразмер", "0").replace(",", "."))
                want = "ГОСТ 14637-2024" if t >= 4 else "ГОСТ 16523-97"
                if want not in gost:
                    problems.append(f"{name}: лист {t} мм — знаменатель по {want}")
            steel = any(s in custom.get("ГОСТ_Материал", "") for s in ("ГОСТ 13663-86", "ГОСТ 14637", "ГОСТ 16523-97",
                                                                    "ГОСТ 8731-74", "ГОСТ 10705-80", "ГОСТ 535-2005"))
            if steel and abs(dens - 7850.0) > 0.1:
                problems.append(f"{name}: плотность стали {dens}")
            if "ABS" in name and abs(dens - 1050.0) > 0.1:
                problems.append(f"{name}: плотность ABS {dens}")
            flat = lambda s: re.sub(r"\s+", " ", re.sub(r"</?STACK[^>]*>|<OVER>", " ", s)).strip()
            if flat(m.get("description") or "") != flat(gost):
                problems.append(f"{name}: описание «{m.get('description')}» не совпадает с обозначением (Д-42)")
        self.assertEqual([], problems)


    def test_T0_current_documents_cite_russian_standards(self):
        """T0 (WP-7.4, Н-35): действующие документы, код и тесты ссылаются на ГОСТ Р 2.104-2023, ГОСТ Р 2.106-2019,
        ГОСТ Р 2.109-2023, а не на недействующие в РФ ГОСТ 2.104-2006 и ГОСТ 2.109-73; исторические документы помечены."""
        files = [ROOT / "README.md", ROOT / "06_Документация" / "РУКОВОДСТВО_ПОЛЬЗОВАТЕЛЯ_И_АДМИНИСТРАТОРА.md",
                 ROOT / "06_Документация" / "Правила_записи_свойств_SWPlus.md"]
        files += addin_sources() + sorted((paths.TESTS / "tests").glob("*.py")) + sorted((paths.TESTS / "eskd_e2e").glob("*.py"))
        pattern = re.compile(r"ГОСТ 2\.104(?:-2006)?(?![\d.])|ГОСТ 2\.109(?:-73)?(?![\d.])|ГОСТ 2\.106(?![\d.-])")
        found = []
        for f in files:
            if f.name == "test_static.py":
                continue
            for n, line in enumerate(f.read_text(encoding="utf-8-sig").splitlines(), 1):
                if pattern.search(line):
                    found.append(f"{f.name}:{n}: {line.strip()[:100]}")
        self.assertEqual([], found)

    def test_T0_no_code_writes_production_folder(self):
        """T0 (ТЗ-04 Р4-2, аудит 19.09.2026): папки «_Производство» больше нет — ни надстройка, ни установщик, ни
        скрипты заказов в неё не пишут; цех работает из папки изделия с отметкой _Выдано."""
        found = []
        for base in (ROOT / "03_Макросы_и_Плагины", ROOT / "01_Настройки_SolidWorks"):
            for src in base.rglob("*"):
                if src.suffix.lower() not in (".cs", ".ps1", ".psm1", ".py", ".bas", ".cls", ".frm"):
                    continue
                for n, line in enumerate(src.read_text(encoding="utf-8", errors="ignore").splitlines(), 1):
                    if re.search(r"_Производство|04_ПРОИЗВОДСТВО", line) and "больше нет" not in line \
                            and "не пополн" not in line:
                        found.append(f"{src.relative_to(ROOT)}:{n}: {line.strip()[:100]}")
        self.assertEqual([], found)

    def test_T0_test_ids_are_unique(self):
        """T0 (аудит 19.09.2026): номер сценария вида P01 встречается только в одном файле — фильтр -k и ссылки
        в документах однозначны."""
        where = {}
        for src in sorted((paths.TESTS / "tests").glob("test_*.py")):
            for tid in set(re.findall(r"def test_([A-Z]\d{2})_", src.read_text(encoding="utf-8"))):
                where.setdefault(tid, []).append(src.name)
        self.assertEqual({}, {k: v for k, v in where.items() if len(v) > 1})

    def test_T0_runner_reports_failed_subtests(self):
        """T0 (аудит 19.09.2026): упавший подтест попадает в отчёт раннера и делает прогон красным — раньше тест
        с упавшим подтестом пропадал из отчёта и итога."""
        import io
        import sys
        sys.path.insert(0, str(paths.TESTS))
        import run_tests

        class Probe(unittest.TestCase):
            def test_sub(self):
                for i in (1, 2):
                    with self.subTest(i=i):
                        self.assertEqual(1, i)

            def test_ok(self):
                with self.subTest(i=1):
                    pass

        result = unittest.TextTestRunner(resultclass=run_tests.Recorder, stream=io.StringIO()).run(
            unittest.TestLoader().loadTestsFromTestCase(Probe))
        self.assertEqual([("test_ok", "pass"), ("test_sub (i=2)", "fail")],
                         sorted((r["id"].rsplit(".", 1)[-1], r["status"]) for r in result.records))


if __name__ == "__main__":
    unittest.main()
