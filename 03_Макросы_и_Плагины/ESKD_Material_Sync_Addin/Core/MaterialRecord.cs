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
        public const string FractionFont = "<FONT size=1.8> <FONT size=3.5>";

        /// <summary>Разметка MProp перед материалом в одну строку: пустая мелкая строка сверху центрирует текст в графе 3.</summary>
        public const string LineFont = "<FONT size=1.8> \n<FONT size=3.5>";

        /// <summary>Начало дроби, которую писали надстройки до этого решения (v5 и v6.0).</summary>
        public const string LegacyFractionMarkup = "<FONT size=1.8><FONT size=3.5><STACK size=1>";

        public string Stamp = "";
        public string Table = "";
        public string Line = "";

        public static MaterialRecord FromLibrary(MaterialInfo info)
        {
            if (info == null) return null;
            string designation = info.GostDesignation ?? "";
            if (designation.Trim().Length == 0) return FromName(info.Name);
            MaterialRecord r = new MaterialRecord();
            if (designation.IndexOf("<STACK", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Дословно: ведущий пробел у дробей без формы («Фанера», «Кромка») MProp пишет так же.
                r.Stamp = FractionFont + designation;
                r.Table = designation;
            }
            else
            {
                r.Stamp = LineFont + designation.Trim();
                r.Table = designation.Trim();
            }
            r.Line = string.IsNullOrWhiteSpace(info.LineDesignation) ? OneLine(designation) : info.LineDesignation.Trim();
            return r;
        }

        public static MaterialRecord FromName(string materialName)
        {
            if (string.IsNullOrWhiteSpace(materialName)) return null;
            string name = materialName.Trim();
            MaterialRecord r = new MaterialRecord();
            r.Stamp = LineFont + name;
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
        /// «Материал_ФБ» или «Материал_Таблица», которые надстройка вправе заменить: пусто, выражение шаблона, запись системы
        /// (isSystemRecord: представление любого известного материала; формат прежних версий надстройки) или строка без
        /// разметки. Выражение SW-Material, «-», «См. таблицу», дробь или текст, введённые в MProp вручную, — значения
        /// пользователя (Д-32).
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
            if (isSystemRecord != null && isSystemRecord(Normalize(raw))) return true;
            return !SwPlusMarkup.HasMarkup(t);
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
