# -*- coding: utf-8 -*-
"""Спайк: знак «×» в заметке и ячейке таблицы SolidWorks разными шрифтами ГОСТ (PDF)."""
import json, sys, time, traceback
from pathlib import Path
sys.path.insert(0, r"D:\Work\_Инструменты_Конструктора\09_Тесты")
from eskd_e2e import build, com, paths
from eskd_e2e.session import SwSession
RUN = paths.RUNS / time.strftime("font_x_spike_%Y%m%d_%H%M%S")
FONTS = ["GOST type A", "GOST Type AU", "GOST 2.304 type A"]
TEXT = "Лист 200×100 Уголок 20×20×3 ГОСТ 8509-93 — 0,63 кг Ч х"
res = {}
RUN.mkdir(parents=True); (RUN / "work").mkdir()
s = SwSession(RUN / "work", load_eskd=False, use_probe=False)
try:
    s.start()
    drw = s.new_doc(paths.DRAWING_TEMPLATE)
    build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
    for i, font in enumerate(FONTS):
        note = com.dyn(drw.InsertNote(f"<FONT name=\"{font}\">{TEXT}"))
        ann = com.dyn(note.GetAnnotation)
        ann.SetPosition2(0.03, 0.26 - i * 0.02, 0)
        fmt = com.dyn(ann.GetTextFormat(0))
        fmt.TypeFaceName = font
        fmt.CharHeight = 0.005
        ann.SetTextFormat(0, False, fmt._oleobj_ if hasattr(fmt, "_oleobj_") else fmt)
        res[font] = str(note.GetText)
    drw.ForceRebuild3(False)
    pdf = RUN / "work" / "fonts.pdf"
    res["pdf"] = s.save_as(drw, pdf)
    import fitz
    page = fitz.open(str(pdf))[0]; k = page.rect.width / 420
    page.get_pixmap(matrix=fitz.Matrix(3, 3), clip=fitz.Rect(20 * k, 25 * k, 300 * k, 90 * k)).save(str(RUN / "fonts.png"))
    res["fonts_in_pdf"] = [f[3] for f in page.get_fonts()]
except Exception:
    res["error"] = traceback.format_exc()
finally:
    s.stop()
    (RUN / "result.json").write_text(json.dumps(res, ensure_ascii=False, indent=1, default=str), encoding="utf-8")
    print(RUN)
