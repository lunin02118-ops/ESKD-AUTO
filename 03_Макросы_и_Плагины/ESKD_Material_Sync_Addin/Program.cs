using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool syncMode = false;
            string exeName = Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName);
            if (exeName.IndexOf("sync", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                syncMode = true;
            }

            foreach (string arg in args)
            {
                if (arg.Equals("/sync", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-sync", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--sync", StringComparison.OrdinalIgnoreCase))
                {
                    syncMode = true;
                }
            }

            // D-8: подключаемся ТОЛЬКО к уже запущенному экземпляру SolidWorks.
            // Новый экземпляр через Activator.CreateInstance больше не поднимается:
            // он молча запускал скрытый процесс SW, который оставался висеть после выхода.
            ISldWorks swApp = null;
            try
            {
                swApp = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
            }
            catch (COMException)
            {
                // MK_E_UNAVAILABLE: SolidWorks не запущен — сообщение пользователю ниже.
            }

            if (swApp == null)
            {
                MessageBox.Show(
                    "SolidWorks не запущен. Запустите SolidWorks и повторите.",
                    "ЕСКД",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                System.Environment.ExitCode = 2;
                return;
            }

            if (syncMode)
            {
                ModelDoc2 doc = null;
                try
                {
                    doc = (ModelDoc2)swApp.ActiveDoc;
                }
                catch (Exception ex)
                {
                    Core.Log.Error("ESKD_Sync: ActiveDoc", ex);
                }

                if (doc == null)
                {
                    MessageBox.Show(
                        "В SolidWorks нет активного документа.\nОткройте деталь, сборку или чертеж для синхронизации.",
                        "ЕСКД Синхронизация",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    ReleaseComObject(swApp);
                    return;
                }

                try
                {
                    Sw.SyncReport report = Sw.SyncService.SyncExplicit(swApp, doc);
                    string title = doc.GetTitle() + " — " + report;

                    // D-22: NotifyIcon освобождается детерминированно через using;
                    // блокирующий Thread.Sleep удалён.
                    using (NotifyIcon tray = new NotifyIcon())
                    {
                        tray.Icon = System.Drawing.SystemIcons.Information;
                        tray.Visible = true;
                        tray.ShowBalloonTip(
                            2000,
                            "ЕСКД Синхронизация",
                            "Синхронизация реквизитов, массы и материала выполнена для: " + title,
                            ToolTipIcon.Info);
                    }
                }
                catch (Exception ex)
                {
                    Core.Log.Error("ESKD_Sync", ex);
                    MessageBox.Show(
                        "Ошибка при синхронизации:\n" + ex.Message,
                        "ЕСКД Синхронизация",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally
                {
                    ReleaseComObject(doc);
                    ReleaseComObject(swApp);
                }
                return;
            }

            // Settings Mode (default)
            using (SettingsForm form = new SettingsForm(swApp))
            {
                Application.Run(form);
            }
            ReleaseComObject(swApp);
        }

        /// <summary>
        /// D-8: освобождение полученных COM-ссылок (RCW) после использования.
        /// </summary>
        private static void ReleaseComObject(object comObject)
        {
            if (comObject != null)
            {
                try { Marshal.ReleaseComObject(comObject); }
                catch (ArgumentException)
                {
                    // Не COM-объект: освобождать нечего.
                }
            }
        }
    }
}
