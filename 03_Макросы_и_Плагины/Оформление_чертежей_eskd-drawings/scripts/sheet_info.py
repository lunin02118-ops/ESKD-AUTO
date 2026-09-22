# -*- coding: utf-8 -*-
import sys
import win32com.client as w
sys.stdout.reconfigure(encoding="utf-8")
SW = w.gencache.EnsureModule("{83A33D31-27C5-11CE-BFD4-00400513BB57}", 0, 33, 0)
sw = w.GetActiveObject("SldWorks.Application")
m = SW.IModelDoc2(sw.GetOpenDocumentByName(sys.argv[1])._oleobj_)
d = SW.IDrawingDoc(m._oleobj_)
for n in d.GetSheetNames():
    s = SW.ISheet(d.Sheet(n)._oleobj_)
    p = s.GetProperties2()
    print(n, "размер %.0f×%.0f" % (p[5] * 1000, p[6] * 1000), "масштаб %g:%g" % (p[2], p[3]), s.GetTemplateName(), "| грязный", m.GetSaveFlag())
