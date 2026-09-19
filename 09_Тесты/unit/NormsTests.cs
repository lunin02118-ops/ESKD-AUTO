using System;
using System.IO;
using System.Linq;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Нормативы производства и раскрой хлыстов (ТЗ-02 Т-13).</summary>
    public static class NormsTests
    {
        private static readonly string[,] Values =
        {
            { "Труба.Хлыст", "6000" }, { "Труба.Захват", "200" }, { "Труба.Торцовка", "20" },
            { "Труба.Рез", "0,5" }, { "Труба.Деловой", "500" }, { "Лист.Формат", "1250x2500" },
            { "Лист.Отход", "1,15" }, { "Краска.Норма", "140" }, { "Краска.Потери", "15" }, { "Краска.Тара", "25" }
        };

        /// <summary>Справочник во временной папке: книга собирается тем же кодом, что заводит журнал изменений.</summary>
        private static string Book(string folder, params string[] skip)
        {
            string path = Norms.PathIn(folder);
            using (XlsxBook book = XlsxBook.Create(path, Norms.SheetName))
            {
                XlsxSheet sheet = book.Sheet(Norms.SheetName);
                sheet.SetText("A1", "Ключ");
                sheet.SetText("B1", "Значение");
                int row = 2;
                for (int i = 0; i < Values.GetLength(0); i++)
                {
                    if (skip.Contains(Values[i, 0])) continue;
                    sheet.SetText("A" + row, Values[i, 0]);
                    sheet.SetText("B" + row, Values[i, 1]);
                    row++;
                }
                book.Save();
            }
            return path;
        }

        private static string Temp()
        {
            string folder = Path.Combine(Path.GetTempPath(), "eskd_norms_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static void Test_Shipped_reference_book_is_complete_and_found_in_toolkit()
        {
            // 02_Шаблоны_и_Форматки\Справочники инструментария (ТЗ-02 Т-13): встроенных нормативов у надстройки нет
            string references = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, @"..\..\..\02_Шаблоны_и_Форматки\Справочники"));
            string problem;
            Norms norms = Norms.Read(Norms.Find(Temp(), references), out problem);
            Assert.IsTrue(norms != null, "справочник инструментария: " + problem);
            Assert.AreEqual(6000.0, norms.Number("Труба.Хлыст"), "хлыст 6 м (Р4-5)");
            double width, length;
            norms.Pair("Лист.Формат", out width, out length);
            Assert.AreEqual(2500.0, length, "формат листа");
            Assert.AreEqual(0.5, norms.Number("Труба.Рез"), "рез");
            Assert.AreEqual("", Norms.Find(Temp(), Temp()), "нигде нет — пусто, без значений по умолчанию");
        }

        public static void Test_Norms_are_read_from_reference_book()
        {
            string folder = Temp();
            try
            {
                string problem;
                Norms norms = Norms.Read(Book(folder), out problem);
                Assert.NotNull(norms, "справочник прочитан: " + problem);
                Assert.AreEqual("", problem, "без ошибок");
                Assert.AreEqual(6000.0, norms.Number("Труба.Хлыст"), "длина хлыста");
                Assert.AreEqual(0.5, norms.Number("Труба.Рез"), "ширина пропила через запятую");
                double width, length;
                norms.Pair("Лист.Формат", out width, out length);
                Assert.AreEqual(1250.0, width, "ширина листа");
                Assert.AreEqual(2500.0, length, "длина листа");
                Assert.AreEqual(10, norms.All.Count(), "все нормативы на месте");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        public static void Test_Missing_reference_is_explained_not_guessed()
        {
            string folder = Temp();
            try
            {
                string problem;
                Assert.IsNull(Norms.Read(Path.Combine(folder, "нет.xlsx"), out problem), "файла нет");
                Assert.IsTrue(problem.Contains("не найден"), problem);
                Assert.IsTrue(problem.Contains("встроенных нормативов"), "объяснено, почему нельзя продолжать: " + problem);

                Assert.IsNull(Norms.Read(Book(folder, "Краска.Норма"), out problem), "не заполнен норматив");
                Assert.IsTrue(problem.Contains("Краска.Норма"), problem);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        public static void Test_Bars_are_planned_greedily()
        {
            // Образец заявки 85_Т: 82 связи по 1996 мм из трубы 50х25 — по две на хлыст, всего 41 хлыст.
            double[] pieces = Enumerable.Repeat(1996.0, 82).ToArray();
            CutResult plan = CutPlan.Plan("Труба 50х25х1.5", pieces, 6000, 200, 20, 0.5, 500);
            Assert.AreEqual(41, plan.BarCount, "хлыстов");
            Assert.AreEqual(82, plan.Bars.Sum(b => b.Pieces.Count), "все заготовки разложены");
            Assert.AreEqual(163672.0, plan.CleanMm, "чистый расход, мм");
            Assert.AreEqual(246000.0, plan.BoughtMm, "закуплено, мм");
            Assert.AreEqual(41, plan.BusinessRests.Count, "деловой обрезок с каждого хлыста");
            // 6000 − захват 200 − торцовка 20 (один конец, как в формуле книги) − 2×(1996 + 0,5) = 1787 мм.
            Assert.IsTrue(plan.BusinessRests.All(r => r > 1780 && r < 1790), "обрезок около 1787 мм: " +
                string.Join(", ", plan.BusinessRests.Take(3).Select(r => r.ToString("0")).ToArray()));
            Assert.AreEqual("66.5%", plan.KimText, "КИМ как в образце заявки");
        }

        public static void Test_Large_run_is_planned_quickly()
        {
            // Тираж в тысячи изделий: раскрой пачками одинаковых длин, а не по одной штуке (аудит 19.09, Л-К6).
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            double[] pieces = Enumerable.Repeat(1000.0, 20000).Concat(Enumerable.Repeat(700.0, 20000)).ToArray();
            CutResult plan = CutPlan.Plan("Труба 20х20", pieces, 6000, 200, 20, 0.5, 500);
            watch.Stop();
            Assert.AreEqual(40000, plan.Bars.Sum(b => b.Pieces.Count), "все заготовки разложены");
            // По 5 длинных на хлыст (5002,5 мм из 5780), в остаток 777,5 — ещё одна короткая; остальные короткие по 8.
            Assert.AreEqual(4000 + (20000 - 4000 + 7) / 8, plan.BarCount, "хлыстов");
            Assert.IsTrue(watch.ElapsedMilliseconds < 3000, "раскрой 40 000 заготовок: " + watch.ElapsedMilliseconds + " мс");
        }

        public static void Test_Short_rest_is_waste_and_long_piece_is_reported()
        {
            CutResult plan = CutPlan.Plan("Труба 20х20", new[] { 2800.0, 2800.0 }, 6000, 200, 20, 0.5, 500);
            Assert.AreEqual(1, plan.BarCount, "обе заготовки с одного хлыста");
            Assert.AreEqual(0, plan.BusinessRests.Count, "остаток 179 мм деловым не считается");
            Assert.IsTrue(plan.WasteMm > 390 && plan.WasteMm < 410, "отход — захват, торцовка, пропилы и хвост: " + plan.WasteMm);

            CutResult tooLong = CutPlan.Plan("Труба 20х20", new[] { 5900.0 }, 6000, 200, 20, 0.5, 500);
            Assert.AreEqual(0, tooLong.BarCount, "заготовку длиннее зоны реза не раскроить");
            Assert.AreEqual(1, tooLong.TooLong.Count, "она названа в замечаниях");
        }

        public static void Test_Sheets_and_paint_follow_norms()
        {
            string folder = Temp();
            try
            {
                string problem;
                Norms norms = Norms.Read(Book(folder), out problem);
                double sheetArea, kim;
                Assert.AreEqual(2, CutPlan.Sheets(5.0, norms, out sheetArea, out kim), "два листа 1250×2500 на 5 м² с отходом");
                Assert.AreEqual(3.125, sheetArea, "площадь листа, м²");
                Assert.IsTrue(kim > 0.79 && kim < 0.81, "КИМ листа: " + kim);

                int cans;
                double kilograms = CutPlan.Paint(100.0, norms, out cans);
                Assert.IsTrue(Math.Abs(kilograms - 16.1) < 0.01, "краска: 100 м² × 140 г/м² × 1,15 = 16,1 кг, получено " + kilograms);
                Assert.AreEqual(1, cans, "одна коробка 25 кг");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
