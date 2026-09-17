using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Строка ведомости изделия, прочитанная для сводной заявки.</summary>
    public sealed class RequestLine
    {
        public string Designation = "";
        public string Name = "";
        public string Material = "";
        public string Size = "";
        public string Operations = "";
        public double MassKg;
        public double AreaM2 = double.NaN;
        public int Quantity = 1;
        public bool IsAssembly;
        public string Code = "";
        public string Unit = "шт";
        public string Okei = "796";
        /// <summary>Файл модели из ведомости: по нему видно, что позиция лежит в папке покупных.</summary>
        public string Path = "";

        /// <summary>Длина заготовки из габарита «L=1996» или «1996×50×25» — по ней считается раскрой.</summary>
        public double LengthMm
        {
            get
            {
                Match m = Regex.Match(Size ?? "", @"L\s*=\s*([0-9]+(?:[.,][0-9]+)?)", RegexOptions.IgnoreCase);
                if (m.Success) return Norms.ParseNumber(m.Groups[1].Value);
                string[] parts = (Size ?? "").Split(new[] { '×', 'x', 'X', 'х', 'Х' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 ? Norms.ParseNumber(parts[0]) : 0;
            }
        }
    }

    /// <summary>Изделие в заявке: его ведомость и тираж.</summary>
    public sealed class RequestProduct
    {
        public string Cipher = "";
        public string Name = "";
        public string WorkbookPath = "";
        public int Quantity = 1;
        public readonly List<RequestLine> Lines = new List<RequestLine>();
        public readonly List<RequestLine> Painted = new List<RequestLine>();
        public readonly List<RequestLine> Purchased = new List<RequestLine>();
        /// <summary>Пометки «?» ведомости: с ними К-5 заявку не принимает (Т-37, Т-39).</summary>
        public readonly List<string> Marks = new List<string>();
    }

    /// <summary>Строка сводной по сортаменту (лист 5).</summary>
    public sealed class ConsumptionRow
    {
        public string Sortament = "";
        public double CleanM;
        public int Bars;
        public double BoughtM;
        public string Kim = "";
        public double BusinessM;
        public double WasteM;
        public int Sheets;
        public double AreaM2;
    }

    /// <summary>Сводная заявка: пять листов по образцу 85_Т.</summary>
    public sealed class ProdRequestData
    {
        public string Number = "";
        public string Order = "";
        public string Chief = "";
        public DateTime Date = DateTime.Now;
        public readonly List<RequestProduct> Products = new List<RequestProduct>();
        /// <summary>Лист 1 — заготовки: строка на деталь, количество умножено на тираж.</summary>
        public readonly List<RequestLine> Blanks = new List<RequestLine>();
        /// <summary>Лист 2 — сборочные единицы.</summary>
        public readonly List<RequestLine> Units = new List<RequestLine>();
        /// <summary>Лист 3 — окрашиваемые единицы с цветом.</summary>
        public readonly List<RequestLine> Painting = new List<RequestLine>();
        /// <summary>Лист 4 — покупные.</summary>
        public readonly List<RequestLine> Purchased = new List<RequestLine>();
        /// <summary>Лист 5 — расход по сортаментам.</summary>
        public readonly List<ConsumptionRow> Consumption = new List<ConsumptionRow>();
        /// <summary>Цвет каждой окрашиваемой единицы: обозначение → RAL.</summary>
        public readonly Dictionary<string, string> Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public double PaintKg;
        public int PaintCans;
        public readonly List<string> Issues = new List<string>();
        public readonly List<string> Norms = new List<string>();

        public int TotalProducts { get { return Products.Sum(p => p.Quantity); } }
    }

    /// <summary>
    /// Свод производственной заявки из ведомостей изделий (ТЗ-02 Т-40). Модели при своде не открываются
    /// (Р-6): всё, что нужно цеху, уже посчитано кнопкой «Ведомость ЛЗК» и лежит в книге изделия.
    /// </summary>
    public static class ProdRequest
    {
        /// <summary>Первая строка данных на листах «Покраска» и «Покупные» ведомости.</summary>
        private const int FirstDataRow = 4;

        /// <summary>Читает ведомость изделия; ошибка — понятная строка и null.</summary>
        public static RequestProduct Read(string workbookPath, string cipher, string name, int quantity, out string problem)
        {
            problem = "";
            if (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath))
            {
                problem = "Нет ведомости изделия «" + cipher + "»: " + workbookPath;
                return null;
            }
            RequestProduct product = new RequestProduct
            {
                Cipher = cipher ?? "", Name = name ?? "", WorkbookPath = workbookPath, Quantity = Math.Max(1, quantity)
            };
            try
            {
                using (XlsxBook book = XlsxBook.Open(workbookPath))
                {
                    if (!Main(book, product, out problem)) return null;
                    Painted(book, product);
                    PurchasedRows(book, product);
                }
            }
            catch (IOException ex)
            {
                problem = "Ведомость «" + Path.GetFileName(workbookPath) + "» не читается (возможно, открыта): " + ex.Message;
                return null;
            }
            return product;
        }

        private static bool Main(XlsxBook book, RequestProduct product, out string problem)
        {
            problem = "";
            Dictionary<string, int> col = new Dictionary<string, int>();
            string sheetName = null;
            int headerRow = 0;
            foreach (string named in LzkWorkbook.RequiredNames)
            {
                string sheet, cell;
                if (!book.TryResolveName(named, out sheet, out cell))
                {
                    problem = "Ведомость «" + Path.GetFileName(product.WorkbookPath) + "» сделана не по шаблону ЛЗК: нет «" + named + "»";
                    return false;
                }
                int c, r;
                XlsxBook.ParseCell(cell, out c, out r);
                if (sheetName == null)
                {
                    sheetName = sheet;
                    headerRow = r;
                }
                col[named] = c;
            }
            XlsxSheet main = book.Sheet(sheetName);
            if (main == null)
            {
                problem = "В ведомости нет листа «" + sheetName + "»";
                return false;
            }
            foreach (int row in main.RowNumbers.Where(r => r > headerRow))
            {
                string path = (main.Get(col["Путь"], row) ?? "").Trim();
                string designation = (main.Get(col["Обозначение"], row) ?? "").Trim();
                string name = (main.Get(col["Наименование"], row) ?? "").Trim();
                if (designation.Length == 0 && name.Length == 0 && path.Length == 0) continue;
                RequestLine line = new RequestLine
                {
                    Designation = designation,
                    Name = name,
                    Material = Clean(main.Get(col["Материал_Строка"], row)),
                    Size = Clean(main.Get(col["Габарит"], row)),
                    Operations = Clean(main.Get(col["Операции"], row)),
                    MassKg = Number(main.Get(col["МассаЕдКг"], row)),
                    Quantity = (int)Math.Max(1, Math.Round(Number(main.Get(col["Количество"], row)))),
                    IsAssembly = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase),
                    Path = path
                };
                product.Lines.Add(line);
                foreach (string named in LzkWorkbook.RequiredNames)
                    if ((main.Get(col[named], row) ?? "").Trim() == LzkWorkbook.Mark)
                        product.Marks.Add((designation.Length > 0 ? designation : name) + ": не заполнено «" + named + "»");
            }
            return true;
        }

        private static void Painted(XlsxBook book, RequestProduct product)
        {
            XlsxSheet sheet = book.Sheet(LzkWorkbook.PaintSheet);
            if (sheet == null) return;
            foreach (int row in sheet.RowNumbers.Where(r => r >= FirstDataRow))
            {
                string designation = Clean(sheet.Get(2, row));
                string name = Clean(sheet.Get(3, row));
                if (designation.Length == 0 && name.Length == 0) continue;
                if (name.Equals("Итого", StringComparison.OrdinalIgnoreCase)) break;
                product.Painted.Add(new RequestLine
                {
                    Designation = designation,
                    Name = name,
                    AreaM2 = Number(sheet.Get(4, row)),
                    Quantity = (int)Math.Max(1, Math.Round(Number(sheet.Get(5, row))))
                });
            }
        }

        private static void PurchasedRows(XlsxBook book, RequestProduct product)
        {
            XlsxSheet sheet = book.Sheet(LzkWorkbook.PurchasedSheet);
            if (sheet == null) return;
            foreach (int row in sheet.RowNumbers.Where(r => r >= FirstDataRow))
            {
                string name = Clean(sheet.Get(2, row));
                if (name.Length == 0) continue;
                product.Purchased.Add(new RequestLine
                {
                    Name = name,
                    Designation = Clean(sheet.Get(3, row)),
                    Code = Clean(sheet.Get(4, row)),
                    Unit = Clean(sheet.Get(5, row)),
                    Okei = Clean(sheet.Get(6, row)),
                    Quantity = (int)Math.Max(1, Math.Round(Number(sheet.Get(7, row))))
                });
            }
        }

        private static string Clean(string value)
        {
            string text = (value ?? "").Trim();
            return text == LzkWorkbook.Mark ? "" : text;
        }

        private static double Number(string value)
        {
            double parsed = LzkOperations.ParseNumber(value);
            return double.IsNaN(parsed) ? 0 : parsed;
        }

        /// <summary>
        /// Свод по всем изделиям заявки: количества умножаются на тираж и складываются по заказу.
        /// Цвета приходят из окна «Цвета покраски» (Т-14в); чего в нём нет — попадает в замечания.
        /// </summary>
        public static ProdRequestData Build(IEnumerable<RequestProduct> products, Norms norms,
            IDictionary<string, string> colors)
        {
            ProdRequestData data = new ProdRequestData();
            data.Products.AddRange(products ?? new RequestProduct[0]);
            if (colors != null)
                foreach (KeyValuePair<string, string> pair in colors) data.Colors[pair.Key] = pair.Value;

            Dictionary<string, RequestLine> blanks = new Dictionary<string, RequestLine>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, RequestLine> units = new Dictionary<string, RequestLine>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, RequestLine> painting = new Dictionary<string, RequestLine>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, RequestLine> purchased = new Dictionary<string, RequestLine>(StringComparer.OrdinalIgnoreCase);

            foreach (RequestProduct product in data.Products)
            {
                // Покупное узнаётся по листу «Покупные» ведомости: у заглушки или наконечника тоже есть
                // материал, но резать её не надо — она идёт цеху отдельным листом, а не в заготовки.
                HashSet<string> bought = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
                foreach (RequestLine line in product.Purchased)
                {
                    if (line.Name.Trim().Length > 0) bought.Add(line.Name.Trim());
                    if (line.Designation.Trim().Length > 0) bought.Add(line.Designation.Trim());
                }
                foreach (RequestLine line in product.Lines)
                {
                    if (line.IsAssembly)
                    {
                        Add(units, Key(line.Designation, line.Name), line, product.Quantity);
                        continue;
                    }
                    // Настоящие ведомости показывают: половина покупных на лист «Покупные» не попадает —
                    // SWTools узнаёт их по признакам свойств. Зато видно папку: «Стандартные изделия
                    // и фурнитура», «Пластик» рядом с ней — это не заготовки заготовительного участка.
                    if (bought.Contains(line.Name.Trim()) || bought.Contains(line.Designation.Trim()) ||
                        ProductLocator.IsPurchasedFolder(line.Path))
                    {
                        Add(purchased, Key(line.Designation, line.Name), line, product.Quantity);
                        continue;
                    }
                    if (line.Material.Trim().Length == 0)
                    {
                        // Деталь без материала не заготовка и не покупное: молча выкинуть её нельзя,
                        // иначе цех недосчитается позиции, о которой никто не узнает.
                        data.Issues.Add("Нет материала, позиция не попала в заготовки: " +
                            (line.Designation.Length > 0 ? line.Designation : line.Name));
                        continue;
                    }
                    Add(blanks, Key(line.Designation, line.Name), line, product.Quantity);
                }
                foreach (RequestLine line in product.Painted)
                    Add(painting, Key(line.Designation, line.Name), line, product.Quantity);
                foreach (RequestLine line in product.Purchased)
                    Add(purchased, Key(line.Code.Length > 0 ? line.Code : line.Designation, line.Name), line, product.Quantity);
            }
            data.Blanks.AddRange(blanks.Values.OrderBy(l => l.Material, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(l => l.Designation, StringComparer.CurrentCultureIgnoreCase));
            data.Units.AddRange(units.Values.OrderBy(l => l.Designation, StringComparer.CurrentCultureIgnoreCase));
            data.Painting.AddRange(painting.Values.OrderBy(l => l.Designation, StringComparer.CurrentCultureIgnoreCase));
            data.Purchased.AddRange(purchased.Values.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase));

            foreach (RequestLine line in data.Painting)
            {
                string color;
                if (data.Colors.TryGetValue(line.Designation, out color) && color.Trim().Length > 0) continue;
                data.Issues.Add("Не назначен цвет: " + (line.Designation.Length > 0 ? line.Designation : line.Name));
            }
            if (norms != null)
            {
                Consumption(data, norms);
                foreach (KeyValuePair<string, string> pair in norms.All) data.Norms.Add(pair.Key + " = " + pair.Value);
            }
            return data;
        }

        private static string Key(string designation, string name)
        {
            return ((designation ?? "") + "" + (name ?? "")).ToLowerInvariant();
        }

        private static void Add(IDictionary<string, RequestLine> target, string key, RequestLine line, int factor)
        {
            RequestLine existing;
            if (target.TryGetValue(key, out existing))
            {
                existing.Quantity += line.Quantity * factor;
                return;
            }
            target[key] = new RequestLine
            {
                Designation = line.Designation, Name = line.Name, Material = line.Material, Size = line.Size,
                Operations = line.Operations, MassKg = line.MassKg, AreaM2 = line.AreaM2,
                Quantity = line.Quantity * factor, IsAssembly = line.IsAssembly,
                Code = line.Code, Unit = line.Unit, Okei = line.Okei
            };
        }

        /// <summary>Лист 5: прокат режется по сортаментам, лист считается по площади, краска — по площади покраски.</summary>
        private static void Consumption(ProdRequestData data, Norms norms)
        {
            // В расход идёт только прокат: пластмассовую спинку и полиэтиленовую заглушку заготовительный
            // участок не режет, и «хлысты» для них считать бессмысленно (боевой прогон 18.09.2026).
            foreach (IGrouping<string, RequestLine> group in data.Blanks
                .Where(l => l.Material.Length > 0 && LzkMaterials.Kind(l.Material) == MaterialKind.RolledMetal)
                .GroupBy(l => l.Material, StringComparer.CurrentCultureIgnoreCase))
            {
                bool sheetMetal = LzkMaterials.IsSheet(group.Key);
                ConsumptionRow row = new ConsumptionRow { Sortament = group.Key };
                if (sheetMetal)
                {
                    // Площадь листовых заготовок надстройка знает только для окрашиваемых единиц, поэтому
                    // лист считается по габаритам заготовки: ширина × длина × количество.
                    double area = group.Sum(l => Area(l) * l.Quantity);
                    double sheetArea, kim;
                    row.AreaM2 = Math.Round(area, 3);
                    row.Sheets = CutPlan.Sheets(area, norms, out sheetArea, out kim);
                    row.Kim = (kim * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
                }
                else
                {
                    List<double> pieces = new List<double>();
                    foreach (RequestLine line in group)
                    {
                        double length = line.LengthMm;
                        if (length <= 0)
                        {
                            data.Issues.Add("Нет длины заготовки: " + (line.Designation.Length > 0 ? line.Designation : line.Name));
                            continue;
                        }
                        for (int i = 0; i < line.Quantity; i++) pieces.Add(length);
                    }
                    if (pieces.Count == 0) continue;
                    CutResult plan = CutPlan.Plan(group.Key, pieces, norms);
                    row.CleanM = Math.Round(plan.CleanMm / 1000.0, 1);
                    row.Bars = plan.BarCount;
                    row.BoughtM = Math.Round(plan.BoughtMm / 1000.0, 1);
                    row.Kim = plan.KimText;
                    row.BusinessM = Math.Round(plan.BusinessRests.Sum() / 1000.0, 1);
                    row.WasteM = Math.Round(plan.WasteMm / 1000.0, 1);
                    foreach (double piece in plan.TooLong)
                        data.Issues.Add("Заготовка длиннее хлыста (" + group.Key + "): " +
                            piece.ToString("0", CultureInfo.InvariantCulture) + " мм");
                }
                data.Consumption.Add(row);
            }
            double paintArea = data.Painting.Where(l => !double.IsNaN(l.AreaM2)).Sum(l => l.AreaM2 * l.Quantity);
            int cans;
            data.PaintKg = Math.Round(CutPlan.Paint(paintArea, norms, out cans), 1);
            data.PaintCans = cans;
        }

        /// <summary>Площадь листовой заготовки по габариту «1996×50×2»: две большие стороны.</summary>
        private static double Area(RequestLine line)
        {
            double[] sides = (line.Size ?? "").Split(new[] { '×', 'x', 'X', 'х', 'Х' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Norms.ParseNumber).Where(v => v > 0).OrderByDescending(v => v).ToArray();
            return sides.Length >= 2 ? sides[0] * sides[1] / 1000000.0 : 0;
        }
    }
}
