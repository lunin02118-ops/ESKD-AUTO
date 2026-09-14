// Спайк С-6 ТЗ-02: XLSX через System.IO.Packaging (WindowsBase.dll), C# 5, без сторонних библиотек.
// Чтение по заголовкам (первые 10 строк, без учёта регистра и пробелов), запись ячеек в копию шаблона
// с сохранением стилей, объединений, ширин и чужих листов; общий и встроенный текст.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using System.Xml;

public static class Xlsx
{
    const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public sealed class Book : IDisposable
    {
        public Package Pkg;
        public PackagePart Workbook;
        public List<string> Shared = new List<string>();
        public Dictionary<string, Uri> Sheets = new Dictionary<string, Uri>();
        bool dirtyShared;
        XmlDocument sharedDoc;
        PackagePart sharedPart;

        public static Book Open(string path, bool write)
        {
            Book b = new Book();
            b.Pkg = Package.Open(path, FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read);
            PackageRelationship rel = b.Pkg.GetRelationshipsByType("http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument").First();
            Uri wbUri = PackUriHelper.ResolvePartUri(new Uri("/", UriKind.Relative), rel.TargetUri);
            b.Workbook = b.Pkg.GetPart(wbUri);
            XmlDocument wb = Load(b.Workbook);
            XmlNamespaceManager ns = Ns(wb);
            foreach (XmlElement s in wb.SelectNodes("//m:sheets/m:sheet", ns))
            {
                string id = s.GetAttribute("id", RelNs);
                Uri target = PackUriHelper.ResolvePartUri(wbUri, b.Workbook.GetRelationship(id).TargetUri);
                b.Sheets[s.GetAttribute("name")] = target;
            }
            foreach (PackageRelationship r in b.Workbook.GetRelationshipsByType("http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings"))
            {
                b.sharedPart = b.Pkg.GetPart(PackUriHelper.ResolvePartUri(wbUri, r.TargetUri));
                b.sharedDoc = Load(b.sharedPart);
                foreach (XmlElement si in b.sharedDoc.SelectNodes("//m:si", Ns(b.sharedDoc)))
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (XmlNode t in si.SelectNodes(".//m:t", Ns(b.sharedDoc))) sb.Append(t.InnerText);
                    b.Shared.Add(sb.ToString());
                }
            }
            return b;
        }

        public Sheet GetSheet(string name) { return new Sheet(this, Pkg.GetPart(Sheets[name])); }

        public int SharedIndex(string text)
        {
            int i = Shared.IndexOf(text);
            if (i >= 0) return i;
            if (sharedDoc == null) return -1; // книги без общего словаря — текст пишется встроенным
            XmlElement si = sharedDoc.CreateElement("si", Main);
            XmlElement t = sharedDoc.CreateElement("t", Main);
            t.SetAttribute("xml:space", "preserve");
            t.InnerText = text;
            si.AppendChild(t);
            sharedDoc.DocumentElement.AppendChild(si);
            Shared.Add(text);
            dirtyShared = true;
            return Shared.Count - 1;
        }

        public void Save()
        {
            if (dirtyShared)
            {
                sharedDoc.DocumentElement.SetAttribute("uniqueCount", Shared.Count.ToString(CultureInfo.InvariantCulture));
                Store(sharedPart, sharedDoc);
            }
            // Excel пересчитывает формулы при открытии
            XmlDocument wb = Load(Workbook);
            XmlNamespaceManager ns = Ns(wb);
            XmlElement calc = (XmlElement)wb.SelectSingleNode("//m:calcPr", ns);
            if (calc == null) { calc = wb.CreateElement("calcPr", Main); wb.DocumentElement.AppendChild(calc); }
            calc.SetAttribute("fullCalcOnLoad", "1");
            Store(Workbook, wb);
            Pkg.Flush();
        }

