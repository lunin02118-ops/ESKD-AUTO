using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Графа «Формат» спецификации по листам чертежа (З-1).</summary>
    public static class DrawingFormatTests
    {
        public static void Test_Main_formats_in_both_orientations()
        {
            Assert.AreEqual("А4", DrawingFormat.FromSize(210, 297), "А4 книжный");
            Assert.AreEqual("А4", DrawingFormat.FromSize(297, 210), "А4 альбомный");
            Assert.AreEqual("А3", DrawingFormat.FromSize(420, 297), "А3");
            Assert.AreEqual("А2", DrawingFormat.FromSize(594, 420), "А2");
            Assert.AreEqual("А1", DrawingFormat.FromSize(841, 594), "А1");
            Assert.AreEqual("А0", DrawingFormat.FromSize(1189, 841), "А0");
            Assert.AreEqual("А3", DrawingFormat.FromSize(420.4, 296.9), "допуск на округление SolidWorks");
        }

        public static void Test_Multiple_formats_and_foreign_sizes()
        {
            Assert.AreEqual("А4х3", DrawingFormat.FromSize(630, 297), "А4х3");
            Assert.AreEqual("А3х3", DrawingFormat.FromSize(420, 891), "А3х3");
            Assert.AreEqual("А0х2", DrawingFormat.FromSize(1682, 1189), "А0х2");
            Assert.AreEqual("", DrawingFormat.FromSize(279.4, 215.9), "Letter — не ГОСТ");
            Assert.AreEqual("", DrawingFormat.FromSize(500, 500), "произвольный лист");
        }

        public static void Test_Column_follows_specification_rules()
        {
            string remark;
            Assert.AreEqual("А4", DrawingFormat.Column(new[] { "А4", "А4" }, out remark), "два листа А4");
            Assert.AreEqual("", remark, "примечание пустое");
            Assert.AreEqual("*)", DrawingFormat.Column(new[] { "А3", "А4", "А3" }, out remark), "разные форматы");
            Assert.AreEqual("*) А4, А3", remark, "перечень без повторов по возрастанию листа");
            DrawingFormat.Column(new[] { "А1", "А4х3", "А4" }, out remark);
            Assert.AreEqual("*) А4, А4х3, А1", remark, "кратный — после основного");
            Assert.AreEqual("*)", DrawingFormat.Column(new[] { "А4х3" }, out remark), "кратный — как SpecEditor");
            Assert.AreEqual("*) А4х3", remark, "кратный — в примечании");
            Assert.AreEqual("", DrawingFormat.Column(new string[0], out remark), "нет листов");
        }

        public static void Test_Remark_is_replaced_only_when_it_holds_format_list()
        {
            Assert.IsTrue(DrawingFormat.RemarkIsFormatList(""), "пусто");
            Assert.IsTrue(DrawingFormat.RemarkIsFormatList(null), "нет свойства");
            Assert.IsTrue(DrawingFormat.RemarkIsFormatList("*) А3, А4"), "прежний перечень");
            Assert.IsFalse(DrawingFormat.RemarkIsFormatList("Покупное"), "текст конструктора");
        }
    }
}
