using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Размер развёртки в ведомости ЛЗК (сверка документации 24.09.2026). Своя модель меряется по развёрнутому телу:
    /// свойства граничной рамки списка вырезов SolidWorks пересчитывает только при его обновлении. Развёртка не
    /// перестроилась — замера нет, и свойства могли остаться от прежней геометрии: такой размер — «оценка» (решение
    /// ревью 23.09.2026). Раньше он шёл в книгу как точный.
    /// </summary>
    public static class FlatSizeTests
    {
        private static readonly double[] Properties = { 200, 100, 3 };
        private static readonly double[] Measured = { 210, 100, 3 };

        public static void Test_Measured_size_wins()
        {
            bool estimate;
            double[] size = FlatSize.Choose(Properties, Measured, true, out estimate);
            Assert.AreEqual(210.0, size[0], "замер по телу");
            Assert.IsFalse(estimate, "замер — не оценка");
        }

        public static void Test_Own_model_not_measured_is_estimate()
        {
            bool estimate;
            double[] size = FlatSize.Choose(Properties, null, true, out estimate);
            Assert.AreEqual(200.0, size[0], "свойства списка вырезов");
            Assert.IsTrue(estimate, "своя модель не измерилась — свойства могли устареть: «оценка»");
        }

        public static void Test_Foreign_model_takes_properties_as_is()
        {
            bool estimate;
            double[] size = FlatSize.Choose(Properties, null, false, out estimate);
            Assert.AreEqual(200.0, size[0], "свойства списка вырезов");
            Assert.IsFalse(estimate, "база, другой заказ: развёртку не включают, свойства — как есть");
        }

        public static void Test_Nothing_gives_null()
        {
            bool estimate;
            Assert.IsNull(FlatSize.Choose(null, null, true, out estimate), "ни замера, ни свойств");
            Assert.IsFalse(estimate, "размера нет — пометку ставит вызывающий (габарит согнутой детали)");
        }
    }
}
