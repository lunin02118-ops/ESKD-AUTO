using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using ESKD.MaterialSync.Sw;

namespace ESKD.Tests
{
    /// <summary>
    /// Подбор материала по геометрии (Р-8, решение владельца 20.09.2026). Проверяется на настоящей библиотеке
    /// ЕСКД: именно она решает, где материал подставляется молча, а где конструктора надо спросить.
    /// </summary>
    public static class StockTests
    {
        private static string LibraryPath()
        {
            return Path.Combine(DictionaryTests.RepoRoot(), "04_Библиотеки_Материалов_и_Профилей",
                                "Библиотека материалов", "Библиотека_Материалов_ГОСТ.sldmat");
        }

        private static List<MaterialInfo> Library()
        {
            List<MaterialInfo> all = MaterialCatalog.All(new[] { LibraryPath() });
            Assert.IsTrue(all.Count > 100, "библиотека прочитана: материалов " + all.Count);
            return all;
        }

        private static StockMatch Profile(List<MaterialInfo> library, string size, string gost)
        {
            return StockCatalog.Match(library, new StockRequest { Kind = StockKind.Profile, Size = size, Gost = gost });
        }

        private static StockMatch Sheet(List<MaterialInfo> library, string size)
        {
            return StockCatalog.Match(library, new StockRequest { Kind = StockKind.Sheet, Size = size });
        }

        // ----------------------------------------------------------------- нормализация
        /// <summary>Типоразмер пишут и кириллицей, и латиницей, и с хвостовым нулём — для сверки это одно и то же.</summary>
        public static void Test_Size_normalization_ignores_letter_case_and_trailing_zeros()
        {
            Assert.AreEqual("40x20x1.5", StockCatalog.NormalizeSize("40Х20Х1,50"), "кириллическая х и хвостовой ноль");
            Assert.AreEqual("40x20x1.5", StockCatalog.NormalizeSize("40x20x1.5"), "латиница и точка");
            Assert.AreEqual("40x20x1.5", StockCatalog.NormalizeSize(" 40 × 20 × 1,5 мм "), "знак умножения, пробелы, «мм»");
            Assert.AreEqual("2", StockCatalog.NormalizeSize("2,0"), "2,0 — это 2");
            Assert.AreEqual("16", StockCatalog.NormalizeSize("16"), "целое без дробной части");
            Assert.AreEqual("0.95", StockCatalog.NormalizeSize("0,95"), "меньше единицы");
            Assert.AreEqual("", StockCatalog.NormalizeSize(""), "пусто остаётся пустым");
        }

        /// <summary>Толщина из элемента листового металла приходит долями миллиметра с дрожью double.</summary>
        public static void Test_Thickness_becomes_size()
        {
            Assert.AreEqual("8", StockCatalog.SizeFromThickness(8.0), "8 мм");
            Assert.AreEqual("3", StockCatalog.SizeFromThickness(3.0000000000000004), "дрожь double гасится");
            Assert.AreEqual("1.5", StockCatalog.SizeFromThickness(1.5), "полтора миллиметра");
            Assert.AreEqual("0.95", StockCatalog.SizeFromThickness(0.95), "оцинковка");
            Assert.AreEqual("", StockCatalog.SizeFromThickness(0), "нулевой толщины не бывает");
            Assert.AreEqual("", StockCatalog.SizeFromThickness(double.NaN), "не листовая деталь");
        }

        /// <summary>ГОСТ пишут с приставкой и без, с пробелом и без — сортамент от этого не меняется.</summary>
        public static void Test_Gost_normalization()
        {
            Assert.AreEqual("8644-68", StockCatalog.NormalizeGost("ГОСТ 8644-68"), "с приставкой");
            Assert.AreEqual("8644-68", StockCatalog.NormalizeGost("гост8644-68"), "без пробела, строчными");
            Assert.AreEqual("8644-68", StockCatalog.NormalizeGost("8644-68"), "без приставки");
            Assert.IsTrue(StockCatalog.NormalizeGost("ГОСТ 8644-68") != StockCatalog.NormalizeGost("ГОСТ 8645-68"),
                          "плоскоовальная и прямоугольная — разные сортаменты");
        }

