using System;
using System.Reflection;

namespace ESKD.Tests
{
    /// <summary>
    /// Подстановка сборок рядом с надстройкой (AssemblyResolve): все .NET-надстройки SolidWorks живут в одном AppDomain,
    /// и обработчик отвечал на любое имя любой сборке — чужой надстройке подсовывал наш файл (сверка SW API 23.09.2026, №8).
    /// </summary>
    public static class AddinResolveTests
    {
        private const string Swconst = "SolidWorks.Interop.swconst, Version=26.1.0.0, Culture=neutral, PublicKeyToken=19f43e188e4269d8";

        private static Assembly Resolve(string name, Assembly requesting)
        {
            MethodInfo m = typeof(ESKD.MaterialSync.SwAddin).GetMethod("ResolveNextToAddin", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m, "обработчик AssemblyResolve");
            return (Assembly)m.Invoke(null, new object[] { null, new ResolveEventArgs(name, requesting) });
        }

        public static void Test_Foreign_requester_gets_nothing()
        {
            // mscorlib лежит не в каталоге надстройки — это «чужая» сборка.
            Assert.IsNull(Resolve(Swconst, typeof(object).Assembly), "интероп чужой сборке не подсовывается");
        }

        public static void Test_Own_requester_gets_its_copy()
        {
            Assert.NotNull(Resolve(Swconst, typeof(ESKD.MaterialSync.SwAddin).Assembly), "своя сборка получает свою копию интеропа");
            Assert.NotNull(Resolve(Swconst, null), "запрос без сборки (CLR для RCW) — тоже");
        }

        public static void Test_Unknown_name_gets_nothing()
        {
            Assert.IsNull(Resolve("ESKD.Tests", typeof(ESKD.MaterialSync.SwAddin).Assembly), "не своя зависимость — не отвечаем");
        }
    }
}
