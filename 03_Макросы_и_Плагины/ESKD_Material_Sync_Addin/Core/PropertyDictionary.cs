using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Роли свойств в порядке строк словаря SWPlus SpecEditor/MyProperties_1.ini
    /// (тот же порядок читает MProp: prpNumber, prpDocCode, …, prpQuantity).
    /// </summary>
    public enum Role
    {
        Number = 0, DocCode, DocDescription, Description, DescriptionMulti, Code, Format, Remark, Lit, LitTable,
        Firm, Section, Group, Designer, Tester, Techcontrol, WorkType, Person, Normcontrol, Approve,
        Mass, MassTable, Material, MaterialTable, FirstApply, InformNumber, FirstApplySP, InformNumberSP, LitSP,
        InformNumberVP, DescriptionVP, ProductCodeVP, NumberDocVP, VendorVP, RemarkVP,
        Project, DraftNumber, DraftDescription, DraftFirstApply, DraftFirstApplySP, Blank, Bor, Quantity
    }

    /// <summary>
    /// Словарь имён свойств SWPlus — единственный источник имён для надстройки.
    /// </summary>
    public sealed class PropertyDictionary
    {
        public const int RoleCount = 43;

        public static readonly string[] DefaultNames = new string[]
        {
            "Обозначение", "Сборка1_ФБ", "Сборка2_ФБ", "Наименование", "Наименование_ФБ", "Код_ФБ", "Формат",
            "Примечание", "Литера_ФБ", "Литера_Таблица", "Контора", "Раздел", "Группа", "Конструктор", "Проверил",
            "Техконтроль", "Характер_работы", "Начальник", "Нормоконтроль", "Утвердил", "Масса_ФБ", "Масса_Таблица",
            "Материал_ФБ", "Материал_Таблица", "Проект", "Справочный_номер", "Первичное_применение_SP",
            "Справочный_номер_SP", "Литера_SP", "Справочный_номер_ВП", "Наименование_ВП", "Код_Продукции",
            "Обозначение_ДНП", "Поставщик", "Примечание_ВП", "Проект_ФБ", "Обозначение2", "Наименование2",
            "Применение2", "Первичное_применение_SP2", "Заготовка", "Заимствование", "Количество"
        };

        /// <summary>Служебные свойства SWPlus вне словаря: надстройка их читает, но не удаляет.</summary>
        public static readonly string[] SwPlusServiceNames = new string[]
        {
            "Number", "Description", "RenameSWP", "Исполнение", "Классификатор", "Формат_ФБ", "Плотность_ФБ",
            "Сборка", "Удален", "Revision", "IsFastener", "UNIT_OF_MEASURE", "Обработка", "CheckFormat",
            "Единицы", "MARKA_MATERIAL", "MATERIAL_MIS", "SHAPE", "SORTAMENT"
        };

        /// <summary>
        /// Свойства, которые ведёт только надстройка (в словаре SWPlus их нет): однострочная запись материала для сводной
        /// ведомости, прежние «Формат» и «Примечание» на время режима «Деталь БЧ» и строки записи БЧ для спецификации.
        /// </summary>
        public static readonly string[] AddinNames = new string[]
        {
            MaterialRecord.LineProperty, BchRecord.SavedFormatProperty, BchRecord.SavedRemarkProperty, BchRecord.LinesProperty
        };

        /// <summary>
        /// Доп. свойства словаря SWPlus — строки 51 и 53 файла `MyProperties_1.ini` (ТЗ-02 Т-15). Их знает и MProp
        /// (поля LblAddPRP1/2), и надстройка: «Операции» ставит конструктор в окне ведомости, «Ревизия» — кнопка
        /// «Новая ревизия» у безчертёжной детали, у которой штампа с таблицей изменений нет.
        /// </summary>
        public const string OperationsName = "Операции";
        public const string RevisionName = "Ревизия";
        public static readonly string[] ExtraNames = new string[] { OperationsName, RevisionName };

        /// <summary>Общие свойства шаблона детали с живыми выражениями «"SW-Mass"» и «"SW-Material"»: надстройка их не пишет, очистка v5 возвращает выражения.</summary>
        public static readonly string[] TemplateNames = new string[] { "Масса", "Материал" };

        /// <summary>Имена, которые писала надстройка v5 и которые никто не читает (класс «лишние»).</summary>
        public static readonly string[] LegacyExtraNames = new string[]
        {
            "Разраб.", "Разработал", "Автор", "п_Разраб", "DrawnBy", "п_Разраб_Дата", "DrawnDate",
            "Пров.", "п_Пров", "CheckedBy", "п_Пров_Дата",
            "Организация", "Организация_ФБ", "Компания", "Firm", "Organization",
            "PartNo", "Сортамент", "ГОСТ_Сортамент", "ГОСТ_Материал", "БЧ"
        };

        private readonly string[] _names;

        public string SourcePath { get; private set; }
        public bool FileNameSplit { get; private set; }
        public string NameSeparator { get; private set; }
        public bool SmallFontMarkup { get; private set; }

        private PropertyDictionary(string[] names, string source, bool split, string separator, bool smallFont)
        {
            _names = names;
            SourcePath = source;
            FileNameSplit = split;
            NameSeparator = separator;
            SmallFontMarkup = smallFont;
        }

        public string this[Role role]
        {
            get { return _names[(int)role]; }
        }

        public static PropertyDictionary Default()
        {
            return new PropertyDictionary((string[])DefaultNames.Clone(), "", true, " ", true);
        }

        /// <summary>Чтение MyProperties_1.ini (Windows-1251). При ошибке — словарь по умолчанию и предупреждение в журнале.</summary>
        public static PropertyDictionary Load(string iniPath)
        {
            if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath))
                return Fallback("Словарь SWPlus MyProperties_1.ini не найден" + (string.IsNullOrEmpty(iniPath) ? "" : ": " + iniPath));
            try
            {
                string[] lines = File.ReadAllLines(iniPath, Encoding.GetEncoding(1251));
                if (lines.Length < RoleCount)
                    return Fallback(string.Format("В словаре SWPlus {0} строк вместо {1}: {2}", lines.Length, RoleCount, iniPath));
                string[] names = new string[RoleCount];
                for (int i = 0; i < RoleCount; i++)
                {
                    string n = lines[i].Trim();
                    names[i] = n.Length > 0 ? n : DefaultNames[i];
                }
                bool split = Line(lines, 47) == "1";
                string sep = lines.Length > 48 ? lines[48] : " ";
                if (string.IsNullOrEmpty(sep)) sep = " ";
                bool smallFont = Line(lines, 49) == "1";
                return new PropertyDictionary(names, iniPath, split, sep, smallFont);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Словарь SWPlus не прочитан (" + ex.Message + "): " + iniPath + FallbackNote);
                return Default();
            }
        }

        private const string FallbackNote = " — используются стандартные имена свойств SWPlus";

        private static PropertyDictionary Fallback(string reason)
        {
            Log.WarnOnce(reason + FallbackNote);
            return Default();
        }

        private static string Line(string[] lines, int index)
        {
            return index < lines.Length ? lines[index].Trim() : "";
        }

        /// <summary>Поиск словаря рядом с надстройкой: .../03_Макросы_и_Плагины/Макросы_SW_ZTool/…</summary>
        public static string Locate(string addinDirectory, string overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;
            string relative = Path.Combine("Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "SpecEditor", "MyProperties_1.ini");
            string cur = addinDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
            {
                string c1 = Path.Combine(cur, relative);
                if (File.Exists(c1)) return c1;
                string c2 = Path.Combine(cur, "03_Макросы_и_Плагины", relative);
                if (File.Exists(c2)) return c2;
                DirectoryInfo parent = Directory.GetParent(cur);
                cur = parent != null ? parent.FullName : null;
            }
            return null;
        }

        public bool IsDictionaryName(string name)
        {
            foreach (string n in _names)
            {
                if (string.Equals(n, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static bool IsLegacyExtra(string name)
        {
            return Array.IndexOf(LegacyExtraNames, name) >= 0;
        }

        public IList<string> Names
        {
            get { return Array.AsReadOnly(_names); }
        }
    }
}
