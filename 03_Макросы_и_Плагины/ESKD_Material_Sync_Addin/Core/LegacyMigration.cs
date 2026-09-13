using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Свойства документа по уровням: "" — общие, остальные ключи — конфигурации. Значения сырые.</summary>
    public sealed class PropertyLevels
    {
        private readonly Dictionary<string, Dictionary<string, string>> _levels =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private readonly List<string> _configurations = new List<string>();

        public PropertyLevels()
        {
            _levels[""] = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public void AddConfiguration(string name)
        {
            if (string.IsNullOrEmpty(name) || _levels.ContainsKey(name)) return;
            _levels[name] = new Dictionary<string, string>(StringComparer.Ordinal);
            _configurations.Add(name);
        }

        public IList<string> Configurations { get { return _configurations.AsReadOnly(); } }

        public IEnumerable<string> Levels
        {
            get
            {
                yield return "";
                foreach (string c in _configurations) yield return c;
            }
        }

        public string Get(string level, string name)
        {
            Dictionary<string, string> props;
            string value;
            return _levels.TryGetValue(level ?? "", out props) && props.TryGetValue(name, out value) ? value : null;
        }

        public void Set(string level, string name, string value)
        {
            if (!_levels.ContainsKey(level ?? "")) AddConfiguration(level);
            _levels[level ?? ""][name] = value ?? "";
        }

        public bool Remove(string level, string name)
        {
            Dictionary<string, string> props;
            return _levels.TryGetValue(level ?? "", out props) && props.Remove(name);
        }

        public IList<string> Names(string level)
        {
            Dictionary<string, string> props;
            return _levels.TryGetValue(level ?? "", out props) ? new List<string>(props.Keys) : new List<string>();
        }

        public PropertyLevels Clone()
        {
            PropertyLevels copy = new PropertyLevels();
            foreach (string c in _configurations) copy.AddConfiguration(c);
            foreach (string level in Levels)
            {
                foreach (KeyValuePair<string, string> p in _levels[level]) copy.Set(level, p.Key, p.Value);
            }
            return copy;
        }
    }

    public enum MigrationAction { Set, Delete }

    public sealed class MigrationOperation
    {
        public string Level;
        public string Name;
        public MigrationAction Action;
        public string OldValue;
        public string NewValue;
        public string Reason;

        public override string ToString()
        {
            string level = string.IsNullOrEmpty(Level) ? "общие" : Level;
            return Action == MigrationAction.Delete
                ? string.Format("[{0}] {1}: удалено ({2})", level, Name, Reason)
                : string.Format("[{0}] {1}: {2} → {3} ({4})", level, Name, OldValue ?? "<нет>", NewValue, Reason);
        }
    }

    /// <summary>
    /// Документ для правил формата MProp в очистке (WP-2.10): имя файла для выражений массы, вид документа, единица массы.
    /// Без него очистка выполняет только перенос алиасов и уровней.
    /// </summary>
    public sealed class MigrationContext
    {
        public string FileTitle = "";
        public bool IsAssembly;
        public bool Grams;
        public bool SmallFont = true;
        /// <summary>Активная конфигурация: её имя стоит в выражении массы общего «Примечания» у детали с одной конфигурацией.</summary>
        public string ActiveConfiguration = "";
    }

    /// <summary>
    /// План очистки документа, сохранённого надстройкой v5 (план, WP-3.3): значения алиасов переносятся в пустые
    /// словарные имена на уровнях MProp, 21 лишнее имя удаляется, копии свойств приводятся к уровням MProp,
    /// статичные «Масса» и «Материал» возвращаются к живым выражениям, «Формат» — кириллицей. С контекстом документа —
    /// те же правила формата MProp, что при сохранении (WP-2.10): масса текстом → выражение MProp, «2,86 кг» у БЧ →
    /// выражение, «Сборочный чертёж» → формат MProp. Повторный план для результата пуст.
    /// </summary>
    public static class LegacyMigration
    {
        public static readonly string[] AuthorAliases = { "Разраб.", "Разработал", "Автор", "п_Разраб", "DrawnBy" };
        public static readonly string[] CheckerAliases = { "Пров.", "п_Пров", "CheckedBy" };
        public static readonly string[] FirmAliases = { "Организация", "Организация_ФБ", "Компания", "Firm", "Organization" };
        public const string LiveMass = "\"SW-Mass\"";
        public const string LiveMaterial = "\"SW-Material\"";

        private static readonly Regex LatinFormat = new Regex(@"^(\s*)A(\d.*)$");

        /// <param name="transferSignatures">false для стандартных и покупных изделий: лишние имена удаляются, подписи не переносятся.</param>
        public static List<MigrationOperation> Plan(PropertyLevels source, PropertyDictionary dict, bool isDrawing, bool normalizeAssemblyCode,
            bool transferSignatures = true, MigrationContext context = null)
        {
            PropertyLevels state = source.Clone();
            List<MigrationOperation> ops = new List<MigrationOperation>();
            IList<string> configs = isDrawing ? new List<string>() : state.Configurations;
            string designer = dict[Role.Designer], tester = dict[Role.Tester], firm = dict[Role.Firm];

            if (!isDrawing && transferSignatures)
            {
                // 1. Подписи: значения алиасов — в пустые словарные имена на уровнях MProp
                if (IsEmpty(state.Get("", designer)))
                {
                    string value = FirstValue(state, "", AuthorAliases);
                    foreach (string cfg in configs)
                    {
                        if (value != null) break;
                        value = FirstValue(state, cfg, AuthorAliases) ?? NonEmpty(state.Get(cfg, designer));
                    }
                    if (value != null) Set(state, ops, "", designer, value, "из алиаса v5; «Конструктор» — общие свойства");
                }
                foreach (string cfg in configs)
                {
                    TransferToConfiguration(state, ops, cfg, tester, CheckerAliases);
                    TransferToConfiguration(state, ops, cfg, firm, FirmAliases);
                }
                // общие копии конфигурационных подписей MProp удаляет — когда каждая конфигурация заполнена
                foreach (string name in new[] { tester, firm })
                {
                    if (state.Get("", name) != null && configs.Count > 0 && AllConfigurationsFilled(state, configs, name))
                        Delete(state, ops, "", name, "уровень MProp — конфигурация");
                }
                // конфигурационные копии «Конструктора» затеняют правку в MProp
                foreach (string cfg in configs)
                {
                    string copy = state.Get(cfg, designer);
                    if (copy != null && !IsEmpty(state.Get("", designer)) && (copy == state.Get("", designer) || IsEmpty(copy)))
                        Delete(state, ops, cfg, designer, "уровень MProp — общие свойства");
                }
            }

            // 2. Лишние имена v5
            foreach (string level in new List<string>(state.Levels))
            {
                foreach (string name in PropertyDictionary.LegacyExtraNames)
                {
                    if (state.Get(level, name) != null) Delete(state, ops, level, name, "лишнее имя v5");
                }
            }

            if (!isDrawing)
            {
                // 3. Копии наименования в конфигурациях, совпадающие с общими
                foreach (string cfg in configs)
                {
                    foreach (string name in new[] { dict[Role.Description], dict[Role.DescriptionMulti] })
                    {
                        string copy = state.Get(cfg, name);
                        if (copy != null && copy == state.Get("", name))
                            Delete(state, ops, cfg, name, "копия общего свойства");
                    }
                }
                // 4. Общие копии массы и материала, когда в конфигурациях свои значения
                foreach (string name in new[] { dict[Role.Mass], dict[Role.MassTable], dict[Role.Material], dict[Role.MaterialTable] })
                {
                    if (state.Get("", name) != null && configs.Count > 0 && AllConfigurationsFilled(state, configs, name))
                        Delete(state, ops, "", name, "уровень MProp — конфигурация");
                }
                // 5. Статичные «Масса» и «Материал» v5 — живые выражения шаблона
                RestoreLive(state, ops, "Масса", "SW-Mass", LiveMass);
                RestoreLive(state, ops, "Материал", "SW-Material", LiveMaterial);
            }

            // 6. «Формат» кириллицей и код «СБ» по D-8
            foreach (string level in new List<string>(state.Levels))
            {
                string format = state.Get(level, dict[Role.Format]);
                if (format != null)
                {
                    Match m = LatinFormat.Match(format);
                    if (m.Success) Set(state, ops, level, dict[Role.Format], m.Groups[1].Value + "А" + m.Groups[2].Value, "«Формат» кириллицей");
                }
                if (normalizeAssemblyCode && state.Get(level, dict[Role.DocCode]) == " СБ")
                    Set(state, ops, level, dict[Role.DocCode], "СБ", "код документа без пробела (D-8)");
            }

            // 7. Формат MProp, как при сохранении (Правила записи свойств SWPlus, разделы 1, 5, 7)
            if (!isDrawing && context != null && !string.IsNullOrEmpty(context.FileTitle))
                MPropFormats(state, ops, dict, context);
            return ops;
        }

        private static void MPropFormats(PropertyLevels state, List<MigrationOperation> ops, PropertyDictionary dict, MigrationContext c)
        {
            string mass = dict[Role.Mass], table = dict[Role.MassTable], remark = dict[Role.Remark], format = dict[Role.Format];
            foreach (string cfg in state.Configurations)
            {
                if (SwPlusMarkup.IsGeneratedMass(state.Get(cfg, mass)))
                    Set(state, ops, cfg, mass, SwPlusFormat.MassStamp(cfg, c.FileTitle, c.IsAssembly, c.Grams, c.SmallFont), "масса текстом → выражение MProp");
                if (SwPlusMarkup.IsGeneratedMass(state.Get(cfg, table)))
                    Set(state, ops, cfg, table, SwPlusFormat.MassExpression(cfg, c.FileTitle, c.IsAssembly), "масса текстом → выражение MProp");
            }
            foreach (string level in new List<string>(state.Levels))
            {
                string cfg = level.Length > 0 ? level : (state.Configurations.Count == 1 ? state.Configurations[0] : null);
                if (level.Length == 0 && state.Configurations.Count == 1 && !string.IsNullOrEmpty(c.ActiveConfiguration)) cfg = c.ActiveConfiguration;
                if (cfg == null) continue;
                string note = state.Get(level, remark);
                if ((state.Get(level, format) ?? "").Trim() == BchRecord.FormatValue && note != null &&
                    Regex.IsMatch(note.Trim(), @"^\d+([.,]\d+)?\s*кг$"))
                    Set(state, ops, level, remark, SwPlusFormat.BchRemark(cfg, c.FileTitle, c.IsAssembly, c.Grams), "масса БЧ текстом → выражение MProp");
            }
            if (!c.IsAssembly) return;
            string text = dict[Role.DocDescription];
            foreach (string cfg in state.Configurations)
            {
                if (SwPlusMarkup.IsLegacyDocDescription(state.Get(cfg, text)))
                    Set(state, ops, cfg, text, SwPlusFormat.DocDescription(SwPlusFormat.AssemblyDrawingText, c.SmallFont), "«Сборочный чертёж» → формат MProp");
            }
            if (SwPlusMarkup.IsLegacyDocDescription(state.Get("", text)))
                Delete(state, ops, "", text, "уровень MProp — конфигурация");
        }

        /// <summary>Состояние после применения плана — для проверки идемпотентности.</summary>
        public static PropertyLevels Apply(PropertyLevels source, IEnumerable<MigrationOperation> ops)
        {
            PropertyLevels state = source.Clone();
            foreach (MigrationOperation op in ops)
            {
                if (op.Action == MigrationAction.Delete) state.Remove(op.Level, op.Name);
                else state.Set(op.Level, op.Name, op.NewValue);
            }
            return state;
        }

        private static void TransferToConfiguration(PropertyLevels state, List<MigrationOperation> ops, string cfg, string target, string[] aliases)
        {
            if (!IsEmpty(state.Get(cfg, target))) return;
            string value = FirstValue(state, cfg, aliases) ?? NonEmpty(state.Get("", target)) ?? FirstValue(state, "", aliases);
            if (value != null) Set(state, ops, cfg, target, value, "из алиаса v5; уровень MProp — конфигурация");
        }

        private static void RestoreLive(PropertyLevels state, List<MigrationOperation> ops, string name, string marker, string live)
        {
            foreach (string level in new List<string>(state.Levels))
            {
                string value = state.Get(level, name);
                if (value == null || value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (level.Length == 0) Set(state, ops, level, name, live, "статичное значение v5 → живое выражение");
                else Delete(state, ops, level, name, "статичная копия v5");
            }
        }

        private static bool AllConfigurationsFilled(PropertyLevels state, IList<string> configs, string name)
        {
            foreach (string cfg in configs)
            {
                if (IsEmpty(state.Get(cfg, name))) return false;
            }
            return true;
        }

        private static string FirstValue(PropertyLevels state, string level, string[] names)
        {
            foreach (string name in names)
            {
                string value = NonEmpty(state.Get(level, name));
                if (value != null) return value;
            }
            return null;
        }

        private static string NonEmpty(string value)
        {
            return IsEmpty(value) ? null : value;
        }

        private static bool IsEmpty(string value)
        {
            return value == null || value.Trim().Length == 0 || value.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Set(PropertyLevels state, List<MigrationOperation> ops, string level, string name, string value, string reason)
        {
            string old = state.Get(level, name);
            if (old == value) return;
            ops.Add(new MigrationOperation { Level = level, Name = name, Action = MigrationAction.Set, OldValue = old, NewValue = value, Reason = reason });
            state.Set(level, name, value);
        }

        private static void Delete(PropertyLevels state, List<MigrationOperation> ops, string level, string name, string reason)
        {
            string old = state.Get(level, name);
            if (old == null) return;
            ops.Add(new MigrationOperation { Level = level, Name = name, Action = MigrationAction.Delete, OldValue = old, Reason = reason });
            state.Remove(level, name);
        }
    }
}
