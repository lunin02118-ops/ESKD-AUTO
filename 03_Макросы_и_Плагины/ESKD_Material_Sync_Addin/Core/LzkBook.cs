using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Один участок бланка ЛЗК: какие операции к нему относятся, указания и подписи (ТЗ-04 Р4-4).</summary>
    public sealed class LzkBlank
    {
        public string Section = "";
        public readonly List<string> Operations = new List<string>();
        public string Notes = "";
        public readonly List<string> Signatures = new List<string>();
    }

    /// <summary>
    /// Участки и тексты бланков ЛЗК. По умолчанию — решение владельца 18.09.2026; справочник
    /// «ЛЗК_бланки.xlsx» (лист «Участки»: участок, операции через «;», указания, подписи через «;»), найденный
    /// вверх по папкам от изделия, заменяет их — формулировки правит владелец, не программист (ТЗ-04 Т4-3).
    /// </summary>
    public sealed class LzkBlanks
    {
        public const string FileName = "ЛЗК_бланки.xlsx";
        public const string SheetName = "Участки";

        public const string Blank = "Заготовительный";
        public const string Welding = "Сварочный";
        public const string Painting = "Покрасочный";
        public const string Kitting = "Комплектовочный";
        /// <summary>Участки в порядке маршрута — в этом порядке идут листы книги.</summary>
        public static readonly string[] All = { Blank, Welding, Painting, Kitting };

        public const string CategoryAssembly = "Сборка";
        public const string CategoryPart = "Деталь";
        public const string CategoryPurchased = "Покупное";
        public const string CategoryOther = "Прочее";
        public const string CategoryMaterial = "Материал";

        private readonly Dictionary<string, LzkBlank> _sections = new Dictionary<string, LzkBlank>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Откуда взяты бланки; пусто — значения по умолчанию.</summary>
        public string SourcePath { get; private set; }

        private LzkBlanks()
        {
            SourcePath = "";
        }

        public LzkBlank Get(string section)
        {
            LzkBlank blank;
            return _sections.TryGetValue(section ?? "", out blank) ? blank : new LzkBlank { Section = section ?? "" };
        }

        public static LzkBlanks Defaults()
        {
            LzkBlanks b = new LzkBlanks();
            b.Add(Blank, new[]
                {
                    LzkOperations.SheetCutting, LzkOperations.TubeCutting, LzkOperations.Bending,
                    "Труборез", "Лазерный раскрой", "Лазерная резка", "Резка", "Пила", "Рубка"
                },
                "Раскрой — по DXF (03_ЧПУ\\Лазер_Лист) и IGS (03_ЧПУ\\Труборез) изделия. Заусенцы удалить, заготовки маркировать обозначением.");
            b.Add(Welding, new[] { LzkOperations.WeldedAssembly, "Сварка" },
                "Сварка — по чертежам сборочных единиц (02_PDF). Швы зачистить, брызги металла удалить.");
            b.Add(Painting, new[] { LzkOperations.Painting, "Окраска", "Порошковая покраска" },
                "Порошковая окраска, цвет — в шапке листа. Перед окраской — обезжиривание.");
            b.Add(Kitting, new[] { LzkOperations.MechanicalAssembly, "Сборка", "Комплектация" },
                "Комплектовать по листу. Покупные и стандартные изделия — со склада по коду 1С.");
            return b;
        }

        private void Add(string section, string[] operations, string notes)
        {
            LzkBlank blank = new LzkBlank { Section = section, Notes = notes };
            blank.Operations.AddRange(operations);
            // Подписей на листах участков по умолчанию нет (решение владельца 19.09.2026, В-3); нужны — справочник, столбец D.
            _sections[section] = blank;
        }

        /// <summary>
        /// Справочник бланков вверх по папкам от <paramref name="folder"/>, иначе — в папке справочников инструментария
        /// (<paramref name="referenceFolder"/>); пусто — нет.
        /// </summary>
        public static string Find(string folder, string referenceFolder)
        {
            for (string current = folder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                string candidate = Path.Combine(current, FileName);
                if (File.Exists(candidate)) return candidate;
            }
            string shared = string.IsNullOrEmpty(referenceFolder) ? "" : Path.Combine(referenceFolder, FileName);
            return shared.Length > 0 && File.Exists(shared) ? shared : "";
        }

        /// <summary>
        /// Бланки из справочника поверх значений по умолчанию. Файл не читается — значения по умолчанию
        /// и объяснение в <paramref name="problem"/>: книга ЛЗК из-за текста бланка не должна не собраться.
        /// </summary>
        public static LzkBlanks Read(string path, out string problem)
        {
            problem = "";
            LzkBlanks blanks = Defaults();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return blanks;
            try
            {
                using (XlsxBook book = XlsxBook.Open(path))
                {
                    XlsxSheet sheet = book.Sheet(SheetName);
                    if (sheet == null)
                    {
                        problem = "В справочнике «" + FileName + "» нет листа «" + SheetName + "» — бланки по умолчанию.";
                        return blanks;
                    }
                    foreach (int row in sheet.RowNumbers)
                    {
                        string section = All.FirstOrDefault(s => string.Equals(s, sheet.Get(1, row).Trim(), StringComparison.OrdinalIgnoreCase));
                        if (section == null) continue;
                        LzkBlank blank = blanks.Get(section);
                        List<string> operations = Split(sheet.Get(2, row));
                        if (operations.Count > 0)
                        {
                            blank.Operations.Clear();
                            blank.Operations.AddRange(operations);
                        }
                        string notes = sheet.Get(3, row).Trim();
                        if (notes.Length > 0) blank.Notes = notes;
                        List<string> signatures = Split(sheet.Get(4, row));
                        if (signatures.Count > 0)
                        {
                            blank.Signatures.Clear();
                            blank.Signatures.AddRange(signatures);
                        }
                    }
                }
                blanks.SourcePath = path;
            }
            catch (Exception ex)
            {
                Log.Error("ЛЗК: справочник бланков " + path, ex);
                problem = "Справочник «" + FileName + "» не читается (" + ex.Message + ") — бланки по умолчанию.";
            }
            return blanks;
        }

        private static List<string> Split(string value)
        {
            return (value ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        /// <summary>Участок операции; пусто — операция ни к одному участку не отнесена.</summary>
        public string SectionOf(string operation)
        {
            string op = (operation ?? "").Trim();
            foreach (string section in All)
                if (Get(section).Operations.Any(o => string.Equals(o, op, StringComparison.OrdinalIgnoreCase))) return section;
            return "";
        }

        /// <summary>
        /// Участки строки в порядке маршрута. Покупное, материал и прочее идут на комплектовку целиком;
        /// операции, не отнесённые ни к одному участку, попадают в <paramref name="unknown"/>.
        /// </summary>
        public List<string> SectionsOf(LzkItem item, ICollection<string> unknown)
        {
            List<string> result = new List<string>();
            if (KittedWhole(item))
            {
                result.Add(Kitting);
                return result;
            }
            foreach (string op in LzkOperations.Parse(item.Operations))
            {
                string section = SectionOf(op);
                if (section.Length == 0)
                {
                    if (unknown != null && !unknown.Contains(op)) unknown.Add(op);
                    continue;
                }
                if (!result.Contains(section)) result.Add(section);
            }
            return All.Where(result.Contains).ToList();
        }

        /// <summary>
        /// Покупное, материал и прочее идут на комплектовку целиком: не режутся, не свариваются, не красятся и не входят
        /// в металлопрокат «Расхода». Одно правило для участков, «Расхода» и «Сводной» — иначе труба с «Разделом»
        /// «Материалы» считалась и в прокате, и в покупных (аудит 21.09.2026, Л-2).
        /// </summary>
        public static bool KittedWhole(LzkItem item)
        {
            string category = Category(item);
            return item.IsPurchased || category == CategoryPurchased || category == CategoryMaterial || category == CategoryOther;
        }

        /// <summary>В «Операциях» есть покраска — по справочнику участков, со всеми её названиями («Окраска», «Порошковая покраска»).</summary>
        public bool HasPainting(string operations)
        {
            return LzkOperations.Parse(operations).Any(op => SectionOf(op) == Painting);
        }

        /// <summary>Сколько штук на изделие красится отдельно, а не в составе окрашиваемого узла (Л-3).</summary>
        public static int PaintCount(LzkItem item)
        {
            if (item.PaintQuantity >= 0) return Math.Min(item.PaintQuantity, item.Quantity);
            return item.InsidePaintedUnit ? 0 : item.Quantity;
        }

        /// <summary>Категория строки по свойству «Раздел» (ТЗ-04 §2); нет свойства — по признакам модели.</summary>
        public static string Category(LzkItem item)
        {
            string section = (item.Section ?? "").ToLowerInvariant();
            if (section.Contains("сборочн")) return CategoryAssembly;
            if (section.Contains("детал")) return CategoryPart;
            if (section.Contains("стандартн") || section.Contains("покупн")) return CategoryPurchased;
            if (section.Contains("материал")) return CategoryMaterial;
            if (section.Contains("прочи") || section.Contains("комплект")) return CategoryOther;
            if (item.IsPurchased) return CategoryPurchased;
            return item.IsAssembly ? CategoryAssembly : CategoryPart;
        }
    }

    /// <summary>
    /// Что пользователь ввёл в книге ЛЗК: тираж, срок, цвет, нормы под заказ, указания участков. Читается из
    /// прежней книги перед её переносом в «_Аннулировано» и переносится в новую (ТЗ-04 §3, К-4).
    /// </summary>
    public sealed class LzkInputs
    {
        public int Quantity = 1;
        public DateTime? Deadline;
        public string Color = "";
        public string Order = "";
        public string Request = "";
        public readonly Dictionary<string, double> Norms = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Значения прочитаны из прежней книги.</summary>
        public bool FromWorkbook;
        /// <summary>Прежняя книга есть, но не прочиталась: введённое в ней перенести нельзя — затирать её нельзя.</summary>
        public string ReadError = "";

        public static LzkInputs Read(string workbookPath)
        {
            LzkInputs inputs = new LzkInputs();
            if (string.IsNullOrEmpty(workbookPath) || !File.Exists(workbookPath)) return inputs;
            try
            {
                using (XlsxBook book = XlsxBook.Open(workbookPath))
                {
                    string v;
                    if (Value(book, LzkBook.NameQuantity, out v))
                    {
                        double q = LzkOperations.ParseNumber(v);
                        if (!double.IsNaN(q) && q >= 1) inputs.Quantity = (int)Math.Round(q);
                        inputs.FromWorkbook = true;
                    }
                    if (Value(book, LzkBook.NameDeadline, out v)) inputs.Deadline = ParseDate(v);
                    if (Value(book, LzkBook.NameColor, out v)) inputs.Color = v.Trim();
                    if (Value(book, LzkBook.NameOrder, out v)) inputs.Order = v.Trim();
                    if (Value(book, LzkBook.NameRequest, out v)) inputs.Request = v.Trim();
                    foreach (LzkBook.NormRow norm in LzkBook.NormRows)
                        if (Value(book, norm.Name, out v))
                        {
                            double n = LzkOperations.ParseNumber(v);
                            if (!double.IsNaN(n)) inputs.Norms[norm.Key] = n;
                        }
                    foreach (string section in LzkBlanks.All)
                        if (Value(book, LzkBook.NotesPrefix + section, out v) && v.Trim().Length > 0) inputs.Notes[section] = v;
                }
            }
            catch (Exception ex)
            {
                Log.Error("ЛЗК: чтение введённого в " + workbookPath, ex);
                inputs.ReadError = ex.Message;
            }
            return inputs;
        }

        private static bool Value(XlsxBook book, string name, out string value)
        {
            value = "";
            string sheet, cell;
            if (!book.TryResolveName(name, out sheet, out cell)) return false;
            XlsxSheet s = book.Sheet(sheet);
            if (s == null) return false;
            value = s.Get(cell) ?? "";
            return true;
        }

        /// <summary>Дата из ячейки: число Excel или текст «25.10.2026»; иначе null.</summary>
        public static DateTime? ParseDate(string text)
        {
            string value = (text ?? "").Trim();
            if (value.Length == 0) return null;
            double serial;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out serial) && serial > 1 && serial < 2958465)
                return DateTime.FromOADate(serial).Date;
            DateTime parsed;
            if (DateTime.TryParse(value, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out parsed)) return parsed.Date;
            return null;
        }
    }

    /// <summary>
    /// Живая книга ЛЗК изделия (ТЗ-04, вариант Б): к листу «Ведомость» SWTools добавляются «Паспорт», листы
    /// участков, калькулятор «Расход» и «Нормы». Строки пишет надстройка — состав зависит только от модели;
    /// всё, что зависит от тиража и норм, считают формулы Excel через имена «Тираж», «Хлыст», «Рез»… (Т4-1).
    /// Формулы — без кэша значений, книга пересчитывается при открытии.
    /// </summary>
    public static class LzkBook
    {
        public const string PassportSheet = "Паспорт";
        public const string SummarySheet = "Сводная";
        public const string CostSheet = "Расход";
        public const string NormsSheet = "Нормы";

        public const string NameQuantity = "Тираж";
        public const string NameDeadline = "Срок";
        public const string NameColor = "Цвет";
        /// <summary>Запас к норме в чистоте, % — нормативы листа «Нормы» (владелец 21.09.2026).</summary>
        private const string AllowanceBar = "ЗапасТруба";
        private const string AllowanceSheet = "ЗапасЛист";
        public const string NameIssued = "Выдано";
        public const string NameOrder = "Паспорт_Заказ";
        public const string NameRequest = "Паспорт_Заявка";
        public const string NotesPrefix = "Указания_";

        public const string Mark = LzkWorkbook.Mark;

        /// <summary>Норматив листа «Нормы»: ключ справочника, имя ячейки, единица, пояснение.</summary>
        public sealed class NormRow
        {
            public string Key;
            public string Name;
            public string Unit;
            public string Title;
            public string Format;
            /// <summary>Значение, когда норматива нет ни в справочнике, ни в прежней книге; NaN — нет умолчания (будет 0).</summary>
            public double Default = double.NaN;

            public NormRow(string key, string name, string unit, string title, string format)
            {
                Key = key;
                Name = name;
                Unit = unit;
                Title = title;
                Format = format;
            }

            public NormRow(string key, string name, string unit, string title, string format, double defaultValue)
                : this(key, name, unit, title, format)
            {
                Default = defaultValue;
            }
        }

        public static readonly NormRow[] NormRows =
        {
            new NormRow("Труба.Хлыст", "Хлыст", "мм", "Длина хлыста", "0"),
            new NormRow("Труба.Захват", "Захват", "мм", "Захват станка — эта часть хлыста не режется", "0"),
            new NormRow("Труба.Торцовка", "Торцовка", "мм", "Торцовка — один конец хлыста; второй конец уходит в захват", "0"),
            new NormRow("Труба.Рез", "Рез", "мм", "Ширина реза", "0.0"),
            new NormRow("Труба.Деловой", "Деловой", "мм", "Деловой остаток — не короче", "0"),
            new NormRow("Лист.Ширина", "ЛистШирина", "мм", "Лист: ширина", "0"),
            new NormRow("Лист.Длина", "ЛистДлина", "мм", "Лист: длина", "0"),
            new NormRow("Лист.Отход", "ЛистОтход", "коэф.", "Коэффициент отхода листа (площадь заготовок × коэф.)", "0.00"),
            new NormRow("Краска.Норма", "КраскаНорма", "г/м²", "Расход порошковой краски", "0"),
            new NormRow("Краска.Потери", "КраскаПотери", "%", "Потери краски", "0"),
            new NormRow("Краска.Тара", "КраскаТара", "кг", "Масса краски в таре", "0.0"),
            // Запас к норме в чистоте («Сводная»): владелец 21.09.2026 — свой на трубу и на лист, правится под заказ
            new NormRow("Запас.Труба", "ЗапасТруба", "%", "Запас к норме в чистоте на трубу и профиль (резы, брак) — лист «Сводная»", "0", 10),
            new NormRow("Запас.Лист", "ЗапасЛист", "%", "Запас к норме в чистоте на листовой прокат — лист «Сводная»", "0", 10)
        };

        /// <summary>Всё, что нужно для книги, кроме самой выгрузки SWTools.</summary>
        public sealed class Options
        {
            public LzkInputs Inputs = new LzkInputs();
            public Norms Norms;
            public LzkBlanks Blanks = LzkBlanks.Defaults();
        }

        /// <summary>Строка основного листа, сопоставленная модели.</summary>
        public sealed class MainRow
        {
            public int Row;
            public LzkItem Item;
        }

        /// <summary>Значение норматива: введённое в прежней книге, иначе из справочника (формат листа — пара чисел).</summary>
        public static double NormValue(string key, LzkInputs inputs, Norms norms)
        {
            double value;
            if (inputs != null && inputs.Norms.TryGetValue(key, out value)) return value;
            if (norms == null || norms.Text(key).Length == 0)
            {
                NormRow row = NormRows.FirstOrDefault(n => n.Key == key);
                if (row != null && !double.IsNaN(row.Default)) return row.Default;
                if (norms == null) return 0;
            }
            if (key == "Лист.Ширина" || key == "Лист.Длина")
            {
                double width, length;
                norms.Pair("Лист.Формат", out width, out length);
                return key == "Лист.Ширина" ? width : length;
            }
            return norms.Number(key);
        }

        /// <summary>Длина заготовки из «Габарита»: «L=1996» — точно; «556×40×20*» — наибольший размер, оценка.</summary>
        public static double BlankLength(string size, out bool estimate)
        {
            estimate = false;
            string text = (size ?? "").Trim();
            Match m = Regex.Match(text, @"L\s*=\s*([0-9]+(?:[.,][0-9]+)?)", RegexOptions.IgnoreCase);
            if (m.Success) return Norms.ParseNumber(m.Groups[1].Value);
            estimate = true;
            double[] sides = Sides(text);
            return sides.Length > 0 ? sides[0] : 0;
        }

        /// <summary>Размеры габарита по убыванию.</summary>
        public static double[] Sides(string size)
        {
            return (size ?? "").Replace("*", "").Split(new[] { '×', 'x', 'X', 'х', 'Х' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Norms.ParseNumber(s.Trim())).Where(v => v > 0).OrderByDescending(v => v).ToArray();
        }

        /// <summary>Заготовку режут из хлыста: металлопрокат не из листа, не сборка, не покупное.</summary>
        public static bool IsBarStock(LzkItem item)
        {
            return !item.IsAssembly && !LzkBlanks.KittedWhole(item) && !item.IsTop &&
                LzkMaterials.Kind(item.Material) == MaterialKind.RolledMetal && !LzkMaterials.IsSheet(item.Material);
        }

        public static bool IsSheetStock(LzkItem item)
        {
            return !item.IsAssembly && !LzkBlanks.KittedWhole(item) && !item.IsTop &&
                LzkMaterials.Kind(item.Material) == MaterialKind.RolledMetal && LzkMaterials.IsSheet(item.Material);
        }

        /// <summary>Группа раскроя: одинаковые заготовки одного сортамента (ТЗ-04 Р4-5, «пакет одинаковых длин»).</summary>
        public sealed class BarGroup
        {
            public string Sortament = "";
            public double LengthMm;
            public int PerProduct;
            public bool Estimate;
            /// <summary>Масса 1 м, кг; NaN — масса деталей не известна.</summary>
            public double KgPerMeter = double.NaN;
            public readonly List<string> Designations = new List<string>();
        }

        /// <summary>Заготовки из хлыста по сортаментам и длинам (длина — до 0,1 мм), в порядке сортамента и убывания длины.</summary>
        public static List<BarGroup> BarGroups(IEnumerable<LzkItem> items)
        {
            Dictionary<string, BarGroup> groups = new Dictionary<string, BarGroup>(StringComparer.CurrentCultureIgnoreCase);
            Dictionary<string, List<double>> masses = new Dictionary<string, List<double>>();
            foreach (LzkItem item in items.Where(IsBarStock))
            {
                bool estimate;
                double length = Math.Round(BlankLength(item.Size, out estimate), 1);
                if (length <= 0 || item.Quantity <= 0) continue;
                string key = item.Material.Trim() + "" + length.ToString("0.0", CultureInfo.InvariantCulture);
                BarGroup g;
                if (!groups.TryGetValue(key, out g))
                {
                    g = new BarGroup { Sortament = item.Material.Trim(), LengthMm = length };
                    groups[key] = g;
                    masses[key] = new List<double>();
                }
                g.PerProduct += item.Quantity;
                g.Estimate |= estimate || item.SizeIsEstimate;
                if (item.Designation.Length > 0 && !g.Designations.Contains(item.Designation)) g.Designations.Add(item.Designation);
                if (!double.IsNaN(item.MassKg) && item.MassKg > 0) masses[key].Add(item.MassKg / (length / 1000.0));
            }
            foreach (KeyValuePair<string, BarGroup> kv in groups)
                if (masses[kv.Key].Count > 0) kv.Value.KgPerMeter = masses[kv.Key].Average();
            return groups.Values.OrderBy(g => g.Sortament, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(g => g.LengthMm).ToList();
        }

        /// <summary>Листовые заготовки: площадь (две большие стороны габарита) и масса на 1 изделие по сортаментам.</summary>
        public static List<KeyValuePair<string, double[]>> SheetGroups(IEnumerable<LzkItem> items)
        {
            Dictionary<string, double[]> groups = new Dictionary<string, double[]>(StringComparer.CurrentCultureIgnoreCase);
            foreach (LzkItem item in items.Where(IsSheetStock))
            {
                double[] sides = Sides(item.Size);
                double area = sides.Length >= 2 ? sides[0] * sides[1] / 1000000.0 : 0;
                double[] sum;
                if (!groups.TryGetValue(item.Material.Trim(), out sum))
                {
                    sum = new double[2];
                    groups[item.Material.Trim()] = sum;
                }
                sum[0] += area * item.Quantity;
                if (!double.IsNaN(item.MassKg)) sum[1] += item.MassKg * item.Quantity;
            }
            return groups.OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// Строки листа участка: единицы (без главной сборки, если у неё нет операции этого участка). На «Комплектовочном»
        /// главной сборки нет никогда: комплектуют то, из чего собирают изделие, а не само изделие (замечание владельца
        /// 21.09.2026). На «Покрасочном» — только то, что красится отдельно от окрашиваемых узлов.
        /// </summary>
        public static List<LzkItem> SectionItems(IEnumerable<LzkItem> items, string section, LzkBlanks blanks)
        {
            return items.Where(i => blanks.SectionsOf(i, null).Contains(section))
                .Where(i => section != LzkBlanks.Kitting || !i.IsTop)
                .Where(i => section != LzkBlanks.Painting || LzkBlanks.PaintCount(i) > 0)
                .ToList();
        }

        // ------------------------------------------------------------------ запись
        public static void Write(XlsxBook book, string mainSheet, IDictionary<string, int> col, int headerRow,
            IList<MainRow> rows, IList<LzkItem> items, LzkHeader header, Options options, LzkResult result)
        {
            if (options == null) options = new Options();
            LzkInputs inputs = options.Inputs ?? new LzkInputs();
            Norms norms = options.Norms ?? Norms.Empty();
            LzkBlanks blanks = options.Blanks ?? LzkBlanks.Defaults();
            Styles st = new Styles(book);
            List<LzkItem> made = items.Where(i => !(i.IsTop && !i.IsAssembly)).ToList();

            List<string> unknown = new List<string>();
            foreach (LzkItem item in made) blanks.SectionsOf(item, unknown);
            foreach (string op in unknown)
                result.Issues.Add("Операция «" + op + "» не отнесена ни к одному участку — поправьте «Операции» или справочник " + LzkBlanks.FileName);

            Passport(book, st, header, inputs);
            List<KittingRow> kitting = new List<KittingRow>();
            foreach (string section in LzkBlanks.All)
                result.SectionRows[section] = SectionSheet(book, st, section, made, header, inputs, blanks, kitting);
            result.PaintRows = result.SectionRows[LzkBlanks.Painting];
            result.PurchasedRows = PurchasedGroups(made).Count;
            CostRows cost = Cost(book, st, made, SectionItems(made, LzkBlanks.Painting, blanks), header, result, inputs, norms);
            Summary(book, st, header, inputs, norms, cost, kitting);
            NormsSheetWrite(book, st, inputs, norms);
            MainColumns(book, mainSheet, col, headerRow, rows, blanks);

            book.MoveSheet(PassportSheet, 0);
            // «Сводная» — сразу за «Ведомостью»: сколько материала идёт на заказ, а не раскрой по деталям
            book.MoveSheet(SummarySheet, 2);
            book.SetActiveSheet(PassportSheet);
            book.RecalculateOnOpen();
        }

        // ------------------------------------------------------------------ стили
        private sealed class Styles
        {
            public const string Input = "FFFFF2CC";
            private const string HeadFill = "FFF2F2F2";
            private const double Body = 10;
            public readonly int Title, Subtitle, Label, Value, InputText, InputInt, InputDate, InputNumber, Head, Text, Center,
                Int, Area, Dec1, Dec3, Percent, Total, TotalLeft, TotalInt, TotalDec1, TotalArea, TotalPercent, Note, Group, Notes, HeadValue,
                HeadDate, NoteWrap;

            public Styles(XlsxBook b)
            {
                Title = b.AddStyle(new XlsxStyle { Bold = true, Size = 12, Horizontal = "left" });
                Subtitle = b.AddStyle(new XlsxStyle { Gray = true, Size = 9, Horizontal = "left" });
                Label = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Wrap = true, Horizontal = "left", Size = Body, Fill = HeadFill });
                Value = b.AddStyle(new XlsxStyle { Border = true, Wrap = true, Horizontal = "left", Size = Body });
                InputText = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Wrap = true, Horizontal = "left", Size = Body, Fill = Input });
                InputInt = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "left", Size = Body, Fill = Input, NumberFormat = "0" });
                InputDate = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "left", Size = Body, Fill = Input, NumberFormat = "dd.mm.yyyy" });
                InputNumber = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "center", Size = Body, Fill = Input });
                Head = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Wrap = true, Horizontal = "center", Fill = HeadFill, Size = Body });
                Text = b.AddStyle(new XlsxStyle { Border = true, Wrap = true, Horizontal = "left", Size = Body });
                Center = b.AddStyle(new XlsxStyle { Border = true, Horizontal = "center", Size = Body });
                Int = b.AddStyle(new XlsxStyle { Border = true, Horizontal = "center", Size = Body, NumberFormat = "0" });
                Area = b.AddStyle(new XlsxStyle { Border = true, Horizontal = "center", Size = Body, NumberFormat = "0.000" });
                Dec1 = b.AddStyle(new XlsxStyle { Border = true, Horizontal = "center", Size = Body, NumberFormat = "0.0" });
                Dec3 = b.AddStyle(new XlsxStyle { Border = true, Horizontal = "center", Size = Body, NumberFormat = "0.000" });
                Percent = b.AddStyle(new XlsxStyle { Border = true, Horizontal = "center", Size = Body, NumberFormat = "0.0%" });
                Total = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "right", Size = Body });
                TotalLeft = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "left", Size = Body });
                TotalInt = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "center", Size = Body, NumberFormat = "0" });
                TotalDec1 = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "center", Size = Body, NumberFormat = "0.0" });
                TotalArea = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "center", Size = Body, NumberFormat = "0.000" });
                TotalPercent = b.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "center", Size = Body, NumberFormat = "0.0%" });
                Note = b.AddStyle(new XlsxStyle { Gray = true, Horizontal = "left", Size = 9 });
                NoteWrap = b.AddStyle(new XlsxStyle { Gray = true, Horizontal = "left", Size = 9, Wrap = true, Vertical = "top" });
                Group = b.AddStyle(new XlsxStyle { Bold = true, Horizontal = "left", Size = Body });
                Notes = b.AddStyle(new XlsxStyle { Border = true, Wrap = true, Horizontal = "left", Vertical = "top", Size = Body });
                HeadValue = b.AddStyle(new XlsxStyle { Bold = true, Horizontal = "left", Size = Body });
                HeadDate = b.AddStyle(new XlsxStyle { Bold = true, Horizontal = "left", Size = Body, NumberFormat = "dd.mm.yyyy" });
            }
        }

        private static string C(int column, int row)
        {
            return XlsxBook.CellName(column, row);
        }

        private static string L(int column)
        {
            string name = XlsxBook.CellName(column, 1);
            return name.Substring(0, name.Length - 1);
        }

        // ------------------------------------------------------------------ Паспорт
        private static void Passport(XlsxBook book, Styles st, LzkHeader header, LzkInputs inputs)
        {
            header = header ?? new LzkHeader();
            XlsxSheet s = book.AddSheet(PassportSheet);
            s.SetColumnWidth(1, 30);
            s.SetColumnWidth(2, 60);
            s.SetText("A1", "Паспорт ЛЗК изделия", st.Title);
            s.Merge("A1:B1");
            s.SetRowHeight(1, 22);
            s.SetText("A2", "Жёлтые ячейки заполняются вручную: листы участков и «Расход» пересчитываются сами. " +
                "Повторная «Ведомость ЛЗК» введённое сохраняет.", st.NoteWrap);
            s.Merge("A2:B2");
            s.SetRowHeight(2, 26);
            int row = 4;
            Line(s, st, row++, "Изделие", header.Product, st.Value);
            Line(s, st, row++, "Шифр", header.Cipher, st.Value);
            Line(s, st, row++, "Наименование", header.Name, st.Value);
            Line(s, st, row++, "Главная сборка", header.Model, st.Value);
            string order = inputs.Order.Length > 0 ? inputs.Order : header.Order;
            Line(s, st, row, "Заказ", order, st.InputText);
            book.DefineName(NameOrder, PassportSheet, C(2, row++));
            Line(s, st, row, "№ заявки (служебной записки)", inputs.Request, st.InputText);
            book.DefineName(NameRequest, PassportSheet, C(2, row++));
            s.SetText(C(1, row), "Изделий в заказе, шт.", st.Label);
            s.SetNumber(C(2, row), Math.Max(1, inputs.Quantity), st.InputInt);
            s.ValidateWholeNumber(C(2, row), 1, "Количество изделий — целое число не меньше 1.");
            book.DefineName(NameQuantity, PassportSheet, C(2, row++));
            s.SetText(C(1, row), "Срок отгрузки", st.Label);
            if (inputs.Deadline.HasValue) s.SetNumber(C(2, row), inputs.Deadline.Value.ToOADate(), st.InputDate);
            else s.SetText(C(2, row), "", st.InputDate);
            book.DefineName(NameDeadline, PassportSheet, C(2, row++));
            Line(s, st, row, "Цвет покраски (RAL)", inputs.Color, st.InputText);
            book.DefineName(NameColor, PassportSheet, C(2, row++));
            Line(s, st, row++, "Составил", header.Author, st.Value);
            Line(s, st, row++, "Сформировано", header.Date, st.Value);
            Line(s, st, row++, "Контрольная сумма сборки", header.Checksum, st.Value);
            Line(s, st, row, "Готово к производству", "", st.Value);
            book.DefineName(NameIssued, PassportSheet, C(2, row++));
            s.FitToWidth(false);
            book.SetPrintNames(PassportSheet, "$A$1:$B$" + row, 0);
        }

        private static void Line(XlsxSheet s, Styles st, int row, string label, string value, int style)
        {
            s.SetText(C(1, row), label, st.Label);
            s.SetText(C(2, row), value ?? "", style);
        }

        /// <summary>Шапка листа: название, изделие, тираж и срок (формулами из «Паспорта»).</summary>
        private static void SheetHead(XlsxSheet s, Styles st, string title, LzkHeader header, int columns, bool color)
        {
            string last = L(columns);
            s.SetText("A1", title, st.Title);
            s.Merge("A1:" + last + "1");
            s.SetRowHeight(1, 22);
            string product = header == null ? "" : (header.Product ?? "");
            string date = header == null ? "" : (header.Date ?? "");
            s.SetText("A2", (product.Length > 0 ? "Изделие: " + product : "") + (date.Length > 0 ? ";  ЛЗК от " + date : ""), st.Subtitle);
            s.Merge("A2:" + last + "2");
            // «Расход» начинается широкой колонкой сортамента — подписи там в A и D:F, у листов участков — в B и D.
            bool wide = columns > 10;
            s.SetText(wide ? "A3" : "B3", "Изделий в заказе, шт.:", st.Group);
            s.SetFormula(wide ? "B3" : "C3", NameQuantity, st.HeadValue);
            s.SetText("D3", "Срок отгрузки:", st.Group);
            if (wide) s.Merge("D3:F3");
            s.SetFormula(wide ? "G3" : "E3", "IF(" + NameDeadline + "=\"\",\"не указан\"," + NameDeadline + ")", st.HeadDate);
            if (wide) s.Merge("G3:H3");
            if (color)
            {
                // У широкого листа D3:F3 и G3:H3 заняты сроком: цвет правее, иначе подпись «Цвет:» пряталась под слиянием,
                // а формула цвета затирала срок отгрузки в G3.
                s.SetText(wide ? "I3" : "F3", "Цвет:", st.Group);
                s.SetFormula(wide ? "J3" : "G3", "IF(" + NameColor + "=\"\",\"не указан\"," + NameColor + ")", st.HeadValue);
                if (wide) s.Merge("J3:" + last + "3");
            }
        }

        private static void Heads(XlsxSheet s, Styles st, int row, string[] titles, double[] widths)
        {
            for (int c = 0; c < titles.Length; c++)
            {
                s.SetText(C(c + 1, row), titles[c], st.Head);
                s.SetColumnWidth(c + 1, widths[c]);
            }
            s.SetRowHeight(row, 30);
        }

        // ------------------------------------------------------------------ листы участков
        private const int SectionHeadRow = 5;

        /// <summary>Строка покупного на «Комплектовочном» — на неё ссылается «Сводная».</summary>
        private sealed class KittingRow
        {
            public PurchasedGroup Group;
            public int Row;
        }

        private static int SectionSheet(XlsxBook book, Styles st, string section, List<LzkItem> items, LzkHeader header,
            LzkInputs inputs, LzkBlanks blanks, List<KittingRow> kitting)
        {
            XlsxSheet s = book.AddSheet(section);
            List<LzkItem> list = SectionItems(items, section, blanks);
            string[] titles;
            double[] widths;
            if (section == LzkBlanks.Blank)
            {
                titles = new[] { "№", "Обозначение", "Наименование", "Материал (сортамент)", "Заготовка, мм", "Операции", "На 1 изд., шт.", "Всего, шт." };
                widths = new double[] { 5, 22, 28, 34, 16, 24, 10, 10 };
            }
            else if (section == LzkBlanks.Painting)
            {
                titles = new[] { "№", "Обозначение", "Наименование", "Площадь 1 шт., м²", "На 1 изд., шт.", "Всего, шт.", "Площадь всего, м²" };
                widths = new double[] { 5, 24, 34, 13, 10, 10, 14 };
            }
            else if (section == LzkBlanks.Kitting)
            {
                titles = new[] { "№", "Обозначение", "Наименование", "Код 1С", "Ед. изм.", "На 1 изд.", "Всего" };
                widths = new double[] { 5, 24, 36, 12, 8, 10, 10 };
            }
            else
            {
                titles = new[] { "№", "Обозначение", "Наименование", "Операции", "На 1 изд., шт.", "Всего, шт." };
                widths = new double[] { 5, 24, 36, 30, 10, 10 };
            }
            int columns = titles.Length;
            SheetHead(s, st, section + " участок", header, columns, section == LzkBlanks.Painting);
            Heads(s, st, SectionHeadRow, titles, widths);
            int row = SectionHeadRow + 1;
            int count = 0;
            int first = row;

            if (section == LzkBlanks.Blank)
            {
                foreach (LzkItem i in list.OrderBy(i => i.Material, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(i => i.Designation, StringComparer.CurrentCultureIgnoreCase))
                {
                    s.SetNumber(C(1, row), ++count, st.Center);
                    s.SetText(C(2, row), i.Designation, st.Text);
                    s.SetText(C(3, row), i.Name, st.Text);
                    s.SetText(C(4, row), i.Material.Length > 0 ? i.Material : Mark, st.Text);
                    s.SetText(C(5, row), i.Size + (i.SizeIsEstimate && !i.Size.EndsWith("*") ? "*" : ""), st.Center);
                    s.SetText(C(6, row), i.Operations, st.Text);
                    s.SetNumber(C(7, row), i.Quantity, st.Int);
                    s.SetFormula(C(8, row), "G" + row + "*" + NameQuantity, st.Int);
                    row++;
                }
                row = Totals(s, st, list.Count, first, row, columns, new[] { 7, 8 }, st.TotalInt, "Итого заготовок");
            }
            else if (section == LzkBlanks.Painting)
            {
                foreach (LzkItem i in list.OrderBy(i => i.Designation, StringComparer.CurrentCultureIgnoreCase))
                {
                    s.SetNumber(C(1, row), ++count, st.Center);
                    s.SetText(C(2, row), i.Designation, st.Text);
                    s.SetText(C(3, row), i.Name, st.Text);
                    if (double.IsNaN(i.AreaM2)) s.SetText(C(4, row), Mark, st.Center);
                    else s.SetNumber(C(4, row), Math.Round(i.AreaM2, 3), st.Area);
                    s.SetNumber(C(5, row), LzkBlanks.PaintCount(i), st.Int);
                    s.SetFormula(C(6, row), "E" + row + "*" + NameQuantity, st.Int);
                    s.SetFormula(C(7, row), "IF(ISNUMBER(D" + row + "),D" + row + "*F" + row + ",\"" + Mark + "\")", st.Area);
                    row++;
                }
                int totalRow = row;
                row = Totals(s, st, list.Count, first, row, columns, new[] { 5, 6 }, st.TotalInt, "Итого");
                if (list.Count > 0)
                {
                    s.SetFormula(C(7, totalRow), "SUM(G" + first + ":G" + (totalRow - 1) + ")", st.TotalArea);
                    row++;
                    s.SetText(C(2, row), "Краска, кг (норма и потери — лист «Нормы»)", st.Group);
                    s.SetFormula(C(7, row), "G" + totalRow + "*КраскаНорма/1000*(1+КраскаПотери/100)", st.TotalDec1);
                    row++;
                    s.SetText(C(2, row), "Тара, шт.", st.Group);
                    s.SetFormula(C(7, row), "IF(КраскаТара>0,ROUNDUP(G" + (row - 1) + "/КраскаТара,0),\"\")", st.TotalInt);
                    row++;
                }
            }
            else if (section == LzkBlanks.Kitting)
            {
                List<LzkItem> units = list.Where(i => i.IsAssembly && !i.IsPurchased)
                    .OrderBy(i => i.Designation, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (units.Count > 0)
                {
                    s.SetText(C(1, row), "Сборочные единицы (механическая сборка)", st.Group);
                    s.Merge("A" + row + ":" + L(columns) + row);
                    row++;
                    foreach (LzkItem i in units)
                    {
                        s.SetNumber(C(1, row), ++count, st.Center);
                        s.SetText(C(2, row), i.Designation, st.Text);
                        s.SetText(C(3, row), i.Name, st.Text);
                        s.SetText(C(4, row), "", st.Center);
                        s.SetText(C(5, row), "шт", st.Center);
                        s.SetNumber(C(6, row), i.Quantity, st.Int);
                        s.SetFormula(C(7, row), "F" + row + "*" + NameQuantity, st.Int);
                        row++;
                    }
                }
                List<PurchasedGroup> bought = PurchasedGroups(list);
                if (bought.Count > 0)
                {
                    s.SetText(C(1, row), "Покупные и стандартные изделия, материалы", st.Group);
                    s.Merge("A" + row + ":" + L(columns) + row);
                    row++;
                    foreach (PurchasedGroup g in bought)
                    {
                        string unit, okei;
                        LzkWorkbook.Unit(g.First.Unit, out unit, out okei);
                        s.SetNumber(C(1, row), ++count, st.Center);
                        s.SetText(C(2, row), g.First.Designation, st.Text);
                        s.SetText(C(3, row), g.First.Name.Length > 0 ? g.First.Name : Path.GetFileNameWithoutExtension(g.First.Path), st.Text);
                        // Код 1С — из справочника снабжения; нет — ячейка пустая, это не забытый реквизит конструктора.
                        s.SetText(C(4, row), g.First.Code.Trim(), st.Center);
                        s.SetText(C(5, row), unit, st.Center);
                        s.SetNumber(C(6, row), g.Quantity, st.Int);
                        s.SetFormula(C(7, row), "F" + row + "*" + NameQuantity, st.Int);
                        if (kitting != null) kitting.Add(new KittingRow { Group = g, Row = row });
                        row++;
                    }
                }
                if (count == 0) row = Totals(s, st, 0, first, row, columns, new int[0], st.TotalInt, "");
            }
            else
            {
                foreach (LzkItem i in list.OrderBy(i => i.Designation, StringComparer.CurrentCultureIgnoreCase))
                {
                    s.SetNumber(C(1, row), ++count, st.Center);
                    s.SetText(C(2, row), i.Designation, st.Text);
                    s.SetText(C(3, row), i.Name, st.Text);
                    s.SetText(C(4, row), i.Operations, st.Text);
                    s.SetNumber(C(5, row), i.Quantity, st.Int);
                    s.SetFormula(C(6, row), "E" + row + "*" + NameQuantity, st.Int);
                    row++;
                }
                row = Totals(s, st, list.Count, first, row, columns, new[] { 5, 6 }, st.TotalInt, "Итого");
            }

            int tableEnd = row - 1;
            row++;
            LzkBlank blank = blanks.Get(section);
            s.SetText(C(1, row), "Технологические указания", st.Group);
            s.Merge("A" + row + ":" + L(columns) + row);
            row++;
            string notes;
            if (!inputs.Notes.TryGetValue(section, out notes)) notes = blank.Notes;
            s.SetText(C(1, row), notes, st.Notes);
            for (int c = 2; c <= columns; c++) s.SetText(C(c, row), "", st.Notes);
            s.Merge("A" + row + ":" + L(columns) + row);
            s.SetRowHeight(row, 60);
            book.DefineName(NotesPrefix + section, section, C(1, row));
            row += 2;
            foreach (string who in blank.Signatures)
            {
                s.SetText(C(2, row), who, st.Group);
                s.SetText(C(3, row), "_______________ / _______________ /     «___» __________ 20__ г.", st.Group);
                row += 2;
            }
            s.FreezeRowsAbove("A" + (SectionHeadRow + 1));
            s.FitToWidth(false);
            book.SetPrintNames(section, "$A$1:$" + L(columns) + "$" + Math.Max(row - 1, tableEnd), SectionHeadRow);
            return count;
        }

        /// <summary>Строка «Итого» с суммами колонок или пометка «нет единиц». Возвращает следующую строку.</summary>
        private static int Totals(XlsxSheet s, Styles st, int count, int first, int row, int columns, int[] sums, int style, string title)
        {
            if (count == 0)
            {
                s.SetText(C(1, row), "Единиц для этого участка в изделии нет", st.Note);
                s.Merge("A" + row + ":" + L(columns) + row);
                return row + 1;
            }
            int labelColumn = sums.Length > 0 ? sums.Min() - 1 : columns;
            for (int c = 1; c <= columns; c++) s.SetText(C(c, row), "", st.Total);
            s.SetText(C(labelColumn, row), title, st.Total);
            foreach (int c in sums) s.SetFormula(C(c, row), "SUM(" + L(c) + first + ":" + L(c) + (row - 1) + ")", style);
            return row + 1;
        }

        public sealed class PurchasedGroup
        {
            public LzkItem First;
            public int Quantity;
        }

        /// <summary>Покупные, материалы и прочие изделия (не сборочные единицы) — одной строкой на позицию.</summary>
        public static List<PurchasedGroup> PurchasedGroups(IEnumerable<LzkItem> items)
        {
            string[] kitted = { LzkBlanks.CategoryPurchased, LzkBlanks.CategoryMaterial, LzkBlanks.CategoryOther };
            return items.Where(i => !i.IsTop && (i.IsPurchased || (!i.IsAssembly && kitted.Contains(LzkBlanks.Category(i)))))
                .GroupBy(i => (i.Designation + "" + i.Name + "" + i.Code).ToLowerInvariant())
                .Select(g => new PurchasedGroup { First = g.First(), Quantity = g.Sum(i => i.Quantity) })
                .OrderBy(g => g.First.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        // ------------------------------------------------------------------ Расход
        /// <summary>Где на «Расходе» итоги по позициям — на них формулами ссылается «Сводная».</summary>
        private sealed class CostRows
        {
            public sealed class Bar
            {
                public string Sortament;
                public int Row;
                public List<BarGroup> Groups;
            }

            public readonly List<Bar> Bars = new List<Bar>();
            public readonly List<KeyValuePair<string, int>> Sheets = new List<KeyValuePair<string, int>>();
            public int PaintAreaRow, PaintKgRow, PaintTaraRow;
        }

        private static CostRows Cost(XlsxBook book, Styles st, List<LzkItem> items, List<LzkItem> painted, LzkHeader header, LzkResult result,
            LzkInputs inputs, Norms norms)
        {
            CostRows layout = new CostRows();
            XlsxSheet s = book.AddSheet(CostSheet);
            // Расход — только сколько идёт на заготовки: чистая длина и она же с запасом по норме. Хлысты, КИМ и закупка
            // убраны — закупка и списание будут отдельным документом (владелец 21.09.2026).
            string[] titles =
            {
                "Сортамент", "L заготовки, мм", "На 1 изд., шт.", "Всего, шт.", "Чистая длина на 1 изд., м",
                "Чистая длина на заказ, м", "Запас, %", "С запасом на заказ, м", "Масса 1 м, кг", "Масса в чистоте, кг",
                "Масса с запасом, кг", "Примечание"
            };
            double[] widths = { 36, 10, 9, 9, 11, 11, 8, 11, 9, 10, 10, 26 };
            int columns = titles.Length;
            SheetHead(s, st, "Расход материалов на заказ", header, columns, false);
            int row = 5;
            s.SetText(C(1, row), "Трубы, профиль и сортовой прокат — чистая длина заготовок и она же с запасом (запас — лист «Нормы»)", st.Group);
            s.Merge("A" + row + ":" + L(columns) + row);
            row++;
            Heads(s, st, row, titles, widths);
            int head = row;
            row++;
            List<BarGroup> groups = BarGroups(items);
            List<int> subtotals = new List<int>();
            int[] barSums = { 5, 6, 8, 10, 11 };
            foreach (IGrouping<string, BarGroup> sortament in groups.GroupBy(g => g.Sortament, StringComparer.CurrentCultureIgnoreCase))
            {
                int first = row;
                foreach (BarGroup g in sortament)
                {
                    string r = row.ToString(CultureInfo.InvariantCulture);
                    s.SetText(C(1, row), g.Sortament, st.Text);
                    s.SetNumber(C(2, row), g.LengthMm, st.Dec1);
                    s.SetNumber(C(3, row), g.PerProduct, st.Int);
                    s.SetFormula(C(4, row), "C" + r + "*" + NameQuantity, st.Int);
                    s.SetFormula(C(5, row), "C" + r + "*B" + r + "/1000", st.Dec3);
                    s.SetFormula(C(6, row), "D" + r + "*B" + r + "/1000", st.Dec1);
                    s.SetFormula(C(7, row), AllowanceBar, st.Int);
                    s.SetFormula(C(8, row), "F" + r + "*(1+G" + r + "/100)", st.Dec1);
                    if (double.IsNaN(g.KgPerMeter)) s.SetText(C(9, row), Mark, st.Center);
                    else s.SetNumber(C(9, row), Math.Round(g.KgPerMeter, 3), st.Dec3);
                    s.SetFormula(C(10, row), "IF(ISNUMBER(I" + r + "),F" + r + "*I" + r + ",\"\")", st.Dec1);
                    s.SetFormula(C(11, row), "IF(ISNUMBER(I" + r + "),H" + r + "*I" + r + ",\"\")", st.Dec1);
                    s.SetText(C(12, row), g.Estimate ? "L измерена по модели — уточните длину заготовки" : "", st.Text);
                    if (g.Estimate)
                        result.Notes.Add("Расход: " + g.Sortament + ", L=" + LzkOperations.Number(g.LengthMm) +
                            " — длина заготовки измерена по модели, не из списка вырезов (" + string.Join(", ", g.Designations.ToArray()) + ")");
                    row++;
                }
                // Итог назван по сортаменту: в «Сводной» он одна строка (замечание владельца 21.09.2026).
                s.SetText(C(1, row), "Итого: " + sortament.Key, st.TotalLeft);
                for (int c = 2; c <= columns; c++) s.SetText(C(c, row), "", st.Total);
                foreach (int c in barSums)
                    s.SetFormula(C(c, row), "SUM(" + L(c) + first + ":" + L(c) + (row - 1) + ")", c == 5 ? st.TotalArea : st.TotalDec1);
                s.SetFormula(C(7, row), AllowanceBar, st.TotalInt);
                layout.Bars.Add(new CostRows.Bar { Sortament = sortament.Key, Row = row, Groups = sortament.ToList() });
                subtotals.Add(row);
                row++;
            }
            if (groups.Count == 0)
            {
                s.SetText(C(1, row), "Заготовок из проката в изделии нет", st.Note);
                s.Merge("A" + row + ":" + L(columns) + row);
                row++;
            }
            else if (subtotals.Count > 1)
            {
                s.SetText(C(1, row), "Всего по трубам и профилю", st.TotalLeft);
                for (int c = 2; c <= columns; c++) s.SetText(C(c, row), "", st.Total);
                foreach (int c in new[] { 10, 11 })
                    s.SetFormula(C(c, row), string.Join("+", subtotals.Select(t => L(c) + t).ToArray()), st.TotalDec1);
                row++;
            }
            int barEnd = row - 1;

            // Лист
            row++;
            s.SetText(C(1, row), "Листовой прокат — площадь и масса заготовок и они же с запасом (запас — лист «Нормы»)", st.Group);
            s.Merge("A" + row + ":" + L(columns) + row);
            row++;
            string[] sheetTitles =
            {
                "Сортамент", "Площадь на 1 изд., м²", "Площадь на заказ, м²", "Запас, %", "С запасом на заказ, м²",
                "Масса на 1 изд., кг", "Масса в чистоте, кг", "Масса с запасом, кг"
            };
            for (int c = 0; c < sheetTitles.Length; c++) s.SetText(C(c + 1, row), sheetTitles[c], st.Head);
            s.SetRowHeight(row, 30);
            row++;
            List<KeyValuePair<string, double[]>> sheets = SheetGroups(items);
            foreach (KeyValuePair<string, double[]> g in sheets)
            {
                string r = row.ToString(CultureInfo.InvariantCulture);
                s.SetText(C(1, row), g.Key, st.Text);
                s.SetNumber(C(2, row), Math.Round(g.Value[0], 4), st.Area);
                s.SetFormula(C(3, row), "B" + r + "*" + NameQuantity, st.Area);
                s.SetFormula(C(4, row), AllowanceSheet, st.Int);
                s.SetFormula(C(5, row), "C" + r + "*(1+D" + r + "/100)", st.Area);
                s.SetNumber(C(6, row), Math.Round(g.Value[1], 3), st.Dec3);
                s.SetFormula(C(7, row), "F" + r + "*" + NameQuantity, st.Dec1);
                s.SetFormula(C(8, row), "G" + r + "*(1+D" + r + "/100)", st.Dec1);
                layout.Sheets.Add(new KeyValuePair<string, int>(g.Key, row));
                row++;
            }
            if (sheets.Count == 0)
            {
                s.SetText(C(1, row), "Листовых заготовок в изделии нет", st.Note);
                row++;
            }

            // Покраска
            row++;
            s.SetText(C(1, row), "Покраска — по площади окрашиваемых единиц (лист «Покрасочный»)", st.Group);
            s.Merge("A" + row + ":" + L(columns) + row);
            row++;
            double area = 0;
            bool unknownArea = false;
            // Те же строки и штуки, что на «Покрасочном»: площадь «Расхода» и «Сводной» с листом участка не расходится.
            foreach (LzkItem i in painted)
            {
                if (double.IsNaN(i.AreaM2)) unknownArea = true;
                else area += i.AreaM2 * LzkBlanks.PaintCount(i);
            }
            int areaRow = row;
            s.SetText(C(1, row), "Площадь покраски на 1 изделие, м²", st.Label);
            s.SetNumber(C(2, row), Math.Round(area, 3), st.Area);
            if (unknownArea) s.SetText(C(3, row), "без единиц с «" + Mark + "» в площади", st.Note);
            row++;
            s.SetText(C(1, row), "Площадь покраски на заказ, м²", st.Label);
            s.SetFormula(C(2, row), "B" + areaRow + "*" + NameQuantity, st.Area);
            layout.PaintAreaRow = row;
            row++;
            s.SetText(C(1, row), "Краска, кг", st.Label);
            s.SetFormula(C(2, row), "B" + (row - 1) + "*КраскаНорма/1000*(1+КраскаПотери/100)", st.Dec1);
            layout.PaintKgRow = row;
            row++;
            s.SetText(C(1, row), "Тара, шт.", st.Label);
            s.SetFormula(C(2, row), "IF(КраскаТара>0,ROUNDUP(B" + (row - 1) + "/КраскаТара,0),\"\")", st.Int);
            layout.PaintTaraRow = row;
            row++;
            s.SetText(C(1, row), "Цвет", st.Label);
            s.SetFormula(C(2, row), "IF(" + NameColor + "=\"\",\"не указан\"," + NameColor + ")", st.Center);
            row += 2;
            s.SetText(C(1, row), "Покупные и стандартные изделия — лист «Комплектовочный». Нормы расчёта — лист «Нормы».", st.Note);
            s.FreezeRowsAbove("A" + (head + 1));
            s.FitToWidth(true);
            book.SetPrintNames(CostSheet, "$A$1:$" + L(columns) + "$" + Math.Max(row, barEnd), head);
            return layout;
        }

        // ------------------------------------------------------------------ Сводная
        private static string Ref(string sheet, int column, int row)
        {
            return "'" + sheet.Replace("'", "''") + "'!" + C(column, row);
        }

        /// <summary>
        /// Одна строка на позицию (замечание владельца 21.09.2026): металлопрокат по сортаментам, краска, покупные и крепёж.
        /// Только сколько идёт на заготовки: в чистоте на изделие и на заказ, запас в % с листа «Нормы» (ЗапасТруба /
        /// ЗапасЛист) и то же с запасом. Хлысты, закупка и списание сюда не входят — это отдельный документ (владелец 21.09.2026).
        /// Числа берутся формулами с «Расхода» и «Комплектовочного», поэтому при смене тиража листы не расходятся.
        /// </summary>
        private static void Summary(XlsxBook book, Styles st, LzkHeader header, LzkInputs inputs, Norms norms,
            CostRows cost, List<KittingRow> kitting)
        {
            XlsxSheet s = book.AddSheet(SummarySheet);
            string[] titles =
            {
                "№", "Сортамент, материал, изделие", "Ед.", "В чистоте на 1 изд.", "В чистоте на заказ", "Запас, %",
                "С запасом на заказ", "Масса в чистоте на 1 изд., кг", "Масса в чистоте на заказ, кг", "Масса с запасом, кг", "Примечание"
            };
            double[] widths = { 5, 44, 7, 11, 11, 8, 11, 11, 11, 11, 40 };
            const int note = 11;
            int columns = titles.Length;
            SheetHead(s, st, "Сводная ведомость расхода материалов", header, columns, true);
            int row = 5;
            s.SetText(C(1, row), "Числа — с листов «Расход» и «Комплектовочный», при смене тиража в «Паспорте» пересчитываются. " +
                "«В чистоте» — длина, площадь и масса заготовок; «с запасом» — плюс запас с листа «Нормы»: " +
                "свой для трубы (ЗапасТруба) и листа (ЗапасЛист).", st.NoteWrap);
            s.Merge("A" + row + ":" + L(columns) + row);
            s.SetRowHeight(row, 30);
            row++;
            Heads(s, st, row, titles, widths);
            int head = row;
            row++;
            int number = 0;

            // Металлопрокат
            s.SetText(C(1, row), "Металлопрокат — трубы, профиль и сортовой прокат по сортаментам, листовой прокат", st.Group);
            s.Merge("A" + row + ":" + L(columns) + row);
            row++;
            int metalFirst = row;
            foreach (CostRows.Bar bar in cost.Bars)
            {
                s.SetNumber(C(1, row), ++number, st.Center);
                s.SetText(C(2, row), bar.Sortament, st.Text);
                s.SetText(C(3, row), "м", st.Center);
                s.SetFormula(C(4, row), Ref(CostSheet, 5, bar.Row), st.Dec3);
                s.SetFormula(C(5, row), Ref(CostSheet, 6, bar.Row), st.Dec1);
                s.SetFormula(C(6, row), Ref(CostSheet, 7, bar.Row), st.Int);
                s.SetFormula(C(7, row), Ref(CostSheet, 8, bar.Row), st.Dec1);
                // Масса на изделие: длина на изделие × масса 1 м по строкам сортамента на «Расходе» (без «?» в массе 1 м).
                int barFirst = bar.Row - bar.Groups.Count;
                s.SetFormula(C(8, row), "SUMPRODUCT(" + Ref(CostSheet, 5, barFirst) + ":" + C(5, bar.Row - 1) + "," +
                    Ref(CostSheet, 9, barFirst) + ":" + C(9, bar.Row - 1) + ")", st.Dec3);
                s.SetFormula(C(9, row), Ref(CostSheet, 10, bar.Row), st.Dec1);
                s.SetFormula(C(10, row), Ref(CostSheet, 11, bar.Row), st.Dec1);
                s.SetText(C(note, row), "", st.Text);
                row++;
            }
            foreach (KeyValuePair<string, int> sheet in cost.Sheets)
            {
                s.SetNumber(C(1, row), ++number, st.Center);
                s.SetText(C(2, row), sheet.Key, st.Text);
                s.SetText(C(3, row), "м²", st.Center);
                s.SetFormula(C(4, row), Ref(CostSheet, 2, sheet.Value), st.Area);
                s.SetFormula(C(5, row), Ref(CostSheet, 3, sheet.Value), st.Area);
                s.SetFormula(C(6, row), Ref(CostSheet, 4, sheet.Value), st.Int);
                s.SetFormula(C(7, row), Ref(CostSheet, 5, sheet.Value), st.Area);
                s.SetFormula(C(8, row), Ref(CostSheet, 6, sheet.Value), st.Dec3);
                s.SetFormula(C(9, row), Ref(CostSheet, 7, sheet.Value), st.Dec1);
                s.SetFormula(C(10, row), Ref(CostSheet, 8, sheet.Value), st.Dec1);
                s.SetText(C(note, row), "площадь и масса заготовок", st.Text);
                row++;
            }
            if (row == metalFirst)
            {
                s.SetText(C(1, row), "Металлопроката в изделии нет", st.Note);
                s.Merge("A" + row + ":" + L(columns) + row);
                row++;
            }
            else
            {
                s.SetText(C(1, row), "Итого металлопрокат, кг", st.TotalLeft);
                for (int c = 2; c <= columns; c++) s.SetText(C(c, row), "", st.Total);
                foreach (int c in new[] { 8, 9, 10 })
                    s.SetFormula(C(c, row), "SUM(" + L(c) + metalFirst + ":" + L(c) + (row - 1) + ")", c == 8 ? st.TotalArea : st.TotalDec1);
                row++;
            }

            // Покраска
            row++;
            s.SetText(C(1, row), "Покраска — лист «Покрасочный», нормы — лист «Нормы»", st.Group);
            s.Merge("A" + row + ":" + L(columns) + row);
            row++;
            s.SetNumber(C(1, row), ++number, st.Center);
            s.SetText(C(2, row), "Площадь покраски на заказ", st.Text);
            s.SetText(C(3, row), "м²", st.Center);
            for (int c = 4; c <= columns; c++) s.SetText(C(c, row), "", st.Text);
            s.SetFormula(C(5, row), Ref(CostSheet, 2, cost.PaintAreaRow), st.Area);
            row++;
            s.SetNumber(C(1, row), ++number, st.Center);
            s.SetFormula(C(2, row), "\"Краска порошковая, \"&IF(" + NameColor + "=\"\",\"цвет не указан\",\"цвет \"&" + NameColor + ")", st.Text);
            s.SetText(C(3, row), "кг", st.Center);
            for (int c = 4; c <= columns; c++) s.SetText(C(c, row), "", st.Text);
            s.SetFormula(C(5, row), Ref(CostSheet, 2, cost.PaintKgRow), st.Dec1);
            s.SetText(C(note, row), "норма и потери — лист «Нормы»", st.Text);
            row++;
            s.SetNumber(C(1, row), ++number, st.Center);
            s.SetText(C(2, row), "Тара с краской", st.Text);
            s.SetText(C(3, row), "шт", st.Center);
            for (int c = 4; c <= columns; c++) s.SetText(C(c, row), "", st.Text);
            s.SetFormula(C(5, row), Ref(CostSheet, 2, cost.PaintTaraRow), st.Int);
            row++;

            // Покупные и крепёж
            row++;
            s.SetText(C(1, row), "Покупные и стандартные изделия, крепёж, материалы — лист «Комплектовочный»", st.Group);
            s.Merge("A" + row + ":" + L(columns) + row);
            row++;
            if (kitting.Count == 0)
            {
                s.SetText(C(1, row), "Покупных в изделии нет", st.Note);
                s.Merge("A" + row + ":" + L(columns) + row);
                row++;
            }
            foreach (KittingRow k in kitting)
            {
                string unit, okei;
                LzkWorkbook.Unit(k.Group.First.Unit, out unit, out okei);
                string name = k.Group.First.Name.Length > 0 ? k.Group.First.Name : Path.GetFileNameWithoutExtension(k.Group.First.Path);
                s.SetNumber(C(1, row), ++number, st.Center);
                s.SetText(C(2, row), (k.Group.First.Designation + " " + name).Trim(), st.Text);
                s.SetText(C(3, row), unit, st.Center);
                for (int c = 4; c <= columns; c++) s.SetText(C(c, row), "", st.Text);
                s.SetFormula(C(4, row), Ref(LzkBlanks.Kitting, 6, k.Row), st.Int);
                s.SetFormula(C(5, row), Ref(LzkBlanks.Kitting, 7, k.Row), st.Int);
                s.SetText(C(note, row), k.Group.First.Code.Trim().Length > 0 ? "код 1С: " + k.Group.First.Code.Trim() : "", st.Text);
                row++;
            }

            s.FreezeRowsAbove("A" + (head + 1));
            s.FitToWidth(true);
            book.SetPrintNames(SummarySheet, "$A$1:$" + L(columns) + "$" + (row - 1), head);
        }

        // ------------------------------------------------------------------ Нормы
        private static void NormsSheetWrite(XlsxBook book, Styles st, LzkInputs inputs, Norms norms)
        {
            XlsxSheet s = book.AddSheet(NormsSheet);
            s.SetColumnWidth(1, 18);
            s.SetColumnWidth(2, 12);
            s.SetColumnWidth(3, 8);
            s.SetColumnWidth(4, 60);
            s.SetText("A1", "Нормы расхода для этого изделия", st.Title);
            s.Merge("A1:D1");
            string source = norms.SourcePath.Length > 0
                ? "Взяты из справочника " + norms.SourcePath
                : "Справочник «" + Norms.FileName + "» не найден";
            if (inputs.Norms.Count > 0) source = "Значения прежней книги ЛЗК (правка под заказ сохраняется). " + source;
            s.SetText("A2", source, st.NoteWrap);
            s.Merge("A2:D2");
            s.SetRowHeight(2, 26);
            string[] titles = { "Норматив", "Значение", "Ед.", "Пояснение" };
            for (int c = 0; c < titles.Length; c++) s.SetText(C(c + 1, 4), titles[c], st.Head);
            int row = 5;
            foreach (NormRow n in NormRows)
            {
                s.SetText(C(1, row), n.Key, st.Text);
                s.SetNumber(C(2, row), NormValue(n.Key, inputs, norms), st.InputNumber);
                s.SetText(C(3, row), n.Unit, st.Center);
                s.SetText(C(4, row), n.Title, st.Text);
                book.DefineName(n.Name, NormsSheet, C(2, row));
                row++;
            }
            row++;
            s.SetText(C(1, row), "Жёлтые значения можно править под этот заказ: «Расход», «Покрасочный» и «Сводная» пересчитаются.", st.Note);
            s.FitToWidth(false);
            book.SetPrintNames(NormsSheet, "$A$1:$D$" + row, 4);
        }

        // ------------------------------------------------------------------ колонки «Ведомости»
        private static void MainColumns(XlsxBook book, string mainSheet, IDictionary<string, int> col, int headerRow,
            IList<MainRow> rows, LzkBlanks blanks)
        {
            XlsxSheet main = book.Sheet(mainSheet);
            if (main == null || rows.Count == 0) return;
            int start = col.Values.Max() + 1;
            int head = main.StyleOf(C(col["Операции"], headerRow));
            main.SetText(C(start, headerRow), "Категория", head);
            main.SetText(C(start + 1, headerRow), "Участки", head);
            main.SetText(C(start + 2, headerRow), "Всего на заказ, шт.", head);
            main.SetColumnWidth(start, 12);
            main.SetColumnWidth(start + 1, 24);
            main.SetColumnWidth(start + 2, 11);
            string quantity = L(col["Количество"]);
            foreach (MainRow r in rows)
            {
                int text = main.StyleOf(C(col["Операции"], r.Row));
                int number = main.StyleOf(C(col["Количество"], r.Row));
                LzkItem item = r.Item;
                main.SetText(C(start, r.Row), item == null ? "" : LzkBlanks.Category(item), text);
                main.SetText(C(start + 1, r.Row), item == null ? "" : string.Join(", ", blanks.SectionsOf(item, null).ToArray()), text);
                main.SetFormula(C(start + 2, r.Row), quantity + r.Row + "*" + NameQuantity, number);
            }
            int last = rows.Max(r => r.Row);
            book.SetAutoFilter(mainSheet, C(1, headerRow) + ":" + C(start + 2, last));
            book.FitPicturesToCells(mainSheet);
        }
    }
}
