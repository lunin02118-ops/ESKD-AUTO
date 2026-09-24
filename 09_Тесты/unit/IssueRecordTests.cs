using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Выданное в производство и новая ревизия (аудит 23.09.2026): отчёт выдачи `_Выдано_…` читается проверкой
    /// (CHK-13), строка журнала несохранённой ревизии помечается «ОТМЕНЕНО» (SAVE-9), файлы выдачи документа
    /// узнаются по имени точно (REV-2).
    /// </summary>
    public static class IssueRecordTests
    {
        /// <summary>
        /// Папка изделия, которая есть, но не перечисляется (сетевая папка отвалилась, оборванная ссылка): Directory.Exists —
        /// да, Directory.GetFiles — исключение. Здесь — соединение (junction) на удалённую папку.
        /// </summary>
        private static string Unlistable()
        {
            string root = Path.Combine(Path.GetTempPath(), "eskd_unlistable_" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(root, "target"), link = Path.Combine(root, "link");
            Directory.CreateDirectory(target);
            System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true };
            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)) process.WaitForExit();
            Directory.Delete(target);
            return link;
        }

        private static void Release(string link)
        {
            string root = Path.GetDirectoryName(link);
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, true);
        }

        public static void Test_Latest_of_unlistable_folder_is_null()
        {
            // Сверка SW API 23.09.2026, №7: исключение уходило из finally ЛЗК и выгрузки прямо в SolidWorks.
            string dir = Unlistable();
            try
            {
                bool denied = false;
                try
                {
                    Directory.GetFiles(dir);
                }
                catch (IOException)
                {
                    denied = true;
                }
                Assert.IsTrue(Directory.Exists(dir) && denied, "подготовка: папка есть, список не читается");
                Assert.IsNull(IssueRecord.Latest(dir), "папку не перечислить — отчёта выдачи нет, без исключения");
            }
            finally
            {
                Release(dir);
            }
        }

        public static void Test_Restamp_survives_unlistable_folder()
        {
            string dir = Unlistable();
            try
            {
                ESKD.MaterialSync.Sw.ProductFreshness.Restamp(dir, new List<StampChange>
                {
                    new StampChange { Path = Path.Combine(dir, "Деталь.sldprt"), Name = "Деталь.sldprt", Before = "aa", After = "bb" }
                }, "тест");
            }
            finally
            {
                Release(dir);
            }
        }

        private const string Part = "ТС-52.00.01.001 Царга.sldprt";
        private const string Drawing = "ТС-52.00.01.001 Царга.slddrw";
        private const string Bch = "ТС-52.00.01.002 Косынка.sldprt";
        private const string Asm = "ТС-52.00.00.000 Стол.sldasm";

        private static string Sum(char c)
        {
            return new string(c, 64);
        }

        private static string Temp()
        {
            string folder = Path.Combine(Path.GetTempPath(), "eskd_issue_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Отчёт выдачи в том виде, в каком его пишет «Готово к производству».</summary>
        private static string IssueText(int journalLine)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Готово к производству");
            sb.AppendLine("Изделие:  ТС-52 (И01_ТС-52)");
            sb.AppendLine("Сборка:   " + Asm);
            sb.AppendLine("Отметил:  Тестов Т.Т., 20.09.2026 10:15");
            sb.AppendLine("Папка:    X:\\Заказ\\02_Металл\\И01_ТС-52");
            if (journalLine >= 0) sb.AppendLine(IssueRecord.JournalText(journalLine));
            sb.AppendLine();
            sb.AppendLine("Выдано (SHA-256):");
            sb.AppendLine("  " + Sum('f') + "  ТС-52.00.01.001 Царга.pdf");
            sb.AppendLine();
            sb.AppendLine(IssueRecord.DocumentsTitle);
            sb.AppendLine("  " + Sum('a') + "  " + Asm);
            sb.AppendLine("  " + Sum('b') + "  " + Part);
            sb.AppendLine("  " + Sum('c') + "  " + Drawing);
            sb.AppendLine("  " + Sum('d') + "  " + Bch);
            return sb.ToString();
        }

        private static ChangeRow Row(int number, string document, DateTime date, bool cancelled = false)
        {
            return new ChangeRow { Number = number, Revision = 1, Date = date, Document = document, What = "правка", Cancelled = cancelled };
        }

        public static void Test_Issue_record_reads_time_journal_line_and_documents()
        {
            IssueRecord record = IssueRecord.Parse(IssueText(3));
            Assert.AreEqual(new DateTime(2026, 9, 20, 10, 15, 0), record.Time, "время выдачи из строки «Отметил»");
            Assert.AreEqual(3, record.JournalLine, "строка журнала на момент выдачи");
            Assert.AreEqual(4, record.Documents.Count, "документы изделия — без файлов блока «Выдано»");
            Assert.AreEqual(Part, record.Documents[1].Key, "имя файла");
            Assert.AreEqual(Sum('b'), record.Documents[1].Value, "сумма файла");
            Assert.AreEqual("Журнал:   строка 3 (Изменения.xlsx)", IssueRecord.JournalText(3), "строка шапки");

            IssueRecord legacy = IssueRecord.Parse(IssueText(-1).Replace("\r\n", "\n"));
            Assert.AreEqual(-1, legacy.JournalLine, "отчёт до 23.09.2026 — строки журнала нет");
            Assert.AreEqual(4, legacy.Documents.Count, "переводы строк LF читаются так же");
        }

        public static void Test_Changed_issued_documents_need_a_revision()
        {
            IssueRecord issued = IssueRecord.Parse(IssueText(3));
            Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { Asm, Sum('1') },      // сборка меняется от сохранения после правки детали — не сверяется
                { Part, Sum('2') },     // деталь изменена — ревизию поднимают на её чертеже
                { Drawing, Sum('c') },
                { Bch, Sum('4') }       // деталь БЧ изменена — ревизия у неё самой
            };
            List<KeyValuePair<string, string>> changed = IssueRecord.Unrevised(issued, current, new ChangeRow[0]);
            Assert.AreEqual(2, changed.Count, "изменены деталь и деталь БЧ, сборка не сверяется: " +
                string.Join("; ", changed.Select(c => c.Key + " → " + c.Value).ToArray()));
            Assert.AreEqual(Drawing, changed.First(c => c.Key == Part).Value, "у детали с чертежом ревизия — на чертеже");
            Assert.AreEqual(Bch, changed.First(c => c.Key == Bch).Value, "у детали БЧ — на ней самой");

            // Строки журнала: до выдачи и отменённая — не ревизия; после выдачи — ревизия.
            List<ChangeRow> journal = new List<ChangeRow>
            {
                Row(3, Drawing, new DateTime(2026, 9, 19)),
                Row(4, Bch, new DateTime(2026, 9, 21), cancelled: true)
            };
            Assert.AreEqual(2, IssueRecord.Unrevised(issued, current, journal).Count, "строка до выдачи и отменённая не считаются");
            journal.Add(Row(5, Drawing, new DateTime(2026, 9, 21)));
            journal.Add(Row(6, Bch.ToUpperInvariant(), new DateTime(2026, 9, 21)));
            Assert.AreEqual(0, IssueRecord.Unrevised(issued, current, journal).Count, "ревизии после выдачи сняли замечания");
        }

        public static void Test_Unread_and_unchanged_files_are_not_flagged()
        {
            IssueRecord issued = IssueRecord.Parse(IssueText(0));
            Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { Part, Sum('B') },     // та же сумма в другом регистре
                { Drawing, "" },        // не прочитан — изменённым не считается
            };
            Assert.AreEqual(0, IssueRecord.Unrevised(issued, current, null).Count, "без изменений замечаний нет");
            Assert.AreEqual(0, IssueRecord.Unrevised(null, current, null).Count, "не выдано — замечаний нет");
        }

        public static void Test_Legacy_issue_record_counts_revisions_by_date()
        {
            IssueRecord issued = IssueRecord.Parse(IssueText(-1));
            Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { Part, Sum('2') },
                { Drawing, Sum('c') }
            };
            Assert.AreEqual(1, IssueRecord.Unrevised(issued, current, new[] { Row(1, Drawing, new DateTime(2026, 9, 19)) }).Count,
                "строка раньше выдачи — не ревизия");
            Assert.AreEqual(0, IssueRecord.Unrevised(issued, current, new[] { Row(1, Drawing, new DateTime(2026, 9, 20)) }).Count,
                "строка в день выдачи и позже — ревизия");
        }

        public static void Test_Latest_issue_record_and_restamp_of_its_documents()
        {
            string folder = Temp();
            try
            {
                Assert.IsNull(IssueRecord.Latest(folder), "выдачи не было");
                File.WriteAllText(Path.Combine(folder, "_Выдано_2026-09-18_0900.txt"), IssueText(1), new UTF8Encoding(true));
                File.WriteAllText(Path.Combine(folder, "_Выдано_2026-09-20_1015.txt"), IssueText(3), new UTF8Encoding(true));
                File.WriteAllText(Path.Combine(folder, "_Выдано_2026-09-20_1015_2.txt"), IssueText(4), new UTF8Encoding(true));
                IssueRecord latest = IssueRecord.Latest(folder);
                Assert.NotNull(latest, "отчёт выдачи найден");
                Assert.AreEqual(4, latest.JournalLine, "последний — второй отчёт той же минуты");
                Assert.IsTrue(latest.Path.EndsWith("_2.txt", StringComparison.Ordinal), "путь последнего отчёта");

                // Сохранение кнопки (ЛЗК, выгрузка) — сумма в отчёте выдачи обновляется, другие блоки не трогаются.
                int replaced;
                string text = ProductStamp.Restamp(IssueText(4), new[] { new StampChange { Name = Part, Before = Sum('b'), After = Sum('e') } },
                    "ведомость ЛЗК", new DateTime(2026, 9, 21, 9, 0, 0), IssueRecord.DocumentsTitle, out replaced);
                Assert.AreEqual(1, replaced, "одна сумма обновлена");
                IssueRecord restamped = IssueRecord.Parse(text);
                Assert.AreEqual(Sum('e'), restamped.Documents.First(d => d.Key == Part).Value, "новая сумма детали");
                Assert.IsTrue(text.Contains(Sum('f') + "  ТС-52.00.01.001 Царга.pdf"), "блок «Выдано» не тронут");
                Assert.AreEqual(2, text.Split(new[] { ProductStamp.RestampLabel }, StringSplitOptions.None).Length,
                    "одна строка «Суммы обновлены»");
                string none = ProductStamp.Restamp(IssueText(4), new[] { new StampChange { Name = Part, Before = Sum('9'), After = Sum('e') } },
                    "ведомость ЛЗК", new DateTime(2026, 9, 21, 9, 0, 0), IssueRecord.DocumentsTitle, out replaced);
                Assert.AreEqual(0, replaced, "файл изменён и до кнопки — сумма не подменяется");
                Assert.AreEqual(IssueText(4), none, "текст без изменений");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        public static void Test_Issue_files_of_a_document_are_matched_exactly()
        {
            const string stem = "ТС-52.00.01.001 Деталь1";
            Assert.IsTrue(ExportNaming.BelongsTo(stem + ".pdf", stem), "PDF черновика");
            Assert.IsTrue(ExportNaming.BelongsTo(stem + "_Изм2.pdf", stem), "PDF ревизии");
            Assert.IsTrue(ExportNaming.BelongsTo(stem + "_S3мм_2шт_120х80.dxf", stem), "DXF развёртки");
            Assert.IsTrue(ExportNaming.BelongsTo(stem + "_S2.5мм_120х80_Изм1.dxf", stem), "DXF с дробной толщиной и ревизией");
            Assert.IsTrue(ExportNaming.BelongsTo(stem.ToUpperInvariant() + ".IGS", stem), "регистр не важен");
            Assert.IsFalse(ExportNaming.BelongsTo("ТС-52.00.01.001 Деталь10.pdf", stem), "«Деталь10» — другой документ");
            Assert.IsFalse(ExportNaming.BelongsTo(stem + " длинная.pdf", stem), "другое наименование");
            Assert.IsFalse(ExportNaming.BelongsTo(stem + "_Изм.pdf", stem), "«_Изм» без номера — не ревизия");
            Assert.IsFalse(ExportNaming.BelongsTo("ЛЗК_ТС-52.pdf", ""), "пустая основа не совпадает ни с чем");
        }

        public static void Test_Cancelled_journal_row_is_marked_not_removed()
        {
            string folder = Temp();
            try
            {
                string path = ChangeLog.Path(folder);
                Assert.IsFalse(ChangeLog.Cancel(path, 1, "штамп не сохранён"), "журнала нет — нечего отменять");
                for (int i = 1; i <= 2; i++)
                    ChangeLog.Append(path, new ChangeRow
                    {
                        Revision = i,
                        Date = new DateTime(2026, 9, 23),
                        Who = "Тестов Т.Т.",
                        Document = Drawing,
                        What = "правка " + i,
                        Reason = "устранение ошибок",
                        Code = "4",
                        Backlog = "использовать"
                    });
                Assert.IsTrue(ChangeLog.Cancel(path, 2, "штамп ревизии 2 не сохранён"), "строка помечена");
                Assert.IsTrue(ChangeLog.Cancel(path, 2, "повтор"), "повторная пометка — не ошибка");
                Assert.IsFalse(ChangeLog.Cancel(path, 7, "нет такой"), "нет строки — false");

                List<ChangeRow> rows = ChangeLog.Read(path);
                Assert.AreEqual(2, rows.Count, "строка не удалена: номера журнала идут подряд");
                Assert.IsFalse(rows[0].Cancelled, "первая строка действует");
                Assert.IsTrue(rows[1].Cancelled, "вторая отменена");
                Assert.AreEqual("ОТМЕНЕНО: штамп ревизии 2 не сохранён — правка 2", rows[1].What, "пометка одна, текст сохранён");
                Assert.AreEqual(2, rows[1].Number, "номер строки");
                Assert.AreEqual(new DateTime(2026, 9, 23), rows[1].Date, "дата строки");
                Assert.AreEqual(Drawing, rows[1].Document, "документ строки");
                Assert.AreEqual(3, ChangeLog.Append(path, new ChangeRow { Revision = 2, Date = DateTime.Today, Document = Drawing, What = "снова" }),
                    "следующая строка — после отменённой");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        public static void Test_Row_cancelled_by_hand_is_recognised()
        {
            // Книга была занята — конструктор помечает строку сам, как велит сообщение (ревью 23.09.2026).
            Assert.IsTrue(ChangeLog.IsCancelled("ОТМЕНЕНО: штамп не сохранён — правка"), "пометка кнопки");
            Assert.IsTrue(ChangeLog.IsCancelled("ОТМЕНЕНО правка"), "без двоеточия");
            Assert.IsTrue(ChangeLog.IsCancelled("  отменено: правка"), "пробелы и регистр");
            Assert.IsFalse(ChangeLog.IsCancelled("правка ОТМЕНЕНО"), "слово не в начале — строка действует");
            Assert.IsFalse(ChangeLog.IsCancelled(""), "пусто");
            Assert.IsFalse(ChangeLog.IsCancelled(null), "нет текста");
        }

        public static void Test_Unread_journal_gives_no_line_number()
        {
            // Журнал есть, но не читается: номер -1, в отчёте выдачи — «не прочитан», проверка сверяет по дате.
            string folder = Temp();
            try
            {
                string path = ChangeLog.Path(folder);
                File.WriteAllText(path, "это не книга Excel");
                Assert.AreEqual(-1, ChangeLog.LastNumber(path), "журнал не прочитан");
                string line = IssueRecord.JournalText(-1);
                Assert.IsFalse(line.Contains("строка"), "номера строки нет: " + line);
                Assert.AreEqual(-1, IssueRecord.Parse(IssueText(-1) + line + "\r\n").JournalLine, "читается как отчёт без номера");
                Assert.AreEqual(0, ChangeLog.LastNumber(Path.Combine(folder, "нет.xlsx")), "журнала нет — 0");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        public static void Test_Lock_files_of_solidworks_are_not_documents()
        {
            Assert.IsFalse(IssueRecord.IsDocumentFile("~$" + Part), "метка «файл открыт»");
            Assert.IsFalse(IssueRecord.IsDocumentFile(@"D:\И\01_3D\~$" + Drawing), "с путём");
            Assert.IsTrue(IssueRecord.IsDocumentFile(Part), "деталь");
            IssueRecord issued = IssueRecord.Parse(IssueText(3) + "  " + Sum('7') + "  ~$" + Part + "\r\n");
            Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { Part, Sum('b') },
                { "~$" + Part, Sum('8') }
            };
            Assert.AreEqual(0, IssueRecord.Unrevised(issued, current, null).Count, "метка в прежнем отчёте выдачи не даёт замечания");
        }
    }
}
