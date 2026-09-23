using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using ESKD.MaterialSync.Sw;

namespace ESKD.Tests
{
    /// <summary>
    /// Окно «Проверить изделие» (решение владельца 23.09.2026, журнал З-27): все вопросы изделия в одном окне, одна кнопка
    /// «Применить и сохранить», ответ «Оставить как есть» помнит сама модель. Проверяется без SolidWorks — план и окно
    /// без показа.
    /// </summary>
    public static class ReviewTests
    {
        private const string Bracket = @"Z:\03_ЗАКАЗЫ\775_Стол\02_Металл\И01\01_3D\775.СТЛ.00.005 Кронштейн.sldprt";
        private const string Strip = @"Z:\03_ЗАКАЗЫ\775_Стол\02_Металл\И01\01_3D\775.СТЛ.00.006 Планка.sldprt";
        private const string Plate = @"Z:\03_ЗАКАЗЫ\775_Стол\02_Металл\И01\01_3D\775.СТЛ.00.007 Пластина.sldprt";

        private static MaterialInfo Material(string name)
        {
            return new MaterialInfo { Name = name, LineDesignation = name + " ГОСТ 19903-2015" };
        }

        /// <summary>
        /// Изделие из трёх деталей: у «Кронштейна» обновится масса; у «Планки» тоже, но в ней несохранённые правки
        /// конструктора; у «Пластины» обновлять нечего, зато спорный материал. И обозначение «Тело5» у «Кронштейна».
        /// </summary>
        private static ReviewPlan Sample()
        {
            ReviewPlan plan = new ReviewPlan();
            plan.Add(Bracket, "775.СТЛ.00.005 Кронштейн", false).Changes.Add("[00] Масса: 1,2 → 1,3");
            plan.Add(Strip, "775.СТЛ.00.006 Планка", true).Changes.Add("[00] Масса: 0,4 → 0,5");
            plan.Add(Plate, "775.СТЛ.00.007 Пластина", false);

            ReviewQuestion material = new ReviewQuestion
            {
                Kind = ReviewQuestionKind.Material,
                Key = "8,0||Сталь 3",
                Subject = "Материал для «8,0»: сейчас «Сталь 3» — не подходит к профилю",
                Current = "Сталь 3",
                Fingerprint = ReviewAccepted.Material("8,0||Сталь 3")
            };
            material.Paths.Add(Plate);
            material.Places.Add("775.СТЛ.00.007 Пластина");
            foreach (string name in new[] { "Лист 8 Ст3сп", "Лист 8 09Г2С" })
            {
                material.Materials.Add(Material(name));
                material.Options.Add(name);
            }
            material.KeepIndex = material.Options.Count;
            material.Options.Add(StockText.KeepCaption("Сталь 3"));
            plan.Questions.Add(material);

            ReviewQuestion designation = new ReviewQuestion
            {
                Kind = ReviewQuestionKind.Designation,
                Key = Bracket,
                Subject = "Обозначение «Тело5» не совпадает с именем файла",
                Current = "Тело5",
                Expected = "775.СТЛ.00.005",
                Fingerprint = ReviewAccepted.Designation("Тело5", "775.СТЛ.00.005"),
                KeepIndex = 1,
                Answer = 0
            };
            designation.Paths.Add(Bracket);
            designation.Places.Add("775.СТЛ.00.005 Кронштейн");
            designation.Options.Add("Взять из имени файла: «775.СТЛ.00.005»");
            designation.Options.Add("Оставить как есть: «Тело5»");
            plan.Questions.Add(designation);
            return plan;
        }

