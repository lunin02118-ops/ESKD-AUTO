using System;
using System.Collections.Generic;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Графа «Формат» спецификации (З-1): формат чертежа по размеру его листов, запись — как у SWPlus.
    /// ГОСТ 2.301: основные А0…А4 и дополнительные кратные «А4х3» (короткая сторона — длинная сторона основного формата,
    /// длинная — кратна его короткой стороне). ГОСТ Р 2.106-2019: листы разных форматов — «*)» в графе, перечень в «Примечании».
    /// SpecEditor пишет «*)» и для одного кратного формата — графа узкая (FrmSpecEditor, Tests).
    /// </summary>
    public static class DrawingFormat
    {
        public const string Several = "*)";

        // Короткая и длинная стороны основных форматов, мм
        private static readonly string[] Names = { "А0", "А1", "А2", "А3", "А4" };
        private static readonly double[,] Sides = { { 841, 1189 }, { 594, 841 }, { 420, 594 }, { 297, 420 }, { 210, 297 } };
        private const double Tolerance = 2.0;

        /// <summary>Формат листа по размеру в мм в любой ориентации: «А3», «А4х3»; не формат ГОСТ — пусто.</summary>
        public static string FromSize(double widthMm, double heightMm)
        {
            double s = Math.Min(widthMm, heightMm), l = Math.Max(widthMm, heightMm);
            for (int i = 0; i < Names.Length; i++)
                if (Near(s, Sides[i, 0]) && Near(l, Sides[i, 1])) return Names[i];
            for (int i = 0; i < Names.Length; i++)
            {
                if (!Near(s, Sides[i, 1])) continue;
                double n = l / Sides[i, 0];
                int k = (int)Math.Round(n);
                if (k >= 2 && Near(l, k * Sides[i, 0])) return Names[i] + "х" + k;
            }
            return "";
        }

        /// <summary>
        /// Графа «Формат» и «Примечание» по форматам листов чертежа: один основной формат — он сам, примечание пустое;
        /// кратный или несколько разных — «*)» и «*) А4, А3» (по возрастанию листа, без повторов). Нет листов — пусто.
        /// </summary>
        public static string Column(IList<string> sheets, out string remark)
        {
            remark = "";
            List<string> distinct = new List<string>();
            foreach (string f in sheets ?? new string[0])
                if (!string.IsNullOrEmpty(f) && !distinct.Contains(f)) distinct.Add(f);
            if (distinct.Count == 0) return "";
            if (distinct.Count == 1 && distinct[0].Length <= 2) return distinct[0];
            distinct.Sort((a, b) => Order(a).CompareTo(Order(b)));
            remark = Several + " " + string.Join(", ", distinct.ToArray());
            return Several;
        }

        /// <summary>Порядок перечня — по возрастанию листа, как у SpecEditor: А4, А4х3, А3, …, А0.</summary>
        private static int Order(string format)
        {
            int i = Array.IndexOf(Names, format.Substring(0, Math.Min(2, format.Length)));
            int k = format.Length > 3 ? int.Parse(format.Substring(3)) : 1;
            return (Names.Length - i) * 100 + k;
        }

        /// <summary>Примечание можно заменить перечнем форматов: пустое или прежний перечень «*) …».</summary>
        public static bool RemarkIsFormatList(string remark)
        {
            string r = (remark ?? "").Trim();
            return r.Length == 0 || r.StartsWith(Several, StringComparison.Ordinal);
        }

        private static bool Near(double a, double b)
        {
            return Math.Abs(a - b) <= Tolerance;
        }
    }
}
