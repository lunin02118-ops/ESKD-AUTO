using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using Environment = System.Environment;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Кнопка К-8 «Снимок эталона» (ТЗ-02 Т-53…Т-55). Состояние эталона базы складывается в
    /// `_Версии\&lt;дата&gt;_Изм&lt;N&gt;\` обычным копированием файлов: снимок должен открываться сам по себе,
    /// без Pack and Go и без SolidWorks. Повторный снимок того же состояния не делается — по контрольным
    /// суммам видно, что ничего не изменилось. В `Изменения.xlsx` эталона дописывается строка с
    /// применяемостью: какие открытые заказы уже взяли этот эталон в производство.
    /// </summary>
    public static class EtalonService
    {
        /// <summary>«ok|папка снимка|файлов|применяемость» или «error|текст» — для автотестов (Т-9).</summary>
        public static string LastOutcome = "";

        public const string VersionsFolder = "_Версии";
        /// <summary>
        /// Манифест снимка: «относительный путь TAB SHA-256» на строку. С ним повторный снимок сравнивается с прежними
        /// без перечитывания их файлов по сети (аудит 19.09, Я-В5); снимки без манифеста сравниваются по файлам, как раньше.
        /// </summary>
        public const string ManifestName = "_Снимок.txt";
        /// <summary>Что входит в снимок: модели, PDF, программы ЧПУ и ведомость изделия.</summary>
        public static readonly string[] Folders = { LzkNaming.ModelsFolder, ExportNaming.PdfFolder, ExportNaming.CncFolder, LzkNaming.DocsFolder };

        public static bool Run(ISldWorks app, bool interactive)
        {
            return Run(app, interactive, null, null);
        }

        public static bool Run(ISldWorks app, bool interactive, string what, string code)
        {
            LastOutcome = "";
            try
            {
                ModelDoc2 doc = app.ActiveDoc as ModelDoc2;
                string path = doc == null ? "" : (doc.GetPathName() ?? "");
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY || path.Length == 0)
                {
                    Fail(app, interactive, "Снимок делается на сохранённой сборке эталона.");
                    return false;
                }
                ProductLocation location = ProductLocator.Locate(path);
                if (!location.InBase)
                {
                    Fail(app, interactive, "Снимок делается только для эталона базы: этот документ лежит вне базы.");
                    return false;
                }
                string productFolder = location.ProductFolder.Length > 0 ? location.ProductFolder : LzkNaming.ProductFolder(path);
                if (productFolder.Length == 0)
                {
                    Fail(app, interactive, "Папка эталона не найдена: рядом с моделями должна быть папка «" +
                        LzkNaming.ModelsFolder + "».");
                    return false;
                }

                Status(app, "ЕСКД: снимок эталона — состояние…");
                int revision = Revision(productFolder);
                Dictionary<string, string> state = State(productFolder);
                string same = SameSnapshot(productFolder, state);
                if (same.Length > 0)
                {
                    Fail(app, interactive, "Эталон не изменился с прошлого снимка «" + same +
                        "»: новый снимок не нужен.");
                    return false;
                }
                string target = Path.Combine(productFolder, VersionsFolder,
                    DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_Изм" + revision);
                for (int i = 2; Directory.Exists(target); i++)
                    target = Path.Combine(productFolder, VersionsFolder,
                        DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_Изм" + revision + "_" + i);

                Status(app, "ЕСКД: снимок эталона — копирование…");
                int files = Copy(productFolder, target, state);
                string wrong = Verify(target, state);
                if (wrong.Length > 0)
                {
                    Fail(app, interactive, "Снимок не сходится с эталоном (" + wrong +
                        "): папка «" + Path.GetFileName(target) + "» оставлена для разбора.");
                    return false;
                }
                WriteManifest(target, state);

                string applicability = Applicability(productFolder, path);
                ChangeLog.Append(ChangeLog.Path(productFolder), new ChangeRow
                {
                    Revision = revision,
                    Date = DateTime.Now,
                    Who = Settings.AuthorOrUser(),
                    Document = "снимок " + Path.GetFileName(target),
                    What = string.IsNullOrWhiteSpace(what) ? "снимок состояния эталона" : what.Trim(),
                    Reason = ChangeReasons.ByCode(code).Text,
                    Code = ChangeReasons.ByCode(code).Code,
                    Backlog = "использовать",
                    Applicability = applicability
                });
                LastOutcome = string.Join("|", new[]
                {
                    "ok", target, files.ToString(CultureInfo.InvariantCulture),
                    applicability.Length > 0 ? applicability : "нет открытых заказов"
                });
                if (interactive)
                    MessageBox.Show("Снимок эталона готов: " + Environment.NewLine + target + Environment.NewLine +
                        Environment.NewLine + "Файлов: " + files + Environment.NewLine +
                        "Применяемость: " + (applicability.Length > 0 ? applicability : "нет открытых заказов"),
                        "ЕСКД: снимок эталона", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Status(app, "");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Снимок эталона", ex);
                Fail(app, interactive, "Снимок не сделан: " + ex.Message);
                return false;
            }
        }

        /// <summary>Наибольшая ревизия чертежей эталона (Т-54); чертежей без ревизии — 0.</summary>
        private static int Revision(string productFolder)
        {
            int highest = 0;
            string models = Path.Combine(productFolder, LzkNaming.ModelsFolder);
            if (!Directory.Exists(models)) return 0;
            foreach (string file in Directory.GetFiles(models, "*.slddrw", SearchOption.AllDirectories))
            {
                // Ревизию чертежа видно и без SolidWorks — по именам файлов выдачи «…_ИзмN» в папках изделия.
                string stem = Path.GetFileNameWithoutExtension(file);
                foreach (string pdf in Directory.Exists(ExportNaming.PdfDirectory(productFolder))
                    ? Directory.GetFiles(ExportNaming.PdfDirectory(productFolder), stem + "_Изм*.pdf")
                    : new string[0])
                {
                    int number = ExportNaming.Revision(Path.GetFileNameWithoutExtension(pdf).Split(new[] { "_Изм" },
                        StringSplitOptions.None).Last());
                    if (number > highest) highest = number;
                }
            }
            return highest;
        }

        /// <summary>Состояние эталона: относительный путь файла → SHA-256.</summary>
        public static Dictionary<string, string> State(string productFolder)
        {
            Dictionary<string, string> state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in Folders)
            {
                string full = Path.Combine(productFolder, folder);
                if (!Directory.Exists(full)) continue;
                foreach (string file in Directory.GetFiles(full, "*.*", SearchOption.AllDirectories))
                    state[Relative(productFolder, file)] = Checksum(file);
            }
            foreach (string book in Directory.Exists(productFolder)
                ? Directory.GetFiles(productFolder, LzkNaming.LegacyWorkbookPrefix + "*.xlsx")
                : new string[0])
                state[Relative(productFolder, book)] = Checksum(book);
            return state;
        }

        private static string Relative(string root, string path)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : Path.GetFileName(path);
        }

        /// <summary>Имя прежнего снимка с тем же состоянием или пустая строка (Т-54).</summary>
        private static string SameSnapshot(string productFolder, Dictionary<string, string> state)
        {
            string versions = Path.Combine(productFolder, VersionsFolder);
            if (!Directory.Exists(versions)) return "";
            foreach (string snapshot in Directory.GetDirectories(versions).OrderByDescending(d => d))
            {
                Dictionary<string, string> previous = ReadManifest(snapshot) ?? State(snapshot);
                if (previous.Count != state.Count) continue;
                bool same = state.All(pair =>
                {
                    string sum;
                    return previous.TryGetValue(pair.Key, out sum) && sum == pair.Value;
                });
                if (same) return Path.GetFileName(snapshot);
            }
            return "";
        }

        private static void WriteManifest(string target, Dictionary<string, string> state)
        {
            try
            {
                File.WriteAllLines(Path.Combine(target, ManifestName),
                    state.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "\t" + pair.Value),
                    new UTF8Encoding(true));
            }
            catch (IOException ex)
            {
                // Без манифеста снимок полноценен: следующий снимок сравнит его по файлам.
                Log.Error("Снимок эталона: манифест " + target, ex);
            }
        }

        /// <summary>Состояние прежнего снимка из манифеста или null, если манифеста нет или он не читается.</summary>
        private static Dictionary<string, string> ReadManifest(string snapshot)
        {
            string path = Path.Combine(snapshot, ManifestName);
            if (!File.Exists(path)) return null;
            try
            {
                Dictionary<string, string> state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int tab = line.LastIndexOf('\t');
                    if (tab <= 0) return null;
                    state[line.Substring(0, tab)] = line.Substring(tab + 1).Trim();
                }
                return state;
            }
            catch (IOException ex)
            {
                Log.Error("Снимок эталона: манифест " + path, ex);
                return null;
            }
        }

        private static int Copy(string productFolder, string target, Dictionary<string, string> state)
        {
            int files = 0;
            foreach (string relative in state.Keys)
            {
                string source = Path.Combine(productFolder, relative);
                if (!File.Exists(source)) continue;
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? target);
                File.Copy(source, destination, true);
                files++;
            }
            return files;
        }

        /// <summary>Первый файл снимка, не совпавший с эталоном, или пустая строка.</summary>
        private static string Verify(string target, Dictionary<string, string> state)
        {
            foreach (KeyValuePair<string, string> pair in state)
            {
                string copy = Path.Combine(target, pair.Key);
                if (!File.Exists(copy)) return "нет файла " + pair.Key;
                if (Checksum(copy) != pair.Value) return "не совпала сумма " + pair.Key;
            }
            return "";
        }

        /// <summary>
        /// Открытые заказы, взявшие этот эталон (Т-55): в папке заказа есть `_Выдано_*.txt` с именем файла
        /// эталона, а сам заказ ещё не сдан (в имени нет «_Сдано»).
        /// </summary>
        public static string Applicability(string productFolder, string assemblyPath)
        {
            List<string> orders = new List<string>();
            try
            {
                string root = OrdersRoot(productFolder);
                if (root.Length == 0) return "";
                string name = Path.GetFileName(assemblyPath);
                foreach (string order in Directory.GetDirectories(root))
                {
                    if (Path.GetFileName(order).IndexOf("_Сдано", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    foreach (string report in Directory.GetFiles(order, ExportNaming.IssuedPrefix + "*.txt", SearchOption.AllDirectories))
                    {
                        string text;
                        try
                        {
                            text = File.ReadAllText(report, Encoding.UTF8);
                        }
                        catch (IOException)
                        {
                            continue;
                        }
                        if (text.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string label = Path.GetFileName(order);
                        if (!orders.Contains(label)) orders.Add(label);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Снимок эталона: применяемость", ex);
            }
            orders.Sort(StringComparer.CurrentCultureIgnoreCase);
            return string.Join(", ", orders.ToArray());
        }

        /// <summary>Корень заказов рядом с базой: «_Заявки» или «03_ЗАКАЗЫ» на том же сетевом диске.</summary>
        private static string OrdersRoot(string productFolder)
        {
            for (string current = productFolder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                foreach (string name in ProductLocator.OrderRoots)
                {
                    string candidate = Path.Combine(current, name);
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
            return "";
        }

        private static void Fail(ISldWorks app, bool interactive, string text)
        {
            LastOutcome = "error|" + text;
            Status(app, "");
            if (interactive) MessageBox.Show(text, "ЕСКД: снимок эталона", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Status(ISldWorks app, string text)
        {
            try
            {
                Frame frame = app.Frame() as Frame;
                if (frame != null && text != null) frame.SetStatusBarText(text);
            }
            catch (Exception ex)
            {
                Log.Error("Снимок эталона: строка состояния", ex);
            }
        }

        private static string Checksum(string path)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch (IOException ex)
            {
                Log.Error("Снимок эталона: контрольная сумма " + path, ex);
                return "";
            }
        }
    }
}
