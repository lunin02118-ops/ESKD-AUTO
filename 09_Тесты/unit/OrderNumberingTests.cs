using System;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Следующий свободный номер детали в децимальной системе изделия (ТЗ-02 Т-20).</summary>
    public static class OrderNumberingTests
    {
        public static void Test_Prefix_of_assembly_designation()
        {
            Assert.AreEqual("ТС-52.00.01", OrderNumbering.Prefix("ТС-52.00.01.000"), "группа подсборки");
            Assert.AreEqual("ТС-52.00.00", OrderNumbering.Prefix("ТС-52.00.00.000"), "группа главной сборки");
            Assert.AreEqual("", OrderNumbering.Prefix("ТС-52.00.01.004"), "деталь группой не бывает");
            Assert.AreEqual("", OrderNumbering.Prefix("Рама"), "без обозначения");
        }

        public static void Test_Next_free_number()
        {
            string[] neighbours =
            {
                "ТС-52.00.01.000 Царга.sldasm",
                "ТС-52.00.01.001 Стойка.sldprt",
                "ТС-52.00.01.002 Полка.sldprt",
                "ТС-52.00.02.001 Чужая группа.sldprt",
                "Заглушка без обозначения.sldprt"
            };
            Assert.AreEqual("ТС-52.00.01.003", OrderNumbering.Next("ТС-52.00.01.000", neighbours), "первый свободный");

            string[] withGap = { "ТС-52.00.01.001 Стойка.sldprt", "ТС-52.00.01.003 Полка.sldprt" };
            Assert.AreEqual("ТС-52.00.01.002", OrderNumbering.Next("ТС-52.00.01.000", withGap), "дырка в нумерации занимается");

            Assert.AreEqual("ТС-52.00.01.001", OrderNumbering.Next("ТС-52.00.01.000", new string[0]), "пустая группа");
            Assert.AreEqual("", OrderNumbering.Next("ТС-52.00.01.004", new string[0]), "от детали номер не строится");
        }

        public static void Test_Executions_do_not_free_the_number()
        {
            // «…003-01» — исполнение той же детали: номер 003 занят, следующей будет 004.
            string[] neighbours =
            {
                "ТС-52.00.01.003 Планка.sldprt",
                "ТС-52.00.01.003-01 Планка.sldprt",
                "ТС-52.00.01.003-02 Планка.sldprt"
            };
            Assert.AreEqual("ТС-52.00.01.001", OrderNumbering.Next("ТС-52.00.01.000", neighbours), "свободен первый номер");
            Assert.IsTrue(OrderNumbering.Taken("ТС-52.00.01", neighbours).Contains(3), "исполнение не освобождает номер");
        }
    }
}
