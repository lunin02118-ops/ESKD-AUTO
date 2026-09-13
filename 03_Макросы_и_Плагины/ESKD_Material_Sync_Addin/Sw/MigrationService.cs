using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Очистка файлов, сохранённых надстройкой v5 (план, WP-3.3). По умолчанию — пробный прогон: только отчёт.
    /// С apply перед сохранением каждого изменяемого файла делается резервная копия. Файлы, уже открытые
    /// пользователем в SolidWorks, пропускаются: их нельзя сохранить без его ведома.
    /// </summary>
    public static class MigrationService
    {
        public sealed class FileResult
        {
            public string Path;
            public readonly List<MigrationOperation> Operations = new List<MigrationOperation>();
            public int Failures;
            public string Skipped;
            public bool Saved;
        }

        public sealed class Summary
        {
            public readonly List<FileResult> Files = new List<FileResult>();
            public string ReportPath;
            public string BackupDirectory;

            public int Changed { get { return Files.FindAll(f => f.Operations.Count > 0).Count; } }
            public int OperationCount { get { int n = 0; foreach (FileResult f in Files) n += f.Operations.Count; return n; } }
            public int Failures { get { int n = 0; foreach (FileResult f in Files) n += f.Failures + (f.Skipped != null && f.Skipped.StartsWith("ошибка") ? 1 : 0); return n; } }

            public override string ToString()
            {
                return string.Format("файлов: {0}, с изменениями: {1}, операций: {2}, ошибок: {3}", Files.Count, Changed, OperationCount, Failures);
            }
        }

        /// <summary>План (и при apply — применение) для открытого документа.</summary>
        public static FileResult Clean(ModelDoc2 doc, bool apply)
        {
            FileResult result = new FileResult { Path = DocInfo.PathOf(doc) };
            int type = doc.GetType();
            bool isDrawing = type == (int)swDocumentTypes_e.swDocDRAWING;
            if (!isDrawing && type != (int)swDocumentTypes_e.swDocPART && type != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                result.Skipped = "не деталь, сборка или чертёж";
                return result;
            }
            Settings settings = Settings.Read();
            PropertyDictionary dict = SyncService.Dictionary(settings);
            PropertyWriter w = new PropertyWriter(doc, !apply);
            bool transfer = !isDrawing && !SyncService.IsProtected(w, doc);
            PropertyLevels levels = Read(w, isDrawing);
            MigrationContext context = null;
            if (!isDrawing && result.Path.Length > 0)
            {
                context = new MigrationContext
                {
                    FileTitle = SwPlusFormat.FileTitle(result.Path),
                    IsAssembly = type == (int)swDocumentTypes_e.swDocASSEMBLY,
                    Grams = SyncService.MassInGrams(w, doc),
                    SmallFont = dict.SmallFontMarkup,
                    ActiveConfiguration = w.ActiveConfigurationName()
                };
            }
            result.Operations.AddRange(LegacyMigration.Plan(levels, dict, isDrawing, true, transfer, context));
            if (!apply) return result;
            foreach (MigrationOperation op in result.Operations)
            {
                if (op.Action == MigrationAction.Delete) w.Delete(op.Level, op.Name);
                else w.Set(op.Level, op.Name, op.NewValue);
            }
            result.Failures = w.Failures;
            return result;
        }

        public static PropertyLevels Read(PropertyWriter w, bool isDrawing)
        {
            PropertyLevels levels = new PropertyLevels();
            if (!isDrawing)
            {
                foreach (string cfg in w.ConfigurationNames()) levels.AddConfiguration(cfg);
            }
            foreach (string level in new List<string>(levels.Levels))
            {
                foreach (string name in w.NamesAt(level))
                {
                    string raw = w.Raw(level, name);
                    if (raw != null) levels.Set(level, name, raw);
                }
            }
            return levels;
        }

        /// <summary>Пакетная очистка файлов и каталогов (рекурсивно .sldprt, .sldasm, .slddrw) с отчётом CSV.</summary>
        public static Summary CleanFiles(ISldWorks app, IEnumerable<string> inputs, bool apply, string reportPath)
        {
            Summary summary = new Summary();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            List<KeyValuePair<string, string>> files = Collect(inputs); // путь → относительный путь для резервной копии
            string firstRoot = null;
            foreach (string input in inputs) { firstRoot = Directory.Exists(input) ? input : System.IO.Path.GetDirectoryName(input); break; }
            if (apply && firstRoot != null) summary.BackupDirectory = System.IO.Path.Combine(firstRoot, "_ESKD_backup_" + stamp);
            summary.ReportPath = string.IsNullOrEmpty(reportPath)
                ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ESKD_clean_" + stamp + ".csv")
                : reportPath;

            foreach (KeyValuePair<string, string> file in files)
            {
                FileResult result;
                try
                {
                    result = CleanFile(app, file.Key, file.Value, apply, summary.BackupDirectory);
                }
                catch (Exception ex)
                {
                    Log.Error("Очистка " + file.Key, ex);
                    result = new FileResult { Path = file.Key, Skipped = "ошибка: " + ex.Message };
                }
                summary.Files.Add(result);
            }
            WriteReport(summary, apply);
            Log.Info(string.Format("Очистка v5 ({0}): {1}; отчёт {2}", apply ? "применение" : "пробный прогон", summary, summary.ReportPath));
            return summary;
        }

        private static FileResult CleanFile(ISldWorks app, string path, string relative, bool apply, string backupDirectory)
        {
            if (app.GetOpenDocumentByName(path) != null)
                return new FileResult { Path = path, Skipped = "документ открыт в SolidWorks — пропущен" };
            int type = DocumentType(path);
            int errors = 0, warnings = 0;
            ModelDoc2 doc = app.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
            if (doc == null)
                return new FileResult { Path = path, Skipped = string.Format("ошибка: файл не открыт (errors={0}, warnings={1})", errors, warnings) };
            try
            {
                FileResult result = Clean(doc, false);
                if (!apply || result.Operations.Count == 0 || result.Skipped != null) return result;
                if (!string.IsNullOrEmpty(backupDirectory))
                {
                    string backup = System.IO.Path.Combine(backupDirectory, relative);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup));
                    File.Copy(path, backup, false);
                }
                FileResult applied = Clean(doc, true);
                applied.Saved = Save(doc, path, applied);
                // Сохранение в SolidWorks с надстройкой ЕСКД дописывает реквизиты (например, «Материал_ФБ» в конфигурации),
                // после чего часть общих копий становится лишней: второй проход доводит файл до чистого состояния.
                FileResult second = Clean(doc, true);
                if (second.Operations.Count > 0)
                {
                    applied.Operations.AddRange(second.Operations);
                    applied.Failures += second.Failures;
                    applied.Saved = Save(doc, path, applied) && applied.Saved;
                }
                return applied;
            }
            finally
            {
                app.CloseDoc(doc.GetTitle());
            }
        }

        private static bool Save(ModelDoc2 doc, string path, FileResult result)
        {
            int errors = 0, warnings = 0;
            bool ok = doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
            if (!ok)
            {
                result.Failures++;
                Log.Error(string.Format("Очистка: файл не сохранён {0} (errors={1}, warnings={2})", path, errors, warnings));
            }
            return ok;
        }

        private static List<KeyValuePair<string, string>> Collect(IEnumerable<string> inputs)
        {
            List<KeyValuePair<string, string>> parts = new List<KeyValuePair<string, string>>();
            List<KeyValuePair<string, string>> assemblies = new List<KeyValuePair<string, string>>();
            List<KeyValuePair<string, string>> drawings = new List<KeyValuePair<string, string>>();
            foreach (string input in inputs)
            {
                if (Directory.Exists(input))
                {
                    string root = System.IO.Path.GetFullPath(input).TrimEnd('\\') + "\\";
                    foreach (string f in Directory.GetFiles(input, "*.*", SearchOption.AllDirectories))
                    {
                        if (f.IndexOf("\\_ESKD_backup_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        Add(parts, assemblies, drawings, System.IO.Path.GetFullPath(f), System.IO.Path.GetFullPath(f).Substring(root.Length));
                    }
                }
                else if (File.Exists(input))
                {
                    Add(parts, assemblies, drawings, System.IO.Path.GetFullPath(input), System.IO.Path.GetFileName(input));
                }
            }
            // Детали раньше сборок, чертежи последними: сборка не держит открытыми детали, которые ещё предстоит сохранить.
            List<KeyValuePair<string, string>> all = new List<KeyValuePair<string, string>>(parts);
            all.AddRange(assemblies);
            all.AddRange(drawings);
            return all;
        }

        private static void Add(List<KeyValuePair<string, string>> parts, List<KeyValuePair<string, string>> assemblies,
            List<KeyValuePair<string, string>> drawings, string path, string relative)
        {
            string name = System.IO.Path.GetFileName(path);
            if (name.StartsWith("~$", StringComparison.Ordinal)) return;
            switch (DocumentType(path))
            {
                case (int)swDocumentTypes_e.swDocPART: parts.Add(new KeyValuePair<string, string>(path, relative)); break;
                case (int)swDocumentTypes_e.swDocASSEMBLY: assemblies.Add(new KeyValuePair<string, string>(path, relative)); break;
                case (int)swDocumentTypes_e.swDocDRAWING: drawings.Add(new KeyValuePair<string, string>(path, relative)); break;
            }
        }

        private static int DocumentType(string path)
        {
            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".sldprt") return (int)swDocumentTypes_e.swDocPART;
            if (ext == ".sldasm") return (int)swDocumentTypes_e.swDocASSEMBLY;
            if (ext == ".slddrw") return (int)swDocumentTypes_e.swDocDRAWING;
            return 0;
        }

        private static void WriteReport(Summary summary, bool apply)
        {
            StringBuilder csv = new StringBuilder();
            csv.AppendLine("Файл;Уровень;Имя;Действие;Было;Стало;Причина");
            foreach (FileResult file in summary.Files)
            {
                if (file.Skipped != null)
                {
                    csv.AppendLine(Row(file.Path, "", "", "пропуск", "", "", file.Skipped));
                    continue;
                }
                foreach (MigrationOperation op in file.Operations)
                {
                    string action = op.Action == MigrationAction.Delete ? "удалить" : "записать";
                    if (apply) action = op.Action == MigrationAction.Delete ? "удалено" : "записано";
                    csv.AppendLine(Row(file.Path, string.IsNullOrEmpty(op.Level) ? "общие" : op.Level, op.Name, action,
                        op.OldValue ?? "", op.NewValue ?? "", op.Reason));
                }
            }
            string directory = System.IO.Path.GetDirectoryName(summary.ReportPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(summary.ReportPath, csv.ToString(), new UTF8Encoding(true));
        }

        private static string Row(params string[] cells)
        {
            string[] quoted = new string[cells.Length];
            for (int i = 0; i < cells.Length; i++)
                quoted[i] = "\"" + (cells[i] ?? "").Replace("\"", "\"\"").Replace("\r", "").Replace("\n", "⏎") + "\"";
            return string.Join(";", quoted);
        }
    }
}
