using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Перезагрузка форматок в существующих чертежах (план согласования, WP-4.7, Н-33). Встроенная форматка чертежа — копия
    /// той версии, с которой он создан; эталон основной надписи (Р-12) она получает только перезагрузкой, как команда
    /// «Перезагрузка форматки» DProp (FrmDProp:503–545). По умолчанию — пробный прогон с отчётом о версии каждой
    /// встроенной форматки; с apply — резервная копия и перезагрузка из каталога форматок поставки. Открытые пользователем
    /// документы пропускаются.
    /// </summary>
    public static class FormatReloadService
    {
        /// <summary>Эталон формы 1 (мм над низом листа), Правила записи свойств SWPlus, раздел 11.</summary>
        public static readonly Dictionary<string, double> EtalonY = new Dictionary<string, double>
        {
            { "MYPRP0", 55.4 }, { "MYPRP4", 44.5 }, { "MYPRP3", 26.0 }, { "MYPRP15", 38.0 }
        };

        public sealed class SheetResult
        {
            public string File;
            public string Sheet;
            public string Format;
            public string Source;
            public string State;
            public string Geometry;
            public string Action;
        }

        public sealed class Summary
        {
            public readonly List<SheetResult> Sheets = new List<SheetResult>();
            public string ReportPath;
            public string BackupDirectory;
            public int Failures;

            public override string ToString()
            {
                int outdated = Sheets.FindAll(s => s.State == "устарела").Count;
                int reloaded = Sheets.FindAll(s => s.Action == "перезагружена").Count;
                return string.Format("листов: {0}, устаревших форматок: {1}, перезагружено: {2}, ошибок: {3}",
                    Sheets.Count, outdated, reloaded, Failures);
            }
        }

        /// <summary>Каталог SWPlus поставки, найденный вверх от каталога надстройки; основные надписи — в 02 рядом (FindSource).</summary>
        public static string LocateFormats(string addinDirectory)
        {
            string relative = Path.Combine("03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0");
            string cur = addinDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
            {
                string candidate = Path.Combine(cur, relative);
                if (Directory.Exists(candidate)) return candidate;
                DirectoryInfo parent = Directory.GetParent(cur);
                cur = parent != null ? parent.FullName : null;
            }
            return null;
        }

        public static Summary ReloadFiles(ISldWorks app, IEnumerable<string> inputs, bool apply, string reportPath, string swplusDirectory)
        {
            Summary summary = new Summary();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            List<string> files = new List<string>();
            string firstRoot = null;
            foreach (string input in inputs)
            {
                if (firstRoot == null) firstRoot = Directory.Exists(input) ? input : Path.GetDirectoryName(input);
                if (Directory.Exists(input))
                {
                    foreach (string f in Directory.GetFiles(input, "*.slddrw", SearchOption.AllDirectories))
                    {
                        if (f.IndexOf("\\_ESKD_backup_", StringComparison.OrdinalIgnoreCase) < 0 && !Path.GetFileName(f).StartsWith("~$"))
                            files.Add(Path.GetFullPath(f));
                    }
                }
                else if (File.Exists(input))
                {
                    files.Add(Path.GetFullPath(input));
                }
            }
            if (apply && firstRoot != null) summary.BackupDirectory = Path.Combine(firstRoot, "_ESKD_backup_" + stamp);
            summary.ReportPath = string.IsNullOrEmpty(reportPath) ? Path.Combine(Path.GetTempPath(), "ESKD_formats_" + stamp + ".csv") : reportPath;
            foreach (string file in files)
            {
                try
                {
                    ReloadFile(app, file, apply, summary, swplusDirectory, firstRoot);
                }
                catch (Exception ex)
                {
                    Log.Error("Перезагрузка форматок " + file, ex);
                    summary.Failures++;
                    summary.Sheets.Add(new SheetResult { File = file, State = "ошибка: " + ex.Message, Action = "" });
                }
            }
            WriteReport(summary);
            Log.Info(string.Format("Перезагрузка форматок ({0}): {1}; отчёт {2}", apply ? "применение" : "пробный прогон", summary, summary.ReportPath));
            return summary;
        }

        private static void ReloadFile(ISldWorks app, string path, bool apply, Summary summary, string swplus, string root)
        {
            if (app.GetOpenDocumentByName(path) != null)
            {
                summary.Sheets.Add(new SheetResult { File = path, State = "пропуск", Action = "документ открыт в SolidWorks" });
                return;
            }
            int errors = 0, warnings = 0;
            ModelDoc2 doc = app.OpenDoc6(path, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
            if (doc == null)
            {
                summary.Failures++;
                summary.Sheets.Add(new SheetResult { File = path, State = string.Format("ошибка: файл не открыт (errors={0})", errors), Action = "" });
                return;
            }
            try
            {
                DrawingDoc drw = (DrawingDoc)doc;
                List<SheetResult> results = new List<SheetResult>();
                string[] names = drw.GetSheetNames() as string[] ?? new string[0];
                foreach (string name in names)
                {
                    drw.ActivateSheet(name);
                    Sheet sheet = (Sheet)drw.GetCurrentSheet();
                    SheetResult r = new SheetResult { File = path, Sheet = name, Format = Path.GetFileName(sheet.GetTemplateName() ?? "") };
                    r.Source = FindSource(swplus, r.Format);
                    string geometry;
                    bool etalon = IsEtalon(drw, out geometry);
                    r.Geometry = geometry;
                    r.State = geometry == "" ? "без надписей MYPRP" : (etalon ? "эталон" : "устарела");
                    r.Action = r.State == "устарела" && r.Source == null ? "форматка поставки не найдена" : "";
                    results.Add(r);
                }
                bool needed = results.Exists(r => r.State == "устарела" && r.Source != null);
                if (apply && needed)
                {
                    if (!string.IsNullOrEmpty(summary.BackupDirectory))
                    {
                        string relative = root != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                            ? path.Substring(root.Length).TrimStart('\\') : Path.GetFileName(path);
                        string backup = Path.Combine(summary.BackupDirectory, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup));
                        File.Copy(path, backup, false);
                    }
                    foreach (SheetResult r in results)
                    {
                        if (r.State != "устарела" || r.Source == null) continue;
                        drw.ActivateSheet(r.Sheet);
                        r.Action = Reload(drw, r.Source) ? "перезагружена" : "ошибка перезагрузки";
                        if (r.Action != "перезагружена") summary.Failures++;
                    }
                    if (names.Length > 0) drw.ActivateSheet(names[0]);
                    doc.ForceRebuild3(false);
                    if (!doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                    {
                        summary.Failures++;
                        foreach (SheetResult r in results) if (r.Action == "перезагружена") r.Action = "перезагружена, файл не сохранён";
                    }
                }
                summary.Sheets.AddRange(results);
            }
            finally
            {
                app.CloseDoc(doc.GetTitle());
            }
        }

        /// <summary>Как «Перезагрузка форматки» DProp: сброс на стандартный лист и установка форматки поставки с прежними параметрами листа.</summary>
        private static bool Reload(DrawingDoc drw, string source)
        {
            Sheet sheet = (Sheet)drw.GetCurrentSheet();
            double[] p = sheet.GetProperties() as double[];
            if (p == null || p.Length < 7) return false;
            string name = sheet.GetName();
            string view = sheet.CustomPropertyView;
            drw.SetupSheet4(name, (int)p[0], (int)swDwgTemplates_e.swDwgTemplateA4sizeVertical, p[2], p[3], p[4] != 0, "", p[5], p[6], view);
            return drw.SetupSheet4(name, (int)p[0], (int)p[1], p[2], p[3], p[4] != 0, source, p[5], p[6], view);
        }

        /// <summary>Основные надписи — 02_Шаблоны_и_Форматки\Основные надписи папки инструментария; формы спецификации — SpecEditor.</summary>
        private static string FindSource(string swplus, string formatFile)
        {
            if (string.IsNullOrEmpty(swplus) || string.IsNullOrEmpty(formatFile)) return null;
            List<string> folders = new List<string>();
            DirectoryInfo root = Directory.GetParent(swplus.TrimEnd('\\'));
            for (int i = 0; i < 2 && root != null; i++) root = root.Parent;
            if (root != null) folders.Add(Path.Combine(Path.Combine(root.FullName, "02_Шаблоны_и_Форматки"), "Основные надписи"));
            folders.Add(Path.Combine(swplus, "SpecEditor"));
            foreach (string folder in folders)
            {
                string candidate = Path.Combine(folder, formatFile);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Положение надписей формы 1 во встроенной форматке текущего листа против эталона (допуск 0,05 мм).</summary>
        /// <summary>Шрифт надписей эталона (Р-14).</summary>
        public const string EtalonFont = "GOST 2.304 type A";

        /// <summary>
        /// Встроенная форматка текущего листа против эталона: шрифт надписей MYPRP* — «GOST 2.304 type A»; в форме 1 (есть
        /// MYPRP4) положение MYPRP0, MYPRP4, MYPRP3, MYPRP15 — по EtalonY с допуском 0,05 мм; форма 2 проверяется только по шрифту.
        /// </summary>
        private static bool IsEtalon(DrawingDoc drw, out string geometry)
        {
            Dictionary<string, Note> notes = new Dictionary<string, Note>();
            View view = (View)drw.GetFirstView();
            object noteObj = view != null ? view.GetFirstNote() : null;
            while (noteObj != null)
            {
                Note note = (Note)noteObj;
                string name = note.GetName() ?? "";
                if (name.StartsWith("MYPRP", StringComparison.Ordinal) && !notes.ContainsKey(name)) notes[name] = note;
                noteObj = note.GetNext();
            }
            StringBuilder sb = new StringBuilder();
            bool all = notes.Count > 0;
            bool form1 = notes.ContainsKey("MYPRP4");
            foreach (KeyValuePair<string, Note> pair in notes)
            {
                TextFormat format = pair.Value.GetTextFormat() as TextFormat;
                if (format != null && format.TypeFaceName != EtalonFont)
                {
                    all = false;
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(pair.Key).Append(" шрифт ").Append(format.TypeFaceName);
                }
                double wanted;
                if (!form1 || !EtalonY.TryGetValue(pair.Key, out wanted)) continue;
                double[] pos = ((Annotation)pair.Value.GetAnnotation()).GetPosition() as double[];
                if (pos == null || pos.Length < 2) continue;
                double y = Math.Round(pos[1] * 1000.0, 2);
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(pair.Key).Append(" y=").Append(y.ToString("0.##", CultureInfo.InvariantCulture));
                if (Math.Abs(y - wanted) > 0.05) all = false;
            }
            geometry = notes.Count == 0 ? "" : (sb.Length > 0 ? sb.ToString() : "шрифт " + EtalonFont);
            return all;
        }

        private static void WriteReport(Summary summary)
        {
            StringBuilder csv = new StringBuilder();
            csv.AppendLine("Файл;Лист;Форматка;Состояние;Надписи;Форматка поставки;Действие");
            foreach (SheetResult s in summary.Sheets)
                csv.AppendLine(Row(s.File, s.Sheet, s.Format, s.State, s.Geometry, s.Source, s.Action));
            string directory = Path.GetDirectoryName(summary.ReportPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(summary.ReportPath, csv.ToString(), new UTF8Encoding(true));
        }

        private static string Row(params string[] cells)
        {
            string[] quoted = new string[cells.Length];
            for (int i = 0; i < cells.Length; i++) quoted[i] = "\"" + (cells[i] ?? "").Replace("\"", "\"\"") + "\"";
            return string.Join(";", quoted);
        }
    }
}
