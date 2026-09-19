using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Списки MProp этого рабочего места: своя фамилия и организация первыми (З-3).</summary>
    public static class SwPlusListsTests
    {
        private static string Join(string[] lines)
        {
            return string.Join("|", lines);
        }

        public static void Test_Own_family_goes_first_and_list_is_clean()
        {
            Assert.AreEqual("Тестов Т.Т.|Иванов И.И.|Петров П.П.",
                Join(SwPlusLists.Families(new[] { "Иванов И.И.", "", " Тестов Т.Т. ", "Петров П.П.", "иванов и.и." }, "Тестов Т.Т.", "")),
                "своя — первой, пустые и повторы убраны");
            Assert.AreEqual("Тестов Т.Т.|Иванов И.И.", Join(SwPlusLists.Families(new string[0], "Тестов Т.Т.", "Иванов И.И.")),
                "проверяющий — в конец");
            Assert.AreEqual("Иванов И.И.", Join(SwPlusLists.Families(new[] { "Иванов И.И." }, "", null)), "без фамилии список не меняется");
        }

        public static void Test_Own_firm_pair_goes_first_with_its_code()
        {
            Assert.AreEqual("ООО «Испытание»|ИС|ТОО «Троя»|ТР",
                Join(SwPlusLists.Firms(new[] { "ТОО «Троя»", "ТР", "ООО «Испытание»", "ИС" }, "ООО «Испытание»")),
                "своя пара с кодом — первой");
            Assert.AreEqual("ООО «Испытание»||ТОО «Троя»|ТР",
                Join(SwPlusLists.Firms(new[] { "ТОО «Троя»", "ТР", "", "" }, "ООО «Испытание»")), "новая — первой с пустым кодом");
            Assert.AreEqual("ООО «Испытание»|", Join(SwPlusLists.Firms(new string[0], "ООО «Испытание»")), "пустой список");
            Assert.AreEqual("ООО «Испытание»|", Join(SwPlusLists.Firms(new[] { "ООО «Испытание»" }, "ООО «Испытание»")),
                "нечётное число строк");
        }
    }
}
