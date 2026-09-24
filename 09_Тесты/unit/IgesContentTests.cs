using System.Collections.Generic;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Что попало в IGS детали (замечание владельца 24.09.2026). SolidWorks пишет в IGS активный документ, а не модель,
    /// у которой вызван SaveAs: выгрузка клала в «<деталь>.igs» всю сборку, а заголовок файла называл деталь. Сборку в
    /// файле выдают подфигуры — сущности 308 (определение) и 408 (экземпляр) раздела D.
    /// </summary>
    public static class IgesContentTests
    {
        private static string Row(string data, char section, int sequence)
        {
            return data.PadRight(72).Substring(0, 72) + section + sequence.ToString().PadLeft(7);
        }

        private static string Parameters(string data, int entity, int sequence)
        {
            return data.PadRight(64).Substring(0, 64) + entity.ToString().PadLeft(8) + "P" + sequence.ToString().PadLeft(7);
        }

        /// <summary>Файл IGES из сущностей: тип и параметры; в заголовке — имя детали, как пишет SolidWorks.</summary>
        private static List<string> File(params KeyValuePair<int, string>[] entities)
        {
            List<string> lines = new List<string>
            {
                Row("SolidWorks IGES file using analytic representation for surfaces", 'S', 1),
                Row("1H,,1H;,29HПРТИ.468211.102 Стойка.sldprt,", 'G', 1),
            };
            List<string> parameters = new List<string>();
            int d = 1, p = 1;
            foreach (KeyValuePair<int, string> e in entities)
            {
                lines.Add(Row(e.Key.ToString().PadLeft(8) + p.ToString().PadLeft(8) + "       0       0       0       0       0       000000000", 'D', d));
                lines.Add(Row(e.Key.ToString().PadLeft(8) + "       0       0       1       0", 'D', d + 1));
                string text = e.Value;
                do
                {
                    parameters.Add(Parameters(text.Length > 64 ? text.Substring(0, 64) : text, d, p++));
                    text = text.Length > 64 ? text.Substring(64) : "";
                }
                while (text.Length > 0);
                d += 2;
            }
            lines.AddRange(parameters);
            lines.Add(Row("S      1G      2D" + (d - 1).ToString().PadLeft(7) + "P" + (p - 1).ToString().PadLeft(7), 'T', 1));
            return lines;
        }

        private static KeyValuePair<int, string> E(int type, string parameters)
        {
            return new KeyValuePair<int, string>(type, parameters);
        }

        public static void Test_Iges_of_one_part_is_accepted()
        {
            List<string> part = File(E(110, "110,0.,0.,0.,300.,0.,0.;"), E(142, "142,1,3,0,1,2;"), E(144, "144,5,1,0,3;"));
            Assert.AreEqual("", IgesContent.Foreign(part), "одна деталь: подфигур сборки нет");
        }

        public static void Test_Iges_with_assembly_subfigures_is_refused()
        {
            // Как «ПРТИ.468211.102 Стойка.igs» прогона r35 X20: внутри изделие — подфигуры деталей и их экземпляры.
            List<string> assembly = File(
                E(144, "144,5,1,0,3;"),
                E(308, "308,0,32HПРТИ.468211.101 Пластина опорная,1,5;"),
                E(408, "408,3,0.,0.,0.,1.;"),
                E(308, "308,0,22HПРТИ.468211.102 Стойка,1,5;"),
                E(408, "408,7,0.,200.,0.,1.;"));
            string reason = IgesContent.Foreign(assembly);
            Assert.IsTrue(reason.StartsWith("в файле сборка"), reason);
            Assert.IsTrue(reason.Contains("ПРТИ.468211.101 Пластина опорная"), "названа чужая деталь: " + reason);
            Assert.IsTrue(reason.Contains("ПРТИ.468211.102 Стойка"), "названа своя деталь: " + reason);
        }

        public static void Test_Iges_subfigure_name_over_two_lines_is_read_whole()
        {
            string name = "ПРТИ.468211.115 Кронштейн крепления направляющей рамы сварочного кондуктора";
            List<string> assembly = File(E(308, "308,0," + name.Length + "H" + name + ",1,5;"), E(408, "408,1,0.,0.,0.,1.;"));
            Assert.IsTrue(IgesContent.Foreign(assembly).Contains(name), IgesContent.Foreign(assembly));
        }

        public static void Test_Iges_instance_without_definition_is_refused()
        {
            // Внешняя ссылка (416) или экземпляр (408) без определения — тоже не одна деталь.
            Assert.IsTrue(IgesContent.Foreign(File(E(144, "144,5,1,0,3;"), E(416, "416,0,9Hpart.igs;"))).StartsWith("в файле сборка"),
                "внешняя ссылка — сборка");
        }

        public static void Test_Iges_numbers_in_text_do_not_count()
        {
            // «308» в заголовке или в параметрах другой сущности — не подфигура: решает тип записи раздела D.
            List<string> part = File(E(110, "110,308.,408.,0.,300.,0.,0.;"));
            part[1] = Row("1H,,1H;,24H308 Кронштейн 408.sldprt,", 'G', 1);
            Assert.AreEqual("", IgesContent.Foreign(part), "числа 308/408 в тексте — не сборка");
        }
    }
}
