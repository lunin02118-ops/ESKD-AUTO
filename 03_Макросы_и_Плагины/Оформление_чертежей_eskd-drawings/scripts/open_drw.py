# -*- coding: utf-8 -*-
"""open_drw.py <полный путь к чертежу> — открыть чертёж (если ещё не открыт) и активировать."""
import sys
import win32com.client as w
sys.stdout.reconfigure(encoding="utf-8")
SW = w.gencache.EnsureModule("{83A33D31-27C5-11CE-BFD4-00400513BB57}", 0, 33, 0)
sw = w.GetActiveObject("SldWorks.Application")
p = sys.argv[1]
for x in sw.GetDocuments() or []:
    m = SW.IModelDoc2(x._oleobj_)
    if m.GetPathName().lower() == p.lower():
        sw.ActivateDoc3(p, False, 0, 0)
        print("уже открыт")
        break
else:
    m = sw.OpenDoc6(p, 3, 0, "", 0, 0)   # 3 = swDocDRAWING
    if isinstance(m, tuple):             # typed-обёртка pywin32 возвращает (документ, ошибки, предупреждения)
        m = m[0]
    print("открыт:", SW.IModelDoc2(m._oleobj_).GetPathName() if m else None)
