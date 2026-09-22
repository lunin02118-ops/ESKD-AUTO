# -*- coding: utf-8 -*-
"""Групповой чертёж листовой детали (Кронштейн 778.КРВ.03.005, ГОСТ 2.113): на каждом листе — главный вид,
вид сбоку и развёртка исполнения; таблица исполнений; ТТ лазерной резки с гибкой.
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


def geo(view):
    return T.Geo(view)


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
    return "/".join(fmt(r) for r in sorted(radii)) or "?", fmt(kf if kf is not None else 0.5)


def near(a, b, eps=0.05):
    return abs(a - b) < eps


def extreme_lines(g):
    """Крайние линии вида: (левая, правая, нижняя, верхняя) — самые длинные на границе."""
    V = [(e, min(p[1], q[1]) / MM, max(p[1], q[1]) / MM, p[0] / MM) for e, p, q in g.lines
         if near(p[0], q[0], 1e-6) and not near(p[1], q[1], 1e-6)]
    H = [(e, min(p[0], q[0]) / MM, max(p[0], q[0]) / MM, p[1] / MM) for e, p, q in g.lines
         if near(p[1], q[1], 1e-6) and not near(p[0], q[0], 1e-6)]
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
    dims.vert(view, L, R and L, 0, 0) if False else None
    dims.vert(view, B, Tp, ((X0 - gap) if left else (X1 + gap)) * MM, (Y0 + Y1) / 2 * MM, pre, "габарит H")
    dims.horiz(view, L, R, (X0 + X1) / 2 * MM, ((Y0 - gap) if below else (Y1 + gap)) * MM, pre, "габарит B")
    return X0, Y0, X1, Y1


def dim_holes(dims, view, X0, Y0, X1, Y1):
    """Отверстия: диаметр с количеством, координаты от ближних кромок."""
    g = geo(view)
    holes = {}
    for e, c, d, axis, full in g.circles:   # (ребро, центр на листе, диаметр в м, ось, замкнутая)
        if not full:
            continue                        # дуга скругления контура, не отверстие
        holes.setdefault(round(d / MM, 1), []).append((e, c[0] / MM, c[1] / MM, full))
    ex = extreme_lines(g)
    L, R, B, Tp = ex[0], ex[1], ex[2], ex[3]
    for d, items in sorted(holes.items()):
        items.sort(key=lambda z: (-z[2], z[1]))
        e, cx, cy, _full = items[0]
        pre = "%d отв. " % len(items) if len(items) > 1 else None
        dims.diam(view, e, (X1 + 16) * MM, cy * MM, pre, "Ø%.1f" % d)
        seen = set()
        col, row = X0 - 22, Y1 + 8          # ряды размеров со сдвигом, чтобы не накладывались
        for e2, x, y, _f in items:
            if (round(x, 1), round(y, 1)) in seen:
                continue
            seen.add((round(x, 1), round(y, 1)))
            dims.vert(view, B, e2, col * MM, (Y0 + y) / 2 * MM, None, "Y отв.")
            dims.horiz(view, L, e2, (X0 + x) / 2 * MM, row * MM, None, "X отв.")
            col -= 8; row += 8
    return len(holes)


def table(drw, x, y):
    t = drw.m.Extension.InsertGeneralTableAnnotation(False, x * MM, y * MM, 1, "", 1 + len(ROWS), 3)
    if t is None:
        log("   таблица исполнений НЕ вставлена"); return None
    t = SW.ITableAnnotation(t._oleobj_)
    SW.IAnnotation(t.GetAnnotation()._oleobj_).SetPosition2(x * MM, y * MM, 0)
    t.TitleVisible = False
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


def sheet_content(drw, sheet, cfg, designation):
    drw.d.ActivateSheet(sheet)
    main = drw.view(PART, "*Справа", 0.075 * 1, 0.33)
    main = SW.IView(main._oleobj_)
    main.ReferencedConfiguration = cfg
    drw.fix_scale()
    main.SetDisplayMode4(False, 2, False, True, False)
    drw.m.ForceRebuild3(False)
    o = T.outline(main)
    T.place(main, 90, 300)
    drw.m.ForceRebuild3(False)
    side = drw.unfolded(main, 0.150, 0.330)
    if side:
        side = SW.IView(side._oleobj_)
        side.SetDisplayMode4(False, 2, False, True, False)
    flat = drw.d.CreateFlatPatternViewFromModelView3(PART, cfg, 0.150, 0.150, 0, False, False)
    if flat is not None:
        flat = SW.IView(flat._oleobj_)
        flat.UseSheetScale = 1
        flat.SetDisplayMode4(False, 2, False, True, False)
    drw.m.ForceRebuild3(False)
    dims = T.Dims(drw)
    X0, Y0, X1, Y1 = dim_outline(dims, main)
    dim_holes(dims, main, X0, Y0, X1, Y1)
    if side:
        os_ = T.outline(side)
        T.place(side, X1 + 45, (Y0 + Y1) / 2)
        drw.m.ForceRebuild3(False)
        ex = extreme_lines(geo(side))
        if ex:
            dims.horiz(side, ex[0], ex[1], (ex[4] + ex[6]) / 2 * MM, (ex[5] - 12) * MM, "А=", "полка А")
    if flat is not None:
        of = T.outline(flat)
        T.place(flat, 200, 175)
        drw.m.ForceRebuild3(False)
        dim_outline(dims, flat, star=True)
        n = drw.m.InsertNote("Развертка")
        if n is not None:
            a = SW.IAnnotation(SW.INote(n._oleobj_).GetAnnotation()._oleobj_)
            of = T.outline(flat)
            a.SetPosition2((of[0] + of[2]) / 2 * MM, (of[3] + 10) * MM, 0)
    log("   %s: размеров %d" % (designation, len(dims.made)))
    return dims


def run(final=False):
    drw = T.Drw("A3-P-1", (1, 1))
    sheets = list(drw.d.GetSheetNames())
    SW.ISheet(drw.d.Sheet(sheets[0])._oleobj_).SetName("DRW1")   # имена DRW — по ним DProp считает листы
    sheet_content(drw, "DRW1", CFG[0][0], CFG[0][1])
    table(drw, 25, 150)
    r, k = bend_params(PART)
    T.add_tt([s % (r, k) if "%s" in s else s for s in TT_SHEET])
    # лист 2 — исполнение -02 (форматка последующего листа, как её делает Master)
    ok = drw.d.NewSheet3("DRW2", T.FORMATS["A3-P-1"]["paper"], 12, 1, 1, True,
                         os.path.join(T.FMT_DIR, "A3-P-2.slddrt"), 0.297, 0.420, "По умолчанию")
    log("   лист 2:", ok, list(drw.d.GetSheetNames()))
    if ok:
        sheet_content(drw, "DRW2", CFG[1][0], CFG[1][1])
    drw.d.ActivateSheet("DRW1")
    out_dir = os.path.dirname(PART) if final else os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(out_dir, ("" if final else "proto_") + "778.КРВ.03.005 Кронштейн.SLDDRW")
    log("   сохранение:", out, drw.m.Extension.SaveAs(out, 0, 1, None, 0, 0))
    for name in drw.d.GetSheetNames():
        drw.d.ActivateSheet(name)
        drw.m.ViewZoomtofit2()
        drw.m.Extension.SaveAs(os.path.join(os.path.dirname(os.path.abspath(__file__)), "krn_%s.jpg" % name), 0, 3, None, 0, 0)
    return drw


if __name__ == "__main__":
    sw.CommandInProgress = True
    try:
        run("--final" in ARGS)
    finally:
        sw.CommandInProgress = False
