using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Открытые окна макросов SWPlus (MProp, DProp, SProp…). Нужны кнопке «Новая ревизия» (ТЗ-02 Т-49):
    /// MProp при «Применить» записывает доп. свойства тем, что было в его форме при открытии (спайк С-1),
    /// и молча вернёт прежнюю ревизию. Окна VBA — обычные окна класса ThunderDFrame внутри процесса
    /// SolidWorks, поэтому распознаются по классу, а не по заголовку: заголовки у макросов разные.
    /// </summary>
    internal static class MacroWindows
    {
        private const string VbaFormClass = "ThunderDFrame";

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        /// <summary>Заголовок открытого окна макроса SWPlus или пустая строка.</summary>
        public static string OpenTitle()
        {
            string found = "";
            try
            {
                int own = Process.GetCurrentProcess().Id;
                EnumWindows(delegate(IntPtr window, IntPtr parameter)
                {
                    if (!IsWindowVisible(window)) return true;
                    int process;
                    GetWindowThreadProcessId(window, out process);
                    if (process != own) return true;
                    StringBuilder name = new StringBuilder(64);
                    GetClassName(window, name, name.Capacity);
                    if (name.ToString() != VbaFormClass) return true;
                    StringBuilder title = new StringBuilder(256);
                    GetWindowText(window, title, title.Capacity);
                    found = title.ToString();
                    if (found.Length == 0) found = "окно макроса SWPlus";
                    return false;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                // Не смогли спросить систему — это не повод запрещать работу: считаем, что окон нет.
                Log.Error("Окна макросов SWPlus", ex);
                return "";
            }
            return found;
        }
    }
}
