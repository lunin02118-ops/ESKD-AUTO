# -*- coding: utf-8 -*-
"""Что внутри файла IGES — без SolidWorks: сборка или деталь, сколько тел, габарит.

Выгрузка писала в IGS детали то, что было активно в SolidWorks, — всю сборку или соседнюю деталь, а в заголовке G
при этом стояло имя правильной детали (замечание владельца 24.09.2026). Имя файла и заголовок ничего не доказывают:
смотреть надо на сами сущности. Сборку выдают подфигуры (308 — определение, 408 — экземпляр); тела собираются из
обрезанных граней (144) по общим точкам их границ.
"""
import hashlib
import re
from pathlib import Path

# Подфигура и её экземпляр, внешняя ссылка, сетевая подфигура и её экземпляр — их пишет только сборка.
ASSEMBLY_TYPES = {308, 408, 416, 320, 420}
FACE_TYPES = {143, 144, 510}


def _fields(text, delimiter=",", end=";"):
    """Поля записи параметров IGES с учётом строк Холлерита («12HПРТИ…»)."""
    out, i, n, current = [], 0, len(text), ""
    while i < n:
        m = re.match(r"\s*(\d+)H", text[i:])
        if m and current.strip() == "":
            start = i + m.end()
            out.append(text[start:start + int(m.group(1))])
            i = start + int(m.group(1))
            while i < n and text[i] not in (delimiter, end):
                i += 1
            if i < n and text[i] == end:
                return out
            i += 1
            continue
        if text[i] == delimiter:
            out.append(current.strip())
            current = ""
        elif text[i] == end:
            out.append(current.strip())
            return out
        else:
            current += text[i]
        i += 1
    if current.strip():
        out.append(current.strip())
    return out


def _number(text):
    return float(text.replace("D", "E"))


def _compose(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) + (a[i][3] if j == 3 else 0.0) for j in range(4)] for i in range(3)]


def _apply(m, p):
    return [m[i][0] * p[0] + m[i][1] * p[1] + m[i][2] * p[2] + m[i][3] for i in range(3)]


class Entity:
    __slots__ = ("de", "type", "matrix", "params")


