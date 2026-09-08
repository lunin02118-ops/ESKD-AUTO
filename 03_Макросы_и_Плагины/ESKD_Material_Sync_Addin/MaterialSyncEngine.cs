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
                AlignDrawingTitleNote(drw);
                AlignSpecificationTableColumns(drw);
                if (triggerRebuild)
                {
                    drwModel.ForceRebuild3(true);
                }
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

                string[] sheetNames = (string[])drw.GetSheetNames();
                string origSheetName = null;
                Sheet curSheet = (Sheet)drw.GetCurrentSheet();
                if (curSheet != null) origSheetName = curSheet.GetName();

                if (sheetNames != null && sheetNames.Length > 1)
                {
                    foreach (string sName in sheetNames)
                    {
                        drw.ActivateSheet(sName);
                        AlignActiveSheetMassNote(drw);
                    }
                    if (!string.IsNullOrEmpty(origSheetName))
                    {
                        drw.ActivateSheet(origSheetName);
                    }
                }
                else
                {
                    AlignActiveSheetMassNote(drw);
                }
            }
            catch { }
        }

        private static void AlignActiveSheetMassNote(DrawingDoc drw)
        {
            if (drw == null) return;
            try
            {
                Sheet sheet = (Sheet)drw.GetCurrentSheet();
                if (sheet == null || IsSpecificationForm2(drw, sheet) || IsSubsequentSheetForm2a(drw, sheet)) return;

                double sheetW = 0.0;
                double[] sProps = (double[])sheet.GetProperties2();
                if (sProps != null && sProps.Length > 5)
                {
                    sheetW = sProps[5];
                }

                View v = (View)drw.GetFirstView();
                Note massNote = null;
                Note scaleNote = null;
                Note litNote = null;

                // Scan drawing views for Scale, Mass, and Litera notes in title block data row (Y in [0.015, 0.045])
                View cur = v;
                while (cur != null)
                {
                    Note n = (Note)cur.GetFirstNote();
                    while (n != null)
                    {
                        string ltxt = n.PropertyLinkedText ?? "";
                        string name = n.GetName() ?? "";

                        Annotation ann = (Annotation)n.GetAnnotation();
                        if (ann != null)
                        {
                            double[] pos = (double[])ann.GetPosition();
                            if (pos != null && pos.Length >= 2 && pos[1] > 0.015 && pos[1] < 0.045)
                            {
                                if (name.Equals("Scale", StringComparison.OrdinalIgnoreCase) ||
                                    ltxt.IndexOf("Sheet Scale", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    ltxt.IndexOf("Масштаб листа", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    ltxt.IndexOf("SW-Sheet Scale", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    (ltxt.IndexOf("Масштаб", StringComparison.OrdinalIgnoreCase) >= 0 && ltxt.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    if (scaleNote == null) scaleNote = n;
                                }
                                else if (name.Equals("MYPRP15", StringComparison.OrdinalIgnoreCase) ||
                                         ltxt.IndexOf("Масса_ФБ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         ltxt.IndexOf("$PRPSHEET:\"Масса", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         ltxt.IndexOf("$PRP:\"Масса", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         ltxt.IndexOf("$PRPSHEET:\"SW-Mass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         ltxt.IndexOf("$PRP:\"SW-Mass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         (ltxt.IndexOf("Масса", StringComparison.OrdinalIgnoreCase) >= 0 && ltxt.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    if (massNote == null) massNote = n;
                                }
                                else if (name.Equals("MYPRP5", StringComparison.OrdinalIgnoreCase) ||
                                         ltxt.IndexOf("Литера_ФБ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         ltxt.IndexOf("$PRPSHEET:\"Литера", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         (ltxt.IndexOf("Литера", StringComparison.OrdinalIgnoreCase) >= 0 && ltxt.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    if (litNote == null) litNote = n;
                                }
                            }
                        }
                        n = (Note)n.GetNext();
                    }
                    cur = (View)cur.GetNextView();
                }

                if (massNote == null && scaleNote == null) return;

                // Check and clean referenced model's legacy <FONT> tags in "Масса_ФБ"
                try
                {
                    View vFirst = (View)drw.GetFirstView();
                    View vModel = vFirst != null ? (View)vFirst.GetNextView() : null;
                    if (vModel != null)
                    {
                        ModelDoc2 refModel = (ModelDoc2)vModel.ReferencedDocument;
                        if (refModel != null && CleanModelMassTags(refModel))
                        {
                            drw.ForceRebuild();
                        }
                    }
                }
                catch { }

                Annotation annM = massNote != null ? (Annotation)massNote.GetAnnotation() : null;
                Annotation annS = scaleNote != null ? (Annotation)scaleNote.GetAnnotation() : null;
                TextFormat tfS = annS != null ? (TextFormat)annS.GetTextFormat(0) : null;
                TextFormat tfM = annM != null ? (TextFormat)annM.GetTextFormat(0) : null;

                // Sync text formatting of Mass and Litera to match Scale (ensures exact identical baseline and line spacing)
                if (tfM != null && annM != null)
                {
                    if (tfS != null)
                    {
                        tfM.LineSpacing = tfS.LineSpacing;
                        tfM.CharHeight = tfS.CharHeight;
                        tfM.TypeFaceName = tfS.TypeFaceName;
                        tfM.Italic = tfS.Italic;
                        tfM.Bold = tfS.Bold;
                    }
                    else
                    {
                        tfM.LineSpacing = 0.001;
                        tfM.CharHeight = 0.0035;
                        tfM.TypeFaceName = "GOST type A";
                    }
                    annM.SetTextFormat(0, false, tfM);
                }

                // Standard GOST 2.104 title block dimensions:
                // Stamp width = 185 mm, right margin = 5 mm.
                // Mass/Scale data cell vertical range: Y in [0.025, 0.040] (15 mm height).
                // Vertical geometric center: Y = 0.0325 m (32.5 mm).
                double cellCenterY = 0.0325;
                double targetY = 0.0347;

                if (scaleNote != null && annS != null)
                {
                    double[] posS = (double[])annS.GetPosition();
                    double[] extS = (double[])scaleNote.GetExtent();
                    if (extS != null && extS.Length >= 6 && (extS[4] - extS[1]) > 0.001)
                    {
                        double curCenterS = (extS[1] + extS[4]) / 2.0;
                        double deltaS = cellCenterY - curCenterS;
                        targetY = posS[1] + deltaS;
                    }
                    else if (posS != null && posS.Length >= 2)
                    {
                        targetY = posS[1];
                    }

                    // Scale cell is 18 mm wide, right margin 5 mm -> center is sheetW - 14 mm (0.014 m)
                    double targetScaleX = sheetW > 0.15 ? (sheetW - 0.0140) : (posS != null ? posS[0] : 0.0);
                    annS.SetPosition(targetScaleX, targetY, posS != null && posS.Length > 2 ? posS[2] : 0.0);
                    scaleNote.SetTextJustification((int)swTextJustification_e.swTextJustificationCenter);
                }

                if (massNote != null && annM != null)
                {
                    double[] extM = (double[])massNote.GetExtent();
                    double[] posM = (double[])annM.GetPosition();
                    double targetMassY = targetY;

                    if (extM != null && extM.Length >= 6 && (extM[4] - extM[1]) > 0.001)
                    {
                        double hM = extM[4] - extM[1];
                        if (hM > 0.006)
                        {
                            // Multi-line note (e.g. legacy <FONT size=1> top line):
                            // Center based on the visible text at the bottom
                            double charH = tfM != null && tfM.CharHeight > 0.001 ? tfM.CharHeight : 0.0035;
                            double visibleCenterM = extM[1] + (charH / 2.0);
                            double deltaM = cellCenterY - visibleCenterM;
                            targetMassY = posM != null && posM.Length >= 2 ? (posM[1] + deltaM) : targetY;
                        }
                        else
                        {
                            // Clean single-line note: center extent directly
                            double curCenterM = (extM[1] + extM[4]) / 2.0;
                            double deltaM = cellCenterY - curCenterM;
                            targetMassY = posM != null && posM.Length >= 2 ? (posM[1] + deltaM) : targetY;
                        }
                    }

                    // Mass cell is 17 mm wide, between (sheetW - 40 mm) and (sheetW - 23 mm) -> center is sheetW - 31.5 mm (0.0315 m)
                    double targetMassX = sheetW > 0.15 ? (sheetW - 0.0315) : (scaleNote != null ? (sheetW - 0.0140 - 0.0175) : 0.0);
                    annM.SetPosition(targetMassX, targetMassY, posM != null && posM.Length > 2 ? posM[2] : 0.0);
                    massNote.SetTextJustification((int)swTextJustification_e.swTextJustificationCenter);
                }

                if (litNote != null)
                {
                    Annotation annL = (Annotation)litNote.GetAnnotation();
                    if (annL != null)
                    {
                        TextFormat tfL = (TextFormat)annL.GetTextFormat(0);
                        if (tfL != null)
                        {
                            if (tfS != null)
                            {
                                tfL.LineSpacing = tfS.LineSpacing;
                                tfL.CharHeight = tfS.CharHeight;
                                tfL.TypeFaceName = tfS.TypeFaceName;
                                tfL.Italic = tfS.Italic;
                                tfL.Bold = tfS.Bold;
                            }
                            else
                            {
                                tfL.LineSpacing = 0.001;
                                tfL.CharHeight = 0.0035;
                                tfL.TypeFaceName = "GOST type A";
                            }
                            annL.SetTextFormat(0, false, tfL);
                        }

                        double[] extL = (double[])litNote.GetExtent();
                        double[] posL = (double[])annL.GetPosition();
                        double targetLitY = targetY;
                        if (extL != null && extL.Length >= 6 && (extL[4] - extL[1]) > 0.001)
                        {
                            double curCenterL = (extL[1] + extL[4]) / 2.0;
                            double deltaL = cellCenterY - curCenterL;
                            targetLitY = posL != null && posL.Length >= 2 ? (posL[1] + deltaL) : targetY;
                        }

                        // Litera cell: default column 2 center is sheetW - 47.5 mm (0.0475 m)
                        double targetLitX = sheetW > 0.15 ? (sheetW - 0.0475) : 0.0;
                        annL.SetPosition(targetLitX, targetLitY, posL != null && posL.Length > 2 ? posL[2] : 0.0);
                        litNote.SetTextJustification((int)swTextJustification_e.swTextJustificationCenter);
                    }
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
                        autoCenter = (int)key.GetValue("AutoCenterMaterial", (int)key.GetValue("AutoCenterMass", 1));
                    }
                }
                if (autoCenter == 0) return;

                string[] sheetNames = (string[])drw.GetSheetNames();
                string origSheetName = null;
                Sheet curSheet = (Sheet)drw.GetCurrentSheet();
                if (curSheet != null) origSheetName = curSheet.GetName();

                if (sheetNames != null && sheetNames.Length > 1)
                {
                    foreach (string sName in sheetNames)
                    {
                        drw.ActivateSheet(sName);
                        AlignActiveSheetMaterialNote(drw);
                    }
                    if (!string.IsNullOrEmpty(origSheetName))
                    {
                        drw.ActivateSheet(origSheetName);
                    }
                }
                else
                {
                    AlignActiveSheetMaterialNote(drw);
                }
            }
            catch { }
        }

        private static void AlignActiveSheetMaterialNote(DrawingDoc drw)
        {
            if (drw == null) return;
            try
            {
                Sheet sheet = (Sheet)drw.GetCurrentSheet();
                if (sheet == null || IsSpecificationForm2(drw, sheet) || IsSubsequentSheetForm2a(drw, sheet)) return;

                double sheetW = 0.0;
                double[] sProps = (double[])sheet.GetProperties2();
                if (sProps != null && sProps.Length > 5)
                {
                    sheetW = sProps[5];
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

        public static void AlignDrawingTitleNote(DrawingDoc drw)
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

                string[] sheetNames = (string[])drw.GetSheetNames();
                string origSheetName = null;
                Sheet curSheet = (Sheet)drw.GetCurrentSheet();
                if (curSheet != null) origSheetName = curSheet.GetName();

                if (sheetNames != null && sheetNames.Length > 1)
                {
                    foreach (string sName in sheetNames)
                    {
                        drw.ActivateSheet(sName);
                        AlignActiveSheetTitleNote(drw);
                    }
                    if (!string.IsNullOrEmpty(origSheetName))
                    {
                        drw.ActivateSheet(origSheetName);
                    }
                }
                else
                {
                    AlignActiveSheetTitleNote(drw);
                }
            }
            catch { }
        }

        private static void AlignActiveSheetTitleNote(DrawingDoc drw)
        {
            if (drw == null) return;
            try
            {
                Sheet sheet = (Sheet)drw.GetCurrentSheet();
                if (sheet == null || IsSubsequentSheetForm2a(drw, sheet)) return;

                bool isForm2 = IsSpecificationForm2(drw, sheet);

                double sheetW = 0.0;
                double[] sProps = (double[])sheet.GetProperties2();
                if (sProps != null && sProps.Length > 5)
                {
                    sheetW = sProps[5];
                }

                // ГОСТ 2.104 title block coordinates:
                // Stamp width = 185 mm, right margin = 5 mm.
                // Графа 1 (Наименование) is 70 mm wide.
                // Left border = sheetW - 125 mm, Right border = sheetW - 55 mm.
                // Cell center X = sheetW - 90 mm (0.090 m).
                //
                // In Form 1 (Основная надпись чертежей, штамп 55 мм):
                // Floor Y = 0.020 m (20 mm from sheet bottom, above the 15 mm material/company cell).
                // Ceiling Y = 0.045 m (45 mm from sheet bottom, below the 15 mm designation cell).
                // Cell height = 0.025 m (25 mm).
                // Center Y = (0.020 + 0.045) / 2 = 0.0325 m (32.5 mm).
                //
                // In Form 2 (Основная надпись спецификации первый лист, штамп 40 мм):
                // Upper row (Y = 30..45 mm, 120x15 mm) is Графа 2 (Обозначение, note MYPRP19).
                // Lower row (Y = 5..30 mm, 70x25 mm) is Графа 1 (Наименование, note MYPRP4).
                // Floor Y = 0.005 m, Ceiling Y = 0.030 m.
                // Cell height = 0.025 m (25 mm).
                // Center Y = (0.005 + 0.030) / 2 = 0.0175 m (17.5 mm).

                double targetCenterX = sheetW > 0.15 ? (sheetW - 0.090) : 0.0;
                double cellCenterY = isForm2 ? 0.0175 : 0.0325;

                View v = (View)drw.GetFirstView();
                Note noteTitle = null;
                Note noteSubtitle = null;

                while (v != null)
                {
                    Note n = (Note)v.GetFirstNote();
                    while (n != null)
                    {
                        string name = n.GetName() ?? "";
                        string ltxt = n.PropertyLinkedText ?? "";

                        bool isTitle = false;
                        bool isSubtitle = false;

                        if (name.Equals("MYPRP4", StringComparison.OrdinalIgnoreCase))
                        {
                            isTitle = true;
                        }
                        else if (!isForm2 && name.Equals("MYPRP3", StringComparison.OrdinalIgnoreCase))
                        {
                            isSubtitle = true;
                        }
                        else if (ltxt.IndexOf("Наименование_ФБ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 ltxt.IndexOf("$PRPSHEET:\"Наименование", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (ltxt.IndexOf("Наименование", StringComparison.OrdinalIgnoreCase) >= 0 && ltxt.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            Annotation a = (Annotation)n.GetAnnotation();
                            if (a != null)
                            {
                                double[] p = (double[])a.GetPosition();
                                if (p != null && p.Length >= 2)
                                {
                                    if (isForm2 && p[1] > 0.003 && p[1] < 0.040)
                                    {
                                        isTitle = true;
                                    }
                                    else if (!isForm2 && p[1] > 0.015 && p[1] < 0.055)
                                    {
                                        isTitle = true;
                                    }
                                }
                            }
                        }
                        else if (!isForm2 && ltxt.IndexOf("Сборка2_ФБ", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            isSubtitle = true;
                        }

                        if (isTitle && noteTitle == null)
                        {
                            noteTitle = n;
                        }
                        if (isSubtitle && noteSubtitle == null)
                        {
                            noteSubtitle = n;
                        }

                        n = (Note)n.GetNext();
                    }
                    v = (View)v.GetNextView();
                }

                if (noteTitle != null)
                {
                    Annotation annTitle = (Annotation)noteTitle.GetAnnotation();
                    if (annTitle != null)
                    {
                        double[] posTitle = (double[])annTitle.GetPosition();
                        double[] extTitle = (double[])noteTitle.GetExtent();
                        double targetX = (targetCenterX > 0.05) ? targetCenterX : (posTitle != null && posTitle.Length > 0 ? posTitle[0] : 0.0);

                        bool hasValidTitleExt = extTitle != null && extTitle.Length >= 6 && (extTitle[4] - extTitle[1]) > 0.001;

                        // Check if subtitle note exists and has non-empty text (e.g. "Сборочный чертеж")
                        bool hasSubtitleText = false;
                        Annotation annSub = null;
                        double[] posSub = null;
                        double[] extSub = null;
                        if (!isForm2 && noteSubtitle != null)
                        {
                            string subText = noteSubtitle.GetText();
                            if (!string.IsNullOrWhiteSpace(subText))
                            {
                                annSub = (Annotation)noteSubtitle.GetAnnotation();
                                if (annSub != null)
                                {
                                    posSub = (double[])annSub.GetPosition();
                                    extSub = (double[])noteSubtitle.GetExtent();
                                    hasSubtitleText = true;
                                }
                            }
                        }

                        if (hasSubtitleText && extSub != null && extSub.Length >= 6 && hasValidTitleExt)
                        {
                            // Two elements: Title and Subtitle stacked together
                            double combinedMinY = Math.Min(extTitle[1], extSub[1]);
                            double combinedMaxY = Math.Max(extTitle[4], extSub[4]);
                            double currentCenterY = (combinedMinY + combinedMaxY) / 2.0;
                            double deltaY = cellCenterY - currentCenterY;

                            double targetYTitle = posTitle[1] + deltaY;
                            double targetYSub = posSub[1] + deltaY;

                            if (Math.Abs(posTitle[1] - targetYTitle) > 0.00015 || Math.Abs(posTitle[0] - targetX) > 0.0005)
                            {
                                annTitle.SetPosition(targetX, targetYTitle, posTitle.Length > 2 ? posTitle[2] : 0.0);
                                noteTitle.SetTextJustification((int)swTextJustification_e.swTextJustificationCenter);
                            }
                            if (Math.Abs(posSub[1] - targetYSub) > 0.00015 || Math.Abs(posSub[0] - targetX) > 0.0005)
                            {
                                annSub.SetPosition(targetX, targetYSub, posSub.Length > 2 ? posSub[2] : 0.0);
                                noteSubtitle.SetTextJustification((int)swTextJustification_e.swTextJustificationCenter);
                            }
                        }
                        else
                        {
                            // Single element: Title alone centered in [0.020, 0.045] (Form 1) or [0.005, 0.030] (Form 2)
                            double targetYTitle = posTitle[1];
                            if (hasValidTitleExt)
                            {
                                double currentCenterY = (extTitle[1] + extTitle[4]) / 2.0;
                                double deltaY = cellCenterY - currentCenterY;
                                targetYTitle = posTitle[1] + deltaY;
                            }
                            else
                            {
                                double hTitle = 0.0055;
                                targetYTitle = cellCenterY + (hTitle / 2.0);
                            }

                            if (Math.Abs(posTitle[1] - targetYTitle) > 0.00015 || Math.Abs(posTitle[0] - targetX) > 0.0005)
                            {
                                annTitle.SetPosition(targetX, targetYTitle, posTitle.Length > 2 ? posTitle[2] : 0.0);
                                noteTitle.SetTextJustification((int)swTextJustification_e.swTextJustificationCenter);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public static void AlignSpecificationTableColumns(DrawingDoc drw)
        {
            if (drw == null) return;
            try
            {
                View v = (View)drw.GetFirstView();
                while (v != null)
                {
                    object[] tables = (object[])v.GetTableAnnotations();
                    if (tables != null)
                    {
                        foreach (TableAnnotation t in tables)
                        {
                            if (t != null && t.ColumnCount >= 7)
                            {
                                int c5Type = t.GetColumnType(5);
                                int c6Type = t.GetColumnType(6);
                                if (c5Type != 203 && c6Type == 203)
                                {
                                    t.MoveColumn(6, (int)swTableItemInsertPosition_e.swTableItemInsertPosition_After, 4);
                                    t.SetColumnWidth(5, 0.010, 0);
                                    t.SetColumnWidth(6, 0.022, 0);
                                }
                            }
                        }
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

                        string[] allCfgs = (string[])model.GetConfigurationNames();
                        if (allCfgs != null)
                        {
                            foreach (string cfg in allCfgs)
                            {
                                CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(cfg);
                                if (cpmCfg != null)
                                {
                                    SetProp(cpmCfg, "Масса_ФБ", massStr);
                                    SetProp(cpmCfg, "Масса", massStr);
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
            if (doc == null) return false;
            bool changed = false;
            try
            {
                changed |= CleanCpmMassTags(doc.Extension.get_CustomPropertyManager(""));
                string[] cfgNames = (string[])doc.GetConfigurationNames();
                if (cfgNames != null)
                {
                    foreach (string cfg in cfgNames)
                    {
                        changed |= CleanCpmMassTags(doc.Extension.get_CustomPropertyManager(cfg));
                    }
                }
            }
            catch { }
            return changed;
        }

        private static bool CleanCpmMassTags(CustomPropertyManager cpm)
        {
            if (cpm == null) return false;
            bool changed = false;
            try
            {
                string[] names = (string[])cpm.GetNames();
                if (names == null) return false;
                foreach (string name in names)
                {
                    if (name.Equals("Масса_ФБ", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = "", resVal = "";
                        bool wasRes = false;
                        cpm.Get5(name, false, out val, out resVal, out wasRes);
                        if (!string.IsNullOrEmpty(val) && (val.IndexOf("<FONT", StringComparison.OrdinalIgnoreCase) >= 0 || val.IndexOf("\n") >= 0))
                        {
                            string cleaned = System.Text.RegularExpressions.Regex.Replace(val, @"<FONT[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                            cpm.Set2(name, cleaned);
                            changed = true;
                        }
                    }
                }
            }
            catch { }
            return changed;
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

                string effectiveBaseDesig = !string.IsNullOrWhiteSpace(parsedDesig)
                    ? parsedDesig
                    : (!isExistingDesigTemplate ? existingGenDesig : "");

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

                string effectiveTitle = !string.IsNullOrWhiteSpace(parsedTitle)
                    ? parsedTitle
                    : (!isExistingTitleTemplate ? existingGenTitle : "");

                // 2. Set general custom properties (for $PRPSHEET and $PRP)
                if (cpmGen != null)
                {
                    string baseFullDesig = BuildExecutionDesignation(rootBaseDesig, "", docCode);
                    if (!string.IsNullOrEmpty(baseFullDesig))
                    {
                        SetProp(cpmGen, "Обозначение", baseFullDesig);
                        SetProp(cpmGen, "PartNo", baseFullDesig);
                        SetProp(cpmGen, "Number", baseFullDesig);
                    }
                    if (!string.IsNullOrEmpty(effectiveTitle))
                    {
                        SetProp(cpmGen, "Наименование", effectiveTitle);
                        SetProp(cpmGen, "Наименование_ФБ", effectiveTitle);
                        SetProp(cpmGen, "Description", effectiveTitle);
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
                                    SetProp(cpmCfg, "Обозначение", configDesignation);
                                    SetProp(cpmCfg, "PartNo", configDesignation);
                                    SetProp(cpmCfg, "Number", configDesignation);

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
                                    SetProp(cpmCfg, "Наименование", effectiveTitle);
                                    SetProp(cpmCfg, "Наименование_ФБ", effectiveTitle);
                                    SetProp(cpmCfg, "Description", effectiveTitle);
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
