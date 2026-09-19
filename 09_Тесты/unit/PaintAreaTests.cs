using System;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Окрашиваемая площадь — только наружная поверхность (З-7).</summary>
    public static class PaintAreaTests
    {
        private static void Near(double expected, double actual, string what)
        {
            Assert.IsTrue(Math.Abs(expected - actual) < 1e-6, what + ": ожидалось " + expected + ", получено " + actual);
        }

        public static void Test_Rectangular_tube_counts_outer_wall_only()
        {
            // 50×25×1,5: наружный периметр 150, внутренний 138.
            Near(150.0 / 288.0, PaintArea.OuterShare("Труба 50х25х1,5 ГОСТ 8645-68 / 08пс ГОСТ 13663-86"), "прямоугольная");
            Near(150.0 / 288.0, PaintArea.OuterShare("Труба 50х25х1.5 ГОСТ 8645-68"), "точка вместо запятой");
            Near(80.0 / 150.4, PaintArea.OuterShare("Труба 20х20х1,2 ГОСТ 8639-82 / 08пс ГОСТ 13663-86"), "квадратная");
        }

        public static void Test_Round_and_oval_tubes()
        {
            Near(16.0 / 29.0, PaintArea.OuterShare("Труба 16х1,5 ГОСТ 10704-91 / 08пс ГОСТ 10705-80"), "круглая D×t");
            double outer = 2 * 15 + Math.PI * 15, inner = 2 * 15 + Math.PI * (15 - 2.4);
            Near(outer / (outer + inner), PaintArea.OuterShare("Труба ПО 30х15х1,2 ГОСТ 8644-68 / 08пс ГОСТ 13663-86"), "плоскоовальная");
        }

        public static void Test_Sheet_and_unknown_material_keep_full_area()
        {
            Assert.AreEqual(1.0, PaintArea.OuterShare("Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97"), "лист красится с обеих сторон");
            Assert.AreEqual(1.0, PaintArea.OuterShare(""), "материал не указан");
            Assert.AreEqual(1.0, PaintArea.OuterShare(null), "материал null");
            Assert.AreEqual(1.0, PaintArea.OuterShare("Труба 10х6 ГОСТ"), "стенка больше радиуса — формула не применима");
        }

        public static void Test_Outer_area_scales_total_and_keeps_unknown()
        {
            // Лежак NC3-7R: полная площадь 0,0994 м² из профиля 50×25×1,5 — наружной ≈ 0,0518 м².
            Near(0.0994 * 150.0 / 288.0, PaintArea.Outer(0.0994, "Труба 50х25х1,5 ГОСТ 8645-68"), "лежак");
            Assert.IsTrue(double.IsNaN(PaintArea.Outer(double.NaN, "Труба 50х25х1,5")), "неизвестная площадь остаётся неизвестной");
        }
    }
}
