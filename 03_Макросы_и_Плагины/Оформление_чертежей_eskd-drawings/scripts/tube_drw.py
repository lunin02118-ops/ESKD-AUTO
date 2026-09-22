# -*- coding: utf-8 -*-
"""
Чертёж детали из профильной трубы (лазерный труборез) по ЕСКД через SolidWorks API.
Главный вид — ось трубы горизонтально, со стороны торцевых срезов/отверстий; вид слева — профиль;
дополнительные виды — для отверстий на других стенках. Длинные трубы — с разрывом (ГОСТ 2.305 п.6.x).
Размеры: габаритная длина, углы реза, координаты и диаметры отверстий, уступы на торцах;
сечение профиля — справочные (*). ТТ — над основной надписью (ГОСТ 2.316).
Запуск: tube_drw.py <подстрока имени> [--keep]   (--keep — не закрывать чертёж)
"""
import os, sys, math, time, glob
import pythoncom
import win32com.client as w
sys.stdout.reconfigure(encoding="utf-8")
SW = w.gencache.EnsureModule("{83A33D31-27C5-11CE-BFD4-00400513BB57}", 0, 33, 0)
# папка 01_3D изделия: переменная ESKD_DRW_ROOT или текущая папка
ROOT = os.environ.get("ESKD_DRW_ROOT") or os.getcwd()
FMT_DIR = r"D:\Work\_Инструменты_Конструктора\02_Шаблоны_и_Форматки\Основные надписи"
TT_DIR = r"D:\Work\_Инструменты_Конструктора\03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\ТТ"
FORMATS = {
    "A4-P-1": dict(paper=7, w=0.210, h=0.297, area=(28, 68, 202, 268)),
    "A3-A-1": dict(paper=8, w=0.420, h=0.297, area=(28, 68, 412, 268)),
}
SCALES = [(1, 1), (1, 2), (1, 2.5), (1, 4), (1, 5), (1, 10)]
TT_TUBE = ["*Размеры для справок.",
           "Деталь изготовить на лазерном труборезе по программе ЧПУ.",
           "Точность реза - класс 1 по ГОСТ ИСО 9013-2017.",
           "Общие допуски: ГОСТ 30893.1-m, ГОСТ 30893.2-K.",
           "Удалить грат и заусенцы. Острые кромки притупить."]
VIEWS = {(0, 0, 1): "*Спереди", (0, 0, -1): "*Сзади", (0, 1, 0): "*Сверху", (0, -1, 0): "*Снизу",
         (1, 0, 0): "*Справа", (-1, 0, 0): "*Слева"}
MM = 0.001
# черновики и снимки для проверки — ESKD_DRW_SNAP или временная папка
import tempfile
SNAP = os.environ.get("ESKD_DRW_SNAP") or tempfile.gettempdir()


def at(*v):
    return w.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8, list(v))


def dot(a, b): return sum(x * y for x, y in zip(a, b))
def sub(a, b): return [x - y for x, y in zip(a, b)]
def add(a, b): return [x + y for x, y in zip(a, b)]
def mul(a, k): return [x * k for x in a]
def cross(a, b): return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
def norm(a):
    l = math.sqrt(dot(a, a)); return [x / l for x in a] if l > 1e-12 else a


sw = w.GetActiveObject("SldWorks.Application")
mu = SW.IMathUtility(sw.GetMathUtility()._oleobj_)


def log(*a):
    print(*a, flush=True)


# ---------- анализ детали ----------
class Tube:
    def __init__(self, path):
        self.path = path
        d = sw.GetOpenDocumentByName(path) or sw.OpenDoc6(path, 1, 1 | 2, "", 0, 0)[0]
        self.m = SW.IModelDoc2(d._oleobj_)
        part = SW.IPartDoc(self.m._oleobj_)
        b = part.GetPartBox(True)
        self.lo, self.hi = list(b[:3]), list(b[3:])
        size = [self.hi[i] - self.lo[i] for i in range(3)]
        self.ax = size.index(max(size))
        self.a = [0, 0, 0]; self.a[self.ax] = 1
        self.L = size[self.ax]
        self.cen = [(self.lo[i] + self.hi[i]) / 2 for i in range(3)]
        self.size = size
        self.miter = None
        self.holes = []   # (d, axis(3), center(3))
        for body in part.GetBodies2(0, True) or []:
            for fc in SW.IBody2(body._oleobj_).GetFaces() or []:
                f = SW.IFace2(fc._oleobj_)
                s = SW.ISurface(f.GetSurface()._oleobj_)
                if s.IsPlane():
                    n = list(f.Normal)
                    c = abs(dot(n, self.a))
                    if 0.05 < c < 0.99:
                        self.miter = norm(sub(n, mul(self.a, dot(n, self.a))))
                elif s.IsCylinder():
                    p = s.CylinderParams
                    axis = [round(x, 4) for x in p[3:6]]
                    if abs(dot(axis, self.a)) > 0.9:
                        continue  # радиусы гиба профиля
                    fb = f.GetBox()
                    self.holes.append((round(p[6] * 2000, 2), axis, [(fb[i] + fb[i + 3]) / 2 for i in range(3)]))
        self.mat = self.prop("Материал")
        self.name = self.prop("Наименование")

    def prop(self, name):
        for cfg in ("", self.m.ConfigurationManager.ActiveConfiguration.Name):
            r = self.m.Extension.CustomPropertyManager(cfg).Get6(name, False)
            if isinstance(r, tuple) and (r[2] or r[1]):
                return r[2] or r[1]
        return ""

    def main_normal(self):
        if self.miter:
            v = cross(self.a, self.miter)
        else:
            cnt = {}
            for d, axis, c in self.holes:
                k = tuple(abs(round(x)) for x in axis)
                cnt[k] = cnt.get(k, 0) + 1
            if cnt:
                v = list(max(cnt, key=cnt.get))
            else:  # без отверстий — на широкую стенку
                lat = [i for i in range(3) if i != self.ax]
                i = min(lat, key=lambda j: self.size[j])
                v = [0, 0, 0]; v[i] = 1
        v = [int(round(x)) for x in v]
        # сторона: отверстия ближе к зрителю
        side = [dot(sub(c, self.cen), v) for d, axis, c in self.holes if abs(dot(axis, v)) > 0.9]
        if side and sum(side) < 0:
            v = [-x for x in v]
        return tuple(v)


