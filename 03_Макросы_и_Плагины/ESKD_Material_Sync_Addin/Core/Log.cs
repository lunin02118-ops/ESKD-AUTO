using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Журнал надстройки %TEMP%\eskd_material_sync.log. Ошибки не глотаются молча (Д-12):
    /// каждое исключение и каждый ненулевой код возврата API попадают сюда с контекстом.
    /// </summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        public const long MaxBytes = 5L * 1024 * 1024;

        public static string FilePath
        {
            get { return Path.Combine(Path.GetTempPath(), "eskd_material_sync.log"); }
        }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        public static void Error(string where, Exception ex)
        {
            Write("ERROR", where + ": " + (ex == null ? "" : ex.GetType().Name + ": " + ex.Message));
        }

        public static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    string path = FilePath;
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string archive = path + ".1";
                        if (File.Exists(archive)) File.Delete(archive);
                        File.Move(path, archive);
                    }
                    File.AppendAllText(path, string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1} {2}\r\n",
                        DateTime.Now, level, message), new UTF8Encoding(false));
                }
            }
            catch (IOException)
            {
                // Журнал недоступен (диск занят, файл заблокирован) — работа надстройки не должна от этого ломаться.
            }
            catch (UnauthorizedAccessException)
            {
                // Нет прав на %TEMP% — то же.
            }
        }

        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Предупреждение, которое повторялось бы при каждом сохранении: пишется один раз за сеанс SolidWorks.</summary>
        public static void WarnOnce(string message)
        {
            lock (Sync)
            {
                if (!Reported.Add(message)) return;
            }
            Warn(message);
        }
    }
}
