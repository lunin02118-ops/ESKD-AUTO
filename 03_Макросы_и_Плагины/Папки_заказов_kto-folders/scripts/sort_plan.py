"""План раскладки файлов по структуре заказа КТО (ТЗ-03). Ничего не меняет.

Построить план:
  python sort_plan.py --src <что разбираем> --order <папка заказа> --out <папка плана>
                      [--product "85T.СМ=01:Кровать одноместная" ...] [--product "=01:Стол учителя"]
Проверить готовый заказ:
  python sort_plan.py --check --order <папка заказа>

--product "шифр=номер:наименование" — изделие по позиции СЗ. Пустой шифр ("=01:...") — изделие
для металла без обозначения. Результат: plan.csv, folders.txt, preview.txt, questions.txt, meta.json.
"""
import argparse
import csv
import datetime
import hashlib
import json
import os
import re
import sys
from collections import defaultdict

MAX_PATH = 240

ORDER_TREE = [
    "01_Исходные данные/Документы", "01_Исходные данные/Фото и видео",
    "01_Исходные данные/Переписка", "01_Исходные данные/На согласовании",
    "02_Металл",
    "03_Корпус/01_Модели", "03_Корпус/02_Схемы сборки", "03_Корпус/03_Раскрой",
    "03_Корпус/04_Ведомости", "03_Корпус/05_Визуализация", "03_Корпус/_Аннулировано",
    "04_Войлок/01_Чертежи", "04_Войлок/02_ЧПУ", "04_Войлок/_Аннулировано",
]
PRODUCT_TREE = ["01_3D", "02_PDF", "03_ЧПУ/Лазер_Лист", "03_ЧПУ/Труборез",
                "04_Сопроводительная документация", "_Аннулировано"]

EXT_3D = {".m3d", ".a3d", ".cdw", ".spw", ".frw", ".kdw", ".sldprt", ".sldasm", ".slddrw",
          ".step", ".stp", ".x_t", ".stl", ".3ds"}
EXT_ASM = {".a3d", ".sldasm"}
EXT_IGS = {".igs", ".iges"}
EXT_IMG = {".jpg", ".jpeg", ".png", ".heic", ".bmp", ".gif", ".webp", ".tif", ".tiff"}
EXT_VIDEO = {".mp4", ".mov", ".avi", ".3gp"}
EXT_D5 = {".drs", ".d5a", ".d5mesh", ".save"}
EXT_BCAD = {".bdf", ".b3d", ".mcr"}
EXT_CUT = {".cut2", ".cut"}
EXT_CLIENT = {".dwg", ".ifc", ".pln", ".bpn"}
EXT_ARCH = {".rar", ".zip", ".7z"}
JUNK = {"thumbs.db", "desktop.ini", ".ds_store"}
TEMPLATE_DIRS = {"01_исходные данные", "документы", "фото и видео", "переписка", "02_для обмена",
                 "03_на согласовании", "04_готово", "05_архив", "06_списание", "металл", "корпус",
                 "войлок", "_сопроводительная документация", "_кд", "для производства",
                 "01 кд в pdf", "02 чпу и раскрой", "лазер_лист", "труборез", "03 заявки в цеха",
                 "бкад", "bcad", "схемы сборки"}
BAD_CHARS = re.compile(r'[№«»,!+#<>:"/\\|?*]')
OLD_RE = re.compile(r"(\.bak\b|\(\d+\)$|\bкопия\b|\bстар(ый|ая|ое)\b|\bold\b|резервная_копия)", re.I)
DESIG_TAIL = re.compile(r"^(.+)\.\d{2}\.\d{3}(-\d{2})?$")
# Имя заказа (ТЗ-03 §4.2): <№>[-<п>]_[<Код>_]<Объект>_<Город>; код — Т, Ал, Аст, ВЭД (кириллица).
ORDER_NAME = re.compile(r"^\d+(-\d+)?_((Т|Ал|Аст|ВЭД)_)?[^_]+_[^_]+$")
# Заготовка изделия в шаблоне заказа (ТЗ-03а): копируется в каждый новый заказ и остаётся в нём.
PRODUCT_TEMPLATE = "_Шаблон_изделия"