# ---------- чертёж ----------
def ok_dialog_watcher():
    import subprocess
    ps = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ok_sheet_dialog.ps1")
    return subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps, "-Minutes", "3"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def wait_idle(timeout=20.0):
    end = time.time() + timeout
    while time.time() < end:
        try:
            a = w.dynamic.Dispatch(sw.GetAddInObject("ESKD.MaterialSync.SwAddin_v5"))
            v = a.PendingIdleTasks
            if int(v() if callable(v) else v) == 0:
                return True
        except Exception:
            pass
        time.sleep(0.3)
    return False


class Drw:
    def __init__(self, fmt, scale):
        self.f = FORMATS[fmt]
        self.fmt = fmt
        self.k = scale[0] / scale[1]
        watcher = ok_dialog_watcher()
        time.sleep(1.0)
        new = sw.NewDocument(sw.GetUserPreferenceStringValue(10), self.f["paper"], self.f["w"], self.f["h"])
        watcher.kill()
        self.m = SW.IModelDoc2(new._oleobj_)
        self.d = SW.IDrawingDoc(new._oleobj_)
        sh = SW.ISheet(self.d.GetCurrentSheet()._oleobj_)
        ok = self.d.SetupSheet5(sh.GetName(), self.f["paper"], 12, scale[0], scale[1], True,
                                os.path.join(FMT_DIR, fmt + ".slddrt"), self.f["w"], self.f["h"], "По умолчанию", True)
        self.scale = scale
        log("лист", fmt, scale, "формат задан" if ok else "ФОРМАТ НЕ ЗАДАН")

    def fix_scale(self):
        """Первый вставленный вид сбивает масштаб листа (автомасштаб SW) — вернуть масштаб по плану."""
        SW.ISheet(self.d.GetCurrentSheet()._oleobj_).SetScale(self.scale[0], self.scale[1], True, False)
        self.m.ForceRebuild3(False)

    def view(self, path, name, x, y):
        v = self.d.CreateDrawViewFromModelView3(path, name, x, y, 0)
        v = SW.IView(v._oleobj_)
        v.UseSheetScale = 1
        return v

    def unfolded(self, parent, x, y):
        self.m.ClearSelection2(True)
        self.m.Extension.SelectByID2(parent.GetName2(), "DRAWINGVIEW", 0, 0, 0, False, 0, None, 0)
        v = self.d.CreateUnfoldedViewAt3(x, y, 0, False)
        self.m.ClearSelection2(True)
        return SW.IView(v._oleobj_) if v is not None else None


def xf_point(view, p):
    t = view.ModelToViewTransform
    mp = SW.IMathPoint(mu.CreatePoint(at(*p))._oleobj_)
    r = SW.IMathPoint(mp.MultiplyTransform(t)._oleobj_).ArrayData
    return r[0], r[1]


def xf_dir(view, v):
    t = view.ModelToViewTransform
    mv = SW.IMathVector(mu.CreateVector(at(*v))._oleobj_)
    r = SW.IMathVector(mv.MultiplyTransform(t)._oleobj_).ArrayData
    return r[0], r[1], r[2]


def view_normal(view):
    for v in [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)]:
        s = xf_dir(view, v)
        n = math.sqrt(sum(x * x for x in s))
        if s[2] / n > 0.99:
            return v
    return None


def outline(view):
    o = view.GetOutline()
    return [x / MM for x in o]  # мм: x0,y0,x1,y1


def place(view, cx, cy):
    """Центр контура вида — в точку (мм)."""
    o = view.GetOutline(); p = view.Position
    ox = (o[0] + o[2]) / 2 - p[0]; oy = (o[1] + o[3]) / 2 - p[1]
    view.Position = at(cx * MM - ox, cy * MM - oy)


