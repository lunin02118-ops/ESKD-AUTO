# -*- coding: utf-8 -*-
"""Толщина линий у вставленных таблиц: наружный контур — основная, сетка — тонкая.
fix_tbl.py <хвост_имени_чертежа> [--set]"""
import sys
import win32com.client as w
sys.stdout.reconfigure(encoding="utf-8")
SW = w.gencache.EnsureModule("{83A33D31-27C5-11CE-BFD4-00400513BB57}", 0, 33, 0)
sw = w.GetActiveObject("SldWorks.Application")
tail = sys.argv[1].lower()
doc = None
for x in sw.GetDocuments() or []:
    m = SW.IModelDoc2(x._oleobj_)
    if m.GetPathName().lower().endswith(tail):
        doc = m
        break
if doc is None:
    print("не открыт:", tail); sys.exit(1)
d = SW.IDrawingDoc(doc._oleobj_)
n = 0
for name in list(d.GetSheetNames()):
    d.ActivateSheet(name)
    v = d.GetFirstView()
    while v is not None:
        v = SW.IView(v._oleobj_)
        for t in v.GetTableAnnotations() or []:
            t = SW.ITableAnnotation(t._oleobj_)
            print(name, v.GetName2(), "таблица", t.RowCount, "x", t.ColumnCount,
                  "контур", t.BorderLineWeight, "сетка", t.GridLineWeight)
            if "--set" in sys.argv and not name.startswith("SP"):  # таблицу SpecEditor не трогаем
                t.BorderLineWeight = 1   # swLW_NORMAL
                t.GridLineWeight = 0     # swLW_THIN
                n += 1
        v = v.GetNextView()
d.ActivateSheet(list(d.GetSheetNames())[0])
if n:
    doc.ForceRebuild3(False)
    print("изменено таблиц:", n, "сохранение:", doc.Save3(1, 0, 0))
