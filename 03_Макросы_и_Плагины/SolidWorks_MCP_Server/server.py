# -*- coding: utf-8 -*-
"""
Advanced SolidWorks Model Context Protocol (MCP) Server
Integrates AI agents with SolidWorks CAD for full modeling, weldments, ESKD properties, and drawings automation.
"""
import os
import sys
import tempfile
import json
from pathlib import Path

import pythoncom
import win32com.client
from mcp.server.fastmcp import FastMCP

# Initialize FastMCP Server
mcp = FastMCP("SolidWorks-Advanced-MCP")

# Корень репозитория — от расположения сервера: 03_Макросы_и_Плагины/SolidWorks_MCP_Server/server.py
WORKSPACE_ROOT = str(Path(__file__).resolve().parents[2])
WELDMENT_PROFILES_DIR = os.path.join(WORKSPACE_ROOT, "04_Библиотеки_Материалов_и_Профилей", "Профили сварных деталей")
TEMPLATES_DIR = os.path.join(WORKSPACE_ROOT, "02_Шаблоны_и_Форматки")
MATERIALS_DIR = os.path.join(WORKSPACE_ROOT, "04_Библиотеки_Материалов_и_Профилей", "Библиотека материалов")
ESKD_ADDIN_DLL = os.path.join(WORKSPACE_ROOT, "03_Макросы_и_Плагины", "ESKD_Material_Sync_Addin", "ESKD_Material_Sync_v5.dll")
ESKD_ADDIN_PROGID = "ESKD.MaterialSync.SwAddin_v5"


def get_sw_app():
    """Connects to active SolidWorks instance or launches a visible one; loads the ESKD add-in once."""
    try:
        sw = win32com.client.GetActiveObject('SldWorks.Application')
    except Exception:
        sw = win32com.client.Dispatch('SldWorks.Application')
        sw.Visible = True
    eskd_addin(sw)
    return sw


def eskd_addin(sw):
    """Объект надстройки ЕСКД. LoadAddIn — только если она ещё не загружена: повторные загрузки роняют SolidWorks (Д-26)."""
    addin = sw.GetAddInObject(ESKD_ADDIN_PROGID)
    if addin is None and os.path.exists(ESKD_ADDIN_DLL):
        sw.LoadAddIn(ESKD_ADDIN_DLL)
        addin = sw.GetAddInObject(ESKD_ADDIN_PROGID)
    return addin


def com_method(obj, name, *args):
    """Вызов метода COM-объекта: позднее связывание pywin32 выполняет метод без аргументов как чтение свойства."""
    ole = obj._oleobj_
    return ole.Invoke(ole.GetIDsOfNames(name), 0, pythoncom.DISPATCH_METHOD, True, *args)


def model_of(doc):
    """Деталь или сборка; для чертежа — модель первого вида."""
    if doc.GetType in (1, 2):
        return doc
    if doc.GetType == 3:
        view = doc.GetFirstView.GetNextView
        return view.ReferencedDocument if view else None
    return None

@mcp.tool()
def sw_get_status() -> dict:
    """Returns the current status of SolidWorks: revision, active document, document type, and units."""
    try:
        sw = get_sw_app()
        rev = sw.RevisionNumber
        model = sw.ActiveDoc
        if not model:
            return {"connected": True, "solidworks_version": rev, "active_document": None, "status": "No active document open."}
        
        doc_type_map = {1: "Part (.sldprt)", 2: "Assembly (.sldasm)", 3: "Drawing (.slddrw)"}
        t = model.GetType
        active_config = model.GetActiveConfiguration.Name if t in (1, 2) else None
        
        return {
            "connected": True,
            "solidworks_version": rev,
            "active_document": model.GetTitle,
            "document_path": model.GetPathName,
            "document_type": doc_type_map.get(t, f"Unknown ({t})"),
            "active_configuration": active_config,
            "is_dirty": model.GetSaveFlag
        }
    except Exception as e:
        return {"connected": False, "error": str(e)}

