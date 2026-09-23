using System;
using System.Collections.Generic;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Материал детали по её телам (сверка SW API 23.09.2026, №33). Свой материал тела перекрывает материал детали, и
    /// массу SolidWorks считает по нему, а графа 3, «Материал_Строка», запись БЧ и проверка «материал не назначен» читали
    /// только материал детали. Отделено от SolidWorks ради юнит-тестов.
    /// </summary>
    public static class BodyMaterials
    {
        /// <summary>
        /// Фактический материал конфигурации cfg (ревью 23.09.2026). belongs — тела, которые видны (активной
        /// конфигурации), принадлежат cfg (<see cref="BodiesBelongTo"/>): тогда — как <see cref="ActualMaterial"/>. Иначе
        /// своё у cfg могут быть другие тела: разнобой не сообщается, а если материал не нашёлся, хотя материалы тел в
        /// детали в ходу (ownInActive), — known = false: «не узнать без переключения», а не «не назначен». Материалов тел
        /// нет вовсе — решает материал детали whole, и known = true.
        /// </summary>
        public static string Actual(IList<string> ownInCfg, IList<string> ownInActive, string whole, bool belongs, List<string> mixed,
            out bool known)
        {
            known = true;
            if (belongs) return ActualMaterial(ownInCfg, whole, mixed);
            string actual = ActualMaterial(ownInCfg, whole, null);
            if (!string.IsNullOrEmpty(actual)) return actual;
            foreach (string own in ownInActive ?? new string[0])
                if (!string.IsNullOrWhiteSpace(own)) known = false;
            return string.IsNullOrEmpty(actual) ? null : actual;
        }

        /// <summary>
        /// Тела, которые отдаёт GetBodies2, — тела активной конфигурации. По ним узнаётся материал тел только этой
        /// конфигурации и её технических производных («00&lt;Как сварено&gt;», развёртка «00SM-FLAT-PATTERN») — в обе
        /// стороны. У другого исполнения свои тела (ревью 23.09.2026): по чужим проверка писала «материал не назначен».
        /// </summary>
        public static bool BodiesBelongTo(string cfg, string active)
        {
            string c = (cfg ?? "").Trim();
            string a = (active ?? "").Trim();
            if (c.Length == 0 || a.Length == 0) return false;
            if (string.Equals(c, a, StringComparison.Ordinal)) return true;
            return string.Equals(CheckRules.TechnicalOwner(c), a, StringComparison.Ordinal) ||
                   string.Equals(CheckRules.TechnicalOwner(a), c, StringComparison.Ordinal);
        }

        /// <summary>
        /// Фактический материал: один на все тела (свой или, у тела без своего, материал детали) — он; тел нет или у всех
        /// своего нет — материал детали whole. Разные — whole, как раньше, а различающиеся материалы — в mixed («» — тела
        /// без материала): что писать в графу 3 у детали из разных материалов, решает владелец (MAT-15).
        /// </summary>
        /// <param name="own">Свой материал каждого тела, «» — своего нет.</param>
        public static string ActualMaterial(IList<string> own, string whole, List<string> mixed)
        {
            if (own == null || own.Count == 0) return whole;
            string part = (whole ?? "").Trim();
            List<string> distinct = new List<string>();
            foreach (string raw in own)
            {
                string name = string.IsNullOrWhiteSpace(raw) ? part : raw.Trim();
                if (!distinct.Contains(name)) distinct.Add(name);
            }
            if (distinct.Count == 1) return distinct[0].Length == 0 ? whole : distinct[0];
            if (mixed != null) mixed.AddRange(distinct);
            return whole;
        }
    }
}
