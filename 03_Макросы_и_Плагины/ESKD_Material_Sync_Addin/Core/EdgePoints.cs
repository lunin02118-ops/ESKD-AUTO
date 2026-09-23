using System;
using System.Collections.Generic;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Точки ребра развёрнутого тела для граничной рамки (L15, сверка 23.09.2026). Концы ребра SolidWorks отдаёт верно
    /// (GetCurveParams2), а кривая ребра у развёрнутого тела считается от другого начала: прямая 0…161 мм по параметрам
    /// ребра вычислялась как −101…60, и гнутый уголок 100+60 выходил 262 мм. Поэтому точки кривой проверяются по концам:
    /// берётся первый способ, чьи крайние точки сходятся с концами ребра; не сошёлся ни один — только концы.
    ///
    /// Здесь — выбор без SolidWorks, проверяемый юнит-тестами; сами способы — в Sw/LzkService.cs.
    /// </summary>
    public static class EdgePoints
    {
        /// <summary>Допуск совпадения с концом ребра, м: 0,01 мм.</summary>
        public const double Tolerance = 1e-5;

        /// <summary>Число шагов по кривому ребру длиной lengthMm: через 0,5 мм, от 8 до 2000.</summary>
        public static int Steps(double lengthMm)
        {
            if (double.IsNaN(lengthMm) || double.IsInfinity(lengthMm)) return 8;
            return (int)Math.Max(8, Math.Min(2000, Math.Ceiling(lengthMm / 0.5)));
        }

        /// <summary>
        /// Точки первого способа, чьи крайние точки — концы ребра (в любом порядке); ни один не сошёлся — концы ребра.
        /// Способы спрашиваются по очереди: каждый — вызовы SolidWorks. null от способа — не сошёлся.
        /// </summary>
        public static List<double[]> Choose(double[] start, double[] end, params Func<List<double[]>>[] ways)
        {
            if (!Point(start) || !Point(end)) return new List<double[]>();
            if (ways != null)
                foreach (Func<List<double[]>> way in ways)
                {
                    List<double[]> points = way == null ? null : way();
                    if (Fits(points, start, end)) return points;
                }
            return new List<double[]> { start, end };
        }

        private static bool Fits(List<double[]> points, double[] start, double[] end)
        {
            if (points == null || points.Count < 2) return false;
            foreach (double[] p in points)
                if (!Point(p)) return false;
            double[] first = points[0], last = points[points.Count - 1];
            return Near(first, start) && Near(last, end) || Near(first, end) && Near(last, start);
        }

        private static bool Point(double[] p)
        {
            if (p == null || p.Length < 3) return false;
            for (int i = 0; i < 3; i++)
                if (double.IsNaN(p[i]) || double.IsInfinity(p[i])) return false;
            return true;
        }

        private static bool Near(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return dx * dx + dy * dy + dz * dz <= Tolerance * Tolerance;
        }
    }
}
