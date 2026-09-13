using System;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Материал конфигурации в трёх представлениях (решение владельца 13.09.2026), все — из библиотеки материалов:
    ///  * «Материал_ФБ» — графа 3 основной надписи: поле «Обозначение_ГОСТ» дословно в разметке MProp — форма перед
    ///    дробью «сортамент / марка», ровно так пишет MProp в режиме «Сортамент»;
    ///  * «Материал_Таблица» — то же обозначение без шрифтовой разметки: его разбирает форма MProp;
    ///  * «Материал_Строка» — однострочная запись для сводной ведомости материалов (поле «Обозначение_Строка»).
    /// Материал вне библиотеки — имя материала SolidWorks, в графе 3 одной строкой.
    /// </summary>
    public sealed class MaterialRecord
    {
        /// <summary>Однострочная запись материала конфигурации для сводной ведомости; свойство ведёт только надстройка.</summary>
        public const string LineProperty = "Материал_Строка";

        /// <summary>Разметка MProp (prpFontSize = 1) перед дробью.</summary>
        public const string FractionFont = SwPlusFormat.MaterialFraction;

        /// <summary>Разметка MProp перед материалом в одну строку: пустая мелкая строка сверху центрирует текст в графе 3.</summary>
        public const string LineFont = SwPlusFormat.MaterialLine;

        /// <summary>Начало дроби, которую писали надстройки до этого решения (v5 и v6.0).</summary>
        public const string LegacyFractionMarkup = "<FONT size=1.8><FONT size=3.5><STACK size=1>";

        public string Stamp = "";
        public string Table = "";
        public string Line = "";

        /// <summary>Запись корпоративной библиотеки ЕСКД (у материала есть «Обозначение_ГОСТ»), а не имя материала.</summary>
        public bool IsLibrary;

        /// <param name="smallFont">Флаг словаря prpFontSize (строка 50): при 0 MProp пишет графу 3 без тегов FONT.</param>
        public static MaterialRecord FromLibrary(MaterialInfo info, bool smallFont = true)
        {
            if (info == null) return null;
            string designation = info.GostDesignation ?? "";
            if (designation.Trim().Length == 0) return FromName(info.Name, smallFont);
            MaterialRecord r = new MaterialRecord();
            r.IsLibrary = true;
            if (designation.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Дословно: ведущий пробел у дробей без формы («Фанера», «Кромка») MProp пишет так же.
                r.Stamp = (smallFont ? FractionFont : "") + designation;
                r.Table = designation;
            }
            else
            {
                r.Stamp = (smallFont ? LineFont : "") + designation.Trim();
                r.Table = designation.Trim();
            }
            r.Line = string.IsNullOrWhiteSpace(info.LineDesignation) ? OneLine(designation) : info.LineDesignation.Trim();
            return r;
        }

        public static MaterialRecord FromName(string materialName, bool smallFont = true)
        {
            if (string.IsNullOrWhiteSpace(materialName)) return null;
            string name = materialName.Trim();
            MaterialRecord r = new MaterialRecord();
            r.Stamp = (smallFont ? LineFont : "") + name;
            r.Table = name;
            r.Line = name;
            return r;
        }

        /// <summary>Значение одной строкой: дробь как «числитель / знаменатель», без разметки и лишних пробелов.</summary>
        public static string OneLine(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string s = Regex.Replace(value, "<OVER>", " / ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]*>", " ");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        /// <summary>SolidWorks хранит перевод строки как CR LF, а пишется LF: сравнения идут по нормализованному тексту.</summary>
        public static string Normalize(string value)
        {
            return value == null ? null : value.Replace("\r\n", "\n");
        }

        /// <summary>
        /// «Материал_ФБ» или «Материал_Таблица», которые надстройка вправе заменить: пусто, выражение шаблона, формат прежних
        /// версий надстройки или запись системы — isSystemRecord: представление, имя или однострочная запись известного
        /// материала (v5 писала имя материала без разметки). Всё остальное — значение пользователя: выражение SW-Material,
        /// «-», «См. таблицу», дробь MProp (Д-32) и текст, набранный вручную, даже без разметки (Д-37).
        /// </summary>
        public static bool IsSystemValue(string raw, Func<string, bool> isSystemRecord)
        {
            if (raw == null || raw.Trim().Length == 0) return true;
            string t = raw.Trim();
            if (t.IndexOf("SW-Material", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (t.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string plain = OneLine(t);
            if (plain == "-" || plain.IndexOf("См.", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (t.StartsWith(LegacyFractionMarkup, StringComparison.Ordinal)) return true;
            return isSystemRecord != null && (isSystemRecord(Normalize(raw)) || isSystemRecord(t));
        }

        /// <summary>
        /// Признак записи системы для конфигурации: запись любой известной библиотеки (catalog) или представление материала
        /// этой конфигурации — имя (v5 писала имя материала без разметки, в том числе материала вне библиотек), графа 3,
        /// таблица и однострочная запись.
        /// </summary>
        public static Func<string, bool> SystemRecordOf(Func<string, bool> catalog, string materialName, MaterialRecord current)
        {
            return value =>
            {
                if (value == null) return false;
                if (catalog != null && catalog(value)) return true;
                string t = Normalize(value).Trim();
                if (t.Length == 0) return false;
                if (!string.IsNullOrEmpty(materialName) && t == materialName.Trim()) return true;
                return current != null && (t == Normalize(current.Stamp).Trim() || t == current.Table.Trim() || t == current.Line.Trim());
            };
        }

        /// <summary>Значение графы 3, которое остаётся за пользователем при любом материале: выражение SW-Material, «-», «См. таблицу».</summary>
        public static bool IsReservedValue(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            if (raw.IndexOf("SW-Material", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string plain = OneLine(raw);
            return plain == "-" || plain.IndexOf("См.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Заменить ли значение «Материал_ФБ» или «Материал_Таблица» записью материала конфигурации (решение владельца 13.09.2026):
        /// материал из библиотеки ЕСКД — единственный источник, его запись заменяет всё, кроме выражения SW-Material, «-» и
        /// «См. таблицу», в том числе дробь MProp и набранный текст; запись материала вне библиотеки заменяет только значения
        /// системы — ручная дробь и текст остаются (Д-32, Д-37).
        /// </summary>
        public static bool ShouldReplace(string raw, MaterialRecord record, Func<string, bool> isSystemRecord)
        {
            if (record == null) return false;
            return record.IsLibrary ? !IsReservedValue(raw) : IsSystemValue(raw, isSystemRecord);
        }

        /// <summary>Материал, введённый пользователем текстом или дробью: не выражение, не «-» и не «См. таблицу».</summary>
        public static bool IsManualText(string raw, Func<string, bool> isSystemRecord)
        {
            if (IsSystemValue(raw, isSystemRecord)) return false;
            string plain = OneLine(raw);
            return plain.Length > 0 && plain != "-" && raw.IndexOf("SW-Material", StringComparison.OrdinalIgnoreCase) < 0 &&
                   plain.IndexOf("См.", StringComparison.OrdinalIgnoreCase) < 0;
        }
    }
}
