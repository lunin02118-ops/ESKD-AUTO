using System;
using System.Collections.Generic;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Кнопка «Деталь БЧ» по семантике MProp и SpecEditor (план, §3.5):
    /// «Формат» = «БЧ», масса в «Примечании», наименование — запись для спецификации.
    /// Исходные «Формат» и «Примечание» сохраняются на время режима и восстанавливаются при снятии.
    /// </summary>
    public static class BchService
    {
        public const int NotPart = 0;
        public const int Enabled = 1;
        public const int Disabled = 2;

        public static bool IsBch(PropertyWriter w, PropertyDictionary dict)
        {
            string format = dict[Role.Format];
            string active = w.ActiveConfigurationName();
            string value = w.Raw(active, format);
            if (value == null) value = w.Raw("", format);
            return (value ?? "").Trim() == BchRecord.FormatValue;
        }

        /// <summary>Текущий тип детали для окна переключения: БЧ или чертёжная и её «Формат» («А3», пусто — не задан).</summary>
        public static bool State(ModelDoc2 doc, out string format)
        {
            format = "";
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART) return false;
            PropertyDictionary dict = SyncService.Dictionary(Settings.Read());
            PropertyWriter w = new PropertyWriter(doc, true);
            string name = dict[Role.Format];
            format = (w.Raw(w.ActiveConfigurationName(), name) ?? w.Raw("", name) ?? "").Trim();
            return IsBch(w, dict);
        }

        public static int Toggle(ISldWorks app, ModelDoc2 doc)
        {
            return Set(app, doc, null);
        }

        /// <summary>
        /// Переключение чертёжная ⇄ безчертёжная. wanted = true — сделать БЧ, false — чертёжной, null — сменить на обратный.
        /// Деталь уже нужного типа не меняется: повторное нажатие не отменяет переключение.
        /// </summary>
        public static int Set(ISldWorks app, ModelDoc2 doc, bool? wanted)
        {
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART) return NotPart;
            Settings settings = Settings.Read();
            PropertyDictionary dict = SyncService.Dictionary(settings);
            PropertyWriter w = new PropertyWriter(doc, settings.DryRun);
            int result;
            try
            {
                bool now = IsBch(w, dict);
                bool target = wanted ?? !now;
                if (target == now) return now ? Enabled : Disabled;
                result = now ? Disable(w, doc, dict, settings) : Enable(w, app, doc, dict, settings);
            }
            catch (Exception ex)
            {
                Log.Error("Деталь БЧ", ex);
                return NotPart;
            }
            Log.Info(string.Format("Деталь БЧ {0}: {1}; операций {2}{3}", result == Enabled ? "установлена" : "снята",
                SafeTitle(doc), w.Operations.Count, w.Operations.Count > 0 ? "\r\n    " + string.Join("\r\n    ", w.Operations.ToArray()) : ""));
            return result;
        }

        /// <summary>Уровни записи «Формат» и «Примечание», как у MProp: конфигурация, общие — при одной конфигурации.</summary>
        private static List<string> Levels(PropertyWriter w)
        {
            List<string> levels = new List<string>();
            string active = w.ActiveConfigurationName();
            levels.Add(active);
            if (w.ConfigurationNames().Length <= 1) levels.Add("");
            return levels;
        }

        private static int Enable(PropertyWriter w, ISldWorks app, ModelDoc2 doc, PropertyDictionary dict, Settings settings)
        {
            string format = dict[Role.Format];
            string remark = dict[Role.Remark];
            string title = dict[Role.Description];
            string titleFb = dict[Role.DescriptionMulti];

            double mass = 0;
            try
            {
                MassProperty mp = doc.Extension.CreateMassProperty() as MassProperty;
                if (mp != null) mass = mp.Mass;
            }
            catch (Exception ex)
            {
                Log.Error("БЧ: масса", ex);
            }

            // «Примечание» — выражение массы MProp с единицей документа (FrmMProp:3033); единицы — по правилу MProp
            string path = DocInfo.PathOf(doc);
            string fileTitle = SwPlusFormat.FileTitle(path.Length > 0 ? path : DocInfo.TitleOf(doc));
            string active = w.ActiveConfigurationName();
            bool grams = SwPlusFormat.UserUnits(w.Raw(active, "Единицы"))
                ? SyncService.GetPreference(doc, (int)swUserPreferenceIntegerValue_e.swUnitsMassPropMass) == (int)swUnitsMassPropMass_e.swUnitsMassPropMass_Grams
                : SwPlusFormat.UnitsFor(mass).Grams;
            foreach (string level in Levels(w))
            {
                string oldFormat = w.Raw(level, format);
                if (oldFormat != null && oldFormat.Trim() != BchRecord.FormatValue && oldFormat.Trim().Length > 0)
                    w.Set(level, BchRecord.SavedFormatProperty, oldFormat);
                w.Set(level, format, BchRecord.FormatValue);

                string oldRemark = w.Raw(level, remark);
                if (oldRemark != null && oldRemark.Trim().Length > 0 && !BchRecord.IsMassNote(oldRemark))
                    w.Set(level, BchRecord.SavedRemarkProperty, oldRemark);
                if (mass > 0.00001 && fileTitle.Length > 0)
                    w.Set(level, remark, SwPlusFormat.BchRemark(level.Length == 0 ? active : level, fileTitle, false, grams));
            }

            // Запись для спецификации: «Наименование» — одно название, остальные строки — «Запись_БЧ» (З-9)
            string baseTitle = CurrentTitle(w, doc, dict);
            string titleLevel = RecordLevel(w);
            string record = BuildRecord(app, doc, w.ActiveConfigurationName(), baseTitle);
            if (BchRecord.IsRecord(w.Raw(titleLevel, title))) w.Delete(titleLevel, title);
            w.Set("", title, BchRecord.ShortTitle(record));
            w.Set(titleLevel, BchRecord.LinesProperty, BchRecord.Lines(record));
            string fb = w.Raw("", titleFb);
            if (PropertyWriter.IsEmptyOrTemplate(fb))
                w.Set("", titleFb, SwPlusFormat.TitleStamp(SwPlusFormat.WrapTitle(BchRecord.ShortTitle(baseTitle)), dict.SmallFontMarkup));

            RemoveLegacyFlag(w);
            return Enabled;
        }

        /// <summary>Уровень записи БЧ («Наименование», «Запись_БЧ»): общие при одной конфигурации, иначе активная конфигурация.</summary>
        internal static string RecordLevel(PropertyWriter w)
        {
            return w.ConfigurationNames().Length <= 1 ? "" : w.ActiveConfigurationName();
        }

        /// <summary>Запись БЧ вида V1 (решение владельца, S-2b): наименование ⏎ дробь «сортамент / марка» ⏎ размер заготовки.</summary>
        internal static string BuildRecord(ISldWorks app, ModelDoc2 doc, string cfg, string baseTitle)
        {
            string sortament = "", grade = "", materialText = "";
            MaterialInfo info = Material(app, (PartDoc)doc, cfg, out materialText);
            if (info != null && (info.GostDesignation ?? "").IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) < 0 &&
                !string.IsNullOrWhiteSpace(info.GostDesignation))
            {
                // Материал одной строкой (ТУ, кромка, плиты — таблица Д-2): запись БЧ повторяет графу 3 без дроби
                sortament = info.GostDesignation.Trim();
                materialText = info.Name;
            }
            else if (info != null)
            {
                sortament = info.Sortament;
                grade = (info.Grade + " " + info.GostMaterial).Trim();
                // Знаменатель — как в графе 3 («Обозначение_ГОСТ»): у записей «как есть» (МДФ, фанера, ДВП, HPL — класс Б
                // таблицы Д-2) он не совпадает с «Марка_Материала ГОСТ_Материал»
                string designation = info.GostDesignation ?? "";
                int over = designation.IndexOf("<OVER>", StringComparison.OrdinalIgnoreCase);
                int end = designation.IndexOf("</STACK>", StringComparison.OrdinalIgnoreCase);
                if (over >= 0 && end > over) grade = designation.Substring(over + 6, end - over - 6).Trim();
                materialText = info.Name;
            }
            else if (!string.IsNullOrEmpty(materialText) && materialText.Contains("/"))
            {
                int slash = materialText.IndexOf('/');
                sortament = materialText.Substring(0, slash).Trim();
                grade = materialText.Substring(slash + 1).Trim();
            }
            double[] dims = BodyDimensions(doc);
            double lengthFromModel = DimensionMm(doc, "RD1@Примечания");
            string size = BchRecord.SizeText(materialText + " " + sortament, dims, lengthFromModel);
            return BchRecord.SpecTitle(baseTitle, sortament, grade, size);
        }

        /// <summary>
        /// При сохранении детали БЧ строки своей записи (BchRecord.IsOwnRecord) пересобираются по материалу и габаритам модели:
        /// после смены материала или размеров спецификация не отстаёт (WP-2.7). Запись, изменённая вручную, остаётся.
        /// «Наименование» не трогается, если в нём название: его пишет конструктор или синхронизация по имени файла.
        /// </summary>
        internal static void UpdateOnSave(PropertyWriter w, ISldWorks app, ModelDoc2 doc, PropertyDictionary dict)
        {
            if (!IsBch(w, dict)) return;
            string title = dict[Role.Description];
            string level = RecordLevel(w);
            string lines = w.Raw(level, BchRecord.LinesProperty);
            // Запись до З-9 — вся в «Наименовании» уровня записи: название уходит в общие, строки — в «Запись_БЧ».
            string old = w.Raw(level, title);
            if (BchRecord.IsRecord(old))
            {
                if (string.IsNullOrEmpty(lines)) lines = BchRecord.Lines(old);
                if (level.Length > 0) w.Delete(level, title);
                w.Set("", title, BchRecord.ShortTitle(old));
                w.Set(level, BchRecord.LinesProperty, lines);
            }
            // Название пропало (пусто, шаблонное «Деталь») — по имени файла.
            string current = w.Raw(level, title) ?? w.Raw("", title);
            string fileTitle = DesignationParser.Parse(SafePath(doc), dict.NameSeparator).Title ?? "";
            string plain = (current ?? "").Trim();
            if (PropertyWriter.IsEmptyOrTemplate(current) || DesignationParser.IsTemplateName(plain))
            {
                plain = BchRecord.ShortTitle(fileTitle.Length > 0 ? fileTitle : current);
                w.Set("", title, plain);
            }
            if (!string.IsNullOrEmpty(lines) && !BchRecord.IsOwnRecord(BchRecord.Join(plain, lines))) return;
            string wanted = BchRecord.Lines(BuildRecord(app, doc, w.ActiveConfigurationName(), plain));
            if (MaterialRecord.Normalize(lines ?? "") != wanted && (wanted.Length > 0 || lines != null))
                w.Set(level, BchRecord.LinesProperty, wanted);
        }

        private static int Disable(PropertyWriter w, ModelDoc2 doc, PropertyDictionary dict, Settings settings)
        {
            string format = dict[Role.Format];
            string remark = dict[Role.Remark];
            string title = dict[Role.Description];
            foreach (string level in Levels(w))
            {
                string savedFormat = w.Raw(level, BchRecord.SavedFormatProperty);
                if (!string.IsNullOrEmpty(savedFormat)) w.Set(level, format, savedFormat);
                else if ((w.Raw(level, format) ?? "").Trim() == BchRecord.FormatValue) w.Delete(level, format);
                w.Delete(level, BchRecord.SavedFormatProperty);

                string savedRemark = w.Raw(level, BchRecord.SavedRemarkProperty);
                if (!string.IsNullOrEmpty(savedRemark)) w.Set(level, remark, savedRemark);
                else if (BchRecord.IsMassNote(w.Raw(level, remark))) w.Delete(level, remark);
                w.Delete(level, BchRecord.SavedRemarkProperty);
            }

            // Наименование: запись БЧ заменяется наименованием из имени файла (или первой строкой записи).
            foreach (string level in new[] { "", w.ActiveConfigurationName() })
            {
                w.Delete(level, BchRecord.LinesProperty);
                string current = w.Raw(level, title);
                if (current == null || !SwPlusMarkup.HasMarkup(current)) continue;
                ParsedName parsed = DesignationParser.Parse(SafePath(doc), dict.NameSeparator);
                string restored = !string.IsNullOrEmpty(parsed.Title) ? parsed.Title : BchRecord.ShortTitle(current);
                if (level.Length == 0) w.Set(level, title, restored);
                else w.Delete(level, title);
            }
            RemoveLegacyFlag(w);
            return Disabled;
        }

        private static void RemoveLegacyFlag(PropertyWriter w)
        {
            w.Delete("", "БЧ");
            foreach (string cfg in w.ConfigurationNames()) w.Delete(cfg, "БЧ");
        }

        private static string CurrentTitle(PropertyWriter w, ModelDoc2 doc, PropertyDictionary dict)
        {
            string raw = w.Raw("", dict[Role.Description]);
            if (!PropertyWriter.IsEmptyOrTemplate(raw)) return BchRecord.ShortTitle(raw);
            ParsedName parsed = DesignationParser.Parse(SafePath(doc), dict.NameSeparator);
            return !string.IsNullOrEmpty(parsed.Title) ? parsed.Title : "Деталь";
        }

        /// <summary>Материал этой конфигурации по тем же правилам, что при сохранении (Д-41): без материала — ничего.</summary>
        private static MaterialInfo Material(ISldWorks app, PartDoc part, string cfg, out string materialName)
        {
            materialName = "";
            try
            {
                string db;
                // Свой материал тела перекрывает материал детали (сверка SW API 23.09.2026, №33).
                materialName = SyncService.ActualMaterial(part, (ModelDoc2)part, cfg, out db, null) ?? "";
                if (materialName.Length == 0) return null;
                List<string> databases = SyncService.MaterialDatabases(app);
                if (MaterialCatalog.IsCorporateLibraryMissing(databases, db))
                {
                    Log.Warn(SyncService.MissingLibraryWarning(cfg, db));
                    return null;
                }
                return MaterialCatalog.Find(databases, db, materialName);
            }
            catch (Exception ex)
            {
                Log.Error("БЧ: материал", ex);
                return null;
            }
        }

        /// <summary>Габариты первого твёрдого тела, мм, по возрастанию.</summary>
        public static double[] BodyDimensions(ModelDoc2 doc)
        {
            try
            {
                PartDoc part = doc as PartDoc;
                object[] bodies = part != null ? part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] : null;
                if (bodies == null || bodies.Length == 0) return null;
                double[] box = ((Body2)bodies[0]).GetBodyBox() as double[];
                if (box == null || box.Length != 6) return null;
                double[] dims = new double[]
                {
                    Math.Round(Math.Abs(box[3] - box[0]) * 1000.0, 1),
                    Math.Round(Math.Abs(box[4] - box[1]) * 1000.0, 1),
                    Math.Round(Math.Abs(box[5] - box[2]) * 1000.0, 1)
                };
                Array.Sort(dims);
                return dims;
            }
            catch (Exception ex)
            {
                Log.Error("БЧ: габариты", ex);
                return null;
            }
        }

        private static double DimensionMm(ModelDoc2 doc, string fullName)
        {
            try
            {
                Dimension dim = doc.Parameter(fullName) as Dimension;
                return dim != null ? dim.SystemValue * 1000.0 : 0;
            }
            catch (Exception ex)
            {
                Log.Error("БЧ: размер " + fullName, ex);
                return 0;
            }
        }

        private static string SafePath(ModelDoc2 doc)
        {
            return DocInfo.PathOf(doc);
        }

        private static string SafeTitle(ModelDoc2 doc)
        {
            return DocInfo.TitleOf(doc);
        }
    }
}
