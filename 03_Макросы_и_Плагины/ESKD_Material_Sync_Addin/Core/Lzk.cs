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
        /// <summary>ТЗ-04 Р4-3: книга ЛЗК лежит в сопроводительной документации изделия.</summary>
        public const string DocsFolder = "04_Сопроводительная документация";
        public const string ArchiveFolder = "_Аннулировано";

        /// <summary>Папка изделия: родитель «01_3D», иначе папка самой сборки.</summary>
        public static string ProductFolder(string assemblyPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) ?? "";
            string parent = Path.GetDirectoryName(dir);
            return string.Equals(Path.GetFileName(dir), ModelsFolder, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(parent)
                ? parent : dir;
        }

        /// <summary>Сборка в папке «01_3D» изделия (структура заказа) — книга в «04_Сопроводительная документация».</summary>
        public static bool InModelsFolder(string assemblyPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) ?? "";
            return string.Equals(Path.GetFileName(dir), ModelsFolder, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Книга сборки вне структуры заказа — прямо рядом со сборкой, без новых папок (замечание владельца 19.09.2026).
        /// </summary>
        public static string LooseWorkbookPath(string assemblyPath, string cipher)
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) ?? "", WorkbookPrefix + SafeFileName(cipher) + ".xlsx");
        }

        /// <summary>
        /// Резервная копия прежней книги сборки вне структуры заказа: рядом со сборкой папок не заводим, поэтому прежняя
        /// книга — в «%LOCALAPPDATA%\ESKD\ЛЗК_прежние\ЛЗК_&lt;шифр&gt;_&lt;дата_время&gt;.xlsx» (без затирания).
        /// </summary>
        public static string BackupPath(string cipher, DateTime stamp)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESKD", "ЛЗК_прежние");
            string stem = WorkbookPrefix + SafeFileName(cipher) + "_" + stamp.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            string path = Path.Combine(dir, stem + ".xlsx");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, stem + "_" + i + ".xlsx");
            return path;
        }

        /// <summary>
        /// Рядом со сборкой писать нельзя (чужой ресурс, архив, защищённая папка) — книга в «Документы», без новых папок.
        /// </summary>
        public static string FallbackWorkbookPath(string cipher)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), WorkbookPrefix + SafeFileName(cipher) + ".xlsx");
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

        /// <summary>Начало имени книги ЛЗК изделия: по нему её находят и снимок эталона, и проверка.</summary>
        public const string WorkbookPrefix = "ЛЗК_";
        /// <summary>Ведомость до ТЗ-04 — в корне папки изделия; читается, пока изделие не пересобрано.</summary>
        public const string LegacyWorkbookPrefix = "Ведомость_";

        public static string WorkbookPath(string productFolder, string cipher)
        {
            return Path.Combine(productFolder, DocsFolder, WorkbookPrefix + SafeFileName(cipher) + ".xlsx");
        }

        public static string LegacyWorkbookPath(string productFolder, string cipher)
        {
            return Path.Combine(productFolder, LegacyWorkbookPrefix + SafeFileName(cipher) + ".xlsx");
        }

        /// <summary>
        /// Книга изделия, которая есть на диске: ЛЗК в «04_Сопроводительная документация», иначе ЛЗК прямо в папке (сборка
        /// вне структуры заказа), иначе ведомость старого образца в корне (по шифру, затем любая); пусто — ни одной.
        /// </summary>
        public static string FindWorkbook(string productFolder, string cipher)
        {
            if (string.IsNullOrEmpty(productFolder) || !Directory.Exists(productFolder)) return "";
            string path = WorkbookPath(productFolder, cipher);
            if (File.Exists(path)) return path;
            string docs = Path.Combine(productFolder, DocsFolder);
            string any = Directory.Exists(docs) ? Directory.GetFiles(docs, WorkbookPrefix + "*.xlsx").FirstOrDefault(NotLock) : null;
            if (any != null) return any;
            path = Path.Combine(productFolder, WorkbookPrefix + SafeFileName(cipher) + ".xlsx");
            if (File.Exists(path)) return path;
            path = LegacyWorkbookPath(productFolder, cipher);
            if (File.Exists(path)) return path;
            return Directory.GetFiles(productFolder, LegacyWorkbookPrefix + "*.xlsx").FirstOrDefault(NotLock) ?? "";
        }

        /// <summary>Книга старого образца (до ТЗ-04): без паспорта, участков и калькулятора.</summary>
        public static bool IsLegacy(string workbookPath)
        {
            return (Path.GetFileName(workbookPath) ?? "").StartsWith(LegacyWorkbookPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool NotLock(string path)
        {
            return !(Path.GetFileName(path) ?? "").StartsWith("~$", StringComparison.Ordinal);
        }

        /// <summary>Куда убрать прежнюю ведомость: _Аннулировано\Ведомость_&lt;шифр&gt;_&lt;дата_время&gt;.xlsx (без затирания).</summary>
        public static string ArchivePath(string productFolder, string cipher, DateTime stamp)
        {
            return ArchivePath(productFolder, cipher, stamp, false);
        }

        /// <summary>legacy — ведомость старого образца: _Аннулировано\Ведомость_&lt;шифр&gt;_&lt;дата_время&gt;.xlsx.</summary>
        public static string ArchivePath(string productFolder, string cipher, DateTime stamp, bool legacy)
        {
            string dir = Path.Combine(productFolder, ArchiveFolder);
            string stem = (legacy ? LegacyWorkbookPrefix : WorkbookPrefix) + SafeFileName(cipher) + "_" +
                stamp.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
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

        /// <summary>Заготовку режут из листа, а не из хлыста: лист «Расход» считает такие метры не в хлыстах (Т-40).</summary>
        public static bool IsSheet(string material)
        {
            foreach (string word in SheetStock)
                if (HasWord(material ?? "", word)) return true;
            return false;
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
        /// <summary>Модель базы эталонов или библиотеки («02_БАЗА», «_Библиотека проектирования»): её файл не правится.</summary>
        public bool InBase;
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
        /// <summary>Свойство «Раздел» (раздел спецификации): по нему — категория строки.</summary>
        public string Section = "";
        /// <summary>Масса 1 шт., кг, из ведомости SWTools; NaN — не известна.</summary>
        public double MassKg = double.NaN;
    }

    public sealed class LzkHeader
    {
        public string Product = "";
        public string Cipher = "";
        public string Name = "";
        public string Order = "";
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
        /// <summary>Строк на листах участков.</summary>
        public readonly Dictionary<string, int> SectionRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Issues = new List<string>();
        /// <summary>Пояснения в отчёт, не замечания: на итог проверки изделия не влияют.</summary>
        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>
    /// Дописывание ведомости, выгруженной SWTools по шаблону «Ведомость_ЛЗК.xlsx»: шапка, пометки «?» (ТЗ-02 Т-35…Т-37)
    /// и живая книга ЛЗК — паспорт, участки, расход, нормы (ТЗ-04, <see cref="LzkBook"/>). Колонки основной таблицы
    /// находятся по именованным диапазонам шаблона.
    /// </summary>
    public static class LzkWorkbook
    {
        public const string Mark = "?";
        /// <summary>Шрифт книги: есть на каждом рабочем месте, в таблице читается лучше Times New Roman.</summary>
        public const string FontFace = "Arial";

        public static readonly string[] RequiredNames =
        {
            "Номер", "Обозначение", "Наименование", "Материал_Строка", "Габарит", "МассаЕдКг", "Количество", "Операции", "Путь"
        };

        public static LzkResult Complete(string xlsxPath, LzkHeader header, IList<LzkItem> items)
        {
            return Complete(xlsxPath, header, items, null);
        }

        public static LzkResult Complete(string xlsxPath, LzkHeader header, IList<LzkItem> items, LzkBook.Options options)
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

            // У исполнений одного файла путь общий: строка SWTools сопоставляется конфигурации по обозначению,
            // затем по количеству (иначе обе строки «Укосины» 00 и 01 получали бы реквизиты первой).
            Dictionary<string, List<LzkItem>> byPath = new Dictionary<string, List<LzkItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (LzkItem item in items)
            {
                List<LzkItem> list;
                if (!byPath.TryGetValue(item.Path, out list)) byPath[item.Path] = list = new List<LzkItem>();
                list.Add(item);
            }
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<LzkItem> used = new HashSet<LzkItem>();
            List<LzkBook.MainRow> mainRows = new List<LzkBook.MainRow>();

            foreach (int row in main.RowNumbers.Where(r => r > headerRow))
            {
                string path = main.Get(col["Путь"], row).Trim();
                string designation = main.Get(col["Обозначение"], row).Trim();
                if (path.Length == 0 && designation.Length == 0 && main.Get(col["Наименование"], row).Trim().Length == 0) continue;
                result.Rows++;
                string label = "Строка " + main.Get(col["Номер"], row).Trim() + " (" +
                    (designation.Length > 0 ? designation : main.Get(col["Наименование"], row).Trim()) + ")";
                LzkItem item = Match(byPath, path, designation, LzkOperations.ParseNumber(main.Get(col["Количество"], row)), used);
                if (path.Length > 0) seen.Add(path);
                mainRows.Add(new LzkBook.MainRow { Row = row, Item = item });
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
                if (item != null && !double.IsNaN(mass) && mass > 0) item.MassKg = mass;
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
                    // «*» — к сведению, а не пометка «?»: длина правдоподобна, её только уточнить (решение владельца 18.09.2026).
                    result.Notes.Add(label + ": длины заготовки в списке вырезов нет — измерена по модели (*)");
                }
            }

            List<string> missing = items.Where(i => !i.IsPurchased && !i.IsTop && !seen.Contains(i.Path))
                .Select(i => i.Designation.Length > 0 ? i.Designation : System.IO.Path.GetFileNameWithoutExtension(i.Path))
                .Distinct().ToList();
            if (missing.Count > 0)
                result.Issues.Add("В ведомости нет изготавливаемых моделей (" + missing.Count + "): " + string.Join(", ", missing.ToArray()));

            // Шаблон SWTools собран из китайского образца: без этого вся книга печатается шрифтом 宋体.
            book.NormalizeFonts(FontFace);
            LzkBook.Write(book, sheetName, col, headerRow, mainRows, items, header, options, result);

            if (header != null)
            {
                SetName(book, main, "Шапка_Изделие", header.Product);
                SetName(book, main, "Шапка_Составил", header.Author);
                SetName(book, main, "Шапка_Модель", header.Model);
                SetName(book, main, "Шапка_Дата", header.Date);
                SetName(book, main, "Шапка_КонтрольнаяСумма", header.Checksum);
            }
            SetName(book, main, "Шапка_Замечания", result.Issues.Count == 0 ? "нет" : result.Issues.Count + " (пометки «?» в таблице)");
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

        /// <summary>Модель строки SWTools: по пути, при нескольких конфигурациях — по обозначению, затем по количеству.</summary>
        private static LzkItem Match(Dictionary<string, List<LzkItem>> byPath, string path, string designation, double quantity,
            HashSet<LzkItem> used)
        {
            List<LzkItem> list;
            if (path.Length == 0 || !byPath.TryGetValue(path, out list) || list.Count == 0) return null;
            LzkItem found = list.FirstOrDefault(i => !used.Contains(i) && designation.Length > 0 &&
                    string.Equals(i.Designation, designation, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(i => !used.Contains(i) && !double.IsNaN(quantity) && i.Quantity == (int)Math.Round(quantity))
                ?? list.FirstOrDefault(i => !used.Contains(i))
                ?? list[0];
            used.Add(found);
            return found;
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

        /// <summary>
        /// Сбой доставки данных из SolidWorks, а не ошибка в изделии. Надстройка SWTools шлёт в SWTools.exe два пакета —
        /// дерево изделия и состав; пакет уходит через SendMessageTimeout с флагом «бросить, если окно занято», и при
        /// занятом окне теряется молча. SWTools этого не замечает и говорит о следствии: узел не выбран, данных нет.
        /// Модель тут ни при чём — выгрузку надо просто повторить (владелец, 20.09.2026: «Не выбрано ни одного узла»).
        /// </summary>
        public static bool DeliveryLost(Outcome o)
        {
            if (o == null || !o.Found || o.Ok || o.ExitCode != 1) return false;
            string error = (o.Error ?? "").Trim();
            foreach (string known in LostDeliveryErrors)
            {
                if (error.StartsWith(known, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Сообщения SWTools, которыми оборачивается потерянный пакет (client-src/ZTool/Frmmain.cs).</summary>
        private static readonly string[] LostDeliveryErrors =
        {
            "Не выбрано ни одного узла",
            "Нет данных для экспорта"
        };

        /// <summary>Причина, понятная конструктору, когда пакет потерялся и повтор не помог.</summary>
        public const string DeliveryLostExplanation =
            "SolidWorks не передал SWTools состав изделия — пакет данных потерялся, повтор не помог. " +
            "Закройте лишние документы и окно SWTools, если открыто, и нажмите «Ведомость ЛЗК» ещё раз.";

        /// <summary>Понятное конструктору объяснение кода завершения SWTools.</summary>
        public static string Explain(Outcome o, int processExitCode)
        {
            if (DeliveryLost(o)) return DeliveryLostExplanation;
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
