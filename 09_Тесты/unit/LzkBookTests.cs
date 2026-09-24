using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Живая книга ЛЗК (ТЗ-04): формулы, имена, участки, калькулятор, перенос введённого.</summary>
    public static class LzkBookTests
    {
        /// <summary>Справочник нормативов инструментария — встроенных нормативов у надстройки нет (ТЗ-02 Т-13).</summary>
        private static Norms ShippedNorms()
        {
            string problem;
            string path = Norms.PathIn(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, @"..\..\..\02_Шаблоны_и_Форматки\Справочники")));
            Norms norms = Norms.Read(path, out problem);
            Assert.IsTrue(norms != null, problem);
            return norms;
        }

        private static string Temp()
        {
            return Path.Combine(Path.GetTempPath(), "eskd_book_" + Guid.NewGuid().ToString("N") + ".xlsx");
        }

        private static string Template()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.GetFullPath(Path.Combine(dir, "..", "data", "Ведомость_ЛЗК.xlsx"));
            string copy = Temp();
            File.Copy(path, copy);
            return copy;
        }

        private static string Part(string path, string part)
        {
            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                ZipArchiveEntry entry = zip.GetEntry(part);
                if (entry == null) throw new FileNotFoundException("Нет части " + part);
                using (StreamReader reader = new StreamReader(entry.Open())) return reader.ReadToEnd();
            }
        }

        public static void Test_Xlsx_writes_formulas_names_filter_and_moves_sheets()
        {
            string path = Temp();
            try
            {
                XlsxBook book = XlsxBook.Create(path, "Первый");
                XlsxSheet second = book.AddSheet("Второй");
                book.SetPrintNames("Второй", "$A$1:$B$9", 1);
                second.SetNumber("B9", 41);
                second.SetFormula("C9", "=B9*2");
                book.DefineName("Тираж", "Второй", "B9");
                book.Sheet("Первый").SetText("A1", "Шапка");
                book.SetAutoFilter("Первый", "A1:C5");
                book.MoveSheet("Второй", 0);
                book.SetActiveSheet("Второй");
                book.RecalculateOnOpen();
                book.Save();

                XlsxBook again = XlsxBook.Open(path);
                Assert.AreEqual("Второй|Первый", string.Join("|", again.SheetNames), "порядок листов");
                string sheet, cell;
                Assert.IsTrue(again.TryResolveName("Тираж", out sheet, out cell), "имя книги");
                Assert.AreEqual("Второй", sheet, "лист имени");
                Assert.AreEqual("B9", cell, "ячейка имени");
                Assert.AreEqual("B9*2", again.Sheet("Второй").Formula("C9"), "формула без «=»");
                string workbook = Part(path, "xl/workbook.xml");
                Assert.IsTrue(workbook.Contains("fullCalcOnLoad=\"1\""), "пересчёт при открытии");
                Assert.IsTrue(workbook.Contains("name=\"_xlnm.Print_Area\" localSheetId=\"0\""), "область печати переехала с листом: " + workbook);
                Assert.IsTrue(workbook.Contains("name=\"_xlnm._FilterDatabase\" localSheetId=\"1\""), "фильтр переехал с листом: " + workbook);
                Assert.IsTrue(workbook.Contains("activeTab=\"0\""), "книга открывается на первом листе");
                Assert.IsTrue(workbook.IndexOf("<definedNames", StringComparison.Ordinal) < workbook.IndexOf("<calcPr", StringComparison.Ordinal),
                    "порядок элементов книги по схеме");
                string first = Part(path, "xl/worksheets/sheet1.xml");
                Assert.IsTrue(first.Contains("<autoFilter ref=\"A1:C5\""), "автофильтр: " + first);
            }
            finally
            {
                File.Delete(path);
            }
        }

        public static void Test_Pictures_are_fitted_inside_their_cells()
        {
            string path = Template();
            try
            {
                AddPicture(path);
                XlsxBook book = XlsxBook.Open(path);
                Assert.AreEqual(1, book.FitPicturesToCells("Ведомость"), "рисунок пересажен");
                book.Save();
                string drawing = Part(path, "xl/drawings/drawing1.xml");
                Assert.IsTrue(drawing.Contains("<xdr:to><xdr:col>1</xdr:col>"), "конец рисунка в той же колонке: " + drawing);
                Assert.IsTrue(drawing.Contains("<xdr:row>6</xdr:row><xdr:rowOff>") &&
                              !drawing.Contains("<xdr:row>7</xdr:row>"), "конец рисунка в той же строке: " + drawing);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>Рисунок 64×48 в B7, как его ставит SWTools: от ячейки до угла следующей.</summary>
        private static void AddPicture(string path)
        {
            const string xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
            const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            const string pr = "http://schemas.openxmlformats.org/package/2006/relationships";
            string drawing = "<xdr:wsDr xmlns:xdr=\"" + xdr + "\" xmlns:a=\"" + a + "\"><xdr:twoCellAnchor editAs=\"twoCell\">" +
                "<xdr:from><xdr:col>1</xdr:col><xdr:colOff>9000</xdr:colOff><xdr:row>6</xdr:row><xdr:rowOff>9000</xdr:rowOff></xdr:from>" +
                "<xdr:to><xdr:col>2</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>7</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>" +
                "<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"1\" name=\"Picture 1\"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill>" +
                "<a:blip xmlns:r=\"" + r + "\" r:embed=\"rId1\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>" +
                "<xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></a:xfrm><a:prstGeom prst=\"rect\"/></xdr:spPr>" +
                "</xdr:pic><xdr:clientData/></xdr:twoCellAnchor></xdr:wsDr>";
            byte[] png = new byte[33];
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
                0, 0, 0, 64, 0, 0, 0, 48 }.CopyTo(png, 0);
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                Write(zip, "xl/drawings/drawing1.xml", drawing);
                Write(zip, "xl/drawings/_rels/drawing1.xml.rels", "<Relationships xmlns=\"" + pr + "\"><Relationship Id=\"rId1\" " +
                    "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/image1.png\"/></Relationships>");
                Write(zip, "xl/worksheets/_rels/sheet1.xml.rels", "<Relationships xmlns=\"" + pr + "\"><Relationship Id=\"rId9\" " +
                    "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing1.xml\"/></Relationships>");
                using (Stream s = zip.CreateEntry("xl/media/image1.png").Open()) s.Write(png, 0, png.Length);
            }
        }

        private static void Write(ZipArchive zip, string name, string text)
        {
            ZipArchiveEntry old = zip.GetEntry(name);
            if (old != null) old.Delete();
            using (StreamWriter w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) w.Write(text);
        }

        /// <summary>
        /// Замечания владельца и аудит 21.09.2026: главная сборка не комплектуется, труба с «Разделом» «Материалы» не идёт
        /// в металлопрокат, покраска считается по экземплярам вне окрашиваемых узлов и по всем названиям операции.
        /// </summary>
        public static void Test_Kitting_stock_and_painting_do_not_double_count()
        {
            LzkBlanks b = LzkBlanks.Defaults();
            const string tube = "Труба 40х20х1,5 ГОСТ 8645-68";
            LzkItem top = new LzkItem { Designation = "А.000", IsAssembly = true, IsTop = true, Quantity = 1,
                Operations = LzkOperations.MechanicalAssembly + "; Покраска" };
            LzkItem unit = new LzkItem { Designation = "А.100", IsAssembly = true, Quantity = 2, Operations = LzkOperations.MechanicalAssembly };
            LzkItem material = new LzkItem { Designation = "А.010", Material = tube, Size = "L=500", Quantity = 3, Section = "Материалы",
                Operations = "Покраска", AreaM2 = 0.1 };
            LzkItem bracket = new LzkItem { Designation = "А.020", Material = tube, Size = "L=300", Quantity = 6, Operations = "Лазерная резка трубы; Окраска",
                AreaM2 = 0.05, PaintQuantity = 2 };
            List<LzkItem> items = new List<LzkItem> { top, unit, material, bracket };

            List<LzkItem> kit = LzkBook.SectionItems(items, LzkBlanks.Kitting, b);
            Assert.IsFalse(kit.Contains(top), "главная сборка — не комплект самой себя");
            Assert.IsTrue(kit.Contains(unit), "сборочная единица в комплекте");
            Assert.IsTrue(kit.Contains(material), "труба с «Разделом» «Материалы» — на комплектовку");

            Assert.IsTrue(LzkBlanks.KittedWhole(material), "материал идёт на комплектовку целиком");
            Assert.IsFalse(LzkBook.IsBarStock(material), "материал не считается в металлопрокате «Расхода»");
            List<LzkBook.BarGroup> bars = LzkBook.BarGroups(items);
            Assert.AreEqual(1, bars.Count, "в «Расходе» только кронштейн");
            Assert.AreEqual(300.0, bars[0].LengthMm, "длина кронштейна");

            Assert.IsTrue(b.HasPainting("Лазерная резка трубы; Окраска"), "«Окраска» — тоже покраска");
            Assert.IsFalse(b.HasPainting("Лазерная резка трубы"), "без покраски");
            List<LzkItem> paint = LzkBook.SectionItems(items, LzkBlanks.Painting, b);
            Assert.IsTrue(paint.Contains(bracket), "кронштейн красится отдельно");
            Assert.IsFalse(paint.Contains(material), "материал не красится");
            Assert.AreEqual(2, LzkBlanks.PaintCount(bracket), "4 из 6 кронштейнов вварены в окрашиваемую раму — отдельно красятся 2");
            Assert.AreEqual(0, LzkBlanks.PaintCount(new LzkItem { Quantity = 4, PaintQuantity = 0 }), "все внутри узла");
            Assert.AreEqual(4, LzkBlanks.PaintCount(new LzkItem { Quantity = 4 }), "без обхода сборки — все");
            Assert.IsFalse(LzkBook.SectionItems(new List<LzkItem> { new LzkItem { Quantity = 4, PaintQuantity = 0, Operations = "Покраска" } },
                LzkBlanks.Painting, b).Any(), "деталь целиком внутри окрашиваемого узла на «Покрасочный» не выходит");
        }

        public static void Test_Sections_follow_operations_and_categories()
        {
            LzkBlanks b = LzkBlanks.Defaults();
            List<string> unknown = new List<string>();
            LzkItem part = new LzkItem { Operations = "Лазерная резка трубы; Покраска" };
            Assert.AreEqual("Заготовительный|Покрасочный", string.Join("|", b.SectionsOf(part, unknown)), "деталь");
            Assert.AreEqual("Заготовительный", string.Join("|", b.SectionsOf(new LzkItem { Operations = "Труборез" }, unknown)), "синоним");
            Assert.AreEqual("Сварочный|Покрасочный",
                string.Join("|", b.SectionsOf(new LzkItem { IsAssembly = true, Operations = "Покраска; Сварная сборка" }, unknown)), "порядок маршрута");
            Assert.AreEqual("Комплектовочный", string.Join("|", b.SectionsOf(new LzkItem { IsPurchased = true, Operations = "Покраска" }, unknown)),
                "покупное — на комплектовку");
            Assert.AreEqual("Комплектовочный", string.Join("|", b.SectionsOf(new LzkItem { Section = "Материалы" }, unknown)), "материал");
            Assert.AreEqual("", string.Join("|", b.SectionsOf(new LzkItem { Operations = "Зенковка" }, unknown)), "неизвестная операция");
            Assert.AreEqual("Зенковка", string.Join("|", unknown), "неизвестная операция в замечаниях");

            Assert.AreEqual("Сборка", LzkBlanks.Category(new LzkItem { Section = "Сборочные единицы" }), "раздел сборочных");
            Assert.AreEqual("Деталь", LzkBlanks.Category(new LzkItem { Section = "Детали" }), "раздел деталей");
            Assert.AreEqual("Покупное", LzkBlanks.Category(new LzkItem { Section = "Стандартные изделия" }), "стандартные");
            Assert.AreEqual("Прочее", LzkBlanks.Category(new LzkItem { Section = "Прочие изделия" }), "прочие");
            Assert.AreEqual("Материал", LzkBlanks.Category(new LzkItem { Section = "Материалы" }), "материалы");
            Assert.AreEqual("Покупное", LzkBlanks.Category(new LzkItem { IsPurchased = true }), "без раздела — покупное по признаку");
            Assert.AreEqual("Сборка", LzkBlanks.Category(new LzkItem { IsAssembly = true }), "без раздела — сборка");
            Assert.AreEqual("Деталь", LzkBlanks.Category(new LzkItem()), "без раздела — деталь");
        }

        public static void Test_Blanks_from_reference_file_replace_defaults()
        {
            string path = Temp();
            try
            {
                XlsxBook book = XlsxBook.Create(path, "Участки");
                XlsxSheet s = book.Sheet("Участки");
                s.SetText("A1", "Участок");
                s.SetText("A2", "Заготовительный");
                s.SetText("B2", "Лазер; Ленточная пила");
                s.SetText("C2", "Резать аккуратно");
                s.SetText("D2", "Мастер; ОТК");
                book.Save();
                string problem;
                LzkBlanks blanks = LzkBlanks.Read(path, out problem);
                Assert.AreEqual("", problem, "без ошибок");
                Assert.AreEqual("Заготовительный", blanks.SectionOf("ленточная пила"), "операция из справочника");
                Assert.AreEqual("", blanks.SectionOf("Лазерная резка листа"), "список операций заменён целиком");
                Assert.AreEqual("Резать аккуратно", blanks.Get("Заготовительный").Notes, "указания");
                Assert.AreEqual("Мастер|ОТК", string.Join("|", blanks.Get("Заготовительный").Signatures), "подписи");
                Assert.AreEqual("Сварочный", blanks.SectionOf("Сварка"), "остальные участки — по умолчанию");
            }
            finally
            {
                File.Delete(path);
            }
        }

        public static void Test_Bar_groups_pack_equal_lengths_per_sortament()
        {
            const string tube = "Труба 40х20х1,5 ГОСТ 8645-68";
            List<LzkItem> items = new List<LzkItem>
            {
                new LzkItem { Designation = "А.001", Material = tube, Size = "L=556", Quantity = 2, MassKg = 0.624 },
                new LzkItem { Designation = "А.002", Material = tube, Size = "L=556", Quantity = 1, MassKg = 0.624 },
                new LzkItem { Designation = "А.003", Material = tube, Size = "400×50×25", Quantity = 1 },
                new LzkItem { Designation = "А.004", Material = "Лист 3 ГОСТ 19903", Size = "200×100×3", Quantity = 4, MassKg = 0.5 },
                new LzkItem { Designation = "А.005", Material = tube, Size = "L=300", Quantity = 1, IsPurchased = true },
                new LzkItem { Designation = "А.006", Material = "Пластик ABS", Size = "L=300", Quantity = 1 }
            };
            List<LzkBook.BarGroup> groups = LzkBook.BarGroups(items);
            Assert.AreEqual(2, groups.Count, "две длины одного сортамента; покупное, лист и пластик не режутся из хлыста");
            Assert.AreEqual(556.0, groups[0].LengthMm, "сначала длинные");
            Assert.AreEqual(3, groups[0].PerProduct, "одинаковые длины сложены");
            Assert.IsFalse(groups[0].Estimate, "длина из L=");
            Assert.IsTrue(Math.Abs(groups[0].KgPerMeter - 0.624 / 0.556) < 1e-9, "масса метра");
            Assert.AreEqual(400.0, groups[1].LengthMm, "длина по габариту");
            Assert.IsTrue(groups[1].Estimate, "по габариту — оценка");
            Assert.IsTrue(double.IsNaN(groups[1].KgPerMeter), "масса не известна");

            List<KeyValuePair<string, double[]>> sheets = LzkBook.SheetGroups(items);
            Assert.AreEqual(1, sheets.Count, "листовой сортамент");
            Assert.IsTrue(Math.Abs(sheets[0].Value[0] - 0.08) < 1e-9, "площадь: 0,2×0,1×4 = 0,08 м²");
            Assert.IsTrue(Math.Abs(sheets[0].Value[1] - 2.0) < 1e-9, "масса листа на изделие");

            bool estimate;
            Assert.AreEqual(1996.0, LzkBook.BlankLength("L=1996", out estimate), "L=");
            Assert.IsFalse(estimate, "точная длина");
            Assert.AreEqual(556.0, LzkBook.BlankLength("556×40×20*", out estimate), "наибольший размер габарита");
            Assert.IsTrue(estimate, "оценка");
        }

        public static void Test_Book_has_live_calculator_and_keeps_inputs()
        {
            string path = Template();
            string second = Temp();
            try
            {
                XlsxBook export = XlsxBook.Open(path);
                XlsxSheet s = export.Sheet("Ведомость");
                string[] row1 = { "1", "", "А.001", "Стойка", "Труба 40х20х1,5 ГОСТ 8645-68", "L=556", "0,624", "4", "Лазерная резка трубы; Покраска", @"D:\И\01_3D\А.001.sldprt" };
                string[] row2 = { "2", "", "А.001-01", "Стойка", "Труба 40х20х1,5 ГОСТ 8645-68", "L=300", "0,337", "2", "Лазерная резка трубы; Покраска", @"D:\И\01_3D\А.001.sldprt" };
                string[] row3 = { "3", "", "А.002", "Пластина", "Лист Б-ПН-НО-3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97", "200×100×3", "0,471", "1", "Лазерная резка; Покраска", @"D:\И\01_3D\А.002.sldprt" };
                for (int c = 0; c < row1.Length; c++)
                {
                    if (row1[c].Length > 0) s.SetText(XlsxBook.CellName(c + 1, 7), row1[c]);
                    if (row2[c].Length > 0) s.SetText(XlsxBook.CellName(c + 1, 8), row2[c]);
                    if (row3[c].Length > 0) s.SetText(XlsxBook.CellName(c + 1, 9), row3[c]);
                }
                s.SetNumber("H7", 4);
                s.SetNumber("H8", 2);
                s.SetNumber("H9", 1);
                export.Save();
                File.Copy(path, second);

                const string tube = "Труба 40х20х1,5 ГОСТ 8645-68";
                List<LzkItem> items = new List<LzkItem>
                {
                    new LzkItem { Path = @"D:\И\01_3D\А.000.sldasm", Designation = "А.000", Name = "Рама", IsAssembly = true, IsTop = true,
                        InProduct = true, Quantity = 1, Operations = "Механическая сборка" },
                    // Два исполнения одного файла: строки SWTools различаются обозначением.
                    new LzkItem { Path = @"D:\И\01_3D\А.001.sldprt", Configuration = "01", Designation = "А.001-01", Name = "Стойка",
                        InProduct = true, Quantity = 2, Material = tube, Size = "L=300", Operations = "Лазерная резка трубы; Покраска", AreaM2 = 0.03 },
                    new LzkItem { Path = @"D:\И\01_3D\А.001.sldprt", Configuration = "00", Designation = "А.001", Name = "Стойка",
                        InProduct = true, Quantity = 4, Material = tube, Size = "L=556", Operations = "Лазерная резка трубы; Покраска", AreaM2 = 0.05,
                        Section = "Детали" },
                    // Листовая деталь 200×100×3 из стали: 0,02 м² и 0,471 кг → 23,55 кг на 1 м² заготовки
                    new LzkItem { Path = @"D:\И\01_3D\А.002.sldprt", Designation = "А.002", Name = "Пластина", InProduct = true, Quantity = 1,
                        Material = "Лист Б-ПН-НО-3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97", Size = "200×100×3", MassKg = 0.471,
                        Operations = "Лазерная резка; Покраска", AreaM2 = 0.04 }
                };
                LzkInputs inputs = new LzkInputs { Quantity = 41, Deadline = new DateTime(2026, 10, 25), Color = "RAL 9005" };
                inputs.Notes["Сварочный"] = "Свой текст";
                inputs.Norms["Труба.Захват"] = 600;
                LzkHeader header = new LzkHeader { Product = "А Рама", Cipher = "А", Name = "Рама", Order = "3021_Заказ", Date = "18.09.2026",
                    Version = "23.09.2026 14:05:31 #1a2b3c4d" };
                LzkResult r = LzkWorkbook.Complete(path, header, items, new LzkBook.Options { Inputs = inputs, Norms = ShippedNorms() });
                Assert.AreEqual(0, r.Errors.Count, string.Join("; ", r.Errors.ToArray()));

                XlsxBook book = XlsxBook.Open(path);
                XlsxSheet main = book.Sheet("Ведомость");
                Assert.AreEqual("А.001", main.Get("C7"), "исполнение 00 — своя строка");
                Assert.AreEqual("А.001-01", main.Get("C8"), "исполнение 01 не перезаписано реквизитами 00");
                Assert.AreEqual("Деталь", main.Get("K7"), "категория");
                Assert.AreEqual("Заготовительный, Покрасочный", main.Get("L7"), "участки");
                Assert.AreEqual("H7*Тираж", main.Formula("M7"), "всего на заказ");
                Assert.IsTrue(Part(path, "xl/worksheets/sheet1.xml").Contains("<autoFilter ref=\"A6:M9\""), "фильтр по таблице");

                XlsxSheet cost = book.Sheet("Расход");
                Assert.AreEqual("Труба 40х20х1,5 ГОСТ 8645-68", cost.Get("A7"), "сортамент");
                Assert.AreEqual("556", cost.Get("B7"), "длинная заготовка первой");
                Assert.AreEqual("4", cost.Get("C7"), "на изделие");
                Assert.AreEqual("C7*Тираж", cost.Formula("D7"), "всего");
                Assert.AreEqual("300", cost.Get("B8"), "вторая длина");
                // Только сколько идёт на заготовки и то же с запасом: хлысты, закупка и списание — отдельный документ
                // (владелец 21.09.2026).
                Assert.AreEqual("Чистая длина на 1 изд., м", cost.Get("E6"), "шапка: чистая длина на изделие");
                Assert.AreEqual("C7*B7/1000", cost.Formula("E7"), "чистая длина на изделие");
                Assert.AreEqual("D7*B7/1000", cost.Formula("F7"), "чистая длина на заказ");
                Assert.AreEqual("Запас, %", cost.Get("G6"), "шапка запаса");
                Assert.AreEqual("ЗапасТруба", cost.Formula("G7"), "запас трубы — норматив «Нормы»");
                Assert.AreEqual("F7*(1+G7/100)", cost.Formula("H7"), "с запасом на заказ");
                Assert.AreEqual("IF(ISNUMBER(I7),F7*I7,\"\")", cost.Formula("J7"), "масса в чистоте = чистая длина × масса 1 м");
                Assert.AreEqual("IF(ISNUMBER(I7),H7*I7,\"\")", cost.Formula("K7"), "масса с запасом");
                Assert.AreEqual("Итого: " + tube, cost.Get("A9"), "итог назван по сортаменту — одна строка «Сводной»");
                Assert.AreEqual("SUM(F7:F8)", cost.Formula("F9"), "итого чистая длина на заказ");
                Assert.AreEqual("SUM(H7:H8)", cost.Formula("H9"), "итого с запасом");
                Assert.AreEqual("SUM(J7:J8)", cost.Formula("J9"), "итого масса в чистоте");
                Assert.IsTrue(book.Sheet("Раскрой") == null, "скрытого листа раскроя больше нет");
                int sheetHead = 0;
                for (int line = 5; line < 30; line++)
                {
                    foreach (string col in new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L" })
                    {
                        string v = cost.Get(col + line) + " " + cost.Formula(col + line);
                        Assert.IsFalse(v.Contains("Хлыст") || v.Contains("хлыст") || v.Contains("акупк") || v.Contains("КИМ"),
                            "на «Расходе» нет хлыстов, закупки и КИМ: " + col + line + " = " + v);
                    }
                    if (cost.Get("A" + line).StartsWith("Листовой прокат")) sheetHead = line + 1;
                }
                Assert.IsTrue(sheetHead > 9, "таблица листа после труб");
                string sr = (sheetHead + 1).ToString();
                Assert.AreEqual("Площадь на заказ, м²", cost.Get("C" + sheetHead), "шапка листа: площадь на заказ");
                Assert.AreEqual("Лист Б-ПН-НО-3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97", cost.Get("A" + sr), "строка листа");
                Assert.AreEqual("B" + sr + "*Тираж", cost.Formula("C" + sr), "площадь на заказ");
                Assert.AreEqual("ЗапасЛист", cost.Formula("D" + sr), "запас листа — свой норматив");
                Assert.AreEqual("C" + sr + "*(1+D" + sr + "/100)", cost.Formula("E" + sr), "площадь с запасом");
                Assert.AreEqual("0.471", cost.Get("F" + sr), "масса листа на изделие");
                Assert.AreEqual("F" + sr + "*Тираж", cost.Formula("G" + sr), "масса в чистоте на заказ");
                Assert.AreEqual("G" + sr + "*(1+D" + sr + "/100)", cost.Formula("H" + sr), "масса с запасом");

                // «Сводная» (замечание владельца 21.09.2026): одна строка на позицию — металл, краска, покупные; числа —
                // формулами с «Расхода» и «Комплектовочного»; в чистоте и с запасом, без хлыстов, закупки и списания.
                XlsxSheet summary = book.Sheet("Сводная");
                Assert.IsTrue(summary != null, "лист «Сводная»");
                Assert.AreEqual("Сводная ведомость расхода материалов", summary.Get("A1"), "название без списания");
                Assert.AreEqual("Сортамент, материал, изделие", summary.Get("B6"), "шапка");
                // Широкая шапка: срок в G3 (слияние G3:H3), цвет правее — в J3; срок не затирается цветом.
                Assert.AreEqual("Срок отгрузки:", summary.Get("D3"), "подпись срока");
                Assert.IsTrue(summary.Formula("G3").Contains("Срок"), "в G3 срок отгрузки, а не цвет: " + summary.Formula("G3"));
                Assert.AreEqual("Цвет:", summary.Get("I3"), "подпись цвета не под слиянием D3:F3");
                Assert.IsTrue(summary.Formula("J3").Contains("Цвет"), "цвет в J3: " + summary.Formula("J3"));
                Assert.AreEqual("", summary.Get("F3"), "в F3 (внутри слияния D3:F3) ничего нет");
                XlsxSheet painting = book.Sheet("Покрасочный");
                Assert.AreEqual("Цвет:", painting.Get("F3"), "узкий лист: цвет на прежнем месте");
                Assert.IsTrue(painting.Formula("E3").Contains("Срок"), "узкий лист: срок в E3");
                Assert.IsTrue(painting.Formula("G3").Contains("Цвет"), "узкий лист: цвет в G3");
                Assert.AreEqual("В чистоте на 1 изд.", summary.Get("D6"), "шапка: в чистоте на изделие");
                Assert.AreEqual("Запас, %", summary.Get("F6"), "шапка: запас");
                Assert.AreEqual("С запасом на заказ", summary.Get("G6"), "шапка: с запасом");
                for (int c = 1; c <= 12; c++)
                {
                    string h = summary.Get(((char)('A' + c - 1)).ToString() + "6");
                    Assert.IsFalse(h.Contains("лыст") || h.Contains("акупк") || h == "КИМ", "в шапке «Сводной» нет хлыстов и закупки: " + h);
                }
                Assert.AreEqual(tube, summary.Get("B8"), "строка сортамента");
                Assert.AreEqual("м", summary.Get("C8"), "единица");
                Assert.AreEqual("'Расход'!E9", summary.Formula("D8"), "в чистоте на изделие, м");
                Assert.AreEqual("'Расход'!F9", summary.Formula("E8"), "в чистоте на заказ, м");
                Assert.AreEqual("'Расход'!G9", summary.Formula("F8"), "запас, %");
                Assert.AreEqual("'Расход'!H9", summary.Formula("G8"), "с запасом на заказ, м");
                // Масса в чистоте на 1 изделие (владелец 22.09.2026): длина на изделие × масса 1 м по строкам сортамента.
                Assert.AreEqual("Масса в чистоте на 1 изд., кг", summary.Get("H6"), "шапка: масса на изделие");
                Assert.AreEqual("Масса в чистоте на заказ, кг", summary.Get("I6"), "шапка: масса на заказ");
                Assert.AreEqual("SUMPRODUCT('Расход'!E7:E8,'Расход'!I7:I8)", summary.Formula("H8"), "масса в чистоте на изделие");
                Assert.AreEqual("'Расход'!J9", summary.Formula("I8"), "масса в чистоте");
                Assert.AreEqual("'Расход'!K9", summary.Formula("J8"), "масса с запасом");
                // Запас — норматив с листа «Нормы», свой на трубу и на лист (владелец 21.09.2026); в справочнике его нет — по умолчанию 10 %
                string allowanceSheet, allowanceCell;
                Assert.IsTrue(book.TryResolveName("ЗапасТруба", out allowanceSheet, out allowanceCell), "имя «ЗапасТруба»");
                Assert.AreEqual("10", book.Sheet(allowanceSheet).Get(allowanceCell), "запас по умолчанию 10 %");
                Assert.IsTrue(book.TryResolveName("ЗапасЛист", out allowanceSheet, out allowanceCell), "имя «ЗапасЛист»");
                Assert.AreEqual("10", book.Sheet(allowanceSheet).Get(allowanceCell), "запас листа по умолчанию 10 %");
                Assert.AreEqual("м²", summary.Get("C9"), "строка листа в сводной");
                Assert.AreEqual("'Расход'!B" + sr, summary.Formula("D9"), "площадь листа на изделие");
                Assert.AreEqual("'Расход'!C" + sr, summary.Formula("E9"), "площадь листа на заказ");
                Assert.AreEqual("'Расход'!D" + sr, summary.Formula("F9"), "запас листа");
                Assert.AreEqual("'Расход'!E" + sr, summary.Formula("G9"), "площадь с запасом");
                Assert.AreEqual("'Расход'!F" + sr, summary.Formula("H9"), "масса листа на изделие");
                Assert.AreEqual("'Расход'!G" + sr, summary.Formula("I9"), "масса листа в чистоте");
                Assert.AreEqual("'Расход'!H" + sr, summary.Formula("J9"), "масса листа с запасом");
                Assert.AreEqual("Итого металлопрокат, кг", summary.Get("A10"), "итог по металлу");
                Assert.AreEqual("SUM(H8:H9)", summary.Formula("H10"), "итого масса на изделие");
                Assert.AreEqual("SUM(I8:I9)", summary.Formula("I10"), "итого масса в чистоте");
                Assert.AreEqual("SUM(J8:J9)", summary.Formula("J10"), "итого масса с запасом");
                int paintRow = 0, kitRow = 0;
                for (int line = 10; line < 30; line++)
                {
                    if (summary.Get("C" + line) == "кг" && paintRow == 0) paintRow = line;
                    if (summary.Get("A" + line) == "Покупных в изделии нет") kitRow = line;
                }
                Assert.IsTrue(paintRow > 0, "строка краски");
                Assert.IsTrue(summary.Formula("B" + paintRow).StartsWith("\"Краска порошковая, \"&IF(Цвет="), "краска с цветом из паспорта");
                Assert.IsTrue(summary.Formula("E" + paintRow).StartsWith("'Расход'!B"), "кг краски на заказ — с «Расхода»: " + summary.Formula("E" + paintRow));
                Assert.AreEqual("шт", summary.Get("C" + (paintRow + 1)), "тара");
                Assert.IsTrue(kitRow > paintRow, "покупных в этой книге нет — сказано словами");

                XlsxSheet passport = book.Sheet("Паспорт");
                string sheet, cell;
                Assert.IsTrue(book.TryResolveName("Тираж", out sheet, out cell), "имя «Тираж»");
                Assert.AreEqual("Паспорт", sheet, "тираж — в паспорте");
                Assert.AreEqual("41", passport.Get(cell), "тираж из окна");
                Assert.IsTrue(book.TryResolveName("Захват", out sheet, out cell), "норматив");
                Assert.AreEqual("600", book.Sheet(sheet).Get(cell), "норма, правленная под заказ");
                Assert.IsTrue(book.TryResolveName("Хлыст", out sheet, out cell), "хлыст");
                Assert.AreEqual("6000", book.Sheet(sheet).Get(cell), "хлыст 6 м из справочника инструментария");
                // Версия изделия (З-27): по ней проверка и «Готово к производству» сверяют книгу с изделием.
                string version;
                Assert.IsTrue(LzkInputs.TryReadVersion(book, out version), "в паспорте есть версия изделия");
                Assert.AreEqual("23.09.2026 14:05:31 #1a2b3c4d", version, "версия из проверки");
                Assert.IsTrue(book.TryResolveName(LzkBook.NameVersion, out sheet, out cell), "имя версии");
                Assert.AreEqual("Паспорт", sheet, "версия — в паспорте");

                LzkInputs read = LzkInputs.Read(path);
                Assert.IsTrue(read.FromWorkbook, "прочитано из книги");
                Assert.AreEqual(41, read.Quantity, "тираж сохранён");
                Assert.AreEqual(new DateTime(2026, 10, 25), read.Deadline.Value, "срок сохранён");
                Assert.AreEqual("RAL 9005", read.Color, "цвет сохранён");
                Assert.AreEqual("3021_Заказ", read.Order, "заказ");
                Assert.AreEqual("Свой текст", read.Notes["Сварочный"], "указания участка сохранены");
                Assert.AreEqual(600.0, read.Norms["Труба.Захват"], "норма сохранена");

                // Повторная сборка по прочитанному — те же значения.
                header.Version = "";
                LzkWorkbook.Complete(second, header, items, new LzkBook.Options { Inputs = read, Norms = ShippedNorms() });
                using (XlsxBook rebuilt = XlsxBook.Open(second))
                {
                    string none;
                    Assert.IsTrue(LzkInputs.TryReadVersion(rebuilt, out none), "строка версии есть и без проверки");
                    Assert.AreEqual("", none, "книга по непроверенному изделию — версия пустая");
                    Assert.IsTrue(rebuilt.TryResolveName(LzkBook.NameVersion, out sheet, out cell), "имя версии");
                    Assert.AreEqual(ProductStamp.Unchecked, rebuilt.Sheet(sheet).Get(cell), "в ячейке — словами");
                }
                LzkInputs again = LzkInputs.Read(second);
                Assert.AreEqual(41, again.Quantity, "тираж пережил пересборку");
                Assert.AreEqual("Свой текст", again.Notes["Сварочный"], "указания пережили пересборку");
            }
            finally
            {
                File.Delete(path);
                File.Delete(second);
            }
        }
    }
}
