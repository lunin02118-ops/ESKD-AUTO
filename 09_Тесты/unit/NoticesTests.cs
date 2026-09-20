using System;
using System.Collections.Generic;
using System.Linq;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Окно «Замечания» (решение владельца 18.09.2026): уровни, подсказки, перевод находок каждой кнопки.</summary>
    public static class NoticesTests
    {
        public static void Test_SplitHint_moves_advice_to_its_own_column()
        {
            string what, hint;
            Notices.SplitHint("свойство «Наименование» не записано в модель: нажмите «Синхронизировать»", out what, out hint);
            Assert.AreEqual("свойство «Наименование» не записано в модель", what, "что не так");
            Assert.AreEqual("Нажмите «Синхронизировать»", hint, "что сделать");

            Notices.SplitHint("не заполнено свойство «Операции» (кнопка «Ведомость ЛЗК»)", out what, out hint);
            Assert.AreEqual("не заполнено свойство «Операции»", what, "скобка с кнопкой");
            Assert.AreEqual("Кнопка «Ведомость ЛЗК»", hint, "кнопка без скобки");

            Notices.SplitHint("сборка перестраивается с ошибками", out what, out hint);
            Assert.AreEqual("сборка перестраивается с ошибками", what, "без подсказки текст целиком");
            Assert.AreEqual("", hint, "подсказки нет");
        }

        public static void Test_Check_findings_map_to_three_levels_and_order()
        {
            CheckReport report = new CheckReport();
            report.Add(CheckRules.Drawing, CheckLevel.Issue, "А.101 Пластина.sldprt", "нет чертежа и не оформлена как безчертёжная (кнопка «Деталь БЧ»)");
            report.Add(CheckRules.References, CheckLevel.Defect, "А.102 Стойка.sldprt", "файл не найден");
            List<Notice> list = Notices.Ordered(Notices.FromCheck(report));
            Assert.AreEqual(NoticeLevel.Critical, list[0].Level, "брак — критично и первым");
            Assert.AreEqual(NoticeLevel.Warning, list[1].Level, "замечание");
            Assert.AreEqual("Кнопка «Деталь БЧ»", list[1].Hint, "подсказка");
            Assert.AreEqual(NoticeLevel.Critical, Notices.Max(list), "худший уровень");

            List<Notice> parsed = Notices.ParseCheckReport(CheckRules.Report(report));
            Assert.AreEqual(2, parsed.Count, "прежний отчёт читается обратно: " + CheckRules.Report(report));
            Assert.AreEqual("А.102 Стойка.sldprt", parsed.First(n => n.Level == NoticeLevel.Critical).Document, "документ");
        }

        public static void Test_Lzk_marks_are_warnings_and_estimates_are_info()
        {
            LzkResult r = new LzkResult();
            r.Issues.Add("Строка 3 (А.101): не заполнено «Масса»");
            r.Notes.Add("Строка 4 (А.102): длины заготовки в списке вырезов нет — измерена по модели (*)");
            r.Errors.Add("нет листа ведомости");
            List<Notice> list = Notices.FromLzk(r);
            Notice mass = list.Single(n => n.Level == NoticeLevel.Warning);
            Assert.AreEqual("Строка 3 (А.101)", mass.Document, "строка книги — документ");
            Assert.AreEqual("не заполнено «Масса»", mass.Text, "текст");
            Assert.IsTrue(mass.Hint.Contains("материал"), "подсказка по пометке: " + mass.Hint);
            Notice star = list.Single(n => n.Level == NoticeLevel.Info);
            Assert.IsTrue(star.Hint.Contains("RD1"), "«*» — уточнить длину: " + star.Hint);
            Assert.AreEqual(1, Notices.Count(list, NoticeLevel.Critical), "ошибка книги — критично");
        }

        public static void Test_Export_skips_split_by_severity()
        {
            List<Notice> list = Notices.FromExport(new[]
            {
                "А.101 Пластина.sldprt — нет чертежа: PDF не сделан",
                "А.102 Стойка.sldprt — документ выдан в производство, оформите новую ревизию",
                "А.103 Рама.slddrw — PDF не сохранён (код 2)"
            });
            Assert.AreEqual(NoticeLevel.Info, list[0].Level, "нет чертежа — к сведению");
            Assert.IsTrue(list[0].Hint.Contains("Деталь БЧ"), list[0].Hint);
            Assert.AreEqual(NoticeLevel.Warning, list[1].Level, "выданный документ — замечание");
            Assert.AreEqual("Кнопка «Новая ревизия» на чертеже", list[1].Hint, "подсказка ревизии");
            Assert.AreEqual(NoticeLevel.Critical, list[2].Level, "файл не получился — критично");
            Assert.AreEqual("А.103 Рама.slddrw", list[2].Document, "документ");
        }

        public static void Test_Export_warnings_are_warnings_before_skips()
        {
            ExportLog log = new ExportLog();
            log.Skip("А.101 Пластина.sldprt", "нет чертежа: PDF не сделан");
            log.Warn("А.102 Стойка.igs", "IGS в глобальной системе координат: ось трубы не найдена");
            List<Notice> list = Notices.FromExport(log.Skipped, log.Warnings);
            Assert.AreEqual(2, list.Count, "пропуск и замечание");
            Assert.AreEqual(NoticeLevel.Warning, list[0].Level, "IGS не по оси — замечание");
            Assert.AreEqual("А.102 Стойка.igs", list[0].Document, "документ замечания");
            string text = log.Text();
            Assert.IsTrue(text.Contains("Замечания:" + System.Environment.NewLine +
                "  А.102 Стойка.igs — IGS в глобальной системе координат"), text);
            Assert.IsTrue(text.Contains("пропущено: 1"), "замечание не считается пропуском");
        }

        public static void Test_Independent_log_reports_changed_source_as_critical()
        {
            IndependentLog log = new IndependentLog();
            log.Created.Add(new IndependentEntry { Source = @"Y:\Б\Пластина.sldprt", Target = @"Y:\И\01_3D\А.150 Пластина.sldprt", Drawing = @"Y:\И\01_3D\А.150 Пластина.slddrw", Instances = 2, Dangling = 1 });
            log.Skip("Болт.sldprt", "покупное или стандартное изделие: оно приходит готовым");
            log.Skip("Уголок.sldprt", "SolidWorks не сделал деталь независимой");
            log.Source(@"Y:\Б\Пластина.sldprt", false);
            List<Notice> list = Notices.FromIndependent(log);
            Assert.AreEqual(2, Notices.Count(list, NoticeLevel.Critical), "эталон изменился и отказ SolidWorks — критично");
            Assert.AreEqual(1, Notices.Count(list, NoticeLevel.Warning), "оборванные размеры — замечание");
            Assert.AreEqual(2, Notices.Count(list, NoticeLevel.Info), "покупное пропущено и созданная копия — к сведению");
            Assert.IsTrue(list.Any(n => n.Text.Contains("экземпляров перепривязано: 2")), "созданное перечислено");
        }

        public static void Test_Remember_keeps_last_notices_as_text()
        {
            Notices.Remember(new[] { Notices.Of(NoticeLevel.Info, "Д", "сведение"), Notices.Of(NoticeLevel.Critical, "К", "беда") });
            string text = Notices.LastText;
            Assert.IsTrue(text.StartsWith("КРИТИЧНО — К — беда", StringComparison.Ordinal), text);
            Assert.IsTrue(text.Contains("К СВЕДЕНИЮ — Д — сведение"), text);
            Notices.Remember(null);
            Assert.AreEqual("", Notices.LastText, "сброс");
        }
    }
}