        /// <summary>Свежесть редакции ГОСТа: две цифры — прошлый век, четыре — как написано.</summary>
        public static void Test_Newest_year_in_name()
        {
            Assert.AreEqual(1989, StockCatalog.NewestYear("ГОСТ 14637-89"), "две цифры — прошлый век");
            Assert.AreEqual(2024, StockCatalog.NewestYear("ГОСТ 14637-2024"), "четыре цифры — как написано");
            Assert.AreEqual(2015, StockCatalog.NewestYear("Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"),
                            "в имени берётся самый поздний год");
            Assert.IsTrue(StockCatalog.NewestYear("Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024") >
                          StockCatalog.NewestYear("Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"),
                          "редакция 2024 свежее редакции 89");
            Assert.AreEqual(0, StockCatalog.NewestYear("без года"), "года нет");
        }

        // ----------------------------------------------------------------- подбор по настоящей библиотеке
        /// <summary>
        /// Случай владельца: плоскоовальная труба 30х15х1,5 в библиотеке одна — спрашивать нечего, подставляем молча.
        /// </summary>
        public static void Test_Flat_oval_tube_matches_exactly_one()
        {
            StockMatch m = Profile(Library(), "30х15х1,5", "ГОСТ 8644-68");
            Assert.IsTrue(m.Single, "плоскоовальная 30х15х1,5: кандидатов " + m.Candidates.Count);
            Assert.IsTrue(m.First.Name.IndexOf("ПО 30х15х1,5", StringComparison.Ordinal) >= 0,
                          "подобран именно плоскоовальный профиль: " + m.First.Name);
            Assert.AreEqual("ГОСТ 8644-68", m.First.GostSortament, "сортамент подобранного материала");
        }

        /// <summary>
        /// Ловушка, на которой уже ошиблись в NC3-7R.02.001: типоразмер один и тот же, сортамент разный.
        /// Поэтому профиль без ГОСТа не подбирается вовсе.
        /// </summary>
        public static void Test_Profile_without_gost_is_not_guessed()
        {
            StockMatch m = Profile(Library(), "40х20х1,5", "");
            Assert.IsTrue(m.None, "без ГОСТа сортамент угадывать нельзя: кандидатов " + m.Candidates.Count);
        }

        /// <summary>
        /// Лист 8 мм: в библиотеке две записи, но свойства у них одинаковые — для конструктора это один материал,
        /// и окно выбора ему не нужно.
        /// </summary>
        public static void Test_Sheet_8mm_collapses_to_single_choice()
        {
            StockMatch m = Sheet(Library(), "8,0");
            Assert.IsTrue(m.Single, "лист 8,0: кандидатов " + m.Candidates.Count + " — редакции ГОСТа должны схлопнуться");
            Assert.AreEqual("Ст3сп", m.First.Grade, "марка листа 8 мм");
            Assert.IsTrue(m.First.Name.IndexOf("14637-2024", StringComparison.Ordinal) >= 0,
                          "остаётся свежая редакция: " + m.First.Name);
        }

        /// <summary>
        /// Лист 6 мм — ровно тот случай, ради которого делалось окно выбора: Ст3сп и 09Г2С, решает конструктор.
        /// </summary>
        public static void Test_Sheet_6mm_offers_two_grades()
        {
            StockMatch m = Sheet(Library(), "6,0");
            Assert.AreEqual(2, m.Candidates.Count, "лист 6,0: два варианта по марке стали");
            List<string> grades = new List<string>();
            foreach (MaterialInfo info in m.Candidates) grades.Add(info.Grade);
            Assert.IsTrue(grades.Contains("Ст3сп") && grades.Contains("09Г2С"),
                          "предлагаются Ст3сп и 09Г2С, а не редакции ГОСТа: " + string.Join(", ", grades.ToArray()));
        }

