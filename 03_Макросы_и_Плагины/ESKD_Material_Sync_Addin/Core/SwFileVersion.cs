using System.Globalization;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Версия файла SolidWorks по истории сохранений (ISldWorks.VersionHistory; сверка SW API 23.09.2026, №26). Файл
    /// прежней версии SolidWorks сразу после открытия помечает изменённым, хотя конструктор ничего не правил, и сообщение
    /// «несохранённые правки» сбивало с толку. Решение владельца 24.09.2026: поведение то же — надстройка такой файл не
    /// сохраняет, — меняется только текст. История выглядит так: «16000[2023/10,2023/96]», «18000[2025/135]» — номер
    /// версии перед «[», последняя запись — версия, в которой файл сохранён (проба 23.09.2026).
    /// </summary>
    public static class SwFileVersion
    {
        /// <summary>Текст вместо «несохранённые правки» для файла прежней версии: правки в нём тоже могут быть.</summary>
        public const string OlderNote = "файл сохранён в прежней версии SolidWorks (или в нём несохранённые правки)";

        private static readonly Regex Entry = new Regex(@"^\s*(\d{3,6})\s*\[", RegexOptions.CultureInvariant);

        /// <summary>Номер версии последней записи истории; 0 — не разобрать.</summary>
        public static int Last(string[] history)
        {
            if (history == null) return 0;
            for (int i = history.Length - 1; i >= 0; i--)
            {
                Match m = Entry.Match(history[i] ?? "");
                int version;
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out version))
                    return version;
            }
            return 0;
        }

        /// <summary>Файл сохранён в версии старше текущей (latest — GetLatestSupportedFileVersion). Не разобрать — нет.</summary>
        public static bool Older(string[] history, int latest)
        {
            int version = Last(history);
            return version > 0 && latest > 0 && version < latest;
        }
    }
}
