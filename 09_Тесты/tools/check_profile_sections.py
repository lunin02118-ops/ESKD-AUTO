# -*- coding: utf-8 -*-
"""Проверка профилей сварных деталей: построится ли из эскиза стенка трубы (PR №1, 15.09.2026).

Копия каждого профиля открывается в собственной сессии SolidWorks, эскиз вытягивается на 10 мм, площадь сечения
(объём / глубина) сравнивается с расчётной по размерам из имени файла. Нет тела или площадь не в пределах 85–105 % —
профиль в сварной конструкции даст не ту трубу. Исходные файлы не меняются.

    python 09_Тесты/tools/check_profile_sections.py список.txt   — пути профилей относительно корня репозитория
    python 09_Тесты/tools/check_profile_sections.py --folder "04_…/Профили сварных деталей/Сортамент ТРОЯ"
"""
import json
import math
import re
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "09_Тесты"))
from eskd_e2e import com  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

DEPTH = 0.01


def expected_area(rel):
    stem = Path(rel).stem.replace(",", ".")
    nums = [float(x) for x in re.findall(r"\d+(?:\.\d+)?", stem)]
    folder = Path(rel).parent.name.lower()
    if "кругл" in folder and len(nums) == 2:
        d, t = nums
        return math.pi / 4 * (d * d - (d - 2 * t) ** 2)
    if ("квадратн" in folder or "прямоугольн" in folder) and len(nums) >= 3:
        a, b, t = nums[:3]
        return 2 * t * (a + b) - 4 * t * t
    if "плоскоовальн" in folder and len(nums) >= 3:
        a, b, t = nums[:3]
        big, small = max(a, b), min(a, b)
        outer = (big - small) * small + math.pi * small * small / 4
        ib = small - 2 * t
        return outer - ((big - small) * ib + math.pi * ib * ib / 4)
    return None


def check(s, rel, work):
    copy = work / ("p%03d.sldlfp" % len(list(work.glob("*.sldlfp"))))
    shutil.copy2(ROOT / rel, copy)
    rec = {"file": rel}
    err, warn = com.ref_int(), com.ref_int()
    raw = s.sw.OpenDoc6(str(copy), 1, com.OPEN_SILENT, "", err, warn)
    if raw is None:
        rec["error"] = f"не открылся: {err.value}"
        return rec
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
            rec["error"] = "нет эскиза"
            return rec
        rec["regions"] = int(com.dyn(profile.GetSpecificFeature2).GetSketchRegionCount)
        doc.ClearSelection2(True)
        profile.Select2(False, 0)
        boss = com.dyn(doc.FeatureManager).FeatureExtrusion2(True, False, False, 0, 0, DEPTH, 0.0, False, False, False,
                                                             False, 0.0, 0.0, False, False, False, False, True, True, True,
                                                             0, 0.0, False)
        if boss is None or not (com.as_list(doc.GetBodies2(0, True)) or []):
            rec["error"] = "тело не построено"
            return rec
        rec["area_mm2"] = round(float(com.dyn(doc.Extension.CreateMassProperty).Volume) / DEPTH * 1e6, 2)
        exp = expected_area(rel)
        if exp:
            rec["expected_mm2"] = round(exp, 2)
            rec["ratio"] = round(rec["area_mm2"] / exp, 3)
        return rec
    finally:
        s.close(doc)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    if sys.argv[1] == "--folder":
        base = ROOT / sys.argv[2]
        items = sorted(str(p.relative_to(ROOT)).replace("\\", "/") for p in base.rglob("*") if p.suffix.lower() == ".sldlfp")
    else:
        items = [x.strip() for x in Path(sys.argv[1]).read_text(encoding="utf-8").splitlines() if x.strip()]
    run = ROOT / "08_Результаты_Тестирования" / "runs" / ("profile_sections_" + time.strftime("%Y%m%d_%H%M%S"))
    work = run / "copies"
    work.mkdir(parents=True, exist_ok=True)
    results, bad = [], 0
    with SwSession(run / "work", load_eskd=False, use_probe=False) as s:
        for rel in items:
            rec = check(s, rel, work)
            ok = "error" not in rec and (rec.get("ratio") is None or 0.85 <= rec["ratio"] <= 1.05)
            bad += not ok
            results.append(rec)
            print(("OK   " if ok else "ПЛОХО"), rel.split("/", 3)[-1], rec.get("area_mm2"), rec.get("expected_mm2"),
                  rec.get("ratio"), rec.get("error", ""), flush=True)
    (run / "result.json").write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"итог: {len(items) - bad} из {len(items)}; {run / 'result.json'}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
