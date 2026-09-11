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

        public static bool IsServiceEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeySettings))
                {
                    if (key != null)
                    {
                        int val = (int)key.GetValue("ServiceEnabled", 1);
                        return val == 1;
                    }
                }
            }
            catch { }
            return true;
        }

        public static bool IsAutoSyncMaterialsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeySettings))
                {
                    if (key != null)
                    {
                        int val = (int)key.GetValue("AutoSyncMaterials", 1);
                        return val == 1;
                    }
                }
            }
            catch { }
            return true;
        }

        private static readonly string[] EskdBlankShapes = new string[] {
            "Лист", "Труба", "Круг", "Швеллер", "Уголок", "Полоса", 
            "Квадрат", "Шестигранник", "Профиль", "Лента", "Двутавр", 
            "Проволока", "Пруток", "Фольга", "Плита", "Сетка", "Рельс"
        };

        public static string FormatEskdMaterialFB(string top, string bottom, out string detectedShape, out string sortamentOnly)
        {
            detectedShape = "";
            sortamentOnly = top ?? "";

            if (string.IsNullOrWhiteSpace(top) || string.IsNullOrWhiteSpace(bottom))
            {
                return (top ?? "") + (bottom ?? "");
            }

            top = top.Trim();
            bottom = bottom.Trim();

            foreach (string s in EskdBlankShapes)
            {
                if (top.StartsWith(s + " ", StringComparison.OrdinalIgnoreCase))
                {
                    detectedShape = top.Substring(0, s.Length).Trim();
                    sortamentOnly = top.Substring(s.Length).Trim();
                    break;
                }
                else if (top.Equals(s, StringComparison.OrdinalIgnoreCase))
                {
                    detectedShape = s;
                    sortamentOnly = "";
                    break;
                }
            }

            // In SolidWorks drawings, the blank shape (Лист, Труба etc.) MUST be inside the numerator of the fraction.
            // When outside <STACK>, SolidWorks baseline-aligns it to the denominator, causing it to sit awkwardly at the bottom.
            // Scale fraction font to fit 15mm title block cell per SWPlus / GOST standard.
            // Guaranteed leading space ensures MProp InStr("<") == 2, Left$(strTemp, 0) == "" (never crashes with Left$(..., -1)).
            return string.Format(" <FONT size=1.8><FONT size=3.5><STACK size=1>{0}<OVER>{1}</STACK>", top, bottom);
        }

        public static string FormatEskdTitleFB(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            return title.Trim();
        }

        public static string FormatEskdMassFB(string mass)
        {
            if (string.IsNullOrWhiteSpace(mass)) return "";
            return mass.Trim();
        }

        public static string NormalizeMaterialFB(string matFB, string fallbackTop, string fallbackBottom, out string detectedShape, out string sortamentOnly)
        {
            detectedShape = "";
            sortamentOnly = fallbackTop ?? "";

            if (string.IsNullOrWhiteSpace(matFB))
            {
                if (!string.IsNullOrEmpty(fallbackTop) && !string.IsNullOrEmpty(fallbackBottom))
                    return FormatEskdMaterialFB(fallbackTop, fallbackBottom, out detectedShape, out sortamentOnly);
                return "";
            }

            matFB = matFB.Trim();

            if (matFB.IndexOf("<OVER>", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (matFB.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    matFB.IndexOf("<FONT", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return " " + matFB.TrimStart();
                }

                int overIdx = matFB.IndexOf("<OVER>", StringComparison.OrdinalIgnoreCase);
                int stackOpen = matFB.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase);
                int stackClose = matFB.IndexOf("</STACK>", StringComparison.OrdinalIgnoreCase);

                string prefixShape = "";
                if (stackOpen > 0)
                {
                    prefixShape = matFB.Substring(0, stackOpen).Trim();
                }

                int topStart = -1;
                if (stackOpen >= 0)
                {
                    int tagClose = matFB.IndexOf('>', stackOpen);
                    if (tagClose >= 0 && tagClose < overIdx)
                    {
                        topStart = tagClose + 1;
                    }
                }
                else
                {
                    topStart = 0;
                }

                string top = (topStart >= 0 && topStart < overIdx) ? matFB.Substring(topStart, overIdx - topStart).Trim() : (fallbackTop ?? "");
                int botStart = overIdx + 6; // len("<OVER>")
                int botEnd = (stackClose >= 0 && stackClose > botStart) ? stackClose : matFB.Length;
                string bottom = (botEnd > botStart) ? matFB.Substring(botStart, botEnd - botStart).Trim() : (fallbackBottom ?? "");

                string fullTop = !string.IsNullOrEmpty(prefixShape) ? (prefixShape + " " + top).Trim() : top;
                return FormatEskdMaterialFB(fullTop, bottom, out detectedShape, out sortamentOnly);
            }
            else if (matFB.StartsWith("<"))
            {
                return " " + matFB;
            }

            return matFB;
        }

        public static void SyncModelProperties(ModelDoc2 model, ISldWorks swApp, bool force = true, bool triggerRebuild = true, string targetFileName = null)
        {
            if (model == null || swApp == null) return;
            if (!force && !IsServiceEnabled()) return;
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

                // Do NOT mutate referenced 3D models during drawing save/sync!
                // Mutating 3D models marks them dirty, alters their properties in memory,
                // and causes drawing views and annotations to shift on save.
                // The official .slddrt templates already have exact, calibrated coordinates per GOST 2.104.
                // Template coordinates and table layouts are preserved strictly as authored.
            }
            catch { }
        }

        private static bool IsSpecificationForm2(DrawingDoc drw, Sheet sheet)
        {
            if (sheet == null) return false;
            try
            {
                string sName = sheet.GetName() ?? "";
                string tmpl = sheet.GetTemplateName() ?? "";

                // 1. Check template or sheet name keywords
                if (tmpl.IndexOf("SP-1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("SP_1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("СП-1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("СП_1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("GSP-1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("GSP_1", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (Regex.IsMatch(sName, @"^(SP|СП|Спец|Spec)\s*1?$", RegexOptions.IgnoreCase))
                {
                    return true;
                }

                // 2. Check notes on sheet: Form 2 has MYPRP19 (designation 120x15 mm at top of 40mm stamp)
                // and DOES NOT have Scale / MYPRP15 / MYPRP16.
                bool hasMyPrp19 = false;
                bool hasScaleOrMass = false;

                View v = (View)drw.GetFirstView();
                while (v != null)
                {
                    Note n = (Note)v.GetFirstNote();
                    while (n != null)
                    {
                        string nName = n.GetName() ?? "";
                        if (nName.Equals("MYPRP19", StringComparison.OrdinalIgnoreCase))
                        {
                            hasMyPrp19 = true;
                        }
                        if (nName.Equals("Scale", StringComparison.OrdinalIgnoreCase) ||
                            nName.Equals("MYPRP15", StringComparison.OrdinalIgnoreCase) ||
                            nName.Equals("MYPRP16", StringComparison.OrdinalIgnoreCase))
                        {
                            hasScaleOrMass = true;
                        }
                        n = (Note)n.GetNext();
                    }
                    v = (View)v.GetNextView();
                }

                if (hasMyPrp19 && !hasScaleOrMass)
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsSubsequentSheetForm2a(DrawingDoc drw, Sheet sheet)
        {
            if (sheet == null) return false;
            try
            {
                string sName = sheet.GetName() ?? "";
                string tmpl = sheet.GetTemplateName() ?? "";

                if (tmpl.IndexOf("SP-2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("SP_2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("СП-2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("СП_2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("GSP-2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("GSP_2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("A4-2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("A3-2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmpl.IndexOf("_2.slddrt", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (Regex.IsMatch(sName, @"^(SP|СП|Спец|Spec)\s*[2-9]\d*$", RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static void RestoreDrawingStampTemplate(DrawingDoc drw)
        {
            // No-op: template coordinates and layout are strictly preserved from GOST 2.104 templates
        }

        public static void AlignDrawingMassNote(DrawingDoc drw)
        {
            // No-op: mass note position is strictly preserved from drawing template
        }

        public static void AlignDrawingMaterialNote(DrawingDoc drw)
        {
            // No-op: material note position is strictly preserved from drawing template
        }

        public static void AlignDrawingTitleNote(DrawingDoc drw)
        {
            // No-op: title note position is strictly preserved from drawing template
        }

        public static void AlignSpecificationTableColumns(DrawingDoc drw)
        {
            // No-op: table columns and widths are preserved strictly as authored
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

            if (!force && !IsServiceEnabled())
            {
                result.Success = true;
                result.Message = "Служба ЕСКД отключена в настройках";
                return result;
            }

            ModelDoc2 model = (ModelDoc2)part;
            string docTitle = model.GetTitle();

            Configuration activeConfig = (Configuration)model.GetActiveConfiguration();
            string activeConfigName = activeConfig != null ? activeConfig.Name : "";

            ApplyUserSettings(model, activeConfigName, result, targetFileName);

            string[] cfgNames = model.GetConfigurationNames() as string[];
            if (cfgNames == null || cfgNames.Length == 0)
            {
                cfgNames = new string[] { activeConfigName };
            }

            string activeMatFB = "";
            string activeMatSP = "";
            string activeSortament = "";
            string activeGostSort = "";
            string activeGostMat = "";
            string activeMatName = "";
            int syncedCount = 0;

            foreach (string cfg in cfgNames)
            {
                if (string.IsNullOrEmpty(cfg)) continue;

                string dbName = "";
                string matName = part.GetMaterialPropertyName2(cfg, out dbName);

                // 1. If material is empty for this configuration, check parent config if derived
                if (string.IsNullOrEmpty(matName))
                {
                    try
                    {
                        Configuration cObj = (Configuration)model.GetConfigurationByName(cfg);
                        if (cObj != null && cObj.IsDerived())
                        {
                            Configuration parentCfg = (Configuration)cObj.GetParent();
                            if (parentCfg != null)
                            {
                                matName = part.GetMaterialPropertyName2(parentCfg.Name, out dbName);
                            }
                        }
                    }
                    catch { }
                }

                // 2. If still empty, check weldment sibling (e.g. "01<As Welded>" <-> "01<As Machined>")
                if (string.IsNullOrEmpty(matName) && cfg.EndsWith("<As Welded>", StringComparison.OrdinalIgnoreCase))
                {
                    string siblingCfg = cfg.Replace("<As Welded>", "<As Machined>");
                    matName = part.GetMaterialPropertyName2(siblingCfg, out dbName);
                }
                if (string.IsNullOrEmpty(matName) && cfg.EndsWith("<As Machined>", StringComparison.OrdinalIgnoreCase))
                {
                    string siblingCfg = cfg.Replace("<As Machined>", "<As Welded>");
                    matName = part.GetMaterialPropertyName2(siblingCfg, out dbName);
                }
                if (string.IsNullOrEmpty(matName) && cfg.Contains("<"))
                {
                    string baseCfg = cfg.Split('<')[0].Trim();
                    if (!string.IsNullOrEmpty(baseCfg) && !baseCfg.Equals(cfg, StringComparison.OrdinalIgnoreCase))
                    {
                        matName = part.GetMaterialPropertyName2(baseCfg, out dbName);
                    }
                }

                // 3. Fallback to active configuration or global part material
                if (string.IsNullOrEmpty(matName) && !string.IsNullOrEmpty(activeConfigName) && !cfg.Equals(activeConfigName, StringComparison.OrdinalIgnoreCase))
                {
                    matName = part.GetMaterialPropertyName2(activeConfigName, out dbName);
                }
                if (string.IsNullOrEmpty(matName))
                {
                    matName = part.GetMaterialPropertyName2("", out dbName);
                }

                if (string.IsNullOrEmpty(matName) ||
                    matName.Equals("Материал <не указан>", StringComparison.OrdinalIgnoreCase) ||
                    matName.Equals("<не указан>", StringComparison.OrdinalIgnoreCase) ||
                    matName.Contains("$PRP") ||
                    matName.Contains("$PRPWLD"))
                {
                    continue;
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
                string detShape = "";
                string sortamentPart = "";

                if (!string.IsNullOrEmpty(xmlMaterialFB))
                {
                    materialFB = NormalizeMaterialFB(xmlMaterialFB, xmlSortament, xmlGostMaterial, out detShape, out sortamentPart);
                    materialSP = !string.IsNullOrEmpty(xmlMaterialSP) ? xmlMaterialSP : matName;
                    sortament = !string.IsNullOrEmpty(sortamentPart) ? sortamentPart : (xmlSortament ?? "");
                    gostSortament = xmlGostSortament ?? "";
                    gostMaterial = xmlGostMaterial ?? "";
                }
                else if (matName.Contains("/") || matName.Contains(" / "))
                {
                    string[] parts = matName.Split(new char[] { '/' }, 2);
                    string top = parts[0].Trim();
                    string bottom = parts[1].Trim();

                    materialFB = FormatEskdMaterialFB(top, bottom, out detShape, out sortamentPart);
                    materialSP = string.Format("{0} / {1}", top, bottom);

                    sortament = !string.IsNullOrEmpty(sortamentPart) ? sortamentPart : top;
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

                CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(cfg);
                if (cpmCfg != null)
                {
                    SetProp(cpmCfg, "Материал_ФБ", materialFB);
                    SetProp(cpmCfg, "Материал_Таблица", materialFB);
                    SetProp(cpmCfg, "Материал", materialSP);

                    if (!string.IsNullOrEmpty(sortament))
                        SetProp(cpmCfg, "Сортамент", sortament);
                    else
                        DeleteProp(cpmCfg, "Сортамент");

                    if (!string.IsNullOrEmpty(gostSortament))
                        SetProp(cpmCfg, "ГОСТ_Сортамент", gostSortament);

                    if (!string.IsNullOrEmpty(gostMaterial))
                        SetProp(cpmCfg, "ГОСТ_Материал", gostMaterial);
                }

                syncedCount++;

                if (cfg.Equals(activeConfigName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(activeMatFB))
                {
                    activeMatName = matName;
                    activeMatFB = materialFB;
                    activeMatSP = materialSP;
                    activeSortament = sortament;
                    activeGostSort = gostSortament;
                    activeGostMat = gostMaterial;
                }
            }

            if (!string.IsNullOrEmpty(activeMatFB))
            {
                CustomPropertyManager cpmGen = model.Extension.get_CustomPropertyManager("");
                if (cpmGen != null)
                {
                    SetProp(cpmGen, "Материал_ФБ", activeMatFB);
                    SetProp(cpmGen, "Материал_Таблица", activeMatFB);
                    SetProp(cpmGen, "Материал", activeMatSP);

                    if (!string.IsNullOrEmpty(activeSortament))
                        SetProp(cpmGen, "Сортамент", activeSortament);
                    else
                        DeleteProp(cpmGen, "Сортамент");

                    if (!string.IsNullOrEmpty(activeGostSort))
                        SetProp(cpmGen, "ГОСТ_Сортамент", activeGostSort);

                    if (!string.IsNullOrEmpty(activeGostMat))
                        SetProp(cpmGen, "ГОСТ_Материал", activeGostMat);
                }
            }

            result.Success = true;
            result.MaterialName = activeMatName;
            result.MaterialFB = activeMatFB;
            result.MaterialSP = activeMatSP;
            result.Sortament = activeSortament;
            result.GostSortament = activeGostSort;
            result.GostMaterial = activeGostMat;
            result.Message = "Синхронизировано конфигураций: " + syncedCount;
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
                CustomPropertyManager cpmGen = model.Extension.get_CustomPropertyManager("");
                string existingGenMass = GetProp(cpmGen, "Масса");
                string existingGenMassFB = GetProp(cpmGen, "Масса_ФБ");

                // If user or MProp already configured dynamic SW-Mass or custom formatted mass, preserve it strictly!
                bool hasDynamicMass = (!string.IsNullOrEmpty(existingGenMass) && (existingGenMass.Contains("SW-Mass") || existingGenMass.Contains("<FONT") || existingGenMass.Contains("\n"))) ||
                                      (!string.IsNullOrEmpty(existingGenMassFB) && (existingGenMassFB.Contains("SW-Mass") || existingGenMassFB.Contains("<FONT") || existingGenMassFB.Contains("\n")));
                if (hasDynamicMass)
                {
                    return;
                }

                MassProperty massProp = (MassProperty)model.Extension.CreateMassProperty();
                if (massProp != null)
                {
                    double massKg = massProp.Mass;
                    if (massKg > 0.00001)
                    {
                        if (result != null) result.MassKg = massKg;

                        string formatStr = "0." + new string('#', decimals);
                        string massStr = massKg.ToString(formatStr, RuCulture);
                        string massFB = string.Format("<FONT size=1> \n<FONT size=3.5>{0}", massStr);

                        SetProp(cpmGen, "Масса_ФБ", massFB);
                        SetProp(cpmGen, "Масса", massFB);

                        if (!string.IsNullOrEmpty(configName))
                        {
                            CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(configName);
                            if (cpmCfg != null)
                            {
                                string existingCfgMass = GetProp(cpmCfg, "Масса");
                                if (string.IsNullOrEmpty(existingCfgMass) || (!existingCfgMass.Contains("SW-Mass") && !existingCfgMass.Contains("<FONT")))
                                {
                                    SetProp(cpmCfg, "Масса_ФБ", massFB);
                                    SetProp(cpmCfg, "Масса", massFB);
                                }
                            }
                        }
                        else
                        {
                            string[] allCfgs = (string[])model.GetConfigurationNames();
                            if (allCfgs != null)
                            {
                                foreach (string cfg in allCfgs)
                                {
                                    CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(cfg);
                                    if (cpmCfg != null)
                                    {
                                        string existingCfgMass = GetProp(cpmCfg, "Масса");
                                        if (string.IsNullOrEmpty(existingCfgMass) || (!existingCfgMass.Contains("SW-Mass") && !existingCfgMass.Contains("<FONT")))
                                        {
                                            SetProp(cpmCfg, "Масса_ФБ", massFB);
                                            SetProp(cpmCfg, "Масса", massFB);
                                        }
                                    }
                                }
                            }
                        }
                        CleanModelMassTags(model);
                    }
                }
            }
            catch { }
        }

        public static bool CleanModelMassTags(ModelDoc2 doc)
        {
            // Do NOT strip formatting tags: SWPlus MProp relies on <FONT> and newlines for title block vertical alignment
            return false;
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
            SetProp(cpmGen, "Материал_Таблица", matFB);
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
                    SetProp(cpmCfg, "Материал_Таблица", matFB);
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

        private static string GetProp(CustomPropertyManager cpm, string name)
        {
            if (cpm == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                string val = null, resVal = null;
                cpm.Get4(name, false, out val, out resVal);
                if (!string.IsNullOrWhiteSpace(resVal)) return resVal.Trim();
                if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
            }
            catch { }
            return null;
        }

        private static string StripExecutionSuffix(string desig, out string foundExec, out string docCode)
        {
            foundExec = null;
            docCode = "";
            if (string.IsNullOrWhiteSpace(desig)) return "";

            string trimmed = desig.Trim();

            // Check for document code like " СБ", " ГЧ", " МЧ", " ТУ", " ВО"
            Match codeMatch = Regex.Match(trimmed, @"(?:\s+|(?:(?<=[0-9])))(СБ|ГЧ|МЧ|ВО|ТУ|ТБ|ПЭ|Э\d|СХ|СЭ|ВП|СП)$", RegexOptions.IgnoreCase);
            if (codeMatch.Success)
            {
                docCode = " " + codeMatch.Groups[1].Value.ToUpper();
                trimmed = trimmed.Substring(0, codeMatch.Index).TrimEnd();
            }

            // Check for dash execution suffix like "-01", "-001", "-02"
            Match dashMatch = Regex.Match(trimmed, @"-(\d{1,4})$");
            if (dashMatch.Success)
            {
                foundExec = dashMatch.Groups[1].Value;
                trimmed = trimmed.Substring(0, dashMatch.Index).TrimEnd();
            }

            return trimmed;
        }

        private static bool ExtractExecutionFromConfigName(string configName, out string execCode, out bool isBaseConfig)
        {
            execCode = null;
            isBaseConfig = false;
            if (string.IsNullOrWhiteSpace(configName)) return false;

            string trimmed = configName.Trim();

            // Strip SolidWorks weldment / sheet metal suffixes like "<Как обработанный>", "<Как сварной>", or "SM-FLAT-PATTERN"
            trimmed = Regex.Replace(trimmed, @"<[^>]+>", "").Trim();
            trimmed = Regex.Replace(trimmed, @"[-_]?SM-FLAT-PATTERN.*$", "", RegexOptions.IgnoreCase).Trim();

            // 1. Base / Default configuration checks
            if (trimmed == "0" || trimmed == "00" || trimmed == "000" ||
                trimmed.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("по умолчанию", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("базовая", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("базовое", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("base", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("главная", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("main", StringComparison.OrdinalIgnoreCase))
            {
                isBaseConfig = true;
                execCode = "";
                return true;
            }

            // 2. Pure number: "01", "02", "1", "10", etc.
            Match mNum = Regex.Match(trimmed, @"^(\d{1,4})$");
            if (mNum.Success)
            {
                int n = 0;
                int.TryParse(mNum.Groups[1].Value, out n);
                if (n == 0)
                {
                    isBaseConfig = true;
                    execCode = "";
                }
                else
                {
                    execCode = mNum.Groups[1].Value.Length == 1 ? ("0" + mNum.Groups[1].Value) : mNum.Groups[1].Value;
                }
                return true;
            }

            // 3. Leading dash: "-01", "-02", "-1", etc.
            Match mDash = Regex.Match(trimmed, @"^-(\d{1,4})$");
            if (mDash.Success)
            {
                int n = 0;
                int.TryParse(mDash.Groups[1].Value, out n);
                if (n == 0)
                {
                    isBaseConfig = true;
                    execCode = "";
                }
                else
                {
                    execCode = mDash.Groups[1].Value.Length == 1 ? ("0" + mDash.Groups[1].Value) : mDash.Groups[1].Value;
                }
                return true;
            }

            // 4. Prefix or embedded "исп" or "исполнение": "исп. 01", "исп.01", "исп 01", "исполнение 1", "Кронштейн исп. 01"
            Match mIsp = Regex.Match(trimmed, @"(?:исп\.?|исполнение)\s*[-_]?\s*(\d{1,4})", RegexOptions.IgnoreCase);
            if (mIsp.Success)
            {
                int n = 0;
                int.TryParse(mIsp.Groups[1].Value, out n);
                if (n == 0)
                {
                    isBaseConfig = true;
                    execCode = "";
                }
                else
                {
                    execCode = mIsp.Groups[1].Value.Length == 1 ? ("0" + mIsp.Groups[1].Value) : mIsp.Groups[1].Value;
                }
                return true;
            }

            // 5. Leading number followed by description: "01 - Оцинкованная", "01_L=100", "01 (Черная)"
            Match mPrefix = Regex.Match(trimmed, @"^(\d{1,4})\s*[-_ \(]");
            if (mPrefix.Success)
            {
                int n = 0;
                int.TryParse(mPrefix.Groups[1].Value, out n);
                if (n == 0)
                {
                    isBaseConfig = true;
                    execCode = "";
                }
                else
                {
                    execCode = mPrefix.Groups[1].Value.Length == 1 ? ("0" + mPrefix.Groups[1].Value) : mPrefix.Groups[1].Value;
                }
                return true;
            }

            // 6. Trailing dash number: "Кронштейн-01", "L100-02"
            Match mSuffix = Regex.Match(trimmed, @"[-_](\d{1,4})$");
            if (mSuffix.Success)
            {
                int n = 0;
                int.TryParse(mSuffix.Groups[1].Value, out n);
                if (n == 0)
                {
                    isBaseConfig = true;
                    execCode = "";
                }
                else
                {
                    execCode = mSuffix.Groups[1].Value.Length == 1 ? ("0" + mSuffix.Groups[1].Value) : mSuffix.Groups[1].Value;
                }
                return true;
            }

            return false;
        }

        private static string BuildExecutionDesignation(string rootBaseDesig, string execCode, string docCode)
        {
            if (string.IsNullOrWhiteSpace(rootBaseDesig)) return "";
            if (string.IsNullOrWhiteSpace(execCode))
            {
                return !string.IsNullOrEmpty(docCode) ? (rootBaseDesig + docCode) : rootBaseDesig;
            }
            string desigWithExec = rootBaseDesig + "-" + execCode;
            return !string.IsNullOrEmpty(docCode) ? (desigWithExec + docCode) : desigWithExec;
        }

        public static string CleanDocumentName(string pathOrTitle)
        {
            if (string.IsNullOrWhiteSpace(pathOrTitle)) return "";
            string name = Path.GetFileName(pathOrTitle.Trim());
            if (string.IsNullOrWhiteSpace(name)) return "";

            // Remove known SolidWorks extensions
            name = Regex.Replace(name, @"\.(sldprt|sldasm|slddrw|prt|asm|drw)$", "", RegexOptions.IgnoreCase).Trim();
            // Strip drawing sheet suffixes if present (e.g. " - Лист1", " - Sheet1")
            name = Regex.Replace(name, @"\s*-\s*(Лист|Sheet)\s*\d*$", "", RegexOptions.IgnoreCase).Trim();
            return name;
        }

        public static void ApplyFileNameDesignationAndTitle(ModelDoc2 model, string targetFileName = null)
        {
            if (model == null) return;
            try
            {
                int docType = model.GetType();
                bool isAssembly = docType == (int)swDocumentTypes_e.swDocASSEMBLY;

                // 1. Determine base designation and title
                CustomPropertyManager cpmGen = model.Extension.get_CustomPropertyManager("");
                string existingGenDesig = GetProp(cpmGen, "Обозначение");
                string existingGenTitle = GetProp(cpmGen, "Наименование");

                string rawName = targetFileName;
                if (string.IsNullOrWhiteSpace(rawName)) rawName = model.GetPathName();
                if (string.IsNullOrWhiteSpace(rawName)) rawName = model.GetTitle();

                string baseName = CleanDocumentName(rawName);
                bool isDefaultTemplateName = !string.IsNullOrEmpty(baseName) &&
                    Regex.IsMatch(baseName, @"^(Деталь|Part|Сборка|Assem|Чертеж|Draw)\s*\d*$", RegexOptions.IgnoreCase);

                string parsedDesig = "";
                string parsedTitle = "";

                if (!isDefaultTemplateName && !string.IsNullOrWhiteSpace(baseName))
                {
                    int spaceIdx = baseName.IndexOf(' ');
                    if (spaceIdx > 0)
                    {
                        parsedDesig = baseName.Substring(0, spaceIdx).Trim();
                        parsedTitle = baseName.Substring(spaceIdx + 1).Trim();

                        // Check if parsedTitle starts with a document code (e.g. "СБ Сборка рамы")
                        Match docCodeMatch = Regex.Match(parsedTitle, @"^(СБ|ГЧ|МЧ|ВО|ТУ|ТБ|ПЭ|Э\d|СХ|СЭ|ВП|СП)(\s+.*|$)", RegexOptions.IgnoreCase);
                        if (docCodeMatch.Success)
                        {
                            parsedDesig = parsedDesig + " " + docCodeMatch.Groups[1].Value.ToUpper();
                            parsedTitle = docCodeMatch.Groups[2].Value.Trim();
                        }
                    }
                    else
                    {
                        if (Regex.IsMatch(baseName, @"\d"))
                            parsedDesig = baseName;
                        else
                            parsedTitle = baseName;
                    }
                }

                // Determine effective root base designation and document code (e.g. " СБ")
                bool isExistingDesigTemplate = string.IsNullOrWhiteSpace(existingGenDesig) ||
                    existingGenDesig.Contains("$PRP") ||
                    Regex.IsMatch(existingGenDesig.Trim(), @"^(Деталь|Part|Сборка|Assem|Чертеж|Draw)\s*\d*$", RegexOptions.IgnoreCase);

                // Priority to existing user/MProp properties over file name parsing
                string effectiveBaseDesig = !isExistingDesigTemplate
                    ? existingGenDesig
                    : (!string.IsNullOrWhiteSpace(parsedDesig) ? parsedDesig : "");

                string foundExecInBase;
                string docCode;
                string rootBaseDesig = StripExecutionSuffix(effectiveBaseDesig, out foundExecInBase, out docCode);

                if (isAssembly && string.IsNullOrEmpty(docCode) && effectiveBaseDesig.EndsWith(" СБ", StringComparison.OrdinalIgnoreCase))
                {
                    docCode = " СБ";
                }

                bool isExistingTitleTemplate = string.IsNullOrWhiteSpace(existingGenTitle) ||
                    existingGenTitle.Contains("$PRP") ||
                    Regex.IsMatch(existingGenTitle.Trim(), @"^(Деталь|Part|Сборка|Assem|Чертеж|Draw)\s*\d*$", RegexOptions.IgnoreCase);

                // Priority to existing user/MProp title over file name parsing
                string effectiveTitle = !isExistingTitleTemplate
                    ? existingGenTitle
                    : (!string.IsNullOrWhiteSpace(parsedTitle) ? parsedTitle : "");

                // 2. Set general custom properties (for $PRPSHEET and $PRP)
                if (cpmGen != null)
                {
                    string baseFullDesig = BuildExecutionDesignation(rootBaseDesig, "", docCode);
                    if (!string.IsNullOrEmpty(baseFullDesig))
                    {
                        string curGenDesig = GetProp(cpmGen, "Обозначение");
                        if (string.IsNullOrEmpty(curGenDesig) || isExistingDesigTemplate)
                        {
                            SetProp(cpmGen, "Обозначение", baseFullDesig);
                            SetProp(cpmGen, "PartNo", baseFullDesig);
                            SetProp(cpmGen, "Number", baseFullDesig);
                        }
                    }
                    if (!string.IsNullOrEmpty(effectiveTitle))
                    {
                        string curGenTitle = GetProp(cpmGen, "Наименование");
                        string curGenTitleFB = GetProp(cpmGen, "Наименование_ФБ");
                        if (string.IsNullOrEmpty(curGenTitle) || isExistingTitleTemplate)
                        {
                            SetProp(cpmGen, "Наименование", effectiveTitle);
                            SetProp(cpmGen, "Description", effectiveTitle);
                        }
                        if (string.IsNullOrEmpty(curGenTitleFB) || curGenTitleFB.Contains("$PRP"))
                        {
                            string titleFB = FormatEskdTitleFB(effectiveTitle);
                            SetProp(cpmGen, "Наименование_ФБ", titleFB);
                        }
                    }
                }

                // 3. Process every configuration
                try
                {
                    string[] cfgNames = model.GetConfigurationNames() as string[];
                    if (cfgNames != null && cfgNames.Length > 0)
                    {
                        foreach (string cfg in cfgNames)
                        {
                            if (string.IsNullOrEmpty(cfg)) continue;

                            Configuration swConfig = (Configuration)model.GetConfigurationByName(cfg);
                            Configuration rootConfig = swConfig;
                            while (rootConfig != null && rootConfig.IsDerived())
                            {
                                rootConfig = (Configuration)rootConfig.GetParent();
                            }
                            string effectiveCfgName = rootConfig != null ? rootConfig.Name : cfg;

                            string execCode;
                            bool isBaseConfig;
                            bool recognized = ExtractExecutionFromConfigName(effectiveCfgName, out execCode, out isBaseConfig);

                            CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(cfg);
                            string existingCfgDesig = GetProp(cpmCfg, "Обозначение");

                            string configDesignation = "";

                            if (recognized)
                            {
                                if (isBaseConfig || string.IsNullOrEmpty(execCode))
                                {
                                    configDesignation = BuildExecutionDesignation(rootBaseDesig, "", docCode);
                                }
                                else
                                {
                                    configDesignation = BuildExecutionDesignation(rootBaseDesig, execCode, docCode);
                                }
                            }
                            else
                            {
                                // Configuration name didn't specify an execution (e.g. "SpecialVariant").
                                // If existing designation has an execution suffix for this base, preserve it
                                if (!string.IsNullOrWhiteSpace(existingCfgDesig) && !existingCfgDesig.Contains("$PRP"))
                                {
                                    configDesignation = existingCfgDesig;
                                }
                                else
                                {
                                    configDesignation = BuildExecutionDesignation(rootBaseDesig, "", docCode);
                                }
                            }

                            // Write configuration properties
                            if (cpmCfg != null)
                            {
                                if (!string.IsNullOrEmpty(configDesignation))
                                {
                                    string curCfgDesig = GetProp(cpmCfg, "Обозначение");
                                    if (string.IsNullOrEmpty(curCfgDesig) || curCfgDesig.Contains("$PRP") || isExistingDesigTemplate)
                                    {
                                        SetProp(cpmCfg, "Обозначение", configDesignation);
                                        SetProp(cpmCfg, "PartNo", configDesignation);
                                        SetProp(cpmCfg, "Number", configDesignation);
                                    }

                                    // MProp execution flag: "2" means execution is active, "0" means base
                                    string curExec, curDoc;
                                    StripExecutionSuffix(configDesignation, out curExec, out curDoc);
                                    if (!string.IsNullOrEmpty(curExec))
                                    {
                                        SetProp(cpmCfg, "Исполнение", "2");
                                    }
                                    else
                                    {
                                        SetProp(cpmCfg, "Исполнение", "0");
                                    }
                                }

                                if (!string.IsNullOrEmpty(effectiveTitle))
                                {
                                    string curCfgTitle = GetProp(cpmCfg, "Наименование");
                                    string curCfgTitleFB = GetProp(cpmCfg, "Наименование_ФБ");
                                    if (string.IsNullOrEmpty(curCfgTitle) || isExistingTitleTemplate)
                                    {
                                        SetProp(cpmCfg, "Наименование", effectiveTitle);
                                        SetProp(cpmCfg, "Description", effectiveTitle);
                                    }
                                    if (string.IsNullOrEmpty(curCfgTitleFB) || curCfgTitleFB.Contains("$PRP"))
                                    {
                                        string titleFB = FormatEskdTitleFB(effectiveTitle);
                                        SetProp(cpmCfg, "Наименование_ФБ", titleFB);
                                    }
                                }
                            }

                            // Synchronize SolidWorks Bill of Materials (BOM) Options
                            if (swConfig != null && !string.IsNullOrEmpty(configDesignation))
                            {
                                try
                                {
                                    swConfig.BOMPartNoSource = (int)swBOMPartNumberSource_e.swBOMPartNumber_UserSpecified;
                                    swConfig.AlternateName = configDesignation;
                                    swConfig.UseAlternateNameInBOM = true;
                                }
                                catch { }
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
