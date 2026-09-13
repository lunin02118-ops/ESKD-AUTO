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
        public static void Test_TitleForStamp_wraps_long_names_by_words()
        {
            Assert.AreEqual("Стойка опорная", SwPlusMarkup.TitleForStamp("Стойка опорная"), "короткое");
            Assert.AreEqual("Рама сварная кондуктора", SwPlusMarkup.TitleForStamp("  Рама сварная кондуктора  "), "пробелы по краям");
            Assert.AreEqual("Втулка направляющая\nопорная длинная", SwPlusMarkup.TitleForStamp("Втулка направляющая опорная длинная"), "перенос");
            Assert.AreEqual("Кронштейн направляющий\nудлинённый", SwPlusMarkup.TitleForStamp("Кронштейн направляющий удлинённый"), "A-06");
            string word = "Сверхдлинноеоднословноенаименованиедетали";
            Assert.AreEqual(word, SwPlusMarkup.TitleForStamp(word), "одно длинное слово не режется");
        }

        public static void Test_TitleForStamp_keeps_markup_untouched()
        {
            string record = "Стойка\n<STACK size=1>Труба 80х80х4,0 ГОСТ 8639-82<OVER>В 10 ГОСТ 13663-86</STACK>\nL = 300 мм";
            Assert.AreEqual(record, SwPlusMarkup.TitleForStamp(record), "запись БЧ (Д-13)");
            string mprop = "<FONT size=4> \n<FONT size=3.5>Кронштейн";
            Assert.AreEqual(mprop, SwPlusMarkup.TitleForStamp(mprop), "разметка MProp");
        }

        public static void Test_Mass_text_uses_comma_and_fixed_decimals()
        {
            Assert.AreEqual("14,50", SwPlusMarkup.MassText(14.5, 2), "фиксированные нули");
            Assert.AreEqual("0,63", SwPlusMarkup.MassText(0.628, 2), "пластина A-01");
            Assert.AreEqual("2,86", SwPlusMarkup.MassText(2.8637, 2), "стойка A-02");
            Assert.AreEqual("2,8637", SwPlusMarkup.MassText(2.8637, 7), "точность ограничена 4 знаками");
            Assert.AreEqual("3", SwPlusMarkup.MassText(2.8637, 0), "без знаков");
            Assert.AreEqual("<FONT size=3.5>14,53", SwPlusMarkup.MassForStamp(14.53, 2), "графа 5");
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

        public static void Test_MaterialForStamp_from_library_designation()
        {
            string expected = " <FONT size=1.8><FONT size=3.5><STACK size=1>Труба 80х80х4,0 ГОСТ 8639-82<OVER>В 10 ГОСТ 13663-86</STACK>";
            Assert.AreEqual(expected, SwPlusMarkup.MaterialForStamp("Труба <STACK size=1>80х80х4,0 ГОСТ 8639-82<OVER>В 10 ГОСТ 13663-86</STACK>", "", ""), "форма заготовки внутри числителя");
            Assert.AreEqual(expected, SwPlusMarkup.MaterialForStamp(expected.Trim(), "", ""), "готовая дробь с ведущим пробелом");
            Assert.AreEqual(" <FONT size=1.8><FONT size=3.5><STACK size=1>Лист 4<OVER>Ст3</STACK>", SwPlusMarkup.MaterialForStamp("Лист 4 / Ст3", "", ""), "строка с косой чертой");
            Assert.AreEqual("Сталь 45", SwPlusMarkup.MaterialForStamp("Сталь 45", "", ""), "простой материал");
            Assert.AreEqual("", SwPlusMarkup.MaterialForStamp("", "", ""), "пусто");
        }

        public static void Test_MaterialForStamp_sortament_wins_over_copied_numerator()
        {
            bool conflict;
            string copied = "Лист <STACK size=1>Б-ПН-НО-4,0 ГОСТ 19903-2015<OVER>Ст3сп ГОСТ 14637-89</STACK>";
            Assert.AreEqual(" <FONT size=1.8><FONT size=3.5><STACK size=1>Лист Б-ПН-НО-6,0 ГОСТ 19903-2015<OVER>Ст3сп ГОСТ 14637-89</STACK>",
                SwPlusMarkup.MaterialForStamp(copied, "Лист Б-ПН-НО-6,0 ГОСТ 19903-2015", "Ст3сп ГОСТ 14637-89", out conflict),
                "Д-27: числитель скопирован у листа 4,0 — берётся Сортамент");
            Assert.IsTrue(conflict, "расхождение отмечено");
            SwPlusMarkup.MaterialForStamp(copied, "Лист  Б-ПН-НО-4,0  ГОСТ 19903-2015", "Ст3сп ГОСТ 14637-89", out conflict);
            Assert.IsFalse(conflict, "разница только в пробелах — не расхождение");
            SwPlusMarkup.MaterialForStamp(copied, "", "", out conflict);
            Assert.IsFalse(conflict, "без Сортамента сверять не с чем");
            Assert.AreEqual(" <FONT size=1.8><FONT size=3.5><STACK size=1>Плита ЛДСП-16 ГОСТ 32289-2013<OVER>ГОСТ 10632-2014</STACK>",
                SwPlusMarkup.MaterialForStamp("Плита <STACK size=1>ЛДСП-16 ГОСТ 32289-2013<OVER>ГОСТ 10632-2014</STACK>", "Плита ЛДСП-16 ГОСТ 32289-2013", "ЛДСП ГОСТ 10632-2014", out conflict),
                "знаменатель неметаллов берётся из библиотеки как есть");
            Assert.IsFalse(conflict, "ЛДСП согласована");
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
            Assert.AreEqual("100х200 мм", BchRecord.SizeText("Лист 4,0 ГОСТ 19903-2015", new double[] { 4, 100, 200 }, 0), "лист");
        }

        public static void Test_spec_title_record()
        {
            string record = BchRecord.SpecTitle("Стойка", "Труба 80х80х4,0 ГОСТ 8639-82", "В 10 ГОСТ 13663-86", "L = 300 мм");
            Assert.AreEqual("Стойка\n<STACK size=1>Труба 80х80х4,0 ГОСТ 8639-82<OVER>В 10 ГОСТ 13663-86</STACK>\nL = 300 мм", record, "черт. 40");
        }

        public static void Test_mass_note_from_model_mass_not_from_font_tag()
        {
            Assert.AreEqual("2,86 кг", BchRecord.MassNote(2.8637, 2), "масса стойки (Д-14)");
            Assert.IsTrue(BchRecord.IsMassNote("2,86 кг"), "распознаёт свою запись");
            Assert.IsTrue(BchRecord.IsMassNote("3.5 кг"), "распознаёт ошибочную запись v5 для исправления");
            Assert.IsFalse(BchRecord.IsMassNote("Покупное"), "чужой текст");
        }
    }

    public static class DictionaryTests
    {
        private static string RepoRoot()
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
