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
    /// <summary>Как запущена проверка изделия.</summary>
    public enum CheckMode
    {
        /// <summary>Только отчёт: модели не меняются (Т-6) — «Готово к производству», автотесты.</summary>
        Report = 0,
        /// <summary>Кнопка: одно окно с вопросами, обновлениями, замечаниями и списком сохранения.</summary>
        Interactive = 1,
        /// <summary>Без окна, с записью и сохранением — как ответило бы окно без конструктора (автотесты).</summary>
        ApplySilent = 2
    }

    /// <summary>
    /// Кнопка К-3 «Проверить изделие» (ТЗ-02 Т-32…Т-34). С 23.09.2026 — единственный порядок работы с изделием (решение
    /// владельца, журнал З-27): проверка собирает всё сразу — что обновится само, о чём спросить конструктора, какие
    /// замечания остаются — и показывает в одном окне (<see cref="ProductReviewForm"/>). «Применить и сохранить»
    /// записывает ответы и обновления и сохраняет отмеченные файлы; отчёт `_Проверка.txt` с итогом ГОТОВО / ЗАМЕЧАНИЯ /
    /// БРАК пишется по состоянию после записи. Сама проверка модели не перестраивает и без ответа ничего не меняет;
    /// в режиме <see cref="CheckMode.Report"/> модели не меняются вовсе (Т-6).
    /// </summary>
    public static class CheckService
    {
        /// <summary>«ok|итог|брак|замечаний|путь отчёта» или «error|текст» — для автотестов и пакетного запуска (Т-9).</summary>
        public static string LastOutcome = "";

        /// <summary>Находки последней проверки: «Готово к производству» показывает их, если изделие не готово.</summary>
        public static CheckReport LastReport;

        /// <summary>Итог записи последней проверки — строка <see cref="BatchReport.StatusLine"/>; пусто — ничего не записано.</summary>
        public static string LastApplied = "";

        /// <summary>Правила, которых запись окна не касается: их находки после записи не пересчитываются.</summary>
        private static readonly string[] Stable =
        {
            CheckRules.References, CheckRules.Drawing, CheckRules.Operations, CheckRules.Export, CheckRules.Drawings
        };

        public static bool Run(ISldWorks app, bool interactive)
        {
            return Run(app, interactive ? CheckMode.Interactive : CheckMode.Report);
        }

        public static bool Run(ISldWorks app, CheckMode mode)
        {
            bool interactive = mode == CheckMode.Interactive;
            LastOutcome = "";
            LastApplied = "";
            LastReport = null;
            try
            {
                ModelDoc2 doc = app.ActiveDoc as ModelDoc2;
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
                string productFolder = ProductReviewService.ProductFolderOf(assemblyPath);
                string cipher = LzkNaming.Cipher(productFolder, assemblyPath);
                // Правки конструктора в самой сборке — до первого обращения к составу. Разрешение облегчённых компонентов
                // сборку изменённой не помечает (e2e K07, 23.09.2026), но читать флаг лучше до любых действий проверки.
                bool topEdited = DocumentGuard.HasUserEdits(doc);

                Status(app, "ЕСКД: проверка изделия — состав…");
                CheckReport report = NewReport(assemblyPath, cipher);
                if (!location.Found)
                    report.Add(CheckRules.References, CheckLevel.Issue, report.Assembly,
                        "изделие лежит вне заказа и базы (" + location.Reason + "): проверены только состав и реквизиты");
                List<ProductNode> nodes = ProductReviewService.Collect(app, doc, assemblyPath, productFolder, report);
                nodes[0].Edited = topEdited;

                Status(app, "ЕСКД: проверка изделия — что обновится…");
                ReviewSession session = ProductReviewService.Prepare(app, productFolder, cipher, nodes, mode != CheckMode.Report);
                Status(app, "ЕСКД: проверка изделия — реквизиты…");
                Inspect(app, nodes, productFolder, assemblyPath, session, report, true);

                bool asked = false;
                BatchReport applied = null;
                if (mode != CheckMode.Report && session.Plan.HasWork)
                {
                    ReviewChoice choice = ReviewChoice.ApplyAndSave;
                    if (interactive)
                    {
                        asked = true;
                        choice = Ask(app, session, report);
                    }
                    else session.Plan.AnswerSilently();
                    if (choice != ReviewChoice.Cancel)
                    {
                        Status(app, "ЕСКД: проверка изделия — запись…");
                        applied = ProductReviewService.Apply(app, session, choice == ReviewChoice.ApplyAndSave);
                        LastApplied = applied.StatusLine();
                        // Отчёт — по состоянию после записи: реквизиты, перестроение и книга считаются заново.
                        Status(app, "ЕСКД: проверка изделия — проверка после записи…");
                        report = Recheck(app, nodes, productFolder, assemblyPath, session, report);
                    }
                }
                // Несохранённые правки в своих документах изделия (З-27): версия изделия считается по файлам, а проверено то,
                // что открыто, — пока правки не сохранены, «Готово к производству» не пройдёт.
                foreach (ProductNode node in nodes.Where(ProductFreshness.Own))
                    if (DocumentGuard.HasUserEdits(node.Model))
                        report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), Path.GetFileName(node.Path),
                            "несохранённые правки: сохраните документ и проверьте изделие снова");
                foreach (ProductNode node in nodes)
                    report.Checksums.Add(new KeyValuePair<string, string>(Path.GetFileName(node.Path), Checksum(node.Path)));
                Status(app, "ЕСКД: проверка изделия — выданные документы…");
                Issued(productFolder, report);
                report.Version = VersionOf(productFolder, report);
                SameVersion(productFolder, assemblyPath, report);

                string path = Write(productFolder, report);
                LastReport = report;
                Notices.Remember(Notices.FromCheck(report));
                LastOutcome = string.Join("|", new[]
                {
                    "ok", CheckRules.OutcomeName(report.Outcome), report.Count(CheckLevel.Defect).ToString(),
                    report.Count(CheckLevel.Issue).ToString(), path
                });
                Status(app, "");
                if (interactive)
                {
                    if (asked) Told(app, report, applied);
                    else Show(app, report, path);
                }
                return report.Outcome != CheckLevel.Defect;
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия", ex);
                Fail(app, interactive, "Проверка не выполнена: " + ex.Message);
                return false;
            }
        }

        private static CheckReport NewReport(string assemblyPath, string cipher)
        {
            return new CheckReport
            {
                Assembly = Path.GetFileName(assemblyPath),
                Product = cipher,
                User = Settings.AuthorOrUser(),
                Time = DateTime.Now
            };
        }

        /// <summary>
        /// Правила проверки. first = false — после записи окна: пересчитываются только правила, которых запись касается
        /// (перестроение, реквизиты, книга), остальные находки переносятся из первого прохода.
        /// </summary>
        private static void Inspect(ISldWorks app, List<ProductNode> nodes, string productFolder, string assemblyPath,
            ReviewSession session, CheckReport report, bool first)
        {
            List<ProductNode> own = nodes.Where(n => n.InProduct && !n.IsPurchased).ToList();
            RebuildErrors(own, report);
            foreach (ProductNode node in own)
            {
                Attributes(app, node, first ? session.Planned(node.Path) : null, report);
                if (!first) continue;
                DrawingOrBch(node, report);
                Operations(node, report);
            }
            ProductReviewService.Findings(session, report);
            Status(app, "ЕСКД: проверка изделия — документы изделия…");
            Workbook(productFolder, assemblyPath, report);
            if (!first) return;
            Export(productFolder, nodes, report);
            Drawings(app, nodes, report);
        }

        private static CheckReport Recheck(ISldWorks app, List<ProductNode> nodes, string productFolder, string assemblyPath,
            ReviewSession session, CheckReport before)
        {
            CheckReport after = NewReport(assemblyPath, before.Product);
            foreach (CheckFinding f in before.Findings.Where(x => Stable.Contains(x.Rule))) after.Findings.Add(f);
            Inspect(app, nodes, productFolder, assemblyPath, session, after, false);
            return after;
        }

        // ------------------------------------------------------------------ окно
        /// <summary>Окно «Проверить изделие»: вопросы, обновления, замечания, список сохранения.</summary>
        private static ReviewChoice Ask(ISldWorks app, ReviewSession session, CheckReport report)
        {
            ReviewPlan plan = session.Plan;
            // Что окно запишет само и о чём оно спрашивает, в замечаниях не повторяется.
            List<Notice> notices = new List<Notice>();
            foreach (CheckFinding f in report.Findings)
                if (!plan.Handles(f)) notices.Add(Notices.Of(Notices.FromCheck(f.Level), f.Document, f.Text));
            List<string> parts = new List<string>();
            if (plan.AutoChanges > 0) parts.Add("обновится само — " + plan.AutoChanges);
            if (plan.Questions.Count > 0) parts.Add("вопросов — " + plan.Questions.Count);
            if (notices.Count > 0) parts.Add("замечаний — " + notices.Count);
            string headline = "Изделие " + report.Product + ": " + string.Join(", ", parts.ToArray());
            string details = "Сборка " + report.Assembly + ". Ответьте на вопросы и нажмите «Применить и сохранить»: ответы " +
                "и сохранение — за один раз, сохранять детали ещё раз не нужно. «Решить позже» ничего не меняет — вопрос " +
                "вернётся при следующей проверке.";
            Status(app, "");
            using (ProductReviewForm form = new ProductReviewForm(plan, notices, headline, details))
            {
                form.ShowDialog(NoticeForm.Owner(app));
                return form.DialogResult == DialogResult.OK ? form.Choice : ReviewChoice.Cancel;
            }
        }

        /// <summary>Итог после окна — в строку состояния; ошибки записи и сохранения — окном.</summary>
        private static void Told(ISldWorks app, CheckReport report, BatchReport applied)
        {
            string outcome = "ЕСКД: изделие проверено — " + CheckRules.OutcomeName(report.Outcome) +
                (report.Findings.Count > 0
                    ? " (брак " + report.Count(CheckLevel.Defect) + ", замечаний " + report.Count(CheckLevel.Issue) + ")"
                    : "");
            if (applied == null)
            {
                Status(app, outcome + "; ничего не изменено");
                return;
            }
            string done = applied.StatusLine();
            if (done.StartsWith("ЕСКД: ", StringComparison.Ordinal)) done = done.Substring("ЕСКД: ".Length);
            Status(app, outcome + "; " + done);
            if (applied.Errors.Count == 0 && applied.Hints.Count == 0) return;
            List<Notice> notices = new List<Notice>();
            foreach (string error in applied.Errors) notices.Add(Split(NoticeLevel.Critical, error));
            foreach (string hint in applied.Hints) notices.Add(Split(NoticeLevel.Warning, hint));
            if (applied.Errors.Count > 0)
                NoticeForm.Present(app, "ЕСКД: проверка изделия", "Записано не всё",
                    "Остальное записано" + (applied.Saved > 0 || applied.AssemblySaved ? " и сохранено" : "") +
                    ". Документы из списка ниже проверьте и сохраните сами; подробности — в журнале надстройки.",
                    notices, NoticeLevel.Critical);
            else
                NoticeForm.Present(app, "ЕСКД: проверка изделия", "Остался вопрос в других исполнениях",
                    "Ответы записаны в активных исполнениях. В исполнениях ниже тот же вопрос о материале — в исполнениях " +
                    "бывают разные материалы, поэтому ответ туда не перенесён: ответьте в каждом отдельно.",
                    notices, NoticeLevel.Warning);
        }

        /// <summary>«документ: текст» → замечание с документом.</summary>
        private static Notice Split(NoticeLevel level, string line)
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            return Notices.Of(level, colon > 0 ? line.Substring(0, colon) : "", colon > 0 ? line.Substring(colon + 2) : line);
        }

        // ------------------------------------------------------------------ правило «б»: перестроение
        /// <summary>
        /// Ошибки перестроения — по списку SolidWorks «Что не так», без перестроения: ForceRebuild3 помечал сборку
        /// изменённой, и после проверки SolidWorks просил её сохранить (аудит 23.09.2026). Предупреждения — не ошибки.
        /// </summary>
        private static void RebuildErrors(List<ProductNode> nodes, CheckReport report)
        {
            foreach (ProductNode node in nodes)
            {
                string name = Path.GetFileName(node.Path);
                try
                {
                    ModelDocExtension ext = node.Model.Extension;
                    if (ext.GetWhatsWrongCount() == 0) continue;
                    object features, codes, warnings;
                    if (!ext.GetWhatsWrong(out features, out codes, out warnings)) continue;
                    int errors = 0;
                    Array flags = warnings as Array;
                    if (flags == null) continue;
                    foreach (object flag in flags)
                        if (flag is bool && !(bool)flag) errors++;
                    if (errors > 0)
                        report.Add(CheckRules.Rebuild, CheckRules.LevelOf(CheckRules.Rebuild), name,
                            "ошибок перестроения: " + errors + " — см. «Что не так» в SolidWorks");
                }
                catch (COMException ex)
                {
                    Log.Error("Проверка изделия: ошибки перестроения " + name, ex);
                }
            }
        }

        // ------------------------------------------------------------------ правило «в»: реквизиты, материал, масса
        private static void Attributes(ISldWorks app, ProductNode node, SyncReport planned, CheckReport report)
        {
            string name = Path.GetFileName(node.Path);
            try
            {
                PropertyWriter w = new PropertyWriter(node.Model, true);
                string cfg = w.ActiveConfigurationName();
                ParsedName parsed = DesignationParser.Parse(node.Path, " ");
                // Пустое свойство — ещё не брак: обозначение и наименование система берёт из имени файла,
                // материал — из материала SolidWorks. Их записывает «Проверить изделие», об этом и сообщаем.
                // Наименование берётся только из слов после обозначения: «775.СТЛ.00.005.sldprt» его не даёт (ревью
                // 23.09.2026 — раньше такой случай обещал запись, которой синхронизация не делает).
                Required(report, name, Value(w, cfg, "Обозначение"), parsed.HasDesignation, "Обозначение",
                    "в имени файла его тоже нет — переименуйте по ЕСКД или оформите как покупное изделие", planned);
                Required(report, name, Value(w, cfg, "Наименование"), parsed.Title.Length > 0, "Наименование",
                    "наименование неоткуда взять: переименуйте файл по ЕСКД", planned);
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
                            "материал «" + material + "» не записан в «Материал_Строка»: нажмите «Проверить изделие»").Fixable =
                            Writes(planned, "Материал_Строка");
                }
                if (LzkOperations.ParseNumber(Value(w, cfg, "Масса_ФБ")) <= 0 &&
                    LzkOperations.ParseNumber(Value(w, cfg, "Масса_Таблица")) <= 0)
                {
                    if (SyncService.ActiveMass(node.Model) > 0)
                        report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), name,
                            "масса не записана в свойства: нажмите «Проверить изделие»").Fixable =
                            Writes(planned, "Масса_ФБ") || Writes(planned, "Масса_Таблица");
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
            Diagnose(app, node, planned, report, name);
        }

        /// <summary>
        /// Толщина листовой детали больше 50 мм — почти всегда ошибка модели (ТЗ-02 Т-32в):
        /// такой лист не режут и не гнут, а масса и раскрой считаются как у листа.
        /// </summary>
        private const double MaxSheetThicknessMm = 50;

        private static void Thickness(ProductNode node, CheckReport report, string name)
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
        /// Предупреждения синхронизации вхолостую (ТЗ-02 Т-32в): ручной материал, обозначение не по имени файла и прочее —
        /// все, без обрезки (раньше показывались первые три). planned — уже сделанная для окна; null — сделать сейчас.
        /// </summary>
        private static void Diagnose(ISldWorks app, ProductNode node, SyncReport planned, CheckReport report, string name)
        {
            try
            {
                SyncReport sync = planned ?? SyncService.SyncModel(app, node.Model,
                    new SyncRequest { Reason = "проверка изделия", DryRun = true });
                string designation = sync.Designation != null ? sync.Designation.Warning : null;
                // Переключение единиц массы — работа самой синхронизации, а не повод разбираться: в отчёт не идёт.
                foreach (string warning in sync.Warnings
                    .Where(x => x.IndexOf(SyncService.MassUnitsWarning, StringComparison.Ordinal) < 0))
                    report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), name, warning).Fixable = warning == designation;
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: диагностика " + name, ex);
                report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), name, "диагностика не выполнена: " + ex.Message);
            }
        }

        /// <summary>
        /// Реквизит записан — молчим; пусто, но восстановимо из имени файла — замечание: «Проверить изделие» запишет;
        /// восстановить неоткуда — брак (ТЗ-02 Т-32, правила «в» и «в2»). Исправимой (окно само запишет) находка
        /// помечается, только если запись этого свойства есть в плане окна — иначе окно скрыло бы то, чего не делает.
        /// </summary>
        private static void Required(CheckReport report, string document, string value, bool recoverable,
            string property, string defectText, SyncReport planned)
        {
            if (value.Length > 0) return;
            if (recoverable)
                report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), document,
                    "свойство «" + property + "» не записано в модель: нажмите «Проверить изделие»").Fixable = Writes(planned, property);
            else
                report.Add(CheckRules.Attributes, CheckRules.LevelOf(CheckRules.Attributes), document,
                    "не заполнено «" + property + "»: " + defectText);
        }

        /// <summary>Синхронизация вхолостую записала бы это свойство («Деталь [00] Масса_ФБ: … → …»).</summary>
        private static bool Writes(SyncReport planned, string property)
        {
            if (planned == null) return false;
            string marker = "] " + property + ": ";
            return planned.Operations.Any(op => op.IndexOf(marker, StringComparison.Ordinal) >= 0 &&
                !op.EndsWith(": удалено", StringComparison.Ordinal));
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
        private static void DrawingOrBch(ProductNode node, CheckReport report)
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
        private static void Operations(ProductNode node, CheckReport report)
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
            try
            {
                using (XlsxBook book = XlsxBook.Open(path))
                {
                    string sheet, cell;
                    // Пометки «?» книга держит в шапке ведомости: «нет» или их число (отчёта _Ведомость.txt больше нет).
                    if (book.TryResolveName("Шапка_Замечания", out sheet, out cell))
                    {
                        string marks = (book.Sheet(sheet).Get(cell) ?? "").Trim();
                        if (marks.Length > 0 && marks != "нет")
                        {
                            // В шапке — «3 (пометки «?» в таблице)», у книг до 18.09 — «3 (см. _Ведомость.txt)»: нужно число.
                            string count = new string(marks.TakeWhile(char.IsDigit).ToArray());
                            report.Add(CheckRules.Workbook, CheckRules.LevelOf(CheckRules.Workbook), name,
                                "в книге ЛЗК пометок «?»: " + (count.Length > 0 ? count : marks) +
                                ": нажмите «Ведомость ЛЗК» — окно покажет, чего не хватает");
                        }
                    }
                    // С 23.09.2026 книга помнит версию изделия — сверка с ней в конце проверки (SameVersion), когда
                    // версия известна. У прежних книг версии нет: сверка по сумме сборки, и такую книгу «Готово к
                    // производству» не примет — изменённые после неё детали по сумме сборки не видны.
                    string version;
                    if (LzkInputs.TryReadVersion(book, out version)) return;
                    if (book.TryResolveName("Шапка_КонтрольнаяСумма", out sheet, out cell) &&
                        !CheckRules.WorkbookMatchesAssembly((book.Sheet(sheet).Get(cell) ?? "").Trim(), Checksum(assemblyPath)))
                        report.Add(CheckRules.Workbook, CheckRules.LevelOf(CheckRules.Workbook), name,
                            "ведомость сделана по другой версии сборки: сформируйте заново");
                    else
                        report.Add(CheckRules.Workbook, CheckRules.LevelOf(CheckRules.Workbook), name,
                            "книга без отметки версии изделия (собрана до 23.09.2026): нажмите «Ведомость ЛЗК» — " +
                            "«Готово к производству» примет только книгу с версией");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: ведомость", ex);
                report.Add(CheckRules.Workbook, CheckRules.LevelOf(CheckRules.Workbook), name, "ведомость не прочитана: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ правило «е»: выгрузка для производства
        private static void Export(string productFolder, List<ProductNode> nodes, CheckReport report)
        {
            string path = Path.Combine(productFolder, CheckRules.ExportReportName);
            if (!File.Exists(path))
            {
                report.Add(CheckRules.Export, CheckRules.LevelOf(CheckRules.Export), CheckRules.ExportReportName,
                    "изделие не выгружено для производства (PDF, DXF, IGS)");
                return;
            }
            // Сверяем блоки отчёта по отдельности: имя детали стоит и в «Пропущено» («PDF не сохранён»), и такая
            // деталь выгруженной не считается (Т-32е).
            ExportLog log = ExportLog.Parse(File.ReadAllText(path, Encoding.UTF8));
            foreach (ProductNode node in nodes.Where(n => n.InProduct && !n.IsPurchased && !n.IsAssembly))
            {
                string name = Path.GetFileNameWithoutExtension(node.Path);
                string file = Path.GetFileName(node.Path);
                string failed = log.Skipped.Select(ExportLog.SplitSkip)
                    .Where(s => Path.GetFileNameWithoutExtension(s.Key).Equals(name, StringComparison.OrdinalIgnoreCase)
                        && !ExportLog.IsBenignSkip(s.Value))
                    .Select(s => s.Value).FirstOrDefault();
                if (failed != null)
                    report.Add(CheckRules.Export, CheckRules.LevelOf(CheckRules.Export), file, "выгрузка не сделана: " + failed);
                else if (!log.Files.Any(f => f.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) &&
                         !log.Skipped.Any(s => Path.GetFileNameWithoutExtension(ExportLog.SplitSkip(s).Key)
                             .Equals(name, StringComparison.OrdinalIgnoreCase)))
                    report.Add(CheckRules.Export, CheckRules.LevelOf(CheckRules.Export), file,
                        "нет в отчёте выгрузки " + CheckRules.ExportReportName);
            }
            // Файлы выгрузки должны лежать на месте и не меняться после выгрузки: цех получит именно их.
            string[] folders =
            {
                ExportNaming.PdfDirectory(productFolder), ExportNaming.LaserDirectory(productFolder),
                ExportNaming.TubeDirectory(productFolder)
            };
            foreach (string exported in log.Files)
            {
                string found = folders.Select(f => Path.Combine(f, exported)).FirstOrDefault(File.Exists);
                string sum;
                if (found == null)
                    report.Add(CheckRules.Export, CheckRules.LevelOf(CheckRules.Export), exported,
                        "файла выгрузки нет в папке изделия: выгрузите изделие заново");
                else if (log.Checksums.TryGetValue(exported, out sum) &&
                         !string.Equals(sum, Checksum(found), StringComparison.OrdinalIgnoreCase))
                    report.Add(CheckRules.Export, CheckRules.LevelOf(CheckRules.Export), exported,
                        "файл изменён после выгрузки: выгрузите изделие заново");
            }
        }

        /// <summary>
        /// Правило «е», выданное: деталь или чертёж из последнего `_Выдано_…` изменены после выдачи, а новой ревизии нет
        /// (аудит 23.09.2026, CHK-13). Суммы SHA-256 отчёта выдачи сверяются с файлами «01_3D»; ревизия есть, если в
        /// журнале изменений после выдачи — строка этого документа. Сохранения самих кнопок (ЛЗК, выгрузка) вписаны в
        /// отчёт выдачи и изменением не считаются; сборки не сверяются — их файл меняется от сохранения после правки
        /// детали. Отчёт выдачи до 23.09.2026 без строки журнала — ревизия считается по дате строки.
        /// </summary>
        private static void Issued(string productFolder, CheckReport report)
        {
            IssueRecord issued = IssueRecord.Latest(productFolder);
            if (issued == null || issued.Documents.Count == 0) return;
            string models = Path.Combine(productFolder, LzkNaming.ModelsFolder);
            if (!Directory.Exists(models)) return;
            Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(models, "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase)) &&
                    IssueRecord.IsDocumentFile(f)))
                current[Path.GetFileName(file)] = Checksum(file);
            List<ChangeRow> journal;
            try
            {
                journal = ChangeLog.Read(ChangeLog.Path(productFolder));
            }
            catch (Exception ex)
            {
                // Непрочитанный журнал — не пустой: иначе каждая изменённая выданная деталь получила бы «новой ревизии
                // нет», хотя ревизия оформлена (ревью 23.09.2026).
                Log.Error("Проверка изделия: журнал изменений", ex);
                report.Add(CheckRules.Export, CheckRules.LevelOf(CheckRules.Export), ChangeLog.FileName,
                    "журнал изменений не прочитан — выданные документы с ним не сверены: закройте книгу, если она открыта, " +
                    "и проверьте изделие снова");
                return;
            }
            foreach (KeyValuePair<string, string> changed in IssueRecord.Unrevised(issued, current, journal))
                report.Add(CheckRules.Export, CheckRules.LevelOf(CheckRules.Export), changed.Key,
                    "изменён после выдачи в производство (" + Path.GetFileName(issued.Path) + "), а новой ревизии нет: нажмите " +
                    "«Новая ревизия» в «" + changed.Value + "» — или верните выданный файл");
        }

        // ------------------------------------------------------------------ правило «з»: чертежи
        private static void Drawings(ISldWorks app, List<ProductNode> nodes, CheckReport report)
        {
            foreach (ProductNode node in nodes.Where(n => n.InProduct && !n.IsPurchased))
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
        // ------------------------------------------------------------------ версия изделия
        /// <summary>
        /// Версия изделия этой проверки (решение владельца 23.09.2026, З-27) — по файлам. Прежняя остаётся, если файлы
        /// изделия ровно те, что в прежнем отчёте (с поправкой на сохранения самих кнопок — ProductStamp.Restamp):
        /// повторная проверка того же изделия книгу и выгрузку не «устаревает». Иначе — новая. Несохранённые правки версию
        /// не меняют (их закроют без сохранения — файлы останутся прежними), а дают замечание проверки.
        /// </summary>
        private static string VersionOf(string productFolder, CheckReport report)
        {
            ProductStamp previous = ProductStamp.Read(CheckRules.ReportPath(productFolder));
            if (previous != null && previous.Version.Length > 0 &&
                string.Equals(previous.Assembly, report.Assembly, StringComparison.OrdinalIgnoreCase) &&
                ProductStamp.Differences(previous.Checksums, report.Checksums).Count == 0)
                return previous.Version;
            return ProductStamp.NewVersion(report.Time, report.Checksums);
        }

        /// <summary>
        /// Книга ЛЗК и выгрузка сделаны по этой же версии изделия? Иначе — замечание: «Готово к производству» идёт только
        /// с книгой и выгрузкой по проверенному и с тех пор не менявшемуся изделию.
        /// </summary>
        private static void SameVersion(string productFolder, string assemblyPath, CheckReport report)
        {
            string cipher = LzkNaming.Cipher(productFolder, assemblyPath);
            string book = LzkNaming.FindWorkbook(productFolder, cipher);
            if (book.Length > 0 && !LzkNaming.IsLegacy(book))
            {
                try
                {
                    using (XlsxBook xlsx = XlsxBook.Open(book))
                    {
                        string version;
                        if (LzkInputs.TryReadVersion(xlsx, out version) && version != report.Version)
                            report.Add(CheckRules.Workbook, CheckRules.LevelOf(CheckRules.Workbook), Path.GetFileName(book),
                                (version.Length == 0
                                    ? "книга собрана по непроверенному изделию"
                                    : "книга собрана по другой версии изделия (" + version + ")") +
                                ": нажмите «Ведомость ЛЗК»");
                    }
                }
                catch (Exception ex)
                {
                    // Не прочиталась — об этом уже сказало правило книги.
                    Log.Error("Проверка изделия: версия в книге ЛЗК", ex);
                }
            }
            string export = Path.Combine(productFolder, CheckRules.ExportReportName);
            if (!File.Exists(export)) return;
            ExportLog log = ExportLog.Parse(File.ReadAllText(export, Encoding.UTF8));
            if (log.Version == report.Version) return;
            report.Add(CheckRules.Export, CheckRules.LevelOf(CheckRules.Export), CheckRules.ExportReportName,
                (log.Version == null
                    ? "выгрузка без отметки версии изделия (сделана до 23.09.2026)"
                    : log.Version.Length == 0
                        ? "выгрузка сделана по непроверенному изделию"
                        : "выгрузка сделана по другой версии изделия (" + log.Version + ")") +
                ": выгрузите изделие заново");
        }

        private static string Write(string productFolder, CheckReport report)
        {
            string path = CheckRules.ReportPath(productFolder);
            if (File.Exists(path)) File.Copy(path, CheckRules.PreviousReportPath(productFolder), true);
            File.WriteAllText(path, CheckRules.Report(report), new UTF8Encoding(true));
            return path;
        }

        private static void Show(ISldWorks app, CheckReport report, string path)
        {
            string details = "Изделие " + report.Product + " · сборка " + report.Assembly + Environment.NewLine +
                (report.Findings.Count == 0 ? "Изделие можно выдавать в производство." :
                    "Критично: " + report.Count(CheckLevel.Defect) + ", замечаний: " + report.Count(CheckLevel.Issue) + ".");
            NoticeForm.Present(app, "ЕСКД: проверка изделия", "Итог: " + CheckRules.OutcomeName(report.Outcome), details,
                Notices.FromCheck(report), report.Findings.Count == 0 ? (NoticeLevel?)null : Notices.FromCheck(report.Outcome));
        }

        /// <summary>Прежняя проверка — тем же окном, что и новая: находки читаются из `_Проверка.txt`.</summary>
        private static void ShowSaved(ISldWorks app, string reportText)
        {
            List<Notice> notices = Notices.ParseCheckReport(reportText);
            string outcome = CheckRules.OutcomeOf(reportText);
            string details = string.Join(Environment.NewLine, (reportText ?? "").Split('\n')
                .Select(l => l.Trim()).Where(l => l.StartsWith("Изделие:", StringComparison.Ordinal) ||
                                                  l.StartsWith("Проверил:", StringComparison.Ordinal))
                .ToArray());
            NoticeForm.Present(app, "ЕСКД: отчёт проверки", "Прежняя проверка: " + outcome, details, notices,
                notices.Count == 0 ? (NoticeLevel?)null : Notices.Max(notices));
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
                if (interactive) ShowSaved(app, File.ReadAllText(path, Encoding.UTF8));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Проверка изделия: показ отчёта", ex);
                Fail(app, interactive, "Отчёт не открылся: " + ex.Message);
                return false;
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
            return ProductFreshness.Checksum(path);
        }
    }
}
