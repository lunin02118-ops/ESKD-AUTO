using System.Collections.Generic;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// «Закрыть заказ» и одноимённые файлы другого заказа (сверка SW API 23.09.2026, №16). SolidWorks ищет компонент
    /// сначала среди открытых документов — по имени файла, а не по пути, — и Pack and Go заказа А мог упаковать в архив
    /// деталь заказа Б с тем же именем. Одно изделие с теми же именами файлов повторяется в разных заказах.
    /// </summary>
    public static class OrderArchiveTests
    {
        private const string OrderA = @"Z:\_Заявки\2026-010 Стенд";
        private const string PartA = OrderA + @"\02_Металл\И01_ПРТИ.468211.100\01_3D\ПРТИ.468211.102 Стойка.sldprt";
        private const string AsmA = OrderA + @"\02_Металл\И01_ПРТИ.468211.100\01_3D\ПРТИ.468211.100 СБ Кондуктор.sldasm";
        private const string PartB = @"Z:\_Заявки\2026-011 Другой\02_Металл\И01_ПРТИ.468211.100\01_3D\ПРТИ.468211.102 Стойка.sldprt";

        public static void Test_Open_namesake_of_other_order_is_found()
        {
            List<string> found = OrderArchive.Namesakes(new[] { PartB }, new[] { AsmA, PartA }, OrderA);
            Assert.AreEqual(1, found.Count, "одноимённая деталь другого заказа открыта");
            Assert.AreEqual(PartB, found[0], "названа полным путём");
            Assert.AreEqual(0, OrderArchive.Namesakes(new[] { PartA }, new[] { AsmA, PartA }, OrderA).Count,
                "документ самого заказа — не одноимённый (его ловит «документ заказа открыт»)");
            Assert.AreEqual(0, OrderArchive.Namesakes(new[] { @"Z:\Библиотека\Болт М8.sldprt", "" }, new[] { AsmA, PartA }, OrderA).Count,
                "другое имя и документ без пути — не мешают");
        }

        public static void Test_Kit_takes_assembly_named_by_product_folder()
        {
            // Прогон r34 24.09.2026 (e2e Y06): у изделия «И01_ПРТИ.468211.100» три сборки, и «Закрыть заказ» брало в комплект
            // самую короткую по пути — подсборку «108 СБ Узел», а не само изделие: главной считалась только сборка с
            // обозначением «….00.000». Главная — та, чьё обозначение совпадает с шифром в имени папки изделия.
            string models = OrderA + @"\02_Металл\И01_ПРТИ.468211.100\01_3D\";
            string[] assemblies =
            {
                models + "ПРТИ.468211.108 СБ Узел.sldasm", models + "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm",
                models + "ПРТИ.468211.110 СБ Узел опоры.sldasm"
            };
            Assert.AreEqual(models + "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm",
                ProductLocator.MainAssembly(OrderA + @"\02_Металл\И01_ПРТИ.468211.100", assemblies), "по шифру папки изделия");
            string table = OrderA + @"\02_Металл\И02_ТС-52_Стол\01_3D\";
            Assert.AreEqual(table + "ТС-52.00.00.000 СБ Стол.sldasm",
                ProductLocator.MainAssembly(OrderA + @"\02_Металл\И02_ТС-52_Стол",
                    new[] { table + "ТС-52.01.00.000 СБ Рама.sldasm", table + "ТС-52.00.00.000 СБ Стол.sldasm" }),
                "шифр «ТС-52» — сборка «ТС-52.00.00.000»");
            Assert.AreEqual(table + "Рама.sldasm", ProductLocator.MainAssembly(OrderA + @"\02_Металл\И02_Стол",
                new[] { table + "Рама сварная.sldasm", table + "Рама.sldasm" }), "без обозначений — самая короткая, как раньше");
            Assert.AreEqual(null, ProductLocator.MainAssembly(OrderA + @"\02_Металл\И03", new string[0]), "сборок нет");
        }

        public static void Test_Order_prefix_does_not_swallow_neighbour_order()
        {
            // Заказ «85_Т» не должен считать своими документы заказа «85_Т2».
            string order = @"Z:\_Заявки\85_Т";
            string own = order + @"\02_Металл\И01\01_3D\Деталь.sldprt";
            string neighbour = @"Z:\_Заявки\85_Т2\02_Металл\И01\01_3D\Деталь.sldprt";
            Assert.AreEqual(1, OrderArchive.Namesakes(new[] { neighbour }, new[] { own }, order + @"\").Count,
                "одноимённая деталь соседнего заказа с похожим именем");
        }

        public static void Test_Namesake_text_names_windows_and_windowless_parts()
        {
            // Ревью 23.09.2026: отказ перечислял все загруженные документы без предела, в том числе детали открытых сборок
            // другого заказа без своего окна, и просил «закройте их» — закрыть такую деталь нечем.
            List<string> visible = new List<string>();
            for (int i = 0; i < 25; i++) visible.Add(@"Z:\_Заявки\2026-011 Другой\Деталь" + i + ".sldprt");
            string text = OrderArchive.NamesakeText(visible, new List<string>(), 20);
            Assert.IsTrue(text.Contains("Деталь19.sldprt"), "первые 20 названы: " + text);
            Assert.IsFalse(text.Contains("Деталь20.sldprt"), "дальше — числом");
            Assert.IsTrue(text.Contains("и ещё 5"), "остаток назван числом: " + text);
            string hidden = OrderArchive.NamesakeText(new List<string>(), new List<string> { PartB }, 20);
            Assert.IsTrue(hidden.Contains("без своего окна"), "у детали сборки нет окна — так и сказано: " + hidden);
            Assert.IsTrue(hidden.Contains("закройте сборки и чертежи"), "что закрыть: " + hidden);
            Assert.IsTrue(hidden.Contains(PartB), "путь назван — по папке видно, чья сборка: " + hidden);
            Assert.IsTrue(hidden.Contains("компоненты открытых сборок"), "не утверждается, что это другой заказ: " + hidden);
            Assert.AreEqual("", OrderArchive.NamesakeText(new List<string>(), new List<string>(), 20), "нечего — пусто");
        }

        public static void Test_Substituted_component_is_reported()
        {
            List<string> bad = OrderArchive.Substituted(new[] { AsmA, PartB }, new[] { AsmA, PartA }, path => true);
            Assert.AreEqual(1, bad.Count, "Pack and Go взял чужую Стойку при своей");
            Assert.IsTrue(bad[0].Contains(PartB), "названа подменённая деталь");
            Assert.AreEqual(0, OrderArchive.Substituted(new[] { AsmA, PartA, @"Z:\Библиотека\Болт М8.sldprt" },
                new[] { AsmA, PartA }, path => true).Count, "общий компонент библиотеки, которого нет в изделии, — не подмена");
            List<string> missing = OrderArchive.Substituted(new[] { AsmA, @"Z:\Нет\Пропавшая.sldprt" }, new[] { AsmA },
                path => path == AsmA);
            Assert.AreEqual(1, missing.Count, "ссылка на несуществующий файл");
            Assert.IsTrue(missing[0].Contains("нет файла"), "причина названа");
        }

        /// <summary>
        /// №16, решение владельца 24.09.2026: проверка, выгрузка и ЛЗК узнают компонент, который SolidWorks взял из другой
        /// папки при своём одноимённом файле изделия.
        /// </summary>
        public static void Test_Swapped_component_of_open_assembly_is_found()
        {
            List<string> swapped = OrderArchive.Swapped(new[] { PartB, PartB, @"Z:\Библиотека\Болт М8.sldprt", PartA.ToUpperInvariant() },
                new[] { AsmA, PartA });
            Assert.AreEqual(1, swapped.Count, "чужая Стойка — один раз; своя (в другом регистре) и библиотечный болт — не подмена");
            Assert.AreEqual(PartB, swapped[0], "назван путь подменённого компонента");
            string text = OrderArchive.SwappedText(swapped, 5);
            Assert.IsTrue(text.Contains("одноимённ") && text.Contains(PartB), "отказ называет причину и файл");
            Assert.AreEqual(0, OrderArchive.Swapped(null, new[] { AsmA }).Count, "нет состава — нет подмены");
        }

        /// <summary>
        /// e2e Y06, 24.09.2026: Pack and Go SolidWorks 2025 SP3 перечислил только сборку и её чертёж — без деталей и
        /// подсборок (и у образцовых сборок из поставки). Комплект уехал в архив одной сборкой, и она открывалась без
        /// компонентов. Состав комплекта берётся из зависимостей сборки, недостающее добавляется явно и сверяется.
        /// </summary>
        public static void Test_Kit_adds_models_missing_from_pack_and_go()
        {
            string product = OrderA + @"\02_Металл\И01_ПРТИ.468211.100";
            string drawing = product + @"\01_3D\ПРТИ.468211.100 СБ Кондуктор.slddrw";
            string sub = product + @"\01_3D\ПРТИ.468211.110 СБ Узел.sldasm";
            string bolt = @"Z:\Библиотека\Болт М8.sldprt";
            string inner = product + @"\01_3D\Деталь1^ПРТИ.468211.100 СБ Кондуктор.sldprt";

            List<string> missing = OrderArchive.KitMissing(new[] { sub, PartA, bolt, PartA.ToUpperInvariant(), inner, "" },
                new[] { AsmA, drawing });
            Assert.AreEqual(3, missing.Count, "подсборка, деталь и библиотечный болт — по разу: " + string.Join("; ", missing.ToArray()));
            Assert.IsFalse(missing.Contains(inner), "виртуальная деталь живёт внутри сборки — файла у неё нет");
            Assert.AreEqual(0, OrderArchive.KitMissing(new[] { PartA }, new[] { AsmA, PartA.ToUpperInvariant() }).Count,
                "то, что Pack and Go перечислил сам, не добавляется второй раз");

            string foreign = @"Z:\Библиотека\ПРТИ.468211.102 Стойка.sldprt";
            List<string> clash = OrderArchive.KitCollisions(new[] { AsmA, PartA, foreign, PartA.ToUpperInvariant() });
            Assert.AreEqual(1, clash.Count, "два разных файла с одним именем не поместятся в одну папку комплекта");
            Assert.IsTrue(clash[0].Contains(PartA) && clash[0].Contains(foreign), "названы оба: " + clash[0]);

            string kit = @"Z:\_tmp\2026-010 Стенд\Комплекты\ПРТИ.468211.100\";
            List<string> absent = OrderArchive.KitAbsent(new[] { AsmA, PartA, bolt },
                new[] { kit + "ПРТИ.468211.100 СБ Кондуктор.SLDASM", kit + "Болт М8.sldprt" });
            Assert.AreEqual(1, absent.Count, "после Pack and Go в папке комплекта нет Стойки");
            Assert.AreEqual(PartA, absent[0], "названа недостающая модель");

            List<string> folders = OrderArchive.KitDrawingFolders(new[] { AsmA, PartA, bolt, sub }, product);
            Assert.AreEqual(1, folders.Count, "чертежи ищутся только в папках изделия: " + string.Join("; ", folders.ToArray()));
            Assert.AreEqual(product + @"\01_3D", folders[0], "папка моделей изделия");
            Assert.AreEqual(0, OrderArchive.KitDrawingFolders(new[] { OrderA + @"\02_Металл\И01_ПРТИ.468211.1000\01_3D\А.sldprt" },
                product).Count, "соседнее изделие с тем же началом имени — не своя папка");
        }
    }
}
