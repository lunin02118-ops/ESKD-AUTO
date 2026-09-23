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
    /// Кнопка К-1 «Сделать независимым с чертежом» (ТЗ-02 Т-19…Т-24). Выделенный в дереве эталон или деталь
    /// чужого изделия становится своей копией в «01_3D» этого изделия: модель отвязывается
    /// (<see cref="AssemblyDoc.MakeIndependent"/>), чертёж копируется и переводится на новую модель, реквизиты
    /// берутся из нового имени файла. Исходный файл не меняется — это общий эталон (Т-24).
    /// </summary>
    public static class IndependentService
    {
        /// <summary>«ok|создано|пропущено|оборванных размеров|путь отчёта» или «error|текст» — для автотестов (Т-9).</summary>
        public static string LastOutcome = "";

        /// <summary>Что предлагает окно К-1 (Т-20) и что у него спрашивают автотесты.</summary>
        public sealed class Candidate
        {
            /// <summary>Файл исходной модели.</summary>
            public string Source = "";
            /// <summary>Экземпляры этой модели, выделенные в дереве: все они перепривязываются (Т-21).</summary>
            public readonly List<Component2> Instances = new List<Component2>();
            /// <summary>Предложенное обозначение — следующий свободный номер в группе родителя (Т-20).</summary>
            public string Designation = "";
            /// <summary>Наименование исходной модели.</summary>
            public string Name = "";
            /// <summary>Рядом с исходной моделью есть чертёж — флажок «с чертежом» включён (Т-20).</summary>
            public bool HasDrawing;
        }

        // ------------------------------------------------------------------ вход
        public static bool Run(ISldWorks app, bool interactive)
        {
            return Run(app, interactive, null, null, null);
        }

        /// <summary>
        /// Сделать выделенное независимым. Обозначение и наименование пустые — берутся предложенные (Т-20);
        /// <paramref name="withDrawing"/> пустой — чертёж копируется, если он есть рядом с исходной моделью.
        /// </summary>
        public static bool Run(ISldWorks app, bool interactive, string designation, string name, bool? withDrawing)
        {
            LastOutcome = "";
            ModelDoc2 doc = null;
            try
            {
                doc = app.ActiveDoc as ModelDoc2;
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    Fail(app, interactive, "Сделать независимым можно компонент, выделенный в сборке изделия.");
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
                if (productFolder.Length == 0)
                {
                    Fail(app, interactive, "Сборка лежит вне папки изделия (" + location.Reason +
                        "): новую деталь некуда положить.");
                    return false;
                }
                string modelsFolder = location.ModelsFolder.Length > 0
                    ? location.ModelsFolder : Path.Combine(productFolder, LzkNaming.ModelsFolder);
                Directory.CreateDirectory(modelsFolder);

                IndependentLog log = new IndependentLog
                {
                    Product = LzkNaming.Cipher(productFolder, assemblyPath),
                    User = Settings.AuthorOrUser(),
                    Time = DateTime.Now
                };
                List<Candidate> candidates = Candidates(app, doc, productFolder, modelsFolder, log,
                    interactive ? (Func<string, string, bool>)AskDetachNode : null);
                if (candidates.Count == 0)
                {
                    Fail(app, interactive, log.Skipped.Count > 0
                        ? "Независимыми делают эталоны базы и детали чужих изделий:" + Environment.NewLine +
                          Environment.NewLine + string.Join(Environment.NewLine, log.Skipped.Take(10).ToArray())
                        : "Выделите в дереве сборки компоненты, которые нужно сделать своими.");
                    return false;
                }
                // Имя и обозначение задают только одной детали: для нескольких сразу правит человек в дереве,
                // иначе одно введённое обозначение досталось бы всем выделенным.
                if (candidates.Count == 1)
                {
                    if (!string.IsNullOrWhiteSpace(designation)) candidates[0].Designation = designation.Trim();
                    if (!string.IsNullOrWhiteSpace(name)) candidates[0].Name = name.Trim();
                }
                if (interactive && !Ask(app, candidates)) return false;

                foreach (Candidate candidate in candidates)
                {
                    Status(app, "ЕСКД: делаю независимым — " + Path.GetFileName(candidate.Source));
                    bool drawing = withDrawing.HasValue ? withDrawing.Value && candidate.HasDrawing : candidate.HasDrawing;
                    MakeOne(app, doc, candidate, modelsFolder, drawing, log);
                }
                // Отчёта _Независимые.txt нет (решение владельца 18.09.2026): итог — окно замечаний; пятое поле пусто.
                Notices.Remember(Notices.FromIndependent(log));
                LastOutcome = string.Join("|", new[]
                {
                    "ok", log.Created.Count.ToString(CultureInfo.InvariantCulture),
                    log.Skipped.Count.ToString(CultureInfo.InvariantCulture),
                    log.Dangling.ToString(CultureInfo.InvariantCulture), ""
                });
                if (interactive) Show(app, log);
                Status(app, "");
                return log.Created.Count > 0;
            }
            catch (Exception ex)
            {
                Log.Error("Сделать независимым", ex);
                Fail(app, interactive, "Не сделано: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ что выделено (Т-19, Т-20)
        /// <summary>Выделенные компоненты, которые можно сделать своими; остальные попадают в пропуски с причиной.</summary>
        public static List<Candidate> Candidates(ISldWorks app, ModelDoc2 doc, string productFolder,
            string modelsFolder, IndependentLog log, Func<string, string, bool> askDetachNode = null)
        {
            List<Candidate> candidates = new List<Candidate>();
            SelectionMgr selection = doc.SelectionManager as SelectionMgr;
            if (selection == null) return candidates;
            // Уже занятые номера считаются один раз: у соседних деталей одной группы номера идут подряд.
            List<string> taken = new List<string>(OrderNumbering.DesignationsInFolder(modelsFolder));
            int count = selection.GetSelectedObjectCount2(-1);
            for (int i = 1; i <= count; i++)
            {
                Component2 comp = selection.GetSelectedObjectsComponent4(i, -1) as Component2;
                if (comp == null) continue;
                // Т-21: деталь внутри чужого узла (эталон базы, узел другого изделия) — MakeIndependent переписал бы
                // ссылку в файле этого узла. Сначала своим делается узел: с согласия конструктора — вместо детали.
                Component2 node = ForeignNode(comp, productFolder);
                if (node != null)
                {
                    string nodeTitle = Path.GetFileName(node.GetPathName() ?? "") ?? "";
                    string partTitle = Path.GetFileName(comp.GetPathName() ?? "") ?? "";
                    if (askDetachNode == null || !askDetachNode(partTitle, nodeTitle))
                    {
                        log.Skip(partTitle, "входит в чужой узел «" + nodeTitle + "»: сначала сделайте независимым узел");
                        continue;
                    }
                    comp = node;
                }
                string path = comp.GetPathName() ?? "";
                string title = path.Length > 0 ? Path.GetFileName(path) : (comp.Name2 ?? "компонент");
                if (path.Length == 0 || !File.Exists(path))
                {
                    log.Skip(title, "файл компонента не найден");
                    continue;
                }
                if (LzkNaming.IsInside(path, productFolder))
                {
                    log.Skip(title, "деталь уже своя: она лежит в папке этого изделия");
                    continue;
                }
                if (ProductLocator.IsPurchasedFolder(path))
                {
                    log.Skip(title, "покупное или стандартное изделие: оно приходит готовым");
                    continue;
                }
                Candidate same = candidates.FirstOrDefault(c => string.Equals(c.Source, path, StringComparison.OrdinalIgnoreCase));
                if (same != null)
                {
                    same.Instances.Add(comp);
                    continue;
                }
                Candidate candidate = new Candidate { Source = path };
                candidate.Instances.Add(comp);
                candidate.Name = ModelName(comp, path);
                candidate.Designation = OrderNumbering.Next(ParentDesignation(app, doc, comp), taken);
                candidate.HasDrawing = File.Exists(IndependentNaming.DrawingOf(path));
                // Предложенный номер сразу считается занятым: две детали подряд получат 004 и 005, а не 004 дважды.
                if (candidate.Designation.Length > 0) taken.Add(candidate.Designation);
                candidates.Add(candidate);
            }
            return candidates;
        }

        /// <summary>Верхний чужой узел над компонентом: сборка вне папки изделия; нет — null.</summary>
        private static Component2 ForeignNode(Component2 comp, string productFolder)
        {
            Component2 foreign = null;
            try
            {
                for (Component2 parent = comp.GetParent() as Component2; parent != null; parent = parent.GetParent() as Component2)
                {
                    string path = parent.GetPathName() ?? "";
                    if (path.Length > 0 && !LzkNaming.IsInside(path, productFolder)) foreign = parent;
                }
            }
            catch (COMException ex)
            {
                Log.Error("Сделать независимым: родительский узел", ex);
            }
            return foreign;
        }

        private static bool AskDetachNode(string part, string node)
        {
            return MessageBox.Show("Деталь «" + part + "» входит в чужой узел «" + node + "» (эталон или узел другого изделия).\n\n" +
                "Сделать независимым весь узел? Тогда он и его детали станут своими для этого изделия, а эталон не изменится.\n\n" +
                "«Нет» — деталь пропускается.", "Сделать независимым", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        /// <summary>
        /// Обозначение сборки, в которой сидит компонент: для детали внутри подсборки — обозначение подсборки,
        /// иначе главной сборки изделия (Т-20). Отсюда берутся код и группы нового номера.
        /// </summary>
        private static string ParentDesignation(ISldWorks app, ModelDoc2 doc, Component2 comp)
        {
            try
            {
                Component2 parent = comp.GetParent() as Component2;
                string path = parent != null ? (parent.GetPathName() ?? "") : (doc.GetPathName() ?? "");
                ModelDoc2 model = parent != null ? parent.GetModelDoc2() as ModelDoc2 : doc;
                return Designation(model, path);
            }
            catch (COMException ex)
            {
                Log.Error("Сделать независимым: родительская сборка", ex);
                return "";
            }
        }

        /// <summary>Обозначение документа: свойство «Обозначение», иначе разбор имени файла.</summary>
        private static string Designation(ModelDoc2 model, string path)
        {
            string value = Property(model, "Обозначение");
            if (value.Length > 0) return value;
            ParsedName parsed = DesignationParser.Parse(Path.GetFileNameWithoutExtension(path ?? "") ?? "", " ");
            return parsed.HasDesignation ? parsed.Root : "";
        }

        /// <summary>Наименование исходной модели: свойство, иначе часть имени файла после обозначения.</summary>
        private static string ModelName(Component2 comp, string path)
        {
            string value = Property(comp.GetModelDoc2() as ModelDoc2, "Наименование");
            if (value.Length > 0) return value;
            ParsedName parsed = DesignationParser.Parse(Path.GetFileNameWithoutExtension(path ?? "") ?? "", " ");
            string title = (parsed.Title ?? "").Trim();
            return title.Length > 0 ? title : Path.GetFileNameWithoutExtension(path ?? "") ?? "";
        }

        private static string Property(ModelDoc2 model, string name)
        {
            if (model == null) return "";
            try
            {
                PropertyWriter w = new PropertyWriter(model, true);
                string cfg = w.ActiveConfigurationName();
                string value = (w.Resolved(cfg, name) ?? "").Trim();
                if (value.Length == 0) value = (w.Resolved("", name) ?? "").Trim();
                return PropertyWriter.IsEmptyOrTemplate(value) ? "" : value;
            }
            catch (Exception ex)
            {
                Log.Error("Сделать независимым: свойство " + name, ex);
                return "";
            }
        }

        // ------------------------------------------------------------------ отвязка модели (Т-21, Т-23)
        private static void MakeOne(ISldWorks app, ModelDoc2 doc, Candidate candidate, string modelsFolder,
            bool withDrawing, IndependentLog log)
        {
            string title = Path.GetFileName(candidate.Source);
            string target = IndependentNaming.TargetPath(modelsFolder, candidate.Designation, candidate.Name, candidate.Source);
            if (ExportNaming.TooLong(IndependentNaming.DrawingOf(target)).Length > 0)
            {
                log.Skip(title, "не сделана независимой: " + ExportNaming.TooLong(IndependentNaming.DrawingOf(target)));
                return;
            }
            string before = Checksum(candidate.Source);
            try
            {
                doc.ClearSelection2(true);
                int selected = 0;
                foreach (Component2 comp in candidate.Instances)
                    if (comp.Select4(true, null, false)) selected++;
                if (selected == 0)
                {
                    log.Skip(title, "компонент не выделяется: обновите дерево и повторите");
                    return;
                }
                if (!((AssemblyDoc)doc).MakeIndependent(target) || !File.Exists(target))
                {
                    log.Skip(title, "SolidWorks не сделал деталь независимой");
                    return;
                }
                doc.ClearSelection2(true);
                IndependentEntry entry = new IndependentEntry
                {
                    Source = candidate.Source, Target = target, Instances = selected
                };
                Rename(app, target, log, title);
                if (withDrawing) Drawing(app, candidate.Source, target, entry, log);
                log.Created.Add(entry);
            }
            catch (Exception ex)
            {
                Log.Error("Сделать независимым: " + candidate.Source, ex);
                log.Skip(title, "не сделано: " + ex.Message);
            }
            finally
            {
                // Эталон общий для всех заказов: кнопка обязана оставить его нетронутым (Т-24).
                log.Source(candidate.Source, before.Length > 0 && before == Checksum(candidate.Source));
            }
        }

        /// <summary>
        /// Реквизиты новой модели (Т-23): прежние «Обозначение» и «Наименование» — от эталона, поэтому они
        /// стираются, а синхронизация заполняет их из нового имени файла. «Ревизия» БЧ-детали эталона к новой детали
        /// отношения не имеет (Т-22, аудит 23.09.2026) — тоже стирается. Материал, подписи и «Операции» остаются как
        /// были: это работа конструктора, а не кнопки.
        /// </summary>
        private static void Rename(ISldWorks app, string target, IndependentLog log, string title)
        {
            ModelDoc2 model = null;
            try
            {
                model = app.GetOpenDocumentByName(target) as ModelDoc2;
                if (model == null)
                {
                    log.Skip(title, "новая деталь не открыта: реквизиты не обновлены");
                    return;
                }
                PropertyWriter w = new PropertyWriter(model, false);
                foreach (string cfg in new[] { "" }.Concat(w.ConfigurationNames()).Distinct())
                    foreach (string name in new[] { "Обозначение", "Наименование", PropertyDictionary.RevisionName })
                        if (w.Exists(cfg, name)) w.Set(cfg, name, "");
                SyncService.SyncModel(app, model, new SyncRequest { Reason = "сделано независимым" });
                int errors = 0, warnings = 0;
                // Несохранённый документ раньше засчитывался как готовый: реквизиты оставались только в памяти
                // и терялись при закрытии (аудит 20.09.2026).
                if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                {
                    Log.Warn("Сделать независимым: деталь не сохранена (код " + errors + ") " + target);
                    log.Skip(title, "новая деталь не сохранена (код " + errors + "): откройте её и сохраните");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Сделать независимым: реквизиты " + target, ex);
                log.Skip(title, "реквизиты новой детали не обновлены: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ чертёж (Т-22)
        private static void Drawing(ISldWorks app, string source, string target, IndependentEntry entry, IndependentLog log)
        {
            string sourceDrawing = IndependentNaming.DrawingOf(source);
            string title = Path.GetFileName(sourceDrawing);
            if (!File.Exists(sourceDrawing))
            {
                log.Skip(title, "чертежа у исходной модели нет: копировать нечего");
                return;
            }
            string targetDrawing = IndependentNaming.DrawingOf(target);
            ModelDoc2 drawing = null;
            try
            {
                File.Copy(sourceDrawing, targetDrawing, true);
                File.SetAttributes(targetDrawing, FileAttributes.Normal);
                // Ссылка меняется у закрытого файла: так виды не успевают перестроиться по эталону
                // и SolidWorks не спрашивает про «файл только для чтения».
                if (!app.ReplaceReferencedDocument(targetDrawing, source, target))
                    Log.Warn("Сделать независимым: ReplaceReferencedDocument отказал для " + targetDrawing);

                int errors = 0, warnings = 0;
                drawing = app.OpenDoc6(targetDrawing, (int)swDocumentTypes_e.swDocDRAWING,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as ModelDoc2;
                if (drawing == null)
                {
                    log.Skip(title, "копия чертежа не открылась (код " + errors + ")");
                    return;
                }
                DrawingDoc drw = (DrawingDoc)drawing;
                entry.Views = Retarget(drw, source, target);
                ClearRevision(drawing, drw);
                drawing.ForceRebuild3(false);
                entry.Dangling = Dangling(drw);
                int saveErrors = 0, saveWarnings = 0;
                // Чертёж засчитывался готовым независимо от того, лёг он на диск или нет (аудит 20.09.2026).
                if (!drawing.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings))
                {
                    Log.Warn("Сделать независимым: чертёж не сохранён (код " + saveErrors + ") " + targetDrawing);
                    log.Skip(title, "чертёж не сохранён (код " + saveErrors + "): откройте его и сохраните");
                    return;
                }
                entry.Drawing = targetDrawing;
            }
            catch (Exception ex)
            {
                Log.Error("Сделать независимым: чертёж " + targetDrawing, ex);
                log.Skip(title, "чертёж не сделан: " + ex.Message);
            }
            finally
            {
                // Чертёж только закрываем; RCW не освобождаем — его держит EventHub до DestroyNotify.
                if (drawing != null) app.CloseDoc(drawing.GetPathName());
            }
        }

        /// <summary>
        /// Виды всех листов переводятся на новую модель (Т-22). Замена ссылки в закрытом файле уже сделала
        /// главное; здесь остаются виды, которые её не приняли, — им модель назначается поимённо.
        /// </summary>
        private static int Retarget(DrawingDoc drw, string source, string target)
        {
            int changed = 0;
            foreach (View view in Views(drw))
            {
                string referenced;
                try
                {
                    ModelDoc2 model = view.ReferencedDocument as ModelDoc2;
                    referenced = model != null ? (model.GetPathName() ?? "") : "";
                }
                catch (COMException ex)
                {
                    Log.Error("Сделать независимым: вид " + view.Name, ex);
                    continue;
                }
                if (string.Equals(referenced, target, StringComparison.OrdinalIgnoreCase))
                {
                    changed++;
                    continue;
                }
                if (!string.Equals(referenced, source, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (drw.ReplaceViewModel(target, new object[] { view }, new object[] { null })) changed++;
                }
                catch (COMException ex)
                {
                    Log.Error("Сделать независимым: перепривязка вида " + view.Name, ex);
                }
            }
            return changed;
        }

        /// <summary>
        /// Ревизия эталона к новой детали отношения не имеет (Т-22): свойство «Revision» и надписи
        /// «Revision2…5» в форматке очищаются, дальше их ведёт кнопка К-7 «Новая ревизия».
        /// </summary>
        private static void ClearRevision(ModelDoc2 drawing, DrawingDoc drw)
        {
            try
            {
                PropertyWriter w = new PropertyWriter(drawing, false);
                foreach (string cfg in new[] { "" }.Concat(w.ConfigurationNames()).Distinct())
                    if (w.Exists(cfg, "Revision")) w.Set(cfg, "Revision", "");
            }
            catch (Exception ex)
            {
                Log.Error("Сделать независимым: свойство Revision", ex);
            }
            string[] sheets = drw.GetSheetNames() as string[] ?? new string[0];
            string active = "";
            try
            {
                Sheet sheet = drw.GetCurrentSheet() as Sheet;
                if (sheet != null) active = sheet.GetName() ?? "";
            }
            catch (COMException ex)
            {
                Log.Error("Сделать независимым: текущий лист", ex);
            }
            foreach (string name in sheets)
            {
                try
                {
                    drw.ActivateSheet(name);
                    View format = drw.GetFirstView() as View;
                    object noteObject = format != null ? format.GetFirstNote() : null;
                    while (noteObject != null)
                    {
                        Note note = (Note)noteObject;
                        string noteName = note.GetName() ?? "";
                        // Пустой текст SolidWorks не принимает, поэтому в графу ставится пробел:
                        // на печати он не виден, а надпись остаётся на месте для кнопки К-7.
                        if (noteName.StartsWith("Revision", StringComparison.OrdinalIgnoreCase)) note.SetText(" ");
                        noteObject = note.GetNext();
                    }
                }
                catch (COMException ex)
                {
                    Log.Error("Сделать независимым: надписи ревизии листа " + name, ex);
                }
            }
            if (active.Length > 0)
            {
                try
                {
                    drw.ActivateSheet(active);
                }
                catch (COMException ex)
                {
                    Log.Error("Сделать независимым: возврат на лист " + active, ex);
                }
            }
        }

        /// <summary>Виды всех листов чертежа, кроме самих листов-форматок.</summary>
        private static IEnumerable<View> Views(DrawingDoc drw)
        {
            object[] sheets = drw.GetViews() as object[] ?? new object[0];
            foreach (object sheetObject in sheets)
            {
                object[] views = sheetObject as object[];
                if (views == null) continue;
                // Первый элемент листа — сам лист с форматкой, модели у него нет.
                for (int i = 1; i < views.Length; i++)
                {
                    View view = views[i] as View;
                    if (view != null) yield return view;
                }
            }
        }

        /// <summary>Оборванные размеры копии чертежа — их правит конструктор (Т-22).</summary>
        private static int Dangling(DrawingDoc drw)
        {
            int count = 0;
            foreach (View view in Views(drw))
            {
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
                        Log.Error("Сделать независимым: размер чертежа", ex);
                    }
                    dimension = dimension.GetNext5() as DisplayDimension;
                }
            }
            return count;
        }

        // ------------------------------------------------------------------ окна и отчёт
        private static bool Ask(ISldWorks app, List<Candidate> candidates)
        {
            string list = string.Join(Environment.NewLine, candidates
                .Select(c => "  " + Path.GetFileName(c.Source) + "  →  " +
                             ExportNaming.Stem(c.Designation, c.Name, c.Source) + Path.GetExtension(c.Source) +
                             (c.HasDrawing ? " (с чертежом)" : ""))
                .ToArray());
            string text = "Будут сделаны своими копиями в «" + LzkNaming.ModelsFolder + "»:" + Environment.NewLine +
                Environment.NewLine + list + Environment.NewLine + Environment.NewLine +
                "Исходные модели не изменятся. Продолжить?";
            return MessageBox.Show(text, "ЕСКД: сделать независимым", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                == DialogResult.Yes;
        }

        private static void Show(ISldWorks app, IndependentLog log)
        {
            List<Notice> notices = Notices.FromIndependent(log);
            NoticeLevel worst = Notices.Max(notices);
            string headline = log.Created.Count == 0 ? "Ничего не сделано" : "Сделано независимыми: " + log.Created.Count;
            string details = "Пропущено: " + log.Skipped.Count +
                (log.Dangling > 0 ? "; оборванных размеров в чертежах: " + log.Dangling : "") + Environment.NewLine +
                (log.SourcesUntouched ? "Исходные модели не изменены (контрольные суммы совпали)." : "ВНИМАНИЕ: исходная модель изменилась.");
            NoticeForm.Present(app, "ЕСКД: сделать независимым", headline, details, notices,
                log.Created.Count == 0 ? NoticeLevel.Critical : worst == NoticeLevel.Info ? (NoticeLevel?)null : worst);
        }

        private static void Fail(ISldWorks app, bool interactive, string text)
        {
            LastOutcome = "error|" + text;
            Status(app, "");
            if (interactive) MessageBox.Show(text, "ЕСКД: сделать независимым", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                Log.Error("Сделать независимым: строка состояния", ex);
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
                Log.Error("Сделать независимым: контрольная сумма " + path, ex);
                return "";
            }
        }
    }
}