def low(s):
    return s.lower().replace("ё", "е")


def sha256(path):
    h = hashlib.sha256()
    with open(long_path(path), "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def long_path(p):
    p = os.path.abspath(p)
    if os.name == "nt" and not p.startswith("\\\\?\\"):
        return "\\\\?\\UNC\\" + p[2:] if p.startswith("\\\\") else "\\\\?\\" + p
    return p


def designation(stem):
    """Обозначение в начале имени файла, нормализованное (ПА00.001 -> ПА.00.001), или None."""
    token = re.split(r"[\s_]", stem.strip(), maxsplit=1)[0]
    token = re.sub(r"(?<=[^\d.\-])(?=\d{2}\.\d{3}(-\d{2})?$)", ".", token)
    return token if DESIG_TAIL.match(token) else None


def cipher_of(desig):
    base = re.sub(r"-\d{2}$", "", desig)
    return re.sub(r"(\.00)*\.000$", "", base) if base.endswith(".000") else None


def clean_name(s, keep_dots=False):
    s = BAD_CHARS.sub(" ", s)
    if not keep_dots:
        s = s.replace(".", " ")
    return re.sub(r"\s+", " ", s).strip(" _-")


def product_folder(nn, cipher, name):
    head = f"И{nn:02d}" + (f"_{cipher}" if cipher else "")
    name = clean_name(name)
    room = 40 - len(head) - 1
    return f"{head}_{name[:max(room, 0)].rstrip()}" if name and room > 3 else head


class Planner:
    def __init__(self, src, order, products):
        self.src, self.order = os.path.abspath(src), os.path.abspath(order)
        self.questions, self.rows = [], []
        self._inner = False
        self.files = []
        for root, dirs, files in os.walk(self.src):
            dirs[:] = [d for d in dirs if not d.startswith("_МИГРАЦИЯ")]
            for f in files:
                self.files.append(os.path.join(root, f))
        self.d5_roots = {os.path.dirname(p) for p in self.files if p.lower().endswith(".drs")}
        self.products = self._products(products)

    # ---------- изделия ----------
    def _products(self, given):
        found = {}  # cipher -> name из главной сборки/чертежа
        for p in self.files:
            stem = os.path.splitext(os.path.basename(p))[0]
            d = designation(stem)
            c = cipher_of(d) if d else None
            if c is None:
                continue
            rest = stem[len(stem.split()[0]):] if " " in stem else ""
            prev = found.get(c)
            if prev is None or (not prev and rest.strip()):
                found[c] = rest.strip(" _")
        tops = {c: n for c, n in found.items()
                if not any(c != o and c.startswith(o + ".") for o in found)}
        prods, used = {}, set()
        for spec in given:
            m = re.match(r"^(.*?)=(\d+):(.*)$", spec)
            if not m:
                sys.exit(f"Неверный --product: {spec}")
            c, nn, name = m.group(1).strip(), int(m.group(2)), m.group(3).strip()
            prods[c] = product_folder(nn, c, name)
            used.add(nn)
        nxt = max(used, default=0)
        for c in sorted(tops):
            if c not in prods:
                nxt += 1
                prods[c] = product_folder(nxt, c, tops[c] or c)
                self.questions.append(f"Изделие «{c}» пронумеровано И{nxt:02d} автоматически — сверьте с СЗ")
        return prods

    def product_for(self, path, stem):
        d = designation(stem)
        if d:
            best = max((c for c in self.products if c and (d == c or d.startswith(c + "."))),
                       key=len, default=None)
            if best:
                return self.products[best]
        real = [c for c in self.products if c]
        if "" in self.products and not real:
            return self.products[""]
        if len(self.products) == 1:
            return next(iter(self.products.values()))
        lp = low(os.path.relpath(path, self.src))
        for c, folder in self.products.items():
            words = [w for w in low(folder).split("_", 2)[-1].split() if len(w) > 3]
            if (c and low(c) in lp) or (words and all(w in lp for w in words)):
                return folder
        return "И00_Не разобрано"

    # ---------- классификация ----------
    def classify(self, path):
        """-> (dst_rel | None, rule, note)"""
        name = os.path.basename(path)
        stem, ext = os.path.splitext(name)
        ext, ln, lstem = ext.lower(), low(name), low(stem)
        rel_dir = low(os.path.relpath(os.path.dirname(path), self.src))
        parts = set(rel_dir.split(os.sep))

        if ln in JUNK or name.startswith("~$"):
            return None, "skip", "служебный файл"
        for r in self.d5_roots:
            if path.startswith(r + os.sep):
                return os.path.join("03_Корпус", "05_Визуализация", os.path.basename(r),
                                    os.path.relpath(path, r)), "d5", ""

        # старые версии: место определяем по основному файлу, кладём в _Аннулировано раздела
        if not self._inner and (OLD_RE.search(stem) or ext == ".bak"):
            if ext == ".bak":
                base_name = stem                                   # Stol.frw.bak -> Stol.frw
            else:
                base = re.sub(r"(\.bak)+$", "", stem, flags=re.I)  # x.bak.bak.bdf -> x.bdf
                base_name = re.sub(r"\s*\(\d+\)$", "", base) + ext  # x (1).bdf -> x.bdf
            main = os.path.join(os.path.dirname(path), base_name)
            self._inner = True
            try:
                dst, rule, _ = self.classify(main)
            finally:
                self._inner = False
            if dst is None:
                return None, "skip", "служебный файл"
            if base_name != name and not os.path.exists(main):
                self.questions.append(f"Основного файла нет, «{name}» оставлен рабочим: {path}")
                return os.path.join(os.path.dirname(dst), name), rule, "только старая версия"
            return os.path.join(annul_dir(dst), name), "old", "старая версия"

        felt = "войлок" in rel_dir or "войлок" in lstem
        corpus = bool(parts & {"корпус", "бкад", "bcad"}) or "bcad" in rel_dir
        metal = "металл" in rel_dir

        if re.match(r"^(сз|служебн)", lstem) or "сводная" in lstem or "заявка на изг" in lstem:
            return name, "sz", ""
        if lstem.startswith("списани") or "списани" in rel_dir:
            self.questions.append(f"Списание → корень заказа (папки _Производство больше нет, ТЗ-04): {path}")
            return os.path.join("_Не разобрано", "Списание", name), "manual", "списание"
        if lstem.startswith("лзк"):
            # Книга ЛЗК (ТЗ-04) — в сопроводительной документации изделия.
            return os.path.join("02_Металл", self.product_for(path, stem), "04_Сопроводительная документация", name), "product", ""
        if lstem.startswith("ведомость_") or "лзк" in lstem or lstem == "изменения":
            return os.path.join("02_Металл", self.product_for(path, stem), name), "product-root", ""
        if re.search(r"\b(паспорт|акт|пакет|отказное|приказ)", lstem):
            p = self.product_for(path, stem)
            if p.startswith("И00"):
                self.questions.append(f"Сопроводительный документ без изделия: {path}")
            return os.path.join("02_Металл", p, "04_Сопроводительная документация", name), "accomp", ""
        if re.search(r"(^|[\s_])(лс|тз)([\s_]|$)|договор|накладн|сертификат|замер|спецификац", lstem) \
                and ext in {".pdf", ".docx", ".doc", ".xlsx", ".xls", ".jpg", ".jpeg", ".png"}:
            return os.path.join("01_Исходные данные", "Документы", name), "docs", ""
        if re.search(r"деталировк|закуп|сопровожд|калькуляц|себестоим", lstem):
            if metal and not corpus:
                return os.path.join("02_Металл", self.product_for(path, stem), name), "product-root", ""
            return os.path.join("03_Корпус", "04_Ведомости", batch(path, self.src), name), "corpus-list", ""
        if re.search(r"(^|[\s_])кп([\s_]|$)|коммерческ", lstem):
            return os.path.join("01_Исходные данные", "На согласовании", name), "approval", ""
        if ext in EXT_D5:
            return os.path.join("03_Корпус", "05_Визуализация", name), "d5", ""
        if ext in EXT_IMG | EXT_VIDEO:
            if re.search(r"рендер|render|визуал|вариант", lstem + rel_dir):
                return os.path.join("03_Корпус", "05_Визуализация", name), "render", ""
            if re.search(r"схем|сборк", lstem) or re.match(r"^\d+\s*схем", lstem) or corpus:
                return os.path.join("03_Корпус", "02_Схемы сборки", batch(path, self.src), name), "scheme", ""
            if "на согласовании" in rel_dir:
                return os.path.join("01_Исходные данные", "На согласовании", name), "approval", ""
            return os.path.join("01_Исходные данные", "Фото и видео", name), "photo", ""
        if ext in EXT_3D:
            p = self.product_for(path, stem)
            sub = sub_3d(path, self.src)
            return os.path.join("02_Металл", p, "01_3D", sub, name), "3d", ""
        if ext in EXT_IGS:
            return os.path.join("02_Металл", self.product_for(path, stem), "03_ЧПУ", "Труборез", name), "igs", ""
        if ext == ".dxf":
            if felt:
                return os.path.join("04_Войлок", "02_ЧПУ", name), "felt-dxf", ""
            return os.path.join("02_Металл", self.product_for(path, stem), "03_ЧПУ", "Лазер_Лист", name), "dxf", ""
        if ext == ".cdr":
            if felt:
                return os.path.join("04_Войлок", "01_Чертежи", name), "felt", ""
            return os.path.join("03_Корпус", "02_Схемы сборки", batch(path, self.src), name), "scheme", ""
        if ext in EXT_BCAD:
            return os.path.join("03_Корпус", "01_Модели", batch(path, self.src), name), "bcad", ""
        if ext in EXT_CUT or "раскрой" in lstem:
            return os.path.join("03_Корпус", "03_Раскрой", batch(path, self.src), name), "cut", ""
        if ext == ".pdf":
            if felt:
                return os.path.join("04_Войлок", "01_Чертежи", name), "felt", ""
            if re.search(r"схем|сборк", lstem) or (corpus and not metal):
                return os.path.join("03_Корпус", "02_Схемы сборки", batch(path, self.src), name), "scheme", ""
            if designation(stem) or metal:
                return os.path.join("02_Металл", self.product_for(path, stem), "02_PDF", name), "pdf", ""
        if ext in EXT_CLIENT:
            self.questions.append(f"Материал от заказчика/архитектора? {path}")
            return os.path.join("01_Исходные данные", "Документы", name), "client", "проверить"
        if ext in EXT_ARCH:
            self.questions.append(f"Архив не распакован, куда положить? {path}")
        self.questions.append(f"Не определено: {path}")
        return os.path.join("_Не разобрано", name), "manual", ""

    # ---------- план ----------
    def build(self):
        rows = []
        for p in sorted(self.files):
            dst, rule, note = self.classify(p)
            st = os.stat(long_path(p))
            rows.append(dict(src=p, dst=os.path.join(self.order, dst) if dst else "",
                             action="skip" if dst is None else "copy", rule=rule, note=note,
                             size=st.st_size, mtime=datetime.datetime.fromtimestamp(st.st_mtime).isoformat(timespec="seconds"),
                             sha256=sha256(p)))
        self._resolve_conflicts(rows)
        for r in rows:
            if r["dst"] and len(r["dst"]) > MAX_PATH:
                self.questions.append(f"Путь длиннее {MAX_PATH} ({len(r['dst'])}): {r['dst']}")
                r["note"] = (r["note"] + "; длинный путь").strip("; ")
        self.rows = rows
        return rows

    def _resolve_conflicts(self, rows):
        groups = defaultdict(list)
        for r in rows:
            if r["dst"]:
                groups[low(r["dst"])].append(r)
        for group in groups.values():
            if len(group) < 2:
                continue
            prio = lambda r: (("готово" in low(r["src"])) or ("запуск" in low(r["src"])), r["mtime"])
            group.sort(key=prio, reverse=True)
            keep = group[0]
            for r in group[1:]:
                parent = os.path.basename(os.path.dirname(r["src"])) or "_"
                sub = "_дубли" if r["sha256"] == keep["sha256"] else clean_name(parent, keep_dots=True)
                rel = os.path.relpath(r["dst"], self.order)
                r["dst"] = os.path.join(self.order, annul_dir(rel), sub, os.path.basename(rel))
                r["rule"], r["note"] = "version", ("дубль" if sub == "_дубли" else f"старая версия (из «{parent}»)")
                if sub != "_дубли" and os.path.splitext(r["dst"])[1].lower() in EXT_3D:
                    self.questions.append(f"Одинаковое имя, разное содержимое — проверьте, что это версии: {r['src']} / {keep['src']}")

    def folders(self):
        dirs = [os.path.join(self.order, d) for d in ORDER_TREE]
        prods = set(self.products.values())
        for r in self.rows:
            m = re.search(r"02_Металл[\\/]([^\\/]+)[\\/]", r["dst"])
            if m:
                prods.add(m.group(1))
        for folder in prods:
            dirs += [os.path.join(self.order, "02_Металл", folder, d) for d in PRODUCT_TREE]
        return sorted(set(dirs))


def annul_dir(dst_rel):
    """_Аннулировано того раздела/изделия, куда ушёл бы файл."""
    parts = re.split(r"[\\/]", dst_rel)
    for i, part in enumerate(parts):
        if part.startswith("И") and i > 0 and parts[i - 1].endswith("02_Металл"):
            return os.path.join(*parts[:i + 1], "_Аннулировано")
        if part in ("03_Корпус", "04_Войлок"):
            return os.path.join(*parts[:i + 1], "_Аннулировано")
    return os.path.join(*parts[:-1], "_Аннулировано") if len(parts) > 1 else "_Аннулировано"  # корень заказа


def batch(path, src):
    """Имя подпапки-партии (не шаблонной), чтобы сохранить её внутри типовой папки."""
    parent = os.path.basename(os.path.dirname(path))
    if os.path.dirname(path) == src or low(parent) in TEMPLATE_DIRS or re.match(r"^\d\d_", parent):
        return ""
    return clean_name(parent, keep_dots=True)


def sub_3d(path, src):
    parent = os.path.basename(os.path.dirname(path))
    grand = low(os.path.basename(os.path.dirname(os.path.dirname(path))))
    if grand in {"3d", "01_3d"} and low(parent) not in TEMPLATE_DIRS:
        return clean_name(parent, keep_dots=True)
    return ""


def write_outputs(pl, out):
    os.makedirs(out, exist_ok=True)
    cols = ["src", "dst", "action", "rule", "note", "size", "mtime", "sha256"]
    with open(os.path.join(out, "plan.csv"), "w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, cols, delimiter=";")
        w.writeheader()
        w.writerows(pl.rows)
    with open(os.path.join(out, "folders.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(pl.folders()) + "\n")
    with open(os.path.join(out, "meta.json"), "w", encoding="utf-8") as f:
        json.dump({"src": pl.src, "order": pl.order, "products": pl.products,
                   "created": datetime.datetime.now().isoformat(timespec="seconds")}, f, ensure_ascii=False, indent=1)
    tree = defaultdict(list)
    for r in pl.rows:
        key = os.path.relpath(os.path.dirname(r["dst"]), pl.order) if r["dst"] else "(не переносится)"
        tree[key].append(os.path.basename(r["src"]))
    lines = [f"Заказ: {pl.order}", f"Источник: {pl.src}", f"Файлов: {len(pl.rows)}", ""]
    for k in sorted(tree):
        lines.append(f"{k}\\   ({len(tree[k])})")
        lines += [f"    {n}" for n in sorted(tree[k])[:15]]
        if len(tree[k]) > 15:
            lines.append(f"    … ещё {len(tree[k]) - 15}")
    with open(os.path.join(out, "preview.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    with open(os.path.join(out, "questions.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(dict.fromkeys(pl.questions)) + "\n")
    by_rule = defaultdict(int)
    for r in pl.rows:
        by_rule[r["rule"]] += 1
    print(f"План: {os.path.join(out, 'plan.csv')}  файлов {len(pl.rows)}; вопросов {len(set(pl.questions))}")
    print("Изделия:", ", ".join(sorted(pl.products.values())) or "—")
    print("По правилам:", ", ".join(f"{k}={v}" for k, v in sorted(by_rule.items())))


def order_problems(order):
    """Расхождения папки заказа со структурой ТЗ-03: список строк, пустой — заказ в порядке."""
    order = os.path.abspath(order)
    name =os.path.basename(order.rstrip("\\/"))
    problems = [] if name.startswith("_") or ORDER_NAME.match(name) else [
        f"Имя заказа не по правилу <№>[-<п>]_<Код>_<Объект>_<Город> (код Т, Ал, Аст, ВЭД): {name}"]
    problems += [f"Нет папки: {d}" for d in ORDER_TREE if not os.path.isdir(os.path.join(order, d))]
    metal = os.path.join(order, "02_Металл")
    if os.path.isdir(metal):
        for p in os.listdir(metal):
            full = os.path.join(metal, p)
            if not os.path.isdir(full):
                problems.append(f"Файл прямо в 02_Металл: {p}")
                continue
            if p != PRODUCT_TEMPLATE and not re.match(r"^И\d{2}(_|$)", p):
                problems.append(f"Папка изделия не по правилу И<nn>_<шифр>_<наименование>: {p}")
            problems += [f"Нет папки: 02_Металл\\{p}\\{d}" for d in PRODUCT_TREE if not os.path.isdir(os.path.join(full, d))]
            chpu = os.path.join(full, "03_ЧПУ")
            if os.path.isdir(chpu):
                problems += [f"Файл прямо в 03_ЧПУ (нужно Лазер_Лист/Труборез): {p}\\{f}"
                             for f in os.listdir(chpu) if os.path.isfile(os.path.join(chpu, f))]
    allowed_top = {"01_Исходные данные", "02_Металл", "03_Корпус", "04_Войлок", "_Не разобрано", "_Аннулировано"}
    for p in os.listdir(order):
        if os.path.isdir(os.path.join(order, p)) and p not in allowed_top:
            problems.append(f"Лишняя папка в корне заказа: {p}")
    for root, _, files in os.walk(order):
        for f in files:
            full = os.path.join(root, f)
            if len(full) > MAX_PATH:
                problems.append(f"Путь длиннее {MAX_PATH}: {full}")
            if f.startswith("~$"):
                problems.append(f"Файл открыт кем-то (~$): {full}")
    return problems


def check(order):
    problems = order_problems(order)
    print("\n".join(problems) if problems else "Заказ соответствует структуре.")
    return 1 if problems else 0


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src")
    ap.add_argument("--order", required=True)
    ap.add_argument("--out")
    ap.add_argument("--product", action="append", default=[])
    ap.add_argument("--check", action="store_true")
    a = ap.parse_args()
    if a.check:
        sys.exit(check(a.order))
    if not (a.src and a.out):
        ap.error("нужны --src и --out (или --check)")
    pl = Planner(a.src, a.order, a.product)
    pl.build()
    write_outputs(pl, a.out)


if __name__ == "__main__":
    main()
