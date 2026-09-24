using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Обход дерева модели, который переживает фоновую работу SolidWorks. Открыв окно документа (ActivateDoc3 — развёртка,
    /// IGS, ось трубы), SolidWorks ещё 0,5–4 с перестраивает в фоне данные элементов, и обход дерева любого открытого
    /// документа в это время натыкается на удалённый элемент: имя пустое, GetTypeName2 отвечает RPC_E_SERVERFAULT, место
    /// каждый раз другое (проба 24.09.2026, прогоны r41–r44). Выгрузка принимала такой сбой за «деталь не листовая» и
    /// «не труба»: при повторной выгрузке DXF развёртки молча не делался (e2e X24, L18). Обход повторяется целиком после
    /// паузы — список элементов в это время менялся. Пауза без обработки сообщений помогала всегда: окно сбоев не
    /// дольше 4 с, запас — до 8 с.
    /// </summary>
    public static class FeatureWalk
    {
        private const int ServerFault = unchecked((int)0x80010105);
        public const int PauseMs = 250;
        public const int Attempts = 32;

        /// <summary>
        /// Выполнить обход (только чтение!) и при RPC_E_SERVERFAULT повторить его целиком после паузы. Другие ошибки и
        /// сбой, не прошедший за 8 с, уходят вызывающему, как раньше.
        /// </summary>
        public static T Retry<T>(Func<T> walk, string what, int pauseMs = PauseMs)
        {
            Stopwatch watch = null;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    T result = walk();
                    if (watch != null)
                        Log.Info("Обход дерева «" + what + "»: SolidWorks был занят фоновой перестройкой, повторов " + (attempt - 1) +
                            ", " + watch.ElapsedMilliseconds + " мс");
                    return result;
                }
                catch (COMException ex)
                {
                    if (ex.ErrorCode != ServerFault || attempt >= Attempts) throw;
                    if (watch == null) watch = Stopwatch.StartNew();
                    Thread.Sleep(pauseMs);
                }
            }
        }
    }
}
