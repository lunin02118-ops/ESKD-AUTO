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
        /// Сырое и вычисленное значение одним вызовом Get4; null — свойства нет или чтение не удалось. fresh — вычисленное
        /// пересчитать (UseCached = false); иначе SolidWorks отдаёт его из кэша. Сырому значению пересчёт не нужен, а
        /// пересчёт «SW-Mass@@исполнение@…» каждого из сотни исполнений библиотечной детали занимал при Ctrl+S ~28 с
        /// против 0,15 с без надстройки (сверка SW API 23.09.2026, №24; e2e P20). pair[2] = "1" — вычисленное свежее.
        /// </summary>
        private string[] Values(string cfg, string name, bool fresh)
        {
            if (!Exists(cfg, name)) return null;
            string key = (cfg ?? "") + "\0" + name;
            string[] pair;
            if (_values.TryGetValue(key, out pair) && (!fresh || pair[2].Length > 0)) return pair;
            string val, resolved;
            try
            {
                Manager(cfg).Get4(name, !fresh, out val, out resolved);
                pair = new[] { val ?? "", resolved ?? "", fresh ? "1" : "" };
            }
            catch (Exception ex)
            {
                Log.Error("Get4 " + name + " " + _docTitle, ex);
                return null;
            }
            _values[key] = pair;
            return pair;
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
                int rc = Manager(cfg).Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value,
                    (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
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

        private static string Short(string s)
        {
            string t = (s ?? "").Replace("\r", "").Replace("\n", "⏎");
            return t.Length > 80 ? t.Substring(0, 80) + "…" : t;
        }
    }
}
