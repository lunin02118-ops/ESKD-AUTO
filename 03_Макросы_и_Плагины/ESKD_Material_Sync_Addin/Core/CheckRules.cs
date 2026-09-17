using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Уровень находки проверки изделия (ТЗ-02 Т-32: а, б, в — брак; г…з — замечания).</summary>
    public enum CheckLevel
    {
        Ok = 0,
        Issue = 1,
        Defect = 2
    }

    /// <summary>Одна находка проверки: правило, документ, что не так.</summary>
    public sealed class CheckFinding
    {
        public string Rule = "";
        public CheckLevel Level = CheckLevel.Issue;
        public string Document = "";
        public string Text = "";

        public override string ToString()
        {
            return CheckRules.LevelName(Level) + " — " + (Document.Length > 0 ? Document : "изделие") + " — " + Text;
        }
    }

    /// <summary>Итог проверки изделия: находки и контрольные суммы документов.</summary>
    public sealed class CheckReport
    {
        public string Product = "";
        public string Assembly = "";
        public string User = "";
        public DateTime Time = DateTime.Now;
        public readonly List<CheckFinding> Findings = new List<CheckFinding>();
        /// <summary>Файл → SHA-256, в порядке обхода.</summary>
        public readonly List<KeyValuePair<string, string>> Checksums = new List<KeyValuePair<string, string>>();

        public CheckLevel Outcome
        {
            get { return Findings.Count == 0 ? CheckLevel.Ok : Findings.Max(f => f.Level); }
        }

        public int Count(CheckLevel level)
        {
            return Findings.Count(f => f.Level == level);
        }

        public void Add(string rule, CheckLevel level, string document, string text)
        {
            Findings.Add(new CheckFinding { Rule = rule, Level = level, Document = document ?? "", Text = text ?? "" });
        }
    }

    /// <summary>
    /// Перечень проверок изделия и правила отчёта (ТЗ-02 Т-32…Т-34). Уровни зашиты здесь и в настройках
    /// не отключаются: проверка должна отвечать одинаково у всех.
    /// </summary>
    public static class CheckRules
    {
        public const string ReportName = "_Проверка.txt";
        public const string PreviousReportName = "_Проверка_пред.txt";
        public const string ExportReportName = "_Экспорт.txt";

        /// <summary>Правило а: компоненты найдены и лежат в этом заказе, базе или библиотеке.</summary>
        public const string References = "а";
        /// <summary>Правило б: перестроение без ошибок.</summary>
        public const string Rebuild = "б";
        /// <summary>Правило в: реквизиты, материал из библиотеки, масса записана.</summary>
        public const string Attributes = "в";
        /// <summary>
        /// Правило в2: реквизит в модель ещё не записан, но берётся из имени файла или материала
        /// SolidWorks — это не брак, а несделанная синхронизация: кнопка «Синхронизировать» всё запишет.
        /// </summary>
        public const string Sync = "в2";
        /// <summary>Правило г: у детали есть чертёж или признак БЧ.</summary>
        public const string Drawing = "г";
        /// <summary>Правило д: заполнено свойство «Операции».</summary>
        public const string Operations = "д";
        /// <summary>Правило е: файлы выгрузки для производства на месте.</summary>
        public const string Export = "е";
        /// <summary>Правило ж: ведомость изделия есть, без пометок и по текущей сборке.</summary>
        public const string Workbook = "ж";
        /// <summary>Правило з: в чертежах нет оборванных размеров.</summary>
        public const string Drawings = "з";

        /// <summary>Уровень правила: а, б, в — брак; остальные — замечания.</summary>
        public static CheckLevel LevelOf(string rule)
        {
            return rule == References || rule == Rebuild || rule == Attributes ? CheckLevel.Defect : CheckLevel.Issue;
        }

        public static string LevelName(CheckLevel level)
        {
            return level == CheckLevel.Defect ? "БРАК" : level == CheckLevel.Issue ? "ЗАМЕЧАНИЕ" : "ГОТОВО";
        }

        public static string OutcomeName(CheckLevel level)
        {
            return level == CheckLevel.Defect ? "БРАК" : level == CheckLevel.Issue ? "ЗАМЕЧАНИЯ" : "ГОТОВО";
        }

        /// <summary>Текст отчёта _Проверка.txt (пишется в UTF-8 с BOM).</summary>
        public static string Report(CheckReport report)
        {
            if (report == null) return "";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Проверка изделия");
            sb.AppendLine("Изделие:  " + report.Product);
            sb.AppendLine("Сборка:   " + report.Assembly);
            sb.AppendLine("Проверил: " + report.User + ", " + report.Time.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")));
            sb.AppendLine("Итог:     " + OutcomeName(report.Outcome) +
                (report.Findings.Count > 0
                    ? " (брак: " + report.Count(CheckLevel.Defect) + ", замечаний: " + report.Count(CheckLevel.Issue) + ")"
                    : ""));
            sb.AppendLine();
            if (report.Findings.Count == 0) sb.AppendLine("Замечаний нет.");
            else
                foreach (CheckFinding f in report.Findings.OrderByDescending(f => f.Level).ThenBy(f => f.Rule, StringComparer.Ordinal))
                    sb.AppendLine(f.ToString());
            if (report.Checksums.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Контрольные суммы (SHA-256):");
                foreach (KeyValuePair<string, string> pair in report.Checksums)
                    sb.AppendLine("  " + pair.Value + "  " + pair.Key);
            }
            return sb.ToString();
        }

        /// <summary>Путь отчёта и его предыдущей копии.</summary>
        public static string ReportPath(string productFolder)
        {
            return Path.Combine(productFolder ?? "", ReportName);
        }

        public static string PreviousReportPath(string productFolder)
        {
            return Path.Combine(productFolder ?? "", PreviousReportName);
        }

        /// <summary>Итог из прежнего отчёта: первая строка «Итог: …» или пустая строка.</summary>
        public static string OutcomeOf(string reportText)
        {
            foreach (string line in (reportText ?? "").Split('\n'))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("Итог:", StringComparison.Ordinal)) continue;
                string value = trimmed.Substring("Итог:".Length).Trim();
                int bracket = value.IndexOf('(');
                return (bracket > 0 ? value.Substring(0, bracket) : value).Trim();
            }
            return "";
        }
    }
}
