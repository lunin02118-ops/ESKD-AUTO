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

            ISldWorks swApp = null;
            try
            {
                swApp = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
            }
            catch { }

            if (swApp == null)
            {
                try
                {
                    Type swType = Type.GetTypeFromProgID("SldWorks.Application");
                    if (swType != null)
                    {
                        swApp = (ISldWorks)Activator.CreateInstance(swType);
                    }
                }
                catch { }
            }

            if (syncMode)
            {
                if (swApp == null)
                {
                    MessageBox.Show(
                        "SolidWorks не запущен.\nСинхронизация активного документа невозможна.",
                        "ЕСКД Синхронизация",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                ModelDoc2 doc = (ModelDoc2)swApp.ActiveDoc;
                if (doc == null)
                {
                    MessageBox.Show(
                        "В SolidWorks нет активного документа.\nОткройте деталь, сборку или чертеж для синхронизации.",
                        "ЕСКД Синхронизация",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    MaterialSyncEngine.SyncModelProperties(doc, swApp, true);
                    string title = doc.GetTitle();
                    
                    NotifyIcon tray = new NotifyIcon();
                    tray.Icon = System.Drawing.SystemIcons.Information;
                    tray.Visible = true;
                    tray.ShowBalloonTip(
                        2000,
                        "ЕСКД Синхронизация",
                        "Синхронизация реквизитов, массы и материала выполнена для: " + title,
                        ToolTipIcon.Info);
                    
                    System.Threading.Thread.Sleep(1500);
                    tray.Visible = false;
                    tray.Dispose();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "Ошибка при синхронизации:\n" + ex.Message,
                        "ЕСКД Синхронизация",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return;
            }

            // Settings Mode (default)
            using (SettingsForm form = new SettingsForm(swApp))
            {
                Application.Run(form);
            }
        }
    }
}