        public static void Test_Accepted_answers_are_kept_in_the_model_property()
        {
            string raw = ReviewAccepted.Add("", ReviewAccepted.Material("8,0||Сталь 3"));
            Assert.IsTrue(ReviewAccepted.Contains(raw, ReviewAccepted.Material("8,0||Сталь 3")), "ответ записан: " + raw);
            Assert.AreEqual(raw, ReviewAccepted.Add(raw, ReviewAccepted.Material("8,0||Сталь 3")), "повторный ответ не меняет свойство");
            Assert.IsFalse(ReviewAccepted.Contains(raw, ReviewAccepted.Material("8,0||Сталь 20")), "другой стоящий материал — новый вопрос");

            string both = ReviewAccepted.Add(raw, ReviewAccepted.Designation("Тело5", "775.СТЛ.00.005"));
            Assert.AreEqual(2, ReviewAccepted.Parse(both).Count, "два ответа: " + both);
            Assert.IsFalse(ReviewAccepted.Contains(both, ReviewAccepted.Designation("Тело5", "775.СТЛ.00.009")),
                "файл переименован — вопрос об обозначении прозвучит снова");
            Assert.IsFalse(ReviewAccepted.Material("труба;40").Contains(";"), "разделитель внутри ответа заменён");

            // Длинная история ответов не растёт без предела: старые сжимаются до отпечатков, сверх того — вытесняются.
            string many = "";
            for (int i = 0; i < 40; i++) many = ReviewAccepted.Add(many, ReviewAccepted.Material(i + "х20х2|ГОСТ 8645-68|Сталь " + i));
            Assert.IsTrue(many.Length <= ReviewAccepted.MaxLength, "длина " + many.Length);
            Assert.IsTrue(ReviewAccepted.Contains(many, ReviewAccepted.Material("39х20х2|ГОСТ 8645-68|Сталь 39")), "последний ответ на месте");
            Assert.IsTrue(ReviewAccepted.Contains(many, ReviewAccepted.Material("25х20х2|ГОСТ 8645-68|Сталь 25")), "сжатый ответ тоже узнаётся");
            Assert.IsFalse(ReviewAccepted.Contains(many, ReviewAccepted.Material("0х20х2|ГОСТ 8645-68|Сталь 0")), "самый старый вытеснен");

            // Ревью 23.09.2026: сварная деталь, шесть профилей, длинные имена материалов библиотеки — все ответы одной
            // записи остаются, ни один не вытесняет другой.
            List<string> weldment = new List<string>();
            for (int i = 0; i < 6; i++)
                weldment.Add(ReviewAccepted.Material("40х20х" + (i + 2) + "|ГОСТ 8645-68|Труба профильная 40х20х" + (i + 2) +
                    " ГОСТ 8645-68 / Ст3сп ГОСТ 13663-86"));
            string value = ReviewAccepted.Add(ReviewAccepted.Add("", ReviewAccepted.Designation("Тело5", "775.СТЛ.00.005")), weldment);
            Assert.IsTrue(value.Length <= ReviewAccepted.MaxLength, "длина " + value.Length);
            foreach (string answer in weldment) Assert.IsTrue(ReviewAccepted.Contains(value, answer), "ответ на месте: " + answer);
            Assert.IsTrue(ReviewAccepted.Contains(value, ReviewAccepted.Designation("Тело5", "775.СТЛ.00.005")), "и прежний тоже");
            Assert.AreEqual(value, ReviewAccepted.Add(value, weldment), "повторная запись тех же ответов ничего не меняет");
        }

        public static void Test_Plan_saves_only_changed_files_without_designer_edits()
        {
            ReviewPlan plan = Sample();
            Assert.IsTrue(plan.HasWork, "окну есть что предложить");
            Assert.AreEqual(3, plan.ListedFiles().Count, "в списке все три: две с обновлениями, одна с вопросом");
            Assert.IsFalse(plan.File(Strip).Save, "файл с несохранёнными правками конструктора — без галочки");
            Assert.IsTrue(plan.File(Bracket).Save, "остальные — с галочкой");
            Assert.AreEqual(1, plan.ToSave(), "сохранится «Кронштейн»: «Пластина» без ответа не меняется, «Планка» без галочки");
            Assert.IsFalse(plan.WillChange(plan.File(Plate)), "вопрос без ответа файл не меняет");

            plan.Questions[0].Answer = plan.Questions[0].KeepIndex;
            Assert.IsTrue(plan.Questions[0].Kept, "ответ «оставить»");
            Assert.IsNull(plan.Questions[0].ChosenMaterial, "материал не выбран");
            Assert.IsTrue(plan.WillChange(plan.File(Plate)), "«оставить» пишется в модель — файл меняется");
            Assert.AreEqual(2, plan.ToSave(), "теперь сохранятся две");

            plan.Questions[0].Answer = 1;
            Assert.AreEqual("Лист 8 09Г2С", plan.Questions[0].ChosenMaterial.Name, "выбран второй материал");
            Assert.IsTrue(plan.Questions[1].FromFile, "обозначение — из имени файла");
            Assert.IsTrue(plan.Covers("775.СТЛ.00.005 Кронштейн.sldprt"), "находки «не записано» по «Кронштейну» окно исправит");
            Assert.IsFalse(plan.Covers("775.СТЛ.00.008 Уголок.sldprt"), "а по детали вне окна — нет");
        }