        /// <summary>
        /// Лист 3 мм: обе записи описывают один материал, но у одной имя осталось от прежней редакции
        /// («… / Ст3сп ГОСТ 14637-89» при свойстве «ГОСТ_Материал = ГОСТ 16523-97»). Детали назначается та,
        /// чьё имя со своими же свойствами не спорит — иначе в дереве стоял бы ГОСТ, которого в материале нет.
        /// Найдено на настоящих деталях заказа NC3-7R 20.09.2026.
        /// </summary>
        public static void Test_Sheet_3mm_keeps_the_name_that_matches_its_own_gost()
        {
            StockMatch m = Sheet(Library(), "3,0");
            Assert.IsTrue(m.Single, "лист 3,0: кандидатов " + m.Candidates.Count);
            Assert.AreEqual("ГОСТ 16523-97", m.First.GostMaterial, "ГОСТ материала");
            Assert.IsTrue(m.First.Name.IndexOf("16523-97", StringComparison.Ordinal) >= 0,
                          "имя должно совпадать со своим ГОСТом, а не остаться от прежней редакции: " + m.First.Name);
            Assert.IsTrue(StockCatalog.NameAgreesWithGost(m.First), "имя не спорит со свойствами");
        }

        // ----------------------------------------------------------------- ответ конструктора
        // Окно выбора материала после сохранения убрано (решение владельца 23.09.2026, З-27): о материале спрашивает окно
        // «Синхронизировать» / «Проверить изделие» (ReviewTests). Здесь — что до ответа и после него происходит с позицией.

        /// <summary>
        /// Сохранение детали — не ответ: замена материала конструктора и выбор из нескольких отменяются, назначается только
        /// то, где материала не было, а подходящий один. «Оставить» помнится по детали до конца сеанса.
        /// </summary>
        public static void Test_Unanswered_question_keeps_the_designer_material()
        {
            List<MaterialInfo> library = Library();
            StockMatch eight = Sheet(library, "8,0");
            StockFinding replace = Finding("листовой металл", eight, "8,0");
            replace.Verdict = StockVerdict.Replace;
            replace.CurrentMaterial = "Сталь 3";
            replace.Chosen = eight.First;
            Assert.IsTrue(replace.NeedsDecision, "замена материала конструктора — это вопрос, а не молчаливое действие");
            StockFinding choose = Finding("Элемент списка вырезов2", Sheet(library, "6,0"), "6,0");
            choose.Chosen = choose.Match.First;
            StockFinding empty = Finding("Элемент списка вырезов1", eight, "8,0");
            empty.Verdict = StockVerdict.Assign;
            empty.Chosen = eight.First;
            StockService.Decline(new[] { replace, choose, empty });
            Assert.IsNull(replace.Chosen, "материал конструктора не заменён");
            Assert.IsNull(choose.Chosen, "из нескольких за конструктора не выбрано");
            Assert.IsTrue(empty.NeedsAssign, "пустой материал с одним подходящим подставляется");
            Assert.IsTrue(StockService.Message(replace).Contains(StockService.WhereToAnswer),
                "в строке состояния — где ответить: " + StockService.Message(replace));
            Assert.IsTrue(StockService.Message(replace).Contains(StockText.Describe(eight.First)), "и что подходит");

            // «Оставить» помнится до конца сеанса по детали: при следующем сохранении вопрос не повторяется.
            replace.Kept = true;
            string path = @"D:\И\01_3D\ПРТИ.301111.005 Косынка.sldprt";
            StockService.RememberKept(path, new[] { replace });
            StockFinding again = Finding("листовой металл", eight, "8,0");
            again.Verdict = StockVerdict.Replace;
            again.CurrentMaterial = "Сталь 3";
            again.Chosen = eight.First;
            Assert.AreEqual(0, StockService.PendingDecisions(path, new[] { again }).Count, "вопрос не повторяется");
            Assert.IsTrue(again.Kept, "позиция оставлена");
            Assert.IsNull(again.Chosen, "и замены нет");
            StockFinding changed = Finding("листовой металл", eight, "8,0");
            changed.Verdict = StockVerdict.Replace;
            changed.CurrentMaterial = "Сталь 20";
            changed.Chosen = eight.First;
            Assert.AreEqual(1, StockService.PendingDecisions(path, new[] { changed }).Count, "другой стоящий материал — новый вопрос");
        }

