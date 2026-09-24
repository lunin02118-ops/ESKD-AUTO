using System;
using System.Collections.Generic;
using System.Threading;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Признак по ключу для опроса кнопок. SolidWorks зовёт enable-метод на каждую перерисовку, и отвечать он должен из
    /// памяти (сверка SW API 23.09.2026, №1): раньше «выдан ли документ» читался с NAS в потоке SolidWorks раз в 2 с, а
    /// переход между двумя окнами каждый раз сбрасывал запомненный ответ. <see cref="Get"/> сам не считает: неизвестный или
    /// устаревший ответ пересчитывается в фоне. compute — только файлы, без SolidWorks: его COM-объекты живут в своём потоке.
    /// </summary>
    public sealed class BackgroundFlags
    {
        private const int Capacity = 256;

        private sealed class Entry
        {
            public bool Known;
            public bool Value;
            public DateTime At = DateTime.MinValue;
            public bool Pending;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, bool> _compute;
        private readonly TimeSpan _fresh;
        private readonly Action<Action> _schedule;
        private readonly Func<DateTime> _clock;
        private int _generation;

        /// <param name="compute">Расчёт признака; выполняется в фоне.</param>
        /// <param name="fresh">Сколько ответ считается свежим.</param>
        /// <param name="schedule">Запуск работы в фоне (<see cref="OnThreadPool"/>; в тестах — список).</param>
        /// <param name="clock">Часы (UTC).</param>
        public BackgroundFlags(Func<string, bool> compute, TimeSpan fresh, Action<Action> schedule, Func<DateTime> clock)
        {
            _compute = compute;
            _fresh = fresh;
            _schedule = schedule;
            _clock = clock;
        }

        public static void OnThreadPool(Action work)
        {
            ThreadPool.QueueUserWorkItem(delegate { work(); });
        }

        /// <summary>
        /// Ответ из памяти; known — он уже посчитан. Пока не посчитан — false. Устаревший ответ отдаётся, пока фон считает
        /// новый; на ключ в фоне идёт не больше одного расчёта.
        /// </summary>
        public bool Get(string key, out bool known)
        {
            key = key ?? "";
            bool start = false;
            bool value;
            int generation;
            lock (_sync)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry))
                {
                    if (_entries.Count >= Capacity) _entries.Clear();
                    entry = new Entry();
                    _entries[key] = entry;
                }
                if (!entry.Pending && (entry.At == DateTime.MinValue || _clock() - entry.At >= _fresh))
                {
                    entry.Pending = true;
                    start = true;
                }
                known = entry.Known;
                value = entry.Value;
                generation = _generation;
            }
            if (start)
            {
                try
                {
                    _schedule(delegate { Refresh(key, generation); });
                }
                catch (Exception ex)
                {
                    Log.Error("Признак в фоне: запуск расчёта «" + key + "»", ex);
                    lock (_sync)
                    {
                        Entry entry;
                        if (_entries.TryGetValue(key, out entry)) entry.Pending = false;
                    }
                }
            }
            return value;
        }

        /// <summary>Забыть все ответы: изделие выдано или заказ закрыт. Расчёт, начатый до этого, свой ответ не запишет.</summary>
        public void Forget()
        {
            lock (_sync)
            {
                _entries.Clear();
                _generation++;
            }
        }

        private void Refresh(string key, int generation)
        {
            // Весь расчёт — в try: необработанное исключение в потоке пула роняет SolidWorks.
            bool value = false, ok = false;
            try
            {
                value = _compute(key);
                ok = true;
            }
            catch (Exception ex)
            {
                Log.Error("Признак в фоне «" + key + "»", ex);
            }
            try
            {
                lock (_sync)
                {
                    Entry entry;
                    if (generation != _generation || !_entries.TryGetValue(key, out entry)) return;
                    entry.Pending = false;
                    // Отметка и при ошибке: недоступную сеть опрос не долбит чаще, чем раз в _fresh.
                    entry.At = _clock();
                    if (ok)
                    {
                        entry.Known = true;
                        entry.Value = value;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Признак в фоне «" + key + "»: запись ответа", ex);
            }
        }
    }
}