        public void Dispose() { Pkg.Close(); }
    }

    public sealed class Sheet
    {
        readonly Book book;
        readonly PackagePart part;
        public readonly XmlDocument Doc;
        readonly XmlNamespaceManager ns;

        internal Sheet(Book b, PackagePart p) { book = b; part = p; Doc = Load(p); ns = Ns(Doc); }

        public string Get(string cellRef)
        {
            XmlElement c = (XmlElement)Doc.SelectSingleNode("//m:sheetData/m:row/m:c[@r='" + cellRef + "']", ns);
            if (c == null) return "";
            string type = c.GetAttribute("t");
            if (type == "inlineStr") { XmlNode t = c.SelectSingleNode("m:is", ns); return t == null ? "" : t.InnerText; }
            XmlNode v = c.SelectSingleNode("m:v", ns);
            if (v == null) return "";
            if (type == "s") return book.Shared[int.Parse(v.InnerText, CultureInfo.InvariantCulture)];
            return v.InnerText;
        }

        /// <summary>Строка и столбцы заголовков: первая строка из первых 10, где найдены все обязательные заголовки.</summary>
        public Dictionary<string, int> FindHeaders(string[] required, out int headerRow)
        {
            headerRow = 0;
            string missing = null;
            for (int r = 1; r <= 10; r++)
            {
                Dictionary<string, int> map = new Dictionary<string, int>();
                foreach (XmlElement c in Doc.SelectNodes("//m:sheetData/m:row[@r='" + r + "']/m:c", ns))
                {
                    string text = Norm(Get(c.GetAttribute("r")));
                    if (text.Length > 0 && !map.ContainsKey(text)) map[text] = ColumnIndex(c.GetAttribute("r"));
                }
                string[] absent = required.Where(h => !map.ContainsKey(Norm(h))).ToArray();
                if (absent.Length == 0) { headerRow = r; return map; }
                if (map.Count > 0 && (missing == null || absent.Length < missing.Split(',').Length)) missing = string.Join(", ", absent);
            }
            throw new InvalidDataException("В служебной записке нет столбца: " + (missing ?? string.Join(", ", required)));
        }

        public int LastRow()
        {
            int max = 0;
            foreach (XmlElement row in Doc.SelectNodes("//m:sheetData/m:row", ns)) max = Math.Max(max, int.Parse(row.GetAttribute("r"), CultureInfo.InvariantCulture));
            return max;
        }

        public void SetText(string cellRef, string text) { Set(cellRef, text, null); }
        public void SetNumber(string cellRef, double value) { Set(cellRef, null, value); }

        void Set(string cellRef, string text, double? number)
        {
            int rowIndex = RowIndex(cellRef);
            XmlElement sheetData = (XmlElement)Doc.SelectSingleNode("//m:sheetData", ns);
            XmlElement row = (XmlElement)sheetData.SelectSingleNode("m:row[@r='" + rowIndex + "']", ns);
            if (row == null)
            {
                row = Doc.CreateElement("row", Main);
                row.SetAttribute("r", rowIndex.ToString(CultureInfo.InvariantCulture));
                XmlElement after = null;
                foreach (XmlElement r in sheetData.SelectNodes("m:row", ns)) if (int.Parse(r.GetAttribute("r")) < rowIndex) after = r;
                if (after == null) sheetData.PrependChild(row); else sheetData.InsertAfter(row, after);
            }
            XmlElement cell = (XmlElement)row.SelectSingleNode("m:c[@r='" + cellRef + "']", ns);
            if (cell == null)
            {
                cell = Doc.CreateElement("c", Main);
                cell.SetAttribute("r", cellRef);
                XmlElement after = null;
                foreach (XmlElement c in row.SelectNodes("m:c", ns)) if (ColumnIndex(c.GetAttribute("r")) < ColumnIndex(cellRef)) after = c;
                if (after == null) row.PrependChild(cell); else row.InsertAfter(cell, after);
            }
            string style = cell.GetAttribute("s"); // стиль ячейки шаблона сохраняется
            cell.RemoveAll();
            cell.SetAttribute("r", cellRef);
            if (style.Length > 0) cell.SetAttribute("s", style);
            if (number.HasValue)
            {
                XmlElement v = Doc.CreateElement("v", Main);
                v.InnerText = number.Value.ToString("R", CultureInfo.InvariantCulture);
                cell.AppendChild(v);
            }
            else
            {
                int shared = book.SharedIndex(text ?? "");
                if (shared >= 0)
                {
                    cell.SetAttribute("t", "s");
                    XmlElement v = Doc.CreateElement("v", Main);
                    v.InnerText = shared.ToString(CultureInfo.InvariantCulture);
                    cell.AppendChild(v);
                }
                else
                {
                    cell.SetAttribute("t", "inlineStr");
                    XmlElement isEl = Doc.CreateElement("is", Main);
                    XmlElement t = Doc.CreateElement("t", Main);
                    t.SetAttribute("xml:space", "preserve");
                    t.InnerText = text ?? "";
                    isEl.AppendChild(t);
                    cell.AppendChild(isEl);
                }
            }
        }

        /// <summary>Скрытый служебный столбец (Р-6: «Папка» — запомненный ответ сопоставления).</summary>
        public void HideColumn(int col)
        {
            XmlElement cols = (XmlElement)Doc.SelectSingleNode("//m:cols", ns);
            if (cols == null)
            {
                cols = Doc.CreateElement("cols", Main);
                XmlNode sheetData = Doc.SelectSingleNode("//m:sheetData", ns);
                Doc.DocumentElement.InsertBefore(cols, sheetData);
            }
            XmlElement c = Doc.CreateElement("col", Main);
            c.SetAttribute("min", col.ToString(CultureInfo.InvariantCulture));
            c.SetAttribute("max", col.ToString(CultureInfo.InvariantCulture));
            c.SetAttribute("width", "20");
            c.SetAttribute("hidden", "1");
            c.SetAttribute("customWidth", "1");
            cols.AppendChild(c);
        }

        public void Save()
        {
            XmlElement dim = (XmlElement)Doc.SelectSingleNode("//m:dimension", ns);
            if (dim != null) dim.ParentNode.RemoveChild(dim); // размер диапазона Excel вычислит сам
            Store(part, Doc);
        }
    }

    public static string Norm(string s) { return new string((s ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant(); }

    public static int ColumnIndex(string cellRef)
    {
        int n = 0;
        foreach (char ch in cellRef) { if (char.IsLetter(ch)) n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1); else break; }
        return n;
    }

    public static int RowIndex(string cellRef) { return int.Parse(new string(cellRef.Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture); }

    public static string Ref(int col, int row)
    {
        string s = "";
        while (col > 0) { int m = (col - 1) % 26; s = (char)('A' + m) + s; col = (col - 1) / 26; }
        return s + row.ToString(CultureInfo.InvariantCulture);
    }

    static XmlDocument Load(PackagePart p)
    {
        XmlDocument d = new XmlDocument();
        d.PreserveWhitespace = true;
        using (Stream s = p.GetStream(FileMode.Open, FileAccess.Read)) d.Load(s);
        return d;
    }

    static void Store(PackagePart p, XmlDocument d)
    {
        using (Stream s = p.GetStream(FileMode.Create, FileAccess.Write))
        using (XmlWriter w = XmlWriter.Create(s, new XmlWriterSettings { Encoding = new UTF8Encoding(false) })) d.Save(w);
    }

    static XmlNamespaceManager Ns(XmlDocument d)
    {
        XmlNamespaceManager m = new XmlNamespaceManager(d.NameTable);
        m.AddNamespace("m", Main);
        return m;
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        string template = args[0], output = args[1];
        File.Copy(template, output, true);
        string[] required = { "Позиция", "Изделие", "Стандартность", "Направление", "Шифр", "Эталон", "Исполнение", "Тираж", "RAL", "Поверхность" };
        using (Xlsx.Book book = Xlsx.Book.Open(output, true))
        {
            Xlsx.Sheet sheet = book.GetSheet("СЗ");
            int headerRow;
            Dictionary<string, int> cols = sheet.FindHeaders(required, out headerRow);
            Console.WriteLine("заголовки в строке " + headerRow + ": " + string.Join("; ", cols.Select(kv => kv.Key + "=" + kv.Value)));
            for (int r = headerRow + 1; r <= sheet.LastRow(); r++)
            {
                string code = sheet.Get(Xlsx.Ref(cols[Xlsx.Norm("Шифр")], r));
                if (code.Length == 0) continue;
                Console.WriteLine(string.Format("строка {0}: шифр «{1}», тираж {2}, стандартность {3}, RAL «{4}»", r, code,
                    sheet.Get(Xlsx.Ref(cols[Xlsx.Norm("Тираж")], r)), sheet.Get(Xlsx.Ref(cols[Xlsx.Norm("Стандартность")], r)),
                    sheet.Get(Xlsx.Ref(cols[Xlsx.Norm("RAL")], r))));
            }
            // Р-6: скрытый столбец «Папка» с запомненным ответом сопоставления
            int folderCol = cols.Values.Max() + 2;
            sheet.SetText(Xlsx.Ref(folderCol, headerRow), "Папка");
            sheet.SetText(Xlsx.Ref(folderCol, headerRow + 1), @"Z:\03_ЗАКАЗЫ\52_Тест\ТС-52-Т1");
            sheet.HideColumn(folderCol);
            // запись в стилизованную ячейку, новое число и новая строка
            sheet.SetText(Xlsx.Ref(cols[Xlsx.Norm("RAL")], headerRow + 2), "RAL 7024 матовый");
            sheet.SetNumber(Xlsx.Ref(cols[Xlsx.Norm("Тираж")], headerRow + 2), 12.5);
            int newRow = sheet.LastRow() + 1;
            sheet.SetText(Xlsx.Ref(cols[Xlsx.Norm("Шифр")], newRow), "ТС-52-Н9 «новое» & <проверка>");
            sheet.SetNumber(Xlsx.Ref(cols[Xlsx.Norm("Тираж")], newRow), 3);
            sheet.Save();
            book.Save();
        }
        using (Xlsx.Book book = Xlsx.Book.Open(output, false))
        {
            Xlsx.Sheet sheet = book.GetSheet("СЗ");
            int headerRow;
            Dictionary<string, int> cols = sheet.FindHeaders(required, out headerRow);
            Console.WriteLine("после записи, строк: " + sheet.LastRow() + "; RAL второй позиции: «" + sheet.Get(Xlsx.Ref(cols[Xlsx.Norm("RAL")], headerRow + 2)) + "»");
        }
        try
        {
            using (Xlsx.Book book = Xlsx.Book.Open(template, false))
            {
                int h;
                book.GetSheet("СЗ").FindHeaders(new[] { "Шифр", "Покрытие" }, out h);
            }
            Console.WriteLine("ОШИБКА: нет исключения о недостающем столбце");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine("недостающий столбец: " + ex.Message);
        }
        return 0;
    }
}
