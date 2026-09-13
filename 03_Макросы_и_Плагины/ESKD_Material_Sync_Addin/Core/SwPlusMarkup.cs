using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Разметка значений для граф основной надписи: наименование, масса, материал.
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

        /// <summary>Начало дроби материала, которую пишет надстройка (так же писала v5); MProp пишет «&lt;FONT size=1.8&gt; &lt;FONT size=3.5&gt;» с пробелом.</summary>
        public const string GeneratedMaterialMarkup = "<FONT size=1.8><FONT size=3.5><STACK size=1>";

        /// <summary>Дробь «сортамент / марка» для графы 3 из двух частей.</summary>
        public static string MaterialFraction(string top, string bottom)
        {
            if (string.IsNullOrWhiteSpace(top) || string.IsNullOrWhiteSpace(bottom))
            {
                return ((top ?? "") + (bottom ?? "")).Trim();
            }
            return string.Format(" " + GeneratedMaterialMarkup + "{0}<OVER>{1}</STACK>", top.Trim(), bottom.Trim());
        }

        /// <summary>
        /// «Материал_ФБ» или «Материал_Таблица», которые надстройка вправе переписать: пусто, выражение шаблона ($PRP),
        /// дробь в разметке надстройки или строка без разметки (так надстройка пишет материал вне библиотеки).
        /// Выражение SW-Material, «-», «См. таблицу», дробь или текст MProp и любая другая разметка — значения
        /// пользователя (Д-32): MProp выбирает сортамент и марку независимо от материала SolidWorks.
        /// </summary>
        public static bool IsDerivedMaterial(string raw)
        {
            if (raw == null) return true;
            string t = raw.Trim();
            if (t.Length == 0) return true;
            if (t.IndexOf("SW-Material", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (t.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t == "-" || t.IndexOf("См.", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (t.StartsWith(GeneratedMaterialMarkup, StringComparison.Ordinal)) return true;
            return !HasMarkup(t);
        }

        /// <summary>Материал, введённый пользователем текстом или дробью (не выражение, не «-» и не «См. таблицу»).</summary>
        public static bool IsManualMaterialText(string raw)
        {
            if (IsDerivedMaterial(raw)) return false;
            string t = raw.Trim();
            return t.IndexOf("SW-Material", StringComparison.OrdinalIgnoreCase) < 0 && !PlainText(t).Equals("-") &&
                   t.IndexOf("См.", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>Текст значения без разметки SolidWorks и косой черты дроби — для сравнения материалов.</summary>
        public static string PlainText(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string s = Regex.Replace(value, "<[^>]*>", " ").Replace("/", " ");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static string MaterialForStamp(string gostDesignation, string sortament, string gradeLine)
        {
            bool sortamentConflict;
            return MaterialForStamp(gostDesignation, sortament, gradeLine, out sortamentConflict);
        }

        /// <summary>
        /// Приведение поля «Обозначение_ГОСТ» библиотеки материалов к разметке графы 3.
        /// Вход может быть уже готовой дробью, дробью без тегов шрифта или строкой «Лист … / Ст3 …».
        /// Числитель дроби сверяется с полем «Сортамент»: при расхождении (запись скопирована у соседнего
        /// типоразмера, Д-27) в числитель идёт «Сортамент», а sortamentConflict = true.
        /// </summary>
        public static string MaterialForStamp(string gostDesignation, string sortament, string gradeLine, out bool sortamentConflict)
        {
            sortamentConflict = false;
            if (string.IsNullOrWhiteSpace(gostDesignation))
            {
                if (!string.IsNullOrEmpty(sortament) && !string.IsNullOrEmpty(gradeLine))
                    return MaterialFraction(sortament, gradeLine);
                return "";
            }
            string m = gostDesignation.Trim();
            int over = m.IndexOf("<OVER>", StringComparison.OrdinalIgnoreCase);
            if (over >= 0)
            {
                if (m.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    m.IndexOf("<FONT", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return " " + m.TrimStart();
                }
                int stackOpen = m.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase);
                int stackClose = m.IndexOf("</STACK>", StringComparison.OrdinalIgnoreCase);
                string prefix = stackOpen > 0 ? m.Substring(0, stackOpen).Trim() : "";
                int topStart = 0;
                if (stackOpen >= 0)
                {
                    int tagEnd = m.IndexOf('>', stackOpen);
                    topStart = tagEnd >= 0 && tagEnd < over ? tagEnd + 1 : over;
                }
                string top = m.Substring(topStart, over - topStart).Trim();
                int botStart = over + "<OVER>".Length;
                int botEnd = stackClose > botStart ? stackClose : m.Length;
                string bottom = m.Substring(botStart, botEnd - botStart).Trim();
                string fullTop = prefix.Length > 0 ? (prefix + " " + top).Trim() : top;
                if (!string.IsNullOrWhiteSpace(sortament) && CollapseSpaces(fullTop) != CollapseSpaces(sortament))
                {
                    sortamentConflict = true;
                    fullTop = sortament.Trim();
                }
                return MaterialFraction(fullTop, bottom);
            }
            if (m.StartsWith("<", StringComparison.Ordinal)) return " " + m;
            int slash = m.IndexOf('/');
            if (slash > 0) return MaterialFraction(m.Substring(0, slash), m.Substring(slash + 1));
            return m;
        }

        private static string CollapseSpaces(string text)
        {
            return Regex.Replace(text.Trim(), @"\s+", " ");
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
