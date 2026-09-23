using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Файл, который сохранила сама кнопка (ЛЗК, выгрузка): его сумма до сохранения и после.</summary>
    public sealed class StampChange
    {
        /// <summary>Полный путь: по нему находится изделие, в отчёты которого идёт новая сумма; пусто — не известен.</summary>
        public string Path = "";
        public string Name = "";
        public string Before = "";
        public string After = "";
    }

    /// <summary>
    /// Версия изделия — отметка того состояния моделей, по которому сделана проверка (решение владельца 23.09.2026, журнал
    /// З-27). Проверка пишет её в `_Проверка.txt` вместе с суммами файлов изделия. ЛЗК и выгрузка сверяют суммы: совпали и
    /// несохранённых правок нет — изделие проверено и с тех пор не менялось, вопросов нет. Книга ЛЗК и отчёт выгрузки
    /// запоминают версию, по которой сделаны; «Готово к производству» требует, чтобы у проверки, книги и выгрузки она была
    /// одна. Свои сохранения кнопок (записанные «Операции», возвращённое исполнение) версию не меняют: кнопка вписывает в
    /// отчёт проверки новые суммы своих файлов (<see cref="Restamp"/>) — только тем файлам, что были ровно как при проверке.
    /// </summary>
    public sealed class ProductStamp
    {
        public const string VersionLabel = "Версия:";
        public const string ChecksumTitle = "Контрольные суммы (SHA-256):";
        public const string RestampLabel = "Суммы обновлены:";
        /// <summary>Книга или выгрузка сделаны по непроверенному (или изменённому после проверки) изделию.</summary>
        public const string Unchecked = "не проверено";

        public string Assembly = "";
        public string Version = "";
        /// <summary>Файл → SHA-256, как в отчёте; пустая сумма — файл не прочитался.</summary>
        public readonly List<KeyValuePair<string, string>> Checksums = new List<KeyValuePair<string, string>>();

        /// <summary>Отметка из текста отчёта проверки. Отчёт до 23.09.2026 — без версии (<see cref="Version"/> пустая).</summary>
        public static ProductStamp Parse(string reportText)
        {
            ProductStamp stamp = new ProductStamp();
            bool sums = false;
            foreach (string raw in (reportText ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (!line.StartsWith(" ", StringComparison.Ordinal))
                {
                    sums = line == ChecksumTitle;
                    if (line.StartsWith("Сборка:", StringComparison.Ordinal))
                        stamp.Assembly = line.Substring("Сборка:".Length).Trim();
                    else if (line.StartsWith(VersionLabel, StringComparison.Ordinal))
                        stamp.Version = line.Substring(VersionLabel.Length).Trim();
                    continue;
                }
                if (!sums) continue;
                string sum, name;
                SplitSum(line.Trim(), out sum, out name);
                if (name.Length > 0) stamp.Checksums.Add(new KeyValuePair<string, string>(name, sum));
            }
            return stamp;
        }

        /// <summary>Отметка из файла отчёта; отчёта нет или он не читается — null.</summary>
        public static ProductStamp Read(string reportPath)
        {
            try
            {
                if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath)) return null;
                return Parse(File.ReadAllText(reportPath, Encoding.UTF8));
            }
            catch (IOException ex)
            {
                Log.Error("Версия изделия: отчёт " + reportPath, ex);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Версия изделия: отчёт " + reportPath, ex);
                return null;
            }
        }

        /// <summary>
        /// Новая версия: время проверки и первые знаки суммы по всем файлам — «23.09.2026 14:05:31 #1a2b3c4d». По времени
        /// её узнаёт человек; по сумме две проверки в одну секунду с разными файлами не получат одну версию.
        /// </summary>
        public static string NewVersion(DateTime time, IEnumerable<KeyValuePair<string, string>> checksums)
        {
            StringBuilder all = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in (checksums ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Value, StringComparer.Ordinal))
                all.Append(pair.Key.ToLowerInvariant()).Append('|').Append(pair.Value).Append('\n');
            string digest;
            using (SHA256 sha = SHA256.Create())
                digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(all.ToString())), 0, 4).Replace("-", "").ToLowerInvariant();
            return time.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " #" + digest;
        }

        /// <summary>
        /// Файлы, которые не совпали с отметкой: изменённые, новые в составе и выбывшие из него (по имени, без повторов).
        /// Пустая сумма (файл не прочитался) не совпадает ни с чем.
        /// </summary>
        public static List<string> Differences(IEnumerable<KeyValuePair<string, string>> recorded,
            IEnumerable<KeyValuePair<string, string>> current)
        {
            List<KeyValuePair<string, string>> left = (recorded ?? Enumerable.Empty<KeyValuePair<string, string>>()).ToList();
            List<string> result = new List<string>();
            foreach (KeyValuePair<string, string> now in current ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                int i = now.Value.Length == 0 ? -1 : left.FindIndex(p => SameFile(p, now.Key, now.Value));
                if (i >= 0) left.RemoveAt(i);
                else Note(result, now.Key);
            }
            foreach (KeyValuePair<string, string> gone in left) Note(result, gone.Key);
            return result;
        }

        /// <summary>
        /// Новые суммы файлов, которые сохранила кнопка: строка отчёта меняется, только если её сумма равна сумме файла до
        /// сохранения — файл был ровно как при проверке, и кнопка изменила в нём только своё. Над суммами — строка, кто и
        /// когда их обновил. Ничего не совпало — текст возвращается прежним.
        /// </summary>
        public static string Restamp(string reportText, IEnumerable<StampChange> changes, string step, DateTime time,
            out int replaced)
        {
            return Restamp(reportText, changes, step, time, ChecksumTitle, out replaced);
        }

        /// <summary>То же для блока сумм с другим заголовком — «Документы изделия:» отчёта выдачи `_Выдано_…`.</summary>
        public static string Restamp(string reportText, IEnumerable<StampChange> changes, string step, DateTime time, string title,
            out int replaced)
        {
            replaced = 0;
            List<StampChange> pending = (changes ?? Enumerable.Empty<StampChange>())
                .Where(c => c != null && c.Before.Length > 0 && c.After.Length > 0 &&
                    !string.Equals(c.Before, c.After, StringComparison.OrdinalIgnoreCase)).ToList();
            string text = reportText ?? "";
            if (pending.Count == 0) return text;
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            int at = lines.FindIndex(l => l.TrimEnd() == title);
            if (at < 0) return text;
            List<string> names = new List<string>();
            for (int i = at + 1; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0 || !line.StartsWith(" ", StringComparison.Ordinal)) break;
                string sum, name;
                SplitSum(line.Trim(), out sum, out name);
                string indent = line.Substring(0, line.Length - line.TrimStart().Length);
                // Кнопка могла сохранить файл за запуск не раз (развёртка исполнения, затем СК трубы): суммы идут цепочкой.
                while (true)
                {
                    string current = sum;
                    int match = pending.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Before, current, StringComparison.OrdinalIgnoreCase));
                    if (match < 0) break;
                    sum = pending[match].After.ToLowerInvariant();
                    lines[i] = indent + sum + "  " + name;
                    Note(names, name);
                    pending.RemoveAt(match);
                    replaced++;
                }
            }
            if (replaced == 0) return text;
            lines.Insert(at, RestampLabel + " " + step + ", " + time.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) +
                " — " + string.Join(", ", names.ToArray()));
            return string.Join(newline, lines.ToArray());
        }

        private static bool SameFile(KeyValuePair<string, string> recorded, string name, string sum)
        {
            return string.Equals(recorded.Key, name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(recorded.Value, sum, StringComparison.OrdinalIgnoreCase);
        }

        private static void Note(List<string> names, string name)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        /// <summary>«сумма  имя»: сумма — 64 шестнадцатеричных знака; не прочитанный файл записан без суммы.</summary>
        private static void SplitSum(string body, out string sum, out string name)
        {
            int gap = body.IndexOf(' ');
            string first = gap > 0 ? body.Substring(0, gap) : body;
            if (first.Length == 64 && first.All(Uri.IsHexDigit))
            {
                sum = first.ToLowerInvariant();
                name = gap > 0 ? body.Substring(gap).Trim() : "";
                return;
            }
            sum = "";
            name = body.Trim();
        }
    }
}
