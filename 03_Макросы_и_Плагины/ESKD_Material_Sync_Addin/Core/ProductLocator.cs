using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Где лежит документ: изделие заказа, эталон базы или «нигде» (ТЗ-02 Т-7а, ТЗ-03 ред. 8).</summary>
    public sealed class ProductLocation
    {
        /// <summary>Папка изделия «И&lt;nn&gt;_&lt;шифр&gt;_&lt;наименование&gt;» (родитель «01_3D»).</summary>
        public string ProductFolder = "";
        /// <summary>Папка «01_3D» изделия, если она есть.</summary>
        public string ModelsFolder = "";
        /// <summary>Папка заказа — родитель «02_Металл».</summary>
        public string OrderFolder = "";
        /// <summary>Изделие внутри заказа (корень «_Заявки» или «03_ЗАКАЗЫ»).</summary>
        public bool InOrder;
        /// <summary>Документ из базы эталонов («02_БАЗА», «_стандартные изделия»).</summary>
        public bool InBase;
        /// <summary>Почему кнопка недоступна — текст для подсказки.</summary>
        public string Reason = "";

        /// <summary>Место опознано: изделие заказа или эталон базы.</summary>
        public bool Found { get { return ProductFolder.Length > 0 && (InOrder || InBase); } }
    }

    /// <summary>
    /// Поиск изделия и заказа по пути документа. Работает со строками пути: так его проверяют тесты и так он
    /// одинаково отвечает и для открытой модели, и для файла на диске.
    /// </summary>
    public static class ProductLocator
    {
        /// <summary>Корень заказов: имя папки по регламенту и прежнее имя из ТЗ.</summary>
        public static readonly string[] OrderRoots = { "_Заявки", "03_ЗАКАЗЫ" };
        /// <summary>
        /// База эталонов и библиотеки. «_Библиотека проектирования» — та самая папка на Synology_TR, откуда
        /// конструкторы берут крепёж и фурнитуру: ссылки на неё законны, и проверка изделия их не считает браком.
        /// </summary>
        public static readonly string[] BaseRoots =
            { "02_БАЗА", "_стандартные изделия", "01_БИБЛИОТЕКА", "_Библиотека проектирования" };
        /// <summary>Раздел заказа, в котором лежат изделия из металла.</summary>
        public const string SectionFolder = "02_Металл";
        /// <summary>
        /// Папки покупного и стандартного внутри изделия. В боевых заказах (CN1-2) конструкторы складывают
        /// заглушки, опоры и крепёж в «Стандартные изделия и фурнитура»: обозначения по ЕСКД у них нет и не
        /// будет, поэтому проверка изделия спрашивает с них только наличие файла.
        /// </summary>
        public static readonly string[] PurchasedFolders =
            { "Стандартные изделия", "Стандартные", "Фурнитура", "Покупные", "Крепеж", "Крепёж" };

        /// <summary>
        /// Документ лежит в папке покупного или стандартного. В изделии считаются только папки внутри «01_3D»: иначе
        /// изделие «Крепёжная рама» целиком стало бы покупным и выпало из выгрузки и проверки (аудит 19.09, Л-В5).
        /// Вне изделия (библиотеки базы) — любой уровень пути, как раньше. Имя папки — слово целиком: «Крепёж ГОСТ» —
        /// покупное, «Крепёжная рама» (сборка вне заказа в своей папке) — нет.
        /// </summary>
        public static bool IsPurchasedFolder(string path, string cipher)
        {
            return IsPurchasedFolder(path) && !IsOwnDesignation(path, cipher);
        }

        /// <summary>
        /// Обозначение в имени файла из той же серии, что шифр изделия: это своя деталь, даже если конструктор положил её
        /// в «Стандартные изделия и фурнитура» (заказ 778, 22.09.2026 — листовой кронштейн выпал из выгрузки DXF и из ЛЗК).
        /// Серия — шифр без последней части: «778.» у «778.КРВ» (детали «778.КРВ.00.005»), «ПРТИ.468211.» у
        /// «ПРТИ.468211.180» (детали нумеруются рядом — «ПРТИ.468211.181»). У покупного обозначения изделия не бывает.
        /// </summary>
        public static bool IsOwnDesignation(string path, string cipher)
        {
            string c = (cipher ?? "").Trim();
            int dot = c.LastIndexOf('.');
            if (dot <= 0) return false;
            string series = c.Substring(0, dot + 1);
            if (!Regex.IsMatch(series, @"\d")) return false;
            ParsedName parsed = DesignationParser.Parse(path, " ");
            return parsed.HasDesignation && parsed.Root.StartsWith(series, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPurchasedFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string dir;
            try
            {
                dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            }
            catch (ArgumentException)
            {
                return false;
            }
            for (string current = dir; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                string name = Path.GetFileName(current) ?? "";
                if (string.Equals(name, LzkNaming.ModelsFolder, StringComparison.OrdinalIgnoreCase)) return false;
                string folder = name.TrimStart('_', ' ');
                if (PurchasedFolders.Any(p => folder.StartsWith(p, StringComparison.OrdinalIgnoreCase) &&
                    (folder.Length == p.Length || !char.IsLetter(folder[p.Length])))) return true;
            }
            return false;
        }

        public static ProductLocation Locate(string path)
        {
            ProductLocation location = new ProductLocation();
            if (string.IsNullOrWhiteSpace(path))
            {
                location.Reason = "документ не сохранён";
                return location;
            }
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                location.Reason = "путь документа не разобран";
                return location;
            }
            List<string> parts = full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries).ToList();
            location.InOrder = parts.Any(p => OrderRoots.Any(r => string.Equals(p, r, StringComparison.OrdinalIgnoreCase)));
            location.InBase = parts.Any(p => BaseRoots.Any(r => string.Equals(p, r, StringComparison.OrdinalIgnoreCase)));

            string dir = Path.GetDirectoryName(full) ?? "";
            // Папка изделия — та, что содержит «01_3D»: сам документ может лежать и в «01_3D», и глубже.
            for (string current = dir; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if (!string.Equals(Path.GetFileName(current), LzkNaming.ModelsFolder, StringComparison.OrdinalIgnoreCase)) continue;
                location.ModelsFolder = current;
                location.ProductFolder = Path.GetDirectoryName(current) ?? "";
                break;
            }
            if (location.ProductFolder.Length == 0) location.ProductFolder = ProductByName(dir);

            for (string current = location.ProductFolder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                string parent = Path.GetDirectoryName(current);
                if (parent == null) break;
                if (string.Equals(Path.GetFileName(current), SectionFolder, StringComparison.OrdinalIgnoreCase))
                {
                    location.OrderFolder = parent;
                    break;
                }
            }

            if (!location.InOrder && !location.InBase)
                location.Reason = "файл не в папке заказа или базы";
            else if (location.ProductFolder.Length == 0)
                location.Reason = "не найдена папка изделия с «" + LzkNaming.ModelsFolder + "»";
            return location;
        }

        /// <summary>Папки внутри корня заказов, которые сами заказом не являются, а хранят заказы (сданные).</summary>
        public static readonly string[] OrderShelves = { "_Сдано" };

        /// <summary>
        /// Папка заказа, в которой лежит файл: первая папка под корнем заказов («_Заявки», «03_ЗАКАЗЫ»; «_Сдано»
        /// пропускается — в ней лежат сданные заказы), без корня — родитель «02_Металл» (диск подключён прямо к папке
        /// заказов). Любое место заказа, а не только изделие: «Общие детали» заказа — тоже этот заказ. Пусто — файл не в
        /// заказе.
        /// </summary>
        public static string OrderOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                return "";
            }
            catch (NotSupportedException)
            {
                return "";
            }
            string[] parts = full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            int last = parts.Length - 1; // имя самого файла
            for (int i = 0; i < last; i++)
            {
                if (!OrderRoots.Any(r => string.Equals(parts[i], r, StringComparison.OrdinalIgnoreCase))) continue;
                int j = i + 1;
                while (j < last && OrderShelves.Any(s => string.Equals(parts[j], s, StringComparison.OrdinalIgnoreCase))) j++;
                return j < last && parts[j].Length > 0 ? string.Join("\\", parts, 0, j + 1) : "";
            }
            for (int i = 1; i < last; i++)
                if (string.Equals(parts[i], SectionFolder, StringComparison.OrdinalIgnoreCase)) return string.Join("\\", parts, 0, i);
            return "";
        }

        /// <summary>
        /// Один и тот же заказ: сравниваются имена папок заказа («778_Школа»). Номер заказа в имени единственный, а путь к
        /// нему бывает разный — «Z:\03_ЗАКАЗЫ» и сетевой «\\Synology_TR\…» у одного файла.
        /// </summary>
        public static bool SameOrder(string order, string other)
        {
            return !string.IsNullOrEmpty(order) && !string.IsNullOrEmpty(other) &&
                string.Equals(Path.GetFileName(order.TrimEnd('\\', '/')), Path.GetFileName(other.TrimEnd('\\', '/')), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Папка изделия по имени «И&lt;nn&gt;_…», если «01_3D» нет (заказ ещё не разложен).</summary>
        private static string ProductByName(string dir)
        {
            for (string current = dir; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (Regex.IsMatch(Path.GetFileName(current) ?? "", @"^И\d{2,}_")) return current;
            return "";
        }

        /// <summary>Обозначение главной сборки изделия: код с нулевыми группами, например ТС-52.00.00.000.</summary>
        public static bool IsMainAssembly(string path)
        {
            ParsedName parsed = DesignationParser.Parse(path ?? "", " ");
            string designation = parsed.HasDesignation ? parsed.Root : "";
            return designation.Length > 0 && Regex.IsMatch(designation, @"(\.00)+\.000$");
        }
    }
}
