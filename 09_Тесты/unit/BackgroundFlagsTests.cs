using System;
using System.Collections.Generic;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Признак для опроса кнопки (сверка SW API 23.09.2026, №1): SolidWorks зовёт enable-метод на каждую перерисовку, а
    /// «выдан ли документ» — это чтение отчётов выдачи с NAS. Раньше он читался в потоке SolidWorks раз в 2 с, а переход
    /// между двумя окнами сбрасывал запомненный ответ. Теперь опрос отвечает из памяти, а пересчёт идёт в фоне.
    /// Планировщик здесь складывает работу в список, часы ручные — поток и время под контролем теста.
    /// </summary>
    public static class BackgroundFlagsTests
    {
        private sealed class Stand
        {
            public readonly List<Action> Queued = new List<Action>();
            public readonly Dictionary<string, bool> Answers = new Dictionary<string, bool>();
            public int Computed;
            public bool Throw;
            public DateTime Now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
            public readonly BackgroundFlags Flags;

            public Stand()
            {
                Flags = new BackgroundFlags(Compute, TimeSpan.FromSeconds(10), work => Queued.Add(work), () => Now);
            }

            private bool Compute(string key)
            {
                Computed++;
                if (Throw) throw new System.IO.IOException("сеть недоступна");
                bool value;
                return Answers.TryGetValue(key, out value) && value;
            }

            /// <summary>Выполнить всё, что поставлено в фон, — как поток пула.</summary>
            public void RunQueued()
            {
                List<Action> work = new List<Action>(Queued);
                Queued.Clear();
                foreach (Action action in work) action();
            }
        }

        public static void Test_Get_never_computes_in_caller()
        {
            Stand s = new Stand();
            s.Answers["d|чертёж"] = true;
            bool known;
            Assert.IsFalse(s.Flags.Get("d|чертёж", out known), "пока не посчитано — «нет»: кнопка серая");
            Assert.IsFalse(known, "ответ ещё неизвестен");
            Assert.AreEqual(0, s.Computed, "в потоке опроса ничего не считается");
            Assert.AreEqual(1, s.Queued.Count, "пересчёт поставлен в фон");
            s.Flags.Get("d|чертёж", out known);
            Assert.AreEqual(1, s.Queued.Count, "повторный опрос второй пересчёт не ставит");
        }

        public static void Test_Get_after_refresh_returns_value_without_recompute()
        {
            Stand s = new Stand();
            s.Answers["d|чертёж"] = true;
            bool known;
            s.Flags.Get("d|чертёж", out known);
            s.RunQueued();
            Assert.AreEqual(1, s.Computed, "посчитано один раз — в фоне");
            s.Now = s.Now.AddSeconds(9);
            Assert.IsTrue(s.Flags.Get("d|чертёж", out known), "ответ из памяти");
            Assert.IsTrue(known, "ответ известен");
            Assert.AreEqual(0, s.Queued.Count, "свежий ответ не пересчитывается");
        }

        public static void Test_Stale_value_is_kept_and_refreshed_once()
        {
            Stand s = new Stand();
            s.Answers["d|чертёж"] = true;
            bool known;
            s.Flags.Get("d|чертёж", out known);
            s.RunQueued();
            s.Now = s.Now.AddSeconds(11);
            s.Answers["d|чертёж"] = false;
            Assert.IsTrue(s.Flags.Get("d|чертёж", out known), "устаревший ответ отдаётся, пока фон считает новый");
            Assert.IsTrue(known, "прежний ответ известен");
            s.Flags.Get("d|чертёж", out known);
            Assert.AreEqual(1, s.Queued.Count, "два опроса — один пересчёт");
            s.RunQueued();
            Assert.IsFalse(s.Flags.Get("d|чертёж", out known), "после пересчёта — новый ответ");
            Assert.AreEqual(2, s.Computed, "всего два расчёта");
        }

        public static void Test_Switching_between_two_documents_does_not_recompute()
        {
            // Раньше помнился ответ только для одного документа: два открытых окна — чтение NAS на каждом переходе.
            Stand s = new Stand();
            s.Answers["d|первый"] = true;
            bool known;
            s.Flags.Get("d|первый", out known);
            s.Flags.Get("m|второй", out known);
            s.RunQueued();
            for (int i = 0; i < 5; i++)
            {
                Assert.IsTrue(s.Flags.Get("d|первый", out known), "первый выдан");
                Assert.IsFalse(s.Flags.Get("m|второй", out known), "второй не выдан");
                Assert.IsTrue(known, "оба ответа известны");
            }
            Assert.AreEqual(2, s.Computed, "по одному расчёту на документ");
            Assert.AreEqual(0, s.Queued.Count, "переходы ничего не пересчитывают");
        }

        public static void Test_Failed_refresh_is_not_retried_before_fresh_time()
        {
            // Недоступная сеть: исключение в потоке пула уронило бы SolidWorks — его ловят, а опрос не долбит NAS.
            Stand s = new Stand();
            s.Throw = true;
            bool known;
            s.Flags.Get("d|чертёж", out known);
            s.RunQueued();
            Assert.IsFalse(s.Flags.Get("d|чертёж", out known), "ответа нет — «нет»");
            Assert.IsFalse(known, "ответ неизвестен");
            Assert.AreEqual(0, s.Queued.Count, "до истечения 10 с повтора нет");
            s.Now = s.Now.AddSeconds(11);
            s.Throw = false;
            s.Answers["d|чертёж"] = true;
            s.Flags.Get("d|чертёж", out known);
            s.RunQueued();
            Assert.IsTrue(s.Flags.Get("d|чертёж", out known), "сеть вернулась — ответ есть");
            Assert.IsTrue(known, "и он известен");
        }

        public static void Test_Forget_drops_late_result()
        {
            // «Готово к производству» только что выдало изделие: опоздавший расчёт «не выдан» не должен лечь поверх.
            Stand s = new Stand();
            bool known;
            s.Flags.Get("d|чертёж", out known);
            s.Flags.Forget();
            s.RunQueued();
            s.Flags.Get("d|чертёж", out known);
            Assert.IsFalse(known, "опоздавший расчёт не записан");
            Assert.AreEqual(1, s.Queued.Count, "после сброса — новый пересчёт");
            s.Answers["d|чертёж"] = true;
            s.RunQueued();
            Assert.IsTrue(s.Flags.Get("d|чертёж", out known), "новый ответ — «выдан»");
        }

        public static void Test_Scheduler_failure_does_not_block_next_poll()
        {
            Stand s = new Stand();
            int calls = 0;
            BackgroundFlags flags = new BackgroundFlags(key => { calls++; return true; }, TimeSpan.FromSeconds(10),
                work => { throw new InvalidOperationException("пул недоступен"); }, () => s.Now);
            bool known;
            Assert.IsFalse(flags.Get("d|чертёж", out known), "не запустилось — «нет»");
            Assert.AreEqual(0, calls, "и в потоке опроса не считалось");
            flags.Get("d|чертёж", out known);
            Assert.IsFalse(known, "опрос не падает и не зависает на «считается»");
        }
    }
}