@mcp.tool()
def sw_open_document(filepath: str, read_only: bool = False, silent: bool = False) -> dict:
    """Opens a CAD document in SolidWorks (Part, Assembly, Drawing, or Profile)."""
    try:
        sw = get_sw_app()
        if not os.path.isabs(filepath):
            filepath = os.path.join(WORKSPACE_ROOT, filepath)
            
        if not os.path.exists(filepath):
            return {"success": False, "error": f"File not found: {filepath}"}
            
        ext = os.path.splitext(filepath)[1].lower()
        doc_type = 1 if ext in ('.sldprt', '.sldlfp', '.prtdot') else (2 if ext in ('.sldasm', '.asmdot') else 3)
        options = (1 if silent else 0) | (2 if read_only else 0)
        
        errors = win32com.client.VARIANT(win32com.client.pythoncom.VT_BYREF | win32com.client.pythoncom.VT_I4, 0)
        warnings = win32com.client.VARIANT(win32com.client.pythoncom.VT_BYREF | win32com.client.pythoncom.VT_I4, 0)
        
        model = sw.OpenDoc6(filepath, doc_type, options, "", errors, warnings)
        if model:
            sw.ActivateDoc3(model.GetTitle, False, 2, errors)
            return {"success": True, "title": model.GetTitle, "path": model.GetPathName}
        else:
            return {"success": False, "error_code": errors.value}
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_create_document(doc_type: str = "part") -> dict:
    """Creates a new document in SolidWorks using corporate templates ('part', 'assembly', 'drawing')."""
    try:
        sw = get_sw_app()
        doc_type_lower = doc_type.lower()
        
        template_map = {
            "part": os.path.join(TEMPLATES_DIR, "Шаблоны документов", "Деталь.prtdot"),
            "part_gost": os.path.join(TEMPLATES_DIR, "Шаблоны документов", "Деталь ГОСТ.prtdot"),
            "assembly": os.path.join(TEMPLATES_DIR, "Шаблоны документов", "Сборка.asmdot"),
            "assembly_gost": os.path.join(TEMPLATES_DIR, "Шаблоны документов", "Сборка ГОСТ.asmdot"),
            "drawing": os.path.join(TEMPLATES_DIR, "Шаблоны документов", "Чертеж.drwdot"),
            "drawing_gost": os.path.join(TEMPLATES_DIR, "Шаблоны документов", "Чертеж ГОСТ.drwdot")
        }
        
        tmpl = template_map.get(doc_type_lower)
        if tmpl and os.path.exists(tmpl):
            model = sw.NewDocument(tmpl, 0, 0, 0)
        else:
            if doc_type_lower == "part":
                model = sw.NewPart()
            elif doc_type_lower == "assembly":
                model = sw.NewAssembly()
            else:
                model = sw.NewDrawing2(0)
                
        if model:
            return {"success": True, "title": model.GetTitle, "type": doc_type}
        return {"success": False, "error": "Could not create document."}
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_get_custom_properties(configuration: str = "") -> dict:
    """Gets all custom properties (General and Configuration-specific) of active document."""
    try:
        sw = get_sw_app()
        model = sw.ActiveDoc
        if not model:
            return {"success": False, "error": "No active document."}
            
        cpm_general = model.Extension.CustomPropertyManager("")
        gen_names = cpm_general.GetNames
        general_props = {n: cpm_general.Get(n) for n in gen_names} if gen_names else {}
        
        config_props = {}
        if model.GetType in (1, 2):
            cfgs = [configuration] if configuration else model.GetConfigurationNames
            if cfgs:
                for c in cfgs:
                    cpm_cfg = model.Extension.CustomPropertyManager(c)
                    cfg_names = cpm_cfg.GetNames
                    if cfg_names:
                        config_props[c] = {n: cpm_cfg.Get(n) for n in cfg_names}
                        
        return {
            "success": True,
            "document": model.GetTitle,
            "general_properties": general_props,
            "configuration_properties": config_props
        }
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_set_custom_properties(properties: dict, configuration: str = "") -> dict:
    """Sets or updates custom properties in active document (e.g. {'Сортамент': 'Труба 100х50х3 ГОСТ 8645-68'})."""
    try:
        sw = get_sw_app()
        model = sw.ActiveDoc
        if not model:
            return {"success": False, "error": "No active document."}
            
        cpm_gen = model.Extension.CustomPropertyManager("")
        for k, v in properties.items():
            cpm_gen.Add3(k, 30, str(v), 1)
            
        if model.GetType in (1, 2):
            cfg_name = configuration or model.GetActiveConfiguration.Name
            cpm_cfg = model.Extension.CustomPropertyManager(cfg_name)
            for k, v in properties.items():
                cpm_cfg.Add3(k, 30, str(v), 1)
                
        model.ForceRebuild3(False)
        return {"success": True, "updated_properties": properties}
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_weldment_transfer_properties() -> dict:
    """Reads cut-list сортамент, наименование, длина, ГОСТ of the active 1-body weldment part (read-only).

    Свойства детали сервер не пишет (решение D-10): запись для спецификации делает sw_eskd_toggle_drawingless —
    кнопка «Деталь БЧ» надстройки ЕСКД по ГОСТ Р 2.109-2023, словарные имена на уровнях MProp.
    """
    try:
        sw = get_sw_app()
        model = sw.ActiveDoc
        if not model or model.GetType != 1:
            return {"success": False, "error": "Active document must be a Part (.sldprt)!"}

        props_extracted = {}
        feat = model.FirstFeature()
        while feat is not None:
            type_name = feat.GetTypeName2() if callable(feat.GetTypeName2) else feat.GetTypeName2
            if type_name in ("CutListFolder", "SubWeldFolder"):
                cpm = feat.CustomPropertyManager
                if cpm:
                    names_fn = cpm.GetNames
                    names = names_fn() if callable(names_fn) else names_fn
                    if names:
                        for n in names:
                            val = cpm.Get(n)
                            if val and str(val).strip():
                                props_extracted[n] = str(val)
                        if "Сортамент" in props_extracted or "Description" in props_extracted:
                            break
            next_fn = feat.GetNextFeature
            feat = next_fn() if callable(next_fn) else feat.GetNextFeature()
            
        if not props_extracted:
            return {"success": False, "error": "No weldment cut-list item found in feature tree."}
            
        sortament = props_extracted.get("Сортамент") or props_extracted.get("Description", "")
        name = props_extracted.get("Наименование", "")
        size = props_extracted.get("Типоразмер", "")
        gost = props_extracted.get("ГОСТ", "")
        blank = props_extracted.get("Заготовка", "")
        length = props_extracted.get("Длина") or props_extracted.get("LENGTH", "")
        
        cut_list = {
            "sortament": sortament,
            "name": name,
            "size": size,
            "gost": gost,
            "blank": blank or name,
            "length_mm": str(length) if length else "",
        }
        return {
            "success": True,
            "cut_list": cut_list,
            "written": False,
            "hint": "Запись для спецификации — sw_eskd_toggle_drawingless (кнопка «Деталь БЧ» надстройки ЕСКД).",
        }
    except Exception as e:
        return {"success": False, "error": str(e)}


