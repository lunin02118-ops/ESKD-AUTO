# -*- coding: utf-8 -*-
"""T0 — статические проверки репозитория без SolidWorks."""
import hashlib
import json
import re
import subprocess
import unittest
from pathlib import Path

from PIL import Image

from eskd_e2e import paths
from eskd_e2e.testing import StaticTestCase, known_defect, tags

ROOT = paths.ROOT
ADDIN = paths.ADDIN_DIR
LEGACY_ALIASES = ("Разраб.", "Разработал", "п_Разраб", "DrawnBy", "DrawnDate", "Пров.", "п_Пров", "CheckedBy",
                  "Организация_ФБ", "\"Компания\"", "\"Firm\"", "\"Organization\"", "PartNo", "ГОСТ_Сортамент",
                  "ГОСТ_Материал")


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
    def test_T0_installer_batch_is_ascii_and_finds_setup(self):
        """T0: УСТАНОВИТЬ_ЕСКД.bat — только ASCII, путь к Setup находится маской (Д-23)."""
        data = (ROOT / "УСТАНОВИТЬ_ЕСКД.bat").read_bytes()
        self.assertTrue(all(b < 128 for b in data), "в батнике есть не-ASCII символы")
        self.assertIn(b"01_*", data)
        self.assertTrue(list(ROOT.glob("01_*/Setup_Workstation_SolidWorks.ps1")), "Setup не найден по маске 01_*")

    def test_T0_mprop_firm_is_pairs(self):
        """T0: MProp_Firm.txt — пары «организация / код», имена не пустые (Д-25)."""
        lines = (paths.SWPLUS / "MProp" / "MProp_Firm.txt").read_bytes().decode("cp1251").split("\r\n")
        if lines and lines[-1] == "":
            lines = lines[:-1]
        self.assertEqual(0, len(lines) % 2, f"нечётное число строк: {lines}")
        self.assertTrue(all(lines[i].strip() for i in range(0, len(lines), 2)), f"пустое имя организации: {lines}")

    @tags("smoke")
    def test_T0_dictionary_has_43_names_and_flags(self):
        """T0: словарь SWPlus читается: 43 имени, разбор имени файла включён, разделитель — пробел."""
        lines = paths.SWPLUS_DICTIONARY.read_bytes().decode("cp1251").split("\r\n")
        self.assertGreaterEqual(len(lines), 50)
        self.assertEqual("Обозначение", lines[0])
        self.assertEqual("Количество", lines[42])
        self.assertEqual("1", lines[47].strip(), "prpFileName")
        self.assertEqual(" ", lines[48], "prpNameSep")

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
            if size and size not in name:
                problems.append(f"Типоразмер «{size}» не входит в имя")
            if problems:
                wrong[name] = problems
        self.assertEqual({}, wrong)

    def test_T0_dll_built_from_current_sources(self):
        """T0: build_manifest.json — хеши исходников совпадают с текущими файлами (Д-24)."""
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
        """T0: в .reg нет значений с одиночными «\\» — reg.exe их молча теряет (Д-23)."""
        reg = ROOT / "01_Настройки_SolidWorks" / "Реестровые_Профили" / "01_SW2025_Корпоративный_Стандарт_ЕСКД.reg"
        bad = []
        for i, line in enumerate(reg.read_bytes().decode("utf-16").splitlines(), 1):
            m = re.match(r'^"[^"]*"="(.*)"$', line)
            if m and re.search(r'(?<!\\)\\(?![\\"])', m.group(1).replace("\\\\", "")):
                bad.append(i)
        self.assertEqual([], bad[:20])

    @known_defect("Д-19")
    def test_T0_property_tab_templates_use_dictionary_names(self):
        """T0: шаблоны вкладки свойств запрашивают словарные имена (Д-19)."""
        wrong = {}
        for prp in paths.PROPERTY_TAB_TEMPLATES.glob("*.prtprp"):
            text = prp.read_bytes().decode("utf-8-sig", errors="replace")
            names = re.findall(r'PropName="([^"]+)"', text)
            bad = [n for n in names if n in ("Разработал", "Организация", "Литера", "Разраб.")]
            if bad:
                wrong[prp.name] = bad
        self.assertEqual({}, wrong)

    @known_defect("Д-21")
    def test_T0_single_sheet_format_set(self):
        """T0: один комплект форматок — «02_…/База шаблонов» убран (Д-21)."""
        self.assertFalse((ROOT / "02_Шаблоны_и_Форматки" / "База шаблонов").exists())

    @known_defect("Д-20")
    def test_T0_mcp_server_writes_no_legacy_aliases(self):
        """T0: MCP-сервер не пишет алиасы v5 (Д-20)."""
        text = (ROOT / "03_Макросы_и_Плагины" / "SolidWorks_MCP_Server" / "server.py").read_text(encoding="utf-8", errors="replace")
        self.assertEqual([], [a for a in ("DrawnBy", "CheckedBy", "п_Разраб", "Организация_ФБ") if a in text])

    @known_defect("Д-24")
    def test_T0_no_build_leftovers_and_reports_in_git(self):
        """T0: в git нет копий DLL (*_old, Legacy_Builds) и старых отчётов TEST_REPORT_* (Д-24)."""
        out = subprocess.run(["git", "-C", str(ROOT), "-c", "core.quotepath=off", "ls-files"], capture_output=True)
        files = out.stdout.decode("utf-8", errors="replace").splitlines()
        leftovers = [f for f in files if f.endswith(("_old", ".f40_old")) or "Legacy_Builds/" in f or "/TEST_REPORT_2" in f]
        self.assertEqual([], leftovers)


if __name__ == "__main__":
    unittest.main()
