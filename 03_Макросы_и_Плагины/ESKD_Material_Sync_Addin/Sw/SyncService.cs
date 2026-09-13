using System;
using System.Collections.Generic;
using System.IO;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    public sealed class SyncRequest
    {
        /// <summary>Путь, по имени которого строятся обозначение и наименование.</summary>
        public string TargetPath;
        /// <summary>Прежний путь документа (для правила происхождения при «Сохранить как»).</summary>
        public string PreviousPath;
        public string Reason = "";
        public bool Names = true;
        public bool Signatures = true;
        public bool Materials = true;
        public bool Mass = true;
        public bool DryRun;
    }

    public sealed class SyncReport
    {
        public int Changes;
        public int Failures;
        public bool Skipped;
        public string SkipReason = "";
        public readonly List<string> Operations = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        public override string ToString()
        {
            return string.Format("изменений: {0}, ошибок: {1}, предупреждений: {2}{3}", Changes, Failures, Warnings.Count,
                Skipped ? ", пропущено: " + SkipReason : "");
        }

        public const int StatusLineLimit = 200;

        /// <summary>Строка состояния SolidWorks после автоматической синхронизации: первое предупреждение и их число (Д-38).</summary>
        public string StatusLine()
        {
            if (Warnings.Count == 0) return "";
            string head = Warnings.Count == 1 ? "ЕСКД: " : string.Format("ЕСКД, предупреждений {0}: ", Warnings.Count);
            string text = head + Warnings[0];
            return text.Length > StatusLineLimit ? text.Substring(0, StatusLineLimit - 1) + "…" : text;
        }
    }

    /// <summary>
    /// Синхронизация реквизитов детали и сборки (план, §3.2–3.4). Уровни хранения повторяют MProp:
    /// обозначение — общие и конфигурации; наименование, «Наименование_ФБ», «Конструктор» — общие;
    /// «Проверил», «Контора», «Сборка1_ФБ», «Сборка2_ФБ», масса и материал — конфигурации.
    /// </summary>
    public static class SyncService
    {
        public static PropertyDictionary Dictionary(Settings settings)
        {
            string addinDir = Path.GetDirectoryName(typeof(SyncService).Assembly.Location) ?? "";
            return PropertyDictionary.Load(PropertyDictionary.Locate(addinDir, settings.DictionaryPath));
        }

        public static SyncReport SyncModel(ISldWorks app, ModelDoc2 doc, SyncRequest req)
        {
            SyncReport report = new SyncReport();
            if (doc == null) { report.Skipped = true; report.SkipReason = "нет документа"; return report; }
            int type = doc.GetType();
            if (type != (int)swDocumentTypes_e.swDocPART && type != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                report.Skipped = true;
                report.SkipReason = "не деталь и не сборка";
                return report;
            }
            Settings settings = Settings.Read();
            PropertyDictionary dict = Dictionary(settings);
            PropertyWriter w = new PropertyWriter(doc, req.DryRun || settings.DryRun);

            if (IsProtected(w, doc))
            {
                report.Skipped = true;
                report.SkipReason = "стандартное или покупное изделие";
                return report;
            }

            bool isAssembly = type == (int)swDocumentTypes_e.swDocASSEMBLY;
            try
            {
                if (req.Names && settings.AutoSplitName && dict.FileNameSplit)
                    SyncNames(w, doc, dict, settings, req, isAssembly, report);
                if (req.Signatures)
                    SyncSignatures(w, dict, settings);
                if (req.Materials && !isAssembly && settings.AutoSyncMaterials)
                    SyncMaterials(w, app, (PartDoc)doc, dict, report);
                if (req.Mass && settings.AutoMass)
                    SyncMass(w, doc, dict, report);
            }
            catch (Exception ex)
            {
                Log.Error("SyncModel " + req.Reason, ex);
                report.Failures++;
            }
            report.Changes = w.Changes;
            report.Failures += w.Failures;
            report.Operations.AddRange(w.Operations);
            foreach (string warning in report.Warnings) Log.Warn(warning);
            if (w.Operations.Count > 0)
                Log.Info(string.Format("Синхронизация ({0}): {1}{2}", req.Reason, report,
                    w.Operations.Count > 0 ? "\r\n    " + string.Join("\r\n    ", w.Operations.ToArray()) : ""));
            return report;
        }

        // ------------------------------------------------------------------ защита
        public static bool IsProtected(PropertyWriter w, ModelDoc2 doc)
        {
            List<string> levels = new List<string> { "", w.ActiveConfigurationName() };
            foreach (string cfg in levels)
            {
                string section = (w.Resolved(cfg, "Раздел") ?? "").Trim().ToLowerInvariant();
                // «ЭМ-Детали» и «ЭМ-Сборочные единицы» заполняются как обычные; «ЭМ-Стандартные изделия», «ЭМ-Прочие изделия»
                // и «ЭМ-Материалы» защищены по словам раздела (Н-15).
                if (section.Contains("стандартн") || section.Contains("прочи") || section.Contains("покупн") ||
                    section.Contains("материал")) return true;
                string fastener = (w.Resolved(cfg, "IsFastener") ?? "").Trim().ToLowerInvariant();
                if (fastener == "1" || fastener == "true" || fastener == "да" || fastener == "yes") return true;
                foreach (string purchased in new[] { "Наименование_ВП", "Поставщик", "Код_Продукции", "Обозначение_ДНП", "Справочный_номер_ВП" })
                {
                    if (!string.IsNullOrEmpty(w.Resolved(cfg, purchased))) return true;
                }
            }
            string path = DocInfo.PathOf(doc).ToLowerInvariant();
            return path.Contains("\\toolbox\\") || path.Contains("solidworks data");
        }

        // ------------------------------------------------------------------ обозначение и наименование
        private static void SyncNames(PropertyWriter w, ModelDoc2 doc, PropertyDictionary dict, Settings settings,
            SyncRequest req, bool isAssembly, SyncReport report)
        {
            string number = dict[Role.Number];
            string title = dict[Role.Description];
            string titleFb = dict[Role.DescriptionMulti];

            string target = req.TargetPath;
            if (string.IsNullOrEmpty(target)) target = DocInfo.PathOf(doc);
            if (string.IsNullOrEmpty(target)) target = DocInfo.TitleOf(doc);
            ParsedName now = DesignationParser.Parse(target, dict.NameSeparator);
            if (now.IsTemplateName || (!now.HasDesignation && string.IsNullOrEmpty(now.Title))) return;
            ParsedName before = null;
            if (!string.IsNullOrEmpty(req.PreviousPath) &&
                !string.Equals(req.PreviousPath, target, StringComparison.OrdinalIgnoreCase))
            {
                before = DesignationParser.Parse(req.PreviousPath, dict.NameSeparator);
            }

            string code = now.DocCode;
            string expected = DesignationParser.Build(now.Root, now.Execution, isAssembly ? "" : code);
            string expectedBefore = before != null && before.HasDesignation
                ? DesignationParser.Build(before.Root, before.Execution, isAssembly ? "" : before.DocCode) : null;
            bool manual = (w.Raw("", "RenameSWP") ?? "").Trim() == "1";
            string current = w.Raw("", number);
            // Миграция v5: у сборки в обозначении мог остаться код « СБ» — это производное значение.
            if (isAssembly && current != null && !string.IsNullOrEmpty(code) &&
                string.Equals(current.Trim(), DesignationParser.Build(now.Root, now.Execution, code), StringComparison.Ordinal))
            {
                current = "";
            }
            Provenance designation = now.HasDesignation
                ? ProvenanceRule.Classify(current, expected, expectedBefore, manual) : Provenance.Manual;
            bool designationDerived = designation != Provenance.Manual;
            if (ProvenanceRule.ShouldWrite(designation) && expected.Length > 0)
            {
                w.Set("", number, expected);
            }
            else if (designation == Provenance.Manual && now.HasDesignation && !manual &&
                     !string.Equals((current ?? "").Trim(), expected, StringComparison.Ordinal))
            {
                report.Warnings.Add(string.Format("Обозначение «{0}» не совпадает с именем файла «{1}» и оставлено без изменений",
                    current, now.BaseName));
            }

            // Обозначения конфигураций (исполнения по ГОСТ 2.113)
            if (designationDerived && now.HasDesignation)
            {
                foreach (string cfg in w.ConfigurationNames())
                {
                    string execution;
                    bool isBase;
                    bool recognized = DesignationParser.ExtractExecutionFromConfigName(RootConfigurationName(doc, cfg), out execution, out isBase);
                    string cfgCurrent = w.Raw(cfg, number);
                    string cfgExpected;
                    if (recognized)
                    {
                        cfgExpected = DesignationParser.Build(now.Root, isBase ? now.Execution : execution, isAssembly ? "" : code);
                    }
                    else if (!PropertyWriter.IsEmptyOrTemplate(cfgCurrent) && !DerivedFromBefore(cfgCurrent, before, isAssembly))
                    {
                        continue;
                    }
                    else
                    {
                        cfgExpected = expected;
                    }
                    w.Set(cfg, number, cfgExpected);
                    string exec, docCode;
                    DesignationParser.StripExecutionSuffix(cfgExpected, out exec, out docCode);
                    w.SetIfEmpty(cfg, "Исполнение", string.IsNullOrEmpty(exec) ? "0" : "2");
                }
            }

            // Сборки: код документа и вторая строка графы 1 (уровень конфигурации, как у MProp)
            if (isAssembly && designationDerived)
            {
                string codeProp = dict[Role.DocCode];
                string codeText = dict[Role.DocDescription];
                string codeValue = string.IsNullOrEmpty(code) ? "" : (settings.LegacyAssemblyCodeSpace ? " " + code : code);
                foreach (string cfg in w.ConfigurationNames())
                {
                    if (codeValue.Length > 0)
                    {
                        w.Set(cfg, codeProp, codeValue);
                        string second = w.Raw(cfg, codeText);
                        if (PropertyWriter.IsEmptyOrTemplate(second) || second.IndexOf("<FONT", StringComparison.OrdinalIgnoreCase) >= 0)
                            w.Set(cfg, codeText, "Сборочный чертёж");
                    }
                    else if (IsDerivedCode(w.Raw(cfg, codeProp)))
                    {
                        w.Delete(cfg, codeProp);
                    }
                }
                string generalCode = w.Raw("", codeProp);
                if (generalCode != null && IsDerivedCode(generalCode))
                {
                    if (codeValue.Length > 0) w.Set("", codeProp, codeValue);
                    else w.Delete("", codeProp);
                }
            }

            // Наименование и «Наименование_ФБ» — общие свойства
            if (string.IsNullOrEmpty(now.Title)) return;
            string currentTitle = w.Raw("", title);
            Provenance titleState = ProvenanceRule.Classify(currentTitle, now.Title, before != null ? before.Title : null, false);
            string effective = currentTitle ?? "";
            if (ProvenanceRule.ShouldWrite(titleState))
            {
                w.Set("", title, now.Title);
                effective = now.Title;
            }
            else if (titleState == Provenance.Current)
            {
                effective = now.Title;
            }
            if (PropertyWriter.IsEmptyOrTemplate(effective)) return;
            string wantedFb = SwPlusMarkup.HasMarkup(effective) ? BchRecord.ShortTitle(effective) : SwPlusMarkup.TitleForStamp(effective);
            string currentFb = w.Raw("", titleFb);
            bool fbDerived = PropertyWriter.IsEmptyOrTemplate(currentFb) ||
                             (before != null && !string.IsNullOrEmpty(before.Title) &&
                              (currentFb == SwPlusMarkup.TitleForStamp(before.Title) || currentFb == before.Title)) ||
                             (!SwPlusMarkup.HasMarkup(currentFb) && currentFb.Replace("\n", " ") == effective);
            if (fbDerived) w.Set("", titleFb, wantedFb);

            // Копии наименования в конфигурациях (оставлены v5) обновляются, только если они производные:
            // иначе устаревшая копия затеняет общее свойство в штампе и спецификации.
            foreach (string cfg in w.ConfigurationNames())
            {
                string cfgTitle = w.Raw(cfg, title);
                if (cfgTitle != null && (PropertyWriter.IsEmptyOrTemplate(cfgTitle) ||
                    (before != null && cfgTitle == before.Title) || cfgTitle == currentTitle))
                {
                    w.Set(cfg, title, effective);
                }
                string cfgFb = w.Raw(cfg, titleFb);
                if (cfgFb != null && !SwPlusMarkup.HasMarkup(cfgFb) && (PropertyWriter.IsEmptyOrTemplate(cfgFb) ||
                    (before != null && !string.IsNullOrEmpty(before.Title) && cfgFb.Replace("\n", " ") == before.Title) ||
                    cfgFb == currentFb))
                {
                    w.Set(cfg, titleFb, wantedFb);
                }
            }
        }

        private static bool DerivedFromBefore(string value, ParsedName before, bool isAssembly)
        {
            if (before == null || !before.HasDesignation || string.IsNullOrEmpty(value)) return false;
            string root = DesignationParser.Build(before.Root, null, "");
            return value.StartsWith(root, StringComparison.Ordinal);
        }

        private static bool IsDerivedCode(string value)
        {
            if (value == null) return false;
            string t = value.Trim();
            return t.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(t, "^(" + DesignationParser.DocCodes + ")$");
        }

        private static string RootConfigurationName(ModelDoc2 doc, string cfg)
        {
            try
            {
                Configuration c = doc.GetConfigurationByName(cfg) as Configuration;
                while (c != null && c.IsDerived())
                {
                    Configuration parent = c.GetParent() as Configuration;
                    if (parent == null) break;
                    c = parent;
                }
                return c != null ? c.Name : cfg;
            }
            catch (Exception ex)
            {
                Log.Error("RootConfigurationName " + cfg, ex);
                return cfg;
            }
        }

        // ------------------------------------------------------------------ подписи
        private static void SyncSignatures(PropertyWriter w, PropertyDictionary dict, Settings settings)
        {
            string designer = dict[Role.Designer];
            string tester = dict[Role.Tester];
            string firm = dict[Role.Firm];
            string active = w.ActiveConfigurationName();
            if (!string.IsNullOrEmpty(settings.Author))
            {
                if (settings.OverwriteSignatures) w.Set("", designer, settings.Author);
                else if (Empty(w.Raw("", designer)) && Empty(w.Raw(active, designer))) w.Set("", designer, settings.Author);
            }
            foreach (string cfg in w.ConfigurationNames())
            {
                if (!string.IsNullOrEmpty(settings.Checker))
                {
                    if (settings.OverwriteSignatures) w.Set(cfg, tester, settings.Checker);
                    else if (Empty(w.Raw(cfg, tester)) && Empty(w.Raw("", tester))) w.Set(cfg, tester, settings.Checker);
                }
                if (!string.IsNullOrEmpty(settings.Organization))
                {
                    if (settings.OverwriteSignatures) w.Set(cfg, firm, settings.Organization);
                    else if (Empty(w.Raw(cfg, firm)) && Empty(w.Raw("", firm))) w.Set(cfg, firm, settings.Organization);
                }
            }
            if (settings.LegacyAliases)
            {
                foreach (string alias in new[] { "Разраб.", "Разработал", "Автор", "п_Разраб", "DrawnBy" })
                    if (!string.IsNullOrEmpty(settings.Author)) w.SetIfEmpty("", alias, settings.Author);
                foreach (string alias in new[] { "Пров.", "п_Пров", "CheckedBy" })
                    if (!string.IsNullOrEmpty(settings.Checker)) w.SetIfEmpty("", alias, settings.Checker);
                foreach (string alias in new[] { "Организация", "Организация_ФБ", "Компания", "Firm", "Organization" })
                    if (!string.IsNullOrEmpty(settings.Organization)) w.SetIfEmpty("", alias, settings.Organization);
            }
        }

        private static bool Empty(string raw)
        {
            return raw == null || raw.Trim().Length == 0;
        }

        // ------------------------------------------------------------------ материал
        /// <summary>
        /// Для каждой конфигурации — материал SolidWorks этой конфигурации в трёх представлениях (Core.MaterialRecord):
        /// «Материал_ФБ» и «Материал_Таблица» — запись библиотеки ЕСКД заменяет прежнюю дробь MProp и текст (решение владельца
        /// 13.09.2026), материал вне библиотеки — только значения системы (Д-32); «Материал_Строка» — графа 3 одной строкой,
        /// для сводной ведомости. Конфигурация без материала ничего не получает (Д-33).
        /// </summary>
        public static void SyncMaterials(PropertyWriter w, ISldWorks app, PartDoc part, PropertyDictionary dict, SyncReport report)
        {
            string stampName = dict[Role.Material];
            string tableName = dict[Role.MaterialTable];
            List<string> databases = MaterialDatabases(app);
            Func<string, bool> catalog = value => MaterialCatalog.IsSystemRecord(databases, value);
            ModelDoc2 model = (ModelDoc2)part;
            foreach (string cfg in w.ConfigurationNames())
            {
                string db;
                string material = MaterialName(part, model, cfg, out db);
                MaterialRecord record = null;
                if (material != null)
                {
                    MaterialInfo info = MaterialCatalog.Find(databases, db, material);
                    record = info != null ? MaterialRecord.FromLibrary(info, dict.SmallFontMarkup) : MaterialRecord.FromName(material, dict.SmallFontMarkup);
                }
                Func<string, bool> isSystemRecord = MaterialRecord.SystemRecordOf(catalog, material, record);
                string stamp = w.Raw(cfg, stampName);
                if (MaterialRecord.ShouldReplace(stamp, record, isSystemRecord))
                {
                    if (record.IsLibrary && MaterialRecord.IsManualText(stamp, isSystemRecord) && MaterialRecord.OneLine(stamp) != record.Line)
                        Log.Info(string.Format("Конфигурация «{0}»: «{1}» = «{2}» заменено записью библиотеки «{3}» — материал SolidWorks из библиотеки ЕСКД",
                            cfg, stampName, MaterialRecord.OneLine(stamp), record.Line));
                    w.Set(cfg, stampName, record.Stamp);
                    if (MaterialRecord.ShouldReplace(w.Raw(cfg, tableName), record, isSystemRecord)) w.Set(cfg, tableName, record.Table);
                }
                else if (record != null && MaterialRecord.IsManualText(stamp, isSystemRecord) &&
                         MaterialRecord.OneLine(stamp) != MaterialRecord.OneLine(record.Stamp))
                {
                    report.Warnings.Add(string.Format("Конфигурация «{0}»: «{1}» = «{2}» введён вручную и не совпадает с материалом SolidWorks «{3}» — значение не изменено",
                        cfg, stampName, MaterialRecord.OneLine(stamp), material));
                }
                string line = LineFor(w, cfg, stampName, record, isSystemRecord);
                if (line.Length > 0) w.Set(cfg, MaterialRecord.LineProperty, line);
                else w.Delete(cfg, MaterialRecord.LineProperty);
            }
        }

        /// <summary>
        /// «Материал_Строка» — то, что стоит в графе 3, одной строкой: запись библиотеки ЕСКД; у материала вне библиотеки — ручная
        /// дробь или текст MProp, иначе материал конфигурации; «См. таблицу», «-» и выражение — материал конфигурации; нет
        /// материала — пусто.
        /// </summary>
        private static string LineFor(PropertyWriter w, string cfg, string stampName, MaterialRecord record, Func<string, bool> isSystemRecord)
        {
            if (record != null && record.IsLibrary) return record.Line;
            string stamp = w.Raw(cfg, stampName);
            if (MaterialRecord.IsManualText(stamp, isSystemRecord)) return MaterialRecord.OneLine(stamp);
            return record != null ? record.Line : "";
        }

        /// <summary>Базы материалов SolidWorks и после них корпоративная библиотека из поставки надстройки (Д-39).</summary>
        internal static List<string> MaterialDatabases(ISldWorks app)
        {
            string[] dbs = null;
            try
            {
                dbs = app.GetMaterialDatabases() as string[];
            }
            catch (Exception ex)
            {
                Log.Error("GetMaterialDatabases", ex);
            }
            string addinDir = Path.GetDirectoryName(typeof(SyncService).Assembly.Location) ?? "";
            return MaterialCatalog.WithCorporateLibrary(dbs, MaterialCatalog.LocateCorporateLibrary(addinDir));
        }

        /// <summary>Материал SolidWorks этой конфигурации (производная — родительской) или null; общий для сохранения и «Детали БЧ» (Д-41).</summary>
        internal static string MaterialName(PartDoc part, ModelDoc2 model, string cfg, out string db)
        {
            db = "";
            string name = part.GetMaterialPropertyName2(cfg, out db);
            try
            {
                // Производная конфигурация (развёртка листовой детали) берёт материал родительской. Материал активной
                // конфигурации не подставляется: у B-01 он попадал в 78 конфигураций без материала (Д-33).
                if (string.IsNullOrEmpty(name))
                {
                    Configuration c = model.GetConfigurationByName(cfg) as Configuration;
                    if (c != null && c.IsDerived())
                    {
                        Configuration parent = c.GetParent() as Configuration;
                        if (parent != null) name = part.GetMaterialPropertyName2(parent.Name, out db);
                    }
                }
                if (string.IsNullOrEmpty(name) && cfg.IndexOf('<') > 0)
                {
                    string baseName = cfg.Substring(0, cfg.IndexOf('<')).Trim();
                    if (baseName.Length > 0) name = part.GetMaterialPropertyName2(baseName, out db);
                }
            }
            catch (Exception ex)
            {
                Log.Error("GetMaterialPropertyName2 " + cfg, ex);
            }
            if (string.IsNullOrEmpty(name) || name.IndexOf("<не указан>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;
            return name;
        }

        // ------------------------------------------------------------------ масса
        /// <summary>
        /// Масса как у MProp (Правила записи свойств SWPlus, раздел 1; Р-1, Р-7): единицы массы документа по массе активной
        /// конфигурации (больше 0,1 кг — м/кг/м³ и 2 знака, иначе см/г/см³ и 1 знак; при «Единицы» = False/0 — единицы
        /// пользователя); у каждой конфигурации «Масса_Таблица» — "SW-Mass@@конф@файл.SLDPRT", «Масса_ФБ» — то же выражение в
        /// разметке графы 5 с « г» при граммах; «Примечание» БЧ — выражение с «кг»/«г». Ручные «-» и «См. таблицу» остаются,
        /// общая копия «Масса_ФБ» удаляется, как у MProp (FrmMProp:2834).
        /// </summary>
        public static void SyncMass(PropertyWriter w, ModelDoc2 doc, PropertyDictionary dict, SyncReport report)
        {
            string path = DocInfo.PathOf(doc);
            if (path.Length == 0) return; // выражение с именем файла пишется после первого сохранения
            string massName = dict[Role.Mass];
            string tableName = dict[Role.MassTable];
            string remark = dict[Role.Remark];
            string format = dict[Role.Format];
            string fileTitle = SwPlusFormat.FileTitle(path);
            bool isAssembly = doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
            string active = w.ActiveConfigurationName();
            bool grams = MassUnitsAsMProp(w, doc, active, report);
            string[] configs = w.ConfigurationNames();
            foreach (string cfg in configs)
            {
                if (IsSystemMassTable(w.Raw(cfg, tableName)))
                    w.Set(cfg, tableName, SwPlusFormat.MassExpression(cfg, fileTitle, isAssembly));
                if (IsSystemMass(w.Raw(cfg, massName)))
                    w.Set(cfg, massName, SwPlusFormat.MassStamp(cfg, fileTitle, isAssembly, grams, dict.SmallFontMarkup));
                if (IsBchMassNote(w, cfg, format, remark))
                    w.Set(cfg, remark, SwPlusFormat.BchRemark(cfg, fileTitle, isAssembly, grams));
            }
            string general = w.Raw("", massName);
            if (general != null && IsSystemMass(general)) w.Delete("", massName);
            if (configs.Length <= 1 && IsBchMassNote(w, "", format, remark))
                w.Set("", remark, SwPlusFormat.BchRemark(active, fileTitle, isAssembly, grams));
        }

        /// <summary>
        /// Единицы массы документа, как выставляет MProp (FrmMProp:2339, 2449–2468, 3353–3368); true — масса в граммах (суффикс
        /// « г» у выражения, FrmMProp:2848). При переключении единиц — предупреждение в строке состояния.
        /// </summary>
        private static bool MassUnitsAsMProp(PropertyWriter w, ModelDoc2 doc, string active, SyncReport report)
        {
            int[] prefs =
            {
                (int)swUserPreferenceIntegerValue_e.swUnitSystem, (int)swUserPreferenceIntegerValue_e.swUnitsMassPropLength,
                (int)swUserPreferenceIntegerValue_e.swUnitsMassPropMass, (int)swUserPreferenceIntegerValue_e.swUnitsMassPropVolume,
                (int)swUserPreferenceIntegerValue_e.swUnitsMassPropDecimalPlaces
            };
            int grams = (int)swUnitsMassPropMass_e.swUnitsMassPropMass_Grams;
            if (SwPlusFormat.UserUnits(w.Raw(active, "Единицы"))) return GetPreference(doc, prefs[2]) == grams;
            double mass = ActiveMass(doc);
            if (mass <= 0.0000001) return GetPreference(doc, prefs[2]) == grams;
            MassUnits units = SwPlusFormat.UnitsFor(mass);
            int[] wanted = { (int)swUnitSystem_e.swUnitSystem_Custom, units.Length, units.Mass, units.Volume, units.Decimals };
            bool changed = false;
            for (int i = 0; i < prefs.Length; i++)
            {
                if (GetPreference(doc, prefs[i]) == wanted[i]) continue;
                changed = true;
                if (w.DryRun) continue;
                try
                {
                    doc.Extension.SetUserPreferenceInteger(prefs[i], (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, wanted[i]);
                }
                catch (Exception ex)
                {
                    Log.Error("SetUserPreferenceInteger " + prefs[i], ex);
                }
            }
            if (changed)
            {
                System.Globalization.CultureInfo ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
                report.Warnings.Add(string.Format("Единицы массы документа переключены, как в MProp: {0} (масса {1})",
                    units.Grams ? "граммы — см, г, см³, 1 знак" : "килограммы — м, кг, м³, 2 знака",
                    units.Grams ? (mass * 1000).ToString("0.#", ru) + " г" : mass.ToString("0.##", ru) + " кг"));
            }
            return units.Grams;
        }

        /// <summary>«Масса_Таблица», которую пишет система: пусто, выражение шаблона или выражение «"SW-Mass@@…"» (конфигурация, имя файла и регистр расширения приводятся к MProp).</summary>
        private static bool IsSystemMassTable(string raw)
        {
            if (raw == null || raw.Trim().Length == 0) return true;
            string t = raw.Trim();
            if (t.IndexOf("SW-Mass@@", StringComparison.OrdinalIgnoreCase) >= 0)
                return System.Text.RegularExpressions.Regex.IsMatch(t, "^\"SW-Mass@@[^\"]*\"$");
            return t.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>«Масса_ФБ», которую пишет система: пусто, выражение шаблона, текст надстройки до v6.2 или выражение MProp; «-» и «См. таблицу» — нет.</summary>
        private static bool IsSystemMass(string raw)
        {
            if (raw == null || raw.Trim().Length == 0) return true;
            string plain = MaterialRecord.OneLine(raw);
            if (plain == "-" || plain.IndexOf("См.", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (SwPlusMarkup.IsGeneratedMass(raw)) return true;
            string t = MaterialRecord.Normalize(raw);
            string body = t.StartsWith(SwPlusFormat.MassPrefix, StringComparison.Ordinal) ? t.Substring(SwPlusFormat.MassPrefix.Length) : t.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(body, "^\"SW-Mass@@[^\"]*\"( г)?$")) return true;
            return raw.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0 && raw.IndexOf("SW-Mass", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool IsBchMassNote(PropertyWriter w, string level, string format, string remark)
        {
            return (w.Raw(level, format) ?? "").Trim() == BchRecord.FormatValue && BchRecord.IsMassNote(w.Raw(level, remark));
        }

        internal static double ActiveMass(ModelDoc2 doc)
        {
            try
            {
                MassProperty mp = doc.Extension.CreateMassProperty() as MassProperty;
                return mp != null ? mp.Mass : 0;
            }
            catch (Exception ex)
            {
                Log.Error("CreateMassProperty", ex);
                return 0;
            }
        }

        internal static int GetPreference(ModelDoc2 doc, int preference)
        {
            try
            {
                return doc.Extension.GetUserPreferenceInteger(preference, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
            }
            catch (Exception ex)
            {
                Log.Error("GetUserPreferenceInteger " + preference, ex);
                return -1;
            }
        }

        // ------------------------------------------------------------------ явная команда
        /// <summary>
        /// Кнопка «Синхронизировать»: деталь и сборка — полная синхронизация по текущему имени файла;
        /// чертёж — синхронизация модели первого вида (модель после этого нужно сохранить).
        /// </summary>
        public static SyncReport SyncExplicit(ISldWorks app, ModelDoc2 doc)
        {
            if (doc == null) return new SyncReport { Skipped = true, SkipReason = "нет активного документа" };
            if (doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
            {
                ModelDoc2 model = ReferencedModel((DrawingDoc)doc);
                if (model == null) return new SyncReport { Skipped = true, SkipReason = "в чертеже нет вида модели" };
                return SyncModel(app, model, new SyncRequest { Reason = "кнопка (модель чертежа)" });
            }
            return SyncModel(app, doc, new SyncRequest { Reason = "кнопка" });
        }

        public static ModelDoc2 ReferencedModel(DrawingDoc drw)
        {
            try
            {
                View sheetView = drw.GetFirstView() as View;
                View view = sheetView != null ? sheetView.GetNextView() as View : null;
                return view != null ? view.ReferencedDocument as ModelDoc2 : null;
            }
            catch (Exception ex)
            {
                Log.Error("ReferencedModel", ex);
                return null;
            }
        }
    }
}
