// Минимальный раннер юнит-тестов для C# 5 без внешних библиотек.
// Тест — public static void метод с именем Test_… в классе, чьё имя заканчивается на Tests.
// Вывод в формате TAP: "ok N имя" / "not ok N имя # сообщение". Код выхода — число провалов.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace ESKD.Tests
{
    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    public static class Assert
    {
        public static void AreEqual<T>(T expected, T actual, string what = "")
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertionException(string.Format("{0}: ожидалось «{1}», получено «{2}»", what, Show(expected), Show(actual)));
        }

        public static void IsTrue(bool condition, string what)
        {
            if (!condition) throw new AssertionException("условие не выполнено: " + what);
        }

        public static void IsFalse(bool condition, string what)
        {
            if (condition) throw new AssertionException("условие должно быть ложным: " + what);
        }

        public static void IsNull(object value, string what)
        {
            if (value != null) throw new AssertionException(what + ": ожидался null, получено «" + value + "»");
        }

        public static void NotNull(object value, string what)
        {
            if (value == null) throw new AssertionException(what + ": получен null");
        }

        private static string Show(object o)
        {
            return o == null ? "<null>" : o.ToString().Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    public static class UnitRunner
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            string filter = args.Length > 0 ? args[0] : "";
            List<MethodInfo> tests = new List<MethodInfo>();
            foreach (Type type in typeof(UnitRunner).Assembly.GetTypes())
            {
                if (!type.Name.EndsWith("Tests", StringComparison.Ordinal)) continue;
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name.StartsWith("Test_", StringComparison.Ordinal) && m.GetParameters().Length == 0 &&
                        (filter.Length == 0 || (type.Name + "." + m.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                        tests.Add(m);
                }
            }
            tests.Sort(delegate(MethodInfo a, MethodInfo b)
            {
                return string.CompareOrdinal(a.DeclaringType.Name + "." + a.Name, b.DeclaringType.Name + "." + b.Name);
            });
            Console.WriteLine("1.." + tests.Count);
            int failed = 0;
            for (int i = 0; i < tests.Count; i++)
            {
                MethodInfo m = tests[i];
                string name = m.DeclaringType.Name + "." + m.Name;
                try
                {
                    m.Invoke(null, null);
                    Console.WriteLine("ok " + (i + 1) + " " + name);
                }
                catch (TargetInvocationException ex)
                {
                    failed++;
                    Exception inner = ex.InnerException ?? ex;
                    string message = inner is AssertionException ? inner.Message : inner.GetType().Name + ": " + inner.Message;
                    Console.WriteLine("not ok " + (i + 1) + " " + name + " # " + message.Replace("\n", " "));
                }
            }
            return failed;
        }
    }
}
