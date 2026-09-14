# -*- coding: utf-8 -*-
"""Спайки ТЗ-02 С-3 (таблица изменений в форматках) и С-5 (деталь из профиля, листовой металл) — только чтение копий."""
import json
import sys
import time
from pathlib import Path

TZ = Path(r"C:\Temp\claude\D--Work--------------------------\e3b29235-8645-4d39-af38-c518c8f2bafd\scratchpad\tz02")
sys.path.insert(0, str(TZ / "09_Тесты"))
from eskd_e2e import com, oracles, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

RUN = Path(r"C:\Temp\claude\D--Work--------------------------\e3b29235-8645-4d39-af38-c518c8f2bafd\scratchpad\spike_run_" + time.strftime("%H%M%S"))
result = {"c3": {}, "c5": {}}


def sheet_info(drw):
    out = {}
    for name in [str(n) for n in com.as_list(drw.GetSheetNames)]:
        drw.ActivateSheet(name)
        sheet = com.dyn(drw.GetCurrentSheet)
        props = com.as_list(sheet.GetProperties2)
        out[name] = {"template": str(sheet.GetTemplateName), "size_mm": [round(float(props[5]) * 1000), round(float(props[6]) * 1000)]}
    return out


def features(doc):
    rows = []
    feat = doc.FirstFeature
    while feat is not None:
        f = com.dyn(feat)
        rows.append({"name": str(f.Name), "type": str(f.GetTypeName2)})
        sub = f.GetFirstSubFeature
        while sub is not None:
            s = com.dyn(sub)
            rows.append({"name": "  " + str(s.Name), "type": str(s.GetTypeName2)})
            sub = s.GetNextSubFeature
        feat = f.GetNextFeature
    return rows


with SwSession(RUN / "work", load_eskd=False, use_probe=False) as s:
    # С-3: форматка A3-P-1 как чертёж и встроенная форматка чертежа-фикстуры A-10
    for label, src, as_name in (("A3-P-1.slddrt", paths.SHEET_FORMATS / "A3-P-1.slddrt", "A3-P-1.SLDDRW"),
                                ("A4-P-1.slddrt", paths.SHEET_FORMATS / "A4-P-1.slddrt", "A4-P-1.SLDDRW"),
                                ("A3-A-2.slddrt", paths.SHEET_FORMATS / "A3-A-2.slddrt", "A3-A-2.SLDDRW")):
        work = s.workspace_copy(src, subdir="c3", name=as_name)
        drw = s.open(work, readonly=True)
        try:
            result["c3"][label] = {"sheets": sheet_info(drw), "notes": oracles.stamp(drw)}
        finally:
            s.close(drw)
    model = s.workspace_copy(paths.FIXTURES_A / "ПРТИ.468211.101 Пластина опорная.sldprt", subdir="c3a10")
    drawing = s.workspace_copy(paths.FIXTURES_A / "ПРТИ.468211.101 Пластина опорная.slddrw", subdir="c3a10")
    drw = s.open(drawing, readonly=True)
    try:
        result["c3"]["A-10 чертёж"] = {"sheets": sheet_info(drw), "notes": oracles.stamp(drw)}
        # таблицы ревизий SolidWorks
        ext = com.dyn(drw.Extension)
        result["c3"]["A-10 чертёж"]["revision_table_present"] = bool(com.dyn(drw.GetCurrentSheet).RevisionTable)
    finally:
        s.close(drw)

    # С-5: признаки профиля и листового металла
    for key in ("ПРТИ.468211.111 Стойка трубная.sldprt", "ПРТИ.468211.105 Рама сварная.sldprt",
                "ПРТИ.468211.101 Пластина опорная.sldprt", "ПРТИ.468211.107 Кожух.sldprt", "ПРТИ.468211.102 Стойка.sldprt"):
        work = s.workspace_copy(paths.FIXTURES_A / key, subdir="c5")
        doc = s.open(work, readonly=True)
        try:
            part = com.dyn(doc)
            bodies = com.as_list(part.GetBodies2(0, True)) or []
            feats = features(doc)
            types = sorted({f["type"] for f in feats})
            info = {"bodies": len(bodies), "feature_types": types,
                    "weldment": any(t in ("WeldMemberFeat", "WeldmentFeature") for t in types),
                    "sheetmetal": any(t in ("SheetMetal", "FlatPattern", "SMBaseFlange", "BaseFlange") for t in types),
                    "coord_systems": [f["name"].strip() for f in feats if f["type"] == "CoordSys"],
                    "features": feats[:80]}
            try:
                dim = com.dyn(doc.Parameter("RD1@Примечания"))
                info["RD1_mm"] = round(float(dim.SystemValue) * 1000, 2)
            except Exception as exc:  # noqa: BLE001
                info["RD1_mm"] = None
            try:
                mgr = com.dyn(com.dyn(doc.Extension).CustomPropertyManager(""))
                info["props_general"] = [str(n) for n in (com.as_list(mgr.GetNames) or [])]
            except Exception:  # noqa: BLE001
                info["props_general"] = None
            # свойства элементов списка вырезов
            cut = []
            feat = doc.FirstFeature
            while feat is not None:
                f = com.dyn(feat)
                if str(f.GetTypeName2) in ("SolidBodyFolder", "CutListFolder"):
                    sub = f.GetFirstSubFeature
                    while sub is not None:
                        sf = com.dyn(sub)
                        if str(sf.GetTypeName2) == "CutListFolder":
                            try:
                                cpm = com.dyn(sf.CustomPropertyManager)
                                names = [str(n) for n in (com.as_list(cpm.GetNames) or [])]
                                vals = {}
                                for n in names:
                                    try:
                                        r = cpm.Get6(n, False)
                                        vals[n] = [str(x) for x in com.as_list(r)] if r is not None else None
                                    except Exception:  # noqa: BLE001
                                        vals[n] = "?"
                                cut.append({"folder": str(sf.Name), "props": vals})
                            except Exception as exc:  # noqa: BLE001
                                cut.append({"folder": str(sf.Name), "error": str(exc)})
                        sub = sf.GetNextSubFeature
                feat = f.GetNextFeature
            info["cutlist"] = cut
            result["c5"][key] = info
        finally:
            s.close(doc)

RUN.mkdir(parents=True, exist_ok=True)
(RUN / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=1, default=str), encoding="utf-8")
print(RUN / "result.json")
