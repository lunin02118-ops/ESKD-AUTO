using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Как материал называется в окнах надстройки. Окно выбора материала после сохранения детали убрано (решение
    /// владельца 23.09.2026, журнал З-27): вопросы о материале задаёт одно окно — «Синхронизировать» в детали и
    /// «Проверить изделие» в сборке (<see cref="ProductReviewForm"/>).
    /// </summary>
    public static class StockText
    {
        /// <summary>Материал в списке ответов — обозначением для графы 3, а не именем файла библиотеки.</summary>
        public static string Describe(MaterialInfo info)
        {
            if (info == null) return "";
            string line = (info.LineDesignation ?? "").Trim();
            if (line.Length > 0) return line;
            return (info.Name ?? "").Trim();
        }

        /// <summary>Ответ «оставить стоящий материал».</summary>
        public static string KeepCaption(string current)
        {
            return "Оставить как есть: «" + (current ?? "").Trim() + "»";
        }
    }
}
