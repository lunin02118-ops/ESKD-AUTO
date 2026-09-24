using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ESKD.MaterialSync.Core
{
    /// <summary>О чём спрашивает окно «Проверить изделие».</summary>
    public enum ReviewQuestionKind
    {
        /// <summary>Материал не сходится с профилем или толщиной, а решить может только конструктор.</summary>
        Material = 1,
        /// <summary>Обозначение в свойствах не совпадает с именем файла.</summary>
        Designation = 2
    }

    /// <summary>
    /// Один вопрос окна «Проверить изделие» (решение владельца 23.09.2026, З-27). Материал спрашивается один раз на
    /// типоразмер во всём изделии: лист 6 мм в десяти деталях — один выбор. Обозначение — по детали.
    /// </summary>
    public sealed class ReviewQuestion
    {
        public ReviewQuestionKind Kind;

        /// <summary>Ключ ответа: у материала — типоразмер, ГОСТ и стоящий материал; у обозначения — путь детали.</summary>
        public string Key = "";

        /// <summary>Что спрашиваем, одной строкой: «Труба 40х20х2 ГОСТ 8645-68, сейчас «Сталь 3»».</summary>
        public string Subject = "";

        /// <summary>Детали, которых касается ответ (имена для окна).</summary>
        public readonly List<string> Places = new List<string>();

        /// <summary>Пути этих деталей.</summary>
        public readonly List<string> Paths = new List<string>();

        /// <summary>Варианты ответа по порядку. «Оставить как есть» — последний, если оставлять есть что.</summary>
        public readonly List<string> Options = new List<string>();

        /// <summary>Материалы библиотеки для вариантов материала: Options[i] ↔ Materials[i].</summary>
        public readonly List<MaterialInfo> Materials = new List<MaterialInfo>();

        /// <summary>Номер варианта «Оставить как есть»; -1 — оставлять нечего (материала нет).</summary>
        public int KeepIndex = -1;

        /// <summary>Выбранный вариант; -1 — без ответа: ничего не меняется, вопрос вернётся при следующей проверке.</summary>
        public int Answer = -1;

        /// <summary>
        /// Отпечаток ответа «Оставить как есть» — пишется в свойство <see cref="ReviewAccepted.PropertyName"/> деталей.
        /// Сменятся профиль, материал или имя файла — отпечаток другой, и вопрос прозвучит снова.
        /// </summary>
        public string Fingerprint = "";

        /// <summary>Обозначение: что стоит сейчас и что даёт имя файла.</summary>
        public string Current = "";
        public string Expected = "";

        public bool Answered { get { return Answer >= 0 && Answer < Options.Count; } }

        public bool Kept { get { return Answered && Answer == KeepIndex; } }

        /// <summary>Выбран материал из библиотеки (не «оставить»).</summary>
        public MaterialInfo ChosenMaterial
        {
            get { return Answered && !Kept && Answer < Materials.Count ? Materials[Answer] : null; }
        }

        /// <summary>Обозначение взять из имени файла.</summary>
        public bool FromFile { get { return Kind == ReviewQuestionKind.Designation && Answered && !Kept; } }

        public string Where
        {
            get
            {
                const int Shown = 4;
                string line = string.Join(", ", Places.Take(Shown).ToArray());
                return Places.Count > Shown ? line + " и ещё " + (Places.Count - Shown) : line;
            }
        }
    }

    /// <summary>Документ изделия, который окно «Проверить изделие» может изменить.</summary>
    public sealed class ReviewFile
    {
        public string Path = "";
        public string Title = "";

        /// <summary>Что обновится без вопросов — строки журнала записи: «[00] Масса: 1,2 → 1,3».</summary>
        public readonly List<string> Changes = new List<string>();

        /// <summary>До проверки в документе были несохранённые правки конструктора: галочка «сохранить» снята.</summary>
        public bool Edited;

        /// <summary>Файл сохранён в прежней версии SolidWorks: «изменён» сразу после открытия (№26) — свой текст в окне.</summary>
        public bool Older;

        /// <summary>Галочка списка «Сохранить».</summary>
        public bool Save;

        public string FileName { get { return System.IO.Path.GetFileName(Path ?? ""); } }
    }

    /// <summary>
    /// Что окно «Проверить изделие» сделает с изделием: обновления без вопросов, вопросы и список сохранения.
    /// Собирается без записи в модели; применяется одной кнопкой.
    /// </summary>
    public sealed class ReviewPlan
    {
        public readonly List<ReviewFile> Files = new List<ReviewFile>();
        public readonly List<ReviewQuestion> Questions = new List<ReviewQuestion>();

        public ReviewFile File(string path)
        {
            return Files.FirstOrDefault(f => string.Equals(f.Path, path ?? "", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Документ в плане; новый — с галочкой «сохранить», если в нём нет несохранённых правок конструктора.</summary>
        public ReviewFile Add(string path, string title, bool edited)
        {
            ReviewFile file = File(path);
            if (file != null) return file;
            file = new ReviewFile { Path = path ?? "", Title = title ?? "", Edited = edited, Save = !edited };
            Files.Add(file);
            return file;
        }

        public IEnumerable<ReviewQuestion> QuestionsFor(string path)
        {
            return Questions.Where(q => q.Paths.Any(p => string.Equals(p, path ?? "", StringComparison.OrdinalIgnoreCase)));
        }

        public ReviewQuestion Find(ReviewQuestionKind kind, string key)
        {
            return Questions.FirstOrDefault(q => q.Kind == kind && string.Equals(q.Key, key ?? "", StringComparison.Ordinal));
        }

        /// <summary>Документ стоит в списке окна: в нём что-то обновится или о нём есть вопрос.</summary>
        public bool Listed(ReviewFile file)
        {
            return file != null && (file.Changes.Count > 0 || QuestionsFor(file.Path).Any());
        }

        /// <summary>Документ изменится при нынешних ответах.</summary>
        public bool WillChange(ReviewFile file)
        {
            return file != null && (file.Changes.Count > 0 || QuestionsFor(file.Path).Any(q => q.Answered));
        }

        public List<ReviewFile> ListedFiles()
        {
            return Files.Where(Listed).ToList();
        }

        /// <summary>Сколько файлов сохранит «Применить и сохранить» при нынешних ответах и галочках.</summary>
        public int ToSave()
        {
            return Files.Count(f => f.Save && WillChange(f));
        }

        public int AutoChanges
        {
            get { return Files.Sum(f => f.Changes.Count); }
        }

        /// <summary>Окну есть что предложить: обновления или вопросы.</summary>
        public bool HasWork
        {
            get { return Files.Any(Listed); }
        }

        /// <summary>Файл (по имени, как его пишет проверка) уже стоит в окне — его находки «не записано» окно и исправит.</summary>
        public bool Covers(string fileName)
        {
            return Files.Any(f => Listed(f) && string.Equals(f.FileName, fileName ?? "", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Находку проверки решает само окно: она помечена исправимой, и её документ стоит в окне. Такие находки в части
        /// «Замечания — это окно их не исправляет» не повторяются.
        /// </summary>
        public bool Handles(CheckFinding finding)
        {
            return finding != null && finding.Fixable && Covers(finding.Document);
        }

        public static string SaveCaption(int count)
        {
            return "Применить и сохранить (" + count + ")";
        }

        /// <summary>
        /// Ответы без окна (автотесты, работа без интерфейса), как раньше у обхода изделия: единственный подходящий материал
        /// заменяет неподходящий, выбор из нескольких и обозначения остаются без ответа — молча решать за конструктора нельзя.
        /// </summary>
        public void AnswerSilently()
        {
            foreach (ReviewQuestion q in Questions)
                q.Answer = q.Kind == ReviewQuestionKind.Material && q.Materials.Count == 1 && q.KeepIndex >= 0 ? 0 : -1;
        }
    }

    /// <summary>
    /// Ответы «Оставить как есть», сохранённые в самой модели — свойство «ЕСКД_Принято» (общее). Без этого вопрос
    /// звучал бы при каждой проверке и в каждом сеансе SolidWorks. MProp чужие свойства не удаляет; «Удалить все
    /// свойства» SProp удалит и это — тогда вопрос просто прозвучит снова.
    ///
    /// Ответ пишется словами («материал 40х20х2|ГОСТ 8645-68|Сталь 3»), чтобы его можно было прочитать в MProp. Не
    /// помещаются в <see cref="MaxLength"/> — старые ответы сжимаются до отпечатка «#1a2b3c4d» (ревью 23.09.2026: у
    /// сварной детали с длинными именами материалов вытеснялись ответы той же записи, и вопросы возвращались). Отпечатков
    /// помещается два десятка; только сверх этого вытесняется самый старый.
    /// </summary>
    public static class ReviewAccepted
    {
        public const string PropertyName = "ЕСКД_Принято";

        /// <summary>Длина значения: чтобы свойство оставалось читаемым в MProp.</summary>
        public const int MaxLength = 255;

        private const string Separator = "; ";
        private const string ShortMark = "#";

        public static List<string> Parse(string raw)
        {
            List<string> list = new List<string>();
            foreach (string part in (raw ?? "").Split(';'))
            {
                string item = part.Trim();
                if (item.Length > 0 && !list.Contains(item)) list.Add(item);
            }
            return list;
        }

        public static bool Contains(string raw, string fingerprint)
        {
            string item = Clean(fingerprint);
            if (item.Length == 0) return false;
            List<string> list = Parse(raw);
            return list.Contains(item) || list.Contains(Short(item));
        }

        /// <summary>Значение свойства с добавленным ответом. Ответ уже есть — прежнее значение без изменений.</summary>
        public static string Add(string raw, string fingerprint)
        {
            return Add(raw, new[] { fingerprint });
        }

        /// <summary>
        /// Значение с ответами одной записи — все сразу: ответ, добавленный этой же записью, не вытесняет другой её ответ.
        /// Нового нет — прежнее значение без изменений.
        /// </summary>
        public static string Add(string raw, IEnumerable<string> fingerprints)
        {
            List<string> fresh = new List<string>();
            foreach (string fingerprint in fingerprints ?? new string[0])
            {
                string item = Clean(fingerprint);
                if (item.Length > 0 && !fresh.Contains(item) && !Contains(raw, item)) fresh.Add(item);
            }
            if (fresh.Count == 0) return raw ?? "";
            List<string> list = Parse(raw);
            list.AddRange(fresh);
            // Сначала старые ответы сжимаются до отпечатков, и только когда сжимать нечего — самый старый вытесняется.
            for (int i = 0; i < list.Count && Length(list) > MaxLength; i++) list[i] = Short(list[i]);
            while (list.Count > 1 && Length(list) > MaxLength) list.RemoveAt(0);
            return string.Join(Separator, list.ToArray());
        }

        private static int Length(List<string> list)
        {
            return string.Join(Separator, list.ToArray()).Length;
        }

        /// <summary>Отпечаток ответа: «#» и 8 знаков SHA-1 текста ответа.</summary>
        private static string Short(string item)
        {
            if (item.StartsWith(ShortMark, StringComparison.Ordinal) && item.Length == ShortMark.Length + 8) return item;
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(item));
                StringBuilder text = new StringBuilder(ShortMark);
                for (int i = 0; i < 4; i++) text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>Материал оставлен: ключ вопроса — типоразмер, ГОСТ и стоящий материал.</summary>
        public static string Material(string decisionKey)
        {
            string key = Clean(decisionKey);
            return key.Length == 0 ? "" : "материал " + key;
        }

        /// <summary>Обозначение оставлено: какое стоит и какое дало бы имя файла.</summary>
        public static string Designation(string current, string expected)
        {
            return "обозначение " + Clean(current) + " > " + Clean(expected);
        }

        private static string Clean(string text)
        {
            return (text ?? "").Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
