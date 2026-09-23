using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>Что надстройка решила про материал одной позиции проката.</summary>
    public enum StockVerdict
    {
        /// <summary>Разбираться не с чем: ни профиля, ни листа.</summary>
        None = 0,
        /// <summary>Материал стоит и соответствует геометрии — трогать нечего.</summary>
        Ok = 1,
        /// <summary>Материала нет, в библиотеке ровно один подходящий — подставляем молча.</summary>
        Assign = 2,
        /// <summary>Материала нет или он не соответствует геометрии, а подходящих несколько — спрашиваем конструктора.</summary>
        Choose = 3,
        /// <summary>Типоразмера нет в библиотеке — замечание, ничего не меняем.</summary>
        NotInLibrary = 4,
        /// <summary>
        /// Материал стоит, но геометрии не соответствует, а подходящий в библиотеке один — заменяем (решение владельца
        /// 22.09.2026; до этого только уведомляли). Подходящих несколько — это <see cref="Choose"/>.
        /// </summary>
        Replace = 5
    }

    /// <summary>Одна позиция проката детали: папка списка вырезов или листовая деталь целиком.</summary>
    public sealed class StockFinding
    {
        /// <summary>Имя папки списка вырезов; у листовой детали — «листовой металл».</summary>
        public string Folder = "";

        /// <summary>
        /// Деталь, к которой относится позиция. При обходе изделия в одном окне сходятся позиции разных
        /// деталей, и по одним именам папок списка вырезов («Элемент списка вырезов2») не понять, где они.
        /// Пусто — окно про одну деталь, её имя уже стоит в заголовке.
        /// </summary>
        public string Owner = "";

        public StockRequest Request = new StockRequest();
        public StockMatch Match = new StockMatch();

        /// <summary>Материал SolidWorks, стоящий сейчас (у тел папки или у детали); пусто — не назначен.</summary>
        public string CurrentMaterial = "";

        public StockVerdict Verdict = StockVerdict.None;

        /// <summary>Что назначать. У Assign заполняется сразу, у Choose — после выбора конструктора.</summary>
        public MaterialInfo Chosen;

        /// <summary>Тела папки списка вырезов; пусто — материал назначается детали целиком.</summary>
        public readonly List<Body2> Bodies = new List<Body2>();

        /// <summary>Папка списка вырезов, которой после смены материала нужен UpdateCutList.</summary>
        public BodyFolder FolderObject;

        public bool NeedsChoice { get { return Verdict == StockVerdict.Choose; } }

        /// <summary>
        /// Решает конструктор: подходящих несколько (Choose) или стоящий материал спорит с геометрией и его предлагается
        /// заменить (Replace). Замена выбора конструктора — только с его согласия: «оставить так» или «исправить»
        /// (решение владельца 23.09.2026). Молча Replace применяется лишь там, где спрашивать некому (окно выключено,
        /// работа без интерфейса).
        /// </summary>
        public bool NeedsDecision { get { return Verdict == StockVerdict.Choose || Verdict == StockVerdict.Replace; } }

        /// <summary>Конструктор оставил стоящий материал: позиция не назначается и в этом сеансе больше не спрашивается.</summary>
        public bool Kept;

        public bool NeedsAssign
        {
            get { return Chosen != null && (Verdict == StockVerdict.Assign || Verdict == StockVerdict.Replace || Verdict == StockVerdict.Choose); }
        }
    }

    /// <summary>
    /// Материал по геометрии (Р-8, решение владельца 20.09.2026): типоразмер профиля берётся из папки списка
    /// вырезов, толщина — из элемента листового металла, материал подбирается в библиотеке ЕСКД.
    ///
    /// Правило владельца:
    ///   материала нет и в библиотеке один подходящий  → подставить молча;
    ///   материала нет и подходящих несколько          → спросить;
    ///   материала нет и подходящих нет                → замечание;
    ///   материал выбран и совпадает                   → ничего;
    ///   материал выбран и не совпадает, подходящий один → заменить молча (решение владельца 22.09.2026);
    ///   материал выбран и не совпадает, подходящих несколько → спросить;
    ///   материал выбран и не совпадает, подходящих нет → замечание, ничего не меняем.
    ///
    /// Чтение идёт без отката модели: свойства профиля доходят до папки списка вырезов сами (проверено на боевых
    /// деталях 20.09.2026), а AccessSelections откатывает дерево, и в откаченном состоянии материал телу не назначить.
    /// </summary>
    public static class StockService
    {
        /// <summary>Имя свойства папки списка вырезов с типоразмером профиля — приходит из файла профиля.</summary>
        public const string SizeProperty = "Типоразмер";

        /// <summary>Имя свойства папки списка вырезов с ГОСТом сортамента.</summary>
        public const string GostProperty = "ГОСТ";

        /// <summary>Запасной источник ГОСТа: «Труба прямоугольная 40х20х1,5 ГОСТ 8645-68».</summary>
        public const string SortamentProperty = "Сортамент";

        public const string SheetFolderName = "листовой металл";

        /// <summary>
        /// Детали, по которым конструктор нажал «Позже». Сохранение детали снова заводит задачу подбора,
        /// и без этой памяти окно выбора всплывало бы после каждого сохранения. Память живёт до конца сеанса
        /// SolidWorks и сбрасывается, как только материал у детали появился.
        /// </summary>
        private static readonly HashSet<string> PostponedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Postpone(string path)
        {
            if (!string.IsNullOrEmpty(path)) lock (PostponedPaths) { PostponedPaths.Add(path); }
        }

        public static bool IsPostponed(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (PostponedPaths) { return PostponedPaths.Contains(path); }
        }

        public static void Resume(string path)
        {
            if (!string.IsNullOrEmpty(path)) lock (PostponedPaths) { PostponedPaths.Remove(path); }
        }

        /// <summary>
        /// Позиции, по которым конструктор ответил «оставить как есть»: путь|типоразмер|ГОСТ|стоящий материал. Живёт до
        /// конца сеанса SolidWorks — иначе вопрос повторялся бы при каждом сохранении. Сменится материал или профиль —
        /// ключ другой, и вопрос прозвучит снова.
        /// </summary>
        private static readonly HashSet<string> KeptDecisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Ключ вопроса о позиции: один типоразмер одного сортамента при одном стоящем материале — один вопрос.</summary>
        public static string DecisionKey(StockFinding finding)
        {
            if (finding == null) return "";
            return StockCatalog.NormalizeSize(finding.Request.Size) + "|" + StockCatalog.NormalizeGost(finding.Request.Gost) + "|" +
                (finding.CurrentMaterial ?? "").Trim();
        }

        /// <summary>Запомнить ответы «оставить» по детали (после окна выбора).</summary>
        public static void RememberKept(string path, IEnumerable<StockFinding> findings)
        {
            if (string.IsNullOrEmpty(path) || findings == null) return;
            lock (KeptDecisions)
                foreach (StockFinding f in findings)
                    if (f != null && f.Kept) KeptDecisions.Add(path + "|" + DecisionKey(f));
        }

        /// <summary>
        /// Снять с деталей позиции, по которым конструктор уже сказал «оставить»: они не назначаются и не спрашиваются.
        /// Возвращает позиции, по которым решение ещё нужно.
        /// </summary>
        public static List<StockFinding> PendingDecisions(string path, IEnumerable<StockFinding> findings)
        {
            List<StockFinding> pending = new List<StockFinding>();
            if (findings == null) return pending;
            foreach (StockFinding f in findings)
            {
                if (f == null || !f.NeedsDecision) continue;
                bool kept;
                lock (KeptDecisions) kept = !string.IsNullOrEmpty(path) && KeptDecisions.Contains(path + "|" + DecisionKey(f));
                if (kept)
                {
                    f.Kept = true;
                    f.Chosen = null;
                    continue;
                }
                pending.Add(f);
            }
            return pending;
        }

        /// <summary>
        /// Конструктор не ответил («Позже» или окно не показано при включённом вопросе): замена его материала отменяется —
        /// назначается только то, где материала не было вовсе (Assign).
        /// </summary>
        public static void Decline(IEnumerable<StockFinding> findings)
        {
            if (findings == null) return;
            foreach (StockFinding f in findings)
                if (f != null && f.Verdict == StockVerdict.Replace) f.Chosen = null;
        }

        /// <summary>Что деталь показывает про себя: только чтение, без отката и перестроения.</summary>
        public static List<StockFinding> Inspect(ISldWorks app, ModelDoc2 doc)
        {
            List<StockFinding> findings = new List<StockFinding>();
            PartDoc part = doc as PartDoc;
            if (part == null) return findings;

            List<MaterialInfo> library = MaterialCatalog.All(SyncService.MaterialDatabases(app));
            if (library.Count == 0) return findings;

            string cfg = ActiveConfiguration(doc);
            double sheetMm = SheetThicknessMm(doc);
            bool sheet = !double.IsNaN(sheetMm) && sheetMm > 0;
            List<string> profiles = new List<string>();  // пути профилей элементов сварной конструкции

            try
            {
                for (Feature f = doc.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                {
                    string type = f.GetTypeName2();
                    if (type == "WeldMemberFeat")
                    {
                        string profile = ProfilePath(f);
                        if (profile.Length > 0 && !profiles.Contains(profile)) profiles.Add(profile);
                        continue;
                    }
                    if (type != "SolidBodyFolder") continue;
                    for (Feature sub = f.GetFirstSubFeature() as Feature; sub != null; sub = sub.GetNextSubFeature() as Feature)
                    {
                        if (sub.GetTypeName2() != "CutListFolder") continue;
                        StockFinding finding = FromFolder(sub, part, library, cfg);
                        if (finding != null) findings.Add(finding);
                    }
                }
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: обход дерева " + DocInfo.TitleOf(doc), ex);
            }

            if (findings.Count > 0) return findings;

            // Список вырезов ещё не построен (свежая деталь) — запасной путь: профиль читается из самого элемента
            // сварной конструкции. Типоразмер и ГОСТ берутся из пути к файлу профиля, как он лежит в библиотеке.
            if (profiles.Count == 1)
            {
                StockFinding fromProfile = FromProfilePath(part, profiles[0], library, cfg);
                if (fromProfile != null) findings.Add(fromProfile);
                return findings;
            }
            if (profiles.Count > 1)
            {
                Log.Warn("Материал по геометрии: в детали " + DocInfo.TitleOf(doc) + " несколько профилей, а список вырезов не построен — " +
                         "обновите список вырезов, иначе неясно, какому телу какой материал");
                return findings;
            }

            // Листовая деталь: материал у детали целиком — назначение телу SolidWorks не принимает.
            if (sheet)
            {
                StockFinding whole = FromSheetPart(part, doc, library, cfg, sheetMm);
                if (whole != null) findings.Add(whole);
            }
            return findings;
        }

        /// <summary>Путь к файлу профиля элемента сварной конструкции; пусто — не прочитан.</summary>
        private static string ProfilePath(Feature member)
        {
            try
            {
                StructuralMemberFeatureData data = member.GetDefinition() as StructuralMemberFeatureData;
                return data != null ? (data.WeldmentProfilePath ?? "").Trim() : "";
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: профиль элемента конструкции", ex);
                return "";
            }
        }

        /// <summary>
        /// Типоразмер и ГОСТ из пути к профилю: «…\Труба плоскоовальная ГОСТ 8644-68\30х15х1,5.SLDLFP».
        /// Имя файла — типоразмер, имя папки — сортамент с ГОСТом.
        /// </summary>
        private static StockFinding FromProfilePath(PartDoc part, string profilePath, List<MaterialInfo> library, string cfg)
        {
            string size = Path.GetFileNameWithoutExtension(profilePath) ?? "";
            string folder = Path.GetFileName(Path.GetDirectoryName(profilePath) ?? "") ?? "";
            string gost = GostFrom(folder);
            if (size.Length == 0 || gost.Length == 0) return null;

            StockFinding finding = new StockFinding();
            finding.Folder = folder.Length > 0 ? folder + " " + size : size;
            finding.Request = new StockRequest
            {
                Kind = StockKind.Profile,
                Size = size,
                Gost = gost,
                Source = profilePath
            };
            foreach (Body2 body in SolidBodies(part)) finding.Bodies.Add(body);
            finding.CurrentMaterial = BodyMaterial(finding.Bodies, cfg);
            if (finding.CurrentMaterial.Length == 0)
            {
                ModelDoc2 model = (ModelDoc2)part;
                string db;
                finding.CurrentMaterial = SyncService.MaterialName(part, model, cfg, out db) ?? "";
            }
            Decide(finding, library);
            return finding;
        }

        private static List<Body2> SolidBodies(PartDoc part)
        {
            List<Body2> bodies = new List<Body2>();
            try
            {
                object[] all = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                if (all != null)
                    foreach (object o in all)
                    {
                        Body2 body = o as Body2;
                        if (body != null) bodies.Add(body);
                    }
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: тела детали", ex);
            }
            return bodies;
        }

        /// <summary>
        /// Позиция списка вырезов проката. Свойства «Типоразмер» и «ГОСТ» приходят сюда из файла профиля сами.
        /// Папка без них — это не прокат (листовая деталь, например): её материал назначается детали целиком.
        /// </summary>
        private static StockFinding FromFolder(Feature folder, PartDoc part, List<MaterialInfo> library, string cfg)
        {
            CustomPropertyManager props = folder.CustomPropertyManager;
            if (props == null) return null;

            string size = Property(props, SizeProperty);
            string gost = Property(props, GostProperty);
            if (gost.Length == 0) gost = GostFrom(Property(props, SortamentProperty));
            if (size.Length == 0 || gost.Length == 0) return null;

            StockFinding finding = new StockFinding();
            finding.Folder = folder.Name ?? "";
            finding.Request = new StockRequest
            {
                Kind = StockKind.Profile,
                Size = size,
                Gost = gost,
                Source = finding.Folder
            };

            BodyFolder bodyFolder = folder.GetSpecificFeature2() as BodyFolder;
            finding.FolderObject = bodyFolder;
            if (bodyFolder != null)
            {
                object[] bodies = bodyFolder.GetBodies() as object[];
                if (bodies != null)
                {
                    foreach (object o in bodies)
                    {
                        Body2 body = o as Body2;
                        if (body != null) finding.Bodies.Add(body);
                    }
                }
            }
            finding.CurrentMaterial = BodyMaterial(finding.Bodies, cfg);
            // У тела своего материала нет — значит действует материал детали, его и сверяем.
            if (finding.CurrentMaterial.Length == 0) finding.CurrentMaterial = PartMaterial(part, cfg);
            Decide(finding, library);
            return finding;
        }

        /// <summary>Материал детали целиком: им перекрыты тела без собственного материала.</summary>
        private static string PartMaterial(PartDoc part, string cfg)
        {
            string db;
            return SyncService.MaterialName(part, (ModelDoc2)part, cfg, out db) ?? "";
        }

        private static StockFinding FromSheetPart(PartDoc part, ModelDoc2 doc, List<MaterialInfo> library, string cfg, double sheetMm)
        {
            StockFinding finding = new StockFinding();
            finding.Folder = SheetFolderName;
            finding.Request = new StockRequest
            {
                Kind = StockKind.Sheet,
                Size = StockCatalog.SizeFromThickness(sheetMm),
                Source = SheetFolderName
            };
            string db;
            finding.CurrentMaterial = SyncService.MaterialName(part, doc, cfg, out db) ?? "";
            Decide(finding, library);
            return finding;
        }

        /// <summary>Решение по одной позиции — то самое правило владельца. Отделено от SolidWorks ради юнит-тестов.</summary>
        public static void Decide(StockFinding finding, List<MaterialInfo> library)
        {
            finding.Match = StockCatalog.Match(library, finding.Request);
            bool assigned = !string.IsNullOrWhiteSpace(finding.CurrentMaterial);
            if (assigned && Fits(finding, library))
            {
                finding.Verdict = StockVerdict.Ok;
                return;
            }
            if (finding.Match.Single)
            {
                finding.Verdict = assigned ? StockVerdict.Replace : StockVerdict.Assign;
                finding.Chosen = finding.Match.First;
            }
            else if (finding.Match.Several) finding.Verdict = StockVerdict.Choose;
            else finding.Verdict = StockVerdict.NotInLibrary;
        }

        /// <summary>Стоящий материал соответствует геометрии: он среди подходящих или совпадает с одним из них по свойствам.</summary>
        private static bool Fits(StockFinding finding, List<MaterialInfo> library)
        {
            foreach (MaterialInfo info in finding.Match.Candidates)
            {
                if (string.Equals((info.Name ?? "").Trim(), finding.CurrentMaterial.Trim(), StringComparison.Ordinal))
                    return true;
            }
            // Материал мог быть схлопнут как дубль по свойствам: тогда он тоже соответствует геометрии.
            MaterialInfo current = ByName(library, finding.CurrentMaterial);
            if (current != null)
            {
                string key = StockCatalog.RecordKey(current);
                foreach (MaterialInfo info in finding.Match.Candidates)
                {
                    if (StockCatalog.RecordKey(info) == key) return true;
                }
            }
            return false;
        }

        /// <summary>« вместо «…»» — в журнале видно, какой материал заменён.</summary>
        private static string Instead(StockFinding finding)
        {
            return string.IsNullOrWhiteSpace(finding.CurrentMaterial) ? "" : " вместо «" + finding.CurrentMaterial.Trim() + "»";
        }

        private static MaterialInfo ByName(List<MaterialInfo> library, string name)
        {
            if (library == null || string.IsNullOrWhiteSpace(name)) return null;
            string wanted = name.Trim();
            foreach (MaterialInfo info in library)
                if (string.Equals((info.Name ?? "").Trim(), wanted, StringComparison.Ordinal)) return info;
            return null;
        }

        /// <summary>Текст замечания для отчёта синхронизации; пусто — говорить не о чем.</summary>
        public static string Message(StockFinding finding)
        {
            if (finding == null) return "";
            string where = finding.Folder.Length > 0 ? "«" + finding.Folder + "»" : "деталь";
            switch (finding.Verdict)
            {
                case StockVerdict.NotInLibrary:
                    return string.Format("{0}: типоразмер «{1}»{2} не найден в библиотеке материалов — проверьте материал",
                        where, finding.Request.Size,
                        finding.Request.Gost.Length > 0 ? " " + finding.Request.Gost : "");
                case StockVerdict.Choose:
                    return string.IsNullOrWhiteSpace(finding.CurrentMaterial)
                        ? string.Format("{0}: типоразмеру «{1}» соответствуют {2} материала — материал не назначен, выберите его",
                            where, finding.Request.Size, finding.Match.Candidates.Count)
                        : string.Format("{0}: материал «{1}» не соответствует геометрии, а типоразмеру «{2}» соответствуют {3} материала — " +
                            "выберите нужный", where, finding.CurrentMaterial, finding.Request.Size, finding.Match.Candidates.Count);
                default:
                    return "";
            }
        }

        /// <summary>
        /// Назначение материала. Только из очереди простоя: в обработчике сохранения SolidWorks ещё держит документ.
        /// Материал ставится телам папки списка вырезов — назначение детали целиком перетёрло бы материалы тел
        /// сварной конструкции и развалило бы раскладку списка вырезов.
        /// </summary>
        public static int Apply(ISldWorks app, ModelDoc2 doc, IEnumerable<StockFinding> findings, SyncReport report)
        {
            if (doc == null || findings == null) return 0;
            PartDoc part = doc as PartDoc;
            if (part == null) return 0;
            string cfg = ActiveConfiguration(doc);
            int changed = 0;
            List<BodyFolder> refresh = new List<BodyFolder>();

            // Позиция в детали одна — материал ставится детали целиком. Так его видит вся прежняя цепочка:
            // «Материал_ФБ», «Материал_Строка», масса, книга ЛЗК — все они читают материал детали, а не тела.
            // Позиций несколько — материал раскладывается по телам: общий материал детали перетёр бы соседние
            // позиции, у которых он и так правильный.
            bool wholePart = Count(findings) <= 1;

            foreach (StockFinding finding in findings)
            {
                if (!finding.NeedsAssign) continue;
                MaterialInfo target = finding.Chosen;
                string database = DatabasePath(app, target);
                try
                {
                    if (finding.Bodies.Count > 0 && !wholePart)
                    {
                        bool ok = true;
                        foreach (Body2 body in finding.Bodies)
                        {
                            int code = body.SetMaterialProperty(cfg, database, target.Name);
                            if (code != 0)
                            {
                                ok = false;
                                if (report != null)
                                    report.Warnings.Add(string.Format("«{0}»: материал «{1}» не назначен телу (код {2})",
                                        finding.Folder, target.Name, code));
                            }
                        }
                        if (ok)
                        {
                            changed++;
                            if (finding.FolderObject != null && !refresh.Contains(finding.FolderObject)) refresh.Add(finding.FolderObject);
                            if (report != null)
                                report.Operations.Add(string.Format("«{0}»: материал «{1}» назначен по типоразмеру «{2}»{3}",
                                    finding.Folder, target.Name, finding.Request.Size, Instead(finding)));
                        }
                    }
                    else
                    {
                        part.SetMaterialPropertyName2(cfg, database, target.Name);
                        // SetMaterialPropertyName2 ничего не возвращает и при неверном имени молча не делает ничего —
                        // поэтому читаем обратно (П-4 отчёта 20.09.2026).
                        string db;
                        string now = SyncService.MaterialName(part, doc, cfg, out db) ?? "";
                        if (string.Equals(now.Trim(), target.Name.Trim(), StringComparison.Ordinal))
                        {
                            changed++;
                            if (finding.FolderObject != null && !refresh.Contains(finding.FolderObject)) refresh.Add(finding.FolderObject);
                            if (report != null)
                                report.Operations.Add(string.Format("Материал «{0}» назначен по типоразмеру «{1}»{2}",
                                    target.Name, finding.Request.Size, Instead(finding)));
                        }
                        else if (report != null)
                        {
                            report.Warnings.Add(string.Format("Материал «{0}» не назначен: SolidWorks оставил «{1}»",
                                target.Name, now));
                        }
                    }
                }
                catch (COMException ex)
                {
                    Log.Error("Материал по геометрии: назначение «" + target.Name + "»", ex);
                    if (report != null) report.Failures++;
                }
            }

            // Список вырезов сам не перестроится: без этого новые материалы не доедут до его свойств.
            foreach (BodyFolder folder in refresh)
            {
                try { folder.UpdateCutList(); }
                catch (COMException ex) { Log.Error("Материал по геометрии: UpdateCutList", ex); }
            }
            return changed;
        }

        private static int Count(IEnumerable<StockFinding> findings)
        {
            int n = 0;
            foreach (StockFinding f in findings) n++;
            return n;
        }

        /// <summary>Путь к файлу библиотеки, в которой лежит материал: SetMaterialProperty требует именно путь.</summary>
        private static string DatabasePath(ISldWorks app, MaterialInfo target)
        {
            if (target == null) return "";
            foreach (string path in SyncService.MaterialDatabases(app))
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), target.Database, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            return target.Database ?? "";
        }

        private static string BodyMaterial(List<Body2> bodies, string cfg)
        {
            foreach (Body2 body in bodies)
            {
                try
                {
                    string db;
                    string name = body.GetMaterialPropertyName(cfg, out db);
                    if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                }
                catch (COMException ex)
                {
                    Log.Error("Материал по геометрии: материал тела", ex);
                }
            }
            return "";
        }

        /// <summary>Толщина листовой детали, мм; NaN — деталь не листовая.</summary>
        public static double SheetThicknessMm(ModelDoc2 model)
        {
            try
            {
                for (Feature f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                {
                    if (f.GetTypeName2() != "SheetMetal") continue;
                    SheetMetalFeatureData data = f.GetDefinition() as SheetMetalFeatureData;
                    if (data != null) return data.Thickness * 1000.0;
                }
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: толщина листа", ex);
            }
            return double.NaN;
        }

        /// <summary>«ГОСТ 8645-68» из строки сортамента; пусто — не нашли.</summary>
        public static string GostFrom(string sortament)
        {
            if (string.IsNullOrEmpty(sortament)) return "";
            int at = sortament.IndexOf("ГОСТ", StringComparison.OrdinalIgnoreCase);
            if (at < 0) at = sortament.IndexOf("ОСТ", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "";
            return sortament.Substring(at).Trim();
        }

        private static string Property(CustomPropertyManager props, string name)
        {
            try
            {
                string raw, resolved;
                props.Get4(name, false, out raw, out resolved);
                string value = string.IsNullOrWhiteSpace(resolved) ? raw : resolved;
                return (value ?? "").Trim();
            }
            catch (COMException)
            {
                return "";
            }
        }

        private static string ActiveConfiguration(ModelDoc2 doc)
        {
            try
            {
                Configuration c = doc.GetActiveConfiguration() as Configuration;
                return c != null ? c.Name : "";
            }
            catch (COMException)
            {
                return "";
            }
        }
    }
}
