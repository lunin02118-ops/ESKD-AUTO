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
    }
}