class Geo:
    """Видимая геометрия вида в координатах листа (м)."""
    def __init__(self, view):
        self.v = view
        self.lines, self.circles, self.verts = [], [], []
        for e in view.GetVisibleEntities2(None, 1) or []:
            edge = SW.IEdge(e._oleobj_)
            c = SW.ICurve(edge.GetCurve()._oleobj_)
            if c.IsLine():
                sv, ev = edge.GetStartVertex(), edge.GetEndVertex()
                if sv is None or ev is None:
                    continue
                p1 = SW.IVertex(sv._oleobj_).GetPoint(); p2 = SW.IVertex(ev._oleobj_).GetPoint()
                q1, q2 = xf_point(view, p1), xf_point(view, p2)
                self.lines.append((e, q1, q2))
                # вершины — концы видимых рёбер (вершины из GetVisibleEntities2 даны в другой системе)
                for vx, q in ((sv, q1), (ev, q2)):
                    if not any(near(q, o) for _, o in self.verts):
                        self.verts.append((vx, q))
            elif c.IsCircle():
                cp = c.CircleParams
                cc = xf_point(view, cp[:3])
                dv = xf_dir(view, cp[3:6])
                s = abs(dv[2]) / math.sqrt(sum(x * x for x in dv))  # в преобразовании есть масштаб вида
                if s > 0.99:  # окружность видна окружностью
                    self.circles.append((e, cc, cp[6] * 2, list(cp[:3]), edge.GetStartVertex() is None))

    def box(self):
        pts = [p for _, p in self.verts] + [p for _, a, b in self.lines for p in (a, b)]
        xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
        return min(xs), min(ys), max(xs), max(ys)


def sel(view, ents):
    ok = True
    for i, e in enumerate(ents):
        ok = bool(view.SelectEntity(e, i > 0)) and ok
    return ok


class Dims:
    def __init__(self, drw):
        self.drw = drw
        self.made = []

    def _wrap(self, dd, prefix=None, what=""):
        if dd is None:
            log("   НЕ создан размер:", what)
            return None
        dd = SW.IDisplayDimension(dd._oleobj_)
        if prefix:
            cur = dd.GetText(1) or ""  # у диаметра в префиксе стоит <MOD-DIAM> — дописываем перед ним
            dd.SetText(1, prefix + cur)
        self.made.append((what, dd))
        return dd

    def horiz(self, view, e1, e2, x, y, prefix=None, what=""):
        self.drw.m.ClearSelection2(True)
        sel(view, [e1, e2])
        r = self.drw.m.AddHorizontalDimension2(x, y, 0)
        self.drw.m.ClearSelection2(True)
        return self._wrap(r, prefix, what)

    def vert(self, view, e1, e2, x, y, prefix=None, what=""):
        self.drw.m.ClearSelection2(True)
        sel(view, [e1, e2])
        r = self.drw.m.AddVerticalDimension2(x, y, 0)
        self.drw.m.ClearSelection2(True)
        return self._wrap(r, prefix, what)

    def diam(self, view, e, x, y, prefix=None, what="", radius=False):
        self.drw.m.ClearSelection2(True)
        sel(view, [e])
        r = self.drw.m.AddRadialDimension2(x, y, 0) if radius else self.drw.m.AddDiameterDimension2(x, y, 0)
        self.drw.m.ClearSelection2(True)
        return self._wrap(r, prefix, what)

    def free(self, view, ents, x, y, prefix=None, what=""):
        self.drw.m.ClearSelection2(True)
        sel(view, ents)
        r = self.drw.m.AddDimension2(x, y, 0)
        self.drw.m.ClearSelection2(True)
        return self._wrap(r, prefix, what)


def is_h(l, tol=1e-6): return abs(l[2][1] - l[1][1]) < tol and abs(l[2][0] - l[1][0]) > tol
def is_v(l, tol=1e-6): return abs(l[2][0] - l[1][0]) < tol and abs(l[2][1] - l[1][1]) > tol
def llen(l): return math.hypot(l[2][0] - l[1][0], l[2][1] - l[1][1])
def near(p, q, tol=1e-6): return abs(p[0] - q[0]) < tol and abs(p[1] - q[1]) < tol