        /// <summary>
        /// Один типоразмер разных сортаментов — разные вопросы: выбор для трубы одного ГОСТа не уходит позиции того же
        /// размера другого ГОСТа (ключ ответа раньше был только по размеру).
        /// </summary>
        public static void Test_Answer_key_is_size_gost_and_current_material()
        {
            List<MaterialInfo> library = Library();
            StockFinding a = Finding("Элемент списка вырезов1", Sheet(library, "6,0"), "6,0");
            StockFinding b = Finding("Элемент списка вырезов2", Sheet(library, "6,0"), "6,0");
            b.Request.Gost = "ГОСТ 19281-2014";
            Assert.IsTrue(StockService.DecisionKey(a) != StockService.DecisionKey(b), "ключи разные");
            StockFinding c = Finding("Элемент списка вырезов3", Sheet(library, "6,0"), "6,0");
            Assert.AreEqual(StockService.DecisionKey(a), StockService.DecisionKey(c), "та же позиция в другой папке — тот же вопрос");
            MaterialInfo sheet = new MaterialInfo { Name = "Лист 6 Ст3сп", LineDesignation = "Лист Б-ПН-НО-6,0 ГОСТ 19903-2015 / Ст3сп" };
            Assert.AreEqual(sheet.LineDesignation, StockText.Describe(sheet), "в ответах — обозначение материала, а не имя файла библиотеки");
            Assert.AreEqual("Лист 6 Ст3сп", StockText.Describe(new MaterialInfo { Name = "Лист 6 Ст3сп" }), "обозначения нет — имя");
        }

        private static StockFinding Finding(string folder, StockMatch match, string size)
        {
            StockFinding finding = new StockFinding
            {
                Folder = folder,
                Request = new StockRequest { Kind = StockKind.Sheet, Size = size, Source = folder },
                Verdict = StockVerdict.Choose
            };
            finding.Match.Candidates.AddRange(match.Candidates);
            return finding;
        }

        /// <summary>Толщины, которой нет в библиотеке, подобрать нельзя — об этом надо сказать, а не молчать.</summary>
        public static void Test_Unknown_thickness_finds_nothing()
        {
            Assert.IsTrue(Sheet(Library(), "7,0").None, "листа 7 мм в библиотеке нет");
        }

        /// <summary>Плита, фанера и кромка листовым прокатом не считаются: у детали из листового металла их быть не может.</summary>
        public static void Test_Sheet_search_ignores_boards_and_plywood()
        {
            StockMatch m = Sheet(Library(), "16");
            foreach (MaterialInfo info in m.Candidates)
                Assert.IsTrue(StockCatalog.IsSheetStock(info), "в листовой прокат попало лишнее: " + info.Name);
        }

        // ----------------------------------------------------------------- правило владельца
        private static StockFinding Finding(StockRequest request, string current)
        {
            StockFinding f = new StockFinding { Request = request, CurrentMaterial = current, Folder = "проверка" };
            StockService.Decide(f, Library());
            return f;
        }

        /// <summary>Материала нет, подходящий один — назначаем сами, конструктор ничего не вводит.</summary>
        public static void Test_Empty_material_with_single_candidate_is_assigned()
        {
            StockFinding f = Finding(new StockRequest { Kind = StockKind.Profile, Size = "30х15х1,5", Gost = "ГОСТ 8644-68" }, "");
            Assert.AreEqual(StockVerdict.Assign, f.Verdict, "вердикт");
            Assert.NotNull(f.Chosen, "что назначать");
            Assert.IsTrue(f.NeedsAssign, "позиция идёт в назначение");
        }

        /// <summary>Материала нет, подходящих несколько — спрашиваем, молча не выбираем за конструктора.</summary>
        public static void Test_Empty_material_with_several_candidates_asks()
        {
            StockFinding f = Finding(new StockRequest { Kind = StockKind.Sheet, Size = "6,0" }, "");
            Assert.AreEqual(StockVerdict.Choose, f.Verdict, "вердикт");
            Assert.IsNull(f.Chosen, "до выбора назначать нечего");
            Assert.IsTrue(f.NeedsChoice, "позиция идёт в окно выбора");
            Assert.IsTrue(StockService.Message(f).IndexOf("выберите", StringComparison.Ordinal) >= 0, "текст замечания");
        }

