using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using View = SolidWorks.Interop.sldworks.View;
using Environment = System.Environment;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Кнопка К-7 «Новая ревизия» (ТЗ-02 Т-48…Т-52). Ревизия принадлежит чертежу (Р0-8): кнопка поднимает
    /// `Revision` на единицу, заполняет графы таблицы изменений в форматке так же, как DProp, дописывает
    /// строку в `Изменения.xlsx` и переэкспортирует документ с суффиксом `_ИзмN`, убрав прежние файлы
    /// выдачи в `_Аннулировано`. Черновик (документ ещё не выдан) правят свободно — кнопка там не нужна.
    /// </summary>
    public static class RevisionService
    {
        /// <summary>«ok|ревизия|строка журнала|выгружено» или «error|текст» — для автотестов (Т-9).</summary>
        public static string LastOutcome = "";

        /// <summary>Свойство ревизии чертежа — то же, что ведёт DProp.</summary>
        public const string RevisionProperty = "Revision";
        /// <summary>Свойство ревизии безчертёжной детали (словарь SWPlus, строка 53).</summary>
        public const string BchRevisionProperty = PropertyDictionary.RevisionName;
        /// <summary>Графы таблицы изменений форматки: «Лист», «№ докум.», «Дата», «Подп.».</summary>
        public static readonly string[] Notes = { "Revision2", "Revision3", "Revision4", "Revision5" };

        private sealed class Target
        {
            public ModelDoc2 Document;      // чертёж или БЧ-деталь — то, у чего поднимается ревизия
            public ModelDoc2 Model;         // модель, на которую ссылается чертёж
            public string DocumentPath = "";
            public string ModelPath = "";
            public bool IsDrawing;
            public string ProductFolder = "";
            public string Designation = "";
            public string Name = "";
            public int Revision;
        }

        // ------------------------------------------------------------------ доступность (Т-48)
        /// <summary>Причина, по которой кнопка недоступна, или пустая строка.</summary>
        public static string Unavailable(ISldWorks app)
        {
            try
            {
                ModelDoc2 doc = app.ActiveDoc as ModelDoc2;
                if (doc == null) return "нет открытого документа";
                int type = doc.GetType();
                if (type == (int)swDocumentTypes_e.swDocASSEMBLY)
                    return "ревизия принадлежит чертежу, а не сборке: откройте чертёж";
                string format;
                if (type == (int)swDocumentTypes_e.swDocPART && !BchService.State(doc, out format))
                    return "у детали с чертежом ревизию поднимают на чертеже";
                string path = doc.GetPathName() ?? "";
                if (path.Length == 0) return "документ ещё не сохранён";
                return Issued(path, type == (int)swDocumentTypes_e.swDocDRAWING)
                    ? "" : "документ ещё не выдан — правьте свободно";
            }
            catch (COMException)
            {
                return "";
            }
        }

        /// <summary>Документ (или его модель) числится в отчётах выдачи изделия (Т-48).</summary>
        private static bool Issued(string path, bool drawing)
        {
            ProductLocation location = ProductLocator.Locate(path);
            string productFolder = location.ProductFolder.Length > 0 ? location.ProductFolder : LzkNaming.ProductFolder(path);
            if (productFolder.Length == 0) return false;
            HashSet<string> issued = ExportNaming.Issued(productFolder);
            if (issued.Contains(Path.GetFileName(path))) return true;
            if (!drawing) return false;
            // Выдают модели, а чертёж уходит их спутником: ревизию поднимают на чертеже выданной детали.
            foreach (string extension in new[] { ".sldprt", ".sldasm" })
                if (issued.Contains(Path.GetFileNameWithoutExtension(path) + extension)) return true;
            return false;
        }

        // ------------------------------------------------------------------ работа
        public static bool Run(ISldWorks app, bool interactive)
        {
            return Run(app, interactive, null, null, null);
        }

        public static bool Run(ISldWorks app, bool interactive, string what, string code, string backlog)
        {
            LastOutcome = "";
            try
            {
                string unavailable = Unavailable(app);
                if (unavailable.Length > 0)
                {
                    Fail(app, interactive, "Новая ревизия не нужна: " + unavailable + ".");
                    return false;
                }
                // MProp при «Применить» возвращает доп. свойства такими, какими они были при открытии окна
                // (спайк С-1), и молча вернёт прежнюю ревизию — поэтому при открытом окне макроса отказ.
                string macro = MacroWindows.OpenTitle();
                if (macro.Length > 0)
                {
                    Fail(app, interactive, "Открыто окно макроса SWPlus («" + macro +
                        "»). Закройте его и повторите: иначе оно вернёт прежнюю ревизию.");
                    return false;
                }
                Target target = Collect(app);
                if (target == null)
                {
                    Fail(app, interactive, "Документ не разобран: сохраните его в папке изделия и повторите.");
                    return false;
                }
                int next = target.Revision + 1;
                string reasonText = ChangeReasons.ByCode(code).Text;
                string reasonCode = ChangeReasons.ByCode(code).Code;
                string change = (what ?? "").Trim();
                string stock = (backlog ?? "").Trim();
                if (interactive)
                {
                    using (RevisionForm form = new RevisionForm(Path.GetFileName(target.DocumentPath), target.Revision, next))
                    {
                        if (form.ShowDialog(Owner(app)) != DialogResult.OK)
                        {
                            LastOutcome = "error|отменено конструктором";
                            return false;
                        }
                        change = form.What;
                        reasonText = form.Reason.Text;
                        reasonCode = form.Reason.Code;
                        stock = form.Backlog;
                    }
                }
                if (change.Length == 0) change = "изменение без описания";
                if (stock.Length == 0) stock = "использовать";

                Status(app, "ЕСКД: новая ревизия — журнал изменений…");
                int line = ChangeLog.Append(ChangeLog.Path(target.ProductFolder), new ChangeRow
                {
                    Revision = next,
                    Date = DateTime.Now,
                    Who = Settings.Read().Author ?? Environment.UserName,
                    Document = Path.GetFileName(target.DocumentPath),
                    What = change,
                    Reason = reasonText,
                    Code = reasonCode,
                    Backlog = stock
                });
                if (line == 0)
                {
                    Fail(app, interactive, "Журнал «" + ChangeLog.FileName +
                        "» занят: строка не записана, ревизия не поднята. Закройте книгу и повторите.");
                    return false;
                }

                Status(app, "ЕСКД: новая ревизия — штамп…");
                Write(app, target, next, line);
                Archive(target);
                Status(app, "ЕСКД: новая ревизия — выгрузка…");
                string exported = Export(app, target);
                LastOutcome = string.Join("|", new[]
                {
                    "ok", next.ToString(CultureInfo.InvariantCulture), line.ToString(CultureInfo.InvariantCulture), exported
                });
                if (interactive) Show(target, next, line, exported);
                Status(app, "");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Новая ревизия", ex);
                Fail(app, interactive, "Ревизия не поднята: " + ex.Message);
                return false;
            }
        }

        private static Target Collect(ISldWorks app)
        {
            ModelDoc2 doc = app.ActiveDoc as ModelDoc2;
            if (doc == null) return null;
            Target target = new Target
            {
                Document = doc,
                DocumentPath = doc.GetPathName() ?? "",
                IsDrawing = doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING
            };
            target.Model = target.IsDrawing ? SyncService.ReferencedModel((DrawingDoc)doc) : doc;
            target.ModelPath = target.Model != null ? (target.Model.GetPathName() ?? "") : "";
            string anchor = target.ModelPath.Length > 0 ? target.ModelPath : target.DocumentPath;
            ProductLocation location = ProductLocator.Locate(anchor);
            target.ProductFolder = location.ProductFolder.Length > 0 ? location.ProductFolder : LzkNaming.ProductFolder(anchor);
            if (target.ProductFolder.Length == 0) return null;

            ModelDoc2 source = target.Model ?? doc;
            PropertyWriter w = new PropertyWriter(source, true);
            string cfg = w.ActiveConfigurationName();
            target.Designation = Value(w, cfg, "Обозначение");
            target.Name = Value(w, cfg, "Наименование");
            target.Revision = ExportNaming.Revision(Value(new PropertyWriter(doc, true), "",
                target.IsDrawing ? RevisionProperty : BchRevisionProperty));
            return target;
        }

        private static string Value(PropertyWriter w, string cfg, string name)
        {
            try
            {
                string value = (w.Resolved(cfg, name) ?? "").Trim();
                if (value.Length == 0) value = (w.Resolved("", name) ?? "").Trim();
                return PropertyWriter.IsEmptyOrTemplate(value) ? "" : value;
            }
            catch (Exception ex)
            {
                Log.Error("Новая ревизия: свойство " + name, ex);
                return "";
            }
        }

        /// <summary>Ревизия и графы таблицы изменений — в том же виде, в каком их ведёт DProp (Т-50).</summary>
        private static void Write(ISldWorks app, Target target, int revision, int line)
        {
            string property = target.IsDrawing ? RevisionProperty : BchRevisionProperty;
            PropertyWriter w = new PropertyWriter(target.Document, false);
            w.Set("", property, revision.ToString(CultureInfo.InvariantCulture));
            if (!target.IsDrawing)
            {
                Save(target.Document);
                return;
            }
            DrawingDoc drw = (DrawingDoc)target.Document;
            string[] sheets = drw.GetSheetNames() as string[] ?? new string[0];
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // «Лист» у однолистового чертежа не заполняется: изменение относится ко всему документу.
                { "Revision2", sheets.Length > 1 ? "все" : "—" },
                { "Revision3", line.ToString(CultureInfo.InvariantCulture) },
                { "Revision4", DateTime.Now.ToString("dd.MM.yy", CultureInfo.InvariantCulture) },
                { "Revision5", (Settings.Read().Author ?? "").Trim() }
            };
            string active = CurrentSheet(drw);
            foreach (string sheet in sheets)
            {
                try
                {
                    drw.ActivateSheet(sheet);
                    View format = drw.GetFirstView() as View;
                    object noteObject = format != null ? format.GetFirstNote() : null;
                    while (noteObject != null)
                    {
                        Note note = (Note)noteObject;
                        string name = note.GetName() ?? "";
                        string value;
                        // Пустую подпись не пишем: у заказчика, который подписывает от руки, графа остаётся его (Т-16).
                        if (values.TryGetValue(name, out value) && value.Length > 0) note.SetText(value);
                        noteObject = note.GetNext();
                    }
                }
                catch (COMException ex)
                {
                    Log.Error("Новая ревизия: надписи листа " + sheet, ex);
                }
            }
            if (active.Length > 0)
            {
                try
                {
                    drw.ActivateSheet(active);
                }
                catch (COMException ex)
                {
                    Log.Error("Новая ревизия: возврат на лист " + active, ex);
                }
            }
            target.Document.ForceRebuild3(false);
            Save(target.Document);
        }

        private static string CurrentSheet(DrawingDoc drw)
        {
            try
            {
                Sheet sheet = drw.GetCurrentSheet() as Sheet;
                return sheet != null ? (sheet.GetName() ?? "") : "";
            }
            catch (COMException ex)
            {
                Log.Error("Новая ревизия: текущий лист", ex);
                return "";
            }
        }

        private static void Save(ModelDoc2 doc)
        {
            int errors = 0, warnings = 0;
            doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
        }

        /// <summary>Прежние файлы выдачи этого документа — в `_Аннулировано` (Т-30, Т-52).</summary>
        private static void Archive(Target target)
        {
            string stem = ExportNaming.Stem(target.Designation, target.Name, target.ModelPath.Length > 0
                ? target.ModelPath : target.DocumentPath);
            if (stem.Length == 0) return;
            string[] folders =
            {
                ExportNaming.PdfDirectory(target.ProductFolder),
                ExportNaming.LaserDirectory(target.ProductFolder),
                ExportNaming.TubeDirectory(target.ProductFolder)
            };
            DateTime stamp = DateTime.Now;
            foreach (string folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.GetFiles(folder, stem + "*"))
                {
                    try
                    {
                        string archive = ExportNaming.ArchivePath(file, stamp);
                        Directory.CreateDirectory(Path.GetDirectoryName(archive) ?? "");
                        File.Move(file, archive);
                    }
                    catch (IOException ex)
                    {
                        Log.Error("Новая ревизия: перенос в " + ExportNaming.ArchiveFolder + " " + file, ex);
                    }
                }
            }
        }

        /// <summary>Выгрузка документа с новым номером ревизии (Т-52): работает К-2 на самой модели.</summary>
        private static string Export(ISldWorks app, Target target)
        {
            if (target.ModelPath.Length == 0) return "модель не найдена: выгрузка не сделана";
            try
            {
                int errors = 0;
                app.ActivateDoc3(target.ModelPath, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                ExportService.Run(app, false);
                string outcome = ExportService.LastOutcome ?? "";
                string[] parts = outcome.Split('|');
                return parts.Length >= 3 && parts[0] == "ok" ? "файлов: " + parts[1] + ", пропущено: " + parts[2] : outcome;
            }
            catch (COMException ex)
            {
                Log.Error("Новая ревизия: выгрузка " + target.ModelPath, ex);
                return "выгрузка не сделана: " + ex.Message;
            }
            finally
            {
                try
                {
                    int errors = 0;
                    app.ActivateDoc3(target.DocumentPath, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                }
                catch (COMException ex)
                {
                    Log.Error("Новая ревизия: возврат к документу", ex);
                }
            }
        }

        private static void Show(Target target, int revision, int line, string exported)
        {
            string text = "Ревизия " + revision + " записана." + Environment.NewLine +
                "Журнал «" + ChangeLog.FileName + "»: строка " + line + "." + Environment.NewLine +
                "Выгрузка: " + exported + Environment.NewLine + Environment.NewLine +
                "Если менялась модель, книга ЛЗК могла устареть — пересоберите её кнопкой «Ведомость ЛЗК» и снова нажмите «Готово к производству».";
            MessageBox.Show(text, "ЕСКД: новая ревизия", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static IWin32Window Owner(ISldWorks app)
        {
            try
            {
                Frame frame = app.Frame() as Frame;
                return frame != null ? new WindowWrapper(new IntPtr(frame.GetHWnd())) : null;
            }
            catch (Exception ex)
            {
                Log.Error("Новая ревизия: окно SolidWorks", ex);
                return null;
            }
        }

        private static void Fail(ISldWorks app, bool interactive, string text)
        {
            LastOutcome = "error|" + text;
            Status(app, "");
            if (interactive) MessageBox.Show(text, "ЕСКД: новая ревизия", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Status(ISldWorks app, string text)
        {
            try
            {
                Frame frame = app.Frame() as Frame;
                if (frame != null && text != null) frame.SetStatusBarText(text);
            }
            catch (Exception ex)
            {
                Log.Error("Новая ревизия: строка состояния", ex);
            }
        }
    }
}
