# -*- coding: utf-8 -*-
"""
Advanced SolidWorks Model Context Protocol (MCP) Server
Integrates AI agents with SolidWorks CAD for full modeling, weldments, ESKD properties, and drawings automation.
"""
import os
import sys
import tempfile
import json
import win32com.client
from mcp.server.fastmcp import FastMCP

# Initialize FastMCP Server
mcp = FastMCP("SolidWorks-Advanced-MCP")

WORKSPACE_ROOT = r"d:\Work\_Инструменты_Конструктора"
WELDMENT_PROFILES_DIR = os.path.join(WORKSPACE_ROOT, "04_Библиотеки_Материалов_и_Профилей", "Профили сварных деталей")
TEMPLATES_DIR = os.path.join(WORKSPACE_ROOT, "02_Шаблоны_и_Форматки")
MATERIALS_DIR = os.path.join(WORKSPACE_ROOT, "04_Библиотеки_Материалов_и_Профилей", "Библиотека материалов")

def get_sw_app():
    """Connects to active SolidWorks instance or launches background instance."""
    try:
        sw = win32com.client.GetActiveObject('SldWorks.Application')
    except Exception:
        sw = win32com.client.Dispatch('SldWorks.Application')
        sw.Visible = True
        
    try:
        addin_dll = os.path.join(WORKSPACE_ROOT, "03_Макросы_и_Плагины", "ESKD_Material_Sync_Addin", "ESKD_Material_Sync_v5.dll")
        if os.path.exists(addin_dll):
            sw.LoadAddIn(addin_dll)
    except Exception:
        pass
        
    return sw

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
    """Transfers cut-list сортамент, наименование, длина, ГОСТ into active part custom properties for 1-body parts."""
    try:
        sw = get_sw_app()
        model = sw.ActiveDoc
        if not model or model.GetType != 1:
            return {"success": False, "error": "Active document must be a Part (.sldprt)!"}
            
        active_cfg = model.GetActiveConfiguration.Name
        cpm_doc = model.Extension.CustomPropertyManager("")
        cpm_cfg = model.Extension.CustomPropertyManager(active_cfg)
        
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
        
        to_write = {
            "Сортамент": sortament,
            "Description": sortament,
            "Наименование": name,
            "Типоразмер": size,
            "ГОСТ_Сортамента": gost,
            "Заготовка": blank or name,
            "Материал": '$PRP:"Material"'
        }
        if length:
            to_write["Длина"] = str(length)
            to_write["Габарит"] = f"L={length} мм"
            
        for k, v in to_write.items():
            if v:
                cpm_doc.Add3(k, 30, v, 1)
                cpm_cfg.Add3(k, 30, v, 1)
                
        model.ForceRebuild3(False)
        return {"success": True, "transferred": to_write}
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
    """Zero-Click Synchronizer: synchronizes physical SolidWorks material with ESKD drawing title block (Материал_ФБ) and BOM properties (Материал, Сортамент, ГОСТ)."""
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
            
        cfg_name = part.GetActiveConfiguration.Name
        db_name_var = win32com.client.VARIANT(win32com.client.pythoncom.VT_BYREF | win32com.client.pythoncom.VT_BSTR, "")
        mat_name = part.GetMaterialPropertyName2(cfg_name, db_name_var)
        if not mat_name:
            mat_name = part.GetMaterialPropertyName2("", db_name_var)
            
        if not mat_name or not str(mat_name).strip():
            return {"success": False, "error": "No physical material assigned to the model in SolidWorks."}
            
        mat_name = str(mat_name).strip()
        
        is_sheet_metal = False
        thickness_mm = 0.0
        feat = part.FirstFeature
        while feat is not None:
            tname = feat.GetTypeName2
            if tname in ("SheetMetal", "SMBaseFlange"):
                is_sheet_metal = True
                try:
                    p = part.Parameter("Thickness@" + feat.Name) or part.Parameter("Толщина@" + feat.Name) or part.Parameter("\"D1@" + feat.Name + "\"")
                    if p and p.SystemValue > 0.00001:
                        thickness_mm = round(p.SystemValue * 1000.0, 2)
                except Exception:
                    pass
                break
            elif tname == "CutListFolder":
                cpm = feat.CustomPropertyManager
                if cpm:
                    val = cpm.Get("Sheet Metal Thickness") or cpm.Get("Толщина листового металла")
                    if val:
                        try:
                            thickness_mm = round(float(str(val).replace(",", ".").strip()), 2)
                            is_sheet_metal = True
                            break
                        except Exception:
                            pass
            feat = feat.GetNextFeature
            
        import re
        def extract_gost(text):
            if not text:
                return ""
            m = re.search(r'(ГОСТ|ТУ|ОСТ)\s*[\w\.\-]+', text, re.IGNORECASE)
            return m.group(0) if m else ""

        prokat = ""
        gost_prokat = ""
        gost_material = ""
        material_fb = ""
        material_sp = ""
        
        if is_sheet_metal and thickness_mm > 0.001 and " / " not in mat_name:
            t_str = f"{thickness_mm:g}".replace(".", ",")
            prokat = f"Лист Б-ПН-О-{t_str} ГОСТ 19903-2015"
            gost_prokat = "ГОСТ 19903-2015"
            gost_material = extract_gost(mat_name)
            material_fb = f"<STACK size=1>{prokat}<OVER>{mat_name}</STACK>"
            material_sp = f"{prokat} / {mat_name}"
        elif " / " in mat_name:
            parts = mat_name.split(" / ", 1)
            prokat = parts[0].strip()
            steel = parts[1].strip()
            gost_prokat = extract_gost(prokat)
            gost_material = extract_gost(steel)
            material_fb = f"<STACK size=1>{prokat}<OVER>{steel}</STACK>"
            material_sp = mat_name
        else:
            prokat = ""
            gost_prokat = ""
            gost_material = extract_gost(mat_name)
            material_fb = mat_name
            material_sp = mat_name
            
        cpm_doc = part.Extension.CustomPropertyManager("")
        cpm_cfg = part.Extension.CustomPropertyManager(cfg_name)
        
        def set_prop(name, val):
            cpm_doc.Add3(name, 30, str(val), 1)
            cpm_doc.Set2(name, str(val))
            if cfg_name:
                cpm_cfg.Add3(name, 30, str(val), 1)
                cpm_cfg.Set2(name, str(val))
                
        def del_prop(name):
            try:
                cpm_doc.Delete2(name)
                if cfg_name:
                    cpm_cfg.Delete2(name)
            except Exception:
                pass
                
        set_prop("Материал_ФБ", material_fb)
        set_prop("Материал", material_sp)
        if prokat:
            set_prop("Сортамент", prokat)
        else:
            del_prop("Сортамент")
            
        if gost_prokat:
            set_prop("ГОСТ_Сортамент", gost_prokat)
        if gost_material:
            set_prop("ГОСТ_Материал", gost_material)

        # Synchronize User Requisites from HKCU\Software\SolidWorks\ESKD_Settings
        import datetime
        import winreg
        author, checker, org, auto_mass, decimals = "", "", "", 1, 2
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\SolidWorks\ESKD_Settings") as reg_key:
                try: author, _ = winreg.QueryValueEx(reg_key, "Author")
                except Exception: pass
                try: checker, _ = winreg.QueryValueEx(reg_key, "Checker")
                except Exception: pass
                try: org, _ = winreg.QueryValueEx(reg_key, "Organization")
                except Exception: pass
                try: auto_mass, _ = winreg.QueryValueEx(reg_key, "AutoMass")
                except Exception: pass
                try: decimals, _ = winreg.QueryValueEx(reg_key, "MassDecimals")
                except Exception: pass
        except Exception:
            pass

        today_ru = datetime.datetime.now().strftime("%d.%m.%y")
        today_iso = datetime.datetime.now().strftime("%Y-%m-%d")

        if author:
            for a_prop in ("Разраб.", "Разработал", "Конструктор", "Автор", "п_Разраб", "DrawnBy"):
                set_prop(a_prop, author)
            set_prop("п_Разраб_Дата", today_ru)
            set_prop("DrawnDate", today_iso)
        if checker:
            for c_prop in ("Пров.", "Проверил", "п_Пров", "CheckedBy"):
                set_prop(c_prop, checker)
            set_prop("п_Пров_Дата", today_ru)
        if org:
            for o_prop in ("Контора", "Организация", "Организация_ФБ", "Компания"):
                set_prop(o_prop, org)

        if auto_mass:
            try:
                mass_prop = part.Extension.CreateMassProperty()
                if mass_prop:
                    m_kg = mass_prop.Mass
                    if m_kg > 0.00001:
                        m_str = f"{m_kg:.{decimals}f}".replace(".", ",")
                        set_prop("Масса_ФБ", m_str)
                        set_prop("Масса", m_str)
            except Exception:
                pass

        # Drawing format notes remain strictly in their template positions

        part.ForceRebuild3(False)
        if doc_type == 3:
            model.ForceRebuild3(False)
            
        return {
            "success": True,
            "document": part.GetTitle,
            "material_name": mat_name,
            "material_fb": material_fb,
            "material_sp": material_sp,
            "sortament": prokat,
            "gost_material": gost_material,
            "gost_sortament": gost_prokat,
            "is_sheet_metal": is_sheet_metal,
            "thickness_mm": thickness_mm,
            "mode": "fraction" if "<STACK" in material_fb else "single_line"
        }
    except Exception as e:
        return {"success": False, "error": str(e)}

if __name__ == "__main__":
    mcp.run()


