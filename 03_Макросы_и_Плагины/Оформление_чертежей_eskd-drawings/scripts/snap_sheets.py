# -*- coding: utf-8 -*-
"""Картинки всех листов чертежа (копией, без сохранения самого чертежа): snap_sheets.py <путь.SLDDRW> <префикс>."""
import os, sys
import win32com.client as w
sys.stdout.reconfigure(encoding="utf-8")
SW = w.gencache.EnsureModule("{83A33D31-27C5-11CE-BFD4-00400513BB57}", 0, 33, 0)
sw = w.GetActiveObject("SldWorks.Application")
OUT = os.path.dirname(os.path.abspath(__file__))
path, pre = sys.argv[1], sys.argv[2]
m = SW.IModelDoc2(sw.OpenDoc6(path, 3, 1 | 2, "", 0, 0)[0]._oleobj_)
sw.ActivateDoc3(m.GetTitle(), False, 0, 0)
d = SW.IDrawingDoc(m._oleobj_)
for name in d.GetSheetNames():
    d.ActivateSheet(name)
    m.ViewZoomtofit2()
    out = os.path.join(OUT, "%s_%s.jpg" % (pre, name))
    print(m.Extension.SaveAs(out, 0, 1 | 2, None, 0, 0), out)
