using System;
using System.IO;
using System.Xml;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync
{
    public class MaterialSyncResult
    {
        public bool Success { get; set; }
        public bool Cached { get; set; }
        public string MaterialName { get; set; }
        public string MaterialFB { get; set; }
        public string MaterialSP { get; set; }
        public string Sortament { get; set; }
        public string GostMaterial { get; set; }
        public string GostSortament { get; set; }
        public bool IsSheetMetal { get; set; }
        public double ThicknessMm { get; set; }
        public double MassKg { get; set; }
        public string Message { get; set; }
    }

    public static class MaterialSyncEngine
    {
        private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
        private static readonly Dictionary<string, string> LastProcessedCache = new Dictionary<string, string>();
        private const string RegKeySettings = @"Software\SolidWorks\ESKD_Settings";

        public static void SyncModelProperties(ModelDoc2 model, ISldWorks swApp, bool force = true, bool triggerRebuild = true, string targetFileName = null)
        {
            if (model == null || swApp == null) return;
            try
            {
                int docType = model.GetType();
                if (docType == (int)swDocumentTypes_e.swDocPART)
                {
                    SyncPart((PartDoc)model, swApp, force, targetFileName);
                }
                else if (docType == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    Configuration activeConfig = (Configuration)model.GetActiveConfiguration();
                    string cfgName = activeConfig != null ? activeConfig.Name : "";
                    MaterialSyncResult res = new MaterialSyncResult();
                    ApplyUserSettings(model, cfgName, res, targetFileName);
                }
                else if (docType == (int)swDocumentTypes_e.swDocDRAWING)
                {
                    SyncDrawing((DrawingDoc)model, swApp, triggerRebuild, targetFileName);
                }
            }
            catch { }
        }

        public static void SyncDrawing(DrawingDoc drw, ISldWorks swApp, bool triggerRebuild = true, string targetFileName = null)
        {
            if (drw == null) return;
            try
            {
                ModelDoc2 drwModel = (ModelDoc2)drw;
                ApplyUserSettings(drwModel, "", null, targetFileName);

                // Also update any referenced 3D models in drawing views so $PRPSHEET links update
                HashSet<string> updatedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    View v = (View)drw.GetFirstView();
                    while (v != null)
                    {
                        ModelDoc2 refDoc = (ModelDoc2)v.ReferencedDocument;
                        if (refDoc != null)
                        {
                            string refPath = refDoc.GetPathName();
                            if (string.IsNullOrEmpty(refPath)) refPath = refDoc.GetTitle();
                            if (!string.IsNullOrEmpty(refPath) && !updatedModels.Contains(refPath))
                            {
                                updatedModels.Add(refPath);
                                string refTargetName = null;
                                string refClean = CleanDocumentName(refPath);
                                if (Regex.IsMatch(refClean, @"^(Деталь|Part|Сборка|Assem|Чертеж|Draw)\s*\d*$", RegexOptions.IgnoreCase))
                                {
                                    refTargetName = targetFileName;
                                }
                                ApplyUserSettings(refDoc, v.ReferencedConfiguration, null, refTargetName);
                                refDoc.SetSaveFlag();
                            }
                        }
                        v = (View)v.GetNextView();
                    }
                }
                catch { }

                AlignDrawingMassNote(drw);
                AlignDrawingMaterialNote(drw);
                if (triggerRebuild)
                {
                    drwModel.ForceRebuild3(true);
                }
            }
            catch { }
        }

        public static void AlignDrawingMassNote(DrawingDoc drw)
        {
            if (drw == null) return;
            try
            {
                int autoCenter = 1;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeySettings))
                {
                    if (key != null)
                    {
                        autoCenter = (int)key.GetValue("AutoCenterMass", 1);
                    }
                }
                if (autoCenter == 0) return;

                Sheet sheet = (Sheet)drw.GetCurrentSheet();
                double sheetW = 0.0;
                if (sheet != null)
                {
                    double[] sProps = (double[])sheet.GetProperties2();
                    if (sProps != null && sProps.Length > 5)
                    {
                        sheetW = sProps[5];
                    }
                }

                View v = (View)drw.GetFirstView();
                double scaleX = 0.0;
                double scaleY = 0.0;
                int scaleJust = 0;
                bool foundScale = false;

                // Pass 1: find dynamic Scale value note (NEVER match static header label "Масштаб")
                View cur = v;
                while (cur != null)
                {
                    Note n = (Note)cur.GetFirstNote();
                    while (n != null)
                    {
                        string ltxt = n.PropertyLinkedText ?? "";
                        string name = n.GetName() ?? "";

                        bool isScaleValue = false;
                        if (name.Equals("Scale", StringComparison.OrdinalIgnoreCase))
                        {
                            isScaleValue = true;
                        }
                        else if (ltxt.IndexOf("Sheet Scale", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 ltxt.IndexOf("Масштаб листа", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 ltxt.IndexOf("SW-Sheet Scale", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            isScaleValue = true;
                        }

                        if (isScaleValue)
                        {
                            Annotation ann = (Annotation)n.GetAnnotation();
                            if (ann != null)
                            {
                                double[] pos = (double[])ann.GetPosition();
                                if (pos != null && pos.Length >= 2 && pos[1] > 0.005)
                                {
                                    scaleX = pos[0];
                                    scaleY = pos[1];
                                    scaleJust = n.GetTextJustification();
                                    foundScale = true;
                                    break;
                                }
                            }
                        }
                        n = (Note)n.GetNext();
                    }
                    if (foundScale) break;
                    cur = (View)cur.GetNextView();
                }

                double targetX = 0.0;
                double targetY = 0.0;

                // Standard GOST 2.104 title block dimensions:
                // Stamp width = 185 mm, right margin = 5 mm.
                // Mass cell center is exactly 31.85 mm from the right edge of sheet (or 17.5 mm to the left of centered Scale cell).
                // Mass cell baseline/center Y is 34.53 mm from bottom edge.
                if (sheetW > 0.15)
                {
                    targetX = sheetW - 0.03185;
                    targetY = foundScale ? scaleY : 0.03453;
                    if (foundScale && scaleJust == (int)swTextJustification_e.swTextJustificationCenter)
                    {
                        targetX = scaleX - 0.0175;
                    }
                }
                else if (foundScale)
                {
                    targetY = scaleY;
                    targetX = scaleX - 0.0175;
                }
                else
                {
                    return;
                }

                // Pass 2: find dynamic Mass value note (NEVER match static header label "Масса")
                cur = v;
                while (cur != null)
                {
                    Note n = (Note)cur.GetFirstNote();
                    while (n != null)
                    {
                        string ltxt = n.PropertyLinkedText ?? "";
                        string name = n.GetName() ?? "";

                        bool isMassValue = false;
                        if (name.Equals("MYPRP15", StringComparison.OrdinalIgnoreCase))
                        {
                            isMassValue = true;
                        }
                        else if (ltxt.IndexOf("Масса_ФБ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 ltxt.IndexOf("$PRPSHEET:\"Масса", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 ltxt.IndexOf("$PRP:\"Масса", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 ltxt.IndexOf("$PRPSHEET:\"SW-Mass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 ltxt.IndexOf("$PRP:\"SW-Mass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (ltxt.IndexOf("Масса", StringComparison.OrdinalIgnoreCase) >= 0 && ltxt.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            isMassValue = true;
                        }

                        if (isMassValue)
                        {
                            Annotation ann = (Annotation)n.GetAnnotation();
                            if (ann != null)
                            {
                                ann.SetPosition(targetX, targetY, 0.0);
                                n.SetTextJustification((int)swTextJustification_e.swTextJustificationCenter);
                            }
                        }
                        n = (Note)n.GetNext();
                    }
                    cur = (View)cur.GetNextView();
                }
            }
            catch { }
        }

        public static void AlignDrawingMaterialNote(DrawingDoc drw)
        {
            if (drw == null) return;
            try
            {
                int autoCenter = 1;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeySettings))
                {
                    if (key != null)
                    {
                        autoCenter = (int)key.GetValue("AutoCenterMass", 1);
                    }
                }
                if (autoCenter == 0) return;

                Sheet sheet = (Sheet)drw.GetCurrentSheet();
                double sheetW = 0.0;
                if (sheet != null)
                {
                    double[] sProps = (double[])sheet.GetProperties2();
                    if (sProps != null && sProps.Length > 5)
                    {
                        sheetW = sProps[5];
                    }
                }

                // The Material cell (Графа 3 по ГОСТ 2.104) is in the main title block (Форма 1):
                // Bottom border of stamp: 5 mm from sheet bottom (Y = 0.005 m).
                // Material cell height: 15 mm (from Y = 0.005 to Y = 0.020).
                // For two-line fraction (<STACK>...</STACK>): optimal anchor Y is 0.0193 m (19.3 mm).
                // For single-line material: optimal anchor Y is 0.0152 m (15.2 mm) for perfect dead-center alignment.
                // Horizontal center: 90 mm from sheet right edge (sheetW - 0.090 m).
                double targetCenterX = sheetW > 0.15 ? (sheetW - 0.090) : 0.0;

                View v = (View)drw.GetFirstView();
                while (v != null)
                {
                    Note n = (Note)v.GetFirstNote();
                    while (n != null)
                    {
                        string name = n.GetName() ?? "";
                        string ltxt = n.PropertyLinkedText ?? "";
                        string txt = n.GetText() ?? "";

                        bool isMatNote = false;
                        if (name.Equals("MYPRP16", StringComparison.OrdinalIgnoreCase))
                        {
                            isMatNote = true;
                        }
                        else if (ltxt.IndexOf("Материал_ФБ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 ltxt.IndexOf("$PRPSHEET:\"Материал", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (ltxt.IndexOf("Материал", StringComparison.OrdinalIgnoreCase) >= 0 && ltxt.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            Annotation annTest = (Annotation)n.GetAnnotation();
                            if (annTest != null)
                            {
                                double[] p = (double[])annTest.GetPosition();
                                if (p != null && p.Length >= 2 && p[1] > 0.003 && p[1] < 0.035)
                                {
                                    isMatNote = true;
                                }
                            }
                        }

                        if (isMatNote)
                        {
                            Annotation ann = (Annotation)n.GetAnnotation();
                            if (ann != null)
                            {
                                double[] pos = (double[])ann.GetPosition();
                                if (pos != null && pos.Length >= 2 && pos[1] > 0.003 && pos[1] < 0.035)
                                {
                                    // Determine whether the material is a two-line fraction or a single line
                                    bool isFraction = txt.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                     txt.IndexOf("<OVER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                     ltxt.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                     ltxt.IndexOf("<OVER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                     txt.IndexOf('\n') >= 0 ||
                                                     txt.IndexOf('\r') >= 0;

                                    if (!isFraction)
                                    {
                                        try
                                        {
                                            View curV = (View)drw.GetFirstView();
                                            while (curV != null)
                                            {
                                                ModelDoc2 refDoc = (ModelDoc2)curV.ReferencedDocument;
                                                if (refDoc != null)
                                                {
                                                    // Check global custom properties
                                                    CustomPropertyManager cpm = refDoc.Extension.get_CustomPropertyManager("");
                                                    string v1, r1;
                                                    cpm.Get4("Материал_ФБ", false, out v1, out r1);
                                                    if (!string.IsNullOrEmpty(r1) && (r1.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0 || r1.IndexOf("<OVER", StringComparison.OrdinalIgnoreCase) >= 0))
                                                    {
                                                        isFraction = true;
                                                        break;
                                                    }
                                                    cpm.Get4("Материал", false, out v1, out r1);
                                                    if (!string.IsNullOrEmpty(r1) && (r1.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0 || r1.IndexOf("<OVER", StringComparison.OrdinalIgnoreCase) >= 0))
                                                    {
                                                        isFraction = true;
                                                        break;
                                                    }

                                                    // Check configuration-specific custom properties
                                                    string[] cfgs = (string[])refDoc.GetConfigurationNames();
                                                    if (cfgs != null)
                                                    {
                                                        foreach (string cfg in cfgs)
                                                        {
                                                            CustomPropertyManager cpmCfg = refDoc.Extension.get_CustomPropertyManager(cfg);
                                                            if (cpmCfg != null)
                                                            {
                                                                cpmCfg.Get4("Материал_ФБ", false, out v1, out r1);
                                                                if (!string.IsNullOrEmpty(r1) && (r1.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0 || r1.IndexOf("<OVER", StringComparison.OrdinalIgnoreCase) >= 0))
                                                                {
                                                                    isFraction = true;
                                                                    break;
                                                                }
                                                                cpmCfg.Get4("Материал", false, out v1, out r1);
                                                                if (!string.IsNullOrEmpty(r1) && (r1.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0 || r1.IndexOf("<OVER", StringComparison.OrdinalIgnoreCase) >= 0))
                                                                {
                                                                    isFraction = true;
                                                                    break;
                                                                }
                                                            }
                                                        }
                                                        if (isFraction) break;
                                                    }
                                                }
                                                curV = (View)curV.GetNextView();
                                            }
                                        }
                                        catch { }
                                    }

                                    // Dynamic extent-based centering
                                    double[] ext = (double[])n.GetExtent();
                                    double targetX = (targetCenterX > 0.05) ? targetCenterX : pos[0];
                                    double targetY = pos[1];

                                    bool hasValidExtent = ext != null && ext.Length >= 6 && (ext[4] - ext[1]) > 0.002;

                                    if (hasValidExtent)
                                    {
                                        // Vertical center of Cell 3 in GOST 2.104 title block (between 5 mm and 20 mm) is 12.5 mm
                                        double currentCenterY = (ext[1] + ext[4]) / 2.0;
                                        double deltaY = 0.0125 - currentCenterY;
                                        targetY = pos[1] + deltaY;

                                        // Safety bounds:
                                        if (isFraction && targetY < 0.0175)
                                        {
                                            targetY = 0.0185;
                                        }
                                    }
                                    else
                                    {
                                        if (isFraction)
                                        {
                                            targetY = 0.0193;
                                        }
                                        else
                                        {
                                            if (!string.IsNullOrEmpty(txt) && txt.Trim().Length > 0)
                                            {
                                                targetY = 0.0152;
                                            }
                                            else
                                            {
                                                if (pos[1] < 0.0140) targetY = 0.0187;
                                                else targetY = pos[1];
                                            }
                                        }
                                    }

                                    if (Math.Abs(pos[1] - targetY) > 0.00015 || Math.Abs(pos[0] - targetX) > 0.0005)
                                    {
                                        ann.SetPosition(targetX, targetY, pos.Length > 2 ? pos[2] : 0.0);
                                        n.SetTextJustification((int)swTextJustification_e.swTextJustificationCenter);
                                    }
                                }
                            }
                            break;
                        }
                        n = (Note)n.GetNext();
                    }
                    v = (View)v.GetNextView();
                }
            }
            catch { }
        }

        public static MaterialSyncResult SyncPart(PartDoc part, ISldWorks swApp, bool force = false, string targetFileName = null)
        {
            MaterialSyncResult result = new MaterialSyncResult();
            if (part == null || swApp == null)
            {
                result.Success = false;
                result.Message = "Null part or swApp";
                return result;
            }

            ModelDoc2 model = (ModelDoc2)part;
            string docTitle = model.GetTitle();

            Configuration activeConfig = (Configuration)model.GetActiveConfiguration();
            string configName = activeConfig != null ? activeConfig.Name : "";
            string cacheKey = docTitle + "::" + configName;

            ApplyUserSettings(model, configName, result, targetFileName);

            string dbName = "";
            string matName = part.GetMaterialPropertyName2(configName, out dbName);

            if (string.IsNullOrEmpty(matName) ||
                matName.Equals("Материал <не указан>", StringComparison.OrdinalIgnoreCase) ||
                matName.Equals("<не указан>", StringComparison.OrdinalIgnoreCase) ||
                matName.Contains("$PRP") ||
                matName.Contains("$PRPWLD"))
            {
                result.Success = true;
                result.Message = "Реквизиты и масса обновлены, материал не указан";
                return result;
            }

            result.MaterialName = matName;

            string cacheValue = matName;
            if (!force && LastProcessedCache.ContainsKey(cacheKey) && LastProcessedCache[cacheKey] == cacheValue)
            {
                result.Success = true;
                result.Cached = true;
                result.Message = "Up-to-date (cached)";
                return result;
            }

            string xmlSortament = null;
            string xmlGostSortament = null;
            string xmlGostMaterial = null;
            string xmlMaterialFB = null;
            string xmlMaterialSP = null;

            TryReadXmlProperties(swApp, matName, dbName, 
                out xmlSortament, out xmlGostSortament, out xmlGostMaterial, out xmlMaterialFB, out xmlMaterialSP);

            string sortament = "";
            string gostSortament = "";
            string gostMaterial = "";
            string materialFB = "";
            string materialSP = "";

            if (!string.IsNullOrEmpty(xmlMaterialFB))
            {
                materialFB = xmlMaterialFB;
                materialSP = !string.IsNullOrEmpty(xmlMaterialSP) ? xmlMaterialSP : matName;
                sortament = xmlSortament ?? "";
                gostSortament = xmlGostSortament ?? "";
                gostMaterial = xmlGostMaterial ?? "";
            }
            else if (matName.Contains("/") || matName.Contains(" / "))
            {
                string[] parts = matName.Split(new char[] { '/' }, 2);
                string top = parts[0].Trim();
                string bottom = parts[1].Trim();

                materialFB = string.Format("<STACK size=1>{0}<OVER>{1}</STACK>", top, bottom);
                materialSP = string.Format("{0} / {1}", top, bottom);

                sortament = top;
                gostSortament = ExtractGost(top);
                gostMaterial = ExtractGost(bottom);
            }
            else
            {
                materialFB = matName;
                materialSP = matName;
                sortament = "";
                gostSortament = "";
                gostMaterial = ExtractGost(matName);
            }

            result.MaterialFB = materialFB;
            result.MaterialSP = materialSP;
            result.Sortament = sortament;
            result.GostSortament = gostSortament;
            result.GostMaterial = gostMaterial;

            WriteModelProperties(model, configName, materialFB, materialSP, sortament, gostSortament, gostMaterial);

            LastProcessedCache[cacheKey] = cacheValue;
            result.Success = true;
            result.Message = "Синхронизировано: " + matName;
            return result;
        }

        public static void ApplyUserSettings(ModelDoc2 model, string configName, MaterialSyncResult result, string targetFileName = null)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeySettings))
                {
                    if (key != null)
                    {
                        string author = key.GetValue("Author") as string;
                        string checker = key.GetValue("Checker") as string;
                        string org = key.GetValue("Organization") as string;
                        int autoMass = (int)key.GetValue("AutoMass", 1);
                        int decimals = (int)key.GetValue("MassDecimals", 2);
                        int autoSplitName = (int)key.GetValue("AutoSplitName", 1);

                        if (autoSplitName == 1)
                        {
                            ApplyFileNameDesignationAndTitle(model, targetFileName);
                        }

                        string dateRu = DateTime.Now.ToString("dd.MM.yy");
                        string dateIso = DateTime.Now.ToString("yyyy-MM-dd");

                        CustomPropertyManager cpmGen = model.Extension.get_CustomPropertyManager("");
                        if (cpmGen != null)
                        {
                            if (!string.IsNullOrEmpty(author))
                            {
                                SetProp(cpmGen, "Разраб.", author);
                                SetProp(cpmGen, "Разработал", author);
                                SetProp(cpmGen, "Конструктор", author);
                                SetProp(cpmGen, "Автор", author);
                                SetProp(cpmGen, "п_Разраб", author);
                                SetProp(cpmGen, "DrawnBy", author);
                                SetProp(cpmGen, "п_Разраб_Дата", dateRu);
                                SetProp(cpmGen, "DrawnDate", dateIso);
                            }
                            if (!string.IsNullOrEmpty(checker))
                            {
                                SetProp(cpmGen, "Пров.", checker);
                                SetProp(cpmGen, "Проверил", checker);
                                SetProp(cpmGen, "п_Пров", checker);
                                SetProp(cpmGen, "CheckedBy", checker);
                                SetProp(cpmGen, "п_Пров_Дата", dateRu);
                            }
                            if (!string.IsNullOrEmpty(org))
                            {
                                SetProp(cpmGen, "Контора", org);
                                SetProp(cpmGen, "Организация", org);
                                SetProp(cpmGen, "Организация_ФБ", org);
                                SetProp(cpmGen, "Компания", org);
                            }
                        }

                        if (!string.IsNullOrEmpty(configName))
                        {
                            CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(configName);
                            if (cpmCfg != null)
                            {
                                if (!string.IsNullOrEmpty(author))
                                {
                                    SetProp(cpmCfg, "Разраб.", author);
                                    SetProp(cpmCfg, "Разработал", author);
                                    SetProp(cpmCfg, "Конструктор", author);
                                    SetProp(cpmCfg, "Автор", author);
                                    SetProp(cpmCfg, "п_Разраб", author);
                                    SetProp(cpmCfg, "DrawnBy", author);
                                    SetProp(cpmCfg, "п_Разраб_Дата", dateRu);
                                    SetProp(cpmCfg, "DrawnDate", dateIso);
                                }
                                if (!string.IsNullOrEmpty(checker))
                                {
                                    SetProp(cpmCfg, "Пров.", checker);
                                    SetProp(cpmCfg, "Проверил", checker);
                                    SetProp(cpmCfg, "п_Пров", checker);
                                    SetProp(cpmCfg, "CheckedBy", checker);
                                    SetProp(cpmCfg, "п_Пров_Дата", dateRu);
                                }
                                if (!string.IsNullOrEmpty(org))
                                {
                                    SetProp(cpmCfg, "Контора", org);
                                    SetProp(cpmCfg, "Организация", org);
                                    SetProp(cpmCfg, "Организация_ФБ", org);
                                    SetProp(cpmCfg, "Компания", org);
                                }
                            }
                        }

                        if (autoMass == 1 && model.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                        {
                            CalculateAndSetMass(model, configName, decimals, result);
                        }
                    }
                }
            }
            catch { }
        }

        private static void CalculateAndSetMass(ModelDoc2 model, string configName, int decimals, MaterialSyncResult result)
        {
            try
            {
                MassProperty massProp = (MassProperty)model.Extension.CreateMassProperty();
                if (massProp != null)
                {
                    double massKg = massProp.Mass;
                    if (massKg > 0.00001)
                    {
                        if (result != null) result.MassKg = massKg;

                        string formatStr = "0." + new string('#', decimals);
                        string massStr = massKg.ToString(formatStr, RuCulture);

                        CustomPropertyManager cpmGen = model.Extension.get_CustomPropertyManager("");
                        SetProp(cpmGen, "Масса_ФБ", massStr);
                        SetProp(cpmGen, "Масса", massStr);

                        if (!string.IsNullOrEmpty(configName))
                        {
                            CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(configName);
                            if (cpmCfg != null)
                            {
                                SetProp(cpmCfg, "Масса_ФБ", massStr);
                                SetProp(cpmCfg, "Масса", massStr);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void TryReadXmlProperties(ISldWorks swApp, string matName, string dbName,
            out string sortament, out string gostSort, out string gostMat, out string matFB, out string matSP)
        {
            sortament = null;
            gostSort = null;
            gostMat = null;
            matFB = null;
            matSP = null;

            try
            {
                object databasesObj = swApp.GetMaterialDatabases();
                string[] dbs = databasesObj as string[];
                if (dbs == null) return;

                string targetDbPath = null;
                foreach (string dbPath in dbs)
                {
                    string fname = Path.GetFileNameWithoutExtension(dbPath);
                    if (!string.IsNullOrEmpty(dbName) && fname.Equals(dbName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetDbPath = dbPath;
                        break;
                    }
                }

                if (targetDbPath == null)
                {
                    foreach (string dbPath in dbs)
                    {
                        if (File.Exists(dbPath))
                        {
                            try
                            {
                                XmlDocument doc = new XmlDocument();
                                doc.Load(dbPath);
                                XmlNode node = doc.SelectSingleNode("//material[@name=\"" + matName + "\"]");
                                if (node != null)
                                {
                                    targetDbPath = dbPath;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (targetDbPath != null && File.Exists(targetDbPath))
                {
                    XmlDocument xml = new XmlDocument();
                    xml.Load(targetDbPath);
                    XmlNode matNode = xml.SelectSingleNode("//material[@name=\"" + matName + "\"]");
                    if (matNode != null)
                    {
                        XmlNode customNode = matNode.SelectSingleNode("custom");
                        if (customNode != null)
                        {
                            foreach (XmlNode propNode in customNode.SelectNodes("prop"))
                            {
                                if (propNode.Attributes != null && propNode.Attributes["name"] != null && propNode.Attributes["value"] != null)
                                {
                                    string pName = propNode.Attributes["name"].Value;
                                    string pVal = propNode.Attributes["value"].Value;

                                    if (pName == "Сортамент") sortament = pVal;
                                    else if (pName == "ГОСТ_Сортамент") gostSort = pVal;
                                    else if (pName == "ГОСТ_Материал") gostMat = pVal;
                                    else if (pName == "Обозначение_ГОСТ") matFB = pVal;
                                    else if (pName == "Обозначение_Строка") matSP = pVal;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void WriteModelProperties(ModelDoc2 model, string configName, 
            string matFB, string matSP, string sortament, string gostSort, string gostMat)
        {
            CustomPropertyManager cpmGen = model.Extension.get_CustomPropertyManager("");
            SetProp(cpmGen, "Материал_ФБ", matFB);
            SetProp(cpmGen, "Материал", matSP);

            if (!string.IsNullOrEmpty(sortament))
                SetProp(cpmGen, "Сортамент", sortament);
            else
                DeleteProp(cpmGen, "Сортамент");

            if (!string.IsNullOrEmpty(gostSort))
                SetProp(cpmGen, "ГОСТ_Сортамент", gostSort);

            if (!string.IsNullOrEmpty(gostMat))
                SetProp(cpmGen, "ГОСТ_Материал", gostMat);

            if (!string.IsNullOrEmpty(configName))
            {
                CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(configName);
                if (cpmCfg != null)
                {
                    SetProp(cpmCfg, "Материал_ФБ", matFB);
                    SetProp(cpmCfg, "Материал", matSP);

                    if (!string.IsNullOrEmpty(sortament))
                        SetProp(cpmCfg, "Сортамент", sortament);
                    else
                        DeleteProp(cpmCfg, "Сортамент");

                    if (!string.IsNullOrEmpty(gostSort))
                        SetProp(cpmCfg, "ГОСТ_Сортамент", gostSort);

                    if (!string.IsNullOrEmpty(gostMat))
                        SetProp(cpmCfg, "ГОСТ_Материал", gostMat);
                }
            }
        }

        private static void SetProp(CustomPropertyManager cpm, string name, string value)
        {
            try
            {
                cpm.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                cpm.Set2(name, value);
            }
            catch { }
        }

        private static void DeleteProp(CustomPropertyManager cpm, string name)
        {
            try { cpm.Delete2(name); } catch { }
        }

        private static string ExtractGost(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            Match m = Regex.Match(text, @"(ГОСТ|ТУ|ОСТ)\s*[\w\.\-]+", RegexOptions.IgnoreCase);
            return m.Success ? m.Value : "";
        }

        public static string CleanDocumentName(string pathOrTitle)
        {
            if (string.IsNullOrWhiteSpace(pathOrTitle)) return "";
            string name = Path.GetFileName(pathOrTitle.Trim());
            if (string.IsNullOrWhiteSpace(name)) return "";

            // Remove known SolidWorks extensions
            name = Regex.Replace(name, @"\.(sldprt|sldasm|slddrw|prt|asm|drw)$", "", RegexOptions.IgnoreCase).Trim();
            return name;
        }

        public static void ApplyFileNameDesignationAndTitle(ModelDoc2 model, string targetFileName = null)
        {
            if (model == null) return;
            try
            {
                string rawName = targetFileName;
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    rawName = model.GetPathName();
                }
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    rawName = model.GetTitle();
                }
                if (string.IsNullOrWhiteSpace(rawName)) return;

                string baseName = CleanDocumentName(rawName);
                if (string.IsNullOrWhiteSpace(baseName)) return;

                // Skip default unsaved template names like "Деталь1", "Деталь 1", "Part1", "Part 1", "Сборка1", "Assem1", "Чертеж1", "Draw1"
                if (Regex.IsMatch(baseName, @"^(Деталь|Part|Сборка|Assem|Чертеж|Draw)\s*\d*$", RegexOptions.IgnoreCase))
                {
                    return;
                }

                string designation = "";
                string title = "";

                int spaceIdx = baseName.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    designation = baseName.Substring(0, spaceIdx).Trim();
                    title = baseName.Substring(spaceIdx + 1).Trim();
                }
                else
                {
                    // No space: if it contains digits or dots, treat as designation, otherwise title
                    if (Regex.IsMatch(baseName, @"\d"))
                        designation = baseName;
                    else
                        title = baseName;
                }

                // 1. General custom properties (for $PRPSHEET and $PRP)
                CustomPropertyManager cpmGen = model.Extension.get_CustomPropertyManager("");
                if (cpmGen != null)
                {
                    if (!string.IsNullOrEmpty(designation))
                    {
                        SetProp(cpmGen, "Обозначение", designation);
                        SetProp(cpmGen, "PartNo", designation);
                        SetProp(cpmGen, "Number", designation);
                    }
                    if (!string.IsNullOrEmpty(title))
                    {
                        SetProp(cpmGen, "Наименование", title);
                        SetProp(cpmGen, "Наименование_ФБ", title);
                        SetProp(cpmGen, "Description", title);
                    }
                }

                // 2. All configurations (ensures configuration-specific tables and drawings resolve correctly)
                try
                {
                    string[] cfgNames = model.GetConfigurationNames() as string[];
                    if (cfgNames != null)
                    {
                        foreach (string cfg in cfgNames)
                        {
                            if (string.IsNullOrEmpty(cfg)) continue;
                            CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(cfg);
                            if (cpmCfg != null)
                            {
                                if (!string.IsNullOrEmpty(designation))
                                {
                                    SetProp(cpmCfg, "Обозначение", designation);
                                    SetProp(cpmCfg, "PartNo", designation);
                                    SetProp(cpmCfg, "Number", designation);
                                }
                                if (!string.IsNullOrEmpty(title))
                                {
                                    SetProp(cpmCfg, "Наименование", title);
                                    SetProp(cpmCfg, "Наименование_ФБ", title);
                                    SetProp(cpmCfg, "Description", title);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }
    }
}
