using System;
using System.IO;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Поиск изделия и заказа по пути документа (ТЗ-02 Т-7а).</summary>
    public static class ProductLocatorTests
    {
        private const string Order = @"\\Synology_TR\Конструкторский отдел\_Заявки\2026-014 Школа";

        public static void Test_Order_archive_is_next_to_orders_root()
        {
            // Диск архива Y: не заводится (решение владельца 19.09.2026): архив — «_Архив» на том же ресурсе.
            Assert.AreEqual(@"\\Synology_TR\Конструкторский отдел\_Архив",
                OrderArchive.DefaultRoot(@"\\Synology_TR\Конструкторский отдел\_Заявки\85-1_Т_Центр_Мангыстау"), "UNC");
            Assert.AreEqual(@"Z:\_Архив", OrderArchive.DefaultRoot(@"Z:\_Заявки\85-1_Т_Центр_Мангыстау\"), "диск Z:");
            Assert.AreEqual("", OrderArchive.DefaultRoot(@"Z:\"), "корень диска — архив не вывести");
        }

        public static void Test_Workbook_checksum_is_compared_by_prefix()
        {
            string full = "9159dab59d43ab7ff8a7394272b309ad55ada44e20c2c1d55c4b9d1fe9bb2856";
            // Шапка книги ЛЗК хранит 16 знаков суммы: полная сумма файла с ними совпадает — книга свежая.
            Assert.IsTrue(CheckRules.WorkbookMatchesAssembly("SHA-256 9159dab59d43ab7f", full), "та же сборка");
            Assert.IsTrue(CheckRules.WorkbookMatchesAssembly("SHA-256 " + full, full), "полная сумма");
            Assert.IsFalse(CheckRules.WorkbookMatchesAssembly("SHA-256 0000dab59d43ab7f", full), "другая сборка");
            Assert.IsTrue(CheckRules.WorkbookMatchesAssembly("", full), "в книге суммы нет — не с чем сравнивать");
        }

        public static void Test_Locate_product_inside_order()
        {
            ProductLocation location = ProductLocator.Locate(Order + @"\02_Металл\И01_ТС-52_Стол\01_3D\ТС-52.00.00.000 Стол.sldasm");
            Assert.IsTrue(location.Found, "изделие найдено: " + location.Reason);
            Assert.IsTrue(location.InOrder, "внутри заказов");
            Assert.IsFalse(location.InBase, "не база");
            Assert.AreEqual(Order + @"\02_Металл\И01_ТС-52_Стол", location.ProductFolder, "папка изделия");
            Assert.AreEqual(Order + @"\02_Металл\И01_ТС-52_Стол\01_3D", location.ModelsFolder, "папка моделей");
            Assert.AreEqual(Order, location.OrderFolder, "папка заказа");
        }

        public static void Test_OrderOf_names_the_order_folder()
        {
            // Решение владельца 23.09.2026: ЛЗК не пишет в модели других заказов — заказ файла узнаётся по пути.
            Assert.AreEqual(Order, ProductLocator.OrderOf(Order + @"\02_Металл\И01_ТС-52_Стол\01_3D\ТС-52.00.00.000 Стол.sldasm"), "UNC, «_Заявки»");
            Assert.AreEqual(@"Z:\03_ЗАКАЗЫ\778_Школа", ProductLocator.OrderOf(@"Z:\03_ЗАКАЗЫ\778_Школа\Общие детали\Кронштейн.sldprt"),
                "любая папка заказа, не только изделие");
            Assert.AreEqual(@"Z:\03_ЗАКАЗЫ\_Сдано\250-АСТ_Школа12",
                ProductLocator.OrderOf(@"Z:\03_ЗАКАЗЫ\_Сдано\250-АСТ_Школа12\02_Металл\И01_Стол\01_3D\Стол.sldasm"), "«_Сдано» — полка, заказ под ней");
            Assert.AreEqual(@"O:\778_Школа", ProductLocator.OrderOf(@"O:\778_Школа\02_Металл\И01_Стол\01_3D\Стол.sldasm"),
                "диск подключён к папке заказов: заказ — над «02_Металл»");
            Assert.AreEqual("", ProductLocator.OrderOf(@"D:\Черновики\Проба\Деталь.sldprt"), "вне заказов");
            Assert.AreEqual("", ProductLocator.OrderOf(@"Z:\03_ЗАКАЗЫ\Деталь.sldprt"), "файл прямо в корне заказов — не в заказе");
            Assert.AreEqual("", ProductLocator.OrderOf(""), "несохранённый документ");
            Assert.IsTrue(ProductLocator.SameOrder(@"Z:\03_ЗАКАЗЫ\778_Школа", @"\\Synology_TR\Конструкторский отдел\03_ЗАКАЗЫ\778_Школа"),
                "один заказ по диску и по сетевому пути");
            Assert.IsFalse(ProductLocator.SameOrder(@"Z:\03_ЗАКАЗЫ\778_Школа", @"Z:\03_ЗАКАЗЫ\775_Стол"), "разные заказы");
            Assert.IsFalse(ProductLocator.SameOrder("", ""), "вне заказов — не «тот же заказ»");
        }

        public static void Test_Locate_finds_product_for_nested_document()
        {
            // Чертёж лежит в 02_PDF, а не в 01_3D: изделие всё равно то же.
            ProductLocation location = ProductLocator.Locate(Order + @"\02_Металл\И02_ТС-53_Стул\02_PDF\ТС-53.00.00.000.pdf");
            Assert.AreEqual(Order + @"\02_Металл\И02_ТС-53_Стул", location.ProductFolder, "папка изделия по имени И<nn>_");
            Assert.AreEqual("", location.ModelsFolder, "папки моделей рядом нет");
            Assert.AreEqual(Order, location.OrderFolder, "папка заказа");
        }

        public static void Test_Locate_base_and_outside()
        {
            ProductLocation etalon = ProductLocator.Locate(
                @"\\Synology_TR\Конструкторский отдел\_Библиотека проектирования\_стандартные изделия\И01_85Т_Кровать\01_3D\85Т.00.00.000.sldasm");
            Assert.IsTrue(etalon.InBase, "эталон базы");
            Assert.IsTrue(etalon.Found, "эталон опознан");

            // Боевой заказ NC3-7R ссылался на крепёж из «_Библиотека проектирования» — это законная библиотека,
            // а не «ссылка за пределами заказа», иначе проверка каждого изделия начинается с ложного брака.
            ProductLocation hardware = ProductLocator.Locate(
                @"\\Synology_TR\Конструкторский отдел\_Библиотека проектирования\_ крепеж и фурнитура\03_Саморезы и конфирматы\Саморез DIN 968 4.2x16.SLDPRT");
            Assert.IsTrue(hardware.InBase, "крепёж библиотеки — в базе");

            ProductLocation outside = ProductLocator.Locate(@"D:\Черновики\Проба\Сборка.sldasm");
            Assert.IsFalse(outside.Found, "вне заказа и базы");
            Assert.AreEqual("файл не в папке заказа или базы", outside.Reason, "подсказка на серой кнопке");

            ProductLocation empty = ProductLocator.Locate("");
            Assert.IsFalse(empty.Found, "несохранённый документ");
            Assert.AreEqual("документ не сохранён", empty.Reason, "подсказка");
        }

        public static void Test_Purchased_folders_of_a_product()
        {
            string models = Order + @"\02_Металл\И01_CN1-2_Каркас\01_3D";
            Assert.IsTrue(ProductLocator.IsPurchasedFolder(models + @"\Стандартные изделия и фурнитура\Заглушка овальная 30х15.SLDPRT"),
                "фурнитура изделия");
            Assert.IsTrue(ProductLocator.IsPurchasedFolder(models + @"\Крепёж\Винт ISO 7380-2 M8x16.SLDPRT"), "крепёж изделия");
            Assert.IsFalse(ProductLocator.IsPurchasedFolder(models + @"\Фанера\Сиденье фанерное.SLDPRT"),
                "фанерная деталь изготавливается, а не покупается");
            Assert.IsFalse(ProductLocator.IsPurchasedFolder(models + @"\CN1-2.SLDASM"), "сама сборка");
            Assert.IsFalse(ProductLocator.IsPurchasedFolder(@"\\NAS\_Заявки\Т-1 Школа\Крепёжная рама\01_3D\Рама.SLDASM"),
                "изделие с «крепёжным» именем не покупное: считаются только папки внутри 01_3D");
            Assert.IsFalse(ProductLocator.IsPurchasedFolder(@"D:\Проекты\Крепёжная рама\Рама.SLDASM"),
                "сборка вне заказа в папке «Крепёжная рама» — не покупное");
            Assert.IsTrue(ProductLocator.IsPurchasedFolder(@"\\NAS\База\Крепёж ГОСТ\М8\Винт.SLDPRT"), "«Крепёж ГОСТ» — покупное");
            Assert.IsTrue(ProductLocator.IsPurchasedFolder(@"\\NAS\База\Стандартные изделия\Винт M8.SLDPRT"),
                "библиотека базы вне изделия — как раньше");
        }

        public static void Test_Main_assembly_by_designation()
        {
            Assert.IsTrue(ProductLocator.IsMainAssembly(@"D:\И\01_3D\ТС-52.00.00.000 Стол.sldasm"), "главная сборка");
            Assert.IsFalse(ProductLocator.IsMainAssembly(@"D:\И\01_3D\ТС-52.00.01.000 Царга.sldasm"), "подсборка");
            Assert.IsFalse(ProductLocator.IsMainAssembly(@"D:\И\01_3D\Рама.sldasm"), "без обозначения");
        }
    }

    /// <summary>Правила проверки изделия и отчёт (ТЗ-02 Т-32…Т-34).</summary>
    public static class CheckRulesTests
    {
        public static void Test_Levels_follow_tz()
        {
            foreach (string rule in new[] { CheckRules.References, CheckRules.Rebuild, CheckRules.Attributes })
                Assert.AreEqual(CheckLevel.Defect, CheckRules.LevelOf(rule), "правило " + rule + " — брак");
            foreach (string rule in new[] { CheckRules.Sync, CheckRules.Drawing, CheckRules.Operations, CheckRules.Export,
                CheckRules.Workbook, CheckRules.Drawings })
                Assert.AreEqual(CheckLevel.Issue, CheckRules.LevelOf(rule), "правило " + rule + " — замечание");
            // Незаписанный реквизит — работа кнопки «Синхронизировать», а не брак конструктора.
            Assert.IsFalse(CheckRules.Sync == CheckRules.Attributes, "правило «в2» отдельно от «в»");
        }

        public static void Test_Outcome_is_worst_finding()
        {
            CheckReport report = new CheckReport();
            Assert.AreEqual(CheckLevel.Ok, report.Outcome, "пусто — готово");
            report.Add(CheckRules.Operations, CheckLevel.Issue, "А.001.sldprt", "не заполнено «Операции»");
            Assert.AreEqual(CheckLevel.Issue, report.Outcome, "замечание");
            report.Add(CheckRules.References, CheckLevel.Defect, "А.002.sldprt", "файл компонента не найден");
            Assert.AreEqual(CheckLevel.Defect, report.Outcome, "брак важнее замечания");
            Assert.AreEqual(1, report.Count(CheckLevel.Issue), "замечаний");
            Assert.AreEqual(1, report.Count(CheckLevel.Defect), "брака");
        }

        public static void Test_Report_text_and_outcome_parsing()
        {
            CheckReport report = new CheckReport
            {
                Product = "ТС-52",
                Assembly = "ТС-52.00.00.000 Стол.sldasm",
                User = "Лунин В.И.",
                Time = new DateTime(2026, 9, 17, 21, 5, 0)
            };
            report.Add(CheckRules.Operations, CheckLevel.Issue, "ТС-52.00.01.001 Царга.sldprt", "не заполнено свойство «Операции»");
            report.Add(CheckRules.References, CheckLevel.Defect, "Ножка.sldprt", "файл компонента не найден");
            report.Checksums.Add(new System.Collections.Generic.KeyValuePair<string, string>("ТС-52.00.00.000 Стол.sldasm", "ab12"));

            string text = CheckRules.Report(report);
            Assert.IsTrue(text.Contains("Итог:     БРАК (брак: 1, замечаний: 1)"), text);
            Assert.IsTrue(text.Contains("Проверил: Лунин В.И., 17.09.2026 21:05"), text);
            int defect = text.IndexOf("БРАК — Ножка.sldprt", StringComparison.Ordinal);
            int issue = text.IndexOf("ЗАМЕЧАНИЕ — ТС-52.00.01.001", StringComparison.Ordinal);
            Assert.IsTrue(defect > 0 && issue > defect, "брак выше замечаний: " + text);
            Assert.IsTrue(text.Contains("ab12  ТС-52.00.00.000 Стол.sldasm"), "контрольные суммы");
            Assert.AreEqual("БРАК", CheckRules.OutcomeOf(text), "итог читается из отчёта");

            CheckReport clean = new CheckReport { Product = "ТС-52", Assembly = "a.sldasm", User = "И" };
            string cleanText = CheckRules.Report(clean);
            Assert.IsTrue(cleanText.Contains("Замечаний нет."), cleanText);
            Assert.AreEqual("ГОТОВО", CheckRules.OutcomeOf(cleanText), "итог чистой проверки");
        }

        public static void Test_Report_paths()
        {
            string folder = Path.Combine(Path.GetTempPath(), "eskd_check");
            Assert.AreEqual(Path.Combine(folder, "_Проверка.txt"), CheckRules.ReportPath(folder), "отчёт");
            Assert.AreEqual(Path.Combine(folder, "_Проверка_пред.txt"), CheckRules.PreviousReportPath(folder), "прежний отчёт");
        }
    }
}
