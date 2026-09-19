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
    /// Кнопка К-5 «Готово к производству» (ТЗ-04 Р4-8): одно изделие, без окна заказа. На открытой главной сборке:
    /// проверка изделия (К-3 = ГОТОВО) → PDF листов участков и «Расхода» книги ЛЗК в 02_PDF → отметка в «Паспорте» →
    /// отчёт _Выдано_&lt;дата&gt;_&lt;время&gt;.txt в папке изделия → сообщение с путём для начальника производства.
    /// Сводной заявки на заказ и копирования в «Производство» больше нет: всё лежит в папке изделия.
    /// </summary>
    public static class ReadyService
    {
        /// <summary>«ok|отчёт|pdf|папка изделия» или «error|текст» — для автотестов.</summary>
        public static string LastOutcome = "";

        public const string Title = "ЕСКД: готово к производству";

        public static bool Run(ISldWorks app, bool interactive)
        {
            LastOutcome = "";
            try
            {
                ModelDoc2 doc = app.ActiveDoc as ModelDoc2;
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                    return Fail(app, interactive, "Откройте главную сборку изделия и нажмите кнопку ещё раз.");
                string assembly = DocInfo.PathOf(doc);
                if (assembly.Length == 0) return Fail(app, interactive, "Сборка ещё не сохранена в файл.");
                string product = LzkNaming.ProductFolder(assembly);
                string cipher = LzkNaming.Cipher(product, assembly);
                string workbook = LzkNaming.FindWorkbook(product, cipher);
                if (workbook.Length == 0)
                    return Fail(app, interactive, "У изделия нет книги ЛЗК. Нажмите «Ведомость ЛЗК», затем эту кнопку.");
                if (LzkNaming.IsLegacy(workbook))
                    return Fail(app, interactive, "Ведомость изделия старого образца (без листов участков): " + Path.GetFileName(workbook) +
                        ". Нажмите «Ведомость ЛЗК» — книга соберётся заново, затем эту кнопку.");
                if (Locked(workbook))
                    return Fail(app, interactive, "Книга " + Path.GetFileName(workbook) + " открыта в Excel. Закройте её и повторите.");
                if (!ExcelPdf.Available)
                    return Fail(app, interactive, "На компьютере нет Microsoft Excel — PDF листов участков сделать нечем.");

                Status(app, "ЕСКД: готово к производству — проверка изделия…");
                string check = Check(app);
                if (check.Length > 0)
                {
                    CheckReport found = CheckService.LastReport;
                    if (!interactive || found == null || found.Findings.Count == 0)
                        return Fail(app, interactive, "Изделие не готово: " + check);
                    // Не готово — показать, что именно не так, тем же окном, что и проверка: ничего не записано.
                    Fail(app, false, "Изделие не готово: " + check);
                    NoticeForm.Present(app, Title, "Изделие не готово к производству",
                        "Проверка изделия — «" + CheckRules.OutcomeName(found.Outcome) + "», в цех идёт только «" +
                        CheckRules.OutcomeName(CheckLevel.Ok) + "». Исправьте замечания и нажмите кнопку ещё раз." +
                        Environment.NewLine + "Ничего не записано, PDF не сделаны.",
                        Notices.FromCheck(found), Notices.FromCheck(found.Outcome));
                    return false;
                }

                Status(app, "ЕСКД: готово к производству — PDF листов участков…");
                List<KeyValuePair<string, string>> sheets = Sheets(workbook, product, cipher);
                string pdfDir = ExportNaming.PdfDirectory(product);
                DateTime now = DateTime.Now;
                foreach (KeyValuePair<string, string> pair in sheets)
                    if (File.Exists(pair.Value) && Locked(pair.Value))
                        return Fail(app, interactive, "Файл " + Path.GetFileName(pair.Value) + " открыт. Закройте его и повторите.");
                // PDF сначала во временную папку: не сработал Excel — прежние PDF цеха остаются на месте.
                string staging = Path.Combine(Path.GetTempPath(), "ESKD_pdf_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    List<KeyValuePair<string, string>> staged = sheets
                        .Select(s => new KeyValuePair<string, string>(s.Key, Path.Combine(staging, Path.GetFileName(s.Value)))).ToList();
                    string problem;
                    if (!ExcelPdf.Export(workbook, staged, out problem)) return Fail(app, interactive, problem);
                    Directory.CreateDirectory(pdfDir);
                    for (int i = 0; i < sheets.Count; i++)
                    {
                        string target = sheets[i].Value;
                        if (File.Exists(target))
                        {
                            string archive = ExportNaming.ArchivePath(target, now);
                            Directory.CreateDirectory(Path.GetDirectoryName(archive) ?? "");
                            File.Move(target, archive);
                        }
                        File.Copy(staged[i].Value, target, false);
                    }
                }
                finally
                {
                    try
                    {
                        Directory.Delete(staging, true);
                    }
                    catch (IOException ex)
                    {
                        Log.Error("Готово к производству: временная папка " + staging, ex);
                    }
                }

                string who = Settings.Read().Author;
                if (string.IsNullOrWhiteSpace(who)) who = Environment.UserName;
                MarkIssued(workbook, now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + ", " + who);
                string report = Report(product, cipher, assembly, workbook, sheets.Select(p => p.Value).ToList(), who, now);
                LastOutcome = string.Join("|", new[] { "ok", report, sheets.Count.ToString(CultureInfo.InvariantCulture), product });
                Log.Info("Готово к производству: " + LastOutcome);
                Status(app, "ЕСКД: изделие готово к производству");
                if (interactive)
                {
                    try
                    {
                        Clipboard.SetText(product);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Готово к производству: буфер обмена", ex);
                    }
                    MessageBox.Show("Изделие " + cipher + " готово к производству." + Environment.NewLine + Environment.NewLine +
                        "Папка изделия (путь скопирован — перешлите его начальнику производства):" + Environment.NewLine + product +
                        Environment.NewLine + Environment.NewLine + "Листы участков и расход — PDF в " + pdfDir + Environment.NewLine +
                        "Книга ЛЗК с калькулятором — " + workbook + Environment.NewLine + "Отчёт — " + report,
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Готово к производству", ex);
                return Fail(app, interactive, "Не выполнено: " + ex.Message);
            }
        }

        /// <summary>Листы, которые идут в цех: участки, где есть единицы, и «Расход».</summary>
        private static List<KeyValuePair<string, string>> Sheets(string workbook, string product, string cipher)
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            using (XlsxBook book = XlsxBook.Open(workbook))
            {
                foreach (string section in LzkBlanks.All.Concat(new[] { LzkBook.CostSheet }))
                {
                    XlsxSheet sheet = book.Sheet(section);
                    if (sheet == null) continue;
                    if (section != LzkBook.CostSheet && sheet.Get("A6").StartsWith("Единиц для этого участка", StringComparison.Ordinal)) continue;
                    result.Add(new KeyValuePair<string, string>(section, PdfPath(product, cipher, section)));
                }
            }
            return result;
        }

        /// <summary>&lt;изделие&gt;\02_PDF\ЛЗК_&lt;шифр&gt;_&lt;лист&gt;.pdf</summary>
        public static string PdfPath(string product, string cipher, string sheet)
        {
            return Path.Combine(ExportNaming.PdfDirectory(product),
                LzkNaming.WorkbookPrefix + LzkNaming.SafeFileName(cipher) + "_" + LzkNaming.SafeFileName(sheet) + ".pdf");
        }

        /// <summary>Проверка изделия заново: кнопка не доверяет прежнему отчёту _Проверка.txt.</summary>
        private static string Check(ISldWorks app)
        {
            CheckService.Run(app, false);
            string[] outcome = (CheckService.LastOutcome ?? "").Split('|');
            if (outcome.Length < 2 || outcome[0] != "ok") return "проверка не выполнена (" + CheckService.LastOutcome + ")";
            if (outcome[1] != CheckRules.OutcomeName(CheckLevel.Ok))
                return "проверка изделия — «" + outcome[1] + "», в производство идёт только «" +
                    CheckRules.OutcomeName(CheckLevel.Ok) + "». Отчёт: " + (outcome.Length > 4 ? outcome[4] : CheckRules.ReportName);
            return "";
        }

        /// <summary>Дата и кто отметил — в ячейку «Выдано» паспорта: видно в самой книге.</summary>
        private static void MarkIssued(string workbook, string text)
        {
            try
            {
                using (XlsxBook book = XlsxBook.Open(workbook))
                {
                    string sheet, cell;
                    if (!book.TryResolveName(LzkBook.NameIssued, out sheet, out cell)) return;
                    XlsxSheet s = book.Sheet(sheet);
                    if (s == null) return;
                    s.SetText(cell, text, s.StyleOf(cell));
                    book.Save();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Готово к производству: отметка в паспорте", ex);
            }
        }

        /// <summary>
        /// Отчёт в папке изделия. Имена файлов со строками «сумма  имя» читает <see cref="ExportNaming.Issued"/>:
        /// по ним «Выгрузить» и «Новая ревизия» узнают, что документ уже у цеха.
        /// </summary>
        private static string Report(string product, string cipher, string assembly, string workbook, IList<string> pdfs,
            string who, DateTime now)
        {
            string path = Path.Combine(product, ExportNaming.IssuedPrefix + now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) + ".txt");
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(product, ExportNaming.IssuedPrefix + now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) + "_" + i + ".txt");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Готово к производству");
            sb.AppendLine("Изделие:  " + cipher + " (" + Path.GetFileName(product) + ")");
            sb.AppendLine("Сборка:   " + Path.GetFileName(assembly));
            sb.AppendLine("Отметил:  " + who + ", " + now.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")));
            sb.AppendLine("Папка:    " + product);
            sb.AppendLine();
            sb.AppendLine("Выдано (SHA-256):");
            foreach (string file in pdfs.Concat(new[] { workbook }))
                sb.AppendLine("  " + Checksum(file) + "  " + Path.GetFileName(file));
            sb.AppendLine();
            sb.AppendLine("Документы изделия:");
            string models = Path.Combine(product, LzkNaming.ModelsFolder);
            if (Directory.Exists(models))
                foreach (string model in Directory.GetFiles(models, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
                    sb.AppendLine("  " + Checksum(model) + "  " + Path.GetFileName(model));
            foreach (string folder in new[] { ExportNaming.PdfDirectory(product), ExportNaming.LaserDirectory(product), ExportNaming.TubeDirectory(product) })
                if (Directory.Exists(folder))
                    foreach (string file in Directory.GetFiles(folder).Where(f => !pdfs.Contains(f, StringComparer.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
                        sb.AppendLine("  " + Checksum(file) + "  " + Path.GetFileName(file));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static bool Fail(ISldWorks app, bool interactive, string text)
        {
            LastOutcome = "error|" + text;
            Log.Warn("Готово к производству: " + text);
            Status(app, "");
            if (interactive) MessageBox.Show(text, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private static void Status(ISldWorks app, string text)
        {
            try
            {
                Frame frame = app != null ? app.Frame() as Frame : null;
                if (frame != null && text != null) frame.SetStatusBarText(text);
            }
            catch (COMException ex)
            {
                Log.Error("Готово к производству: строка состояния", ex);
            }
        }

        private static bool Locked(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException)
            {
                return true;
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
                Log.Error("Готово к производству: контрольная сумма " + path, ex);
                return "";
            }
        }
    }
}
