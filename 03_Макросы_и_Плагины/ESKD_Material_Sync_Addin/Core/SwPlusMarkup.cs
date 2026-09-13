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

        /// <summary>Живое выражение массы, которое пишет MProp в «Масса_ФБ»: его надстройка не трогает.</summary>
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

        private const string LiveMassHead = "\"SW-Mass@@";

        /// <summary>
        /// Выражение массы конфигурации, которое MProp пишет в «Масса_Таблица»: SolidWorks считает по нему массу
        /// неактивной конфигурации без её активации, имя файла ставит своё и меняет при «Сохранить как» (контракт C05, C06).
        /// </summary>
        public static string LiveMassExpression(string configuration, string fileName)
        {
            return LiveMassHead + configuration + "@" + fileName + "\"";
        }

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

        /// <summary>
        /// Масса из вычисленного выражения «SW-Mass»: число с точкой в единицах массы документа (swUnitsMassPropMass_e:
        /// 1 — мг, 2 — г, 3 — кг, 4 — фунт) с точностью документа. Возвращает массу в килограммах и число знаков после
        /// запятой, которое значение даёт в килограммах.
        /// </summary>
        public static bool TryParseResolvedMass(string resolved, int massUnit, out double kg, out int decimals)
        {
            kg = 0;
            decimals = 0;
            if (string.IsNullOrEmpty(resolved)) return false;
            string text = Regex.Replace(resolved, "<[^>]*>", "").Trim();
            Match m = Regex.Match(text, @"^(\d{1,3}(?:[ ,\u00A0]\d{3})+|\d+)(?:\.(\d+))?$");
            if (!m.Success) return false;
            string number = Regex.Replace(m.Groups[1].Value, @"[ ,\u00A0]", "");
            string fraction = m.Groups[2].Success ? m.Groups[2].Value : "";
            double value;
            if (!double.TryParse(fraction.Length > 0 ? number + "." + fraction : number, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out value))
                return false;
            decimals = fraction.Length;
            switch (massUnit)
            {
                case 1: kg = value / 1000000.0; decimals += 6; break;
                case 2: kg = value / 1000.0; decimals += 3; break;
                case 4: kg = value * 0.45359237; break;
                default: kg = value; break;
            }
            return true;
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
