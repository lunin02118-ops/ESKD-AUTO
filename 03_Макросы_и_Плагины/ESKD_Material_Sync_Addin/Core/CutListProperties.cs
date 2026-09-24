using System;
using System.Collections.Generic;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Имена свойств папки списка вырезов. SolidWorks заводит их сам и называет на языке интерфейса: в русском
    /// это «ДЛИНА» и «КОЛИЧЕСТВО», в английском — LENGTH и QUANTITY. Искать по английскому имени нельзя —
    /// у владельца русский SolidWorks, и длина заготовки не находилась вовсе: книга ЛЗК брала наибольший
    /// размер габаритного ящика и помечала строку звёздочкой «оценка» (замечание владельца 21.09.2026).
    ///
    /// Надёжный признак — не имя, а значение: SolidWorks пишет туда ссылку вида «LENGTH@@@&lt;папка&gt;@&lt;файл&gt;»,
    /// и левая часть ссылки всегда английская, на каком бы языке ни был интерфейс.
    /// </summary>
    public static class CutListProperties
    {
        /// <summary>
        /// Число из свойства списка вырезов: вычисленное значение, а записанное — только если это не ссылка SolidWorks
        /// («LENGTH@@@Элемент списка вырезов1@…»), то есть вписано руками. Из ссылки выходило 1 — номер папки (или 40 из
        /// «Труба 40х20…»), и это «1 мм» шло в «Расход» и в признак профиля выгрузки, а длина по телу уже не мерилась
        /// (сверка SW API 23.09.2026, №35). NaN — числа нет.
        /// </summary>
        public static double Number(string raw, string resolved)
        {
            double v = IsLink(resolved) ? double.NaN : LzkOperations.ParseNumber(resolved);
            if (!double.IsNaN(v)) return v;
            return IsLink(raw) ? double.NaN : LzkOperations.ParseNumber(raw);
        }

        /// <summary>Значение — ссылка SolidWorks на величину («…@@@…»), а не само число.</summary>
        public static bool IsLink(string value)
        {
            return (value ?? "").IndexOf("@@@", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Длина заготовки: то, что уйдёт в «Расход» книги ЛЗК.</summary>
        public static readonly string[] LengthSpellings = { "LENGTH", "ДЛИНА" };

        /// <summary>Сколько таких заготовок в папке.</summary>
        public static readonly string[] QuantitySpellings = { "QUANTITY", "КОЛИЧЕСТВО" };

        /// <summary>
        /// Развёртка листовой детали: длина и ширина прямоугольника заготовки (SolidWorks считает сам). В русском
        /// SolidWorks 2025 и имя, и ссылка внутри значения русские («SW-Длина граничной рамки@@@…»), поэтому здесь
        /// ищется по имени; английское имя — для английской сборки.
        /// </summary>
        public static readonly string[] BoundingBoxLengthSpellings = { "Bounding Box Length", "Длина граничной рамки", "Длина ограничивающего прямоугольника" };
        public static readonly string[] BoundingBoxWidthSpellings = { "Bounding Box Width", "Ширина граничной рамки", "Ширина ограничивающего прямоугольника" };
        public static readonly string[] SheetThicknessSpellings = { "Sheet Metal Thickness", "Толщина листового металла" };

        /// <summary>
        /// Имя свойства, в котором лежит величина link, или пустая строка. properties — пары «имя → записанное
        /// значение» (raw, а не вычисленное: ссылка видна только в записанном).
        /// </summary>
        public static string Find(IEnumerable<KeyValuePair<string, string>> properties, string link, string[] spellings)
        {
            if (properties == null || string.IsNullOrEmpty(link)) return "";
            string prefix = link + "@@@";
            // Величины развёртки SolidWorks связывает с префиксом «SW-»: «SW-Bounding Box Length@@@…».
            string swPrefix = "SW-" + prefix;
            string byName = "";
            foreach (KeyValuePair<string, string> pair in properties)
            {
                string name = (pair.Key ?? "").Trim();
                if (name.Length == 0) continue;
                string value = Unquote(pair.Value);
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith(swPrefix, StringComparison.OrdinalIgnoreCase)) return name;
                if (byName.Length == 0 && spellings != null)
                {
                    foreach (string spelling in spellings)
                        if (string.Equals(name, spelling, StringComparison.OrdinalIgnoreCase)) { byName = name; break; }
                }
            }
            // Ссылки нет — свойство вписали руками; тогда годится имя на любом из известных языков.
            return byName;
        }

        /// <summary>Записанное значение связанного свойства SolidWorks берёт в кавычки: «"LENGTH@@@…"».</summary>
        private static string Unquote(string raw)
        {
            string s = (raw ?? "").Trim();
            return s.StartsWith("\"", StringComparison.Ordinal) ? s.Substring(1) : s;
        }
    }
}
