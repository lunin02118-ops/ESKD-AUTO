# -*- coding: utf-8 -*-
"""
Сборочный чертёж подсборки 778 (сварная рама из труб) по ЕСКД через SolidWorks API, как у Кровати владельца:
лист DRW1 A3-A-1 — главный вид в плоскости рамы и вид слева, габаритные и присоединительные размеры, позиции (AutoBalloon5),
ТТ по сварке и покрытию над основной надписью; лист SP1 SP-1 — спецификация: BOM по шаблону SpecEditor_sp,
разделы «Документация» и «Детали» (заголовки подчёркнуты, по центру, как пишет SpecEditor), позиции по порядку строк.
Запуск: asm_drw.py <подстрока имени сборки> [--final] [--keep]
"""
import os, sys, math, glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
_argv = sys.argv; sys.argv = sys.argv[:1]
import tube_drw as T
sys.argv = _argv
SW, sw, MM, log = T.SW, T.sw, T.MM, T.log
SPEC_DIR = r"D:\Work\_Инструменты_Конструктора\03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\SpecEditor"
SCALES = [(1, 2), (1, 2.5), (1, 4), (1, 5), (1, 10), (1, 15), (1, 20)]
TT_WELD = ["*Размеры для справок.",
           "Сварка - полуавтоматическая в среде CO2 по ГОСТ 14771-76,",
           "проволока Св-08Г2С ГОСТ 2246-70.",
           "Швы - угловые, по контуру сопряжения, катет K = 2 мм.",
           "Общие допуски сварной конструкции по ГОСТ Р ИСО 13920-BF.",
           "Сварные швы и околошовную зону зачистить от шлака и брызг металла.",
           "Покрытие: порошковая полимерная краска, цвет - по заказу."]
SECTION_ORDER = ["Документация", "Комплексы", "Сборочные единицы", "Детали", "Стандартные изделия", "Прочие изделия", "Материалы", "Комплекты"]


def prop(m, cfg, name):
    for c in (cfg, ""):
        r = m.Extension.CustomPropertyManager(c).Get6(name, False)
        if isinstance(r, tuple) and (r[2] or r[1]):
            return r[2] or r[1]
    return ""


def pick_scale(w_, h_, area_w, area_h):
    for s in SCALES:
        k = s[0] / s[1]
        if w_ * k <= area_w + 1e-6 and h_ * k <= area_h + 1e-6:
            return s
    return SCALES[-1]


BEFORE, AFTER = 2, 3  # swTableItemInsertPosition_e


class AGeo:
    """Видимая геометрия вида сборки в координатах листа (м): рёбра компонентов с их преобразованием."""
    def __init__(self, view, asm):
        self.v = view
        self.lines, self.circles, self.verts = [], [], []
        vt = view.ModelToViewTransform
        # рёбра компонента вида даны в системе детали: преобразование — у компонента сборки с тем же именем
        byname = {SW.IComponent2(c._oleobj_).Name2: SW.IComponent2(c._oleobj_)
                  for c in SW.IAssemblyDoc(asm._oleobj_).GetComponents(False) or []}
        for comp in view.GetVisibleComponents() or []:
            comp = SW.IComponent2(comp._oleobj_)
            ac = byname.get(comp.Name2.split("/", 1)[-1])
            ct = ac.Transform2 if ac is not None else None
            ct = SW.IMathTransform(ct._oleobj_) if ct is not None else None
            comp = ac or comp
            for e in view.GetVisibleEntities2(comp, 1) or []:
                edge = SW.IEdge(e._oleobj_)
                c = SW.ICurve(edge.GetCurve()._oleobj_)
                if not c.IsLine():
                    continue
                sv, ev = edge.GetStartVertex(), edge.GetEndVertex()
                if sv is None or ev is None:
                    continue
                q = []
                for vx in (sv, ev):
                    mp = SW.IMathPoint(T.mu.CreatePoint(T.at(*SW.IVertex(vx._oleobj_).GetPoint()))._oleobj_)
                    if ct is not None:
                        mp = SW.IMathPoint(mp.MultiplyTransform(ct)._oleobj_)
                    r = SW.IMathPoint(mp.MultiplyTransform(vt)._oleobj_).ArrayData
                    q.append((r[0], r[1]))
                self.lines.append((e, q[0], q[1], comp))
                for vx, p in ((sv, q[0]), (ev, q[1])):
                    if not any(T.near(p, o) for _, o, _c in self.verts):
                        self.verts.append((vx, p, comp))

    def box(self):
        xs = [p[0] for _, p, _c in self.verts]; ys = [p[1] for _, p, _c in self.verts]
        return min(xs), min(ys), max(xs), max(ys)


