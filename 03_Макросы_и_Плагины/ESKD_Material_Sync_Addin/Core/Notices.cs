using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Критичность замечания в окне «Замечания» (решение владельца 18.09.2026): три уровня для всех кнопок.
    /// </summary>
    public enum NoticeLevel
    {
        /// <summary>Не мешает: пояснение, пропуск, который так и должен быть.</summary>
        Info = 0,
        /// <summary>Надо доделать: «Готово к производству» изделие не пропустит.</summary>
        Warning = 1,
        /// <summary>В производство нельзя, или действие кнопки не выполнилось.</summary>
        Critical = 2
    }

    /// <summary>Одна строка окна «Замечания»: уровень, документ, что не так, что сделать.</summary>
    public sealed class Notice
    {
        public NoticeLevel Level = NoticeLevel.Warning;
        public string Document = "";
        public string Text = "";
        public string Hint = "";

        public override string ToString()
        {
            return Notices.LevelName(Level) + " — " + (Document.Length > 0 ? Document : "изделие") + " — " + Text +
                (Hint.Length > 0 ? " → " + Hint : "");
        }
    }

    /// <summary>
    /// Замечания кнопок вкладки ЕСКД в одном виде: их показывает окно, а не текстовые отчёты (решение владельца
    /// 18.09.2026). Здесь — перевод находок каждой кнопки в <see cref="Notice"/> без SolidWorks и окон, для юнит-тестов.
    /// </summary>
    public static class Notices
    {
        /// <summary>Подсказки «что сделать» в тексте находки: всё после маркера уходит в колонку «Что сделать».</summary>
        private static readonly string[] HintMarkers =
        {
            ": нажмите ", " — нажмите ", ": поправьте ", " — поправьте ", ": уточните ", " — уточните ",
            ": оформите ", ", оформите ", " (кнопка ", ": сохраните ", " — сохраните "
        };

        private static List<Notice> _last = new List<Notice>();

        /// <summary>
        /// Замечания последней кнопки — и при запуске без окна (COM, автотесты): текстовых отчётов для людей больше
        /// нет, а проверять, что кнопка сказала, нужно.
        /// </summary>
        public static void Remember(IEnumerable<Notice> notices)
        {
            _last = Ordered(notices);
        }

        /// <summary>Замечания последней кнопки текстом, строка на замечание (<see cref="Notice.ToString"/>).</summary>
        public static string LastText
        {
            get { return Text(_last); }
        }

        public static string LevelName(NoticeLevel level)
        {
            return level == NoticeLevel.Critical ? "КРИТИЧНО" : level == NoticeLevel.Warning ? "ЗАМЕЧАНИЕ" : "К СВЕДЕНИЮ";
        }

        public static Notice Of(NoticeLevel level, string document, string text)
        {
            string what, hint;
            SplitHint(text, out what, out hint);
            return new Notice { Level = level, Document = (document ?? "").Trim(), Text = what, Hint = hint };
        }

        public static Notice Of(NoticeLevel level, string document, string text, string hint)
        {
            return new Notice { Level = level, Document = (document ?? "").Trim(), Text = (text ?? "").Trim(), Hint = Capital(hint) };
        }

        /// <summary>«нет чертежа (кнопка «Деталь БЧ»)» → «нет чертежа» и «Кнопка «Деталь БЧ»».</summary>
        public static void SplitHint(string text, out string what, out string hint)
        {
            text = (text ?? "").Trim();
            int best = -1;
            string marker = "";
            foreach (string m in HintMarkers)
            {
                int i = text.IndexOf(m, StringComparison.Ordinal);
                if (i > 0 && (best < 0 || i < best))
                {
                    best = i;
                    marker = m;
                }
            }
            if (best < 0)
            {
                what = text;
                hint = "";
                return;
            }
            what = text.Substring(0, best).Trim();
            string rest = text.Substring(best + marker.Length).Trim();
            if (marker == " (кнопка ")
            {
                if (rest.EndsWith(")", StringComparison.Ordinal)) rest = rest.Substring(0, rest.Length - 1);
                rest = "кнопка " + rest;
            }
            else
            {
                rest = marker.Trim(' ', ':', '—', ',') + " " + rest;
            }
            hint = Capital(rest);
        }

        private static string Capital(string text)
        {
            text = (text ?? "").Trim();
            return text.Length == 0 ? text : char.ToUpper(text[0]) + text.Substring(1);
        }

        public static NoticeLevel Max(IEnumerable<Notice> notices)
        {
            NoticeLevel max = NoticeLevel.Info;
            foreach (Notice n in notices ?? Enumerable.Empty<Notice>()) if (n.Level > max) max = n.Level;
            return max;
        }

        public static int Count(IEnumerable<Notice> notices, NoticeLevel level)
        {
            return (notices ?? Enumerable.Empty<Notice>()).Count(n => n.Level == level);
        }

        /// <summary>Сначала критичные, затем замечания, затем сведения; внутри уровня — порядок появления.</summary>
        public static List<Notice> Ordered(IEnumerable<Notice> notices)
        {
            return (notices ?? Enumerable.Empty<Notice>())
                .Select((n, i) => new { n, i })
                .OrderByDescending(x => x.n.Level).ThenBy(x => x.i)
                .Select(x => x.n).ToList();
        }

        /// <summary>Текст для буфера обмена: строка на замечание.</summary>
        public static string Text(IEnumerable<Notice> notices)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Notice n in Ordered(notices)) sb.AppendLine(n.ToString());
            return sb.ToString();
        }

        // ------------------------------------------------------------------ проверка изделия
        public static NoticeLevel FromCheck(CheckLevel level)
        {
            return level == CheckLevel.Defect ? NoticeLevel.Critical : level == CheckLevel.Issue ? NoticeLevel.Warning : NoticeLevel.Info;
        }

        public static List<Notice> FromCheck(CheckReport report)
        {
            List<Notice> list = new List<Notice>();
            if (report == null) return list;
            foreach (CheckFinding f in report.Findings) list.Add(Of(FromCheck(f.Level), f.Document, f.Text));
            return list;
        }

        /// <summary>
        /// Находки из прежнего отчёта `_Проверка.txt` (пункт «Отчёт проверки»): строки «БРАК — документ — текст»
        /// и «ЗАМЕЧАНИЕ — документ — текст».
        /// </summary>
        public static List<Notice> ParseCheckReport(string reportText)
        {
            List<Notice> list = new List<Notice>();
            string defect = CheckRules.LevelName(CheckLevel.Defect) + " — ";
            string issue = CheckRules.LevelName(CheckLevel.Issue) + " — ";
            foreach (string raw in (reportText ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                NoticeLevel level;
                string rest;
                if (line.StartsWith(defect, StringComparison.Ordinal))
                {
                    level = NoticeLevel.Critical;
                    rest = line.Substring(defect.Length);
                }
                else if (line.StartsWith(issue, StringComparison.Ordinal))
                {
                    level = NoticeLevel.Warning;
                    rest = line.Substring(issue.Length);
                }
                else continue;
                int dash = rest.IndexOf(" — ", StringComparison.Ordinal);
                string document = dash > 0 ? rest.Substring(0, dash) : "";
                string text = dash > 0 ? rest.Substring(dash + 3) : rest;
                if (document == "изделие") document = "";
                list.Add(Of(level, document, text));
            }
            return list;
        }

        // ------------------------------------------------------------------ книга ЛЗК
        /// <summary>
        /// Замечания книги ЛЗК: ошибки — критично, пометки «?» — замечания (с ними изделие не выдаётся),
        /// пояснения (длина по габариту «*», расход) — к сведению. Строки вида «Строка 12 (обозначение): текст»
        /// делятся на документ и текст.
        /// </summary>
        public static List<Notice> FromLzk(LzkResult result)
        {
            List<Notice> list = new List<Notice>();
            if (result == null) return list;
            foreach (string e in result.Errors) list.Add(Labeled(NoticeLevel.Critical, e));
            foreach (string i in result.Issues) list.Add(Labeled(NoticeLevel.Warning, i));
            foreach (string n in result.Notes) list.Add(Labeled(NoticeLevel.Info, n));
            return list;
        }

        private static Notice Labeled(NoticeLevel level, string text)
        {
            text = (text ?? "").Trim();
            string document = "";
            if (text.StartsWith("Строка ", StringComparison.Ordinal))
            {
                int colon = text.IndexOf("): ", StringComparison.Ordinal);
                if (colon > 0)
                {
                    document = text.Substring(0, colon + 1);
                    text = text.Substring(colon + 3);
                }
            }
            Notice notice = Of(level, document, text);
            if (notice.Hint.Length == 0) notice.Hint = LzkHint(notice.Text);
            return notice;
        }

        /// <summary>Что делать с пометкой книги ЛЗК — по её тексту.</summary>
        private static string LzkHint(string text)
        {
            if (text.Contains("«Материал»") || text.Contains("«Масса»"))
                return "Назначьте материал из библиотеки, сохраните деталь и пересоберите книгу";
            if (text.Contains("«Операции»")) return "Отметьте операции в окне «Операции» кнопки «Ведомость ЛЗК»";
            if (text.Contains("габарит") || text.Contains("измерена по модели"))
                return "Проверьте длину заготовки: задайте размер RD1 в «Примечаниях» или обновите список вырезов и пересоберите книгу";
            if (text.Contains("модели нет в составе")) return "Уберите строку или добавьте модель в сборку";
            if (text.Contains("«Раздел»")) return "Проверьте свойство «Раздел» (SProp)";
            return "";
        }

        // ------------------------------------------------------------------ выгрузка
        /// <summary>
        /// Пропуски выгрузки «документ — причина»: файл не получился — критично; выданный документ — замечание
        /// (нужна ревизия); нет чертежа или развёртки — к сведению (так бывает у БЧ и у деталей не из листа).
        /// </summary>
        public static List<Notice> FromExport(IEnumerable<string> skipped, IEnumerable<string> warnings = null)
        {
            List<Notice> list = new List<Notice>();
            foreach (string line in warnings ?? Enumerable.Empty<string>())
            {
                string document, reason;
                // Многотельной детали IGS не сделан (З-51) — проверять нечего, поправить нужно модель. Строка делится по
                // « — » перед замечанием: « — » бывает и в имени файла.
                if (ExportLog.SplitNote(line, ExportLog.MultibodyNote, out document, out reason))
                {
                    list.Add(Of(NoticeLevel.Warning, document, reason, "Проверьте чертёж и сборку детали"));
                    continue;
                }
                SplitDocument(line, out document, out reason);
                // Убранный в «_Аннулировано» прежний файл — сведения, а не забота: в папке выдачи его уже нет (З-48).
                if (ExportLog.IsArchiveNote(reason)) list.Add(Of(NoticeLevel.Info, document, reason, ""));
                else list.Add(Of(NoticeLevel.Warning, document, reason, "Проверьте файл перед резкой"));
            }
            foreach (string line in skipped ?? Enumerable.Empty<string>())
            {
                string document, reason;
                SplitDocument(line, out document, out reason);
                NoticeLevel level;
                string hint = "";
                if (reason.Contains("выдан в производство"))
                {
                    level = NoticeLevel.Warning;
                    hint = "Кнопка «Новая ревизия» на чертеже";
                    reason = "документ выдан в производство — не перезаписан";
                }
                else if (reason.StartsWith("нет чертежа", StringComparison.Ordinal) || reason.StartsWith("нет развёртки", StringComparison.Ordinal))
                {
                    level = NoticeLevel.Info;
                    hint = reason.StartsWith("нет чертежа", StringComparison.Ordinal)
                        ? "Сделайте чертёж или оформите деталь кнопкой «Деталь БЧ»" : "";
                }
                else
                {
                    level = NoticeLevel.Critical;
                    hint = "Проверьте документ в SolidWorks и повторите выгрузку";
                }
                list.Add(Of(level, document, reason, hint));
            }
            return list;
        }

        // ------------------------------------------------------------------ сделать независимым
        public static List<Notice> FromIndependent(IndependentLog log)
        {
            List<Notice> list = new List<Notice>();
            if (log == null) return list;
            foreach (KeyValuePair<string, bool> source in log.Sources)
                if (!source.Value)
                    list.Add(Of(NoticeLevel.Critical, source.Key, "исходная модель изменилась (контрольная сумма не совпала)",
                        "Проверьте эталон и сообщите куратору базы"));
            foreach (string line in log.Skipped)
            {
                string document, reason;
                SplitDocument(line, out document, out reason);
                bool expected = reason.StartsWith("деталь уже своя", StringComparison.Ordinal) ||
                                reason.StartsWith("покупное или стандартное", StringComparison.Ordinal) ||
                                reason.StartsWith("чертежа у исходной модели нет", StringComparison.Ordinal);
                list.Add(Of(expected ? NoticeLevel.Info : NoticeLevel.Critical, document, reason));
            }
            foreach (IndependentEntry e in log.Created)
            {
                if (e.Dangling > 0)
                    list.Add(Of(NoticeLevel.Warning, System.IO.Path.GetFileName(e.Drawing),
                        "оборванных размеров в копии чертежа: " + e.Dangling, "Поправьте размеры в чертеже вручную"));
                list.Add(Of(NoticeLevel.Info, System.IO.Path.GetFileName(e.Target),
                    "сделана своя копия из " + System.IO.Path.GetFileName(e.Source) + ", экземпляров перепривязано: " + e.Instances +
                    (e.Drawing.Length > 0 ? ", с чертежом" : "")));
            }
            return list;
        }

        private static void SplitDocument(string line, out string document, out string reason)
        {
            line = (line ?? "").Trim();
            int dash = line.IndexOf(" — ", StringComparison.Ordinal);
            document = dash > 0 ? line.Substring(0, dash) : "";
            reason = dash > 0 ? line.Substring(dash + 3) : line;
        }
    }
}
