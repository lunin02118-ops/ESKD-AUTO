# -*- coding: utf-8 -*-
"""Перерисовка эскизов профилей сварных деталей по размерам из имени файла (PR №1, 15.09.2026).

Файл профиля сохраняется (свойства, признак библиотечного элемента, имя эскиза) — заменяется только геометрия эскиза:
    труба круглая ГОСТ 10704-91 øD×t     — две окружности D/2 и D/2 − t;
    труба квадратная/прямоугольная a×b×t — скруглённые прямоугольники: наружный R = 1,8t, внутренний r = 0,8t (правило
                                           рабочей библиотеки ГОСТ 8639/8645: совпадает с площадью её профилей);
    труба плоскоовальная ГОСТ 8644-68 a×b×t — две прямые и две полуокружности, стенка t.
Ориентация (какой размер по горизонтали) берётся из прежнего эскиза. Проверка результата — площадь сечения
вытягиванием (09_Тесты/tools/check_profile_sections.py).

    python 09_Тесты/tools/redraw_weldment_profiles.py список.txt   — пути профилей относительно корня репозитория
Нужен закрытый SolidWorks: скрипт поднимает собственную сессию.
"""
import math
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "09_Тесты"))
from eskd_e2e import com  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

MM = 0.001


def dims(path):
    stem = Path(path).stem.replace(",", ".")
    return [float(x) for x in re.findall(r"\d+(?:\.\d+)?", stem)]


def kind(path):
    folder = Path(path).parent.name.lower()
    if "кругл" in folder:
        return "round"
    if "плоскоовальн" in folder:
        return "oval"
    if "квадратн" in folder or "прямоугольн" in folder:
        return "rect"
    return None


def rounded_rect(sm, w, h, r):
    x, y = w / 2, h / 2
    sm.CreateLine(-x + r, -y, 0, x - r, -y, 0)
    sm.CreateLine(x, -y + r, 0, x, y - r, 0)
    sm.CreateLine(x - r, y, 0, -x + r, y, 0)
    sm.CreateLine(-x, y - r, 0, -x, -y + r, 0)
    for cx, cy, sx, sy, ex, ey in ((x - r, -y + r, x - r, -y, x, -y + r), (x - r, y - r, x, y - r, x - r, y),
                                   (-x + r, y - r, -x + r, y, -x, y - r), (-x + r, -y + r, -x, -y + r, -x + r, -y)):
        sm.CreateArc(cx, cy, 0, sx, sy, 0, ex, ey, 0, 1)


def stadium(sm, w, h):
    r, l = h / 2, (w - h) / 2
    sm.CreateLine(-l, r, 0, l, r, 0)
    sm.CreateLine(l, -r, 0, -l, -r, 0)
    sm.CreateArc(l, 0, 0, l, -r, 0, l, r, 0, 1)
    sm.CreateArc(-l, 0, 0, -l, r, 0, -l, -r, 0, 1)


def horizontal_is_wider(sketch):
    xs, ys = [], []
    for p in com.as_list(sketch.GetSketchPoints2) or []:
        p = com.dyn(p)
        xs.append(abs(float(p.X)))
        ys.append(abs(float(p.Y)))
    return not xs or max(xs) >= max(ys)


def redraw(s, rel):
    path = ROOT / rel
    k, d = kind(rel), dims(rel)
    if k is None:
        return "нет правила для папки"
    err, warn = com.ref_int(), com.ref_int()
    raw = s.sw.OpenDoc6(str(path), 1, com.OPEN_SILENT, "", err, warn)
    if raw is None:
        return f"не открылся: {err.value}"
    doc = com.dyn(raw)
    s._opened.append(doc)
    try:
        feat, profile = doc.FirstFeature, None
        while feat is not None:
            f = com.dyn(feat)
            if str(f.GetTypeName2) == "ProfileFeature":
                profile = f
            feat = f.GetNextFeature
        if profile is None:
            return "нет эскиза"
        wide = horizontal_is_wider(com.dyn(profile.GetSpecificFeature2))
        doc.ClearSelection2(True)
        profile.Select2(False, 0)
        doc.EditSketch()
        doc.ClearSelection2(True)
        doc.Extension.SelectAll()
        doc.EditDelete()
        sm = com.dyn(doc.SketchManager)
        sm.AddToDB = True
        sm.DisplayWhenAdded = False
        try:
            if k == "round":
                outer, t = d[0] * MM, d[1] * MM
                sm.CreateCircleByRadius(0, 0, 0, outer / 2)
                sm.CreateCircleByRadius(0, 0, 0, outer / 2 - t)
            else:
                a, b, t = d[0] * MM, d[1] * MM, d[2] * MM
                w, h = (max(a, b), min(a, b)) if wide else (min(a, b), max(a, b))
                if k == "rect":
                    rounded_rect(sm, w, h, 1.8 * t)
                    rounded_rect(sm, w - 2 * t, h - 2 * t, 0.8 * t)
                elif w >= h:
                    stadium(sm, w, h)
                    stadium(sm, w - 2 * t, h - 2 * t)
                else:
                    return "вертикальный плоскоовальный профиль не поддержан"
        finally:
            sm.AddToDB = False
            sm.DisplayWhenAdded = True
        sm.InsertSketch(True)
        doc.ForceRebuild3(False)
        errors, warnings = com.ref_int(), com.ref_int()
        ok = doc.Save3(1, errors, warnings)
        return "ok" if ok else f"не сохранён: {errors.value}"
    finally:
        s.close(doc)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    items = [line.strip() for line in Path(sys.argv[1]).read_text(encoding="utf-8").splitlines() if line.strip()]
    run = ROOT / "08_Результаты_Тестирования" / "runs" / "redraw_profiles"
    failed = 0
    with SwSession(run, load_eskd=False, use_probe=False) as s:
        for rel in items:
            result = redraw(s, rel)
            failed += result != "ok"
            print(("OK   " if result == "ok" else "НЕТ  ") + rel.split("/", 3)[-1] + ("" if result == "ok" else " — " + result), flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
