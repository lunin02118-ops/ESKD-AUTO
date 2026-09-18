using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// PDF листов книги через Excel (ТЗ-04 Т4-4): формулы книги ЛЗК посчитаны только в Excel, поэтому печатает он.
    /// Excel запускается скрытым, без макросов, книга открывается только для чтения и закрывается без сохранения.
    /// Позднее связывание — сборки Office надстройке не нужны.
    /// </summary>
    public static class ExcelPdf
    {
        private const int PdfType = 0;           // xlTypePDF
        private const int StandardQuality = 0;   // xlQualityStandard
        private const int DisableMacros = 3;     // msoAutomationSecurityForceDisable

        /// <summary>Есть ли Excel на этом компьютере.</summary>
        public static bool Available
        {
            get { return Type.GetTypeFromProgID("Excel.Application") != null; }
        }

        /// <summary>
        /// Листы книги → PDF (лист → путь). false — ничего не создано или создано не всё; объяснение в <paramref name="problem"/>.
        /// </summary>
        public static bool Export(string workbook, IList<KeyValuePair<string, string>> sheets, out string problem)
        {
            problem = "";
            Type type = Type.GetTypeFromProgID("Excel.Application");
            if (type == null)
            {
                problem = "На компьютере нет Microsoft Excel — PDF книги ЛЗК сделать нечем. Книга в папке изделия, её можно напечатать с другого компьютера.";
                return false;
            }
            object app = null, books = null, book = null, worksheets = null;
            try
            {
                app = Activator.CreateInstance(type);
                SetProperty(app, "Visible", false);
                SetProperty(app, "DisplayAlerts", false);
                SetProperty(app, "ScreenUpdating", false);
                try
                {
                    SetProperty(app, "AutomationSecurity", DisableMacros);
                }
                catch (Exception ex)
                {
                    Log.Error("Excel: AutomationSecurity", ex);
                }
                books = GetProperty(app, "Workbooks");
                // Open(Filename, UpdateLinks = 0, ReadOnly = true)
                book = Call(books, "Open", workbook, 0, true);
                Call(app, "CalculateFull");
                worksheets = GetProperty(book, "Worksheets");
                foreach (KeyValuePair<string, string> pair in sheets)
                {
                    object sheet = null;
                    try
                    {
                        sheet = worksheets.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, worksheets, new object[] { pair.Key });
                        Directory.CreateDirectory(Path.GetDirectoryName(pair.Value) ?? "");
                        // ExportAsFixedFormat(Type, Filename, Quality, IncludeDocProperties, IgnorePrintAreas)
                        Call(sheet, "ExportAsFixedFormat", PdfType, pair.Value, StandardQuality, true, false);
                    }
                    finally
                    {
                        Release(sheet);
                    }
                    if (!File.Exists(pair.Value))
                    {
                        problem = "Excel не создал " + pair.Value;
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Log.Error("Excel: PDF " + workbook, inner);
                problem = "Excel не сделал PDF: " + inner.Message;
                return false;
            }
            finally
            {
                Release(worksheets);
                if (book != null)
                {
                    try
                    {
                        Call(book, "Close", false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Excel: закрытие книги", ex);
                    }
                }
                Release(book);
                Release(books);
                if (app != null)
                {
                    try
                    {
                        Call(app, "Quit");
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Excel: выход", ex);
                    }
                }
                Release(app);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private static object GetProperty(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }

        private static void SetProperty(object target, string name, object value)
        {
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
        }

        private static object Call(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }

        private static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }
}
