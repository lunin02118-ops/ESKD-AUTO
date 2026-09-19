using System;
using System.IO;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    public static class ParserTests
    {
        public static void Test_CleanDocumentName_strips_path_extension_and_sheet()
        {
            Assert.AreEqual("ПРТИ.468211.010 Стойка", DesignationParser.CleanDocumentName(@"C:\CAD\ПРТИ.468211.010 Стойка.sldprt"), "путь и расширение");
            Assert.AreEqual("Чертеж1", DesignationParser.CleanDocumentName("Чертеж1 - Лист1.slddrw"), "лист чертежа");
            Assert.AreEqual("ПРТИ.001", DesignationParser.CleanDocumentName("ПРТИ.001 - Sheet2"), "лист на английском");
            Assert.AreEqual("Сборка рамы", DesignationParser.CleanDocumentName("Сборка рамы.SLDASM"), "регистр расширения");
            Assert.AreEqual("", DesignationParser.CleanDocumentName("   "), "пустое имя");
        }

        public static void Test_StripExecutionSuffix_variants()
        {
            string exec, code;
            Assert.AreEqual("ПРТИ.468211.010", DesignationParser.StripExecutionSuffix("ПРТИ.468211.010", out exec, out code), "без суффикса");
            Assert.IsNull(exec, "исполнение");
            Assert.AreEqual("", code, "код");
            Assert.AreEqual("ПРТИ.468211.010", DesignationParser.StripExecutionSuffix("ПРТИ.468211.010-01", out exec, out code), "-01");
            Assert.AreEqual("01", exec, "исполнение -01");
            Assert.AreEqual("ПРТИ.468211.010", DesignationParser.StripExecutionSuffix("ПРТИ.468211.010-02 СБ", out exec, out code), "-02 СБ");
            Assert.AreEqual("02", exec, "исполнение -02");
            Assert.AreEqual(" СБ", code, "код СБ");
            DesignationParser.StripExecutionSuffix("ПРТИ.468211.010-0001", out exec, out code);
            Assert.AreEqual("0001", exec, "четырёхзначное исполнение");
            Assert.AreEqual("АБВГ.123", DesignationParser.StripExecutionSuffix("АБВГ.123СБ", out exec, out code), "код сразу после цифры");
            Assert.AreEqual(" СБ", code, "код после цифры");
            Assert.AreEqual("ПРТИ.468211.01001", DesignationParser.StripExecutionSuffix("ПРТИ.468211.01001", out exec, out code), "цифры без дефиса");
            Assert.IsNull(exec, "цифры без дефиса — не исполнение");
        }

        public static void Test_ExtractExecutionFromConfigName_recognizes_gost_2_113_forms()
        {
            Check("Default", true, true, "");
            Check("По умолчанию", true, true, "");
            Check("00", true, true, "");
            Check("01", true, false, "01");
            Check("1", true, false, "01");
            Check("-02", true, false, "02");
            Check("исп. 3", true, false, "03");
            Check("Кронштейн исп.01", true, false, "01");
            Check("01 - Оцинкованная", true, false, "01");
            Check("L100-02", true, false, "02");
            Check("Config_05", true, false, "05");
            Check("01<Как обработанный>", true, false, "01");
            Check("Длинная деталь-100", true, false, "100");
            string e;
            bool b;
            Assert.IsFalse(DesignationParser.ExtractExecutionFromConfigName("Длинная деталь 100", out e, out b), "«Длинная деталь 100» — не исполнение");
            Assert.IsFalse(DesignationParser.ExtractExecutionFromConfigName("Как сварной", out e, out b), "без номера");
        }

        private static void Check(string name, bool recognized, bool isBase, string exec)
        {
            string e;
            bool b;
            Assert.AreEqual(recognized, DesignationParser.ExtractExecutionFromConfigName(name, out e, out b), name + ": распознано");
            Assert.AreEqual(isBase, b, name + ": базовое");
            Assert.AreEqual(exec, e, name + ": исполнение");
        }

        public static void Test_Build_designation()
        {
            Assert.AreEqual("ПРТИ.468211.010", DesignationParser.Build("ПРТИ.468211.010", "", ""), "базовое");
            Assert.AreEqual("ПРТИ.468211.010-01", DesignationParser.Build("ПРТИ.468211.010", "01", ""), "исполнение");
            Assert.AreEqual("ПРТИ.468211.010-02 СБ", DesignationParser.Build("ПРТИ.468211.010", "02", " СБ"), "исполнение и код");
            Assert.AreEqual("", DesignationParser.Build("", "01", "СБ"), "без корня");
        }

        public static void Test_Parse_part_assembly_template_and_title_only()
        {
            ParsedName p = DesignationParser.Parse(@"D:\P\ПРТИ.468211.010 Стойка опорная.SLDPRT", " ");
            Assert.AreEqual("ПРТИ.468211.010", p.Root, "корень");
            Assert.AreEqual("Стойка опорная", p.Title, "наименование");
            Assert.AreEqual("", p.DocCode, "код");
            p = DesignationParser.Parse("ПРТИ.468211.110 СБ Узел опоры.sldasm", " ");
            Assert.AreEqual("ПРТИ.468211.110", p.Root, "корень сборки");
            Assert.AreEqual("СБ", p.DocCode, "код сборки");
            Assert.AreEqual("Узел опоры", p.Title, "наименование сборки");
            p = DesignationParser.Parse("ПРТИ.468211.103-01 Планка.sldprt", " ");
            Assert.AreEqual("01", p.Execution, "исполнение в имени файла");
            Assert.AreEqual("ПРТИ.468211.103-01", p.Designation, "обозначение с исполнением");
            p = DesignationParser.Parse("Деталь1", " ");
            Assert.IsTrue(p.IsTemplateName, "имя шаблона");
            p = DesignationParser.Parse("Сборка рамы.sldasm", " ");
            Assert.IsFalse(p.HasDesignation, "без цифр — нет обозначения");
            Assert.AreEqual("Сборка рамы", p.Title, "только наименование");
            p = DesignationParser.Parse("АБВГ.123456.001.SLDPRT", " ");
            Assert.AreEqual("АБВГ.123456.001", p.Root, "только обозначение");
            p = DesignationParser.Parse("ПРТИ.1_Стойка", "_");
            Assert.AreEqual("ПРТИ.1", p.Root, "разделитель словаря");
            Assert.AreEqual("Стойка", p.Title, "наименование после разделителя");
            // случаи прежнего Tier 2
            p = DesignationParser.Parse("ПРТИ.468211.010 СБ Рама сварная", " ");
            Assert.AreEqual("ПРТИ.468211.010", p.Root, "Tier 2: корень при коде СБ");
            Assert.AreEqual("СБ", p.DocCode, "Tier 2: код СБ");
            Assert.AreEqual("Рама сварная", p.Title, "Tier 2: наименование после кода");
            Assert.IsTrue(DesignationParser.Parse("Сборка2", " ").IsTemplateName, "Tier 2: имя шаблона сборки");
        }
    }

    public static class ProvenanceTests
    {
        public static void Test_template_values_are_derived()
        {
            Assert.AreEqual(Provenance.Template, ProvenanceRule.Classify(null, "A", null, false), "нет свойства");
            Assert.AreEqual(Provenance.Template, ProvenanceRule.Classify("", "A", null, false), "пусто");
            Assert.AreEqual(Provenance.Template, ProvenanceRule.Classify("$PRP:\"SW-File Name\"", "A", null, false), "выражение шаблона");
            Assert.AreEqual(Provenance.Template, ProvenanceRule.Classify("Деталь1", "A", null, false), "имя шаблона");
        }

        public static void Test_current_previous_and_manual()
        {
            Assert.AreEqual(Provenance.Current, ProvenanceRule.Classify("ПРТИ.1", "ПРТИ.1", "ПРТИ.0", false), "совпадает");
            Assert.AreEqual(Provenance.DerivedFromPrevious, ProvenanceRule.Classify("ПРТИ.0", "ПРТИ.1", "ПРТИ.0", false), "от прежнего имени");
            Assert.AreEqual(Provenance.Manual, ProvenanceRule.Classify("ПРТИ.199", "ПРТИ.1", "ПРТИ.0", false), "ручное");
            Assert.AreEqual(Provenance.Manual, ProvenanceRule.Classify("ПРТИ.1", "ПРТИ.1", null, true), "RenameSWP = 1");
            Assert.AreEqual(Provenance.Manual, ProvenanceRule.Classify("", "ПРТИ.1", null, true), "RenameSWP = 1 важнее пустого значения");
        }

        public static void Test_should_write_only_derived()
        {
            Assert.IsTrue(ProvenanceRule.ShouldWrite(Provenance.Template), "шаблон");
            Assert.IsTrue(ProvenanceRule.ShouldWrite(Provenance.DerivedFromPrevious), "прежнее имя");
            Assert.IsFalse(ProvenanceRule.ShouldWrite(Provenance.Current), "уже актуально");
            Assert.IsFalse(ProvenanceRule.ShouldWrite(Provenance.Manual), "ручное");
        }
    }

    public static class MarkupTests
    {
        public static void Test_WrapTitle_wraps_long_names_by_words_for_mprop_markup()
        {
            Assert.AreEqual("Стойка опорная", SwPlusFormat.WrapTitle("Стойка опорная"), "короткое");
            Assert.AreEqual("Рама сварная\nкондуктора", SwPlusFormat.WrapTitle("  Рама сварная кондуктора  "), "23 знака — две строки по 22");
            Assert.AreEqual("Втулка направляющая\nопорная длинная", SwPlusFormat.WrapTitle("Втулка направляющая опорная длинная"), "перенос");
        }

        public static void Test_TitleStamp_and_plain_round_trip()
        {
            Assert.AreEqual("<FONT size=4> \n<FONT size=5>Стойка", SwPlusFormat.TitleStamp("Стойка", true), "одна строка");
            Assert.AreEqual("<FONT size=2> \r\n<FONT size=5>Кронштейн направляющий\nудлинённый",
                SwPlusFormat.TitleStamp(SwPlusFormat.WrapTitle("Кронштейн направляющий удлинённый"), true), "A-06 в две строки");
            Assert.AreEqual("Кронштейн направляющий удлинённый", SwPlusFormat.TitlePlain("<FONT size=2> \r\n<FONT size=5>Кронштейн направляющий\nудлинённый"), "разметка снята");
            Assert.AreEqual("Кронштейн направляющий удлинённый", SwPlusFormat.TitlePlain("Кронштейн направляющий\nудлинённый"), "перенос v6.1");
            Assert.AreEqual("Кронштейн", SwPlusFormat.TitlePlain("<FONT size=4> \n<FONT size=3.5>Кронштейн"), "форма ChkFont");
            string word = "Сверхдлинноеоднословноенаименованиедетали";
            Assert.AreEqual(word, SwPlusFormat.WrapTitle(word), "одно длинное слово не режется");
            Assert.IsTrue(BchRecord.IsRecord("Стойка\n<STACK size=1>Труба<OVER>В10</STACK>"), "запись БЧ");
            Assert.IsFalse(BchRecord.IsRecord("Стойка"), "обычное наименование");
        }

        public static void Test_mass_ownership_classification()
        {
            Assert.IsTrue(SwPlusMarkup.IsLiveMass("\"SW-Mass@@00@ПРТИ.468211.101 Пластина.SLDPRT\""), "живое выражение MProp");
            Assert.IsTrue(SwPlusMarkup.IsLiveMass("<FONT size=1> \n<FONT size=3.5>\"SW-Mass@@00@x.SLDPRT\""), "выражение в разметке MProp");
            Assert.IsTrue(SwPlusMarkup.IsGeneratedMass("<FONT size=3.5>0,63"), "текст надстройки");
            Assert.IsTrue(SwPlusMarkup.IsGeneratedMass("0,63"), "число");
            Assert.IsFalse(SwPlusMarkup.IsGeneratedMass("См.таблицу"), "ручной текст");
            Assert.IsFalse(SwPlusMarkup.IsGeneratedMass("-"), "прочерк");
        }

        public static void Test_live_mass_configuration_of_expression()
        {
            const string file = "ПРТИ.468211.103 Планка.sldprt";
            const string flat = "По умолчанию<Как обработанный>SM-FLAT-PATTERN";
            Assert.AreEqual("01", SwPlusMarkup.LiveMassConfiguration("\"SW-Mass@@01@ПРТИ.468211.103 Планка.SLDPRT\"", file), "регистр расширения");
            Assert.AreEqual(flat, SwPlusMarkup.LiveMassConfiguration(SwPlusFormat.MassExpression(flat, "ПРТИ.468211.103 Планка", false), file), "развёртка (B-03)");
            Assert.IsNull(SwPlusMarkup.LiveMassConfiguration("\"SW-Mass@@01@Другой.sldprt\"", file), "другой файл");
            Assert.IsNull(SwPlusMarkup.LiveMassConfiguration("\"SW-Mass\"", file), "выражение шаблона");
        }

        public static void Test_status_line_shows_first_warning_and_count()
        {
            ESKD.MaterialSync.Sw.SyncReport none = new ESKD.MaterialSync.Sw.SyncReport();
            Assert.AreEqual("", none.StatusLine(), "без предупреждений строка не меняется");
            ESKD.MaterialSync.Sw.SyncReport one = new ESKD.MaterialSync.Sw.SyncReport();
            one.Warnings.Add("Конфигурация «00»: «Материал_ФБ» = «Бронза БрАЖ9-4» введён вручную");
            Assert.AreEqual("ЕСКД: Конфигурация «00»: «Материал_ФБ» = «Бронза БрАЖ9-4» введён вручную", one.StatusLine(), "одно предупреждение (Д-38)");
            ESKD.MaterialSync.Sw.SyncReport many = new ESKD.MaterialSync.Sw.SyncReport();
            many.Warnings.Add(new string('а', 300));
            many.Warnings.Add("второе");
            string line = many.StatusLine();
            Assert.IsTrue(line.StartsWith("ЕСКД, предупреждений 2: ааа", StringComparison.Ordinal), "число предупреждений");
            Assert.AreEqual(ESKD.MaterialSync.Sw.SyncReport.StatusLineLimit, line.Length, "длина строки состояния ограничена");
        }

    }

    public static class MaterialRecordTests
    {
        private const string SheetDesignation = "Лист <STACK size=1>Б-ПН-НО-4,0 ГОСТ 19903-2015<OVER>Ст3сп ГОСТ 14637-89</STACK>";

        private static MaterialInfo Info(string name, string designation, string line)
        {
            MaterialInfo m = new MaterialInfo();
            m.Name = name;
            m.GostDesignation = designation;
            m.LineDesignation = line;
            return m;
        }

        /// <summary>Как MProp пишет «Материал_ФБ» в режиме «Сортамент» (MProp.swp, запись prpMaterial при prpFontSize = 1).</summary>
        private static string MPropStamp(string shape, string sortament, string marka)
        {
            return "<FONT size=1.8> <FONT size=3.5>" + shape + " <STACK size=1>" + sortament + "<OVER>" + marka + "</STACK>";
        }

        /// <summary>Как MProp пишет «Материал_Таблица» в режиме «Сортамент».</summary>
        private static string MPropTable(string shape, string sortament, string marka)
        {
            return shape + " <STACK size=1>" + sortament + "<OVER>" + marka + "</STACK>";
        }

        /// <summary>Разбор «Материал_Таблица» формой MProp (FrmMProp.Disp): InStr от единицы, Left$ и Mid$.</summary>
        private static string[] MPropSplit(string s)
        {
            if (s.StartsWith("<FONT")) s = s.Substring(s.LastIndexOf("5>") + 2);
            int lt = s.IndexOf('<') + 1;
            string shape = lt - 2 > 0 ? s.Substring(0, lt - 2) : "";
            int gt = s.IndexOf('>') + 1;
            int lt2 = s.IndexOf('<', gt - 1) + 1;
            string sortament = s.Substring(gt, lt2 - gt - 1);
            int gt2 = s.IndexOf('>', lt2 - 1) + 1;
            int lt3 = s.IndexOf('<', gt2 - 1) + 1;
            string marka = s.Substring(gt2, lt3 - gt2 - 1);
            return new[] { shape, sortament, marka };
        }

        public static void Test_library_fraction_is_written_as_mprop_sortament()
        {
            MaterialRecord r = MaterialRecord.FromLibrary(Info("Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", SheetDesignation,
                "Лист Б-ПН-НО-4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"));
            Assert.AreEqual(MPropStamp("Лист", "Б-ПН-НО-4,0 ГОСТ 19903-2015", "Ст3сп ГОСТ 14637-89"), r.Stamp, "графа 3 — как MProp, форма перед дробью");
            Assert.AreEqual(MPropTable("Лист", "Б-ПН-НО-4,0 ГОСТ 19903-2015", "Ст3сп ГОСТ 14637-89"), r.Table, "таблица — как MProp");
            Assert.AreEqual("Лист Б-ПН-НО-4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", r.Line, "одна строка для сводной ведомости");
            string[] split = MPropSplit(r.Table);
            Assert.AreEqual("Лист", split[0], "MProp: форма");
            Assert.AreEqual("Б-ПН-НО-4,0 ГОСТ 19903-2015", split[1], "MProp: сортамент");
            Assert.AreEqual("Ст3сп ГОСТ 14637-89", split[2], "MProp: марка");
        }

        public static void Test_legacy_table_value_was_misread_by_mprop()
        {
            string legacy = " " + MaterialRecord.LegacyFractionMarkup + "Лист Б-ПН-НО-4,0 ГОСТ 19903-2015<OVER>Ст3сп ГОСТ 14637-89</STACK>";
            string[] split = MPropSplit(legacy);
            Assert.AreEqual("", split[0] + split[1] + split[2], "прежний формат: форма MProp получала пустые поля и после «OK» затирала дробь (Д-34)");
        }

        public static void Test_fraction_without_shape_and_one_line_designations()
        {
            MaterialRecord plywood = MaterialRecord.FromLibrary(Info("Фанера ФК 6 мм", " <STACK size=1>Фанера ФК-6 ГОСТ 3916.1-96<OVER>Береза ГОСТ 3916.1-96</STACK>", ""));
            Assert.AreEqual(MPropStamp("", "Фанера ФК-6 ГОСТ 3916.1-96", "Береза ГОСТ 3916.1-96"), plywood.Stamp, "дробь без формы — как MProp с пустой формой");
            Assert.AreEqual("Фанера ФК-6 ГОСТ 3916.1-96 / Береза ГОСТ 3916.1-96", plywood.Line, "строка из дроби, если в библиотеке она пустая");
            MaterialRecord steel = MaterialRecord.FromLibrary(Info("Сталь 3сп (ГОСТ 380-2005)", "Сталь 3сп ГОСТ 380-2005", "Сталь 3сп ГОСТ 380-2005"));
            Assert.AreEqual("<FONT size=1.8> \n<FONT size=3.5>Сталь 3сп ГОСТ 380-2005", steel.Stamp, "одна строка — как «материал пользователя» MProp");
            Assert.AreEqual("Сталь 3сп ГОСТ 380-2005", steel.Table, "таблица");
            MaterialRecord bare = MaterialRecord.FromLibrary(Info("Сталь без обозначения", "", ""));
            Assert.AreEqual("Сталь без обозначения", bare.Line, "без «Обозначения_ГОСТ» — имя материала");
            MaterialRecord other = MaterialRecord.FromName("Простая углеродистая сталь");
            Assert.AreEqual("<FONT size=1.8> \n<FONT size=3.5>Простая углеродистая сталь", other.Stamp, "материал вне библиотеки");
            Assert.AreEqual("Простая углеродистая сталь", other.Line, "строка");
            Assert.IsNull(MaterialRecord.FromName("  "), "нет материала");
        }

        public static void Test_system_and_user_values()
        {
            Func<string, bool> library = v => v == MPropStamp("Лист", "Б-ПН-НО-4,0 ГОСТ 19903-2015", "Ст3сп ГОСТ 14637-89") ||
                                              v == "<FONT size=1.8> \n<FONT size=3.5>Сталь 3сп ГОСТ 380-2005";
            Assert.IsTrue(MaterialRecord.IsSystemValue(null, library), "нет свойства");
            Assert.IsTrue(MaterialRecord.IsSystemValue("$PRP:\"Материал\"", library), "выражение шаблона");
            Assert.IsTrue(MaterialRecord.IsSystemValue(" " + MaterialRecord.LegacyFractionMarkup + "Лист 4<OVER>Ст3</STACK>", library), "формат прежних версий");
            Assert.IsTrue(MaterialRecord.IsSystemValue(MPropStamp("Лист", "Б-ПН-НО-4,0 ГОСТ 19903-2015", "Ст3сп ГОСТ 14637-89"), library), "запись библиотеки");
            Assert.IsTrue(MaterialRecord.IsSystemValue("<FONT size=1.8> \r\n<FONT size=3.5>Сталь 3сп ГОСТ 380-2005", library), "CR LF с диска равен LF");
            Assert.IsFalse(MaterialRecord.IsSystemValue("Сталь 20кп ГОСТ 535-88", library), "строка без разметки, которой нет в библиотеке, — ручная (Д-37)");
            string manual = MPropStamp("Лист", "Б-ПН-НО-5,0 ГОСТ 19903-2015", "09Г2С-12 ГОСТ 19281-2014");
            Assert.IsFalse(MaterialRecord.IsSystemValue(manual, library), "сортамент, введённый в MProp вручную (Д-32)");
            Assert.IsTrue(MaterialRecord.IsManualText(manual, library), "ручной ввод");
            Assert.IsFalse(MaterialRecord.IsSystemValue("<STACK size=1>Труба 80х80х4 ГОСТ 8639-82<OVER>В 10 ГОСТ 13663-86</STACK>", library), "дробь корпуса Б (B-01)");
            string live = "<FONT size=1.8> \n<FONT size=3.5>\"SW-Material@@00@ПРТИ.468211.101.SLDPRT\"";
            Assert.IsFalse(MaterialRecord.IsSystemValue(live, library), "выражение MProp");
            Assert.IsFalse(MaterialRecord.IsManualText(live, library), "выражение — не ручной текст");
            Assert.IsFalse(MaterialRecord.IsSystemValue("<FONT size=1.8> \n<FONT size=3.5>См. таблицу", library), "см. таблицу");
            Assert.IsFalse(MaterialRecord.IsManualText("<FONT size=1> \n<FONT size=3.5>-", library), "прочерк MProp");
            Assert.AreEqual("Лист Б-ПН-НО-5,0 ГОСТ 19903-2015 / 09Г2С-12 ГОСТ 19281-2014", MaterialRecord.OneLine(manual), "ручная дробь одной строкой");
        }

        public static void Test_corporate_library_records_are_recognized()
        {
            string db = Path.Combine(DictionaryTests.RepoRoot(), "04_Библиотеки_Материалов_и_Профилей", "Библиотека материалов", "Библиотека_Материалов_ГОСТ.sldmat");
            MaterialCatalog.ClearCache();
            MaterialInfo tube = MaterialCatalog.Lookup(db, "Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86");
            MaterialRecord r = MaterialRecord.FromLibrary(tube);
            Assert.AreEqual(MPropStamp("Труба", "80х80х4,0 ГОСТ 8639-82", "В 10 ГОСТ 13663-86"), r.Stamp, "труба: форма перед дробью");
            Assert.AreEqual("Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86", r.Line, "поле «Обозначение_Строка»");
            string[] dbs = { db };
            Assert.IsTrue(MaterialCatalog.IsSystemRecord(dbs, MaterialRecord.Normalize(r.Stamp)), "графа 3 трубы — запись системы");
            Assert.IsTrue(MaterialCatalog.IsSystemRecord(dbs, r.Table), "таблица трубы — запись системы");
            Assert.IsFalse(MaterialCatalog.IsSystemRecord(dbs, MPropStamp("Труба", "80х80х4 ГОСТ 8639-82", "В 10 ГОСТ 13663-86")), "похожая ручная дробь — не запись системы");
            MaterialCatalog.ClearCache();
        }

        private static string CorporateLibrary()
        {
            return Path.Combine(DictionaryTests.RepoRoot(), "04_Библиотеки_Материалов_и_Профилей", "Библиотека материалов", "Библиотека_Материалов_ГОСТ.sldmat");
        }

        public static void Test_plain_text_is_manual_unless_it_is_a_library_name_or_line()
        {
            string[] dbs = { CorporateLibrary() };
            MaterialCatalog.ClearCache();
            Func<string, bool> library = v => MaterialCatalog.IsSystemRecord(dbs, v);
            Assert.IsFalse(MaterialRecord.IsSystemValue("Бронза БрАЖ9-4", library), "набран вручную без разметки (Д-37, M15)");
            Assert.IsTrue(MaterialRecord.IsManualText("Бронза БрАЖ9-4", library), "ручной текст идёт в «Материал_Строка»");
            Assert.IsTrue(MaterialRecord.IsSystemValue("Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", library), "имя материала библиотеки — так писала v5 (M16)");
            Assert.IsTrue(MaterialRecord.IsSystemValue("Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89  ", library), "имя с пробелами по краям");
            Assert.IsTrue(MaterialRecord.IsSystemValue("Лист Б-ПН-НО-4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-2024", library), "однострочная запись библиотеки");
            Assert.IsTrue(MaterialRecord.IsSystemValue(MPropTable("Лист", "Б-ПН-НО-4,0 ГОСТ 19903-2015", "Ст3сп ГОСТ 14637-2024"), library), "дробь без тегов FONT (prpFontSize = 0)");
            Assert.IsFalse(MaterialRecord.IsSystemValue("Лист 4,0", library), "часть имени — ручная");
            Assert.IsFalse(MaterialRecord.IsSystemValue("Бронза БрАЖ9-4", null), "без библиотек текст ручной");
            MaterialCatalog.ClearCache();
        }

        public static void Test_current_material_of_configuration_is_system_even_outside_libraries()
        {
            Func<string, bool> none = v => false;
            MaterialRecord steel = MaterialRecord.FromName("Ст3сп ГОСТ 380-2005");
            Func<string, bool> b03 = MaterialRecord.SystemRecordOf(none, "Ст3сп ГОСТ 380-2005", steel);
            Assert.IsTrue(MaterialRecord.IsSystemValue("Ст3сп ГОСТ 380-2005", b03), "имя материала конфигурации без разметки — так писала v5 (B-03)");
            Assert.IsTrue(MaterialRecord.IsSystemValue("<FONT size=1.8> \r\n<FONT size=3.5>Ст3сп ГОСТ 380-2005", b03), "графа 3 этого материала с диска");
            Assert.IsFalse(MaterialRecord.IsSystemValue("Бронза БрАЖ9-4", b03), "другой текст — ручной");
            Func<string, bool> noMaterial = MaterialRecord.SystemRecordOf(none, null, null);
            Assert.IsFalse(MaterialRecord.IsSystemValue("Ст3сп ГОСТ 380-2005", noMaterial), "у конфигурации без материала текст ручной");
        }

        public static void Test_library_material_replaces_manual_fraction_but_not_reserved_values()
        {
            Func<string, bool> none = v => false;
            MaterialRecord sheet = MaterialRecord.FromLibrary(Info("Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", SheetDesignation, ""));
            MaterialRecord steel = MaterialRecord.FromName("Простая углеродистая сталь");
            Assert.IsTrue(sheet.IsLibrary, "запись библиотеки ЕСКД");
            Assert.IsFalse(steel.IsLibrary, "материал вне библиотеки");
            string mprop = MPropStamp("Лист", "Б-ПН-НО-5,0 ГОСТ 19903-2015", "09Г2С-12 ГОСТ 19281-2014");
            Assert.IsTrue(MaterialRecord.ShouldReplace(mprop, sheet, none), "дробь MProp уступает материалу из библиотеки (решение владельца 13.09.2026)");
            Assert.IsTrue(MaterialRecord.ShouldReplace("Бронза БрАЖ9-4", sheet, none), "набранный текст уступает материалу из библиотеки");
            Assert.IsFalse(MaterialRecord.ShouldReplace("-", sheet, none), "прочерк остаётся при любом материале");
            Assert.IsFalse(MaterialRecord.ShouldReplace("<FONT size=1.8> \n<FONT size=3.5>См. таблицу", sheet, none), "«См. таблицу» остаётся");
            string live = "<FONT size=1.8> \n<FONT size=3.5>\"SW-Material@@00@Пластина.SLDPRT\"";
            Assert.IsTrue(MaterialRecord.ShouldReplace(live, sheet, none), "выражение SW-Material режима «материал SW» уступает библиотеке (Р-6, Н-07)");
            Assert.IsFalse(MaterialRecord.ShouldReplace(live, steel, none), "у материала вне библиотеки выражение остаётся");
            Assert.IsTrue(MaterialRecord.ShouldReplace(null, sheet, none), "пустое поле");
            Assert.IsFalse(MaterialRecord.ShouldReplace(mprop, steel, none), "у материала вне библиотеки дробь MProp остаётся (Д-32)");
            Assert.IsFalse(MaterialRecord.ShouldReplace("Бронза БрАЖ9-4", steel, none), "у материала вне библиотеки текст остаётся (Д-37)");
            Assert.IsTrue(MaterialRecord.ShouldReplace("", steel, none), "пустое поле у материала вне библиотеки");
            Assert.IsFalse(MaterialRecord.ShouldReplace("", null, none), "без материала ничего не пишется (Д-33)");
        }

        public static void Test_small_font_flag_zero_writes_without_font_tags()
        {
            MaterialInfo sheet = Info("Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89", SheetDesignation, "Лист Б-ПН-НО-4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89");
            MaterialRecord fraction = MaterialRecord.FromLibrary(sheet, false);
            Assert.AreEqual(MPropTable("Лист", "Б-ПН-НО-4,0 ГОСТ 19903-2015", "Ст3сп ГОСТ 14637-89"), fraction.Stamp, "дробь без тегов FONT (Д-40)");
            MaterialRecord steel = MaterialRecord.FromLibrary(Info("Сталь 3сп (ГОСТ 380-2005)", "Сталь 3сп ГОСТ 380-2005", ""), false);
            Assert.AreEqual("Сталь 3сп ГОСТ 380-2005", steel.Stamp, "одна строка без тегов FONT");
            Assert.AreEqual("Простая углеродистая сталь", MaterialRecord.FromName("Простая углеродистая сталь", false).Stamp, "материал вне библиотеки");
            Assert.AreEqual("\"SW-Mass@@00@П.SLDPRT\"", SwPlusFormat.MassStamp("00", "П", false, false, false), "масса без тега FONT");
        }

        public static void Test_corporate_library_only_from_solidworks_list()
        {
            string located = CorporateLibrary();
            System.Collections.Generic.List<string> dbs = new System.Collections.Generic.List<string> { located };
            Assert.IsFalse(MaterialCatalog.IsCorporateLibraryMissing(dbs, "Библиотека_Материалов_ГОСТ"), "библиотека подключена в SolidWorks");
            Assert.IsTrue(MaterialCatalog.IsCorporateLibraryMissing(new string[0], "Библиотека_Материалов_ГОСТ"),
                "библиотеки нет в списке SolidWorks — это ошибка настройки, запасной копии нет (решение 14.09.2026)");
            Assert.IsTrue(MaterialCatalog.IsCorporateLibraryMissing(new[] { @"C:\нет\Библиотека_Материалов_ГОСТ.sldmat" }, "Библиотека_Материалов_ГОСТ"),
                "путь в списке есть, а файла нет — тоже не подключена");
            Assert.IsFalse(MaterialCatalog.IsCorporateLibraryMissing(new string[0], "solidworks materials"), "материал другой базы — не предупреждение");
            Assert.IsNull(MaterialCatalog.Find(new string[0], "Библиотека_Материалов_ГОСТ", "Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86"),
                "без подключённой библиотеки материал не находится в запасной копии");
            MaterialCatalog.ClearCache();
            MaterialInfo tube = MaterialCatalog.Find(dbs, "Библиотека_Материалов_ГОСТ", "Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86");
            Assert.NotNull(tube, "материал детали находится в библиотеке поставки");
            Assert.IsTrue(MaterialCatalog.IsSystemRecord(dbs, MaterialRecord.Normalize(MaterialRecord.FromLibrary(tube).Stamp)), "дробь трубы узнаётся без списка SolidWorks");
            string manual = MPropStamp("Труба", "80х80х4 ГОСТ 8639-82", "В 10 ГОСТ 13663-86");
            Assert.IsFalse(MaterialRecord.IsSystemValue(manual, v => MaterialCatalog.IsSystemRecord(dbs, v)),
                "дробь MProp в той же разметке остаётся ручной: разметка и «Материал_Строка» не признак системы (Д-32)");
            MaterialCatalog.ClearCache();
        }
    }

    public static class BchTests
    {
        public static void Test_short_title()
        {
            Assert.AreEqual("Стойка", BchRecord.ShortTitle("Стойка\n<STACK size=1>Труба<OVER>В 10</STACK>"), "первая строка записи");
            Assert.AreEqual("Кронштейн", BchRecord.ShortTitle("Кронштейн БЧ"), "суффикс БЧ v5");
            Assert.AreEqual("Деталь", BchRecord.ShortTitle(""), "пусто");
        }

        public static void Test_size_text_profile_sheet_and_model_length()
        {
            Assert.AreEqual("L = 300 мм", BchRecord.SizeText("Труба 80х80х4,0 ГОСТ 8639-82", new double[] { 80, 80, 300 }, 0), "труба по габариту");
            Assert.AreEqual("L = 250 мм", BchRecord.SizeText("Труба 80х80х4,0", new double[] { 80, 80, 300 }, 250), "длина из размера модели");
            Assert.AreEqual("100\u00D7200 мм", BchRecord.SizeText("Лист 4,0 ГОСТ 19903-2015", new double[] { 4, 100, 200 }, 0), "лист — знак «×» (Р-14)");
        }

        public static void Test_spec_title_record()
        {
            string record = BchRecord.SpecTitle("Стойка", "Труба 80х80х4,0 ГОСТ 8639-82", "В 10 ГОСТ 13663-86", "L = 300 мм");
            Assert.AreEqual("Стойка\n<STACK size=1>Труба 80х80х4,0 ГОСТ 8639-82<OVER>В 10 ГОСТ 13663-86</STACK>\nL = 300 мм", record, "черт. 40");
        }

        public static void Test_bch_record_splits_into_title_and_lines()
        {
            // З-9: «Наименование» — только название, строки записи — «Запись_БЧ»; вместе они дают запись целиком.
            string record = "Стойка\r\n<STACK size=1>Труба<OVER>В 10</STACK>\r\nL = 300 мм";
            Assert.AreEqual("<STACK size=1>Труба<OVER>В 10</STACK>\nL = 300 мм", BchRecord.Lines(record), "строки после названия");
            Assert.AreEqual("", BchRecord.Lines("Стойка"), "одно название");
            Assert.AreEqual(record.Replace("\r\n", "\n"), BchRecord.Join("Стойка", BchRecord.Lines(record)), "название + строки");
            Assert.AreEqual("Стойка", BchRecord.Join("Стойка", ""), "без строк");
        }

        public static void Test_own_bch_record_is_recognized()
        {
            Assert.IsTrue(BchRecord.IsOwnRecord("Стойка\n<STACK size=1>Труба<OVER>В 10</STACK>\nL = 300 мм"), "прокат");
            Assert.IsTrue(BchRecord.IsOwnRecord("Пластина опорная\r\n<STACK size=1>Лист<OVER>Ст3сп</STACK>\r\n100х200 мм"), "лист с прежней буквой «х», CR LF");
            Assert.IsTrue(BchRecord.IsOwnRecord("Планка\n100\u00D7200 мм"), "без материала");
            Assert.IsFalse(BchRecord.IsOwnRecord("Стойка\n<STACK size=1>Труба<OVER>В 10</STACK>\nL=24±0,5мм"), "размер с допуском, набранный вручную");
            Assert.IsFalse(BchRecord.IsOwnRecord("Стойка"), "одна строка");
            Assert.IsFalse(BchRecord.IsOwnRecord(null), "пусто");
        }

        public static void Test_bch_remark_is_mprop_mass_expression()
        {
            Assert.AreEqual("\"SW-Mass@@00@ПРТИ.468211.102 Стойка.SLDPRT\" кг", SwPlusFormat.BchRemark("00", "ПРТИ.468211.102 Стойка", false, false), "масса стойки как у MProp (3033)");
            Assert.IsTrue(BchRecord.IsMassNote("\"SW-Mass@@00@ПРТИ.468211.102 Стойка.SLDPRT\" кг"), "распознаёт свою запись");
            Assert.IsTrue(BchRecord.IsMassNote("\"SW-Mass@@00@ПРТИ.468211.113 Прокладка.SLDPRT\" г"), "граммы");
            Assert.IsTrue(BchRecord.IsMassNote("2,86 кг"), "распознаёт запись до v6.2 для перевода в формат MProp");
            Assert.IsTrue(BchRecord.IsMassNote("3.5 кг"), "распознаёт ошибочную запись v5 для исправления");
            Assert.IsFalse(BchRecord.IsMassNote("Покупное"), "чужой текст");
        }
    }

    public static class MigrationTests
    {
        private static readonly PropertyDictionary Dict = PropertyDictionary.Default();

        /// <summary>Деталь после сохранения надстройкой v5 — по слепку baseline/v5/A01_open_save.json.</summary>
        private static PropertyLevels V5Part()
        {
            PropertyLevels p = new PropertyLevels();
            p.AddConfiguration("00");
            string[] both = { "CheckedBy", "Проверкин П.П.", "DrawnBy", "Тестов Т.Т.", "DrawnDate", "2026-09-12", "Firm", "ООО «Испытание»",
                "Organization", "ООО «Испытание»", "PartNo", "ПРТИ.468211.101", "Автор", "Тестов Т.Т.", "ГОСТ_Материал", "ГОСТ 14637-89",
                "ГОСТ_Сортамент", "ГОСТ 19903-2015", "Компания", "ООО «Испытание»", "Конструктор", "Тестов Т.Т.", "Контора", "ООО «Испытание»",
                "Масса", "<FONT size=3.5>0,63", "Масса_ФБ", "<FONT size=3.5>0,63", "Материал", "Лист 4,0 / Ст3сп",
                "Материал_ФБ", " <FONT size=1.8><FONT size=3.5><STACK size=1>Лист<OVER>Ст3сп</STACK>", "Материал_Таблица", "Лист",
                "Наименование", "Пластина опорная", "Наименование_ФБ", "Пластина опорная", "Обозначение", "ПРТИ.468211.101",
                "Организация", "ООО «Испытание»", "Организация_ФБ", "ООО «Испытание»", "Пров.", "Проверкин П.П.", "Проверил", "Проверкин П.П.",
                "Разраб.", "Тестов Т.Т.", "Разработал", "Тестов Т.Т.", "Сортамент", "Б-ПН-НО-4,0", "п_Пров", "Проверкин П.П.",
                "п_Пров_Дата", "12.09.26", "п_Разраб", "Тестов Т.Т.", "п_Разраб_Дата", "12.09.26" };
            for (int i = 0; i < both.Length; i += 2)
            {
                p.Set("", both[i], both[i + 1]);
                p.Set("00", both[i], both[i + 1]);
            }
            p.Set("", "Формат", "A3");
            p.Set("", "Литера", "01");
            p.Set("00", "Исполнение", "0");
            return p;
        }

        public static void Test_v5_part_plan_follows_mprop_levels_and_is_idempotent()
        {
            PropertyLevels before = V5Part();
            var ops = LegacyMigration.Plan(before, Dict, false, false);
            PropertyLevels after = LegacyMigration.Apply(before, ops);
            foreach (string level in after.Levels)
            {
                foreach (string name in PropertyDictionary.LegacyExtraNames)
                    Assert.IsNull(after.Get(level, name), "лишнее имя " + name + " [" + level + "]");
            }
            Assert.AreEqual("Тестов Т.Т.", after.Get("", "Конструктор"), "конструктор в общих");
            Assert.IsNull(after.Get("00", "Конструктор"), "копия конструктора в конфигурации удалена");
            Assert.AreEqual("Проверкин П.П.", after.Get("00", "Проверил"), "проверил в конфигурации");
            Assert.IsNull(after.Get("", "Проверил"), "общая копия «Проверил» удалена");
            Assert.AreEqual("ООО «Испытание»", after.Get("00", "Контора"), "контора в конфигурации");
            Assert.IsNull(after.Get("", "Контора"), "общая копия «Контора» удалена");
            Assert.IsNull(after.Get("", "Масса_ФБ"), "общая копия массы удалена");
            Assert.IsNull(after.Get("", "Материал_ФБ"), "общая копия материала удалена");
            Assert.AreEqual("<FONT size=3.5>0,63", after.Get("00", "Масса_ФБ"), "масса в конфигурации сохранена");
            Assert.IsNull(after.Get("00", "Наименование"), "копия наименования в конфигурации удалена");
            Assert.AreEqual("Пластина опорная", after.Get("", "Наименование"), "наименование в общих");
            Assert.AreEqual(LegacyMigration.LiveMass, after.Get("", "Масса"), "живая масса");
            Assert.AreEqual(LegacyMigration.LiveMaterial, after.Get("", "Материал"), "живой материал");
            Assert.IsNull(after.Get("00", "Масса"), "статичная копия массы удалена");
            Assert.AreEqual("А3", after.Get("", "Формат"), "формат кириллицей");
            Assert.AreEqual("01", after.Get("", "Литера"), "чужие имена вне класса «лишние» не трогаются");
            Assert.AreEqual("ПРТИ.468211.101", after.Get("00", "Обозначение"), "обозначение в конфигурации сохранено");
            Assert.AreEqual(0, LegacyMigration.Plan(after, Dict, false, false).Count, "повторный план пуст");
        }

        public static void Test_alias_values_fill_only_empty_dictionary_names()
        {
            PropertyLevels p = new PropertyLevels();
            p.AddConfiguration("00");
            p.AddConfiguration("01");
            p.Set("", "Разраб.", "Петров П.П.");
            p.Set("", "Организация", "ООО «Вектор»");
            p.Set("00", "Пров.", "Сидоров С.С.");
            p.Set("01", "Проверил", "Кузнецов К.К.");
            p.Set("01", "п_Пров", "Сидоров С.С.");
            PropertyLevels after = LegacyMigration.Apply(p, LegacyMigration.Plan(p, Dict, false, false));
            Assert.AreEqual("Петров П.П.", after.Get("", "Конструктор"), "конструктор из «Разраб.»");
            Assert.AreEqual("Сидоров С.С.", after.Get("00", "Проверил"), "проверил из алиаса конфигурации");
            Assert.AreEqual("Кузнецов К.К.", after.Get("01", "Проверил"), "ручное значение не перезаписано");
            Assert.AreEqual("ООО «Вектор»", after.Get("00", "Контора"), "контора из общего алиаса");
            Assert.AreEqual("ООО «Вектор»", after.Get("01", "Контора"), "контора во всех конфигурациях");

            PropertyLevels manual = new PropertyLevels();
            manual.AddConfiguration("00");
            manual.Set("", "Конструктор", "Иванов И.И.");
            manual.Set("", "Разраб.", "Петров П.П.");
            PropertyLevels kept = LegacyMigration.Apply(manual, LegacyMigration.Plan(manual, Dict, false, false));
            Assert.AreEqual("Иванов И.И.", kept.Get("", "Конструктор"), "заполненный конструктор не меняется");
            Assert.IsNull(kept.Get("", "Разраб."), "алиас удалён");
        }

        public static void Test_drawing_protected_part_and_assembly_code()
        {
            PropertyLevels drawing = new PropertyLevels();
            drawing.Set("", "Разраб.", "Лунин В.И.");
            drawing.Set("", "Конструктор", "Лунин В.И.");
            drawing.Set("", "SWFormatSize", "297мм*420мм");
            var drawingOps = LegacyMigration.Plan(drawing, Dict, true, false);
            Assert.AreEqual(1, drawingOps.Count, "в чертеже удаляются только лишние имена");
            Assert.AreEqual("Разраб.", drawingOps[0].Name, "удалён алиас");

            PropertyLevels bolt = new PropertyLevels();
            bolt.AddConfiguration("00");
            bolt.Set("", "DrawnBy", "Тестов Т.Т.");
            var boltOps = LegacyMigration.Plan(bolt, Dict, false, false, false);
            Assert.IsTrue(boltOps.TrueForAll(o => o.Action == MigrationAction.Delete), "стандартное изделие: подписи не переносятся");

            PropertyLevels assembly = new PropertyLevels();
            assembly.AddConfiguration("00");
            assembly.Set("00", "Сборка1_ФБ", " СБ");
            Assert.AreEqual(1, LegacyMigration.Plan(assembly, Dict, false, true).Count, "« СБ» → «СБ» (Р-3)");
            PropertyLevels normalized = LegacyMigration.Apply(assembly, LegacyMigration.Plan(assembly, Dict, false, true));
            Assert.AreEqual("СБ", normalized.Get("00", "Сборка1_ФБ"), "код без пробела по D-8");
        }

        public static void Test_cleanup_applies_mprop_formats_like_save()
        {
            MigrationContext part = new MigrationContext { FileTitle = "ПРТИ.468211.101 Пластина опорная", ActiveConfiguration = "00" };
            PropertyLevels p = new PropertyLevels();
            p.AddConfiguration("00");
            p.Set("00", "Масса_ФБ", "<FONT size=3.5>0,63");
            p.Set("00", "Масса_Таблица", "0,63");
            p.Set("", "Формат", "БЧ");
            p.Set("", "Примечание", "0,63 кг");
            p.Set("00", "Формат", "БЧ");
            p.Set("00", "Примечание", "0,63 кг");
            PropertyLevels after = LegacyMigration.Apply(p, LegacyMigration.Plan(p, Dict, false, true, true, part));
            Assert.AreEqual("<FONT size=1> \n<FONT size=3.5>\"SW-Mass@@00@ПРТИ.468211.101 Пластина опорная.SLDPRT\"", after.Get("00", "Масса_ФБ"), "масса текстом → выражение");
            Assert.AreEqual("\"SW-Mass@@00@ПРТИ.468211.101 Пластина опорная.SLDPRT\"", after.Get("00", "Масса_Таблица"), "таблица → выражение");
            Assert.AreEqual("\"SW-Mass@@00@ПРТИ.468211.101 Пластина опорная.SLDPRT\" кг", after.Get("00", "Примечание"), "«0,63 кг» у БЧ → выражение");
            Assert.AreEqual("\"SW-Mass@@00@ПРТИ.468211.101 Пластина опорная.SLDPRT\" кг", after.Get("", "Примечание"), "общее «Примечание» при одной конфигурации");
            Assert.AreEqual(0, LegacyMigration.Plan(after, Dict, false, true, true, part).Count, "повторный план пуст");

            PropertyLevels grams = new PropertyLevels();
            grams.AddConfiguration("00");
            grams.Set("00", "Масса_ФБ", "0,02");
            MigrationContext light = new MigrationContext { FileTitle = "П", Grams = true };
            Assert.AreEqual("<FONT size=1> \n<FONT size=3.5>\"SW-Mass@@00@П.SLDPRT\" г",
                LegacyMigration.Apply(grams, LegacyMigration.Plan(grams, Dict, false, true, true, light)).Get("00", "Масса_ФБ"), "граммы");

            PropertyLevels manual = new PropertyLevels();
            manual.AddConfiguration("00");
            manual.Set("00", "Масса_ФБ", "<FONT size=1> \n<FONT size=3.5>-");
            manual.Set("00", "Примечание", "0,63 кг");
            Assert.AreEqual(0, LegacyMigration.Plan(manual, Dict, false, true, true, part).Count, "прочерк и «Примечание» не БЧ не трогаются");

            MigrationContext asm = new MigrationContext { FileTitle = "ПРТИ.468211.110 СБ Узел опоры", IsAssembly = true };
            PropertyLevels a = new PropertyLevels();
            a.AddConfiguration("00");
            a.Set("00", "Сборка1_ФБ", " СБ");
            a.Set("00", "Сборка2_ФБ", "Сборочный чертёж");
            a.Set("", "Сборка2_ФБ", "Сборочный чертёж");
            PropertyLevels asmAfter = LegacyMigration.Apply(a, LegacyMigration.Plan(a, Dict, false, true, true, asm));
            Assert.AreEqual("СБ", asmAfter.Get("00", "Сборка1_ФБ"), "« СБ» → «СБ»");
            Assert.AreEqual("<FONT size=1> \n<FONT size=2.5>Сборочный чертеж", asmAfter.Get("00", "Сборка2_ФБ"), "формат MProp (FrmMProp:3066)");
            Assert.IsNull(asmAfter.Get("", "Сборка2_ФБ"), "общая копия удалена");
        }
    }

    public static class DictionaryTests
    {
        internal static string RepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "03_Макросы_и_Плагины"))) return dir;
                DirectoryInfo parent = Directory.GetParent(dir);
                dir = parent != null ? parent.FullName : null;
            }
            throw new AssertionException("не найден корень репозитория от " + AppDomain.CurrentDomain.BaseDirectory);
        }

        public static void Test_corporate_dictionary_roles_and_flags()
        {
            string ini = PropertyDictionary.Locate(Path.Combine(RepoRoot(), "03_Макросы_и_Плагины", "ESKD_Material_Sync_Addin"), null);
            Assert.NotNull(ini, "словарь найден рядом с надстройкой");
            PropertyDictionary d = PropertyDictionary.Load(ini);
            Assert.AreEqual("Обозначение", d[Role.Number], "обозначение");
            Assert.AreEqual("Наименование_ФБ", d[Role.DescriptionMulti], "наименование для штампа");
            Assert.AreEqual("Конструктор", d[Role.Designer], "конструктор");
            Assert.AreEqual("Проверил", d[Role.Tester], "проверил");
            Assert.AreEqual("Контора", d[Role.Firm], "организация");
            Assert.AreEqual("Масса_ФБ", d[Role.Mass], "масса");
            Assert.AreEqual("Материал_ФБ", d[Role.Material], "материал");
            Assert.AreEqual("Количество", d[Role.Quantity], "последняя роль");
            Assert.IsTrue(d.FileNameSplit, "prpFileName = 1");
            Assert.AreEqual(" ", d.NameSeparator, "prpNameSep = пробел");
        }

        public static void Test_addin_and_template_names_are_outside_the_dictionary()
        {
            PropertyDictionary d = PropertyDictionary.Default();
            foreach (string name in PropertyDictionary.AddinNames)
                Assert.IsFalse(d.IsDictionaryName(name), name + ": вне словаря SWPlus");
            Assert.AreEqual("Материал_Строка|Формат_до_БЧ|Примечание_до_БЧ|Запись_БЧ", string.Join("|", PropertyDictionary.AddinNames), "имена надстройки (А-5)");
            Assert.AreEqual("Масса|Материал", string.Join("|", PropertyDictionary.TemplateNames), "живые выражения шаблона");
        }

        public static void Test_missing_dictionary_falls_back_to_defaults()
        {
            PropertyDictionary d = PropertyDictionary.Load(@"C:\нет\такого\файла.ini");
            Assert.AreEqual("Обозначение", d[Role.Number], "значение по умолчанию");
            Assert.IsTrue(PropertyDictionary.IsLegacyExtra("Разраб."), "алиас v5 — лишний");
            Assert.IsFalse(PropertyDictionary.IsLegacyExtra("Конструктор"), "словарное имя — не лишнее");
        }

        public static void Test_material_catalog_reads_corporate_library_with_cache()
        {
            string db = Path.Combine(RepoRoot(), "04_Библиотеки_Материалов_и_Профилей", "Библиотека материалов", "Библиотека_Материалов_ГОСТ.sldmat");
            MaterialCatalog.ClearCache();
            MaterialInfo m = MaterialCatalog.Lookup(db, "Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86");
            Assert.NotNull(m, "материал найден");
            Assert.AreEqual("Труба 80х80х4,0 ГОСТ 8639-82", m.Sortament, "сортамент");
            Assert.AreEqual("В 10", m.Grade, "марка");
            Assert.AreEqual("ГОСТ 13663-86", m.GostMaterial, "ГОСТ материала");
            Assert.AreEqual(7850.0, m.Density, "плотность");
            Assert.IsTrue(object.ReferenceEquals(m, MaterialCatalog.Lookup(db, m.Name)), "повторный запрос из кэша");
            Assert.IsNull(MaterialCatalog.Lookup(db, "Материал \"с кавычками\" 'и апострофом'"), "кавычки в имени не ломают поиск");
        }

        public static void Test_material_catalog_rereads_only_changed_library()
        {
            string db = Path.Combine(Path.GetTempPath(), "eskd_unit_" + Guid.NewGuid().ToString("N") + ".sldmat");
            try
            {
                WriteLibrary(db, "Лист Б-ПН-НО-4,0 ГОСТ 19903-2015");
                DateTime stamp = File.GetLastWriteTimeUtc(db);
                MaterialCatalog.ClearCache();
                var first = MaterialCatalog.Load(db);
                Assert.AreEqual("Лист Б-ПН-НО-4,0 ГОСТ 19903-2015", first["Лист 6,0"].Sortament, "первое чтение");
                Assert.IsTrue(object.ReferenceEquals(first, MaterialCatalog.Load(db)), "без изменений файл не перечитывается (Д-11)");

                WriteLibrary(db, "Лист Б-ПН-НО-6,0 ГОСТ 19903-2015");
                File.SetLastWriteTimeUtc(db, stamp.AddSeconds(2));
                var second = MaterialCatalog.Load(db);
                Assert.IsFalse(object.ReferenceEquals(first, second), "изменённый файл перечитан");
                Assert.AreEqual("Лист Б-ПН-НО-6,0 ГОСТ 19903-2015", second["Лист 6,0"].Sortament, "исправление библиотеки подхвачено без перезапуска");
            }
            finally
            {
                MaterialCatalog.ClearCache();
                if (File.Exists(db)) File.Delete(db);
            }
        }

        private static void WriteLibrary(string path, string sortament)
        {
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<mstns:materials xmlns:mstns=\"http://www.solidworks.com/sldmaterials\">\r\n" +
                         "<classification name=\"Сталь\"><material name=\"Лист 6,0\"><physicalproperties><DENS value=\"7850.0\"/></physicalproperties>" +
                         "<custom><prop name=\"Сортамент\" value=\"" + sortament + "\"/></custom></material></classification>\r\n</mstns:materials>\r\n";
            File.WriteAllText(path, xml, System.Text.Encoding.Unicode);
        }
    }
}
