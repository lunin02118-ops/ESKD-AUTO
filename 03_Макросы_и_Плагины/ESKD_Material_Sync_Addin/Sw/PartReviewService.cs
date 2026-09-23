using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Кнопка «Синхронизировать» в детали (решение владельца 23.09.2026, журнал З-27, шаг 2): то же окно, что у «Проверить
    /// изделие», для одной детали — что обновится само, вопросы (материал к профилю, обозначение не по имени файла),
    /// замечания и галочка «сохранить». Ctrl+S больше ничего не спрашивает: спорное ждёт этой кнопки или проверки изделия.
    /// </summary>
    public static class PartReviewService
    {
        /// <summary>Итог последнего запуска — строка состояния; пусто — не запускалось или деталь не сохранена в файл.</summary>
        public static string LastApplied = "";

        /// <summary>
        /// Окно для детали; interactive = false — без окна, ответы как без конструктора (автотесты). null — деталь ещё не
        /// сохранена в файл: без имени файла и папки окну не на что опереться, вызывающий делает прежнюю синхронизацию.
        /// </summary>
        public static string Run(ISldWorks app, ModelDoc2 part, bool interactive)
        {
            LastApplied = "";
            string path = DocInfo.PathOf(part);
            if (path.Length == 0) return null;
            string title = DocInfo.TitleOf(part);
            string productFolder = FolderOf(path);
            string cipher = LzkNaming.Cipher(productFolder, path);
            ProductNode node = new ProductNode
            {
                Path = path,
                Model = part,
                IsTop = true,
                InProduct = true,
                IsPurchased = ComponentKind.IsPurchased(null, part, path, "Синхронизировать", cipher),
                Edited = DocumentGuard.HasUserEdits(part)
            };
            if (node.IsPurchased)
                return Done("ЕСКД: покупное или стандартное изделие — надстройка его не меняет");

            ReviewSession session = ProductReviewService.Prepare(app, productFolder, cipher, new List<ProductNode> { node }, true);
            List<Notice> notices = Notes(session, title);
            if (!session.Plan.HasWork)
            {
                if (interactive && notices.Count > 0)
                    NoticeForm.Present(app, "ЕСКД: синхронизация", "Деталь " + title + ": обновлять нечего",
                        "Реквизиты записаны. Ниже — то, что надстройка сама не исправляет.", notices, NoticeLevel.Warning);
                return Done("ЕСКД: реквизиты актуальны" + (notices.Count > 0 ? ", замечаний " + notices.Count : ""));
            }

            ReviewChoice choice = ReviewChoice.ApplyAndSave;
            if (interactive) choice = Ask(app, session, notices, title);
            else session.Plan.AnswerSilently();
            if (choice == ReviewChoice.Cancel) return Done("ЕСКД: ничего не изменено");

            BatchReport applied = ProductReviewService.Apply(app, session, choice == ReviewChoice.ApplyAndSave);
            if (interactive && applied.Errors.Count > 0)
            {
                List<Notice> errors = applied.Errors.Select(e => Notices.Of(NoticeLevel.Critical, title, e)).ToList();
                NoticeForm.Present(app, "ЕСКД: синхронизация", "Записано не всё",
                    "Документ проверьте и сохраните сами; подробности — в журнале надстройки.", errors, NoticeLevel.Critical);
            }
            return Done(applied.StatusLine());
        }

        private static string Done(string status)
        {
            LastApplied = status ?? "";
            return LastApplied;
        }

        /// <summary>Папка изделия детали: по структуре заказа; деталь вне неё (или папку не узнать) — её собственная папка.</summary>
        private static string FolderOf(string path)
        {
            string own = Path.GetDirectoryName(path) ?? "";
            try
            {
                string product = ProductReviewService.ProductFolderOf(path);
                return product.Length > 0 && LzkNaming.IsInside(path, product) ? product : own;
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)) throw;
                return own;
            }
        }

        /// <summary>Замечания окна: чего оно не исправляет — отказ записи, типоразмер вне библиотеки, прочие предупреждения.</summary>
        private static List<Notice> Notes(ReviewSession session, string title)
        {
            List<Notice> notices = new List<Notice>();
            foreach (string warning in session.Report.Warnings) notices.Add(Notices.Of(NoticeLevel.Warning, title, Tail(warning)));
            foreach (ReviewSession.Target t in session.Targets)
            {
                foreach (string note in t.StockNotes) notices.Add(Notices.Of(NoticeLevel.Warning, title, note));
                if (t.Planned == null) continue;
                string designation = t.Planned.Designation != null ? t.Planned.Designation.Warning : null;
                foreach (string warning in t.Planned.Warnings)
                {
                    // Обозначение не по имени файла — вопрос окна, единицы массы — его обновление: в замечаниях не повторяются.
                    if (warning == designation || warning.IndexOf(SyncService.MassUnitsWarning, StringComparison.Ordinal) >= 0) continue;
                    notices.Add(Notices.Of(NoticeLevel.Warning, title, warning));
                }
            }
            return notices;
        }

        /// <summary>«Деталь.sldprt: не обновляется — …» → «не обновляется — …»: имя детали в окне стоит отдельно.</summary>
        private static string Tail(string warning)
        {
            int colon = (warning ?? "").IndexOf(": ", StringComparison.Ordinal);
            return colon > 0 ? warning.Substring(colon + 2) : warning ?? "";
        }

        private static ReviewChoice Ask(ISldWorks app, ReviewSession session, List<Notice> notices, string title)
        {
            ReviewPlan plan = session.Plan;
            List<string> parts = new List<string>();
            if (plan.AutoChanges > 0) parts.Add("обновится само — " + plan.AutoChanges);
            if (plan.Questions.Count > 0) parts.Add("вопросов — " + plan.Questions.Count);
            if (notices.Count > 0) parts.Add("замечаний — " + notices.Count);
            string headline = "Деталь " + title + ": " + string.Join(", ", parts.ToArray());
            string details = "Ответьте на вопросы и нажмите «Применить и сохранить»: ответы и сохранение — за один раз. " +
                "«Решить позже» ничего не меняет — вопрос вернётся при следующей синхронизации или проверке изделия.";
            using (ProductReviewForm form = new ProductReviewForm(plan, notices, headline, details))
            {
                form.Text = "ЕСКД: синхронизация детали";
                form.ShowDialog(NoticeForm.Owner(app));
                return form.DialogResult == DialogResult.OK ? form.Choice : ReviewChoice.Cancel;
            }
        }
    }
}
