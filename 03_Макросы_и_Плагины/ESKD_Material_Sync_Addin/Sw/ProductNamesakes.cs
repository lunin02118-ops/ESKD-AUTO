using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Подменённые компоненты открытой сборки изделия (№16; решение владельца 24.09.2026): SolidWorks взял одноимённый
    /// открытый документ другой папки вместо своего файла изделия. Проверка называет их браком, выгрузка и ЛЗК
    /// останавливаются до записи файлов. Правило — <see cref="OrderArchive.Swapped"/>.
    /// </summary>
    internal static class ProductNamesakes
    {
        /// <summary>Своё изделие: все файлы SolidWorks в папке изделия.</summary>
        public static string[] OwnFiles(string productFolder)
        {
            try
            {
                return string.IsNullOrEmpty(productFolder) || !Directory.Exists(productFolder)
                    ? new string[0] : Directory.GetFiles(productFolder, "*.sld*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                Log.Error("Одноимённые файлы: папка изделия " + productFolder, ex);
                return new string[0];
            }
        }

        /// <summary>Пути компонентов сборки, взятых не из папки изделия при своём одноимённом файле.</summary>
        public static List<string> Swapped(ModelDoc2 assembly, string productFolder)
        {
            AssemblyDoc asm = assembly as AssemblyDoc;
            if (asm == null) return new List<string>();
            List<string> resolved = new List<string>();
            try
            {
                object[] comps = asm.GetComponents(false) as object[];
                if (comps != null)
                    foreach (object o in comps)
                    {
                        Component2 comp = o as Component2;
                        if (comp != null) resolved.Add(comp.GetPathName() ?? "");
                    }
            }
            catch (COMException ex)
            {
                Log.Error("Одноимённые файлы: состав сборки", ex);
            }
            return OrderArchive.Swapped(resolved, OwnFiles(productFolder));
        }
    }
}
