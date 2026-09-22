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

    def test_T0_graphics_settings_do_not_depend_on_developer_pc(self):
        """Замечание владельца 20.09.2026: на другом ПК SolidWorks не запускался после настройки. Аппаратный конвейер
        графики включается только на дискретной видеокарте (ключ -Graphics: Auto/Safe/Hardware), программный OpenGL
        остаётся запасным путём, а профиль реестра не несёт слепок видеокарты разработчика."""
        setup = (ROOT / "01_Настройки_SolidWorks" / "_Служебное" / "Setup_Workstation_SolidWorks.ps1").read_text(encoding="utf-8-sig")
        self.assertIn('[ValidateSet("Auto", "Safe", "Hardware")][string]$Graphics', setup, "нет выбора режима графики")
        self.assertIn("$hardwareGraphics", setup, "конвейер включается без проверки видеокарты")
        pipeline = setup.index('"Use Performance Pipeline 2020" 1')
        self.assertLess(setup.index("$hardwareGraphics = switch"), pipeline, "конвейер включается до проверки видеокарты")
        # Своя маска AllowList роняет SolidWorks 2025 при старте (0xC0000005, GeForce RTX 2080 Ti): 20.09.2026 — 0x32408 под
        # общими именами, 22.09.2026 — 0x30408 под точным рендерером. Маски только снимаются.
        self.assertNotIn('Set-Reg "$U\\SolidWorks\\AllowList', setup, "установщик пишет маску AllowList")
        self.assertNotIn("CreateSubKey", setup, "установщик пишет маску AllowList через .NET")
        self.assertNotIn('"Workarounds"', setup, "установщик пишет маску обхода")
        self.assertIn('$cu.DeleteSubKeyTree($stale, $false)', setup, "маска AllowList от прежней настройки не снимается")
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
        self.assertIn("645654CF9055FDA11EF16CF131952F9BF235CBD3841AFDE6B5663DCC16C18F15", setup,
                      "движок сверяет сборку Drew по контрольному хэшу")
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
        self.assertIn("$recordedAuto -eq $autoHash", setup, "Drew от того же установщика не признаётся установленным")
        self.assertNotIn("-Silent -NoActivate", setup, "старый вызов классического установщика убран")
        self.assertNotIn("Drew не активирован", setup)
        self.assertNotIn("Activation.code", setup)
        self.assertIn("[switch]$SwInternetBlock", setup, "опция отучения от сети объявлена")
        self.assertIn("[switch]$DrewRussian", setup, "опция русского интерфейса объявлена")
        block = ROOT / "01_Настройки_SolidWorks" / "SwInternetBlock"
        self.assertTrue((block / "Set-SwInternetBlock.ps1").exists() and (block / "SWInternetBlock.manifest.json").exists(),
                        "нет пакета SwInternetBlock рядом с движком")
        configurator = (ROOT / "01_Настройки_SolidWorks" / "_Исходники" / "CAD_Workstation_Configurator.py").read_text(encoding="utf-8")
        for needed in ("Gov-издание (лицензия встроена", "Отучение SolidWorks от сети", "DREW_LANG=ru",
                       '"-SkipDrew"', '"-SwInternetBlock"', '"-DrewRussian"'):
            self.assertIn(needed, configurator, f"в окне настройки нет: {needed}")
        for gone in ("Активация Drew", "Client-Activate-Drew.ps1", "drew_needs_activation"):
            self.assertNotIn(gone, configurator, f"артефакт активации не должен остаться в окне: {gone}")

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
                                           "TemplateNames", "ExtraNames", "CutListNames")}
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

        # Молча — только при единственном подходящем: Chosen заполняется в ветке Single и больше нигде.
        self.assertEqual(1, service.count("finding.Chosen = "), "материал выбирается за конструктора не в одном месте")
        single = re.search(r"if \(finding\.Match\.Single\)\s*\{(.*?)\}", service, flags=re.S)
        self.assertIsNotNone(single, "ветка «подходящий один» не найдена")
        self.assertIn("finding.Chosen = finding.Match.First", single.group(1), "молча подставляется не единственный подходящий")

        # Расхождение — только уведомление: к назначению идут лишь Assign и Choose.
        needs = re.search(r"public bool NeedsAssign\s*\{[^}]*?return ([^;]+);", service, flags=re.S)
        self.assertIsNotNone(needs, "NeedsAssign не найден")
        self.assertNotIn("Mismatch", needs.group(1), "материал конструктора переписывается при расхождении")
        self.assertIn("не изменено", service, "при расхождении конструктору не сказано, что значение оставлено")

        # Запись документа — из очереди простоя, а не из обработчика сохранения.
        inspect = re.search(r"private static void InspectStock\(.*?\n        \}", sync, flags=re.S)
        self.assertIsNotNone(inspect, "InspectStock не найден")
        self.assertNotIn("StockService.Apply", inspect.group(0), "материал назначается прямо при сохранении")
        self.assertIn('Kind = "stock"', hub, "задача подбора не ставится в очередь простоя")

    def test_T0_fixture_materials_follow_library(self):
        """T0: по манифесту фикстур у деталей из проката корпуса А и у копий корпуса Б — сортамент из корпоративной библиотеки,
        у стандартных и покупных изделий сортамента нет; копии корпуса Б сделаны из текущих исходных файлов и не изменены
        (решение владельца 13.09.2026; материалы в самих файлах проверяет I07)."""
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
        сеть чаще раза в 2 с; выгрузка открывает чертёж один раз и возвращает настройки DXF; «покупное» решает
        ComponentKind; установщик запускает повышенные процессы из %TEMP% и проверяет чужие пути в профиле."""
        sync = (ADDIN / "Sw" / "SyncService.cs").read_text(encoding="utf-8")
        self.assertIn("DictionaryRecheck", sync, "словарь SWPlus кешируется")
        writer = (ADDIN / "Sw" / "PropertyWriter.cs").read_text(encoding="utf-8")
        self.assertIn("_values.Clear()", writer, "кеш значений сбрасывается при записи")
        addin = (ADDIN / "SwAddin.cs").read_text(encoding="utf-8")
        self.assertIn("RevisionService.Unavailable(_app, true)", addin, "опрос кнопки ревизии — с кешем")
        export = (ADDIN / "Sw" / "ExportService.cs").read_text(encoding="utf-8")
        self.assertEqual(1, export.count("swDocumentTypes_e.swDocDRAWING,"), "чертёж открывается в одном месте")
        self.assertIn("восстановление настройки DXF", export, "настройки DXF пользователя возвращаются")
        services = {name: (ADDIN / "Sw" / name).read_text(encoding="utf-8")
                    for name in ("CheckService.cs", "ExportService.cs", "LzkService.cs")}
        self.assertEqual([], [n for n, text in services.items() if "ComponentKind.IsPurchased(" not in text],
                         "проверка, выгрузка и ЛЗК решают «покупное» одним правилом")
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
        body = re.search(r"private int OnDocDestroy\(DocState s\)\s*\{(.*?)\n        \}", hub, re.S).group(1)
        code = "\n".join(line for line in body.splitlines() if not line.strip().startswith("//"))
        self.assertNotIn("Untrack", code)
        self.assertNotIn("-=", code)
        self.assertIn("s.Destroyed = true", code)
        for handler in ("OnDocSave(DocState s, string fileName)", "OnDocSavePost(DocState s, int saveType, string fileName)"):
            start = hub.index(handler)
            self.assertIn("if (s.Destroyed) return 0;", hub[start:start + 200], handler)

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