def sel_ent(view, ents):
    ok = True
    for i, e in enumerate(ents):
        ok = bool(view.SelectEntity(e, i > 0)) and ok
    return ok


def adim(drw, view, e1, e2, x, y, kind, prefix=None, what="", made=None):
    drw.m.ClearSelection2(True)
    sel_ent(view, [e1, e2])
    r = drw.m.AddHorizontalDimension2(x, y, 0) if kind == "h" else drw.m.AddVerticalDimension2(x, y, 0)
    drw.m.ClearSelection2(True)
    if r is None:
        log("   НЕ создан размер", what)
        return None
    dd = SW.IDisplayDimension(r._oleobj_)
    if prefix:
        dd.SetText(1, prefix + (dd.GetText(1) or ""))
    if made is not None:
        made.append(what)
    return dd


def overall_dims(drw, view, made, left_side=True, bottom=True, star=False):
    g = AGeo(view)
    x0, y0, x1, y1 = g.box()
    L = min(g.verts, key=lambda v: (round(v[1][0], 5), v[1][1])); R = max(g.verts, key=lambda v: (round(v[1][0], 5), -v[1][1]))
    B = min(g.verts, key=lambda v: (round(v[1][1], 5), v[1][0])); Tp = max(g.verts, key=lambda v: (round(v[1][1], 5), -v[1][0]))
    pre = "*" if star else None
    adim(drw, view, L[0], R[0], (x0 + x1) / 2, (y0 - 14 * MM) if bottom else (y1 + 14 * MM), "h", pre, "габарит по X", made)
    if y1 - y0 > 3 * MM:
        adim(drw, view, B[0], Tp[0], (x0 - 14 * MM) if left_side else (x1 + 14 * MM), (y0 + y1) / 2, "v", pre, "габарит по Y", made)
    return g


def comp_boxes(view, asm):
    """Габариты компонентов верхнего уровня в координатах листа (мм): (x0, y0, x1, y1, имя)."""
    out = []
    for c in SW.IAssemblyDoc(asm._oleobj_).GetComponents(True) or []:
        c = SW.IComponent2(c._oleobj_)
        if c.IsSuppressed():
            continue
        b = c.GetBox(False, False)
        if not b:
            continue
        pts = [T.xf_point(view, (b[i], b[j], b[k])) for i in (0, 3) for j in (1, 4) for k in (2, 5)]
        xs = [p[0] / MM for p in pts]; ys = [p[1] / MM for p in pts]
        out.append((min(xs), min(ys), max(xs), max(ys), c.Name2))
    return out


def pick(drw, pts, kinds=("SILHOUETTE", "EDGE")):
    """Выбрать рёбра по точкам листа (мм); True — все выбраны."""
    drw.m.ClearSelection2(True)
    ok = True
    for i, pt in enumerate(pts):
        cands = pt if isinstance(pt, list) else [pt]
        got = False
        for x, y in cands:
            for kind in kinds:
                if drw.m.Extension.SelectByID2("", kind, x * MM, y * MM, 0, i > 0, 0, None, 0):
                    got = True
                    break
            if got:
                break
        ok = ok and got
    return ok


