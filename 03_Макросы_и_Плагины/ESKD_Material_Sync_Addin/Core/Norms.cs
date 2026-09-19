using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Нормативы производства из справочника `Нормативы_производства.xlsx`, лист «Нормативы» (ТЗ-02 Т-13).
    /// Встроенных «запасных» значений нет намеренно: на разных ПК цифры разойдутся, и цех получит заявку,
    /// посчитанную не по тем нормам. Файла нет или он занят — кнопка объясняет это и останавливается.
    /// </summary>
    public sealed class Norms
    {
        public const string FileName = "Нормативы_производства.xlsx";
        public const string SheetName = "Нормативы";

        /// <summary>Ключи, без которых заявку не посчитать.</summary>
        public static readonly string[] Required =
        {
            "Труба.Хлыст", "Труба.Захват", "Труба.Торцовка", "Труба.Рез", "Труба.Деловой",
            "Лист.Формат", "Лист.Отход", "Краска.Норма", "Краска.Потери", "Краска.Тара"
        };

        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Откуда прочитаны нормативы — печатается в указаниях листа 5 (Т-40).</summary>
        public string SourcePath { get; private set; }

        private Norms(string path)
        {
            SourcePath = path ?? "";
        }

        /// <summary>Значения как есть: их печатает окно выдачи и указания листа 5.</summary>
        public IEnumerable<KeyValuePair<string, string>> All
        {
            get { return Required.Where(_values.ContainsKey).Select(k => new KeyValuePair<string, string>(k, _values[k])); }
        }

        public string Text(string key)
        {
            string value;
            return _values.TryGetValue(key ?? "", out value) ? value : "";
        }

        /// <summary>Число по ключу; значение «1250x2500» даёт первое число.</summary>
        public double Number(string key)
        {
            return ParseNumber(Text(key));
        }

        /// <summary>Пара чисел «1250x2500» (или «1250х2500» кириллицей) — формат листа.</summary>
        public void Pair(string key, out double first, out double second)
        {
            string[] parts = Text(key).Split(new[] { 'x', 'X', 'х', 'Х', '*', '×' }, StringSplitOptions.RemoveEmptyEntries);
            first = parts.Length > 0 ? ParseNumber(parts[0]) : 0;
            second = parts.Length > 1 ? ParseNumber(parts[1]) : 0;
        }

        /// <summary>Правка на один заказ (Т-13): справочник при этом не меняется.</summary>
        public void Override(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(key)) _values[key.Trim()] = (value ?? "").Trim();
        }

        internal static double ParseNumber(string text)
        {
            string cleaned = (text ?? "").Replace(',', '.').Trim();
            int end = 0;
            while (end < cleaned.Length && (char.IsDigit(cleaned[end]) || cleaned[end] == '.' || (end == 0 && cleaned[end] == '-'))) end++;
            double value;
            return double.TryParse(cleaned.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : 0;
        }

        /// <summary>Пустые нормативы: книга ЛЗК берёт все значения из прежней книги (правка под заказ).</summary>
        public static Norms Empty()
        {
            return new Norms("");
        }

        /// <summary>
        /// Справочник для изделия: вверх по папкам от <paramref name="folder"/> (свой на заказ или на отдел), иначе — в
        /// папке справочников инструментария (<paramref name="referenceFolder"/>); пусто — нигде нет.
        /// </summary>
        public static string Find(string folder, string referenceFolder)
        {
            string found = FindUp(folder);
            if (found.Length > 0) return found;
            return !string.IsNullOrEmpty(referenceFolder) && File.Exists(PathIn(referenceFolder)) ? PathIn(referenceFolder) : "";
        }

        /// <summary>
        /// Справочник, найденный вверх по папкам от <paramref name="folder"/> (он лежит рядом с корнем заказов, Т-13);
        /// пустая строка — не найден.
        /// </summary>
        public static string FindUp(string folder)
        {
            for (string current = folder; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
                if (File.Exists(PathIn(current))) return PathIn(current);
            return "";
        }

        /// <summary>Путь справочника рядом с библиотекой материалов или в указанной папке.</summary>
        public static string PathIn(string folder)
        {
            return System.IO.Path.Combine(folder ?? "", FileName);
        }

        /// <summary>
        /// Читает справочник. Ошибка — понятная строка в <paramref name="problem"/> и null: «встроенных
        /// значений нет» здесь означает, что молча продолжать нельзя.
        /// </summary>
        public static Norms Read(string path, out string problem)
        {
            problem = "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                problem = "Справочник «" + FileName + "» не найден: " + (path ?? "") +
                          ". Положите его на место — встроенных нормативов у кнопки нет.";
                return null;
            }
            Norms norms = new Norms(path);
            try
            {
                using (XlsxBook book = XlsxBook.Open(path))
                {
                    XlsxSheet sheet = book.Sheet(SheetName);
                    if (sheet == null)
                    {
                        problem = "В справочнике «" + FileName + "» нет листа «" + SheetName + "».";
                        return null;
                    }
                    foreach (int row in sheet.RowNumbers)
                    {
                        string key = (sheet.Get(1, row) ?? "").Trim();
                        if (key.Length == 0 || key.Equals("Ключ", StringComparison.OrdinalIgnoreCase)) continue;
                        norms._values[key] = (sheet.Get(2, row) ?? "").Trim();
                    }
                }
            }
            catch (IOException ex)
            {
                problem = "Справочник «" + FileName + "» не читается (возможно, открыт): " + ex.Message;
                return null;
            }
            string[] missing = Required.Where(k => !norms._values.ContainsKey(k) || norms._values[k].Length == 0).ToArray();
            if (missing.Length > 0)
            {
                problem = "В справочнике «" + FileName + "» не заполнены нормативы: " + string.Join(", ", missing) + ".";
                return null;
            }
            return norms;
        }
    }
}
