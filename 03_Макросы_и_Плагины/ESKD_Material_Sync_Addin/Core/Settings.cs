using System;
using Microsoft.Win32;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Настройки HKCU\Software\SolidWorks\ESKD_Settings. Читаются при каждом обращении:
    /// выключатель службы и флаги действуют без перезапуска SolidWorks.
    /// </summary>
    public sealed class Settings
    {
        public const string KeyPath = @"Software\SolidWorks\ESKD_Settings";

        public bool ServiceEnabled = true;
        public bool AutoSyncMaterials = true;

        /// <summary>Подбор материала по геометрии (Р-8): типоразмер профиля и толщина листа против библиотеки ЕСКД.</summary>
        public bool AutoStockMaterial = true;

        public bool AutoMass = true;
        public bool AutoSplitName = true;
        public string Author = "";
        public string Checker = "";
        public string Organization = "";

        // Флаги v6 (план, §3.8)
        public bool SyncOnSave = true;
        public bool SyncOnOpen;

        /// <summary>
        /// После «Сохранить как» файл пересохраняется ещё раз, чтобы обозначение по новому имени легло и на диск —
        /// продолжение команды конструктора, а не отдельное сохранение.
        /// </summary>
        public bool ResaveAfterSaveAs = true;

        /// <summary>«Сохранить копию»: копия открывается скрыто, получает реквизиты по своему имени и сохраняется.</summary>
        public bool FixCopies;

        /// <summary>
        /// Формат листов чертежа — в «Формат» модели при сохранении чертежа (З-1). Выключено (по умолчанию): открытой
        /// модели свойство пишется без сохранения (сохранит конструктор), закрытая не открывается — пустой «Формат»
        /// дозаполнит «Проверить изделие». Включено — как до 23.09.2026: закрытая модель открывается скрыто,
        /// записывается и сохраняется, открытая без других правок сохраняется сразу. Деталь без команды конструктора
        /// не сохраняется (решение владельца 23.09.2026).
        /// </summary>
        public bool FormatSavesModel;
        public bool OverwriteSignatures;
        public bool LegacyAliases;
        public bool DryRun;
        public string DictionaryPath = "";

        /// <summary>Раздел сведений об установке: папка инструментария, из которой настроено рабочее место.</summary>
        public const string InstallKeyPath = @"Software\SolidWorks\ESKD_Install";

        /// <summary>
        /// Справочники производства в папке инструментария (02_Шаблоны_и_Форматки\Справочники): нормативы, бланки ЛЗК.
        /// Путь — из «SourceRoot» сведений об установке; не установлено — пусто.
        /// </summary>
        public static string ReferenceFolder()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InstallKeyPath))
                {
                    string root = key == null ? "" : Str(key, "SourceRoot");
                    return root.Length == 0 ? "" : System.IO.Path.Combine(System.IO.Path.Combine(root, "02_Шаблоны_и_Форматки"), "Справочники");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Settings.ReferenceFolder", ex);
                return "";
            }
        }

        /// <summary>Кто работает: фамилия из настроек, а если её не задали — учётная запись Windows.
        /// (`Author` пустой, а не null, поэтому «Author ?? UserName» подстановку не делал никогда.)</summary>
        public static string AuthorOrUser()
        {
            string author = (Read().Author ?? "").Trim();
            return author.Length > 0 ? author : Environment.UserName;
        }

        public static Settings Read()
        {
            Settings s = new Settings();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key == null) return s;
                    s.ServiceEnabled = Int(key, "ServiceEnabled", 1) == 1;
                    s.AutoSyncMaterials = Int(key, "AutoSyncMaterials", 1) == 1;
                    s.AutoStockMaterial = Int(key, "AutoStockMaterial", 1) == 1;
                    s.AutoMass = Int(key, "AutoMass", 1) == 1;
                    s.AutoSplitName = Int(key, "AutoSplitName", 1) == 1;
                    s.Author = Str(key, "Author");
                    s.Checker = Str(key, "Checker");
                    s.Organization = Str(key, "Organization");
                    s.SyncOnSave = Int(key, "SyncOnSave", 1) == 1;
                    s.SyncOnOpen = Int(key, "SyncOnOpen", 0) == 1;
                    s.ResaveAfterSaveAs = Int(key, "ResaveAfterSaveAs", 1) == 1;
                    s.FixCopies = Int(key, "FixCopies", 0) == 1;
                    s.FormatSavesModel = Int(key, "FormatSavesModel", 0) == 1;
                    s.OverwriteSignatures = Int(key, "OverwriteSignatures", 0) == 1;
                    s.LegacyAliases = Int(key, "LegacyAliases", 0) == 1;
                    s.DryRun = Int(key, "DryRun", 0) == 1;
                    s.DictionaryPath = Str(key, "DictionaryPath");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Settings.Read", ex);
            }
            return s;
        }

        public static int Int(RegistryKey key, string name, int def)
        {
            if (key == null) return def;
            try
            {
                object raw = key.GetValue(name, def);
                if (raw is int) return (int)raw;
                if (raw is byte) return (byte)raw;
                string text = raw as string;
                int parsed;
                if (text != null && int.TryParse(text.Trim(), out parsed)) return parsed;
            }
            catch (Exception ex)
            {
                Log.Error("Settings.Int " + name, ex);
            }
            return def;
        }

        private static string Str(RegistryKey key, string name)
        {
            object v = key.GetValue(name);
            return v == null ? "" : v.ToString().Trim();
        }
    }
}