        public static void Test_Silent_answers_do_not_decide_for_the_designer()
        {
            ReviewPlan plan = Sample();
            ReviewQuestion single = new ReviewQuestion { Kind = ReviewQuestionKind.Material, Key = "6,0||Сталь 3", Current = "Сталь 3" };
            single.Materials.Add(Material("Лист 6 Ст3сп"));
            single.Options.Add("Лист 6 Ст3сп");
            single.KeepIndex = 1;
            single.Options.Add(StockText.KeepCaption("Сталь 3"));
            plan.Questions.Add(single);

            plan.AnswerSilently();
            Assert.AreEqual(-1, plan.Questions[0].Answer, "из нескольких материалов без конструктора не выбирается");
            Assert.AreEqual(-1, plan.Questions[1].Answer, "обозначение без конструктора не переписывается");
            Assert.AreEqual(0, single.Answer, "единственный подходящий заменяет неподходящий — как прежний обход изделия");
        }

        public static void Test_Review_form_leaves_open_questions_open()
        {
            ReviewPlan plan = Sample();
            List<Notice> findings = new List<Notice> { Notices.Of(NoticeLevel.Warning, "изделие", "нет книги ЛЗК изделия: нажмите «Ведомость ЛЗК»") };
            using (ProductReviewForm form = new ProductReviewForm(plan, findings, "Изделие 775.СТЛ.00", "проверка"))
            {
                ComboBox[] lists = form.AnswerLists();
                Assert.AreEqual(2, lists.Length, "два вопроса — два списка");
                Assert.AreEqual(ProductReviewForm.Later, (string)lists[0].Items[0], "первый пункт — решить позже");
                Assert.AreEqual(0, lists[0].SelectedIndex, "материал заранее не выбран");
                Assert.AreEqual(StockText.KeepCaption("Сталь 3"), (string)lists[0].Items[lists[0].Items.Count - 1], "последний — оставить");
                Assert.AreEqual(1, lists[1].SelectedIndex, "«Тело5» — явная случайность: имя файла предложено заранее");

                CheckedListBox save = form.SaveList;
                Assert.AreEqual(3, save.Items.Count, "три файла в списке сохранения");
                int strip = Enumerable.Range(0, save.Items.Count).First(i => ((string)save.Items[i]).Contains("Планка"));
                Assert.IsFalse(save.GetItemChecked(strip), "файл с правками конструктора без галочки");
                Assert.IsTrue(((string)save.Items[strip]).Contains("несохранённые правки"), "и видно почему: " + save.Items[strip]);
                Assert.AreEqual(ReviewPlan.SaveCaption(1), form.SaveCaption, "на кнопке — сколько файлов сохранится");

                lists[0].SelectedIndex = lists[0].Items.Count - 1;
                Assert.AreEqual(ReviewPlan.SaveCaption(2), form.SaveCaption, "ответ «оставить» добавил файл к сохранению");
                save.SetItemChecked(strip, true);
                Assert.AreEqual(ReviewPlan.SaveCaption(3), form.SaveCaption, "галочку можно поставить самому");
                for (int i = 0; i < save.Items.Count; i++) save.SetItemChecked(i, false);
                Assert.IsFalse(form.CanApplyAndSave, "сохранять нечего — кнопка недоступна");
                Assert.IsTrue(form.CanApply, "а записать без сохранения — можно");

                lists[1].SelectedIndex = 0;
                form.Commit(ReviewChoice.Apply);
                Assert.AreEqual(ReviewChoice.Apply, form.Choice, "выбор кнопки");
                Assert.IsTrue(plan.Questions[0].Kept, "ответ «оставить» — в плане");
                Assert.IsFalse(plan.Questions[1].Answered, "«решить позже» — без ответа");
                Assert.IsFalse(plan.File(Bracket).Save, "галочки — в плане");
            }
        }

