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
    /// Кнопка К-5 «Выдать в производство» (ТЗ-02 Т-38…Т-43). Собирает сводную заявку по ведомостям изделий
    /// заказа, копирует документы в `04_ПРОИЗВОДСТВО` и пишет отчёт `_Выдано_&lt;дата&gt;_&lt;время&gt;.txt`.
    /// В моделях не пишет ничего (Р0-9) и при своде их не открывает (Р-6): всё нужное уже в ведомостях.
    /// Черновик сводной (режим конструктора) собирает книгу в папке заказа и на этом останавливается.
    /// </summary>
    public static class IssueService
    {
        /// <summary>«ok|сводная|изделий|файлов|отчёт» или «error|текст» — для автотестов (Т-9).</summary>
        public static string LastOutcome = "";

        /// <summary>
        /// Папка производства рядом с корнем заказов. На NAS она называется «_Производство»
        /// (ТЗ-02: `04_ПРОИЗВОДСТВО` = `…\_Производство`), поэтому ищем оба имени и никогда
        /// не заводим второе: цех смотрит в одну папку, а не в две похожие.
        /// </summary>
        public static readonly string[] ProductionFolders = { "_Производство", "04_ПРОИЗВОДСТВО" };
        public const string ProductionFolder = "04_ПРОИЗВОДСТВО";
        public const string SectionFolder = ProductLocator.SectionFolder;
        /// <summary>Папки выдачи по образцу 85_Т (Т-41).</summary>
        public const string PdfFolder = "01 КД в PDF";
        public const string CncFolder = "02 ЧПУ и Раскрой";
        public const string RequestFolder = "03 Заявки в цеха";

        private sealed class Product
        {
            public string Folder = "";
            public string Cipher = "";
            public string Name = "";
            public string Assembly = "";
            public string Workbook = "";
            public int Quantity = 1;
        }

        public static bool Run(ISldWorks app, bool interactive, string orderFolder, string number,
            string quantities, string defaultColor, bool draft)
        {
            LastOutcome = "";
            try
            {
                if (interactive)
                {
                    using (IssueForm form = new IssueForm(Order(app, orderFolder)))
                    {
                        if (form.ShowDialog() != DialogResult.OK)
                        {
                            LastOutcome = "error|отменено";
                            return false;
                        }
                        orderFolder = form.Order;
                        number = form.Number;
                        quantities = form.Quantities;
                        defaultColor = form.Color;
                        draft = form.Draft;
                    }
                }
                string order = Order(app, orderFolder);
                if (order.Length == 0)
                {
                    Fail(app, interactive, "Не найдена папка заказа: откройте изделие заказа или укажите папку.");
                    return false;
                }
                List<Product> products = Products(order, quantities);
                if (products.Count == 0)
                {
                    Fail(app, interactive, "В заказе «" + Path.GetFileName(order) + "» нет изделий с главной сборкой в «" +
                        SectionFolder + "».");
                    return false;
                }
                string requestNumber = string.IsNullOrWhiteSpace(number) ? Number(order) : number.Trim();

                Status(app, "ЕСКД: выдача — нормативы…");
                string problem;
                Norms norms = Norms.Read(Norms.PathIn(NormsFolder(order)), out problem);
                if (norms == null)
                {
                    Fail(app, interactive, problem);
                    return false;
                }

                Status(app, "ЕСКД: выдача — проверка изделий…");
                List<string> stoppers = new List<string>();
                List<RequestProduct> read = new List<RequestProduct>();
                foreach (Product product in products)
                {
                    if (product.Workbook.Length == 0)
                    {
                        stoppers.Add(product.Cipher + ": нет ведомости изделия — сделайте «Ведомость ЛЗК»");
                        continue;
                    }
                    RequestProduct data = ProdRequest.Read(product.Workbook, product.Cipher, product.Name, product.Quantity, out problem);
                    if (data == null)
                    {
                        stoppers.Add(product.Cipher + ": " + problem);
                        continue;
                    }
                    read.Add(data);
                    // Черновик собирают до готовности изделия — иначе конструктор не увидит, что получится.
                    if (draft) continue;
                    if (data.Marks.Count > 0)
                    {
                        stoppers.Add(product.Cipher + ": ведомость с пометками «" + LzkWorkbook.Mark + "» (" +
                            data.Marks.Count + "), первая — " + data.Marks[0]);
                        continue;
                    }
                    string check = Check(app, product);
                    if (check.Length > 0) stoppers.Add(product.Cipher + ": " + check);
                }
                if (stoppers.Count > 0)
                {
                    Fail(app, interactive, "Выдача остановлена, ничего не скопировано:" + Environment.NewLine +
                        Environment.NewLine + string.Join(Environment.NewLine, stoppers.ToArray()));
                    return false;
                }

                Status(app, "ЕСКД: выдача — сводная заявка…");
                Dictionary<string, string> colors = Colors(order, requestNumber, read, defaultColor);
                ProdRequestData data2 = ProdRequest.Build(read, norms, colors);
                data2.Number = requestNumber;
                data2.Order = Path.GetFileName(order);
                data2.Chief = Settings.Read().Author ?? Environment.UserName;
                int revision = Revision(order, requestNumber);
                string requestPath = ProdRequestBook.Path(order, requestNumber, revision);
                Archive(order, requestNumber, revision);
                ProdRequestBook.Write(requestPath, data2);

                if (draft)
                {
                    LastOutcome = string.Join("|", new[] { "ok", requestPath, read.Count.ToString(CultureInfo.InvariantCulture), "0", "черновик" });
                    if (interactive)
                        MessageBox.Show("Черновик сводной заявки готов:" + Environment.NewLine + requestPath +
                            Environment.NewLine + Environment.NewLine + "В производство ничего не скопировано.",
                            "ЕСКД: выдача в производство", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Status(app, "");
                    return true;
                }

                Status(app, "ЕСКД: выдача — копирование в производство…");
                List<string> copied = new List<string>();
                string production = Copy(order, products, requestPath, copied);
                string report = Report(order, products, requestPath, production, copied, data2);
                LastOutcome = string.Join("|", new[]
                {
                    "ok", requestPath, read.Count.ToString(CultureInfo.InvariantCulture),
                    copied.Count.ToString(CultureInfo.InvariantCulture), report
                });
                if (interactive)
                    MessageBox.Show("Выдано в производство." + Environment.NewLine + Environment.NewLine +
                        "Сводная заявка: " + requestPath + Environment.NewLine +
                        "Скопировано файлов: " + copied.Count + Environment.NewLine +
                        "Папка производства: " + production + Environment.NewLine +
                        "Отчёт: " + report, "ЕСКД: выдача в производство", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Status(app, "");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Выдача в производство", ex);
                Fail(app, interactive, "Выдача не выполнена: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ заказ и изделия
        private static string Order(ISldWorks app, string orderFolder)
        {
            if (!string.IsNullOrWhiteSpace(orderFolder) && Directory.Exists(orderFolder)) return Path.GetFullPath(orderFolder.Trim());
            try
            {
                ModelDoc2 doc = app != null ? app.ActiveDoc as ModelDoc2 : null;
                string path = doc == null ? "" : (doc.GetPathName() ?? "");
                if (path.Length == 0) return "";
                return ProductLocator.Locate(path).OrderFolder;
            }
            catch (COMException ex)
            {
                Log.Error("Выдача: папка заказа", ex);
                return "";
            }
        }

        /// <summary>Изделия заказа: папки «02_Металл\И…» с главной сборкой и ведомостью.</summary>
        private static List<Product> Products(string order, string quantities)
        {
            Dictionary<string, int> wanted = Quantities(quantities);
            List<Product> products = new List<Product>();
            string section = Path.Combine(order, SectionFolder);
            if (!Directory.Exists(section)) return products;
            foreach (string folder in Directory.GetDirectories(section).OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase))
            {
                string models = Path.Combine(folder, LzkNaming.ModelsFolder);
                if (!Directory.Exists(models)) continue;
                string assembly = Directory.GetFiles(models, "*.sldasm", SearchOption.AllDirectories)
                    .OrderBy(f => f.Length)
                    .FirstOrDefault(ProductLocator.IsMainAssembly)
                    ?? Directory.GetFiles(models, "*.sldasm").OrderBy(f => f.Length).FirstOrDefault();
                if (assembly == null) continue;
                string name = Path.GetFileName(folder);
                int quantity;
                if (wanted.Count > 0 && !wanted.TryGetValue(name, out quantity)) continue;
                if (!wanted.TryGetValue(name, out quantity)) quantity = 1;
                products.Add(new Product
                {
                    Folder = folder,
                    Cipher = LzkNaming.Cipher(folder, assembly),
                    Name = name,
                    Assembly = assembly,
                    Quantity = Math.Max(1, quantity),
                    Workbook = Directory.GetFiles(folder, LzkNaming.WorkbookPrefix + "*.xlsx")
                        .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal)) ?? ""
                });
            }
            return products;
        }

        /// <summary>«И01_ТС-52=2;И02_Х=1» — сколько каких изделий в тираже.</summary>
        private static Dictionary<string, int> Quantities(string text)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (text ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=');
                int quantity;
                if (pair.Length != 2 || !int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity))
                    continue;
                result[pair[0].Trim()] = Math.Max(1, quantity);
            }
            return result;
        }

        /// <summary>Номер заявки по умолчанию — из имени папки заказа (Т-38).</summary>
        private static string Number(string order)
        {
            string name = Path.GetFileName(order) ?? "";
            string[] parts = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : name;
        }

        /// <summary>Справочник нормативов лежит рядом с корнем заказов (Т-13).</summary>
        private static string NormsFolder(string order)
        {
            for (string current = order; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (File.Exists(Norms.PathIn(current))) return current;
            return Path.GetDirectoryName(order) ?? order;
        }

        /// <summary>Проверка изделия заново (Т-39): кнопка не доверяет прежнему отчёту.</summary>
        private static string Check(ISldWorks app, Product product)
        {
            ModelDoc2 assembly = null;
            bool opened = false;
            try
            {
                assembly = app.GetOpenDocumentByName(product.Assembly) as ModelDoc2;
                if (assembly == null)
                {
                    int errors = 0, warnings = 0;
                    assembly = app.OpenDoc6(product.Assembly, (int)swDocumentTypes_e.swDocASSEMBLY,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as ModelDoc2;
                    opened = assembly != null;
                }
                if (assembly == null) return "сборка изделия не открылась";
                int activateErrors = 0;
                app.ActivateDoc3(product.Assembly, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
                CheckService.Run(app, false);
                string[] outcome = (CheckService.LastOutcome ?? "").Split('|');
                if (outcome.Length < 2 || outcome[0] != "ok") return "проверка не выполнена (" + CheckService.LastOutcome + ")";
                if (outcome[1] != CheckRules.OutcomeName(CheckLevel.Ok))
                    return "проверка изделия — «" + outcome[1] + "», в производство идёт только «" +
                        CheckRules.OutcomeName(CheckLevel.Ok) + "» (отчёт " + (outcome.Length > 4 ? outcome[4] : "") + ")";
                return "";
            }
            catch (Exception ex)
            {
                Log.Error("Выдача: проверка " + product.Assembly, ex);
                return "проверка не выполнена: " + ex.Message;
            }
            finally
            {
                if (opened && assembly != null) app.CloseDoc(assembly.GetPathName());
            }
        }

        /// <summary>Цвета покраски: прежние — из последней сводной, остальным ставится цвет по умолчанию.</summary>
        private static Dictionary<string, string> Colors(string order, string number, IEnumerable<RequestProduct> products, string defaultColor)
        {
            Dictionary<string, string> colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string previous in Directory.GetFiles(order, "*_Сводная_заявка*.xlsx").OrderByDescending(f => f))
            {
                ProdRequestBook.ReadPrevious(previous, colors, null);
                break;
            }
            string fallback = (defaultColor ?? "").Trim();
            if (fallback.Length == 0) return colors;
            foreach (RequestProduct product in products)
                foreach (RequestLine line in product.Painted)
                {
                    string key = line.Designation.Length > 0 ? line.Designation : line.Name;
                    if (!colors.ContainsKey(key) || colors[key].Length == 0) colors[key] = fallback;
                }
            return colors;
        }

        /// <summary>Номер ревизии сводной: сколько раз заказ уже выдавали (Т-41).</summary>
        private static int Revision(string order, string number)
        {
            return Directory.GetFiles(order, "_Выдано_*.txt").Length;
        }

        /// <summary>Прежняя сводная уходит в `_Аннулировано` (Т-41).</summary>
        private static void Archive(string order, string number, int revision)
        {
            for (int previous = 0; previous < revision; previous++)
            {
                string path = ProdRequestBook.Path(order, number, previous);
                if (!File.Exists(path)) continue;
                try
                {
                    string target = ExportNaming.ArchivePath(path, DateTime.Now);
                    Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
                    File.Move(path, target);
                }
                catch (IOException ex)
                {
                    Log.Error("Выдача: перенос прежней сводной " + path, ex);
                }
            }
        }

        // ------------------------------------------------------------------ копирование (Т-41)
        private static string Copy(string order, IEnumerable<Product> products, string requestPath, List<string> copied)
        {
            string production = Path.Combine(ProductionRoot(order), Path.GetFileName(order));
            Directory.CreateDirectory(production);
            foreach (Product product in products)
            {
                CopyTree(ExportNaming.PdfDirectory(product.Folder), Path.Combine(production, PdfFolder, product.Cipher), copied);
                CopyTree(ExportNaming.LaserDirectory(product.Folder),
                    Path.Combine(production, CncFolder, ExportNaming.LaserFolder), copied);
                CopyTree(ExportNaming.TubeDirectory(product.Folder),
                    Path.Combine(production, CncFolder, ExportNaming.TubeFolder), copied);
                if (product.Workbook.Length > 0)
                    CopyFile(product.Workbook, Path.Combine(production, RequestFolder, Path.GetFileName(product.Workbook)), copied);
            }
            CopyFile(requestPath, Path.Combine(production, RequestFolder, Path.GetFileName(requestPath)), copied);
            return production;
        }

        /// <summary>Папка производства рядом с корнем заказов: цех и КТО смотрят в одно место.</summary>
        public static string ProductionRoot(string order)
        {
            for (string current = order; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                foreach (string name in ProductionFolders)
                    if (Directory.Exists(Path.Combine(current, name))) return Path.Combine(current, name);
                // Папки производства ещё нет: заводим её там, где лежит корень заказов, — рядом с «_Заявки».
                foreach (string root in ProductLocator.OrderRoots)
                    if (Directory.Exists(Path.Combine(current, root)))
                    {
                        string candidate = Path.Combine(current, ProductionFolder);
                        Directory.CreateDirectory(candidate);
                        return candidate;
                    }
            }
            string near = Path.Combine(Path.GetDirectoryName(order) ?? order, ProductionFolder);
            Directory.CreateDirectory(near);
            return near;
        }

        private static void CopyTree(string source, string target, List<string> copied)
        {
            if (!Directory.Exists(source)) return;
            foreach (string file in Directory.GetFiles(source))
            {
                if (Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal)) continue;
                CopyFile(file, Path.Combine(target, Path.GetFileName(file)), copied);
            }
        }

        private static void CopyFile(string source, string target, List<string> copied)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
                // Прежний файл того же имени уходит в `_Аннулировано` папки производства: у цеха
                // не должно остаться двух версий одного документа под одним именем.
                if (File.Exists(target) && Checksum(target) != Checksum(source))
                {
                    string archive = ExportNaming.ArchivePath(target, DateTime.Now);
                    Directory.CreateDirectory(Path.GetDirectoryName(archive) ?? "");
                    File.Move(target, archive);
                }
                File.Copy(source, target, true);
                copied.Add(target);
            }
            catch (IOException ex)
            {
                Log.Error("Выдача: копирование " + source, ex);
            }
        }

        // ------------------------------------------------------------------ отчёт (Т-42)
        private static string Report(string order, IEnumerable<Product> products, string requestPath,
            string production, IEnumerable<string> copied, ProdRequestData data)
        {
            DateTime now = DateTime.Now;
            string path = Path.Combine(order, ExportNaming.IssuedPrefix +
                now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) + ".txt");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Выдано в производство");
            sb.AppendLine("Заказ:    " + Path.GetFileName(order));
            sb.AppendLine("Заявка:   " + data.Number + ", изделий: " + data.Products.Count + ", всего единиц: " + data.TotalProducts);
            sb.AppendLine("Выдал:    " + data.Chief + ", " + now.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")));
            sb.AppendLine("Сводная:  " + Path.GetFileName(requestPath));
            sb.AppendLine("Куда:     " + production);
            sb.AppendLine();
            sb.AppendLine("Изделия:");
            foreach (Product product in products)
                sb.AppendLine("  " + product.Cipher + " × " + product.Quantity + "  (" + product.Name + ")");
            if (data.Issues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Замечания свода:");
                foreach (string issue in data.Issues) sb.AppendLine("  " + issue);
            }
            sb.AppendLine();
            sb.AppendLine("Выдано (SHA-256):");
            foreach (string file in copied) sb.AppendLine("  " + Checksum(file) + "  " + Path.GetFileName(file));
            // Модели изделий перечисляются отдельно: по этому списку кнопки «Выгрузить» и «Новая ревизия»
            // понимают, что документ уже у цеха и его нельзя молча переписать (Т-30).
            sb.AppendLine();
            sb.AppendLine("Документы изделий:");
            foreach (Product product in products)
                foreach (string model in Directory.Exists(Path.Combine(product.Folder, LzkNaming.ModelsFolder))
                    ? Directory.GetFiles(Path.Combine(product.Folder, LzkNaming.ModelsFolder), "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ||
                                    // Чертежи тоже в перечне: ревизию поднимают именно на чертеже (Т-48),
                                    // и без этой строки он выглядел бы черновиком после выдачи.
                                    f.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase))
                    : new string[0])
                sb.AppendLine("  " + Checksum(model) + "  " + Path.GetFileName(model));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static void Fail(ISldWorks app, bool interactive, string text)
        {
            LastOutcome = "error|" + text;
            Status(app, "");
            if (interactive) MessageBox.Show(text, "ЕСКД: выдача в производство", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                Log.Error("Выдача: строка состояния", ex);
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
                Log.Error("Выдача: контрольная сумма " + path, ex);
                return "";
            }
        }
    }
}
