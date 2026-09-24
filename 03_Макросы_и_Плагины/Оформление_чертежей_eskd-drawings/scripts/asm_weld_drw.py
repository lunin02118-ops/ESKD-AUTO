# -*- coding: utf-8 -*-
"""Перестройка первого листа сборочного чертежа сварной рамы на уже заданном формате (формат — макросом DProp/Master,
спецификация — макросом SpecEditor на листе SP1, их не трогаем): виды, размеры, позиции, сварные швы по ГОСТ 2.312, ТТ.
python asm_weld_drw.py "<часть имени сборки>" [--save]"""
import glob, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
ARGS = sys.argv[1:]
sys.argv = sys.argv[:1]
import asm_drw as A
T, SW, sw, MM, log = A.T, A.SW, A.sw, A.MM, A.log
mm = A.mm

WELD_GOST = "ГОСТ 14771-76"
WELDS = {  # № шва → обозначение по ГОСТ 2.312 (способ УП — в CO2 плавящимся электродом)
    1: WELD_GOST + "-С2-УП ○",     # ○ — шов по замкнутому контуру (ГОСТ 2.312, табл. 4)
    2: WELD_GOST + "-Т1-УП-△2 ○",
}
TT_WELD = [
    "*Размеры для справок.",
    "Сварка полуавтоматическая в среде CO2, проволока Св-08Г2С ГОСТ 2246-70.",
    "Общие допуски сварной конструкции по ГОСТ Р ИСО 13920-BF.",
    "Сварные швы и околошовную зону зачистить от шлака и брызг металла.",
    "Покрытие: порошковая полимерная краска, цвет - по заказу.",
]
HALF_UP, HALF_DOWN = 8, 9  # swCLOSETOP_ARROWHEAD / swCLOSEBOT_ARROWHEAD — односторонняя стрелка ГОСТ 2.312


class Open(T.Drw):
    """Уже открытый чертёж: те же методы, что у нового."""
    def __init__(self, path):
        d = sw.GetOpenDocumentByName(path) or sw.OpenDoc6(path, 3, 1, "", 0, 0)[0]
        self.m = SW.IModelDoc2(d._oleobj_)
        self.d = SW.IDrawingDoc(d._oleobj_)
        sw.ActivateDoc3(self.m.GetTitle(), False, 0, 0)
        self.sheet = list(self.d.GetSheetNames())[0]
        self.d.ActivateSheet(self.sheet)
        p = SW.ISheet(self.d.GetCurrentSheet()._oleobj_).GetProperties2()
        self.scale = (p[2], p[3]); self.k = p[2] / p[3]
        self.W, self.H = p[5] / MM, p[6] / MM
        log("чертёж", os.path.basename(path), "лист %s %.0f×%.0f %g:%g" % (self.sheet, self.W, self.H, p[2], p[3]))

    def clear(self):
        """Очистить первый лист целиком: виды, заметки, таблицы, линии эскиза листа — и поставленное скриптом, и ручные
        правки конструктора. Лист перестраивается заново (SKILL.md, шаг 6); с --save прежний чертёж уходит в _Аннулировано."""
        m, d = self.m, self.d
        sheet_view = SW.IView(d.GetFirstView()._oleobj_)
        names = []
        v = sheet_view.GetNextView()
        while v is not None:
            v = SW.IView(v._oleobj_); names.append(v.GetName2()); v = v.GetNextView()
        for n in names:
            m.ClearSelection2(True)
            m.Extension.SelectByID2(n, "DRAWINGVIEW", 0, 0, 0, False, 0, None, 0)
            m.Extension.DeleteSelection2(0)
        k = 0
        for n in list(sheet_view.GetNotes() or []):
            a = SW.IAnnotation(SW.INote(n._oleobj_).GetAnnotation()._oleobj_)
            m.ClearSelection2(True); a.Select3(False, None); m.Extension.DeleteSelection2(0); k += 1
        tb = 0
        for t in list(sheet_view.GetTableAnnotations() or []):
            a = SW.IAnnotation(SW.ITableAnnotation(t._oleobj_).GetAnnotation()._oleobj_)
            p_ = a.GetPosition()
            if p_[0] < 0:            # таблицу вне листа не выбрать
                a.SetPosition2(0.05, 0.05, 0); m.ForceRebuild3(False); p_ = a.GetPosition()
            m.ClearSelection2(True)  # таблица выбирается точкой, по имени — нет
            if m.Extension.SelectByID2("", "TABLEANNOTATION", p_[0] + 0.002, p_[1] - 0.002, 0, False, 0, None, 0):
                m.Extension.DeleteSelection2(0); tb += 1
        sk = sheet_view.GetSketch()
        segs = list(SW.ISketch(sk._oleobj_).GetSketchSegments() or []) if sk is not None else []
        for s in segs:
            m.ClearSelection2(True); SW.ISketchSegment(s._oleobj_).Select4(False, None); m.Extension.DeleteSelection2(0)
        m.ClearSelection2(True)
        log("   удалено видов %d, заметок %d, таблиц %d, линий эскиза %d" % (len(names), k, tb, len(segs)))