class Iges:
    """Файл IGES: сущности раздела D с параметрами раздела P."""

    def __init__(self, path):
        sections = {"S": [], "G": [], "D": [], "P": [], "T": []}
        for line in Path(path).read_bytes().decode("cp1251", errors="replace").splitlines():
            if len(line) >= 73 and line[72] in sections:
                sections[line[72]].append(line)
        header = "".join(line[:72] for line in sections["G"])
        delimiter, end = ",", ";"
        m = re.match(r"1H(.),1H(.),", header)
        if m:
            delimiter, end = m.group(1), m.group(2)
        self.header = _fields(header, delimiter, end)
        self.entities = {}
        d = sections["D"]
        for k in range(0, len(d) - 1, 2):
            e = Entity()
            e.de = int(d[k][73:80])
            e.type = int(d[k][0:8])
            e.matrix = int(d[k][48:56].strip() or 0)
            e.params = []
            self.entities[e.de] = e
        records = {}
        for line in sections["P"]:
            de = int(line[64:72])
            records[de] = records.get(de, "") + line[:64]
        for de, text in records.items():
            if de in self.entities:
                self.entities[de].params = _fields(text, delimiter, end)
        digest = hashlib.sha256()
        for line in sections["D"] + sections["P"]:
            digest.update(line[:72].encode("cp1251", errors="replace"))
        # Сумма геометрии — без разделов S и G, где имя файла и время записи: одинаковая у одной и той же детали.
        self.geometry_digest = digest.hexdigest()

    @property
    def source(self):
        """Имя модели из заголовка G (поле 3) — только для сообщений: при ошибке выгрузки там имя своей детали."""
        return self.header[2] if len(self.header) > 2 else ""

    @property
    def assembly_markers(self):
        return sorted({e.type for e in self.entities.values() if e.type in ASSEMBLY_TYPES})

    @property
    def subfigures(self):
        """Имена подфигур (308) — деталей сборки, попавших в файл."""
        return [e.params[2] if len(e.params) > 2 else "" for e in self.entities.values() if e.type == 308]

    def faces(self):
        return [e for e in self.entities.values() if e.type in FACE_TYPES]

    def _matrix(self, de, depth=0):
        e = self.entities.get(de)
        if e is None or depth > 8:
            return None
        p = [_number(x) for x in e.params[1:13]]
        own = [p[0:4], p[4:8], p[8:12]]
        outer = self._matrix(e.matrix, depth + 1)
        return own if outer is None else _compose(outer, own)

    def _curve_points(self, de, depth=0):
        e = self.entities.get(de)
        if e is None or depth > 6:
            return []
        f = e.params
        points = []
        try:
            if e.type == 110:  # отрезок
                v = [_number(x) for x in f[1:7]]
                points = [v[0:3], v[3:6]]
            elif e.type == 102:  # составная кривая
                for x in f[2:2 + int(f[1])]:
                    points.extend(self._curve_points(int(x), depth + 1))
            elif e.type == 126:  # сплайн — по полюсам
                k, m = int(f[1]), int(f[2])
                start = 7 + (1 + k - m + 2 * m + 1) + (k + 1)
                c = [_number(x) for x in f[start:start + 3 * (k + 1)]]
                points = [c[i:i + 3] for i in range(0, len(c), 3)]
            elif e.type == 100:  # дуга — по концам
                z, _, _, x2, y2, x3, y3 = (_number(x) for x in f[1:8])
                points = [[x2, y2, z], [x3, y3, z]]
            elif e.type == 142:  # кривая на поверхности — её модельная кривая
                points = self._curve_points(int(f[4]), depth + 1)
            elif e.type == 116:  # точка
                points = [[_number(x) for x in f[1:4]]]
        except (ValueError, IndexError):
            return []
        m = self._matrix(e.matrix)
        return [_apply(m, p) for p in points] if m else points

    def face_points(self, face):
        """Точки границ обрезанной грани (144): внешняя граница и внутренние."""
        f = face.params
        points = []
        if face.type == 144 and len(f) > 4:
            inner = int(f[3]) if f[3] else 0
            for de in [int(f[4]) if f[4] else 0] + [int(x) for x in f[5:5 + inner]]:
                points.extend(self._curve_points(de))
        m = self._matrix(face.matrix)
        return [_apply(m, p) for p in points] if m else points

    def extents(self):
        """Размах X, Y, Z по границам граней, мм; None — граней нет."""
        points = [p for face in self.faces() for p in self.face_points(face)]
        if not points:
            return None
        return [round(max(p[i] for p in points) - min(p[i] for p in points), 3) for i in range(3)]

    def bodies(self, tolerance=0.01):
        """Тела — связные наборы граней: грани одного тела делят точки границ."""
        faces = self.faces()
        parent = list(range(len(faces)))

        def root(i):
            while parent[i] != i:
                parent[i] = parent[parent[i]]
                i = parent[i]
            return i
        owner = {}
        for i, face in enumerate(faces):
            for key in {tuple(round(c / tolerance) for c in p) for p in self.face_points(face)}:
                if key in owner:
                    a, b = root(i), root(owner[key])
                    if a != b:
                        parent[a] = b
                else:
                    owner[key] = i
        groups = {}
        for i, face in enumerate(faces):
            groups.setdefault(root(i), []).append(face)
        return list(groups.values())


def describe(path):
    """Коротко — для сообщения о провале: что на самом деле лежит в файле."""
    g = Iges(path)
    if g.assembly_markers:
        return f"сборка: подфигуры {g.subfigures}"
    return f"тел {len(g.bodies())}, граней {len(g.faces())}, размах {g.extents()}"


def assert_part(case, path, bodies, extents, delta=1.0):
    """В IGS — только своя деталь: без подфигур сборки, своё число тел и свой габарит (в любом порядке осей, мм).
    Имя файла и заголовок G ничего не доказывают: выгрузка писала в «<деталь>.igs» всю сборку или соседнюю деталь,
    а заголовок называл правильную деталь (замечание владельца 24.09.2026)."""
    path = Path(path)
    model = Iges(path)
    case.assertEqual([], model.assembly_markers, f"{path.name}: в IGS сборка, подфигуры {model.subfigures}")
    found = f"{path.name}: {describe(path)}"
    case.assertEqual(bodies, len(model.bodies()), found)
    for actual, expected in zip(sorted(model.extents() or [0.0, 0.0, 0.0]), sorted(extents)):
        case.assertAlmostEqual(expected, actual, delta=delta, msg=found)