@mcp.tool()
def sw_eskd_toggle_drawingless() -> dict:
    """Toggles «Деталь БЧ» for the active part through the ESKD add-in (Формат = БЧ, масса в «Примечании», запись черт. 40)."""
    try:
        sw = get_sw_app()
        model = sw.ActiveDoc
        if not model or model.GetType != 1:
            return {"success": False, "error": "Active document must be a Part (.sldprt)!"}
        addin = eskd_addin(sw)
        if addin is None:
            return {"success": False, "error": "Надстройка ЕСКД не загружена — свойства не записаны."}
        result = com_method(addin, "ToggleDrawinglessSilent")
        states = {1: "enabled", 2: "disabled", 0: "not_a_part"}
        return {"success": result in (1, 2), "state": states.get(result, "error"), "document": model.GetTitle}
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_weldment_list_profiles(category_query: str = "") -> dict:
    """Lists available weldment library profiles and standard sizes from the normalized library."""
    try:
        categories = {}
        for s in os.listdir(WELDMENT_PROFILES_DIR):
            s_path = os.path.join(WELDMENT_PROFILES_DIR, s)
            if os.path.isdir(s_path):
                for t in os.listdir(s_path):
                    if category_query.lower() in t.lower() or not category_query:
                        t_path = os.path.join(s_path, t)
                        if os.path.isdir(t_path):
                            sizes = [f[:-7] for f in os.listdir(t_path) if f.endswith('.sldlfp')]
                            categories[f"{s}/{t}"] = sizes
        return {"success": True, "categories_count": len(categories), "profiles": categories}
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_get_mass_properties() -> dict:
    """Calculates mass properties of the active document: Mass (kg), Volume, Center of Mass, Surface Area."""
    try:
        sw = get_sw_app()
        model = sw.ActiveDoc
        if not model:
            return {"success": False, "error": "No active document."}
            
        mp = model.Extension.CreateMassProperty
        if not mp:
            return {"success": False, "error": "Could not create mass property calculator."}
            
        return {
            "success": True,
            "mass_kg": round(mp.Mass, 4),
            "volume_m3": round(mp.Volume, 6),
            "surface_area_m2": round(mp.SurfaceArea, 4),
            "center_of_mass_mm": [round(c * 1000.0, 2) for c in mp.CenterOfMass],
            "density_kg_m3": round(mp.Density, 2)
        }
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_export(output_path: str, export_format: str = "STEP") -> dict:
    """Exports active model to STEP, DXF, PDF, STL, IGES, Parasolid."""
    try:
        sw = get_sw_app()
        model = sw.ActiveDoc
        if not model:
            return {"success": False, "error": "No active document."}
            
        if not os.path.isabs(output_path):
            output_path = os.path.join(WORKSPACE_ROOT, output_path)
            
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        
        errors = win32com.client.VARIANT(win32com.client.pythoncom.VT_BYREF | win32com.client.pythoncom.VT_I4, 0)
        warnings = win32com.client.VARIANT(win32com.client.pythoncom.VT_BYREF | win32com.client.pythoncom.VT_I4, 0)
        
        res = model.SaveAs4(output_path, 0, 1, errors, warnings)
        return {"success": res, "output_path": output_path, "errors": errors.value}
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_run_vba_macro(vba_code: str, module_name: str = "main", procedure_name: str = "main") -> dict:
    """Dynamically executes arbitrary SolidWorks VBA macro code (.swp path or raw VBA script)."""
    try:
        sw = get_sw_app()
        error_val = win32com.client.VARIANT(win32com.client.pythoncom.VT_BYREF | win32com.client.pythoncom.VT_I4, 0)
        
        # Check if vba_code is a path to an existing macro (.swp / .swb)
        macro_path = vba_code.strip().strip('"')
        if not os.path.isabs(macro_path):
            macro_path = os.path.join(WORKSPACE_ROOT, macro_path)
            
        if os.path.exists(macro_path) and macro_path.lower().endswith(('.swp', '.swb')):
            res = sw.RunMacro2(macro_path, module_name, procedure_name, 1, error_val)
            return {"success": bool(res), "file": macro_path, "module": module_name, "procedure": procedure_name, "error_code": error_val.value}
            
        # Otherwise treat vba_code as raw script and run via temporary .swb
        temp_dir = tempfile.gettempdir()
        temp_swb = os.path.join(temp_dir, "sw_mcp_temp_macro.swb")
        with open(temp_swb, "w", encoding="cp1251", errors="replace") as f:
            f.write(vba_code)
            
        res = sw.RunMacro2(temp_swb, "", "main", 1, error_val)
        if os.path.exists(temp_swb):
            try:
                os.remove(temp_swb)
            except Exception:
                pass
                
        return {"success": bool(res), "type": "script", "error_code": error_val.value}
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_list_tt_profiles() -> dict:
    """Returns the list of all available Technical Requirements (ТТ) profiles from SWPlus TT_Prof.txt."""
    try:
        tt_prof_path = os.path.join(WORKSPACE_ROOT, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "ТТ", "TT_Prof.txt")
        if not os.path.exists(tt_prof_path):
            return {"success": False, "error": "TT_Prof.txt not found."}
            
        with open(tt_prof_path, "r", encoding="cp1251", errors="replace") as f:
            lines = [l.strip() for l in f]
            
        profiles = {}
        cur = None
        for l in lines:
            if l.startswith("$$$"):
                cur = l[3:].strip()
                profiles[cur] = []
            elif cur and l:
                profiles[cur].append(l)
                
        return {"success": True, "profiles": list(profiles.keys()), "count": len(profiles)}
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_apply_tt_profile(profile_name: str) -> dict:
    """Applies a standard Technical Requirements (ТТ) profile from TT_Prof.txt to the active drawing in SolidWorks."""
    try:
        tt_prof_path = os.path.join(WORKSPACE_ROOT, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "ТТ", "TT_Prof.txt")
        if not os.path.exists(tt_prof_path):
            return {"success": False, "error": "TT_Prof.txt not found."}
            
        with open(tt_prof_path, "r", encoding="cp1251", errors="replace") as f:
            lines = [l.strip() for l in f]
            
        profiles = {}
        cur = None
        for l in lines:
            if l.startswith("$$$"):
                cur = l[3:].strip()
                profiles[cur] = []
            elif cur and l:
                profiles[cur].append(l)
                
        if profile_name not in profiles:
            return {"success": False, "error": f"Profile '{profile_name}' not found.", "available": list(profiles.keys())}
            
        items = profiles[profile_name]
        ref_items = [it for it in items if it.startswith("*")]
        other_items = [it for it in items if not it.startswith("*")]
        all_ordered = ref_items + other_items
        
        formatted_lines = []
        for idx, item in enumerate(all_ordered, 1):
            if len(all_ordered) == 1:
                formatted_lines.append(item)
            else:
                formatted_lines.append(f"{idx}. {item}")
                
        tt_text = "\r\n".join(formatted_lines)
        
        sw = get_sw_app()
        drw = sw.ActiveDoc
        if not drw or drw.GetType != 3:
            return {"success": False, "error": "No drawing active in SolidWorks."}
            
        sheet_names = drw.GetSheetNames
        if not sheet_names or len(sheet_names) == 0:
            return {"success": False, "error": "No sheets found in drawing."}

        current_sheet = drw.GetCurrentSheet.GetName
        sheet1_name = sheet_names[0]  # ГОСТ 2.316-2008: ТТ наносятся ТОЛЬКО на первый лист

        drw.ActivateSheet(sheet1_name)
        sheet = drw.GetCurrentSheet
        props = sheet.GetProperties2
        sheet_width = props[5]

        drw.ActivateView("")
        sheet_view = drw.GetFirstView

        existing_tt_note = None
        if sheet_view:
            n = sheet_view.GetFirstNote
            while n:
                name = n.GetName or ""
                txt = n.GetText or ""
                ann = n.GetAnnotation
                pos = ann.GetPosition if ann else None
                
                is_tt = False
                if "заметка_тт" in name.lower() or "tt_note" in name.lower():
                    is_tt = True
                elif pos and len(pos) >= 2:
                    in_tt_zone = (pos[0] >= (sheet_width - 0.200)) and (0.055 <= pos[1] <= 0.280)
                    if in_tt_zone and any(k in txt.lower() for k in ["*размеры для справок", "для справок", "гост 30893", "гост 14771", "технические требования"]):
                        is_tt = True
                        
                if is_tt:
                    existing_tt_note = n
                    break
                n = n.GetNext

        pos_x = sheet_width - 0.185
        line_count = len(formatted_lines)
        pos_y = 0.065 + (0.0065 * line_count)

        if existing_tt_note:
            existing_tt_note.SetText(tt_text)
            existing_tt_note.SetName("Заметка_ТТ")
            ann = existing_tt_note.GetAnnotation
            if ann:
                ann.SetPosition(pos_x, pos_y, 0.0)
                tf = existing_tt_note.GetTextFormat
                if tf:
                    tf.CharHeight = 0.0035
                    tf.Italic = True
                    ann.SetTextFormat(0, False, tf)
        else:
            note = drw.CreateText2(tt_text, pos_x, pos_y, 0, 0.0035, 0)
            if note:
                note.SetName("Заметка_ТТ")
                tf = note.GetTextFormat
                if tf:
                    tf.CharHeight = 0.0035
                    tf.Italic = True
                    note.GetAnnotation.SetTextFormat(0, False, tf)

        if current_sheet != sheet1_name:
            drw.ActivateSheet(current_sheet)

        drw.ClearSelection2(True)
        drw.ForceRebuild3(False)

        return {"success": True, "applied_profile": profile_name, "line_count": len(formatted_lines), "target_sheet": sheet1_name}
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_set_material(material_name: str, database_name: str = "") -> dict:
    """Assigns a physical material to active Part or referenced Part in Drawing."""
    try:
        sw = get_sw_app()
        model = sw.ActiveDoc
        if not model:
            return {"success": False, "error": "No active document in SolidWorks."}
            
        doc_type = model.GetType
        part = None
        if doc_type == 1:
            part = model
        elif doc_type == 3:
            v = model.GetFirstView.GetNextView
            if v:
                ref_model = v.ReferencedDocument
                if ref_model and ref_model.GetType == 1:
                    part = ref_model
            if not part:
                return {"success": False, "error": "Drawing does not reference a valid Part document."}
        else:
            return {"success": False, "error": "Active document must be a Part or Drawing of a Part."}
            
        if not database_name:
            database_name = os.path.join(MATERIALS_DIR, "Библиотека_Материалов_ГОСТ.sldmat")
            
        cfg_name = part.GetActiveConfiguration.Name
        part.SetMaterialPropertyName2(cfg_name, database_name, material_name)
        part.ForceRebuild3(False)
        
        return {"success": True, "material": material_name, "database": database_name, "part": part.GetTitle}
    except Exception as e:
        return {"success": False, "error": str(e)}

