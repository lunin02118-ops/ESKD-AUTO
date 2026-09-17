using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Папка изделия, шифр и файлы ведомости ЛЗК (ТЗ-02 Т-36, ТЗ-03: И&lt;nn&gt;_&lt;шифр&gt;_&lt;наименование&gt;\01_3D).</summary>
    public static class LzkNaming
    {
        public const string ModelsFolder = "01_3D";
        public const string ArchiveFolder = "_Аннулировано";
        public const string ReportName = "_Ведомость.txt";

        /// <summary>Папка изделия: родитель «01_3D», иначе папка самой сборки.</summary>
        public static string ProductFolder(string assemblyPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) ?? "";
            string parent = Path.GetDirectoryName(dir);
            return string.Equals(Path.GetFileName(dir), ModelsFolder, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(parent)
                ? parent : dir;
        }

        /// <summary>
        /// Шифр изделия: из имени папки «И&lt;nn&gt;_&lt;шифр&gt;_…», иначе обозначение главной сборки без нулевых хвостов
        /// (КОД.00.00.000 → КОД), иначе обозначение как есть, иначе имя файла сборки.
        /// </summary>
        public static string Cipher(string productFolder, string assemblyPath)
        {
            Match m = Regex.Match(Path.GetFileName(productFolder ?? "") ?? "", @"^И\d{2,}_([^_]+)(?:_|$)");
            if (m.Success && Regex.IsMatch(m.Groups[1].Value, @"[\d.]")) return m.Groups[1].Value;
            ParsedName parsed = DesignationParser.Parse(assemblyPath, " ");
            string designation = parsed.HasDesignation ? parsed.Root : "";
            if (designation.Length > 0)
            {
                Match zeros = Regex.Match(designation, @"^(.+?)(?:\.00)*\.000$");
                return zeros.Success ? zeros.Groups[1].Value : designation;
            }
            return SafeFileName(Path.GetFileNameWithoutExtension(assemblyPath) ?? "Изделие");
        }

        /// <summary>Начало имени ведомости изделия: по нему её находят и снимок эталона, и проверка.</summary>
        public const string WorkbookPrefix = "Ведомость_";

        public static string WorkbookPath(string productFolder, string cipher)
        {
            return Path.Combine(productFolder, WorkbookPrefix + SafeFileName(cipher) + ".xlsx");
        }

        public static string ReportPath(string productFolder)
        {
            return Path.Combine(productFolder, ReportName);
        }

        /// <summary>Куда убрать прежнюю ведомость: _Аннулировано\Ведомость_&lt;шифр&gt;_&lt;дата_время&gt;.xlsx (без затирания).</summary>
        public static string ArchivePath(string productFolder, string cipher, DateTime stamp)
        {
            string dir = Path.Combine(productFolder, ArchiveFolder);
            string stem = "Ведомость_" + SafeFileName(cipher) + "_" + stamp.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            string path = Path.Combine(dir, stem + ".xlsx");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, stem + "_" + i + ".xlsx");
            return path;
        }

        public static bool IsInside(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(folder).TrimEnd('\\', '/') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        public static string SafeFileName(string value)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (value ?? "").Trim())
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }

    /// <summary>Признаки модели для автоподсказки операций (Т-14б).</summary>
    public sealed class ModelTraits
    {
        public bool IsAssembly;
        public bool IsSheetMetal;
        public bool HasBends;
        public bool IsStructuralMember;
        public bool HasWeldBeads;
        public bool IsWeldment;
        /// <summary>Материал детали («Материал_Строка»): по нему решается покраска.</summary>
        public string Material = "";
        /// <summary>Покупное или стандартное изделие: приходит готовым, ничего с ним не делают.</summary>
        public bool IsPurchased;
        /// <summary>Плотность материала модели, кг/м³: запасной признак металла, когда «Материал_Строка» не заполнен.</summary>
        public double DensityKgM3;
    }

    /// <summary>Что за материал у детали: от этого зависит покраска (замечание владельца З-1 к ТЗ-02б).</summary>
    public enum MaterialKind
    {
        /// <summary>Материал не заполнен или не опознан — решает конструктор.</summary>
        Unknown,
        /// <summary>Металлопрокат: лист, труба, профиль, уголок, швеллер, круг, полоса. Красится.</summary>
        RolledMetal,
        /// <summary>Пластик, ЛДСП, МДФ, ДВП, ХДФ, фанера, кромка, резина, стекло. Не красится.</summary>
        NonMetal
    }

    /// <summary>
    /// Распознавание материала по строке обозначения («Материал_Строка» или имя материала библиотеки):
    /// первое слово — сортамент, дальше марка. Списки держатся здесь, чтобы правка была в одном месте.
    /// </summary>
    public static class LzkMaterials
    {
        /// <summary>Сортамент проката — красится.</summary>
        public static readonly string[] Rolled =
        {
            "Лист", "Труба", "Профиль", "Уголок", "Швеллер", "Двутавр", "Балка", "Круг", "Квадрат",
            "Полоса", "Шестигранник", "Лента", "Проволока", "Рулон", "Прокат", "Сталь", "Прут"
        };

        /// <summary>Неметалл — не красится: пластик, древесные плиты, кромка, стекло, резина.</summary>
        public static readonly string[] NonMetal =
        {
            "Плита", "Фанера", "Пластик", "Кромка", "ЛДСП", "МДФ", "ДВП", "ХДФ", "ДСП", "Полиэтилен",
            "Полипропилен", "Полиамид", "Поликарбонат", "ПВХ", "ABS", "HPL", "Оргстекло", "Акрил",
            "Резина", "Стекло", "Поролон", "Ткань", "Картон", "Дерево", "Брус", "Доска", "Капролон", "Фторопласт"
        };

        /// <summary>Плотность, ниже которой материал точно не металл: алюминий 2700, сталь 7850, ЛДСП 800, пластики до 1600.</summary>
        public const double MetalDensityKgM3 = 2000;

        public static MaterialKind Kind(string material)
        {
            string value = (material ?? "").Trim();
            if (value.Length == 0) return MaterialKind.Unknown;
            foreach (string word in NonMetal)
                if (HasWord(value, word)) return MaterialKind.NonMetal;
            foreach (string word in Rolled)
                if (HasWord(value, word)) return MaterialKind.RolledMetal;
            return MaterialKind.Unknown;
        }

        /// <summary>
        /// Материал с учётом плотности модели: у заказов, ещё не оформленных по ЕСКД, «Материал_Строка» пуст,
        /// но материал детали в SolidWorks задан — по нему и видно, металл это или пластик.
        /// </summary>
        public static MaterialKind Kind(string material, double densityKgM3)
        {
            MaterialKind byName = Kind(material);
            if (byName != MaterialKind.Unknown || densityKgM3 <= 0) return byName;
            return densityKgM3 >= MetalDensityKgM3 ? MaterialKind.RolledMetal : MaterialKind.NonMetal;
        }

        public static bool IsRolledMetal(string material)
        {
            return Kind(material) == MaterialKind.RolledMetal;
        }

        /// <summary>Сортамент, который режут лазером по листу.</summary>
        private static readonly string[] SheetStock = { "Лист", "Рулон" };

        /// <summary>Сортамент, который режут на трубном лазере.</summary>
        private static readonly string[] TubeStock = { "Труба", "Профиль", "Уголок", "Швеллер", "Двутавр", "Балка" };

        /// <summary>
        /// Раскрой по сортаменту материала для деталей, у которых SolidWorks не дал признаков (деталь не листовая
        /// и не элемент сварной конструкции): «Лист …» — резка листа, «Труба …», «Профиль …» — резка трубы.
        /// Круг, полоса, проволока сюда не попадают: их режут не лазером, операцию ставит конструктор.
        /// </summary>
        public static string Cutting(string material)
        {
            if (Kind(material) != MaterialKind.RolledMetal) return "";
            foreach (string word in SheetStock)
                if (HasWord(material, word)) return LzkOperations.SheetCutting;
            foreach (string word in TubeStock)
                if (HasWord(material, word)) return LzkOperations.TubeCutting;
            return "";
        }

        /// <summary>Слово целиком, без учёта регистра: «Лист 4,0 …» — лист, «Листогиб» — нет.</summary>
        private static bool HasWord(string value, string word)
        {
            return Regex.IsMatch(value, @"(^|[^\p{L}\p{N}])" + Regex.Escape(word) + @"($|[^\p{L}\p{N}])",
                RegexOptions.IgnoreCase);
        }
    }

    /// <summary>Свойство «Операции»: словарь, порядок маршрута, автоподсказка (ТЗ-02 Т-14б, Р0-5).</summary>
    public static class LzkOperations
    {
        public const string PropertyName = PropertyDictionary.OperationsName;
        public const string SizePropertyName = "Габарит";
        public const string SheetCutting = "Лазерная резка листа";
        public const string TubeCutting = "Лазерная резка трубы";
        public const string Bending = "Гибка";
        public const string WeldedAssembly = "Сварная сборка";
        public const string MechanicalAssembly = "Механическая сборка";
        public const string Painting = "Покраска";
        public const string Separator = "; ";

        /// <summary>Все операции в порядке маршрута.</summary>
        public static readonly string[] All = { SheetCutting, TubeCutting, Bending, WeldedAssembly, MechanicalAssembly, Painting };

        public static List<string> Parse(string value)
        {
            List<string> result = new List<string>();
            foreach (string raw in (value ?? "").Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = raw.Trim();
                if (item.Length == 0) continue;
                string known = All.FirstOrDefault(a => string.Equals(a, item, StringComparison.OrdinalIgnoreCase));
                item = known ?? item;
                if (!result.Contains(item)) result.Add(item);
            }
            return result;
        }

        /// <summary>Строка свойства: известные операции в порядке маршрута, затем прочие в исходном порядке.</summary>
        public static string Join(IEnumerable<string> operations)
        {
            List<string> list = Parse(string.Join(";", (operations ?? new string[0]).ToArray()));
            List<string> ordered = All.Where(list.Contains).ToList();
            ordered.AddRange(list.Where(o => Array.IndexOf(All, o) < 0));
            return string.Join(Separator, ordered.ToArray());
        }

        public static bool Contains(string value, string operation)
        {
            return Parse(value).Contains(operation);
        }

        /// <summary>
        /// Автоподсказка по модели: лист → резка листа (+ гибка при сгибах); элемент сварной конструкции → резка трубы;
        /// сборка со швами или сварная → сварная, иначе механическая.
        ///
        /// «Покраска» (З-1): красится металлопрокат и сварной узел целиком; пластик, древесные плиты, кромка и покупные
        /// не красятся; у сварного узла детали внутри на лист «Покраска» не выводятся (Т-14б). Материал не опознан —
        /// покраска не подставляется, решает конструктор в окне «Операции».
        /// </summary>
        public static List<string> Suggest(ModelTraits t)
        {
            List<string> result = new List<string>();
            if (t == null || t.IsPurchased) return result;
            if (t.IsAssembly)
            {
                bool welded = t.HasWeldBeads || t.IsWeldment;
                result.Add(welded ? WeldedAssembly : MechanicalAssembly);
                if (welded) result.Add(Painting);
                return result;
            }
            if (t.IsSheetMetal)
            {
                result.Add(SheetCutting);
                if (t.HasBends) result.Add(Bending);
            }
            else if (t.IsStructuralMember)
            {
                result.Add(TubeCutting);
            }
            else
            {
                // Признаков нет (деталь смоделирована телом, а не листовым металлом или элементом конструкции) —
                // раскрой по сортаменту материала.
                string cutting = LzkMaterials.Cutting(t.Material);
                if (cutting.Length > 0) result.Add(cutting);
            }
            if (LzkMaterials.Kind(t.Material, t.DensityKgM3) == MaterialKind.RolledMetal) result.Add(Painting);
            return result;
        }

        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        /// <summary>Габарит «Д×Ш×В» в мм по убыванию, до десятых.</summary>
        public static string FormatSize(double lengthMm, double widthMm, double heightMm)
        {
            double[] d = { Math.Abs(lengthMm), Math.Abs(widthMm), Math.Abs(heightMm) };
            Array.Sort(d);
            return Number(d[2]) + "×" + Number(d[1]) + "×" + Number(d[0]);
        }

        /// <summary>Длина заготовки проката.</summary>
        public static string FormatLength(double lengthMm)
        {
            return "L=" + Number(Math.Abs(lengthMm));
        }

        public static string Number(double value)
        {
            return Math.Round(value, 1).ToString("0.#", Ru);
        }

        /// <summary>Число из свойства («1 200,5», «1200.5 мм», «L=600») или NaN.</summary>
        public static double ParseNumber(string text)
        {
            Match m = Regex.Match((text ?? "").Replace(' ', ' ').Replace(" ", ""), @"-?\d+(?:[.,]\d+)?");
            double v;
            return m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }
    }

    /// <summary>Компонент изделия для ведомости: одна модель (путь + конфигурация) с количеством на 1 шт.</summary>
    public sealed class LzkItem
    {
        public string Path = "";
        public string Configuration = "";
        public string Designation = "";
        public string Name = "";
        public bool IsAssembly;
        public bool IsPurchased;
        public bool InProduct;
        public string Operations = "";
        public int Quantity;
        public double AreaM2 = double.NaN;
        public string Code = "";
        public string Unit = "";
        public string Size = "";
        public bool SizeIsEstimate;
        public bool IsProfile;
        /// <summary>Компонент входит в узел, который красится целиком: на лист «Покраска» не выводится.</summary>
        public bool InsidePaintedUnit;
        /// <summary>Материал_Строка модели (для деталей).</summary>
        public string Material = "";
        /// <summary>Главная сборка: в таблице её нет (SWTools выводит только состав).</summary>
        public bool IsTop;
    }

    public sealed class LzkHeader
    {
        public string Product = "";
        public string Author = "";
        public string Model = "";
        public string Date = "";
        public string Checksum = "";
    }

    public sealed class LzkResult
    {
        public int Rows;
        public int PaintRows;
        public int PurchasedRows;
        public readonly List<string> Issues = new List<string>();
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>
    /// Дописывание ведомости, выгруженной SWTools по шаблону «Ведомость_ЛЗК.xlsx»: шапка, пометки «?», листы «Покраска»
    /// и «Покупные» (ТЗ-02 Т-35…Т-37). Колонки основной таблицы находятся по именованным диапазонам шаблона.
    /// </summary>
    public static class LzkWorkbook
    {
        public const string Mark = "?";
        public const string PaintSheet = "Покраска";
        public const string PurchasedSheet = "Покупные";
        /// <summary>Шрифт книги: есть на каждом рабочем месте, в таблице читается лучше Times New Roman.</summary>
        public const string FontFace = "Arial";
        /// <summary>Разделитель ключа группировки: в реквизитах такого символа нет.</summary>
        private const string Key = "\u0001";

        public static readonly string[] RequiredNames =
        {
            "Номер", "Обозначение", "Наименование", "Материал_Строка", "Габарит", "МассаЕдКг", "Количество", "Операции", "Путь"
        };

        public static LzkResult Complete(string xlsxPath, LzkHeader header, IList<LzkItem> items)
        {
            LzkResult result = new LzkResult();
            items = items ?? new List<LzkItem>();
            XlsxBook book = XlsxBook.Open(xlsxPath);
            Dictionary<string, int> col = new Dictionary<string, int>();
            string sheetName = null;
            int headerRow = 0;
            foreach (string name in RequiredNames)
            {
                string sheet, cell;
                if (!book.TryResolveName(name, out sheet, out cell))
                {
                    result.Errors.Add("В ведомости нет именованного диапазона «" + name + "» — выгрузка сделана не по шаблону ЛЗК");
                    continue;
                }
                int c, r;
                XlsxBook.ParseCell(cell, out c, out r);
                if (sheetName == null) { sheetName = sheet; headerRow = r; }
                else if (!string.Equals(sheet, sheetName, StringComparison.OrdinalIgnoreCase) || r != headerRow)
                    result.Errors.Add("Колонка «" + name + "» не в строке заголовка шаблона");
                col[name] = c;
            }
            if (result.Errors.Count > 0) return result;
            XlsxSheet main = book.Sheet(sheetName);
            if (main == null)
            {
                result.Errors.Add("В ведомости нет листа «" + sheetName + "»");
                return result;
            }

            Dictionary<string, LzkItem> byPath = new Dictionary<string, LzkItem>(StringComparer.OrdinalIgnoreCase);
            foreach (LzkItem item in items)
                if (!byPath.ContainsKey(item.Path)) byPath[item.Path] = item;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (int row in main.RowNumbers.Where(r => r > headerRow))
            {
                string path = main.Get(col["Путь"], row).Trim();
                string designation = main.Get(col["Обозначение"], row).Trim();
                if (path.Length == 0 && designation.Length == 0 && main.Get(col["Наименование"], row).Trim().Length == 0) continue;
                result.Rows++;
                string label = "Строка " + main.Get(col["Номер"], row).Trim() + " (" +
                    (designation.Length > 0 ? designation : main.Get(col["Наименование"], row).Trim()) + ")";
                LzkItem item;
                byPath.TryGetValue(path, out item);
                if (path.Length > 0) seen.Add(path);
                if (item != null)
                {
                    // Реквизиты ЕСКД — из свойств модели: разбор имён файлов в SWTools зависит от его настроек.
                    Fill(main, col["Обозначение"], row, item.Designation, true);
                    Fill(main, col["Наименование"], row, item.Name, true);
                    if (!item.IsAssembly) Fill(main, col["Материал_Строка"], row, item.Material, false);
                    Fill(main, col["Операции"], row, item.Operations, false);
                    designation = main.Get(col["Обозначение"], row).Trim();
                    label = "Строка " + main.Get(col["Номер"], row).Trim() + " (" +
                        (designation.Length > 0 ? designation : main.Get(col["Наименование"], row).Trim()) + ")";
                }
                bool assembly = item != null ? item.IsAssembly : path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
                if (item == null)
                    result.Issues.Add(label + ": модели нет в составе изделия");
                else if (item.IsPurchased)
                    result.Issues.Add(label + ": покупное или стандартное изделие в основной таблице — проверьте свойство «Раздел»");

                if (!assembly) Require(main, col["Материал_Строка"], row, label, "Материал", result);
                double mass = LzkOperations.ParseNumber(main.Get(col["МассаЕдКг"], row));
                if (double.IsNaN(mass) || mass <= 0)
                {
                    main.SetText(XlsxBook.CellName(col["МассаЕдКг"], row), Mark);
                    result.Issues.Add(label + ": не заполнена «Масса»");
                }
                Require(main, col["Операции"], row, label, "Операции", result);

                string size = main.Get(col["Габарит"], row).Trim();
                if (size.Length == 0 && item != null && item.Size.Length > 0)
                {
                    size = item.Size + (item.SizeIsEstimate ? "*" : "");
                    main.SetText(XlsxBook.CellName(col["Габарит"], row), size);
                }
                if (size.Length == 0)
                {
                    main.SetText(XlsxBook.CellName(col["Габарит"], row), Mark);
                    result.Issues.Add(label + ": не определены габаритные размеры");
                }
                else if (item != null && item.IsProfile && item.SizeIsEstimate)
                {
                    result.Issues.Add(label + ": профиль без длины заготовки — указан габарит (*)");
                }
            }

            List<string> missing = items.Where(i => !i.IsPurchased && !i.IsTop && !seen.Contains(i.Path))
                .Select(i => i.Designation.Length > 0 ? i.Designation : System.IO.Path.GetFileNameWithoutExtension(i.Path))
                .Distinct().ToList();
            if (missing.Count > 0)
                result.Issues.Add("В ведомости нет изготавливаемых моделей (" + missing.Count + "): " + string.Join(", ", missing.ToArray()));

            // Шаблон SWTools собран из китайского образца: без этого вся книга печатается шрифтом 宋体.
            book.NormalizeFonts(FontFace);
            result.PaintRows = WritePaint(book, items, header);
            result.PurchasedRows = WritePurchased(book, items, header);

            if (header != null)
            {
                SetName(book, main, "Шапка_Изделие", header.Product);
                SetName(book, main, "Шапка_Составил", header.Author);
                SetName(book, main, "Шапка_Модель", header.Model);
                SetName(book, main, "Шапка_Дата", header.Date);
                SetName(book, main, "Шапка_КонтрольнаяСумма", header.Checksum);
            }
            SetName(book, main, "Шапка_Замечания", result.Issues.Count == 0 ? "нет" : result.Issues.Count + " (см. " + LzkNaming.ReportName + ")");
            book.Save();
            return result;
        }

        /// <summary>Значение из модели в ячейку: всегда (replace) или только в пустую.</summary>
        private static void Fill(XlsxSheet sheet, int column, int row, string value, bool replace)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) return;
            string current = sheet.Get(column, row).Trim();
            if (current == value || (!replace && current.Length > 0)) return;
            sheet.SetText(XlsxBook.CellName(column, row), value);
        }

        private static void Require(XlsxSheet sheet, int column, int row, string label, string what, LzkResult result)
        {
            if (sheet.Get(column, row).Trim().Length > 0) return;
            sheet.SetText(XlsxBook.CellName(column, row), Mark);
            result.Issues.Add(label + ": не заполнено «" + what + "»");
        }

        private static void SetName(XlsxBook book, XlsxSheet main, string name, string value)
        {
            string sheet, cell;
            if (value == null || !book.TryResolveName(name, out sheet, out cell)) return;
            XlsxSheet target = book.Sheet(sheet) ?? main;
            target.SetText(cell, value);
        }

        private static int WritePaint(XlsxBook book, IList<LzkItem> items, LzkHeader header)
        {
            List<LzkItem> painted = items
                .Where(i => !i.IsPurchased && !i.InsidePaintedUnit && LzkOperations.Contains(i.Operations, LzkOperations.Painting))
                .ToList();
            string[] titles = { "№", "Обозначение", "Наименование", "Площадь 1 шт., м²", "Кол-во, шт.", "Площадь всего, м²" };
            double[] widths = { 5, 24, 40, 14, 11, 15 };
            Layout layout = new Layout(book, PaintSheet, "Покраска (на одно изделие)", header, titles, widths);
            int row = layout.FirstRow;
            double total = 0;
            foreach (LzkItem i in painted)
            {
                layout.Sheet.SetNumber(XlsxBook.CellName(1, row), row - layout.FirstRow + 1, layout.Center);
                layout.Sheet.SetText(XlsxBook.CellName(2, row), i.Designation, layout.Text);
                layout.Sheet.SetText(XlsxBook.CellName(3, row), i.Name, layout.Text);
                if (double.IsNaN(i.AreaM2))
                {
                    layout.Sheet.SetText(XlsxBook.CellName(4, row), Mark, layout.Center);
                    layout.Sheet.SetText(XlsxBook.CellName(6, row), Mark, layout.Center);
                }
                else
                {
                    double area = Math.Round(i.AreaM2, 3);
                    layout.Sheet.SetNumber(XlsxBook.CellName(4, row), area, layout.Area);
                    layout.Sheet.SetNumber(XlsxBook.CellName(6, row), Math.Round(area * i.Quantity, 3), layout.Area);
                    total += area * i.Quantity;
                }
                layout.Sheet.SetNumber(XlsxBook.CellName(5, row), i.Quantity, layout.Center);
                row++;
            }
            if (painted.Count == 0)
            {
                layout.Sheet.SetText(XlsxBook.CellName(1, row), "Окрашиваемых единиц нет: операция «Покраска» не назначена ни одной единице", layout.Note);
                layout.Sheet.Merge("A" + row + ":" + XlsxBook.CellName(titles.Length, row));
            }
            else
            {
                layout.Sheet.SetText(XlsxBook.CellName(3, row), "Итого", layout.Total);
                layout.Sheet.SetText(XlsxBook.CellName(4, row), "", layout.Total);
                layout.Sheet.SetText(XlsxBook.CellName(5, row), "", layout.Total);
                layout.Sheet.SetNumber(XlsxBook.CellName(6, row), Math.Round(total, 3), layout.TotalNumber);
            }
            layout.Finish(row);
            return painted.Count;
        }

        private static int WritePurchased(XlsxBook book, IList<LzkItem> items, LzkHeader header)
        {
            var groups = items.Where(i => i.IsPurchased)
                .GroupBy(i => (i.Designation + Key + i.Name + Key + i.Code).ToLowerInvariant())
                .Select(g => new { First = g.First(), Quantity = g.Sum(i => i.Quantity) })
                .OrderBy(g => g.First.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            string[] titles = { "№", "Наименование", "Обозначение", "Код 1С", "Ед. изм.", "ОКЕИ", "Кол-во" };
            double[] widths = { 5, 44, 26, 12, 10, 8, 10 };
            Layout layout = new Layout(book, PurchasedSheet,
                "Покупные и стандартные изделия (на одно изделие)", header, titles, widths);
            int row = layout.FirstRow;
            foreach (var g in groups)
            {
                LzkItem i = g.First;
                string unit, okei;
                Unit(i.Unit, out unit, out okei);
                layout.Sheet.SetNumber(XlsxBook.CellName(1, row), row - layout.FirstRow + 1, layout.Center);
                layout.Sheet.SetText(XlsxBook.CellName(2, row), i.Name.Length > 0 ? i.Name : System.IO.Path.GetFileNameWithoutExtension(i.Path), layout.Text);
                layout.Sheet.SetText(XlsxBook.CellName(3, row), i.Designation, layout.Text);
                // Код 1С — из справочника снабжения (ТЗ-02 Т-14а). Справочника нет — ячейка пустая: это не забытый
                // реквизит конструктора, замечанием в отчёт не идёт.
                layout.Sheet.SetText(XlsxBook.CellName(4, row), i.Code.Trim(), layout.Center);
                layout.Sheet.SetText(XlsxBook.CellName(5, row), unit, layout.Center);
                layout.Sheet.SetText(XlsxBook.CellName(6, row), okei, layout.Center);
                layout.Sheet.SetNumber(XlsxBook.CellName(7, row), g.Quantity, layout.Center);
                row++;
            }
            if (groups.Count == 0)
            {
                layout.Sheet.SetText(XlsxBook.CellName(1, row), "Покупных и стандартных изделий в составе нет", layout.Note);
                layout.Sheet.Merge("A" + row + ":" + XlsxBook.CellName(titles.Length, row));
            }
            layout.Finish(row);
            return groups.Count;
        }

        /// <summary>«796 ШТ», «шт» → единица и код ОКЕИ; по умолчанию штуки (ТЗ-02 Т-14а).</summary>
        public static void Unit(string value, out string unit, out string okei)
        {
            string raw = (value ?? "").Trim();
            Match m = Regex.Match(raw, @"^(\d{3})\s*(.*)$");
            okei = m.Success ? m.Groups[1].Value : "";
            unit = (m.Success ? m.Groups[2].Value : raw).Trim().ToLowerInvariant();
            if (unit.Length == 0) unit = "шт";
            if (okei.Length == 0 && unit == "шт") okei = "796";
        }

        /// <summary>Вёрстка листа ведомости: название, подзаголовок, шапка, стили строк, печать.</summary>
        private sealed class Layout
        {
            private const string HeadFill = "FFF2F2F2";
            private readonly XlsxBook _book;
            private readonly string _name;
            private readonly int _columns;

            public readonly XlsxSheet Sheet;
            public readonly int FirstRow = 4;
            public readonly int Text, Center, Area, Total, TotalNumber, Note;

            public Layout(XlsxBook book, string name, string title, LzkHeader header, string[] titles, double[] widths)
            {
                _book = book;
                _name = name;
                _columns = titles.Length;
                Sheet = book.AddSheet(name);
                int titleStyle = book.AddStyle(new XlsxStyle { Bold = true, Size = 12, Horizontal = "left" });
                int subtitle = book.AddStyle(new XlsxStyle { Gray = true, Size = 9, Horizontal = "left" });
                int head = book.AddStyle(new XlsxStyle { Bold = true, Border = true, Wrap = true, Horizontal = "center", Fill = HeadFill });
                Text = book.AddStyle(new XlsxStyle { Border = true, Wrap = true, Horizontal = "left" });
                Center = book.AddStyle(new XlsxStyle { Border = true, Horizontal = "center" });
                Area = book.AddStyle(new XlsxStyle { Border = true, Horizontal = "center", NumberFormat = "0.000" });
                Total = book.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "right" });
                TotalNumber = book.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "center", NumberFormat = "0.000" });
                Note = book.AddStyle(new XlsxStyle { Gray = true, Horizontal = "left" });

                Sheet.SetText("A1", title, titleStyle);
                Sheet.Merge("A1:" + XlsxBook.CellName(_columns, 1));
                Sheet.SetRowHeight(1, 22);
                Sheet.SetText("A2", Subtitle(header), subtitle);
                Sheet.Merge("A2:" + XlsxBook.CellName(_columns, 2));
                for (int c = 0; c < titles.Length; c++)
                {
                    Sheet.SetText(XlsxBook.CellName(c + 1, 3), titles[c], head);
                    Sheet.SetColumnWidth(c + 1, widths[c]);
                }
                Sheet.SetRowHeight(3, 30);
            }

            private static string Subtitle(LzkHeader header)
            {
                if (header == null) return "";
                string product = (header.Product ?? "").Trim();
                string date = (header.Date ?? "").Trim();
                return (product.Length > 0 ? "Изделие: " + product : "") +
                    (product.Length > 0 && date.Length > 0 ? ";  " : "") +
                    (date.Length > 0 ? "сформировано " + date : "");
            }

            /// <summary>Закрепление шапки, область печати и повтор шапки на каждой странице.</summary>
            public void Finish(int lastRow)
            {
                Sheet.FreezeRowsAbove("A" + FirstRow);
                Sheet.FitToWidth();
                string lastColumn = XlsxBook.CellName(_columns, 1);
                lastColumn = lastColumn.Substring(0, lastColumn.Length - 1);
                _book.SetPrintNames(_name, "$A$1:$" + lastColumn + "$" + Math.Max(lastRow, FirstRow), 3);
            }
        }

        /// <summary>Текст отчёта _Ведомость.txt.</summary>
        public static string Report(LzkHeader header, string workbookPath, LzkResult result, IEnumerable<string> notes)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Ведомость изделия (ЛЗК)");
            sb.AppendLine("Изделие:  " + (header != null ? header.Product : ""));
            sb.AppendLine("Сборка:   " + (header != null ? header.Model : ""));
            sb.AppendLine("Составил: " + (header != null ? header.Author : "") + ", " + (header != null ? header.Date : ""));
            sb.AppendLine("Файл:     " + workbookPath);
            if (result != null)
            {
                sb.AppendLine(string.Format("Строк: {0}; покраска: {1}; покупные: {2}", result.Rows, result.PaintRows, result.PurchasedRows));
                sb.AppendLine();
                foreach (string e in result.Errors) sb.AppendLine("ОШИБКА: " + e);
                if (result.Issues.Count == 0 && result.Errors.Count == 0) sb.AppendLine("Замечаний нет.");
                else foreach (string i in result.Issues) sb.AppendLine("? " + i);
            }
            if (notes != null)
            {
                List<string> list = notes.Where(n => !string.IsNullOrEmpty(n)).ToList();
                if (list.Count > 0)
                {
                    sb.AppendLine();
                    foreach (string n in list) sb.AppendLine(n);
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Запуск выгрузки SWTools без окна и разбор его отчёта (docs/integration/HEADLESS_BOM_EXPORT_RU.md в SWTools).</summary>
    public static class SwToolsExport
    {
        public const string Preset = "ЛЗК";
        /// <summary>Надстройка SWTools: выгрузку запускает её метод StartBomExport (SWTools 1.1.109+).</summary>
        public const string AddinClsid = "{59959DFA-3229-4B86-852E-52ABF2BDB8C0}";
        public const string ResultSchema = "swtools.headless-bom-export.v1";

        public sealed class Outcome
        {
            public bool Found;
            public bool Ok;
            public int ExitCode = -1;
            public int Rows;
            public string Error = "";
            public string Version = "";
        }

        public static Outcome ReadResult(string resultPath)
        {
            Outcome o = new Outcome();
            if (string.IsNullOrEmpty(resultPath) || !File.Exists(resultPath)) return o;
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(resultPath, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            string schema;
            if (!values.TryGetValue("schema", out schema) || schema != ResultSchema) return o;
            o.Found = true;
            string v;
            o.Ok = values.TryGetValue("status", out v) && v == "OK";
            int n;
            if (values.TryGetValue("exit_code", out v) && int.TryParse(v, out n)) o.ExitCode = n;
            if (values.TryGetValue("rows", out v) && int.TryParse(v, out n)) o.Rows = n;
            if (values.TryGetValue("error", out v)) o.Error = v;
            if (values.TryGetValue("version", out v)) o.Version = v;
            return o;
        }

        /// <summary>Понятное конструктору объяснение кода завершения SWTools.</summary>
        public static string Explain(Outcome o, int processExitCode)
        {
            if (!o.Found)
                return "SWTools завершился без отчёта" + (processExitCode >= 0 ? " (код " + processExitCode + ")" : "") +
                    ". Проверьте установку SWTools: запустите его из SolidWorks один раз.";
            switch (o.ExitCode)
            {
                case 0: return "";
                case 2: return "SWTools не принял задание: " + o.Error + ". Обновите SWTools до версии с пресетом «ЛЗК» (1.1.109 или новее).";
                case 3: return "Нет действующей лицензии SWTools. Откройте SWTools и активируйте лицензию, затем повторите.";
                case 4: return "SWTools не смог прочитать модель из SolidWorks: " + o.Error;
                case 5: return "Открыто окно SWTools. Закройте его и нажмите «Ведомость ЛЗК» ещё раз.";
                default: return "Ошибка выгрузки SWTools: " + o.Error;
            }
        }
    }
}
