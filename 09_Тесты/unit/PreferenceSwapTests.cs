using System;
using System.Collections.Generic;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Временная подмена настроек SolidWorks на время выгрузки (сверка SW API 23.09.2026, №25): PDF и IGES возвращали все
    /// настройки подряд, и при сбое чтения посередине конструктор получал «false» и «0» вместо своих.
    /// </summary>
    public static class PreferenceSwapTests
    {
        private sealed class Store<T>
        {
            public readonly Dictionary<int, T> Values = new Dictionary<int, T>();
            public readonly List<int> Writes = new List<int>();
            public int BrokenRead = -1;
            public int RefusedWrite = -1;

            public T Get(int id)
            {
                if (id == BrokenRead) throw new InvalidOperationException("настройка не читается");
                return Values[id];
            }

            public bool Set(int id, T value)
            {
                Writes.Add(id);
                if (id == RefusedWrite) return false;
                Values[id] = value;
                return true;
            }
        }

        public static void Test_Only_different_settings_are_changed_and_restored()
        {
            Store<bool> store = new Store<bool>();
            store.Values[1] = true;
            store.Values[2] = false;
            PreferenceSwap<bool> swap = new PreferenceSwap<bool>(store.Get, store.Set, new[] { 1, 2 }, new[] { true, true }, "тест");
            Assert.AreEqual("2", string.Join(",", store.Writes), "записана только отличающаяся");
            Assert.IsTrue(store.Values[2], "на время выгрузки — нужное значение");
            swap.Restore();
            Assert.IsFalse(store.Values[2], "после — прежнее");
            Assert.AreEqual("2,2", string.Join(",", store.Writes), "совпадавшая не записывалась ни разу");
        }

        public static void Test_Unread_setting_is_not_restored()
        {
            Store<int> store = new Store<int>();
            store.Values[1] = 5;
            store.Values[2] = 7;
            store.Values[3] = 9;
            store.BrokenRead = 2;
            PreferenceSwap<int> swap = new PreferenceSwap<int>(store.Get, store.Set, new[] { 1, 2, 3 }, new[] { 0, 0, 0 }, "тест");
            swap.Restore();
            Assert.AreEqual(5, store.Values[1], "первая возвращена");
            Assert.AreEqual(7, store.Values[2], "непрочитанная не затёрта нулём");
            Assert.AreEqual(9, store.Values[3], "после сбоя остальные тоже подменены и возвращены");
            Assert.IsFalse(store.Writes.Contains(2), "непрочитанная не записывалась");
        }

        public static void Test_Refused_write_is_not_restored()
        {
            Store<int> store = new Store<int>();
            store.Values[1] = 5;
            store.RefusedWrite = 1;
            PreferenceSwap<int> swap = new PreferenceSwap<int>(store.Get, store.Set, new[] { 1 }, new[] { 0 }, "тест");
            swap.Restore();
            Assert.AreEqual("1", string.Join(",", store.Writes), "отказанная запись не возвращается");
        }

        public static void Test_Restore_twice_writes_once()
        {
            Store<int> store = new Store<int>();
            store.Values[1] = 5;
            PreferenceSwap<int> swap = new PreferenceSwap<int>(store.Get, store.Set, new[] { 1 }, new[] { 0 }, "тест");
            swap.Restore();
            swap.Restore();
            Assert.AreEqual("1,1", string.Join(",", store.Writes), "подмена и одно возвращение");
        }
    }
}
