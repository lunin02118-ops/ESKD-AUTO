using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>Итог применения окна «Проверить изделие» — строка состояния и автотесты.</summary>
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
        /// <summary>Ответов «Оставить как есть», записанных в модели.</summary>
        public int Kept;
        /// <summary>Изменённых документов, которые не сохранены: галочка снята или «Применить без сохранения».</summary>
        public int Unsaved;
        /// <summary>Свойств главной сборки, обновлённых проверкой.</summary>
        public int AssemblyChanges;
        public bool AssemblySaved;
        /// <summary>Не выполнено — почему (сборка не сохранена). Пусто — выполнено.</summary>
        public string Refused = "";
        /// <summary>Замечания плана: документы, которые не обновляются, и почему. Уходят в отчёт проверки.</summary>
        public readonly List<string> Warnings = new List<string>();
        /// <summary>Ошибки записи и сохранения — «документ: что не так». Показываются окном после записи.</summary>
        public readonly List<string> Errors = new List<string>();
        /// <summary>Что ещё ответить: тот же вопрос о материале в других исполнениях — «документ: подсказка».</summary>
        public readonly List<string> Hints = new List<string>();

        public string StatusLine()
        {
            if (Refused.Length > 0) return "ЕСКД: проверка изделия не выполнена — " + Refused;
            return string.Format("ЕСКД: деталей {0}, обновлено {1}, сохранено {2}, материалов назначено {3}{4}{5}{6}{7}{8}{9}{10}",
                Parts, Changed, Saved, MaterialsAssigned, Formats > 0 ? ", формат из чертежа " + Formats : "",
                Renamed > 0 ? ", обозначение по имени файла " + Renamed : "",
                Kept > 0 ? ", оставлено как есть " + Kept : "",
                Unsaved > 0 ? ", не сохранено (сохраните сами) " + Unsaved : "",
                AssemblyChanges > 0 ? ", свойств сборки " + AssemblyChanges : "",
                AssemblySaved ? ", сборка сохранена" : "",
                Failed > 0 ? ", с ошибками " + Failed : "");
        }
    }

    /// <summary>Документ изделия, как его видит проверка: путь, модель, где лежит.</summary>
    public sealed class ProductNode
    {
        public string Path = "";
        public ModelDoc2 Model;
        public bool IsAssembly;
        /// <summary>Главная сборка — та, на которой нажата кнопка.</summary>
        public bool IsTop;
        public bool IsPurchased;
        public bool InProduct;
        /// <summary>До проверки в документе были несохранённые правки конструктора.</summary>
        public bool Edited;
        /// <summary>
        /// Исполнения (конфигурации), стоящие в изделии, в порядке появления; пусто — не известны. Проверка сверяет каждое,
        /// а не только активное в файле (сверка SW API 23.09.2026, находка 14).
        /// </summary>
        public readonly List<string> Configurations = new List<string>();
    }

    /// <summary>План окна «Проверить изделие» вместе с документами, к которым он относится.</summary>
    public sealed class ReviewSession
    {
        public readonly ReviewPlan Plan = new ReviewPlan();
        public readonly BatchReport Report = new BatchReport();
        public string ProductFolder = "";

        internal readonly List<Target> Targets = new List<Target>();

        /// <summary>Документ, который проверка может менять, и что она про него узнала без записи.</summary>
        internal sealed class Target
        {
            public ProductNode Node;
            public string Title = "";
            /// <summary>Синхронизация вхолостую: что изменится и о чём предупредить.</summary>
            public SyncReport Planned;
            public bool Stock;
            public bool Format;
            /// <summary>Замечания по прокату, которые окно не решает: типоразмера нет в библиотеке.</summary>
            public readonly List<string> StockNotes = new List<string>();
            public bool Touched;
        }

        /// <summary>Синхронизация вхолостую документа; null — документ не в плане.</summary>
        public SyncReport Planned(string path)
        {
            Target t = Targets.FirstOrDefault(x => string.Equals(x.Node.Path, path ?? "", StringComparison.OrdinalIgnoreCase));
            return t != null ? t.Planned : null;
        }
    }

    /// <summary>
    /// Кнопка «Проверить изделие» — один порядок работы (решение владельца 23.09.2026, журнал З-27). Всё, что раньше
    /// делали «Синхронизировать» на сборке, окно выбора материала после сохранения и окно обозначений, собрано здесь:
    ///   1) <see cref="Prepare"/> — план без записи: что обновится само (имена, дробь, масса, формат, материал с одним
    ///      подходящим), о чём спросить (материал к профилю, обозначение не по имени файла), какие файлы сохранить;
    ///   2) окно <see cref="ProductReviewForm"/> — ответы и галочки;
    ///   3) <see cref="Apply"/> — запись и одно сохранение выбранных файлов. Нажатие «Применить и сохранить» и есть
    ///      команда конструктора сохранить: второй раз жать Ctrl+S в каждой детали не нужно.
    ///
    /// Границы — <see cref="DocumentGuard"/>: только документы папки изделия; покупные, стандартные, чужие и открытые
    /// только для чтения не меняются. Документы с несохранёнными правками конструктора в списке сохранения без галочки.
    /// </summary>
    public static class ProductReviewService
    {
        /// <summary>
        /// Идёт применение. Сохранения внутри него не должны поднимать синхронизацию по событию: свойства уже записаны,
        /// а повторный круг завёл бы ещё и задачу подбора материала с окном.
        /// </summary>
        public static bool Running { get; private set; }

        private const string UnitsChange = "единицы массы документа — как в MProp";

        // ------------------------------------------------------------------ состав
        /// <summary>
        /// Документы изделия: главная сборка первой, затем каждый компонент по одному разу. Пропавший файл и ссылка за
        /// пределы заказа — находки правила «а» в report (null — без находок).
        /// </summary>
        public static List<ProductNode> Collect(ISldWorks app, ModelDoc2 doc, string assemblyPath, string productFolder,
            CheckReport report)
        {
            string cipher = LzkNaming.Cipher(productFolder, assemblyPath);
            List<ProductNode> nodes = new List<ProductNode>
            {
                new ProductNode { Path = assemblyPath, Model = doc, IsAssembly = true, IsTop = true, InProduct = true }
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
            Dictionary<string, ProductNode> byPath = new Dictionary<string, ProductNode>(StringComparer.OrdinalIgnoreCase);
            foreach (object o in comps)
            {
                Component2 comp = o as Component2;
                if (comp == null) continue;
                try
                {
                    string path = comp.GetPathName() ?? "";
                    string name = path.Length > 0 ? Path.GetFileName(path) : (comp.Name2 ?? "компонент");
                    // Пропавший файл проверяется раньше подавления: SolidWorks показывает потерянный компонент
                    // подавленным, а это не исключение из состава, а оборванная ссылка.
                    if (path.Length == 0 || !File.Exists(path))
                    {
                        if (report != null)
                            report.Add(CheckRules.References, CheckRules.LevelOf(CheckRules.References), name, "файл компонента не найден");
                        continue;
                    }
                    if (ComponentState.Suppressed(comp)) continue;
                    ProductNode known;
                    if (byPath.TryGetValue(path, out known))
                    {
                        Use(known, comp);
                        continue;
                    }
                    if (!seen.Add(path)) continue;
                    ModelDoc2 model = comp.GetModelDoc2() as ModelDoc2;
                    if (model == null)
                    {
                        if (report != null)
                            report.Add(CheckRules.References, CheckRules.LevelOf(CheckRules.References), name, "модель не загрузилась");
                        continue;
                    }
                    bool inProduct = LzkNaming.IsInside(path, productFolder);
                    if (!inProduct && report != null && !Allowed(path, assemblyPath))
                        report.Add(CheckRules.References, CheckRules.LevelOf(CheckRules.References), name,
                            "ссылка за пределами заказа и базы (или на другой заказ): " + path);
                    ProductNode node = new ProductNode
                    {
                        Path = path,
                        Model = model,
                        IsAssembly = model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY,
                        // Покупное — и по свойствам (SProp), и по папке заказа: в боевых заказах фурнитуру
                        // складывают в «Стандартные изделия и фурнитура», не помечая каждую модель.
                        IsPurchased = ComponentKind.IsPurchased(null, model, path, "Проверка изделия", cipher),
                        InProduct = inProduct,
                        // До первой записи: потом правки конструктора уже не отличить от записанного проверкой.
                        Edited = DocumentGuard.HasUserEdits(model)
                    };
                    Use(node, comp);
                    nodes.Add(node);
                    byPath[path] = node;
                }
                catch (COMException ex)
                {
                    Log.Error("Проверка изделия: компонент", ex);
                    if (report != null)
                        report.Add(CheckRules.References, CheckRules.LevelOf(CheckRules.References), NameOf(comp),
                            "компонент не прочитан: " + ex.Message.Trim());
                }
            }
            return nodes;
        }

        /// <summary>Исполнение компонента — в список исполнений изделия; исключённый из спецификации не в счёт, как в выгрузке.</summary>
        private static void Use(ProductNode node, Component2 comp)
        {
            try
            {
                if (comp.ExcludeFromBOM) return;
                string cfg = comp.ReferencedConfiguration ?? "";
                if (cfg.Length > 0 && !node.Configurations.Contains(cfg, StringComparer.OrdinalIgnoreCase)) node.Configurations.Add(cfg);
            }
            catch (COMException ex)
            {
                Log.Error("Проверка изделия: исполнение компонента", ex);
            }
        }

        private static string NameOf(Component2 comp)
        {
            try
            {
                return comp.Name2 ?? "компонент";
            }
            catch (COMException)
            {
                return "компонент";
            }
        }

        /// <summary>Ссылка допустима: этот же заказ, база эталонов или библиотека стандартных изделий. Деталь чужого
        /// заказа — нет (Т-32а): тот заказ закроют в архив, и ссылка оборвётся.</summary>
        private static bool Allowed(string path, string assemblyPath)
        {
            ProductLocation location = ProductLocator.Locate(path);
            if (location.InBase) return true;
            if (!location.InOrder) return false;
            string own = ProductLocator.Locate(assemblyPath).OrderFolder;
            return own.Length == 0 || string.Equals(location.OrderFolder.TrimEnd('\\'), own.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ план
        /// <summary>
        /// План без записи в модели. full = false — только синхронизация вхолостую и прокат (отчёт без окна): форматы из
        /// чертежей не сверяются, закрытые чертежи ради этого не открываются.
        /// </summary>
        public static ReviewSession Prepare(ISldWorks app, string productFolder, string cipher, List<ProductNode> nodes, bool full)
        {
            ReviewSession session = new ReviewSession { ProductFolder = productFolder ?? "" };
            Settings settings = Settings.Read();
            Dictionary<string, ReviewQuestion> materials = new Dictionary<string, ReviewQuestion>(StringComparer.Ordinal);
            // Сначала подсборки и детали, главная сборка — последней: она сохраняется после своих компонентов.
            foreach (ProductNode node in nodes.Where(n => !n.IsTop).Concat(nodes.Where(n => n.IsTop)))
            {
                if (!node.InProduct || node.IsPurchased || node.Model == null) continue;
                try
                {
                    string why = DocumentGuard.WhyNotWrite(node.Model, node.Path, productFolder, cipher);
                    if (why.Length > 0)
                    {
                        session.Report.Skipped++;
                        // Файл только для чтения (архив, чужой сеанс на общей папке) — замечание, только если в нём есть
                        // что обновить: готовое изделие только для чтения — норма, и «Готово к производству» не должно
                        // из-за этого отказывать (ревью 23.09.2026).
                        if (DocumentGuard.IsQuietRefusal(why) || !NeedsWork(app, node))
                            Log.Info("Проверка изделия: " + Path.GetFileName(node.Path) + " не меняется — " + why);
                        else session.Report.Warnings.Add(Path.GetFileName(node.Path) + ": не обновляется — " + why);
                        continue;
                    }
                    SyncReport planned = SyncService.SyncModel(app, node.Model,
                        new SyncRequest { Reason = "проверка изделия (что обновится)", DryRun = true });
                    if (planned.Skipped) { session.Report.Skipped++; continue; }
                    ReviewSession.Target target = new ReviewSession.Target { Node = node, Title = DocInfo.TitleOf(node.Model), Planned = planned };
                    session.Targets.Add(target);
                    if (!node.IsAssembly) session.Report.Parts++;

                    ReviewFile file = session.Plan.Add(node.Path, target.Title, node.Edited);
                    foreach (string operation in planned.Operations) file.Changes.Add(Change(operation, target.Title));
                    if (planned.Warnings.Any(IsUnits)) file.Changes.Add(UnitsChange);
                    if (planned.Designation != null) AskDesignation(session.Plan, node, target.Title, planned.Designation);
                    if (!node.IsAssembly && settings.AutoStockMaterial) PlanStock(app, session, target, file, materials);
                    if (full) PlanFormat(app, target, file);
                }
                catch (Exception ex)
                {
                    Log.Error("Проверка изделия: план " + Path.GetFileName(node.Path), ex);
                    session.Report.Failed++;
                    session.Report.Warnings.Add(Path.GetFileName(node.Path) + ": не разобран — " + ex.Message);
                }
            }
            return session;
        }

        /// <summary>Синхронизация вхолостую нашла, что обновить (или не удалась — тогда считаем, что нашла).</summary>
        private static bool NeedsWork(ISldWorks app, ProductNode node)
        {
            try
            {
                SyncReport planned = SyncService.SyncModel(app, node.Model,
                    new SyncRequest { Reason = "проверка изделия (документ только для чтения)", DryRun = true });
                return !planned.Skipped && (planned.Operations.Count > 0 || planned.Designation != null);
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: план " + Path.GetFileName(node.Path), ex);
                return true;
            }
        }

        /// <summary>«Деталь [00] Масса: 1 → 2» → «[00] Масса: 1 → 2»: имя документа в окне стоит отдельно.</summary>
        private static string Change(string operation, string title)
        {
            string text = operation ?? "";
            if (title.Length > 0 && text.StartsWith(title + " ", StringComparison.Ordinal)) text = text.Substring(title.Length + 1);
            return text.Trim();
        }

        private static bool IsUnits(string warning)
        {
            return (warning ?? "").IndexOf(SyncService.MassUnitsWarning, StringComparison.Ordinal) >= 0;
        }

        private static void AskDesignation(ReviewPlan plan, ProductNode node, string title, DesignationMismatch mismatch)
        {
            ReviewQuestion q = new ReviewQuestion
            {
                Kind = ReviewQuestionKind.Designation,
                Key = node.Path,
                Subject = "Обозначение «" + mismatch.Current + "» не совпадает с именем файла",
                Current = mismatch.Current,
                Expected = mismatch.Expected,
                Fingerprint = ReviewAccepted.Designation(mismatch.Current, mismatch.Expected)
            };
            q.Places.Add(title);
            q.Paths.Add(node.Path);
            q.Options.Add("Взять из имени файла: «" + mismatch.Expected + "»");
            q.Options.Add("Оставить как есть: «" + mismatch.Current + "»");
            q.KeepIndex = 1;
            // Имена тел и элементов («Тело5», «Вырез-Вытянуть3[1]»), которые SolidWorks переносит при разделении тел, —
            // явная случайность: ответ предложен заранее (как галочка прежнего окна обозначений, 22.09.2026).
            if (!DesignationParser.LooksLikeDesignation(mismatch.Current)) q.Answer = 0;
            plan.Questions.Add(q);
        }

        /// <summary>Материал по профилю и толщине (Р-8): один подходящий — само; спорный — вопрос на всё изделие.</summary>
        private static void PlanStock(ISldWorks app, ReviewSession session, ReviewSession.Target target, ReviewFile file,
            Dictionary<string, ReviewQuestion> materials)
        {
            ModelDoc2 model = target.Node.Model;
            List<StockFinding> findings = StockService.Inspect(app, model);
            if (findings.Count == 0) return;
            target.Stock = true;
            List<StockFinding> pending = StockService.PendingDecisions(target.Node.Path, findings, StockService.Accepted(model));
            foreach (StockFinding f in findings)
            {
                if (f.Verdict == StockVerdict.Assign && f.Chosen != null)
                    file.Changes.Add("материал «" + StockText.Describe(f.Chosen) + "» — по типоразмеру «" + f.Request.Size + "»" +
                        (f.Folder.Length > 0 && f.Folder != StockService.SheetFolderName ? " («" + f.Folder + "»)" : ""));
                else if (f.Verdict == StockVerdict.NotInLibrary || f.Verdict == StockVerdict.Unclear)
                    target.StockNotes.Add(StockService.Message(f));
            }
            foreach (StockFinding f in pending)
            {
                string key = StockService.DecisionKey(f);
                ReviewQuestion q;
                if (!materials.TryGetValue(key, out q))
                {
                    string current = (f.CurrentMaterial ?? "").Trim();
                    q = new ReviewQuestion
                    {
                        Kind = ReviewQuestionKind.Material,
                        Key = key,
                        Subject = "Материал для «" + f.Request.Size + "»" + (f.Request.Gost.Length > 0 ? " " + f.Request.Gost : "") +
                            (current.Length > 0 ? ": сейчас «" + current + "» — не подходит к профилю" : ": не назначен, подходят несколько"),
                        Current = current,
                        Fingerprint = ReviewAccepted.Material(key)
                    };
                    foreach (MaterialInfo info in f.Match.Candidates)
                    {
                        q.Materials.Add(info);
                        q.Options.Add(StockText.Describe(info));
                    }
                    if (current.Length > 0)
                    {
                        q.KeepIndex = q.Options.Count;
                        q.Options.Add(StockText.KeepCaption(current));
                    }
                    materials.Add(key, q);
                    session.Plan.Questions.Add(q);
                }
                if (!q.Paths.Contains(target.Node.Path, StringComparer.OrdinalIgnoreCase))
                {
                    q.Paths.Add(target.Node.Path);
                    q.Places.Add(target.Title);
                }
            }
        }

        /// <summary>«Формат» из чертежа рядом — вхолостую: что изменится, без записи.</summary>
        private static void PlanFormat(ISldWorks app, ReviewSession.Target target, ReviewFile file)
        {
            List<string> operations = new List<string>();
            if (DrawingFormatService.Backfill(app, target.Node.Model, true, operations) == 0) return;
            target.Format = true;
            foreach (string operation in operations) file.Changes.Add(Change(operation, target.Title) + " (по чертежу)");
        }

        /// <summary>
        /// Находки плана для отчёта проверки: вопросы без ответа, типоразмеры вне библиотеки, документы, которые не
        /// обновляются. Обозначение не по имени файла отчёт узнаёт сам — из синхронизации вхолостую.
        /// </summary>
        public static void Findings(ReviewSession session, CheckReport report)
        {
            if (session == null || report == null) return;
            foreach (string warning in session.Report.Warnings)
            {
                int colon = warning.IndexOf(": ", StringComparison.Ordinal);
                report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), colon > 0 ? warning.Substring(0, colon) : "",
                    colon > 0 ? warning.Substring(colon + 2) : warning);
            }
            foreach (ReviewSession.Target t in session.Targets)
                foreach (string note in t.StockNotes)
                    report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), Path.GetFileName(t.Node.Path), note);
            // Вопрос стоит в окне — в его «Замечаниях» он не повторяется (Fixable); в отчёте без окна — остаётся.
            foreach (ReviewQuestion q in session.Plan.Questions.Where(x => x.Kind == ReviewQuestionKind.Material && !x.Answered))
                foreach (string path in q.Paths)
                    report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), Path.GetFileName(path),
                        q.Subject + ": выберите материал — кнопка «Проверить изделие»").Fixable = true;
        }

        // ------------------------------------------------------------------ применение
        /// <summary>
        /// Записать план с ответами конструктора и сохранить документы с галочкой (save = false — «Применить без
        /// сохранения»: всё остаётся в открытых документах).
        /// </summary>
        public static BatchReport Apply(ISldWorks app, ReviewSession session, bool save)
        {
            BatchReport batch = session.Report;
            Running = true;
            try
            {
                foreach (ReviewSession.Target t in session.Targets)
                {
                    try
                    {
                        ApplyTo(app, session, t, batch);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Проверка изделия: применение " + t.Title, ex);
                        batch.Failed++;
                        batch.Errors.Add(t.Title + ": не применено — " + ex.Message);
                    }
                }
                foreach (ReviewSession.Target t in session.Targets)
                {
                    if (!t.Touched) continue;
                    ReviewFile file = session.Plan.File(t.Node.Path);
                    if (!save || file == null || !file.Save)
                    {
                        batch.Unsaved++;
                        continue;
                    }
                    if (!Save(t, batch)) continue;
                    // Главный документ — деталь у «Синхронизировать» в детали: он считается среди сохранённых деталей.
                    if (t.Node.IsTop && t.Node.IsAssembly) batch.AssemblySaved = true;
                    else batch.Saved++;
                }
            }
            finally
            {
                Running = false;
            }
            return batch;
        }

        private static void ApplyTo(ISldWorks app, ReviewSession session, ReviewSession.Target t, BatchReport batch)
        {
            ModelDoc2 model = t.Node.Model;
            ReviewPlan plan = session.Plan;
            ReviewFile file = plan.File(t.Node.Path);
            List<ReviewQuestion> questions = plan.QuestionsFor(t.Node.Path).ToList();
            bool fromFile = questions.Any(q => q.FromFile);

            // 1. Реквизиты — как при сохранении, по имени файла; обозначение из имени файла — если так ответил конструктор.
            if ((file != null && file.Changes.Count > 0) || fromFile)
            {
                SyncReport sync = SyncService.SyncModel(app, model, new SyncRequest
                {
                    Reason = "проверка изделия", Stock = false, DesignationFromFile = fromFile
                });
                batch.Failed += sync.Failures;
                bool units = sync.Warnings.Any(IsUnits);
                if (sync.Changes > 0 || units) t.Touched = true;
                if (t.Node.IsTop && t.Node.IsAssembly) batch.AssemblyChanges += sync.Changes;
                else if (sync.Changes > 0) batch.Changed++;
                if (fromFile) batch.Renamed++;
            }

            // 2. Материал по профилю: единственный подходящий — сам; спорный — по ответу; без ответа — не меняется.
            if (t.Stock) ApplyStock(app, plan, t, batch);

            // 3. «Формат» из чертежа.
            if (t.Format && DrawingFormatService.Backfill(app, model) > 0)
            {
                batch.Formats++;
                t.Touched = true;
            }

            // 4. «Оставить как есть» — в саму модель: вопрос не вернётся, пока не сменятся профиль, материал или имя файла.
            //    Все ответы документа — одной записью: ответ этой же записи не вытесняет другой.
            List<string> kept = questions.Where(x => x.Kept).Select(x => x.Fingerprint).ToList();
            if (kept.Count > 0 && Accept(model, kept))
            {
                batch.Kept += kept.Count;
                t.Touched = true;
            }
        }

        private static void ApplyStock(ISldWorks app, ReviewPlan plan, ReviewSession.Target t, BatchReport batch)
        {
            ModelDoc2 model = t.Node.Model;
            // Осмотр заново: указатели на тела, снятые при плане, после записи свойств могут быть недействительны.
            List<StockFinding> findings = StockService.Inspect(app, model);
            List<StockFinding> pending = StockService.PendingDecisions(t.Node.Path, findings, StockService.Accepted(model));
            foreach (StockFinding f in pending)
            {
                ReviewQuestion q = plan.Find(ReviewQuestionKind.Material, StockService.DecisionKey(f));
                if (q != null && q.Kept)
                {
                    f.Kept = true;
                    f.Chosen = null;
                }
                else if (q != null && q.ChosenMaterial != null)
                {
                    f.Chosen = q.ChosenMaterial;
                }
                else
                {
                    // Без ответа материал конструктора не заменяется и из нескольких не выбирается.
                    f.Chosen = null;
                }
            }
            SyncReport applied = new SyncReport();
            int assigned = StockService.Apply(app, model, findings, applied);
            foreach (string warning in applied.Warnings) batch.Errors.Add(t.Title + ": " + warning);
            foreach (string operation in applied.Operations) Log.Info(t.Title + ": " + operation);
            foreach (string hint in applied.Hints)
            {
                Log.Info(t.Title + ": " + hint);
                batch.Hints.Add(t.Title + ": " + hint);
            }
            batch.Failed += applied.Failures;
            StockService.RememberKept(t.Node.Path, findings);
            if (assigned == 0) return;
            // Материал уже в детали: даже если дальше что-то сорвётся, документ изменён и попадёт в сохранение.
            t.Touched = true;
            batch.MaterialsAssigned += assigned;
            SyncService.SyncModel(app, model, new SyncRequest
            {
                Reason = "проверка изделия (материал по типоразмеру)", Names = false, Signatures = false, Stock = false
            });
            // Перестроение без сохранения: деталь может остаться несохранённой (галочка снята), и SolidWorks спросил бы
            // «перестроить?» при её сохранении конструктором (X05).
            model.EditRebuild3();
        }

        /// <summary>Ответы «Оставить как есть» — в свойство модели. true — свойство изменено.</summary>
        private static bool Accept(ModelDoc2 model, List<string> fingerprints)
        {
            PropertyWriter w = new PropertyWriter(model, Settings.Read().DryRun);
            string raw = w.Raw("", ReviewAccepted.PropertyName);
            string value = ReviewAccepted.Add(raw, fingerprints);
            if (string.Equals(value, raw ?? "", StringComparison.Ordinal)) return false;
            w.Set("", ReviewAccepted.PropertyName, value);
            return w.Changes > 0;
        }

        private static bool Save(ReviewSession.Target t, BatchReport batch)
        {
            ModelDoc2 doc = t.Node.Model;
            int errors = 0, warnings = 0;
            try
            {
                // Save3 сборки без swSaveAsOptions_SaveReferenced её изменённые компоненты не пишет (e2e D18, проверено
                // 23.09.2026): деталь с правками конструктора и деталь без галочки остаются несохранёнными.
                // Неперестроенную модель SolidWorks сохраняет с вопросом «перестроить?» — перестраиваем сами (X05).
                doc.EditRebuild3();
                if (doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings)) return true;
                batch.Failed++;
                batch.Errors.Add(t.Title + string.Format(": не сохранён (errors={0}, warnings={1})", errors, warnings));
                return false;
            }
            catch (COMException ex)
            {
                Log.Error("Проверка изделия: сохранение " + t.Title, ex);
                batch.Failed++;
                batch.Errors.Add(t.Title + ": не сохранён — " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ без окна
        /// <summary>
        /// Обновить изделие без окна — автотесты и работа без интерфейса (прежний «обход изделия»): всё, что обновляется
        /// само, и единственный подходящий материал вместо неподходящего; спорное — без ответа. Сохраняются изменённые
        /// документы без несохранённых правок конструктора.
        /// </summary>
        public static BatchReport SyncProduct(ISldWorks app, ModelDoc2 assembly)
        {
            string assemblyPath = DocInfo.PathOf(assembly);
            if (assemblyPath.Length == 0)
                return new BatchReport { Refused = "сборка не сохранена" };
            string productFolder = ProductFolderOf(assemblyPath);
            string cipher = LzkNaming.Cipher(productFolder, assemblyPath);
            bool topEdited = DocumentGuard.HasUserEdits(assembly);
            List<ProductNode> nodes = Collect(app, assembly, assemblyPath, productFolder, null);
            nodes[0].Edited = topEdited;
            ReviewSession session = Prepare(app, productFolder, cipher, nodes, true);
            session.Plan.AnswerSilently();
            return Apply(app, session, true);
        }

        /// <summary>Папка изделия сборки: по структуре заказа, иначе папка сборки (или над «01_3D»).</summary>
        public static string ProductFolderOf(string assemblyPath)
        {
            ProductLocation location = ProductLocator.Locate(assemblyPath);
            return location.ProductFolder.Length > 0 ? location.ProductFolder : LzkNaming.ProductFolder(assemblyPath);
        }
    }
}