        /// <summary>Типоразмера нет в библиотеке — замечание, и ничего не трогаем.</summary>
        public static void Test_Unknown_size_reports_and_changes_nothing()
        {
            StockFinding f = Finding(new StockRequest { Kind = StockKind.Sheet, Size = "7,0" }, "");
            Assert.AreEqual(StockVerdict.NotInLibrary, f.Verdict, "вердикт");
            Assert.IsFalse(f.NeedsAssign, "назначать нечего");
            Assert.IsTrue(StockService.Message(f).IndexOf("не найден в библиотеке", StringComparison.Ordinal) >= 0, "текст замечания");
        }

        /// <summary>Материал стоит и соответствует геометрии — молчим.</summary>
        public static void Test_Matching_material_is_left_alone()
        {
            StockFinding f = Finding(new StockRequest { Kind = StockKind.Profile, Size = "30х15х1,5", Gost = "ГОСТ 8644-68" },
                                     "Труба ПО 30х15х1,5 ГОСТ 8644-68 / 08пс ГОСТ 13663-86");
            Assert.AreEqual(StockVerdict.Ok, f.Verdict, "вердикт");
            Assert.AreEqual("", StockService.Message(f), "говорить не о чем");
        }

        /// <summary>
        /// Материал стоит и геометрии не соответствует, подходящий один (случай с экрана владельца: профиль 80х80х4, а
        /// материал от трубы 40х40х2). Замена готова, но это выбор конструктора: сохранение его не переписывает, в строке
        /// состояния — что подходит и где ответить; заменяет ответ в окне «Синхронизировать» / «Проверить изделие», где
        /// этот вариант первый (решения владельца 23.09.2026, З-25 и З-27; до них, 22.09.2026, менялось молча).
        /// </summary>
        public static void Test_Wrong_material_with_single_candidate_is_offered()
        {
            StockFinding f = Finding(new StockRequest { Kind = StockKind.Profile, Size = "30х15х1,5", Gost = "ГОСТ 8644-68" },
                                     "Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97");
            Assert.AreEqual(StockVerdict.Replace, f.Verdict, "вердикт");
            Assert.NotNull(f.Chosen, "на что заменить");
            Assert.IsTrue(f.NeedsDecision, "решает конструктор");
            Assert.IsFalse(f.NeedsChoice, "вариант один — выбирать из нескольких не нужно");
            string message = StockService.Message(f);
            Assert.IsTrue(message.Contains("не соответствует геометрии") && message.Contains(StockText.Describe(f.Chosen)),
                "в строке состояния — что подходит: " + message);
            Assert.IsTrue(message.Contains(StockService.WhereToAnswer), "и где ответить: " + message);
        }

        /// <summary>Материал не соответствует, подходящих несколько — спрашиваем, сами не выбираем.</summary>
        public static void Test_Wrong_material_with_several_candidates_asks()
        {
            StockFinding f = Finding(new StockRequest { Kind = StockKind.Sheet, Size = "6,0" },
                                     "Труба ПО 30х15х1,5 ГОСТ 8644-68 / 08пс ГОСТ 13663-86");
            Assert.AreEqual(StockVerdict.Choose, f.Verdict, "вердикт");
            Assert.IsNull(f.Chosen, "до выбора назначать нечего");
            Assert.IsTrue(StockService.Message(f).IndexOf("не соответствует геометрии", StringComparison.Ordinal) >= 0, "текст вопроса");
        }

        /// <summary>Материал не соответствует, а типоразмера нет в библиотеке — замечание, стоящий материал не трогаем.</summary>
        public static void Test_Wrong_material_without_candidates_is_kept()
        {
            StockFinding f = Finding(new StockRequest { Kind = StockKind.Sheet, Size = "7,0" },
                                     "Труба ПО 30х15х1,5 ГОСТ 8644-68 / 08пс ГОСТ 13663-86");
            Assert.AreEqual(StockVerdict.NotInLibrary, f.Verdict, "вердикт");
            Assert.IsFalse(f.NeedsAssign, "менять не на что");
        }

