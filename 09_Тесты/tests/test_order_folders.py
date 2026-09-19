# -*- coding: utf-8 -*-
"""T0 — скрипты раскладки папок заказа (скилл kto-folders, ТЗ-03): план, проверка заказа, перенос со сверкой.

Без SolidWorks: заказы и «беспорядочные» папки собираются во временном каталоге. Главное, что сторожат тесты, —
скрипты не теряют файлов (исходник цел, копия сверена по SHA-256, чужой файл не перезаписан), а проверка заказа
пропускает то, что положено по ТЗ-03, и ловит то, что ему противоречит.
"""
import csv
import hashlib
import importlib.util
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

from eskd_e2e import paths

SCRIPTS = paths.ROOT / "03_Макросы_и_Плагины" / "Папки_заказов_kto-folders" / "scripts"


def load(name):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run(script, *args):
    env = dict(os.environ, PYTHONUTF8="1")
    return subprocess.run([sys.executable, str(SCRIPTS / script), *map(str, args)], capture_output=True,
                          text=True, encoding="utf-8", env=env)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class OrderFolders(unittest.TestCase):

    def setUp(self):
        self.sp = load("sort_plan")
        self.tmp = Path(tempfile.mkdtemp(prefix="kto_"))
        self.addCleanup(lambda: __import__("shutil").rmtree(self.tmp, ignore_errors=True))

    def _order(self, name="85-1_Т_Центр соц услуг_Мангыстау", products=("И01_85T.СМ_Кровать одноместная",)):
        order = self.tmp / name
        for d in self.sp.ORDER_TREE:
            (order / d).mkdir(parents=True, exist_ok=True)
        for product in products:
            for d in self.sp.PRODUCT_TREE:
                (order / "02_Металл" / product / d).mkdir(parents=True, exist_ok=True)
        return order

    def test_T0_check_accepts_order_with_product_template(self):
        """Заказ из шаблона: изделие И01 и заготовка «_Шаблон_изделия» (ТЗ-03а) — замечаний нет, код возврата 0."""
        order = self._order(products=("И01_85T.СМ_Кровать одноместная", self.sp.PRODUCT_TEMPLATE))
        self.assertEqual([], self.sp.order_problems(order))
        result = run("sort_plan.py", "--check", "--order", order)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("Заказ соответствует структуре", result.stdout)

    def test_T0_check_names_what_breaks_the_structure(self):
        """Имя заказа по-старому, изделие без И<nn>, файл прямо в 03_ЧПУ, открытый файл — каждое названо."""
        order = self._order(name="Т 85 кровати", products=("Кровать",))
        (order / "02_Металл" / "Кровать" / "03_ЧПУ" / "деталь.dxf").write_text("0", encoding="ascii")
        (order / "01_Исходные данные" / "~$СЗ.docx").write_text("x", encoding="ascii")
        (order / "Старое").mkdir()
        problems = "\n".join(self.sp.order_problems(order))
        for expected in ("Имя заказа не по правилу", "Папка изделия не по правилу И<nn>_<шифр>_<наименование>: Кровать",
                         "Файл прямо в 03_ЧПУ", "Файл открыт кем-то", "Лишняя папка в корне заказа: Старое"):
            self.assertIn(expected, problems)
        self.assertEqual(1, run("sort_plan.py", "--check", "--order", order).returncode)

    def test_T0_order_name_rule(self):
        """Имя заказа по ТЗ-03 §4.2: код Т, Ал, Аст, ВЭД или без кода; шаблон и служебные папки («_…») — не заказы."""
        good = ("85-1_Т_Центр соц услуг_Мангыстау", "207-1_Аст_КМГ Диджитал_Астана", "56_Шоурум_Алматы",
                "171_ВЭД_АТУ_Алматы")
        bad = ("Т 85 кровати", "АСТ_ 207-1_ КГМ. ДИЖИТАЛ_ г Астана", "85-1_Центр", "85-1_T_Центр_Мангыстау")
        for name in good:
            self.assertRegex(name, self.sp.ORDER_NAME, name)
        for name in bad:
            self.assertNotRegex(name, self.sp.ORDER_NAME, name)
        self.assertEqual([], [p for p in self.sp.order_problems(self._order(name="_Шаблон_заказа", products=()))
                              if "Имя заказа" in p], "шаблон заказа проверяется без правила имени")

    def test_T0_designation_cipher_and_product_folder(self):
        """Обозначение из имени файла, шифр главной сборки и имя папки изделия (≤ 40 знаков)."""
        self.assertEqual("ПА.00.001", self.sp.designation("ПА00.001 Пластина"))
        self.assertEqual("85T.СМ.01.001", self.sp.designation("85T.СМ.01.001_Ножка"))
        self.assertIsNone(self.sp.designation("Кровать одноместная"))
        self.assertEqual("85T.СМ", self.sp.cipher_of("85T.СМ.00.000"))
        self.assertIsNone(self.sp.cipher_of("85T.СМ.01.001"), "деталь — не главная сборка")
        self.assertEqual("И01_85T.СМ_Кровать одноместная", self.sp.product_folder(1, "85T.СМ", "Кровать одноместная"))
        long_name = self.sp.product_folder(12, "85T.СМ", "Кровать двухъярусная с лестницей и ящиками, №2")
        self.assertLessEqual(len(long_name), 40, long_name)
        self.assertTrue(long_name.startswith("И12_85T.СМ_"), long_name)
        self.assertNotRegex(long_name, r"[№,]", "запрещённые знаки убраны")

    def _mess(self):
        src = self.tmp / "флешка"
        files = {
            "85T.СМ.00.000 Кровать одноместная.sldasm": "asm",
            "85T.СМ.01.001 Ножка.sldprt": "leg",
            "85T.СМ.01.001 Ножка (1).sldprt": "old leg",
            "85T.СМ.01.001 Ножка.dxf": "dxf",
            "85T.СМ.01.002 Труба.igs": "igs",
            "ЛЗК_85T.СМ.xlsx": "book",
            "СЗ 85-1.docx": "memo",
            "фото объекта.jpg": "photo",
            "непонятное.xyz": "?",
            "Thumbs.db": "junk",
        }
        for name, text in files.items():
            (src / name).parent.mkdir(parents=True, exist_ok=True)
            (src / name).write_text(text, encoding="utf-8")
            self._age(src / name)
        return src

    @staticmethod
    def _age(path, hours=24):
        """Файл «вчерашний»: только что изменённые скрипт не трогает (ТЗ-03 §2 п. 5)."""
        old = time.time() - hours * 3600
        os.utime(path, (old, old))

    def _plan(self, src, order):
        out = self.tmp / "plan"
        result = run("sort_plan.py", "--src", src, "--order", order, "--out", out,
                     "--product", "85T.СМ=01:Кровать одноместная")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        with open(out / "plan.csv", encoding="utf-8-sig") as f:
            rows = {Path(r["src"]).name: r for r in csv.DictReader(f, delimiter=";")}
        return out, rows

    def test_T0_plan_places_files_by_structure_and_changes_nothing(self):
        """План раскладывает по ТЗ-03 (модели, DXF, IGS, книга ЛЗК, СЗ, фото, старые версии) и ничего не трогает."""
        src = self._mess()
        before = {p.name: sha(p) for p in src.iterdir()}
        order = self.tmp / "85-1_Т_Центр соц услуг_Мангыстау"
        out, rows = self._plan(src, order)
        product = Path("02_Металл") / "И01_85T.СМ_Кровать одноместная"

        def dst(name):
            return str(Path(rows[name]["dst"]).relative_to(order)) if rows[name]["dst"] else ""

        self.assertEqual(str(product / "01_3D" / "85T.СМ.01.001 Ножка.sldprt"), dst("85T.СМ.01.001 Ножка.sldprt"))
        self.assertEqual(str(product / "_Аннулировано" / "85T.СМ.01.001 Ножка (1).sldprt"),
                         dst("85T.СМ.01.001 Ножка (1).sldprt"), "старая версия — в _Аннулировано изделия")
        self.assertEqual(str(product / "03_ЧПУ" / "Лазер_Лист" / "85T.СМ.01.001 Ножка.dxf"), dst("85T.СМ.01.001 Ножка.dxf"))
        self.assertEqual(str(product / "03_ЧПУ" / "Труборез" / "85T.СМ.01.002 Труба.igs"), dst("85T.СМ.01.002 Труба.igs"))
        self.assertEqual(str(product / "04_Сопроводительная документация" / "ЛЗК_85T.СМ.xlsx"), dst("ЛЗК_85T.СМ.xlsx"))
        self.assertEqual("СЗ 85-1.docx", dst("СЗ 85-1.docx"), "служебная записка — в корне заказа")
        self.assertEqual(str(Path("01_Исходные данные") / "Фото и видео" / "фото объекта.jpg"), dst("фото объекта.jpg"))
        self.assertEqual(str(Path("_Не разобрано") / "непонятное.xyz"), dst("непонятное.xyz"))
        self.assertEqual("skip", rows["Thumbs.db"]["action"])
        self.assertIn("непонятное.xyz", (out / "questions.txt").read_text(encoding="utf-8"))
        self.assertEqual(before, {p.name: sha(p) for p in src.iterdir()}, "план не меняет исходную папку")
        self.assertFalse(order.exists(), "план не создаёт заказ")

    def test_T0_apply_copies_verifies_and_never_overwrites(self):
        """Перенос: пробный запуск ничего не делает; --apply создаёт дерево и копирует со сверкой SHA-256; файл
        с тем же именем и другим содержимым не перезаписывается — новый кладётся в «_Конфликт_<дата>»."""
        src = self._mess()
        order = self.tmp / "85-1_Т_Центр соц услуг_Мангыстау"
        out, rows = self._plan(src, order)

        dry = run("apply_plan.py", "--plan", out / "plan.csv")
        self.assertEqual(0, dry.returncode, dry.stdout + dry.stderr)
        self.assertIn("пробный запуск", dry.stdout)
        self.assertFalse(order.exists(), "пробный запуск ничего не создаёт")

        occupied = Path(rows["85T.СМ.01.001 Ножка.sldprt"]["dst"])
        occupied.parent.mkdir(parents=True)
        occupied.write_text("чужая деталь", encoding="utf-8")
        result = run("apply_plan.py", "--plan", out / "plan.csv", "--apply")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

        self.assertEqual("чужая деталь", occupied.read_text(encoding="utf-8"), "существующий файл не перезаписан")
        conflicts = list(occupied.parent.glob("_Конфликт_*/" + occupied.name))
        self.assertEqual(1, len(conflicts), "новый файл — в _Конфликт_<дата>")
        for row in rows.values():
            if row["action"] != "copy" or Path(row["dst"]) == occupied:
                continue
            self.assertEqual(row["sha256"], sha(row["dst"]), f"копия сверена: {row['dst']}")
            self.assertTrue(Path(row["src"]).is_file(), "без --move исходник остаётся")
        self.assertEqual([], self.sp.order_problems(order), "дерево заказа полное — проверка проходит")
        logs = list(out.glob("apply_log_*.csv"))
        self.assertEqual(1, len(logs), "журнал запуска с датой в имени")
        log = logs[0].read_text(encoding="utf-8-sig")
        self.assertIn("конфликт имени", log)
        self.assertNotIn("ОШИБКА", log)

    def test_T0_apply_refuses_destinations_outside_order(self):
        """Колонку dst правят руками: путь вне папки заказа (опечатка, чужой заказ, «..») — остановка без единого
        действия, код 2."""
        src = self._mess()
        order = self.tmp / "85-1_Т_Центр соц услуг_Мангыстау"
        out, rows = self._plan(src, order)
        plan = out / "plan.csv"
        text = plan.read_text(encoding="utf-8-sig")
        stray = str(self.tmp / "85-1_Т_Центр соц услуг_Мангыстау2" / "СЗ 85-1.docx")
        text = text.replace(rows["СЗ 85-1.docx"]["dst"], stray)
        plan.write_text(text, encoding="utf-8-sig")
        result = run("apply_plan.py", "--plan", plan, "--apply")
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn("вне папки заказа", result.stdout)
        self.assertFalse(order.exists(), "ничего не создано")
        self.assertFalse(Path(stray).parent.exists(), "в соседний заказ ничего не записано")

    def test_T0_fresh_files_and_lonely_copies(self):
        """Файл, изменённый менее 2 ч назад, в план переноса не попадает; «копия» без основного файла остаётся
        рабочей, а не уходит в _Аннулировано (ТЗ-03 §2 п. 5, §4.5); «сзади.jpg» — не служебная записка."""
        src = self._mess()
        (src / "85T.СМ.01.003 Уголок - копия.sldprt").write_text("copy", encoding="utf-8")
        self._age(src / "85T.СМ.01.003 Уголок - копия.sldprt")
        (src / "сзади.jpg").write_text("photo", encoding="utf-8")
        self._age(src / "сзади.jpg")
        (src / "85T.СМ.01.004 Косынка.sldprt").write_text("now", encoding="utf-8")
        order = self.tmp / "85-1_Т_Центр соц услуг_Мангыстау"
        out, rows = self._plan(src, order)
        self.assertEqual("skip", rows["85T.СМ.01.004 Косынка.sldprt"]["action"], "свежий файл не трогаем")
        self.assertIn("менее 2 ч назад", (out / "questions.txt").read_text(encoding="utf-8"))
        lonely = Path(rows["85T.СМ.01.003 Уголок - копия.sldprt"]["dst"])
        self.assertNotIn("_Аннулировано", lonely.parts, f"копия без основного — рабочая: {lonely}")
        self.assertNotEqual(order / "сзади.jpg", Path(rows["сзади.jpg"]["dst"]), "не служебная записка")

    def test_T0_latin_and_cyrillic_cipher_is_one_product(self):
        """«85T» латиницей и «85Т» кириллицей — одно изделие: второй папки И02 нет, расхождение — в вопросах."""
        src = self._mess()
        for name in ("85Т.СМ.00.000 Кровать одноместная.slddrw", "85Т.СМ.01.005 Ребро.sldprt"):  # «Т» кириллицей
            (src / name).write_text("cyr", encoding="utf-8")
            self._age(src / name)
        order = self.tmp / "85-1_Т_Центр соц услуг_Мангыстау"
        out, rows = self._plan(src, order)
        product = order / "02_Металл" / "И01_85T.СМ_Кровать одноместная"
        self.assertEqual(product / "01_3D" / "85Т.СМ.01.005 Ребро.sldprt", Path(rows["85Т.СМ.01.005 Ребро.sldprt"]["dst"]))
        folders = {Path(r["dst"]).relative_to(order).parts[1] for r in rows.values()
                   if r["dst"] and Path(r["dst"]).relative_to(order).parts[0] == "02_Металл"}
        self.assertEqual({"И01_85T.СМ_Кровать одноместная"}, folders, "одно изделие")
        self.assertIn("латиница/кириллица", (out / "questions.txt").read_text(encoding="utf-8"))

    def test_T0_move_sends_source_to_quarantine(self):
        """--move: копия сверена, исходник уехал в карантин с тем же относительным путём, ничего не удалено."""
        src = self._mess()
        order = self.tmp / "85-1_Т_Центр соц услуг_Мангыстау"
        quarantine = self.tmp / "карантин"
        out, rows = self._plan(src, order)
        result = run("apply_plan.py", "--plan", out / "plan.csv", "--apply", "--move", "--quarantine", quarantine)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        leg = rows["85T.СМ.01.001 Ножка.sldprt"]
        self.assertEqual(leg["sha256"], sha(leg["dst"]), "копия на месте")
        self.assertFalse(Path(leg["src"]).exists(), "исходник убран из разбираемой папки")
        self.assertEqual(leg["sha256"], sha(quarantine / "85T.СМ.01.001 Ножка.sldprt"), "исходник — в карантине")
        self.assertTrue((src / "Thumbs.db").exists(), "пропущенный файл не тронут")


if __name__ == "__main__":
    unittest.main()
