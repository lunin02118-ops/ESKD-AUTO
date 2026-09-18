using System;
using System.IO;
using ESKD.MaterialSync.Sw;

namespace ESKD.Tests
{
    /// <summary>Куда К-5 копирует документы: папка производства рядом с корнем заказов (ТЗ-02 Т-41).</summary>
    public static class IssueFoldersTests
    {
        private static string Temp()
        {
            string folder = Path.Combine(Path.GetTempPath(), "eskd_issue_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Раскладка NAS: «_Заявки\&lt;заказ&gt;» рядом с «_Производство».</summary>
        private static string Order(string root, string productionName)
        {
            string order = Path.Combine(root, "_Заявки", "101_Т_СОШ 29");
            Directory.CreateDirectory(order);
            if (productionName != null) Directory.CreateDirectory(Path.Combine(root, productionName));
            return order;
        }

        public static void Test_Existing_production_folder_is_used_as_is()
        {
            string root = Temp();
            try
            {
                // На NAS папка называется «_Производство»; заводить рядом вторую «04_ПРОИЗВОДСТВО» нельзя —
                // цех тогда не найдёт документы там, куда привык смотреть.
                string production = IssueService.ProductionRoot(Order(root, "_Производство"));
                Assert.AreEqual(Path.Combine(root, "_Производство"), production, "взята существующая папка");
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "04_ПРОИЗВОДСТВО")), "вторая папка не заведена");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        public static void Test_Production_folder_is_created_next_to_orders_root()
        {
            string root = Temp();
            try
            {
                string production = IssueService.ProductionRoot(Order(root, null));
                Assert.AreEqual(Path.Combine(root, "04_ПРОИЗВОДСТВО"), production, "заведена рядом с «_Заявки»");
                Assert.IsTrue(Directory.Exists(production), "папка создана");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
