using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Книга .xlsx (Office Open XML) без Excel и сторонних библиотек (Р-3): чтение ячеек и именованных диапазонов,
    /// запись значений, новые листы. Листы, которые не менялись, переписываются байт в байт.
    /// </summary>
    public sealed class XlsxBook : IDisposable
    {
        internal static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        internal static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
        private const string WorksheetType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
        private const string WorksheetContent = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

        private readonly string _path;
        private readonly Dictionary<string, byte[]> _parts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new List<string>();
        private readonly XDocument _workbook;
        private readonly XDocument _workbookRels;
        private readonly XDocument _contentTypes;
        private readonly List<string> _sharedStrings = new List<string>();
        private readonly Dictionary<string, XlsxSheet> _sheets = new Dictionary<string, XlsxSheet>(StringComparer.OrdinalIgnoreCase);
        private XDocument _styles;
        private bool _stylesChanged;
        // Одинаковый стиль, запрошенный повторно, отдаёт прежний индекс: без этого каждая книга ЛЗК дописывала
        // в styles.xml десятки одинаковых шрифтов, рамок и xf (аудит 19.09, желательное по Xlsx).
        private readonly Dictionary<string, int> _styleIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        private XlsxBook(string path)
        {
            _path = path;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    using (Stream s = entry.Open())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        _parts[entry.FullName] = ms.ToArray();
                        _order.Add(entry.FullName);
                    }
                }
            }
            _workbook = Load("xl/workbook.xml");
            _workbookRels = Load("xl/_rels/workbook.xml.rels");
            _contentTypes = Load("[Content_Types].xml");
            if (_parts.ContainsKey("xl/sharedStrings.xml"))
            {
                foreach (XElement si in Load("xl/sharedStrings.xml").Root.Elements(Main + "si"))
                    _sharedStrings.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));
            }
        }

        public static XlsxBook Open(string path)
        {
            return new XlsxBook(path);
        }

        /// <summary>
        /// Новая книга с одним листом. Журнал изменений (ТЗ-02 Т-51) заводится в папке изделия сам:
        /// держать в репозитории двоичный шаблон ради одного листа с шапкой незачем, а книга,
        /// собранная здесь, открывается и Excel, и этим же разбором.
        /// </summary>
        public static XlsxBook Create(string path, string sheetName)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            XDocument types = new XDocument(new XElement(ContentTypes + "Types",
                new XElement(ContentTypes + "Default", new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ContentTypes + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(ContentTypes + "Override", new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(ContentTypes + "Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                    new XAttribute("ContentType", WorksheetContent)),
                new XElement(ContentTypes + "Override", new XAttribute("PartName", "/xl/styles.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))));
            XDocument rels = new XDocument(new XElement(PackageRel + "Relationships",
                new XElement(PackageRel + "Relationship", new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml"))));
            XDocument workbook = new XDocument(new XElement(Main + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName),
                new XElement(Main + "sheets", new XElement(Main + "sheet", new XAttribute("name", sheetName),
                    new XAttribute("sheetId", 1), new XAttribute(Rel + "id", "rId1")))));
            XDocument workbookRels = new XDocument(new XElement(PackageRel + "Relationships",
                new XElement(PackageRel + "Relationship", new XAttribute("Id", "rId1"),
                    new XAttribute("Type", WorksheetType), new XAttribute("Target", "worksheets/sheet1.xml")),
                new XElement(PackageRel + "Relationship", new XAttribute("Id", "rId2"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                    new XAttribute("Target", "styles.xml"))));
            XDocument sheet = new XDocument(new XElement(Main + "worksheet",
                new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName),
                new XElement(Main + "sheetData"),
                new XElement(Main + "pageMargins", new XAttribute("left", "0.4"), new XAttribute("right", "0.4"),
                    new XAttribute("top", "0.6"), new XAttribute("bottom", "0.6"),
                    new XAttribute("header", "0.3"), new XAttribute("footer", "0.3"))));
            // Нулевые шрифт, заливки и рамка — основа, к которой AddStyle дописывает свои.
            XDocument styles = new XDocument(new XElement(Main + "styleSheet",
                new XElement(Main + "fonts", new XAttribute("count", 1),
                    new XElement(Main + "font", new XElement(Main + "sz", new XAttribute("val", "10")),
                        new XElement(Main + "name", new XAttribute("val", "Arial")),
                        new XElement(Main + "charset", new XAttribute("val", "204")),
                        new XElement(Main + "family", new XAttribute("val", "2")))),
                new XElement(Main + "fills", new XAttribute("count", 2),
                    new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "none"))),
                    new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "gray125")))),
                new XElement(Main + "borders", new XAttribute("count", 1),
                    new XElement(Main + "border", new XElement(Main + "left"), new XElement(Main + "right"),
                        new XElement(Main + "top"), new XElement(Main + "bottom"), new XElement(Main + "diagonal"))),
                new XElement(Main + "cellStyleXfs", new XAttribute("count", 1),
                    new XElement(Main + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0),
                        new XAttribute("fillId", 0), new XAttribute("borderId", 0))),
                new XElement(Main + "cellXfs", new XAttribute("count", 1),
                    new XElement(Main + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0),
                        new XAttribute("fillId", 0), new XAttribute("borderId", 0), new XAttribute("xfId", 0))),
                // Именованный стиль «Обычный»: без него Excel и сторонние разборы считают книгу без стиля по умолчанию.
                new XElement(Main + "cellStyles", new XAttribute("count", 1),
                    new XElement(Main + "cellStyle", new XAttribute("name", "Normal"),
                        new XAttribute("xfId", 0), new XAttribute("builtinId", 0)))));

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Write(zip, "[Content_Types].xml", types);
                Write(zip, "_rels/.rels", rels);
                Write(zip, "xl/workbook.xml", workbook);
                Write(zip, "xl/_rels/workbook.xml.rels", workbookRels);
                Write(zip, "xl/worksheets/sheet1.xml", sheet);
                Write(zip, "xl/styles.xml", styles);
            }
            return Open(path);
        }

        private static void Write(ZipArchive zip, string part, XDocument doc)
        {
            ZipArchiveEntry entry = zip.CreateEntry(part, CompressionLevel.Optimal);
            using (Stream s = entry.Open())
            using (XmlWriter w = XmlWriter.Create(s, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                doc.Save(w);
        }

        public void Dispose()
        {
        }

        private XDocument Load(string part)
        {
            byte[] data;
            if (!_parts.TryGetValue(part, out data)) throw new InvalidDataException("В книге нет части " + part);
            using (MemoryStream ms = new MemoryStream(data))
                return XDocument.Load(ms, LoadOptions.PreserveWhitespace);
        }

        private void Store(string part, XDocument doc)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                XmlWriterSettings settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false) };
                using (XmlWriter w = XmlWriter.Create(ms, settings)) doc.Save(w);
                if (!_parts.ContainsKey(part)) _order.Add(part);
                _parts[part] = ms.ToArray();
            }
        }

        internal string SharedString(int index)
        {
            return index >= 0 && index < _sharedStrings.Count ? _sharedStrings[index] : "";
        }

        public IList<string> SheetNames
        {
            get { return _workbook.Root.Element(Main + "sheets").Elements(Main + "sheet").Select(s => (string)s.Attribute("name")).ToList(); }
        }

        /// <summary>Именованный диапазон книги: лист и ячейка (A1) первой ячейки; false, если имени нет.</summary>
        public bool TryResolveName(string name, out string sheet, out string cell)
        {
            sheet = cell = null;
            XElement names = _workbook.Root.Element(Main + "definedNames");
            if (names == null) return false;
            XElement dn = names.Elements(Main + "definedName").FirstOrDefault(d =>
                d.Attribute("localSheetId") == null && string.Equals((string)d.Attribute("name"), name, StringComparison.Ordinal));
            if (dn == null) return false;
            Match m = Regex.Match(dn.Value.Trim(), @"^(?:'((?:[^']|'')+)'|([^!]+))!\$?([A-Z]{1,3})\$?(\d+)");
            if (!m.Success) return false;
            sheet = m.Groups[1].Success ? m.Groups[1].Value.Replace("''", "'") : m.Groups[2].Value;
            cell = m.Groups[3].Value + m.Groups[4].Value;
            return true;
        }

        public XlsxSheet Sheet(string name)
        {
            XlsxSheet sheet;
            if (_sheets.TryGetValue(name, out sheet)) return sheet;
            XElement node = _workbook.Root.Element(Main + "sheets").Elements(Main + "sheet")
                .FirstOrDefault(s => string.Equals((string)s.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
            if (node == null) return null;
            string part = PartOf((string)node.Attribute(Rel + "id"));
            sheet = new XlsxSheet(this, part, Load(part));
            _sheets[name] = sheet;
            return sheet;
        }

        private string PartOf(string relId)
        {
            XElement rel = _workbookRels.Root.Elements(PackageRel + "Relationship").First(r => (string)r.Attribute("Id") == relId);
            string target = ((string)rel.Attribute("Target")).Replace('\\', '/');
            return target.StartsWith("/") ? target.Substring(1) : "xl/" + target;
        }

        /// <summary>Новый пустой лист в конце книги.</summary>
        public XlsxSheet AddSheet(string name)
        {
            if (SheetNames.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Лист «" + name + "» уже есть в книге");
            // Листы, добавленные до Save, ещё не в _parts — их части занимают номера тоже.
            int n = 1;
            while (_parts.ContainsKey("xl/worksheets/sheet" + n + ".xml") ||
                   _sheets.Values.Any(x => string.Equals(x.Part, "xl/worksheets/sheet" + n + ".xml", StringComparison.OrdinalIgnoreCase))) n++;
            string part = "xl/worksheets/sheet" + n + ".xml";
            int rid = 1;
            while (_workbookRels.Root.Elements(PackageRel + "Relationship").Any(r => (string)r.Attribute("Id") == "rId" + rid)) rid++;
            _workbookRels.Root.Add(new XElement(PackageRel + "Relationship",
                new XAttribute("Id", "rId" + rid), new XAttribute("Type", WorksheetType), new XAttribute("Target", "worksheets/sheet" + n + ".xml")));
            XElement sheets = _workbook.Root.Element(Main + "sheets");
            int sheetId = sheets.Elements(Main + "sheet").Select(s => (int)s.Attribute("sheetId")).DefaultIfEmpty(0).Max() + 1;
            sheets.Add(new XElement(Main + "sheet", new XAttribute("name", name), new XAttribute("sheetId", sheetId),
                new XAttribute(Rel + "id", "rId" + rid)));
            _contentTypes.Root.Add(new XElement(ContentTypes + "Override",
                new XAttribute("PartName", "/" + part), new XAttribute("ContentType", WorksheetContent)));
            XDocument doc = new XDocument(new XElement(Main + "worksheet",
                new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName),
                new XElement(Main + "sheetData"),
                new XElement(Main + "pageMargins", new XAttribute("left", "0.4"), new XAttribute("right", "0.4"),
                    new XAttribute("top", "0.6"), new XAttribute("bottom", "0.6"), new XAttribute("header", "0.3"), new XAttribute("footer", "0.3")),
                new XElement(Main + "pageSetup", new XAttribute("paperSize", "9"), new XAttribute("orientation", "portrait"))));
            XlsxSheet sheet = new XlsxSheet(this, part, doc) { Changed = true };
            _sheets[name] = sheet;
            return sheet;
        }

        /// <summary>Стиль ячейки: жирный шрифт, тонкие рамки, перенос по словам. Возвращает индекс в cellXfs.</summary>
        public int AddStyle(bool bold, bool border, bool wrap)
        {
            return AddStyle(new XlsxStyle { Bold = bold, Border = border, Wrap = wrap });
        }

        /// <summary>Стиль ячейки по описанию: шрифт, рамки, выравнивание, заливка, числовой формат.</summary>
        public int AddStyle(XlsxStyle style)
        {
            if (style == null) style = new XlsxStyle();
            string key = string.Join("|", new[]
            {
                style.Bold ? "b" : "", style.Border ? "r" : "", style.Wrap ? "w" : "", style.Gray ? "g" : "",
                style.Size.ToString(CultureInfo.InvariantCulture), style.Fill ?? "", style.Horizontal ?? "",
                style.Vertical ?? "", style.NumberFormat ?? ""
            });
            int known;
            if (_styleIndex.TryGetValue(key, out known)) return known;
            if (_styles == null) _styles = Load("xl/styles.xml");
            XElement root = _styles.Root;
            int fontId = 0;
            if (style.Bold || style.Size > 0 || style.Gray)
            {
                XElement fonts = root.Element(Main + "fonts");
                XElement font = new XElement(fonts.Elements(Main + "font").First());
                font.Elements(Main + "b").Remove();
                font.Elements(Main + "color").Remove();
                if (style.Bold) font.AddFirst(new XElement(Main + "b"));
                if (style.Size > 0)
                {
                    font.Elements(Main + "sz").Remove();
                    font.Add(new XElement(Main + "sz", new XAttribute("val", style.Size.ToString(CultureInfo.InvariantCulture))));
                }
                if (style.Gray) font.Add(new XElement(Main + "color", new XAttribute("rgb", "FF595959")));
                fonts.Add(font);
                fontId = fonts.Elements(Main + "font").Count() - 1;
                fonts.SetAttributeValue("count", fontId + 1);
            }
            int borderId = 0;
            if (style.Border)
            {
                XElement borders = root.Element(Main + "borders");
                XElement thin = new XElement(Main + "border");
                foreach (string side in new[] { "left", "right", "top", "bottom" })
                    thin.Add(new XElement(Main + side, new XAttribute("style", "thin"), new XElement(Main + "color", new XAttribute("indexed", "64"))));
                thin.Add(new XElement(Main + "diagonal"));
                borders.Add(thin);
                borderId = borders.Elements(Main + "border").Count() - 1;
                borders.SetAttributeValue("count", borderId + 1);
            }
            int fillId = 0;
            if (style.Fill != null && style.Fill.Length > 0)
            {
                XElement fills = root.Element(Main + "fills");
                fills.Add(new XElement(Main + "fill",
                    new XElement(Main + "patternFill", new XAttribute("patternType", "solid"),
                        new XElement(Main + "fgColor", new XAttribute("rgb", style.Fill)),
                        new XElement(Main + "bgColor", new XAttribute("indexed", "64")))));
                fillId = fills.Elements(Main + "fill").Count() - 1;
                fills.SetAttributeValue("count", fillId + 1);
            }
            int numFmtId = 0;
            if (style.NumberFormat != null && style.NumberFormat.Length > 0)
            {
                XElement formats = root.Element(Main + "numFmts");
                if (formats == null)
                {
                    formats = new XElement(Main + "numFmts");
                    root.AddFirst(formats);
                }
                numFmtId = formats.Elements(Main + "numFmt").Select(f => (int)f.Attribute("numFmtId")).DefaultIfEmpty(163).Max() + 1;
                formats.Add(new XElement(Main + "numFmt", new XAttribute("numFmtId", numFmtId),
                    new XAttribute("formatCode", style.NumberFormat)));
                formats.SetAttributeValue("count", formats.Elements(Main + "numFmt").Count());
            }
            XElement xfs = root.Element(Main + "cellXfs");
            XElement xf = new XElement(Main + "xf", new XAttribute("numFmtId", numFmtId), new XAttribute("fontId", fontId),
                new XAttribute("fillId", fillId), new XAttribute("borderId", borderId), new XAttribute("xfId", 0));
            if (fontId > 0) xf.SetAttributeValue("applyFont", 1);
            if (borderId > 0) xf.SetAttributeValue("applyBorder", 1);
            if (fillId > 0) xf.SetAttributeValue("applyFill", 1);
            if (numFmtId > 0) xf.SetAttributeValue("applyNumberFormat", 1);
            if (style.Wrap || style.Horizontal != null)
            {
                xf.SetAttributeValue("applyAlignment", 1);
                XElement alignment = new XElement(Main + "alignment", new XAttribute("vertical", style.Vertical ?? "center"));
                if (style.Horizontal != null) alignment.SetAttributeValue("horizontal", style.Horizontal);
                if (style.Wrap) alignment.SetAttributeValue("wrapText", 1);
                xf.Add(alignment);
            }
            xfs.Add(xf);
            int index = xfs.Elements(Main + "xf").Count() - 1;
            xfs.SetAttributeValue("count", index + 1);
            _stylesChanged = true;
            _styleIndex[key] = index;
            return index;
        }

        /// <summary>Шрифт всей книги: шаблон SWTools собран из китайского образца (宋体), кириллица в нём выглядит чужой.</summary>
        public void NormalizeFonts(string face)
        {
            if (_styles == null) _styles = Load("xl/styles.xml");
            foreach (XElement font in _styles.Root.Element(Main + "fonts").Elements(Main + "font"))
            {
                XElement name = font.Element(Main + "name");
                if (name == null) font.Add(new XElement(Main + "name", new XAttribute("val", face)));
                else name.SetAttributeValue("val", face);
                XElement charset = font.Element(Main + "charset");
                if (charset == null) font.Add(new XElement(Main + "charset", new XAttribute("val", "204")));
                else charset.SetAttributeValue("val", "204");
                XElement family = font.Element(Main + "family");
                if (family == null) font.Add(new XElement(Main + "family", new XAttribute("val", "2")));
                else family.SetAttributeValue("val", "2");
            }
            _stylesChanged = true;
        }

        /// <summary>Печать листа: область, повторяемая шапка (строка) — именованными диапазонами книги.</summary>
        public void SetPrintNames(string sheetName, string printArea, int repeatRow)
        {
            int index = SheetIndex(sheetName);
            if (index < 0) return;
            XElement names = WorkbookSection("definedNames");
            string quoted = Quote(sheetName);
            Define(names, index, "_xlnm.Print_Area", quoted + "!" + printArea);
            if (repeatRow > 0)
                Define(names, index, "_xlnm.Print_Titles", quoted + "!$" + repeatRow + ":$" + repeatRow);
        }

        // Порядок дочерних элементов книги задан схемой, как и у листа.
        private static readonly string[] WorkbookOrder =
        {
            "fileVersion", "fileSharing", "workbookPr", "workbookProtection", "bookViews", "sheets", "functionGroups",
            "externalReferences", "definedNames", "calcPr", "oleSize", "customWorkbookViews", "pivotCaches", "smartTagPr",
            "smartTagTypes", "webPublishing", "fileRecoveryPr", "webPublishObjects", "extLst"
        };

        private XElement WorkbookSection(string name)
        {
            XElement existing = _workbook.Root.Element(Main + name);
            if (existing != null) return existing;
            XElement created = new XElement(Main + name);
            int index = Array.IndexOf(WorkbookOrder, name);
            XElement after = _workbook.Root.Elements()
                .LastOrDefault(e => Array.IndexOf(WorkbookOrder, e.Name.LocalName) >= 0 && Array.IndexOf(WorkbookOrder, e.Name.LocalName) < index);
            if (after != null) after.AddAfterSelf(created);
            else _workbook.Root.AddFirst(created);
            return created;
        }

        private static string Quote(string sheetName)
        {
            return "'" + (sheetName ?? "").Replace("'", "''") + "'";
        }

        private int SheetIndex(string sheetName)
        {
            return _workbook.Root.Element(Main + "sheets").Elements(Main + "sheet").ToList()
                .FindIndex(s => string.Equals((string)s.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Имя книги на одну ячейку, например «Тираж» → 'Паспорт'!$B$9: формулы других листов ссылаются на имя,
        /// а не на адрес, и не ломаются, если строку в «Паспорте» передвинут.
        /// </summary>
        public void DefineName(string name, string sheetName, string cell)
        {
            int column, row;
            ParseCell(cell, out column, out row);
            string letters = CellName(column, 1);
            letters = letters.Substring(0, letters.Length - 1);
            XElement names = WorkbookSection("definedNames");
            names.Elements(Main + "definedName")
                .Where(n => n.Attribute("localSheetId") == null && string.Equals((string)n.Attribute("name"), name, StringComparison.Ordinal))
                .Remove();
            names.Add(new XElement(Main + "definedName", new XAttribute("name", name),
                Quote(sheetName) + "!$" + letters + "$" + row.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>Пересчитать формулы при открытии: книга пишется без Excel, значений формул в файле нет.</summary>
        public void RecalculateOnOpen()
        {
            WorkbookSection("calcPr").SetAttributeValue("fullCalcOnLoad", 1);
        }

        /// <summary>Автофильтр по диапазону с шапкой (A6:M40): сортировка и отбор строк в Excel.</summary>
        public void SetAutoFilter(string sheetName, string range)
        {
            XlsxSheet sheet = Sheet(sheetName);
            int index = SheetIndex(sheetName);
            if (sheet == null || index < 0) return;
            sheet.AutoFilter(range);
            string[] ends = range.Split(':');
            int c1, r1, c2, r2;
            ParseCell(ends[0], out c1, out r1);
            ParseCell(ends.Length > 1 ? ends[1] : ends[0], out c2, out r2);
            string absolute = "$" + Letters(c1) + "$" + r1 + ":$" + Letters(c2) + "$" + r2;
            XElement names = WorkbookSection("definedNames");
            Define(names, index, "_xlnm._FilterDatabase", Quote(sheetName) + "!" + absolute);
            names.Elements(Main + "definedName")
                .Where(n => (string)n.Attribute("name") == "_xlnm._FilterDatabase" && (int?)n.Attribute("localSheetId") == index)
                .ToList().ForEach(n => n.SetAttributeValue("hidden", 1));
        }

        private static string Letters(int column)
        {
            string name = CellName(column, 1);
            return name.Substring(0, name.Length - 1);
        }

        /// <summary>
        /// Переставить лист на место <paramref name="position"/> (0 — первый). Ссылки имён уровня листа
        /// (область печати, шапка, фильтр) хранят номер листа — они переписываются вслед за листами.
        /// </summary>
        public void MoveSheet(string sheetName, int position)
        {
            XElement sheets = _workbook.Root.Element(Main + "sheets");
            List<XElement> list = sheets.Elements(Main + "sheet").ToList();
            int from = SheetIndex(sheetName);
            if (from < 0) return;
            position = Math.Max(0, Math.Min(position, list.Count - 1));
            if (from == position) return;
            List<XElement> reordered = new List<XElement>(list);
            XElement moved = reordered[from];
            reordered.RemoveAt(from);
            reordered.Insert(position, moved);
            Dictionary<int, int> map = new Dictionary<int, int>();
            for (int i = 0; i < reordered.Count; i++) map[list.IndexOf(reordered[i])] = i;
            sheets.ReplaceNodes(reordered.Select(e => new XElement(e)).ToArray());
            XElement names = _workbook.Root.Element(Main + "definedNames");
            if (names != null)
                foreach (XElement n in names.Elements(Main + "definedName"))
                {
                    int? local = (int?)n.Attribute("localSheetId");
                    if (local.HasValue && map.ContainsKey(local.Value)) n.SetAttributeValue("localSheetId", map[local.Value]);
                }
            XElement view = _workbook.Root.Element(Main + "bookViews") == null ? null
                : _workbook.Root.Element(Main + "bookViews").Element(Main + "workbookView");
            if (view != null)
            {
                int? active = (int?)view.Attribute("activeTab");
                if (active.HasValue && map.ContainsKey(active.Value)) view.SetAttributeValue("activeTab", map[active.Value]);
                view.SetAttributeValue("firstSheet", 0);
            }
        }

        /// <summary>Лист, на котором открывается книга; выделение вкладки снимается с остальных (иначе Excel их группирует).</summary>
        public void SetActiveSheet(string sheetName)
        {
            int index = SheetIndex(sheetName);
            if (index < 0) return;
            XElement views = WorkbookSection("bookViews");
            XElement view = views.Element(Main + "workbookView");
            if (view == null)
            {
                view = new XElement(Main + "workbookView");
                views.Add(view);
            }
            view.SetAttributeValue("activeTab", index);
            foreach (string name in SheetNames)
            {
                XlsxSheet sheet = Sheet(name);
                if (sheet != null) sheet.SelectTab(string.Equals(name, sheetName, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        private static readonly XNamespace DrawingMain = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private const long EmuPerPixel = 9525;
        private const long EmuPerPoint = 12700;

        /// <summary>
        /// Эскизы SWTools привязаны от своей ячейки до угла следующей: Excel считает такой рисунок вылезающим
        /// из строки и при сортировке оставляет его на месте — эскизы перепутываются со строками (проверка
        /// 18.09.2026: 20 из 20). Рисунок вписывается внутрь своей ячейки с сохранением пропорций — тогда
        /// «перемещать вместе с ячейками» работает и при сортировке. Возвращает число пересаженных рисунков.
        /// </summary>
        public int FitPicturesToCells(string sheetName)
        {
            XlsxSheet sheet = Sheet(sheetName);
            if (sheet == null) return 0;
            string relsPart = RelsOf(sheet.Part);
            if (!_parts.ContainsKey(relsPart)) return 0;
            XElement drawingRel = Load(relsPart).Root.Elements(PackageRel + "Relationship")
                .FirstOrDefault(r => ((string)r.Attribute("Type") ?? "").EndsWith("/drawing", StringComparison.Ordinal));
            if (drawingRel == null) return 0;
            string drawingPart = Resolve(sheet.Part, (string)drawingRel.Attribute("Target"));
            if (!_parts.ContainsKey(drawingPart)) return 0;
            XDocument drawing = Load(drawingPart);
            Dictionary<string, string> images = new Dictionary<string, string>();
            string drawingRels = RelsOf(drawingPart);
            if (_parts.ContainsKey(drawingRels))
                foreach (XElement r in Load(drawingRels).Root.Elements(PackageRel + "Relationship"))
                    images[(string)r.Attribute("Id")] = Resolve(drawingPart, (string)r.Attribute("Target"));
            int moved = 0;
            foreach (XElement anchor in drawing.Root.Elements(Drawing + "twoCellAnchor").ToList())
            {
                XElement pic = anchor.Element(Drawing + "pic");
                XElement from = anchor.Element(Drawing + "from");
                XElement to = anchor.Element(Drawing + "to");
                if (pic == null || from == null || to == null) continue;
                int col = (int)from.Element(Drawing + "col");
                int row = (int)from.Element(Drawing + "row");
                long cellWidth = (long)(sheet.ColumnWidthPixels(col + 1) * EmuPerPixel);
                long cellHeight = (long)(sheet.RowHeightPoints(row + 1) * EmuPerPoint);
                // Поле по 2 px с каждой стороны и запас 5 %: оценка ширины колонки в пикселях приблизительна.
                long margin = 2 * EmuPerPixel;
                long boxW = (long)((cellWidth - 2 * margin) * 0.95), boxH = (long)((cellHeight - 2 * margin) * 0.95);
                if (boxW <= 0 || boxH <= 0) continue;
                double imgW = 4, imgH = 3;
                XElement blip = pic.Descendants(DrawingMain + "blip").FirstOrDefault();
                string embed = blip == null ? null : (string)blip.Attribute(Rel + "embed");
                string image;
                if (embed != null && images.TryGetValue(embed, out image)) PngSize(image, ref imgW, ref imgH);
                double scale = Math.Min(boxW / imgW, boxH / imgH);
                long w = (long)(imgW * scale), h = (long)(imgH * scale);
                long left = margin + (cellWidth - 2 * margin - w) / 2, top = margin + (cellHeight - 2 * margin - h) / 2;
                SetAnchor(from, col, left, row, top);
                SetAnchor(to, col, left + w, row, top + h);
                anchor.SetAttributeValue("editAs", "twoCell");
                XElement ext = pic.Descendants(DrawingMain + "ext").FirstOrDefault();
                if (ext != null)
                {
                    ext.SetAttributeValue("cx", w);
                    ext.SetAttributeValue("cy", h);
                }
                moved++;
            }
            if (moved > 0) Store(drawingPart, drawing);
            return moved;
        }

        private static void SetAnchor(XElement point, int col, long colOff, int row, long rowOff)
        {
            point.SetElementValue(Drawing + "col", col);
            point.SetElementValue(Drawing + "colOff", colOff);
            point.SetElementValue(Drawing + "row", row);
            point.SetElementValue(Drawing + "rowOff", rowOff);
        }

        /// <summary>Размер PNG из заголовка IHDR; не PNG — размеры не меняются.</summary>
        private void PngSize(string part, ref double width, ref double height)
        {
            byte[] data;
            if (!_parts.TryGetValue(part, out data) || data.Length < 24 || data[1] != 'P' || data[2] != 'N' || data[3] != 'G') return;
            int w = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            int h = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
            if (w > 0 && h > 0)
            {
                width = w;
                height = h;
            }
        }

        private static string RelsOf(string part)
        {
            int slash = part.LastIndexOf('/');
            return part.Substring(0, slash + 1) + "_rels/" + part.Substring(slash + 1) + ".rels";
        }

        /// <summary>Относительная ссылка части («../drawings/drawing1.xml») → путь в архиве.</summary>
        private static string Resolve(string fromPart, string target)
        {
            target = (target ?? "").Replace('\\', '/');
            if (target.StartsWith("/")) return target.Substring(1);
            List<string> segments = fromPart.Split('/').ToList();
            segments.RemoveAt(segments.Count - 1);
            foreach (string s in target.Split('/'))
            {
                if (s == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
                else if (s != "." && s.Length > 0) segments.Add(s);
            }
            return string.Join("/", segments.ToArray());
        }

        private static void Define(XElement names, int sheetIndex, string name, string value)
        {
            names.Elements(Main + "definedName")
                .Where(n => (string)n.Attribute("name") == name && (int?)n.Attribute("localSheetId") == sheetIndex).Remove();
            names.Add(new XElement(Main + "definedName", new XAttribute("name", name),
                new XAttribute("localSheetId", sheetIndex), value));
        }

        /// <summary>Записать книгу: во временный файл рядом, затем заменить исходный.</summary>
        public void Save()
        {
            foreach (XlsxSheet sheet in _sheets.Values)
                if (sheet.Changed) Store(sheet.Part, sheet.Document);
            Store("xl/workbook.xml", _workbook);
            Store("xl/_rels/workbook.xml.rels", _workbookRels);
            Store("[Content_Types].xml", _contentTypes);
            if (_stylesChanged) Store("xl/styles.xml", _styles);
            string temp = _path + ".part";
            using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (string part in _order)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(part, CompressionLevel.Optimal);
                    using (Stream s = entry.Open())
                    {
                        byte[] data = _parts[part];
                        s.Write(data, 0, data.Length);
                    }
                }
            }
            Replace(temp, _path);
        }

        /// <summary>Готовый файл встаёт на место целиком: обрыв сети посреди записи не оставляет полкниги.</summary>
        private static void Replace(string temp, string path)
        {
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }
            try
            {
                File.Replace(temp, path, null, true);
            }
            catch (PlatformNotSupportedException)
            {
                // Файловая система без замены (часть NAS): копия поверх — как раньше.
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }

        /// <summary>Номер колонки (1 — A) и строки из ссылки A1.</summary>
        public static void ParseCell(string cell, out int column, out int row)
        {
            Match m = Regex.Match(cell ?? "", @"^\$?([A-Za-z]{1,3})\$?(\d+)$");
            if (!m.Success) throw new ArgumentException("Не ссылка на ячейку: " + cell);
            column = 0;
            foreach (char c in m.Groups[1].Value.ToUpperInvariant()) column = column * 26 + (c - 'A' + 1);
            row = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        public static string CellName(int column, int row)
        {
            string letters = "";
            for (int c = column; c > 0; c = (c - 1) / 26) letters = (char)('A' + (c - 1) % 26) + letters;
            return letters + row.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Описание стиля ячейки для <see cref="XlsxBook.AddStyle(XlsxStyle)"/>.</summary>
    public sealed class XlsxStyle
    {
        public bool Bold;
        public bool Border;
        public bool Wrap;
        public bool Gray;
        public double Size;
        /// <summary>Заливка ARGB, например FFF2F2F2.</summary>
        public string Fill;
        /// <summary>left / center / right; null — по умолчанию.</summary>
        public string Horizontal;
        /// <summary>top / center / bottom; по умолчанию center.</summary>
        public string Vertical;
        /// <summary>Числовой формат, например 0.000.</summary>
        public string NumberFormat;
    }

    public sealed class XlsxSheet
    {
        // Порядок дочерних элементов листа задан схемой: элемент не на своём месте Excel считает повреждением файла.
        private static readonly string[] ChildOrder =
        {
            "sheetPr", "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "sheetCalcPr", "sheetProtection",
            "protectedRanges", "scenarios", "autoFilter", "sortState", "dataConsolidate", "customSheetViews", "mergeCells",
            "phoneticPr", "conditionalFormatting", "dataValidations", "hyperlinks", "printOptions", "pageMargins",
            "pageSetup", "headerFooter"
        };

        private readonly XlsxBook _book;
        private readonly XElement _data;

        internal XlsxSheet(XlsxBook book, string part, XDocument doc)
        {
            _book = book;
            Part = part;
            Document = doc;
            _data = doc.Root.Element(XlsxBook.Main + "sheetData");
            if (_data == null) throw new InvalidDataException("Лист без sheetData: " + part);
        }

        internal string Part { get; private set; }
        internal XDocument Document { get; private set; }
        internal bool Changed { get; set; }

        /// <summary>Номера строк, в которых есть хотя бы одна ячейка.</summary>
        public IList<int> RowNumbers
        {
            get { return _data.Elements(XlsxBook.Main + "row").Select(r => (int)r.Attribute("r")).ToList(); }
        }

        /// <summary>Текст значения ячейки (число — в записи файла), пустая строка, если ячейки нет.</summary>
        public string Get(string cell)
        {
            XElement c = Find(cell, false);
            if (c == null) return "";
            string type = (string)c.Attribute("t") ?? "";
            if (type == "inlineStr")
            {
                XElement inline = c.Element(XlsxBook.Main + "is");
                return inline == null ? "" : string.Concat(inline.Descendants(XlsxBook.Main + "t").Select(t => t.Value));
            }
            XElement v = c.Element(XlsxBook.Main + "v");
            if (v == null) return "";
            if (type == "s")
            {
                int index;
                return int.TryParse(v.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) ? _book.SharedString(index) : "";
            }
            return v.Value;
        }

        public string Get(int column, int row)
        {
            return Get(XlsxBook.CellName(column, row));
        }

        public void SetText(string cell, string text, int style = -1)
        {
            XElement c = Find(cell, true);
            c.RemoveNodes();
            c.SetAttributeValue("t", "inlineStr");
            if (style >= 0) c.SetAttributeValue("s", style);
            // Управляющие символы (бывают в свойствах SolidWorks) XML запрещает: запись книги упала бы целиком.
            text = XmlSafe(text ?? "");
            XElement t = new XElement(XlsxBook.Main + "t", text);
            if (text.Trim() != text) t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            c.Add(new XElement(XlsxBook.Main + "is", t));
            Changed = true;
        }

        public void SetNumber(string cell, double value, int style = -1)
        {
            // «NaN» и «Infinity» в <v> — повреждённая книга: Excel откроет её только с восстановлением.
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                SetText(cell, "", style);
                return;
            }
            XElement c = Find(cell, true);
            c.RemoveNodes();
            c.SetAttributeValue("t", null);
            if (style >= 0) c.SetAttributeValue("s", style);
            c.Add(new XElement(XlsxBook.Main + "v", value.ToString("R", CultureInfo.InvariantCulture)));
            Changed = true;
        }

        public void SetColumnWidth(int column, double width)
        {
            XElement cols = Document.Root.Element(XlsxBook.Main + "cols");
            if (cols == null)
            {
                cols = new XElement(XlsxBook.Main + "cols");
                _data.AddBeforeSelf(cols);
            }
            // Диапазоны <col> обязаны не пересекаться и идти по возрастанию (схема): колонка внутри диапазона шаблона
            // («13…16384») вырезается из него, иначе Excel открывает книгу с «восстановлением сведений о столбцах».
            foreach (XElement range in cols.Elements(XlsxBook.Main + "col").ToList())
            {
                int min = (int)range.Attribute("min"), max = (int)range.Attribute("max");
                if (column < min || column > max) continue;
                if (min < column) range.AddBeforeSelf(CopyRange(range, min, column - 1));
                if (column < max) range.AddAfterSelf(CopyRange(range, column + 1, max));
                range.Remove();
            }
            XElement fresh = new XElement(XlsxBook.Main + "col", new XAttribute("min", column), new XAttribute("max", column),
                new XAttribute("width", width.ToString(CultureInfo.InvariantCulture)), new XAttribute("customWidth", 1));
            XElement next = cols.Elements(XlsxBook.Main + "col").FirstOrDefault(c => (int)c.Attribute("min") > column);
            if (next != null) next.AddBeforeSelf(fresh);
            else cols.Add(fresh);
            Changed = true;
        }

        private static XElement CopyRange(XElement range, int min, int max)
        {
            XElement copy = new XElement(range);
            copy.SetAttributeValue("min", min);
            copy.SetAttributeValue("max", max);
            return copy;
        }

        /// <summary>Текст без символов, запрещённых в XML 1.0 (кроме табуляции и переводов строки).</summary>
        internal static string XmlSafe(string text)
        {
            StringBuilder sb = null;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                bool bad = (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') || ch == '\uFFFE' || ch == '\uFFFF';
                if (bad && sb == null) sb = new StringBuilder(text, 0, i, text.Length);
                if (!bad && sb != null) sb.Append(ch);
            }
            return sb == null ? text : sb.ToString();
        }

        /// <summary>Объединить ячейки, например A1:F1.</summary>
        public void Merge(string range)
        {
            XElement merges = Section("mergeCells");
            merges.Elements(XlsxBook.Main + "mergeCell").Where(m => (string)m.Attribute("ref") == range).Remove();
            merges.Add(new XElement(XlsxBook.Main + "mergeCell", new XAttribute("ref", range)));
            merges.SetAttributeValue("count", merges.Elements(XlsxBook.Main + "mergeCell").Count());
            Changed = true;
        }

        /// <summary>Высота строки в пунктах.</summary>
        public void SetRowHeight(int row, double height)
        {
            XElement c = Find(XlsxBook.CellName(1, row), true);
            XElement rowNode = c.Parent;
            rowNode.SetAttributeValue("ht", height.ToString(CultureInfo.InvariantCulture));
            rowNode.SetAttributeValue("customHeight", 1);
            if (!c.HasElements && c.Attribute("t") == null) c.Remove();
            Changed = true;
        }

        /// <summary>Закрепить строки выше указанной ячейки (шапка остаётся на экране при прокрутке).</summary>
        public void FreezeRowsAbove(string cell)
        {
            int column, row;
            XlsxBook.ParseCell(cell, out column, out row);
            XElement views = Section("sheetViews");
            XElement view = views.Element(XlsxBook.Main + "sheetView");
            if (view == null)
            {
                view = new XElement(XlsxBook.Main + "sheetView", new XAttribute("workbookViewId", 0));
                views.Add(view);
            }
            view.Elements(XlsxBook.Main + "pane").Remove();
            view.AddFirst(new XElement(XlsxBook.Main + "pane", new XAttribute("ySplit", row - 1),
                new XAttribute("topLeftCell", cell), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")));
            Changed = true;
        }

        /// <summary>Формула без кэша значения (<see cref="XlsxBook.RecalculateOnOpen"/>): английские имена функций, «;» не годится — «,».</summary>
        public void SetFormula(string cell, string formula, int style = -1)
        {
            XElement c = Find(cell, true);
            c.RemoveNodes();
            c.SetAttributeValue("t", null);
            if (style >= 0) c.SetAttributeValue("s", style);
            string text = (formula ?? "").Trim();
            if (text.StartsWith("=")) text = text.Substring(1);
            c.Add(new XElement(XlsxBook.Main + "f", text));
            Changed = true;
        }

        /// <summary>Формула ячейки (без «=») или пустая строка.</summary>
        public string Formula(string cell)
        {
            XElement c = Find(cell, false);
            XElement f = c == null ? null : c.Element(XlsxBook.Main + "f");
            return f == null ? "" : f.Value;
        }

        /// <summary>Индекс стиля ячейки; -1 — ячейки нет или стиль по умолчанию.</summary>
        public int StyleOf(string cell)
        {
            XElement c = Find(cell, false);
            int? s = c == null ? null : (int?)c.Attribute("s");
            return s ?? -1;
        }

        /// <summary>Только целое число не меньше <paramref name="min"/> (ячейка ввода тиража).</summary>
        public void ValidateWholeNumber(string cell, int min, string message)
        {
            XElement list = Section("dataValidations");
            list.Elements(XlsxBook.Main + "dataValidation").Where(v => (string)v.Attribute("sqref") == cell).Remove();
            list.Add(new XElement(XlsxBook.Main + "dataValidation", new XAttribute("type", "whole"),
                new XAttribute("operator", "greaterThanOrEqual"), new XAttribute("allowBlank", 1),
                new XAttribute("showErrorMessage", 1), new XAttribute("errorTitle", "Недопустимое значение"),
                new XAttribute("error", message ?? ""), new XAttribute("sqref", cell),
                new XElement(XlsxBook.Main + "formula1", min.ToString(CultureInfo.InvariantCulture))));
            list.SetAttributeValue("count", list.Elements(XlsxBook.Main + "dataValidation").Count());
            Changed = true;
        }

        internal void AutoFilter(string range)
        {
            Section("autoFilter").SetAttributeValue("ref", range);
            Changed = true;
        }

        /// <summary>Выделить вкладку листа или снять выделение.</summary>
        internal void SelectTab(bool selected)
        {
            XElement views = Document.Root.Element(XlsxBook.Main + "sheetViews");
            XElement view = views == null ? null : views.Element(XlsxBook.Main + "sheetView");
            if (!selected)
            {
                if (view != null && view.Attribute("tabSelected") != null)
                {
                    view.SetAttributeValue("tabSelected", null);
                    Changed = true;
                }
                return;
            }
            if (view == null)
            {
                views = Section("sheetViews");
                view = new XElement(XlsxBook.Main + "sheetView", new XAttribute("workbookViewId", 0));
                views.Add(view);
            }
            view.SetAttributeValue("tabSelected", 1);
            Changed = true;
        }

        /// <summary>Ширина колонки в пикселях (шрифт по умолчанию, 7 px на знак).</summary>
        internal double ColumnWidthPixels(int column)
        {
            XElement cols = Document.Root.Element(XlsxBook.Main + "cols");
            if (cols != null)
                foreach (XElement c in cols.Elements(XlsxBook.Main + "col"))
                    if ((int)c.Attribute("min") <= column && column <= (int)c.Attribute("max"))
                        return (double?)c.Attribute("width") * 7 ?? 64;
            XElement format = Document.Root.Element(XlsxBook.Main + "sheetFormatPr");
            double? width = format == null ? null : (double?)format.Attribute("defaultColWidth");
            return width.HasValue ? width.Value * 7 : 64;
        }

        /// <summary>Высота строки в пунктах: своя, иначе по умолчанию листа, иначе 15.</summary>
        internal double RowHeightPoints(int row)
        {
            XElement node = _data.Elements(XlsxBook.Main + "row").FirstOrDefault(r => (int)r.Attribute("r") == row);
            double? height = node == null ? null : (double?)node.Attribute("ht");
            if (height.HasValue) return height.Value;
            XElement format = Document.Root.Element(XlsxBook.Main + "sheetFormatPr");
            double? fallback = format == null ? null : (double?)format.Attribute("defaultRowHeight");
            return fallback ?? 15;
        }

        /// <summary>A4, книжная, вписать в одну страницу по ширине.</summary>
        public void FitToWidth()
        {
            FitToWidth(false);
        }

        /// <summary>A4, книжная или альбомная, вписать в одну страницу по ширине.</summary>
        public void FitToWidth(bool landscape)
        {
            XElement properties = Section("sheetPr");
            properties.Elements(XlsxBook.Main + "pageSetUpPr").Remove();
            properties.Add(new XElement(XlsxBook.Main + "pageSetUpPr", new XAttribute("fitToPage", 1)));
            XElement setup = Section("pageSetup");
            setup.SetAttributeValue("paperSize", 9);
            setup.SetAttributeValue("orientation", landscape ? "landscape" : "portrait");
            setup.SetAttributeValue("fitToWidth", 1);
            setup.SetAttributeValue("fitToHeight", 0);
            Changed = true;
        }

        /// <summary>Существующий или созданный на своём месте по схеме элемент листа.</summary>
        private XElement Section(string name)
        {
            XElement existing = Document.Root.Element(XlsxBook.Main + name);
            if (existing != null) return existing;
            XElement created = new XElement(XlsxBook.Main + name);
            int index = Array.IndexOf(ChildOrder, name);
            XElement after = Document.Root.Elements()
                .LastOrDefault(e => Array.IndexOf(ChildOrder, e.Name.LocalName) >= 0 && Array.IndexOf(ChildOrder, e.Name.LocalName) < index);
            if (after != null) after.AddAfterSelf(created);
            else Document.Root.AddFirst(created);
            return created;
        }

        // Индекс строк: книга ЛЗК на сотни строк заполняется по ячейке, и полный проход по строкам с Regex на каждую
        // ячейку давал десятки секунд. Индекс только ускоряет: найденная строка перепроверяется, не найденная ищется как раньше.
        private readonly Dictionary<int, XElement> _rows = new Dictionary<int, XElement>();

        private XElement IndexedRow(int row)
        {
            XElement node;
            if (_rows.TryGetValue(row, out node) && node.Parent == _data && (int)node.Attribute("r") == row) return node;
            _rows.Remove(row);
            return null;
        }

        /// <summary>Номер колонки по ссылке «AB12» без регулярных выражений.</summary>
        private static int ColumnOf(string reference)
        {
            int column = 0;
            foreach (char ch in reference ?? "")
            {
                if (ch == '$') continue;
                char u = char.ToUpperInvariant(ch);
                if (u < 'A' || u > 'Z') break;
                column = column * 26 + (u - 'A' + 1);
            }
            return column;
        }

        private XElement Find(string cell, bool create)
        {
            int column, row;
            XlsxBook.ParseCell(cell, out column, out row);
            string name = XlsxBook.CellName(column, row);
            XElement rowNode = IndexedRow(row);
            if (rowNode != null) return FindInRow(rowNode, column, name, create);
            // Частый случай — дописывание строк в конец листа.
            XElement last = _data.Elements(XlsxBook.Main + "row").LastOrDefault();
            if (last != null && (int)last.Attribute("r") < row)
            {
                if (!create) return null;
                rowNode = new XElement(XlsxBook.Main + "row", new XAttribute("r", row));
                last.AddAfterSelf(rowNode);
                _rows[row] = rowNode;
                return FindInRow(rowNode, column, name, true);
            }
            foreach (XElement r in _data.Elements(XlsxBook.Main + "row"))
            {
                int number = (int)r.Attribute("r");
                if (number == row) { rowNode = r; break; }
                if (number > row)
                {
                    if (!create) return null;
                    rowNode = new XElement(XlsxBook.Main + "row", new XAttribute("r", row));
                    r.AddBeforeSelf(rowNode);
                    break;
                }
            }
            if (rowNode == null)
            {
                if (!create) return null;
                rowNode = new XElement(XlsxBook.Main + "row", new XAttribute("r", row));
                _data.Add(rowNode);
            }
            _rows[row] = rowNode;
            return FindInRow(rowNode, column, name, create);
        }

        private static XElement FindInRow(XElement rowNode, int column, string name, bool create)
        {
            foreach (XElement c in rowNode.Elements(XlsxBook.Main + "c"))
            {
                int cc = ColumnOf((string)c.Attribute("r"));
                if (cc == column) return c;
                if (cc > column)
                {
                    if (!create) return null;
                    XElement created = new XElement(XlsxBook.Main + "c", new XAttribute("r", name));
                    c.AddBeforeSelf(created);
                    rowNode.SetAttributeValue("spans", null);
                    return created;
                }
            }
            if (!create) return null;
            XElement added = new XElement(XlsxBook.Main + "c", new XAttribute("r", name));
            rowNode.Add(added);
            rowNode.SetAttributeValue("spans", null);
            return added;
        }
    }
}
