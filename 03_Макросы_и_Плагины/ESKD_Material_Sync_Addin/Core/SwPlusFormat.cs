using System;
using System.Collections.Generic;
using System.IO;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Единицы массы документа, которые выставляет MProp (FrmMProp:2449–2468, 3353–3368).</summary>
    public sealed class MassUnits
    {
        public bool Grams;
        /// <summary>swLengthUnit_e: 1 — см, 2 — м.</summary>
        public int Length;
        /// <summary>swUnitsMassPropMass_e: 2 — г, 3 — кг.</summary>
        public int Mass;
        /// <summary>swUnitsMassPropVolume_e: 5 — см³, 6 — м³.</summary>
        public int Volume;
        public int Decimals;
        /// <summary>Подпись единицы массы, как «LblMass.Caption» MProp: «г» или «кг».</summary>
        public string Caption { get { return Grams ? "г" : "кг"; } }
    }

    /// <summary>
    /// Все строки формата, которые надстройка пишет в общие с MProp свойства (Правила записи свойств SWPlus,
    /// 06_Документация/Правила_записи_свойств_SWPlus.md). Каждая строка повторяет строку MProp дословно; ссылки — строки
    /// выгрузки 03_Макросы_и_Плагины/Макросы_SW_ZTool/_VBA_выгрузка/MProp/FrmMProp.frm.txt, юнит-тесты FormatTests сверяют
    /// их с выгрузкой.
    /// </summary>
    public static class SwPlusFormat
    {
        /// <summary>Разметка графы 5 перед массой (FrmMProp:2856).</summary>
        public const string MassPrefix = "<FONT size=1> \n<FONT size=3.5>";
        /// <summary>Суффикс массы в граммах (FrmMProp:2850).</summary>
        public const string GramSuffix = " г";
        /// <summary>Порог MProp: масса больше 0,1 кг — килограммы, иначе граммы (FrmMProp:3355).</summary>
        public const double GramThresholdKg = 0.1;

        /// <summary>Наименование графы 1 в одну строку (FrmMProp:2652).</summary>
        public const string TitleOneLine = "<FONT size=4> \n<FONT size=5>";
        /// <summary>Наименование в две строки, перевод строки CR LF (FrmMProp:2645).</summary>
        public const string TitleTwoLines = "<FONT size=2> \r\n<FONT size=5>";
        /// <summary>Наименование в три строки (FrmMProp:2640).</summary>
        public const string TitleThreeLines = "<FONT size=3.5>";
        /// <summary>Строк графы 1 шрифтом 5 мм: знаков в строке (спайк S-4).</summary>
        public const int TitleLineLimit = 22;
        /// <summary>Строк графы 1 шрифтом 3,5 мм: знаков в строке (спайк S-4).</summary>
        public const int TitleSmallLineLimit = 31;

        /// <summary>Разметка второй строки графы 1 сборки (FrmMProp:3066).</summary>
        public const string DocDescriptionPrefix = "<FONT size=1> \n<FONT size=2.5>";
        /// <summary>Код сборочного чертежа в «Сборка1_ФБ», как в шаблоне сборки и форме MProp.</summary>
        public const string AssemblyCode = "СБ";
        /// <summary>Текст второй строки графы 1 сборки, как в SpecEditor (FrmSpecEditor:705).</summary>
        public const string AssemblyDrawingText = "Сборочный чертеж";

        /// <summary>Графа 3 — дробь сортамента (FrmMProp:2932).</summary>
        public const string MaterialFraction = "<FONT size=1.8> <FONT size=3.5>";
        /// <summary>Графа 3 — материал одной строкой (FrmMProp:2913).</summary>
        public const string MaterialLine = "<FONT size=1.8> \n<FONT size=3.5>";
        /// <summary>Графа 3 — материал в несколько строк (FrmMProp:2911).</summary>
        public const string MaterialMultiLine = "<FONT size=5.0> <FONT size=3.5>";

        /// <summary>Расширение в выражениях SolidWorks, как пишет MProp: заглавными.</summary>
        public static string Extension(bool isAssembly)
        {
            return isAssembly ? ".SLDASM" : ".SLDPRT";
        }

        /// <summary>Имя файла без пути и расширения .SLD*, как «sNumberTitle» MProp (FrmMProp:1488–1495).</summary>
        public static string FileTitle(string pathOrTitle)
        {
            if (string.IsNullOrEmpty(pathOrTitle)) return "";
            string name = Path.GetFileName(pathOrTitle);
            string ext = Path.GetExtension(name);
            if (ext.Length == 7 && ext.StartsWith(".SLD", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 7);
            return name;
        }

        /// <summary>«Масса_Таблица»: "SW-Mass@@конфигурация@файл.SLDPRT" (FrmMProp:2862, 2894).</summary>
        public static string MassExpression(string configuration, string fileTitle, bool isAssembly)
        {
            return "\"SW-Mass@@" + configuration + "@" + fileTitle + Extension(isAssembly) + "\"";
        }

        /// <summary>«Масса_ФБ» (FrmMProp:2850, 2852, 2856, 2882, 2884, 2888).</summary>
        public static string MassStamp(string configuration, string fileTitle, bool isAssembly, bool grams, bool smallFont)
        {
            return (smallFont ? MassPrefix : "") + MassExpression(configuration, fileTitle, isAssembly) + (grams ? GramSuffix : "");
        }

        /// <summary>«Плотность_ФБ» детали (FrmMProp:2830) — пишет MProp; надстройка использует для распознавания.</summary>
        public static string DensityExpression(string configuration, string fileTitle)
        {
            return "\"SW-Density@@" + configuration + "@" + fileTitle + ".SLDPRT\"";
        }

        /// <summary>«Примечание» безчертёжной детали: выражение массы, пробел, «кг» или «г» (FrmMProp:3033, 3036).</summary>
        public static string BchRemark(string configuration, string fileTitle, bool isAssembly, bool grams)
        {
            return MassExpression(configuration, fileTitle, isAssembly) + " " + (grams ? "г" : "кг");
        }

        /// <summary>Единицы массы документа по массе активной конфигурации (FrmMProp:3353–3368, 2455–2468).</summary>
        public static MassUnits UnitsFor(double activeMassKg)
        {
            bool grams = !(activeMassKg > GramThresholdKg);
            return grams
                ? new MassUnits { Grams = true, Length = 1, Mass = 2, Volume = 5, Decimals = 1 }
                : new MassUnits { Grams = false, Length = 2, Mass = 3, Volume = 6, Decimals = 2 };
        }

        /// <summary>«Единицы» конфигурации = «False» или «0»: единицы пользователя, MProp их не трогает (FrmMProp:2339).</summary>
        public static bool UserUnits(string unitsFlag)
        {
            string t = (unitsFlag ?? "").Trim();
            return t == "False" || t == "0";
        }

        /// <summary>«Наименование_ФБ» по числу строк текста (FrmMProp:2636–2657); строки разделены LF.</summary>
        public static string TitleStamp(string text, bool smallFont)
        {
            string t = (text ?? "").Replace("\r\n", "\n");
            if (!smallFont) return t;
            int first = t.IndexOf('\n');
            if (first < 0) return TitleOneLine + t;
            if (t.IndexOf('\n', first + 1) >= 0) return TitleThreeLines + t;
            return TitleTwoLines + t;
        }

        /// <summary>
        /// Наименование графы 1 строками для разметки MProp: до 22 знаков — одна строка; до двух строк по 22 знака — перенос по
        /// словам на две; длиннее — три строки по 31 знаку (шрифт 3,5 мм). Слово длиннее строки не режется.
        /// </summary>
        public static string WrapTitle(string title)
        {
            string t = (title ?? "").Replace("\r\n", " ").Replace("\n", " ").Trim();
            if (t.Length <= TitleLineLimit) return t;
            List<string> two = Wrap(t, TitleLineLimit);
            if (two.Count <= 2) return string.Join("\n", two.ToArray());
            List<string> three = Wrap(t, TitleSmallLineLimit);
            return string.Join("\n", three.ToArray());
        }

        private static List<string> Wrap(string text, int limit)
        {
            List<string> lines = new List<string>();
            string line = "";
            foreach (string word in text.Split(new[] { ' ', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (candidate.Length <= limit || line.Length == 0)
                {
                    line = candidate;
                    continue;
                }
                lines.Add(line);
                line = word;
            }
            if (line.Length > 0) lines.Add(line);
            return lines;
        }

        /// <summary>Текст наименования без разметки MProp графы 1 (любое число строк) и переводов строк — для сравнения.</summary>
        public static string TitlePlain(string stamp)
        {
            string t = (stamp ?? "").Replace("\r\n", "\n");
            foreach (string prefix in new[] { TitleTwoLines.Replace("\r\n", "\n"), TitleOneLine, TitleThreeLines,
                                              "<FONT size=2> \n<FONT size=3.5>", "<FONT size=4> \n<FONT size=3.5>" })
            {
                if (t.StartsWith(prefix, StringComparison.Ordinal))
                {
                    t = t.Substring(prefix.Length);
                    break;
                }
            }
            return string.Join(" ", t.Split(new[] { '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>«Сборка2_ФБ» (FrmMProp:3063–3069): многострочный текст — как есть, иначе разметка перед текстом.</summary>
        public static string DocDescription(string text, bool smallFont)
        {
            string t = text ?? "";
            if (t.IndexOf('\n') >= 0 || !smallFont) return t;
            return DocDescriptionPrefix + t;
        }
    }
}
