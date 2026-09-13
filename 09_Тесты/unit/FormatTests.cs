using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// WP-1.3: каждая строка формата Core.SwPlusFormat сверяется со строкой выгрузки MProp (FrmMProp.frm.txt): строковое
    /// выражение VBA вычисляется с подстановкой переменных и сравнивается с тем, что пишет надстройка.
    /// </summary>
    public static class FormatTests
    {
        private static string[] _lines;

        private static string[] MProp()
        {
            if (_lines != null) return _lines;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, Path.Combine("03_Макросы_и_Плагины",
                    Path.Combine("Макросы_SW_ZTool", Path.Combine("_VBA_выгрузка", Path.Combine("MProp", "FrmMProp.frm.txt")))));
                if (File.Exists(candidate))
                {
                    _lines = File.ReadAllText(candidate, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');
                    return _lines;
                }
                DirectoryInfo parent = Directory.GetParent(dir.TrimEnd('\\', '/'));
                dir = parent != null ? parent.FullName : null;
            }
            throw new AssertionException("не найдена выгрузка FrmMProp.frm.txt (WP-0.2)");
        }

        /// <summary>Правая часть присваивания строки выгрузки (номер строки с 1), вычисленная как выражение VBA.</summary>
        private static string Vba(int line, Dictionary<string, string> vars, string mustContain)
        {
            string text = MProp()[line - 1];
            Assert.IsTrue(text.Contains(mustContain), string.Format("FrmMProp:{0} содержит «{1}»: {2}", line, mustContain, text.Trim()));
            int eq = text.IndexOf(") = ", StringComparison.Ordinal);
            string expr = eq >= 0 ? text.Substring(eq + 4) : text.Substring(text.IndexOf(" = ", StringComparison.Ordinal) + 3);
            int comment = expr.IndexOf(" ' ", StringComparison.Ordinal);
            if (comment >= 0) expr = expr.Substring(0, comment);
            return Evaluate(expr.Trim(), vars, line);
        }

        private static string Evaluate(string expr, Dictionary<string, string> vars, int line)
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < expr.Length)
            {
                char c = expr[i];
                if (c == ' ' || c == '&') { i++; continue; }
                if (c == '"')
                {
                    int j = i + 1;
                    StringBuilder lit = new StringBuilder();
                    while (j < expr.Length)
                    {
                        if (expr[j] == '"' && j + 1 < expr.Length && expr[j + 1] == '"') { lit.Append('"'); j += 2; continue; }
                        if (expr[j] == '"') break;
                        lit.Append(expr[j]);
                        j++;
                    }
                    sb.Append(lit);
                    i = j + 1;
                    continue;
                }
                int k = i;
                while (k < expr.Length && expr[k] != ' ' && expr[k] != '&') k++;
                string token = expr.Substring(i, k - i);
                if (token.StartsWith("Chr$(", StringComparison.Ordinal) || token.StartsWith("Chr(", StringComparison.Ordinal))
                {
                    string code = token.Substring(token.IndexOf('(') + 1).TrimEnd(')');
                    sb.Append((char)int.Parse(code));
                }
                else
                {
                    string value;
                    if (!vars.TryGetValue(token, out value))
                        throw new AssertionException(string.Format("FrmMProp:{0}: переменная «{1}» не задана в тесте", line, token));
                    sb.Append(value);
                }
                i = k;
            }
            return sb.ToString();
        }

        private static Dictionary<string, string> Vars(params string[] pairs)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[pairs[i]] = pairs[i + 1];
            return d;
        }

        private const string Cfg = "00";
        private const string File1 = "ПРТИ.468211.101 Пластина опорная";

        public static void Test_mass_stamp_matches_mprop_part_and_assembly()
        {
            Dictionary<string, string> v = Vars("sConfigName", Cfg, "sNumberReal", File1);
            Assert.AreEqual(Vba(2850, v, "\" г\""), SwPlusFormat.MassStamp(Cfg, File1, false, true, true), "деталь, граммы, FONT");
            Assert.AreEqual(Vba(2852, v, "\" г\""), SwPlusFormat.MassStamp(Cfg, File1, false, true, false), "деталь, граммы, без FONT");
            Assert.AreEqual(Vba(2856, v, ".SLDPRT"), SwPlusFormat.MassStamp(Cfg, File1, false, false, true), "деталь, килограммы");
            Assert.AreEqual(Vba(2882, v, ".SLDASM"), SwPlusFormat.MassStamp(Cfg, File1, true, true, true), "сборка, граммы");
            Assert.AreEqual(Vba(2888, v, ".SLDASM"), SwPlusFormat.MassStamp(Cfg, File1, true, false, true), "сборка, килограммы");
        }

        public static void Test_mass_table_and_density_match_mprop()
        {
            Dictionary<string, string> v = Vars("sConfigName", Cfg, "sNumberReal", File1);
            Assert.AreEqual(Vba(2862, v, "prpMassTable"), SwPlusFormat.MassExpression(Cfg, File1, false), "Масса_Таблица детали");
            Assert.AreEqual(Vba(2894, v, "prpMassTable"), SwPlusFormat.MassExpression(Cfg, File1, true), "Масса_Таблица сборки");
            Assert.AreEqual(Vba(2830, v, "Плотность_ФБ"), SwPlusFormat.DensityExpression(Cfg, File1), "Плотность_ФБ");
        }

        public static void Test_bch_remark_matches_mprop()
        {
            string part = Vba(3033, Vars("sConfigName", Cfg, "sNumberTitle", File1, "LblMass.Caption", "кг"), "SLDPRT");
            Assert.AreEqual(part, SwPlusFormat.BchRemark(Cfg, File1, false, false), "деталь, кг");
            string grams = Vba(3033, Vars("sConfigName", Cfg, "sNumberTitle", File1, "LblMass.Caption", "г"), "SLDPRT");
            Assert.AreEqual(grams, SwPlusFormat.BchRemark(Cfg, File1, false, true), "деталь, г");
            string asm = Vba(3036, Vars("sConfigName", Cfg, "sNumberTitle", File1, "LblMass.Caption", "кг"), "SLDASM");
            Assert.AreEqual(asm, SwPlusFormat.BchRemark(Cfg, File1, true, false), "сборка, кг");
        }

        public static void Test_title_stamp_matches_mprop_by_line_count()
        {
            string one = "Пластина опорная", two = "Кронштейн направляющий\nудлинённый", three = "Стойка\nсварная\nопорная";
            Assert.AreEqual(Vba(2652, Vars("Наименование.Value", one), "size=5"), SwPlusFormat.TitleStamp(one, true), "одна строка");
            Assert.AreEqual(Vba(2645, Vars("Наименование.Value", two), "size=5"), SwPlusFormat.TitleStamp(two, true), "две строки");
            Assert.AreEqual(Vba(2640, Vars("Наименование.Value", three), "size=3.5"), SwPlusFormat.TitleStamp(three, true), "три строки");
            Assert.AreEqual(one, SwPlusFormat.TitleStamp(one, false), "prpFontSize = 0 (2656)");
        }

        public static void Test_doc_description_and_material_forms_match_mprop()
        {
            string text = SwPlusFormat.AssemblyDrawingText;
            Assert.AreEqual(Vba(3066, Vars("TxtAssem2.Value", text), "size=2.5"), SwPlusFormat.DocDescription(text, true), "Сборка2_ФБ");
            Assert.AreEqual(Vba(3068, Vars("TxtAssem2.Value", text), "TxtAssem2"), SwPlusFormat.DocDescription(text, false), "без FONT");
            string mat = "Паронит ПОН-Б 2 ГОСТ 481-80";
            Assert.AreEqual(Vba(2913, Vars("Материал.Value", mat), "size=1.8"), SwPlusFormat.MaterialLine + mat, "материал одной строкой");
            Assert.AreEqual(Vba(2911, Vars("Материал.Value", mat), "size=5.0"), SwPlusFormat.MaterialMultiLine + mat, "материал в несколько строк");
            string fraction = Vba(2932, Vars("TxtShape.Value", "Лист", "TxtSortament.Value", "Б-ПН-НО 4 ГОСТ 19903-2015",
                "TxtMarka.Value", "Ст3сп ГОСТ 14637-2024"), "STACK");
            Assert.AreEqual(fraction, SwPlusFormat.MaterialFraction + "Лист <STACK size=1>Б-ПН-НО 4 ГОСТ 19903-2015<OVER>Ст3сп ГОСТ 14637-2024</STACK>",
                "дробь сортамента");
            Assert.AreEqual(MaterialRecord.FractionFont, SwPlusFormat.MaterialFraction, "MaterialRecord берёт разметку из SwPlusFormat");
            Assert.AreEqual(MaterialRecord.LineFont, SwPlusFormat.MaterialLine, "MaterialRecord берёт разметку из SwPlusFormat");
        }

        public static void Test_units_follow_mprop_threshold()
        {
            string[] lines = MProp();
            Assert.IsTrue(lines[3355 - 1].Contains("mv > 0.1"), "порог FrmMProp:3355");
            Assert.IsTrue(lines[2456 - 1].Contains("swCM") && lines[2457 - 1].Contains("Grams") && lines[2458 - 1].Contains("Centimeters3"), "граммы 2456–2458");
            Assert.IsTrue(lines[2460 - 1].Contains("swMETER") && lines[2461 - 1].Contains("Kilograms") && lines[2462 - 1].Contains("Meters3"), "килограммы 2460–2462");
            MassUnits heavy = SwPlusFormat.UnitsFor(0.63), light = SwPlusFormat.UnitsFor(0.0188), edge = SwPlusFormat.UnitsFor(0.1);
            Assert.IsFalse(heavy.Grams, "0,63 кг — килограммы");
            Assert.AreEqual(2, heavy.Length, "м");
            Assert.AreEqual(3, heavy.Mass, "кг");
            Assert.AreEqual(6, heavy.Volume, "м³");
            Assert.AreEqual(2, heavy.Decimals, "2 знака");
            Assert.IsTrue(light.Grams, "18,8 г — граммы");
            Assert.AreEqual(1, light.Length, "см");
            Assert.AreEqual(2, light.Mass, "г");
            Assert.AreEqual(5, light.Volume, "см³");
            Assert.AreEqual(1, light.Decimals, "1 знак");
            Assert.IsTrue(edge.Grams, "ровно 0,1 кг — граммы (порог строгий)");
            Assert.IsTrue(SwPlusFormat.UserUnits("False") && SwPlusFormat.UserUnits("0"), "единицы пользователя (2339)");
            Assert.IsFalse(SwPlusFormat.UserUnits("True") || SwPlusFormat.UserUnits(""), "единицы по массе");
        }

        public static void Test_file_title_and_title_wrap()
        {
            Assert.AreEqual("ПРТИ.468211.101 Пластина опорная", SwPlusFormat.FileTitle(@"C:\w\ПРТИ.468211.101 Пластина опорная.sldprt"), "путь");
            Assert.AreEqual("ПРТИ.468211.110 СБ Узел опоры", SwPlusFormat.FileTitle("ПРТИ.468211.110 СБ Узел опоры.SLDASM"), "сборка");
            Assert.AreEqual("Пластина опорная", SwPlusFormat.WrapTitle("Пластина опорная"), "короткое");
            Assert.AreEqual("Кронштейн направляющий\nудлинённый", SwPlusFormat.WrapTitle("Кронштейн направляющий удлинённый"), "две строки по 22");
            string three = SwPlusFormat.WrapTitle("Кронштейн крепления направляющей рамы сварочного кондуктора");
            Assert.AreEqual(3, three.Split('\n').Length, "три строки: " + three);
        }
    }
}