def dim_main(dm, view, t, k):
    g = Geo(view)
    x0, y0, x1, y1 = g.box()
    ym = (y0 + y1) / 2
    step = 8 * MM
    # габаритная длина — между крайними вершинами, над видом
    left = min(g.verts, key=lambda v: (round(v[1][0], 6), -v[1][1]))
    right = max(g.verts, key=lambda v: (round(v[1][0], 6), v[1][1]))
    top_row = y1 + 10 * MM
    dm.horiz(view, left[0], right[0], (x0 + x1) / 2, top_row + step * 0.0, what="длина")
    # углы реза
    slanted = [l for l in g.lines if not is_h(l) and not is_v(l)]
    hor = sorted([l for l in g.lines if is_h(l)], key=llen, reverse=True)
    done_ends = set()
    for sl in slanted:
        side = "L" if min(sl[1][0], sl[2][0]) < (x0 + x1) / 2 else "R"
        if side in done_ends:
            continue
        for h in hor:
            corner = next((p for p in (sl[1], sl[2]) if near(p, h[1]) or near(p, h[2])), None)
            if corner is None:
                continue
            o1 = sl[2] if near(corner, sl[1]) else sl[1]
            o2 = h[2] if near(corner, h[1]) else h[1]
            u1 = norm([o1[0] - corner[0], o1[1] - corner[1], 0]); u2 = norm([o2[0] - corner[0], o2[1] - corner[1], 0])
            if dot(u1, u2) <= 0:
                continue  # тупой угол у пятки — нужен острый у носка
            b = norm(add(u1, u2))
            r = 14 * MM
            dm.free(view, [sl[0], h[0]], corner[0] + b[0] * r, corner[1] + b[1] * r, what="угол реза " + side)
            done_ends.add(side)
            break
    # отверстия — видимые окружностями
    groups = {}
    for c in g.circles:
        groups.setdefault(round(c[2] / MM, 1), []).append(c)
    row = y0 - 10 * MM
    for dia, cs in sorted(groups.items()):
        cs.sort(key=lambda c: c[1][0])
        n = len(cs)
        c = cs[0]
        dm.free(view, [c[0]], c[1][0] + 12 * MM, y1 + 4 * MM + 0 * MM, prefix=("%d отв. " % n) if n > 1 else None, what="Ø%.1f" % dia)
        for c in cs:
            ref = left if c[1][0] - x0 <= x1 - c[1][0] else right
            dm.horiz(view, ref[0], c[0], (ref[1][0] + c[1][0]) / 2, row, what="коорд. отв. Ø%.1f" % dia)
            row -= step
        # по высоте — если не по оси стенки
        for c in cs[:1]:
            if abs(c[1][1] - ym) > 0.2 * MM:
                edge = min([l for l in hor], key=lambda l: abs(l[1][1] - c[1][1]))
                dm.vert(view, edge[0], c[0], x0 - 8 * MM, (edge[1][1] + c[1][1]) / 2, what="высота отв.")
    return g


def dim_end(dm, view):
    g = Geo(view)
    x0, y0, x1, y1 = g.box()
    vl = [l for l in g.lines if is_v(l)]; hl = [l for l in g.lines if is_h(l)]
    if len(vl) >= 2:
        a = min(vl, key=lambda l: l[1][0]); b = max(vl, key=lambda l: l[1][0])
        dm.horiz(view, a[0], b[0], (x0 + x1) / 2, y0 - 8 * MM, prefix="*", what="сечение ширина")
    if len(hl) >= 2:
        a = min(hl, key=lambda l: l[1][1]); b = max(hl, key=lambda l: l[1][1])
        dm.vert(view, a[0], b[0], x1 + 8 * MM, (y0 + y1) / 2, prefix="*", what="сечение высота")
    return g


def features_along(t, view, g):
    """Интервалы (x листа) с элементами, которые нельзя разрывать: отверстия и зоны торцов."""
    x0, y0, x1, y1 = g.box()
    keep = []
    for c in g.circles:
        keep.append((c[1][0] - c[2], c[1][0] + c[2]))
    xs = sorted({round(v[1][0], 6) for v in g.verts})
    # зоны торцов: вершины у концов (срезы, уступы)
    lz = [x for x in xs if x - x0 < 0.6 * (x1 - x0) / 2]
    rz = [x for x in xs if x1 - x < 0.6 * (x1 - x0) / 2]
    keep.append((x0, max(lz)))
    keep.append((min(rz), x1))
    keep.sort()
    merged = []
    for s_, e_ in keep:
        if merged and s_ <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(merged[-1][1], e_))
        else:
            merged.append((s_, e_))
    return merged


def insert_break(drw, view, t, g, target_w):
    x0, y0, x1, y1 = g.box()
    total = x1 - x0
    if total <= target_w:
        return False
    feats = features_along(t, view, g)
    log("   box %s outline %s pos %s feats %s" % ([round(x / MM) for x in g.box()], [round(x) for x in outline(view)],
        [round(x / MM) for x in view.Position], [(round(a_ / MM), round(b_ / MM)) for a_, b_ in feats]))
    gaps = [(feats[i][1], feats[i + 1][0]) for i in range(len(feats) - 1)]
    gaps = [gp for gp in gaps if gp[1] - gp[0] > 12 * MM]
    if not gaps:
        log("   разрыв: нет свободного участка")
        return False
    gp = max(gaps, key=lambda gp: gp[1] - gp[0])
    margin = 5 * MM
    cut = min(total - target_w, gp[1] - gp[0] - 2 * margin)
    mid = (gp[0] + gp[1]) / 2
    p1, p2 = mid - cut / 2, mid + cut / 2
    px = view.Position[0]  # положение линий разрыва — относительно точки привязки вида
    bl = view.InsertBreak3(2, p1 - px, p2 - px, 3, 1, False)
    drw.m.ClearSelection2(True)
    drw.m.Extension.SelectByID2(view.GetName2(), "DRAWINGVIEW", 0, 0, 0, False, 0, None, 0)
    drw.d.BreakView()
    drw.m.ClearSelection2(True)
    drw.m.ForceRebuild3(False)
    log("   разрыв %.1f…%.1f мм листа: %s, IsBroken=%s" % (p1 / MM, p2 / MM, bl is not None, view.IsBroken()))
    return True


