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

        /// <summary>Где конструктор отвечает на вопрос о материале (решение владельца 23.09.2026, журнал З-27).</summary>
        public const string WhereToAnswer = "кнопка «Синхронизировать» (в сборке — «Проверить изделие»)";

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
            return PendingDecisions(path, findings, "");
        }

        /// <summary>
        /// То же с ответами, сохранёнными в самой модели (свойство <see cref="ReviewAccepted.PropertyName"/>, значение —
        /// <see cref="Accepted"/>): «Оставить как есть» из окна «Проверить изделие» помнится и в следующих сеансах.
        /// </summary>
        public static List<StockFinding> PendingDecisions(string path, IEnumerable<StockFinding> findings, string accepted)
        {
            List<StockFinding> pending = new List<StockFinding>();
            if (findings == null) return pending;
            foreach (StockFinding f in findings)
            {
                if (f == null || !f.NeedsDecision) continue;
                bool kept;
                lock (KeptDecisions) kept = !string.IsNullOrEmpty(path) && KeptDecisions.Contains(path + "|" + DecisionKey(f));
                if (!kept) kept = ReviewAccepted.Contains(accepted, ReviewAccepted.Material(DecisionKey(f)));
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

        /// <summary>Сохранённые в модели ответы «Оставить как есть» (значение свойства; пусто — нет).</summary>
        public static string Accepted(ModelDoc2 doc)
        {
            if (doc == null) return "";
            try
            {
                return new PropertyWriter(doc, true).Raw("", ReviewAccepted.PropertyName) ?? "";
            }
            catch (Exception ex)
            {
                Log.Error("Материал по геометрии: свойство " + ReviewAccepted.PropertyName, ex);
                return "";
            }
        }

        /// <summary>
        /// Конструктор ещё не ответил (сохранение детали — не ответ): замена его материала и выбор из нескольких
        /// отменяются — назначается только то, где материала не было, а подходящий один (Assign).
        /// </summary>
        public static void Decline(IEnumerable<StockFinding> findings)
        {
            if (findings == null) return;
            foreach (StockFinding f in findings)
                if (f != null && f.NeedsDecision) f.Chosen = null;
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
            Dictionary<string, List<Feature>> members = new Dictionary<string, List<Feature>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                for (Feature f = doc.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                {
                    string type = f.GetTypeName2();
                    if (type == "WeldMemberFeat")
                    {
                        string profile = ProfilePath(f);
                        if (profile.Length == 0) continue;
                        if (!profiles.Contains(profile)) profiles.Add(profile);
                        if (!members.ContainsKey(profile)) members[profile] = new List<Feature>();
                        members[profile].Add(f);
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
                StockFinding fromProfile = FromProfilePath(part, profiles[0], members[profiles[0]], library, cfg);
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
        /// <remarks>
        /// Без списка вырезов тела профиля узнаются по граням элементов конструкции. Они — вся деталь (или тело одно) —
        /// материал ставится детали целиком. Иначе позиции нет: пластина, приваренная к трубе, или тело, полученное
        /// зеркалом или массивом (его грани принадлежат не элементу конструкции), — какому телу какой материал, без
        /// списка вырезов не понять. Раньше материал трубы получали все тела детали (аудит 23.09.2026, MAT-6), а после
        /// первой правки — только найденные по граням, и тела зеркала оставались без материала навсегда (ревью 23.09.2026).
        /// </remarks>
        private static StockFinding FromProfilePath(PartDoc part, string profilePath, List<Feature> members,
            List<MaterialInfo> library, string cfg)
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
            List<Body2> all = SolidBodies(part);
            if (all.Count > 1 && MemberBodies(members).Count < all.Count)
            {
                Log.Warn("Материал по геометрии: в детали " + DocInfo.TitleOf((ModelDoc2)part) + " кроме профиля «" + finding.Folder +
                         "» есть другие тела, а список вырезов не построен — обновите список вырезов, иначе неясно, какому " +
                         "телу какой материал");
                return null;
            }
            finding.Bodies.AddRange(all);
            finding.CurrentMaterial = PositionMaterial(part, finding.Bodies, cfg);
            Decide(finding, library);
            return finding;
        }

        /// <summary>Тела, которым принадлежат грани элементов конструкции; одно тело — один раз.</summary>
        private static List<Body2> MemberBodies(IEnumerable<Feature> members)
        {
            List<Body2> bodies = new List<Body2>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Feature member in members ?? Enumerable.Empty<Feature>())
            {
                try
                {
                    object[] faces = member.GetFaces() as object[];
                    if (faces == null) continue;
                    foreach (object o in faces)
                    {
                        Face2 face = o as Face2;
                        Body2 body = face != null ? face.GetBody() as Body2 : null;
                        if (body == null) continue;
                        string name = body.Name ?? "";
                        if (name.Length > 0 ? seen.Add(name) : !bodies.Contains(body)) bodies.Add(body);
                    }
                }
                catch (COMException ex)
                {
                    Log.Error("Материал по геометрии: тела элемента конструкции", ex);
                }
            }
            return bodies;
        }

        private static string BodyName(Body2 body)
        {
            try
            {
                return body.Name ?? "";
            }
            catch (COMException)
            {
                return "";
            }
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
            // У тела своего материала нет — значит действует материал детали, его и сверяем.
            finding.CurrentMaterial = PositionMaterial(part, finding.Bodies, cfg);
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
                        ? string.Format("{0}: типоразмеру «{1}» соответствуют {2} материала — материал не назначен, выберите его: {3}",
                            where, finding.Request.Size, finding.Match.Candidates.Count, WhereToAnswer)
                        : string.Format("{0}: материал «{1}» не соответствует геометрии, а типоразмеру «{2}» соответствуют {3} материала — " +
                            "выберите нужный: {4}", where, finding.CurrentMaterial, finding.Request.Size, finding.Match.Candidates.Count,
                            WhereToAnswer);
                case StockVerdict.Replace:
                    // Сохранение детали материал конструктора не меняет (З-27): заменить или оставить — его ответ в окне.
                    return string.Format("{0}: материал «{1}» не соответствует геометрии, типоразмеру «{2}» подходит «{3}» — " +
                        "заменить или оставить: {4}", where, finding.CurrentMaterial, finding.Request.Size,
                        StockText.Describe(finding.Match.First), WhereToAnswer);
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
            List<StockFinding> assigned = new List<StockFinding>();
            int changed = ApplyIn(app, doc, part, cfg, findings, report, assigned, "");
            if (assigned.Count > 0) changed += OtherConfigurations(app, doc, part, cfg, assigned, report);
            return changed;
        }

        /// <summary>Назначение в одной конфигурации — активной сейчас; assigned — сюда позиции, получившие материал.</summary>
        private static int ApplyIn(ISldWorks app, ModelDoc2 doc, PartDoc part, string cfg, IEnumerable<StockFinding> findings,
            SyncReport report, List<StockFinding> assigned, string prefix)
        {
            int changed = 0;
            List<BodyFolder> refresh = new List<BodyFolder>();
            int solids = SolidBodies(part).Count;

            foreach (StockFinding finding in findings)
            {
                if (!finding.NeedsAssign) continue;
                MaterialInfo target = finding.Chosen;
                string database = DatabasePath(app, target);
                // Позиция — вся деталь (все её тела или листовая деталь) — материал ставится детали целиком. Так его видит
                // вся прежняя цепочка: «Материал_ФБ», «Материал_Строка», масса, книга ЛЗК — все они читают материал
                // детали, а не тела. Позиция — часть тел — материал только им: материал детали перетёр бы соседние тела.
                // Раньше «одна позиция — вся деталь», и пластины, приваренные к трубе, получали материал трубы
                // (аудит 23.09.2026, MAT-6).
                bool wholePart = finding.Bodies.Count == 0 || finding.Bodies.Count >= solids;
                try
                {
                    if (!wholePart)
                    {
                        bool ok = true;
                        foreach (Body2 body in finding.Bodies)
                        {
                            // Успех у SolidWorks — swBodyMaterialApplicationError_NoError = 1, а не 0. Но и «успех» ещё не
                            // материал: на SolidWorks 2025 назначение телу отвечает NoError и не меняет ни материала тела, ни
                            // массы (e2e T14, 23.09.2026) — поэтому материал тела читается обратно.
                            int code = body.SetMaterialProperty(cfg, database, target.Name);
                            if (code == (int)swBodyMaterialApplicationError_e.swBodyMaterialApplicationError_NoError &&
                                !string.Equals(OwnMaterial(body, cfg), target.Name.Trim(), StringComparison.Ordinal))
                            {
                                ok = false;
                                if (report != null)
                                    report.Warnings.Add(prefix + string.Format(
                                        "«{0}»: SolidWorks не поставил материал «{1}» телу «{2}» — назначьте его этим телам вручную " +
                                        "(список вырезов или «Твёрдые тела» → правой кнопкой → «Материал»); детали целиком он не " +
                                        "назначается: у остальных тел свой материал", finding.Folder, target.Name, BodyName(body)));
                                break;
                            }
                            if (code != (int)swBodyMaterialApplicationError_e.swBodyMaterialApplicationError_NoError)
                            {
                                ok = false;
                                if (report != null)
                                    report.Warnings.Add(prefix + string.Format("«{0}»: материал «{1}» не назначен телу (код {2})",
                                        finding.Folder, target.Name, code));
                            }
                        }
                        if (ok)
                        {
                            changed++;
                            if (assigned != null) assigned.Add(finding);
                            if (finding.FolderObject != null && !refresh.Contains(finding.FolderObject)) refresh.Add(finding.FolderObject);
                            if (report != null)
                                report.Operations.Add(prefix + string.Format("«{0}»: материал «{1}» назначен по типоразмеру «{2}»{3}",
                                    finding.Folder, target.Name, finding.Request.Size, Instead(finding)));
                            // Графа 3 («Материал_ФБ», «Материал_Строка»), масса и книга ЛЗК берут материал детали целиком, а
                            // он при назначении телам не меняется: остался заменённый или пустой — это надо видеть (ревью
                            // 23.09.2026). Материал детали, выбранный для других тел, — выбор конструктора, о нём молчим.
                            string whole = PartMaterial(part, cfg).Trim();
                            string old = (finding.CurrentMaterial ?? "").Trim();
                            if (report != null && (whole.Length == 0 || old.Length > 0 && string.Equals(whole, old, StringComparison.Ordinal)))
                                report.Warnings.Add(prefix + string.Format(
                                    "«{0}»: материал «{1}» стоит только у тел этой позиции; графа 3 основной надписи, масса и книга " +
                                    "ЛЗК берут материал детали целиком, а там {2} — назначьте материал детали (дерево → «Материал» → " +
                                    "правой кнопкой)", finding.Folder, target.Name, whole.Length == 0 ? "пусто" : "по-прежнему «" + whole + "»"));
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
                            if (assigned != null) assigned.Add(finding);
                            if (finding.FolderObject != null && !refresh.Contains(finding.FolderObject)) refresh.Add(finding.FolderObject);
                            if (report != null)
                                report.Operations.Add(prefix + string.Format("Материал «{0}» назначен по типоразмеру «{1}»{2}",
                                    target.Name, finding.Request.Size, Instead(finding)));
                        }
                        else if (report != null)
                        {
                            report.Warnings.Add(prefix + string.Format("Материал «{0}» не назначен: SolidWorks оставил «{1}»",
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

        /// <summary>
        /// Материал SolidWorks у каждой конфигурации свой: назначенный только в активной, в других исполнениях он
        /// оставался прежним, и их «Материал_Строка», масса и книга ЛЗК расходились (аудит 23.09.2026, MAT-5). Каждое
        /// другое исполнение показывается, его позиции читаются заново. Где та же позиция (вид, типоразмер, ГОСТ) и стоит
        /// тот же прежний материал:
        ///   материала нет и подходящий один — ставится сам, как его поставила бы и сама система в этом исполнении;
        ///   выбор из нескольких или замена материала — это ответ конструктора, в другое исполнение он не копируется:
        ///   в исполнениях бывают разные материалы (решение владельца 23.09.2026) — подсказка «ответьте в нём отдельно».
        /// Исполнение с другой геометрией или своим материалом не трогается. В конце — снова прежнее исполнение.
        /// </summary>
        private static int OtherConfigurations(ISldWorks app, ModelDoc2 doc, PartDoc part, string active,
            List<StockFinding> assigned, SyncReport report)
        {
            string[] names = doc.GetConfigurationNames() as string[];
            if (names == null || names.Length < 2 || active.Length == 0) return 0;
            int changed = 0;
            bool switched = false;
            try
            {
                foreach (string name in names)
                {
                    if (string.Equals(name, active, StringComparison.Ordinal)) continue;
                    // Производная конфигурация (развёртка «…SM-FLAT-PATTERN», «Как сварено») — не исполнение: материал у
                    // неё родительский. Раньше надстройка переключалась в неё и просила «ответить в ней» (ревью 23.09.2026).
                    Configuration configuration = doc.GetConfigurationByName(name) as Configuration;
                    if (configuration != null && configuration.IsDerived()) continue;
                    if (!doc.ShowConfiguration2(name))
                    {
                        if (report != null)
                            report.Warnings.Add("исполнение «" + name + "» не открылось — материал в нём не проверен: сделайте " +
                                "его активным и нажмите «Синхронизировать»");
                        continue;
                    }
                    switched = true;
                    List<StockFinding> same = new List<StockFinding>();
                    foreach (StockFinding finding in Inspect(app, doc))
                    {
                        StockFinding source = assigned.FirstOrDefault(a => SameRequest(a.Request, finding.Request) &&
                            string.Equals((a.CurrentMaterial ?? "").Trim(), (finding.CurrentMaterial ?? "").Trim(), StringComparison.Ordinal));
                        if (source == null) continue;
                        if (finding.Verdict == StockVerdict.Assign && source.Verdict == StockVerdict.Assign)
                        {
                            same.Add(finding);
                            continue;
                        }
                        if (report != null && finding.NeedsDecision)
                            report.Hints.Add("исполнение «" + name + "»: " + (finding.Folder.Length > 0 ? "«" + finding.Folder + "», " : "") +
                                "тот же типоразмер «" + finding.Request.Size + "» — материал " +
                                (string.IsNullOrWhiteSpace(finding.CurrentMaterial) ? "не выбран" : "«" + finding.CurrentMaterial.Trim() + "» не подходит") +
                                ". Ответ в одном исполнении в другое не переносится: сделайте «" + name + "» активным и ответьте в нём (" +
                                WhereToAnswer + ")");
                    }
                    if (same.Count > 0) changed += ApplyIn(app, doc, part, name, same, report, null, "[" + name + "] ");
                }
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: другие исполнения " + DocInfo.TitleOf(doc), ex);
                if (report != null) report.Failures++;
            }
            finally
            {
                if (switched)
                {
                    try
                    {
                        if (!doc.ShowConfiguration2(active) && report != null)
                            report.Warnings.Add("прежнее исполнение «" + active + "» не вернулось активным — сделайте его активным сами");
                    }
                    catch (COMException ex)
                    {
                        Log.Error("Материал по геометрии: возврат исполнения " + active, ex);
                    }
                }
            }
            return changed;
        }

        private static bool SameRequest(StockRequest a, StockRequest b)
        {
            return a.Kind == b.Kind &&
                string.Equals(StockCatalog.NormalizeSize(a.Size), StockCatalog.NormalizeSize(b.Size), StringComparison.OrdinalIgnoreCase) &&
                string.Equals((a.Gost ?? "").Trim(), (b.Gost ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// Материал позиции: у каждого тела — свой, а без своего — материал детали. Он у всех тел один — это он. Тела
        /// расходятся только потому, что у части тел своего материала нет, — пусто: позиция не в порядке, недостающим
        /// телам нужен материал. Раньше брался материал первого тела со своим материалом, и тела без него (например,
        /// полученные зеркалом) так и оставались без материала (ревью 23.09.2026). У тел разные свои материалы — первый
        /// из них, как прежде: это выбор конструктора, и молча его не перетирают.
        /// </summary>
        private static string PositionMaterial(PartDoc part, List<Body2> bodies, string cfg)
        {
            string whole = null;
            string first = null;
            bool mixed = false;
            List<string> own = new List<string>();
            foreach (Body2 body in bodies)
            {
                string name = OwnMaterial(body, cfg);
                if (name.Length > 0) { if (!own.Contains(name)) own.Add(name); }
                else name = whole ?? (whole = PartMaterial(part, cfg).Trim());
                if (first == null) first = name;
                else if (!string.Equals(first, name, StringComparison.Ordinal)) mixed = true;
            }
            if (first == null) return PartMaterial(part, cfg).Trim();
            if (!mixed) return first;
            return own.Count <= 1 ? "" : own[0];
        }

        /// <summary>Свой материал тела; нет или не прочитан — пусто.</summary>
        private static string OwnMaterial(Body2 body, string cfg)
        {
            try
            {
                string db;
                string name = body.GetMaterialPropertyName(cfg, out db);
                return string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: материал тела", ex);
                return "";
            }
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
