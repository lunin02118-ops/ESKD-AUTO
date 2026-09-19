using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Отчёт выгрузки `_Экспорт.txt` (ТЗ-02 Т-31): что выгружено с контрольными суммами и что пропущено.
    /// Контрольные суммы нужны проверке изделия (Т-32е), поэтому формат читается обеими кнопками.
    /// </summary>
    public sealed class ExportLog
    {
        public string Product = "";
        public string User = "";
        public DateTime Time = DateTime.Now;
        public readonly System.Collections.Generic.List<string> Files = new System.Collections.Generic.List<string>();
        public readonly System.Collections.Generic.List<string> Skipped = new System.Collections.Generic.List<string>();
        /// <summary>Файл выгрузки → SHA-256.</summary>
        public readonly System.Collections.Generic.Dictionary<string, string> Checksums =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void Add(string path)
        {
            if (!string.IsNullOrEmpty(path)) Files.Add(path);
        }

        public void Skip(string document, string reason)
        {
            Skipped.Add((document ?? "") + " — " + (reason ?? ""));
        }

        public string Text()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Выгрузка для производства");
            sb.AppendLine("Изделие:  " + Product);
            sb.AppendLine("Выгрузил: " + User + ", " + Time.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")));
            sb.AppendLine("Файлов:   " + Files.Count + ", пропущено: " + Skipped.Count);
            sb.AppendLine();
            if (Files.Count == 0) sb.AppendLine("Ничего не выгружено.");
            else
            {
                sb.AppendLine("Выгружено (SHA-256):");
                foreach (string file in Files)
                {
                    string sum;
                    sb.AppendLine("  " + (Checksums.TryGetValue(file, out sum) ? sum : new string('-', 8)) + "  " +
                        Path.GetFileName(file));
                }
            }
            if (Skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Пропущено:");
                foreach (string line in Skipped) sb.AppendLine("  " + line);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Имена и места файлов выдачи для производства (ТЗ-02 Т-27…Т-31). Правила вынесены из работы
    /// с SolidWorks, чтобы их можно было проверить юнит-тестами и прочитать глазами.
    /// </summary>
    public static class ExportNaming
    {
        public const string PdfFolder = "02_PDF";
        public const string CncFolder = "03_ЧПУ";
        public const string LaserFolder = "Лазер_Лист";
        public const string TubeFolder = "Труборез";
        public const string ReportName = "_Экспорт.txt";
        public const string ArchiveFolder = "_Аннулировано";

        /// <summary>Папка PDF изделия: &lt;изделие&gt;\02_PDF.</summary>
        public static string PdfDirectory(string productFolder)
        {
            return Path.Combine(productFolder ?? "", PdfFolder);
        }

        /// <summary>Развёртки для лазера: &lt;изделие&gt;\03_ЧПУ\Лазер_Лист.</summary>
        public static string LaserDirectory(string productFolder)
        {
            return Path.Combine(productFolder ?? "", CncFolder, LaserFolder);
        }

        /// <summary>Профиль для трубореза: &lt;изделие&gt;\03_ЧПУ\Труборез.</summary>
        public static string TubeDirectory(string productFolder)
        {
            return Path.Combine(productFolder ?? "", CncFolder, TubeFolder);
        }

        public static string ReportPath(string productFolder)
        {
            return Path.Combine(productFolder ?? "", ReportName);
        }

        /// <summary>
        /// «&lt;Обозначение&gt; &lt;Наименование&gt;» — основа имени любого файла выдачи; пусто — имя файла модели.
        /// У детали БЧ в «Наименовании» запись для спецификации — в имя идёт только её первая строка.
        /// </summary>
        public static string Stem(string designation, string name, string modelPath)
        {
            string left = (designation ?? "").Trim();
            string right = BchRecord.IsRecord(name) ? BchRecord.ShortTitle(name) : (name ?? "").Trim();
            string stem = (left + " " + right).Trim();
            if (stem.Length == 0) stem = Path.GetFileNameWithoutExtension(modelPath ?? "") ?? "";
            return LzkNaming.SafeFileName(stem);
        }

        /// <summary>Суффикс ревизии: 0 и меньше — пусто, иначе «_ИзмN» (Т-30).</summary>
        public static string RevisionSuffix(int revision)
        {
            return revision > 0 ? "_Изм" + revision.ToString(CultureInfo.InvariantCulture) : "";
        }

        public static string PdfPath(string productFolder, string designation, string name, string modelPath, int revision)
        {
            return Path.Combine(PdfDirectory(productFolder), Stem(designation, name, modelPath) + RevisionSuffix(revision) + ".pdf");
        }

        /// <summary>
        /// DXF развёртки (Т-28): «&lt;стем&gt;_S&lt;толщина&gt;мм_&lt;ширина&gt;х&lt;длина&gt;[_ИзмN].dxf».
        /// Толщина — без лишнего нуля («S3мм», «S2.5мм»), рамка развёртки — целые миллиметры.
        /// </summary>
        public static string DxfPath(string productFolder, string designation, string name, string modelPath,
            double thicknessMm, double widthMm, double lengthMm, int revision)
        {
            string file = Stem(designation, name, modelPath) +
                "_S" + Thickness(thicknessMm) + "мм" +
                "_" + Round(widthMm) + "х" + Round(lengthMm) +
                RevisionSuffix(revision) + ".dxf";
            return Path.Combine(LaserDirectory(productFolder), file);
        }

        public static string IgsPath(string productFolder, string designation, string name, string modelPath, int revision)
        {
            return Path.Combine(TubeDirectory(productFolder), Stem(designation, name, modelPath) + RevisionSuffix(revision) + ".igs");
        }

        /// <summary>Толщина в имени файла: «3», «2.5», «0.8» — дробная через точку, целая без дробной части (Т-28).</summary>
        public static string Thickness(double mm)
        {
            double value = Math.Round(mm, 2);
            if (Math.Abs(value - Math.Round(value)) < 0.001) return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
            return value.ToString("0.0#", CultureInfo.InvariantCulture);
        }

        /// <summary>Сторона рамки развёртки: целые миллиметры, вверх — деталь меньше рамки не бывает.</summary>
        public static string Round(double mm)
        {
            return Math.Max(0, Math.Round(mm, MidpointRounding.AwayFromZero)).ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>Куда убрать прежний файл выдачи при новой ревизии: _Аннулировано рядом с ним (Т-30).</summary>
        public static string ArchivePath(string exportPath, DateTime stamp)
        {
            string dir = Path.Combine(Path.GetDirectoryName(exportPath ?? "") ?? "", ArchiveFolder);
            string stem = Path.GetFileNameWithoutExtension(exportPath ?? "") + "_" +
                stamp.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            string extension = Path.GetExtension(exportPath ?? "");
            string path = Path.Combine(dir, stem + extension);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, stem + "_" + i + extension);
            return path;
        }

        /// <summary>Имя файла выдачи: «_Выдано_&lt;дата&gt;.txt» пишет кнопка «Готово к производству» в папке изделия.</summary>
        public const string IssuedPrefix = "_Выдано_";

        /// <summary>
        /// Что уже выдано в производство (Т-30): имена документов из всех `_Выдано_*.txt` папки изделия.
        /// Выданное не перезаписывается — на него оформляют новую ревизию.
        /// </summary>
        public static HashSet<string> Issued(string productFolder)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(productFolder) || !Directory.Exists(productFolder)) return names;
            // Отчёт выдачи кнопка К-5 пишет в папку заказа (Т-42) — один на все изделия; более ранние
            // отчёты могли лечь и в папку изделия. Читаем оба места, иначе выданное будет выглядеть
            // черновиком, и К-2 молча перезапишет документ, который уже у цеха.
            List<string> reports = new List<string>(Directory.GetFiles(productFolder, IssuedPrefix + "*.txt"));
            string section = Path.GetDirectoryName(productFolder.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
            string order = string.Equals(Path.GetFileName(section), ProductLocator.SectionFolder,
                StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(section) ?? "") : "";
            if (order.Length > 0 && Directory.Exists(order))
                reports.AddRange(Directory.GetFiles(order, IssuedPrefix + "*.txt"));
            foreach (string file in reports)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file, Encoding.UTF8);
                }
                catch (IOException)
                {
                    continue;
                }
                foreach (string line in lines)
                {
                    // Строка отчёта выдачи — «<контрольная сумма>  <имя файла>» или просто имя файла.
                    string name = line.Trim();
                    if (name.Length == 0) continue;
                    int gap = name.LastIndexOf("  ", StringComparison.Ordinal);
                    if (gap >= 0) name = name.Substring(gap + 2).Trim();
                    if (name.IndexOf('.') > 0) names.Add(name);
                }
            }
            return names;
        }

        /// <summary>Ревизия документа из свойства «Revision»: «0», «», «—» — нет ревизии.</summary>
        public static int Revision(string value)
        {
            Match m = Regex.Match((value ?? "").Trim(), @"^(\d+)");
            int number;
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number : 0;
        }
    }
}