        public static void Test_Kept_material_in_the_model_is_not_asked_again()
        {
            StockFinding f = new StockFinding
            {
                Folder = StockService.SheetFolderName,
                Request = new StockRequest { Size = "8,0" },
                Verdict = StockVerdict.Replace,
                CurrentMaterial = "Сталь 3"
            };
            string path = @"Z:\03_ЗАКАЗЫ\775_Стол\02_Металл\И01\01_3D\775.СТЛ.00.011 Косынка.sldprt";
            Assert.AreEqual(1, StockService.PendingDecisions(path, new[] { f }, "").Count, "без ответа — вопрос");
            string accepted = ReviewAccepted.Add("", ReviewAccepted.Material(StockService.DecisionKey(f)));
            // Замену подбор уже предложил — ответ из модели её снимает.
            f.Chosen = Material("Лист 8 Ст3сп");
            Assert.AreEqual(0, StockService.PendingDecisions(path, new[] { f }, accepted).Count, "ответ из модели — вопроса нет");
            Assert.IsTrue(f.Kept, "позиция оставлена");
            Assert.IsNull(f.Chosen, "и замены нет");

            StockFinding other = new StockFinding
            {
                Folder = StockService.SheetFolderName,
                Request = new StockRequest { Size = "8,0" },
                Verdict = StockVerdict.Replace,
                CurrentMaterial = "Сталь 20"
            };
            Assert.AreEqual(1, StockService.PendingDecisions(path, new[] { other }, accepted).Count, "сменился материал — вопрос снова");
        }

        public static void Test_Check_finding_marks_what_the_window_fixes()
        {
            // Вопрос о материале стоит в окне — в его «Замечаниях» он не повторяется (ревью 23.09.2026: повторялся по разу
            // на каждую деталь, с советом нажать кнопку, окно которой уже открыто). В отчёте без окна он остаётся.
            ReviewSession session = new ReviewSession();
            ReviewPlan sample = Sample();
            foreach (ReviewFile file in sample.Files) session.Plan.Files.Add(file);
            foreach (ReviewQuestion question in sample.Questions) session.Plan.Questions.Add(question);
            CheckReport report = new CheckReport();
            ProductReviewService.Findings(session, report);
            CheckFinding material = report.Findings.Single(x => x.Document == "775.СТЛ.00.007 Пластина.sldprt");
            Assert.IsTrue(material.Text.Contains("выберите материал"), material.Text);
            Assert.IsTrue(session.Plan.Handles(material), "вопрос о материале решает окно: " + material.Text);

            report.Add(CheckRules.Drawing, CheckRules.LevelOf(CheckRules.Drawing), "775.СТЛ.00.005 Кронштейн.sldprt", "нет чертежа");
            Assert.IsFalse(session.Plan.Handles(report.Findings.Last()), "чертёж окно не сделает");
            CheckFinding outside = report.Add(CheckRules.Sync, CheckRules.LevelOf(CheckRules.Sync), "775.СТЛ.00.008 Уголок.sldprt",
                "масса не записана в свойства: нажмите «Проверить изделие»");
            outside.Fixable = true;
            Assert.IsFalse(session.Plan.Handles(outside), "документа нет в окне — замечание остаётся");
            string what, hint;
            Notices.SplitHint(outside.Text, out what, out hint);
            Assert.AreEqual("Нажмите «Проверить изделие»", hint, "что сделать");
        }
    }
}
