using System;
using System.Collections.Generic;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Единственная точка записи пользовательских свойств документа.
    /// Пишет только при фактическом отличии сырого значения, проверяет коды возврата API
    /// и ведёт журнал операций; в режиме DryRun только строит журнал.
    /// </summary>
    public sealed class PropertyWriter
    {
        private readonly ModelDoc2 _doc;
        private readonly bool _dryRun;
        private readonly string _docTitle;
        private readonly Dictionary<string, HashSet<string>> _names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Один синхронизатор читает одно свойство по нескольку раз (сырое, вычисленное, «пусто ли»), а каждое чтение —
        // межпроцессный вызов COM. Значения держим до первой записи: вычисленное значение одного свойства может
        // ссылаться на другое ($PRP), поэтому любая запись сбрасывает весь кэш значений (аудит 19.09, волна 2).
        private readonly Dictionary<string, CustomPropertyManager> _managers = new Dictionary<string, CustomPropertyManager>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _values = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private string[] _configurations;
        private bool _dirty;

        public int Changes { get; private set; }
        public int Failures { get; private set; }
        public readonly List<string> Operations = new List<string>();

        public PropertyWriter(ModelDoc2 doc, bool dryRun)
        {
            _doc = doc;
            _dryRun = dryRun;
            _docTitle = DocInfo.TitleOf(doc);
            if (_docTitle.Length == 0) _docTitle = "?";
        }

        public ModelDoc2 Document { get { return _doc; } }

        /// <summary>«Сводка → Автор» документа (SummaryInfo 2) — поле, из которого MProp берёт «Конструктора».</summary>
        public string Author()
        {
            try
            {
                return _doc.get_SummaryInfo((int)swSummInfoField_e.swSumInfoAuthor) ?? "";
            }
            catch (Exception ex)
            {
                Log.Error("SummaryInfo Author " + _docTitle, ex);
                return "";
            }
        }

        /// <summary>Записать «Сводка → Автор», если отличается; в журнал операций, как свойство.</summary>
        public bool SetAuthor(string value)
        {
            value = value ?? "";
            if (Author() == value) return false;
            Operations.Add(string.Format("[Сводка] Автор = «{0}»", value));
            if (_dryRun) return false;
            try
            {
                _doc.set_SummaryInfo((int)swSummInfoField_e.swSumInfoAuthor, value);
                Changes++;
                Dirty();
                return true;
            }
            catch (Exception ex)
            {
                Failures++;
                Log.Error("SummaryInfo Author = " + value, ex);
                return false;
            }
        }

        /// <summary>
        /// Свойства, записанные через API, SolidWorks изменением документа не считает (D13, 23.09.2026): без флага документ
        /// закрылся бы без вопроса «сохранить?», и записанное надстройкой в открытую модель (формат из чертежа, кнопка
        /// «Синхронизировать», «Применить без сохранения») молча пропало бы. Флаг ставится один раз на экземпляр.
        /// </summary>
        private void Dirty()
        {
            if (_dirty) return;
            _dirty = true;
            try
            {
                _doc.SetSaveFlag();
            }
            catch (Exception ex)
            {
                Log.Error("SetSaveFlag " + _docTitle, ex);
            }
        }

        /// <summary>Модель изменена в обход записи свойств (единицы, материал): вычисленные значения перечитать.</summary>
        public void Forget()
        {
            _values.Clear();
        }

        /// <summary>Режим DryRun: запись только в журнал (единицы документа тоже не переключаются).</summary>
        public bool DryRun { get { return _dryRun; } }

        public string[] ConfigurationNames()
        {
            if (_configurations != null) return (string[])_configurations.Clone();
            try
            {
                string[] names = _doc.GetConfigurationNames() as string[];
                _configurations = names ?? new string[0];
                return (string[])_configurations.Clone();
            }
            catch (Exception ex)
            {
                Log.Error("GetConfigurationNames " + _docTitle, ex);
                return new string[0];
            }
        }

        public string ActiveConfigurationName()
        {
            try
            {
                Configuration c = _doc.GetActiveConfiguration() as Configuration;
                return c != null ? c.Name : "";
            }
            catch (Exception ex)
            {
                Log.Error("GetActiveConfiguration " + _docTitle, ex);
                return "";
            }
        }

        private CustomPropertyManager Manager(string cfg)
        {
            string key = cfg ?? "";
            CustomPropertyManager manager;
            if (!_managers.TryGetValue(key, out manager))
            {
                manager = _doc.Extension.get_CustomPropertyManager(key);
                _managers[key] = manager;
            }
            return manager;
        }

        /// <summary>
        /// Сырое значение (fresh = false) или сырое и вычисленное (fresh = true); null — свойства нет или чтение не удалось.
        /// Сырое читается CustomInfo2, как у MProp: Get4 и с UseCached = true при первом чтении выражения
        /// «SW-Mass@@исполнение@…» после открытия файла пересчитывает массу исполнения — у библиотечной детали с 79
        /// исполнениями этап «масса» занимал при Ctrl+S 4,5 с, с UseCached = false — ~31 с, против 0,17 с без надстройки
        /// (сверка SW API 23.09.2026, №24; проба 24.09.2026; e2e P20, контракт C11). Вычисленное — Get4 с пересчётом.
        /// pair[2] = "1" — вычисленное прочитано.
        /// </summary>
        private string[] Values(string cfg, string name, bool fresh)
        {
            if (!Exists(cfg, name)) return null;
            string key = (cfg ?? "") + "\0" + name;
            string[] pair;
            if (_values.TryGetValue(key, out pair) && (!fresh || pair[2].Length > 0)) return pair;
            try
            {
                if (fresh)
                {
                    string val, resolved;
                    Manager(cfg).Get4(name, false, out val, out resolved);
                    pair = new[] { val ?? "", resolved ?? "", "1" };
                }
                else
                {
                    pair = new[] { RawValue(cfg, name), "", "" };
                }
            }
            catch (Exception ex)
            {
                Log.Error("Get4 " + name + " " + _docTitle, ex);
                return null;
            }
            _values[key] = pair;
            return pair;
        }

        /// <summary>Сырое значение без пересчёта — ModelDoc2.CustomInfo2, как читает MProp (контракт C11).</summary>
        private string RawValue(string cfg, string name)
        {
            return _doc.get_CustomInfo2(cfg ?? "", name) ?? "";
        }

        private HashSet<string> Names(string cfg)
        {
            string key = cfg ?? "";
            HashSet<string> set;
            if (_names.TryGetValue(key, out set)) return set;
            set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                object[] raw = Manager(key).GetNames() as object[];
                if (raw != null)
                {
                    foreach (object o in raw)
                    {
                        if (o != null) set.Add(o.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("GetNames " + _docTitle + " [" + key + "]", ex);
            }
            _names[key] = set;
            return set;
        }

        public bool Exists(string cfg, string name)
        {
            return Names(cfg).Contains(name);
        }

        /// <summary>Имена пользовательских свойств уровня (копия списка).</summary>
        public List<string> NamesAt(string cfg)
        {
            return new List<string>(Names(cfg));
        }

        /// <summary>Сырое значение (выражение, как записано) или null, если свойства нет.</summary>
        public string Raw(string cfg, string name)
        {
            string[] pair = Values(cfg, name, false);
            return pair != null ? pair[0] : null;
        }

        public string Resolved(string cfg, string name)
        {
            string[] pair = Values(cfg, name, true);
            return pair != null ? pair[1] : null;
        }

        public static bool IsEmptyOrTemplate(string raw)
        {
            return raw == null || raw.Trim().Length == 0 || raw.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Записать значение, если сырое значение отличается. Перевод строки SolidWorks возвращает как CR LF, а пишется LF:
        /// без нормализации многострочные значения переписывались бы при каждом сохранении открытого заново документа.
        /// Возвращает true при записи.
        /// </summary>
        public bool Set(string cfg, string name, string value)
        {
            value = value ?? "";
            string current = Raw(cfg, name);
            if (current != null && string.Equals(MaterialRecord.Normalize(current), MaterialRecord.Normalize(value), StringComparison.Ordinal)) return false;
            string level = string.IsNullOrEmpty(cfg) ? "общие" : cfg;
            Operations.Add(string.Format("{0} [{1}] {2}: {3} → {4}", _docTitle, level, name,
                current == null ? "<нет>" : Short(current), Short(value)));
            if (_dryRun) return false;
            _values.Clear();
            try
            {
                // Существующее текстовое свойство — на своей строке «Свойств файла», как CustomInfo2 у MProp: DeleteAndAdd
                // уносил его в конец списка, и порядок в каждом файле выходил свой (сверка SW API 23.09.2026, №23).
                // ReplaceValue типа не меняет и у свойства другого типа не отказывает (контракт C09), поэтому нетекстовое
                // пишется, как раньше, с заменой — текстом.
                bool inPlace = current != null &&
                    Manager(cfg).GetType2(name) == (int)swCustomInfoType_e.swCustomInfoText;
                int rc = Manager(cfg).Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value,
                    inPlace ? (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue
                            : (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
                if (rc != (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged && inPlace)
                {
                    Log.Info(string.Format("Add3 ReplaceValue код {0}: {1} [{2}] {3} — запись с заменой свойства", rc, _docTitle, level, name));
                    rc = Manager(cfg).Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value,
                        (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
                }
                if (rc != (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged)
                {
                    Failures++;
                    Log.Error(string.Format("Add3 код возврата {0}: {1} [{2}] {3}", rc, _docTitle, level, name));
                    return false;
                }
                Names(cfg).Add(name);
                Changes++;
                Dirty();
                return true;
            }
            catch (Exception ex)
            {
                Failures++;
                Log.Error("Add3 " + name + " " + _docTitle + " [" + level + "]", ex);
                return false;
            }
        }

        public bool SetIfEmpty(string cfg, string name, string value)
        {
            string current = Raw(cfg, name);
            if (current != null && current.Trim().Length > 0) return false;
            return Set(cfg, name, value);
        }

        public bool Delete(string cfg, string name)
        {
            if (!Exists(cfg, name)) return false;
            string level = string.IsNullOrEmpty(cfg) ? "общие" : cfg;
            Operations.Add(string.Format("{0} [{1}] {2}: удалено", _docTitle, level, name));
            if (_dryRun) return false;
            _values.Clear();
            try
            {
                int rc = Manager(cfg).Delete2(name);
                if (rc != (int)swCustomInfoDeleteResult_e.swCustomInfoDeleteResult_OK)
                {
                    Failures++;
                    Log.Error(string.Format("Delete2 код возврата {0}: {1} [{2}] {3}", rc, _docTitle, level, name));
                    return false;
                }
                Names(cfg).Remove(name);
                Changes++;
                Dirty();
                return true;
            }
            catch (Exception ex)
            {
                Failures++;
                Log.Error("Delete2 " + name + " " + _docTitle + " [" + level + "]", ex);
                return false;
            }
        }

        // ------------------------------------------------------------------ единый порядок (№23)

        /// <summary>
        /// Типы свойств, которые перенос в конец пересоздаёт без потерь (swCustomInfoType_e: число, да/нет, текст, дата;
        /// контракт C10). Дробное SolidWorks отдаёт числом (3) со значением «2.500000» и такое обратно не принимает — оно,
        /// уравнение (105) и неизвестные не трогаются: уровень с ними пропускается целиком.
        /// </summary>
        private static readonly int[] MovableTypes = { 3, 11, 30, 64 };

        /// <summary>Имена уровня в порядке «Свойств файла» (свежий GetNames); null — не прочитано.</summary>
        public List<string> OrderedNames(string cfg)
        {
            try
            {
                List<string> list = new List<string>();
                object[] raw = Manager(cfg).GetNames() as object[];
                if (raw != null)
                    foreach (object o in raw)
                        if (o != null) list.Add(o.ToString());
                return list;
            }
            catch (Exception ex)
            {
                Log.Error("Порядок свойств: GetNames " + _docTitle + " [" + (cfg ?? "") + "]", ex);
                return null;
            }
        }

        /// <summary>
        /// Почему свойство нельзя перенести в конец: связано с родителем, управляется таблицей параметров, тип вне
        /// проверенных. Пусто — можно.
        /// </summary>
        public string MoveRefusal(string cfg, string name)
        {
            CustomPropertyManager m = Manager(cfg);
            try
            {
                // Связь с родителем бывает только у производной конфигурации. У общих свойств и у обычной конфигурации
                // SolidWorks тоже отвечает LinkAll = true (прогон 24.09.2026: пропущены общие свойства новой пластины).
                bool derived = false;
                if (!string.IsNullOrEmpty(cfg))
                {
                    Configuration c = _doc.GetConfigurationByName(cfg) as Configuration;
                    derived = c != null && c.IsDerived();
                }
                if (derived && m.LinkAll) return "свойства уровня связаны с родителем";
                if (derived)
                {
                    string raw, resolved;
                    bool wasResolved, linked;
                    m.Get6(name, true, out raw, out resolved, out wasResolved, out linked);
                    if (linked) return "«" + name + "» связано с родителем";
                }
                string val = RawValue(cfg, name);
                if (!m.IsCustomPropertyEditable(name, cfg ?? "")) return "«" + name + "» не редактируется (таблица параметров)";
                int type = m.GetType2(name);
                if (Array.IndexOf(MovableTypes, type) < 0) return "«" + name + "»: тип " + type;
                long whole;
                if (type == (int)swCustomInfoType_e.swCustomInfoNumber &&
                    !long.TryParse((val ?? "").Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out whole))
                    return "«" + name + "»: дробное число «" + val + "»";
                return "";
            }
            catch (Exception ex)
            {
                Log.Error("Порядок свойств: " + name + " " + _docTitle + " [" + (cfg ?? "") + "]", ex);
                return "«" + name + "» не прочитано";
            }
        }

        /// <summary>
        /// Перенести свойство в конец списка уровня: одно Add3 с заменой — тем же типом и тем же сырым значением (выражение
        /// массы остаётся выражением). После — сверка типа и значения; расхождение — ошибка в журнале.
        /// </summary>
        public bool MoveToEnd(string cfg, string name)
        {
            if (_dryRun) return false;
            string level = string.IsNullOrEmpty(cfg) ? "общие" : cfg;
            CustomPropertyManager m = Manager(cfg);
            try
            {
                // Сырое значение — без пересчёта «SW-Mass@@…» (№24, контракт C11).
                string raw = RawValue(cfg, name);
                int type = m.GetType2(name);
                string value = MaterialRecord.Normalize(raw);
                _values.Clear();
                int rc = m.Add3(name, type, value, (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
                if (rc != (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged)
                {
                    Failures++;
                    Log.Error(string.Format("Порядок свойств: Add3 код {0}: {1} [{2}] {3}, тип {4}, значение «{5}»",
                        rc, _docTitle, level, name, type, value));
                    return false;
                }
                string after = RawValue(cfg, name);
                int typeAfter = m.GetType2(name);
                if (typeAfter != type || !string.Equals(MaterialRecord.Normalize(after ?? ""), value, StringComparison.Ordinal))
                {
                    Failures++;
                    Log.Error(string.Format("Порядок свойств: {0} [{1}] {2} было тип {3} «{4}», стало тип {5} «{6}»",
                        _docTitle, level, name, type, value, typeAfter, after));
                }
                Changes++;
                Dirty();
                return true;
            }
            catch (Exception ex)
            {
                Failures++;
                Log.Error("Порядок свойств: перенос " + name + " " + _docTitle + " [" + level + "]", ex);
                return false;
            }
        }

        /// <summary>Строка в журнал операций синхронизации.</summary>
        public void Note(string text)
        {
            Operations.Add(_docTitle + ": " + text);
        }

        private static string Short(string s)
        {
            string t = (s ?? "").Replace("\r", "").Replace("\n", "⏎");
            return t.Length > 80 ? t.Substring(0, 80) + "…" : t;
        }
    }
}
