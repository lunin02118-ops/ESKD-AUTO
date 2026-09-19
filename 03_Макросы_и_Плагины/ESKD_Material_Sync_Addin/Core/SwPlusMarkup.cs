using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Распознавание значений граф основной надписи (строки формата — SwPlusFormat, материал — MaterialRecord).
    /// </summary>
    public static class SwPlusMarkup
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        /// <summary>Есть ли в значении разметка SolidWorks или перевод строки — такие значения не переформатируются.</summary>
        public static bool HasMarkup(string value)
        {
            return !string.IsNullOrEmpty(value) && (value.IndexOf('<') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0);
        }

        /// <summary>Живое выражение массы, которое пишет MProp в «Масса_ФБ»: его надстройка не трогает.</summary>
        public static bool IsLiveMass(string raw)
        {
            return !string.IsNullOrEmpty(raw) &&
                   (raw.IndexOf("SW-Mass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    raw.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>Текст массы с запятой, который писала надстройка до v6.2 (с тегом размера шрифта или без) — переводится в формат MProp.</summary>
        public static bool IsGeneratedMass(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            return Regex.IsMatch(raw.Trim(), @"^(<FONT size=[0-9.,]+>\s*)*\d+(,\d+)?$", RegexOptions.IgnoreCase);
        }

        /// <summary>«Масса_Таблица», которую пишет система: пусто, выражение шаблона или выражение «"SW-Mass@@…"» (конфигурация, имя файла и регистр расширения приводятся к MProp).</summary>
        public static bool IsSystemMassTable(string raw)
        {
            if (raw == null || raw.Trim().Length == 0) return true;
            string t = raw.Trim();
            if (t.IndexOf("SW-Mass@@", StringComparison.OrdinalIgnoreCase) >= 0)
                return Regex.IsMatch(t, "^\"SW-Mass@@[^\"]*\"$");
            return t.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>«Масса_ФБ», которую пишет система: пусто, выражение шаблона, текст надстройки до v6.2 или выражение MProp; «-» и «См. таблицу» — нет.</summary>
        public static bool IsSystemMass(string raw)
        {
            if (raw == null || raw.Trim().Length == 0) return true;
            string plain = MaterialRecord.OneLine(raw);
            if (plain == "-" || plain.IndexOf("См.", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (IsGeneratedMass(raw)) return true;
            string t = MaterialRecord.Normalize(raw);
            string body = t.StartsWith(SwPlusFormat.MassPrefix, StringComparison.Ordinal) ? t.Substring(SwPlusFormat.MassPrefix.Length) : t.Trim();
            if (Regex.IsMatch(body, "^\"SW-Mass@@[^\"]*\"( г)?$")) return true;
            return raw.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0 && raw.IndexOf("SW-Mass", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>«Сборка2_ФБ», которую писала надстройка до v6.2: «Сборочный чертёж» без разметки.</summary>
        public static bool IsLegacyDocDescription(string value)
        {
            if (value == null) return false;
            string t = value.Trim();
            return t == "Сборочный чертёж" || t == SwPlusFormat.AssemblyDrawingText;
        }

        private const string LiveMassHead = "\"SW-Mass@@";

        /// <summary>Конфигурация из выражения «"SW-Mass@@конфигурация@файл"» этого файла; для другого значения — null.</summary>
        public static string LiveMassConfiguration(string raw, string fileName)
        {
            if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(fileName)) return null;
            string t = raw.Trim();
            string tail = "@" + fileName + "\"";
            if (t.Length <= LiveMassHead.Length + tail.Length || !t.StartsWith(LiveMassHead, StringComparison.OrdinalIgnoreCase) ||
                !t.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                return null;
            return t.Substring(LiveMassHead.Length, t.Length - LiveMassHead.Length - tail.Length);
        }
    }
}
