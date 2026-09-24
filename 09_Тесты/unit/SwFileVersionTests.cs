using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Версия файла по истории сохранений (сверка SW API 23.09.2026, №26; решение владельца 24.09.2026 — меняется только
    /// текст сообщения). Строки — как их отдал SolidWorks 2025 на фикстурах (проба 23.09.2026).
    /// </summary>
    public static class SwFileVersionTests
    {
        private static readonly string[] Current = { "1399[2000/82,2000/95]", "16000[2023/10,2023/96,2023/205]", "18000[2025/135]" };
        private static readonly string[] Old2023 = { "1399[2000/95]", "9000[2016/40]", "16000[2023/205]" };

        public static void Test_Last_entry_is_the_saved_version()
        {
            Assert.AreEqual(18000, SwFileVersion.Last(Current), "последняя запись — 2025");
            Assert.AreEqual(16000, SwFileVersion.Last(Old2023), "последняя запись — 2023");
        }

        public static void Test_Older_file_is_recognised()
        {
            Assert.IsTrue(SwFileVersion.Older(Old2023, 18000), "файл 2023 в SolidWorks 2025 — прежняя версия");
            Assert.IsFalse(SwFileVersion.Older(Current, 18000), "файл 2025 — не прежняя");
        }

        public static void Test_Unreadable_history_is_not_older()
        {
            Assert.AreEqual(0, SwFileVersion.Last(null), "истории нет");
            Assert.AreEqual(0, SwFileVersion.Last(new[] { "", "мусор" }), "не разобрать");
            Assert.IsFalse(SwFileVersion.Older(new[] { "мусор" }, 18000), "не разобрать — не прежняя: текст как обычно");
            Assert.IsFalse(SwFileVersion.Older(Old2023, 0), "текущая версия не известна");
        }
    }
}
