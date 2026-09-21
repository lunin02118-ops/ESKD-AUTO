using System;
using System.Collections.Generic;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Имена свойств списка вырезов зависят от языка SolidWorks. Замечание владельца 21.09.2026: в его
    /// русском SolidWorks длина называется «ДЛИНА», книга искала «LENGTH» и не находила — в ведомость
    /// уходил наибольший размер габаритного ящика с пометкой «оценка», а для наклонной детали это не длина
    /// вовсе. Значения взяты из настоящей детали заказа NC3-7R.02.001.
    /// </summary>
    public static class CutListTests
    {
        private static List<KeyValuePair<string, string>> Russian()
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("ДЛИНА", "\"LENGTH@@@Элемент списка вырезов2@NC3-7R.02.001 Стойка.SLDPRT\""),
                new KeyValuePair<string, string>("УГОЛ1", "\"ANGLE1@@@Элемент списка вырезов2@NC3-7R.02.001 Стойка.SLDPRT\""),
                new KeyValuePair<string, string>("Типоразмер", "40х20х1,5"),
                new KeyValuePair<string, string>("QUANTITY", "\"QUANTITY@@@Элемент списка вырезов2@NC3-7R.02.001 Стойка.SLDPRT\""),
                new KeyValuePair<string, string>("TOTAL LENGTH", "\"TOTAL LENGTH@@@Элемент списка вырезов2@NC3-7R.02.001 Стойка.SLDPRT\"")
            };
        }

        /// <summary>Русский SolidWorks: имя «ДЛИНА», но ссылка внутри значения английская — по ней и находим.</summary>
        public static void Test_Length_is_found_by_link_not_by_name()
        {
            Assert.AreEqual("ДЛИНА", CutListProperties.Find(Russian(), "LENGTH", CutListProperties.LengthSpellings),
                            "длина заготовки в русском SolidWorks");
        }

        /// <summary>
        /// Развёртка листовой детали (замечание владельца 21.09.2026: у гнутых деталей в ведомость уходил габарит
        /// готовой детали). В русском SolidWorks 2025 и имя, и ссылка русские — «SW-Длина граничной рамки@@@…»
        /// (снято с построенной детали), поэтому находим по имени; английская сборка — по ссылке «SW-Bounding Box…».
        /// </summary>
        public static void Test_Flat_pattern_bounding_box_is_found_in_russian_and_english()
        {
            List<KeyValuePair<string, string>> russian = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Длина граничной рамки", "\"SW-Длина граничной рамки@@@Элемент списка вырезов1@Деталь1.SLDPRT\""),
                new KeyValuePair<string, string>("Ширина граничной рамки", "\"SW-Ширина граничной рамки@@@Элемент списка вырезов1@Деталь1.SLDPRT\""),
                new KeyValuePair<string, string>("Площадь граничной рамки", "\"SW-Площадь граничной рамки@@@Элемент списка вырезов1@Деталь1.SLDPRT\""),
                new KeyValuePair<string, string>("Толщина листового металла", "\"SW-Толщина листового металла@@@Элемент списка вырезов1@Деталь1.SLDPRT\"")
            };
            Assert.AreEqual("Длина граничной рамки",
                            CutListProperties.Find(russian, "Bounding Box Length", CutListProperties.BoundingBoxLengthSpellings), "длина развёртки");
            Assert.AreEqual("Ширина граничной рамки",
                            CutListProperties.Find(russian, "Bounding Box Width", CutListProperties.BoundingBoxWidthSpellings), "ширина развёртки");
            Assert.AreEqual("Толщина листового металла",
                            CutListProperties.Find(russian, "Sheet Metal Thickness", CutListProperties.SheetThicknessSpellings), "толщина листа");
            Assert.AreEqual("", CutListProperties.Find(russian, "LENGTH", CutListProperties.LengthSpellings), "длины проката у листа нет");

            List<KeyValuePair<string, string>> english = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Bounding Box Length", "\"SW-Bounding Box Length@@@Cut-List-Item1@Part1.SLDPRT\""),
                new KeyValuePair<string, string>("Bounding Box Width", "\"SW-Bounding Box Width@@@Cut-List-Item1@Part1.SLDPRT\"")
            };
            Assert.AreEqual("Bounding Box Length",
                            CutListProperties.Find(english, "Bounding Box Length", CutListProperties.BoundingBoxLengthSpellings), "по ссылке «SW-…»");
            Assert.AreEqual("Bounding Box Width",
                            CutListProperties.Find(english, "Bounding Box Width", CutListProperties.BoundingBoxWidthSpellings), "по ссылке «SW-…»");
        }

        /// <summary>«ОБЩАЯ ДЛИНА» — это длина всех заготовок папки вместе; на одну заготовку она не годится.</summary>
        public static void Test_Total_length_is_not_mistaken_for_length()
        {
            List<KeyValuePair<string, string>> only = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("TOTAL LENGTH", "\"TOTAL LENGTH@@@папка@файл.SLDPRT\"")
            };
            Assert.AreEqual("", CutListProperties.Find(only, "LENGTH", CutListProperties.LengthSpellings),
                            "общая длина за длину заготовки не выдаётся");
        }

        /// <summary>Английский SolidWorks: имя и ссылка совпадают — ничего не меняется.</summary>
        public static void Test_English_solidworks_still_works()
        {
            List<KeyValuePair<string, string>> english = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("LENGTH", "\"LENGTH@@@Cut-List-Item1@Part1.SLDPRT\""),
                new KeyValuePair<string, string>("QUANTITY", "\"QUANTITY@@@Cut-List-Item1@Part1.SLDPRT\"")
            };
            Assert.AreEqual("LENGTH", CutListProperties.Find(english, "LENGTH", CutListProperties.LengthSpellings), "длина");
            Assert.AreEqual("QUANTITY", CutListProperties.Find(english, "QUANTITY", CutListProperties.QuantitySpellings), "количество");
        }

        /// <summary>Свойство вписали руками, ссылки нет — тогда годится имя на любом из известных языков.</summary>
        public static void Test_Hand_written_property_is_found_by_name()
        {
            List<KeyValuePair<string, string>> hand = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("ДЛИНА", "1250"),
                new KeyValuePair<string, string>("Типоразмер", "40х20х1,5")
            };
            Assert.AreEqual("ДЛИНА", CutListProperties.Find(hand, "LENGTH", CutListProperties.LengthSpellings), "вписанная руками длина");
        }

        /// <summary>Длины нет вовсе (так у NC3-7R.02.002 Кронштейн) — книга должна это заметить, а не выдумать.</summary>
        public static void Test_Missing_length_is_reported_as_missing()
        {
            List<KeyValuePair<string, string>> without = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("MATERIAL", "\"SW-Material@@@папка@файл.SLDPRT\""),
                new KeyValuePair<string, string>("QUANTITY", "\"QUANTITY@@@папка@файл.SLDPRT\"")
            };
            Assert.AreEqual("", CutListProperties.Find(without, "LENGTH", CutListProperties.LengthSpellings), "длины нет");
        }

        /// <summary>Количество тоже ищется по ссылке: у владельца папка называет его «КОЛИЧЕСТВО».</summary>
        public static void Test_Quantity_is_found_too()
        {
            Assert.AreEqual("QUANTITY", CutListProperties.Find(Russian(), "QUANTITY", CutListProperties.QuantitySpellings), "количество");
        }
    }
}