def pdim(drw, pts, x, y, kind, what, made, prefix=None):
    if not pick(drw, pts):
        drw.m.ClearSelection2(True)
        log("   не выбраны рёбра для", what, [(round(a, 1), round(b, 1)) for a, b in pts])
        return None
    r = drw.m.AddHorizontalDimension2(x * MM, y * MM, 0) if kind == "h" else drw.m.AddVerticalDimension2(x * MM, y * MM, 0)
    drw.m.ClearSelection2(True)
    if r is None:
        log("   НЕ создан размер", what)
        return None
    dd = SW.IDisplayDimension(r._oleobj_)
    if prefix:
        dd.SetText(1, prefix + (dd.GetText(1) or ""))
    made.append(what)
    return dd


def edim(drw, view, e1, e2, x, y, kind, what, made, prefix=None):
    drw.m.ClearSelection2(True)
    ok = bool(view.SelectEntity(e1, False)) and bool(view.SelectEntity(e2, True))
    r = (drw.m.AddHorizontalDimension2(x * MM, y * MM, 0) if kind == "h" else drw.m.AddVerticalDimension2(x * MM, y * MM, 0)) if ok else None
    drw.m.ClearSelection2(True)
    if r is None:
        log("   НЕ создан размер", what, "(выбор %s)" % ok)
        return None
    dd = SW.IDisplayDimension(r._oleobj_)
    if prefix:
        dd.SetText(1, prefix + (dd.GetText(1) or ""))
    made.append(what)
    return dd


def mm(p): return (p[0] / MM, p[1] / MM)


def frame_dims(drw, view, asm, made, star=False, right=False, col0=25):
    """Габариты и положение внутренних элементов по точной геометрии видимых рёбер (не выбором точкой)."""
    g = AGeo(view, asm)
    V = [(l, min(mm(l[1])[1], mm(l[2])[1]), max(mm(l[1])[1], mm(l[2])[1]), mm(l[1])[0]) for l in g.lines
         if abs(l[1][0] - l[2][0]) < 1e-6 and abs(l[1][1] - l[2][1]) > 1e-6]
    H = [(l, min(mm(l[1])[0], mm(l[2])[0]), max(mm(l[1])[0], mm(l[2])[0]), mm(l[1])[1]) for l in g.lines
         if abs(l[1][1] - l[2][1]) < 1e-6 and abs(l[1][0] - l[2][0]) > 1e-6]
    X0 = min(v[3] for v in V); X1 = max(v[3] for v in V); Y0 = min(h[3] for h in H); Y1 = max(h[3] for h in H)
    longest = lambda cand: max(cand, key=lambda c: c[2] - c[1])[0]
    L = longest([v for v in V if v[3] < X0 + 0.01]); R = longest([v for v in V if v[3] > X1 - 0.01])
    B = longest([h for h in H if h[3] < Y0 + 0.01]); Tp = longest([h for h in H if h[3] > Y1 - 0.01])
    pre = "*" if star else None
    bx = comp_boxes(view, asm)
    touch = lambda b: b[0] <= X0 + 0.5 or b[2] >= X1 - 0.5 or b[1] <= Y0 + 0.5 or b[3] >= Y1 - 0.5
    adj = lambda b: any(o is not b and (abs(b[0] - o[2]) < 0.5 or abs(b[2] - o[0]) < 0.5) and o[1] < b[3] and o[3] > b[1] for o in bx)
    inner = [b for b in bx if not touch(b)]
    row = Y0 - 10
    seen = set()
    for b in sorted([b for b in inner if (b[3] - b[1]) > (b[2] - b[0]) and not adj(b)], key=lambda b: b[0]):
        own = [v for v in V if v[0][3].Name2 == b[4]]
        if not own or round(b[0], 1) in seen:
            continue
        seen.add(round(b[0], 1))
        e = longest([v for v in own if v[3] < min(o[3] for o in own) + 0.01])
        edim(drw, view, L[0], e[0], (X0 + b[0]) / 2, row, "h", "положение " + b[4], made)
        row -= 7
    edim(drw, view, L[0], R[0], (X0 + X1) / 2, row, "h", "габарит X", made, pre)
    col = X0 + col0
    seen = set()
    for b in sorted([b for b in inner if (b[2] - b[0]) >= (b[3] - b[1]) or adj(b)], key=lambda b: b[1]):
        own = [h for h in H if h[0][3].Name2 == b[4]]
        if not own or round(b[1], 1) in seen:
            continue
        seen.add(round(b[1], 1))
        e = longest([h for h in own if h[3] < min(o[3] for o in own) + 0.01])
        edim(drw, view, B[0], e[0], col, (Y0 + b[1]) / 2, "v", "положение " + b[4], made)
        col += 12
    edim(drw, view, B[0], Tp[0], (X1 + 11) if right else (X0 - 11), (Y0 + Y1) / 2, "v", "габарит Y", made, pre)
    return X0, Y0, X1, Y1, row


