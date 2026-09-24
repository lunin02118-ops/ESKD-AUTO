using System;
using System.Collections.Generic;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Точки рёбер развёртки для граничной рамки (L15, сверка 23.09.2026): у развёрнутого тела SolidWorks 2025 отдаёт
    /// концы ребра верно, а кривая ребра считается от другого начала — прямая 0…161 мм по параметрам ребра
    /// вычислялась как −101…60, и уголок 100+60 выходил 262 мм. Точки берутся только те, что сходятся с концами ребра.
    /// </summary>
    public static class EdgePointsTests
    {
        private static readonly double[] Start = { 0, 0, 0.08 };
        private static readonly double[] End = { 0.16107, 0, 0.08 };

        private static List<double[]> Line(double from, double to, int steps)
        {
            List<double[]> points = new List<double[]>();
            for (int k = 0; k <= steps; k++) points.Add(new[] { from + (to - from) * k / steps, 0, 0.08 });
            return points;
        }

        public static void Test_Shifted_curve_is_rejected()
        {
            List<double[]> points = EdgePoints.Choose(Start, End, () => Line(-0.10107, 0.06, 4), () => Line(0, 0.16107, 4));
            Assert.AreEqual(5, points.Count, "взяты точки второго способа");
            Assert.AreEqual(0.16107, Math.Round(points[4][0], 6), "по концам ребра, а не по сдвинутой кривой");
        }

        public static void Test_Reversed_points_are_accepted()
        {
            List<double[]> back = Line(0.16107, 0, 3);
            List<double[]> points = EdgePoints.Choose(Start, End, () => back);
            Assert.AreEqual(4, points.Count, "обход от конца к началу — те же точки");
        }

        public static void Test_Nothing_fits_gives_the_ends()
        {
            int called = 0;
            List<double[]> points = EdgePoints.Choose(Start, End,
                () => { called++; return Line(-0.10107, 0.06, 4); }, () => { called++; return null; },
                () => { called++; return new List<double[]> { Start }; });
            Assert.AreEqual(3, called, "спрошены все способы");
            Assert.AreEqual(2, points.Count, "остались концы ребра");
            Assert.AreEqual(0.0, points[0][0], "начало");
            Assert.AreEqual(0.16107, points[1][0], "конец");
        }

        public static void Test_First_fitting_way_stops_the_search()
        {
            int called = 0;
            EdgePoints.Choose(Start, End, () => { called++; return Line(0, 0.16107, 2); },
                () => { called++; return Line(0, 0.16107, 2); });
            Assert.AreEqual(1, called, "второй способ не спрашивается — лишний вызов SolidWorks");
        }

        public static void Test_Closed_edge_fits_at_its_seam()
        {
            double[] seam = { 0.075, 0, 0 };
            List<double[]> circle = new List<double[]>();
            for (int k = 0; k <= 8; k++)
                circle.Add(new[] { 0.075 * Math.Cos(k * Math.PI / 4), 0.075 * Math.Sin(k * Math.PI / 4), 0 });
            Assert.AreEqual(9, EdgePoints.Choose(seam, seam, () => circle).Count, "окружность замыкается в начале");
        }

        public static void Test_Tolerance_is_hundredth_of_millimetre()
        {
            List<double[]> near = Line(0.000005, 0.16107, 2);
            Assert.AreEqual(3, EdgePoints.Choose(Start, End, () => near).Count, "0,005 мм — сходится");
            List<double[]> far = Line(0.00005, 0.16107, 2);
            Assert.AreEqual(2, EdgePoints.Choose(Start, End, () => far).Count, "0,05 мм — нет, только концы");
        }

        public static void Test_Steps_by_length()
        {
            Assert.AreEqual(8, EdgePoints.Steps(1.0), "короткая дуга — не меньше 8 точек");
            Assert.AreEqual(471, EdgePoints.Steps(235.5), "через 0,5 мм");
            Assert.AreEqual(2000, EdgePoints.Steps(5000.0), "не больше 2000");
            Assert.AreEqual(8, EdgePoints.Steps(double.NaN), "длина не известна — 8");
        }

        public static void Test_Degenerate_input_gives_what_is_known()
        {
            Assert.AreEqual(0, EdgePoints.Choose(null, null).Count, "концов нет");
            Assert.AreEqual(2, EdgePoints.Choose(Start, End).Count, "способов нет — концы");
        }
    }
}
