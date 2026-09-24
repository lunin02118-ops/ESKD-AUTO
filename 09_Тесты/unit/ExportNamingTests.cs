using System;
using System.Collections.Generic;
using System.IO;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Имена и места файлов выдачи для производства (ТЗ-02 Т-27…Т-31).</summary>
    public static class ExportNamingTests
    {
        /// <summary>№42, решение владельца 24.09.2026: DXF на каждое листовое тело — «_тело2» перед толщиной.</summary>
        public static void Test_Dxf_of_one_body_of_multibody_part()
        {
            string path = ExportNaming.DxfPath(@"Z:\И01", "ПРТИ.301111.067", "Лист двойной", @"Z:\И01\01_3D\x.sldprt", 2, 8, 1, 100, 200, 0);
            Assert.AreEqual("ПРТИ.301111.067 Лист двойной_тело2_S8мм_1шт_100х200.dxf", System.IO.Path.GetFileName(path), "имя DXF тела");
            string single = ExportNaming.DxfPath(@"Z:\И01", "ПРТИ.301111.067", "Лист двойной", @"Z:\И01\01_3D\x.sldprt", 3, 1, 100, 200, 0);
            Assert.AreEqual("ПРТИ.301111.067 Лист двойной_S3мм_1шт_100х200.dxf", System.IO.Path.GetFileName(single), "однотельная — как раньше");
            Assert.IsTrue(ExportNaming.BelongsTo("ПРТИ.301111.067 Лист двойной_тело2_S8мм_1шт_100х200.dxf", "ПРТИ.301111.067 Лист двойной"),
                "файл тела — выдача этого документа: ревизия унесёт его в архив");
            Assert.AreEqual(2, ExportNaming.BodyOf("ПРТИ.301111.067 Лист двойной_тело2_S8мм_1шт_100х200.dxf", "ПРТИ.301111.067 Лист двойной"), "номер тела");
            Assert.AreEqual(0, ExportNaming.BodyOf("ПРТИ.301111.067 Лист двойной_S8мм_100х200.dxf", "ПРТИ.301111.067 Лист двойной"), "без номера");
        }

        private const string Product = @"\\Synology_TR\Конструкторский отдел\_Заявки\2026-014 Школа\02_Металл\И01_ТС-52_Стол";
        private const string Model = @"D:\И\01_3D\ТС-52.00.01.004 Заглушка.sldprt";

        public static void Test_Folders_follow_regulation()
        {
            Assert.AreEqual(Product + @"\02_PDF", ExportNaming.PdfDirectory(Product), "PDF чертежей");
            Assert.AreEqual(Product + @"\03_ЧПУ\Лазер_Лист", ExportNaming.LaserDirectory(Product), "развёртки лазера");
            Assert.AreEqual(Product + @"\03_ЧПУ\Труборез", ExportNaming.TubeDirectory(Product), "профиль трубореза");
            Assert.AreEqual(Product + @"\_Экспорт.txt", ExportNaming.ReportPath(Product), "отчёт выгрузки");
        }

        public static void Test_Names_are_designation_and_title()
        {
            Assert.AreEqual(Product + @"\02_PDF\ТС-52.00.01.004 Заглушка.pdf",
                ExportNaming.PdfPath(Product, "ТС-52.00.01.004", "Заглушка", Model, 0), "PDF без ревизии");
            Assert.AreEqual(Product + @"\02_PDF\ТС-52.00.01.004 Заглушка_Изм2.pdf",
                ExportNaming.PdfPath(Product, "ТС-52.00.01.004", "Заглушка", Model, 2), "PDF второй ревизии");
            // Реквизитов ещё нет — имя берётся от файла модели, а не остаётся пустым.
            Assert.AreEqual(Product + @"\02_PDF\ТС-52.00.01.004 Заглушка.pdf",
                ExportNaming.PdfPath(Product, "", "", Model, 0), "PDF по имени файла");
            Assert.AreEqual(Product + @"\03_ЧПУ\Труборез\ТС-52.00.01.002 Стойка.igs",
                ExportNaming.IgsPath(Product, "ТС-52.00.01.002", "Стойка", Model, 0), "IGS профиля");
        }

        public static void Test_Bch_record_gives_only_its_title()
        {
            // З-9: у детали БЧ «Наименование» — запись для спецификации; в имя файла она не попадает.
            string record = "Распорка\n<STACK size=1>Труба ПО 30х15х1,5 ГОСТ 8644-68<OVER>08пс ГОСТ 13663-86</STACK>\nL = 369 мм";
            Assert.AreEqual(Product + @"\03_ЧПУ\Труборез\NC3-7R.03.001 Распорка.igs",
                ExportNaming.IgsPath(Product, "NC3-7R.03.001", record, Model, 0), "IGS детали БЧ");
        }

        public static void Test_Dxf_carries_thickness_and_frame()
        {
            // Образец технолога записан как «_S2.0мм», но Т-28 требует целую толщину без дробной части:
            // иначе у одного и того же листа появятся два имени — «S2мм» и «S2.0мм».
            Assert.AreEqual(Product + @"\03_ЧПУ\Лазер_Лист\85T.СМ.01.004 Заглушка_S2мм_23х48.dxf",
                ExportNaming.DxfPath(Product, "85T.СМ.01.004", "Заглушка", Model, 2.0, 0, 22.6, 47.5, 0), "развёртка 2 мм, количество не известно");
            Assert.AreEqual(Product + @"\03_ЧПУ\Лазер_Лист\85T.СМ.01.004 Заглушка_S2.5мм_23х48_Изм1.dxf",
                ExportNaming.DxfPath(Product, "85T.СМ.01.004", "Заглушка", Model, 2.5, 0, 22.6, 47.5, 1), "дробная толщина и ревизия");
            Assert.AreEqual(Product + @"\03_ЧПУ\Лазер_Лист\778.КРВ.00.005-02 Кронштейн угловой левый_S3мм_2шт_120х80.dxf",
                ExportNaming.DxfPath(Product, "778.КРВ.00.005-02", "Кронштейн угловой левый", Model, 3.0, 2, 120, 80, 0),
                "исполнение и количество на изделие");
            Assert.AreEqual("3", ExportNaming.Thickness(3.0), "целая толщина без дробной части");
            Assert.AreEqual("1.5", ExportNaming.Thickness(1.5), "дробная толщина через точку");
            Assert.AreEqual("0.8", ExportNaming.Thickness(0.8), "тонкий лист");
            Assert.AreEqual("48", ExportNaming.Round(47.5), "сторона рамки вверх");
            Assert.AreEqual("0", ExportNaming.Round(-1), "отрицательной стороны не бывает");
        }

        public static void Test_Report_lists_files_and_skips()
        {
            ExportLog log = new ExportLog
            {
                Product = "ТС-52",
                User = "Лунин В.И.",
                Time = new DateTime(2026, 9, 17, 22, 10, 0)
            };
            string pdf = Path.Combine(Product, "02_PDF", "ТС-52.00.01.004 Заглушка.pdf");
            log.Add(pdf);
            log.Checksums[pdf] = "ab12";
            log.Skip("ТС-52.00.01.007 Кронштейн.sldprt", "нет чертежа: PDF не сделан");

            string text = log.Text();
            Assert.IsTrue(text.Contains("Изделие:  ТС-52"), text);
            Assert.IsTrue(text.Contains("Выгрузил: Лунин В.И., 17.09.2026 22:10"), text);
            Assert.IsTrue(text.Contains("Файлов:   1, пропущено: 1"), text);
            Assert.IsTrue(text.Contains("  ab12  ТС-52.00.01.004 Заглушка.pdf"), "файл с контрольной суммой: " + text);
            Assert.IsTrue(text.Contains("нет чертежа: PDF не сделан"), "причина пропуска: " + text);

            string empty = new ExportLog { Product = "ТС-52", User = "И" }.Text();
            Assert.IsTrue(empty.Contains("Ничего не выгружено."), empty);
        }

        public static void Test_Report_is_parsed_back_by_blocks()
        {
            // Проверка изделия (Т-32е) читает отчёт по блокам: имя детали в «Пропущено» не делает её выгруженной.
            string sum = new string('a', 64);
            ExportLog log = new ExportLog { Product = "ТС-52", User = "И" };
            string pdf = Path.Combine(Product, "02_PDF", "ТС-52.00.01.004 Заглушка.pdf");
            log.Add(pdf);
            log.Checksums[pdf] = sum;
            log.Skip("ТС-52.00.01.005 Уголок.slddrw", "PDF не сохранён (код 2)");
            log.Skip("ТС-52.00.01.007 Кронштейн.sldprt", "нет чертежа: PDF не сделан");
            log.Warn("ТС-52.00.02.001 Труба.sldprt", "IGS не по оси трубы");

            ExportLog back = ExportLog.Parse(log.Text());
            Assert.AreEqual(1, back.Files.Count, "один выгруженный файл");
            Assert.AreEqual("ТС-52.00.01.004 Заглушка.pdf", back.Files[0], "имя файла");
            Assert.AreEqual(sum, back.Checksums["ТС-52.00.01.004 Заглушка.pdf"], "сумма");
            Assert.AreEqual(2, back.Skipped.Count, "два пропуска");
            Assert.AreEqual(1, back.Warnings.Count, "замечание");
            KeyValuePair<string, string> skip = ExportLog.SplitSkip(back.Skipped[0]);
            Assert.AreEqual("ТС-52.00.01.005 Уголок.slddrw", skip.Key, "документ пропуска");
            Assert.AreEqual("PDF не сохранён (код 2)", skip.Value, "причина");
            Assert.IsFalse(ExportLog.IsBenignSkip(skip.Value), "несохранённый PDF — несделанная выгрузка");
            Assert.IsTrue(ExportLog.IsBenignSkip(ExportLog.SplitSkip(back.Skipped[1]).Value), "у детали без чертежа PDF нет по делу");
            Assert.IsTrue(ExportLog.IsBenignSkip("документ выдан в производство, оформите новую ревизию"), "выданное не переписывается");
            // Подпись исполнения: имя конфигурации с «<», «>» и точкой (ревью 23.09.2026).
            Assert.AreEqual("Труба", ExportLog.DocumentName("Труба.sldprt [00<Как обработано>]"), "угловые скобки");
            Assert.AreEqual("Труба", ExportLog.DocumentName("Труба.SLDPRT [1.5]"), "точка в имени исполнения");
            Assert.AreEqual("ПРТИ.468211.161 Лист", ExportLog.DocumentName("ПРТИ.468211.161 Лист.sldprt"), "без исполнения");
            Assert.AreEqual("ПРТИ.468211.100 СБ Опора", ExportLog.DocumentName("ПРТИ.468211.100 СБ Опора.sldasm"), "сборка");
            Assert.AreEqual("ПРТИ.468211.221-01 Стойка", ExportLog.DocumentName("ПРТИ.468211.221-01 Стойка.igs"), "файл выдачи");

            ExportLog legacy = ExportLog.Parse("Выгружено (SHA-256):\r\n  --------  А.pdf\r\n");
            Assert.AreEqual(1, legacy.Files.Count, "строка без суммы — файл есть");
            Assert.IsFalse(legacy.Checksums.ContainsKey("А.pdf"), "без суммы нечего сверять");
        }

        public static void Test_Issued_documents_are_read_from_reports()
        {
            string folder = Path.Combine(Path.GetTempPath(), "eskd_issued_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string[] lines =
                {
                    "Выдано в производство", "",
                    "ab12cd  ТС-52.00.01.004 Заглушка.sldprt",
                    "ef34  ТС-52.00.00.000 Стол.sldasm"
                };
                File.WriteAllLines(Path.Combine(folder, "_Выдано_2026-09-10.txt"), lines, System.Text.Encoding.UTF8);
                System.Collections.Generic.HashSet<string> issued = ExportNaming.Issued(folder);
                Assert.IsTrue(issued.Contains("ТС-52.00.01.004 Заглушка.sldprt"), "деталь выдана");
                Assert.IsTrue(issued.Contains("ТС-52.00.00.000 Стол.sldasm"), "сборка выдана");
                Assert.IsFalse(issued.Contains("Выдано в производство"), "заголовок отчёта — не документ");
                Assert.AreEqual(0, ExportNaming.Issued(Path.Combine(folder, "нет")).Count, "папки нет — ничего не выдано");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        public static void Test_Dxf_frame_is_measured_from_file()
        {
            // Кусок DXF: рамка снимается по координатам линий тела чертежа, служебные точки заголовка не в счёт.
            string[] dxf =
            {
                "0", "SECTION", "2", "HEADER", "9", "$EXTMIN", "10", "-999.0", "20", "-999.0", "0", "ENDSEC",
                "0", "SECTION", "2", "ENTITIES",
                "0", "LINE", "10", "0.0", "20", "0.0", "11", "48.0", "21", "0.0",
                "0", "LINE", "10", "48.0", "20", "0.0", "11", "48.0", "21", "22.5",
                "0", "ENDSEC", "0", "EOF"
            };
            double width, length;
            using (StringReader reader = new StringReader(string.Join(Environment.NewLine, dxf)))
                Assert.IsTrue(DxfFrame.Measure(reader, out width, out length), "рамка измерена");
            Assert.AreEqual("23", ExportNaming.Round(width), "ширина — меньшая сторона");
            Assert.AreEqual("48", ExportNaming.Round(length), "длина — большая сторона");

            double w2, l2;
            string[] blank = { "0", "SECTION", "2", "ENTITIES", "0", "ENDSEC" };
            using (StringReader empty = new StringReader(string.Join(Environment.NewLine, blank)))
                Assert.IsFalse(DxfFrame.Measure(empty, out w2, out l2), "пустая развёртка — не рамка");
        }

        /// <summary>Рамка тела чертежа «ширинахдлина» в целых мм, как в имени DXF; пусто — рамки нет.</summary>
        private static string Frame(string[] header, params string[] entities)
        {
            List<string> dxf = new List<string>();
            if (header.Length > 0)
            {
                dxf.AddRange(new[] { "0", "SECTION", "2", "HEADER" });
                dxf.AddRange(header);
                dxf.AddRange(new[] { "0", "ENDSEC" });
            }
            dxf.AddRange(new[] { "0", "SECTION", "2", "ENTITIES" });
            dxf.AddRange(entities);
            dxf.AddRange(new[] { "0", "ENDSEC", "0", "EOF" });
            double width, length;
            using (StringReader reader = new StringReader(string.Join(Environment.NewLine, dxf.ToArray())))
                if (!DxfFrame.Measure(reader, out width, out length)) return "";
            Assert.IsTrue(Math.Abs(width - Math.Round(width)) < 0.005 && Math.Abs(length - Math.Round(length)) < 0.005,
                "рамка точная, а не приближённая: " + width + " x " + length);
            return ExportNaming.Round(width) + "х" + ExportNaming.Round(length);
        }

        private static string[] Line(double x1, double y1, double x2, double y2)
        {
            return new[] { "0", "LINE", "8", "0", "10", N(x1), "20", N(y1), "30", "0.0", "11", N(x2), "21", N(y2), "31", "0.0" };
        }

        private static string N(double value)
        {
            return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string[] Join(params string[][] parts)
        {
            List<string> all = new List<string>();
            foreach (string[] part in parts) all.AddRange(part);
            return all.ToArray();
        }

        public static void Test_Dxf_frame_takes_arcs_and_circles_by_their_outline()
        {
            // Круглая деталь: одна окружность. Центр — не рамка: раньше выходило «развёртка пустая», и DXF не делался.
            Assert.AreEqual("150х150", Frame(new string[0], "0", "CIRCLE", "10", "500.0", "20", "300.0", "30", "0.0", "40", "75.0"),
                "диск Ø150 далеко от начала координат");
            // Планка с полукруглыми торцами: радиусы — часть длины (было 80х200 вместо 80х280, проверено на SolidWorks 2025).
            Assert.AreEqual("80х280", Frame(new string[0], Join(Line(-100, -40, 100, -40), Line(100, 40, -100, 40),
                new[] { "0", "ARC", "10", "100.0", "20", "0.0", "40", "40.0", "50", "270.0", "51", "90.0" },
                new[] { "0", "ARC", "10", "-100.0", "20", "0.0", "40", "40.0", "50", "90.0", "51", "270.0" })), "планка R40");
            // SolidWorks пишет часть дуг с нормалью (0, 0, −1): их X в своей системе координат — зеркальный. Торец с одной
            // стороны: без зеркала дуга легла бы с другого конца, и вышло бы 80х200 (ревью 23.09.2026 — симметричная
            // планка этого не ловила).
            Assert.AreEqual("40х80", Frame(new string[0], Join(Line(-100, -40, -100, 40),
                new[] { "0", "ARC", "10", "100.0", "20", "0.0", "40", "40.0", "210", "0.0", "220", "0.0", "230", "-1.0", "50", "90.0", "51", "270.0" })),
                "дуга с зеркальной системой координат");
            // Полный оборот с округлением записи — окружность, а не точка.
            Assert.AreEqual("150х150", Frame(new string[0], "0", "ARC", "10", "0.0", "20", "0.0", "40", "75.0", "50", "0.0",
                "51", "360.0000000001"), "дуга 0…360 с округлением");
        }

        public static void Test_Dxf_frame_is_smallest_rectangle_not_axes()
        {
            // Пластина 200х100, построенная под 30°: SolidWorks кладёт развёртку в DXF наискось, а его граничная рамка —
            // 200х100 (проверено на SolidWorks 2025). По осям DXF выходило 187х223.
            double a = Math.PI / 6;
            double[,] corners = { { -100, -50 }, { 100, -50 }, { 100, 50 }, { -100, 50 } };
            List<string[]> lines = new List<string[]>();
            for (int i = 0; i < 4; i++)
            {
                double x1 = corners[i, 0], y1 = corners[i, 1], x2 = corners[(i + 1) % 4, 0], y2 = corners[(i + 1) % 4, 1];
                lines.Add(Line(x1 * Math.Cos(a) - y1 * Math.Sin(a) + 1000, x1 * Math.Sin(a) + y1 * Math.Cos(a),
                    x2 * Math.Cos(a) - y2 * Math.Sin(a) + 1000, x2 * Math.Sin(a) + y2 * Math.Cos(a)));
            }
            Assert.AreEqual("100х200", Frame(new string[0], Join(lines.ToArray())), "пластина под 30°");
            // Косынка — прямоугольный треугольник: рамка по катетам и по гипотенузе равны по площади; берётся та, что
            // ближе к осям чертежа, — по катетам.
            Assert.AreEqual("100х200", Frame(new string[0], Join(Line(0, 0, 200, 0), Line(200, 0, 0, 100), Line(0, 100, 0, 0))),
                "косынка по катетам");
        }

        public static void Test_Dxf_frame_reads_polylines_splines_ellipses_and_blocks()
        {
            // Та же планка одной замкнутой полилинией: дуги торцов — выпуклостью 42 (1 — полуокружность против часовой).
            Assert.AreEqual("80х280", Frame(new string[0], "0", "LWPOLYLINE", "90", "4", "70", "1",
                "10", "-100.0", "20", "-40.0", "10", "100.0", "20", "-40.0", "42", "1.0",
                "10", "100.0", "20", "40.0", "10", "-100.0", "20", "40.0", "42", "1.0"), "полилиния с дугами");
            // Сплайн: кривая проходит ниже средней управляющей точки, а коды 12/22, 13/23 — касательные, не точки.
            // Раньше касательные тянули рамку к началу координат: 1000 мм вместо 50х100.
            Assert.AreEqual("50х100", Frame(new string[0], Join(Line(1000, 1000, 1100, 1000),
                new[] { "0", "SPLINE", "70", "8", "71", "2", "72", "6", "73", "3", "74", "0",
                    "12", "0.447", "22", "0.894", "13", "0.447", "23", "-0.894",
                    "40", "0.0", "40", "0.0", "40", "0.0", "40", "1.0", "40", "1.0", "40", "1.0",
                    "10", "1000.0", "20", "1000.0", "10", "1050.0", "20", "1100.0", "10", "1100.0", "20", "1000.0" })),
                "сплайн — по кривой, не по управляющим точкам");
            // Эллипс: 11/21 — вектор большой полуоси от центра, а не точка.
            Assert.AreEqual("100х200", Frame(new string[0], "0", "ELLIPSE", "10", "300.0", "20", "300.0", "11", "100.0", "21", "0.0",
                "40", "0.5", "41", "0.0", "42", "6.283185307179586"), "эллипс 200х100");
            Assert.AreEqual("100х200", Frame(new string[0], "0", "ELLIPSE", "10", "300.0", "20", "300.0", "11", "100.0", "21", "0.0",
                "40", "0.5", "41", "0.0", "42", "6.2831853072"), "эллипс с 2π, округлённым до 10 знаков");
            // Контур внутри блока: вставка с масштабом 2.
            string[] blocks = { "0", "SECTION", "2", "BLOCKS", "0", "BLOCK", "2", "Контур", "10", "0.0", "20", "0.0" };
            List<string> dxf = new List<string>(blocks);
            dxf.AddRange(Join(Line(0, 0, 10, 0), Line(10, 0, 10, 20), Line(10, 20, 0, 20), Line(0, 20, 0, 0)));
            dxf.AddRange(new[] { "0", "ENDBLK", "0", "ENDSEC", "0", "SECTION", "2", "ENTITIES",
                "0", "INSERT", "2", "Контур", "10", "500.0", "20", "500.0", "41", "2.0", "42", "2.0", "0", "ENDSEC", "0", "EOF" });
            double width, length;
            using (StringReader reader = new StringReader(string.Join(Environment.NewLine, dxf.ToArray())))
                Assert.IsTrue(DxfFrame.Measure(reader, out width, out length), "блок измерен");
            Assert.AreEqual("20х40", ExportNaming.Round(width) + "х" + ExportNaming.Round(length), "контур из блока с масштабом");
            // Блок с базовой точкой (10, 0), вставка под 90° массивом в два столбца через 30 и отрезок рядом: копии
            // ложатся в X −20…0, Y −10…0 и 20…30, с отрезком x = 60 — рамка 80х40. Без базы, поворота или массива — иная.
            dxf = new List<string> { "0", "SECTION", "2", "BLOCKS", "0", "BLOCK", "2", "Полоса", "10", "10.0", "20", "0.0" };
            dxf.AddRange(Join(Line(0, 0, 10, 0), Line(10, 0, 10, 20), Line(10, 20, 0, 20), Line(0, 20, 0, 0)));
            dxf.AddRange(new[] { "0", "ENDBLK", "0", "ENDSEC", "0", "SECTION", "2", "ENTITIES",
                "0", "INSERT", "2", "Полоса", "10", "0.0", "20", "0.0", "50", "90.0", "70", "2", "44", "30.0" });
            dxf.AddRange(Line(60, -10, 60, 30));
            dxf.AddRange(new[] { "0", "ENDSEC", "0", "EOF" });
            using (StringReader reader = new StringReader(string.Join(Environment.NewLine, dxf.ToArray())))
                Assert.IsTrue(DxfFrame.Measure(reader, out width, out length), "массив блока измерен");
            Assert.AreEqual("40х80", ExportNaming.Round(width) + "х" + ExportNaming.Round(length), "база, поворот и массив вставки");
            // Чертёж в дюймах ($INSUNITS = 1): в имени — миллиметры.
            Assert.AreEqual("254х508", Frame(new[] { "9", "$INSUNITS", "70", "1" }, Join(Line(0, 0, 20, 0), Line(20, 0, 20, 10),
                Line(20, 10, 0, 10), Line(0, 10, 0, 0))), "дюймы в мм");
        }

        public static void Test_Stale_dxf_is_same_document_and_revision_under_other_name()
        {
            const string stem = "ПРТИ.468211.161 Лист";
            const string now = stem + "_S3мм_2шт_100х250.dxf";
            Assert.IsTrue(ExportNaming.IsStaleDxf(stem + "_S3мм_1шт_100х200.dxf", stem, 0, now), "другая рамка и количество");
            // №42: развёртки тел многотельной детали — у каждого своя; прежняя — только того же тела.
            string body1 = stem + "_тело1_S3мм_1шт_100х200.dxf";
            Assert.IsFalse(ExportNaming.IsStaleDxf(stem + "_тело2_S8мм_1шт_100х200.dxf", stem, 0, body1), "тело 2 — не прежняя тела 1");
            Assert.IsTrue(ExportNaming.IsStaleDxf(stem + "_тело1_S3мм_1шт_90х200.dxf", stem, 0, body1), "тело 1 с другой рамкой");
            Assert.IsTrue(ExportNaming.IsStaleDxf(stem + "_S3мм_1шт_100х200.dxf", stem, 0, body1), "деталь стала многотельной");
            Assert.IsTrue(ExportNaming.IsStaleDxf(body1, stem, 0, now), "деталь стала однотельной");
            Assert.IsTrue(ExportNaming.IsStaleDxf(stem + "_S2.5мм_100х250.dxf", stem, 0, now), "другая толщина, без количества");
            Assert.IsFalse(ExportNaming.IsStaleDxf(now.ToUpperInvariant(), stem, 0, now), "только что выгруженный");
            Assert.IsFalse(ExportNaming.IsStaleDxf(stem + "_S3мм_1шт_100х200_Изм1.dxf", stem, 0, now), "другая ревизия");
            Assert.IsTrue(ExportNaming.IsStaleDxf(stem + "_S3мм_1шт_100х200_Изм1.dxf", stem, 1, stem + "_S3мм_2шт_100х250_Изм1.dxf"),
                "та же ревизия");
            Assert.IsFalse(ExportNaming.IsStaleDxf(stem + "_S3мм_1шт_100х200_Изм2.dxf", stem, 1, now), "ревизия 2 — не 1");
            Assert.IsFalse(ExportNaming.IsStaleDxf("ПРТИ.468211.161 Лист10_S3мм_1шт_100х200.dxf", stem, 0, now), "другой документ");
            Assert.IsFalse(ExportNaming.IsStaleDxf("ПРТИ.468211.161-01 Лист_S3мм_1шт_100х200.dxf", "ПРТИ.468211.161", 0, now),
                "исполнение со своим обозначением");
            Assert.IsFalse(ExportNaming.IsStaleDxf(stem + ".pdf", stem, 0, now), "не развёртка");
            Assert.IsFalse(ExportNaming.IsStaleDxf("_замер_0a1b.dxf", stem, 0, now), "временный файл");
        }

        public static void Test_Frame_side_rounds_up()
        {
            // Заготовка в имени не меньше детали (ТЗ-02 Т-28, «целые»): 200,4 — «201»; сотые — погрешность замера.
            Assert.AreEqual("201", ExportNaming.Round(200.4), "вверх");
            Assert.AreEqual("200", ExportNaming.Round(200.0), "целое остаётся");
            Assert.AreEqual("200", ExportNaming.Round(199.9995), "замер дуги ломаной");
            Assert.AreEqual("200", ExportNaming.Round(200.004), "сотые — не деталь");
            Assert.AreEqual("23", ExportNaming.Round(22.5), "половина — вверх");
        }

        public static void Test_Revision_and_archive()
        {
            Assert.AreEqual(0, ExportNaming.Revision(""), "нет свойства");
            Assert.AreEqual(0, ExportNaming.Revision("0"), "ревизия 0 — это черновик");
            Assert.AreEqual(3, ExportNaming.Revision("3"), "третья ревизия");
            Assert.AreEqual(2, ExportNaming.Revision("2 (по акту)"), "ревизия с пояснением");
            Assert.AreEqual("", ExportNaming.RevisionSuffix(0), "черновик — без суффикса");
            Assert.AreEqual("_Изм4", ExportNaming.RevisionSuffix(4), "суффикс ревизии");

            string pdf = Path.Combine(Product, "02_PDF", "ТС-52.00.01.004 Заглушка.pdf");
            Assert.AreEqual(Path.Combine(Product, "02_PDF", "_Аннулировано", "ТС-52.00.01.004 Заглушка_2026-09-17_2145.pdf"),
                ExportNaming.ArchivePath(pdf, new DateTime(2026, 9, 17, 21, 45, 0)), "прежний файл выдачи");
        }

        public static void Test_Path_longer_than_240_is_refused()
        {
            string ok = @"Z:\" + new string('a', 237);
            Assert.AreEqual(240, ok.Length, "подготовка");
            Assert.AreEqual("", ExportNaming.TooLong(ok), "240 знаков — можно");
            Assert.IsTrue(ExportNaming.TooLong(ok + "b").StartsWith("путь 241 знаков, больше 240"), "241 — отказ с причиной");
            Assert.AreEqual("", ExportNaming.TooLong(null), "нет пути — не отказ");
        }

        /// <summary>З-48: файл выдачи — по своей папке; заметки и служебные файлы Windows выгрузка не трогает.</summary>
        public static void Test_Output_files_by_their_folder()
        {
            Assert.IsTrue(ExportNaming.IsOutputFile(Product + @"\02_PDF\ТС-52.00.01.004 Заглушка.pdf"), "PDF в 02_PDF");
            Assert.IsTrue(ExportNaming.IsOutputFile(Product + @"\03_ЧПУ\Лазер_Лист\Х_S3мм_1шт_10х20.DXF"), "DXF в Лазер_Лист, регистр любой");
            Assert.IsTrue(ExportNaming.IsOutputFile(Product + @"\03_ЧПУ\Лазер_Лист\Эскиз заказчика.dwg"), "DWG в Лазер_Лист");
            Assert.IsTrue(ExportNaming.IsOutputFile(Product + @"\03_ЧПУ\Труборез\Стойка.igs"), "IGS в Труборез");
            Assert.IsTrue(ExportNaming.IsOutputFile(Product + @"\03_ЧПУ\Труборез\Стойка.step"), "STEP в Труборез");
            Assert.IsFalse(ExportNaming.IsOutputFile(Product + @"\02_PDF\Для цеха.txt"), "заметка — не файл выдачи");
            Assert.IsFalse(ExportNaming.IsOutputFile(Product + @"\02_PDF\Thumbs.db"), "служебный файл Windows");
            Assert.IsFalse(ExportNaming.IsOutputFile(Product + @"\02_PDF\~$Заглушка.pdf"), "метка открытого файла");
            Assert.IsFalse(ExportNaming.IsOutputFile(Product + @"\03_ЧПУ\Лазер_Лист\Заглушка.pdf"), "PDF не в своей папке");
            Assert.IsFalse(ExportNaming.IsOutputFile(Product + @"\01_3D\Заглушка.pdf"), "не папка выдачи");
            Assert.IsTrue(ExportNaming.IsLzkSheet("ЛЗК_ТС-52_Расход.pdf"), "лист ЛЗК от «Готово к производству»");
            Assert.IsFalse(ExportNaming.IsLzkSheet("ЛЗК_ТС-52.xlsx"), "книга — не PDF листа");
        }

        /// <summary>З-48: выданная ревизия документа — по его файлам в отчётах выдачи; ревизия 0 — файлы без «_ИзмN».</summary>
        public static void Test_Issued_revision_of_document()
        {
            Assert.AreEqual(0, ExportNaming.RevisionOf("Х Деталь.pdf"), "без суффикса");
            Assert.AreEqual(2, ExportNaming.RevisionOf("Х Деталь_Изм2.pdf"), "PDF второй ревизии");
            Assert.AreEqual(1, ExportNaming.RevisionOf("Х Деталь_S3мм_1шт_10х20_Изм1.dxf"), "DXF первой ревизии");
            Assert.AreEqual(0, ExportNaming.RevisionOf("Х Изм1 Деталь.pdf"), "«Изм» в наименовании — не суффикс");
            string[] stems = { "ПРТИ.468211.241 Пластина", "ПРТИ.468211.241-01 Пластина" };
            string[] issued = { "ПРТИ.468211.241 Пластина.pdf", "ПРТИ.468211.241-01 Пластина_S3мм_1шт_100х200_Изм1.dxf",
                "ПРТИ.468211.2410 Пластина_Изм2.pdf", "ПРТИ.468211.241 Пластина.sldprt" };
            Assert.IsTrue(ExportNaming.IssuedAtRevision(issued, stems, 0), "ревизия 0 выдана PDF");
            Assert.IsTrue(ExportNaming.IssuedAtRevision(issued, stems, 1), "ревизия 1 выдана развёрткой исполнения -01");
            Assert.IsFalse(ExportNaming.IssuedAtRevision(issued, stems, 2), "«…2410» — чужой документ, ревизия 2 не выдана");
            Assert.IsFalse(ExportNaming.IssuedAtRevision(issued, new string[0], 0), "без основ имён — не выдан");
        }

        /// <summary>
        /// З-48: полная выгрузка изделия — что остаётся в папках выдачи. Прежние файлы убранной и переименованной детали и
        /// чужие файлы — в «_Аннулировано»; выданное цеху, файлы сорвавшейся выгрузки и листы ЛЗК — на месте.
        /// </summary>
        public static void Test_Leftovers_after_full_export()
        {
            ExportLeftovers l = new ExportLeftovers();
            l.Previous.UnionWith(new[] { "ПРТИ.468211.262 Косынка_S3мм_1шт_100х200.dxf", "ПРТИ.468211.264 Ребро.pdf" });
            l.Issued.UnionWith(new[] { "ПРТИ.468211.270 Опора.pdf", "ПРТИ.468211.271 Упор.pdf", "ПРТИ.468211.272 Рёбра.pdf",
                "ЛЗК_ПРТИ.468211.260_Расход.pdf" });
            l.Exported.AddRange(new[] { "ПРТИ.468211.261 Пластина", "ПРТИ.468211.270 Опора" });
            l.Protected.Add("ПРТИ.468211.271 Упор");
            l.Failed.AddRange(new[] { "ПРТИ.468211.264 Ребро" });
            string reason;

            Assert.AreEqual(LeftoverAction.Keep, l.Decide("ЛЗК_ПРТИ.468211.260_Расход.pdf", out reason), "лист ЛЗК — не выгрузки");
            Assert.AreEqual(LeftoverAction.Archive, l.Decide("ПРТИ.468211.262 Косынка_S3мм_1шт_100х200.dxf", out reason),
                "прежняя развёртка убранной детали");
            Assert.IsTrue(reason.StartsWith("прежний файл выгрузки"), reason);
            Assert.AreEqual(LeftoverAction.Archive, l.Decide("Эскиз заказчика.dxf", out reason), "чужой файл");
            Assert.IsTrue(reason.StartsWith("не из выгрузки"), reason);
            Assert.AreEqual(LeftoverAction.Carry, l.Decide("ПРТИ.468211.264 Ребро.pdf", out reason), "выгрузка сорвалась — прежний на месте");
            Assert.AreEqual("", reason, "о сорвавшейся выгрузке скажет пропуск");
            Assert.AreEqual(LeftoverAction.Carry, l.Decide("ПРТИ.468211.271 Упор.pdf", out reason), "выданный документ пропущен — файл у цеха");
            Assert.AreEqual(LeftoverAction.Carry, l.Decide("ПРТИ.468211.271 Упор_S3мм_1шт_10х20.dxf", out reason),
                "файл выданного документа, которого нет в отчётах выдачи, — тоже его");
            Assert.AreEqual(LeftoverAction.Archive, l.Decide("ПРТИ.468211.270 Опора.pdf", out reason), "выдан ревизией 0, выгружена новая");
            Assert.IsTrue(reason.StartsWith("выдан прежней ревизией"), reason);
            Assert.AreEqual(LeftoverAction.Carry, l.Decide("ПРТИ.468211.272 Рёбра.pdf", out reason),
                "выданный файл документа, которого в изделии нет, без новой ревизии сборки — на месте");
            Assert.IsTrue(reason.StartsWith(ExportLeftovers.OrphanIssued), "замечание — то, что читает проверка: " + reason);
            Assert.IsTrue(reason.Contains("оформите новую ревизию сборочного чертежа"), reason);
            l.TopExported = true;
            Assert.AreEqual(LeftoverAction.Archive, l.Decide("ПРТИ.468211.272 Рёбра.pdf", out reason),
                "новая ревизия сборки убрала документ — его выданный файл в архив");
        }

        /// <summary>З-48: убранный в «_Аннулировано» файл — сведения в окне итога, не забота.</summary>
        public static void Test_Archive_note_is_information()
        {
            List<Notice> list = Notices.FromExport(new string[0], new[]
            {
                "Х Косынка_S3мм_1шт_100х200.dxf — " + ExportLog.ArchiveNote + ": прежний файл выгрузки, сейчас его нет",
                "Х Стойка.igs — IGS не по оси трубы"
            });
            Assert.AreEqual(NoticeLevel.Info, list[0].Level, "убранный файл — сведения");
            Assert.AreEqual("Х Косынка_S3мм_1шт_100х200.dxf", list[0].Document, "файл назван");
            Assert.AreEqual(NoticeLevel.Warning, list[1].Level, "прочие замечания выгрузки — как раньше");
        }

        /// <summary>
        /// З-51 (решение владельца 24.09.2026): многотельная деталь в IGS не идёт. Замечание с «Лазерная резка трубы» проверка
        /// изделия читает из отчёта выгрузки (правило «е»); без резки трубы — только предупреждение. В окне итога подсказка —
        /// про модель: файла нет, проверять нечего.
        /// </summary>
        public static void Test_Multibody_note_is_read_back_by_check()
        {
            string reason = ExportLog.MultibodyReason(3, 0, 0, true);
            Assert.IsTrue(reason.StartsWith(ExportLog.MultibodyNoIgs + " (тел 3): проверьте чертёж и сборку"), reason);
            Assert.IsTrue(reason.Contains("снимите «" + LzkOperations.TubeCutting + "» в ведомости ЛЗК"), "как снять замечание: " + reason);
            ExportLog log = new ExportLog();
            log.Warn("А.105 Рама.sldprt [00]", reason);
            ExportLog back = ExportLog.Parse(log.Text());
            Assert.AreEqual(1, back.Warnings.Count, "замечание в отчёте");
            KeyValuePair<string, string> note = ExportLog.SplitSkip(back.Warnings[0]);
            Assert.AreEqual("А.105 Рама.sldprt [00]", note.Key, "документ с исполнением");
            Assert.IsTrue(note.Value.StartsWith(ExportLog.MultibodyNoIgs), "то, что читает проверка: " + note.Value);
            Assert.AreEqual(0, back.Skipped.Count, "не пропуск: деталь выгружена, прежние файлы её полная выгрузка убирает");

            string unticked = ExportLog.MultibodyReason(2, 1, 1, false);
            Assert.IsFalse(unticked.StartsWith(ExportLog.MultibodyNoIgs), "без резки трубы проверка не останавливает: " + unticked);
            Assert.IsTrue(unticked.Contains("(тел 2, из них поверхностей 1, скрытых 1)"), unticked);
            Assert.IsFalse(unticked.Contains("поставьте"), "совета поставить резку трубы нет — IGS всё равно не будет: " + unticked);
            Assert.IsTrue(ExportLog.MultibodyReason(2, 0, 1, true).Contains("(тел 2, из них скрытых 1)"), "скрытое тело названо");

            Assert.IsTrue(ExportLog.IsMultibodyNote(reason) && ExportLog.IsMultibodyNote(unticked), "оба — о многотельной детали");
            Assert.IsFalse(ExportLog.IsMultibodyNote("многотельная листовая деталь: листовых тел 2 — DXF на каждое тело"),
                "замечание о развёртках листовых тел — другое");
            List<Notice> list = Notices.FromExport(new string[0], new[] { "А.105 Рама.sldprt — " + reason, "А.106 Рама.sldprt — " + unticked });
            Assert.AreEqual(NoticeLevel.Warning, list[0].Level, "замечание");
            Assert.AreEqual("Проверьте чертёж и сборку детали", list[0].Hint, "подсказка — про модель");
            Assert.AreEqual("Проверьте чертёж и сборку детали", list[1].Hint, "и без резки трубы");
        }
    }
}