def add_tt(lines):
    sys.path.insert(0, TT_DIR)
    import apply_tt_profile as tt
    tt.load_profiles = lambda: {"Деталь: Лазерная резка трубы": lines}
    return tt.apply_profile_to_active_drawing("Деталь: Лазерная резка трубы", False)


def hole_dirs(t):
    """Направления взгляда, с которых отверстия видны окружностями: {(ось со знаком стенки): [(d, s по оси)]}.
    Сквозные (одинаковые на противоположных стенках) — одно направление."""
    groups = {}
    for d, axis, c in t.holes:
        ax = [abs(round(x)) for x in axis]
        side = 1 if dot(sub(c, t.cen), ax) >= 0 else -1
        key = tuple(x * side for x in ax)
        groups.setdefault(key, set()).add((d, round(c[t.ax] * 1000, 1)))
    out = {}
    for key, items in groups.items():
        opp = tuple(-x for x in key)
        if opp in out and out[opp] == items:
            continue  # сквозные
        out[key] = items
    return out


def pick_main(t, dirs):
    if t.miter:
        v = [int(round(x)) for x in cross(t.a, t.miter)]
        if tuple(-x for x in v) in dirs:
            v = [-x for x in v]
        return tuple(v)
    keys = list(dirs)
    if not keys:
        lat = [i for i in range(3) if i != t.ax]
        i = min(lat, key=lambda j: t.size[j])
        v = [0, 0, 0]; v[i] = 1
        return tuple(v)
    axes = {tuple(abs(x) for x in k) for k in keys}
    if len(keys) == 1 or len(axes) > 1:
        return max(keys, key=lambda k: len(dirs[k]))
    # отверстия на противоположных стенках разные — главный вид сбоку, стенки — видами сверху/снизу
    # (проекционный вид строится только вверх-вниз, вид «сзади» из главного не получить)
    ax = next(iter(axes))
    lat = [i for i in range(3) if i != t.ax and ax[i] == 0][0]
    v = [0, 0, 0]; v[lat] = 1
    return tuple(v)


def axis_x(t, view, s):
    p = list(t.cen); p[t.ax] = s
    return xf_point(view, p)[0]


def keep_zones(t, view):
    """Интервалы x листа, где нельзя ставить разрыв: зоны торцов и отверстия."""
    lo, hi = t.lo[t.ax], t.hi[t.ax]
    zl, zh = lo, hi
    for body in SW.IPartDoc(t.m._oleobj_).GetBodies2(0, True) or []:
        for fc in SW.IBody2(body._oleobj_).GetFaces() or []:
            f = SW.IFace2(fc._oleobj_)
            s = SW.ISurface(f.GetSurface()._oleobj_)
            if s.IsPlane() and abs(dot(list(f.Normal), t.a)) > 0.05:
                b = f.GetBox()
                if b[t.ax] - lo < t.L / 2:
                    zl = max(zl, b[t.ax + 3])
                else:
                    zh = min(zh, b[t.ax])
    zones = [(lo, zl + 0.005), (zh - 0.005, hi)]
    for d, axis, c in t.holes:
        r = d / 2000 + 0.004
        zones.append((c[t.ax] - r, c[t.ax] + r))
    xs = []
    for a_, b_ in zones:
        p, q = axis_x(t, view, a_), axis_x(t, view, b_)
        xs.append((min(p, q), max(p, q)))
    xs.sort()
    merged = []
    for s_, e_ in xs:
        if merged and s_ <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(merged[-1][1], e_))
        else:
            merged.append((s_, e_))
    return merged


def insert_break2(drw, view, t, target_w):
    o = view.GetOutline()
    total = o[2] - o[0]
    if total <= target_w:
        return False
    feats = keep_zones(t, view)
    gaps = [(feats[i][1], feats[i + 1][0]) for i in range(len(feats) - 1)]
    gaps = [gp for gp in gaps if gp[1] - gp[0] > 12 * MM]
    if not gaps:
        log("   разрыв: нет свободного участка")
        return False
    need = total - target_w
    px = view.Position[0]
    # несколько разрывов, если один участок мал: берём самые длинные
    gaps.sort(key=lambda gp: gp[1] - gp[0], reverse=True)
    n = 0
    for gp in gaps:
        if need <= 0:
            break
        cut = min(need, gp[1] - gp[0] - 10 * MM)
        if cut < 10 * MM:
            continue
        mid = (gp[0] + gp[1]) / 2
        view.InsertBreak3(2, mid - cut / 2 - px, mid + cut / 2 - px, 3, 1, False)
        need -= cut
        n += 1
    drw.m.ClearSelection2(True)
    drw.m.Extension.SelectByID2(view.GetName2(), "DRAWINGVIEW", 0, 0, 0, False, 0, None, 0)
    drw.d.BreakView()
    drw.m.ClearSelection2(True)
    drw.m.ForceRebuild3(False)
    log("   разрывов %d, IsBroken=%s, ширина %.0f мм" % (n, view.IsBroken(), (view.GetOutline()[2] - view.GetOutline()[0]) / MM))
    return True


