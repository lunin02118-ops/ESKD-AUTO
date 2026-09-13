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

        public static int Toggle(ISldWorks app, ModelDoc2 doc)
        {
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART) return NotPart;
            Settings settings = Settings.Read();
            PropertyDictionary dict = SyncService.Dictionary(settings);
            PropertyWriter w = new PropertyWriter(doc, settings.DryRun);
            int result;
            try
            {
                result = IsBch(w, dict) ? Disable(w, doc, dict, settings) : Enable(w, app, doc, dict, settings);
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

            foreach (string level in Levels(w))
            {
                string oldFormat = w.Raw(level, format);
                if (oldFormat != null && oldFormat.Trim() != BchRecord.FormatValue && oldFormat.Trim().Length > 0)
                    w.Set(level, BchRecord.SavedFormatProperty, oldFormat);
                w.Set(level, format, BchRecord.FormatValue);

                string oldRemark = w.Raw(level, remark);
                if (oldRemark != null && oldRemark.Trim().Length > 0 && !BchRecord.IsMassNote(oldRemark))
                    w.Set(level, BchRecord.SavedRemarkProperty, oldRemark);
                if (mass > 0.00001) w.Set(level, remark, BchRecord.MassNote(mass, settings.MassDecimals));
            }

            // Наименование для спецификации
            string baseTitle = CurrentTitle(w, doc, dict);
            string sortament = "", grade = "", materialText = "";
            MaterialInfo info = Material(app, (PartDoc)doc, w.ActiveConfigurationName(), out materialText);
            if (info != null)
            {
                sortament = info.Sortament;
                grade = (info.Grade + " " + info.GostMaterial).Trim();
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
            string record = BchRecord.SpecTitle(baseTitle, sortament, grade, size);
            string titleLevel = w.ConfigurationNames().Length <= 1 ? "" : w.ActiveConfigurationName();
            w.Set(titleLevel, title, record);
            string fb = w.Raw("", titleFb);
            if (PropertyWriter.IsEmptyOrTemplate(fb)) w.Set("", titleFb, SwPlusMarkup.TitleForStamp(BchRecord.ShortTitle(baseTitle)));

            RemoveLegacyFlag(w);
            return Enabled;
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
                materialName = SyncService.MaterialName(part, (ModelDoc2)part, cfg, out db) ?? "";
                if (materialName.Length == 0) return null;
                return MaterialCatalog.Find(SyncService.MaterialDatabases(app), db, materialName);
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
