using System;
using System.Collections.Generic;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Версия изделия (решение владельца 23.09.2026, З-27): проверка отмечает состояние файлов, ЛЗК и выгрузка сверяются с
    /// ним и запоминают версию, свои сохранения кнопок версию не меняют.
    /// </summary>
    public static class ProductStampTests
    {
        private const string Asm = "ТС-52.00.00.000 Стол.sldasm";
        private const string Part = "ТС-52.00.01.001 Царга.sldprt";

        private static string Sum(char c)
        {
            return new string(c, 64);
        }

        private static KeyValuePair<string, string> File(string name, string sum)
        {
            return new KeyValuePair<string, string>(name, sum);
        }

        private static string ReportText(string version)
        {
            CheckReport report = new CheckReport
            {
                Product = "ТС-52",
                Assembly = Asm,
                User = "Тестов Т.Т.",
                Time = new DateTime(2026, 9, 23, 14, 5, 0),
                Version = version
            };
            report.Add(CheckRules.Operations, CheckLevel.Issue, Part, "не заполнено свойство «Операции»");
            report.Checksums.Add(File(Asm, Sum('a')));
            report.Checksums.Add(File(Part, Sum('b')));
            report.Checksums.Add(File("Косынка.sldprt", ""));
            return CheckRules.Report(report);
        }

        public static void Test_Report_carries_version_and_stamp_reads_back()
        {
            string version = "23.09.2026 14:05:31 #1a2b3c4d";
            string text = ReportText(version);
            Assert.IsTrue(text.Contains("Версия:   " + version), text);
            ProductStamp stamp = ProductStamp.Parse(text);
            Assert.AreEqual(Asm, stamp.Assembly, "сборка");
            Assert.AreEqual(version, stamp.Version, "версия");
            Assert.AreEqual(3, stamp.Checksums.Count, "суммы всех файлов");
            Assert.AreEqual(Part, stamp.Checksums[1].Key, "имя с пробелом прочитано целиком");
            Assert.AreEqual(Sum('b'), stamp.Checksums[1].Value, "сумма");
            Assert.AreEqual("Косынка.sldprt", stamp.Checksums[2].Key, "не прочитанный файл — без суммы");
            Assert.AreEqual("", stamp.Checksums[2].Value, "его сумма пустая");
            Assert.AreEqual("ЗАМЕЧАНИЯ", CheckRules.OutcomeOf(text), "итог по-прежнему читается");

            ProductStamp legacy = ProductStamp.Parse(ReportText(""));
            Assert.AreEqual("", legacy.Version, "отчёт до 23.09.2026 — без версии");
            Assert.IsFalse(ReportText("").Contains("Версия:"), "пустая версия не печатается");
        }

        public static void Test_Differences_compare_files_by_name_and_sum()
        {
            List<KeyValuePair<string, string>> recorded = new List<KeyValuePair<string, string>>
            {
                File(Asm, Sum('a')), File(Part, Sum('b')), File("Ножка.sldprt", Sum('c')), File("Ножка.sldprt", Sum('d'))
            };
            // Порядок обхода и одинаковые имена файлов из разных папок не мешают.
            List<KeyValuePair<string, string>> same = new List<KeyValuePair<string, string>>
            {
                File("ножка.SLDPRT", Sum('d')), File(Part, Sum('b')), File(Asm, Sum('a')), File("Ножка.sldprt", Sum('c'))
            };
            Assert.AreEqual(0, ProductStamp.Differences(recorded, same).Count, "то же изделие");

            List<KeyValuePair<string, string>> changed = new List<KeyValuePair<string, string>>
            {
                File(Asm, Sum('a')), File(Part, Sum('e')), File("Ножка.sldprt", Sum('c')), File("Ножка.sldprt", Sum('d')),
                File("Пластина.sldprt", Sum('f'))
            };
            List<string> diff = ProductStamp.Differences(recorded, changed);
            Assert.AreEqual(2, diff.Count, string.Join(", ", diff.ToArray()));
            Assert.IsTrue(diff.Contains(Part), "изменённая деталь");
            Assert.IsTrue(diff.Contains("Пластина.sldprt"), "новая деталь");

            List<KeyValuePair<string, string>> fewer = new List<KeyValuePair<string, string>>
            {
                File(Asm, Sum('a')), File(Part, Sum('b')), File("Ножка.sldprt", Sum('c'))
            };
            diff = ProductStamp.Differences(recorded, fewer);
            Assert.AreEqual(1, diff.Count, "выбывший экземпляр одноимённого файла");
            Assert.AreEqual("Ножка.sldprt", diff[0], "его имя");

            List<KeyValuePair<string, string>> unreadable = new List<KeyValuePair<string, string>>
            {
                File(Asm, Sum('a')), File(Part, ""), File("Ножка.sldprt", Sum('c')), File("Ножка.sldprt", Sum('d'))
            };
            Assert.AreEqual(Part, ProductStamp.Differences(recorded, unreadable)[0], "не прочитанный файл не совпадает ни с чем");
        }

        public static void Test_Restamp_updates_only_files_saved_by_the_button_unchanged_since_check()
        {
            string text = ReportText("23.09.2026 14:05:31 #1a2b3c4d");
            List<StampChange> changes = new List<StampChange>
            {
                // Ведомость записала «Операции» в деталь, которая была ровно как при проверке.
                new StampChange { Name = Part, Before = Sum('b'), After = Sum('e') },
                // А эту сборку до кнопки изменили — её строку трогать нельзя.
                new StampChange { Name = Asm, Before = Sum('9'), After = Sum('f') }
            };
            int replaced;
            string updated = ProductStamp.Restamp(text, changes, "ведомость ЛЗК", new DateTime(2026, 9, 23, 14, 30, 0), out replaced);
            Assert.AreEqual(1, replaced, "одна строка");
            ProductStamp stamp = ProductStamp.Parse(updated);
            Assert.AreEqual("23.09.2026 14:05:31 #1a2b3c4d", stamp.Version, "версия не меняется");
            Assert.AreEqual(Sum('e'), stamp.Checksums[1].Value, "новая сумма детали");
            Assert.AreEqual(Sum('a'), stamp.Checksums[0].Value, "сумма сборки прежняя");
            int note = updated.IndexOf("Суммы обновлены: ведомость ЛЗК, 23.09.2026 14:30 — " + Part, StringComparison.Ordinal);
            int title = updated.IndexOf(ProductStamp.ChecksumTitle, StringComparison.Ordinal);
            Assert.IsTrue(note > 0 && note < title, "кто обновил — над суммами: " + updated);
            Assert.IsFalse(updated.Replace("\r\n", "").Contains("\n"), "переводы строк прежние");
            List<string> left = ProductStamp.Differences(stamp.Checksums, new List<KeyValuePair<string, string>>
            {
                File(Asm, Sum('a')), File(Part, Sum('e')), File("Косынка.sldprt", "")
            });
            Assert.AreEqual(1, left.Count, "следующая кнопка видит изменённым только не прочитанный файл: " + string.Join(", ", left.ToArray()));
            Assert.AreEqual("Косынка.sldprt", left[0], "это он");

            int chained;
            string twice = ProductStamp.Restamp(text, new List<StampChange>
            {
                // Выгрузка сохранила деталь после развёртки исполнения, затем после временной СК трубы.
                new StampChange { Name = Part, Before = Sum('e'), After = Sum('7') },
                new StampChange { Name = Part, Before = Sum('b'), After = Sum('e') }
            }, "выгрузка", new DateTime(2026, 9, 23, 15, 0, 0), out chained);
            Assert.AreEqual(2, chained, "обе записи цепочки");
            Assert.AreEqual(Sum('7'), ProductStamp.Parse(twice).Checksums[1].Value, "в отчёте последняя сумма файла");

            int none;
            string same = ProductStamp.Restamp(text, new[] { new StampChange { Name = Part, Before = Sum('0'), After = Sum('1') } },
                "выгрузка", DateTime.Now, out none);
            Assert.AreEqual(0, none, "ничего не совпало");
            Assert.AreEqual(text, same, "отчёт не тронут");
        }

        public static void Test_New_version_is_time_and_files()
        {
            DateTime time = new DateTime(2026, 9, 23, 14, 5, 31);
            List<KeyValuePair<string, string>> a = new List<KeyValuePair<string, string>> { File(Asm, Sum('a')), File(Part, Sum('b')) };
            List<KeyValuePair<string, string>> b = new List<KeyValuePair<string, string>> { File(Asm, Sum('a')), File(Part, Sum('c')) };
            List<KeyValuePair<string, string>> reordered = new List<KeyValuePair<string, string>> { File(Part, Sum('b')), File(Asm, Sum('a')) };
            string va = ProductStamp.NewVersion(time, a);
            Assert.IsTrue(va.StartsWith("23.09.2026 14:05:31 #", StringComparison.Ordinal), va);
            Assert.AreEqual(va.Length, "23.09.2026 14:05:31 #".Length + 8, "восемь знаков суммы");
            Assert.IsTrue(va != ProductStamp.NewVersion(time, b), "в ту же секунду, но другие файлы — другая версия");
            Assert.AreEqual(va, ProductStamp.NewVersion(time, reordered), "порядок обхода не важен");
        }

        public static void Test_Export_log_remembers_product_version()
        {
            ExportLog log = new ExportLog { Product = "ТС-52", User = "И", Version = "23.09.2026 14:05:31 #1a2b3c4d" };
            log.Add(@"D:\И\02_PDF\ТС-52.00.01.001 Царга.pdf");
            log.Checksums[log.Files[0]] = Sum('b');
            string text = log.Text();
            Assert.IsTrue(text.Contains("Версия:   23.09.2026 14:05:31 #1a2b3c4d"), text);
            ExportLog back = ExportLog.Parse(text);
            Assert.AreEqual(log.Version, back.Version, "версия читается");
            Assert.AreEqual(1, back.Files.Count, "файлы читаются, как прежде");

            ExportLog blank = new ExportLog { Product = "ТС-52", User = "И", Version = "" };
            Assert.IsTrue(blank.Text().Contains("Версия:   " + ProductStamp.Unchecked), "непроверенное изделие — словами");
            Assert.AreEqual("", ExportLog.Parse(blank.Text()).Version, "и читается как пустая версия");

            ExportLog legacy = ExportLog.Parse("Выгрузка для производства\r\nИзделие:  ТС-52\r\n");
            Assert.IsNull(legacy.Version, "отчёт до 23.09.2026 — без строки версии");
            Assert.IsFalse(new ExportLog().Text().Contains("Версия:"), "версия не задана — строки нет");
        }

        public static void Test_Changed_tube_cutting_marks_export_unchecked()
        {
            // ЛЗК записала другую «Лазерную резку трубы»: версия изделия прежняя, а выгрузка решала по этой галочке, делать
            // ли IGS, — её отчёт становится «не проверено», и «Готово к производству» попросит выгрузить заново.
            Assert.IsTrue(LzkOperations.TubeDecisionMayDiffer("Лазерная резка трубы; Покраска", "Покраска"), "галочку сняли");
            Assert.IsTrue(LzkOperations.TubeDecisionMayDiffer("Покраска", "Лазерная резка трубы; Покраска"), "галочку поставили");
            Assert.IsTrue(LzkOperations.TubeDecisionMayDiffer("", "Покраска"), "без операций решал признак профиля");
            Assert.IsFalse(LzkOperations.TubeDecisionMayDiffer("Покраска", "Покраска; Гибка"), "труба не затронута");
            Assert.IsFalse(LzkOperations.TubeDecisionMayDiffer("Лазерная резка трубы", "Лазерная резка трубы; Покраска"), "труба осталась");

            ExportLog log = new ExportLog { Product = "ТС-52", User = "И", Version = "23.09.2026 14:05:31 #1a2b3c4d" };
            log.Add(@"D:\И\03_ЧПУ\Труборез\ТС-52.00.01.001 Царга.igs");
            log.Checksums[log.Files[0]] = Sum('b');
            string text = log.Text();
            bool changed;
            string marked = ExportLog.MarkUnchecked(text, out changed);
            Assert.IsTrue(changed, "версия была");
            Assert.AreEqual(text.Replace("Версия:   23.09.2026 14:05:31 #1a2b3c4d", "Версия:   " + ProductStamp.Unchecked), marked,
                "меняется только строка версии");
            ExportLog back = ExportLog.Parse(marked);
            Assert.AreEqual("", back.Version, "читается как непроверенная");
            Assert.AreEqual(Sum('b'), back.Checksums[back.Files[0]], "файлы и суммы прежние");
            bool again;
            Assert.AreEqual(marked, ExportLog.MarkUnchecked(marked, out again), "второй раз — без изменений");
            Assert.IsFalse(again, "уже «не проверено»");
            bool legacy;
            ExportLog.MarkUnchecked("Выгрузка для производства\r\nИзделие:  ТС-52\r\n", out legacy);
            Assert.IsFalse(legacy, "отчёт до 23.09.2026 без строки версии не трогается");
        }

        public static void Test_Archived_files_leave_export_report()
        {
            // «Новая ревизия» уносит прежние файлы после выгрузки — отчёт выгрузки не должен на них ссылаться (ревью 23.09.2026).
            ExportLog log = new ExportLog { Product = "ТС-52", User = "И", Version = "23.09.2026 14:05:31 #1a2b3c4d" };
            log.Add(@"D:\И\02_PDF\ТС-52.00.01.001 Царга.pdf");
            log.Add(@"D:\И\02_PDF\ТС-52.00.01.001 Царга_Изм1.pdf");
            log.Checksums[log.Files[0]] = Sum('a');
            log.Checksums[log.Files[1]] = Sum('b');
            log.Skip("ТС-52.00.01.002 Косынка.sldprt", "нет чертежа");
            string text = log.Text();
            bool changed;
            string left = ExportLog.RemoveFiles(text, new[] { "тс-52.00.01.001 царга.pdf" }, out changed);
            Assert.IsTrue(changed, "файл был в отчёте");
            ExportLog back = ExportLog.Parse(left);
            Assert.AreEqual(1, back.Files.Count, "остался один файл");
            Assert.AreEqual("ТС-52.00.01.001 Царга_Изм1.pdf", back.Files[0], "новая ревизия осталась");
            Assert.AreEqual(Sum('b'), back.Checksums[back.Files[0]], "её сумма прежняя");
            Assert.AreEqual(1, back.Skipped.Count, "пропуски не тронуты");
            Assert.AreEqual("23.09.2026 14:05:31 #1a2b3c4d", back.Version, "версия не тронута");
            Assert.IsTrue(left.Contains("Файлов:   1, пропущено: 1"), "счёт файлов исправлен");
            Assert.IsTrue(left.Contains("Изделие:  ТС-52"), "шапка не тронута");
            bool again;
            Assert.AreEqual(left, ExportLog.RemoveFiles(left, new[] { "Другой.pdf" }, out again), "чужой файл — без изменений");
            Assert.IsFalse(again, "нечего убирать");
            string none = ExportLog.RemoveFiles(left, new[] { "ТС-52.00.01.001 Царга_Изм1.pdf" }, out changed);
            Assert.IsTrue(none.Contains("Ничего не выгружено.") && none.Contains("Файлов:   0, пропущено: 1"), none);
        }
    }
}
