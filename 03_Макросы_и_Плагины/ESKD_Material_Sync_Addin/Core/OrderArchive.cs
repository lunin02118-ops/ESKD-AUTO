using System.IO;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Куда «Закрыть заказ» складывает сданный заказ (ТЗ-02 Т-45…Т-47). Отдельного диска архива нет (решение владельца
    /// 19.09.2026: диск один — Z:), поэтому архив — папка «_Архив» на том же общем ресурсе рядом с корнем заказов:
    /// <c>…\Конструкторский отдел\_Заявки\&lt;заказ&gt;</c> → <c>…\Конструкторский отдел\_Архив\&lt;год&gt;\&lt;заказ&gt;</c>.
    /// Путь выводится из папки заказа, поэтому не зависит от буквы диска.
    /// </summary>
    public static class OrderArchive
    {
        public const string FolderName = "_Архив";

        /// <summary>Архив по умолчанию для заказа <paramref name="orderFolder"/>; пусто — папку не вывести.</summary>
        public static string DefaultRoot(string orderFolder)
        {
            string order = (orderFolder ?? "").TrimEnd('\\', '/');
            string ordersRoot = Path.GetDirectoryName(order);
            string share = string.IsNullOrEmpty(ordersRoot) ? null : Path.GetDirectoryName(ordersRoot);
            return string.IsNullOrEmpty(share) ? "" : Path.Combine(share, FolderName);
        }
    }
}
