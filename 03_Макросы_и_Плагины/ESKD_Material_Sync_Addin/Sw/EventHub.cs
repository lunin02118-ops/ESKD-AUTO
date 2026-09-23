using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Подписки на события SolidWorks по измеренной модели (план, §1 и §3.3):
    ///  * FileSaveNotify — единственная точка до записи файла, видимая и UI, и API: запись реквизитов;
    ///  * FileSavePostNotify(2) — первое сохранение и «Сохранить как»: новое имя известно только здесь,
    ///    синхронизация и однократное пересохранение выполняются из очереди OnIdleNotify;
    ///  * открытие и переключение окон ничего не пишут;
    ///  * задачи простоя документ не сохраняют: сохраняет конструктор (решение владельца 23.09.2026). Исключения —
    ///    продолжение его же команды: пересохранение после «Сохранить как» (ResaveAfterSaveAs) и копия
    ///    (FixCopies), оба — настройки;
    ///  * очередь простоя без зацикливаний: одна задача на документ и вид, и проход выполняет только задачи,
    ///    стоявшие в очереди к его началу.
    /// </summary>
    public sealed class EventHub
    {
        private const int SaveTypeSave = 1;
        private const int SaveTypeSaveAs = 2;
        private const int SaveTypeCopy = 3;
        private const int SaveTypeCopyAndOpen = 4;
        private const int CmdEditMaterial = 501;          // swCommands_e.swCommands_EditMaterial
        private const int CmdFavoriteMaterialFirst = 2007; // swCommands_e.swCommands_Favorite_Material_1
        private const int CmdFavoriteMaterialLast = 2016; // swCommands_e.swCommands_Favorite_Material_10

        private sealed class DocState
        {
            public ModelDoc2 Doc;
            /// <summary>Пришёл DestroyNotify: события этого документа больше не обрабатываются.</summary>
            public bool Destroyed;
            public int Type;
            public string LastPath = "";
            public PartDoc Part;
            public AssemblyDoc Asm;
            public DrawingDoc Drw;
            public DPartDocEvents_FileSaveNotifyEventHandler PSave;
            public DPartDocEvents_FileSavePostNotifyEventHandler PPost;
            public DPartDocEvents_DestroyNotifyEventHandler PDestroy;
            public DAssemblyDocEvents_FileSaveNotifyEventHandler ASave;
            public DAssemblyDocEvents_FileSavePostNotifyEventHandler APost;
            public DAssemblyDocEvents_DestroyNotifyEventHandler ADestroy;
            public DDrawingDocEvents_FileSavePostNotifyEventHandler DPost;
            public DDrawingDocEvents_DestroyNotifyEventHandler DDestroy;
        }

        private sealed class IdleTask
        {
            public ModelDoc2 Doc;
            public string Kind;
            public string TargetPath;
            public string PreviousPath;
            public string Text;
            /// <summary>Форматы листов чертежа, снятые при его сохранении (задача «drawingformat»).</summary>
            public List<string> Formats;
        }

        private readonly ISldWorks _app;
        private readonly SldWorks _events;
        private readonly List<DocState> _docs = new List<DocState>();
        private readonly Queue<IdleTask> _idle = new Queue<IdleTask>();
        private readonly List<string> _lastWarnings = new List<string>();
        private bool _resaving;
        private bool _processingIdle;

        public EventHub(ISldWorks app)
        {
            _app = app;
            _events = (SldWorks)app;
        }

        /// <summary>Предупреждения последней автоматической синхронизации: сохранение, «Сохранить как», смена материала.</summary>
        public string LastWarnings
        {
            get { return string.Join("\n", _lastWarnings.ToArray()); }
        }

        public void Attach()
        {
            _events.FileNewNotify2 += OnFileNew2;
            _events.FileOpenPostNotify += OnFileOpenPost;
            _events.ActiveDocChangeNotify += OnActiveDocChange;
            _events.CommandCloseNotify += OnCommandClose;
            _events.OnIdleNotify += OnIdle;
            try
            {
                ModelDoc2 d = _app.GetFirstDocument() as ModelDoc2;
                while (d != null)
                {
                    Track(d);
                    d = d.GetNext() as ModelDoc2;
                }
            }
            catch (Exception ex)
            {
                Log.Error("EventHub.Attach: открытые документы", ex);
            }
        }

        public void Detach()
        {
            try
            {
                _events.FileNewNotify2 -= OnFileNew2;
                _events.FileOpenPostNotify -= OnFileOpenPost;
                _events.ActiveDocChangeNotify -= OnActiveDocChange;
                _events.CommandCloseNotify -= OnCommandClose;
                _events.OnIdleNotify -= OnIdle;
            }
            catch (Exception ex)
            {
                Log.Error("EventHub.Detach", ex);
            }
            foreach (DocState s in _docs.ToArray()) Untrack(s);
            _docs.Clear();
            _idle.Clear();
        }

        // ------------------------------------------------------------------ события приложения
        private int OnFileNew2(object newDoc, int docType, string templateName)
        {
            try
            {
                ModelDoc2 doc = newDoc as ModelDoc2;
                Track(doc);
                Settings settings = Settings.Read();
                if (doc != null && settings.ServiceEnabled && docType != (int)swDocumentTypes_e.swDocDRAWING)
                {
                    // Новый документ ещё не сохранён и так помечен изменённым: подписи — только в пустые поля.
                    SyncService.SyncModel(_app, doc, new SyncRequest
                    {
                        Reason = "новый документ", Names = false, Materials = false, Mass = false
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error("FileNewNotify2", ex);
            }
            return 0;
        }

        private int OnFileOpenPost(string fileName)
        {
            try
            {
                ModelDoc2 doc = FindDoc(fileName);
                DocState state = Track(doc);
                if (state != null) state.LastPath = SafePath(doc);
                Settings settings = Settings.Read();
                if (doc == null || !settings.ServiceEnabled || doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING) return 0;
                // Модель или чертёж, которые надстройка открыла скрыто ради формата, сразу закроются.
                if (DrawingFormatService.Busy) return 0;
                if (settings.SyncOnOpen)
                {
                    SyncService.SyncModel(_app, doc, new SyncRequest { Reason = "открытие (SyncOnOpen)" });
                }
                else
                {
                    SyncReport plan = SyncService.SyncModel(_app, doc, new SyncRequest
                    {
                        Reason = "диагностика при открытии", DryRun = true, Signatures = false, Materials = false, Mass = false
                    });
                    if (plan.Operations.Count > 0)
                        Log.Info(string.Format("Реквизиты «{0}» будут обновлены при сохранении: {1} операций", SafeTitle(doc), plan.Operations.Count));
                }
            }
            catch (Exception ex)
            {
                Log.Error("FileOpenPostNotify", ex);
            }
            return 0;
        }

        private int OnActiveDocChange()
        {
            try
            {
                Track(_app.ActiveDoc as ModelDoc2);
            }
            catch (Exception ex)
            {
                Log.Error("ActiveDocChangeNotify", ex);
            }
            return 0;
        }

        private int OnCommandClose(int command, int reason)
        {
            if (command != CmdEditMaterial && (command < CmdFavoriteMaterialFirst || command > CmdFavoriteMaterialLast)) return 0;
            try
            {
                ModelDoc2 doc = _app.ActiveDoc as ModelDoc2;
                if (doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocPART && Settings.Read().ServiceEnabled)
                {
                    Enqueue(new IdleTask { Doc = doc, Kind = "material" });
                }
            }
            catch (Exception ex)
            {
                Log.Error("CommandCloseNotify", ex);
            }
            return 0;
        }

        /// <summary>
        /// Отложенных задач (пересохранение после «Сохранить как», формат чертежа, копия): пока их больше нуля,
        /// внешний процесс не должен закрывать документ и дёргать его события — SolidWorks принимает входящий
        /// COM-вызов посреди Save3 в простое и падает (ucrtbase 0xc0000409, R01 19.09.2026).
        /// </summary>
        public int PendingTasks
        {
            get { return _idle.Count + (_processingIdle ? 1 : 0); }
        }

        private int OnIdle()
        {
            try
            {
                LzkService.PollIdle();
            }
            catch (Exception ex)
            {
                Log.Error("OnIdleNotify: ведомость ЛЗК", ex);
            }
            if (_processingIdle || _idle.Count == 0) return 0;
            _processingIdle = true;
            try
            {
                // Только задачи, стоявшие в очереди к началу прохода. Задача, заведённая самим проходом (сохранение
                // внутри задачи → событие сохранения → новая задача), ждёт следующего простоя: цепочка
                // задача → событие → задача не замыкается в бесконечный цикл внутри одного OnIdleNotify.
                int batch = _idle.Count;
                while (batch-- > 0 && _idle.Count > 0)
                {
                    IdleTask task = _idle.Dequeue();
                    try
                    {
                        RunIdleTask(task);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("OnIdleNotify: " + task.Kind, ex);
                    }
                }
            }
            finally
            {
                _processingIdle = false;
            }
            return 0;
        }

        private void RunIdleTask(IdleTask task)
        {
            if (task.Kind == "drawingformat")
            {
                // Задача не привязана к документу чертежа: его обычно закрывают сразу после сохранения (21.09.2026).
                if (!Settings.Read().ServiceEnabled) return;
                string told = DrawingFormatService.ApplyToModel(_app, task.TargetPath, task.Formats);
                if (told.Length > 0) Status(told);
                return;
            }
            if (task.Doc == null) return;
            if (task.Kind == "material")
            {
                if (!Alive(task.Doc)) return;
                // Конструктор сам выбрал материал — свойства за ним, без сверки с геометрией: она будет при сохранении,
                // с выбором «оставить / исправить». Иначе его выбор тут же оспаривался бы окном или молча заменялся.
                Remember(task.Doc, SyncService.SyncModel(_app, task.Doc, new SyncRequest
                {
                    Reason = "смена материала", Names = false, Signatures = false, Mass = false, Stock = false
                }));
                return;
            }
            if (task.Kind == "stock")
            {
                // Пока задача ждала простоя, документ могли закрыть, переоткрыть только для чтения, а службу —
                // выключить (так автотесты читают файл, и так делает конструктор в «Настройках ЕСКД»). Назначать
                // материал и вызывать Save3 в таком состоянии нельзя: это не замечание в журнале, а падение
                // SolidWorks (R01, 19.09.2026).
                if (!Settings.Read().ServiceEnabled) { Log.Info("Материал по геометрии: служба выключена, задача отменена"); return; }
                if (!Alive(task.Doc)) { Log.Info("Материал по геометрии: документ закрыт, задача отменена"); return; }
                if (ReadOnly(task.Doc)) { Log.Info("Материал по геометрии: документ открыт только для чтения, задача отменена"); return; }
                ApplyStock(task.Doc);
                return;
            }
            if (task.Kind == "status")
            {
                Status(task.Text);
                return;
            }
            if (task.Kind == "saveas")
            {
                string current = SafePath(task.Doc);
                if (!string.Equals(current, task.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("Пересохранение отменено: документ сменил путь (" + current + ")");
                    return;
                }
                SyncAndResave(task.Doc, "после «Сохранить как»", task.TargetPath, task.PreviousPath);
                return;
            }
            if (task.Kind == "copy")
            {
                FixCopy(task.TargetPath, task.PreviousPath);
                return;
            }
        }

        /// <summary>
        /// Задача в очередь простоя — одна на документ и вид: десять сохранений подряд дают одну сверку материала, а не
        /// десять окон; у строки состояния остаётся последний текст, у формата — последний снимок листов.
        /// «Сохранить как» и копия не схлопываются: у каждой свой файл.
        /// </summary>
        private void Enqueue(IdleTask task)
        {
            if (task == null) return;
            if (task.Kind != "saveas" && task.Kind != "copy")
            {
                foreach (IdleTask queued in _idle)
                {
                    if (queued.Kind != task.Kind) continue;
                    bool same = task.Doc != null
                        ? object.ReferenceEquals(queued.Doc, task.Doc)
                        : queued.Doc == null && string.Equals(queued.TargetPath, task.TargetPath, StringComparison.OrdinalIgnoreCase);
                    if (!same) continue;
                    if (task.Text != null) queued.Text = task.Text;
                    if (task.Formats != null) queued.Formats = task.Formats;
                    return;
                }
            }
            _idle.Enqueue(task);
        }

        private void Status(string text)
        {
            try
            {
                Frame frame = _app.Frame() as Frame;
                if (frame != null) frame.SetStatusBarText(text ?? "");
            }
            catch (Exception ex)
            {
                Log.Error("Строка состояния", ex);
            }
        }

        /// <summary>Документ ещё жив: надстройка о нём знает и его не разрушали. Указатель закрытого документа — падение.</summary>
        private bool Alive(ModelDoc2 doc)
        {
            if (doc == null) return false;
            foreach (DocState s in _docs)
                if (object.ReferenceEquals(s.Doc, doc)) return !s.Destroyed;
            return false;
        }

        /// <summary>Документ открыт только для чтения. Спрашивать не о чем: сохранить его всё равно нельзя.</summary>
        private static bool ReadOnly(ModelDoc2 doc)
        {
            try
            {
                return doc.IsOpenedReadOnly();
            }
            catch (Exception ex)
            {
                Log.Warn("Материал по геометрии: документ не ответил, открыт ли он только для чтения (" + ex.Message + ") — задача отменена");
                return true;
            }
        }

        /// <summary>
        /// Материал по геометрии (Р-8) — в простое, когда SolidWorks отпустил документ. Деталь осматривается заново:
        /// указатели на тела, снятые во время сохранения, к этому моменту могут быть недействительны.
        ///
        /// Документ здесь НЕ сохраняется (решение владельца 23.09.2026: деталь без команды конструктора не сохраняется).
        /// Раньше тут стоял Save3: деталь переписывалась сама после каждого сохранения и смены материала, а событие
        /// этого сохранения снова заводило подбор. Теперь назначенный материал и свойства остаются в открытой детали —
        /// её сохраняет конструктор.
        ///
        /// Кто решает: пустой материал с единственным кандидатом (Assign) подставляется молча — правило 20.09.2026.
        /// Замена стоящего материала (Replace) и выбор из нескольких (Choose) — только ответом в окне «Синхронизировать»
        /// (деталь) или «Проверить изделие» (сборка), где есть и «Оставить как есть». Ctrl+S ничего не спрашивает, в
        /// строке состояния — подсказка (решение владельца 23.09.2026, журнал З-27); прежнее окно выбора материала
        /// после сохранения убрано.
        /// </summary>
        private void ApplyStock(ModelDoc2 doc)
        {
            if (doc == null) return;
            try
            {
                string path = SafePath(doc);
                List<StockFinding> findings = StockService.Inspect(_app, doc);
                List<StockFinding> pending = StockService.PendingDecisions(path, findings, StockService.Accepted(doc));
                StockService.Decline(pending);
                string ask = pending.Count > 0 ? "материал к профилю ждёт вашего ответа (" + pending.Count + ") — " + StockService.WhereToAnswer : "";

                SyncReport applied = new SyncReport();
                int changed = StockService.Apply(_app, doc, findings, applied);
                foreach (string warning in applied.Warnings) Log.Warn(warning);
                foreach (string operation in applied.Operations) Log.Info(operation);
                if (changed == 0)
                {
                    if (ask.Length > 0) Status("ЕСКД: " + ask);
                    return;
                }

                // Свойства и масса — сразу в открытую деталь, иначе графа 3 и книга ЛЗК разошлись бы с телом.
                // Перестроение без сохранения: смена материала и списка вырезов оставляет модель неперестроенной, и
                // SolidWorks спросил бы «перестроить?» при сохранении конструктором (X05, 21.09.2026).
                SyncReport report = SyncService.SyncModel(_app, doc, new SyncRequest
                {
                    Reason = "материал по типоразмеру", Names = false, Signatures = false, Stock = false
                });
                doc.EditRebuild3();
                _lastWarnings.Clear();
                _lastWarnings.AddRange(report.Warnings);
                string text = "ЕСКД: материал назначен по типоразмеру (" + changed + ") — деталь изменена, сохраните её" +
                    (ask.Length > 0 ? "; " + ask : "");
                Log.Info(text + ": " + path);
                Status(text);
            }
            catch (Exception ex)
            {
                Log.Error("Материал по геометрии (простой)", ex);
            }
        }

        private IWin32Window Owner()
        {
            try
            {
                Frame frame = _app.Frame() as Frame;
                if (frame != null) return new WindowWrapper(new IntPtr(frame.GetHWnd()));
            }
            catch (Exception ex)
            {
                Log.Error("Материал по геометрии: окно SolidWorks", ex);
            }
            return null;
        }

        private void SyncAndResave(ModelDoc2 doc, string reason, string targetPath, string previousPath)
        {
            SyncReport report = SyncService.SyncModel(_app, doc, new SyncRequest
            {
                Reason = reason, TargetPath = targetPath, PreviousPath = previousPath
            });
            Remember(doc, report);
            if (report.Changes == 0) return;
            int errors = 0, warnings = 0;
            _resaving = true;
            try
            {
                bool ok = doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
                if (ok) Log.Info("Пересохранено (" + reason + "): " + targetPath);
                else Log.Error(string.Format("Пересохранение не выполнено: {0} (errors={1}, warnings={2})", targetPath, errors, warnings));
            }
            finally
            {
                _resaving = false;
            }
        }

        /// <summary>
        /// FixCopies = 1: копия, записанная «Сохранить как копию», открывается невидимой, получает реквизиты
        /// по своему имени (правило происхождения относительно исходного файла), сохраняется и закрывается.
        /// </summary>
        private void FixCopy(string copyPath, string originalPath)
        {
            if (string.IsNullOrEmpty(copyPath)) return;
            if (_app.GetOpenDocumentByName(copyPath) != null)
            {
                Log.Warn("Копия «" + copyPath + "» уже открыта: реквизиты обновятся при её сохранении");
                return;
            }
            int type = DocumentTypeOf(copyPath);
            if (type == 0) return;
            int errors = 0, warnings = 0;
            ModelDoc2 copy;
            _app.DocumentVisible(false, type);
            try
            {
                copy = _app.OpenDoc6(copyPath, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
            }
            finally
            {
                _app.DocumentVisible(true, type);
            }
            if (copy == null)
            {
                Log.Error(string.Format("Копия не открыта для обновления реквизитов: {0} (errors={1}, warnings={2})", copyPath, errors, warnings));
                return;
            }
            try
            {
                SyncAndResave(copy, "копия", copyPath, originalPath);
            }
            finally
            {
                _app.CloseDoc(copy.GetTitle());
            }
        }

        private static int DocumentTypeOf(string path)
        {
            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".sldprt") return (int)swDocumentTypes_e.swDocPART;
            if (ext == ".sldasm") return (int)swDocumentTypes_e.swDocASSEMBLY;
            return 0;
        }

        // ------------------------------------------------------------------ события документа
        private int OnDocSave(DocState s, string fileName)
        {
            if (s.Destroyed) return 0;
            try
            {
                Settings settings = Settings.Read();
                if (!settings.ServiceEnabled || !settings.SyncOnSave) return 0;
                if (s.Type == (int)swDocumentTypes_e.swDocDRAWING) return 0;
                // «Проверить изделие» сохраняет документы сам и уже всё записал — второй круг не нужен. ЛЗК и выгрузка
                // сохраняют только своё (ToolSaves): не спрошенное у конструктора при их сохранении не пишется.
                if (ProductReviewService.Running || DrawingFormatService.Busy || ToolSaves.Busy) return 0;
                Remember(s.Doc, SyncService.SyncModel(_app, s.Doc, new SyncRequest
                {
                    Reason = _resaving ? "пересохранение" : "сохранение",
                    TargetPath = SafePath(s.Doc),
                    PreviousPath = s.LastPath
                }));
            }
            catch (Exception ex)
            {
                Log.Error("FileSaveNotify", ex);
            }
            return 0;
        }

        private int OnDocSavePost(DocState s, int saveType, string fileName)
        {
            if (s.Destroyed) return 0;
            try
            {
                Settings settings = Settings.Read();
                string previous = s.LastPath;
                if (saveType == SaveTypeSave || saveType == SaveTypeSaveAs)
                {
                    s.LastPath = fileName ?? s.LastPath;
                }
                if (!settings.ServiceEnabled || !settings.SyncOnSave) return 0;
                if (ProductReviewService.Running || DrawingFormatService.Busy || ToolSaves.Busy) return 0;
                if (s.Type == (int)swDocumentTypes_e.swDocDRAWING)
                {
                    // З-1: формат листов — в «Формат» модели. Листы читаются сейчас, пока чертёж открыт; запись в модель —
                    // в простое по её пути, даже если чертёж к тому времени закрыт (замечание владельца 21.09.2026).
                    string modelPath;
                    List<string> sheets;
                    if ((saveType == SaveTypeSave || saveType == SaveTypeSaveAs) &&
                        DrawingFormatService.Capture((DrawingDoc)s.Doc, out modelPath, out sheets))
                        Enqueue(new IdleTask { Kind = "drawingformat", TargetPath = modelPath, PreviousPath = fileName, Formats = sheets });
                    return 0;
                }
                if (saveType == SaveTypeSaveAs && settings.ResaveAfterSaveAs && !_resaving)
                {
                    Enqueue(new IdleTask { Doc = s.Doc, Kind = "saveas", TargetPath = fileName, PreviousPath = previous });
                }
                else if (saveType == SaveTypeCopy && settings.FixCopies)
                {
                    Enqueue(new IdleTask { Doc = s.Doc, Kind = "copy", TargetPath = fileName, PreviousPath = SafePath(s.Doc) });
                }
                else if (saveType == SaveTypeCopy)
                {
                    Log.Warn("Копия «" + fileName + "» сохранена с реквизитами исходного документа; откройте её и сохраните, чтобы обновить обозначение");
                }
                else if (saveType == SaveTypeCopyAndOpen)
                {
                    ModelDoc2 copy = FindDoc(fileName);
                    if (copy != null)
                    {
                        Track(copy);
                        Enqueue(new IdleTask { Doc = copy, Kind = "saveas", TargetPath = SafePath(copy), PreviousPath = previous });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("FileSavePostNotify", ex);
            }
            return 0;
        }

        private int OnDocDestroy(DocState s)
        {
            // Документ разрушается: к нему не обращаемся и отменяем отложенные задачи. Подписки здесь НЕ снимаются:
            // отписка (Unadvise) внутри DestroyNotify меняет список подписчиков, который SolidWorks в этот момент
            // перебирает, — изредка это нарушение доступа в mfc140u и падение SolidWorks при закрытии детали
            // (M07 в длинном прогоне 19.09.2026, отчёт CXPA: DestroyNotify → Release → DestroyNotify2). Документ
            // забывается, его события дальше игнорируются; подписки уходят вместе с разрушенным документом —
            // так же делает зонд автотестов (ESKD_ProbeHost).
            if (s.Destroyed) return 0;
            s.Destroyed = true;
            if (_idle.Count > 0)
            {
                IdleTask[] pending = _idle.ToArray();
                _idle.Clear();
                foreach (IdleTask t in pending)
                {
                    if (!object.ReferenceEquals(t.Doc, s.Doc)) _idle.Enqueue(t);
                    else Log.Warn("Документ закрыт до выполнения отложенной задачи «" + t.Kind + "»: " + t.TargetPath);
                }
            }
            _docs.Remove(s);
            s.Doc = null;
            return 0;
        }

        /// <summary>
        /// Предупреждения видны пользователю (Д-38): после автоматической синхронизации — в строке состояния SolidWorks
        /// (выставляется в простое, иначе её затирает сообщение о записи файла), полностью — по кнопке «Синхронизировать»
        /// (деталь, чертёж), в окне «Проверить изделие» и в журнале.
        /// </summary>
        private void Remember(ModelDoc2 doc, SyncReport report)
        {
            if (report == null || report.Skipped) return;
            _lastWarnings.Clear();
            _lastWarnings.AddRange(report.Warnings);
            // Материал по геометрии (Р-8): назначать и спрашивать — только в простое, документ сейчас занят SolidWorks.
            if (report.StockNeedsWork && doc != null)
                Enqueue(new IdleTask { Doc = doc, Kind = "stock" });
            if (report.Warnings.Count > 0 && doc != null)
                Enqueue(new IdleTask { Doc = doc, Kind = "status", Text = report.StatusLine() });
        }

        // ------------------------------------------------------------------ учёт документов
        private DocState Track(ModelDoc2 doc)
        {
            if (doc == null) return null;
            foreach (DocState existing in _docs)
            {
                if (object.ReferenceEquals(existing.Doc, doc)) return existing;
            }
            DocState s = new DocState();
            s.Doc = doc;
            s.LastPath = SafePath(doc);
            try
            {
                s.Type = doc.GetType();
                if (s.Type == (int)swDocumentTypes_e.swDocPART)
                {
                    s.Part = (PartDoc)doc;
                    s.PSave = delegate(string fn) { return OnDocSave(s, fn); };
                    s.PPost = delegate(int t, string fn) { return OnDocSavePost(s, t, fn); };
                    s.PDestroy = delegate() { return OnDocDestroy(s); };
                    s.Part.FileSaveNotify += s.PSave;
                    s.Part.FileSavePostNotify += s.PPost;
                    s.Part.DestroyNotify += s.PDestroy;
                }
                else if (s.Type == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    s.Asm = (AssemblyDoc)doc;
                    s.ASave = delegate(string fn) { return OnDocSave(s, fn); };
                    s.APost = delegate(int t, string fn) { return OnDocSavePost(s, t, fn); };
                    s.ADestroy = delegate() { return OnDocDestroy(s); };
                    s.Asm.FileSaveNotify += s.ASave;
                    s.Asm.FileSavePostNotify += s.APost;
                    s.Asm.DestroyNotify += s.ADestroy;
                }
                else if (s.Type == (int)swDocumentTypes_e.swDocDRAWING)
                {
                    s.Drw = (DrawingDoc)doc;
                    s.DPost = delegate(int t, string fn) { return OnDocSavePost(s, t, fn); };
                    s.DDestroy = delegate() { return OnDocDestroy(s); };
                    s.Drw.FileSavePostNotify += s.DPost;
                    s.Drw.DestroyNotify += s.DDestroy;
                }
                else
                {
                    return null;
                }
                _docs.Add(s);
                return s;
            }
            catch (Exception ex)
            {
                // Часть подписок могла встать — снимаем их, иначе они остаются без учёта (аудит 19.09, Я-В8).
                Log.Error("Track " + SafeTitle(doc), ex);
                Untrack(s);
                return null;
            }
        }

        private static void Untrack(DocState s)
        {
            try
            {
                if (s.Part != null)
                {
                    s.Part.FileSaveNotify -= s.PSave;
                    s.Part.FileSavePostNotify -= s.PPost;
                    s.Part.DestroyNotify -= s.PDestroy;
                }
                else if (s.Asm != null)
                {
                    s.Asm.FileSaveNotify -= s.ASave;
                    s.Asm.FileSavePostNotify -= s.APost;
                    s.Asm.DestroyNotify -= s.ADestroy;
                }
                else if (s.Drw != null)
                {
                    s.Drw.FileSavePostNotify -= s.DPost;
                    s.Drw.DestroyNotify -= s.DDestroy;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Untrack", ex);
            }
            s.Part = null;
            s.Asm = null;
            s.Drw = null;
            s.Doc = null;
        }

        private ModelDoc2 FindDoc(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    ModelDoc2 d = _app.GetOpenDocumentByName(path) as ModelDoc2;
                    if (d != null) return d;
                }
                return _app.ActiveDoc as ModelDoc2;
            }
            catch (Exception ex)
            {
                Log.Error("FindDoc " + path, ex);
                return null;
            }
        }

        private static string SafePath(ModelDoc2 doc)
        {
            return DocInfo.PathOf(doc);
        }

        private static string SafeTitle(ModelDoc2 doc)
        {
            return DocInfo.TitleOf(doc);
        }
    }
}
