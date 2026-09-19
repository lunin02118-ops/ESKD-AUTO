# -*- coding: utf-8 -*-
"""E2E, группа O — работа в структуре заказа КТО (ТЗ-03 ред. 8, регламент §2.1, §7.2).

Заказ собирается в каталоге прогона как на сервере: _Заявки\\<заказ>\\02_Металл\\И<nn>_<шифр>_<наименование>\\01_3D.
Проверяется, что сборка находит детали в 01_3D, выгрузки из 01_3D ложатся в 02_PDF, 03_ЧПУ\\Лазер_Лист,
03_ЧПУ\\Труборез по относительным путям (так настроены профиль Drew и MCP-сервер), а проверка скилла kto-folders
принимает получившийся заказ.
"""
import importlib.util
import os
import shutil
import sys
from pathlib import Path

from eskd_e2e import com, paths
from eskd_e2e.testing import SwTestCase

ORDER = "9-9_Т_Тест_Павлодар"
PRODUCT = "И01_ПРТИ.468211.100_Кондуктор"
ASSEMBLY = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
COMPONENTS = ("ПРТИ.468211.110 СБ Узел опоры.sldasm", "ПРТИ.468211.101 Пластина опорная.sldprt",
              "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt", "ПРТИ.468211.102 Стойка.sldprt", "ПРТИ.468211.103 Планка.sldprt",
              "Электродвигатель АИР71А4.sldprt", "ПРТИ.468211.104 Кронштейн направляющий удлинённый.sldprt",
              "ПРТИ.468211.105 Рама сварная.sldprt")
DRAWING = "ПРТИ.468211.101 Пластина опорная.slddrw"
TUBE = "ПРТИ.468211.102 Стойка.sldprt"
PRODUCT_TREE = ("01_3D", "02_PDF", "03_ЧПУ/Лазер_Лист", "03_ЧПУ/Труборез", "04_Сопроводительная документация",
                "_Аннулировано")
SKILL_SCRIPT = Path(os.environ.get("USERPROFILE", "")) / ".claude" / "skills" / "kto-folders" / "scripts" / "sort_plan.py"
MCP_SERVER = paths.ROOT / "03_Макросы_и_Плагины" / "SolidWorks_MCP_Server" / "server.py"


def _load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class OrderStructure(SwTestCase):

    def setUp(self):
        super().setUp()
        # Короткий корень: полный путь к деталям должен уложиться в 240 знаков (Т-7) даже внутри каталога прогона
        root = self.s.run_dir / "O"
        shutil.rmtree(root, ignore_errors=True)
        self.order = root / "_Заявки" / ORDER
        self.product = self.order / "02_Металл" / PRODUCT
        for sub in PRODUCT_TREE:
            (self.product / sub).mkdir(parents=True, exist_ok=True)
        self.model_dir = self.product / "01_3D"
        for name in (ASSEMBLY, DRAWING) + COMPONENTS:
            shutil.copy2(paths.FIXTURES_A / name, self.model_dir / name)
            os.chmod(self.model_dir / name, 0o666)
        longest = max(len(str(p)) for p in self.model_dir.iterdir())
        self.assertLessEqual(longest, 240, "каталог прогона слишком глубокий для проверки структуры заказа")

    def _export(self, doc, relative):
        """Выгрузка по пути относительно папки документа — как PathPattern «<Directory>..\\…» профиля Drew."""
        target = Path(os.path.normpath(self.model_dir / relative))
        ok, err, _ = self.s.save_as(doc, target, com.SAVE_SILENT | 2)  # 2 = копия, документ остаётся прежним
        self.assertTrue(ok and target.is_file() and target.stat().st_size > 0, f"выгрузка не создана: {target} (err={err})")
        return target

    def test_O01_assembly_resolves_components_inside_product(self):
        """O01: сборка в 01_3D открывается и берёт все детали из той же папки изделия."""
        asm = self.s.open(self.model_dir / ASSEMBLY)
        comps = com.as_list(asm.GetComponents(False)) or []
        self.assertTrue(comps, "в сборке нет компонентов")
        outside = []
        for c in comps:
            c = com.dyn(c)
            path = c.GetPathName
            if not path or not os.path.normcase(path).startswith(os.path.normcase(str(self.model_dir))):
                outside.append((c.Name2, path))
        self.assertEqual([], outside, "компоненты не из папки 01_3D изделия")
        self.s.close(asm)

    def test_O02_pdf_and_dxf_from_drawing_go_to_product_folders(self):
        """O02: PDF чертежа — в 02_PDF, DXF — в 03_ЧПУ\\Лазер_Лист, имя = имя чертежа (регламент §7.2)."""
        drw = self.s.open(self.model_dir / DRAWING)
        stem = Path(DRAWING).stem
        pdf = self._export(drw, Path("..") / "02_PDF" / (stem + ".pdf"))
        dxf = self._export(drw, Path("..") / "03_ЧПУ" / "Лазер_Лист" / (stem + ".dxf"))
        self.assertEqual(self.product / "02_PDF", pdf.parent)
        self.assertEqual(self.product / "03_ЧПУ" / "Лазер_Лист", dxf.parent)
        self.assertEqual([], [p.name for p in (self.product / "03_ЧПУ").iterdir() if p.is_file()], "файлы прямо в 03_ЧПУ")
        self.s.close(drw)

    def test_O03_igs_goes_to_tube_cutter_folder_via_mcp_server(self):
        """O03: MCP-сервер ЕСКД — относительный путь от папки документа, расширение по формату: IGS в 03_ЧПУ\\Труборез."""
        try:
            server = _load(MCP_SERVER, "eskd_mcp_server_under_test")
        except ImportError as exc:
            self.skipTest(f"нет пакетов MCP-сервера: {exc}")
        part = self.s.open(self.model_dir / TUBE)
        self.s.activate(part)
        raw_sw = object.__getattribute__(self.s.sw, "_obj")
        server.get_sw_app = lambda: raw_sw
        stem = Path(TUBE).stem
        result = server.sw_export(str(Path("..") / "03_ЧПУ" / "Труборез" / stem), "IGES")
        expected = self.product / "03_ЧПУ" / "Труборез" / (stem + ".igs")
        self.assertTrue(result.get("success"), result)
        self.assertEqual(os.path.normcase(str(expected)), os.path.normcase(result["output_path"]))
        self.assertTrue(expected.is_file() and expected.stat().st_size > 0, "IGS не создан")
        self.s.close(part)

    def test_O04_skill_check_accepts_order(self):
        """O04: проверка скилла kto-folders (sort_plan.py --check) принимает заказ с полным деревом папок."""
        if not SKILL_SCRIPT.is_file():
            self.skipTest(f"скилл kto-folders не установлен: {SKILL_SCRIPT}")
        skill = _load(SKILL_SCRIPT, "kto_sort_plan_under_test")
        for d in skill.ORDER_TREE:
            (self.order / d).mkdir(parents=True, exist_ok=True)
        self.assertEqual(0, skill.check(str(self.order)), "проверка скилла нашла нарушения (см. вывод теста)")
        stray = self.product / "03_ЧПУ" / "лишний.dxf"
        stray.write_bytes(b"0")
        self.assertEqual(1, skill.check(str(self.order)), "файл прямо в 03_ЧПУ не замечен")
