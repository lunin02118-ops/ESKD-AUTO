using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Запись безчертёжной детали для спецификации (ГОСТ Р 2.106-2019, ГОСТ Р 2.109-2023):
    /// наименование, дробь «сортамент / марка материала» и размеры заготовки; масса — в «Примечании».
    /// </summary>
    public static class BchRecord
    {
        public const string FormatValue = "БЧ";
        public const string SavedFormatProperty = "Формат_до_БЧ";
        public const string SavedRemarkProperty = "Примечание_до_БЧ";

        /// <summary>Значение «Наименования» — запись БЧ: несколько строк или дробь материала.</summary>
        public static bool IsRecord(string value)
        {
            return !string.IsNullOrEmpty(value) && (value.IndexOf('\n') >= 0 || value.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string ShortTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "Деталь";
            string t = title.Replace("\r", "");
            int nl = t.IndexOf('\n');
            if (nl >= 0) t = t.Substring(0, nl);
            t = t.Trim();
            if (t.EndsWith(" БЧ", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 3).TrimEnd();
            return t.Length > 0 ? t : "Деталь";
        }

        public static bool IsProfile(string text)
        {
            string s = (text ?? "").ToLowerInvariant();
            return s.Contains("труба") || s.Contains("уголок") || s.Contains("швеллер") || s.Contains("круг") ||
                   s.Contains("полоса") || s.Contains("квадрат") || s.Contains("двутавр") || s.Contains("профиль") ||
                   s.Contains("шестигранник") || s.Contains("пруток");
        }

        public static bool IsSheet(string text)
        {
            string s = (text ?? "").ToLowerInvariant();
            return s.Contains("лист") || s.Contains("плита") || s.Contains("лента");
        }

        /// <summary>Знак умножения в размерах записи (Р-14: шрифт «GOST 2.304 type A» рисует настоящий «×»).</summary>
        public const string Times = "\u00D7";

        /// <summary>
        /// Запись БЧ, которую сделала надстройка: наименование, при материале — строка материала, последняя строка — размер
        /// заготовки «L = … мм» или «B×L мм» (прежняя буква «х» тоже). Такую запись сохранение пересобирает по модели (WP-2.7);
        /// запись, изменённую вручную, не трогает.
        /// </summary>
        public static bool IsOwnRecord(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string[] lines = value.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 2 || lines.Length > 3 || lines[0].Trim().Length == 0) return false;
            if (lines.Length == 3 && lines[1].Trim().Length == 0) return false;
            return Regex.IsMatch(lines[lines.Length - 1], "^(L = \\d+ мм|\\d+[х\u00D7]\\d+ мм)$");
        }

        /// <summary>Размер заготовки: для проката «L = …», для листа «B×L», иначе «L = …» по наибольшему габариту.</summary>
        public static string SizeText(string materialText, double[] sortedDimsMm, double lengthFromModelMm)
        {
            double max = sortedDimsMm != null && sortedDimsMm.Length == 3 ? sortedDimsMm[2] : 0;
            double mid = sortedDimsMm != null && sortedDimsMm.Length == 3 ? sortedDimsMm[1] : 0;
            double length = lengthFromModelMm > 0 ? lengthFromModelMm : max;
            if (IsSheet(materialText) && !IsProfile(materialText))
            {
                return Number(mid) + Times + Number(max) + " мм";
            }
            return length > 0 ? "L = " + Number(length) + " мм" : "";
        }

        public static string SpecTitle(string title, string sortament, string grade, string sizeText)
        {
            string head = ShortTitle(title);
            string material;
            if (!string.IsNullOrWhiteSpace(sortament) && !string.IsNullOrWhiteSpace(grade))
                material = "<STACK size=1>" + sortament.Trim() + "<OVER>" + grade.Trim() + "</STACK>";
            else
                material = ((sortament ?? "") + (grade ?? "")).Trim();
            string result = head;
            if (material.Length > 0) result += "\n" + material;
            if (!string.IsNullOrWhiteSpace(sizeText)) result += "\n" + sizeText.Trim();
            return result;
        }

        /// <summary>«Примечание», которое ведёт надстройка: выражение массы MProp с «кг»/«г» или текст массы до v6.2 («2,86 кг»).</summary>
        public static bool IsMassNote(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                   (Regex.IsMatch(value.Trim(), @"^\d+([.,]\d+)?\s*кг$") ||
                    Regex.IsMatch(value.Trim(), "^\"SW-Mass@@[^\"]*\" (кг|г)$"));
        }

        private static string Number(double mm)
        {
            return Math.Round(mm, 0).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
