using System;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Одно правило «покупное» для проверки (К-3), выгрузки (К-2) и ведомости ЛЗК (К-4): защищённый документ
    /// (покупное, крепёж, Toolbox — как у синхронизации) или файл в папке стандартных изделий внутри «01_3D».
    /// Раньше ЛЗК смотрела только на свойства, и крепёж из «Стандартных изделий» получал подсказку
    /// «Лазерная резка» и запись свойств в файл (аудит 19.09, Л-В4).
    /// </summary>
    public static class ComponentKind
    {
        /// <param name="cipher">шифр изделия: деталь с ним в имени файла — своя и в папке стандартных изделий.</param>
        public static bool IsPurchased(PropertyWriter w, ModelDoc2 model, string path, string context, string cipher = "")
        {
            if (ProductLocator.IsPurchasedFolder(path, cipher)) return true;
            try
            {
                return SyncService.IsProtected(w ?? new PropertyWriter(model, true), model);
            }
            catch (Exception ex)
            {
                Log.Error(context + ": признак покупного", ex);
                return false;
            }
        }
    }
}
