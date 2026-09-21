// ESKD_ProbeHost — внешний зонд событий SolidWorks для харнесса 09_Тесты.
//
// Зонд — отдельный процесс: надстройки, зарегистрированные только в HKCU, SolidWorks по
// LoadAddIn не активирует, а регистрация в HKLM требует прав администратора.
// Процесс подключается к уже запущенной тестовой сессии через ROT и:
//  * пишет JSONL-журнал событий приложения и документов (сохранения, команды, свойства);
//  * по команде ставит путь в очередь «Сохранить как»: в FileSaveAsNotify2 путь подставляется
//    через SetSaveAsFileName, обработчик возвращает S_FALSE — диалог не показывается;
//  * отмечает каждое сохранение вне каталога прогона как нарушение.
// Команды харнесс передаёт файлами cmd_NNNNNN.json в каталоге --control, ответы — ack_NNNNNN.json.
// Свойства документов зонд не изменяет никогда.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.TestProbe
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string journal = Arg(args, "--journal", Path.Combine(Path.GetTempPath(), "eskd_probe.jsonl"));
            string workspace = Arg(args, "--workspace", "");
            string control = Arg(args, "--control", "");
            int parentPid = int.Parse(Arg(args, "--parent-pid", "0"));
            int timeoutSec = int.Parse(Arg(args, "--attach-timeout", "120"));

            ProbeCore core = new ProbeCore(journal, workspace);
            core.DocEvents = Arg(args, "--doc-events", "1") == "1";
            core.PropEvents = Arg(args, "--prop-events", "1") == "1";
            core.SaveEvents = Arg(args, "--save-events", "1") == "1";
            core.DestroyEvents = Arg(args, "--destroy-events", "1") == "1";
            core.CommandEvents = Arg(args, "--command-events", "1") == "1";
            ISldWorks app = null;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSec)
            {
                try { app = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
                catch { app = null; }
                if (app != null) break;
                Thread.Sleep(500);
            }
            if (app == null)
            {
                core.Write("ProbeAttachFailed", null, "\"seconds\":" + (int)sw.Elapsed.TotalSeconds);
                return 2;
            }
            core.Attach(app);
            using (HostForm form = new HostForm(core, control, parentPid))
            {
                Application.Run(form);
            }
            core.Detach();
            return 0;
        }

        private static string Arg(string[] args, string name, string def)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return def;
        }
    }

    internal sealed class HostForm : Form
    {
        private readonly ProbeCore _core;
        private readonly string _control;
        private readonly int _parentPid;
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private DateTime _lastLiveness = DateTime.MinValue;

        public HostForm(ProbeCore core, string control, int parentPid)
        {
            _core = core;
            _control = control;
            _parentPid = parentPid;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Width = 1;
            Height = 1;
            Opacity = 0;
            _timer.Interval = 50;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        private void OnTick(object sender, EventArgs e)
        {
            _timer.Stop();
            try
            {
                if (!string.IsNullOrEmpty(_control) && Directory.Exists(_control))
                {
                    string[] files = Directory.GetFiles(_control, "cmd_*.json");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                    {
                        if (!Handle(f)) { Application.ExitThread(); return; }
                    }
                }
                if ((DateTime.Now - _lastLiveness).TotalSeconds >= 2)
                {
                    _lastLiveness = DateTime.Now;
                    if (Process.GetProcessesByName("SLDWORKS").Length == 0) { Application.ExitThread(); return; }
                    if (_parentPid > 0)
                    {
                        try { Process.GetProcessById(_parentPid); }
                        catch (ArgumentException) { Application.ExitThread(); return; }
                    }
                }
            }
            catch (Exception ex)
            {
                _core.Write("ProbeError", null, "\"where\":\"Tick\",\"message\":" + Json.Str(ex.Message));
            }
            _timer.Start();
        }

        private bool Handle(string cmdFile)
        {
            string text;
            try { text = File.ReadAllText(cmdFile, Encoding.UTF8); }
            catch (IOException) { return true; }
            string id = Path.GetFileNameWithoutExtension(cmdFile).Substring(4);
            string op = Json.Field(text, "op");
            string arg = Json.Field(text, "arg");
            string result = "null";
            bool keepRunning = true;
            switch (op)
            {
                case "ping": result = Json.Str("probe-ok"); break;
                case "mark": _core.Mark(arg); break;
                case "saveas": _core.QueueSaveAs(arg); break;
                case "clear_saveas": _core.ClearSaveAs(); break;
                case "pending_saveas": result = _core.PendingSaveAs().ToString(); break;
                case "violations": result = _core.Violations().ToString(); break;
                case "seq": result = _core.Seq().ToString(); break;
                case "attached": result = _core.AttachedCount().ToString(); break;
                case "detach": result = _core.DetachByTitle(arg).ToString(); break;
                case "doc_events": _core.DocEvents = arg == "1"; result = Json.Str(_core.DocEvents ? "1" : "0"); break;
                case "stop": keepRunning = false; break;
                default: result = Json.Str("unknown op: " + op); break;
            }
            string ack = Path.Combine(_control, "ack_" + id + ".json");
            File.WriteAllText(ack + ".tmp", "{\"ok\":true,\"result\":" + result + "}", new UTF8Encoding(false));
            File.Move(ack + ".tmp", ack);
            File.Delete(cmdFile);
            return keepRunning;
        }
    }

    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Команды харнесса — плоский JSON вида {"op":"...","arg":"..."}; значения экранированы json.dumps.
        public static string Field(string json, string name)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return Regex.Unescape(m.Groups[1].Value);
        }
    }

    internal sealed class ProbeCore
    {
        private readonly object _sync = new object();
        private readonly string _journal;
        private readonly string _workspaceRoot;
        private readonly Queue<string> _saveAsQueue = new Queue<string>();
        private readonly List<DocHooks> _hooks = new List<DocHooks>();
        private ISldWorks _app;
        private SldWorks _events;
        private string _mark = "";
        // Подписки внешнего процесса на события документа роняют SolidWorks при закрытии детали после
        // сохранения (21.09.2026: любая одна подписка — сохранение, свойства или DestroyNotify — даёт нарушение
        // доступа в sldsessionu за 1–4 повтора цикла «открыть → сохранить → закрыть», даже снятая до закрытия).
        // Поэтому сессия автотестов запускает зонд с --doc-events 0 и включает их командой doc_events только
        // контрактным тестам; события приложения безопасны.
        public bool DocEvents = true;
        public bool PropEvents = true;
        public bool SaveEvents = true;
        public bool DestroyEvents = true;
        public bool CommandEvents = true;
        private long _seq;
        private int _violations;

        private sealed class DocHooks
        {
            public ModelDoc2 Doc;
            public PartDoc Part;
            public AssemblyDoc Asm;
            public DrawingDoc Drw;
            public string Title = "";
            public string Path = "";
            public bool Destroyed;
            public bool Detached;
            public DPartDocEvents_FileSaveNotifyEventHandler PSave; public DPartDocEvents_FileSaveAsNotify2EventHandler PSaveAs; public DPartDocEvents_FileSavePostNotifyEventHandler PPost; public DPartDocEvents_FileSavePostCancelNotifyEventHandler PCancel; public DPartDocEvents_AddCustomPropertyNotifyEventHandler PAdd; public DPartDocEvents_ChangeCustomPropertyNotifyEventHandler PChange; public DPartDocEvents_DeleteCustomPropertyNotifyEventHandler PDelete; public DPartDocEvents_DestroyNotifyEventHandler PDestroy;
            public DAssemblyDocEvents_FileSaveNotifyEventHandler ASave; public DAssemblyDocEvents_FileSaveAsNotify2EventHandler ASaveAs; public DAssemblyDocEvents_FileSavePostNotifyEventHandler APost; public DAssemblyDocEvents_FileSavePostCancelNotifyEventHandler ACancel; public DAssemblyDocEvents_AddCustomPropertyNotifyEventHandler AAdd; public DAssemblyDocEvents_ChangeCustomPropertyNotifyEventHandler AChange; public DAssemblyDocEvents_DeleteCustomPropertyNotifyEventHandler ADelete; public DAssemblyDocEvents_DestroyNotifyEventHandler ADestroy;
            public DDrawingDocEvents_FileSaveNotifyEventHandler DSave; public DDrawingDocEvents_FileSaveAsNotify2EventHandler DSaveAs; public DDrawingDocEvents_FileSavePostNotifyEventHandler DPost; public DDrawingDocEvents_FileSavePostCancelNotifyEventHandler DCancel; public DDrawingDocEvents_AddCustomPropertyNotifyEventHandler DAdd; public DDrawingDocEvents_ChangeCustomPropertyNotifyEventHandler DChange; public DDrawingDocEvents_DeleteCustomPropertyNotifyEventHandler DDelete; public DDrawingDocEvents_DestroyNotifyEventHandler DDestroy;
        }

        public ProbeCore(string journal, string workspaceRoot)
        {
            _journal = journal;
            _workspaceRoot = workspaceRoot ?? "";
            string dir = Path.GetDirectoryName(journal);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        public void Attach(ISldWorks app)
        {
            _app = app;
            _events = (SldWorks)app;
            _events.FileNewNotify2 += OnFileNew2;
            _events.FileOpenPostNotify += OnFileOpenPost;
            _events.ActiveDocChangeNotify += OnActiveDocChange;
            _events.DocumentLoadNotify2 += OnDocumentLoad2;
            if (CommandEvents)
            {
                _events.CommandOpenPreNotify += OnCommandOpenPre;
                _events.CommandCloseNotify += OnCommandClose;
            }
            _events.FileCloseNotify += OnFileClose;
            AttachOpenDocuments();
            Write("ProbeConnected", null, "\"revision\":" + Json.Str(app.RevisionNumber()) + ",\"pid\":" + Process.GetCurrentProcess().Id);
        }

        public void Detach()
        {
            try
            {
                if (_events != null)
                {
                    _events.FileNewNotify2 -= OnFileNew2;
                    _events.FileOpenPostNotify -= OnFileOpenPost;
                    _events.ActiveDocChangeNotify -= OnActiveDocChange;
                    _events.DocumentLoadNotify2 -= OnDocumentLoad2;
                    _events.CommandOpenPreNotify -= OnCommandOpenPre;
                    _events.CommandCloseNotify -= OnCommandClose;
                    _events.FileCloseNotify -= OnFileClose;
                }
            }
            catch { }
            lock (_sync) { _hooks.Clear(); }
            Write("ProbeDisconnected", null, null);
        }

        public void Mark(string label)
        {
            lock (_sync) { _mark = label ?? ""; }
            Write("Mark", null, "\"label\":" + Json.Str(label));
        }

        public void QueueSaveAs(string path)
        {
            lock (_sync) { _saveAsQueue.Enqueue(path); }
            Write("SaveAsQueued", null, "\"path\":" + Json.Str(path));
        }

        public void ClearSaveAs() { lock (_sync) { _saveAsQueue.Clear(); } }
        public int PendingSaveAs() { lock (_sync) { return _saveAsQueue.Count; } }
        public int Violations() { lock (_sync) { return _violations; } }
        public long Seq() { lock (_sync) { return _seq; } }
        public int AttachedCount() { lock (_sync) { int n = 0; foreach (DocHooks h in _hooks) if (!h.Destroyed && !h.Detached) n++; return n; } }

        // ------------------------------------------------------------ события приложения
        private int OnFileNew2(object newDoc, int docType, string templateName)
        {
            ModelDoc2 d = newDoc as ModelDoc2;
            AttachDoc(d);
            Write("FileNewNotify2", d, "\"docType\":" + docType + ",\"template\":" + Json.Str(templateName));
            return 0;
        }

        private int OnFileOpenPost(string fileName)
        {
            ModelDoc2 d = FindDoc(fileName);
            AttachDoc(d);
            Write("FileOpenPostNotify", d, "\"fileName\":" + Json.Str(fileName));
            return 0;
        }

        private int OnActiveDocChange()
        {
            ModelDoc2 d = null;
            try { d = _app.ActiveDoc as ModelDoc2; } catch { }
            Write("ActiveDocChangeNotify", d, null);
            return 0;
        }

        private int OnDocumentLoad2(string docTitle, string docPath)
        {
            Write("DocumentLoadNotify2", null, "\"docTitle\":" + Json.Str(docTitle) + ",\"docPath\":" + Json.Str(docPath));
            return 0;
        }

        private int OnCommandOpenPre(int command, int userCommand)
        {
            Write("CommandOpenPreNotify", ActiveDoc(), "\"command\":" + command + ",\"userCommand\":" + userCommand);
            return 0;
        }

        private int OnCommandClose(int command, int reason)
        {
            Write("CommandCloseNotify", ActiveDoc(), "\"command\":" + command + ",\"reason\":" + reason);
            return 0;
        }

        private int OnFileClose(string fileName, int reason)
        {
            Write("FileCloseNotify", null, "\"fileName\":" + Json.Str(fileName) + ",\"reason\":" + reason);
            return 0;
        }

        // ------------------------------------------------------------ события документа
        // Внутри событий документа зонд не обращается к SolidWorks: GetTitle/GetPathName/GetSaveFlag из внешнего
        // процесса посреди сохранения или записи свойств портят документ, и SolidWorks падает при его закрытии
        // (нарушение доступа в sldsessionu при DestroyNotify; цикл «открыть → сохранить → закрыть» падал за 1–2
        // повтора, 21.09.2026). Имя и путь берутся из кэша DocHooks, флаг изменённости не читается.
        private void WriteDoc(string evt, DocHooks h, string extra)
        {
            WriteRaw(evt, h.Title, h.Path, "null", extra);
        }

        private int OnSave(DocHooks h, string fileName)
        {
            WriteDoc("FileSaveNotify", h, "\"fileName\":" + Json.Str(fileName));
            return 0;
        }

        private int OnSaveAs2(DocHooks h, string fileName)
        {
            string queued = null;
            lock (_sync) { if (_saveAsQueue.Count > 0) queued = _saveAsQueue.Dequeue(); }
            if (queued == null)
            {
                WriteDoc("FileSaveAsNotify2", h, "\"fileName\":" + Json.Str(fileName));
                return 0;
            }
            // Единственный намеренный вызов в SolidWorks из события: подмена имени файла вместо диалога (C02).
            string error = null;
            try { h.Doc.SetSaveAsFileName(queued); }
            catch (Exception ex) { error = ex.Message; }
            WriteDoc("FileSaveAsNotify2", h, "\"fileName\":" + Json.Str(fileName) + ",\"substituted\":" + Json.Str(queued) + ",\"error\":" + Json.Str(error));
            return error == null ? 1 : 0; // 1 = S_FALSE: диалог не показывается, используется SetSaveAsFileName
        }

        private int OnSavePost(DocHooks h, int saveType, string fileName)
        {
            // После «Сохранить как» документ живёт под новым именем; копия (saveType 3) имени не меняет.
            if (saveType != 3 && !string.IsNullOrEmpty(fileName))
            {
                h.Path = fileName;
                h.Title = Path.GetFileName(fileName);
            }
            bool outside = false;
            if (!string.IsNullOrEmpty(_workspaceRoot) && !string.IsNullOrEmpty(fileName))
            {
                string full = Full(fileName);
                string root = Full(_workspaceRoot).TrimEnd('\\') + "\\";
                outside = !full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                if (outside) lock (_sync) { _violations++; }
            }
            WriteDoc("FileSavePostNotify", h, "\"saveType\":" + saveType + ",\"fileName\":" + Json.Str(fileName) + ",\"outsideWorkspace\":" + (outside ? "true" : "false"));
            return 0;
        }

        private int OnSavePostCancel(DocHooks h)
        {
            WriteDoc("FileSavePostCancelNotify", h, null);
            return 0;
        }

        private int OnAddProp(DocHooks h, string name, string cfg, string value, int type)
        {
            WriteDoc("AddCustomPropertyNotify", h, "\"name\":" + Json.Str(name) + ",\"cfg\":" + Json.Str(cfg) + ",\"value\":" + Json.Str(value) + ",\"type\":" + type);
            return 0;
        }

        private int OnChangeProp(DocHooks h, string name, string cfg, string oldValue, string newValue, int type)
        {
            WriteDoc("ChangeCustomPropertyNotify", h, "\"name\":" + Json.Str(name) + ",\"cfg\":" + Json.Str(cfg) + ",\"old\":" + Json.Str(oldValue) + ",\"new\":" + Json.Str(newValue) + ",\"type\":" + type);
            return 0;
        }

        private int OnDeleteProp(DocHooks h, string name, string cfg, string value, int type)
        {
            WriteDoc("DeleteCustomPropertyNotify", h, "\"name\":" + Json.Str(name) + ",\"cfg\":" + Json.Str(cfg) + ",\"value\":" + Json.Str(value) + ",\"type\":" + type);
            return 0;
        }

        private int OnDestroy(DocHooks h)
        {
            // Документ уже разрушается: к нему нельзя обращаться (падение SolidWorks при
            // GetTitle/GetSaveFlag на удаляемом объекте). Пишем кэшированные имя и путь, а
            // ссылку оставляем в списке до конца сессии, чтобы сборщик мусора не освобождал
            // RCW мёртвого документа.
            h.Destroyed = true;
            WriteRaw("DestroyNotify", h.Title, h.Path, "null", null);
            return 0;
        }

        // ------------------------------------------------------------ подписки
        public int DetachByTitle(string title)
        {
            int n = 0;
            lock (_sync)
            {
                foreach (DocHooks h in _hooks)
                {
                    if (h.Destroyed || h.Detached) continue;
                    string current = h.Title;
                    try { current = h.Doc.GetTitle() ?? h.Title; } catch { }
                    if (title == "*" || string.Equals(current, title, StringComparison.OrdinalIgnoreCase))
                    {
                        Unsubscribe(h);
                        n++;
                    }
                }
            }
            Write("Detached", null, "\"title\":" + Json.Str(title) + ",\"count\":" + n);
            return n;
        }

        // Отписка обязательна до закрытия документа: SolidWorks падает, если документ с
        // подписками внешнего процесса разрушается, а затем создаётся следующий документ.
        private static void Unsubscribe(DocHooks h)
        {
            h.Detached = true;
            try
            {
                if (h.Part != null)
                {
                    if (h.PSave != null) { h.Part.FileSaveNotify -= h.PSave; h.Part.FileSaveAsNotify2 -= h.PSaveAs; h.Part.FileSavePostNotify -= h.PPost; h.Part.FileSavePostCancelNotify -= h.PCancel; }
                    if (h.PAdd != null) { h.Part.AddCustomPropertyNotify -= h.PAdd; h.Part.ChangeCustomPropertyNotify -= h.PChange; h.Part.DeleteCustomPropertyNotify -= h.PDelete; }
                    if (h.PDestroy != null) h.Part.DestroyNotify -= h.PDestroy;
                }
                else if (h.Asm != null)
                {
                    if (h.ASave != null) { h.Asm.FileSaveNotify -= h.ASave; h.Asm.FileSaveAsNotify2 -= h.ASaveAs; h.Asm.FileSavePostNotify -= h.APost; h.Asm.FileSavePostCancelNotify -= h.ACancel; }
                    if (h.AAdd != null) { h.Asm.AddCustomPropertyNotify -= h.AAdd; h.Asm.ChangeCustomPropertyNotify -= h.AChange; h.Asm.DeleteCustomPropertyNotify -= h.ADelete; }
                    if (h.ADestroy != null) h.Asm.DestroyNotify -= h.ADestroy;
                }
                else if (h.Drw != null)
                {
                    if (h.DSave != null) { h.Drw.FileSaveNotify -= h.DSave; h.Drw.FileSaveAsNotify2 -= h.DSaveAs; h.Drw.FileSavePostNotify -= h.DPost; h.Drw.FileSavePostCancelNotify -= h.DCancel; }
                    if (h.DAdd != null) { h.Drw.AddCustomPropertyNotify -= h.DAdd; h.Drw.ChangeCustomPropertyNotify -= h.DChange; h.Drw.DeleteCustomPropertyNotify -= h.DDelete; }
                    if (h.DDestroy != null) h.Drw.DestroyNotify -= h.DDestroy;
                }
            }
            catch { }
        }

        private void AttachOpenDocuments()
        {
            try
            {
                ModelDoc2 d = _app.GetFirstDocument() as ModelDoc2;
                while (d != null)
                {
                    AttachDoc(d);
                    d = d.GetNext() as ModelDoc2;
                }
            }
            catch (Exception ex)
            {
                Write("ProbeError", null, "\"where\":\"AttachOpenDocuments\",\"message\":" + Json.Str(ex.Message));
            }
        }

        private ModelDoc2 ActiveDoc()
        {
            try { return _app.ActiveDoc as ModelDoc2; } catch { return null; }
        }

        private ModelDoc2 FindDoc(string path)
        {
            if (string.IsNullOrEmpty(path)) return ActiveDoc();
            try { return _app.GetOpenDocumentByName(path) as ModelDoc2 ?? ActiveDoc(); }
            catch { return ActiveDoc(); }
        }

        private void AttachDoc(ModelDoc2 d)
        {
            if (d == null || !DocEvents) return;
            lock (_sync)
            {
                foreach (DocHooks existing in _hooks)
                {
                    if (!existing.Destroyed && object.ReferenceEquals(existing.Doc, d)) return;
                }
                DocHooks h = new DocHooks();
                h.Doc = d;
                try { h.Title = d.GetTitle() ?? ""; } catch { }
                try { h.Path = d.GetPathName() ?? ""; } catch { }
                try
                {
                    int type = d.GetType();
                    if (type == (int)swDocumentTypes_e.swDocPART)
                    {
                        h.Part = (PartDoc)d;
                        if (SaveEvents)
                        {
                            h.PSave = delegate(string fn) { return OnSave(h, fn); }; h.Part.FileSaveNotify += h.PSave;
                            h.PSaveAs = delegate(string fn) { return OnSaveAs2(h, fn); }; h.Part.FileSaveAsNotify2 += h.PSaveAs;
                            h.PPost = delegate(int t, string fn) { return OnSavePost(h, t, fn); }; h.Part.FileSavePostNotify += h.PPost;
                            h.PCancel = delegate() { return OnSavePostCancel(h); }; h.Part.FileSavePostCancelNotify += h.PCancel;
                        }
                        if (PropEvents)
                        {
                            h.PAdd = delegate(string n, string c, string v, int t) { return OnAddProp(h, n, c, v, t); }; h.Part.AddCustomPropertyNotify += h.PAdd;
                            h.PChange = delegate(string n, string c, string o, string v, int t) { return OnChangeProp(h, n, c, o, v, t); }; h.Part.ChangeCustomPropertyNotify += h.PChange;
                            h.PDelete = delegate(string n, string c, string v, int t) { return OnDeleteProp(h, n, c, v, t); }; h.Part.DeleteCustomPropertyNotify += h.PDelete;
                        }
                        if (DestroyEvents) { h.PDestroy = delegate() { return OnDestroy(h); }; h.Part.DestroyNotify += h.PDestroy; }
                    }
                    else if (type == (int)swDocumentTypes_e.swDocASSEMBLY)
                    {
                        h.Asm = (AssemblyDoc)d;
                        if (SaveEvents)
                        {
                            h.ASave = delegate(string fn) { return OnSave(h, fn); }; h.Asm.FileSaveNotify += h.ASave;
                            h.ASaveAs = delegate(string fn) { return OnSaveAs2(h, fn); }; h.Asm.FileSaveAsNotify2 += h.ASaveAs;
                            h.APost = delegate(int t, string fn) { return OnSavePost(h, t, fn); }; h.Asm.FileSavePostNotify += h.APost;
                            h.ACancel = delegate() { return OnSavePostCancel(h); }; h.Asm.FileSavePostCancelNotify += h.ACancel;
                        }
                        if (PropEvents)
                        {
                            h.AAdd = delegate(string n, string c, string v, int t) { return OnAddProp(h, n, c, v, t); }; h.Asm.AddCustomPropertyNotify += h.AAdd;
                            h.AChange = delegate(string n, string c, string o, string v, int t) { return OnChangeProp(h, n, c, o, v, t); }; h.Asm.ChangeCustomPropertyNotify += h.AChange;
                            h.ADelete = delegate(string n, string c, string v, int t) { return OnDeleteProp(h, n, c, v, t); }; h.Asm.DeleteCustomPropertyNotify += h.ADelete;
                        }
                        if (DestroyEvents) { h.ADestroy = delegate() { return OnDestroy(h); }; h.Asm.DestroyNotify += h.ADestroy; }
                    }
                    else if (type == (int)swDocumentTypes_e.swDocDRAWING)
                    {
                        h.Drw = (DrawingDoc)d;
                        if (SaveEvents)
                        {
                            h.DSave = delegate(string fn) { return OnSave(h, fn); }; h.Drw.FileSaveNotify += h.DSave;
                            h.DSaveAs = delegate(string fn) { return OnSaveAs2(h, fn); }; h.Drw.FileSaveAsNotify2 += h.DSaveAs;
                            h.DPost = delegate(int t, string fn) { return OnSavePost(h, t, fn); }; h.Drw.FileSavePostNotify += h.DPost;
                            h.DCancel = delegate() { return OnSavePostCancel(h); }; h.Drw.FileSavePostCancelNotify += h.DCancel;
                        }
                        if (PropEvents)
                        {
                            h.DAdd = delegate(string n, string c, string v, int t) { return OnAddProp(h, n, c, v, t); }; h.Drw.AddCustomPropertyNotify += h.DAdd;
                            h.DChange = delegate(string n, string c, string o, string v, int t) { return OnChangeProp(h, n, c, o, v, t); }; h.Drw.ChangeCustomPropertyNotify += h.DChange;
                            h.DDelete = delegate(string n, string c, string v, int t) { return OnDeleteProp(h, n, c, v, t); }; h.Drw.DeleteCustomPropertyNotify += h.DDelete;
                        }
                        if (DestroyEvents) { h.DDestroy = delegate() { return OnDestroy(h); }; h.Drw.DestroyNotify += h.DDestroy; }
                    }
                    else
                    {
                        return;
                    }
                    _hooks.Add(h);
                }
                catch (Exception ex)
                {
                    Write("ProbeError", d, "\"where\":\"AttachDoc\",\"message\":" + Json.Str(ex.Message));
                }
            }
        }

        // ------------------------------------------------------------ журнал
        public void Write(string evt, ModelDoc2 d, string extra)
        {
            string title = "", path = "", dirty = "null";
            if (d != null)
            {
                try { title = d.GetTitle() ?? ""; } catch { }
                try { path = d.GetPathName() ?? ""; } catch { }
                try { dirty = d.GetSaveFlag() ? "true" : "false"; } catch { }
            }
            WriteRaw(evt, title, path, dirty, extra);
        }

        private void WriteRaw(string evt, string title, string path, string dirty, string extra)
        {
            lock (_sync)
            {
                _seq++;
                StringBuilder sb = new StringBuilder(256);
                sb.Append("{\"seq\":").Append(_seq);
                sb.Append(",\"t\":").Append(Json.Str(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")));
                sb.Append(",\"mark\":").Append(Json.Str(_mark));
                sb.Append(",\"event\":").Append(Json.Str(evt));
                sb.Append(",\"title\":").Append(Json.Str(title));
                sb.Append(",\"path\":").Append(Json.Str(path));
                sb.Append(",\"dirty\":").Append(dirty);
                if (!string.IsNullOrEmpty(extra)) sb.Append(",").Append(extra);
                sb.Append("}\n");
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        File.AppendAllText(_journal, sb.ToString(), new UTF8Encoding(false));
                        break;
                    }
                    catch (IOException) { Thread.Sleep(10); }
                }
            }
        }

        private static string Full(string p)
        {
            try { return Path.GetFullPath(p); } catch { return p ?? ""; }
        }
    }
}
