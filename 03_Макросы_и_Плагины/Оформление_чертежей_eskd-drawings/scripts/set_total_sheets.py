# -*- coding: utf-8 -*-
"""Проставить «Листов N» в основной надписи каждого листа открытого чертежа (так это делает DProp)."""
import sys
import win32com.client as w
sys.stdout.reconfigure(encoding="utf-8")
SW = w.gencache.EnsureModule("{83A33D31-27C5-11CE-BFD4-00400513BB57}", 0, 33, 0)
sw = w.GetActiveObject("SldWorks.Application")
tail = sys.argv[1].lower() if len(sys.argv) > 1 else "кронштейн.slddrw"
m = None
for x in sw.GetDocuments() or []:
    d = SW.IModelDoc2(x._oleobj_)
    if d.GetPathName().lower().endswith(tail):
        m = d
        break
if m is None:
    print("чертёж не открыт"); sys.exit()
d = SW.IDrawingDoc(m._oleobj_)
names = list(d.GetSheetNames())
for name in names:
    d.ActivateSheet(name)
    sv = SW.IView(d.GetFirstView()._oleobj_)
    for n in sv.GetNotes() or []:
        n = SW.INote(n._oleobj_)
        t = n.GetText() or ""
        if t.strip().startswith("Листов"):
            print(name, repr(t), "->", n.SetText("Листов %d" % len(names)))
d.ActivateSheet(names[0])
m.ForceRebuild3(False)
print("сохранение:", m.Save3(1, 0, 0))