def joints(view, asm):
    """Стыки деталей рамы в главном виде: [(№ шва, точка на шве мм, направление наружу)]."""
    g = A.AGeo(view, asm)
    segs = [(mm(l[1]), mm(l[2]), l[3].Name2) for l in g.lines]
    X0 = min(min(a[0], b[0]) for a, b, _ in segs); X1 = max(max(a[0], b[0]) for a, b, _ in segs)
    Y0 = min(min(a[1], b[1]) for a, b, _ in segs); Y1 = max(max(a[1], b[1]) for a, b, _ in segs)
    cx, cy = (X0 + X1) / 2, (Y0 + Y1) / 2
    out = []
    # №1 — углы рамы на ус: наклонные рёбра у углов
    for a, b, n in segs:
        dx, dy = b[0] - a[0], b[1] - a[1]
        if abs(dx) > 0.5 and abs(dy) > 0.5 and abs(abs(dx) - abs(dy)) < 0.3:
            p = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
            if min(abs(p[0] - X0), abs(p[0] - X1)) < 8 and min(abs(p[1] - Y0), abs(p[1] - Y1)) < 8:
                out.append((1, p, (math.copysign(1, p[0] - cx), math.copysign(1, p[1] - cy)), n))
    # примыкание (торец одной детали на стенке другой) — №2: габариты на виде касаются по стороне с перекрытием.
    # Общую линию SW отдаёт только одной детали, поэтому по габаритам, а не по совпадающим рёбрам.
    box = {}
    for a, b, n in segs:
        r = box.setdefault(n, [1e9, 1e9, -1e9, -1e9])
        r[0] = min(r[0], a[0], b[0]); r[1] = min(r[1], a[1], b[1]); r[2] = max(r[2], a[0], b[0]); r[3] = max(r[3], a[1], b[1])
    names = sorted(box)
    for i, n1 in enumerate(names):
        for n2 in names[i + 1:]:
            p1, p2 = box[n1], box[n2]
            ox = min(p1[2], p2[2]) - max(p1[0], p2[0]); oy = min(p1[3], p2[3]) - max(p1[1], p2[1])
            if ox > 0.3 and oy > 0.3:
                continue  # перекрываются по площади — угол на ус (№1) или одна внутри другой
            if abs(ox) < 0.3 and oy > 2:      # касание по вертикальной линии
                x = p1[2] if abs(p1[2] - p2[0]) < abs(p2[2] - p1[0]) else p2[2]
                out.append((2, (x, max(p1[1], p2[1]) + oy / 2), (0, 0), n1 + "|" + n2))
            elif abs(oy) < 0.3 and ox > 2:    # касание по горизонтальной линии
                y = p1[3] if abs(p1[3] - p2[1]) < abs(p2[3] - p1[1]) else p2[3]
                out.append((2, (max(p1[0], p2[0]) + ox / 2, y), (0, 0), n1 + "|" + n2))
    return out, (X0, Y0, X1, Y1)


