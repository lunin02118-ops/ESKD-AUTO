using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Следующий свободный номер детали в децимальной системе изделия (ТЗ-02 Т-20). Правило простое:
    /// новая деталь получает обозначение родительской сборки, у которой последняя группа заменена на
    /// первый незанятый номер — «ТС-52.00.01.000» даёт «ТС-52.00.01.004», если 001…003 уже есть.
    /// </summary>
    public static class OrderNumbering
    {
        /// <summary>Обозначение с нулевой последней группой: «ТС-52.00.01.000» → «ТС-52.00.01», иначе пусто.</summary>
        public static string Prefix(string assemblyDesignation)
        {
            Match m = Regex.Match((assemblyDesignation ?? "").Trim(), @"^(.*)\.(\d+)$");
            if (!m.Success) return "";
            return m.Groups[2].Value.All(c => c == '0') ? m.Groups[1].Value : "";
        }

        /// <summary>
        /// Номера, уже занятые в этой группе: из обозначений (или имён файлов) вида «&lt;префикс&gt;.&lt;номер&gt;».
        /// Нулевой номер — сама сборка, он не считается занятым для детали.
        /// </summary>
        public static SortedSet<int> Taken(string prefix, IEnumerable<string> designations)
        {
            SortedSet<int> taken = new SortedSet<int>();
            if (string.IsNullOrEmpty(prefix) || designations == null) return taken;
            Regex pattern = new Regex(@"^" + Regex.Escape(prefix) + @"\.(\d+)(?:-\d+)?$");
            foreach (string value in designations)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                ParsedName parsed = DesignationParser.Parse(value.Trim(), " ");
                string designation = parsed.HasDesignation ? parsed.Root : value.Trim();
                Match m = pattern.Match(designation);
                int number;
                if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                    continue;
                if (number > 0) taken.Add(number);
            }
            return taken;
        }

        /// <summary>Первый свободный номер группы: 1, 2, 3… с тем же числом знаков, что у сборки (обычно три).</summary>
        public static string Next(string assemblyDesignation, IEnumerable<string> designations)
        {
            string prefix = Prefix(assemblyDesignation);
            if (prefix.Length == 0) return "";
            SortedSet<int> taken = Taken(prefix, designations);
            int number = 1;
            while (taken.Contains(number)) number++;
            int digits = Digits(assemblyDesignation);
            return prefix + "." + number.ToString(new string('0', digits), CultureInfo.InvariantCulture);
        }

        private static int Digits(string assemblyDesignation)
        {
            Match m = Regex.Match((assemblyDesignation ?? "").Trim(), @"\.(\d+)$");
            return m.Success ? m.Groups[1].Value.Length : 3;
        }

        /// <summary>Обозначения соседних документов папки моделей — источник занятых номеров.</summary>
        public static IEnumerable<string> DesignationsInFolder(string modelsFolder)
        {
            if (string.IsNullOrEmpty(modelsFolder) || !Directory.Exists(modelsFolder)) return new string[0];
            List<string> names = new List<string>();
            foreach (string path in Directory.GetFiles(modelsFolder, "*.*", SearchOption.AllDirectories))
            {
                string extension = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                if (extension != ".sldprt" && extension != ".sldasm") continue;
                names.Add(Path.GetFileNameWithoutExtension(path));
            }
            return names;
        }
    }
}
