using System;
using System.Collections.Generic;
using System.IO;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    public static class LzkNamingTests
    {
        public static void Test_ProductFolder_is_parent_of_models_folder()
        {
            Assert.AreEqual(@"D:\З\И01_ПРТИ.468211.100_Кондуктор",
                LzkNaming.ProductFolder(@"D:\З\И01_ПРТИ.468211.100_Кондуктор\01_3D\ПРТИ.468211.100.sldasm"), "01_3D");
            Assert.AreEqual(@"D:\З\Прочее", LzkNaming.ProductFolder(@"D:\З\Прочее\Сборка.sldasm"), "без 01_3D");
        }

        public static void Test_Cipher_from_folder_then_designation()
        {
            Assert.AreEqual("ПРТИ.468211.100",
                LzkNaming.Cipher(@"D:\З\И01_ПРТИ.468211.100_Кондуктор", @"D:\З\И01_ПРТИ.468211.100_Кондуктор\01_3D\X.sldasm"), "папка");
            Assert.AreEqual("КТО.2104",
                LzkNaming.Cipher(@"D:\З\Прочее", @"D:\З\Прочее\КТО.2104.00.00.000 Рама.sldasm"), "нулевые хвосты");
            Assert.AreEqual("Рама сварная", LzkNaming.Cipher(@"D:\З\Прочее", @"D:\З\Прочее\Рама сварная.sldasm"), "имя файла");
        }

        public static void Test_Paths_and_archive_do_not_overwrite()
        {
            string dir = Path.Combine(Path.GetTempPath(), "eskd_lzk_" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.AreEqual(Path.Combine(dir, "04_Сопроводительная документация", "ЛЗК_А_Б.xlsx"), LzkNaming.WorkbookPath(dir, "А/Б"),
                    "книга ЛЗК — в сопроводительной документации, недопустимый символ заменён");
                Assert.AreEqual(Path.Combine(dir, "Ведомость_А_Б.xlsx"), LzkNaming.LegacyWorkbookPath(dir, "А/Б"), "старый образец");
                DateTime stamp = new DateTime(2026, 9, 17, 15, 4, 0);
                string first = LzkNaming.ArchivePath(dir, "Ш", stamp);
                Assert.AreEqual(Path.Combine(dir, "_Аннулировано", "ЛЗК_Ш_2026-09-17_1504.xlsx"), first, "архив");
                Assert.AreEqual(Path.Combine(dir, "_Аннулировано", "Ведомость_Ш_2026-09-17_1504.xlsx"),
                    LzkNaming.ArchivePath(dir, "Ш", stamp, true), "архив старого образца");
                Directory.CreateDirectory(Path.GetDirectoryName(first));
                File.WriteAllText(first, "");
                Assert.AreEqual(Path.Combine(dir, "_Аннулировано", "ЛЗК_Ш_2026-09-17_1504_2.xlsx"),
                    LzkNaming.ArchivePath(dir, "Ш", stamp), "второй архив в ту же минуту");
                Assert.IsTrue(LzkNaming.IsInside(Path.Combine(dir, "01_3D", "a.sldprt"), dir), "внутри");
                Assert.IsFalse(LzkNaming.IsInside(dir + "2\\a.sldprt", dir), "соседняя папка с тем же началом");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        public static void Test_FindWorkbook_prefers_new_book_then_legacy()
        {
            string dir = Path.Combine(Path.GetTempPath(), "eskd_lzk_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                Assert.AreEqual("", LzkNaming.FindWorkbook(dir, "Ш"), "ничего нет");
                File.WriteAllText(LzkNaming.LegacyWorkbookPath(dir, "Ш"), "");
                Assert.AreEqual(LzkNaming.LegacyWorkbookPath(dir, "Ш"), LzkNaming.FindWorkbook(dir, "Ш"), "старый образец");
                Assert.IsTrue(LzkNaming.IsLegacy(LzkNaming.FindWorkbook(dir, "Ш")), "признак старого образца");
                Directory.CreateDirectory(Path.Combine(dir, LzkNaming.DocsFolder));
                File.WriteAllText(LzkNaming.WorkbookPath(dir, "Ш"), "");
                Assert.AreEqual(LzkNaming.WorkbookPath(dir, "Ш"), LzkNaming.FindWorkbook(dir, "Ш"), "новая книга важнее");
                Assert.IsFalse(LzkNaming.IsLegacy(LzkNaming.FindWorkbook(dir, "Ш")), "новый образец");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }

    public static class LzkOperationsTests
    {
        public static void Test_Join_orders_by_route_and_keeps_unknown()
        {
            Assert.AreEqual("Лазерная резка листа; Гибка; Покраска; Зенковка",
                LzkOperations.Join(new[] { "покраска", "Зенковка", "Гибка", "Лазерная резка листа", "Гибка" }), "порядок");
            Assert.AreEqual(3, LzkOperations.Parse("Гибка, Покраска;\nСварная сборка").Count, "разделители");
            Assert.IsTrue(LzkOperations.Contains("гибка; покраска", LzkOperations.Painting), "регистр");
            Assert.AreEqual("", LzkOperations.Join(new string[0]), "пусто");
        }

        public static void Test_Suggest_by_model_traits()
        {
            Assert.AreEqual("Лазерная резка листа; Гибка",
                LzkOperations.Join(LzkOperations.Suggest(new ModelTraits { IsSheetMetal = true, HasBends = true })), "лист со сгибами");
            Assert.AreEqual("Лазерная резка трубы",
                LzkOperations.Join(LzkOperations.Suggest(new ModelTraits { IsStructuralMember = true })), "профиль");
            Assert.AreEqual("Сварная сборка; Покраска",
                LzkOperations.Join(LzkOperations.Suggest(new ModelTraits { IsAssembly = true, HasWeldBeads = true })),
                "сварная: узел красится целиком (З-1)");
            Assert.AreEqual("Механическая сборка",
                LzkOperations.Join(LzkOperations.Suggest(new ModelTraits { IsAssembly = true })), "механическая");
            Assert.AreEqual(0, LzkOperations.Suggest(new ModelTraits()).Count, "прочая деталь");
        }

        public static void Test_Size_and_numbers()
        {
            Assert.AreEqual("1200,5×300×2", LzkOperations.FormatSize(2, 1200.49, 300), "по убыванию, десятые");
            Assert.AreEqual("L=600", LzkOperations.FormatLength(-600.01), "длина");
            Assert.AreEqual(1200.5, LzkOperations.ParseNumber("1 200,5 мм"), "пробел и запятая");
            Assert.AreEqual(600.0, LzkOperations.ParseNumber("L=600"), "L=");
            Assert.IsTrue(double.IsNaN(LzkOperations.ParseNumber("нет")), "не число");
        }
    }

    public static class SwToolsExportTests
    {
        public static void Test_ReadResult_and_Explain()
        {
            string path = Path.Combine(Path.GetTempPath(), "eskd_swt_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                SwToolsExport.Outcome none = SwToolsExport.ReadResult(path);
                Assert.IsFalse(none.Found, "нет файла");
                Assert.IsTrue(SwToolsExport.Explain(none, 7).Contains("код 7"), "без отчёта — код процесса");

                File.WriteAllText(path, "schema=swtools.headless-bom-export.v1\r\nstatus=OK\r\nexit_code=0\r\nrows=12\r\nversion=1.1.109\r\nerror=\r\n");
                SwToolsExport.Outcome ok = SwToolsExport.ReadResult(path);
                Assert.IsTrue(ok.Found && ok.Ok, "успех");
                Assert.AreEqual(12, ok.Rows, "строк");
                Assert.AreEqual("1.1.109", ok.Version, "версия");
                Assert.AreEqual("", SwToolsExport.Explain(ok, 0), "без пояснения");

                File.WriteAllText(path, "schema=swtools.headless-bom-export.v1\r\nstatus=FAILED\r\nexit_code=3\r\nerror=license\r\n");
                SwToolsExport.Outcome lic = SwToolsExport.ReadResult(path);
                Assert.IsFalse(lic.Ok, "ошибка");
                Assert.IsTrue(SwToolsExport.Explain(lic, 3).Contains("лицензии"), "лицензия");

                File.WriteAllText(path, "schema=other\r\nstatus=OK\r\n");
                Assert.IsFalse(SwToolsExport.ReadResult(path).Found, "чужая схема");
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// U-LZK-2 (владелец, 20.09.2026): потерянный по дороге пакет данных отличается от ошибки в изделии —
        /// выгрузка повторяется сама, а конструктору называется причина, а не следствие.
        /// </summary>
        public static void Test_DeliveryLost_is_told_apart_from_real_errors()
        {
            string path = Path.Combine(Path.GetTempPath(), "eskd_swt_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                foreach (string error in new[] { "Не выбрано ни одного узла",
                                                 "Нет данных для экспорта: SolidWorks не вернул состав модели",
                                                 "нет данных для экспорта" })
                {
                    File.WriteAllText(path, "schema=swtools.headless-bom-export.v1\r\nstatus=FAILED\r\nexit_code=1\r\nerror=" + error + "\r\n");
                    SwToolsExport.Outcome lost = SwToolsExport.ReadResult(path);
                    Assert.IsTrue(SwToolsExport.DeliveryLost(lost), "потерянный пакет: " + error);
                    Assert.AreEqual(SwToolsExport.DeliveryLostExplanation, SwToolsExport.Explain(lost, -1), "причина, а не следствие: " + error);
                }

                // Настоящие отказы повторять нельзя: повтор ничего не изменит, а конструктор ждёт вдвое дольше.
                File.WriteAllText(path, "schema=swtools.headless-bom-export.v1\r\nstatus=FAILED\r\nexit_code=3\r\nerror=Нет действующей лицензии\r\n");
                Assert.IsFalse(SwToolsExport.DeliveryLost(SwToolsExport.ReadResult(path)), "лицензия — не потеря пакета");
                File.WriteAllText(path, "schema=swtools.headless-bom-export.v1\r\nstatus=FAILED\r\nexit_code=1\r\nerror=Шаблон пресета должен быть книгой .xlsx\r\n");
                Assert.IsFalse(SwToolsExport.DeliveryLost(SwToolsExport.ReadResult(path)), "шаблон — не потеря пакета");
                File.WriteAllText(path, "schema=swtools.headless-bom-export.v1\r\nstatus=OK\r\nexit_code=0\r\nrows=3\r\nerror=\r\n");
                Assert.IsFalse(SwToolsExport.DeliveryLost(SwToolsExport.ReadResult(path)), "успех — не потеря пакета");
                Assert.IsFalse(SwToolsExport.DeliveryLost(null), "нет итога — не потеря пакета");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    public static class LzkWorkbookTests
    {
        private static string Template()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.GetFullPath(Path.Combine(dir, "..", "data", "Ведомость_ЛЗК.xlsx"));
            if (!File.Exists(path)) throw new FileNotFoundException("Нет шаблона ЛЗК для теста", path);
            return path;
        }

        private static string CopyTemplate()
        {
            string path = Path.Combine(Path.GetTempPath(), "eskd_lzk_" + Guid.NewGuid().ToString("N") + ".xlsx");
            File.Copy(Template(), path);
            return path;
        }

        public static void Test_Xlsx_resolves_names_and_round_trips_cells()
        {
            string path = CopyTemplate();
            try
            {
                XlsxBook book = XlsxBook.Open(path);
                string sheet, cell;
                Assert.IsTrue(book.TryResolveName("Путь", out sheet, out cell), "имя Путь");
                Assert.AreEqual("Ведомость", sheet, "лист");
                Assert.AreEqual("J6", cell, "ячейка");
                Assert.IsFalse(book.TryResolveName("Нет_такого", out sheet, out cell), "нет имени");
                XlsxSheet main = book.Sheet("Ведомость");
                Assert.AreEqual("Операции", main.Get("I6"), "заголовок");
                main.SetText("C8", "Текст <&> «кавычки»");
                main.SetNumber("G8", 1.25);
                XlsxSheet extra = book.AddSheet("Покраска");
                int style = book.AddStyle(true, true, true);
                extra.SetText("A1", "x", style);
                Assert.AreEqual(style, book.AddStyle(true, true, true), "тот же стиль — прежний индекс, без новых записей");
                Assert.IsTrue(book.AddStyle(true, true, false) != style, "другой стиль — новый индекс");
                book.Save();

                XlsxBook again = XlsxBook.Open(path);
                Assert.AreEqual("Текст <&> «кавычки»", again.Sheet("Ведомость").Get("C8"), "текст");
                Assert.AreEqual("1.25", again.Sheet("Ведомость").Get(7, 8), "число");
                Assert.AreEqual(2, again.SheetNames.Count, "листов");
                Assert.AreEqual("x", again.Sheet("Покраска").Get("A1"), "новый лист");
                Assert.IsFalse(File.Exists(path + ".part"), "временный файл убран");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void Row(XlsxSheet s, int row, string number, string designation, string name, string material,
            string size, string mass, string qty, string ops, string path)
        {
            string[] values = { number, "", designation, name, material, size, mass, qty, ops, path };
            for (int c = 0; c < values.Length; c++)
                if (values[c].Length > 0) s.SetText(XlsxBook.CellName(c + 1, row), values[c]);
        }

        public static void Test_Complete_marks_gaps_and_adds_sheets()
        {
            string path = CopyTemplate();
            try
            {
                XlsxBook export = XlsxBook.Open(path);
                XlsxSheet s = export.Sheet("Ведомость");
                Row(s, 7, "1", "И.100", "Кондуктор", "", "", "12,5", "1", "Сварная сборка; Покраска", @"D:\И\01_3D\И.100.sldasm");
                Row(s, 8, "2", "И.101", "Пластина", "Лист 3 ГОСТ 19903", "", "", "2", "", @"D:\И\01_3D\И.101.sldprt");
                Row(s, 9, "3", "", "Болт М8", "", "10×10×30", "0,01", "4", "Сборка", @"D:\Lib\Болт.sldprt");
                export.Save();

                List<LzkItem> items = new List<LzkItem>
                {
                    new LzkItem { Path = @"D:\И\01_3D\И.100.sldasm", Designation = "И.100", Name = "Кондуктор", IsAssembly = true,
                        InProduct = true, Quantity = 1, Operations = "Сварная сборка; Покраска", AreaM2 = 2.5, Size = "500×400×300" },
                    new LzkItem { Path = @"D:\И\01_3D\И.101.sldprt", Designation = "И.101", Name = "Пластина", InProduct = true,
                        Quantity = 2, Size = "200×100×3", InsidePaintedUnit = true, Operations = "Покраска" },
                    new LzkItem { Path = @"D:\И\01_3D\И.102.sldprt", Designation = "И.102", Name = "Труба", InProduct = true,
                        Quantity = 1, IsProfile = true, SizeIsEstimate = true, Size = "600×40×40" },
                    new LzkItem { Path = @"D:\Lib\Болт.sldprt", Name = "Болт М8", IsPurchased = true, Quantity = 4, Code = "" },
                    new LzkItem { Path = @"D:\Lib\Болт2.sldprt", Name = "Болт М8", IsPurchased = true, Quantity = 2, Code = "" },
                    new LzkItem { Path = @"D:\Lib\Шайба.sldprt", Name = "Шайба 8", IsPurchased = true, Quantity = 8,
                        Code = "4180-001", Unit = "796 шт" }
                };
                LzkHeader header = new LzkHeader { Product = "И Кондуктор", Author = "Иванов", Model = "И.100.sldasm", Date = "17.09.2026", Checksum = "SHA-256 ab" };
                LzkResult r = LzkWorkbook.Complete(path, header, items);

                Assert.AreEqual(0, r.Errors.Count, string.Join("; ", r.Errors.ToArray()));
                Assert.AreEqual(3, r.Rows, "строк");
                Assert.AreEqual(1, r.PaintRows, "покраска: деталь внутри окрашиваемого узла не выводится");
                Assert.AreEqual(2, r.PurchasedRows, "покупные сгруппированы");
                string issues = string.Join("\n", r.Issues.ToArray());
                Assert.IsTrue(issues.Contains("Строка 2 (И.101): не заполнена «Масса»"), issues);
                Assert.IsFalse(issues.Contains("Строка 2 (И.101): не заполнено «Операции»"), "операции подставлены из модели: " + issues);
                Assert.IsTrue(issues.Contains("Строка 3 (Болт М8): покупное"), issues);
                Assert.IsTrue(issues.Contains("нет изготавливаемых моделей (1): И.102"), issues);
                Assert.IsFalse(issues.Contains("Строка 1 (И.100): не заполнено «Материал»"), "у сборки материал не требуется");

                XlsxBook book = XlsxBook.Open(path);
                XlsxSheet main = book.Sheet("Ведомость");
                Assert.AreEqual("500×400×300", main.Get("F7"), "габарит дописан из модели");
                Assert.AreEqual("?", main.Get("G8"), "масса");
                Assert.AreEqual("Покраска", main.Get("I8"), "операции из модели в пустую ячейку");
                Assert.AreEqual("200×100×3", main.Get("F8"), "габарит детали");
                Assert.AreEqual("И Кондуктор", main.Get("B2"), "шапка изделие");
                Assert.AreEqual("Иванов", main.Get("G2"), "шапка составил");
                Assert.AreEqual(r.Issues.Count + " (пометки «?» в таблице)", main.Get("G4"), "шапка замечания");

                XlsxSheet paint = book.Sheet("Покрасочный");
                Assert.AreEqual("И.100", paint.Get("B6"), "окрашиваемый узел");
                Assert.AreEqual("2.5", paint.Get("D6"), "площадь");
                Assert.AreEqual("E6*Тираж", paint.Formula("F6"), "всего на заказ — формулой от тиража");
                Assert.AreEqual("Итого", paint.Get("D7"), "итог");
                XlsxSheet kit = book.Sheet("Комплектовочный");
                Assert.AreEqual("Покупные и стандартные изделия, материалы", kit.Get("A6"), "раздел покупных");
                Assert.AreEqual("Болт М8", kit.Get("C7"), "по наименованию");
                Assert.AreEqual("6", kit.Get("F7"), "количество сложено");
                Assert.AreEqual("", kit.Get("D7"), "кода 1С нет — ячейка пустая, а не «?»");
                Assert.AreEqual("шт", kit.Get("E7"), "единица по умолчанию");
                Assert.AreEqual("4180-001", kit.Get("D8"), "код 1С из модели");
                Assert.AreEqual("F7*Тираж", kit.Formula("G7"), "всего на заказ");

                Assert.AreEqual(3, r.Rows, "строк");
                Assert.AreEqual(1, r.PaintRows, "покраска");
                Assert.AreEqual(2, r.PurchasedRows, "покупные");
                Assert.AreEqual(r.Issues.Count, Notices.Count(Notices.FromLzk(r), NoticeLevel.Warning), "пометки «?» — замечания окна");
            }
            finally
            {
                File.Delete(path);
            }
        }

        public static void Test_Suggest_paints_rolled_metal_and_welded_units()
        {
            string[] sheet = LzkOperations.Suggest(new ModelTraits
            { IsSheetMetal = true, HasBends = true, Material = "Лист 3,0 ГОСТ 19903-2015 / Ст3сп" }).ToArray();
            Assert.AreEqual("Лазерная резка листа; Гибка; Покраска", LzkOperations.Join(sheet), "лист красится");

            string[] tube = LzkOperations.Suggest(new ModelTraits
            { IsStructuralMember = true, Material = "Труба 40х20х1,5 ГОСТ 8645-68" }).ToArray();
            Assert.AreEqual("Лазерная резка трубы; Покраска", LzkOperations.Join(tube), "профиль красится");

            string[] plastic = LzkOperations.Suggest(new ModelTraits { Material = "Пластик слоистый HPL-1,2 ГОСТ 9590-76" }).ToArray();
            Assert.AreEqual("", LzkOperations.Join(plastic), "пластик не красится");

            string[] board = LzkOperations.Suggest(new ModelTraits
            { IsSheetMetal = false, Material = "Плита ЛДСП-16 ГОСТ 32289-2013" }).ToArray();
            Assert.AreEqual("", LzkOperations.Join(board), "ЛДСП не красится");

            string[] plywood = LzkOperations.Suggest(new ModelTraits { Material = "Фанера ФК-II/III-E1-Ш2-12 ГОСТ 3916.1-96" }).ToArray();
            Assert.AreEqual("", LzkOperations.Join(plywood), "фанера не красится");

            string[] purchased = LzkOperations.Suggest(new ModelTraits { IsPurchased = true, Material = "Лист 3,0" }).ToArray();
            Assert.AreEqual("", LzkOperations.Join(purchased), "покупное не красится");

            string[] welded = LzkOperations.Suggest(new ModelTraits { IsAssembly = true, HasWeldBeads = true }).ToArray();
            Assert.AreEqual("Сварная сборка; Покраска", LzkOperations.Join(welded), "сварной узел красится целиком");

            string[] mechanical = LzkOperations.Suggest(new ModelTraits { IsAssembly = true }).ToArray();
            Assert.AreEqual("Механическая сборка", LzkOperations.Join(mechanical), "механическая сборка не красится");

            string[] unknown = LzkOperations.Suggest(new ModelTraits { Material = "Композит XYZ" }).ToArray();
            Assert.AreEqual("", LzkOperations.Join(unknown), "незнакомый материал — решает конструктор");

            // Деталь смоделирована телом: признаков листового металла и сварной конструкции нет, раскрой — по сортаменту.
            string[] body = LzkOperations.Suggest(new ModelTraits { Material = "Труба 40х20х1,5 ГОСТ 8645-68 / 08пс" }).ToArray();
            Assert.AreEqual("Лазерная резка трубы; Покраска", LzkOperations.Join(body), "труба без признаков модели");
            string[] plate = LzkOperations.Suggest(new ModelTraits { Material = "Лист Б-ПН-НО-3,0 ГОСТ 19903-2015" }).ToArray();
            Assert.AreEqual("Лазерная резка листа; Покраска", LzkOperations.Join(plate), "лист без признаков модели");
            string[] round = LzkOperations.Suggest(new ModelTraits { Material = "Круг 20 ГОСТ 2590-2006" }).ToArray();
            Assert.AreEqual("Покраска", LzkOperations.Join(round), "круг лазером не режут — только покраска");

            // Заказ, ещё не оформленный по ЕСКД: «Материал_Строка» пуст, но материал модели задан — решает плотность.
            string[] steel = LzkOperations.Suggest(new ModelTraits { IsSheetMetal = true, DensityKgM3 = 7850 }).ToArray();
            Assert.AreEqual("Лазерная резка листа; Покраска", LzkOperations.Join(steel), "сталь по плотности");
            string[] aluminium = LzkOperations.Suggest(new ModelTraits { DensityKgM3 = 2700 }).ToArray();
            Assert.AreEqual("Покраска", LzkOperations.Join(aluminium), "алюминий по плотности");
            string[] plasticByDensity = LzkOperations.Suggest(new ModelTraits { DensityKgM3 = 1200 }).ToArray();
            Assert.AreEqual("", LzkOperations.Join(plasticByDensity), "пластик по плотности не красится");
            string[] boardByDensity = LzkOperations.Suggest(new ModelTraits { DensityKgM3 = 800 }).ToArray();
            Assert.AreEqual("", LzkOperations.Join(boardByDensity), "древесная плита по плотности не красится");
            string[] nameWins = LzkOperations.Suggest(new ModelTraits { Material = "Плита ЛДСП-16 ГОСТ 32289-2013", DensityKgM3 = 7850 }).ToArray();
            Assert.AreEqual("", LzkOperations.Join(nameWins), "сортамент важнее плотности");
        }

        public static void Test_Material_kind_reads_sortament()
        {
            Assert.AreEqual(MaterialKind.RolledMetal, LzkMaterials.Kind("Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024"), "лист");
            Assert.AreEqual(MaterialKind.RolledMetal, LzkMaterials.Kind("труба 60х60х2,0 ГОСТ 8639-82"), "регистр");
            Assert.AreEqual(MaterialKind.RolledMetal, LzkMaterials.Kind("Лист АМг2.М 1,5 ГОСТ 21631-76"), "алюминиевый прокат");
            Assert.AreEqual(MaterialKind.NonMetal, LzkMaterials.Kind("Кромка ПВХ 2,0х22"), "кромка");
            Assert.AreEqual(MaterialKind.NonMetal, LzkMaterials.Kind("Полиэтилен 20908-040 ГОСТ 16338-85"), "полиэтилен");
            Assert.AreEqual(MaterialKind.Unknown, LzkMaterials.Kind(""), "пусто");
            Assert.AreEqual(MaterialKind.Unknown, LzkMaterials.Kind("Листогиб"), "слово целиком");
        }

        public static void Test_Unit_splits_okei_code()
        {
            string unit, okei;
            LzkWorkbook.Unit("796 ШТ", out unit, out okei);
            Assert.AreEqual("шт", unit, "единица");
            Assert.AreEqual("796", okei, "код ОКЕИ");
            LzkWorkbook.Unit("", out unit, out okei);
            Assert.AreEqual("шт", unit, "по умолчанию штуки");
            Assert.AreEqual("796", okei, "код по умолчанию");
            LzkWorkbook.Unit("м", out unit, out okei);
            Assert.AreEqual("м", unit, "метры");
            Assert.AreEqual("", okei, "код неизвестен");
        }

        public static void Test_Sheets_are_typeset_and_fonts_are_cyrillic()
        {
            string path = CopyTemplate();
            try
            {
                XlsxBook export = XlsxBook.Open(path);
                Row(export.Sheet("Ведомость"), 7, "1", "И.100", "Рама", "", "", "12,5", "1", "Сварная сборка; Покраска",
                    @"D:\И\01_3D\И.100.sldasm");
                export.Save();
                List<LzkItem> items = new List<LzkItem>
                {
                    new LzkItem { Path = @"D:\И\01_3D\И.100.sldasm", Designation = "И.100", Name = "Рама", IsAssembly = true,
                        IsTop = true, InProduct = true, Quantity = 1, Operations = "Сварная сборка; Покраска", AreaM2 = 2.5 },
                    new LzkItem { Path = @"D:\Lib\Саморез.sldprt", Name = "Саморез 4,2x16", IsPurchased = true, Quantity = 12 }
                };
                LzkWorkbook.Complete(path, new LzkHeader { Product = "И.100 Рама", Date = "17.09.2026 19:30" }, items);

                string styles = Part(path, "xl/styles.xml");
                Assert.IsFalse(styles.Contains("宋体"), "китайского шрифта в книге нет");
                Assert.IsTrue(styles.Contains("val=\"Arial\""), "шрифт книги — Arial");
                Assert.IsTrue(styles.Contains("charset val=\"204\"") || styles.Contains("<charset val=\"204\"/>"), "кириллическая кодировка");

                foreach (string name in new[] { "Покрасочный участок", "Комплектовочный участок", "Расход материалов на заказ" })
                {
                    string xml = SheetXml(path, name);
                    Assert.IsTrue(xml.Contains("<mergeCell"), name + ": заголовок объединён");
                    Assert.IsTrue(xml.Contains("state=\"frozen\""), name + ": шапка закреплена");
                    Assert.IsTrue(xml.Contains("fitToWidth=\"1\""), name + ": печать по ширине страницы");
                    Assert.IsTrue(xml.Contains("customWidth=\"1\""), name + ": ширины колонок заданы");
                    Assert.IsTrue(xml.Contains("customHeight=\"1\""), name + ": высоты строк заданы");
                }
                string workbook = Part(path, "xl/workbook.xml");
                Assert.IsTrue(workbook.Contains("_xlnm.Print_Titles"), "шапка повторяется на каждой странице");
                Assert.IsTrue(workbook.Contains("_xlnm.Print_Area"), "область печати");
                Assert.IsTrue(workbook.Contains("fullCalcOnLoad=\"1\""), "пересчёт при открытии");

                XlsxBook book = XlsxBook.Open(path);
                Assert.AreEqual("Паспорт|Ведомость|Сводная|Заготовительный|Сварочный|Покрасочный|Комплектовочный|Расход|Нормы",
                    string.Join("|", book.SheetNames), "листы книги");
                XlsxSheet paint = book.Sheet("Покрасочный");
                Assert.AreEqual("Покрасочный участок", paint.Get("A1"), "название листа");
                Assert.IsTrue(paint.Get("A2").Contains("И.100 Рама"), "подзаголовок с изделием: " + paint.Get("A2"));
                Assert.AreEqual("Тираж", paint.Formula("C3"), "тираж в шапке — из паспорта");
                Assert.AreEqual("№", paint.Get("A5"), "шапка таблицы в пятой строке");
                Assert.AreEqual("И.100", paint.Get("B6"), "главная сборка красится целиком");
                XlsxSheet welding = book.Sheet("Сварочный");
                Assert.AreEqual("И.100", welding.Get("B6"), "главная сборка сваривается");
                XlsxSheet kit = book.Sheet("Комплектовочный");
                Assert.AreEqual("Саморез 4,2x16", kit.Get("C7"), "покупное");
                Assert.AreEqual("", kit.Get("D7"), "код 1С пустой");
                XlsxSheet blank = book.Sheet("Заготовительный");
                Assert.AreEqual("Единиц для этого участка в изделии нет", blank.Get("A6"), "пустой участок");
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>Содержимое части xlsx как текста.</summary>
        private static string Part(string path, string part)
        {
            using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(path))
            {
                System.IO.Compression.ZipArchiveEntry entry = zip.GetEntry(part);
                if (entry == null) throw new FileNotFoundException("Нет части " + part + " в " + path);
                using (StreamReader reader = new StreamReader(entry.Open()))
                    return reader.ReadToEnd();
            }
        }

        /// <summary>XML листа, на котором встречается образец текста.</summary>
        private static string SheetXml(string path, string marker)
        {
            using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(path))
                foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith("xl/worksheets/sheet")) continue;
                    string xml;
                    using (StreamReader reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
                    if (xml.Contains(marker)) return xml;
                }
            throw new InvalidOperationException("Нет листа с текстом «" + marker + "» в " + path);
        }

        public static void Test_Complete_takes_attributes_from_models()
        {
            string path = CopyTemplate();
            try
            {
                XlsxBook export = XlsxBook.Open(path);
                XlsxSheet s = export.Sheet("Ведомость");
                Row(s, 7, "1", "И.201 Пластина", "", "", "", "0,5", "1", "", @"D:\И\01_3D\И.201 Пластина.SLDPRT");
                Row(s, 8, "2", "", "", "Лист 2", "", "0,5", "1", "Гибка", @"D:\И\01_3D\И.202.sldprt");
                export.Save();
                List<LzkItem> items = new List<LzkItem>
                {
                    new LzkItem { Path = @"D:\И\01_3D\И.200 СБ Узел.sldasm", Designation = "И.200", Name = "Узел", IsAssembly = true,
                        IsTop = true, InProduct = true, Quantity = 1 },
                    new LzkItem { Path = @"D:\И\01_3D\И.201 Пластина.sldprt", Designation = "И.201", Name = "Пластина", InProduct = true,
                        Quantity = 1, Material = "Лист 3 ГОСТ 19903", Operations = "Лазерная резка листа", Size = "10×10×3" },
                    new LzkItem { Path = @"D:\И\01_3D\И.202.sldprt", Designation = "И.202", Name = "Скоба", InProduct = true,
                        Quantity = 1, Material = "Лист 4", Operations = "Лазерная резка листа", Size = "20×10×4" }
                };
                LzkResult r = LzkWorkbook.Complete(path, null, items);
                XlsxSheet main = XlsxBook.Open(path).Sheet("Ведомость");
                Assert.AreEqual("И.201", main.Get("C7"), "обозначение из свойства, путь без учёта регистра");
                Assert.AreEqual("Пластина", main.Get("D7"), "наименование из свойства");
                Assert.AreEqual("Лист 3 ГОСТ 19903", main.Get("E7"), "материал в пустую ячейку");
                Assert.AreEqual("Лазерная резка листа", main.Get("I7"), "операции в пустую ячейку");
                Assert.AreEqual("Лист 2", main.Get("E8"), "материал из выгрузки не заменяется");
                Assert.AreEqual("Гибка", main.Get("I8"), "операции из выгрузки не заменяются");
                string issues = string.Join("\n", r.Issues.ToArray());
                Assert.IsFalse(issues.Contains("нет изготавливаемых"), "главная сборка не считается пропущенной: " + issues);
                Assert.IsFalse(issues.Contains("Материал"), issues);
                Assert.IsTrue(issues.Contains("Строка 1 (И.201)") || r.Issues.Count == 0, "подпись строки — по обозначению из модели: " + issues);
            }
            finally
            {
                File.Delete(path);
            }
        }

        public static void Test_Complete_rejects_foreign_template()
        {
            string path = CopyTemplate();
            try
            {
                LzkResult r = LzkWorkbook.Complete(path, null, new List<LzkItem>());
                Assert.AreEqual(0, r.Errors.Count, "шаблон ЛЗК принят");
                Assert.AreEqual("нет", XlsxBook.Open(path).Sheet("Ведомость").Get("G4"), "замечаний нет");
                LzkResult twice = null;
                try
                {
                    twice = LzkWorkbook.Complete(path, null, new List<LzkItem>());
                }
                catch (InvalidOperationException)
                {
                }
                Assert.IsNull(twice, "повторное дописывание той же книги отвергается (листы уже есть)");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
