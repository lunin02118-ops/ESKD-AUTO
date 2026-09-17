using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Книга производственной заявки по образцу 85_Т (ТЗ-02 Т-40): пять листов — заготовки, сварка,
    /// покраска, покупные и сводный расход. Каждый лист печатается отдельно и подписывается своим
    /// начальником участка, поэтому шапка повторяется на каждом листе, а не только в начале книги.
    /// </summary>
    public static class ProdRequestBook
    {
        public const string BlanksSheet = "Лист 1 Заготовки";
        public const string UnitsSheet = "Лист 2 Сварка";
        public const string PaintSheet = "Лист 3 Покраска";
        public const string PurchasedSheet = "Лист 4 Покупные";
        public const string ConsumptionSheet = "Лист 5 Расход";
        /// <summary>Скрытые листы (Т-40): по ним повторная выдача подставляет прежние цвета и состав.</summary>
        public const string ColorsSheet = "Цвета";
        public const string SetSheet = "Комплект";

        /// <summary>Имя файла сводной: «&lt;№ заявки&gt;_Сводная_заявка[_ИзмN].xlsx».</summary>
        public static string FileName(string number, int revision)
        {
            return LzkNaming.SafeFileName((number ?? "").Trim()) + "_Сводная_заявка" +
                ExportNaming.RevisionSuffix(revision) + ".xlsx";
        }

        public static string Path(string folder, string number, int revision)
        {
            return System.IO.Path.Combine(folder ?? "", FileName(number, revision));
        }

        private sealed class Sheet
        {
            public readonly XlsxSheet Target;
            public readonly int Text, Center, Number3, Number1, Total, Note;
            private readonly XlsxBook _book;
            private readonly string _name;
            private readonly int _columns;

            public Sheet(XlsxBook book, string name, ProdRequestData data, string title, string subtitle,
                string[] titles, double[] widths)
            {
                _book = book;
                _name = name;
                _columns = titles.Length;
                Target = book.Sheet(name) ?? book.AddSheet(name);
                int head = book.AddStyle(new XlsxStyle { Bold = true, Border = true, Wrap = true, Horizontal = "center", Fill = "FFF2F2F2" });
                int caption = book.AddStyle(new XlsxStyle { Bold = true, Size = 12, Horizontal = "left" });
                int gray = book.AddStyle(new XlsxStyle { Gray = true, Size = 9, Horizontal = "left" });
                Text = book.AddStyle(new XlsxStyle { Border = true, Wrap = true, Horizontal = "left" });
                Center = book.AddStyle(new XlsxStyle { Border = true, Horizontal = "center" });
                Number3 = book.AddStyle(new XlsxStyle { Border = true, Horizontal = "center", NumberFormat = "0.000" });
                Number1 = book.AddStyle(new XlsxStyle { Border = true, Horizontal = "center", NumberFormat = "0.0" });
                Total = book.AddStyle(new XlsxStyle { Bold = true, Border = true, Horizontal = "center" });
                Note = book.AddStyle(new XlsxStyle { Gray = true, Wrap = true, Horizontal = "left" });

                Target.SetText("A1", title, caption);
                Target.Merge("A1:" + XlsxBook.CellName(_columns, 1));
                Target.SetRowHeight(1, 22);
                Target.SetText("A2", Header(data) + "  ·  " + subtitle, gray);
                Target.Merge("A2:" + XlsxBook.CellName(_columns, 2));
                for (int i = 0; i < titles.Length; i++)
                {
                    Target.SetText(XlsxBook.CellName(i + 1, 3), titles[i], head);
                    Target.SetColumnWidth(i + 1, widths[i]);
                }
                Target.SetRowHeight(3, 30);
                Target.FreezeRowsAbove("A4");
            }

            public void Finish(int lastRow, string[] instructions, string signature)
            {
                int row = lastRow + 2;
                if (instructions != null && instructions.Length > 0)
                {
                    Target.SetText("A" + row, "Технологические указания участка:", Note);
                    Target.Merge("A" + row + ":" + XlsxBook.CellName(_columns, row));
                    row++;
                    foreach (string line in instructions)
                    {
                        Target.SetText("A" + row, "• " + line, Note);
                        Target.Merge("A" + row + ":" + XlsxBook.CellName(_columns, row));
                        Target.SetRowHeight(row, 26);
                        row++;
                    }
                    row++;
                }
                Target.SetText("A" + row, "Нач. КТО: ______________ / ______________", Note);
                Target.SetText(XlsxBook.CellName(Math.Max(2, _columns - 2), row), signature + ": ______________ / ______________", Note);
                Target.FitToWidth();
                string lastColumn = XlsxBook.CellName(_columns, 1);
                lastColumn = lastColumn.Substring(0, lastColumn.Length - 1);
                _book.SetPrintNames(_name, "$A$1:$" + lastColumn + "$" + row, 3);
            }

            private static string Header(ProdRequestData data)
            {
                return "Заявка № " + data.Number + "  ·  заказ " + data.Order + "  ·  " +
                    data.Date.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU")) +
                    "  ·  Нач. КТО: " + data.Chief;
            }
        }

        public static void Write(string path, ProdRequestData data)
        {
            if (File.Exists(path)) File.Delete(path);
            using (XlsxBook book = XlsxBook.Create(path, BlanksSheet))
            {
                Blanks(book, data);
                Units(book, data);
                Painting(book, data);
                Purchased(book, data);
                Consumption(book, data);
                Hidden(book, data);
                book.NormalizeFonts(LzkWorkbook.FontFace);
                book.Save();
            }
        }

        private static void Blanks(XlsxBook book, ProdRequestData data)
        {
            Sheet sheet = new Sheet(book, BlanksSheet, data,
                "1. Цех металлоизделий — заготовительный участок",
                "спецификация чистых заготовок на тираж",
                new[] { "№", "Обозначение", "Наименование детали", "Сортамент / материал", "L, мм", "Всего, шт", "Операция" },
                new double[] { 5, 22, 34, 30, 10, 11, 26 });
            int row = 4;
            foreach (RequestLine line in data.Blanks)
            {
                sheet.Target.SetNumber(XlsxBook.CellName(1, row), row - 3, sheet.Center);
                sheet.Target.SetText(XlsxBook.CellName(2, row), line.Designation, sheet.Text);
                sheet.Target.SetText(XlsxBook.CellName(3, row), line.Name, sheet.Text);
                sheet.Target.SetText(XlsxBook.CellName(4, row), line.Material, sheet.Text);
                if (line.LengthMm > 0) sheet.Target.SetNumber(XlsxBook.CellName(5, row), Math.Round(line.LengthMm, 1), sheet.Number1);
                else sheet.Target.SetText(XlsxBook.CellName(5, row), line.Size, sheet.Center);
                sheet.Target.SetNumber(XlsxBook.CellName(6, row), line.Quantity, sheet.Center);
                sheet.Target.SetText(XlsxBook.CellName(7, row), line.Operations, sheet.Text);
                row++;
            }
            Totals(sheet, row, 6, data.Blanks.Sum(l => l.Quantity), "Итого заготовок на заказ");
            sheet.Finish(row, new[]
            {
                "Заготовки резать по размерам таблицы; предельное отклонение по длине ±0,5 мм.",
                "Заусенцы на торцах снять, зоны сварных стыков зачистить.",
                "Листовые детали резать по чистому контуру DXF из папки 02 ЧПУ и Раскрой."
            }, "Начальник цеха металлоизделий");
        }

        private static void Units(XlsxBook book, ProdRequestData data)
        {
            Sheet sheet = new Sheet(book, UnitsSheet, data,
                "2. Сварочно-сборочный цех",
                "сборочные единицы металлокаркаса и масса конструкций",
                new[] { "№", "Обозначение узла", "Наименование сборочной единицы", "Масса 1 шт, кг", "Всего на тираж", "Общая масса, кг" },
                new double[] { 5, 24, 40, 14, 14, 15 });
            int row = 4;
            double mass = 0;
            foreach (RequestLine line in data.Units)
            {
                sheet.Target.SetNumber(XlsxBook.CellName(1, row), row - 3, sheet.Center);
                sheet.Target.SetText(XlsxBook.CellName(2, row), line.Designation, sheet.Text);
                sheet.Target.SetText(XlsxBook.CellName(3, row), line.Name, sheet.Text);
                sheet.Target.SetNumber(XlsxBook.CellName(4, row), Math.Round(line.MassKg, 3), sheet.Number3);
                sheet.Target.SetNumber(XlsxBook.CellName(5, row), line.Quantity, sheet.Center);
                sheet.Target.SetNumber(XlsxBook.CellName(6, row), Math.Round(line.MassKg * line.Quantity, 2), sheet.Number3);
                mass += line.MassKg * line.Quantity;
                row++;
            }
            sheet.Target.SetText(XlsxBook.CellName(3, row), "Итого готовый металлокаркас", sheet.Total);
            sheet.Target.SetNumber(XlsxBook.CellName(5, row), data.Units.Sum(l => l.Quantity), sheet.Total);
            sheet.Target.SetNumber(XlsxBook.CellName(6, row), Math.Round(mass, 2), sheet.Total);
            sheet.Finish(row, new[]
            {
                "Сварка полуавтоматическая в среде защитных газов; швы сплошные, без непроваров и прожогов.",
                "Лицевые швы зачистить заподлицо под полимерную покраску."
            }, "Начальник сварочного цеха");
        }

        private static void Painting(XlsxBook book, ProdRequestData data)
        {
            Sheet sheet = new Sheet(book, PaintSheet, data,
                "3. Участок полимерно-порошковой покраски",
                "ведомость окрашиваемых единиц и цвет покрытия",
                new[] { "№", "Обозначение узла", "Наименование", "Всего на тираж", "Площадь всего, м²", "Цвет покрытия" },
                new double[] { 5, 24, 40, 14, 16, 18 });
            int row = 4;
            double area = 0;
            foreach (RequestLine line in data.Painting)
            {
                string color;
                data.Colors.TryGetValue(line.Designation, out color);
                sheet.Target.SetNumber(XlsxBook.CellName(1, row), row - 3, sheet.Center);
                sheet.Target.SetText(XlsxBook.CellName(2, row), line.Designation, sheet.Text);
                sheet.Target.SetText(XlsxBook.CellName(3, row), line.Name, sheet.Text);
                sheet.Target.SetNumber(XlsxBook.CellName(4, row), line.Quantity, sheet.Center);
                if (!double.IsNaN(line.AreaM2) && line.AreaM2 > 0)
                {
                    sheet.Target.SetNumber(XlsxBook.CellName(5, row), Math.Round(line.AreaM2 * line.Quantity, 3), sheet.Number3);
                    area += line.AreaM2 * line.Quantity;
                }
                else sheet.Target.SetText(XlsxBook.CellName(5, row), LzkWorkbook.Mark, sheet.Center);
                sheet.Target.SetText(XlsxBook.CellName(6, row), color ?? "", sheet.Center);
                row++;
            }
            sheet.Target.SetText(XlsxBook.CellName(3, row), "Итого на покраску", sheet.Total);
            sheet.Target.SetNumber(XlsxBook.CellName(4, row), data.Painting.Sum(l => l.Quantity), sheet.Total);
            sheet.Target.SetNumber(XlsxBook.CellName(5, row), Math.Round(area, 3), sheet.Total);
            sheet.Finish(row, new[]
            {
                "Покрытие сплошное и однородное, без непрокрасов, потёков и механических повреждений.",
                "Расход краски на заказ: " + data.PaintKg.ToString("0.0", CultureInfo.InvariantCulture) + " кг (" +
                    data.PaintCans + " коробк(и) тары)."
            }, "Мастер малярного участка");
        }

        private static void Purchased(XlsxBook book, ProdRequestData data)
        {
            Sheet sheet = new Sheet(book, PurchasedSheet, data,
                "4. Склад ТМЦ и участок комплектации",
                "комплектовочная ведомость покупных изделий",
                new[] { "№", "Код 1С", "Номенклатура ТМЦ", "Обозначение", "Ед. изм.", "ОКЕИ", "Всего на тираж" },
                new double[] { 5, 16, 44, 24, 10, 8, 14 });
            int row = 4;
            foreach (RequestLine line in data.Purchased)
            {
                sheet.Target.SetNumber(XlsxBook.CellName(1, row), row - 3, sheet.Center);
                sheet.Target.SetText(XlsxBook.CellName(2, row), line.Code, sheet.Center);
                sheet.Target.SetText(XlsxBook.CellName(3, row), line.Name, sheet.Text);
                sheet.Target.SetText(XlsxBook.CellName(4, row), line.Designation, sheet.Text);
                sheet.Target.SetText(XlsxBook.CellName(5, row), line.Unit, sheet.Center);
                sheet.Target.SetText(XlsxBook.CellName(6, row), line.Okei, sheet.Center);
                sheet.Target.SetNumber(XlsxBook.CellName(7, row), line.Quantity, sheet.Center);
                row++;
            }
            Totals(sheet, row, 7, data.Purchased.Sum(l => l.Quantity), "Итого позиций комплектации");
            sheet.Finish(row, new[]
            {
                "Пустой код 1С означает, что позиции ещё нет в справочнике снабжения — заведите её перед выдачей.",
                "Фурнитуру, которая приваривается, выдать в сварочный цех заблаговременно."
            }, "Кладовщик склада ТМЦ");
        }

        private static void Consumption(XlsxBook book, ProdRequestData data)
        {
            Sheet sheet = new Sheet(book, ConsumptionSheet, data,
                "5. Сводная ведомость расхода и списания ТМЦ",
                "потребность в прокате, КИМ, деловые обрезки и отход",
                new[] { "№", "Сортамент и ГОСТ", "Чистый расход, м", "Хлыстов", "Листов", "Закуплено, м", "КИМ", "Деловые, м", "Отход, м" },
                new double[] { 5, 34, 14, 10, 10, 14, 10, 12, 12 });
            int row = 4;
            foreach (ConsumptionRow line in data.Consumption)
            {
                sheet.Target.SetNumber(XlsxBook.CellName(1, row), row - 3, sheet.Center);
                sheet.Target.SetText(XlsxBook.CellName(2, row), line.Sortament, sheet.Text);
                if (line.Sheets > 0)
                {
                    sheet.Target.SetNumber(XlsxBook.CellName(3, row), line.AreaM2, sheet.Number3);
                    sheet.Target.SetNumber(XlsxBook.CellName(5, row), line.Sheets, sheet.Center);
                }
                else
                {
                    sheet.Target.SetNumber(XlsxBook.CellName(3, row), line.CleanM, sheet.Number1);
                    sheet.Target.SetNumber(XlsxBook.CellName(4, row), line.Bars, sheet.Center);
                    sheet.Target.SetNumber(XlsxBook.CellName(6, row), line.BoughtM, sheet.Number1);
                    sheet.Target.SetNumber(XlsxBook.CellName(8, row), line.BusinessM, sheet.Number1);
                    sheet.Target.SetNumber(XlsxBook.CellName(9, row), line.WasteM, sheet.Number1);
                }
                sheet.Target.SetText(XlsxBook.CellName(7, row), line.Kim, sheet.Center);
                row++;
            }
            sheet.Target.SetText(XlsxBook.CellName(2, row), "Итого металлопрокат на заказ", sheet.Total);
            sheet.Target.SetNumber(XlsxBook.CellName(4, row), data.Consumption.Sum(c => c.Bars), sheet.Total);
            sheet.Target.SetNumber(XlsxBook.CellName(5, row), data.Consumption.Sum(c => c.Sheets), sheet.Total);
            sheet.Target.SetNumber(XlsxBook.CellName(6, row), Math.Round(data.Consumption.Sum(c => c.BoughtM), 1), sheet.Total);

            List<string> instructions = new List<string>
            {
                "Краска: " + data.PaintKg.ToString("0.0", CultureInfo.InvariantCulture) + " кг, тары — " + data.PaintCans + " шт.",
                "Деловым считается обрезок не короче норматива «Труба.Деловой»; остальное — технологический отход."
            };
            if (data.Norms.Count > 0) instructions.Add("Использованные нормативы: " + string.Join("; ", data.Norms.ToArray()));
            foreach (string issue in data.Issues.Take(10)) instructions.Add("Замечание: " + issue);
            sheet.Finish(row, instructions.ToArray(), "Начальник снабжения");
        }

        private static void Totals(Sheet sheet, int row, int column, double value, string title)
        {
            sheet.Target.SetText(XlsxBook.CellName(Math.Max(1, column - 1), row), title, sheet.Total);
            sheet.Target.SetNumber(XlsxBook.CellName(column, row), value, sheet.Total);
        }

        /// <summary>Скрытые листы «Цвета» и «Комплект»: из них повторная выдача берёт прежние решения (Т-40).</summary>
        private static void Hidden(XlsxBook book, ProdRequestData data)
        {
            XlsxSheet colors = book.AddSheet(ColorsSheet);
            colors.SetText("A1", "Обозначение");
            colors.SetText("B1", "Цвет");
            int row = 2;
            foreach (KeyValuePair<string, string> pair in data.Colors)
            {
                colors.SetText("A" + row, pair.Key);
                colors.SetText("B" + row, pair.Value);
                row++;
            }
            XlsxSheet set = book.AddSheet(SetSheet);
            set.SetText("A1", "Изделие");
            set.SetText("B1", "Наименование");
            set.SetText("C1", "Количество");
            set.SetText("D1", "Ведомость");
            row = 2;
            foreach (RequestProduct product in data.Products)
            {
                set.SetText("A" + row, product.Cipher);
                set.SetText("B" + row, product.Name);
                set.SetNumber("C" + row, product.Quantity);
                set.SetText("D" + row, product.WorkbookPath);
                row++;
            }
            book.Hide(ColorsSheet);
            book.Hide(SetSheet);
        }

        /// <summary>Состав и цвета прошлой выдачи: их подставляет окно при повторной выдаче (Т-38).</summary>
        public static bool ReadPrevious(string path, IDictionary<string, string> colors,
            IDictionary<string, int> quantities)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (XlsxBook book = XlsxBook.Open(path))
                {
                    XlsxSheet colorSheet = book.Sheet(ColorsSheet);
                    if (colorSheet != null && colors != null)
                        foreach (int row in colorSheet.RowNumbers.Where(r => r > 1))
                        {
                            string key = (colorSheet.Get(1, row) ?? "").Trim();
                            if (key.Length > 0) colors[key] = (colorSheet.Get(2, row) ?? "").Trim();
                        }
                    XlsxSheet setSheet = book.Sheet(SetSheet);
                    if (setSheet != null && quantities != null)
                        foreach (int row in setSheet.RowNumbers.Where(r => r > 1))
                        {
                            string key = (setSheet.Get(1, row) ?? "").Trim();
                            double value = LzkOperations.ParseNumber(setSheet.Get(3, row));
                            int quantity = double.IsNaN(value) ? 1 : (int)Math.Max(1, Math.Round(value));
                            if (key.Length > 0) quantities[key] = quantity;
                            // Тот же тираж кладём и под именем папки изделия: окно выдачи перебирает
                            // «02_Металл\И01_…», а шифр в имени папки — только часть.
                            string workbook = (setSheet.Get(4, row) ?? "").Trim();
                            string folder = workbook.Length > 0
                                ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(workbook) ?? "") : "";
                            if (folder.Length > 0) quantities[folder] = quantity;
                        }
                }
                return true;
            }
            catch (IOException ex)
            {
                Log.Error("Сводная заявка: чтение прошлой выдачи " + path, ex);
                return false;
            }
        }
    }
}