def weld_note(drw, p, text, shelf):
    """Линия-выноска с односторонней стрелкой от шва в точке p (мм); обозначение над полкой (стиль Underline, как у
    позиций ГОСТ 2.109), полка в shelf (мм)."""
    m = drw.m
    m.ClearSelection2(True)
    if not m.Extension.SelectByID2("", "EDGE", p[0] * MM, p[1] * MM, 0, False, 0, None, 0):
        log("   шов: не выбрано ребро в", [round(x, 1) for x in p]); return None
    n = m.InsertNote(text)
    m.ClearSelection2(True)
    if n is None:
        log("   шов: заметка не создана"); return None
    n = SW.INote(n._oleobj_)
    n.SetBalloon(10, 0)                               # swBS_Underline — текст над полкой
    a = SW.IAnnotation(n.GetAnnotation()._oleobj_)
    a.SetLeader3(1, 0, False, False, False, False)    # прямая линия-выноска к концу полки
    a.SetArrowHeadStyleAtIndex(0, HALF_UP)
    a.SetPosition2(shelf[0] * MM, shelf[1] * MM, 0)
    return a


def put_welds(drw, view, asm):
    """Одинаковым швам — один номер (ГОСТ 2.312 п.8): полное обозначение у первого, у остальных полка с номером.
    Полки — внутри рамы (снаружи размеры и позиции)."""
    js, (X0, Y0, X1, Y1) = joints(view, asm)
    uniq = {}
    for j in js:
        uniq.setdefault((j[0], round(j[1][0]), round(j[1][1])), j)
    js = list(uniq.values())
    log("   стыков:", len(js), sorted({j[0] for j in js}))
    cx = (X0 + X1) / 2
    cnt = {}
    for j in js:
        cnt[j[0]] = cnt.get(j[0], 0) + 1
    done = {}
    notes = []
    for num, p, (sx, sy), who in sorted(js, key=lambda j: (j[0], -round(j[1][1]), -j[1][0]) if j[0] == 1 else (j[0], -j[1][0], -j[1][1])):
        full = num not in done
        if not full and cnt[num] > 6:
            done[num] += 1
            continue  # одинаковых швов много: одна выноска, количество — в таблице швов
        text = "№%d" % num
        done[num] = done.get(num, 0) + 1
        inward = 1 if p[0] < cx else -1
        dy = -1 if p[1] > (Y0 + Y1) / 2 else 1
        if num == 1:  # угол — выноска внутрь рамы по диагонали
            shelf = (p[0] + inward * (16 if full else 12), p[1] + dy * 12)
        else:          # примыкание — полка вбок к середине рамы, ниже стыка
            below = [q for q in js if q[0] == 2 and abs(q[1][0] - p[0]) < 1 and 0 < q[1][1] - p[1] < 40]
            shelf = (p[0] + inward * 24, p[1] + (8 if below else -8))  # нижний из пары — полка вверх
        if inward < 0:  # текст левее точки выхода полки
            shelf = (shelf[0] - len(text) * 2.0, shelf[1])
        a = weld_note(drw, p, text, shelf)
        if a is not None:
            notes.append((num, who, [round(x, 1) for x in p]))
    for x in notes:
        log("     шов", x)
    return notes, cnt


def weld_table(drw, cnt, x, y):
    """Таблица сварных швов: номер шва — обозначение по ГОСТ 2.312 — количество."""
    rows = 1 + len(cnt)
    t = drw.m.Extension.InsertGeneralTableAnnotation(False, x * MM, y * MM, 1, "", rows, 3)
    if t is None:
        log("   таблица швов НЕ вставлена"); return None
    t = SW.ITableAnnotation(t._oleobj_)
    SW.IAnnotation(t.GetAnnotation()._oleobj_).SetPosition2(x * MM, y * MM, 0)  # вставка кладёт таблицу мимо листа
    t.TitleVisible = False
    for c, w in enumerate((15, 75, 12)):
        t.SetColumnWidth(c, w * MM, 0)
    for r in range(rows):
        t.SetRowHeight(r, 8 * MM, 0)
    head = ("№ шва", "Обозначение шва по ГОСТ 2.312", "Кол.")
    for c, v in enumerate(head):
        t.SetText(0, c, v)
    for i, num in enumerate(sorted(cnt), start=1):
        for c, v in enumerate(("№%d" % num, WELDS[num], str(cnt[num]))):
            t.SetText(i, c, v)
    for r in range(rows):
        for c in range(3):
            t.SetCellTextHorizontalJustification(r, c, 1 if c == 1 else 2)
    log("   таблица швов: %d строк" % rows)
    return t


