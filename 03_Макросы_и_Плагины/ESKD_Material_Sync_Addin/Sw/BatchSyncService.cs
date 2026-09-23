using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
        /// <summary>Моделей, которым «Формат» дозаполнен по их чертежу (21.09.2026).</summary>
        public int Formats;
        /// <summary>Деталей, которым обозначение взято из имени файла по выбору конструктора.</summary>
        public int Renamed;
        /// <summary>
        /// Изменённых обходом документов, которые он не сохранил: в них были несохранённые правки конструктора или
        /// конструктор ответил «не сохранять» (23.09.2026).
        /// </summary>
        public int Unsaved;
        /// <summary>Свойств самой сборки, обновлённых обходом. Сборку он не сохраняет — это документ конструктора.</summary>
        public int AssemblyChanges;
        /// <summary>Обход не выполнен — почему (сборка не сохранена). Пусто — выполнен.</summary>
        public string Refused = "";
        public readonly List<string> Warnings = new List<string>();

        public string StatusLine()
        {
            if (Refused.Length > 0) return "ЕСКД: обход изделия не выполнен — " + Refused;
            return string.Format("ЕСКД: деталей {0}, обновлено {1}, сохранено {2}, материалов назначено {3}{4}{5}{6}{7}{8}",
                Parts, Changed, Saved, MaterialsAssigned, Formats > 0 ? ", формат из чертежа " + Formats : "",
                Renamed > 0 ? ", обозначение по имени файла " + Renamed : "",
                Unsaved > 0 ? ", не сохранено (сохраните сами) " + Unsaved : "",
                AssemblyChanges > 0 ? ", свойств сборки " + AssemblyChanges : "",
                Failed > 0 ? ", с ошибками " + Failed : "");
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
    /// Что делает: реквизиты самой сборки; обходит детали изделия, каждой выполняет то же, что выполняется при
    /// сохранении, и подбирает материал по геометрии. Неоднозначные типоразмеры и замена материала, выбранного
    /// конструктором, спрашиваются один раз на всё изделие. Изменённые документы сохраняются только после
    /// подтверждения конструктора (решение владельца 23.09.2026: без его команды деталь не сохраняется); документы,
    /// в которых до обхода были его несохранённые правки, обход не сохраняет никогда.
    ///
    /// Границы — <see cref="DocumentGuard"/>: только документы из папки изделия; покупные и стандартные (по папке и по
    /// свойствам), крепёж из библиотеки, детали чужих заказов и файлы только для чтения не трогаются. Несохранённая
    /// сборка — отказ: папки изделия у неё нет (раньше в этом случае обход брал всё подряд).
    /// </summary>
    public static class BatchSyncService
    {
        /// <summary>
        /// Идёт пакетный обход. Сохранения внутри него не должны поднимать обычную синхронизацию по событию:
        /// свойства уже записаны этим же проходом, а повторный круг ещё и завёл бы вторую задачу подбора материала.
        /// </summary>
        public static bool Running { get; private set; }

        /// <summary>Обойти изделие целиком. owner — окно SolidWorks для вопросов; null — без окон (автотесты), сохраняет сам.</summary>
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
            if (root.Length == 0)
            {
                batch.Refused = "сборка не сохранена";
                batch.Warnings.Add("Сборка не сохранена, и папки изделия у неё нет — обход не выполнен: он переписал бы и " +
                    "библиотечные, и покупные, и чужие детали. Сохраните сборку в папку изделия и нажмите «Синхронизировать» ещё раз.");
                return batch;
            }
            string assemblyPath = SafePath(assembly);
            string cipher = LzkNaming.Cipher(LzkNaming.ProductFolder(assemblyPath), assemblyPath);

            // Сама сборка — как кнопкой в детали: реквизиты в открытый документ, сохраняет её конструктор.
            SyncReport own = SyncService.SyncModel(app, assembly, new SyncRequest { Reason = "кнопка (сборка)" });
            if (!own.Skipped)
            {
                batch.AssemblyChanges = own.Changes;
                batch.Failed += own.Failures;
                foreach (string warning in own.Warnings) batch.Warnings.Add(Title(assembly) + ": " + warning);
            }

            // Документы с несохранёнными правками конструктора — до первой записи обхода: потом их уже не отличить.
            HashSet<string> edited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ModelDoc2> parts = Components(app, asm, root, cipher, batch, ".sldprt", edited);
            batch.Parts = parts.Count;
            HashSet<string> touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (parts.Count > 0) SyncParts(app, assembly, parts, owner, batch, touched);

            List<ModelDoc2> changed = new List<ModelDoc2>();
            foreach (ModelDoc2 part in parts)
                if (touched.Contains(SafePath(part))) changed.Add(part);
            changed.AddRange(Formats(app, asm, assembly, root, cipher, batch, edited));
            Commit(changed, edited, owner, batch);
            return batch;
        }

        /// <summary>Реквизиты, обозначения и материал деталей — в открытые документы. Сохранение — отдельно (<see cref="Commit"/>).</summary>
        private static void SyncParts(ISldWorks app, ModelDoc2 assembly, List<ModelDoc2> parts, IWin32Window owner,
            BatchReport batch, HashSet<string> touched)
        {
            // Проход 1: реквизиты, материал в свойства, осмотр проката. Сначала собираются все вопросы, чтобы задать
            // их один раз. Ключ — путь к файлу: обёртки COM одного и того же документа не всегда равны друг другу.
            Dictionary<string, List<StockFinding>> stock = new Dictionary<string, List<StockFinding>>(StringComparer.OrdinalIgnoreCase);
            var mismatches = new List<KeyValuePair<string, KeyValuePair<string, DesignationMismatch>>>();
            foreach (ModelDoc2 part in parts)
            {
                SyncReport report = SyncService.SyncModel(app, part, new SyncRequest { Reason = "пакет изделия" });
                if (report.Skipped) { batch.Skipped++; continue; }
                if (report.Changes > 0) { batch.Changed++; touched.Add(SafePath(part)); }
                // «Формат» из чертежа, сохранённого до исправления 21.09.2026: иначе графа спецификации пуста.
                if (DrawingFormatService.Backfill(app, part) > 0) { batch.Formats++; touched.Add(SafePath(part)); }
                batch.Failed += report.Failures;
                // Расхождение обозначения с именем файла спрашивается в своём окне, а не повторяется в общем списке.
                bool pick = report.Designation != null && owner != null;
                if (pick)
                    mismatches.Add(new KeyValuePair<string, KeyValuePair<string, DesignationMismatch>>(SafePath(part),
                        new KeyValuePair<string, DesignationMismatch>(Title(part), report.Designation)));
                foreach (string warning in report.Warnings)
                    if (!pick || warning != report.Designation.Warning) batch.Warnings.Add(Title(part) + ": " + warning);
                if (report.Stock.Count == 0) continue;
                stock[SafePath(part)] = report.Stock;
                // В окне сойдутся позиции разных деталей — без имени детали конструктор не поймёт, где они.
                foreach (StockFinding finding in report.Stock) finding.Owner = Title(part);
            }

            // Один вопрос на всё изделие: лист 6 мм в десяти деталях — это один выбор, а не десять окон. Замена
            // материала, выбранного конструктором, — тоже вопрос («оставить / исправить»), а не молчаливое действие.
            List<StockFinding> ask = new List<StockFinding>();
            foreach (KeyValuePair<string, List<StockFinding>> pair in stock)
                ask.AddRange(StockService.PendingDecisions(pair.Key, pair.Value));
            if (ask.Count > 0 && owner != null)
            {
                using (StockPickForm form = new StockPickForm("Изделие " + Title(assembly), ask))
                {
                    bool chosen = form.ShowDialog(owner) == DialogResult.OK;
                    foreach (KeyValuePair<string, List<StockFinding>> pair in stock)
                    {
                        if (!chosen)
                        {
                            // «Позже» — не переспрашиваем при сохранении каждой из этих деталей и выбор конструктора не меняем.
                            StockService.Postpone(pair.Key);
                            StockService.Decline(pair.Value);
                            continue;
                        }
                        StockService.Resume(pair.Key);
                        form.ApplyTo(pair.Value);
                        StockService.RememberKept(pair.Key, pair.Value);
                    }
                }
            }

            // Обозначения не по имени файла: какие исправить — решает конструктор (22.09.2026).
            HashSet<string> rename = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (mismatches.Count > 0)
            {
                using (DesignationPickForm form = new DesignationPickForm(mismatches))
                {
                    if (form.ShowDialog(owner) == DialogResult.OK) rename = form.Chosen;
                }
                foreach (var row in mismatches)
                    if (!rename.Contains(row.Key)) batch.Warnings.Add(row.Value.Key + ": " + row.Value.Value.Warning);
            }

            // Проход 2: обозначение по имени файла, назначение материала, повторная запись свойств.
            foreach (ModelDoc2 part in parts)
            {
                try
                {
                    if (rename.Contains(SafePath(part)))
                    {
                        SyncReport renamed = SyncService.SyncModel(app, part, new SyncRequest
                        {
                            Reason = "пакет изделия (обозначение по имени файла)", DesignationFromFile = true,
                            Signatures = false, Materials = false, Mass = false, Stock = false
                        });
                        if (renamed.Changes > 0) touched.Add(SafePath(part));
                        batch.Renamed++;
                    }
                    List<StockFinding> findings;
                    if (!stock.TryGetValue(SafePath(part), out findings)) continue;
                    SyncReport applied = new SyncReport();
                    int assigned = StockService.Apply(app, part, findings, applied);
                    foreach (string warning in applied.Warnings) batch.Warnings.Add(Title(part) + ": " + warning);
                    foreach (string operation in applied.Operations) Log.Info(Title(part) + ": " + operation);
                    if (assigned == 0) continue;
                    batch.MaterialsAssigned += assigned;
                    SyncService.SyncModel(app, part, new SyncRequest
                    {
                        Reason = "пакет изделия (материал по типоразмеру)", Names = false, Signatures = false, Stock = false
                    });
                    // Перестроение без сохранения: деталь может остаться несохранённой (правки конструктора или «Нет» в
                    // окне сохранения), и SolidWorks спросил бы «перестроить?» при её сохранении (X05, как в EventHub.ApplyStock).
                    part.EditRebuild3();
                    touched.Add(SafePath(part));
                }
                catch (Exception ex)
                {
                    Log.Error("Пакет изделия: " + Title(part), ex);
                    batch.Failed++;
                }
            }
        }

        /// <summary>
        /// «Формат» сборочных единиц — по их чертежам СБ (21.09.2026). Возвращает подсборки, которым формат дозаполнен:
        /// их сохранение решает <see cref="Commit"/>. Главная сборка — документ конструктора, её сохраняет он сам.
        /// </summary>
        private static List<ModelDoc2> Formats(ISldWorks app, AssemblyDoc asm, ModelDoc2 assembly, string root, string cipher,
            BatchReport batch, HashSet<string> edited)
        {
            List<ModelDoc2> changed = new List<ModelDoc2>();
            List<ModelDoc2> assemblies = Components(app, asm, root, cipher, new BatchReport(), ".sldasm", edited);
            foreach (ModelDoc2 sub in assemblies)
            {
                try
                {
                    if (DrawingFormatService.Backfill(app, sub) == 0) continue;
                    batch.Formats++;
                    changed.Add(sub);
                }
                catch (Exception ex)
                {
                    Log.Error("Пакет изделия: формат " + Title(sub), ex);
                    batch.Failed++;
                }
            }
            try
            {
                if (DrawingFormatService.Backfill(app, assembly) > 0) batch.Formats++;
            }
            catch (Exception ex)
            {
                Log.Error("Пакет изделия: формат " + Title(assembly), ex);
                batch.Failed++;
            }
            return changed;
        }

        /// <summary>
        /// Сохранение изменённых обходом документов. Документ с несохранёнными правками конструктора не сохраняется
        /// никогда — вместе со свойствами ушли бы и его незаконченные изменения. Остальные — после «Да» конструктора;
        /// без окна (owner = null: автотесты, работа без интерфейса) — сразу, как раньше.
        /// </summary>
        private static void Commit(List<ModelDoc2> changed, HashSet<string> edited, IWin32Window owner, BatchReport batch)
        {
            List<ModelDoc2> save = new List<ModelDoc2>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelDoc2 doc in changed)
            {
                string path = SafePath(doc);
                if (!seen.Add(path)) continue;
                if (edited.Contains(path))
                {
                    batch.Unsaved++;
                    batch.Warnings.Add(Title(doc) + ": до обхода в документе были несохранённые правки — свойства записаны, " +
                        "но документ не сохранён. Проверьте и сохраните его сами.");
                    continue;
                }
                save.Add(doc);
            }
            if (save.Count == 0) return;
            if (owner != null && !Confirm(owner, save))
            {
                batch.Unsaved += save.Count;
                return;
            }
            foreach (ModelDoc2 doc in save)
            {
                try
                {
                    if (Save(doc, batch)) batch.Saved++;
                }
                catch (Exception ex)
                {
                    Log.Error("Пакет изделия: сохранение " + Title(doc), ex);
                    batch.Failed++;
                }
            }
        }

        private static bool Confirm(IWin32Window owner, List<ModelDoc2> docs)
        {
            const int Shown = 15;
            StringBuilder list = new StringBuilder();
            for (int i = 0; i < docs.Count && i < Shown; i++) list.AppendLine("    " + Title(docs[i]));
            if (docs.Count > Shown) list.AppendLine("    … и ещё " + (docs.Count - Shown));
            string text = "Обход изделия изменил документов: " + docs.Count + System.Environment.NewLine + System.Environment.NewLine + list +
                System.Environment.NewLine + "Сохранить их сейчас?" + System.Environment.NewLine + System.Environment.NewLine +
                "Да — сохранить." + System.Environment.NewLine +
                "Нет — оставить изменёнными: сохраните их сами или закройте без сохранения.";
            return MessageBox.Show(owner, text, "ЕСКД: обход изделия", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1) == DialogResult.Yes;
        }

        /// <summary>
        /// Детали (.sldprt) или подсборки (.sldasm) изделия: каждая по одному разу, только те, что разрешает
        /// <see cref="DocumentGuard"/>. edited — пути документов, в которых уже есть несохранённые правки конструктора.
        /// </summary>
        private static List<ModelDoc2> Components(ISldWorks app, AssemblyDoc asm, string root, string cipher, BatchReport batch,
            string extension, HashSet<string> edited)
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
                    if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(path)) continue;
                    // По пути — до загрузки: чужой облегчённый компонент ради отказа не разворачивается.
                    string why = DocumentGuard.PathVerdict(path, root, cipher);
                    if (Refuse(why, path, batch)) continue;

                    ModelDoc2 model = component.GetModelDoc2() as ModelDoc2;
                    if (model == null)
                    {
                        // Облегчённый компонент: без загрузки у него нет ни свойств, ни дерева построения.
                        component.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved);
                        model = component.GetModelDoc2() as ModelDoc2;
                    }
                    if (model == null) { batch.Skipped++; continue; }
                    if (Refuse(DocumentGuard.DocVerdict(model), path, batch)) continue;
                    if (DocumentGuard.HasUserEdits(model)) edited.Add(path);
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

        /// <summary>Отказ по <see cref="DocumentGuard"/>: чужое и покупное пропускается молча, остальное — с замечанием.</summary>
        private static bool Refuse(string why, string path, BatchReport batch)
        {
            if (string.IsNullOrEmpty(why)) return false;
            batch.Skipped++;
            if (DocumentGuard.IsQuietRefusal(why)) Log.Info("Пакет изделия: " + Path.GetFileName(path) + " пропущен — " + why);
            else batch.Warnings.Add(Path.GetFileName(path) + ": " + why + " — пропущен");
            return true;
        }

        /// <summary>Папка изделия — папка сборки. Детали выше по дереву каталогов считаются чужими. Не сохранена — "".</summary>
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

        /// <summary>
        /// Лежит ли файл в папке изделия или её подпапках. Пустой корень (сборка не сохранена) — нет: раньше тут было
        /// «берём всё», и обход несохранённой сборки переписывал библиотеку и чужие заказы (аудит 23.09.2026).
        /// </summary>
        public static bool Inside(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
            string a = root.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(a, StringComparison.OrdinalIgnoreCase);
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