        /// <summary>
        /// Лист 8 мм с материалом из прежней редакции ГОСТа расхождением не считается: свойства у записей те же,
        /// иначе надстройка ругалась бы на исправные детали прошлых заказов.
        /// </summary>
        public static void Test_Same_record_under_another_library_name_is_not_a_mismatch()
        {
            StockFinding f = Finding(new StockRequest { Kind = StockKind.Sheet, Size = "8,0" },
                                     "Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89");
            Assert.AreEqual(StockVerdict.Ok, f.Verdict, "прежняя редакция имени — тот же материал");
        }

        /// <summary>ГОСТ из строки сортамента — запасной путь, когда отдельного свойства «ГОСТ» у папки нет.</summary>
        public static void Test_Gost_is_taken_from_sortament_when_property_is_missing()
        {
            Assert.AreEqual("ГОСТ 8645-68", StockService.GostFrom("Труба прямоугольная 40х20х1,5 ГОСТ 8645-68"), "из сортамента");
            Assert.AreEqual("", StockService.GostFrom("Труба прямоугольная 40х20х1,5"), "ГОСТа в строке нет");
            Assert.AreEqual("", StockService.GostFrom(""), "пустая строка");
        }

        public static void Test_Sheet_bodies_of_different_thickness_are_unclear()
        {
            // Сверка SW API 23.09.2026, №39: толщина первого «Листового металла» в дереве шла всей многотельной детали, и
            // тела 8 мм получали «Лист 3». Разные толщины (или нелистовое тело рядом) — материал по толщине не подбирается.
            double single;
            Assert.AreEqual("", StockCatalog.SheetBodiesProblem(new[] { 8.0, 8.0 }, out single), "одна толщина — вся деталь");
            Assert.AreEqual(8.0, single, "её толщина");
            string mixed = StockCatalog.SheetBodiesProblem(new[] { 3.0, 8.0 }, out single);
            Assert.IsTrue(mixed.Contains("разной толщины") && mixed.Contains("3") && mixed.Contains("8"), mixed);
            Assert.IsTrue(double.IsNaN(single), "толщины для всей детали нет");
            string plain = StockCatalog.SheetBodiesProblem(new[] { 3.0, double.NaN }, out single);
            Assert.IsTrue(plain.Contains("не листов"), plain);
            Assert.AreEqual("", StockCatalog.SheetBodiesProblem(new[] { double.NaN }, out single), "листовых тел нет — не листовая деталь");
            Assert.IsTrue(double.IsNaN(single), "и толщины нет");
            StockFinding f = new StockFinding { Folder = StockService.SheetFolderName, Verdict = StockVerdict.Unclear,
                Request = new StockRequest { Kind = StockKind.Sheet, Size = mixed } };
            string message = StockService.Message(f);
            Assert.IsTrue(message.Contains("разной толщины") && message.Contains("назначьте"), message);
            Assert.IsFalse(f.NeedsAssign, "не назначается");
            Assert.IsFalse(f.NeedsDecision, "и не спрашивается");
        }