def rebuild(stem, save=False):
    asm_path = os.path.join(T.ROOT, stem + ".SLDASM")
    model = SW.IModelDoc2((sw.GetOpenDocumentByName(asm_path) or sw.OpenDoc6(asm_path, 2, 1, "", 0, 0)[0])._oleobj_)
    cfg = model.ConfigurationManager.ActiveConfiguration.Name
    if save:
        T.archive_existing(os.path.join(T.ROOT, stem + ".SLDDRW"), copy=True)
    drw = Open(os.path.join(T.ROOT, stem + ".SLDDRW"))
    drw.clear()
    b = SW.IAssemblyDoc(model._oleobj_).GetBox(0)
    size = [abs(b[i + 3] - b[i]) * 1000 for i in range(3)]
    thin = size.index(min(size))
    vn = [0, 0, 0]; vn[thin] = 1
    main = drw.view(asm_path, T.VIEWS[tuple(vn)], 0.15, 0.25)
    main.ReferencedConfiguration = cfg
    drw.fix_scale()
    main.SetDisplayMode4(False, 2, False, True, False)
    drw.m.ForceRebuild3(False)
    o = T.outline(main)
    w_, h_ = o[2] - o[0], o[3] - o[1]
    top = drw.H - 42          # ниже графы 14×70 сверху слева и полок позиций над видом
    # на альбомном листе основная надпись и ТТ занимают правую колонку шириной 185 мм: виды левее неё
    # (вид сверху — проекционный, вбок не двигается, поэтому сдвигаем главный)
    cx = 20 + (drw.W - 25) / 2 - 8
    if drw.W > drw.H:
        cx = max(25 + w_ / 2, min(cx, (drw.W - 191) - w_ / 2))
    T.place(main, cx, top - h_ / 2)
    drw.m.ForceRebuild3(False)
    o = T.outline(main)
    side = drw.unfolded(main, (o[0] + o[2]) / 2 * MM, (o[1] - 50) * MM)
    if side:
        side.SetDisplayMode4(False, 2, False, True, False)
    drw.m.ForceRebuild3(False)
    made = []
    X0, Y0, X1, Y1, row = A.frame_dims(drw, main, model, made, col0=45)
    if side:
        os_ = T.outline(side)
        w_s = os_[2] - os_[0]
        # левее колонки ТТ и основной надписи (на альбомном листе они справа)
        T.place(side, (os_[0] + os_[2]) / 2, row - 14 - (os_[3] - os_[1]) / 2)
        drw.m.ForceRebuild3(False)
        A.side_dims(drw, side, model, made)
    log("   размеров:", made)
    A.place_balloons(drw, main, model, X0, Y0, X1, Y1, X1 + 18, tt_top=0, tt_x=9999)
    drw.m.ForceRebuild3(False)
    _, cnt = put_welds(drw, main, model)
    drw.m.ForceRebuild3(False)
    weld_table(drw, cnt, 20 if drw.W < drw.H else 25, 150 if drw.W < drw.H else 48)
    drw.m.ForceRebuild3(False)
    T.add_tt(TT_WELD)
    drw.d.ActivateSheet(drw.sheet)
    drw.m.ViewZoomtofit2()
    snap = os.path.join(os.path.dirname(os.path.abspath(__file__)), "re_%s.jpg" % os.path.basename(stem))
    drw.m.Extension.SaveAs(snap, 0, 3, None, 0, 0)
    log("   снимок", snap)
    if save:
        log("   сохранён", drw.m.Save3(1, 0, 0))
    return drw


if __name__ == "__main__":
    sw.CommandInProgress = True
    try:
        for s in [x for x in ARGS if not x.startswith("--")]:
            rebuild(T.find_model(s, ".SLDASM"), "--save" in ARGS)
    finally:
        sw.CommandInProgress = False