@mcp.tool()
def sw_sync_eskd_materials() -> dict:
    """Synchronizes ESKD properties of the active document (материал, масса, обозначение, подписи) through the ESKD add-in.

    Сервер свойства не пишет: запись выполняет надстройка по словарю SWPlus и уровням MProp (решение D-10),
    результат совпадает с кнопкой «Синхронизировать ЕСКД». В ответе — план изменений и итоговые значения граф.
    """
    try:
        sw = get_sw_app()
        model = sw.ActiveDoc
        if not model:
            return {"success": False, "error": "No active document in SolidWorks."}
        addin = eskd_addin(sw)
        if addin is None:
            return {"success": False, "error": "Надстройка ЕСКД не загружена — реквизиты не записаны."}
        plan = com_method(addin, "DiagnoseActiveDocument")
        changes = com_method(addin, "SyncActiveDocumentSilent")
        result = {"success": changes >= 0, "document": model.GetTitle, "changes": changes, "plan": plan}
        if changes < 0:
            result["error"] = "Надстройка не синхронизировала документ: подробности в %TEMP%\\eskd_material_sync.log"
            return result
        target = model_of(model)
        if target is not None:
            cpm = target.Extension.CustomPropertyManager(target.GetActiveConfiguration.Name)
            result["stamp"] = {name: cpm.Get(name) for name in ("Обозначение", "Материал_ФБ", "Масса_ФБ")}
        return result
    except Exception as e:
        return {"success": False, "error": str(e)}


if __name__ == "__main__":
    mcp.run()


