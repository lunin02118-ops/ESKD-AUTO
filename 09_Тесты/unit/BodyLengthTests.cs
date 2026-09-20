using System;
using System.Collections.Generic;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Длина заготовки по телу (решение владельца 21.09.2026): когда SolidWorks не дал длину в списке вырезов,
    /// книга меряет тело вдоль самого длинного прямого ребра и берёт большее из измеренного и габарита по осям.
    /// </summary>
    public static class BodyLengthTests
    {
        // GetCurveParams2: начало (x,y,z), конец (x,y,z), дальше параметры кривой — их здесь пять нулей
        private static double[] Params(double x1, double y1, double z1, double x2, double y2, double z2)
        {
            return new[] { x1, y1, z1, x2, y2, z2, 0, 0, 0, 0, 0 };
        }

        /// <summary>Наклонная труба 500 мм под 45°: по осям — 354, хорда ребра — 500. Ловушка, ради которой всё сделано.</summary>
        public static void Test_Chord_of_a_diagonal_edge_is_its_true_length()
        {
            double a = 0.5 / Math.Sqrt(2);
            Assert.AreEqual(0.5, Math.Round(BodyLength.Chord(Params(0, 0, 0, a, 0, a)), 6), "хорда наклонного ребра");
            double[] dir = BodyLength.Direction(Params(0, 0, 0, a, 0, a));
            Assert.AreEqual(1.0, Math.Round(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2], 9), "направление единичное");
            Assert.AreEqual(Math.Round(1 / Math.Sqrt(2), 6), Math.Round(dir[0], 6), "под 45° к оси X");
        }

        /// <summary>Рёбра спрашиваются о прямизне от самого длинного к короткому — обычно хватает одного вопроса.</summary>
        public static void Test_Edges_are_ordered_by_chord_longest_first()
        {
            int[] order = BodyLength.ByChordDescending(new List<double> { 0.04, 0.5, 0.02, 0.5, 0.125 });
            Assert.AreEqual(5, order.Length, "все рёбра на месте");
            Assert.IsTrue(order[0] == 1 || order[0] == 3, "первым — одно из двух длинных: " + order[0]);
            Assert.AreEqual(2, order[4], "последним — самое короткое");
        }

        /// <summary>Протяжённость вдоль оси по крайним точкам: косой рез не укорачивает заготовку.</summary>
        public static void Test_Extent_along_axis_covers_the_mitre_cut()
        {
            double[] dir = { 0, -1, 0 };
            // крайние точки тела вдоль -Y и +Y: от y=0 до y=-0.125 (та самая «Кронштейн», 125 мм)
            double along = BodyLength.Along(new[] { 0.0, -0.125, 0.0 }, new[] { 0.015, 0.0, 0.0075 }, dir);
            Assert.AreEqual(0.125, Math.Round(along, 6), "поперечное смещение крайних точек не считается");
        }

        /// <summary>Страховка владельца: из измеренного и габарита по осям в закуп уходит большее.</summary>
        public static void Test_Larger_of_measured_and_box_wins()
        {
            Assert.AreEqual(500.0, BodyLength.Choose(500.0, 354.0), "наклонная труба: измеренное длиннее габарита");
            Assert.AreEqual(600.0, BodyLength.Choose(120.0, 600.0), "ось нашлась не та — спасает габарит");
            Assert.AreEqual(600.0, BodyLength.Choose(double.NaN, 600.0), "мерить не по чему — габарит");
            Assert.AreEqual(0.0, BodyLength.Choose(double.NaN, double.NaN), "нет ничего — ноль, книга поставит «?»");
        }

        /// <summary>Пустые или вырожденные данные не роняют расчёт.</summary>
        public static void Test_Degenerate_input_is_harmless()
        {
            Assert.AreEqual(0.0, BodyLength.Chord(null), "нет параметров");
            Assert.AreEqual(0.0, BodyLength.Chord(new[] { 1.0, 2.0 }), "обрезанные параметры");
            Assert.IsTrue(BodyLength.Direction(Params(1, 1, 1, 1, 1, 1)) == null, "нулевое ребро без направления");
            Assert.AreEqual(0, BodyLength.ByChordDescending(null).Length, "нет рёбер");
            Assert.AreEqual(0.0, BodyLength.Along(null, null, null), "нет точек");
        }
    }
}
