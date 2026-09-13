using System;
using System.Globalization;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Запись безчертёжной детали для спецификации (ГОСТ 2.106, ГОСТ 2.109-73, черт. 40):
    /// наименование, дробь «сортамент / марка материала» и размеры заготовки; масса — в «Примечании».
    /// </summary>
    public static class BchRecord
    {
        public const string FormatValue = "БЧ";
        public const string SavedFormatProperty = "Формат_до_БЧ";
        public const string SavedRemarkProperty = "Примечание_до_БЧ";

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

        /// <summary>Размер заготовки: для проката «L = …», для листа «B×L», иначе «L = …» по наибольшему габариту.</summary>
        public static string SizeText(string materialText, double[] sortedDimsMm, double lengthFromModelMm)
        {
            double max = sortedDimsMm != null && sortedDimsMm.Length == 3 ? sortedDimsMm[2] : 0;
            double mid = sortedDimsMm != null && sortedDimsMm.Length == 3 ? sortedDimsMm[1] : 0;
            double length = lengthFromModelMm > 0 ? lengthFromModelMm : max;
            if (IsSheet(materialText) && !IsProfile(materialText))
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}х{1} мм", Number(mid), Number(max));
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

        /// <summary>Масса для «Примечания» БЧ: «2,86 кг». Масса берётся из модели, а не из разметки (Д-14).</summary>
        public static string MassNote(double massKg, int decimals)
        {
            return SwPlusMarkup.MassText(massKg, decimals) + " кг";
        }

        public static bool IsMassNote(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                   System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^\d+([.,]\d+)?\s*кг$");
        }

        private static string Number(double mm)
        {
            return Math.Round(mm, 0).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