def side_dims(drw, view, asm, made):
    """Вид под главным: толщина рамы — справочный размер слева."""
    g = AGeo(view, asm)
    H = [(l, min(mm(l[1])[0], mm(l[2])[0]), max(mm(l[1])[0], mm(l[2])[0]), mm(l[1])[1]) for l in g.lines
         if abs(l[1][1] - l[2][1]) < 1e-6 and abs(l[1][0] - l[2][0]) > 1e-6]
    Y0 = min(h[3] for h in H); Y1 = max(h[3] for h in H); X0 = min(h[1] for h in H)
    longest = lambda cand: max(cand, key=lambda c: c[2] - c[1])[0]
    B = longest([h for h in H if h[3] < Y0 + 0.01]); Tp = longest([h for h in H if h[3] > Y1 - 0.01])
    edim(drw, view, B[0], Tp[0], X0 - 11, (Y0 + Y1) / 2, "v", "толщина", made, "*")


def balloons(drw, view):
    drw.m.ClearSelection2(True)
    drw.m.Extension.SelectByID2(view.GetName2(), "DRAWINGVIEW", 0, 0, 0, False, 0, None, 0)
    opt = drw.d.CreateAutoBalloonOptions()
    opt.Layout = 1          # по периметру вида
    opt.ReverseDirection = False
    opt.IgnoreMultiple = True
    opt.InsertMagneticLine = False
    opt.LeaderAttachmentToFaces = False
    opt.Style = 10          # swBS_Underline — полка с номером, ГОСТ 2.109
    opt.Size = 2
    opt.UpperTextContent = 1  # номер позиции
    res = drw.d.AutoBalloon5(opt)
    drw.m.ClearSelection2(True)
    n = len(res) if res else 0
    log("   позиций поставлено:", n)
    return n


def arrange_balloons(drw, view, x_col, y_lo, y_hi, gap=9.0, tt_top=118.0, tt_x=233.0):
    """Позиции — колонкой справа от вида (ГОСТ 2.109 п.4.12): по высоте точки выноски, не ближе gap мм друг к другу;
    в колонке ТТ (x > tt_x) — не ниже её верха."""
    items = []
    for n in view.GetNotes() or []:
        n = SW.INote(n._oleobj_)
        if not n.IsBomBalloon():
            continue
        ann = SW.IAnnotation(n.GetAnnotation()._oleobj_)
        pts = ann.GetLeaderPointsAtIndex(0)
        ay = pts[1] / MM if pts else ann.GetPosition()[1] / MM
        items.append((ay, ann))
    items.sort(key=lambda t: -t[0])
    lo = max(y_lo, tt_top) if x_col > tt_x else y_lo
    ys, prev = [], None
    for ay, ann in items:
        y = min(ay + 2, y_hi) if prev is None else min(ay + 2, prev - gap)
        ys.append(y); prev = y
    # не хватило места снизу — сдвинуть всю колонку вверх
    if ys and ys[-1] < lo:
        shift = lo - ys[-1]
        ys = [y + shift for y in ys]
    for (ay, ann), y in zip(items, ys):
        ann.SetPosition2(x_col * MM, y * MM, 0)
    log("   позиции колонкой x=%.0f: %s" % (x_col, [round(y) for y in ys]))


