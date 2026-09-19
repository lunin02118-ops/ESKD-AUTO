using System;
using System.Collections.Generic;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Списки MProp этого рабочего места (З-3): при пустом свойстве MProp берёт первую строку списка — «Контору» из
    /// MProp_Firm.txt (FrmMProp: CboFirm.ListIndex = 0) и, после «Удалить все свойства», «Разработал» из MProp_Fam.txt.
    /// Поэтому фамилия и организация из настроек ЕСКД стоят в списках первыми; остальные строки сохраняются по порядку.
    /// </summary>
    public static class SwPlusLists
    {
        /// <summary>MProp_Fam.txt: фамилия на строке, без пустых и повторов; first — первой, second — если нет, в конец.</summary>
        public static string[] Families(IEnumerable<string> lines, string first, string second)
        {
            List<string> names = new List<string>();
            foreach (string line in lines ?? new string[0])
            {
                string t = (line ?? "").Trim();
                if (t.Length > 0 && !Contains(names, t)) names.Add(t);
            }
            string f = (first ?? "").Trim();
            if (f.Length > 0)
            {
                names.RemoveAll(n => Same(n, f));
                names.Insert(0, f);
            }
            string s = (second ?? "").Trim();
            if (s.Length > 0 && !Contains(names, s)) names.Add(s);
            return names.ToArray();
        }

        /// <summary>MProp_Firm.txt: пары «организация / код»; пара org (с её кодом) — первой, новой — с пустым кодом.</summary>
        public static string[] Firms(IList<string> lines, string org)
        {
            List<string[]> pairs = new List<string[]>();
            lines = lines ?? new string[0];
            for (int i = 0; i < lines.Count; i += 2)
            {
                string name = (lines[i] ?? "").Trim();
                string code = i + 1 < lines.Count ? (lines[i + 1] ?? "").Trim() : "";
                if (name.Length > 0 && !pairs.Exists(p => Same(p[0], name))) pairs.Add(new[] { name, code });
            }
            string o = (org ?? "").Trim();
            if (o.Length > 0)
            {
                string[] own = pairs.Find(p => Same(p[0], o)) ?? new[] { o, "" };
                pairs.RemoveAll(p => Same(p[0], o));
                pairs.Insert(0, new[] { o, own[1] });
            }
            List<string> output = new List<string>();
            foreach (string[] p in pairs) output.AddRange(p);
            return output.ToArray();
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(List<string> names, string name)
        {
            return names.Exists(n => Same(n, name));
        }
    }
}
