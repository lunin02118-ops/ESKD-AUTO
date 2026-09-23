using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Результат разбора имени файла «Обозначение [код документа] Наименование».</summary>
    public sealed class ParsedName
    {
        public string BaseName = "";
        public string Root = "";
        public string Execution;
        public string DocCode = "";
        public string Title = "";
        public bool IsTemplateName;

        /// <summary>Обозначение без кода документа, с исполнением из имени файла (если есть).</summary>
        public string Designation
        {
            get { return DesignationParser.Build(Root, Execution, ""); }
        }

        public bool HasDesignation
        {
            get { return !string.IsNullOrEmpty(Root); }
        }
    }

    public static class DesignationParser
    {
        public const string DocCodes = "СБ|ГЧ|МЧ|ВО|ТУ|ТБ|ПЭ|Э\\d|СХ|СЭ|ВП|СП";
        private static readonly Regex TemplateName = new Regex(@"^(Деталь|Part|Сборка|Assem|Чертеж|Чертёж|Draw)\s*\d*$", RegexOptions.IgnoreCase);
        private static readonly Regex LeadingDocCode = new Regex(@"^(" + DocCodes + @")(\s+.*|$)", RegexOptions.IgnoreCase);
        private static readonly Regex TrailingDocCode = new Regex(@"(?:\s+|(?<=[0-9]))(" + DocCodes + @")$", RegexOptions.IgnoreCase);
        private static readonly Regex TrailingExecution = new Regex(@"-(\d{1,4})$");

        public static string CleanDocumentName(string pathOrTitle)
        {
            if (string.IsNullOrWhiteSpace(pathOrTitle)) return "";
            string name;
            try { name = Path.GetFileName(pathOrTitle.Trim()); }
            catch (ArgumentException) { name = pathOrTitle.Trim(); }
            if (string.IsNullOrWhiteSpace(name)) return "";
            name = Regex.Replace(name, @"\.(sldprt|sldasm|slddrw|prt|asm|drw)$", "", RegexOptions.IgnoreCase).Trim();
            name = Regex.Replace(name, @"\s*-\s*(Лист|Sheet)\s*\d*$", "", RegexOptions.IgnoreCase).Trim();
            return name;
        }

        /// <summary>
        /// Значение похоже на обозначение ЕСКД: есть цифры и точка, нет скобок («778.01.000», «ПРТИ.000000.001»).
        /// «Тело5», «Вырез-Вытянуть3[1]» — имена тел и элементов, их окно обхода изделия предлагает заменить именем файла.
        /// </summary>
        public static bool LooksLikeDesignation(string value)
        {
            string v = (value ?? "").Trim();
            return v.IndexOf('.') > 0 && Regex.IsMatch(v, @"\d") && v.IndexOfAny(new[] { '[', ']', '(', ')' }) < 0;
        }

        public static bool IsTemplateName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && TemplateName.IsMatch(value.Trim());
        }

        /// <summary>Разбор имени файла по разделителю словаря SWPlus (prpNameSep, обычно пробел).</summary>
        public static ParsedName Parse(string pathOrTitle, string separator)
        {
            ParsedName p = new ParsedName();
            string baseName = CleanDocumentName(pathOrTitle);
            p.BaseName = baseName;
            if (string.IsNullOrEmpty(baseName)) return p;
            if (IsTemplateName(baseName))
            {
                p.IsTemplateName = true;
                return p;
            }
            if (string.IsNullOrEmpty(separator)) separator = " ";
            string designation;
            int idx = baseName.IndexOf(separator, StringComparison.Ordinal);
            // Обозначение ЕСКД всегда содержит цифры: «Сборка рамы» — это только наименование.
            if (idx > 0 && !Regex.IsMatch(baseName.Substring(0, idx), @"\d"))
            {
                p.Title = baseName;
                return p;
            }
            if (idx > 0)
            {
                designation = baseName.Substring(0, idx).Trim();
                string title = baseName.Substring(idx + separator.Length).Trim();
                Match code = LeadingDocCode.Match(title);
                if (code.Success)
                {
                    p.DocCode = code.Groups[1].Value.ToUpperInvariant();
                    title = code.Groups[2].Value.Trim();
                }
                p.Title = title;
            }
            else if (Regex.IsMatch(baseName, @"\d"))
            {
                designation = baseName;
            }
            else
            {
                p.Title = baseName;
                return p;
            }

            string exec, trailingCode;
            p.Root = StripExecutionSuffix(designation, out exec, out trailingCode);
            p.Execution = exec;
            if (string.IsNullOrEmpty(p.DocCode) && !string.IsNullOrEmpty(trailingCode)) p.DocCode = trailingCode.Trim();
            return p;
        }

        /// <summary>Отделяет код документа (« СБ») и суффикс исполнения («-01») от обозначения.</summary>
        public static string StripExecutionSuffix(string designation, out string execution, out string docCode)
        {
            execution = null;
            docCode = "";
            if (string.IsNullOrWhiteSpace(designation)) return "";
            string trimmed = designation.Trim();
            Match code = TrailingDocCode.Match(trimmed);
            if (code.Success)
            {
                docCode = " " + code.Groups[1].Value.ToUpperInvariant();
                trimmed = trimmed.Substring(0, code.Index).TrimEnd();
            }
            Match exec = TrailingExecution.Match(trimmed);
            if (exec.Success)
            {
                execution = exec.Groups[1].Value;
                trimmed = trimmed.Substring(0, exec.Index).TrimEnd();
            }
            return trimmed;
        }

        /// <summary>
        /// Техническая производная по имени (сверка SW API 23.09.2026, №40): развёртка «…SM-FLAT-PATTERN», «…&lt;Как
        /// сварено&gt;», «…&lt;Как обработанный&gt;». Это не исполнение: материал ставится её родителю. Производная обычного
        /// типа («01» от «00», «Укосина») — исполнение (M06b). Признаки — те же, что у разбора исполнения ниже.
        /// </summary>
        public static bool IsTechnicalConfigurationName(string name)
        {
            string s = (name ?? "").Trim();
            return s.IndexOf("SM-FLAT-PATTERN", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf('<') > 0;
        }

        /// <summary>Исполнение по имени конфигурации (ГОСТ 2.113): «00» — базовое, «01», «-02», «исп. 3» …</summary>
        public static bool ExtractExecutionFromConfigName(string configName, out string execution, out bool isBase)
        {
            execution = null;
            isBase = false;
            if (string.IsNullOrWhiteSpace(configName)) return false;
            string t = Regex.Replace(configName.Trim(), @"<[^>]+>", "").Trim();
            t = Regex.Replace(t, @"[-_]?SM-FLAT-PATTERN.*$", "", RegexOptions.IgnoreCase).Trim();

            string[] baseNames = { "0", "00", "000", "default", "по умолчанию", "базовая", "базовое", "base", "главная", "main" };
            foreach (string b in baseNames)
            {
                if (string.Equals(t, b, StringComparison.OrdinalIgnoreCase))
                {
                    isBase = true;
                    execution = "";
                    return true;
                }
            }

            string[] patterns =
            {
                @"^(\d{1,4})$",
                @"^-(\d{1,4})$",
                @"(?:исп\.?|исполнение)\s*[-_]?\s*(\d{1,4})",
                @"^(\d{1,4})\s*[-_ \(]",
                @"[-_](\d{1,4})$"
            };
            foreach (string pattern in patterns)
            {
                Match m = Regex.Match(t, pattern, RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                int n;
                int.TryParse(m.Groups[1].Value, out n);
                if (n == 0)
                {
                    isBase = true;
                    execution = "";
                }
                else
                {
                    execution = m.Groups[1].Value.Length == 1 ? "0" + m.Groups[1].Value : m.Groups[1].Value;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Значение «Исполнение» конфигурации — как галочки MProp (FrmMProp:1439–1456, 2603–2613, 3262–3275):
        /// «1» — «Исполнение» и «Из»: номер — первое слово имени конфигурации («01» → «-01»); «2» — «Исполнение» без «Из»:
        /// номер вписан (конфигурация «Покраска» под «01» берёт номер у родителя, из её имени его не взять); «0» — без исполнения
        /// (базовая конфигурация, или номер уже в имени файла — иначе MProp удвоит суффикс).
        /// </summary>
        public static string ExecutionFlag(string configName, string execution, bool fromConfiguration)
        {
            if (!fromConfiguration || string.IsNullOrEmpty(execution)) return "0";
            string name = (configName ?? "").Trim();
            int space = name.IndexOf(' ');
            string first = space > 0 ? name.Substring(0, space) : name;
            return string.Equals(first, execution, StringComparison.Ordinal) ? "1" : "2";
        }

        public static string Build(string root, string execution, string docCode)
        {
            if (string.IsNullOrWhiteSpace(root)) return "";
            string s = string.IsNullOrWhiteSpace(execution) ? root : root + "-" + execution;
            if (!string.IsNullOrWhiteSpace(docCode)) s = s + " " + docCode.Trim();
            return s;
        }
    }

    public enum Provenance
    {
        /// <summary>Значения нет или это выражение/имя шаблона — значение производное, писать.</summary>
        Template,
        /// <summary>Значение совпадает с разбором прежнего имени файла — производное, переписать.</summary>
        DerivedFromPrevious,
        /// <summary>Значение уже соответствует имени файла.</summary>
        Current,
        /// <summary>Значение введено вручную (RenameSWP = 1 или не совпадает с именем файла) — не трогать.</summary>
        Manual
    }

    /// <summary>
    /// Правило происхождения обозначения и наименования (план, §3.4): надстройка переписывает
    /// только производные значения и никогда — введённые вручную.
    /// </summary>
    public static class ProvenanceRule
    {
        public static Provenance Classify(string currentRaw, string expected, string expectedFromPrevious, bool manualFlag)
        {
            if (manualFlag) return Provenance.Manual;
            string cur = currentRaw == null ? "" : currentRaw.Trim();
            if (cur.Length == 0 || cur.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                DesignationParser.IsTemplateName(cur))
            {
                return Provenance.Template;
            }
            if (!string.IsNullOrEmpty(expected) && string.Equals(cur, expected.Trim(), StringComparison.Ordinal))
            {
                return Provenance.Current;
            }
            if (!string.IsNullOrEmpty(expectedFromPrevious) &&
                string.Equals(cur, expectedFromPrevious.Trim(), StringComparison.Ordinal))
            {
                return Provenance.DerivedFromPrevious;
            }
            return Provenance.Manual;
        }

        public static bool ShouldWrite(Provenance p)
        {
            return p == Provenance.Template || p == Provenance.DerivedFromPrevious;
        }
    }
}