        public static void Test_Sheet_bodies_with_fitting_materials_are_settled()
        {
            // Ревью 23.09.2026: замечание «тела разной толщины — назначьте телам сами» повторялось при каждом сохранении и
            // после того, как конструктор так и сделал. Решено — у каждого листового тела материал его толщины, у не
            // листового — какой-нибудь материал.
            // Подходит ли материал толщине — правилом самой надстройки (FitsName: и дубль библиотеки под другим именем).
            Func<double, string, bool> fit = (mm, m) => mm == 3.0 ? m == "Лист 3" :
                mm == 8.0 && (m == "Лист 8 Ст3" || m == "Лист 8 09Г2С");
            Assert.IsTrue(StockCatalog.SheetBodiesSettled(new[] { 3.0, 8.0 }, new[] { "Лист 3", " Лист 8 09Г2С" }, fit),
                "у каждого тела подходящий");
            Assert.IsFalse(StockCatalog.SheetBodiesSettled(new[] { 3.0, 8.0 }, new[] { "Лист 3", "Лист 3" }, fit), "у тела 8 мм лист 3");
            Assert.IsFalse(StockCatalog.SheetBodiesSettled(new[] { 3.0, 8.0 }, new[] { "Лист 3", "" }, fit), "у тела нет материала");
            Assert.IsTrue(StockCatalog.SheetBodiesSettled(new[] { 3.0, double.NaN }, new[] { "Лист 3", "Круг 20" }, fit),
                "не листовое тело — любой материал");
            Assert.IsFalse(StockCatalog.SheetBodiesSettled(new[] { 3.0, double.NaN }, new[] { "Лист 3", "" }, fit),
                "не листовое тело без материала");
            Assert.IsFalse(StockCatalog.SheetBodiesSettled(new[] { 3.0, double.NaN }, new[] { "Лист 3", "Лист 3" }, fit),
                "у не листового тела материал листа (унаследован от детали) — никто не решал");
            Assert.IsFalse(StockCatalog.SheetBodiesSettled(new[] { 3.0 }, new string[0], fit), "материалы не прочитаны — не решено");
        }

        public static void Test_Whole_part_material_that_took_is_counted()
        {
            // Ревью 23.09.2026: материал детали встал, а тела позиции со своим материалом его перекрывают. Раньше отчёт писал
            // «не назначен», хотя материал детали уже сменился, и графа 3 с другими исполнениями не обновлялись.
            bool applied, propagate;
            string bodies = "у тел «Т1» свой материал перекрывает материал детали — снимите его";
            string library = "SolidWorks взял его из библиотеки «X», а не «Y»";
            string w = StockCatalog.WholePartResult("Старый", "Т", "Т", "", bodies, out applied, out propagate);
            Assert.IsTrue(applied, "материал детали сменился — назначен");
            Assert.IsTrue(propagate, "в другие исполнения — как всегда");
            Assert.IsTrue(w.StartsWith("Материал «Т» назначен детали, но у тел «Т1»"), w);
            w = StockCatalog.WholePartResult("", " Т ", "Т", "", "", out applied, out propagate);
            Assert.IsTrue(applied && propagate, "встал без замечаний");
            Assert.AreEqual("", w, "замечания нет");
            w = StockCatalog.WholePartResult("Т", "Т", "Т", "", bodies, out applied, out propagate);
            Assert.IsFalse(applied, "материал детали уже стоял — ничего не изменилось, назначением это не считается");
            Assert.IsTrue(w.Contains("не назначен") && w.Contains("уже стоит") && w.Contains("«Т1»"), w);
            w = StockCatalog.WholePartResult("", "Т", "Т", library, "", out applied, out propagate);
            Assert.IsTrue(applied, "материал детали сменился — графа 3 должна это видеть");
            Assert.IsFalse(propagate, "материал чужой библиотеки в другие исполнения не переносится");
            Assert.IsTrue(w.Contains("«X»") && w.Contains("вручную") && w.Contains("не переносится"), w);
            w = StockCatalog.WholePartResult("", "Старый", "Т", "", "", out applied, out propagate);
            Assert.IsFalse(applied, "SolidWorks оставил прежний");
            Assert.IsTrue(w.Contains("не назначен") && w.Contains("«Старый»"), w);
            w = StockCatalog.WholePartResult("", "", "Т", "", "", out applied, out propagate);
            Assert.IsFalse(applied, "SolidWorks не поставил");
            Assert.IsTrue(w.Contains("не назначен") && w.Contains("не поставил"), w);
        }

