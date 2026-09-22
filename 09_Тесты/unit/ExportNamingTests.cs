using System;
using System.Collections.Generic;
using System.IO;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Имена и места файлов выдачи для производства (ТЗ-02 Т-27…Т-31).</summary>
    public static class ExportNamingTests
    {
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
    }
}
