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
        Replace = 5,
        /// <summary>
        /// Листовая деталь, по которой материал не подобрать: тела разной толщины или рядом нелистовые (сверка SW API
        /// 23.09.2026, №39) — замечание, ничего не меняем и не спрашиваем.
        /// </summary>
        Unclear = 6
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

        /// <summary>
        /// Позиция папки списка вырезов. У неё пустой список тел — не «вся деталь», а папка без тел в этом исполнении
        /// (сверка SW API 23.09.2026, №32); «вся деталь» без тел — только листовая деталь.
        /// </summary>
        public bool FromCutList;

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
                    // Элемент, погашенный в активном исполнении, — профиль другого исполнения (сверка SW API 23.09.2026,
                    // №32): с ним у «Укосины» выходило «несколько профилей», и деталь оставалась без позиции.
                    if (type == "WeldMemberFeat" && !f.IsSuppressed())
                    {
                        string profile = ProfilePath(f);
                        if (profile.Length == 0) continue;
                        if (!profiles.Contains(profile)) profiles.Add(profile);
                        if (!members.ContainsKey(profile)) members[profile] = new List<Feature>();
                        members[profile].Add(f);
                    }
                }
                // Папки с телами активного исполнения, вместе с подсварками (CutListFolders): папка
                // погашенного тела давала позицию «вся деталь» с чужим профилем и ставила его материал детали (№32).
                foreach (Feature sub in CutListFolders.Active(doc))
                {
                    StockFinding finding = FromFolder(sub, part, library, cfg);
                    if (finding != null) findings.Add(finding);
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

            // Многотельная листовая деталь: материал детали целиком — только если все тела листовые и одной толщины
            // (сверка SW API 23.09.2026, №39). Раньше толщина первого элемента «Листовой металл» в дереве шла всей детали,
            // и тела 8 мм получали «Лист 3»; вложенный в папку «Листовой металл» давал NaN, и подбор молча пропускался.
            List<Body2> solids = SolidBodies(part);
            if (solids.Count > 1)
            {
                List<double> thickness = solids.Select(b => BodySheetThicknessMm(b, doc)).ToList();
                double single;
                string problem = StockCatalog.SheetBodiesProblem(thickness, out single);
                // Конструктор уже назначил телам материалы по их толщине — вопроса нет, замечание при каждом сохранении
                // только мешало бы (ревью 23.09.2026).
                if (problem.Length > 0 && StockCatalog.SheetBodiesSettled(thickness, BodyActualMaterials(part, solids, cfg),
                        (mm, material) => FitsThickness(library, mm, material)))
                {
                    Log.Info("Материал по геометрии: " + DocInfo.TitleOf(doc) + " — " + problem + ", материалы телам назначены");
                    return findings;
                }
                if (problem.Length > 0)
                {
                    Log.Warn("Материал по геометрии: " + DocInfo.TitleOf(doc) + " — " + problem);
                    findings.Add(new StockFinding
                    {
                        Folder = SheetFolderName,
                        Verdict = StockVerdict.Unclear,
                        Request = new StockRequest { Kind = StockKind.Sheet, Size = problem, Source = SheetFolderName }
                    });
                    return findings;
                }
                if (!double.IsNaN(single))
                {
                    sheetMm = single;
                    sheet = true;
                }
            }

            // Листовая деталь: материал у детали целиком — назначение телу SolidWorks не принимает.
            if (sheet)
            {
                StockFinding whole = FromSheetPart(part, doc, library, cfg, sheetMm);
                if (whole != null) findings.Add(whole);
            }
            return findings;
        }

        /// <summary>
        /// Исполнение cfg, которое стоит в изделии, но не активно в файле (№36; решение владельца 24.09.2026): профиль —
        /// из элементов сварной конструкции, не погашенных в cfg, материал — по имени конфигурации. Исполнение не
        /// переключается, деталь не помечается изменённой — только чтение. Список вырезов, тела и толщина листа видны только
        /// у активной конфигурации, поэтому сверяется деталь из одного профиля. Материал, который без переключения не узнать
        /// (он у тел), с профилем не сверяется: про него проверка изделия уже пишет «материал не проверен» (находка 14).
        /// </summary>
        public static List<StockFinding> InspectExecution(ISldWorks app, ModelDoc2 doc, string cfg)
        {
            List<StockFinding> findings = new List<StockFinding>();
            PartDoc part = doc as PartDoc;
            if (part == null || string.IsNullOrEmpty(cfg)) return findings;
            List<string> profiles = new List<string>();
            try
            {
                for (Feature f = doc.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                {
                    if (f.GetTypeName2() != "WeldMemberFeat" || SuppressedIn(f, cfg)) continue;
                    string profile = ProfilePath(f);
                    if (profile.Length > 0 && !profiles.Contains(profile, StringComparer.OrdinalIgnoreCase)) profiles.Add(profile);
                }
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: обход дерева " + DocInfo.TitleOf(doc) + ", исполнение " + cfg, ex);
                return findings;
            }
            if (profiles.Count != 1)
            {
                if (profiles.Count > 1)
                    Log.Info("Материал по геометрии: " + DocInfo.TitleOf(doc) + ", исполнение «" + cfg + "» — профилей " +
                             profiles.Count + ": без переключения исполнения не понять, какому телу какой материал");
                return findings;
            }
            StockFinding finding = ProfileFinding(profiles[0]);
            if (finding == null) return findings;
            List<MaterialInfo> library = MaterialCatalog.All(SyncService.MaterialDatabases(app));
            if (library.Count == 0) return findings;
            string db;
            bool known;
            List<string> mixed = new List<string>();
            string material = SyncService.ActualMaterial(part, doc, cfg, out db, mixed, out known);
            if (!known || mixed.Count > 0)
            {
                Log.Info("Материал по геометрии: " + DocInfo.TitleOf(doc) + ", исполнение «" + cfg + "» — материал у тел, он " +
                         "виден, только когда исполнение активно: с профилем не сверяется");
                return findings;
            }
            finding.CurrentMaterial = (material ?? "").Trim();
            Decide(finding, library);
            findings.Add(finding);
            return findings;
        }

        /// <summary>
        /// Замечание по исполнению cfg, не активному в файле (№36): ответить в окне можно только за активное исполнение,
        /// поэтому — «сделайте его активным и нажмите «Синхронизировать»». Материал не назначен — пусто: это брак
        /// «материал не назначен» проверки изделия (находка 14), повторять его незачем.
        /// </summary>
        public static string ExecutionMessage(string cfg, StockFinding finding)
        {
            if (finding == null || finding.Request == null) return "";
            string current = (finding.CurrentMaterial ?? "").Trim();
            string gost = (finding.Request.Gost ?? "").Length > 0 ? " " + finding.Request.Gost : "";
            switch (finding.Verdict)
            {
                case StockVerdict.NotInLibrary:
                    return string.Format("исполнение «{0}» (стоит в изделии): типоразмер «{1}»{2} не найден в библиотеке " +
                        "материалов — проверьте материал", cfg, finding.Request.Size, gost);
                case StockVerdict.Choose:
                case StockVerdict.Replace:
                    if (current.Length == 0) return "";
                    return string.Format("исполнение «{0}» (стоит в изделии): материал «{1}» не соответствует профилю «{2}»{3} — " +
                        "сделайте «{0}» активным в детали и нажмите «Синхронизировать»", cfg, current, finding.Request.Size, gost);
                default:
                    return "";
            }
        }

        /// <summary>Элемент погашен в конфигурации cfg — без её переключения; не прочитать — считается погашенным.</summary>
        private static bool SuppressedIn(Feature f, string cfg)
        {
            try
            {
                bool[] states = f.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { cfg }) as bool[];
                return states != null && states.Length > 0 && states[0];
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: погашен ли элемент в " + cfg, ex);
                return true;
            }
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
            StockFinding finding = ProfileFinding(profilePath);
            if (finding == null) return null;
            List<Body2> all = SolidBodies(part);
            if (all.Count > 1 && MemberBodies(members).Count < all.Count)
            {
                Log.Warn("Материал по геометрии: в детали " + DocInfo.TitleOf((ModelDoc2)part) + " кроме профиля «" + finding.Folder +
                         "» есть другие тела, а список вырезов не построен — обновите список вырезов, иначе неясно, какому " +
                         "телу какой материал");
                return null;
            }
            finding.Bodies.AddRange(all);
            finding.CurrentMaterial = PositionMaterial(part, finding, library, cfg);
            Decide(finding, library);
            return finding;
        }

        /// <summary>Позиция по пути к профилю, без тел и материала; путь не по библиотеке (нет типоразмера или ГОСТа) — null.</summary>
        private static StockFinding ProfileFinding(string profilePath)
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
            // Сначала тела: папка без тел в активном исполнении — не позиция, а пустой список тел означал бы «вся деталь».
            List<Body2> bodies = CutListFolders.Bodies(folder);
            if (bodies.Count == 0) return null;
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

            finding.FolderObject = folder.GetSpecificFeature2() as BodyFolder;
            finding.FromCutList = true;
            finding.Bodies.AddRange(bodies);
            // У тела своего материала нет — значит действует материал детали, его и сверяем.
            finding.CurrentMaterial = PositionMaterial(part, finding, library, cfg);
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
            return FitsName(finding, library, finding.CurrentMaterial);
        }

        /// <summary>Материал name соответствует геометрии позиции (finding.Match уже посчитан).</summary>
        private static bool FitsName(StockFinding finding, List<MaterialInfo> library, string name)
        {
            string wanted = (name ?? "").Trim();
            if (wanted.Length == 0) return false;
            foreach (MaterialInfo info in finding.Match.Candidates)
            {
                if (string.Equals((info.Name ?? "").Trim(), wanted, StringComparison.Ordinal))
                    return true;
            }
            // Материал мог быть схлопнут как дубль по свойствам: тогда он тоже соответствует геометрии.
            MaterialInfo current = ByName(library, wanted);
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
                case StockVerdict.Unclear:
                    return string.Format("{0}: {1} — материал по толщине не подбирается, назначьте его телам сами (список " +
                        "вырезов или «Твёрдые тела» → правой кнопкой → «Материал»)", where, finding.Request.Size);
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
            string active = ActiveConfiguration(doc);
            // Активна развёртка «…SM-FLAT-PATTERN» или «Как сварено» — не исполнение: материал ставится её родителю, иначе
            // он доставался одной развёртке, а исполнение с его графой 3 и массой оставалось прежним (сверка SW API
            // 23.09.2026, №40). Переключаться не нужно: материал ставится и читается по имени конфигурации.
            string cfg = MaterialConfiguration(doc, active);
            string db;
            string before = (SyncService.MaterialName(part, doc, cfg, out db) ?? "").Trim();
            List<StockFinding> assigned = new List<StockFinding>();
            int changed = ApplyIn(app, doc, part, cfg, findings, report, assigned, "");
            if (assigned.Count > 0)
            {
                changed += OtherConfigurations(app, doc, part, cfg, active, assigned, report);
                changed += DerivedOverride(doc, part, cfg, before, report);
            }
            return changed;
        }

        /// <summary>
        /// Производная, которая не исполнение: развёртка «…SM-FLAT-PATTERN», «&lt;Как сварено&gt;», «&lt;Как обработанный&gt;»
        /// (swConfigurationType_e или признак в имени; сверка SW API 23.09.2026, №40). Производная обычного типа («01» от
        /// «00», «Укосина») — исполнение (M06b).
        /// </summary>
        private static bool IsTechnical(Configuration c)
        {
            if (c == null) return false;
            try
            {
                if (!c.IsDerived()) return false;
                int type = c.Type;
                return type == (int)swConfigurationType_e.swConfiguration_AsMachined ||
                       type == (int)swConfigurationType_e.swConfiguration_AsWelded ||
                       type == (int)swConfigurationType_e.swConfiguration_SheetMetal ||
                       DesignationParser.IsTechnicalConfigurationName(c.Name);
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: вид конфигурации", ex);
                return false;
            }
        }

        /// <summary>Где ставится материал: у технической производной — у её родителя (до первого не технического), иначе — в ней.</summary>
        internal static string MaterialConfiguration(ModelDoc2 doc, string cfg)
        {
            try
            {
                Configuration c = doc.GetConfigurationByName(cfg) as Configuration;
                for (int depth = 0; depth < 8 && IsTechnical(c); depth++)
                {
                    Configuration parent = c.GetParent() as Configuration;
                    if (parent == null) break;
                    c = parent;
                }
                return c != null ? c.Name ?? cfg : cfg;
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: родитель конфигурации " + cfg, ex);
                return cfg;
            }
        }

        /// <summary>
        /// Материал технических производных исполнения после назначения ему материала (№40). SolidWorks копирует материал
        /// исполнения в производную при её создании и дальше за исполнением не следит (проба 23.09.2026): развёртка,
        /// созданная при материале трубы, оставалась трубой. Производная с материалом, который был у исполнения до
        /// назначения (before), — такая копия: она получает новый материал вместе с исполнением. Другой материал —
        /// переопределение, сделанное вручную: надстройка его не трогает, только говорит — развёртка и её масса
        /// разойдутся с деталью. Возвращает число производных, получивших материал.
        /// </summary>
        private static int DerivedOverride(ModelDoc2 doc, PartDoc part, string cfg, string before, SyncReport report)
        {
            int changed = 0;
            try
            {
                string[] names = doc.GetConfigurationNames() as string[];
                if (names == null) return 0;
                string db;
                string own = (SyncService.MaterialName(part, doc, cfg, out db) ?? "").Trim();
                string ownDb = db ?? "";
                foreach (string name in names)
                {
                    if (string.Equals(name, cfg, StringComparison.Ordinal)) continue;
                    if (!IsTechnical(doc.GetConfigurationByName(name) as Configuration) ||
                        !string.Equals(MaterialConfiguration(doc, name), cfg, StringComparison.Ordinal)) continue;
                    string there = (part.GetMaterialPropertyName2(name, out db) ?? "").Trim();
                    if (there.Length == 0 || string.Equals(there, own, StringComparison.Ordinal)) continue;
                    if (own.Length > 0 && string.Equals(there, before ?? "", StringComparison.Ordinal))
                    {
                        part.SetMaterialPropertyName2(name, ownDb, own);
                        string now = (part.GetMaterialPropertyName2(name, out db) ?? "").Trim();
                        if (string.Equals(now, own, StringComparison.Ordinal))
                        {
                            changed++;
                            Log.Info("Материал по геометрии: производная «" + name + "» получила материал «" + own +
                                     "» вместе с «" + cfg + "» — " + DocInfo.TitleOf(doc));
                            continue;
                        }
                    }
                    if (report == null) continue;
                    report.Warnings.Add(string.Format("в «{0}» свой материал «{1}», а у «{2}» — «{3}»: развёртка и её масса " +
                        "разойдутся с деталью — уберите материал «{0}» (дерево → «Материал» в этой конфигурации)", name, there, cfg, own));
                }
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: материал производных " + DocInfo.TitleOf(doc), ex);
            }
            return changed;
        }

        /// <summary>
        /// Позиция — вся деталь: листовая деталь (не папка и без тел) или её тела вместе с телами других позиций, которым
        /// назначается тот же материал, покрывают все тела детали (сверка SW API 23.09.2026, №41). Рама из одной трубы
        /// разной длины — папка на каждую длину, подсварка — свои папки: каждая позиция покрывала часть тел, материал
        /// ставился телам, а телу SolidWorks 2025 материал через API не ставит (T14) — рама оставалась без материала.
        /// Папка без тел — не вся деталь (№32). Отделено от SolidWorks ради юнит-тестов.
        /// </summary>
        public static bool CoversWholePart(IEnumerable<StockFinding> findings, StockFinding finding, int solids)
        {
            if (finding == null) return false;
            if (finding.Bodies.Count == 0) return !finding.FromCutList;
            if (finding.Chosen == null) return finding.Bodies.Count >= solids;
            int covered = 0;
            foreach (StockFinding f in findings ?? Enumerable.Empty<StockFinding>())
                if (f != null && f.NeedsAssign && SameTarget(f.Chosen, finding.Chosen)) covered += f.Bodies.Count;
            if (!(findings ?? Enumerable.Empty<StockFinding>()).Contains(finding)) covered += finding.Bodies.Count;
            return covered >= solids;
        }

        /// <summary>Материал детали встал, но из другой библиотеки (одноимённый) (№31); пусто — из нужной.</summary>
        private static string LibraryProblem(MaterialInfo target, string db)
        {
            string got = Path.GetFileNameWithoutExtension(db ?? "") ?? "";
            string wanted = Path.GetFileNameWithoutExtension(target.Database ?? "") ?? "";
            if (got.Length > 0 && wanted.Length > 0 && !string.Equals(got, wanted, StringComparison.OrdinalIgnoreCase))
                return "SolidWorks взял его из библиотеки «" + got + "», а не «" + wanted + "»";
            return "";
        }

        /// <summary>
        /// Материал детали встал, но у тел позиции свой материал, не подходящий геометрии, — он перекрывает материал детали
        /// (№31); пусто — не перекрывает.
        /// </summary>
        private static string BodiesProblem(List<StockFinding> all, StockFinding finding, string cfg)
        {
            MaterialInfo target = finding.Chosen;
            List<string> covered = new List<string>();
            foreach (StockFinding f in all)
            {
                if (f != finding && !(f.NeedsAssign && SameTarget(f.Chosen, target))) continue;
                foreach (Body2 body in f.Bodies)
                {
                    string own = OwnMaterial(body, cfg);
                    if (own.Length == 0 || string.Equals(own, target.Name.Trim(), StringComparison.Ordinal) ||
                        f.Match.Candidates.Any(c => string.Equals((c.Name ?? "").Trim(), own, StringComparison.Ordinal))) continue;
                    string name = BodyName(body);
                    if (!covered.Contains(name)) covered.Add(name);
                }
            }
            return covered.Count == 0 ? "" : "у тел «" + string.Join("», «", covered.ToArray()) + "» свой материал перекрывает " +
                "материал детали — снимите его (список вырезов → правой кнопкой → «Материал»)";
        }

        private static bool SameTarget(MaterialInfo a, MaterialInfo b)
        {
            return a != null && b != null && string.Equals(TargetKey(a), TargetKey(b), StringComparison.Ordinal);
        }

        private static string TargetKey(MaterialInfo target)
        {
            return (target.Database ?? "").Trim() + "|" + (target.Name ?? "").Trim();
        }

        /// <summary>Назначение в одной конфигурации — активной сейчас; assigned — сюда позиции, получившие материал.</summary>
        private static int ApplyIn(ISldWorks app, ModelDoc2 doc, PartDoc part, string cfg, IEnumerable<StockFinding> findings,
            SyncReport report, List<StockFinding> assigned, string prefix)
        {
            int changed = 0;
            List<BodyFolder> refresh = new List<BodyFolder>();
            int solids = SolidBodies(part).Count;
            List<StockFinding> all = findings.ToList();
            // Материал детали целиком по группе позиций ставится один раз: ключ — назначаемый материал, значение — встал ли.
            Dictionary<string, bool> wholeDone = new Dictionary<string, bool>(StringComparer.Ordinal);
            // Материал детали из чужой библиотеки: встал, но в другие исполнения не переносится.
            HashSet<string> localOnly = new HashSet<string>(StringComparer.Ordinal);

            foreach (StockFinding finding in all)
            {
                if (!finding.NeedsAssign) continue;
                // Папка без тел в этом исполнении — не позиция (№32): пустой список тел означал бы «вся деталь».
                if (finding.FromCutList && finding.Bodies.Count == 0) continue;
                MaterialInfo target = finding.Chosen;
                string database = DatabasePath(app, target);
                // Позиция — вся деталь (все её тела вместе с позициями того же материала, или листовая деталь) — материал
                // ставится детали целиком. Так его видит вся прежняя цепочка: «Материал_ФБ», «Материал_Строка», масса, книга
                // ЛЗК — все они читают материал детали, а не тела. Позиция — часть тел — материал только им: материал детали
                // перетёр бы соседние тела. Раньше «одна позиция — вся деталь», и пластины, приваренные к трубе, получали
                // материал трубы (аудит 23.09.2026, MAT-6).
                bool wholePart = CoversWholePart(all, finding, solids);
                bool done;
                if (wholePart && wholeDone.TryGetValue(TargetKey(target), out done))
                {
                    // Деталь уже получила этот материал по соседней позиции группы (другая длина той же трубы).
                    if (done && assigned != null && !localOnly.Contains(TargetKey(target))) assigned.Add(finding);
                    continue;
                }
                try
                {
                    if (!wholePart)
                    {
                        bool ok = AssignBodies(app, doc, part, finding, cfg, database, target, report, prefix, refresh);
                        if (ok)
                        {
                            changed++;
                            if (assigned != null) assigned.Add(finding);
                            if (finding.FolderObject != null && !refresh.Contains(finding.FolderObject)) refresh.Add(finding.FolderObject);
                            if (report != null)
                                report.Operations.Add(prefix + string.Format("«{0}»: материал «{1}» назначен по типоразмеру «{2}»{3}",
                                    finding.Folder, target.Name, finding.Request.Size, Instead(finding)));
                            // Графа 3 («Материал_ФБ», «Материал_Строка») и книга ЛЗК берут фактический материал: один на все
                            // тела — его, иначе материал детали (№33). Назначение части тел его не меняет: остался заменённый
                            // или пустой — это надо видеть (ревью 23.09.2026). Материал детали, выбранный для других тел, —
                            // выбор конструктора, о нём молчим.
                            string actualDb;
                            string actual = (SyncService.ActualMaterial(part, doc, cfg, out actualDb, null) ?? "").Trim();
                            string whole = actual.Length > 0 ? actual : PartMaterial(part, cfg).Trim();
                            string old = (finding.CurrentMaterial ?? "").Trim();
                            if (report != null && !string.Equals(actual, target.Name.Trim(), StringComparison.Ordinal) &&
                                (whole.Length == 0 || old.Length > 0 && string.Equals(whole, old, StringComparison.Ordinal)))
                                report.Warnings.Add(prefix + string.Format(
                                    "«{0}»: материал «{1}» стоит только у тел этой позиции; графа 3 основной надписи, масса и книга " +
                                    "ЛЗК берут материал детали целиком, а там {2} — назначьте материал детали (дерево → «Материал» → " +
                                    "правой кнопкой)", finding.Folder, target.Name, whole.Length == 0 ? "пусто" : "по-прежнему «" + whole + "»"));
                        }
                    }
                    else
                    {
                        string beforeDb;
                        string before = SyncService.MaterialName(part, doc, cfg, out beforeDb) ?? "";
                        part.SetMaterialPropertyName2(cfg, database, target.Name);
                        // SetMaterialPropertyName2 ничего не возвращает и при неверном имени молча не делает ничего —
                        // поэтому читаем обратно (П-4 отчёта 20.09.2026).
                        string db;
                        string now = SyncService.MaterialName(part, doc, cfg, out db) ?? "";
                        // Встал — назначен: материал детали уже сменился, графа 3 и другие исполнения должны его видеть. Чужая
                        // библиотека или тела со своим материалом, перекрывающим материал детали (сверка SW API 23.09.2026,
                        // №31), — замечанием «назначен детали, но …», а не «не назначен» при сменившемся материале (ревью
                        // 23.09.2026).
                        // Назначением считается только смена материала детали (уже стоял — ничего не изменилось); материал
                        // чужой библиотеки в другие исполнения не переносится.
                        bool set, propagate;
                        bool took = string.Equals(now.Trim(), target.Name.Trim(), StringComparison.Ordinal);
                        string outcome = StockCatalog.WholePartResult(before, now, target.Name,
                            took ? LibraryProblem(target, db) : "", took ? BodiesProblem(all, finding, cfg) : "", out set, out propagate);
                        wholeDone[TargetKey(target)] = set;
                        if (!propagate) localOnly.Add(TargetKey(target));
                        if (set)
                        {
                            changed++;
                            if (propagate && assigned != null) assigned.Add(finding);
                            if (finding.FolderObject != null && !refresh.Contains(finding.FolderObject)) refresh.Add(finding.FolderObject);
                            if (report != null)
                                report.Operations.Add(prefix + string.Format("Материал «{0}» назначен по типоразмеру «{1}»{2}",
                                    target.Name, finding.Request.Size, Instead(finding)));
                        }
                        if (outcome.Length > 0 && report != null) report.Warnings.Add(prefix + outcome);
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
        /// <param name="assignedIn">Где материал уже поставлен (у технической производной — её родитель).</param>
        /// <param name="active">Активная конфигурация: к ней выгрузка возвращается в конце.</param>
        private static int OtherConfigurations(ISldWorks app, ModelDoc2 doc, PartDoc part, string assignedIn, string active,
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
                    if (string.Equals(name, active, StringComparison.Ordinal) || string.Equals(name, assignedIn, StringComparison.Ordinal))
                        continue;
                    // Техническая производная (развёртка «…SM-FLAT-PATTERN», «Как сварено») — не исполнение: материал у неё
                    // родительский. Раньше надстройка переключалась в неё и просила «ответить в ней» (ревью 23.09.2026).
                    // Производная обычного типа («01» от «00») — исполнение (M06b): раньше её пропускали вместе с
                    // техническими, и исполнение без материала его не получало (сверка SW API 23.09.2026, №40).
                    if (IsTechnical(doc.GetConfigurationByName(name) as Configuration)) continue;
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
            return target == null ? "" : DatabasePathByName(app, target.Database);
        }

        /// <summary>Путь подключённой библиотеки материалов по её имени (или пути); не нашлась — как есть.</summary>
        private static string DatabasePathByName(ISldWorks app, string database)
        {
            string wanted = System.IO.Path.GetFileNameWithoutExtension(database ?? "") ?? "";
            foreach (string path in SyncService.MaterialDatabases(app))
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), wanted, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            return database ?? "";
        }

        /// <summary>
        /// Материал телам позиции — всем или ни одному (сверка SW API 23.09.2026, №38): на отказе SolidWorks пройденным
        /// телам возвращается прежний свой материал; что вернуть не удалось — в замечание, а папка — на UpdateCutList, чтобы
        /// список вырезов показывал фактическое. Раньше в одной позиции оставались разные материалы.
        /// </summary>
        private static bool AssignBodies(ISldWorks app, ModelDoc2 doc, PartDoc part, StockFinding finding, string cfg, string database,
            MaterialInfo target, SyncReport report, string prefix, List<BodyFolder> refresh)
        {
            string wanted = target.Name.Trim();
            Dictionary<string, string> beforeDb = new Dictionary<string, string>(StringComparer.Ordinal);
            bool rebuilt = false;
            BodyAssignmentOutcome outcome = BodyAssignment.Apply(finding.Bodies,
                body => BodyName(body),
                body =>
                {
                    string db;
                    string old = SyncService.OwnMaterial(body, cfg, out db);
                    beforeDb[BodyName(body)] = db ?? "";
                    return old;
                },
                // Успех у SolidWorks — swBodyMaterialApplicationError_NoError = 1, а не 0.
                body => body.SetMaterialProperty(cfg, database, target.Name),
                done =>
                {
                    // «Успех» ещё не материал: на SolidWorks 2025 назначение телу отвечает NoError и не меняет ни материала
                    // тела, ни массы (e2e T14) — материал читается обратно; не встал — одно перестроение и ещё раз.
                    List<Body2> missed = NotApplied(part, done, cfg, wanted);
                    if (missed.Count > 0 && !rebuilt)
                    {
                        rebuilt = true;
                        doc.EditRebuild3();
                        missed = NotApplied(part, done, cfg, wanted);
                    }
                    return missed;
                },
                (body, old) =>
                {
                    Body2 fresh = Fresh(part, body);
                    if (old.Length == 0)
                        return fresh.RemoveMaterialProperty((int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { cfg }) &&
                               OwnMaterial(fresh, cfg).Length == 0;
                    string db;
                    beforeDb.TryGetValue(BodyName(body), out db);
                    return fresh.SetMaterialProperty(cfg, DatabasePathByName(app, db), old) == SwCodes.BodyMaterialNoError &&
                           string.Equals(OwnMaterial(fresh, cfg), old, StringComparison.Ordinal);
                },
                SwCodes.BodyMaterialProblem);
            if (outcome.Ok) return true;
            if (outcome.Stuck.Count > 0 && finding.FolderObject != null && !refresh.Contains(finding.FolderObject))
                refresh.Add(finding.FolderObject);
            if (report != null)
                report.Warnings.Add(prefix + string.Format("«{0}»: материал «{1}» не назначен — {2}; {3}", finding.Folder, target.Name,
                    outcome.Failure, outcome.Stuck.Count == 0 ? "у тел позиции материал прежний"
                        : "у тел «" + string.Join("», «", outcome.Stuck.ToArray()) + "» он уже сменился и назад не вернулся — проверьте их"));
            return false;
        }

        /// <summary>Тела, у которых материал не встал; после перестроения тела ищутся заново по имени.</summary>
        private static List<Body2> NotApplied(PartDoc part, List<Body2> done, string cfg, string wanted)
        {
            List<Body2> missed = new List<Body2>();
            foreach (Body2 body in done)
                if (!string.Equals(OwnMaterial(Fresh(part, body), cfg), wanted, StringComparison.Ordinal)) missed.Add(body);
            return missed;
        }

        /// <summary>Тело детали с тем же именем среди нынешних тел (после перестроения прежний объект может устареть).</summary>
        private static Body2 Fresh(PartDoc part, Body2 body)
        {
            string name = BodyName(body);
            foreach (Body2 now in SolidBodies(part))
                if (string.Equals(BodyName(now), name, StringComparison.Ordinal)) return now;
            return body;
        }

        /// <summary>
        /// Стоящий материал позиции по фактическому материалу каждого тела: свой, а без своего — материал детали (правило —
        /// <see cref="StockCatalog.PositionCurrent"/>). Раньше при теле со своим материалом и теле с материалом детали выходило
        /// «материала нет», подходящий молча ставился детали, а свой материал тела оставался (сверка SW API 23.09.2026, №31).
        /// Тела без материала (например, полученные зеркалом) дозаполняются (ревью 23.09.2026).
        /// </summary>
        private static string PositionMaterial(PartDoc part, StockFinding finding, List<MaterialInfo> library, string cfg)
        {
            string whole = null;
            List<string> actual = new List<string>();
            foreach (Body2 body in finding.Bodies)
            {
                string name = OwnMaterial(body, cfg);
                actual.Add(name.Length > 0 ? name : whole ?? (whole = PartMaterial(part, cfg).Trim()));
            }
            if (actual.Count == 0) return PartMaterial(part, cfg).Trim();
            return PositionCurrentFor(finding, library, actual);
        }

        /// <summary>Стоящий материал позиции по материалам её тел — с подбором по геометрии позиции. Отделено ради юнит-тестов.</summary>
        public static string PositionCurrentFor(StockFinding finding, List<MaterialInfo> library, IList<string> bodyMaterials)
        {
            finding.Match = StockCatalog.Match(library, finding.Request);
            return StockCatalog.PositionCurrent(bodyMaterials, name => FitsName(finding, library, name));
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

        /// <summary>Фактический материал каждого тела: свой, без своего — материал детали.</summary>
        private static List<string> BodyActualMaterials(PartDoc part, List<Body2> bodies, string cfg)
        {
            string whole = PartMaterial(part, cfg).Trim();
            return bodies.Select(b =>
            {
                string own = OwnMaterial(b, cfg);
                return own.Length > 0 ? own : whole;
            }).ToList();
        }

        /// <summary>Материал подходит листу этой толщины — тем же правилом, что позиция (FitsName: и дубль библиотеки).</summary>
        private static bool FitsThickness(List<MaterialInfo> library, double mm, string material)
        {
            StockFinding probe = new StockFinding
            {
                Folder = SheetFolderName,
                Request = new StockRequest { Kind = StockKind.Sheet, Size = StockCatalog.SizeFromThickness(mm), Source = SheetFolderName }
            };
            probe.Match = StockCatalog.Match(library, probe.Request);
            return FitsName(probe, library, material);
        }

        /// <summary>
        /// Толщина листового тела, мм, из его собственного элемента «Листовой металл» (IBody2.GetFeatures, сверка SW API
        /// 23.09.2026, №39): у многотельной листовой детали он у каждого тела свой. NaN — тело не листовое или не прочитано.
        /// </summary>
        internal static double BodySheetThicknessMm(Body2 body, ModelDoc2 model)
        {
            try
            {
                if (body == null || !body.IsSheetMetal()) return double.NaN;
                object[] features = body.GetFeatures() as object[];
                if (features == null) return double.NaN;
                foreach (object o in features)
                {
                    Feature f = o as Feature;
                    if (f == null || f.GetTypeName2() != "SheetMetal") continue;
                    double mm = FeatureThicknessMm(f, model);
                    if (!double.IsNaN(mm)) return mm;
                }
            }
            catch (COMException ex)
            {
                Log.Error("Материал по геометрии: толщина листового тела", ex);
            }
            return double.NaN;
        }

        /// <summary>
        /// Толщина элемента «Листовой металл», мм; NaN — не прочитана. У многотельной листовой детали SolidWorks после
        /// переоткрытия файла отдаёт 0 у элемента тела, чья толщина совпадает с толщиной детали по умолчанию, — даже
        /// «Толщина листового металла» в списке вырезов у него 0 (проба 24.09.2026, X21: DXF «_тело1_S0мм»). Тогда верна
        /// толщина папки «Листовой металл» — её и берём.
        /// </summary>
        internal static double FeatureThicknessMm(Feature feature, ModelDoc2 model)
        {
            SheetMetalFeatureData data = feature.GetDefinition() as SheetMetalFeatureData;
            if (data == null) return double.NaN;
            double mm = data.Thickness * 1000.0;
            return mm > 0 ? mm : DefaultSheetThicknessMm(model);
        }

        /// <summary>Толщина листа детали по умолчанию — из папки «Листовой металл» многотельной детали; NaN — папки нет.</summary>
        private static double DefaultSheetThicknessMm(ModelDoc2 model)
        {
            SheetMetalFolder folder = model.FeatureManager.GetSheetMetalFolder() as SheetMetalFolder;
            Feature feature = folder != null ? folder.GetFeature() as Feature : null;
            SheetMetalFeatureData data = feature != null ? feature.GetDefinition() as SheetMetalFeatureData : null;
            return data != null && data.Thickness > 0 ? data.Thickness * 1000.0 : double.NaN;
        }

        /// <summary>Толщина листовой детали, мм; NaN — деталь не листовая.</summary>
        public static double SheetThicknessMm(ModelDoc2 model)
        {
            try
            {
                for (Feature f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                {
                    if (f.GetTypeName2() != "SheetMetal") continue;
                    double mm = FeatureThicknessMm(f, model);
                    if (!double.IsNaN(mm)) return mm;
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