        public static void Test_Foreign_body_material_is_asked_not_overwritten()
        {
            // Сверка SW API 23.09.2026, №31: у одного тела трубы свой материал листа, у другого — подходящий материал детали.
            // Раньше позиция считалась «без материала» (Assign), подходящий молча ставился детали, а лист тела оставался. Теперь
            // стоящий материал позиции — лист: заменить его можно только с согласия конструктора.
            const string tube = "Труба ПО 30х15х1,5 ГОСТ 8644-68 / 08пс ГОСТ 13663-86";
            const string sheet = "Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97";
            List<MaterialInfo> library = Library();
            StockFinding f = new StockFinding { Request = new StockRequest { Kind = StockKind.Profile, Size = "30х15х1,5", Gost = "ГОСТ 8644-68" } };
            Assert.AreEqual(sheet, StockService.PositionCurrentFor(f, library, new[] { sheet, tube }), "лист у части тел — он");
            Assert.AreEqual("", StockService.PositionCurrentFor(f, library, new[] { tube, "" }), "подходящий и без материала — дозаполнить");
            Assert.AreEqual(tube, StockService.PositionCurrentFor(f, library, new[] { tube, tube }), "у всех подходящий — он");
            f.CurrentMaterial = StockService.PositionCurrentFor(f, library, new[] { "", sheet });
            StockService.Decide(f, library);
            Assert.AreEqual(StockVerdict.Replace, f.Verdict, "не подходящий материал тела — замена с согласия");
            Assert.IsTrue(f.NeedsDecision, "решает конструктор");
        }

        /// <summary>Позиция папки списка вырезов: вердикт Assign, материал chosen, bodies тел (заглушки — счёт, не геометрия).</summary>
        private static StockFinding CutListPosition(MaterialInfo chosen, int bodies)
        {
            StockFinding finding = new StockFinding { Verdict = StockVerdict.Assign, Chosen = chosen, FromCutList = true };
            for (int i = 0; i < bodies; i++) finding.Bodies.Add(null);
            return finding;
        }

        public static void Test_Frame_of_one_tube_in_two_lengths_is_whole_part()
        {
            // Сверка SW API 23.09.2026, №41: рама из одной трубы разной длины — папка списка вырезов на каждую длину, и каждая
            // позиция покрывала часть тел: материал ставился телам, а на SolidWorks 2025 телу он через API не встаёт (T14).
            // Позиции одного материала вместе покрывают все тела — это вся деталь.
            MaterialInfo tube = new MaterialInfo { Name = "Труба ПО 30х15х1,5 ГОСТ 8644-68 / 08пс ГОСТ 13663-86", Database = "Библиотека" };
            MaterialInfo sheet = new MaterialInfo { Name = "Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024", Database = "Библиотека" };
            StockFinding longer = CutListPosition(tube, 1), shorter = CutListPosition(tube, 2);
            Assert.IsTrue(StockService.CoversWholePart(new[] { longer, shorter }, longer, 3), "две длины одной трубы — вся деталь");
            Assert.IsTrue(StockService.CoversWholePart(new[] { longer, shorter }, shorter, 3), "и со стороны второй папки");
            Assert.IsFalse(StockService.CoversWholePart(new[] { longer }, longer, 3), "одна длина из трёх тел — часть");
            StockFinding plate = CutListPosition(sheet, 1);
            Assert.IsFalse(StockService.CoversWholePart(new[] { longer, shorter, plate }, longer, 4),
                "с пластиной другого материала — только тела трубы");
            StockFinding kept = CutListPosition(tube, 1);
            kept.Verdict = StockVerdict.Ok;
            Assert.IsFalse(StockService.CoversWholePart(new[] { shorter, kept }, shorter, 3), "позиция без назначения группу не дополняет");
        }

        public static void Test_Empty_cut_list_folder_is_not_whole_part()
        {
            // Сверка SW API 23.09.2026, №32: папка без тел — погашенный элемент другого исполнения, а не «вся деталь».
            MaterialInfo tube = new MaterialInfo { Name = "Труба 40х40х2,0 ГОСТ 8639-82 / Ст3сп ГОСТ 13663-86", Database = "Библиотека" };
            StockFinding hidden = CutListPosition(tube, 0);
            Assert.IsFalse(StockService.CoversWholePart(new[] { hidden }, hidden, 1), "пустая папка — не вся деталь");
            StockFinding sheet = new StockFinding { Verdict = StockVerdict.Assign, Chosen = tube };
            Assert.IsTrue(StockService.CoversWholePart(new[] { sheet }, sheet, 1), "листовая деталь без тел в позиции — вся");
            Assert.IsFalse(StockService.CoversWholePart(new StockFinding[0], null, 1), "позиции нет");
        }
    }
}
