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

        private static readonly string[] BlankShapes =
        {
            "Лист", "Труба", "Круг", "Швеллер", "Уголок", "Полоса", "Квадрат", "Шестигранник", "Профиль", "Лента",
            "Двутавр", "Проволока", "Пруток", "Фольга", "Плита", "Сетка", "Рельс"
        };

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

        /// <summary>Форма заготовки из начала строки сортамента: «Труба», «Лист» …</summary>
        public static string BlankShape(string sortament)
        {
            if (string.IsNullOrWhiteSpace(sortament)) return "";
            string t = sortament.Trim();
            foreach (string s in BlankShapes)
            {
                if (t.StartsWith(s + " ", StringComparison.OrdinalIgnoreCase) || string.Equals(t, s, StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            return "";
        }
    }
}
