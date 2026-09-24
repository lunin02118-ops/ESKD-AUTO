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
        /// <summary>
        /// Версия изделия, по которой сделана выгрузка (<see cref="ProductStamp"/>); пусто — изделие не проверено или
        /// изменено после проверки; null — отчёт до 23.09.2026, без строки версии.
        /// </summary>
        public string Version;
        public readonly System.Collections.Generic.List<string> Files = new System.Collections.Generic.List<string>();
        public readonly System.Collections.Generic.List<string> Skipped = new System.Collections.Generic.List<string>();
        /// <summary>Выгружено, но с оговоркой: цеху стоит проверить файл (например, IGS не по оси трубы, Т-29).</summary>
        public readonly System.Collections.Generic.List<string> Warnings = new System.Collections.Generic.List<string>();
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

        public void Warn(string document, string reason)
        {
            Warnings.Add((document ?? "") + " — " + (reason ?? ""));
        }

        public string Text()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Выгрузка для производства");
            sb.AppendLine("Изделие:  " + Product);
            sb.AppendLine("Выгрузил: " + User + ", " + Time.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")));
            if (Version != null)
                sb.AppendLine(ProductStamp.VersionLabel + "   " + (Version.Length > 0 ? Version : ProductStamp.Unchecked));
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
            if (Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Замечания:");
                foreach (string line in Warnings) sb.AppendLine("  " + line);
            }
            return sb.ToString();
        }

        /// <summary>Причина пропуска, при которой выгружать было нечего: у детали нет чертежа (её проверяет правило «г»)
        /// или документ уже выдан и защищён (Т-30). Остальные пропуски — несделанная выгрузка.</summary>
        public static bool IsBenignSkip(string reason)
        {
            string r = (reason ?? "").Trim();
            return r.StartsWith("нет чертежа", StringComparison.OrdinalIgnoreCase) ||
                r.StartsWith("документ выдан в производство", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Начало замечания о файле, который полная выгрузка убрала из папки выдачи (З-48).</summary>
        public const string ArchiveNote = "убран в «" + ExportNaming.ArchiveFolder + "»";

        /// <summary>Замечание «убран в _Аннулировано»: событие прошлой выгрузки, а не забота документа — дальше не переносится.</summary>
        public static bool IsArchiveNote(string reason)
        {
            return (reason ?? "").Trim().StartsWith(ArchiveNote, StringComparison.Ordinal);
        }

        /// <summary>
        /// Начало замечания о многотельной детали с «Лазерная резка трубы» (решение владельца 24.09.2026, З-51): в IGS для
        /// трубореза она не идёт — в файл ушли бы все её тела, сварная рама целиком. Проверка изделия делает из него
        /// замечание правила «е»: ведомость ЛЗК обещает цеху IGS, которого нет.
        /// </summary>
        public const string MultibodyNoIgs = "многотельная деталь в IGS не идёт";

        /// <summary>Начало замечания о многотельной детали — с резкой трубы и без неё (<see cref="MultibodyReason"/>).</summary>
        private const string Multibody = "многотельная деталь";

        /// <summary>
        /// Замечание выгрузки о многотельной детали (З-51): тел всего, из них поверхностей и скрытых — конструктор находит
        /// лишнее тело и в дереве, и среди скрытых. tubeCutting — в «Операциях» стоит «Лазерная резка трубы»: тогда IGS
        /// ждёт цех и проверка изделия не пропустит; без неё — только предупреждение.
        /// </summary>
        public static string MultibodyReason(int bodies, int surfaces, int hidden, bool tubeCutting)
        {
            string count = "тел " + bodies + (surfaces > 0 ? ", из них поверхностей " + surfaces : "") +
                (hidden > 0 ? (surfaces > 0 ? ", скрытых " : ", из них скрытых ") + hidden : "");
            if (tubeCutting)
                return MultibodyNoIgs + " (" + count + "): проверьте чертёж и сборку — труба для трубореза должна быть " +
                    "отдельной деталью из одного тела; если резать на труборезе не нужно, снимите «" +
                    LzkOperations.TubeCutting + "» в ведомости ЛЗК";
            return Multibody + " из трубы (" + count + "), «" + LzkOperations.TubeCutting + "» в «Операциях» нет: IGS не " +
                "делается — проверьте чертёж и сборку";
        }

        /// <summary>Замечание о многотельной детали (<see cref="MultibodyReason"/>) — в окне итога с подсказкой про чертёж.</summary>
        public static bool IsMultibodyNote(string reason)
        {
            return (reason ?? "").Trim().StartsWith(Multibody, StringComparison.Ordinal);
        }

        /// <summary>
        /// Разбор `_Экспорт.txt` обратно: выгруженные файлы с суммами и пропуски «документ — причина».
        /// Проверка изделия (Т-32е) сверяет по ним наличие файлов и суммы, а не ищет имя по всему тексту.
        /// </summary>
        public static ExportLog Parse(string text)
        {
            ExportLog log = new ExportLog();
            string block = "";
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (!line.StartsWith("  ", StringComparison.Ordinal))
                {
                    if (line.StartsWith(ProductStamp.VersionLabel, StringComparison.Ordinal))
                    {
                        string version = line.Substring(ProductStamp.VersionLabel.Length).Trim();
                        log.Version = version == ProductStamp.Unchecked ? "" : version;
                    }
                    block = line.StartsWith("Выгружено", StringComparison.Ordinal) ? "files"
                        : line.StartsWith("Пропущено", StringComparison.Ordinal) ? "skipped"
                        : line.StartsWith("Замечания", StringComparison.Ordinal) ? "warnings" : "";
                    continue;
                }
                string body = line.Trim();
                if (block == "files")
                {
                    int gap = body.IndexOf("  ", StringComparison.Ordinal);
                    if (gap <= 0) continue;
                    string sum = body.Substring(0, gap).Trim();
                    string file = body.Substring(gap).Trim();
                    log.Files.Add(file);
                    if (Regex.IsMatch(sum, "^[0-9a-fA-F]{64}$")) log.Checksums[file] = sum.ToLowerInvariant();
                }
                else if (block == "skipped") log.Skipped.Add(body);
                else if (block == "warnings") log.Warnings.Add(body);
            }
            return log;
        }

        /// <summary>
        /// Отчёт выгрузки больше не по проверенной версии изделия: строка «Версия:» становится «не проверено». changed —
        /// строка была с версией. Прочее в тексте не трогается.
        /// </summary>
        public static string MarkUnchecked(string text, out bool changed)
        {
            changed = false;
            string source = text ?? "";
            string newline = source.Contains("\r\n") ? "\r\n" : "\n";
            string[] lines = source.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith(ProductStamp.VersionLabel, StringComparison.Ordinal)) continue;
                string version = lines[i].Substring(ProductStamp.VersionLabel.Length).Trim();
                if (version.Length == 0 || version == ProductStamp.Unchecked) return source;
                lines[i] = ProductStamp.VersionLabel + "   " + ProductStamp.Unchecked;
                changed = true;
                return string.Join(newline, lines);
            }
            return source;
        }

        /// <summary>
        /// Файлы, перенесённые в «_Аннулировано» после выгрузки, — из блока «Выгружено» и из счёта «Файлов:». Кнопка
        /// «Новая ревизия» уносит прежние файлы уже после того, как выгрузка записала отчёт, и отчёт ссылался на файл,
        /// которого в папке выдачи нет (ревью 23.09.2026, REV-2). Прочее в тексте не трогается.
        /// </summary>
        public static string RemoveFiles(string text, IEnumerable<string> names, out bool changed)
        {
            changed = false;
            string source = text ?? "";
            HashSet<string> drop = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (drop.Count == 0) return source;
            string newline = source.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = new List<string>(source.Replace("\r\n", "\n").Split('\n'));
            bool files = false;
            int header = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.Length > 0 && !line.StartsWith("  ", StringComparison.Ordinal))
                {
                    files = line.StartsWith("Выгружено", StringComparison.Ordinal);
                    if (files) header = i;
                    continue;
                }
                if (!files) continue;
                string body = line.Trim();
                int gap = body.IndexOf("  ", StringComparison.Ordinal);
                if (gap <= 0 || !drop.Contains(body.Substring(gap).Trim())) continue;
                lines.RemoveAt(i--);
                changed = true;
            }
            if (!changed) return source;
            ExportLog left = Parse(string.Join("\n", lines.ToArray()));
            if (left.Files.Count == 0 && header >= 0) lines[header] = "Ничего не выгружено.";
            int count = lines.FindIndex(l => l.StartsWith("Файлов:", StringComparison.Ordinal));
            if (count >= 0) lines[count] = "Файлов:   " + left.Files.Count + ", пропущено: " + left.Skipped.Count;
            return string.Join(newline, lines.ToArray());
        }

        /// <summary>
        /// Имя документа из подписи строки отчёта — без исполнения и расширения: «Труба.sldprt [01]» → «Труба». Имя
        /// конфигурации SolidWorks бывает с «&lt;», «&gt;» и точкой («По умолчанию&lt;Как сварено&gt;», «1.5»): Path.* на «&lt;»
        /// бросал исключение — проверка изделия и выгрузка одной детали падали, а точку принимал за расширение — пропуск
        /// исполнения терялся (ревью 23.09.2026).
        /// </summary>
        public static string DocumentName(string label)
        {
            string s = (label ?? "").Trim();
            foreach (string extension in new[] { ".sldprt", ".sldasm", ".slddrw" })
            {
                int at = s.IndexOf(extension + " [", StringComparison.OrdinalIgnoreCase);
                if (at > 0) return s.Substring(0, at);
            }
            if (s.EndsWith("]", StringComparison.Ordinal))
            {
                int open = s.IndexOf(" [", StringComparison.Ordinal);
                if (open > 0) s = s.Substring(0, open);
            }
            int slash = s.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) s = s.Substring(slash + 1);
            int dot = s.LastIndexOf('.');
            return dot > 0 ? s.Substring(0, dot) : s;
        }

        /// <summary>Документ и причина из строки пропуска «документ — причина».</summary>
        public static KeyValuePair<string, string> SplitSkip(string line)
        {
            string s = line ?? "";
            int dash = s.IndexOf(" — ", StringComparison.Ordinal);
            return dash < 0 ? new KeyValuePair<string, string>(s.Trim(), "")
                : new KeyValuePair<string, string>(s.Substring(0, dash).Trim(), s.Substring(dash + 3).Trim());
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

        /// <summary>Предел длины пути файла (ТЗ-02 Т-7): запас до 260 знаков Windows на «_ИзмN» и копии в архив.</summary>
        public const int MaxPath = 240;

        /// <summary>Путь длиннее <see cref="MaxPath"/>: такой файл не пишется, причина — в отчёт.</summary>
        public static string TooLong(string path)
        {
            int length = (path ?? "").Length;
            return length <= MaxPath ? "" : "путь " + length + " знаков, больше " + MaxPath +
                " (ТЗ-02 Т-7): сократите наименование или имя папки изделия";
        }

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
        /// <summary>
        /// «Обозначение Наименование_S3мм_2шт_120х80.dxf»: обозначение с исполнением, наименование, толщина, количество
        /// на изделие и рамка развёртки (заказ 778, 22.09.2026 — у каждого исполнения своя развёртка и своё количество).
        /// quantity ≤ 0 — количество не известно (выгрузка одной детали), в имени его нет.
        /// </summary>
        public static string DxfPath(string productFolder, string designation, string name, string modelPath,
            double thicknessMm, int quantity, double widthMm, double lengthMm, int revision)
        {
            return DxfPath(productFolder, designation, name, modelPath, 0, thicknessMm, quantity, widthMm, lengthMm, revision);
        }

        /// <summary>
        /// DXF развёртки одного листового тела многотельной детали (№42; решение владельца 24.09.2026 — DXF на каждое тело):
        /// «Обозначение Наименование_тело2_S8мм_1шт_200х100.dxf». body ≤ 0 — деталь из одного листового тела, номера нет.
        /// </summary>
        public static string DxfPath(string productFolder, string designation, string name, string modelPath, int body,
            double thicknessMm, int quantity, double widthMm, double lengthMm, int revision)
        {
            string file = Stem(designation, name, modelPath) +
                (body > 0 ? BodyMark + body.ToString(CultureInfo.InvariantCulture) : "") +
                "_S" + Thickness(thicknessMm) + "мм" +
                (quantity > 0 ? "_" + quantity.ToString(CultureInfo.InvariantCulture) + "шт" : "") +
                "_" + Round(widthMm) + "х" + Round(lengthMm) +
                RevisionSuffix(revision) + ".dxf";
            return Path.Combine(LaserDirectory(productFolder), file);
        }

        /// <summary>Номер листового тела в имени DXF многотельной детали: «_тело2».</summary>
        public const string BodyMark = "_тело";

        /// <summary>Номер тела в имени DXF документа stem; 0 — номера нет (деталь из одного листового тела).</summary>
        public static int BodyOf(string fileName, string stem)
        {
            string name = fileName ?? "";
            if (string.IsNullOrEmpty(stem) || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return 0;
            Match m = Regex.Match(name.Substring(stem.Length), "^" + BodyMark + @"(\d+)_S", RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
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
        /// <remarks>
        /// Округлялось до ближайшего — 200,4 мм давали «200», заготовка в имени меньше детали (сверка 23.09.2026, находка 13).
        /// Сотые доли миллиметра — погрешность замера, а не деталь: 200,004 остаются «200».
        /// </remarks>
        public static string Round(double mm)
        {
            return Math.Max(0, Math.Ceiling(mm - 0.01)).ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Файл выдачи именно этого документа: имя — основа (<see cref="Stem"/>), за ней расширение, суффикс ревизии «_ИзмN»
        /// или толщина развёртки «_S3мм_». Просто «начинается с основы» захватывало чужие файлы: «Деталь1» — и «Деталь10 …»
        /// (аудит 23.09.2026, REV-2).
        /// </summary>
        public static bool BelongsTo(string fileName, string stem)
        {
            string name = fileName ?? "";
            if (string.IsNullOrEmpty(stem) || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = name.Substring(stem.Length);
            return rest.StartsWith(".", StringComparison.Ordinal) ||
                Regex.IsMatch(rest, @"^_Изм\d+\.", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(rest, "^(" + BodyMark + @"\d+)?_S\d+(\.\d+)?мм_", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Прежняя развёртка того же документа и той же ревизии под другим именем: в имени DXF — толщина, количество и рамка,
        /// и после правки детали (или нового округления рамки — вверх, сверка SW API 23.09.2026) выгрузка кладёт файл с
        /// новым именем, а старый оставался рядом: в «Лазер_Лист» две развёртки одной детали. current — только что
        /// выгруженный файл, он не прежний. Файлы другой ревизии («_ИзмN») и других документов — не её.
        /// </summary>
        public static bool IsStaleDxf(string fileName, string stem, int revision, string current)
        {
            string name = fileName ?? "";
            if (!name.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(stem) ||
                string.Equals(name, current ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(name.Substring(stem.Length), "^(" + BodyMark + @"\d+)?_S\d+(\.\d+)?мм_", RegexOptions.IgnoreCase))
                return false;
            // Развёртка другого тела той же детали — не прежняя (№42): выгрузка тела 1 не уносит в архив файл тела 2.
            // Деталь стала однотельной или многотельной — прежние файлы без номера или с номером прежние.
            int body = BodyOf(name, stem), now = BodyOf(current, stem);
            if (body > 0 && now > 0 && body != now) return false;
            Match suffix = Regex.Match(name, @"_Изм(\d+)\.dxf$", RegexOptions.IgnoreCase);
            int found = suffix.Success ? int.Parse(suffix.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            return found == Math.Max(0, revision);
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

        /// <summary>
        /// Файл выдачи по своей папке: PDF в «02_PDF», DXF и DWG в «Лазер_Лист», IGS, IGES и STEP в «Труборез». Прочее
        /// (заметки, служебные файлы Windows, метки «~$» открытых файлов) выгрузка не трогает, и проверка его не считает.
        /// </summary>
        public static bool IsOutputFile(string path)
        {
            string name = Path.GetFileName(path ?? "") ?? "";
            if (name.Length == 0 || name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal))
                return false;
            string folder = Path.GetFileName(Path.GetDirectoryName(path ?? "") ?? "") ?? "";
            string extension = (Path.GetExtension(name) ?? "").ToLowerInvariant();
            if (string.Equals(folder, PdfFolder, StringComparison.OrdinalIgnoreCase)) return extension == ".pdf";
            if (string.Equals(folder, LaserFolder, StringComparison.OrdinalIgnoreCase)) return extension == ".dxf" || extension == ".dwg";
            if (string.Equals(folder, TubeFolder, StringComparison.OrdinalIgnoreCase))
                return extension == ".igs" || extension == ".iges" || extension == ".step" || extension == ".stp";
            return false;
        }

        /// <summary>PDF листа книги ЛЗК «ЛЗК_&lt;шифр&gt;_&lt;лист&gt;.pdf»: его делает и заменяет «Готово к производству», а не выгрузка.</summary>
        public static bool IsLzkSheet(string fileName)
        {
            string name = fileName ?? "";
            return name.StartsWith(LzkNaming.WorkbookPrefix, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Ревизия файла выдачи по суффиксу «_ИзмN» перед расширением; без суффикса — 0.</summary>
        public static int RevisionOf(string fileName)
        {
            Match m = Regex.Match(fileName ?? "", @"_Изм(\d+)\.[^.\\/]+$", RegexOptions.IgnoreCase);
            int number;
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number : 0;
        }

        /// <summary>
        /// Ревизия revision документа уже у цеха: среди выданных есть его файл (по одной из основ имён stems) с этой
        /// ревизией. Выгрузка такой документ не переписывает — ни на ревизии 0, ни на «_Изм1» после повторной выдачи
        /// (замечание владельца 24.09.2026, З-48; раньше защищалась только ревизия 0).
        /// </summary>
        public static bool IssuedAtRevision(IEnumerable<string> issued, IEnumerable<string> stems, int revision)
        {
            List<string> bases = new List<string>(stems ?? new string[0]);
            foreach (string name in issued ?? new string[0])
            {
                if (RevisionOf(name) != Math.Max(0, revision)) continue;
                foreach (string stem in bases)
                    if (BelongsTo(name, stem)) return true;
            }
            return false;
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

    /// <summary>Что полная выгрузка изделия делает с файлом папки выдачи, который в этот раз не выгружен.</summary>
    public enum LeftoverAction
    {
        /// <summary>Не файл выгрузки: остаётся на месте, в отчёт выгрузки не идёт (PDF листов ЛЗК).</summary>
        Keep,
        /// <summary>Остаётся на месте и переходит в новый отчёт выгрузки: он у цеха или выгрузка его документа сорвалась.</summary>
        Carry,
        /// <summary>Уходит в «_Аннулировано».</summary>
        Archive
    }

    /// <summary>
    /// Порядок в папках выдачи после полной выгрузки изделия с главной сборки (замечание владельца 24.09.2026, З-48): в них
    /// остаётся выгруженное сейчас и то, что уже у цеха, а прежние файлы убранных, переименованных и изменённых документов
    /// и файлы не из выгрузки уходят в «_Аннулировано». Раньше они оставались: выгрузка убирала только прежнюю развёртку
    /// под тем же обозначением, а «Готово к производству» вписывает в отчёт выдачи всё, что лежит в папках, — цех получил
    /// бы развёртку детали, которой в изделии уже нет. Решение принимается по имени файла; основы имён — <see cref="ExportNaming.Stem"/>.
    /// </summary>
    public sealed class ExportLeftovers
    {
        /// <summary>
        /// Начало замечания выгрузки о выданном файле документа, которого в изделии больше нет. Проверка изделия делает из
        /// него замечание правила «е»: без новой ревизии сборки «Готово к производству» отдало бы файл цеху снова.
        /// </summary>
        public const string OrphanIssued = "выдан в производство, а его документа в изделии больше нет";

        /// <summary>Имена файлов прежнего отчёта выгрузки.</summary>
        public readonly HashSet<string> Previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Выданное в производство — имена из отчётов «_Выдано_…» (<see cref="ExportNaming.Issued"/>).</summary>
        public readonly HashSet<string> Issued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Основы имён документов, выгруженных сейчас.</summary>
        public readonly List<string> Exported = new List<string>();
        /// <summary>Основы имён выданных документов, которые выгрузка пропустила: их файлы у цеха.</summary>
        public readonly List<string> Protected = new List<string>();
        /// <summary>Основы имён документов, чья выгрузка сорвалась, и имена их моделей без расширения.</summary>
        public readonly List<string> Failed = new List<string>();
        /// <summary>
        /// Главная сборка выгружена сейчас, а не пропущена как выданная: на неё оформлена новая ревизия, и выданные файлы
        /// документов, которых в изделии больше нет, она уже заменила.
        /// </summary>
        public bool TopExported;

        /// <summary>
        /// Решение по файлу, который лежит в папке выдачи, но сейчас не выгружен. reason — строка для отчёта выгрузки:
        /// почему файл убран или почему оставлен, хотя документа в изделии нет; пусто — сказать нечего.
        /// </summary>
        public LeftoverAction Decide(string fileName, out string reason)
        {
            reason = "";
            string name = fileName ?? "";
            if (ExportNaming.IsLzkSheet(name)) return LeftoverAction.Keep;
            // Выгрузка документа сорвалась — его прежний файл ждёт повторной выгрузки на месте; «выгрузка не сделана»
            // проверка скажет сама, и «Готово к производству» до повторной выгрузки не пройдёт.
            if (Belongs(name, Failed) || Belongs(name, Protected)) return LeftoverAction.Carry;
            if (Issued.Contains(name))
            {
                if (Belongs(name, Exported))
                {
                    reason = "выдан прежней ревизией — новая ревизия выгружена";
                    return LeftoverAction.Archive;
                }
                if (TopExported)
                {
                    reason = "выдан, но его документа в изделии больше нет — его убрала новая ревизия сборки";
                    return LeftoverAction.Archive;
                }
                reason = OrphanIssued + " (убран или переименован после выдачи): оформите новую ревизию сборочного чертежа — " +
                    "выгрузка уберёт файл в «" + ExportNaming.ArchiveFolder + "»";
                return LeftoverAction.Carry;
            }
            reason = Previous.Contains(name)
                ? "прежний файл выгрузки, сейчас его нет — документ убран из изделия, переименован или изменился"
                : "не из выгрузки — в папках выдачи остаются только выгруженные файлы";
            return LeftoverAction.Archive;
        }

        private static bool Belongs(string name, IEnumerable<string> stems)
        {
            foreach (string stem in stems)
                if (ExportNaming.BelongsTo(name, stem)) return true;
            return false;
        }
    }
}
