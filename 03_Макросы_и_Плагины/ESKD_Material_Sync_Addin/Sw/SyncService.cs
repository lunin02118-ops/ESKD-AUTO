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

        /// <summary>Сверять материал с геометрией детали — типоразмером профиля и толщиной листа (Р-8).</summary>
        public bool Stock = true;

        public bool DryRun;

        /// <summary>
        /// Обозначение — по имени файла, даже если в свойстве другое значение: конструктор сам выбрал это в окне
        /// «Проверить изделие» (замечание владельца 22.09.2026: «Тело5» у тел, выделенных из многотельной детали).
        /// </summary>
        public bool DesignationFromFile;
    }

    /// <summary>Обозначение в свойствах расходится с именем файла — вопрос конструктору в окне «Проверить изделие».</summary>
    public sealed class DesignationMismatch
    {
        public string Current = "";
        public string Expected = "";
        /// <summary>Текст замечания в отчёте синхронизации — окно выбора показывает его вместо общего списка.</summary>
        public string Warning = "";
    }

    public sealed class SyncReport
    {
        public int Changes;
        public int Failures;
        public bool Skipped;
        public string SkipReason = "";
        public readonly List<string> Operations = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        /// <summary>Что ещё ответить конструктору: тот же вопрос о материале в других исполнениях детали.</summary>
        public readonly List<string> Hints = new List<string>();

        /// <summary>Обозначение введено не по имени файла — null, если совпадает или имя файла без обозначения.</summary>
        public DesignationMismatch Designation;

        /// <summary>
        /// Что деталь сказала про свой прокат (Р-8). Назначение материала здесь не делается: обработчик сохранения
        /// не место для правки документа — этим занимается очередь простоя, читая этот список.
        /// </summary>
        public readonly List<StockFinding> Stock = new List<StockFinding>();

        /// <summary>Есть ли что назначать или о чём спрашивать конструктора.</summary>
        public bool StockNeedsWork
        {
            get
            {
                foreach (StockFinding f in Stock)
                    if ((f.NeedsAssign || f.NeedsChoice) && !f.Kept) return true;
                return false;
            }
        }

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
        // Словарь SWPlus нужен на каждом сохранении и при каждом опросе кнопок, а лежит рядом с надстройкой — у
        // установки с NAS это сетевые чтения. Держим прочитанный словарь и перечитываем, когда меняется файл
        // (проверка времени изменения — не чаще раза в DictionaryRecheck) или путь из настроек.
        private static readonly TimeSpan DictionaryRecheck = TimeSpan.FromSeconds(10);
        private static readonly object DictionaryLock = new object();
        private static PropertyDictionary _dictionary;
        private static string _dictionaryOverride, _dictionaryPath;
        private static DateTime _dictionaryStamp, _dictionaryChecked;

        public static PropertyDictionary Dictionary(Settings settings)
        {
            string overridePath = settings.DictionaryPath ?? "";
            lock (DictionaryLock)
            {
                DateTime now = DateTime.UtcNow;
                if (_dictionary != null && _dictionaryOverride == overridePath && now - _dictionaryChecked < DictionaryRecheck)
                    return _dictionary;
                string addinDir = Path.GetDirectoryName(typeof(SyncService).Assembly.Location) ?? "";
                string path = PropertyDictionary.Locate(addinDir, overridePath) ?? "";
                DateTime stamp = path.Length > 0 && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (_dictionary == null || _dictionaryOverride != overridePath || _dictionaryPath != path || _dictionaryStamp != stamp)
                {
                    _dictionary = PropertyDictionary.Load(path.Length > 0 ? path : null);
                    _dictionaryOverride = overridePath;
                    _dictionaryPath = path;
                    _dictionaryStamp = stamp;
                }
                _dictionaryChecked = now;
                return _dictionary;
            }
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
                if (req.Stock && !isAssembly && settings.AutoStockMaterial && !req.DryRun && !settings.DryRun)
                    InspectStock(app, doc, report);
                if (!isAssembly)
                    BchService.UpdateOnSave(w, app, doc, dict);
                if (req.Mass && settings.AutoMass)
                    SyncMass(w, doc, dict, report);
                SyncLevels(w, dict, isAssembly);
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
            // Код документа из имени файла отбрасывается и никуда не пишется (Р-9, Н-11): его ставят шаблон и MProp.
            string expected = DesignationParser.Build(now.Root, now.Execution, "");
            string expectedBefore = before != null && before.HasDesignation
                ? DesignationParser.Build(before.Root, before.Execution, "") : null;
            bool manual = (w.Raw("", "RenameSWP") ?? "").Trim() == "1";
            string current = w.Raw("", number);
            // Миграция v5 и v6.1: в обозначении мог остаться код « СБ» — это производное значение.
            if (current != null && !string.IsNullOrEmpty(code) &&
                string.Equals(current.Trim(), DesignationParser.Build(now.Root, now.Execution, code), StringComparison.Ordinal))
            {
                current = "";
            }
            Provenance designation = now.HasDesignation
                ? ProvenanceRule.Classify(current, expected, expectedBefore, manual) : Provenance.Manual;
            if (req.DesignationFromFile && now.HasDesignation && designation == Provenance.Manual)
            {
                // Конструктор выбрал имя файла: пишем, как у новой детали, и снимаем отметку ручного ввода.
                designation = Provenance.Template;
                if (manual) { w.Set("", "RenameSWP", "0"); manual = false; }
            }
            bool designationDerived = designation != Provenance.Manual;
            if (ProvenanceRule.ShouldWrite(designation) && expected.Length > 0)
            {
                w.Set("", number, expected);
            }
            else if (designation == Provenance.Manual && now.HasDesignation && !manual &&
                     !string.Equals((current ?? "").Trim(), expected, StringComparison.Ordinal) &&
                     // «Оставить как есть» в окне «Проверить изделие» — ответ помнит сама модель; сменится имя файла — спросим снова.
                     !ReviewAccepted.Contains(w.Raw("", ReviewAccepted.PropertyName), ReviewAccepted.Designation((current ?? "").Trim(), expected)))
            {
                string warning = string.Format("Обозначение «{0}» не совпадает с именем файла «{1}» и оставлено без изменений",
                    current, now.BaseName);
                report.Warnings.Add(warning);
                report.Designation = new DesignationMismatch { Current = (current ?? "").Trim(), Expected = expected, Warning = warning };
            }

            // Имя без обозначения («Кронштейн сварной»): обозначение пустое, как у MProp после правки WP-3.6 — выражение шаблона
            // «SW-File Name» вывело бы в графу 2 всё имя файла (Р-11, Н-30); введённое вручную обозначение остаётся.
            if (!now.HasDesignation && !manual && PropertyWriter.IsEmptyOrTemplate(current))
            {
                w.Set("", number, "");
                foreach (string cfg in w.ConfigurationNames())
                {
                    if (PropertyWriter.IsEmptyOrTemplate(w.Raw(cfg, number))) w.Set(cfg, number, "");
                    if (string.IsNullOrWhiteSpace(w.Raw(cfg, "Исполнение"))) w.Set(cfg, "Исполнение", "0");
                }
            }

            // Обозначения конфигураций (исполнения по ГОСТ 2.113)
            if (designationDerived && now.HasDesignation)
            {
                bool bchPart = !isAssembly && BchService.IsBch(w, dict);
                foreach (string cfg in w.ConfigurationNames())
                {
                    string execution;
                    bool isBase;
                    // Исполнение — по имени самой конфигурации: производная «01» от «00» — это исполнение -01, а не базовое
                    // (замечание владельца 18.09.2026, «Укосина»). Корневая — только если своё имя не читается как исполнение
                    // («00SM-FLAT-PATTERN» разбирается сам, «Покраска» под «01» берёт номер у «01»).
                    bool recognized = DesignationParser.ExtractExecutionFromConfigName(cfg, out execution, out isBase) ||
                        DesignationParser.ExtractExecutionFromConfigName(RootConfigurationName(doc, cfg), out execution, out isBase);
                    string cfgCurrent = w.Raw(cfg, number);
                    string cfgExpected;
                    if (recognized)
                    {
                        cfgExpected = DesignationParser.Build(now.Root, isBase ? now.Execution : execution, "");
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
                    // «Исполнение» — как галочки MProp: «1» («Исполнение» + «Из») — номер из имени конфигурации, «2» — номер
                    // вписан, «0» — без исполнения; номер из имени файла уже в обозначении документа — «0», иначе MProp удвоит
                    // суффикс (FrmMProp:2677–2690, прогон К-1 A-20). Ставится при каждой синхронизации, чтобы конструктору не
                    // отмечать галочки в MProp вручную (замечание владельца 22.09.2026); у детали БЧ — только в пустое.
                    bool fromConfiguration = recognized && !isBase && !string.IsNullOrEmpty(execution);
                    string executionFlag = (w.Raw(cfg, "Исполнение") ?? "").Trim();
                    string wantedFlag = DesignationParser.ExecutionFlag(cfg, execution, fromConfiguration);
                    if (executionFlag.Length == 0 || (!bchPart && executionFlag != wantedFlag)) w.Set(cfg, "Исполнение", wantedFlag);
                }
            }

            // Сборки: «Сборка1_ФБ» ставят шаблон и MProp — надстройка его не пишет и не удаляет, только « СБ» → «СБ» (Р-3);
            // «Сборка2_ФБ» — в формате MProp, только если свойства нет; общие копии MProp удаляет (FrmMProp:3055–3069).
            if (isAssembly)
            {
                string codeProp = dict[Role.DocCode];
                string codeText = dict[Role.DocDescription];
                foreach (string cfg in w.ConfigurationNames())
                {
                    string codeValue = w.Raw(cfg, codeProp);
                    if (codeValue != null && codeValue != codeValue.Trim() && IsDerivedCode(codeValue))
                        w.Set(cfg, codeProp, codeValue.Trim());
                    string second = w.Raw(cfg, codeText);
                    bool isAssemblyDrawing = (w.Raw(cfg, codeProp) ?? "").Trim() == SwPlusFormat.AssemblyCode;
                    if ((second == null && isAssemblyDrawing) || IsLegacyDocDescription(second))
                        w.Set(cfg, codeText, SwPlusFormat.DocDescription(SwPlusFormat.AssemblyDrawingText, dict.SmallFontMarkup));
                }
                string generalCode = w.Raw("", codeProp);
                if (generalCode != null && IsDerivedCode(generalCode)) w.Delete("", codeProp);
                string generalText = w.Raw("", codeText);
                if (generalText != null && (IsLegacyDocDescription(generalText) || IsMPropDocDescription(generalText))) w.Delete("", codeText);
            }

            // Наименование и «Наименование_ФБ» — общие свойства
            if (string.IsNullOrEmpty(now.Title)) return;
            string currentTitle = w.Raw("", title);
            Provenance titleState = ProvenanceRule.Classify(currentTitle, now.Title, before != null ? before.Title : null, false);
            // У детали БЧ «Наименование» — такое же название, как у любой детали; строки записи — в «Запись_БЧ» (З-9)
            bool bch = !isAssembly && BchService.IsBch(w, dict);
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
            // Графа 1 — разметка MProp по числу строк (FrmMProp:2636–2657), перенос по 22 знака (Р-2); у БЧ — первая строка записи.
            string stampTitle = BchRecord.IsRecord(effective) || bch ? BchRecord.ShortTitle(effective) : effective;
            string wantedFb = SwPlusFormat.TitleStamp(SwPlusFormat.WrapTitle(stampTitle), dict.SmallFontMarkup);
            string currentFb = w.Raw("", titleFb);
            if (IsDerivedTitleStamp(currentFb, stampTitle, before)) w.Set("", titleFb, wantedFb);

            // Копии наименования в конфигурациях MProp удаляет (FrmMProp:2671–2673, 2701–2703): производные копии удаляются,
            // иначе устаревшая копия затеняет общее свойство в штампе и спецификации.
            foreach (string cfg in w.ConfigurationNames())
            {
                string cfgTitle = w.Raw(cfg, title);
                if (cfgTitle != null && (PropertyWriter.IsEmptyOrTemplate(cfgTitle) ||
                    (before != null && cfgTitle == before.Title) || cfgTitle == currentTitle || cfgTitle == effective))
                {
                    w.Delete(cfg, title);
                }
                string cfgFb = w.Raw(cfg, titleFb);
                if (cfgFb != null && (IsDerivedTitleStamp(cfgFb, stampTitle, before) || MaterialRecord.Normalize(cfgFb) == MaterialRecord.Normalize(currentFb)))
                    w.Delete(cfg, titleFb);
            }
        }

        /// <summary>«Наименование_ФБ», которое пишет система: пусто, выражение шаблона или текст наименования (нынешнего или прежнего имени файла) в любой форме — без разметки, с переносом, в разметке MProp.</summary>
        private static bool IsDerivedTitleStamp(string raw, string title, ParsedName before)
        {
            if (PropertyWriter.IsEmptyOrTemplate(raw)) return true;
            if (raw.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            string plain = SwPlusFormat.TitlePlain(raw);
            if (plain == SwPlusFormat.TitlePlain(title)) return true;
            return before != null && !string.IsNullOrEmpty(before.Title) && plain == SwPlusFormat.TitlePlain(before.Title);
        }

        private static bool DerivedFromBefore(string value, ParsedName before, bool isAssembly)
        {
            if (before == null || !before.HasDesignation || string.IsNullOrEmpty(value)) return false;
            string root = DesignationParser.Build(before.Root, null, "");
            return value.StartsWith(root, StringComparison.Ordinal);
        }

        private static bool IsLegacyDocDescription(string value)
        {
            return SwPlusMarkup.IsLegacyDocDescription(value);
        }

        private static bool IsMPropDocDescription(string value)
        {
            string t = MaterialRecord.Normalize(value ?? "");
            return t.StartsWith(SwPlusFormat.DocDescriptionPrefix, StringComparison.Ordinal);
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
            // «Конструктор» и «Сводка → Автор» — одно значение: MProp заполняет форму из «Автора» (FrmMProp:1906) и пишет его в
            // оба места (2717–2724), поэтому пустой «Автор» стирал «Конструктора» (Н-04, WP-2.5).
            string current = w.Raw("", designer);
            string author = w.Author();
            string wanted = settings.OverwriteSignatures && !string.IsNullOrEmpty(settings.Author) ? settings.Author
                : !Empty(current) ? current
                : !Empty(author) ? author
                : Empty(w.Raw(active, designer)) ? settings.Author : null;
            if (!string.IsNullOrEmpty(wanted))
            {
                if (!Empty(current) || Empty(w.Raw(active, designer)) || settings.OverwriteSignatures) w.Set("", designer, wanted.Trim());
                w.SetAuthor(wanted.Trim());
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
        /// «Материал_ФБ» и «Материал_Таблица» — запись библиотеки ЕСКД заменяет прежнюю дробь MProp, текст и выражение SW-Material
        /// (Р-5, Р-6), замена набранного текста — предупреждение отчёта в строке состояния (Н-23); материал вне библиотеки —
        /// только значения системы (Д-32); «Материал_Строка» — графа 3 одной строкой, для сводной ведомости. Конфигурация без
        /// материала ничего не получает (Д-33).
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
                    if (MaterialCatalog.IsCorporateLibraryMissing(databases, db))
                    {
                        report.Warnings.Add(MissingLibraryWarning(cfg, db));
                        continue;
                    }
                    MaterialInfo info = MaterialCatalog.Find(databases, db, material);
                    record = info != null ? MaterialRecord.FromLibrary(info, dict.SmallFontMarkup) : MaterialRecord.FromName(material, dict.SmallFontMarkup);
                }
                Func<string, bool> isSystemRecord = MaterialRecord.SystemRecordOf(catalog, material, record);
                string stamp = w.Raw(cfg, stampName);
                if (MaterialRecord.ShouldReplace(stamp, record, isSystemRecord))
                {
                    if (record.IsLibrary && MaterialRecord.IsManualText(stamp, isSystemRecord) && MaterialRecord.OneLine(stamp) != record.Line)
                        report.Warnings.Add(string.Format("Конфигурация «{0}»: «{1}» = «{2}» заменено записью библиотеки «{3}» — материал SolidWorks из библиотеки ЕСКД",
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

        /// <summary>Базы материалов, подключённые в SolidWorks. Запасной копии библиотеки нет (решение владельца 14.09.2026).</summary>
        internal static List<string> MaterialDatabases(ISldWorks app)
        {
            List<string> result = new List<string>();
            try
            {
                string[] dbs = app.GetMaterialDatabases() as string[];
                if (dbs != null) result.AddRange(dbs);
            }
            catch (Exception ex)
            {
                Log.Error("GetMaterialDatabases", ex);
            }
            return result;
        }

        internal static string MissingLibraryWarning(string cfg, string database)
        {
            return string.Format("Конфигурация «{0}»: материал из библиотеки «{1}», но она не подключена в SolidWorks " +
                "(Параметры → Месторасположение файлов → Базы данных материалов) — дробь материала не записана. " +
                "Запустите настройку рабочего места.", cfg, database);
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

        // ------------------------------------------------------------------ материал по геометрии
        /// <summary>
        /// Сверка материала с тем, из чего деталь сделана (Р-8, решение владельца 20.09.2026). Здесь только чтение
        /// и замечания: правка документа в обработчике сохранения запрещена, назначением занимается очередь простоя,
        /// читая отсюда SyncReport.Stock.
        /// </summary>
        private static void InspectStock(ISldWorks app, ModelDoc2 doc, SyncReport report)
        {
            try
            {
                List<StockFinding> findings = StockService.Inspect(app, doc);
                // Ответ «Оставить как есть» (в этом сеансе или в свойстве модели) снимает вопрос: без этого конструктору
                // при каждом сохранении предлагали бы выбрать материал, который он оставил (ревью 23.09.2026).
                StockService.PendingDecisions(DocInfo.PathOf(doc), findings, StockService.Accepted(doc));
                report.Stock.AddRange(findings);
                foreach (StockFinding finding in findings)
                {
                    if (finding.Kept) continue;
                    string message = StockService.Message(finding);
                    if (message.Length > 0) report.Warnings.Add(message);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Материал по геометрии", ex);
            }
        }

        // ------------------------------------------------------------------ уровни, как у MProp
        /// <summary>
        /// Уровни хранения, как их оставляет MProp (Правила записи свойств SWPlus, раздел 8): общую копию «Материал_ФБ» со
        /// значением шаблона или записью системы MProp удаляет (FrmMProp:2904); «Формат», «Примечание», «Раздел» у документа с
        /// несколькими конфигурациями живут в конфигурациях, общую копию MProp удаляет (FrmMProp:3021–3055) — надстройка
        /// переносит значение в конфигурации, где его нет, и удаляет общую копию (Н-22).
        /// </summary>
        private static void SyncLevels(PropertyWriter w, PropertyDictionary dict, bool isAssembly)
        {
            string material = dict[Role.Material];
            string generalMaterial = w.Raw("", material);
            if (generalMaterial != null && (PropertyWriter.IsEmptyOrTemplate(generalMaterial) ||
                generalMaterial.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0 && !isAssembly && AllConfigurationsHave(w, material)))
                w.Delete("", material);

            string[] configs = w.ConfigurationNames();
            if (configs.Length <= 1) return;
            foreach (string name in new[] { dict[Role.Format], dict[Role.Remark], dict[Role.Section] })
            {
                string general = w.Raw("", name);
                if (general == null) continue;
                foreach (string cfg in configs)
                {
                    if (w.Raw(cfg, name) == null) w.Set(cfg, name, general);
                }
                w.Delete("", name);
            }
        }

        private static bool AllConfigurationsHave(PropertyWriter w, string name)
        {
            foreach (string cfg in w.ConfigurationNames())
            {
                if (string.IsNullOrWhiteSpace(w.Raw(cfg, name))) return false;
            }
            return true;
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
        /// <summary>Начало предупреждения о единицах массы: их выставляет сама синхронизация, конструктору решать нечего.</summary>
        internal const string MassUnitsWarning = "Единицы массы документа переключены, как в MProp";

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
            if (changed && !w.DryRun) w.Forget();
            if (changed)
            {
                System.Globalization.CultureInfo ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
                report.Warnings.Add(string.Format(MassUnitsWarning + ": {0} (масса {1})",
                    units.Grams ? "граммы — см, г, см³, 1 знак" : "килограммы — м, кг, м³, 2 знака",
                    units.Grams ? (mass * 1000).ToString("0.#", ru) + " г" : mass.ToString("0.##", ru) + " кг"));
            }
            return units.Grams;
        }

        private static bool IsSystemMassTable(string raw)
        {
            return SwPlusMarkup.IsSystemMassTable(raw);
        }

        private static bool IsSystemMass(string raw)
        {
            return SwPlusMarkup.IsSystemMass(raw);
        }

        /// <summary>
        /// Масса документа в граммах по правилу MProp, без переключения единиц: при «Единицы» = False/0 или нулевой массе —
        /// единица документа, иначе — порог 0,1 кг по массе активной конфигурации (для «Очистки v5», WP-2.10).
        /// </summary>
        internal static bool MassInGrams(PropertyWriter w, ModelDoc2 doc)
        {
            int grams = (int)swUnitsMassPropMass_e.swUnitsMassPropMass_Grams;
            int current = GetPreference(doc, (int)swUserPreferenceIntegerValue_e.swUnitsMassPropMass);
            if (SwPlusFormat.UserUnits(w.Raw(w.ActiveConfigurationName(), "Единицы"))) return current == grams;
            double mass = ActiveMass(doc);
            return mass <= 0.0000001 ? current == grams : SwPlusFormat.UnitsFor(mass).Grams;
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
