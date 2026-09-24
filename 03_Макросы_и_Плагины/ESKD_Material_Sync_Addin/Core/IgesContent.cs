using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Что попало в IGS детали. SolidWorks пишет в IGS активный документ, а не модель, у которой вызван SaveAs: выгрузка
    /// клала в «&lt;деталь&gt;.igs» всю сборку или соседнюю деталь, а заголовок файла называл деталь (замечание владельца
    /// 24.09.2026). Сборку в файле выдают подфигуры раздела D: 308 — определение, 408 — экземпляр, 416 — внешняя ссылка,
    /// 320/420 — сетевая подфигура. Одна деталь их не пишет.
    /// </summary>
    public static class IgesContent
    {
        private static readonly int[] AssemblyTypes = { 308, 408, 416, 320, 420 };

        /// <summary>Кодировка строк Холлерита: SolidWorks пишет имена деталей в IGS в ANSI (1251).</summary>
        public static readonly Encoding Cyrillic = Encoding.GetEncoding(1251);

        /// <summary>
        /// Почему файл — не одна деталь: «в файле сборка — детали: …»; пусто — подфигур сборки нет. Читаются только типы
        /// записей раздела D и имена определений 308 из раздела P: числа в заголовке и в параметрах не в счёт.
        /// </summary>
        public static string Foreign(IEnumerable<string> lines)
        {
            HashSet<int> definitions = new HashSet<int>();
            bool assembly = false;
            Dictionary<int, StringBuilder> parameters = new Dictionary<int, StringBuilder>();
            bool first = true;
            foreach (string line in lines ?? Enumerable.Empty<string>())
            {
                if (line == null || line.Length < 73) continue;
                char section = line[72];
                if (section == 'D')
                {
                    // Запись D — две строки; тип сущности — в первых восьми знаках первой.
                    if (first)
                    {
                        int type;
                        if (int.TryParse(line.Substring(0, 8).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out type) &&
                            AssemblyTypes.Contains(type))
                        {
                            assembly = true;
                            int sequence;
                            if (type == 308 && line.Length >= 80 &&
                                int.TryParse(line.Substring(73, 7).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence))
                                definitions.Add(sequence);
                        }
                    }
                    first = !first;
                }
                else if (section == 'P' && definitions.Count > 0)
                {
                    int entity;
                    if (!int.TryParse(line.Substring(64, 8).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out entity) ||
                        !definitions.Contains(entity))
                        continue;
                    StringBuilder text;
                    if (!parameters.TryGetValue(entity, out text)) parameters[entity] = text = new StringBuilder();
                    text.Append(line.Substring(0, 64));
                }
            }
            if (!assembly) return "";
            List<string> names = parameters.Values.Select(t => SubfigureName(t.ToString())).Where(n => n.Length > 0).ToList();
            return names.Count > 0
                ? "в файле сборка — детали: " + string.Join(", ", names)
                : "в файле сборка (подфигуры IGES)";
        }

        /// <summary>Имя подфигуры — третий параметр записи 308 («308,0,22HПРТИ.468211.102 Стойка,…»), строка Холлерита.</summary>
        private static string SubfigureName(string record)
        {
            int comma = record.IndexOf(',');
            comma = comma < 0 ? -1 : record.IndexOf(',', comma + 1);
            if (comma < 0) return "";
            int h = record.IndexOf('H', comma + 1);
            int length;
            if (h < 0 || !int.TryParse(record.Substring(comma + 1, h - comma - 1).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out length) || length <= 0 || h + 1 + length > record.Length)
                return "";
            return record.Substring(h + 1, length).Trim();
        }
    }
}
