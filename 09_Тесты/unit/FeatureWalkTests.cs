using System;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Sw;

namespace ESKD.Tests
{
    /// <summary>
    /// Обход дерева после открытия окна документа (проба 24.09.2026, r41–r44): SolidWorks 0,5–4 с перестраивает дерево в
    /// фоне, и GetTypeName2 отвечает RPC_E_SERVERFAULT в любом документе. Выгрузка принимала это за «не листовая» и молча
    /// не делала DXF (e2e X24). FeatureWalk.Retry повторяет обход целиком только при этом коде.
    /// </summary>
    public static class FeatureWalkTests
    {
        private const int ServerFault = unchecked((int)0x80010105);

        public static void Test_Walk_is_repeated_while_solidworks_rebuilds_in_background()
        {
            int calls = 0;
            double mm = FeatureWalk.Retry(() =>
            {
                calls++;
                if (calls < 4) throw new COMException("Ошибка на сервере.", ServerFault);
                return 6.0;
            }, "толщина листа", 1);
            Assert.AreEqual(6.0, mm, "обход, прошедший после фоновой перестройки, даёт толщину");
            Assert.AreEqual(4, calls, "три сбоя — три повтора");
        }

        public static void Test_Other_com_errors_are_not_repeated()
        {
            int calls = 0;
            COMException error = null;
            try
            {
                FeatureWalk.Retry<bool>(() =>
                {
                    calls++;
                    throw new COMException("Нет интерфейса.", unchecked((int)0x80004002));
                }, "признак профиля", 1);
            }
            catch (COMException ex)
            {
                error = ex;
            }
            Assert.IsTrue(error != null, "другая ошибка уходит вызывающему, как раньше");
            Assert.AreEqual(1, calls, "и без повторов");
        }

        public static void Test_Persistent_server_fault_gives_up()
        {
            int calls = 0;
            COMException error = null;
            try
            {
                FeatureWalk.Retry<bool>(() =>
                {
                    calls++;
                    throw new COMException("Ошибка на сервере.", ServerFault);
                }, "список вырезов", 1);
            }
            catch (COMException ex)
            {
                error = ex;
            }
            Assert.IsTrue(error != null, "сбой, не прошедший за отведённое время, уходит вызывающему");
            Assert.AreEqual(FeatureWalk.Attempts, calls, "попыток не больше заданного");
        }
    }
}