def seg_dist(p, a, b):
    ax, ay = a; bx, by = b; px, py = p
    dx, dy = bx - ax, by - ay
    L2 = dx * dx + dy * dy
    t = 0 if L2 == 0 else max(0, min(1, ((px - ax) * dx + (py - ay) * dy) / L2))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def place_balloons(drw, view, asm, X0, Y0, X1, Y1, x_col, tt_top=126.0, tt_x=233.0, gap=9.0):
    """Позиции по одной на запись спецификации (ГОСТ 2.109 п.4.12): выноска к ребру экземпляра, ближайшего к колонке
    справа; элементы левой половины — строкой над видом. Полка с номером (swBS_Underline)."""
    g = AGeo(view, asm)
    lines = [(mm(l[1]), mm(l[2]), l[3]) for l in g.lines]
    items, cands = {}, {}
    for a_, b_, comp in lines:
        key = (comp.GetPathName().lower(), comp.ReferencedConfiguration)
        for t in (0.5, 0.3, 0.7, 0.15, 0.85, 0.92):
            p = (a_[0] + (b_[0] - a_[0]) * t, a_[1] + (b_[1] - a_[1]) * t)
            if min([seg_dist(p, o1, o2) for o1, o2, oc in lines if oc.Name2 != comp.Name2] or [9]) < 0.6:
                continue  # на ребре соседа — не выбрать однозначно
            cands.setdefault(key, []).append(p)
            if key not in items or p[0] > items[key][0][0] + 0.01:
                items[key] = (p, comp)
    right, top = [], []
    for key, (p, comp) in items.items():
        (right if p[0] > (X0 + X1) / 2 else top).append((p, comp))
    opt = drw.m.Extension.CreateBalloonOptions()
    opt.Style = 10; opt.Size = 2; opt.UpperTextContent = 1
    made = []

    def put(p, x, y):
        drw.m.ClearSelection2(True)
        if not drw.m.Extension.SelectByID2("", "EDGE", p[0] * MM, p[1] * MM, 0, False, 0, None, 0):
            log("   не выбрано ребро для позиции в", p); return None
        n = drw.m.Extension.InsertBOMBalloon2(opt)
        drw.m.ClearSelection2(True)
        if n is None:
            log("   НЕ поставлена позиция в", p); return None
        ann = SW.IAnnotation(SW.INote(n._oleobj_).GetAnnotation()._oleobj_)
        ann.SetPosition2(x * MM, y * MM, 0)
        made.append(SW.INote(n._oleobj_).GetText())
        return n

    lo = max(Y0, tt_top) if x_col > tt_x else Y0 - 5
    right.sort(key=lambda t: -t[0][1])
    ys, prev = [], None
    for p, comp in right:
        y = p[1] + 3 if prev is None else min(p[1] + 3, prev - gap)
        ys.append(min(y, Y1 + 8)); prev = ys[-1]
    if ys and ys[-1] < lo:
        ys = [y + lo - ys[-1] for y in ys]
    for (p, comp), y in zip(right, ys):
        if x_col > tt_x and p[1] < tt_top - 3:
            # элемент ниже верха ТТ: выноска не должна пересекать текст ТТ — точка левее, чтобы линия прошла над ним
            key = (comp.GetPathName().lower(), comp.ReferencedConfiguration)
            ok_ = [q for q in cands.get(key, []) if x_col - (x_col - q[0]) * (y - tt_top + 8) / max(y - q[1], 1) < tt_x - 1]
            if ok_:
                p = max(ok_, key=lambda q: q[0])
        put(p, x_col, y)
    top.sort(key=lambda t: t[0][0])
    xs, prev = [], None
    for p, comp in top:
        x = p[0] + 4 if prev is None else max(p[0] + 4, prev + gap + 2)
        xs.append(x); prev = x
    for (p, comp), x in zip(top, xs):
        put(p, x, Y1 + 9)
    log("   позиций: %d — %s" % (len(made), made))
    return made


