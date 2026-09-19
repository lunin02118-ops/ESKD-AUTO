using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Один хлыст раскроя: что из него режут и что остаётся.</summary>
    public sealed class CutBar
    {
        public readonly List<double> Pieces = new List<double>();
        /// <summary>Сумма заготовок и резов, мм.</summary>
        public double Used;
        /// <summary>Остаток хлыста после последнего реза, мм.</summary>
        public double Rest;
        /// <summary>Остаток годится в дело: он не короче норматива «Труба.Деловой».</summary>
        public bool Business;
    }

    /// <summary>Итог раскроя одного сортамента.</summary>
    public sealed class CutResult
    {
        public string Sortament = "";
        public readonly List<CutBar> Bars = new List<CutBar>();
        /// <summary>Чистый расход — сумма длин заготовок, мм.</summary>
        public double CleanMm;
        /// <summary>Длина закупаемого проката: хлысты × длина хлыста, мм.</summary>
        public double BoughtMm;
        /// <summary>Деловые обрезки (≥ норматива), мм.</summary>
        public readonly List<double> BusinessRests = new List<double>();
        /// <summary>Отход: короткие остатки, торцовка и пропилы, мм.</summary>
        public double WasteMm;
        /// <summary>Заготовки длиннее хлыста — их не раскроить, они уходят в замечания.</summary>
        public readonly List<double> TooLong = new List<double>();

        public int BarCount { get { return Bars.Count; } }

        /// <summary>Коэффициент использования материала: чистый расход к закупленной длине.</summary>
        public double Kim { get { return BoughtMm > 0 ? CleanMm / BoughtMm : 0; } }

        public string KimText
        {
            get { return (Kim * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"; }
        }
    }

    /// <summary>
    /// Раскрой хлыстов на заготовки (ТЗ-02 Т-13). Задача одномерная и решается жадно «сначала длинные»:
    /// цеху нужен понятный план, который можно проверить глазами, а не идеальная укладка. Нормативы —
    /// из справочника: длина хлыста, захват станка (эту часть хлыста не режут), торцовка одного конца,
    /// ширина пропила и нижняя граница делового обрезка.
    /// </summary>
    public static class CutPlan
    {
        public static CutResult Plan(string sortament, IEnumerable<double> pieces, Norms norms)
        {
            if (norms == null) throw new ArgumentNullException("norms");
            double bar = norms.Number("Труба.Хлыст");
            double grip = norms.Number("Труба.Захват");
            double face = norms.Number("Труба.Торцовка");
            double kerf = norms.Number("Труба.Рез");
            double business = norms.Number("Труба.Деловой");
            return Plan(sortament, pieces, bar, grip, face, kerf, business);
        }

        public static CutResult Plan(string sortament, IEnumerable<double> pieces, double barMm, double gripMm,
            double faceMm, double kerfMm, double businessMm)
        {
            CutResult result = new CutResult { Sortament = sortament ?? "" };
            // Зона реза: хлыст без захвата станка и без торцовки одного конца. Второй конец уходит в захват
            // и не торцуется (решение владельца 19.09.2026). Так же считает формула «Шт. из хлыста» в книге ЛЗК,
            // иначе «оптимально N хл.» расходился бы с таблицей.
            double zone = Math.Max(0, barMm - gripMm);
            double usable = zone - faceMm;
            // Одинаковые длины раскладываются пачкой: «сначала длинные» даёт тот же план, что и по одной штуке,
            // но тираж в тысячи изделий не превращается в миллиарды сравнений в потоке SolidWorks.
            foreach (IGrouping<double, double> group in (pieces ?? new double[0]).Where(p => p > 0)
                .GroupBy(p => p).OrderByDescending(g => g.Key))
            {
                double piece = group.Key, step = piece + kerfMm;
                int left = group.Count();
                if (step > usable)
                {
                    for (int i = 0; i < left; i++) result.TooLong.Add(piece);
                    continue;
                }
                result.CleanMm += piece * left;
                foreach (CutBar bar in result.Bars)
                {
                    if (left == 0) break;
                    left -= Put(bar, piece, step, Math.Min(left, (int)Math.Floor(bar.Rest / step)));
                }
                while (left > 0)
                {
                    CutBar bar = new CutBar { Used = faceMm, Rest = usable };
                    result.Bars.Add(bar);
                    left -= Put(bar, piece, step, Math.Min(left, (int)Math.Floor(bar.Rest / step)));
                }
            }
            foreach (CutBar b in result.Bars)
            {
                b.Business = b.Rest >= businessMm && businessMm > 0;
                if (b.Business) result.BusinessRests.Add(b.Rest);
            }
            result.BoughtMm = result.Bars.Count * barMm;
            // Отход — всё, что не стало ни заготовкой, ни деловым обрезком: захват, торцовка, пропилы, короткие хвосты.
            result.WasteMm = Math.Max(0, result.BoughtMm - result.CleanMm - result.BusinessRests.Sum());
            return result;
        }

        private static int Put(CutBar bar, double piece, double step, int count)
        {
            if (count <= 0) return 0;
            for (int i = 0; i < count; i++) bar.Pieces.Add(piece);
            bar.Used += step * count;
            bar.Rest -= step * count;
            return count;
        }

        /// <summary>Листовой прокат (Т-13): площадь заготовок с коэффициентом отхода → целые листы формата.</summary>
        public static int Sheets(double areaM2, Norms norms, out double sheetAreaM2, out double kim)
        {
            double width, length;
            norms.Pair("Лист.Формат", out width, out length);
            double waste = norms.Number("Лист.Отход");
            sheetAreaM2 = width * length / 1000000.0;
            kim = 0;
            if (sheetAreaM2 <= 0 || areaM2 <= 0) return 0;
            if (waste <= 0) waste = 1;
            int sheets = (int)Math.Ceiling(areaM2 * waste / sheetAreaM2);
            kim = areaM2 / (sheets * sheetAreaM2);
            return sheets;
        }

        /// <summary>Краска (Т-13): площадь × норма × (1 + потери) — килограммы и число вёдер тары.</summary>
        public static double Paint(double areaM2, Norms norms, out int cans)
        {
            double rate = norms.Number("Краска.Норма");        // г/м²
            double losses = norms.Number("Краска.Потери");     // %
            double can = norms.Number("Краска.Тара");          // кг
            double kilograms = areaM2 * rate / 1000.0 * (1 + losses / 100.0);
            cans = can > 0 ? (int)Math.Ceiling(kilograms / can) : 0;
            return kilograms;
        }
    }
}
