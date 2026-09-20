using System;
using System.Collections.Generic;
using System.IO;
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
        /// Материал выбран конструктором и геометрии не соответствует: это и есть ошибка NC3-7R.02.001 —
        /// профиль прямоугольный по ГОСТ 8645-68, а материал от плоскоовальной трубы. Уведомляем, но не переписываем.
        /// </summary>
        public static void Test_Wrong_material_is_reported_but_not_overwritten()
        {
            StockFinding f = Finding(new StockRequest { Kind = StockKind.Profile, Size = "40х20х1,5", Gost = "ГОСТ 8645-68" },
                                     "Труба ПО 40х20х1,5 ГОСТ 8644-68 / 08пс ГОСТ 13663-86");
            Assert.AreEqual(StockVerdict.Mismatch, f.Verdict, "вердикт");
            Assert.IsFalse(f.NeedsAssign, "выбор конструктора не переписываем");
            Assert.IsTrue(StockService.Message(f).IndexOf("не соответствует геометрии", StringComparison.Ordinal) >= 0, "текст уведомления");
            Assert.IsTrue(StockService.Message(f).IndexOf("значение не изменено", StringComparison.Ordinal) >= 0, "сказано, что ничего не меняли");
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
