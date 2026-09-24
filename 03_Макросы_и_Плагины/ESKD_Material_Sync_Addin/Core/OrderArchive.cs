using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Куда «Закрыть заказ» складывает сданный заказ (ТЗ-02 Т-45…Т-47). Отдельного диска архива нет (решение владельца
    /// 19.09.2026: диск один — Z:), поэтому архив — папка «_Архив» на том же общем ресурсе рядом с корнем заказов:
    /// <c>…\Конструкторский отдел\_Заявки\&lt;заказ&gt;</c> → <c>…\Конструкторский отдел\_Архив\&lt;год&gt;\&lt;заказ&gt;</c>.
    /// Путь выводится из папки заказа, поэтому не зависит от буквы диска.
    /// </summary>
    public static class OrderArchive
    {
        public const string FolderName = "_Архив";

        /// <summary>Архив по умолчанию для заказа <paramref name="orderFolder"/>; пусто — папку не вывести.</summary>
        public static string DefaultRoot(string orderFolder)
        {
            string order = (orderFolder ?? "").TrimEnd('\\', '/');
            string ordersRoot = Path.GetDirectoryName(order);
            string share = string.IsNullOrEmpty(ordersRoot) ? null : Path.GetDirectoryName(ordersRoot);
            return string.IsNullOrEmpty(share) ? "" : Path.Combine(share, FolderName);
        }

        /// <summary>
        /// Открытые документы других папок с именем файла этого заказа (сверка SW API 23.09.2026, №16). SolidWorks ищет
        /// компонент сначала среди открытых документов — по имени файла, а не по пути, — и Pack and Go заказа взял бы
        /// одноимённую деталь другого заказа: одно изделие с теми же именами файлов повторяется в разных заказах.
        /// </summary>
        public static List<string> Namesakes(IEnumerable<string> openPaths, IEnumerable<string> orderFiles, string order)
        {
            HashSet<string> own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in orderFiles ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(file)) own.Add(Path.GetFileName(file));
            // С разделителем: заказ «85_Т» не должен считать своими документы заказа «85_Т2».
            string prefix = (order ?? "").TrimEnd('\\', '/') + "\\";
            List<string> found = new List<string>();
            foreach (string path in openPaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(path) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (own.Contains(Path.GetFileName(path)) && !found.Contains(path)) found.Add(path);
            }
            return found;
        }

        /// <summary>
        /// Список для отказа «Закрыть заказ» (ревью 23.09.2026): документы с окном — путями, не больше max, остальное числом;
        /// загруженные без своего окна — детали открытых сборок и чертежей другого заказа — отдельной строкой: закрывать
        /// надо не их, а сборки и чертежи, где они стоят. Раньше — все пути подряд без предела и «закройте их».
        /// </summary>
        public static string NamesakeText(IList<string> visible, IList<string> hidden, int max)
        {
            List<string> lines = new List<string>();
            List<string> shown = (visible ?? new List<string>()).Where(p => !string.IsNullOrEmpty(p)).ToList();
            List<string> inside = (hidden ?? new List<string>()).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (max < 1) max = 1;
            lines.AddRange(shown.Take(max));
            if (shown.Count > max) lines.Add("… и ещё " + (shown.Count - max));
            if (inside.Count > 0)
            {
                // Путём, а не только именем: по папке видно, чья это сборка (ревью 23.09.2026).
                const int names = 5;
                lines.Add("без своего окна — это компоненты открытых сборок или чертежей: закройте сборки и чертежи, в которых " +
                    "они стоят:");
                lines.AddRange(inside.Take(names));
                if (inside.Count > names) lines.Add("… и ещё " + (inside.Count - names) + " без окна");
            }
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        /// <summary>
        /// Документы комплекта, которые Pack and Go взял не из своей папки при своём одноимённом файле, и ссылки на
        /// несуществующие файлы (№16). Общий компонент библиотеки, чьего имени в изделии нет, — не подмена.
        /// </summary>
        public static List<string> Substituted(IEnumerable<string> resolved, IEnumerable<string> ownFiles, Func<string, bool> exists)
        {
            HashSet<string> paths = new HashSet<string>(ownFiles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> names = new HashSet<string>(paths.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            List<string> bad = new List<string>();
            foreach (string path in resolved ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (names.Contains(Path.GetFileName(path)) && !paths.Contains(path))
                    bad.Add("взят «" + path + "» вместо своего одноимённого");
                else if (!exists(path))
                    bad.Add("нет файла «" + path + "»");
            }
            return bad;
        }
    }
}
