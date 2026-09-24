using System;
using System.IO;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Можно ли надстройке писать в документ и сохранять его — одно правило вместо проверок, разбросанных по обходу
    /// изделия, формату и простою (аудит 23.09.2026). Пустая строка — можно; иначе причина, понятная конструктору.
    ///
    /// Правило: документ сохранён и лежит в папке изделия; не покупной и не стандартный ни по папке, ни по свойствам
    /// (то же правило, что у ЛЗК, проверки и выгрузки, — <see cref="ComponentKind"/>); файл не защищён от записи и
    /// открыт не только для чтения. Несохранённые правки конструктора — не запрет на запись свойств, а запрет на
    /// сохранение: его вызывающий проверяет сам через <see cref="HasUserEdits"/> до первой своей записи.
    /// </summary>
    public static class DocumentGuard
    {
        public const string OutsideProduct = "вне папки изделия";
        public const string PurchasedByFolder = "покупное или стандартное (папка)";
        public const string PurchasedByProperties = "покупное или стандартное (свойства)";

        /// <summary>Проверки по пути — до загрузки компонента: облегчённый компонент ради отказа не разворачивается.</summary>
        public static string PathVerdict(string path, string productRoot, string cipher)
        {
            if (string.IsNullOrEmpty(path)) return "документ не сохранён";
            if (string.IsNullOrEmpty(productRoot)) return "папка изделия не известна — сборка не сохранена";
            bool inside;
            try
            {
                inside = LzkNaming.IsInside(path, productRoot);
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)) throw;
                return "путь не разобран (" + ex.Message.Trim() + ")";
            }
            if (!inside) return OutsideProduct;
            if (ProductLocator.IsPurchasedFolder(path, cipher)) return PurchasedByFolder;
            if (FileReadOnly(path)) return "файл только для чтения";
            return "";
        }

        /// <summary>Проверки загруженного документа: открыт только для чтения, покупное по свойствам.</summary>
        public static string DocVerdict(ModelDoc2 doc)
        {
            if (doc == null) return "документ не загружен";
            try
            {
                if (doc.IsOpenedReadOnly()) return "открыт только для чтения";
                if (SyncService.IsProtected(new PropertyWriter(doc, true), doc)) return PurchasedByProperties;
            }
            catch (COMException ex)
            {
                return "документ не ответил (" + ex.Message.Trim() + ")";
            }
            return "";
        }

        public static string WhyNotWrite(ModelDoc2 doc, string path, string productRoot, string cipher)
        {
            string why = PathVerdict(path, productRoot, cipher);
            return why.Length > 0 ? why : DocVerdict(doc);
        }

        /// <summary>Отказ, о котором молчат: чужой или покупной файл в изделии — норма, а не замечание.</summary>
        public static bool IsQuietRefusal(string why)
        {
            return why == OutsideProduct || why == PurchasedByFolder || why == PurchasedByProperties;
        }

        /// <summary>В документе есть несохранённые правки. Не удалось спросить — считаем, что есть: сохранять нельзя.</summary>
        public static bool HasUserEdits(ModelDoc2 doc)
        {
            if (doc == null) return false;
            try
            {
                return doc.GetSaveFlag();
            }
            catch (COMException)
            {
                return true;
            }
        }

        public static bool FileReadOnly(string path)
        {
            try
            {
                return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
    }
}