def spec_sheet(drw, asm_path, cfg, designation):
    """Лист SP1 со спецификацией (как FrmSpecEditor:1663/1718): BOM по шаблону SpecEditor_sp, разделы, позиции."""
    d, m = drw.d, drw.m
    d.NewSheet3("SP1", 12, 12, 1.0, 50.0, True, os.path.join(SPEC_DIR, "SP-1.slddrt"), 0.21, 0.297, "По умолчанию")
    d.ActivateSheet("SP1")
    v = d.CreateDrawViewFromModelView3(asm_path, "*Изометрия", 0.40, 0.15, 0)  # вид-носитель, за листом
    v = SW.IView(v._oleobj_)
    v.ReferencedConfiguration = cfg
    ann = v.InsertBomTable4(True, 0.02, 0.292, 1, 2, cfg, os.path.join(SPEC_DIR, "SpecEditor_sp.sldbomtbt"), False, 0, False)
    ta = SW.ITableAnnotation(ann._oleobj_)
    feat = SW.IBomFeature(SW.IBomTableAnnotation(ann._oleobj_).BomFeature._oleobj_)
    feat.DisplayAsOneItem = True
    m.ForceRebuild3(False)
    items = [(ta.Text(r, 3), ta.Text(r, 4)) for r in range(1, ta.RowCount)]
    want = sorted(items)
    for t_, key in enumerate(want, start=1):
        cur = next(r for r in range(1, ta.RowCount) if (ta.Text(r, 3), ta.Text(r, 4)) == key)
        if cur != t_:
            ta.MoveRow(cur, BEFORE, t_)
    feat.KeepCurrentItemNumbers = True
    for n, r in enumerate(range(1, ta.RowCount), start=1):
        ta.SetText(r, 2, str(n))
    m.ForceRebuild3(False)
    fmt = SW.ITextFormat(ta.GetCellTextFormat(1, 4)._oleobj_)
    fmt.Underline = True

    def insert(at, cells, head=False):
        ta.InsertRow(BEFORE, at)
        ta.SetText(at, 2, " ")
        for c, val in cells.items():
            ta.SetText(at, c, val)
        if head:
            ta.SetCellTextFormat(at, 4, False, fmt)
            ta.SetCellTextHorizontalJustification(at, 4, 2)

    # снизу вверх, всё перед первой записью «Деталей»
    insert(1, {}); insert(1, {4: "Детали"}, True); insert(1, {})
    insert(1, {0: "А3", 3: designation + " СБ", 4: "Сборочный чертеж"})
    insert(1, {}); insert(1, {4: "Документация"}, True); insert(1, {})
    plain = SW.ITextFormat(ta.GetCellTextFormat(1, 4)._oleobj_)
    plain.Underline = False
    heads = {"Документация", "Детали", "Сборочные единицы", "Стандартные изделия", "Прочие изделия", "Материалы"}
    for r in range(1, ta.RowCount):
        for c in (3, 4):
            if ta.Text(r, 4) in heads and c == 4:
                continue
            ta.SetCellTextFormat(r, c, False, plain)
            ta.SetCellTextHorizontalJustification(r, c, 1)
    m.ForceRebuild3(False)
    rows = [[ta.Text(r, c) for c in range(ta.ColumnCount)] for r in range(ta.RowCount)]
    for r in rows:
        log("     ", r)
    return v, ta


