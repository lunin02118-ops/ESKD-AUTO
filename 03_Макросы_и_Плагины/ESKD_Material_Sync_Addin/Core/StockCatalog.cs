using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Из чего сделана деталь: прокат по профилю сварной конструкции или лист.</summary>
    public enum StockKind
    {
        Unknown = 0,
        Profile = 1,
        Sheet = 2
    }

    /// <summary>
    /// Что ищем в библиотеке материалов. Для профиля типоразмер и ГОСТ приходят из папки списка вырезов
    /// (свойства профиля доходят до детали сами), для листа — толщина из элемента листового металла.
    /// </summary>
    public sealed class StockRequest
    {
        public StockKind Kind = StockKind.Unknown;

        /// <summary>«30х15х1,5» у профиля, «8,0» у листа.</summary>
        public string Size = "";

        /// <summary>«ГОСТ 8644-68» — сортамент профиля; у листа пусто, сортамент задаёт Kind.</summary>
        public string Gost = "";

        /// <summary>Откуда взято — имя папки списка вырезов или «лист 8 мм»; показывается конструктору.</summary>
        public string Source = "";

        public bool IsUsable
        {
            get { return Kind != StockKind.Unknown && !string.IsNullOrEmpty(StockCatalog.NormalizeSize(Size)); }
        }
    }

    /// <summary>
    /// Ответ библиотеки на запрос. Кандидаты уже схлопнуты: записи, дающие одни и те же свойства, считаются
    /// одной. Поэтому «Лист 8,0» с двумя редакциями ГОСТ 14637 в именах материалов даёт один вариант и
    /// подставляется молча, а «Лист 6,0» со Ст3сп и 09Г2С — два, и конструктор выбирает.
    /// </summary>
    public sealed class StockMatch
    {
        public readonly List<MaterialInfo> Candidates = new List<MaterialInfo>();

        public bool None { get { return Candidates.Count == 0; } }
        public bool Single { get { return Candidates.Count == 1; } }
        public bool Several { get { return Candidates.Count > 1; } }
        public MaterialInfo First { get { return Candidates.Count > 0 ? Candidates[0] : null; } }
    }

    /// <summary>
    /// Подбор материала из библиотеки по типоразмеру проката (Р-8, решение владельца 20.09.2026).
    /// Чистая логика без SolidWorks: то, что здесь решено, проверяется юнит-тестами на настоящей библиотеке.
    /// </summary>
    public static class StockCatalog
    {
        /// <summary>
        /// Типоразмер к виду, в котором его можно сравнивать: «40Х20Х1,50» и «40x20x1.5» — одно и то же.
        /// Кириллическая «х», латинская «x» и знак «×» равнозначны, запятая равна точке, хвостовые нули
        /// у дробной части не значат ничего, пробелы и «мм» отбрасываются.
        /// </summary>
        public static string NormalizeSize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            StringBuilder sb = new StringBuilder(raw.Length);
            foreach (char c in raw.ToLowerInvariant())
            {
                if (c == 'х' || c == 'x' || c == '×') sb.Append('x');
                else if (c == ',') sb.Append('.');
                else if (char.IsWhiteSpace(c)) continue;
                else sb.Append(c);
            }
            string s = sb.ToString().Replace("мм", "").Replace("mm", "");
            string[] parts = s.Split('x');
            for (int i = 0; i < parts.Length; i++) parts[i] = TrimZeros(parts[i]);
            return string.Join("x", parts);
        }

        private static string TrimZeros(string part)
        {
            if (part.IndexOf('.') < 0) return part;
            string t = part.TrimEnd('0');
            if (t.EndsWith(".", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
            return t.Length > 0 ? t : "0";
        }

        /// <summary>Толщина листа в мм — в типоразмер: 8,0 → «8», 1,5 → «1.5». Округление до 0,001 мм гасит дрожь double.</summary>
        public static string SizeFromThickness(double millimetres)
        {
            if (double.IsNaN(millimetres) || millimetres <= 0) return "";
            double mm = Math.Round(millimetres, 3, MidpointRounding.AwayFromZero);
            return NormalizeSize(mm.ToString("0.###", CultureInfo.InvariantCulture));
        }

        /// <summary>«ГОСТ 8644-68», «гост8644-68» и «8644-68» — один и тот же сортамент.</summary>
        public static string NormalizeGost(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.ToLowerInvariant().Replace("гост", "").Replace("gost", "").Replace("ост", "");
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || c == ' ') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Листовой прокат: сортамент начинается с «Лист». Плита, фанера и кромка листовым металлом не бывают.</summary>
        public static bool IsSheetStock(MaterialInfo info)
        {
            if (info == null) return false;
            string s = (info.Sortament ?? "").TrimStart();
            return s.StartsWith("Лист", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ключ записи: материалы с одинаковыми сортаментом, маркой и ГОСТами запишут в свойства одно и то же,
        /// поэтому для конструктора это один и тот же материал, как бы ни назывались файлы библиотеки.
        /// </summary>
        public static string RecordKey(MaterialInfo info)
        {
            if (info == null) return "";
            return string.Join("|", new[]
            {
                (info.Sortament ?? "").Trim(),
                (info.Grade ?? "").Trim(),
                (info.GostSortament ?? "").Trim(),
                (info.GostMaterial ?? "").Trim(),
                (info.GostDesignation ?? "").Trim(),
                (info.LineDesignation ?? "").Trim()
            });
        }

        /// <summary>
        /// Материалы библиотеки, подходящие под запрос. Порядок кандидатов устойчив: как в библиотеке,
        /// а внутри схлопнутой группы остаётся запись с самой свежей редакцией ГОСТа в имени.
        /// </summary>
        public static StockMatch Match(IEnumerable<MaterialInfo> library, StockRequest request)
        {
            StockMatch result = new StockMatch();
            if (library == null || request == null || !request.IsUsable) return result;

            string size = NormalizeSize(request.Size);
            string gost = NormalizeGost(request.Gost);

            List<MaterialInfo> hits = new List<MaterialInfo>();
            foreach (MaterialInfo info in library)
            {
                if (info == null) continue;
                string infoSize = NormalizeSize(info.StandardSize);
                if (infoSize.Length == 0 || infoSize != size) continue;
                if (gost.Length > 0)
                {
                    if (NormalizeGost(info.GostSortament) != gost) continue;
                }
                else if (request.Kind == StockKind.Sheet)
                {
                    if (!IsSheetStock(info)) continue;
                }
                else continue;  // профиль без ГОСТа не ищем: типоразмер один и тот же у разных сортаментов
                hits.Add(info);
            }

            // Порядок кандидатов не зависит от порядка чтения библиотеки: иначе диалог выбора у разных
            // конструкторов показывал бы одни и те же варианты в разном порядке.
            hits.Sort(delegate (MaterialInfo a, MaterialInfo b)
            {
                return string.CompareOrdinal(a.Name ?? "", b.Name ?? "");
            });

            // Схлопывание: из записей с одинаковыми свойствами остаётся одна, самая свежая по редакции ГОСТа в имени.
            Dictionary<string, MaterialInfo> best = new Dictionary<string, MaterialInfo>(StringComparer.Ordinal);
            List<string> order = new List<string>();
            foreach (MaterialInfo info in hits)
            {
                string key = RecordKey(info);
                MaterialInfo kept;
                if (!best.TryGetValue(key, out kept))
                {
                    best.Add(key, info);
                    order.Add(key);
                }
                else if (Better(info, kept))
                {
                    best[key] = info;
                }
            }
            foreach (string key in order) result.Candidates.Add(best[key]);
            return result;
        }

        /// <summary>
        /// Какую из двух одинаковых по свойствам записей показать и назначить. Сначала та, чьё имя не спорит
        /// с её же свойствами: в библиотеке есть «Лист 3,0 … / Ст3сп ГОСТ 14637-89» со свойством
        /// «ГОСТ_Материал = ГОСТ 16523-97» — имя осталось от прежней редакции, и ставить его детали незачем.
        /// При равенстве — запись со свежей редакцией ГОСТа в имени.
        /// </summary>
        public static bool Better(MaterialInfo candidate, MaterialInfo kept)
        {
            bool a = NameAgreesWithGost(candidate), b = NameAgreesWithGost(kept);
            if (a != b) return a;
            return NewestYear(candidate.Name) > NewestYear(kept.Name);
        }

        /// <summary>Имя материала содержит свой же ГОСТ материала — значит имя не устарело.</summary>
        public static bool NameAgreesWithGost(MaterialInfo info)
        {
            if (info == null) return false;
            string gost = (info.GostMaterial ?? "").Trim();
            if (gost.Length == 0) return true;  // сортамент без ГОСТа материала сверять не с чем
            return (info.Name ?? "").IndexOf(gost, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Самый поздний год в имени материала: «ГОСТ 14637-89» — 1989, «ГОСТ 14637-2024» — 2024.</summary>
        public static int NewestYear(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int newest = 0;
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] != '-') continue;
                int j = i + 1;
                while (j < name.Length && char.IsDigit(name[j])) j++;
                int len = j - i - 1;
                if (len != 2 && len != 4) continue;
                int value;
                if (!int.TryParse(name.Substring(i + 1, len), NumberStyles.None, CultureInfo.InvariantCulture, out value)) continue;
                if (len == 2) value += value < 30 ? 2000 : 1900;
                if (value > newest) newest = value;
            }
            return newest;
        }
    }
}
