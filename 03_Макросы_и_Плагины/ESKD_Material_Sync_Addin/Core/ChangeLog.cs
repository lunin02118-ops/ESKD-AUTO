using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Причина изменения: код и текст (ГОСТ 2.503-2013 табл. Б.1, приложение А регламента КТО v3.1).</summary>
    public sealed class ChangeReason
    {
        public readonly string Code;
        public readonly string Text;

        public ChangeReason(string code, string text)
        {
            Code = code;
            Text = text;
        }

        public override string ToString()
        {
            return Code + " — " + Text;
        }
    }

    /// <summary>
    /// Список причин для окна «Новая ревизия» (ТЗ-02 Т-49). Восемь пунктов — те же и в том же порядке,
    /// что в приложении А регламента КТО; код в журнале — их номер, чтобы строки можно было считать.
    /// </summary>
    public static class ChangeReasons
    {
        public static readonly ChangeReason[] All =
        {
            new ChangeReason("1", "введение новых или изменение ранее принятых решений по конструкции"),
            new ChangeReason("2", "улучшение технологичности"),
            new ChangeReason("3", "изменение по требованию заказчика"),
            new ChangeReason("4", "устранение ошибок"),
            new ChangeReason("5", "унификация и стандартизация"),
            new ChangeReason("6", "замена материала"),
            new ChangeReason("7", "изменение по результатам испытаний и эксплуатации"),
            new ChangeReason("8", "прочие")
        };

        /// <summary>Причина по коду; неизвестный код — «прочие»: строка журнала не должна пропадать из-за опечатки.</summary>
        public static ChangeReason ByCode(string code)
        {
            string wanted = (code ?? "").Trim();
            foreach (ChangeReason reason in All)
                if (string.Equals(reason.Code, wanted, StringComparison.OrdinalIgnoreCase)) return reason;
            return All[All.Length - 1];
        }
    }

    /// <summary>Строка журнала изменений (ТЗ-02 Т-12).</summary>
    public sealed class ChangeRow
    {
        public int Number;
        public int Revision;
        public DateTime Date = DateTime.Now;
        public string Who = "";
        public string Document = "";
        public string What = "";
        public string Reason = "";
        public string Code = "";
        /// <summary>Что делать с заделом: использовать / доработать / в брак.</summary>
        public string Backlog = "";
        public string Applicability = "";
        /// <summary>Строка отменена: штамп ревизии не сохранился (<see cref="ChangeLog.Cancel"/>).</summary>
        public bool Cancelled;

        public string[] Cells()
        {
            return new[]
            {
                Number.ToString(CultureInfo.InvariantCulture),
                Revision.ToString(CultureInfo.InvariantCulture),
                Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                Who ?? "", Document ?? "", What ?? "", Reason ?? "", Code ?? "", Backlog ?? "", Applicability ?? ""
            };
        }
    }

    /// <summary>
    /// Журнал `Изменения.xlsx` в папке изделия или эталона (ТЗ-02 Т-12, Т-51, Т-55). Строки пишут кнопки
    /// К-7 и К-8; человек журнал не ведёт. Файла нет — книга заводится с шапкой; файл занят коллегой —
    /// запись повторяется до 30 секунд, потому что потерянная строка равносильна потерянному извещению.
    /// </summary>
    public static class ChangeLog
    {
        public const string FileName = "Изменения.xlsx";
        public const string SheetName = "Изменения";
        public static readonly string[] Columns =
        {
            "№", "Ревизия", "Дата", "Кто", "Документ", "Что изменено", "Причина", "Код по ГОСТ 2.503",
            "Задел", "Применяемость"
        };
        private static readonly double[] Widths = { 5, 9, 12, 18, 34, 40, 34, 12, 14, 30 };
        /// <summary>Сколько ждать занятый файл (Т-51). Ожидание идёт в потоке SolidWorks — дольше нельзя: окно «зависнет».</summary>
        public static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
        /// <summary>Метка «журнал пишет другой конструктор» старше этого — след упавшего сеанса, её можно снять.</summary>
        public static readonly TimeSpan StaleLock = TimeSpan.FromMinutes(2);

        public static string Path(string folder)
        {
            return System.IO.Path.Combine(folder ?? "", FileName);
        }

        /// <summary>
        /// Номер последней строки журнала; журнала нет или он пуст — 0; журнал есть, но не прочитан — -1. Раньше и
        /// непрочитанный журнал давал 0, и отчёт выдачи записывал «строка 0» — проверка потом считала ревизией любую
        /// строку журнала (ревью 23.09.2026).
        /// </summary>
        public static int LastNumber(string path)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                using (XlsxBook book = XlsxBook.Open(path))
                {
                    XlsxSheet sheet = book.Sheet(SheetName) ?? book.Sheet(book.SheetNames.FirstOrDefault() ?? "");
                    return sheet == null ? 0 : LastNumber(sheet);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Журнал изменений: чтение " + path, ex);
                return -1;
            }
        }

        private static int LastNumber(XlsxSheet sheet)
        {
            int last = 0;
            foreach (int row in sheet.RowNumbers)
            {
                int number;
                if (int.TryParse((sheet.Get(1, row) ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) &&
                    number > last) last = number;
            }
            return last;
        }

        /// <summary>
        /// Дописывает строку и возвращает её номер; 0 — записать не удалось (тогда причина уже в журнале надстройки).
        /// Номер строки идёт в графу «№ докум.» штампа, поэтому он считается по самому журналу, а не по счётчику.
        /// </summary>
        public static int Append(string path, ChangeRow row)
        {
            DateTime deadline = DateTime.UtcNow + Wait;
            Exception last = null;
            while (true)
            {
                try
                {
                    // Книга читается целиком в память и записывается заново: два конструктора, дописывающие строку
                    // одновременно, затёрли бы строки друг друга. Метка-файл рядом с книгой пускает писать по одному.
                    using (Lock(path))
                        return Write(path, row);
                }
                catch (IOException ex)
                {
                    last = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    last = ex;
                }
                if (DateTime.UtcNow >= deadline) break;
                Thread.Sleep(500);
            }
            Log.Error("Журнал изменений: строка не записана в " + path, last);
            return 0;
        }

        /// <summary>Метка «журнал занят»: файл `Изменения.xlsx.lock`, удаляется при закрытии. Занято — IOException.</summary>
        private static FileStream Lock(string path)
        {
            string lockPath = path + ".lock";
            string directory = System.IO.Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            try
            {
                if (File.Exists(lockPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) > StaleLock)
                    File.Delete(lockPath);
            }
            catch (IOException ex)
            {
                Log.Error("Журнал изменений: старая метка " + lockPath, ex);
            }
            return new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }

        private static int Write(string path, ChangeRow row)
        {
            bool fresh = !File.Exists(path);
            using (XlsxBook book = fresh ? XlsxBook.Create(path, SheetName) : XlsxBook.Open(path))
            {
                XlsxSheet sheet = book.Sheet(SheetName);
                if (sheet == null) sheet = book.AddSheet(SheetName);
                int header = book.AddStyle(new XlsxStyle { Bold = true, Border = true, Wrap = true, Horizontal = "center" });
                int cell = book.AddStyle(new XlsxStyle { Border = true, Wrap = true, Vertical = "top", Horizontal = "left" });
                if (sheet.RowNumbers.Count == 0)
                {
                    for (int i = 0; i < Columns.Length; i++)
                    {
                        sheet.SetText(XlsxBook.CellName(i + 1, 1), Columns[i], header);
                        sheet.SetColumnWidth(i + 1, Widths[i]);
                    }
                    sheet.SetRowHeight(1, 30);
                    sheet.FreezeRowsAbove("A2");
                }
                int number = LastNumber(sheet) + 1;
                row.Number = number;
                int line = sheet.RowNumbers.Count == 0 ? 2 : sheet.RowNumbers.Max() + 1;
                string[] cells = row.Cells();
                for (int i = 0; i < cells.Length; i++) sheet.SetText(XlsxBook.CellName(i + 1, line), cells[i], cell);
                book.NormalizeFonts("Arial");
                book.Save();
                return number;
            }
        }

        /// <summary>Пометка отменённой строки в графе «Что изменено».</summary>
        public const string CancelledPrefix = "ОТМЕНЕНО: ";

        /// <summary>
        /// Строка отменена: графа «Что изменено» начинается словом «ОТМЕНЕНО» — с двоеточием или без, в любом регистре.
        /// Так её пометит и конструктор вручную, когда книга была занята (ревью 23.09.2026): раньше засчитывалась только
        /// точная пометка кнопки.
        /// </summary>
        public static bool IsCancelled(string what)
        {
            return (what ?? "").TrimStart().StartsWith(CancelledPrefix.TrimEnd(' ', ':'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Строка журнала отменена — штамп ревизии не сохранился (аудит 23.09.2026, SAVE-9): графа «Что изменено» получает
        /// пометку «ОТМЕНЕНО: причина — …». Строка не удаляется: журнал нумеруется подряд, а номер мог уже попасть в
        /// штамп. true — пометка записана.
        /// </summary>
        public static bool Cancel(string path, int number, string reason)
        {
            if (!File.Exists(path)) return false;
            DateTime deadline = DateTime.UtcNow + Wait;
            Exception last = null;
            while (true)
            {
                try
                {
                    using (Lock(path))
                    using (XlsxBook book = XlsxBook.Open(path))
                    {
                        XlsxSheet sheet = book.Sheet(SheetName);
                        if (sheet == null) return false;
                        foreach (int line in sheet.RowNumbers)
                        {
                            int n;
                            if (!int.TryParse((sheet.Get(1, line) ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ||
                                n != number) continue;
                            string cell = XlsxBook.CellName(6, line);
                            string what = sheet.Get(6, line) ?? "";
                            if (IsCancelled(what)) return true;
                            sheet.SetText(cell, CancelledPrefix + (reason ?? "") + " — " + what, sheet.StyleOf(cell));
                            book.Save();
                            return true;
                        }
                        return false;
                    }
                }
                catch (IOException ex)
                {
                    last = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    last = ex;
                }
                if (DateTime.UtcNow >= deadline) break;
                Thread.Sleep(500);
            }
            Log.Error("Журнал изменений: строка " + number + " не помечена отменённой в " + path, last);
            return false;
        }

        /// <summary>Строки журнала: номер, ревизия, дата, документ, что изменено; отменённые помечены.</summary>
        public static List<ChangeRow> Read(string path)
        {
            List<ChangeRow> rows = new List<ChangeRow>();
            foreach (Dictionary<string, string> row in Rows(path))
            {
                int number, revision;
                DateTime date;
                int.TryParse(row[Columns[0]].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
                int.TryParse(row[Columns[1]].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out revision);
                DateTime.TryParseExact(row[Columns[2]].Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
                string what = row[Columns[5]];
                rows.Add(new ChangeRow
                {
                    Number = number,
                    Revision = revision,
                    Date = date,
                    Who = row[Columns[3]],
                    Document = row[Columns[4]],
                    What = what,
                    Cancelled = IsCancelled(what)
                });
            }
            return rows;
        }

        /// <summary>Строки журнала как словари «колонка → значение» — для проверок и отчётов.</summary>
        public static List<Dictionary<string, string>> Rows(string path)
        {
            List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
            if (!File.Exists(path)) return rows;
            using (XlsxBook book = XlsxBook.Open(path))
            {
                XlsxSheet sheet = book.Sheet(SheetName);
                if (sheet == null) return rows;
                foreach (int line in sheet.RowNumbers)
                {
                    if (line == 1) continue;
                    Dictionary<string, string> row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < Columns.Length; i++) row[Columns[i]] = sheet.Get(i + 1, line) ?? "";
                    if (row[Columns[0]].Trim().Length > 0) rows.Add(row);
                }
            }
            return rows;
        }
    }
}