def dim_holes(dm, view, g, left, right, row, step, above_y, t=None):
    n_view = view_normal(view)
    x0, y0, x1, y1 = g.box()
    ym = (y0 + y1) / 2
    hor = [l for l in g.lines if is_h(l)]
    cs_all = g.circles
    if t is not None and n_view:
        # только стенка со стороны зрителя: дальняя видна сквозь отверстие и на этом виде не образмеривается
        near_ = [c for c in cs_all if dot(sub(c[3], t.cen), n_view) > 0]
        cs_all = near_
    groups = {}
    for c in cs_all:
        groups.setdefault((round(c[2] / MM, 1), c[4]), []).append(c)
    rows = {"L": row, "R": row}
    for (dia, full), cs in sorted(groups.items()):
        cs.sort(key=lambda c: c[1][0])
        c = cs[0]
        if full:
            pre = ("%d отв. " % len(cs)) if len(cs) > 1 else None
            dm.diam(view, c[0], c[1][0] + 7 * MM, above_y + 4 * MM, prefix=pre, what="Ø%.1f" % dia)
        else:
            dm.diam(view, c[0], c[1][0] + 7 * MM, above_y + 4 * MM, what="R%.1f" % (dia / 2), radius=True)
            continue  # скругления уступа — только радиус
        for c in cs:
            side = "L" if c[1][0] - x0 <= x1 - c[1][0] else "R"
            ref = left if side == "L" else right
            if abs(ref[1][0] - c[1][0]) < 0.3 * MM:
                continue
            dm.horiz(view, ref[0], c[0], (ref[1][0] + c[1][0]) / 2, rows[side], what="коорд. Ø%.1f" % dia)
            rows[side] -= step
        if abs(c[1][1] - ym) > 0.3 * MM and hor:
            edge = min(hor, key=lambda l: abs(l[1][1] - c[1][1]))
            dm.vert(view, edge[0], c[0], x0 - 8 * MM, (edge[1][1] + c[1][1]) / 2, what="высота Ø%.1f" % dia)
    return min(rows.values())


def dim_tongues(dm, view, g):
    """Высота язычка на торце: короткая горизонтальная линия у конца — от противоположной наружной кромки."""
    x0, y0, x1, y1 = g.box()
    L = x1 - x0
    hor = [l for l in g.lines if is_h(l)]
    outer_top = max(hor, key=lambda l: l[1][1]); outer_bot = min(hor, key=lambda l: l[1][1])
    done = set()
    for l in hor:
        xa, xb = sorted((l[1][0], l[2][0]))
        if llen(l) > 0.2 * L:
            continue
        side = "L" if xa - x0 < 0.15 * L else ("R" if x1 - xb < 0.15 * L else None)
        if side is None or side in done:
            continue
        yl = l[1][1]
        if abs(yl - y0) < 0.3 * MM or abs(yl - y1) < 0.3 * MM:
            continue
        far = outer_bot if abs(yl - y1) < abs(yl - y0) else outer_top
        xd = x0 - 8 * MM if side == "L" else x1 + 8 * MM
        dm.vert(view, l[0], far[0], xd, (yl + far[1][1]) / 2, what="высота язычка " + side)
        done.add(side)


def dim_steps(dm, view, g, left, right, row_y, t):
    """Уступы торцов: вершины у концов, отличные от крайней, — размер от торца."""
    x0, y0, x1, y1 = g.box()
    circ_pts = [c[1] for c in g.circles]
    xs = sorted({round(v[1][0], 5) for v in g.verts})
    zone = 0.15 * (x1 - x0)
    made = 0
    for ref, sgn in ((left, 1), (right, -1)):
        cand = [v for v in g.verts if 0.3 * MM < (v[1][0] - ref[1][0]) * sgn < zone]
        seen = []
        for v in sorted(cand, key=lambda v: abs(v[1][0] - ref[1][0])):
            if any(abs(v[1][0] - s_) < 0.3 * MM for s_ in seen):
                continue
            if any(math.hypot(v[1][0] - p[0], v[1][1] - p[1]) < 6 * MM for p in circ_pts):
                continue  # концы дуг полуотверстий
            seen.append(v[1][0])
            dm.horiz(view, ref[0], v[0], (ref[1][0] + v[1][0]) / 2, row_y, what="уступ")
            made += 1
            break  # один уступ на торец
    return made


def extremes(g):
    left = min(g.verts, key=lambda v: (round(v[1][0], 5), -v[1][1]))
    right = max(g.verts, key=lambda v: (round(v[1][0], 5), v[1][1]))
    return left, right


