using System;
using System.Collections.Generic;

namespace ESKD.MaterialSync.Core
{
    /// <summary>План одного уровня свойств: канонический порядок и что для него перенести в конец списка.</summary>
    public sealed class OrderPlan
    {
        /// <summary>Имена уровня в едином порядке.</summary>
        public readonly List<string> Canonical;

        /// <summary>Имена, которые по одному уходят в конец списка (в этом порядке); остальные стоят на месте.</summary>
        public readonly List<string> Move;

        public OrderPlan(List<string> canonical, List<string> move)
        {
            Canonical = canonical;
            Move = move;
        }

        public bool InOrder { get { return Move.Count == 0; } }
    }

    /// <summary>
    /// Единый порядок пользовательских свойств (решение владельца 24.09.2026, сверка SW API №23: «порядок свойств должен быть
    /// всегда одинаковый универсальный»). Уровень — общие свойства или одно исполнение; у каждого уровня порядок наводится
    /// отдельно. Сначала известные имена в порядке мастер-списка, затем свойства конструктора — в том порядке, в каком он
    /// их завёл. Свойства не добавляются и не удаляются.
    /// </summary>
    public static class PropertyOrder
    {
        /// <summary>
        /// Мастер-список: 43 имени словаря SWPlus в порядке строк MyProperties_1.ini (роль, переименованная в ini, остаётся
        /// на своём месте), затем имена шаблона, надстройки, служебные SWPlus, прежние v5 и «поздние». Повтор не
        /// добавляется: первое вхождение побеждает.
        /// </summary>
        public static List<string> Master(PropertyDictionary dict)
        {
            List<string> master = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            IEnumerable<string>[] groups =
            {
                dict.Names, PropertyDictionary.TemplateNames, PropertyDictionary.AddinNames, PropertyDictionary.SwPlusServiceNames,
                PropertyDictionary.LegacyExtraNames, PropertyDictionary.LateNames
            };
            foreach (IEnumerable<string> group in groups)
                foreach (string name in group)
                    if (!string.IsNullOrEmpty(name) && seen.Add(name)) master.Add(name);
            return master;
        }

        /// <summary>
        /// Канонический порядок уровня и наименьший набор переносов в конец. SolidWorks умеет только дописать свойство в
        /// конец списка, поэтому на месте остаётся самое длинное начало канонического списка, которое уже стоит в текущем
        /// в том же порядке, а остальное по одному уходит в конец — это минимум переносов.
        /// </summary>
        public static OrderPlan Plan(IList<string> current, IList<string> master)
        {
            Dictionary<string, int> rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < master.Count; i++)
                if (master[i] != null && !rank.ContainsKey(master[i])) rank.Add(master[i], i);
            List<string> known = new List<string>(), own = new List<string>(), present = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in current)
            {
                if (name == null || !seen.Add(name)) continue;
                present.Add(name);
                if (rank.ContainsKey(name)) known.Add(name);
                else own.Add(name);
            }
            known.Sort(delegate(string a, string b) { return rank[a].CompareTo(rank[b]); });
            List<string> canonical = new List<string>(known);
            canonical.AddRange(own);
            int kept = 0;
            foreach (string name in present)
                if (kept < canonical.Count && string.Equals(name, canonical[kept], StringComparison.Ordinal)) kept++;
            return new OrderPlan(canonical, canonical.GetRange(kept, canonical.Count - kept));
        }
    }
}
