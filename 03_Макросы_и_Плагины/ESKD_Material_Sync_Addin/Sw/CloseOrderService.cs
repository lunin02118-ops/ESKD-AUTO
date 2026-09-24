using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// Кнопка К-6 «Закрыть заказ» (ТЗ-02 Т-44…Т-47). Собирает заказ во временную папку (Pack and Go
    /// комплектов изделий + остальные файлы заказа), сверяет копию по контрольным суммам, переносит
    /// в архив `_Архив\&lt;год&gt;\&lt;заказ&gt;` рядом с `_Заявки` (<see cref="OrderArchive"/>) и убирает папку заказа в `_Сдано` с запиской `_Архив.txt`.
    /// Ничего не удаляется, пока копия не сверена: оборванный перенос оставляет временную папку,
    /// и повторный запуск продолжает с неё (Т-46).
    /// </summary>
    public static class CloseOrderService
    {
        /// <summary>«ok|архив|файлов|сдано» или «error|текст» — для автотестов (Т-9).</summary>
        public static string LastOutcome = "";

        public const string TempFolder = "_tmp";
        public const string DoneFolder = "_Сдано";
        public const string KitsFolder = "Комплекты";
        public const string ArchiveNote = "_Архив.txt";

        public static bool Run(ISldWorks app, bool interactive, string orderFolder, string archiveRoot)
        {
            LastOutcome = "";
            string temp = "";
            try
            {
                string order = Order(app, orderFolder);
                if (order.Length == 0 && interactive)
                    using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                    {
                        dialog.Description = "Папка заказа, который закрываем";
                        if (dialog.ShowDialog() == DialogResult.OK) order = dialog.SelectedPath;
                    }
                if (order.Length == 0)
                {
                    Fail(app, interactive, "Не найдена папка заказа: откройте документ заказа или укажите папку.");
                    return false;
                }
                // Отметка «Готово к производству» лежит в папке изделия (ТЗ-04 Р4-8), у старых заказов — в папке заказа.
                if (Directory.GetFiles(order, ExportNaming.IssuedPrefix + "*.txt", SearchOption.AllDirectories).Length == 0)
                {
                    Fail(app, interactive, "Заказ «" + Path.GetFileName(order) + "» не выдавался в производство: " +
                        "ни одно изделие не отмечено «Готово к производству» (нет «" + ExportNaming.IssuedPrefix + "…txt»). Закрывать нечего.");
                    return false;
                }
                string open = OpenInside(app, order);
                if (open.Length > 0)
                {
                    Fail(app, interactive, "Документ заказа открыт в SolidWorks — закройте его и повторите:" +
                        Environment.NewLine + open);
                    return false;
                }
                string namesakes = OpenNamesakes(app, order);
                if (namesakes.Length > 0)
                {
                    Fail(app, interactive, "В SolidWorks открыты одноимённые документы из других папок — сборка заказа взяла " +
                        "бы их вместо своих, и в архив ушли бы чужие файлы. Закройте документы из списка и повторите:" + Environment.NewLine + namesakes);
                    return false;
                }
                string root = (archiveRoot ?? "").Trim();
                if (root.Length == 0) root = OrderArchive.DefaultRoot(order);
                if (interactive)
                {
                    // Закрытие заказа необратимо для рабочей папки: спрашиваем один раз, но по-честному —
                    // с именем заказа и адресом архива, чтобы «да» нажимали осознанно.
                    DialogResult answer = MessageBox.Show(
                        "Закрыть заказ «" + Path.GetFileName(order) + "»?" + Environment.NewLine + Environment.NewLine +
                        "Комплекты изделий и файлы заказа уедут в архив " + root + "," + Environment.NewLine +
                        "папка заказа переедет в «" + DoneFolder + "».",
                        "ЕСКД: закрыть заказ", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes)
                    {
                        LastOutcome = "error|отменено";
                        return false;
                    }
                }
                if (!Directory.Exists(root))
                {
                    Fail(app, interactive, "Архив «" + root + "» недоступен: нет папки или нет прав на запись (создаёт сисадмин). Заказ не тронут.");
                    return false;
                }

                // Заказ с тем же именем уже в архиве или в «_Сдано» (повтор после сбоя, одноимённый заказ прошлых лет):
                // перенос слил бы папки, а откат при ошибке удалил бы и прежнее содержимое. Отказ до любых действий.
                string target = Path.Combine(root, DateTime.Now.Year.ToString(CultureInfo.InvariantCulture), Path.GetFileName(order));
                string done = Path.Combine(OrdersRoot(order), DoneFolder, Path.GetFileName(order));
                foreach (string busy in new[] { target, done })
                    if (Directory.Exists(busy))
                    {
                        Fail(app, interactive, "Папка «" + busy + "» уже есть. Заказ не тронут: проверьте, не закрывался ли он " +
                            "раньше, и переименуйте ту папку или этот заказ.");
                        return false;
                    }

                temp = Path.Combine(OrdersRoot(order), TempFolder, Path.GetFileName(order));
                Directory.CreateDirectory(temp);

                Status(app, "ЕСКД: закрытие заказа — комплекты изделий…");
                List<string> problems = new List<string>();
                Kits(app, order, temp, problems);
                if (problems.Count > 0)
                {
                    Fail(app, interactive, "Комплекты собраны не полностью, заказ не закрыт (временная папка " +
                        temp + " оставлена для повтора):" + Environment.NewLine + Environment.NewLine +
                        string.Join(Environment.NewLine, problems.ToArray()));
                    return false;
                }

                Status(app, "ЕСКД: закрытие заказа — копия папки заказа…");
                int copied = CopyTree(order, temp, problems);
                if (problems.Count > 0)
                {
                    Fail(app, interactive, "Копия заказа не собрана (временная папка " + temp + " оставлена):" +
                        Environment.NewLine + string.Join(Environment.NewLine, problems.ToArray()));
                    return false;
                }

                Status(app, "ЕСКД: закрытие заказа — сверка копии…");
                Verify(order, temp, problems);
                if (problems.Count > 0)
                {
                    Fail(app, interactive, "Копия не совпала с оригиналом, перенос не начат:" + Environment.NewLine +
                        string.Join(Environment.NewLine, problems.Take(20).ToArray()));
                    return false;
                }

                Status(app, "ЕСКД: закрытие заказа — перенос в архив…");
                if (!Move(temp, target, problems))
                {
                    // Недописанная папка в архиве убирается целиком: половина заказа хуже, чем ничего (Т-46).
                    Remove(target);
                    Fail(app, interactive, "Перенос в архив не удался, архив очищен, временная папка " + temp +
                        " оставлена — повторите запуск:" + Environment.NewLine +
                        string.Join(Environment.NewLine, problems.Take(20).ToArray()));
                    return false;
                }

                Status(app, "ЕСКД: закрытие заказа — папка в «" + DoneFolder + "»…");
                Note(order, target, copied);
                bool moved = Move(order, done, problems);
                // Отчёты выдачи заказа переехали: запомненные ответы кнопки «Новая ревизия» больше не верны.
                RevisionService.ForgetIssued();
                if (!moved)
                {
                    LastOutcome = "error|Заказ в архиве «" + target + "», но папка заказа не перенесена в «" +
                        DoneFolder + "»: " + string.Join("; ", problems.Take(5).ToArray());
                    Status(app, "");
                    if (interactive) MessageBox.Show(LastOutcome.Substring(6), "ЕСКД: закрыть заказ",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                Remove(temp);

                LastOutcome = string.Join("|", new[] { "ok", target, copied.ToString(CultureInfo.InvariantCulture), done });
                if (interactive)
                    MessageBox.Show("Заказ закрыт." + Environment.NewLine + Environment.NewLine +
                        "Архив:  " + target + Environment.NewLine +
                        "Файлов: " + copied + Environment.NewLine +
                        "Папка заказа: " + done, "ЕСКД: закрыть заказ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Status(app, "");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Закрытие заказа", ex);
                Fail(app, interactive, "Заказ не закрыт: " + ex.Message +
                    (temp.Length > 0 ? Environment.NewLine + "Временная папка " + temp + " оставлена для повтора." : ""));
                return false;
            }
        }

        // ------------------------------------------------------------------ заказ
        private static string Order(ISldWorks app, string orderFolder)
        {
            if (!string.IsNullOrWhiteSpace(orderFolder) && Directory.Exists(orderFolder)) return Path.GetFullPath(orderFolder.Trim());
            try
            {
                ModelDoc2 doc = app != null ? app.ActiveDoc as ModelDoc2 : null;
                string path = doc == null ? "" : (doc.GetPathName() ?? "");
                return path.Length == 0 ? "" : ProductLocator.Locate(path).OrderFolder;
            }
            catch (Exception ex)
            {
                Log.Error("Закрытие заказа: папка заказа", ex);
                return "";
            }
        }

        /// <summary>Корень заказов: `_tmp` и `_Сдано` живут рядом с папками заказов, а не внутри заказа.</summary>
        private static string OrdersRoot(string order)
        {
            string parent = Path.GetDirectoryName(order) ?? order;
            return parent;
        }

        /// <summary>Открытый документ заказа держит файл — перенос его порвёт (Т-3).</summary>
        private static string OpenInside(ISldWorks app, string order)
        {
            try
            {
                List<string> open = new List<string>();
                ModelDoc2 doc = app != null ? app.GetFirstDocument() as ModelDoc2 : null;
                while (doc != null)
                {
                    string path = doc.GetPathName() ?? "";
                    // С разделителем: заказ «85_Т» не должен считать своими документы заказа «85_Т2».
                    if (path.Length > 0 && path.StartsWith(order.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        open.Add(Path.GetFileName(path));
                    doc = doc.GetNext() as ModelDoc2;
                }
                return string.Join(", ", open.ToArray());
            }
            catch (Exception ex)
            {
                Log.Error("Закрытие заказа: открытые документы", ex);
                return "";
            }
        }

        /// <summary>Открытые документы других папок с именами файлов заказа — через строку (№16); пусто — нет.</summary>
        private static string OpenNamesakes(ISldWorks app, string order)
        {
            try
            {
                List<string> open = new List<string>();
                HashSet<string> windowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ModelDoc2 doc = app != null ? app.GetFirstDocument() as ModelDoc2 : null;
                while (doc != null)
                {
                    string path = doc.GetPathName() ?? "";
                    open.Add(path);
                    // Детали открытых сборок другого заказа загружены без своего окна: закрыть их нечем — закрывают сборку
                    // или чертёж (ревью 23.09.2026).
                    if (doc.Visible) windowed.Add(path);
                    doc = doc.GetNext() as ModelDoc2;
                }
                if (open.Count == 0) return "";
                List<string> found = OrderArchive.Namesakes(open,
                    Directory.GetFiles(order, "*.sld*", SearchOption.AllDirectories), order);
                return OrderArchive.NamesakeText(found.Where(p => windowed.Contains(p)).ToList(),
                    found.Where(p => !windowed.Contains(p)).ToList(), 20);
            }
            catch (Exception ex)
            {
                // Не узнали — значит нельзя: пустая строка пропустила бы чужие файлы в архив.
                Log.Error("Закрытие заказа: одноимённые документы", ex);
                return "SolidWorks не ответил, какие документы открыты (" + ex.Message.Trim() + ") — повторите";
            }
        }

        // ------------------------------------------------------------------ комплекты (Т-45)
        private static void Kits(ISldWorks app, string order, string temp, List<string> problems)
        {
            string section = Path.Combine(order, ProductLocator.SectionFolder);
            if (!Directory.Exists(section)) return;
            foreach (string folder in Directory.GetDirectories(section).OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase))
            {
                string models = Path.Combine(folder, LzkNaming.ModelsFolder);
                if (!Directory.Exists(models)) continue;
                string assembly = Directory.GetFiles(models, "*.sldasm", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
                    .OrderBy(f => f.Length).FirstOrDefault(ProductLocator.IsMainAssembly)
                    ?? Directory.GetFiles(models, "*.sldasm").Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
                        .OrderBy(f => f.Length).FirstOrDefault();
                if (assembly == null) continue;
                string cipher = LzkNaming.Cipher(folder, assembly);
                string product = folder;
                string kit = Path.Combine(temp, KitsFolder, LzkNaming.SafeFileName(cipher));
                // Повторный запуск не пересобирает готовый комплект: Pack and Go — самая долгая часть (Т-46).
                if (Directory.Exists(kit) && Directory.GetFiles(kit).Any(f =>
                        f.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase))) continue;
                Directory.CreateDirectory(kit);
                string problem = Pack(app, assembly, kit, product);
                if (problem.Length > 0) problems.Add(cipher + ": " + problem);
            }
        }

        /// <summary>Pack and Go главной сборки с чертежами в одну папку (Т-45); product — папка изделия.</summary>
        private static string Pack(ISldWorks app, string assembly, string kit, string product)
        {
            ModelDoc2 doc = null;
            bool opened = false;
            try
            {
                doc = app.GetOpenDocumentByName(assembly) as ModelDoc2;
                bool fresh = false;
                if (doc == null)
                {
                    int errors = 0, warnings = 0;
                    doc = app.OpenDoc6(assembly, (int)swDocumentTypes_e.swDocASSEMBLY,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as ModelDoc2;
                    if (doc == null) return "сборка не открылась: " + SwCodes.OpenProblem(errors);
                    fresh = true;
                    if (warnings != 0) Log.Info("Закрытие заказа: " + assembly + " открыта с предупреждениями (код " + warnings + ")");
                }
                // SolidWorks мог подставить уже открытую одноимённую сборку другой папки. Чужой документ не закрываем: в
                // нём могут быть несохранённые правки конструктора (сверка SW API 23.09.2026, №16).
                string actual = doc.GetPathName() ?? "";
                if (!string.Equals(actual, assembly, StringComparison.OrdinalIgnoreCase))
                {
                    doc = null;
                    return "SolidWorks подставил одноимённую сборку «" + actual + "»";
                }
                opened = fresh;

                PackAndGo pack = doc.Extension.GetPackAndGo() as PackAndGo;
                if (pack == null) return "Pack and Go недоступен";
                pack.IncludeDrawings = true;
                pack.IncludeSimulationResults = false;
                pack.FlattenToSingleFolder = true;
                // Состав Pack and Go пересчитывается именно этим вызовом: без него SolidWorks сохраняет
                // список, собранный до IncludeDrawings, и комплект уезжает в архив без единого чертежа
                // (боевой прогон 18.09.2026 — 20 моделей и 0 чертежей).
                object names = null;
                pack.GetDocumentNames(out names);
                // Компонент мог прийти из другой папки при своём одноимённом файле (открытый документ другого заказа
                // подменяет свой) или не найтись вовсе: такой комплект в архив не идёт (№16).
                List<string> swapped = OrderArchive.Substituted((names as string[]) ?? new string[0],
                    Directory.GetFiles(product, "*.sld*", SearchOption.AllDirectories), File.Exists);
                if (swapped.Count > 0) return string.Join("; ", swapped.Take(10).ToArray());
                pack.SetSaveToName(true, kit);
                string[] wanted = Drawings(pack, names as string[]);
                // В журнал — из чего собирается комплект: по этой строке видно, нашёл ли SolidWorks чертежи.
                Log.Info("Комплект «" + Path.GetFileName(kit) + "»: Pack and Go насчитал " +
                    (wanted == null ? "?" : wanted.Length.ToString(CultureInfo.InvariantCulture)) + " документов, из них чертежей " +
                    (wanted == null ? "?" : wanted.Count(n => (n ?? "").EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase))
                        .ToString(CultureInfo.InvariantCulture)));
                doc.Extension.SavePackAndGo(pack);
                string[] written = Directory.GetFiles(kit);
                if (written.Length == 0) return "Pack and Go не записал ни одного файла";
                if (!written.Any(f => f.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)))
                    return "в комплекте нет сборки";
                if (wanted != null && written.Length < wanted.Length)
                    return "в комплекте " + written.Length + " файлов из " + wanted.Length;
                return "";
            }
            catch (Exception ex)
            {
                Log.Error("Закрытие заказа: Pack and Go " + assembly, ex);
                return "Pack and Go не выполнен: " + ex.Message;
            }
            finally
            {
                if (opened && doc != null)
                {
                    try { app.CloseDoc(doc.GetPathName()); }
                    catch (Exception ex) { Log.Error("Закрытие заказа: закрытие " + assembly, ex); }
                }
            }
        }

        /// <summary>
        /// Чертежи комплекта. `IncludeDrawings` полагается на поисковые пути SolidWorks, а на боевом
        /// заказе они не настроены: Pack and Go насчитал 20 моделей и ни одного чертежа. Поэтому чертежи
        /// ищем сами — все `.slddrw` в папках моделей комплекта — и добавляем в комплект явно.
        /// </summary>
        private static string[] Drawings(PackAndGo pack, string[] models)
        {
            if (models == null) return null;
            List<string> all = new List<string>(models);
            HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> known = new HashSet<string>(models, StringComparer.OrdinalIgnoreCase);
            foreach (string model in models)
            {
                string folder = Path.GetDirectoryName(model ?? "") ?? "";
                if (folder.Length > 0 && Directory.Exists(folder)) folders.Add(folder);
            }
            List<string> drawings = new List<string>();
            foreach (string folder in folders)
                foreach (string drawing in Directory.GetFiles(folder, "*.slddrw"))
                {
                    if (Path.GetFileName(drawing).StartsWith("~$", StringComparison.Ordinal)) continue;
                    if (known.Add(drawing)) drawings.Add(drawing);
                }
            if (drawings.Count == 0) return all.ToArray();
            try
            {
                pack.AddExternalDocuments(drawings.ToArray());
                all.AddRange(drawings);
            }
            catch (Exception ex)
            {
                Log.Error("Закрытие заказа: чертежи в комплект", ex);
            }
            return all.ToArray();
        }

        // ------------------------------------------------------------------ копия, сверка, перенос
        private static int CopyTree(string source, string target, List<string> problems)
        {
            int count = 0;
            foreach (string file in Directory.GetFiles(source, "*.*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal)) continue;
                string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar);
                string path = Path.Combine(target, relative);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");
                    File.Copy(file, path, true);
                    count++;
                }
                catch (IOException ex)
                {
                    problems.Add(relative + ": " + ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    problems.Add(relative + ": " + ex.Message);
                }
            }
            return count;
        }

        /// <summary>Сверка копии по контрольным суммам: без неё «перенесли» значит «надеемся» (Т-46).</summary>
        private static void Verify(string source, string target, List<string> problems)
        {
            foreach (string file in Directory.GetFiles(source, "*.*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal)) continue;
                string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar);
                string copy = Path.Combine(target, relative);
                if (!File.Exists(copy)) { problems.Add("нет копии: " + relative); continue; }
                if (Checksum(file) != Checksum(copy)) problems.Add("копия не совпала: " + relative);
            }
        }

        /// <summary>Перенос папки: на одном диске — переименованием, иначе копия со сверкой и удалением.</summary>
        private static bool Move(string source, string target, List<string> problems)
        {
            problems.Clear();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target.TrimEnd(Path.DirectorySeparatorChar)) ?? "");
                if (!Directory.Exists(target) &&
                    string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(source, target);
                    return true;
                }
                CopyTree(source, target, problems);
                if (problems.Count > 0) return false;
                Verify(source, target, problems);
                if (problems.Count > 0) return false;
                Remove(source);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Закрытие заказа: перенос " + source + " → " + target, ex);
                problems.Add(ex.Message);
                return false;
            }
        }

        private static void Remove(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (IOException ex)
            {
                Log.Error("Закрытие заказа: уборка " + folder, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Закрытие заказа: уборка " + folder, ex);
            }
        }

        /// <summary>`_Архив.txt` кладётся в папку заказа до переноса — так он уезжает в `_Сдано` вместе с ней (Т-47).</summary>
        private static void Note(string order, string archive, int files)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Заказ закрыт и сдан в архив");
            sb.AppendLine("Заказ:  " + Path.GetFileName(order));
            sb.AppendLine("Архив:  " + archive);
            sb.AppendLine("Файлов: " + files);
            sb.AppendLine("Когда:  " + DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")));
            sb.AppendLine("Кто:    " + Settings.AuthorOrUser());
            sb.AppendLine();
            sb.AppendLine("Рабочие файлы заказа лежат в архиве; эта папка оставлена для истории.");
            try
            {
                File.WriteAllText(Path.Combine(order, ArchiveNote), sb.ToString(), new UTF8Encoding(true));
            }
            catch (IOException ex)
            {
                Log.Error("Закрытие заказа: записка " + order, ex);
            }
        }

        private static void Fail(ISldWorks app, bool interactive, string text)
        {
            LastOutcome = "error|" + text;
            Status(app, "");
            if (interactive) MessageBox.Show(text, "ЕСКД: закрыть заказ", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Status(ISldWorks app, string text)
        {
            try
            {
                Frame frame = app != null ? app.Frame() as Frame : null;
                if (frame != null && text != null) frame.SetStatusBarText(text);
            }
            catch (Exception ex)
            {
                Log.Error("Закрытие заказа: строка состояния", ex);
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
                Log.Error("Закрытие заказа: контрольная сумма " + path, ex);
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}
