using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using ESKD.MaterialSync.Sw;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace ESKD.MaterialSync
{
    /// <summary>
    /// Надстройка ЕСКД v6: оболочка COM. События — Sw.EventHub, синхронизация — Sw.SyncService,
    /// кнопка «Деталь БЧ» — Sw.BchService. CLSID и ProgId не меняются: регистрация рабочих мест остаётся прежней.
    /// </summary>
    [Guid("B64E6875-B101-4D5C-B245-FF8D50772E25")]
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    [ProgId("ESKD.MaterialSync.SwAddin_v5")]
    public class SwAddin : ISwAddin
    {
        public const string Version = "6.0.0";
        private const int CommandGroupId = 9997;
        private const string TabTitle = "ЕСКД";
        private static readonly int[] CommandUserIds = { 9900, 9901, 9902, 9903 };

        private ISldWorks _app;
        private ICommandManager _commands;
        private int _cookie;
        private EventHub _hub;

        static SwAddin()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveNextToAddin;
        }

        private static Assembly ResolveNextToAddin(object sender, ResolveEventArgs args)
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(SwAddin).Assembly.Location);
                string candidate = Path.Combine(dir ?? "", new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch (Exception ex)
            {
                Core.Log.Error("AssemblyResolve " + args.Name, ex);
                return null;
            }
        }

        // Совместимость: прежние макросы и тесты вызывали SwAddin.Log.
        public static void Log(string message)
        {
            Core.Log.Info(message);
        }

        public bool ConnectToSW(object ThisSW, int cookie)
        {
            Core.Log.Info("=== ConnectToSW v" + Version + ", cookie " + cookie + " ===");
            try
            {
                _app = (ISldWorks)ThisSW;
                _cookie = cookie;
                _app.SetAddinCallbackInfo2(0, this, cookie);
                CreateCommands();
                _hub = new EventHub(_app);
                _hub.Attach();
                return true;
            }
            catch (Exception ex)
            {
                Core.Log.Error("ConnectToSW", ex);
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            Core.Log.Info("=== DisconnectFromSW ===");
            try
            {
                if (_hub != null) _hub.Detach();
                RemoveCommands();
            }
            catch (Exception ex)
            {
                Core.Log.Error("DisconnectFromSW", ex);
            }
            _hub = null;
            _commands = null;
            _app = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        // ------------------------------------------------------------------ вкладка и меню
        private void CreateCommands()
        {
            _commands = _app.GetCommandManager(_cookie);
            if (_commands == null)
            {
                Core.Log.Error("GetCommandManager вернул null");
                return;
            }
            object registryIds;
            bool haveRegistry = _commands.GetGroupDataFromRegistry(CommandGroupId, out registryIds);
            bool ignorePrevious = !haveRegistry || !SameIds(registryIds as int[], CommandUserIds);

            int error = 0;
            ICommandGroup group = _commands.CreateCommandGroup2(CommandGroupId, TabTitle,
                "Инструменты ЕСКД: реквизиты, материал, масса, безчертёжные детали", "", -1, ignorePrevious, ref error);
            if (group == null)
            {
                Core.Log.Error("CreateCommandGroup2: код " + error);
                return;
            }
            string iconDir = Path.Combine(Path.GetDirectoryName(typeof(SwAddin).Assembly.Location) ?? "", "Icons");
            SetIcon(iconDir, "icons_large.bmp", delegate(string p) { group.LargeIconList = p; });
            SetIcon(iconDir, "icons_small.bmp", delegate(string p) { group.SmallIconList = p; });
            SetIcon(iconDir, "main_24.bmp", delegate(string p) { group.LargeMainIcon = p; });
            SetIcon(iconDir, "main_16.bmp", delegate(string p) { group.SmallMainIcon = p; });

            // Пустой первый элемент занимает внутренний идентификатор, который SolidWorks сопоставляет с Routing.
            group.AddCommandItem2("", -1, "", "", 0, "", "", CommandUserIds[0], 0);
            int buttons = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
            int settings = group.AddCommandItem2("Настройки ЕСКД", -1, "Фамилии, организация, масса, флаги синхронизации",
                "Настройки ЕСКД", 0, "ShowSettings", "EnableCommand", CommandUserIds[1], buttons);
            int sync = group.AddCommandItem2("Синхронизировать", -1, "Обновить реквизиты, материал и массу активного документа",
                "Синхронизировать", 1, "SyncCurrentDoc", "EnableCommand", CommandUserIds[2], buttons);
            int bch = group.AddCommandItem2("Деталь БЧ", -1, "Установить или снять признак безчертёжной детали (ГОСТ 2.109)",
                "Деталь БЧ", 2, "ToggleDrawingless", "EnablePartCommand", CommandUserIds[3], buttons);
            group.HasToolbar = true;
            group.HasMenu = true;
            group.Activate();
            try
            {
                if (group.ToolbarId > 0) _app.SetToolbarVisibility(group.ToolbarId, false);
            }
            catch (Exception ex)
            {
                Core.Log.Error("SetToolbarVisibility", ex);
            }

            int[] ids = { group.get_CommandID(settings), group.get_CommandID(sync), group.get_CommandID(bch) };
            foreach (int docType in new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING })
            {
                try
                {
                    CommandTab tab = _commands.GetCommandTab(docType, TabTitle);
                    if (tab != null && ignorePrevious)
                    {
                        _commands.RemoveCommandTab(tab);
                        tab = null;
                    }
                    if (tab == null)
                    {
                        tab = _commands.AddCommandTab(docType, TabTitle);
                        CommandTabBox box = tab.AddCommandTabBox();
                        bool part = docType == (int)swDocumentTypes_e.swDocPART;
                        int below = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;
                        box.AddCommands(part ? ids : new[] { ids[0], ids[1] }, part ? new[] { below, below, below } : new[] { below, below });
                    }
                }
                catch (Exception ex)
                {
                    Core.Log.Error("Вкладка ЕСКД для типа " + docType, ex);
                }
            }
        }

        private void RemoveCommands()
        {
            if (_commands == null) return;
            try
            {
                _commands.RemoveCommandGroup2(CommandGroupId, true);
            }
            catch (Exception ex)
            {
                Core.Log.Error("RemoveCommandGroup2", ex);
            }
        }

        private static bool SameIds(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void SetIcon(string dir, string name, Action<string> apply)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) apply(path);
        }

        // ------------------------------------------------------------------ команды (вызываются SolidWorks и через COM)
        public int EnableCommand()
        {
            return 1;
        }

        public int EnablePartCommand()
        {
            try
            {
                ModelDoc2 doc = _app.ActiveDoc as ModelDoc2;
                return doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocPART ? 1 : 0;
            }
            catch (COMException)
            {
                // SolidWorks опрашивает состояние кнопки постоянно; занятый COM — просто «недоступна».
                return 0;
            }
        }

        public void ShowSettings()
        {
            try
            {
                using (SettingsForm form = new SettingsForm(_app))
                {
                    IntPtr hwnd = IntPtr.Zero;
                    try
                    {
                        Frame frame = _app.Frame() as Frame;
                        if (frame != null) hwnd = new IntPtr(frame.GetHWnd());
                    }
                    catch (Exception ex)
                    {
                        Core.Log.Error("Frame", ex);
                    }
                    if (hwnd != IntPtr.Zero) form.ShowDialog(new WindowWrapper(hwnd));
                    else form.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                Core.Log.Error("ShowSettings", ex);
                MessageBox.Show("Ошибка открытия настроек ЕСКД: " + ex.Message, "ЕСКД", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void SyncCurrentDoc()
        {
            int changes = SyncActiveDocumentSilent();
            try
            {
                if (changes > 0) StatusText("ЕСКД: обновлено свойств — " + changes);
                else if (changes == 0) StatusText("ЕСКД: реквизиты актуальны");
            }
            catch (Exception ex)
            {
                Core.Log.Error("SetStatusBarText", ex);
            }
        }

        /// <summary>Синхронизация активного документа без интерфейса: число изменённых свойств или -1.</summary>
        public int SyncActiveDocumentSilent()
        {
            try
            {
                ModelDoc2 doc = _app.ActiveDoc as ModelDoc2;
                if (doc == null) return -1;
                SyncReport report = SyncService.SyncExplicit(_app, doc);
                return report.Skipped ? -1 : report.Changes;
            }
            catch (Exception ex)
            {
                Core.Log.Error("SyncActiveDocumentSilent", ex);
                return -1;
            }
        }

        public void ToggleDrawingless()
        {
            int result = ToggleDrawinglessSilent();
            if (result == BchService.NotPart)
            {
                MessageBox.Show("Признак БЧ применяется только к деталям.", "ЕСКД: Деталь БЧ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                StatusText(result == BchService.Enabled
                    ? "ЕСКД: деталь оформлена как безчертёжная (Формат = БЧ, масса в «Примечании»)"
                    : "ЕСКД: признак безчертёжной детали снят, прежние «Формат» и «Примечание» восстановлены");
            }
            catch (Exception ex)
            {
                Core.Log.Error("SetStatusBarText", ex);
            }
        }

        /// <summary>0 — не деталь; 1 — признак БЧ установлен; 2 — снят.</summary>
        public int ToggleDrawinglessSilent()
        {
            try
            {
                return BchService.Toggle(_app, _app.ActiveDoc as ModelDoc2);
            }
            catch (Exception ex)
            {
                Core.Log.Error("ToggleDrawinglessSilent", ex);
                return BchService.NotPart;
            }
        }

        /// <summary>Отчёт «что будет записано» для активного документа — без записи.</summary>
        public string DiagnoseActiveDocument()
        {
            try
            {
                ModelDoc2 doc = _app.ActiveDoc as ModelDoc2;
                if (doc == null) return "нет активного документа";
                if (doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING) doc = SyncService.ReferencedModel((DrawingDoc)doc);
                SyncReport report = SyncService.SyncModel(_app, doc, new SyncRequest { Reason = "диагностика", DryRun = true });
                return report + (report.Operations.Count > 0 ? "\n" + string.Join("\n", report.Operations.ToArray()) : "") +
                       (report.Warnings.Count > 0 ? "\nПредупреждения:\n" + string.Join("\n", report.Warnings.ToArray()) : "");
            }
            catch (Exception ex)
            {
                Core.Log.Error("DiagnoseActiveDocument", ex);
                return "ошибка: " + ex.Message;
            }
        }

        private void StatusText(string text)
        {
            Frame frame = _app != null ? _app.Frame() as Frame : null;
            if (frame != null) frame.SetStatusBarText(text);
        }

        public string GetVersion()
        {
            return Version;
        }

        public bool ActivateTab(int docType)
        {
            try
            {
                CommandTab tab = _commands != null ? _commands.GetCommandTab(docType, TabTitle) : null;
                if (tab == null) return false;
                tab.Visible = true;
                tab.Active = true;
                return true;
            }
            catch (Exception ex)
            {
                Core.Log.Error("ActivateTab", ex);
                return false;
            }
        }
    }
}
