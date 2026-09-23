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

        // ----------------------------------------------------------------- окно выбора
        /// <summary>
        /// Окно выбора: один типоразмер — одна строка, даже если позиций с ним в детали несколько, а в списке
        /// стоят обозначения материала, а не имена файлов библиотеки. Показывать окно тесту нельзя (прогон
        /// остановился бы на модальном окне), поэтому проверяется его состав и то, что выбрано.
        /// </summary>
        public static void Test_Pick_form_asks_once_per_size_and_keeps_the_choice()
        {
            List<MaterialInfo> library = Library();
            StockFinding first = Finding("Элемент списка вырезов1", Sheet(library, "6,0"), "6,0");
            StockFinding same = Finding("Элемент списка вырезов2", Sheet(library, "6,0"), "6,0");
            StockFinding other = Finding("листовой металл", Sheet(library, "10,0"), "10,0");

            using (StockPickForm form = new StockPickForm("ПРТИ.301111.001 Пластина",
                                                          new List<StockFinding> { first, same, other }))
            {
                ComboBox[] lists = form.Lists();
                Assert.AreEqual(2, lists.Length, "две строки: 6,0 спрашивается один раз на обе позиции");
                Assert.AreEqual(first.Match.Candidates.Count, lists[0].Items.Count, "варианты листа 6 мм");
                Assert.AreEqual(StockPickForm.Describe(first.Match.First), (string)lists[0].Items[0],
                                "в списке обозначение материала, а не имя файла библиотеки");

                Assert.AreEqual(-1, lists[0].SelectedIndex, "заранее ничего не выбрано: вопрос с готовым ответом — не вопрос");

                lists[0].SelectedIndex = 1;   // конструктор выбрал вторую марку
                form.ReadChoices();

                MaterialInfo picked;
                Assert.IsTrue(form.Chosen.TryGetValue(StockService.DecisionKey(first), out picked), "выбор по листу 6 мм запомнен");
                Assert.AreEqual(first.Match.Candidates[1].Name, picked.Name, "запомнено именно выбранное");

                form.ApplyTo(new[] { first, same, other });
                Assert.AreEqual(first.Match.Candidates[1].Name, first.Chosen.Name, "выбор дошёл до первой позиции");
                Assert.AreEqual(first.Match.Candidates[1].Name, same.Chosen.Name, "и до второй позиции того же листа");
                Assert.IsNull(other.Chosen, "лист 10 мм не выбран — назначать нечего");
            }
        }

        /// <summary>
        /// Материал стоит, но с профилем не сходится, а подходящий один (Replace): заменить выбор конструктора можно
        /// только с его согласия — в списке есть «Оставить как есть», и ответ «оставить» снимает замену (23.09.2026).
        /// </summary>
        public static void Test_Pick_form_offers_to_keep_a_material_that_disagrees_with_geometry()
        {
            List<MaterialInfo> library = Library();
            StockMatch eight = Sheet(library, "8,0");
            StockFinding replace = Finding("листовой металл", eight, "8,0");
            replace.Verdict = StockVerdict.Replace;
            replace.CurrentMaterial = "Сталь 3";
            replace.Chosen = eight.First;
            Assert.IsTrue(replace.NeedsDecision, "замена материала конструктора — это вопрос, а не молчаливое действие");

            using (StockPickForm form = new StockPickForm("ПРТИ.301111.005 Косынка", new List<StockFinding> { replace }))
            {
                ComboBox list = form.Lists()[0];
                Assert.AreEqual(eight.Candidates.Count + 1, list.Items.Count, "кандидаты и пункт «оставить»");
                Assert.AreEqual(StockPickForm.KeepCaption("Сталь 3"), (string)list.Items[list.Items.Count - 1], "последний пункт — оставить");
                Assert.IsTrue(form.Captions()[0].Contains("сейчас «Сталь 3»"), "в строке виден стоящий материал: " + form.Captions()[0]);
                Assert.AreEqual(-1, list.SelectedIndex, "заранее ничего не выбрано");

                list.SelectedIndex = list.Items.Count - 1;
                form.ReadChoices();
                form.ApplyTo(new[] { replace });
                Assert.IsTrue(replace.Kept, "ответ «оставить» запомнен в позиции");
                Assert.IsNull(replace.Chosen, "замены не будет");
                Assert.IsFalse(replace.NeedsAssign, "назначать нечего");
            }

            // Окно закрыто без ответа («Позже»): замена тоже отменяется, назначается только то, где материала не было.
            StockFinding later = Finding("листовой металл", eight, "8,0");
            later.Verdict = StockVerdict.Replace;
            later.CurrentMaterial = "Сталь 3";
            later.Chosen = eight.First;
            StockFinding empty = Finding("Элемент списка вырезов1", eight, "8,0");
            empty.Verdict = StockVerdict.Assign;
            empty.Chosen = eight.First;
            StockService.Decline(new[] { later, empty });
            Assert.IsNull(later.Chosen, "«Позже» — материал конструктора не заменён");
            Assert.IsTrue(empty.NeedsAssign, "пустой материал по-прежнему подставляется");

            // «Оставить» помнится до конца сеанса по детали: при следующем сохранении вопрос не повторяется.
            string path = @"D:\И\01_3D\ПРТИ.301111.005 Косынка.sldprt";
            StockService.RememberKept(path, new[] { replace });
            StockFinding again = Finding("листовой металл", eight, "8,0");
            again.Verdict = StockVerdict.Replace;
            again.CurrentMaterial = "Сталь 3";
            again.Chosen = eight.First;
            Assert.AreEqual(0, StockService.PendingDecisions(path, new[] { again }).Count, "вопрос не повторяется");
            Assert.IsTrue(again.Kept && again.Chosen == null, "и замены нет");
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
        public static void Test_Pick_form_keys_the_answer_by_size_and_gost()
        {
            List<MaterialInfo> library = Library();
            StockFinding a = Finding("Элемент списка вырезов1", Sheet(library, "6,0"), "6,0");
            StockFinding b = Finding("Элемент списка вырезов2", Sheet(library, "6,0"), "6,0");
            b.Request.Gost = "ГОСТ 19281-2014";
            Assert.IsTrue(StockService.DecisionKey(a) != StockService.DecisionKey(b), "ключи разные");
            using (StockPickForm form = new StockPickForm("", new List<StockFinding> { a, b }))
            {
                Assert.AreEqual(2, form.Lists().Length, "две строки — по сортаменту");
                form.Lists()[0].SelectedIndex = 0;
                form.ReadChoices();
                form.ApplyTo(new[] { a, b });
                Assert.NotNull(a.Chosen, "первая строка выбрана");
                Assert.IsNull(b.Chosen, "вторая строка не получила чужой выбор");
            }
        }

        /// <summary>Кнопка «Позже» ничего не выбирает: молча подставить материал за конструктора нельзя.</summary>
        public static void Test_Pick_form_keeps_nothing_until_the_choice_is_read()
        {
            List<MaterialInfo> library = Library();
            StockFinding finding = Finding("листовой металл", Sheet(library, "6,0"), "6,0");
            using (StockPickForm form = new StockPickForm("", new List<StockFinding> { finding }))
            {
                Assert.AreEqual(0, form.Chosen.Count, "пока выбор не подтверждён, назначать нечего");
            }
        }

        /// <summary>
        /// При обходе изделия в одном окне сходятся позиции разных деталей. По именам папок списка вырезов
        /// («Элемент списка вырезов2») не понять, где они, поэтому в строке стоит деталь.
        /// </summary>
        public static void Test_Pick_form_names_the_parts_when_walking_a_product()
        {
            List<MaterialInfo> library = Library();
            StockMatch six = Sheet(library, "6,0");
            StockFinding first = Finding("Элемент списка вырезов2", six, "6,0");
            StockFinding second = Finding("Элемент списка вырезов2", six, "6,0");
            first.Owner = "ПРТИ.301111.001 Стойка";
            second.Owner = "ПРТИ.301111.002 Полка";

            using (StockPickForm form = new StockPickForm("Изделие ПРТИ.301111.000 СБ Рама",
                                                          new List<StockFinding> { first, second }))
            {
                Assert.AreEqual(1, form.Lists().Length, "один типоразмер — один вопрос на обе детали");
                string caption = form.Captions()[0];
                Assert.IsTrue(caption.IndexOf("Стойка", StringComparison.Ordinal) >= 0 &&
                              caption.IndexOf("Полка", StringComparison.Ordinal) >= 0,
                              "в строке названы обе детали: " + caption);
            }
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
        /// Материал стоит и геометрии не соответствует, подходящий один — заменяем молча (решение владельца 22.09.2026:
        /// «он должен менять, предупреждение — только если вариантов несколько»). Случай с экрана владельца: профиль
        /// 80х80х4, а материал от трубы 40х40х2.
        /// </summary>
        public static void Test_Wrong_material_with_single_candidate_is_replaced()
        {
            StockFinding f = Finding(new StockRequest { Kind = StockKind.Profile, Size = "30х15х1,5", Gost = "ГОСТ 8644-68" },
                                     "Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 16523-97");
            Assert.AreEqual(StockVerdict.Replace, f.Verdict, "вердикт");
            Assert.NotNull(f.Chosen, "на что заменить");
            Assert.IsTrue(f.NeedsAssign, "позиция идёт в назначение");
            Assert.IsFalse(f.NeedsChoice, "не спрашиваем");
            Assert.AreEqual("", StockService.Message(f), "без предупреждения");
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
    }
}
