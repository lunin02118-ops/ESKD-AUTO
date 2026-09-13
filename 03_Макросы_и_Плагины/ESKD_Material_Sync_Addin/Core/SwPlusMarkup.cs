using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Разметка значений для граф основной надписи: наименование и масса (материал — MaterialRecord).
    /// </summary>
    public static class SwPlusMarkup
    {
        public const int TitleLineLimit = 24;
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

        /// <summary>
        /// «Наименование_ФБ» для графы 1 (70 мм, ~24 знака): перенос по словам на две строки.
        /// Значение с разметкой или переводами строк возвращается без изменений (Д-13).
        /// </summary>
        public static string TitleForStamp(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            string t = title.Trim();
            if (HasMarkup(t)) return t;
            if (t.Length <= TitleLineLimit) return t;
            string[] words = t.Split(new[] { ' ', '\t', '\u00A0' }, StringSplitOptions.RemoveEmptyEntries);
            string line1 = "", line2 = "";
            bool second = false;
            foreach (string word in words)
            {
                if (!second)
                {
                    string candidate = line1.Length == 0 ? word : line1 + " " + word;
                    if (candidate.Length <= TitleLineLimit || line1.Length == 0)
                    {
                        line1 = candidate;
                        continue;
                    }
                    second = true;
                }
                line2 = line2.Length == 0 ? word : line2 + " " + word;
            }
            return line2.Length > 0 ? line1 + "\n" + line2 : line1;
        }

        /// <summary>Масса для графы 5: запятая, фиксированное число знаков (0–4).</summary>
        public static string MassText(double massKg, int decimals)
        {
            if (decimals < 0) decimals = 0;
            if (decimals > 4) decimals = 4;
            string format = decimals == 0 ? "0" : "0." + new string('0', decimals);
            return massKg.ToString(format, Ru);
        }

        public static string MassForStamp(double massKg, int decimals)
        {
            return "<FONT size=3.5>" + MassText(massKg, decimals);
        }

        /// <summary>Живое выражение массы, которое пишет MProp: его надстройка не трогает.</summary>
        public static bool IsLiveMass(string raw)
        {
            return !string.IsNullOrEmpty(raw) &&
                   (raw.IndexOf("SW-Mass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    raw.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>Текст массы, который писала надстройка (с тегом размера шрифта или без).</summary>
        public static bool IsGeneratedMass(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            return Regex.IsMatch(raw.Trim(), @"^(<FONT size=[0-9.,]+>\s*)*\d+(,\d+)?$", RegexOptions.IgnoreCase);
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
