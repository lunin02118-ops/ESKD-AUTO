using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>Итог пакетной синхронизации изделия — то, что видит конструктор после нажатия кнопки.</summary>
    public sealed class BatchReport
    {
        public int Parts;
        public int Changed;
        public int Saved;
        public int Failed;
        public int Skipped;
        public int MaterialsAssigned;
        public readonly List<string> Warnings = new List<string>();

        public string StatusLine()
        {
            return string.Format("ЕСКД: деталей {0}, обновлено {1}, сохранено {2}, материалов назначено {3}{4}",
                Parts, Changed, Saved, MaterialsAssigned, Failed > 0 ? ", с ошибками " + Failed : "");
        }
    }

    /// <summary>
    /// Пакетная синхронизация изделия по одной кнопке (замечание владельца 20.09.2026).
    ///
    /// Зачем. Материал конструктор назначает правой кнопкой в дереве сборки и деталь отдельно не сохраняет.
    /// Событие сохранения приходит от сборки, а не от каждой детали, поэтому синхронизация по детали не
    /// срабатывает и в книге ЛЗК материал остаётся «?». Приходилось открывать каждую деталь и жать «Сохранить».
    /// Теперь достаточно нажать «Синхронизировать», стоя в сборке.
    ///
    /// Что делает: обходит детали изделия, каждой выполняет то же, что выполняется при сохранении, подбирает
    /// материал по геометрии и сохраняет. Неоднозначные типоразмеры спрашиваются один раз на всё изделие.
    ///
    /// Границы: берутся только детали из папки изделия. Покупные, крепёж из библиотеки и детали чужих заказов
    /// не трогаются — им в папке изделия не место, а переписывать чужие файлы нельзя.
    /// </summary>
    public static class BatchSyncService
    {
        /// <summary>
        /// Идёт пакетный обход. Сохранения внутри него не должны поднимать обычную синхронизацию по событию:
        /// свойства уже записаны этим же проходом, а повторный круг ещё и завёл бы вторую задачу подбора материала.
        /// </summary>
        public static bool Running { get; private set; }

        /// <summary>Обойти изделие целиком. owner — окно SolidWorks для диалога выбора материала; null — не спрашивать.</summary>
        public static BatchReport SyncProduct(ISldWorks app, ModelDoc2 assembly, IWin32Window owner)
        {
            Running = true;
            try { return Walk(app, assembly, owner); }
            finally { Running = false; }
        }

        private static BatchReport Walk(ISldWorks app, ModelDoc2 assembly, IWin32Window owner)
        {
            BatchReport batch = new BatchReport();
            AssemblyDoc asm = assembly as AssemblyDoc;
            if (asm == null) return batch;

            string root = RootFolder(assembly);
            List<ModelDoc2> parts = Parts(app, asm, root, batch);
            batch.Parts = parts.Count;
            if (parts.Count == 0) return batch;

            // Проход 1: реквизиты, материал в свойства, осмотр проката. Документы пока не сохраняем —
            // сначала соберём все неоднозначные типоразмеры, чтобы спросить о них один раз.
            // Ключ — путь к файлу: обёртки COM одного и того же документа не всегда равны друг другу.
            Dictionary<string, List<StockFinding>> stock = new Dictionary<string, List<StockFinding>>(StringComparer.OrdinalIgnoreCase);
            // Сохраняются только тронутые детали. Изделие целиком — это и покупные, и уже согласованные
            // детали: переписывать их файлы незачем, у них меняется только дата, а заказ потом не понять.
            HashSet<string> touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<StockFinding> ask = new List<StockFinding>();
            foreach (ModelDoc2 part in parts)
            {
                SyncReport report = SyncService.SyncModel(app, part, new SyncRequest { Reason = "пакет изделия" });
                if (report.Skipped) { batch.Skipped++; continue; }
                if (report.Changes > 0) { batch.Changed++; touched.Add(SafePath(part)); }
                batch.Failed += report.Failures;
                foreach (string warning in report.Warnings) batch.Warnings.Add(Title(part) + ": " + warning);
                if (report.Stock.Count == 0) continue;
                stock[SafePath(part)] = report.Stock;
                foreach (StockFinding finding in report.Stock)
                {
                    // В окне сойдутся позиции разных деталей — без имени детали конструктор не поймёт, где они.
                    finding.Owner = Title(part);
                    if (finding.NeedsChoice) ask.Add(finding);
                }
            }

            // Один вопрос на всё изделие: лист 6 мм в десяти деталях — это один выбор, а не десять окон.
            if (ask.Count > 0 && owner != null)
            {
                using (StockPickForm form = new StockPickForm("Изделие " + Title(assembly), ask))
                {
                    bool chosen = form.ShowDialog(owner) == DialogResult.OK;
                    foreach (KeyValuePair<string, List<StockFinding>> pair in stock)
                    {
                        if (!chosen)
                        {
                            // «Позже» — не переспрашиваем при сохранении каждой из этих деталей.
                            StockService.Postpone(pair.Key);
                            continue;
                        }
                        StockService.Resume(pair.Key);
                        foreach (StockFinding finding in pair.Value)
                        {
                            if (!finding.NeedsChoice) continue;
                            MaterialInfo picked;
                            if (form.Chosen.TryGetValue(StockCatalog.NormalizeSize(finding.Request.Size), out picked))
                                finding.Chosen = picked;
                        }
                    }
                }
            }

            // Проход 2: назначение материала, повторная запись свойств и сохранение.
            foreach (ModelDoc2 part in parts)
            {
                try
                {
                    int assigned = 0;
                    List<StockFinding> findings;
                    if (stock.TryGetValue(SafePath(part), out findings))
                    {
                        SyncReport applied = new SyncReport();
                        assigned = StockService.Apply(app, part, findings, applied);
                        foreach (string warning in applied.Warnings) batch.Warnings.Add(Title(part) + ": " + warning);
                        foreach (string operation in applied.Operations) Log.Info(Title(part) + ": " + operation);
                    }
                    if (assigned > 0)
                    {
                        batch.MaterialsAssigned += assigned;
                        SyncService.SyncModel(app, part, new SyncRequest
                        {
                            Reason = "пакет изделия (материал по типоразмеру)", Names = false, Signatures = false, Stock = false
                        });
                        touched.Add(SafePath(part));
                    }
                    if (!touched.Contains(SafePath(part))) continue;
                    if (Save(part, batch)) batch.Saved++;
                }
                catch (Exception ex)
                {
                    Log.Error("Пакет изделия: " + Title(part), ex);
                    batch.Failed++;
                }
            }
            return batch;
        }

        /// <summary>Детали изделия: каждая по одному разу, только из папки изделия, разрешённые и не только для чтения.</summary>
        private static List<ModelDoc2> Parts(ISldWorks app, AssemblyDoc asm, string root, BatchReport batch)
        {
            List<ModelDoc2> parts = new List<ModelDoc2>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object[] components;
            try
            {
                components = asm.GetComponents(false) as object[];  // false — всё изделие, а не только верхний уровень
            }
            catch (COMException ex)
            {
                Log.Error("Пакет изделия: состав", ex);
                return parts;
            }
            if (components == null) return parts;

            foreach (object o in components)
            {
                Component2 component = o as Component2;
                if (component == null) continue;
                try
                {
                    if (component.IsSuppressed()) continue;
                    string path = component.GetPathName();
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!path.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(path)) continue;
                    if (!Inside(root, path)) { batch.Skipped++; continue; }

                    ModelDoc2 model = component.GetModelDoc2() as ModelDoc2;
                    if (model == null)
                    {
                        // Облегчённый компонент: без загрузки у него нет ни свойств, ни дерева построения.
                        component.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved);
                        model = component.GetModelDoc2() as ModelDoc2;
                    }
                    if (model == null) { batch.Skipped++; continue; }
                    if (ReadOnly(path))
                    {
                        batch.Skipped++;
                        batch.Warnings.Add(Path.GetFileName(path) + ": файл только для чтения — пропущен");
                        continue;
                    }
                    parts.Add(model);
                }
                catch (COMException ex)
                {
                    Log.Error("Пакет изделия: компонент", ex);
                    batch.Failed++;
                }
            }
            return parts;
        }

        /// <summary>Папка изделия — папка сборки. Детали выше по дереву каталогов считаются чужими.</summary>
        public static string RootFolder(ModelDoc2 assembly)
        {
            try
            {
                string path = assembly.GetPathName();
                return string.IsNullOrEmpty(path) ? "" : (Path.GetDirectoryName(path) ?? "");
            }
            catch (COMException)
            {
                return "";
            }
        }

        /// <summary>Лежит ли файл в папке изделия или её подпапках. Пустой корень — сборка не сохранена, берём всё.</summary>
        public static bool Inside(string root, string path)
        {
            if (string.IsNullOrEmpty(root)) return true;
            if (string.IsNullOrEmpty(path)) return false;
            string a = root.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ReadOnly(string path)
        {
            try
            {
                return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static bool Save(ModelDoc2 part, BatchReport batch)
        {
            int errors = 0, warnings = 0;
            try
            {
                // Неперестроенную модель SolidWorks сохраняет с вопросом «перестроить?» — при обходе изделия это
                // окно на каждую деталь. Перестраиваем сами (X05, 21.09.2026).
                part.EditRebuild3();
                if (part.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings)) return true;
                batch.Failed++;
                batch.Warnings.Add(Title(part) + string.Format(": не сохранена (errors={0}, warnings={1})", errors, warnings));
                return false;
            }
            catch (COMException ex)
            {
                Log.Error("Пакет изделия: сохранение " + Title(part), ex);
                batch.Failed++;
                return false;
            }
        }

        private static string Title(ModelDoc2 doc)
        {
            return DocInfo.TitleOf(doc);
        }

        private static string SafePath(ModelDoc2 doc)
        {
            try { return doc != null ? doc.GetPathName() : ""; }
            catch (COMException) { return ""; }
        }
    }
}
