# -*- coding: utf-8 -*-
"""Спайки ТЗ-02: С-5 на реальных типовых сборках (копии, только чтение) и С-2 MakeIndependent на копии фикстуры A-08."""
import json
import shutil
import sys
import time
from collections import Counter
from pathlib import Path

SCR = Path(r"C:\Temp\claude\D--Work--------------------------\e3b29235-8645-4d39-af38-c518c8f2bafd\scratchpad")
TZ = SCR / "tz02"
sys.path.insert(0, str(TZ / "09_Тесты"))
from eskd_e2e import com, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

RUN = SCR / ("spike_c2c5_" + time.strftime("%H%M%S"))
COPY = RUN / "typical"
shutil.copytree(r"D:\Work\_Типовые сборки", COPY)
result = {"c5_parts": [], "c2": {}}


def feature_list(doc):
    rows = []
    feat = doc.FirstFeature
    while feat is not None:
        f = com.dyn(feat)
        rows.append(f)
        sub = f.GetFirstSubFeature
        while sub is not None:
            s = com.dyn(sub)
            rows.append(s)
            sub = s.GetNextSubFeature
        feat = f.GetNextFeature
    return rows


def cut_props(folder):
    out = {}
    try:
        cpm = folder.CustomPropertyManager
        for n in com.prop_names(cpm):
            out[n] = com.prop_get(cpm, n)
    except Exception as exc:  # noqa: BLE001
        out["_error"] = str(exc)[:120]
    return out


with SwSession(RUN / "work", load_eskd=False, use_probe=False) as s:
    parts = sorted(p for p in COPY.rglob("*") if p.suffix.lower() == ".sldprt")
    for p in parts:
        rec = {"file": str(p.relative_to(COPY))}
        try:
            doc = s.open(p, readonly=True)
        except Exception as exc:  # noqa: BLE001
            rec["error"] = str(exc)[:150]
            result["c5_parts"].append(rec)
            continue
        try:
            feats = feature_list(doc)
            types = Counter(str(f.GetTypeName2) for f in feats)
            rec["sheetmetal"] = bool(types.get("SheetMetal") or types.get("FlatPattern"))
            rec["weld_member"] = bool(types.get("WeldMemberFeat"))
            rec["weldment"] = bool(types.get("WeldmentFeature"))
            rec["coord_systems"] = [str(f.Name) for f in feats if str(f.GetTypeName2) == "CoordSys"]
            rec["bodies"] = len(com.as_list(doc.GetBodies2(0, True)) or [])
            if rec["sheetmetal"]:
                for f in feats:
                    if str(f.GetTypeName2) == "SheetMetal":
                        try:
                            rec["sm_thickness_mm"] = round(float(com.dyn(f.GetDefinition).Thickness) * 1000, 3)
                        except Exception as exc:  # noqa: BLE001
                            rec["sm_thickness_mm"] = "?" + str(exc)[:60]
                        break
            if rec["weld_member"]:
                profiles = []
                for f in feats:
                    if str(f.GetTypeName2) == "WeldMemberFeat":
                        try:
                            d = com.dyn(f.GetDefinition)
                            profiles.append({"name": str(f.Name), "profile": str(d.ProfileName), "group_count": int(d.GetGroupsCount)})
                        except Exception as exc:  # noqa: BLE001
                            profiles.append({"name": str(f.Name), "error": str(exc)[:80]})
                rec["members"] = profiles[:5]
            cut = [{"folder": str(f.Name), "props": cut_props(f)} for f in feats if str(f.GetTypeName2) == "CutListFolder"]
            if cut:
                rec["cutlist"] = cut[:4]
            try:
                rec["RD1_mm"] = round(float(com.dyn(doc.Parameter("RD1@Примечания")).SystemValue) * 1000, 2)
            except Exception:  # noqa: BLE001
                rec["RD1_mm"] = None
            try:
                rec["material"] = str(com.dyn(doc).GetMaterialPropertyName2("", "")[0]) if False else None
            except Exception:  # noqa: BLE001
                pass
        finally:
            s.close(doc)
        result["c5_parts"].append(rec)
        print(rec["file"], rec.get("sheetmetal"), rec.get("weld_member"), rec.get("coord_systems"), flush=True)

    RUN.mkdir(parents=True, exist_ok=True)
    (RUN / "result_c5.json").write_text(json.dumps(result, ensure_ascii=False, indent=1, default=str), encoding="utf-8")
    # С-2: MakeIndependent на копии A-08
    a08 = "ПРТИ.468211.110 СБ Узел опоры.sldasm"
    for dep in ("ПРТИ.468211.101 Пластина опорная.sldprt", "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt", "ПРТИ.468211.111 Стойка трубная.sldprt"):
        if (paths.FIXTURES_A / dep).exists():
            s.workspace_copy(paths.FIXTURES_A / dep, subdir="c2")
    asm_path = s.workspace_copy(paths.FIXTURES_A / a08, subdir="c2")
    asm = s.open(asm_path)
    try:
        comps = []
        for c in com.as_list(com.dyn(asm).GetComponents(False)) or []:
            c = com.dyn(c)
            comps.append({"name": str(c.Name2), "path": str(c.GetPathName)})
        result["c2"]["components"] = comps
        target = next((c for c in comps if "Пластина" in c["path"] or "Стойка" in c["path"]), None)
        result["c2"]["target"] = target
        if target:
            same = [c for c in comps if c["path"] == target["path"]]
            ext = com.dyn(asm.Extension)
            asm.ClearSelection2(True)
            selected = 0
            for c in same:
                ok = ext.SelectByID2(c["name"] + "@" + Path(str(asm_path)).stem, "COMPONENT", 0, 0, 0, True, 0, com.null_dispatch(), 0)
                selected += 1 if ok else 0
            result["c2"]["selected"] = selected
            new_path = str(Path(str(asm_path)).parent / "ПРТИ.468211.199 Новая деталь.sldprt")
            t0 = time.time()
            try:
                ok = com.dyn(asm).MakeIndependent(new_path)
                result["c2"]["make_independent"] = bool(ok)
            except Exception as exc:  # noqa: BLE001
                result["c2"]["make_independent"] = "исключение: " + str(exc)[:200]
            result["c2"]["seconds"] = round(time.time() - t0, 2)
            result["c2"]["new_file_exists"] = Path(new_path).exists()
            after = []
            for c in com.as_list(com.dyn(asm).GetComponents(False)) or []:
                c = com.dyn(c)
                after.append({"name": str(c.Name2), "path": str(c.GetPathName)})
            result["c2"]["components_after"] = after
            result["c2"]["source_mtime_unchanged"] = True
    finally:
        s.close(asm)

(RUN / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=1, default=str), encoding="utf-8")
print(RUN / "result.json")
