using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Отчёт выдачи `_Выдано_&lt;дата&gt;.txt` (кнопка «Готово к производству»): когда выдано, до какой строки дошёл журнал
    /// изменений и суммы SHA-256 документов изделия. По нему проверка находит выданный документ, изменённый без новой
    /// ревизии (аудит 23.09.2026, CHK-13): цех работает по выданному, и правка без ревизии до него не дойдёт.
    /// </summary>
    public sealed class IssueRecord
    {
        /// <summary>Блок сумм документов изделия — модели, чертежи и файлы выгрузки.</summary>
        public const string DocumentsTitle = "Документы изделия:";
        /// <summary>Строка шапки: последняя строка журнала изменений в момент выдачи.</summary>
        public const string JournalLabel = "Журнал:";

        public string Path = "";
        public DateTime Time = DateTime.MinValue;
        /// <summary>Последняя строка журнала изменений в момент выдачи; -1 — отчёт до 23.09.2026, строки нет.</summary>
        public int JournalLine = -1;
        /// <summary>Имя файла → SHA-256 из блока «Документы изделия»; не прочитанный при выдаче файл — без суммы.</summary>
        public readonly List<KeyValuePair<string, string>> Documents = new List<KeyValuePair<string, string>>();

        /// <summary>
        /// Строка шапки отчёта выдачи о журнале изменений. lastLine &lt; 0 — журнал не прочитан: номера нет, и проверка
        /// сверяет ревизии по дате строки, как у отчётов до 23.09.2026 (писать «строка 0» нельзя — тогда ревизией
        /// считалась бы любая строка журнала).
        /// </summary>
        public static string JournalText(int lastLine)
        {
            return lastLine < 0
                ? JournalLabel + "   не прочитан (" + ChangeLog.FileName + ") — ревизии сверяются по дате"
                : JournalLabel + "   строка " + lastLine.ToString(CultureInfo.InvariantCulture) + " (" + ChangeLog.FileName + ")";
        }

        /// <summary>
        /// Файл документа изделия, а не служебный: «~$Имя.SLDPRT» — метка SolidWorks «файл открыт», её содержимое зависит
        /// от того, кто и где открыл документ (ревью 23.09.2026).
        /// </summary>
        public static bool IsDocumentFile(string name)
        {
            return !(System.IO.Path.GetFileName(name ?? "") ?? "").StartsWith("~$", StringComparison.Ordinal);
        }

        public static IssueRecord Parse(string text)
        {
            IssueRecord record = new IssueRecord();
            bool documents = false;
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (!line.StartsWith(" ", StringComparison.Ordinal))
                {
                    documents = line == DocumentsTitle;
                    if (line.StartsWith("Отметил:", StringComparison.Ordinal))
                    {
                        Match m = Regex.Match(line, @"(\d{2}\.\d{2}\.\d{4} \d{2}:\d{2})\s*$");
                        DateTime time;
                        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out time)) record.Time = time;
                    }
                    else if (line.StartsWith(JournalLabel, StringComparison.Ordinal))
                    {
                        Match m = Regex.Match(line, @"строка\s+(\d+)");
                        int number;
                        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                            record.JournalLine = number;
                    }
                    continue;
                }
                if (!documents) continue;
                string body = line.Trim();
                int gap = body.IndexOf("  ", StringComparison.Ordinal);
                string first = gap > 0 ? body.Substring(0, gap) : "";
                if (first.Length == 64 && first.All(Uri.IsHexDigit))
                    record.Documents.Add(new KeyValuePair<string, string>(body.Substring(gap).Trim(), first.ToLowerInvariant()));
                else
                    record.Documents.Add(new KeyValuePair<string, string>(gap > 0 ? body.Substring(gap).Trim() : body, ""));
            }
            return record;
        }

        /// <summary>Последний отчёт выдачи в папке изделия; нет — null. Имена с датой и временем сортируются по времени.</summary>
        public static IssueRecord Latest(string productFolder)
        {
            if (string.IsNullOrEmpty(productFolder) || !Directory.Exists(productFolder)) return null;
            string path = Directory.GetFiles(productFolder, ExportNaming.IssuedPrefix + "*.txt")
                .OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.Ordinal).LastOrDefault();
            if (path == null) return null;
            try
            {
                IssueRecord record = Parse(File.ReadAllText(path, Encoding.UTF8));
                record.Path = path;
                return record;
            }
            catch (IOException ex)
            {
                Log.Error("Отчёт выдачи " + path, ex);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Отчёт выдачи " + path, ex);
                return null;
            }
        }

        /// <summary>
        /// Выданные детали и чертежи, изменённые после выдачи без новой ревизии: пара «изменённый файл → документ, на котором
        /// поднимают ревизию» (чертёж детали, у детали без чертежа — сама деталь). Изменённым считается файл, сумма которого
        /// не равна выданной; ревизия есть, если в журнале после выдачи — не отменённая строка этого документа. Сборки не
        /// сверяются: их файл меняется и от сохранения после правки детали. Не прочитанный файл изменённым не считается.
        /// </summary>
        /// <param name="current">Имя файла → его сумма сейчас (модели и чертежи из «01_3D»).</param>
        public static List<KeyValuePair<string, string>> Unrevised(IssueRecord issued, IDictionary<string, string> current,
            IEnumerable<ChangeRow> journal)
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            if (issued == null || current == null) return result;
            List<ChangeRow> rows = (journal ?? Enumerable.Empty<ChangeRow>()).Where(r => r != null && !r.Cancelled).ToList();
            foreach (KeyValuePair<string, string> doc in issued.Documents)
            {
                string name = doc.Key;
                string extension = (System.IO.Path.GetExtension(name) ?? "").ToLowerInvariant();
                if (extension != ".sldprt" && extension != ".slddrw" || !IsDocumentFile(name)) continue;
                string now;
                if (doc.Value.Length == 0 || !current.TryGetValue(name, out now) || now.Length == 0) continue;
                if (string.Equals(now, doc.Value, StringComparison.OrdinalIgnoreCase)) continue;
                string drawing = System.IO.Path.GetFileNameWithoutExtension(name) + ".slddrw";
                string owner = extension == ".slddrw" || !current.Keys.Any(k => string.Equals(k, drawing, StringComparison.OrdinalIgnoreCase))
                    ? name : current.Keys.First(k => string.Equals(k, drawing, StringComparison.OrdinalIgnoreCase));
                bool revised = rows.Any(r => string.Equals((r.Document ?? "").Trim(), owner, StringComparison.OrdinalIgnoreCase) &&
                    (issued.JournalLine >= 0 ? r.Number > issued.JournalLine : r.Date.Date >= issued.Time.Date));
                if (!revised) result.Add(new KeyValuePair<string, string>(name, owner));
            }
            return result;
        }
    }
}
