# -*- coding: utf-8 -*-
"""Групповой чертёж листовой детали (Кронштейн 778.КРВ.03.005, ГОСТ 2.113): на каждом листе — главный вид,
вид сверху с радиусами гибов, полностью размеренная развёртка; таблица исполнений; ТТ лазерной резки с гибкой.
python sm_group_drw.py [--final]"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
ARGS = sys.argv[1:]
sys.argv = sys.argv[:1]
import tube_drw as T
SW, sw, MM, log = T.SW, T.sw, T.MM, T.log

PART = os.path.join(T.ROOT, r"Стандартные изделия и фурнитура\778.КРВ.03.005 Кронштейн.SLDPRT")
T.FORMATS["A3-P-1"] = dict(paper=12, w=0.297, h=0.420, area=(28, 68, 289, 391))
TT_SHEET = [
    "*Размеры для справок.",
    "Контур детали вырезать по программе лазерной резки.",
    "Точность контура реза - класс 1 по ГОСТ ИСО 9013-2017.",
    "Гибка: радиусы гибов R%s, K-фактор %s.",   # подставляется по модели, см. bend_params()
    "Общие допуски: ГОСТ 30893.1-т, ГОСТ 30893.2-К.",
    "Удалить грат и заусенцы. Острые кромки притупить.",
    "Исполнения -01 и -03 - зеркальные отражения исполнений 778.КРВ.03.005 и -02.",
]
CFG = [("00", "778.КРВ.03.005"), ("02", "778.КРВ.03.005-02")]
ROWS = [("778.КРВ.03.005", "19,5", ""), ("778.КРВ.03.005-01", "19,5", "Зеркальное"),
        ("778.КРВ.03.005-02", "25", ""), ("778.КРВ.03.005-03", "25", "Зеркальное")]
# раскладка листа, мм: развёртка справа от видов, а если широкая — под ними
MAIN_C, TOP_Y, FLAT_C, FLAT_WIDE_C = (85, 327), 225, (215, 327), (175, 150)


def geo(view):
    return T.Geo(view)


def place_y(view, cy):
    """Центр контура проекционного вида — на высоту cy (мм); по горизонтали он привязан к родителю."""
    o = view.GetOutline(); p = view.Position
    oy = (o[1] + o[3]) / 2 - p[1]
    view.Position = T.at(p[0], cy * MM - oy)


def near(a, b, eps=0.05):
    return abs(a - b) < eps


def h_lines(g):
    """Горизонтальные отрезки вида: (ребро, y, x0, x1) в мм."""
    out = []
    for e, p, q in g.lines:
        if near(p[1], q[1], 1e-6) and not near(p[0], q[0], 1e-6):
            out.append((e, p[1] / MM, min(p[0], q[0]) / MM, max(p[0], q[0]) / MM))
    return out


def v_lines(g):
    out = []
    for e, p, q in g.lines:
        if near(p[0], q[0], 1e-6) and not near(p[1], q[1], 1e-6):
            out.append((e, p[0] / MM, min(p[1], q[1]) / MM, max(p[1], q[1]) / MM))
    return out


def bend_params(path):
    """Радиусы гибов и K-фактор из модели: у «Ребро-кромка» свой радиус, он часто не равен радиусу
    в параметрах листового металла — писать в ТТ надо фактический (З-38)."""
    d = sw.GetOpenDocumentByName(path) or sw.OpenDoc6(path, 1, 32, "", 0, 0)
    if isinstance(d, tuple):
        d = d[0]
    m = SW.IModelDoc2(d._oleobj_)
    radii, kf = set(), None
    f = m.FirstFeature()
    while f is not None:
        f = SW.IFeature(f._oleobj_)
        t = f.GetTypeName2()
        if t in ("SheetMetal", "SMBaseFlange", "EdgeFlange", "OneBend", "SketchBend"):
            df = f.GetDefinition()
            try:
                if t in ("EdgeFlange", "OneBend", "SketchBend"):
                    radii.add(round(df.BendRadius * 1000, 2))
                else:
                    kf = kf or round(df.KFactor, 3)
            except Exception:
                pass
        f = f.GetNextFeature()
    fmt = lambda v: ("%g" % v).replace(".", ",")
    return sorted(radii), "/".join(fmt(r) for r in sorted(radii)) or "?", fmt(kf if kf is not None else 0.5)


def extreme_lines(g):
    """Крайние линии вида: (левая, правая, нижняя, верхняя, X0, Y0, X1, Y1) — самые длинные на границе."""
    V = [(e, y0, y1, x) for e, x, y0, y1 in v_lines(g)]
    H = [(e, x0, x1, y) for e, y, x0, x1 in h_lines(g)]
    if not V or not H:
        return None
    X0 = min(v[3] for v in V); X1 = max(v[3] for v in V)
    Y0 = min(h[3] for h in H); Y1 = max(h[3] for h in H)
    longest = lambda c: max(c, key=lambda z: z[2] - z[1])[0]
    return (longest([v for v in V if near(v[3], X0, 0.01)]), longest([v for v in V if near(v[3], X1, 0.01)]),
            longest([h for h in H if near(h[3], Y0, 0.01)]), longest([h for h in H if near(h[3], Y1, 0.01)]),
            X0, Y0, X1, Y1)


def dim_outline(dims, view, star=False, left=True, below=True, gap=12.0):
    ex = extreme_lines(geo(view))
    if ex is None:
        log("   нет габаритных линий"); return None
    L, R, B, Tp, X0, Y0, X1, Y1 = ex
    pre = "*" if star else None
    dims.vert(view, B, Tp, ((X0 - gap) if left else (X1 + gap)) * MM, (Y0 + Y1) / 2 * MM, pre, "габарит H")
    dims.horiz(view, L, R, (X0 + X1) / 2 * MM, ((Y0 - gap) if below else (Y1 + gap)) * MM, pre, "габарит B")
    return X0, Y0, X1, Y1


def dim_holes(dims, view, box, dia_dx, coord_x, coord_y, star=False):
    """Отверстия: «N отв. Ø…» и координаты от левой и нижней кромок."""
    X0, Y0, X1, Y1 = box
    g = geo(view)
    ex = extreme_lines(g)
    L, R, B, Tp = ex[0], ex[1], ex[2], ex[3]
    holes = {}
    for e, c, d, axis, full in g.circles:
        if not full:
            continue                                  # дуга контура, не отверстие
        holes.setdefault(round(d / MM, 1), []).append((e, c[0] / MM, c[1] / MM))
    pre = "*" if star else None
    for d, items in sorted(holes.items()):
        items.sort(key=lambda z: (-z[2], z[1]))
        e, cx, cy = items[0]
        # выноска диаметра — рядом с отверстием, иначе полка уезжает через весь лист
        dims.diam(view, e, (cx + dia_dx) * MM, (cy + 14) * MM,
                  ("%d отв. " % len(items)) if len(items) > 1 else None, "Ø%.1f" % d)
        cx_off, cy_off = 0, 0
        for e2, x, y in items:
            dims.vert(view, B, e2, (coord_x - cx_off) * MM, (Y0 + y) / 2 * MM, pre, "Y отв.")
            dims.horiz(view, L, e2, (X0 + x) / 2 * MM, (coord_y - cy_off) * MM, pre, "X отв.")
            cx_off += 9; cy_off += 9
    return len(holes)


def dim_radii(dims, view, box, star=False):
    """Радиусы контура: по одному размеру на каждый встретившийся радиус."""
    X0, Y0, X1, Y1 = box
    g = geo(view)
    seen = {}
    for e, c, d, axis, full in g.circles:
        if full:
            continue
        r = round(d / 2 / MM, 1)
        if r in seen:
            continue
        seen[r] = True
        cx, cy = c[0] / MM, c[1] / MM
        # выноска наружу от центра дуги
        sx = 14 if cx > (X0 + X1) / 2 else -14
        sy = 10 if cy > (Y0 + Y1) / 2 else -10
        dims.diam(view, e, (cx + sx) * MM, (cy + sy) * MM, "*" if star else None, "R%.1f" % r, radius=True)
    return list(seen)


def dim_bends(dims, drw, view, box, y):
    """Положение линий гиба на развёртке. Размер к линии гиба API не ставит (она — эскиз внутри вида),
    поэтому в эскизе вида рисуется вспомогательная (конструктивная, не печатается) линия поверх неё."""
    X0, Y0, X1, Y1 = box
    n = view.GetBendLineCount()
    if not n:
        return 0
    g = geo(view)
    V = sorted(v_lines(g), key=lambda z: z[1])
    made = 0
    for i, s in enumerate(view.GetBendLines() or []):
        try:
            ln = SW.ISketchLine(s._oleobj_)
            p1 = SW.ISketchPoint(ln.GetStartPoint2()._oleobj_)
            p2 = SW.ISketchPoint(ln.GetEndPoint2()._oleobj_)
        except Exception as e:
            log("   линия гиба %d не прочиталась: %s" % (i, e)); continue
        a = T.xf_point(view, (p1.X, p1.Y, p1.Z))
        b = T.xf_point(view, (p2.X, p2.Y, p2.Z))
        vertical = abs(a[0] - b[0]) < abs(a[1] - b[1])
        pos = view.Position
        drw.d.ActivateView(view.GetName2())
        drw.m.ClearSelection2(True)
        line = drw.m.SketchManager.CreateLine(a[0] - pos[0], a[1] - pos[1], 0, b[0] - pos[0], b[1] - pos[1], 0)
        if line is None:
            drw.m.SketchManager.InsertSketch(True); continue
        SW.ISketchSegment(line._oleobj_).ConstructionGeometry = True
        drw.m.ClearSelection2(True)
        if vertical:                                   # гиб вдоль Y — размер от ближней вертикальной кромки
            base = V[0] if abs(a[0] / MM - V[0][1]) < abs(a[0] / MM - V[-1][1]) else V[-1]
        else:
            H = sorted(h_lines(g), key=lambda z: z[1])
            base = H[0] if abs(a[1] / MM - H[0][1]) < abs(a[1] / MM - H[-1][1]) else H[-1]
        SW.ISketchSegment(line._oleobj_).Select4(True, None)
        view.SelectEntity(base[0], True)
        if vertical:
            r = drw.m.AddHorizontalDimension2((a[0] + base[1] * MM) / 2, (y - 9 * i) * MM, 0)
        else:
            r = drw.m.AddVerticalDimension2((X1 + 20) * MM, (a[1] + base[1] * MM) / 2, 0)
        drw.m.ClearSelection2(True)
        drw.m.SketchManager.InsertSketch(True)
        if r is not None:
            dd = SW.IDisplayDimension(r._oleobj_)
            dd.SetText(1, "*" + (dd.GetText(1) or ""))
            dims.made.append(("линия гиба", dd))
            made += 1
        else:
            log("   размер до линии гиба %d не создан" % i)
    drw.d.ActivateView("")
    return made


def table(drw, x, y):
    t = drw.m.Extension.InsertGeneralTableAnnotation(False, x * MM, y * MM, 1, "", 1 + len(ROWS), 3)
    if t is None:
        log("   таблица исполнений НЕ вставлена"); return None
    t = SW.ITableAnnotation(t._oleobj_)
    SW.IAnnotation(t.GetAnnotation()._oleobj_).SetPosition2(x * MM, y * MM, 0)
    t.TitleVisible = False
    t.BorderLineWeight = 1      # наружный контур — основная линия (иначе SW ставит утолщённую, З-35)
    t.GridLineWeight = 0
    for c, w in enumerate((45, 16, 24)):
        t.SetColumnWidth(c, w * MM, 0)
    for r in range(1 + len(ROWS)):
        t.SetRowHeight(r, 8 * MM, 0)
    for c, v in enumerate(("Обозначение", "А", "Примечание")):
        t.SetText(0, c, v)
    for i, row in enumerate(ROWS, start=1):
        for c, v in enumerate(row):
            t.SetText(i, c, v)
    for r in range(1 + len(ROWS)):
        for c in range(3):
            t.SetCellTextHorizontalJustification(r, c, 2 if c else 1)
    log("   таблица исполнений: %d строк" % (1 + len(ROWS)))
    return t


def note(drw, text, x, y):
    n = drw.m.InsertNote(text)
    if n is None:
        return None
    n = SW.INote(n._oleobj_)
    SW.IAnnotation(n.GetAnnotation()._oleobj_).SetPosition2(x * MM, y * MM, 0)
    return n


def sheet_content(drw, sheet, cfg, designation, bend_r):
    drw.d.ActivateSheet(sheet)
    main = SW.IView(drw.view(PART, "*Справа", MAIN_C[0] * MM, MAIN_C[1] * MM)._oleobj_)
    main.ReferencedConfiguration = cfg
    drw.fix_scale()
    main.SetDisplayMode4(False, 2, False, True, False)
    drw.m.ForceRebuild3(False)
    # проекционный вид создаём ДО переноса родителя: по горизонтали он привязан к родителю и сам не двигается
    o = main.GetOutline()          # точка вставки — под геометрией вида (Position у этой детали далеко от неё)
    top = drw.unfolded(main, (o[0] + o[2]) / 2, o[1] - 0.040)      # ниже главного — вид сверху (ГОСТ 2.305)
    if top is not None:
        top = SW.IView(top._oleobj_)
        top.SetDisplayMode4(False, 2, False, True, False)
        # у проекционного вида начало координат своё: по привязке он уезжает за рамку — снимаем привязку
        top.RemoveAlignment()
    drw.m.ForceRebuild3(False)
    T.place(main, *MAIN_C)
    drw.m.ForceRebuild3(False)
    if top is not None:
        T.place(top, MAIN_C[0], TOP_Y)
        drw.m.ForceRebuild3(False)
    flat = drw.d.CreateFlatPatternViewFromModelView3(PART, cfg, FLAT_C[0] * MM, FLAT_C[1] * MM, 0, False, False)
    if flat is not None:
        flat = SW.IView(flat._oleobj_)
        flat.UseSheetScale = 1
        flat.SetDisplayMode4(False, 2, False, True, False)
        drw.m.ForceRebuild3(False)
        o = flat.GetOutline()
        wide = (o[2] - o[0]) / MM > 110          # рядом с видами уже не помещается
        T.place(flat, *(FLAT_WIDE_C if wide else FLAT_C))
    drw.m.ForceRebuild3(False)
    dims = T.Dims(drw)

    box = dim_outline(dims, main, left=True, below=True, gap=12)
    if box:
        dim_holes(dims, main, box, dia_dx=16, coord_x=box[2] + 12, coord_y=box[1] - 24)

    if top is not None:
        g = geo(top)
        ex = extreme_lines(g)
        if ex:
            L, R, B, Tp, X0, Y0, X1, Y1 = ex
            dims.vert(top, B, Tp, (X0 - 14) * MM, (Y0 + Y1) / 2 * MM, "А=", "полка А")
            H = sorted(h_lines(g), key=lambda z: z[1])
            th = [h for h in H if 2.5 < abs(h[1] - Y1) < 3.5]      # толщина листа у наружной плоскости
            if th:
                dims.vert(top, th[0][0], Tp, (X1 + 12) * MM, (Y1 - 1.5) * MM, "*", "толщина")
            k = 0
            for e, c, d, axis, full in g.circles:
                if full or abs(d / 2 / MM - bend_r) > 0.2:
                    continue
                dims.diam(top, e, (c[0] / MM - 16 - 8 * k) * MM, (c[1] / MM - 14 - 8 * k) * MM,
                          None, "R гиба", radius=True)
                k += 1
            log("   вид сверху: полка, толщина, радиусов гиба %d" % k)

    if flat is not None:
        fbox = dim_outline(dims, flat, star=True, left=False, below=True, gap=12)
        if fbox:
            dim_holes(dims, flat, fbox, dia_dx=18, coord_x=fbox[0] - 12, coord_y=fbox[1] - 24, star=True)
            dim_radii(dims, flat, fbox, star=True)
            dim_bends(dims, drw, flat, fbox, y=fbox[1] - 42)
            note(drw, "Развертка", (fbox[0] + fbox[2]) / 2 - 12, fbox[3] + 10)
    log("   %s: размеров %d" % (designation, len(dims.made)))
    return dims


def run(final=False):
    sw.SetUserPreferenceToggle(10, False)        # swInputDimValOnCreate — не показывать окно «Изменить»
    sw.OpenDoc6(PART, 1, 0, "", 0, 0)
    radii, r_text, k_text = bend_params(PART)
    bend_r = radii[0] if radii else 2.5
    drw = T.Drw("A3-P-1", (1, 1))
    sheets = list(drw.d.GetSheetNames())
    SW.ISheet(drw.d.Sheet(sheets[0])._oleobj_).SetName("DRW1")   # имена DRW — по ним DProp считает листы
    sheet_content(drw, "DRW1", CFG[0][0], CFG[0][1], bend_r)
    table(drw, 164, 210)
    T.add_tt([s % (r_text, k_text) if "%s" in s else s for s in TT_SHEET])
    ok = drw.d.NewSheet3("DRW2", T.FORMATS["A3-P-1"]["paper"], 12, 1, 1, True,
                         os.path.join(T.FMT_DIR, "A3-P-2.slddrt"), 0.297, 0.420, "По умолчанию")
    log("   лист 2:", ok, list(drw.d.GetSheetNames()))
    if ok:
        sheet_content(drw, "DRW2", CFG[1][0], CFG[1][1], bend_r)
    drw.d.ActivateSheet("DRW1")
    out_dir = os.path.dirname(PART) if final else os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(out_dir, ("" if final else "proto_") + "778.КРВ.03.005 Кронштейн.SLDDRW")
    log("   сохранение:", out, drw.m.Extension.SaveAs(out, 0, 1, None, 0, 0))
    for name in drw.d.GetSheetNames():
        drw.d.ActivateSheet(name)
        drw.m.ViewZoomtofit2()
        drw.m.Extension.SaveAs(os.path.join(T.SNAP, "krn_%s.jpg" % name), 0, 3, None, 0, 0)
    return drw


if __name__ == "__main__":
    sw.CommandInProgress = True
    try:
        run("--final" in ARGS)
    finally:
        sw.CommandInProgress = False
