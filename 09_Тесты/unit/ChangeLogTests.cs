using System;
using System.Collections.Generic;
using System.IO;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Журнал изменений «Изменения.xlsx» и коды причин (ТЗ-02 Т-12, Т-49, Т-51).</summary>
    public static class ChangeLogTests
    {
        private static string Temp()
        {
            string folder = Path.Combine(Path.GetTempPath(), "eskd_changes_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static void Test_Reasons_are_eight_with_codes()
        {
            Assert.AreEqual(8, ChangeReasons.All.Length, "восемь причин приложения А");
            Assert.AreEqual("4", ChangeReasons.ByCode("4").Code, "причина по коду");
            Assert.AreEqual("устранение ошибок", ChangeReasons.ByCode("4").Text, "текст причины");
            Assert.AreEqual("прочие", ChangeReasons.ByCode("нет такого").Text, "неизвестный код не теряет строку");
            Assert.AreEqual("4 — устранение ошибок", ChangeReasons.ByCode("4").ToString(), "вид в списке окна");
        }

        public static void Test_Concurrent_appends_keep_every_row()
        {
            // Два конструктора дописывают журнал одновременно: без метки-блокировки последняя запись затирала строку первой.
            string folder = Temp();
            try
            {
                string path = ChangeLog.Path(folder);
                int[] numbers = new int[6];
                System.Threading.Thread[] threads = new System.Threading.Thread[numbers.Length];
                for (int i = 0; i < threads.Length; i++)
                {
                    int k = i;
                    threads[i] = new System.Threading.Thread(() => numbers[k] = ChangeLog.Append(path, new ChangeRow
                    {
                        Revision = 1, Date = new DateTime(2026, 9, 19), Who = "Поток " + k, Document = "Д" + k + ".slddrw",
                        What = "проверка", Reason = "прочие", Code = "8", Backlog = "использовать"
                    }));
                    threads[i].Start();
                }
                foreach (System.Threading.Thread thread in threads) thread.Join();
                Array.Sort(numbers);
                Assert.AreEqual("1,2,3,4,5,6", string.Join(",", numbers), "каждая запись получила свой номер");
                Assert.AreEqual(6, ChangeLog.Rows(path).Count, "ни одна строка не потеряна");
                Assert.IsFalse(File.Exists(path + ".lock"), "метка занятости снята");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        public static void Test_Journal_is_created_with_header()
        {
            string folder = Temp();
            try
            {
                string path = ChangeLog.Path(folder);
                Assert.AreEqual(0, ChangeLog.LastNumber(path), "журнала ещё нет");
                int number = ChangeLog.Append(path, new ChangeRow
                {
                    Revision = 1,
                    Date = new DateTime(2026, 9, 18),
                    Who = "Лунин В.И.",
                    Document = "ТС-52.00.01.004 Заглушка.slddrw",
                    What = "толщина 2 → 3 мм",
                    Reason = "устранение ошибок",
                    Code = "4",
                    Backlog = "доработать"
                });
                Assert.AreEqual(1, number, "первая строка журнала");
                Assert.IsTrue(File.Exists(path), "журнал заведён");

                List<Dictionary<string, string>> rows = ChangeLog.Rows(path);
                Assert.AreEqual(1, rows.Count, "одна строка");
                Assert.AreEqual("1", rows[0]["Ревизия"], "ревизия");
                Assert.AreEqual("18.09.2026", rows[0]["Дата"], "дата в русском виде");
                Assert.AreEqual("Лунин В.И.", rows[0]["Кто"], "кто");
                Assert.AreEqual("толщина 2 → 3 мм", rows[0]["Что изменено"], "что изменено");
                Assert.AreEqual("4", rows[0]["Код по ГОСТ 2.503"], "код причины");
                Assert.AreEqual("доработать", rows[0]["Задел"], "задел");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        public static void Test_Numbers_continue_and_nothing_is_lost()
        {
            string folder = Temp();
            try
            {
                string path = ChangeLog.Path(folder);
                for (int i = 1; i <= 3; i++)
                    Assert.AreEqual(i, ChangeLog.Append(path, new ChangeRow { Revision = i, Document = "Ч" + i }), "номер строки " + i);
                Assert.AreEqual(3, ChangeLog.LastNumber(path), "последний номер");
                List<Dictionary<string, string>> rows = ChangeLog.Rows(path);
                Assert.AreEqual(3, rows.Count, "три строки");
                Assert.AreEqual("Ч3", rows[2]["Документ"], "последняя строка — последняя записанная");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
