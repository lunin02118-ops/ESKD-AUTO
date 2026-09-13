using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync
{
    /// <summary>
    /// ESKD.exe — окно настроек; ESKD_Sync.exe (или /sync) — синхронизация активного документа;
    /// ESKD_Sync.exe /clean &lt;файлы и каталоги&gt; [/apply] [/report отчёт.csv] — очистка файлов надстройки v5 (WP-3.3):
    /// без /apply только отчёт, с /apply — резервные копии и сохранение. Нужен запущенный SolidWorks.
    /// </summary>
    static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [STAThread]
        static void Main(string[] args)
        {
            if (Array.Exists(args, a => IsSwitch(a, "clean")))
            {
                System.Environment.ExitCode = RunClean(args);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool syncMode = false;
            string exeName = Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName);
            if (exeName.IndexOf("sync", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                syncMode = true;
            }

            if (Array.Exists(args, a => IsSwitch(a, "sync")))
            {
                syncMode = true;
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

        private static int RunClean(string[] args)
        {
            // Утилита собрана как winexe: вывод идёт в перенаправленный поток или в консоль, из которой её запустили.
            System.Text.Encoding utf8 = new System.Text.UTF8Encoding(false);
            if (Console.IsOutputRedirected)
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            if (Console.IsErrorRedirected)
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            if (!Console.IsOutputRedirected && AttachConsole(-1))
            {
                try
                {
                    Console.OutputEncoding = System.Text.Encoding.UTF8;
                }
                catch (IOException)
                {
                    // Консоль не позволяет сменить кодировку — вывод в её кодировке.
                }
            }
            bool apply = false;
            string report = null;
            List<string> inputs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (IsSwitch(args[i], "clean") || IsSwitch(args[i], "sync")) continue;
                if (IsSwitch(args[i], "apply")) { apply = true; continue; }
                if (IsSwitch(args[i], "report") && i + 1 < args.Length) { report = args[++i]; continue; }
                inputs.Add(args[i]);
            }
            List<string> missing = inputs.FindAll(p => !File.Exists(p) && !Directory.Exists(p));
            if (inputs.Count == 0 || missing.Count > 0)
            {
                Console.Error.WriteLine("Использование: ESKD_Sync.exe /clean <файлы и каталоги> [/apply] [/report отчёт.csv]");
                foreach (string m in missing) Console.Error.WriteLine("Не найден путь: " + m);
                return 2;
            }

            ISldWorks swApp = null;
            try
            {
                swApp = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
            }
            catch (COMException)
            {
                // SolidWorks не запущен — сообщение ниже.
            }
            if (swApp == null)
            {
                Console.Error.WriteLine("SolidWorks не запущен: очистка открывает файлы в запущенном SolidWorks.");
                return 2;
            }
            try
            {
                Sw.MigrationService.Summary summary = Sw.MigrationService.CleanFiles(swApp, inputs, apply, report);
                Console.WriteLine((apply ? "Очистка файлов v5 выполнена: " : "Пробный прогон очистки файлов v5 (без изменений): ") + summary);
                Console.WriteLine("Отчёт: " + summary.ReportPath);
                if (summary.BackupDirectory != null && Directory.Exists(summary.BackupDirectory))
                    Console.WriteLine("Резервные копии: " + summary.BackupDirectory);
                return summary.Failures > 0 ? 3 : 0;
            }
            catch (Exception ex)
            {
                Core.Log.Error("ESKD_Sync /clean", ex);
                Console.Error.WriteLine("Ошибка очистки: " + ex.Message);
                return 3;
            }
            finally
            {
                ReleaseComObject(swApp);
            }
        }

        private static bool IsSwitch(string arg, string name)
        {
            return arg.Equals("/" + name, StringComparison.OrdinalIgnoreCase) ||
                   arg.Equals("-" + name, StringComparison.OrdinalIgnoreCase) ||
                   arg.Equals("--" + name, StringComparison.OrdinalIgnoreCase);
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
