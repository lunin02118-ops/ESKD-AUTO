using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Единый порядок пользовательских свойств в файле (решение владельца 24.09.2026, сверка SW API №23; правило —
    /// Core.PropertyOrder). Наводится только внутри сохранения, которое запустил конструктор: Ctrl+S, пересохранение после
    /// «Сохранить как», «Применить и сохранить» в окне «Проверить изделие». Порядок сам по себе никогда не повод сохранить
    /// или пометить документ: при открытии, в простое, по кнопке «Синхронизировать» и при сохранениях кнопок его нет.
    /// </summary>
    public static class PropertyOrderService
    {
        /// <summary>
        /// Сколько времени одно сохранение тратит на порядок, мс. Не уложились — остальные уровни ждут следующего
        /// сохранения: у библиотечной детали из сотни исполнений старый файл приходит в порядок за несколько сохранений.
        /// </summary>
        public const int OrderBudgetMs = 2000;

        /// <summary>
        /// Уровни, где порядок наводить нельзя (связь с родителем, таблица параметров, непроверенный тип), — по пути, уровню
        /// и составу свойств. Пока состав тот же, отказ не перепроверяется: у библиотечной детали с таблицей параметров
        /// иначе каждое сохранение тратило бы время на сотню одинаковых проверок. Живёт до перезапуска SolidWorks.
        /// </summary>
        private static readonly Dictionary<string, string> Refused = new Dictionary<string, string>(StringComparer.Ordinal);
        private const int RefusedLimit = 5000;

        /// <summary>
        /// Почему в этом документе порядок не наводится: папка покупных, только для чтения, файл другого заказа (скрытая
        /// деталь, сохранённая вместе со сборкой по команде в чужом заказе). Пусто — можно.
        /// </summary>
        public static string WhyNot(ModelDoc2 doc, string path, string orderFor)
        {
            if (ProductLocator.IsPurchasedFolder(path)) return "покупное изделие (папка покупных)";
            try
            {
                if (doc.IsOpenedReadOnly()) return "документ открыт только для чтения";
            }
            catch (COMException ex)
            {
                Log.Error("Порядок свойств: только для чтения " + path, ex);
                return "не удалось узнать, открыт ли документ только для чтения";
            }
            if (DocumentGuard.FileReadOnly(path)) return "файл только для чтения";
            if (!SamePlace(path, orderFor)) return "файл другого заказа (сохранение — из «" + orderFor + "»)";
            return "";
        }

        /// <summary>Тот же заказ, что у документа, по чьей команде идёт сохранение; два файла вне заказов — одно место.</summary>
        internal static bool SamePlace(string path, string by)
        {
            if (string.IsNullOrEmpty(by) || string.Equals(path, by, StringComparison.OrdinalIgnoreCase)) return true;
            string order = ProductLocator.OrderOf(path), other = ProductLocator.OrderOf(by);
            if (order.Length == 0 && other.Length == 0) return true;
            return ProductLocator.SameOrder(order, other);
        }

        /// <summary>
        /// Навести порядок на всех уровнях документа: общие, активное исполнение, остальные. Уровень, где любое переносимое
        /// свойство связано, управляется таблицей параметров или непроверенного типа, пропускается целиком. Возвращает
        /// число перенесённых свойств; в режиме DryRun только считает.
        /// </summary>
        public static int Restore(PropertyWriter w, PropertyDictionary dict, string orderFor)
        {
            Stopwatch clock = Stopwatch.StartNew();
            ModelDoc2 doc = w.Document;
            string path = DocInfo.PathOf(doc);
            string title = DocInfo.TitleOf(doc);
            List<string> master = PropertyOrder.Master(dict);
            List<string> tail = PropertyOrder.Tail(dict);
            List<string> levels = new List<string> { "" };
            string active = w.ActiveConfigurationName();
            if (active.Length > 0) levels.Add(active);
            foreach (string cfg in w.ConfigurationNames())
                if (!levels.Contains(cfg)) levels.Add(cfg);

            string refusal = null, firstSkip = "";
            int arranged = 0, moved = 0, postponed = 0, skipped = 0;
            for (int i = 0; i < levels.Count; i++)
            {
                if (clock.ElapsedMilliseconds > OrderBudgetMs)
                {
                    postponed = levels.Count - i;
                    break;
                }
                string level = levels[i];
                string shown = level.Length == 0 ? "общие" : level;
                List<string> names = w.OrderedNames(level);
                if (names == null) continue;
                OrderPlan plan = PropertyOrder.Plan(names, master, tail);
                if (plan.InOrder) continue;
                // Отказ по документу — лениво, при первом уровне вне порядка: у документа в порядке он не нужен.
                if (refusal == null) refusal = WhyNot(doc, path, orderFor);
                if (refusal.Length > 0)
                {
                    Log.Info("Порядок свойств «" + title + "» не наводится: " + refusal);
                    return 0;
                }
                string key = path.ToLowerInvariant() + "\0" + level + "\0" + string.Join("\n", names.ToArray());
                string why;
                if (!Refused.TryGetValue(key, out why))
                {
                    why = "";
                    foreach (string name in plan.Move)
                    {
                        why = w.MoveRefusal(level, name);
                        if (why.Length > 0) break;
                    }
                    if (why.Length > 0)
                    {
                        if (Refused.Count >= RefusedLimit) Refused.Clear();
                        Refused[key] = why;
                    }
                }
                if (why.Length > 0)
                {
                    // Одна строка на сохранение, а не на уровень: у библиотечной детали с таблицей параметров их сотня.
                    if (skipped++ == 0) firstSkip = "[" + shown + "] " + why;
                    continue;
                }
                arranged++;
                if (w.DryRun)
                {
                    moved += plan.Move.Count;
                    continue;
                }
                foreach (string name in plan.Move)
                {
                    if (!w.MoveToEnd(level, name)) break;
                    moved++;
                }
                List<string> after = w.OrderedNames(level);
                if (after == null || !string.Join("\n", after.ToArray()).Equals(string.Join("\n", plan.Canonical.ToArray()), StringComparison.Ordinal))
                    Log.Warn("Порядок свойств «" + title + "» [" + shown + "] не совпал с единым: " +
                        (after == null ? "не прочитан" : string.Join(", ", after.ToArray())));
            }
            if (skipped > 0)
                Log.Info("Порядок свойств «" + title + "»: уровней пропущено " + skipped + ", первый — " + firstSkip);
            if (arranged > 0 || postponed > 0)
            {
                string text = "порядок свойств — уровней " + arranged + ", переносов " + moved +
                    (postponed > 0 ? ", не проверено уровней " + postponed + " — при следующем сохранении" : "");
                w.Note(text);
                if (postponed > 0)
                    Log.Info("Порядок свойств «" + title + "»: не уложились в " + OrderBudgetMs + " мс, не проверено уровней " + postponed +
                        " — при следующем сохранении");
            }
            return moved;
        }
    }
}
