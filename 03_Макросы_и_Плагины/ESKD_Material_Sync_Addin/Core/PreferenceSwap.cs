using System;
using System.Collections.Generic;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Временная подмена настроек пользователя SolidWorks на время выгрузки (сверка SW API 23.09.2026, №25): меняется только
    /// то, что отличается от нужного, и в конце возвращается только изменённое, в обратном порядке и один раз. Раньше PDF и
    /// IGES возвращали все настройки подряд — при сбое чтения посередине конструктор получал «false» и «0» вместо своих.
    /// Сбой чтения или отказ записи — в журнал, такая настройка не считается изменённой.
    /// </summary>
    public sealed class PreferenceSwap<T>
    {
        private readonly Func<int, T, bool> _set;
        private readonly string _label;
        private readonly List<KeyValuePair<int, T>> _changed = new List<KeyValuePair<int, T>>();

        /// <param name="set">Записать настройку; false — SolidWorks отказал.</param>
        /// <param name="label">Метка для журнала: «Выгрузка: восстановление настройки DXF».</param>
        public PreferenceSwap(Func<int, T> get, Func<int, T, bool> set, int[] ids, T[] wanted, string label)
        {
            _set = set;
            _label = label ?? "";
            for (int i = 0; i < ids.Length; i++)
            {
                try
                {
                    T before = get(ids[i]);
                    if (EqualityComparer<T>.Default.Equals(before, wanted[i])) continue;
                    if (set(ids[i], wanted[i])) _changed.Add(new KeyValuePair<int, T>(ids[i], before));
                }
                catch (Exception ex)
                {
                    Log.Error(_label + ": настройка " + ids[i], ex);
                }
            }
        }

        public void Restore()
        {
            for (int i = _changed.Count - 1; i >= 0; i--)
            {
                try
                {
                    _set(_changed[i].Key, _changed[i].Value);
                }
                catch (Exception ex)
                {
                    Log.Error(_label + ": настройка " + _changed[i].Key, ex);
                }
            }
            _changed.Clear();
        }
    }
}
