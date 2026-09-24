using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Единый порядок пользовательских свойств (решение владельца 24.09.2026, сверка SW API №23).</summary>
    public static class PropertyOrderTests
    {
        private static readonly List<string> Master = PropertyOrder.Master(PropertyDictionary.Default());
        private static readonly List<string> Tail = PropertyOrder.Tail(PropertyDictionary.Default());

        public static void Test_Master_groups_in_order()
        {
            Assert.AreEqual(93, Master.Count, "43 имени словаря, 2 шаблона, 4 надстройки, 19 служебных SWPlus, 21 прежнее v5, 4 поздних");
            Assert.AreEqual(Master.Count, new HashSet<string>(Master, StringComparer.Ordinal).Count, "без повторов");
            Assert.AreEqual("Обозначение", Master[0], "первое — обозначение");
            Assert.AreEqual("Количество", Master[42], "последняя роль словаря");
            Assert.AreEqual("Масса|Материал|Материал_Строка|Формат_до_БЧ|Примечание_до_БЧ|Запись_БЧ|Number",
                string.Join("|", Master.Skip(43).Take(7).ToArray()), "шаблон, надстройка, служебные SWPlus");
            Assert.AreEqual("SORTAMENT|Разраб.", string.Join("|", Master.Skip(67).Take(2).ToArray()), "служебные SWPlus, затем v5");
            Assert.AreEqual("Операции|Ревизия|Габарит|ЕСКД_Принято", string.Join("|", Master.Skip(89).ToArray()),
                "поздние имена — последние среди известных");
        }

        public static void Test_Master_follows_renamed_role()
        {
            // Роль, переименованная в словаре SWPlus, остаётся на своём месте — порядок задаёт строка ini, а не имя.
            string ini = Path.Combine(Path.GetTempPath(), "eskd_order_" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                string[] lines = (string[])PropertyDictionary.DefaultNames.Clone();
                lines[0] = "Децимальный_номер";
                File.WriteAllLines(ini, lines.Concat(new[] { "", "", "", "", "1", " ", "1" }).ToArray(), Encoding.GetEncoding(1251));
                List<string> master = PropertyOrder.Master(PropertyDictionary.Load(ini));
                Assert.AreEqual("Децимальный_номер", master[0], "переименованная роль 0");
                Assert.IsFalse(master.Contains("Обозначение"), "прежнее имя роли — уже своё свойство");
                Assert.AreEqual("Сборка1_ФБ", master[1], "дальше — как в словаре");
            }
            finally
            {
                File.Delete(ini);
            }
        }

        public static void Test_Tail_is_mprop_apply_order()
        {
            // «Применить» MProp удаляет «Примечание», «Формат», «Раздел» и дописывает их в конец в этом порядке
            // (FrmMProp 3021–3053) — единый порядок ставит их туда же, и MProp с надстройкой не переставляют их друг
            // за другом (решение владельца 24.09.2026).
            Assert.AreEqual("Примечание|Формат|Раздел", string.Join("|", Tail.ToArray()));
        }

        public static void Test_Tail_follows_renamed_role()
        {
            // Хвост — роли словаря, как у MProp (prpRemark, prpFormat, prpSection): переименованная в ini роль уходит в
            // конец под новым именем.
            string ini = Path.Combine(Path.GetTempPath(), "eskd_order_" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                string[] lines = (string[])PropertyDictionary.DefaultNames.Clone();
                lines[6] = "Формат_листа";
                File.WriteAllLines(ini, lines.Concat(new[] { "", "", "", "", "1", " ", "1" }).ToArray(), Encoding.GetEncoding(1251));
                Assert.AreEqual("Примечание|Формат_листа|Раздел", string.Join("|", PropertyOrder.Tail(PropertyDictionary.Load(ini)).ToArray()));
            }
            finally
            {
                File.Delete(ini);
            }
        }

        public static void Test_Plan_stable_after_mprop_apply()
        {
            // «Применить» MProp на уровне в едином порядке: три последних удалены и дописаны заново — порядок тот же,
            // следующему сохранению переносить нечего.
            foreach (string[][] c in Cases())
            {
                List<string> canonical = PropertyOrder.Plan(c[0], Master, Tail).Canonical;
                List<string> applied = canonical.Where(n => !Tail.Contains(n)).ToList();
                applied.AddRange(Tail.Where(canonical.Contains));
                Assert.IsTrue(PropertyOrder.Plan(applied, Master, Tail).InOrder, "после MProp порядок прежний: " + string.Join("|", c[0]));
                Assert.AreEqual(string.Join("|", canonical.ToArray()), string.Join("|", applied.ToArray()), "MProp не переставил: " + string.Join("|", c[0]));
            }
        }

        public static void Test_Plan_cases()
        {
            int count = 0;
            foreach (string[][] c in Cases())
            {
                count++;
                OrderPlan plan = PropertyOrder.Plan(c[0], Master, Tail);
                string what = "случай " + count + " «" + string.Join("|", c[0]) + "»";
                Assert.AreEqual(string.Join("|", c[1]), string.Join("|", plan.Canonical.ToArray()), what + ": канон");
                Assert.AreEqual(string.Join("|", c[2]), string.Join("|", plan.Move.ToArray()), what + ": переносы");
                Assert.AreEqual(string.Join("|", c[1]), string.Join("|", Simulate(c[0], plan.Move).ToArray()), what + ": переносы дают канон");
            }
            Assert.IsTrue(count >= 10, "случаи прочитаны: " + count);
        }

        public static void Test_Plan_idempotent()
        {
            foreach (string[][] c in Cases())
            {
                OrderPlan plan = PropertyOrder.Plan(c[0], Master, Tail);
                OrderPlan again = PropertyOrder.Plan(plan.Canonical, Master, Tail);
                Assert.IsTrue(again.InOrder, "канон уже в порядке: " + string.Join("|", c[0]));
                Assert.AreEqual(string.Join("|", plan.Canonical.ToArray()), string.Join("|", again.Canonical.ToArray()), "канон канона");
            }
        }

        public static void Test_Plan_minimal_by_brute_force()
        {
            // Все перестановки шести имён — свои и известные вперемешку. Переносы плана дают канон, и их не больше, чем
            // у кратчайшего пути поиском в ширину по ходу «любое свойство — в конец».
            string[] names = { "Моё_1", "Формат", "Обозначение", "Моё_2", "Масса_ФБ", "Операции" };
            int checkedCount = 0;
            foreach (List<string> perm in Permutations(names.ToList()))
            {
                OrderPlan plan = PropertyOrder.Plan(perm, Master, Tail);
                Assert.AreEqual(string.Join("|", plan.Canonical.ToArray()), string.Join("|", Simulate(perm.ToArray(), plan.Move).ToArray()),
                    "переносы дают канон: " + string.Join("|", perm.ToArray()));
                Assert.AreEqual(Shortest(perm, plan.Canonical), plan.Move.Count, "наименьшее число переносов: " + string.Join("|", perm.ToArray()));
                checkedCount++;
            }
            Assert.AreEqual(720, checkedCount, "все перестановки");
        }

        private static List<string> Simulate(IList<string> current, IList<string> moves)
        {
            List<string> list = new List<string>(current);
            foreach (string name in moves)
            {
                list.Remove(name);
                list.Add(name);
            }
            return list;
        }

        private static int Shortest(List<string> start, List<string> goal)
        {
            string target = string.Join("|", goal.ToArray());
            Dictionary<string, int> seen = new Dictionary<string, int>();
            Queue<List<string>> queue = new Queue<List<string>>();
            seen[string.Join("|", start.ToArray())] = 0;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                List<string> cur = queue.Dequeue();
                string key = string.Join("|", cur.ToArray());
                int depth = seen[key];
                if (key == target) return depth;
                foreach (string name in cur)
                {
                    List<string> next = Simulate(cur, new[] { name });
                    string nextKey = string.Join("|", next.ToArray());
                    if (seen.ContainsKey(nextKey)) continue;
                    seen[nextKey] = depth + 1;
                    queue.Enqueue(next);
                }
            }
            throw new AssertionException("канон недостижим: " + target);
        }

        private static IEnumerable<List<string>> Permutations(List<string> items)
        {
            if (items.Count <= 1)
            {
                yield return new List<string>(items);
                yield break;
            }
            for (int i = 0; i < items.Count; i++)
            {
                List<string> rest = new List<string>(items);
                rest.RemoveAt(i);
                foreach (List<string> tail in Permutations(rest))
                {
                    tail.Insert(0, items[i]);
                    yield return tail;
                }
            }
        }

        /// <summary>Случаи из 09_Тесты/unit/data/property_order_cases.txt: current, canonical, move.</summary>
        private static List<string[][]> Cases()
        {
            string path = Path.Combine(DictionaryTests.RepoRoot(), "09_Тесты", "unit", "data", "property_order_cases.txt");
            List<string[][]> cases = new List<string[][]>();
            string[] current = null, canonical = null;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.StartsWith("current=", StringComparison.Ordinal)) current = Split(line.Substring(8));
                else if (line.StartsWith("canonical=", StringComparison.Ordinal)) canonical = Split(line.Substring(10));
                else if (line.StartsWith("move=", StringComparison.Ordinal))
                {
                    cases.Add(new[] { current, canonical, Split(line.Substring(5)) });
                    current = canonical = null;
                }
            }
            return cases;
        }

        private static string[] Split(string value)
        {
            return value.Length == 0 ? new string[0] : value.Split('|');
        }
    }
}
