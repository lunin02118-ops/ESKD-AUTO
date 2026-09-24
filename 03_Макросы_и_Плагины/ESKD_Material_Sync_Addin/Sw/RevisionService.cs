using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
    /// Порядок (аудит 23.09.2026, SAVE-9, REV-2): журнал → штамп (не сохранился — штамп возвращается, строка журнала
    /// помечается «ОТМЕНЕНО») → выгрузка → перенос прежних файлов, и только туда, куда легли новые.
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
            /// <summary>Основы имён файлов выдачи — у каждого исполнения своя (обозначение с «-01»).</summary>
            public readonly List<string> Stems = new List<string>();
        }

        /// <summary>Штамп до записи: свойство ревизии и надписи таблицы изменений — чтобы вернуть, если не сохранилось.</summary>
        private sealed class Stamp
        {
            public string Property = "";
            public bool Existed;
            public string Value = "";
            /// <summary>Лист → имя надписи → прежний текст.</summary>
            public readonly Dictionary<string, Dictionary<string, string>> Notes =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Файл выдачи до выгрузки новой ревизии: переписанный выгрузкой уже новый.</summary>
        private sealed class IssueFile
        {
            public string Path = "";
            public DateTime Written;
            public long Length;
        }

        // ------------------------------------------------------------------ доступность (Т-48)
        // SolidWorks опрашивает доступность кнопки на каждую перерисовку, а ответ «выдан ли документ» — это чтение
        // отчётов выдачи с NAS. Раньше опрос читал их в потоке SolidWorks раз в 2 с, и подвисший NAS замораживал
        // SolidWorks; переход между двумя окнами каждый раз сбрасывал запомненный ответ (сверка SW API 23.09.2026, №1).
        // Теперь опрос отвечает из памяти, а отчёты перечитываются в фоне раз в 10 с по каждому документу. Само нажатие
        // и автотесты (RevisionUnavailable) читают отчёты заново.
        private static readonly BackgroundFlags IssuedFlags = new BackgroundFlags(IssuedByKey, TimeSpan.FromSeconds(10),
            BackgroundFlags.OnThreadPool, delegate { return DateTime.UtcNow; });

        /// <summary>Ключ признака: «d|» — чертёж, «m|» — модель, дальше путь. Только файлы, без SolidWorks: идёт в фоне.</summary>
        private static bool IssuedByKey(string key)
        {
            return Issued(key.Substring(2), key.StartsWith("d|", StringComparison.Ordinal));
        }

        /// <summary>
        /// Для опроса кнопки: те же проверки, что у <see cref="Unavailable"/>, но «выдан ли» — из памяти. Пока ответ не
        /// посчитан, кнопка серая: у только что открытого выданного чертежа — доли секунды до следующего опроса.
        /// </summary>
        public static bool AvailableForButton(ISldWorks app)
        {
            ModelDoc2 doc = app.ActiveDoc as ModelDoc2;
            if (doc == null) return false;
            int type = doc.GetType();
            bool drawing = type == (int)swDocumentTypes_e.swDocDRAWING;
            string format;
            if (!drawing && (type != (int)swDocumentTypes_e.swDocPART || !BchService.State(doc, out format))) return false;
            string path = doc.GetPathName() ?? "";
            if (path.Length == 0) return false;
            bool known;
            bool issued = IssuedFlags.Get((drawing ? "d|" : "m|") + path, out known);
            return known && issued;
        }

        /// <summary>Выдача изделия или закрытие заказа: запомненные ответы опроса кнопки больше не верны.</summary>
        public static void ForgetIssued()
        {
            IssuedFlags.Forget();
        }

        /// <summary>Причина, по которой кнопка недоступна, или пустая строка. Отчёты выдачи читаются заново.</summary>
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
                bool drawing = type == (int)swDocumentTypes_e.swDocDRAWING;
                return Issued(path, drawing) ? "" : "документ ещё не выдан — правьте свободно";
            }
            catch (COMException ex)
            {
                // Пустая строка означает «препятствий нет» — на занятом SolidWorks это превращало запрет на правку
                // выданного документа в разрешение (аудит 20.09.2026). Не узнали — значит нельзя.
                Log.Error("Ревизия: проверка выданного документа", ex);
                return "SolidWorks не ответил — проверить, выдан ли документ, не удалось; повторите";
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
            Target target = null;
            Stamp stamp = null;
            int line = 0, next = 0;
            bool saved = false;
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
                target = Collect(app);
                if (target == null)
                {
                    Fail(app, interactive, "Документ не разобран: сохраните его в папке изделия и повторите.");
                    return false;
                }
                // Документ только для чтения (открыт у коллеги): штамп не сохранится, а строка журнала и перенос
                // прежних PDF в «_Аннулировано» уже случились бы — отказ до любых изменений.
                if (target.Document.IsOpenedReadOnly())
                {
                    Fail(app, interactive, "Документ открыт только для чтения (его держит другой пользователь или файл " +
                        "защищён от записи). Ревизия не поднята, журнал и файлы выдачи не тронуты.");
                    return false;
                }
                next = target.Revision + 1;
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
                line = ChangeLog.Append(ChangeLog.Path(target.ProductFolder), new ChangeRow
                {
                    Revision = next,
                    Date = DateTime.Now,
                    Who = Settings.AuthorOrUser(),
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
                stamp = new Stamp();
                string saveProblem = Write(app, target, next, line, stamp);
                if (saveProblem.Length > 0)
                {
                    // Пока документ не сохранён, действующей остаётся прежняя ревизия (аудит 23.09.2026, SAVE-9). Раньше
                    // новый номер оставался в открытом документе, а строка — в журнале: следующее сохранение конструктора
                    // записало бы ревизию без выгрузки и без переноса прежних файлов.
                    Fail(app, interactive, "Штамп ревизии " + next + " не сохранён (" + saveProblem + "). Ревизия не поднята. " +
                        Undo(target, stamp, line, next));
                    return false;
                }
                saved = true;
                List<IssueFile> before = IssueFiles(target);
                Status(app, "ЕСКД: новая ревизия — выгрузка…");
                string exported = Export(app, target);
                List<string> moved;
                string kept = Archive(target, before, out moved);
                DropFromReport(target.ProductFolder, moved);
                if (kept.Length > 0) exported += "; " + kept;
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
                // Файлы прежней ревизии уносит выгрузка изделия с главной сборки (З-48): выданное прежней ревизией документа,
                // чья новая ревизия выгружена, уходит в «_Аннулировано».
                if (saved)
                    Fail(app, interactive, "Ревизия " + next + " записана, но дальше ошибка: " + ex.Message +
                        ". Выгрузите изделие с главной сборки кнопкой «Выгрузить в производство»: файлы ревизии " + next +
                        " выгрузятся, а файлы прежней ревизии этого документа уйдут в «" + ExportNaming.ArchiveFolder + "».");
                else if (line > 0 && target != null)
                    Fail(app, interactive, "Ревизия не поднята: " + ex.Message + ". " + Undo(target, stamp, line, next));
                else
                    Fail(app, interactive, "Ревизия не поднята: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Штамп не сохранился: вернуть штамп в открытом документе и пометить строку журнала «ОТМЕНЕНО» (строку не
        /// удаляют — номер мог уже попасть в штамп). Возвращает текст для сообщения конструктору.
        /// </summary>
        private static string Undo(Target target, Stamp stamp, int line, int revision)
        {
            if (stamp != null) Restore(target, stamp);
            bool cancelled = ChangeLog.Cancel(ChangeLog.Path(target.ProductFolder), line, "штамп ревизии " +
                revision.ToString(CultureInfo.InvariantCulture) + " не сохранён");
            return (stamp != null ? "Штамп в документе возвращён к прежнему; " : "") + (cancelled
                ? "строка " + line + " журнала «" + ChangeLog.FileName + "» помечена «ОТМЕНЕНО»"
                : "строку " + line + " журнала «" + ChangeLog.FileName + "» пометить не удалось (книга занята) — впишите " +
                  "в начало её графы «Что изменено» слово «" + ChangeLog.CancelledPrefix.Trim() + "»") + ". Прежние файлы выдачи не тронуты.";
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
            // Прежние файлы выдачи ищутся по основе имени каждого исполнения: чертёж один на все исполнения детали.
            AddStem(target, ExportNaming.Stem(target.Designation, target.Name, anchor));
            foreach (string configuration in w.ConfigurationNames())
                AddStem(target, ExportNaming.Stem(Value(w, configuration, "Обозначение"), Value(w, configuration, "Наименование"), anchor));
            return target;
        }

        private static void AddStem(Target target, string stem)
        {
            if (stem.Length > 0 && !target.Stems.Contains(stem, StringComparer.OrdinalIgnoreCase)) target.Stems.Add(stem);
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
        /// <param name="stamp">Сюда — прежнее свойство ревизии и прежние надписи: не сохранилось — их возвращают.</param>
        /// <returns>Пусто — сохранено; иначе причина, по которой документ не сохранился.</returns>
        private static string Write(ISldWorks app, Target target, int revision, int line, Stamp stamp)
        {
            string property = target.IsDrawing ? RevisionProperty : BchRevisionProperty;
            PropertyWriter w = new PropertyWriter(target.Document, false);
            stamp.Property = property;
            stamp.Existed = w.Exists("", property);
            stamp.Value = w.Raw("", property) ?? "";
            w.Set("", property, revision.ToString(CultureInfo.InvariantCulture));
            if (!target.IsDrawing) return Save(target.Document);
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
            VisitNotes(drw, sheets, (sheet, note) =>
            {
                string name = note.GetName() ?? "";
                string value;
                // Пустую подпись не пишем: у заказчика, который подписывает от руки, графа остаётся его (Т-16).
                if (!values.TryGetValue(name, out value) || value.Length == 0) return;
                Dictionary<string, string> before;
                if (!stamp.Notes.TryGetValue(sheet, out before))
                {
                    before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    stamp.Notes[sheet] = before;
                }
                if (!before.ContainsKey(name)) before[name] = NoteText(note);
                note.SetText(value);
            });
            // Полное перестроение нужно: связанные надписи ревизии обновляются на всех листах до PDF, а EditRebuild3 (Ctrl+B)
            // перестроил бы только активный лист. Время — в журнал: если на крупном СБ оно долгое, перейти на EditRebuild3
            // по каждому листу в VisitNotes (сверка SW API 23.09.2026, №30).
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            target.Document.ForceRebuild3(false);
            Log.Info("Новая ревизия: перестроение чертежа " + watch.ElapsedMilliseconds + " мс — " + target.DocumentPath);
            return Save(target.Document);
        }

        /// <summary>Каждая надпись форматки на каждом из листов; после — снова прежний активный лист.</summary>
        private static void VisitNotes(DrawingDoc drw, IEnumerable<string> sheets, Action<string, Note> visit)
        {
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
                        visit(sheet, note);
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
        }

        /// <summary>Текст надписи как записан: со ссылкой на свойство, если она есть, — иначе видимый текст.</summary>
        private static string NoteText(Note note)
        {
            string shown = note.GetText() ?? "";
            try
            {
                string linked = note.PropertyLinkedText ?? "";
                return linked.IndexOf("$PRP", StringComparison.OrdinalIgnoreCase) >= 0 ? linked : shown;
            }
            catch (COMException)
            {
                return shown;
            }
        }

        /// <summary>
        /// Документ не сохранился — штамп в открытом документе снова прежний (аудит 23.09.2026, SAVE-9): свойство ревизии
        /// и надписи таблицы изменений. Пустую надпись SolidWorks не принимает — вместо неё пробел, выглядит так же.
        /// </summary>
        private static void Restore(Target target, Stamp stamp)
        {
            try
            {
                PropertyWriter w = new PropertyWriter(target.Document, false);
                if (stamp.Existed) w.Set("", stamp.Property, stamp.Value);
                else w.Delete("", stamp.Property);
            }
            catch (Exception ex)
            {
                Log.Error("Новая ревизия: возврат свойства " + stamp.Property, ex);
            }
            if (!target.IsDrawing || stamp.Notes.Count == 0) return;
            try
            {
                DrawingDoc drw = (DrawingDoc)target.Document;
                VisitNotes(drw, stamp.Notes.Keys.ToList(), (sheet, note) =>
                {
                    string before;
                    if (stamp.Notes[sheet].TryGetValue(note.GetName() ?? "", out before)) note.SetText(before.Length > 0 ? before : " ");
                });
                target.Document.ForceRebuild3(false);
            }
            catch (Exception ex)
            {
                Log.Error("Новая ревизия: возврат надписей штампа", ex);
            }
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

        private static string Save(ModelDoc2 doc)
        {
            int errors = 0, warnings = 0;
            if (doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) && errors == 0) return "";
            Log.Warn("Новая ревизия: Save3 errors=" + errors + " warnings=" + warnings + " " + (doc.GetPathName() ?? ""));
            return "код ошибки SolidWorks " + errors;
        }

        /// <summary>
        /// Файлы выдачи этого документа сейчас: имя — основа имени любого его исполнения, за ней расширение, «_ИзмN» или
        /// толщина развёртки (<see cref="ExportNaming.BelongsTo"/>). Раньше брались все файлы, начинающиеся с основы, —
        /// и ревизия «Детали1» уносила файлы «Детали10» (аудит 23.09.2026, REV-2).
        /// </summary>
        private static List<IssueFile> IssueFiles(Target target)
        {
            List<IssueFile> files = new List<IssueFile>();
            string[] folders =
            {
                ExportNaming.PdfDirectory(target.ProductFolder),
                ExportNaming.LaserDirectory(target.ProductFolder),
                ExportNaming.TubeDirectory(target.ProductFolder)
            };
            foreach (string folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.GetFiles(folder))
                {
                    string name = Path.GetFileName(file);
                    if (!target.Stems.Any(stem => ExportNaming.BelongsTo(name, stem))) continue;
                    try
                    {
                        FileInfo info = new FileInfo(file);
                        files.Add(new IssueFile { Path = file, Written = info.LastWriteTimeUtc, Length = info.Length });
                    }
                    catch (IOException ex)
                    {
                        Log.Error("Новая ревизия: файл выдачи " + file, ex);
                    }
                }
            }
            return files;
        }

        /// <summary>
        /// Прежние файлы выдачи этого документа — в `_Аннулировано` (Т-30, Т-52), но только после выгрузки новой ревизии и
        /// только там, где лёг новый файл взамен (аудит 23.09.2026, REV-2): раньше прежние файлы уносились до выгрузки, и
        /// если она не удалась, у цеха не оставалось ничего. PDF чертежа один на все исполнения — прежний PDF заменён
        /// любым новым PDF документа, под каким бы исполнением он ни лёг (ревью 23.09.2026). DXF и IGS у каждого
        /// исполнения свои, а выгрузка детали делает только активное исполнение (EXP-3): файлы остальных исполнений
        /// остаются на месте, и конструктору называются поимённо — их уносит выгрузка изделия с главной сборки (З-48).
        /// Переписанный выгрузкой файл — новый.
        /// </summary>
        /// <param name="moved">Имена перенесённых файлов — их убирают из отчёта выгрузки.</param>
        /// <returns>Пусто — перенесено всё; иначе что осталось на месте и что делать.</returns>
        private static string Archive(Target target, List<IssueFile> before, out List<string> moved)
        {
            moved = new List<string>();
            List<IssueFile> now = IssueFiles(target);
            List<string> fresh = now.Where(file => !before.Any(old => Same(old, file))).Select(file => file.Path).ToList();
            string pdf = ExportNaming.PdfDirectory(target.ProductFolder);
            DateTime stamp = DateTime.Now;
            List<string> kept = new List<string>();
            List<string> notMoved = new List<string>();
            foreach (IGrouping<string, IssueFile> group in before.GroupBy(file => GroupOf(target, pdf, file.Path),
                StringComparer.OrdinalIgnoreCase))
            {
                int bar = group.Key.LastIndexOf('|');
                string folder = group.Key.Substring(0, bar);
                string stem = group.Key.Substring(bar + 1);
                // Файлы IssueFiles — уже только этого документа: в папке PDF взамен годится любой новый.
                if (!fresh.Any(file => string.Equals(Path.GetDirectoryName(file) ?? "", folder, StringComparison.OrdinalIgnoreCase) &&
                    (stem == AnyStem || ExportNaming.BelongsTo(Path.GetFileName(file), stem))))
                {
                    kept.AddRange(group.Where(file => File.Exists(file.Path)).Select(file => Path.GetFileName(file.Path)));
                    continue;
                }
                foreach (IssueFile file in group)
                {
                    if (fresh.Contains(file.Path, StringComparer.OrdinalIgnoreCase) || !File.Exists(file.Path)) continue;
                    try
                    {
                        string archive = ExportNaming.ArchivePath(file.Path, stamp);
                        Directory.CreateDirectory(Path.GetDirectoryName(archive) ?? "");
                        File.Move(file.Path, archive);
                        moved.Add(Path.GetFileName(file.Path));
                    }
                    catch (IOException ex)
                    {
                        Log.Error("Новая ревизия: перенос в " + ExportNaming.ArchiveFolder + " " + file.Path, ex);
                        notMoved.Add(Path.GetFileName(file.Path));
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Log.Error("Новая ревизия: перенос в " + ExportNaming.ArchiveFolder + " " + file.Path, ex);
                        notMoved.Add(Path.GetFileName(file.Path));
                    }
                }
            }
            List<string> notes = new List<string>();
            if (kept.Count > 0)
                notes.Add("остались файлы прежней ревизии — замены для них не выгружено (выгружено только активное исполнение): " +
                    string.Join(", ", kept.ToArray()) + ". Выгрузите изделие с главной сборки: остальные исполнения выгрузятся " +
                    "с новой ревизией, а эти файлы уйдут в «" + ExportNaming.ArchiveFolder + "» (З-48)");
            if (notMoved.Count > 0)
                notes.Add("не перенесены в «" + ExportNaming.ArchiveFolder + "» (заняты): " + string.Join(", ", notMoved.ToArray()) +
                    " — перенесите вручную");
            return string.Join("; ", notes.ToArray());
        }

        private const string AnyStem = "*";

        /// <summary>Группа файлов, которые заменяют друг друга: папка и исполнение; в папке PDF — вся папка.</summary>
        private static string GroupOf(Target target, string pdfFolder, string path)
        {
            string folder = Path.GetDirectoryName(path) ?? "";
            return folder + "|" + (string.Equals(folder.TrimEnd('\\'), pdfFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? AnyStem : StemOf(target, path));
        }

        /// <summary>
        /// Перенесённые в `_Аннулировано` файлы — из отчёта выгрузки: выгрузка уже записала его и оставила в нём прежние
        /// файлы, которые тогда ещё лежали на месте (ревью 23.09.2026, REV-2).
        /// </summary>
        private static void DropFromReport(string productFolder, List<string> moved)
        {
            if (moved == null || moved.Count == 0) return;
            string path = ExportNaming.ReportPath(productFolder);
            try
            {
                if (!File.Exists(path)) return;
                string text = File.ReadAllText(path, Encoding.UTF8);
                bool changed;
                string updated = ExportLog.RemoveFiles(text, moved, out changed);
                if (changed) File.WriteAllText(path, updated, new UTF8Encoding(true));
            }
            catch (IOException ex)
            {
                Log.Error("Новая ревизия: отчёт выгрузки " + path, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Новая ревизия: отчёт выгрузки " + path, ex);
            }
        }

        /// <summary>Основа имени (исполнение), к которой относится файл выдачи.</summary>
        private static string StemOf(Target target, string path)
        {
            string name = Path.GetFileName(path);
            return target.Stems.FirstOrDefault(stem => ExportNaming.BelongsTo(name, stem)) ?? "";
        }

        private static bool Same(IssueFile a, IssueFile b)
        {
            return string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) && a.Written == b.Written && a.Length == b.Length;
        }

        /// <summary>Выгрузка документа с новым номером ревизии (Т-52): работает К-2 на самой модели.</summary>
        private static string Export(ISldWorks app, Target target)
        {
            if (target.ModelPath.Length == 0) return "модель не найдена: выгрузка не сделана";
            // Модель чертежа загружена без окна: окно открывается ради выгрузки и в конце закрывается (сверка SW API
            // 23.09.2026, №15). Раньше оно оставалось у конструктора.
            bool hidden = false;
            try
            {
                hidden = target.Model != null && !target.Model.Visible;
            }
            catch (COMException ex)
            {
                Log.Error("Новая ревизия: окно модели", ex);
            }
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
                    // Окно с несохранёнными изменениями остаётся: о нём замечание выгрузки. Сохранённый ревизией чертёж
                    // пишет в модель свой формат — модель изменена. CloseDoc не сохраняет и снимает с модели, оставшейся
                    // в чертеже, отметку «изменена»: правка молча пропала бы при закрытии чертежа (проба 23.09.2026).
                    if (hidden && target.Model.Visible && !DocumentGuard.HasUserEdits(target.Model)) app.CloseDoc(target.ModelPath);
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
