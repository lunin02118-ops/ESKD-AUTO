using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Свод сводной заявки: тираж, группировка и расход (ТЗ-02 Т-40).</summary>
    public static class ProdRequestTests
    {
        private static readonly string[,] NormValues =
        {
            { "Труба.Хлыст", "6000" }, { "Труба.Захват", "200" }, { "Труба.Торцовка", "20" },
            { "Труба.Рез", "0,5" }, { "Труба.Деловой", "500" }, { "Лист.Формат", "1250x2500" },
            { "Лист.Отход", "1,15" }, { "Краска.Норма", "140" }, { "Краска.Потери", "15" }, { "Краска.Тара", "25" }
        };

        private static Norms Reference()
        {
            string folder = Path.Combine(Path.GetTempPath(), "eskd_request_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Norms.PathIn(folder);
                using (XlsxBook book = XlsxBook.Create(path, Norms.SheetName))
                {
                    XlsxSheet sheet = book.Sheet(Norms.SheetName);
                    sheet.SetText("A1", "Ключ");
                    sheet.SetText("B1", "Значение");
                    for (int i = 0; i < NormValues.GetLength(0); i++)
                    {
                        sheet.SetText("A" + (i + 2), NormValues[i, 0]);
                        sheet.SetText("B" + (i + 2), NormValues[i, 1]);
                    }
                    book.Save();
                }
                string problem;
                Norms norms = Norms.Read(path, out problem);
                Assert.NotNull(norms, "нормативы: " + problem);
                return norms;
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        /// <summary>Изделие из двух стоек, листовой пластины и покупного болта.</summary>
        private static RequestProduct Product(string cipher, int quantity)
        {
            RequestProduct product = new RequestProduct { Cipher = cipher, Name = cipher, Quantity = quantity };
            product.Lines.Add(new RequestLine
            {
                Designation = "ПРТИ.468211.102", Name = "Стойка", Material = "Труба 50х25х1.5 ГОСТ 8645-68",
                Size = "L=1996", MassKg = 2.4, Quantity = 2, Operations = "Труборез; Сварка; Покраска"
            });
            product.Lines.Add(new RequestLine
            {
                Designation = "ПРТИ.468211.101", Name = "Пластина опорная", Material = "Лист 6 Ст3сп ГОСТ 19903-2015",
                Size = "200x120x6", MassKg = 1.13, Quantity = 1, Operations = "Лазерный раскрой; Покраска"
            });
            product.Lines.Add(new RequestLine
            {
                Designation = cipher, Name = "Изделие", MassKg = 12.5, Quantity = 1,
                Operations = "Сварка", IsAssembly = true
            });
            product.Painted.Add(new RequestLine { Designation = "ПРТИ.468211.102", Name = "Стойка", AreaM2 = 0.5, Quantity = 2 });
            product.Purchased.Add(new RequestLine
            {
                Name = "Болт М10х40", Designation = "ГОСТ 7798-70", Code = "00-00012345", Unit = "шт", Okei = "796", Quantity = 4
            });
            return product;
        }

        public static void Test_Quantities_are_multiplied_by_the_run()
        {
            ProdRequestData data = ProdRequest.Build(new[] { Product("ПРТИ.468211.100", 3) }, Reference(),
                new Dictionary<string, string> { { "ПРТИ.468211.102", "RAL 7035 шагрень" } });
            Assert.AreEqual(3, data.TotalProducts, "единиц в заявке");
            RequestLine stoika = data.Blanks.First(l => l.Designation == "ПРТИ.468211.102");
            Assert.AreEqual(6, stoika.Quantity, "2 стойки × 3 изделия");
            Assert.AreEqual(3, data.Units.Single().Quantity, "сборка изделия — в листе сварки");
            Assert.AreEqual(12, data.Purchased.Single().Quantity, "покупные тоже умножаются");
            Assert.AreEqual(0, data.Issues.Count, "цвет назначен — замечаний нет");
        }

        public static void Test_Same_part_in_two_products_is_summed_once()
        {
            ProdRequestData data = ProdRequest.Build(
                new[] { Product("ПРТИ.468211.100", 1), Product("ПРТИ.468211.200", 2) }, Reference(),
                new Dictionary<string, string> { { "ПРТИ.468211.102", "RAL 9005 шагрень" } });
            Assert.AreEqual(1, data.Blanks.Count(l => l.Designation == "ПРТИ.468211.102"), "одна строка на сортамент и номер");
            Assert.AreEqual(6, data.Blanks.First(l => l.Designation == "ПРТИ.468211.102").Quantity, "2×1 + 2×2");
            Assert.AreEqual(2, data.Units.Count, "сборки изделий не сливаются: у них разные обозначения");
        }

        public static void Test_Consumption_counts_bars_sheets_and_paint()
        {
            ProdRequestData data = ProdRequest.Build(new[] { Product("ПРТИ.468211.100", 2) }, Reference(),
                new Dictionary<string, string> { { "ПРТИ.468211.102", "RAL 7035 шагрень" } });
            ConsumptionRow tube = data.Consumption.First(r => r.Sortament.Contains("Труба"));
            // 4 стойки по 1996 мм: в зону реза 5800 мм влезают две — два хлыста.
            Assert.AreEqual(2, tube.Bars, "хлысты");
            Assert.AreEqual(8.0, tube.CleanM, "чистый расход, м (4 × 1996 мм, округление до 0,1)");
            Assert.IsTrue(tube.Kim.EndsWith("%"), "КИМ: " + tube.Kim);

            ConsumptionRow sheet = data.Consumption.First(r => r.Sortament.Contains("Лист"));
            Assert.AreEqual(1, sheet.Sheets, "две пластины 200×120 умещаются на одном листе");

            // Покраска: 0,5 м² × 2 стойки × 2 изделия = 2 м²; 2 × 140 г × 1,15 = 0,322 кг → в заявке 0,3 кг.
            Assert.AreEqual(0.3, data.PaintKg, "краска, кг");
            Assert.AreEqual(1, data.PaintCans, "одна тара");
        }

        public static void Test_Unpainted_unit_is_reported_not_guessed()
        {
            ProdRequestData data = ProdRequest.Build(new[] { Product("ПРТИ.468211.100", 1) }, Reference(),
                new Dictionary<string, string>());
            Assert.AreEqual(1, data.Issues.Count, "замечание о цвете: " + string.Join("; ", data.Issues.ToArray()));
            Assert.IsTrue(data.Issues[0].Contains("Не назначен цвет"), data.Issues[0]);
            Assert.IsTrue(data.Norms.Count >= 10, "использованные нормативы перечислены для листа 5");
        }
    }
}
