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
    /// <summary>
    /// Режим синхронизации чертежа (D-1).
    /// OnSave — полный путь: запись свойств в ссылочную 3D-модель + ForceRebuild3
    ///          (используется для FileSaveNotify/FileSaveAsNotify2 и явной синхронизации).
    /// OnActivate — лёгкий путь: синхронизируются только свойства самого чертежа,
    ///          ссылочная 3D-модель не мутируется, ForceRebuild3 не вызывается.
    /// </summary>
    public enum SyncMode
    {
        OnSave = 0,
        OnActivate = 1
    }

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
        private const string RegKeySettings = @"Software\SolidWorks\ESKD_Settings";

        /// <summary>
        /// Безопасное чтение целочисленного значения из реестра (D-14):
        /// поддерживает int/byte и строковое представление через int.TryParse,
        /// при любом сбое возвращает значение по умолчанию.
        /// </summary>
        public static int ReadIntSafe(RegistryKey key, string name, int def)
        {
            if (key == null || string.IsNullOrEmpty(name)) return def;
            try
            {
                object raw = key.GetValue(name, def);
                if (raw == null) return def;
                if (raw is int) return (int)raw;
                if (raw is byte) return (int)(byte)raw;
                string s = raw as string;
                if (s != null)
                {
                    int parsed;
                    if (int.TryParse(s.Trim(), out parsed)) return parsed;
                }
            }
            catch { }
            return def;
        }

        public static bool IsServiceEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeySettings))
                {
                    if (key != null)
                    {
                        int val = ReadIntSafe(key, "ServiceEnabled", 1);
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
                        int val = ReadIntSafe(key, "AutoSyncMaterials", 1);
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

        // =====================================================================================
        // Безчертёжные детали (БЧ), ГОСТ 2.109-73 п. 3.3: деталь, на которую не выпускается
        // чертёж, изготовляется по данным спецификации — в графе «Наименование» спецификации
        // после наименования указывается индекс «БЧ». Свойство-признак «БЧ»="БЧ" служит
        // для фильтров PDM/поиска и не даёт конвейеру «забыть» статус при пересинхронизации.
        //
        // Возвращает: 0 — документ не деталь (или null); 1 — признак БЧ УСТАНОВЛЕН;
        //             2 — признак БЧ СНЯТ.
        // =====================================================================================
        public static int MarkDrawinglessPart(ModelDoc2 model)
        {
            if (model == null) return 0;
            try
            {
                if (model.GetType() != (int)swDocumentTypes_e.swDocPART) return 0;

                CustomPropertyManager cpmGen = model.Extension.get_CustomPropertyManager("");
                if (cpmGen == null) return 0;

                string bchRaw = GetPropRaw(cpmGen, "БЧ");
                bool isBch = !string.IsNullOrEmpty(bchRaw);

                string title = GetProp(cpmGen, "Наименование") ?? "";
                string titleFb = GetProp(cpmGen, "Наименование_ФБ") ?? "";

                if (!isBch)
                {
                    SetProp(cpmGen, "БЧ", "БЧ");
                    if (title.Length > 0 && !title.TrimEnd().EndsWith(" БЧ", StringComparison.Ordinal))
                    {
                        SetProp(cpmGen, "Наименование", title.TrimEnd() + " БЧ");
                    }
                    if (titleFb.Length > 0)
                    {
                        SetProp(cpmGen, "Наименование_ФБ", AppendBchSuffix(titleFb));
                    }
                    ApplyBchToConfigurations(model, true);
                    return 1;
                }
                else
                {
                    try { cpmGen.Delete2("БЧ"); } catch { }
                    if (title.TrimEnd().EndsWith(" БЧ", StringComparison.Ordinal))
                    {
                        SetProp(cpmGen, "Наименование", title.TrimEnd().Substring(0, title.TrimEnd().Length - 3).TrimEnd());
                    }
                    if (titleFb.Length > 0)
                    {
                        SetProp(cpmGen, "Наименование_ФБ", RemoveBchSuffix(titleFb));
                    }
                    ApplyBchToConfigurations(model, false);
                    return 2;
                }
            }
            catch { }
            return 0;
        }

        // Индекс «БЧ» проставляется в ПОСЛЕДНЕЙ строке наименования (графа 2 может быть
        // двухстрочной) — на печати БЧ стоит сразу после текста, на той же строке.
        private static string AppendBchSuffix(string value)
        {
            string v = value.TrimEnd();
            if (v.EndsWith(" БЧ", StringComparison.Ordinal)) return value;
            return v + " БЧ";
        }

        private static string RemoveBchSuffix(string value)
        {
            string v = value.TrimEnd();
            if (v.EndsWith(" БЧ", StringComparison.Ordinal))
            {
                return v.Substring(0, v.Length - 3).TrimEnd();
            }
            return value;
        }

        // Конфигурационные копии: спецификация/BOM читает свойство конфигурации раньше
        // общего — признак и индекс обязаны совпадать на обоих уровнях.
        private static void ApplyBchToConfigurations(ModelDoc2 model, bool setBch)
        {
            try
            {
                string[] cfgNames = model.GetConfigurationNames() as string[];
                if (cfgNames == null) return;
                foreach (string cfg in cfgNames)
                {
                    if (string.IsNullOrEmpty(cfg)) continue;
                    CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(cfg);
                    if (cpmCfg == null) continue;
                    if (setBch)
                    {
                        SetProp(cpmCfg, "БЧ", "БЧ");
                        string t = GetProp(cpmCfg, "Наименование") ?? "";
                        if (t.Length > 0 && !t.TrimEnd().EndsWith(" БЧ", StringComparison.Ordinal))
                        {
                            SetProp(cpmCfg, "Наименование", t.TrimEnd() + " БЧ");
                        }
                    }
                    else
                    {
                        try { cpmCfg.Delete2("БЧ"); } catch { }
                        string t = GetProp(cpmCfg, "Наименование") ?? "";
                        if (t.TrimEnd().EndsWith(" БЧ", StringComparison.Ordinal))
                        {
                            SetProp(cpmCfg, "Наименование", t.TrimEnd().Substring(0, t.TrimEnd().Length - 3).TrimEnd());
                        }
                    }
                }
            }
            catch { }
        }

        public static string FormatEskdTitleFB(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            string t = title.Trim();

            // ГОСТ 2.104: графа 2 (наименование) шириной 70 мм при шрифте ~7 мм вмещает ~24
            // символа. Длинное наименование делим по словам максимум на две строки: перенос
            // строки в значении свойства рендерится штамповой заметкой (MYPRP4) как вторая
            // строка — та же механика, что у двухстрочной массы. Слова не режем: если слово
            // длиннее лимита, оно остаётся целиком (превышение ширины не усугубляем).
            const int TitleLineLimit = 24;
            if (t.Length <= TitleLineLimit) return t;

            string[] words = t.Split(new char[] { ' ', '\t', '\u00A0' });
            string line1 = "";
            string line2 = "";
            bool toSecond = false;
            foreach (string word in words)
            {
                if (string.IsNullOrEmpty(word)) continue;
                if (!toSecond)
                {
                    string cand = line1.Length == 0 ? word : line1 + " " + word;
                    if (cand.Length <= TitleLineLimit || line1.Length == 0)
                    {
                        line1 = cand;
                        continue;
                    }
                    toSecond = true;
                }
                line2 = line2.Length == 0 ? word : line2 + " " + word;
            }
            return line2.Length > 0 ? line1 + "\n" + line2 : line1;
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

        public static void SyncModelProperties(ModelDoc2 model, ISldWorks swApp, bool force = true, bool triggerRebuild = true, string targetFileName = null, SyncMode mode = SyncMode.OnSave)
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
                    SyncAssembly((AssemblyDoc)model, swApp, force, targetFileName);
                }
                else if (docType == (int)swDocumentTypes_e.swDocDRAWING)
                {
                    SyncDrawing((DrawingDoc)model, swApp, triggerRebuild, targetFileName, force, mode);
                }
            }
            catch { }
        }

        public static void SyncAssembly(AssemblyDoc asm, ISldWorks swApp, bool force = false, string targetFileName = null)
        {
            if (asm == null || swApp == null) return;
            if (!force && !IsServiceEnabled()) return;
            try
            {
                ModelDoc2 model = (ModelDoc2)asm;

                // STRICT RECURSION GUARD:
                // Only process root assembly document. Never traverse child components (asm.GetComponents).
                Configuration activeConfig = (Configuration)model.GetActiveConfiguration();
                string cfgName = activeConfig != null ? activeConfig.Name : "";
                MaterialSyncResult res = new MaterialSyncResult();
                ApplyUserSettings(model, cfgName, res, targetFileName);
            }
            catch { }
        }

        public static void SyncDrawing(DrawingDoc drw, ISldWorks swApp, bool triggerRebuild = true, string targetFileName = null, bool syncReferencedModel = false, SyncMode mode = SyncMode.OnSave)
        {
            if (drw == null) return;
            try
            {
                ModelDoc2 drwModel = (ModelDoc2)drw;
                ApplyUserSettings(drwModel, "", null, targetFileName);

                // D-1: при смене активного документа (OnActivate) выполняется ТОЛЬКО лёгкая
                // синхронизация самого чертежа: ссылочная 3D-модель не мутируется,
                // ForceRebuild3 не вызывается. Полный путь (запись в модель + ForceRebuild3)
                // зарезервирован для FileSaveNotify/FileSaveAsNotify2 и явной синхронизации (OnSave).
                if (mode == SyncMode.OnActivate)
                {
                    return;
                }

                // If explicit sync (SettingsForm, SyncCurrentDoc, or force sync), propagate user settings to the referenced 3D model!
                // Drawing title block cells link via $PRPSHEET:"Разраб." and $PRPSHEET:"Организация" (or "Контора")
                // which dynamically query the primary referenced 3D model.
                if (syncReferencedModel || triggerRebuild)
                {
                    try
                    {
                        View v = (View)drw.GetFirstView();
                        if (v != null)
                        {
                            Sheet sheet = (Sheet)drw.GetCurrentSheet();
                            string customPropView = sheet != null ? sheet.CustomPropertyView : null;
                            View refView = null;
                            if (string.IsNullOrEmpty(customPropView) ||
                                customPropView.Equals("По умолчанию", StringComparison.OrdinalIgnoreCase) ||
                                customPropView.Equals("Default", StringComparison.OrdinalIgnoreCase))
                            {
                                refView = (View)v.GetNextView();
                            }
                            else
                            {
                                View cur = (View)v.GetNextView();
                                while (cur != null)
                                {
                                    if (string.Equals(cur.GetName2(), customPropView, StringComparison.OrdinalIgnoreCase))
                                    {
                                        refView = cur;
                                        break;
                                    }
                                    cur = (View)cur.GetNextView();
                                }
                                if (refView == null) refView = (View)v.GetNextView();
                            }

                            if (refView != null)
                            {
                                ModelDoc2 refModel = (ModelDoc2)refView.ReferencedDocument;
                            if (refModel != null)
                            {
                                string refCfg = refView.ReferencedConfiguration;
                                // Compare-and-skip: ApplyUserSettings не пишет неизменённые значения,
                                // поэтому частая запись в ссылочную модель безопасна (документ не «грязнится»).
                                // Zero-Drift (ГОСТ 2.104 / коммит 51c81ac): ForceRebuild3 на чертеже
                                // НЕ вызывается — перестроение смещает заметки штампа (замерено до 9 мм);
                                // заметки $PRPSHEET обновятся штатным перестроением SolidWorks при сохранении.
                                _lastApplyChanged = false;
                                ApplyUserSettings(refModel, refCfg, null, null);
                            }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
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

        public static bool IsStandardOrPurchasedPart(ModelDoc2 model)
        {
            if (model == null) return false;
            try
            {
                // 1. Check general custom properties
                CustomPropertyManager cpmGen = model.Extension.get_CustomPropertyManager("");
                if (cpmGen != null && CheckStandardPartProperties(cpmGen)) return true;

                // 2. Check active configuration custom properties
                Configuration activeConfig = (Configuration)model.GetActiveConfiguration();
                if (activeConfig != null)
                {
                    CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(activeConfig.Name);
                    if (cpmCfg != null && CheckStandardPartProperties(cpmCfg)) return true;
                }

                // 3. Check SolidWorks Toolbox part status (path-based: \toolbox\, solidworks data).
                // D-18: dead reflection branch on ToolboxPartInformation removed — the interop
                // IModelDocExtension does not expose that property, so pi was always null.
                string pName = model.GetPathName();
                if (!string.IsNullOrEmpty(pName))
                {
                    string lp = pName.ToLowerInvariant();
                    if (lp.Contains("toolbox") || lp.Contains("solidworks data")) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool CheckStandardPartProperties(CustomPropertyManager cpm)
        {
            if (cpm == null) return false;
            string section = GetProp(cpm, "Раздел");
            if (!string.IsNullOrEmpty(section))
            {
                string s = section.Trim().ToLowerInvariant();
                if (s.Contains("стандартн") || s.Contains("прочи") || s.Contains("покупн") || s.Contains("материал") || s.StartsWith("эм-"))
                {
                    return true;
                }
            }

            // B1: признак IsFastener учитывается только при истинном значении ("1"/"true"/"да"/"yes");
            // "0"/"false"/"нет" означают НЕ крепёж — иначе легитимные детали ошибочно защищались бы от синхронизации.
            string fastenerFlag = GetProp(cpm, "IsFastener");
            if (!string.IsNullOrEmpty(fastenerFlag))
            {
                string f = fastenerFlag.Trim().ToLowerInvariant();
                if (f == "1" || f == "true" || f == "да" || f == "yes") return true;
            }
            if (!string.IsNullOrEmpty(GetProp(cpm, "Наименование_ВП"))) return true;
            if (!string.IsNullOrEmpty(GetProp(cpm, "Поставщик"))) return true;
            if (!string.IsNullOrEmpty(GetProp(cpm, "Код_Продукции"))) return true;
            if (!string.IsNullOrEmpty(GetProp(cpm, "Обозначение_ДНП"))) return true;
            if (!string.IsNullOrEmpty(GetProp(cpm, "Справочный_номер_ВП"))) return true;

            return false;
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

            // SProp / Standard / Purchased part protection:
            // Do NOT mutate standard or purchased parts (skip filename splitting, designation/title override, material override).
            if (IsStandardOrPurchasedPart(model))
            {
                result.Success = true;
                result.Message = "Стандартное/покупное изделие (SProp): синхронизация пропущена";
                return result;
            }

            string docTitle = model.GetTitle();

            Configuration activeConfig = (Configuration)model.GetActiveConfiguration();
            string activeConfigName = activeConfig != null ? activeConfig.Name : "";

            ApplyUserSettings(model, activeConfigName, result, targetFileName);

            // D-2: гейт AutoSyncMaterials действует ТОЛЬКО на синхронизацию материалов
            // (поиск/чтение sldmat-баз, NormalizeMaterialFB, формирование Материал_ФБ/Материал/Сортамент/ГОСТ_*).
            // Реквизиты (Обозначение/Наименование/Разраб./Организация) и масса уже синхронизированы
            // вызовом ApplyUserSettings выше и этим флагом НЕ гейтятся.
            if (!IsAutoSyncMaterialsEnabled())
            {
                result.Success = true;
                result.Message = "Автосинхронизация материалов отключена (реквизиты и масса синхронизированы)";
                return result;
            }

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
            _lastApplyChanged = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeySettings))
                {
                    if (key != null)
                    {
                        string author = key.GetValue("Author") as string;
                        string checker = key.GetValue("Checker") as string;
                        string org = key.GetValue("Organization") as string;
                        int autoMass = ReadIntSafe(key, "AutoMass", 1);
                        int decimals = ReadIntSafe(key, "MassDecimals", 2);
                        int autoSplitName = ReadIntSafe(key, "AutoSplitName", 1);

                        if (autoSplitName == 1 && !IsStandardOrPurchasedPart(model))
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
                                SetDatePropIfEmpty(cpmGen, "п_Разраб_Дата", dateRu);
                                SetDatePropIfEmpty(cpmGen, "DrawnDate", dateIso);
                            }
                            if (!string.IsNullOrEmpty(checker))
                            {
                                SetProp(cpmGen, "Пров.", checker);
                                SetProp(cpmGen, "Проверил", checker);
                                SetProp(cpmGen, "п_Пров", checker);
                                SetProp(cpmGen, "CheckedBy", checker);
                                SetDatePropIfEmpty(cpmGen, "п_Пров_Дата", dateRu);
                            }
                            if (!string.IsNullOrEmpty(org))
                            {
                                SetProp(cpmGen, "Контора", org);
                                SetProp(cpmGen, "Организация", org);
                                SetProp(cpmGen, "Организация_ФБ", org);
                                SetProp(cpmGen, "Компания", org);
                                SetProp(cpmGen, "Firm", org);
                                SetProp(cpmGen, "Organization", org);
                            }
                        }

                        // Write configuration-specific properties across all configurations
                        // MProp calls swModel.CustomInfo2(sConfigName, "Контора") which only queries configuration properties!
                        string[] allCfgs = model.GetConfigurationNames() as string[];
                        if (allCfgs != null && allCfgs.Length > 0)
                        {
                            foreach (string cfg in allCfgs)
                            {
                                if (string.IsNullOrEmpty(cfg)) continue;
                                CustomPropertyManager cpmCfg = model.Extension.get_CustomPropertyManager(cfg);
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
                                        SetDatePropIfEmpty(cpmCfg, "п_Разраб_Дата", dateRu);
                                        SetDatePropIfEmpty(cpmCfg, "DrawnDate", dateIso);
                                    }
                                    if (!string.IsNullOrEmpty(checker))
                                    {
                                        SetProp(cpmCfg, "Пров.", checker);
                                        SetProp(cpmCfg, "Проверил", checker);
                                        SetProp(cpmCfg, "п_Пров", checker);
                                        SetProp(cpmCfg, "CheckedBy", checker);
                                        SetDatePropIfEmpty(cpmCfg, "п_Пров_Дата", dateRu);
                                    }
                                    if (!string.IsNullOrEmpty(org))
                                    {
                                        SetProp(cpmCfg, "Контора", org);
                                        SetProp(cpmCfg, "Организация", org);
                                        SetProp(cpmCfg, "Организация_ФБ", org);
                                        SetProp(cpmCfg, "Компания", org);
                                        SetProp(cpmCfg, "Firm", org);
                                        SetProp(cpmCfg, "Organization", org);
                                    }
                                }
                            }
                        }
                        else if (!string.IsNullOrEmpty(configName))
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
                                    SetDatePropIfEmpty(cpmCfg, "п_Разраб_Дата", dateRu);
                                    SetDatePropIfEmpty(cpmCfg, "DrawnDate", dateIso);
                                }
                                if (!string.IsNullOrEmpty(checker))
                                {
                                    SetProp(cpmCfg, "Пров.", checker);
                                    SetProp(cpmCfg, "Проверил", checker);
                                    SetProp(cpmCfg, "п_Пров", checker);
                                    SetProp(cpmCfg, "CheckedBy", checker);
                                    SetDatePropIfEmpty(cpmCfg, "п_Пров_Дата", dateRu);
                                }
                                if (!string.IsNullOrEmpty(org))
                                {
                                    SetProp(cpmCfg, "Контора", org);
                                    SetProp(cpmCfg, "Организация", org);
                                    SetProp(cpmCfg, "Организация_ФБ", org);
                                    SetProp(cpmCfg, "Компания", org);
                                    SetProp(cpmCfg, "Firm", org);
                                    SetProp(cpmCfg, "Organization", org);
                                }
                            }
                        }

                        if (autoMass == 1 && model.GetType() != (int)swDocumentTypes_e.swDocDRAWING && !IsStandardOrPurchasedPart(model))
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
                // B3: чистка legacy-артефактов выполняется ДО анализа существующих значений —
                // дублирующиеся <FONT>-теги сами содержат "<FONT", и ранний return ниже при
                // hasDynamicMass иначе сделал бы CleanModelMassTags недостижимым для основного
                // класса «грязных» значений.
                CleanModelMassTags(model);
                string existingGenMass = GetProp(cpmGen, "Масса");
                string existingGenMassFB = GetProp(cpmGen, "Масса_ФБ");

                // Динамическим считаем ТОЛЬКО однозначно живое значение: "SW-Mass..." или
                // '$PRP'-выражение в СЫРОМ значении. Собственный вывод надстройки
                // ("<FONT size=3.5>X") — статическая копия и пересчитывается при каждом
                // сохранении (compare-and-skip в SetProp отсечёт запись без изменений).
                string massFBRaw = GetPropRaw(cpmGen, "Масса_ФБ") ?? existingGenMassFB;
                bool hasDynamicMassFB = !string.IsNullOrEmpty(massFBRaw) &&
                    (massFBRaw.Contains("SW-Mass") || massFBRaw.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0);
                if (hasDynamicMassFB)
                {
                    return;
                }
                // Шаблонное «Масса»="SW-Mass" (asmdot) — живая масса для BOM/PDM: не трогаем.
                bool massPlainIsDynamic = !string.IsNullOrEmpty(existingGenMass) && existingGenMass.Contains("SW-Mass");

                MassProperty massProp = (MassProperty)model.Extension.CreateMassProperty();
                if (massProp != null)
                {
                    double massKg = massProp.Mass;
                    if (massKg > 0.00001)
                    {
                        if (result != null) result.MassKg = massKg;

                        if (decimals < 0) decimals = 0;
                        if (decimals > 4) decimals = 4;

                        // ГОСТ R-4: фиксированные нули ("0.00" вместо "0.##"), чтобы 14,50 не превращалось в 14,5;
                        // десятичный разделитель ru-RU (запятая) сохраняется.
                        string formatStr = "0." + new string('0', decimals);
                        string massStr = massKg.ToString(formatStr, RuCulture);
                        // ГОСТ 2.104 (графа 5): масса центрируется в ячейке. Однострочный формат:
                        // прежний вариант с крошечной первой строкой ("<FONT size=1> \n<FONT size=3.5>X")
                        // прижимал число к нижней кромке ячейки (зазор ~10 мм сверху).
                        string massFB = string.Format("<FONT size=3.5>{0}", massStr);

                        SetProp(cpmGen, "Масса_ФБ", massFB);
                        // «Масса» перезаписывается только если там нет динамического значения
                        // (шаблонное "SW-Mass" из asmdot сохраняется — это живая масса для BOM/PDM).
                        if (!massPlainIsDynamic)
                        {
                            SetProp(cpmGen, "Масса", massFB);
                        }

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

                        // D-4: при пустом configName одно и то же значение массы пишется ТОЛЬКО в общие
                        // свойства документа (custom properties с ключом ""). Конфигурационные менеджеры
                        // не затрагиваются — записи одинаковой массы во ВСЕ конфигурации удалены.
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// D-9 / коммит b932330: чистка искусственных артефактов в свойствах «Масса_ФБ» и «Наименование» модели:
        /// (а) литеральные последовательности "\n" (backslash + 'n' как текст) приводятся к реальному
        ///     переводу строки, как его формирует генератор массы ("<FONT size=1> \n<FONT size=3.5>...");
        /// (б) дублирующие друг друга подряд идущие одинаковые теги <FONT size=N> схлопываются в один
        ///     (в начале значения остаётся ровно один <FONT size=1> и один <FONT size=3.5>);
        /// (в) удаляются ведущие/хвостовые пробельные артефакты.
        /// Свойство «Материал_ФБ» и корректные значения не затрагиваются (идемпотентность).
        /// Возвращает true, если хотя бы одно свойство было очищено и перезаписано.
        /// </summary>
        public static bool CleanModelMassTags(ModelDoc2 doc)
        {
            if (doc == null) return false;
            bool cleanedAny = false;
            try
            {
                // Общие (сводные) свойства документа
                CustomPropertyManager cpmGen = doc.Extension.get_CustomPropertyManager("");
                if (cpmGen != null)
                {
                    cleanedAny = CleanMassTagProperty(cpmGen, "Масса_ФБ") || cleanedAny;
                    cleanedAny = CleanMassTagProperty(cpmGen, "Масса") || cleanedAny;
                    cleanedAny = CleanMassTagProperty(cpmGen, "Наименование") || cleanedAny;
                }

                // Конфигурационные копии тех же свойств
                string[] cfgNames = doc.GetConfigurationNames() as string[];
                if (cfgNames != null)
                {
                    foreach (string cfg in cfgNames)
                    {
                        if (string.IsNullOrEmpty(cfg)) continue;
                        try
                        {
                            CustomPropertyManager cpmCfg = doc.Extension.get_CustomPropertyManager(cfg);
                            if (cpmCfg != null)
                            {
                                cleanedAny = CleanMassTagProperty(cpmCfg, "Масса_ФБ") || cleanedAny;
                                cleanedAny = CleanMassTagProperty(cpmCfg, "Масса") || cleanedAny;
                                cleanedAny = CleanMassTagProperty(cpmCfg, "Наименование") || cleanedAny;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return cleanedAny;
        }

        private static bool CleanMassTagProperty(CustomPropertyManager cpm, string propName)
        {
            if (cpm == null || string.IsNullOrEmpty(propName)) return false;
            try
            {
                // «Материал_ФБ» не трогаем: SWPlus MProp полагается на его точную STACK/FONT разметку
                if (propName.IndexOf("Материал", StringComparison.OrdinalIgnoreCase) >= 0) return false;

                // Читаем сырое значение (без Trim) — иначе хвостовые пробельные артефакты не будут обнаружены
                string val = null;
                string resVal = null;
                cpm.Get4(propName, false, out val, out resVal);
                // Сырое значение первично: резолв expression-свойства перезаписал бы
                // выражение пользователя литералом.
                string raw = !string.IsNullOrWhiteSpace(val) ? val : resVal;
                if (string.IsNullOrWhiteSpace(raw)) return false;

                bool changed = false;
                string cleaned = CleanMassTagValue(raw, out changed);
                if (!changed) return false;
                if (string.IsNullOrWhiteSpace(cleaned)) return false;

                SetProp(cpm, propName, cleaned); // Add3 с заменой + Set2
                return true;
            }
            catch { }
            return false;
        }

        private static string CleanMassTagValue(string value, out bool changed)
        {
            changed = false;
            if (string.IsNullOrEmpty(value)) return value;

            string result = value;

            // (а) литеральные "\n" (backslash + 'n' как текст) -> реальный перевод строки
            if (result.IndexOf("\\n", StringComparison.Ordinal) >= 0)
            {
                result = result.Replace("\\n", "\n");
            }

            // (б) схлопывание подряд идущих ОДИНАКОВЫХ тегов <FONT size=N> в один;
            // разные теги (size=1 и size=3.5) не схлопываются — это корректная пара генератора массы
            result = Regex.Replace(
                result,
                @"(<FONT\s+size=(?<size>[0-9]+(?:[.,][0-9]+)?>)[^<]*)(?:\s*<FONT\s+size=\k<size>>)+",
                "$1",
                RegexOptions.IgnoreCase);

            // (б2) legacy-формат генератора массы ("<FONT size=1> \n<FONT size=3.5>X") прижимал
            // число к нижней кромке ячейки массы — нормализуем к однострочному виду
            // "<FONT size=3.5>X" (центрирование по ГОСТ 2.104, графа 5).
            result = Regex.Replace(
                result,
                @"^\s*<FONT\s+size=1\s*>\s*[\r\n]+\s*<FONT\s+size=3\.5\s*>",
                "<FONT size=3.5>",
                RegexOptions.IgnoreCase);

            // (в) ведущие/хвостовые пробельные артефакты всего значения
            result = result.Trim(' ', '\t', '\r', '\n');

            changed = !string.Equals(result, value, StringComparison.Ordinal);
            return result;
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

        private static void SetProp(CustomPropertyManager cpm, string name, string value)
        {
            try
            {
                // Compare-and-skip: запись выполняется только при фактическом изменении значения.
                // Это исключает «грязнение» документа (dirty flag) при каждом сохранении, когда
                // реквизиты не менялись, и снижает число COM-вызовов в обработчиках событий.
                // Compare-and-skip: сравнение по СЫРОМУ значению без Trim. Резолв (GetProp)
                // триммит и разворачивает выражения, из-за чего свойства с значимыми краевыми
                // пробелами (« СБ» — ведущий пробел обязателен для склейки в форматке) либо
                // перезаписывались бы вечно, либо шаблонное «СБ» считалось бы равным « СБ».
                string curRaw = GetPropRaw(cpm, name);
                if (string.Equals(curRaw ?? "", value ?? "", StringComparison.Ordinal))
                {
                    return;
                }
                string cur = GetProp(cpm, name);
                if (string.Equals(cur ?? "", value == null ? "" : value.Trim(), StringComparison.Ordinal) &&
                    (curRaw == null || curRaw.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) < 0))
                {
                    // Значимое исключение: совпадение ТОЛЬКО по триммленному резолву при
                    // различии сырого вида (например шаблонное «СБ» vs требуемое « СБ») —
                    // НЕ skip: точный вид свойства важен для штампа.
                    if (string.Equals((curRaw ?? "").Trim(), (value ?? "").Trim(), StringComparison.Ordinal) &&
                        (curRaw ?? "") != (value ?? ""))
                    {
                        // разница только в краевых пробелах — приводим к требуемому виду
                    }
                    else
                    {
                        return;
                    }
                }
                // Свойства-выражения ($PRP:"SW-File Name" из шаблонов prtdot/asmdot) не
                // перезаписываются через Add3/ReplaceValue — сначала удаляем, затем создаём
                // как обычное текстовое свойство.
                if (curRaw != null && curRaw.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { cpm.Delete2(name); } catch { }
                }
                cpm.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                cpm.Set2(name, value);
                _lastApplyChanged = true;
            }
            catch { }
        }

        // Флаг фактического изменения свойств последним ApplyUserSettings/SetProp-конвейером.
        // Используется SyncDrawing для условного ForceRebuild3 (ребилд только при изменениях).
        private static bool _lastApplyChanged;

        public static bool WasLastApplyChanged()
        {
            return _lastApplyChanged;
        }

        // #6: дата разработки/проверки проставляется только один раз — при пустом поле.
        // Безусловная перезапись датой «сегодня» делала старые чертежи «грязными» при
        // каждом сохранении/переключении и стирала историческую дату разработки.
        private static void SetDatePropIfEmpty(CustomPropertyManager cpm, string name, string value)
        {
            try
            {
                if (string.IsNullOrEmpty(GetProp(cpm, name)))
                {
                    SetProp(cpm, name, value);
                }
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

        // Сырое (не резолвленное) значение свойства: для '$PRP:"-выражений GetProp возвращает
        // РЕзолв (например имя файла), из-за чего проверки Contains("$PRP") на нём никогда не
        // срабатывали и шаблонные expression-свойства не распознавались как шаблонные.
        private static string GetPropRaw(CustomPropertyManager cpm, string name)
        {
            if (cpm == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                string val = null, resVal = null;
                cpm.Get4(name, false, out val, out resVal);
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

                // ЧЕРТЕЖИ не разбираются по имени файла: графы штампа ($PRPSHEET) обязаны
                // резолвиться из модели первого вида. Обозначение/наименование, выведенные
                // из имени файла чертежа (например "TestDrawing_Tube.slddrw" -> "TestDrawing_Tube"),
                // записались бы в свойства ЧЕРТЕЖА и перекрыли подстановку из модели.
                if (docType == (int)swDocumentTypes_e.swDocDRAWING) return;
                if (IsStandardOrPurchasedPart(model)) return;
                bool isAssembly = docType == (int)swDocumentTypes_e.swDocASSEMBLY;

                // 1. Determine base designation and title
                CustomPropertyManager cpmGen = model.Extension.get_CustomPropertyManager("");
                // Детекция шаблонов — по СЫРОМУ значению: резолв '$PRP:"SW-File Name"' возвращает
                // имя файла, и проверки Contains("$PRP") на резолве никогда не срабатывали.
                string existingGenDesigRaw = GetPropRaw(cpmGen, "Обозначение");
                string existingGenDesig = !string.IsNullOrWhiteSpace(existingGenDesigRaw) ? existingGenDesigRaw : GetProp(cpmGen, "Обозначение");
                string existingGenTitleRaw = GetPropRaw(cpmGen, "Наименование");
                string existingGenTitle = !string.IsNullOrWhiteSpace(existingGenTitleRaw) ? existingGenTitleRaw : GetProp(cpmGen, "Наименование");

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

                // Стабильность шифра между проходами синхронизации: после первой же записи
                // «Обозначение» становится чистым (без « СБ»), и StripExecutionSuffix по нему
                // больше не видит шифра — тогда шифр восстанавливается из разбора ИМЕНИ ФАЙЛА
                // (первоисточник конструктора: «...010 СБ Рама...»), иначе «фантом-делит»
                // ниже стирал только что записанную «Сборка1_ФБ» на повторном проходе.
                if (string.IsNullOrEmpty(docCode) && !string.IsNullOrWhiteSpace(parsedDesig))
                {
                    string execIgnored;
                    string docCodeFromFile;
                    StripExecutionSuffix(parsedDesig, out execIgnored, out docCodeFromFile);
                    if (!string.IsNullOrEmpty(docCodeFromFile))
                    {
                        docCode = docCodeFromFile;
                    }
                }

                // ЕСКД-разделение для сборок (ГОСТ 2.102/2.104): «СБ» — шифр СБОРОЧНОГО ЧЕРТЕЖА,
                // а не изделия. В модели «Обозначение» хранится ЧИСТЫМ (так оно попадает в
                // спецификацию по ГОСТ 2.106 без шифра), а «СБ» живёт в свойстве «Сборка1_ФБ»,
                // которое форматка склеивает с обозначением в графе 1 и в графе 26:
                //   $PRPSHEET:"Обозначение"$PRPSHEET:"Сборка1_ФБ"
                // Ведущий пробел обязателен: склейка в заметке выполняется без разделителя.
                string assemblyCodeFB = (isAssembly && !string.IsNullOrWhiteSpace(docCode))
                    ? " " + docCode.Trim()
                    : "";

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
                    // Для сборок docCode (« СБ») в обозначение НЕ включается — он уходит
                    // в «Сборка1_ФБ» (склейка в форматке). Для деталей/doc-кодов — как прежде.
                    string baseFullDesig = BuildExecutionDesignation(rootBaseDesig, "", isAssembly ? "" : docCode);
                    if (!string.IsNullOrEmpty(baseFullDesig))
                    {
                        string curGenDesig = GetPropRaw(cpmGen, "Обозначение") ?? GetProp(cpmGen, "Обозначение");
                        // Миграция двойного шифра: если обозначение сборки уже несёт хвост " СБ"
                        // (записан прежней версией надстройки), заменяем на чистый корень —
                        // иначе штамп показал бы "...010 СБ" + " СБ" = двойное СБ.
                        bool migrateAssemblySb = isAssembly &&
                            !string.IsNullOrEmpty(curGenDesig) &&
                            curGenDesig.TrimEnd().EndsWith(" СБ", StringComparison.OrdinalIgnoreCase);
                        if (string.IsNullOrEmpty(curGenDesig) || isExistingDesigTemplate || migrateAssemblySb)
                        {
                            SetProp(cpmGen, "Обозначение", baseFullDesig);
                            SetProp(cpmGen, "PartNo", baseFullDesig);
                            SetProp(cpmGen, "Number", baseFullDesig);
                        }
                    }
                    if (!string.IsNullOrEmpty(assemblyCodeFB))
                    {
                        SetProp(cpmGen, "Сборка1_ФБ", assemblyCodeFB);
                        // ГОСТ 2.109: под наименованием сборочного чертежа указывают
                        // «Сборочный чертёж» (вторая строка графы 2, заметка MYPRP3).
                        if (string.IsNullOrWhiteSpace(GetProp(cpmGen, "Сборка2_ФБ")))
                        {
                            SetProp(cpmGen, "Сборка2_ФБ", "Сборочный чертёж");
                        }
                    }
                    else if (isAssembly)
                    {
                        // docCode исчез (файл переименован без « СБ», обозначение задано
                        // вручную) — устаревший шифр обязан уйти, иначе форматка склеит
                        // фантомное «...020 СБ».
                        try { cpmGen.Delete2("Сборка1_ФБ"); } catch { }
                    }
                    if (!string.IsNullOrEmpty(effectiveTitle))
                    {
                        string curGenTitle = GetPropRaw(cpmGen, "Наименование") ?? GetProp(cpmGen, "Наименование");
                        string curGenTitleFB = GetPropRaw(cpmGen, "Наименование_ФБ") ?? GetProp(cpmGen, "Наименование_ФБ");
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
                        else if (curGenTitleFB.IndexOf('<') < 0)
                        {
                            // Миграция на двухстрочный формат: старый однострочный вывод
                            // (без тегов) предыдущей версии заменяется переносом. Значения
                            // с тегами (<FONT/<STACK) считаем пользовательскими — не трогаем.
                            string titleFBNew = FormatEskdTitleFB(effectiveTitle);
                            if (!string.Equals(curGenTitleFB, titleFBNew, StringComparison.Ordinal))
                            {
                                SetProp(cpmGen, "Наименование_ФБ", titleFBNew);
                            }
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
                            string existingCfgDesig = GetPropRaw(cpmCfg, "Обозначение") ?? GetProp(cpmCfg, "Обозначение");

                            string configDesignation = "";

                            if (recognized)
                            {
                                if (isBaseConfig || string.IsNullOrEmpty(execCode))
                                {
                                    configDesignation = BuildExecutionDesignation(rootBaseDesig, "", isAssembly ? "" : docCode);
                                }
                                else
                                {
                                    configDesignation = BuildExecutionDesignation(rootBaseDesig, execCode, isAssembly ? "" : docCode);
                                }
                            }
                            else
                            {
                                // Configuration name didn't specify an execution (e.g. "SpecialVariant").
                                // If existing designation has an execution suffix for this base, preserve it.
                                // D-11: если имя конфигурации не распознано, но в существующем обозначении
                                // был суффикс исполнения — сохраняем его: root + foundExec + docCode.
                                if (!string.IsNullOrWhiteSpace(existingCfgDesig) && !existingCfgDesig.Contains("$PRP"))
                                {
                                    configDesignation = existingCfgDesig;
                                }
                                else
                                {
                                    configDesignation = BuildExecutionDesignation(rootBaseDesig, foundExecInBase, isAssembly ? "" : docCode);
                                }
                            }

                            // Write configuration properties
                            if (cpmCfg != null)
                            {
                                if (!string.IsNullOrEmpty(configDesignation))
                                {
                                    string curCfgDesig = GetPropRaw(cpmCfg, "Обозначение") ?? GetProp(cpmCfg, "Обозначение");
                                    bool migrateCfgSb = isAssembly &&
                                        !string.IsNullOrEmpty(curCfgDesig) &&
                                        curCfgDesig.TrimEnd().EndsWith(" СБ", StringComparison.OrdinalIgnoreCase) &&
                                        !curCfgDesig.Equals(configDesignation, StringComparison.OrdinalIgnoreCase);
                                    if (string.IsNullOrEmpty(curCfgDesig) || curCfgDesig.Contains("$PRP") || isExistingDesigTemplate || migrateCfgSb)
                                    {
                                        SetProp(cpmCfg, "Обозначение", configDesignation);
                                        SetProp(cpmCfg, "PartNo", configDesignation);
                                        SetProp(cpmCfg, "Number", configDesignation);
                                    }
                                    // Конфигурационные копии шифра сборочного чертежа (графа 1/26
                                    // штампа резолвят свойство конфигурации раньше общего)
                                    if (!string.IsNullOrEmpty(assemblyCodeFB))
                                    {
                                        SetProp(cpmCfg, "Сборка1_ФБ", assemblyCodeFB);
                                        if (string.IsNullOrWhiteSpace(GetProp(cpmCfg, "Сборка2_ФБ")))
                                        {
                                            SetProp(cpmCfg, "Сборка2_ФБ", "Сборочный чертёж");
                                        }
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
                                    string curCfgTitle = GetPropRaw(cpmCfg, "Наименование") ?? GetProp(cpmCfg, "Наименование");
                                    string curCfgTitleFB = GetPropRaw(cpmCfg, "Наименование_ФБ") ?? GetProp(cpmCfg, "Наименование_ФБ");
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

                            // Synchronize SolidWorks Bill of Materials (BOM) Options — УДАЛЕНО.
                            // Установка UseAlternateNameInBOM=true + AlternateName разрывала резолв
                            // $PRPSHEET:"Обозначение" для видов этой модели: графа 1 и графа 26 штампа
                            // оставались пустыми (подтверждено живым A/B-тестом: без AlternateName
                            // обозначение подтягивается мгновенно). Обозначения в спецификации
                            // резолвятся из конфигурационных свойств «Обозначение», которые
                            // записаны выше, — альтернативные имена не требуются.
                        }
                    }
                }
                catch { }
            }
            catch { }
        }
    }
}
