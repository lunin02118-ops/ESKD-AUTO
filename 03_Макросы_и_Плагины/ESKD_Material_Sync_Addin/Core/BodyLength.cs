using System;
using System.Collections.Generic;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Длина заготовки проката по самому телу — когда SolidWorks её не посчитал (решение владельца 21.09.2026).
    ///
    /// Свойство длины в списке вырезов пропадает, стоит доработать тело элемента конструкции: отрезать плоскостью,
    /// приварить бобышку. Габарит по осям детали тут не годится — наклонная труба 500 мм под 45° даёт по осям
    /// 354 мм, и в закуп уйдёт короткий хлыст. Направление берётся из самой детали: у прямого проката самое
    /// длинное прямое ребро идёт вдоль оси, и при косых резах тоже. Путь элемента конструкции для этого не
    /// читается: он требует AccessSelections, а это откат модели.
    ///
    /// Здесь — арифметика без SolidWorks, проверяемая юнит-тестами; обход рёбер и тел — в Sw/LzkService.cs.
    /// </summary>
    public static class BodyLength
    {
        /// <summary>Хорда ребра по его концам (GetCurveParams2: первые три числа — начало, следующие три — конец).</summary>
        public static double Chord(double[] curveParams)
        {
            double[] d = Delta(curveParams);
            return d == null ? 0 : Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        }

        /// <summary>Единичное направление ребра по его концам; null — концы совпадают или данных нет.</summary>
        public static double[] Direction(double[] curveParams)
        {
            double[] d = Delta(curveParams);
            if (d == null) return null;
            double n = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            if (n <= 1e-12) return null;
            return new[] { d[0] / n, d[1] / n, d[2] / n };
        }

        /// <summary>
        /// Порядок, в котором рёбра стоит спрашивать о прямизне: от самой длинной хорды к короткой. Вопрос
        /// «прямое ли» — отдельный вызов SolidWorks на ребро; по хордам он нужен обычно один, победителю.
        /// </summary>
        public static int[] ByChordDescending(IList<double> chords)
        {
            int[] order = new int[chords == null ? 0 : chords.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, delegate (int a, int b) { return chords[b].CompareTo(chords[a]); });
            return order;
        }

        /// <summary>Протяжённость тела вдоль направления по двум крайним точкам (в обе стороны).</summary>
        public static double Along(double[] extremeForward, double[] extremeBackward, double[] direction)
        {
            if (extremeForward == null || extremeBackward == null || direction == null) return 0;
            double s = 0;
            for (int i = 0; i < 3; i++) s += (extremeForward[i] - extremeBackward[i]) * direction[i];
            return Math.Abs(s);
        }

        /// <summary>
        /// Что записать в книгу: измеренное вдоль оси или наибольший габарит по осям — что больше. Ось могла
        /// определиться неверно; тогда в закуп уйдёт большее число, а не меньшее (страховка владельца).
        /// </summary>
        public static double Choose(double measuredMm, double boxMaxMm)
        {
            double a = double.IsNaN(measuredMm) ? 0 : measuredMm;
            double b = double.IsNaN(boxMaxMm) ? 0 : boxMaxMm;
            return Math.Max(a, b);
        }

        private static double[] Delta(double[] p)
        {
            if (p == null || p.Length < 6) return null;
            return new[] { p[3] - p[0], p[4] - p[1], p[5] - p[2] };
        }
    }
}