def dim_angles(dm, view, g):
    x0, y0, x1, y1 = g.box()
    slanted = [l for l in g.lines if not is_h(l) and not is_v(l)]
    hor = sorted([l for l in g.lines if is_h(l)], key=llen, reverse=True)
    done = set()
    for sl in slanted:
        side = "L" if min(sl[1][0], sl[2][0]) < (x0 + x1) / 2 else "R"
        if side in done:
            continue
        for h in hor:
            corner = next((p for p in (sl[1], sl[2]) if near(p, h[1]) or near(p, h[2])), None)
            if corner is None:
                continue
            o1 = sl[2] if near(corner, sl[1]) else sl[1]
            o2 = h[2] if near(corner, h[1]) else h[1]
            u1 = norm([o1[0] - corner[0], o1[1] - corner[1], 0]); u2 = norm([o2[0] - corner[0], o2[1] - corner[1], 0])
            if dot(u1, u2) <= 0:
                continue
            b = norm(add(u1, u2))
            dm.free(view, [sl[0], h[0]], corner[0] + b[0] * 14 * MM, corner[1] + b[1] * 14 * MM, what="угол " + side)
            done.add(side)
            break


def run(stem, keep=False, out_dir=None, fmt="A4-P-1"):
    path = os.path.join(ROOT, stem + ".SLDPRT")
    t = Tube(path)
    dirs = hole_dirs(t)
    vn = pick_main(t, dirs)
    extra_dirs = [d for d in dirs if d != vn]
    log("деталь %s L=%.1f ось %s срез %s главный %s доп. %s" % (os.path.basename(path), t.L * 1000, "XYZ"[t.ax],
        t.miter, VIEWS[vn], [VIEWS.get(d, d) for d in extra_dirs]))
    scale = (1, 2)
    drw = Drw(fmt, scale)
    k = scale[0] / scale[1]
    a = drw.f["area"]
    main = drw.view(path, VIEWS[vn], (a[0] + 80) * MM, 200 * MM)
    drw.fix_scale()
    sd = xf_dir(main, t.a)
    ang = math.atan2(sd[1], sd[0])
    if abs(math.sin(ang)) > 0.01:
        drw.m.ClearSelection2(True)
        drw.m.Extension.SelectByID2(main.GetName2(), "DRAWINGVIEW", 0, 0, 0, False, 0, None, 0)
        drw.d.DrawingViewRotate(-ang)
        drw.m.ClearSelection2(True)
    main.SetDisplayMode4(False, 2, False, True, False)
    drw.m.ForceRebuild3(False)
    prof = sorted(t.size[i] for i in range(3) if i != t.ax)
    target = (a[2] - a[0]) * MM - (prof[0] + prof[1]) * k - 65 * MM
    insert_break2(drw, main, t, target)
    wv = (main.GetOutline()[2] - main.GetOutline()[0])
    end_w = (prof[0] + prof[1]) * k / 2 + 10 * MM  # вид слева (сторона профиля) и его размер
    if fmt == "A4-P-1" and wv > (a[2] - a[0]) * MM - 22 * MM - 30 * MM - end_w:
        log("   на A4 не помещается (%.0f мм) — A3" % (wv / MM))
        sw.CloseDoc(drw.m.GetTitle())
        return run(stem, keep, out_dir, "A3-A-1")
    o = outline(main)
    block = (o[2] - o[0]) + 30 + (prof[0] + prof[1]) * k / 2 / MM + 18  # главный + зазор + вид слева с размером
    left_x = a[0] + max(22.0, (a[2] - a[0] - block) / 2 + 8)
    place(main, left_x + (o[2] - o[0]) / 2, 200)
    drw.m.ForceRebuild3(False)
    o = outline(main)
    # дополнительные виды: сверху/снизу главного, нужная сторона стенки
    extras = []
    for dvec in extra_dirs:
        got = None
        for dy in (-1, 1):
            v = drw.unfolded(main, (o[0] + o[2]) / 2 * MM, ((o[1] + o[3]) / 2 + dy * 45) * MM)
            if v is None:
                continue
            drw.m.ForceRebuild3(False)
            if view_normal(v) == tuple(dvec):
                got = (v, dy); break
            drw.m.ClearSelection2(True)
            drw.m.Extension.SelectByID2(v.GetName2(), "DRAWINGVIEW", 0, 0, 0, False, 0, None, 0)
            drw.m.EditDelete()
        if got:
            got[0].SetDisplayMode4(False, 2, False, True, False)
            extras.append(got)
        else:
            log("   НЕ построен вид для отверстий", dvec)
    end = drw.unfolded(main, (o[2] + 35) * MM, (o[1] + o[3]) / 2 * MM)
    if end:
        end.SetDisplayMode4(False, 2, False, True, False)
    drw.m.ForceRebuild3(False)
    # раскладка по вертикали: над ТТ (≈110 мм) до верха поля
    hs = {"main": o[3] - o[1]}
    blocks = [("main", main)] + [("x%d" % i, v) for i, (v, dy) in enumerate(extras)]
    order = sorted(blocks, key=lambda b: 0 if b[0] == "main" else -next(dy for v, dy in extras if v is b[1]))
    # сверху вниз: вид снизу (dy=+1) — над главным, вид сверху (dy=-1) — под главным
    order = [b for b in blocks if b[0] != "main" and next(dy for v, dy in extras if v is b[1]) > 0] + \
            [("main", main)] + [b for b in blocks if b[0] != "main" and next(dy for v, dy in extras if v is b[1]) < 0]
    heights = [outline(v)[3] - outline(v)[1] for _, v in order]
    gap = 38.0
    total_h = sum(heights) + gap * (len(order) - 1)
    lo_y, hi_y, below = 124.0, 255.0, 16.0  # над ТТ, под графой; место под размеры нижнего вида
    top = min(hi_y, lo_y + below + total_h + max(0.0, (hi_y - lo_y - below - total_h) / 2))
    # сначала главный (проекционные виды едут за ним), затем дополнительные от него
    idx = [nm for nm, _ in order].index("main")
    main_cy = top - sum(heights[:idx]) - gap * idx - heights[idx] / 2
    om = outline(main)
    place(main, (om[0] + om[2]) / 2, main_cy)
    drw.m.ForceRebuild3(False)
    for i_, ((nm, v), h) in enumerate(zip(order, heights)):
        if nm == "main":
            continue
        cy = top - sum(heights[:i_]) - gap * i_ - h / 2
        ov = outline(v)
        place(v, (ov[0] + ov[2]) / 2, cy)
    drw.m.ForceRebuild3(False)
    if end:
        om = outline(main); oe = outline(end)
        place(end, om[2] + 30 + (oe[2] - oe[0]) / 2, (om[1] + om[3]) / 2)
        drw.m.ForceRebuild3(False)
    # размеры
    dm = Dims(drw)
    step = 8 * MM
    g = Geo(main)
    x0, y0, x1, y1 = g.box()
    left, right = extremes(g)
    has_steps = False
    if not t.miter:
        has_steps = dim_steps(dm, main, g, left, right, y1 + 10 * MM, t) > 0
    dm.horiz(main, left[0], right[0], (x0 + x1) / 2, y1 + (18 if has_steps else 10) * MM, what="длина")
    if t.miter:
        # у косого реза длины сторон разные: один размер не говорит, по какой стороне резать.
        # вторую длину берём по внутренним концам косых рёбер
        inner = {}
        for sl in [l for l in g.lines if not is_h(l) and not is_v(l)]:
            side = "L" if min(sl[1][0], sl[2][0]) < (x0 + x1) / 2 else "R"
            pt = max(sl[1], sl[2], key=lambda p: p[0]) if side == "L" else min(sl[1], sl[2], key=lambda p: p[0])
            if side not in inner or (pt[0] < inner[side][1][0]) == (side == "L"):
                vx = next((v for v in g.verts if near(v[1], pt)), None)
                if vx is not None:
                    inner[side] = vx
        if "L" in inner and "R" in inner and \
           abs((right[1][0] - left[1][0]) - (inner["R"][1][0] - inner["L"][1][0])) > 0.5 * MM:
            dm.horiz(main, inner["L"][0], inner["R"][0], (x0 + x1) / 2, y0 - 10 * MM,
                     what="длина по короткой стороне")
    dim_angles(dm, main, g)
    dim_holes(dm, main, g, left, right, y0 - 10 * MM, step, y1 + 4 * MM, t)
    if not t.miter:
        dim_tongues(dm, main, g)
    for v, dy in extras:
        ge = Geo(v)
        ex0, ey0, ex1, ey1 = ge.box()
        l2, r2 = extremes(ge)
        if not t.miter:
            dim_steps(dm, v, ge, l2, r2, (ey0 - 10 * MM) if dy < 0 else (ey1 + 10 * MM), t)
        dim_holes(dm, v, ge, l2, r2, (ey0 - (18 if not t.miter else 10) * MM), step, ey1 + 4 * MM, t)
        if not t.miter:
            dim_tongues(dm, v, ge)
    if end:
        dim_end(dm, end)
    drw.m.ForceRebuild3(False)
    add_tt(TT_TUBE)
    log("   размеров: %d — %s" % (len(dm.made), ", ".join(wh for wh, _ in dm.made)))
    out_dir = os.path.dirname(path) if out_dir == "model" else (out_dir or SNAP)
    name = os.path.basename(stem) + ".SLDDRW"
    out = os.path.join(out_dir, name if out_dir == os.path.dirname(path) else "proto_" + name)
    res = drw.m.Extension.SaveAs(out, 0, 1, None, 0, 0)
    log("   сохранён:", out, res)
    drw.m.ViewZoomtofit2()
    drw.m.Extension.SaveAs(os.path.join(SNAP, "snap_" + os.path.basename(stem) + ".jpg"), 0, 1 | 2, None, 0, 0)
    if not keep:
        sw.CloseDoc(drw.m.GetPathName())
    return drw, t


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    OUT_DIR = "model" if "--final" in sys.argv else None
    sw.CommandInProgress = True
    try:
        for s in args:
            cand = [os.path.splitext(p[len(ROOT) + 1:])[0] for p in glob.glob(ROOT + r"\**\*.SLDPRT", recursive=True)
                    if s in os.path.basename(p) and not os.path.basename(p).startswith("~$")]
            log("  → ", cand)
            run(cand[0], "--keep" in sys.argv, OUT_DIR)
    finally:
        sw.CommandInProgress = False
