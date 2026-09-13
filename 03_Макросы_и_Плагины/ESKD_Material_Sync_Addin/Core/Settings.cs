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
        public bool AutoMass = true;
        public bool AutoSplitName = true;
        public string Author = "";
        public string Checker = "";
        public string Organization = "";

        // Флаги v6 (план, §3.8)
        public bool SyncOnSave = true;
        public bool SyncOnOpen;
        public bool ResaveAfterSaveAs = true;
        public bool FixCopies;
        public bool OverwriteSignatures;
        public bool LegacyAliases;
        public bool LegacyAssemblyCodeSpace = true;
        public bool DryRun;
        public string DictionaryPath = "";

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
                    s.AutoMass = Int(key, "AutoMass", 1) == 1;
                    s.AutoSplitName = Int(key, "AutoSplitName", 1) == 1;
                    s.Author = Str(key, "Author");
                    s.Checker = Str(key, "Checker");
                    s.Organization = Str(key, "Organization");
                    s.SyncOnSave = Int(key, "SyncOnSave", 1) == 1;
                    s.SyncOnOpen = Int(key, "SyncOnOpen", 0) == 1;
                    s.ResaveAfterSaveAs = Int(key, "ResaveAfterSaveAs", 1) == 1;
                    s.FixCopies = Int(key, "FixCopies", 0) == 1;
                    s.OverwriteSignatures = Int(key, "OverwriteSignatures", 0) == 1;
                    s.LegacyAliases = Int(key, "LegacyAliases", 0) == 1;
                    s.LegacyAssemblyCodeSpace = Int(key, "LegacyAssemblyCodeSpace", 1) == 1;
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
