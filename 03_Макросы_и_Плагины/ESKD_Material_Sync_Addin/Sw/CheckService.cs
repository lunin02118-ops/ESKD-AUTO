using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using View = SolidWorks.Interop.sldworks.View;
using Environment = System.Environment;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Кнопка К-3 «Проверить изделие» (ТЗ-02 Т-32…Т-34): изделие проверяется целиком и получает отчёт
    /// `_Проверка.txt` с итогом ГОТОВО / ЗАМЕЧАНИЯ / БРАК. Ничего в моделях не меняется (Т-6).
    /// </summary>
    public static class CheckService
    {
        /// <summary>«ok|итог|брак|замечаний|путь отчёта» или «error|текст» — для автотестов и пакетного запуска (Т-9).</summary>
        public static string LastOutcome = "";

        private sealed class Node
        {
            public string Path = "";
            public ModelDoc2 Model;
            public bool IsAssembly;
            public bool IsPurchased;
            public bool InProduct;
        }

        public static bool Run(ISldWorks app, bool interactive)
        {
            LastOutcome = "";
            ModelDoc2 doc = null;
            try
            {
                doc = app.ActiveDoc as ModelDoc2;
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    Fail(app, interactive, "Проверка изделия запускается на сборке изделия.");
                    return false;
                }
                string assemblyPath = doc.GetPathName() ?? "";
                if (assemblyPath.Length == 0)
                {
                    Fail(app, interactive, "Сборка ещё не сохранена: сохраните её в папке изделия и повторите.");
                    return false;
                }
                ProductLocation location = ProductLocator.Locate(assemblyPath);
                string productFolder = location.ProductFolder.Length > 0
                    ? location.ProductFolder : LzkNaming.ProductFolder(assemblyPath);

                Status(app, "ЕСКД: проверка изделия — состав…");
                CheckReport report = new CheckReport
                {
                    Assembly = Path.GetFileName(assemblyPath),
                    Product = LzkNaming.Cipher(productFolder, assemblyPath),
                    User = Settings.Read().Author ?? Environment.UserName,
                    Time = DateTime.Now
                };
                if (!location.Found)
                    report.Add(CheckRules.References, CheckLevel.Issue, report.Assembly,
                        "изделие лежит вне заказа и базы (" + location.Reason + "): проверены только состав и реквизиты");

                List<Node> nodes = Collect(app, doc, assemblyPath, productFolder, report);
                Status(app, "ЕСКД: проверка изделия — перестроение…");
                Rebuild(doc, report);
                Status(app, "ЕСКД: проверка изделия — реквизиты…");
                foreach (Node node in nodes.Where(n => n.InProduct && !n.IsPurchased))
                {
                    Attributes(app, node, report);
                    DrawingOrBch(node, report);
                    Operations(node, report);
                }
                Status(app, "ЕСКД: проверка изделия — документы изделия…");
                Workbook(productFolder, assemblyPath, report);
                Export(productFolder, nodes, report);
                Drawings(app, nodes, report);
                foreach (Node node in nodes)
                    report.Checksums.Add(new KeyValuePair<string, string>(Path.GetFileName(node.Path), Checksum(node.Path)));

                string path = Write(productFolder, report);
                LastOutcome = string.Join("|", new[]
                {
                    "ok", CheckRules.OutcomeName(report.Outcome), report.Count(CheckLevel.Defect).ToString(),
                    report.Count(CheckLevel.Issue).ToString(), path
                });
                if (interactive) Show(app, report, path);
                Status(app, "");
                return report.Outcome != CheckLevel.Defect;
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия", ex);
                Fail(app, interactive, "Проверка не выполнена: " + ex.Message);
                return false;
            }
            finally
            {
                if (doc != null) Marshal.ReleaseComObject(doc);
            }
        }

        // ------------------------------------------------------------------ правило «а»: состав
        private static List<Node> Collect(ISldWorks app, ModelDoc2 doc, string assemblyPath, string productFolder, CheckReport report)
        {
            List<Node> nodes = new List<Node>
            {
                new Node { Path = assemblyPath, Model = doc, IsAssembly = true, InProduct = true }
            };
            AssemblyDoc asm = (AssemblyDoc)doc;
            try
            {
                asm.ResolveAllLightWeightComponents(false);
            }
            catch (COMException ex)
            {
                Log.Error("Проверка изделия: разрешение облегчённых компонентов", ex);
            }
            object[] comps = asm.GetComponents(false) as object[] ?? new object[0];
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { assemblyPath };
            foreach (object o in comps)
            {
                Component2 comp = o as Component2;
                if (comp == null) continue;
                string path = comp.GetPathName() ?? "";
                string name = path.Length > 0 ? Path.GetFileName(path) : (comp.Name2 ?? "компонент");
                // Пропавший файл проверяется раньше подавления: SolidWorks показывает потерянный
                // компонент подавленным, а это не исключение из состава, а оборванная ссылка.
                if (path.Length == 0 || !File.Exists(path))
                {
                    report.Add(CheckRules.References, CheckRules.LevelOf(CheckRules.References), name, "файл компонента не найден");
                    continue;
                }
                if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                if (!seen.Add(path)) continue;
                ModelDoc2 model = comp.GetModelDoc2() as ModelDoc2;
                if (model == null)
                {
                    report.Add(CheckRules.References, CheckRules.LevelOf(CheckRules.References), name, "модель не загрузилась");
                    continue;
                }
                bool inProduct = LzkNaming.IsInside(path, productFolder);
                if (!inProduct && !Allowed(path))
                    report.Add(CheckRules.References, CheckRules.LevelOf(CheckRules.References), name,
                        "ссылка за пределами заказа и базы: " + path);
                nodes.Add(new Node
                {
                    Path = path,
                    Model = model,
                    IsAssembly = model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY,
                    // Покупное — и по свойствам (SProp), и по папке заказа: в боевых заказах фурнитуру
                    // складывают в «Стандартные изделия и фурнитура», не помечая каждую модель.
                    IsPurchased = IsPurchased(model) || ProductLocator.IsPurchasedFolder(path),
                    InProduct = inProduct
                });
            }
            return nodes;
        }

        /// <summary>Ссылка допустима: заказ, база эталонов или библиотека стандартных изделий.</summary>
        private static bool Allowed(string path)
        {
            ProductLocation location = ProductLocator.Locate(path);
            return location.InOrder || location.InBase;
        }

        private static bool IsPurchased(ModelDoc2 model)
        {
            try
            {
                return SyncService.IsProtected(new PropertyWriter(model, true), model);
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: признак покупного", ex);
                return false;
            }
        }

        // ------------------------------------------------------------------ правило «б»: перестроение
        private static void Rebuild(ModelDoc2 doc, CheckReport report)
        {
            try
            {
                if (!doc.ForceRebuild3(false))
                    report.Add(CheckRules.Rebuild, CheckRules.LevelOf(CheckRules.Rebuild), Path.GetFileName(doc.GetPathName() ?? ""),
                        "сборка не перестроилась без ошибок");
            }
            catch (COMException ex)
            {
                Log.Error("Проверка изделия: перестроение", ex);
                report.Add(CheckRules.Rebuild, CheckRules.LevelOf(CheckRules.Rebuild), Path.GetFileName(doc.GetPathName() ?? ""),
                    "перестроение не выполнено: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ правило «в»: реквизиты, материал, масса
        private static void Attributes(ISldWorks app, Node node, CheckReport report)
        {
            string name = Path.GetFileName(node.Path);
            try
            {
                PropertyWriter w = new PropertyWriter(node.Model, true);
                string cfg = w.ActiveConfigurationName();
                ParsedName parsed = DesignationParser.Parse(node.Path, " ");
                // Пустое свойство — ещё не брак: обозначение и наименование система берёт из имени файла,
                // материал — из материала SolidWorks. Их записывает «Синхронизировать», об этом и сообщаем.
                Required(report, name, Value(w, cfg, "Обозначение"), parsed.HasDesignation, "Обозначение",
                    "в имени файла его тоже нет — переименуйте по ЕСКД или оформите как покупное изделие");
                Required(report, name, Value(w, cfg, "Наименование"),
                    parsed.Title.Length > 0 || parsed.BaseName.Length > 0, "Наименование",
                    "наименование неоткуда взять: переименуйте файл по ЕСКД");
                if (!node.IsAssembly && Value(w, cfg, "Материал_Строка").Length == 0)
                {
                    string database;
                    PartDoc part = node.Model as PartDoc;
                    string material = part == null ? null : SyncService.MaterialName(part, node.Model, cfg, out database);
                    if (material == null)
                        report.Add(CheckRules.Attributes, CheckRules.LevelOf(CheckRules.Attributes), name,
                            "материал не назначен: выберите его из библиотеки материалов");
                    else
                        report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), name,
                            "материал «" + material + "» не записан в «Материал_Строка»: нажмите «Синхронизировать»");
                }
                if (LzkOperations.ParseNumber(Value(w, cfg, "Масса_ФБ")) <= 0 &&
                    LzkOperations.ParseNumber(Value(w, cfg, "Масса_Таблица")) <= 0)
                {
                    if (SyncService.ActiveMass(node.Model) > 0)
                        report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), name,
                            "масса не записана в свойства: нажмите «Синхронизировать»");
                    else
                        report.Add(CheckRules.Attributes, CheckRules.LevelOf(CheckRules.Attributes), name,
                            "массы нет: в модели нет тел или не назначен материал");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: реквизиты " + name, ex);
                report.Add(CheckRules.Attributes, CheckRules.LevelOf(CheckRules.Attributes), name, "реквизиты не прочитаны: " + ex.Message);
            }
            Thickness(node, report, name);
            Diagnose(app, node, report, name);
        }

        /// <summary>
        /// Толщина листовой детали больше 50 мм — почти всегда ошибка модели (ТЗ-02 Т-32в):
        /// такой лист не режут и не гнут, а масса и раскрой считаются как у листа.
        /// </summary>
        private const double MaxSheetThicknessMm = 50;

        private static void Thickness(Node node, CheckReport report, string name)
        {
            if (node.IsAssembly) return;
            try
            {
                for (Feature f = node.Model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                {
                    if (f.GetTypeName2() != "SheetMetal") continue;
                    SheetMetalFeatureData data = f.GetDefinition() as SheetMetalFeatureData;
                    if (data == null) continue;
                    double mm = data.Thickness * 1000.0;
                    if (mm > MaxSheetThicknessMm)
                        report.Add(CheckRules.Attributes, CheckRules.LevelOf(CheckRules.Attributes), name,
                            "толщина листовой детали " + mm.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) +
                            " мм — больше " + MaxSheetThicknessMm + " мм: проверьте модель");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: толщина " + name, ex);
            }
        }

        /// <summary>
        /// Предупреждения синхронизации вхолостую (ТЗ-02 Т-32в): ручной материал, обозначение не по имени
        /// файла и прочее, о чём кнопка «Синхронизировать» сказала бы конструктору окном.
        /// </summary>
        private static void Diagnose(ISldWorks app, Node node, CheckReport report, string name)
        {
            try
            {
                SyncReport sync = SyncService.SyncModel(app, node.Model,
                    new SyncRequest { Reason = "проверка изделия", DryRun = true });
                // Переключение единиц массы — работа самой синхронизации, а не повод разбираться: в отчёт не идёт.
                foreach (string warning in sync.Warnings
                    .Where(x => x.IndexOf(SyncService.MassUnitsWarning, StringComparison.Ordinal) < 0).Take(3))
                    report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), name, warning);
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: диагностика " + name, ex);
                report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), name, "диагностика не выполнена: " + ex.Message);
            }
        }

        /// <summary>
        /// Реквизит записан — молчим; пусто, но восстановимо из имени файла — замечание «синхронизировать»;
        /// восстановить неоткуда — брак (ТЗ-02 Т-32, правила «в» и «в2»).
        /// </summary>
        private static void Required(CheckReport report, string document, string value, bool recoverable,
            string property, string defectText)
        {
            if (value.Length > 0) return;
            if (recoverable)
                report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), document,
                    "свойство «" + property + "» не записано в модель: нажмите «Синхронизировать»");
            else
                report.Add(CheckRules.Attributes, CheckRules.LevelOf(CheckRules.Attributes), document,
                    "не заполнено «" + property + "»: " + defectText);
        }

        private static string Value(PropertyWriter w, string cfg, string name)
        {
            try
            {
                string resolved = w.Resolved(cfg, name);
                if ((resolved ?? "").Trim().Length > 0) return resolved.Trim();
                return (w.Resolved("", name) ?? "").Trim();
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: свойство " + name, ex);
                return "";
            }
        }

        // ------------------------------------------------------------------ правило «г»: чертёж или БЧ
        private static void DrawingOrBch(Node node, CheckReport report)
        {
            if (node.IsAssembly) return;
            string name = Path.GetFileName(node.Path);
            if (File.Exists(Path.ChangeExtension(node.Path, ".slddrw"))) return;
            try
            {
                PropertyWriter w = new PropertyWriter(node.Model, true);
                if (BchService.IsBch(w, SyncService.Dictionary(Settings.Read()))) return;
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: признак БЧ " + name, ex);
            }
            report.Add(CheckRules.Drawing, CheckRules.LevelOf(CheckRules.Drawing), name,
                "нет чертежа и не оформлена как безчертёжная (кнопка «Деталь БЧ»)");
        }

        // ------------------------------------------------------------------ правило «д»: операции
        private static void Operations(Node node, CheckReport report)
        {
            string name = Path.GetFileName(node.Path);
            try
            {
                PropertyWriter w = new PropertyWriter(node.Model, true);
                if (Value(w, w.ActiveConfigurationName(), LzkOperations.PropertyName).Length > 0) return;
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: операции " + name, ex);
            }
            report.Add(CheckRules.Operations, CheckRules.LevelOf(CheckRules.Operations), name,
                "не заполнено свойство «Операции» (кнопка «Ведомость ЛЗК»)");
        }

        // ------------------------------------------------------------------ правило «ж»: ведомость
        private static void Workbook(string productFolder, string assemblyPath, CheckReport report)
        {
            string cipher = LzkNaming.Cipher(productFolder, assemblyPath);
            string path = LzkNaming.FindWorkbook(productFolder, cipher);
            string name = Path.GetFileName(path.Length > 0 ? path : LzkNaming.WorkbookPath(productFolder, cipher));
            if (path.Length == 0)
            {
                report.Add(CheckRules.Workbook, CheckRules.LevelOf(CheckRules.Workbook), name,
                    "нет книги ЛЗК изделия: нажмите «Ведомость ЛЗК»");
                return;
            }
            if (LzkNaming.IsLegacy(path))
                report.Add(CheckRules.Workbook, CheckRules.LevelOf(CheckRules.Workbook), name,
                    "ведомость старого образца (без участков и калькулятора): нажмите «Ведомость ЛЗК»");
            string reportPath = LzkNaming.ReportPath(productFolder);
            if (File.Exists(reportPath))
            {
                string text = File.ReadAllText(reportPath, Encoding.UTF8);
                if (text.IndexOf("Замечаний нет", StringComparison.Ordinal) < 0)
                    report.Add(CheckRules.Workbook, CheckRules.LevelOf(CheckRules.Workbook), name,
                        "в ведомости есть пометки: см. " + LzkNaming.ReportName);
            }
            try
            {
                using (XlsxBook book = XlsxBook.Open(path))
                {
                    string sheet, cell;
                    if (!book.TryResolveName("Шапка_КонтрольнаяСумма", out sheet, out cell)) return;
                    string written = (book.Sheet(sheet).Get(cell) ?? "").Trim();
                    string current = "SHA-256 " + Checksum(assemblyPath);
                    if (written.Length > 0 && !string.Equals(written, current, StringComparison.OrdinalIgnoreCase))
                        report.Add(CheckRules.Workbook, CheckRules.LevelOf(CheckRules.Workbook), name,
                            "ведомость сделана по другой версии сборки: сформируйте заново");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: ведомость", ex);
                report.Add(CheckRules.Workbook, CheckRules.LevelOf(CheckRules.Workbook), name, "ведомость не прочитана: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ правило «е»: выгрузка для производства
        private static void Export(string productFolder, List<Node> nodes, CheckReport report)
        {
            string path = Path.Combine(productFolder, CheckRules.ExportReportName);
            if (!File.Exists(path))
            {
                report.Add(CheckRules.Export, CheckRules.LevelOf(CheckRules.Export), CheckRules.ExportReportName,
                    "изделие не выгружено для производства (PDF, DXF, IGS)");
                return;
            }
            string text = File.ReadAllText(path, Encoding.UTF8);
            foreach (Node node in nodes.Where(n => n.InProduct && !n.IsPurchased && !n.IsAssembly))
            {
                string name = Path.GetFileNameWithoutExtension(node.Path);
                if (text.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
                    report.Add(CheckRules.Export, CheckRules.LevelOf(CheckRules.Export), Path.GetFileName(node.Path),
                        "нет в отчёте выгрузки " + CheckRules.ExportReportName);
            }
        }

        // ------------------------------------------------------------------ правило «з»: чертежи
        private static void Drawings(ISldWorks app, List<Node> nodes, CheckReport report)
        {
            foreach (Node node in nodes.Where(n => n.InProduct && !n.IsPurchased))
            {
                string drawingPath = Path.ChangeExtension(node.Path, ".slddrw");
                if (!File.Exists(drawingPath)) continue;
                ModelDoc2 drawing = null;
                bool opened = false;
                try
                {
                    drawing = app.GetOpenDocumentByName(drawingPath) as ModelDoc2;
                    if (drawing == null)
                    {
                        int errors = 0, warnings = 0;
                        drawing = app.OpenDoc6(drawingPath, (int)swDocumentTypes_e.swDocDRAWING,
                            (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly,
                            "", ref errors, ref warnings) as ModelDoc2;
                        opened = drawing != null;
                    }
                    if (drawing == null)
                    {
                        report.Add(CheckRules.Drawings, CheckRules.LevelOf(CheckRules.Drawings), Path.GetFileName(drawingPath),
                            "чертёж не открылся");
                        continue;
                    }
                    int dangling = Dangling(drawing);
                    if (dangling > 0)
                        report.Add(CheckRules.Drawings, CheckRules.LevelOf(CheckRules.Drawings), Path.GetFileName(drawingPath),
                            "оборванных размеров: " + dangling);
                }
                catch (Exception ex)
                {
                    Log.Error("Проверка изделия: чертёж " + drawingPath, ex);
                    report.Add(CheckRules.Drawings, CheckRules.LevelOf(CheckRules.Drawings), Path.GetFileName(drawingPath),
                        "чертёж не проверен: " + ex.Message);
                }
                finally
                {
                    if (opened && drawing != null) app.CloseDoc(drawing.GetPathName());
                }
            }
        }

        /// <summary>Оборванные размеры всех листов чертежа.</summary>
        private static int Dangling(ModelDoc2 drawing)
        {
            int count = 0;
            DrawingDoc drw = drawing as DrawingDoc;
            if (drw == null) return 0;
            object[] sheets = drw.GetViews() as object[] ?? new object[0];
            foreach (object sheetObject in sheets)
            {
                object[] views = sheetObject as object[];
                if (views == null) continue;
                foreach (object viewObject in views)
                {
                    View view = viewObject as View;
                    if (view == null) continue;
                    DisplayDimension dimension = view.GetFirstDisplayDimension5() as DisplayDimension;
                    while (dimension != null)
                    {
                        try
                        {
                            Annotation annotation = dimension.GetAnnotation() as Annotation;
                            if (annotation != null && annotation.IsDangling()) count++;
                        }
                        catch (COMException ex)
                        {
                            Log.Error("Проверка изделия: размер чертежа", ex);
                        }
                        dimension = dimension.GetNext5() as DisplayDimension;
                    }
                }
            }
            return count;
        }

        // ------------------------------------------------------------------ отчёт
        private static string Write(string productFolder, CheckReport report)
        {
            string path = CheckRules.ReportPath(productFolder);
            if (File.Exists(path)) File.Copy(path, CheckRules.PreviousReportPath(productFolder), true);
            File.WriteAllText(path, CheckRules.Report(report), new UTF8Encoding(true));
            return path;
        }

        private static void Show(ISldWorks app, CheckReport report, string path)
        {
            string text = CheckRules.OutcomeName(report.Outcome) + Environment.NewLine + Environment.NewLine +
                (report.Findings.Count == 0
                    ? "Замечаний нет."
                    : string.Join(Environment.NewLine, report.Findings.Take(15).Select(f => f.ToString()).ToArray()) +
                      (report.Findings.Count > 15 ? Environment.NewLine + "…" : "")) +
                Environment.NewLine + Environment.NewLine + "Отчёт: " + path +
                Environment.NewLine + Environment.NewLine + "Открыть отчёт?";
            DialogResult answer = MessageBox.Show(text, "ЕСКД: проверка изделия", MessageBoxButtons.YesNo,
                report.Outcome == CheckLevel.Defect ? MessageBoxIcon.Error
                    : report.Outcome == CheckLevel.Issue ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) Open(path);
        }

        /// <summary>
        /// Показать последний отчёт, не проверяя заново (ТЗ-02 Т-34). Изделие берётся от активной сборки,
        /// поэтому пункт работает и тогда, когда проверку запускал другой конструктор.
        /// </summary>
        public static bool ShowLast(ISldWorks app, bool interactive)
        {
            LastOutcome = "";
            try
            {
                ModelDoc2 doc = app.ActiveDoc as ModelDoc2;
                string assemblyPath = doc == null ? "" : (doc.GetPathName() ?? "");
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY || assemblyPath.Length == 0)
                {
                    Fail(app, interactive, "Отчёт проверки показывается на сохранённой сборке изделия.");
                    return false;
                }
                ProductLocation location = ProductLocator.Locate(assemblyPath);
                string productFolder = location.ProductFolder.Length > 0
                    ? location.ProductFolder : LzkNaming.ProductFolder(assemblyPath);
                string path = CheckRules.ReportPath(productFolder);
                if (!File.Exists(path))
                {
                    Fail(app, interactive, "Изделие ещё не проверялось: отчёта " + CheckRules.ReportName + " нет.");
                    return false;
                }
                LastOutcome = string.Join("|", new[]
                {
                    "ok", CheckRules.OutcomeOf(File.ReadAllText(path, Encoding.UTF8)), "", "", path
                });
                if (interactive) Open(path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: показ отчёта", ex);
                Fail(app, interactive, "Отчёт не открылся: " + ex.Message);
                return false;
            }
        }

        private static void Open(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(path);
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: открытие отчёта " + path, ex);
            }
        }

        private static void Fail(ISldWorks app, bool interactive, string text)
        {
            LastOutcome = "error|" + text;
            Status(app, "");
            if (interactive) MessageBox.Show(text, "ЕСКД: проверка изделия", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Status(ISldWorks app, string text)
        {
            try
            {
                Frame frame = app.Frame() as Frame;
                if (frame != null && text != null) frame.SetStatusBarText(text);
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: строка состояния", ex);
            }
        }

        private static string Checksum(string path)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch (IOException ex)
            {
                Log.Error("Проверка изделия: контрольная сумма " + path, ex);
                return "";
            }
        }
    }
}