def run(stem, keep=False, final=False):
    path = os.path.join(T.ROOT, stem + ".SLDASM")
    md = sw.GetOpenDocumentByName(path) or sw.OpenDoc6(path, 2, 1, "", 0, 0)[0]
    model = SW.IModelDoc2(md._oleobj_)
    cfg = model.ConfigurationManager.ActiveConfiguration.Name
    designation = prop(model, cfg, "Обозначение")
    b = SW.IAssemblyDoc(model._oleobj_).GetBox(0)
    size = [abs(b[i + 3] - b[i]) * 1000 for i in range(3)]
    thin = size.index(min(size))
    vn = [0, 0, 0]; vn[thin] = 1
    lat = [i for i in range(3) if i != thin]
    log("сборка %s (%s) конф %s габарит %s главный %s" % (os.path.basename(path), designation, cfg, [round(x) for x in size], T.VIEWS[tuple(vn)]))
    fmt = "A3-A-1"
    # поле видов: ширина 384 мм без места под вид слева и позиции; высота 200 мм без места под ТТ-колонку справа
    horiz = size[0] if thin != 0 else size[2]  # *Спереди/*Сзади — X по горизонтали, *Сверху — X, *Слева — Z
    vert = size[1] if thin != 1 else size[2]
    scale = pick_scale(horiz, vert + size[thin], 202, 190)  # поле левее колонки ТТ: x 28…224 мм
    drw = T.Drw(fmt, scale)
    k = scale[0] / scale[1]
    a = drw.f["area"]
    main = drw.view(path, T.VIEWS[tuple(vn)], 0.15, 0.18)
    main.ReferencedConfiguration = cfg
    drw.fix_scale()
    main.SetDisplayMode4(False, 2, False, True, False)
    drw.m.ForceRebuild3(False)
    o = T.outline(main)
    w_, h_ = o[2] - o[0], o[3] - o[1]
    # поле видов — левее колонки ТТ и основной надписи (x ≤ 228 мм), ниже графы (y ≤ 262 мм)
    right = 250 - h_ > 125  # низ главного вида выше колонки ТТ — габарит по высоте и позиции справа
    cx = (30 + w_ / 2) if right else max(34 + w_ / 2, (28 + 224) / 2)
    T.place(main, cx, 250 - h_ / 2)
    drw.m.ForceRebuild3(False)
    o = T.outline(main)
    side = drw.unfolded(main, (o[0] + o[2]) / 2 * MM, (o[1] - 50) * MM)
    if side:
        side.SetDisplayMode4(False, 2, False, True, False)
    drw.m.ForceRebuild3(False)
    made = []
    X0, Y0, X1, Y1, row = frame_dims(drw, main, model, made, right=right)
    if side:
        os_ = T.outline(side)
        T.place(side, (os_[0] + os_[2]) / 2, row - 16 - (os_[3] - os_[1]) / 2)
        drw.m.ForceRebuild3(False)
        side_dims(drw, side, model, made)
        log("   вид сверху:", [round(x) for x in T.outline(side)], "нормаль", T.view_normal(side))
    log("   размеров:", made)
    # спецификация — до позиций: позиции берут номера из BOM
    spec_sheet(drw, path, cfg, designation)
    drw.d.ActivateSheet("DRW1") if "DRW1" in drw.d.GetSheetNames() else drw.d.ActivateSheet(drw.d.GetSheetNames()[0])
    drw.m.ForceRebuild3(False)
    place_balloons(drw, main, model, X0, Y0, X1, Y1, X1 + (24 if right else 12))
    drw.m.ForceRebuild3(False)
    T.add_tt(TT_WELD)
    out_dir = os.path.dirname(path) if final else os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(out_dir, ("" if final else "proto_") + os.path.basename(stem) + ".SLDDRW")
    res = drw.m.Extension.SaveAs(out, 0, 1, None, 0, 0)
    log("   сохранён:", out, res)
    for name in drw.d.GetSheetNames():
        drw.d.ActivateSheet(name)
        drw.m.ViewZoomtofit2()
        drw.m.Extension.SaveAs(os.path.join(os.path.dirname(os.path.abspath(__file__)), "snapA_%s_%s.jpg" % (os.path.basename(stem), name)), 0, 3, None, 0, 0)
    drw.d.ActivateSheet(drw.d.GetSheetNames()[0])
    if not keep:
        sw.CloseDoc(drw.m.GetPathName())
    return drw


if __name__ == "__main__":
    args = [x for x in sys.argv[1:] if not x.startswith("--")]
    sw.CommandInProgress = True
    try:
        for s in args:
            cand = [os.path.splitext(p[len(T.ROOT) + 1:])[0] for p in glob.glob(T.ROOT + r"\**\*.SLDASM", recursive=True)
                    if s in os.path.basename(p) and not os.path.basename(p).startswith("~$")]
            run(cand[0], "--keep" in sys.argv, "--final" in sys.argv)
    finally:
        sw.CommandInProgress = False
