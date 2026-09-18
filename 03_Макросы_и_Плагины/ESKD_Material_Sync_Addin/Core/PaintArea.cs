using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Окрашиваемая площадь — только наружная поверхность (З-7, решение владельца 18.09.2026): внутренняя поверхность
    /// труб не красится; места сварки и касания, пластик и покупные внутри сборки — пренебречь.
    ///
    /// SolidWorks даёт полную площадь тела. У трубы это наружная + внутренняя стенки (+ торцы), поэтому полная
    /// площадь умножается на долю наружного периметра, посчитанную по сечению из сортамента «Материал_Строка»:
    /// прямоугольная a×b×t — 2(a+b) против 2(a+b)−8t; плоскоовальная — 2(a−b)+πb против 2(a−b)+π(b−2t);
    /// круглая D×t — πD против π(D−2t). Торцы и кромки отверстий делятся в той же пропорции — погрешность
    /// меньше процента. Лист, пруток и прочее — площадь как есть (красятся обе стороны).
    /// </summary>
    public static class PaintArea
    {
        private const string Num = @"(\d+(?:[.,]\d+)?)";
        private const string By = @"\s*[хx×*]\s*";
        private static readonly Regex Tube = new Regex(@"^\s*Труба\s+(ПО\s+)?" + Num + By + Num + "(?:" + By + Num + ")?",
            RegexOptions.IgnoreCase);

        /// <summary>Доля наружной поверхности в полной площади детали из этого материала (1 — не труба).</summary>
        public static double OuterShare(string material)
        {
            Match m = Tube.Match(material ?? "");
            if (!m.Success) return 1.0;
            bool oval = m.Groups[1].Success;
            double first = Parse(m.Groups[2].Value), second = Parse(m.Groups[3].Value);
            double outer, inner;
            if (m.Groups[4].Success)
            {
                double a = Math.Max(first, second), b = Math.Min(first, second), t = Parse(m.Groups[4].Value);
                if (oval)
                {
                    outer = 2 * (a - b) + Math.PI * b;
                    inner = 2 * (a - b) + Math.PI * (b - 2 * t);
                }
                else
                {
                    outer = 2 * (a + b);
                    inner = 2 * (a + b) - 8 * t;
                }
            }
            else
            {
                // Круглая «Труба D×t»: периметры пропорциональны диаметрам.
                outer = first;
                inner = first - 2 * second;
            }
            if (outer <= 0 || inner <= 0) return 1.0;
            return outer / (outer + inner);
        }

        /// <summary>Наружная площадь детали, м², по полной площади из SolidWorks; NaN остаётся NaN.</summary>
        public static double Outer(double totalM2, string material)
        {
            if (double.IsNaN(totalM2)) return totalM2;
            return totalM2 * OuterShare(material);
        }

        private static double Parse(string value)
        {
            return double.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);
        }
    }
}
