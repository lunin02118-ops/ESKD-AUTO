using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace ESKD.MaterialSync.Core
{
    public sealed class MaterialInfo
    {
        public string Name = "";
        public string Database = "";
        public string Sortament = "";
        public string GostSortament = "";
        public string Grade = "";
        public string GostMaterial = "";
        public string GostDesignation = "";
        public string LineDesignation = "";
        public double Density;
    }

    /// <summary>
    /// Библиотеки материалов .sldmat с кэшем по пути и времени изменения файла (Д-11):
    /// файл разбирается один раз, а не для каждой конфигурации при каждом сохранении.
    /// </summary>
    public static class MaterialCatalog
    {
        private sealed class Entry
        {
            public DateTime Stamp;
            public Dictionary<string, MaterialInfo> Materials;
            public HashSet<string> Records;
        }

        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Sync = new object();

        public static MaterialInfo Find(IEnumerable<string> databasePaths, string databaseName, string materialName)
        {
            if (string.IsNullOrEmpty(materialName) || databasePaths == null) return null;
            List<string> paths = new List<string>(databasePaths);
            if (!string.IsNullOrEmpty(databaseName))
            {
                foreach (string p in paths)
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(p), databaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        MaterialInfo hit = Lookup(p, materialName);
                        if (hit != null) return hit;
                    }
                }
            }
            foreach (string p in paths)
            {
                MaterialInfo hit = Lookup(p, materialName);
                if (hit != null) return hit;
            }
            return null;
        }

        public static MaterialInfo Lookup(string databasePath, string materialName)
        {
            Dictionary<string, MaterialInfo> all = Load(databasePath);
            MaterialInfo info;
            return all != null && all.TryGetValue(materialName, out info) ? info : null;
        }

        public static Dictionary<string, MaterialInfo> Load(string databasePath)
        {
            if (string.IsNullOrEmpty(databasePath) || !File.Exists(databasePath)) return null;
            DateTime stamp = File.GetLastWriteTimeUtc(databasePath);
            lock (Sync)
            {
                Entry e;
                if (Cache.TryGetValue(databasePath, out e) && e.Stamp == stamp) return e.Materials;
            }
            Dictionary<string, MaterialInfo> parsed = Parse(databasePath);
            lock (Sync)
            {
                Entry fresh = new Entry();
                fresh.Stamp = stamp;
                fresh.Materials = parsed;
                Cache[databasePath] = fresh;
            }
            return parsed;
        }

        public static void ClearCache()
        {
            lock (Sync) { Cache.Clear(); }
        }

        /// <summary>
        /// Значение (с LF вместо CR LF) — запись какого-либо материала этих библиотек: «Материал_ФБ» или «Материал_Таблица»,
        /// которые пишет надстройка (с тегами FONT и без них, флаг prpFontSize), однострочная запись или имя материала,
        /// которое без разметки писала v5. Такое значение принадлежит системе и следует за материалом.
        /// </summary>
        public static bool IsSystemRecord(IEnumerable<string> databasePaths, string value)
        {
            if (string.IsNullOrEmpty(value) || databasePaths == null) return false;
            foreach (string path in databasePaths)
            {
                if (Load(path) == null) continue;
                HashSet<string> records;
                lock (Sync)
                {
                    Entry e;
                    if (!Cache.TryGetValue(path, out e)) continue;
                    if (e.Records == null)
                    {
                        e.Records = new HashSet<string>(StringComparer.Ordinal);
                        foreach (MaterialInfo info in e.Materials.Values)
                        {
                            e.Records.Add(info.Name.Trim());
                            foreach (bool smallFont in new[] { true, false })
                            {
                                MaterialRecord r = MaterialRecord.FromLibrary(info, smallFont);
                                if (r == null) continue;
                                e.Records.Add(MaterialRecord.Normalize(r.Stamp));
                                e.Records.Add(MaterialRecord.Normalize(r.Table));
                                e.Records.Add(r.Line);
                            }
                        }
                    }
                    records = e.Records;
                }
                if (records.Contains(value)) return true;
            }
            return false;
        }

        /// <summary>Корпоративная библиотека рядом с надстройкой: …/04_Библиотеки_Материалов_и_Профилей/Библиотека материалов.</summary>
        public const string CorporateLibraryFile = "Библиотека_Материалов_ГОСТ.sldmat";

        public static string LocateCorporateLibrary(string addinDirectory)
        {
            string relative = Path.Combine("04_Библиотеки_Материалов_и_Профилей", "Библиотека материалов", CorporateLibraryFile);
            string cur = addinDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
            {
                string candidate = Path.Combine(cur, relative);
                if (File.Exists(candidate)) return candidate;
                DirectoryInfo parent = Directory.GetParent(cur);
                cur = parent != null ? parent.FullName : null;
            }
            return null;
        }

        /// <summary>
        /// Базы SolidWorks и после них корпоративная библиотека из поставки (Д-39): дробь, записанная по библиотеке,
        /// узнаётся и заменяется при смене материала, даже если в списке баз SolidWorks этой библиотеки нет.
        /// </summary>
        public static List<string> WithCorporateLibrary(IEnumerable<string> databasePaths, string corporateLibrary)
        {
            List<string> result = new List<string>();
            if (databasePaths != null)
            {
                foreach (string p in databasePaths)
                {
                    if (!string.IsNullOrEmpty(p)) result.Add(p);
                }
            }
            if (string.IsNullOrEmpty(corporateLibrary)) return result;
            foreach (string p in result)
            {
                if (string.Equals(FullPath(p), FullPath(corporateLibrary), StringComparison.OrdinalIgnoreCase)) return result;
            }
            result.Add(corporateLibrary);
            return result;
        }

        private static string FullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                return path;
            }
            catch (NotSupportedException)
            {
                return path;
            }
            catch (PathTooLongException)
            {
                return path;
            }
        }

        private static Dictionary<string, MaterialInfo> Parse(string databasePath)
        {
            Dictionary<string, MaterialInfo> result = new Dictionary<string, MaterialInfo>(StringComparer.Ordinal);
            try
            {
                XmlDocument xml = new XmlDocument();
                xml.XmlResolver = null;
                xml.Load(databasePath);
                string dbName = Path.GetFileNameWithoutExtension(databasePath);
                // Обход элементов, а не XPath с подстановкой имени: кавычки в имени материала безопасны.
                foreach (XmlNode node in xml.GetElementsByTagName("material"))
                {
                    XmlAttribute nameAttr = node.Attributes != null ? node.Attributes["name"] : null;
                    if (nameAttr == null || string.IsNullOrEmpty(nameAttr.Value)) continue;
                    MaterialInfo info = new MaterialInfo();
                    info.Name = nameAttr.Value;
                    info.Database = dbName;
                    foreach (XmlNode child in node.ChildNodes)
                    {
                        if (child.Name == "physicalproperties")
                        {
                            foreach (XmlNode prop in child.ChildNodes)
                            {
                                if (prop.Name == "DENS" && prop.Attributes != null && prop.Attributes["value"] != null)
                                {
                                    double d;
                                    if (double.TryParse(prop.Attributes["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                                        info.Density = d;
                                }
                            }
                        }
                        else if (child.Name == "custom")
                        {
                            foreach (XmlNode prop in child.ChildNodes)
                            {
                                if (prop.Attributes == null || prop.Attributes["name"] == null || prop.Attributes["value"] == null) continue;
                                string pn = prop.Attributes["name"].Value;
                                string pv = prop.Attributes["value"].Value;
                                switch (pn)
                                {
                                    case "Сортамент": info.Sortament = pv; break;
                                    case "ГОСТ_Сортамент": info.GostSortament = pv; break;
                                    case "Марка_Материала": info.Grade = pv; break;
                                    case "ГОСТ_Материал": info.GostMaterial = pv; break;
                                    case "Обозначение_ГОСТ": info.GostDesignation = pv; break;
                                    case "Обозначение_Строка": info.LineDesignation = pv; break;
                                }
                            }
                        }
                    }
                    if (!result.ContainsKey(info.Name)) result.Add(info.Name, info);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Библиотека материалов не прочитана: " + databasePath + " — " + ex.Message);
            }
            return result;
        }
    }
}
