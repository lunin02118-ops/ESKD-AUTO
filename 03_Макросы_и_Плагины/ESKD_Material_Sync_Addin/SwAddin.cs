using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace ESKD.MaterialSync
{
    [Guid("B64E6875-B101-4D5C-B245-FF8D50772E25")]
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    [ProgId("ESKD.MaterialSync.SwAddin_v5")]
    public class SwAddin : ISwAddin
    {
        static SwAddin()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            }
            catch { }
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string loc = typeof(SwAddin).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    string simpleName = new AssemblyName(args.Name).Name;
                    string candidate = Path.Combine(dir, simpleName + ".dll");
                    if (File.Exists(candidate))
                    {
                        return Assembly.LoadFrom(candidate);
                    }
                }
            }
            catch { }
            return null;
        }

        private const int SwCmdEditMaterial = 175; // swCommands_EditMaterial
        private const int CommandGroupId = 9997;

        private ISldWorks iSwApp;
        private ICommandManager iCmdMgr;
        private int iSwCookie;
        private bool _isSyncing = false;

        /// <summary>
        /// D-3: подписки FileSaveNotify/FileSaveAsNotify2/DestroyNotify одного документа.
        /// Делегаты хранятся явно, чтобы их можно было снять оператором -=
        /// при DestroyNotify документа и в DisconnectFromSW.
        /// </summary>
        private class DocEventHooks
        {
            public string Key;
            public ModelDoc2 Doc;
            public int DocType;
            public DPartDocEvents_FileSaveNotifyEventHandler PartFileSave;
            public DPartDocEvents_FileSaveAsNotify2EventHandler PartFileSaveAs;
            public DPartDocEvents_DestroyNotifyEventHandler PartDestroy;
            public DAssemblyDocEvents_FileSaveNotifyEventHandler AsmFileSave;
            public DAssemblyDocEvents_FileSaveAsNotify2EventHandler AsmFileSaveAs;
            public DAssemblyDocEvents_DestroyNotifyEventHandler AsmDestroy;
            public DDrawingDocEvents_FileSaveNotifyEventHandler DrwFileSave;
            public DDrawingDocEvents_FileSaveAsNotify2EventHandler DrwFileSaveAs;
            public DDrawingDocEvents_DestroyNotifyEventHandler DrwDestroy;
        }

        // D-3: ключ хука — стабильный (полный путь документа, при его отсутствии — заголовок),
        // а не нестабильный doc.GetHashCode()
        private readonly Dictionary<string, DocEventHooks> _docHooks =
            new Dictionary<string, DocEventHooks>(StringComparer.OrdinalIgnoreCase);

        public static void Log(string msg)
        {
            try
            {
                string logFile = Path.Combine(Path.GetTempPath(), "eskd_material_sync.log");

                // D-15: ротация лога — при превышении 5 МБ файл перезаписывается (обрезается) с пометкой
                try
                {
                    FileInfo fi = new FileInfo(logFile);
                    if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                    {
                        File.WriteAllText(logFile, string.Format(
                            "[{0:yyyy-MM-dd HH:mm:ss.fff}] LOG ROTATED: eskd_material_sync.log превысил 5 МБ и был усечён\r\n",
                            DateTime.Now));
                    }
                }
                catch { }

                File.AppendAllText(logFile, string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}\r\n", DateTime.Now, msg));
            }
            catch { }
        }

        public bool ConnectToSW(object ThisSW, int cookie)
        {
            Log("=== ConnectToSW called. Cookie: " + cookie + " ===");
            try
            {
                iSwApp = (ISldWorks)ThisSW;
                iSwCookie = cookie;

                try
                {
                    bool cbOk = iSwApp.SetAddinCallbackInfo2(0L, this, cookie);
                    Log("SetAddinCallbackInfo2 result: " + cbOk);
                }
                catch { }

                // 1. Build UI: CommandManager and Menus
                try
                {
                    AddCommandManager();
                    AddMenuItems();
                    Log("UI (CommandManager & Menus) created successfully.");
                }
                catch (Exception exUI)
                {
                    Log("UI creation exception: " + exUI.Message);
                }

                // 2. Hook application-level document lifecycle events
                try
                {
                    AttachAppEvents();
                    Log("AttachAppEvents completed.");
                }
                catch (Exception exEv)
                {
                    Log("AttachAppEvents exception: " + exEv.Message);
                }

                // 3. Hook current active document if already open
                try
                {
                    ModelDoc2 currentDoc = (ModelDoc2)iSwApp.ActiveDoc;
                    if (currentDoc != null)
                    {
                        AttachDocEvents(currentDoc);
                        Log("AttachDocEvents for active doc: " + currentDoc.GetTitle());
                    }
                }
                catch { }

                Log("=== ConnectToSW finished successfully. ===");
            }
            catch (Exception exGlobal)
            {
                Log("CRITICAL GLOBAL EXCEPTION in ConnectToSW: " + exGlobal.ToString());
                // D-16: после глобального сбоя инициализации аддин обязан сообщить SolidWorks о неудаче
                return false;
            }

            return true;
        }

        public bool DisconnectFromSW()
        {
            Log("=== DisconnectFromSW called. ===");
            try
            {
                RemoveCommandManager();
                DetachAppEvents();
                DetachAllDocEvents();
                iSwApp = null;
                Log("DisconnectFromSW finished successfully.");
            }
            catch (Exception ex)
            {
                Log("DisconnectFromSW exception: " + ex.Message);
            }
            return true;
        }

        public bool ActivateTab(int docType)
        {
            try
            {
                Log("ActivateTab called for docType=" + docType);
                if (iCmdMgr == null)
                {
                    Log("ActivateTab: iCmdMgr is null");
                    return false;
                }
                CommandTab tab = iCmdMgr.GetCommandTab(docType, "ЕСКД");
                if (tab == null)
                {
                    try
                    {
                        object tabsObj = iCmdMgr.CommandTabs(docType);
                        if (tabsObj != null)
                        {
                            foreach (object o in (object[])tabsObj)
                            {
                                CommandTab t = (CommandTab)o;
                                if (t != null && !string.IsNullOrEmpty(t.Name) && t.Name.StartsWith("ЕСКД"))
                                {
                                    tab = t;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (tab != null)
                {
                    tab.Visible = true;
                    tab.Active = true;
                    Log("ActivateTab: tab found and activated. Name=" + tab.Name + ", Visible=" + tab.Visible + ", Active=" + tab.Active);
                    return true;
                }
                else
                {
                    Log("ActivateTab: tab is null for docType=" + docType);
                }
            }
            catch (Exception ex)
            {
                Log("ActivateTab exception: " + ex.Message);
            }
            return false;
        }

        #region CommandManager & Menu Integration

        public void AddCommandManager()
        {
            try
            {
                Log("AddCommandManager: Start");
                iCmdMgr = iSwApp.GetCommandManager(iSwCookie);
                if (iCmdMgr == null)
                {
                    Log("AddCommandManager: iCmdMgr is null!");
                    return;
                }

                int cmdGroupErr = 0;
                bool ignorePreviousVersion = false;
                object registryIDsObj;
                bool getRegResult = iCmdMgr.GetGroupDataFromRegistry(CommandGroupId, out registryIDsObj);
                int[] expectedIDs = new int[] { 9900, 9901, 9902 };
                if (!getRegResult || registryIDsObj == null)
                {
                    ignorePreviousVersion = true;
                }
                else
                {
                    int[] regIDs = (int[])registryIDsObj;
                    if (regIDs.Length != expectedIDs.Length)
                    {
                        ignorePreviousVersion = true;
                    }
                    else
                    {
                        for (int i = 0; i < expectedIDs.Length; i++)
                        {
                            if (regIDs[i] != expectedIDs[i])
                            {
                                ignorePreviousVersion = true;
                                break;
                            }
                        }
                    }
                }
                Log("AddCommandManager: Calling CreateCommandGroup2 with ID " + CommandGroupId + ", ignorePrevious=" + ignorePreviousVersion);
                ICommandGroup cmdGroup = iCmdMgr.CreateCommandGroup2(
                    CommandGroupId,
                    "ЕСКД",
                    "Инструменты ЕСКД: Настройки реквизитов, синхронизация материалов и массы",
                    "",
                    -1,
                    ignorePreviousVersion,
                    ref cmdGroupErr);

                if (cmdGroup != null)
                {
                    Log("AddCommandManager: cmdGroup created successfully");
                    string asmDir = Path.GetDirectoryName(typeof(SwAddin).Assembly.Location) ?? "";
                    string iconDir = Path.Combine(asmDir, "Icons");
                    string iconSmall = Path.Combine(iconDir, "icons_small.bmp");
                    string iconLarge = Path.Combine(iconDir, "icons_large.bmp");
                    string mainSmall = Path.Combine(iconDir, "main_16.bmp");
                    string mainLarge = Path.Combine(iconDir, "main_24.bmp");

                    if (File.Exists(iconLarge)) cmdGroup.LargeIconList = iconLarge;
                    if (File.Exists(iconSmall)) cmdGroup.SmallIconList = iconSmall;
                    if (File.Exists(mainLarge)) cmdGroup.LargeMainIcon = mainLarge;
                    if (File.Exists(mainSmall)) cmdGroup.SmallMainIcon = mainSmall;

                    // Dummy command at index 0 to consume command ID 41655 (which SW internal resources map to Routing)
                    cmdGroup.AddCommandItem2(
                        "",
                        -1,
                        "",
                        "",
                        0,
                        "",
                        "",
                        9900,
                        0);

                    int cmdIndexSettings = cmdGroup.AddCommandItem2(
                        "Настройки ЕСКД",
                        -1,
                        "Открыть настройки реквизитов ЕСКД (фамилии, контора, масса)",
                        "Настройки ЕСКД",
                        0,
                        "ShowSettings",
                        "EnableCommand",
                        9901,
                        (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

                    int cmdIndexSync = cmdGroup.AddCommandItem2(
                        "Синхронизировать",
                        -1,
                        "Синхронизировать материал, массу и штамп в активном документе",
                        "Синхронизировать",
                        1,
                        "SyncCurrentDoc",
                        "EnableCommand",
                        9902,
                        (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

                    int cmdIndexBch = cmdGroup.AddCommandItem2(
                        "Деталь БЧ",
                        -1,
                        "Пометить/снять признак безчертёжной детали (БЧ) — ГОСТ 2.109: индекс БЧ попадает в спецификацию",
                        "Деталь БЧ",
                        2,
                        "ToggleDrawingless",
                        "EnablePartCommand",
                        9903,
                        (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

                    cmdGroup.HasToolbar = true; // Must be true so buttons exist for CommandTabBox
                    cmdGroup.HasMenu = true;
                    try
                    {
                        cmdGroup.ShowInDocumentType = 0; // Prevent SW from automatically showing toolbar in documents!
                    }
                    catch { }

                    Log("AddCommandManager: Activating cmdGroup");
                    cmdGroup.Activate();

                    try
                    {
                        int tbId = cmdGroup.ToolbarId;
                        Log("AddCommandManager: ToolbarId=" + tbId);
                        if (tbId > 0)
                        {
                            iSwApp.SetToolbarVisibility(tbId, false);
                            iSwApp.HideToolbar2(iSwCookie, tbId);
                        }
                    }
                    catch { }

                    int cmdIDSettings = cmdGroup.get_CommandID(cmdIndexSettings);
                    int cmdIDSync = cmdGroup.get_CommandID(cmdIndexSync);
                    int cmdIDBch = cmdGroup.get_CommandID(cmdIndexBch);
                    Log("AddCommandManager: cmdIDSettings=" + cmdIDSettings + ", cmdIDSync=" + cmdIDSync + ", cmdIDBch=" + cmdIDBch);

                    int[] docTypes = new int[] {
                        (int)swDocumentTypes_e.swDocPART,
                        (int)swDocumentTypes_e.swDocASSEMBLY,
                        (int)swDocumentTypes_e.swDocDRAWING
                    };

                    Log("AddCommandManager: Setting up CommandTabs");
                    foreach (int dt in docTypes)
                    {
                        try
                        {
                            CommandTab tab = null;
                            try
                            {
                                object tabsObj = iCmdMgr.CommandTabs(dt);
                                if (tabsObj != null)
                                {
                                    foreach (object o in (object[])tabsObj)
                                    {
                                        CommandTab t = (CommandTab)o;
                                        if (t != null && !string.IsNullOrEmpty(t.Name) && t.Name.StartsWith("ЕСКД"))
                                        {
                                            if (tab == null)
                                            {
                                                tab = t;
                                                try { tab.Name = "ЕСКД"; } catch { }
                                                Log("AddCommandManager: Reusing existing CommandTab for dt=" + dt + " (" + t.Name + ")");
                                            }
                                            else
                                            {
                                                Log("AddCommandManager: Removing duplicate CommandTab for dt=" + dt + " (" + t.Name + ")");
                                                try { iCmdMgr.RemoveCommandTab(t); } catch { }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }

                            if (tab == null)
                            {
                                tab = iCmdMgr.AddCommandTab(dt, "ЕСКД");
                                Log("AddCommandManager: Created new CommandTab for dt=" + dt);
                            }

                            if (tab != null)
                            {
                                tab.Visible = true;
                                CommandTabBox box = null;
                                object boxesObj = null;
                                try { boxesObj = tab.CommandTabBoxes(); } catch { }

                                if (boxesObj != null && ((object[])boxesObj).Length > 0)
                                {
                                    box = (CommandTabBox)((object[])boxesObj)[0];
                                }
                                else
                                {
                                    box = tab.AddCommandTabBox();
                                }

                                if (box != null)
                                {
                                    try
                                    {
                                        object curCmds;
                                        object curTextStyles;
                                        int cmdCount = box.GetCommands(out curCmds, out curTextStyles);
                                        if (cmdCount > 0 && curCmds != null)
                                        {
                                            box.RemoveCommands(curCmds);
                                        }
                                    }
                                    catch { }

                                    // Кнопка «Деталь БЧ» имеет смысл только в контексте детали
                                    int[] cmdIDs = (dt == (int)swDocumentTypes_e.swDocPART)
                                        ? new int[] { cmdIDSettings, cmdIDSync, cmdIDBch }
                                        : new int[] { cmdIDSettings, cmdIDSync };
                                    int ttBelow = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;
                                    int[] textTypes = (dt == (int)swDocumentTypes_e.swDocPART)
                                        ? new int[] { ttBelow, ttBelow, ttBelow }
                                        : new int[] { ttBelow, ttBelow };
                                    bool addOk = box.AddCommands(cmdIDs, textTypes);
                                    Log("AddCommandManager: box.AddCommands result for dt=" + dt + ": " + addOk);
                                }
                            }
                        }
                        catch (Exception exTab)
                        {
                            Log("CommandTab setup error for dt=" + dt + ": " + exTab.Message);
                        }
                    }
                    Log("AddCommandManager: Completed successfully");
                }
                else
                {
                    Log("AddCommandManager: cmdGroup is null! Error code=" + cmdGroupErr);
                }
            }
            catch (Exception ex)
            {
                Log("AddCommandManager exception: " + ex.ToString());
            }
        }

        public void RemoveCommandManager()
        {
            try
            {
                if (iCmdMgr != null)
                {
                    try { iCmdMgr.RemoveCommandGroup2(CommandGroupId, true); } catch { }
                }
            }
            catch { }
        }

        public void HideGroupToolbar()
        {
            try
            {
                if (iCmdMgr != null && iSwApp != null)
                {
                    foreach (int gid in new int[] { CommandGroupId, 9995, 9996 })
                    {
                        try
                        {
                            ICommandGroup cg = iCmdMgr.GetCommandGroup(gid);
                            if (cg != null)
                            {
                                int tbId = cg.ToolbarId;
                                if (tbId > 0)
                                {
                                    iSwApp.SetToolbarVisibility(tbId, false);
                                    if (iSwCookie > 0) iSwApp.HideToolbar2(iSwCookie, tbId);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public void AddMenuItems()
        {
            try
            {
                int[] docTypes = new int[] {
                    (int)swDocumentTypes_e.swDocNONE,
                    (int)swDocumentTypes_e.swDocPART,
                    (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swDocumentTypes_e.swDocDRAWING
                };

                foreach (int dt in docTypes)
                {
                    try
                    {
                        iSwApp.AddMenuItem3(
                            dt,
                            iSwCookie,
                            "Настройки ЕСКД...@Tools",
                            -1,
                            "ShowSettings",
                            "EnableCommand",
                            "Настройка реквизитов основной надписи и параметров ЕСКД",
                            "");
                        iSwApp.AddMenuItem3(
                            dt,
                            iSwCookie,
                            "Настройки ЕСКД...@Инструменты",
                            -1,
                            "ShowSettings",
                            "EnableCommand",
                            "Настройка реквизитов основной надписи и параметров ЕСКД",
                            "");

                        if (dt != (int)swDocumentTypes_e.swDocNONE)
                        {
                            iSwApp.AddMenuItem3(
                                dt,
                                iSwCookie,
                                "Синхронизировать ЕСКД@Tools",
                                -1,
                                "SyncCurrentDoc",
                                "EnableCommand",
                                "Синхронизировать свойства материала, массы и реквизитов активного документа",
                                "");
                            iSwApp.AddMenuItem3(
                                dt,
                                iSwCookie,
                                "Синхронизировать ЕСКД@Инструменты",
                                -1,
                                "SyncCurrentDoc",
                                "EnableCommand",
                                "Синхронизировать свойства материала, массы и реквизитов активного документа",
                                "");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("AddMenuItems exception: " + ex.Message);
            }
        }

        public void ShowSettings()
        {
            try
            {
                using (SettingsForm form = new SettingsForm(iSwApp))
                {
                    IntPtr swHwnd = IntPtr.Zero;
                    try
                    {
                        Frame frame = (Frame)iSwApp.Frame();
                        if (frame != null) swHwnd = new IntPtr(frame.GetHWnd());
                    }
                    catch { }

                    if (swHwnd != IntPtr.Zero)
                    {
                        form.ShowDialog(new WindowWrapper(swHwnd));
                    }
                    else
                    {
                        form.ShowDialog();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("ShowSettings error: " + ex.ToString());
                MessageBox.Show("Ошибка открытия настроек ЕСКД: " + ex.Message, "ЕСКД", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =====================================================================================
        // Безчертёжные детали (БЧ), ГОСТ 2.109: тоггл признака на активной детали.
        // Метод public — доступен и через COM (GetAddInObject) для автотестов.
        // =====================================================================================
        public void ToggleDrawingless()
        {
            try
            {
                int result = ToggleDrawinglessSilent();
                if (result == 0)
                {
                    MessageBox.Show("Признак БЧ применяется только к деталям (активный документ — не деталь).",
                        "ЕСКД: Деталь БЧ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                MessageBox.Show(
                    result == 1
                        ? "Деталь оформлена как БЕЗЧЕРТЁЖНАЯ (БЧ) по ГОСТ 2.109-73 п. 3.3:\n\n• Графа «Формат»: БЧ\n• Графа «Наименование»: сортамент заготовки и определяющие размеры (L / BxL)\n• Графа «Примечание»: расчётная масса заготовки\n\nДанные сформированы и будут выведены в спецификацию изделия."
                        : "Признак БЧ снят: стандартный формат чертежа (А3), исходное наименование и свойства восстановлены.",
                    "ЕСКД: Деталь БЧ", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("ToggleDrawingless exception: " + ex.Message);
            }
        }

        // Тихий вариант без UI — для автоматизации (COM GetAddInObject) и макросов.
        // Возвращает 0 — не деталь/нет документа; 1 — БЧ установлен; 2 — БЧ снят.
        public int ToggleDrawinglessSilent()
        {
            try
            {
                ModelDoc2 doc = (ModelDoc2)iSwApp.ActiveDoc;
                if (doc == null) return 0;
                int result = MaterialSyncEngine.MarkDrawinglessPart(doc);
                if (result != 0)
                {
                    Log("ToggleDrawingless: " + (result == 1 ? "установлен" : "снят") + " для " + doc.GetTitle());
                }
                return result;
            }
            catch (Exception ex)
            {
                Log("ToggleDrawinglessSilent exception: " + ex.Message);
                return 0;
            }
        }

        public int EnablePartCommand()
        {
            try
            {
                ModelDoc2 doc = (ModelDoc2)iSwApp.ActiveDoc;
                if (doc == null) return 0;
                return doc.GetType() == (int)swDocumentTypes_e.swDocPART ? 1 : 0;
            }
            catch { return 0; }
        }

        public void SyncCurrentDoc()
        {
            try
            {
                ModelDoc2 doc = (ModelDoc2)iSwApp.ActiveDoc;
                if (doc != null)
                {
                    MaterialSyncEngine.SyncModelProperties(doc, iSwApp, true);
                    // Zero-Drift: ForceRebuild3 смещает заметки чертежа (до 9 мм) — для
                    // чертежей принудительный ребилд не выполняется.
                    if (doc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                    {
                        doc.ForceRebuild3(true);
                    }
                    Log("SyncCurrentDoc successfully completed for: " + doc.GetTitle());
                }
                else
                {
                    Log("SyncCurrentDoc called with no active doc.");
                }
            }
            catch (Exception ex)
            {
                Log("SyncCurrentDoc error: " + ex.ToString());
            }
        }

        public int EnableCommand()
        {
            return 1;
        }

        #endregion

        #region Application & Document Lifecycle Events

        private void AttachAppEvents()
        {
            try
            {
                SldWorks app = (SldWorks)iSwApp;
                app.FileOpenPostNotify += OnFileOpenPost;
                app.FileNewNotify2 += OnFileNew2;
                app.ActiveDocChangeNotify += OnActiveDocChange;
                app.CommandCloseNotify += OnCommandClose;
            }
            catch (Exception ex)
            {
                Log("AttachAppEvents exception: " + ex.Message);
            }
        }

        private void DetachAppEvents()
        {
            try
            {
                SldWorks app = (SldWorks)iSwApp;
                if (app != null)
                {
                    app.FileOpenPostNotify -= OnFileOpenPost;
                    app.FileNewNotify2 -= OnFileNew2;
                    app.ActiveDocChangeNotify -= OnActiveDocChange;
                    app.CommandCloseNotify -= OnCommandClose;
                }
            }
            catch (Exception ex)
            {
                Log("DetachAppEvents exception: " + ex.Message);
            }
        }

        private int OnFileOpenPost(string fileName)
        {
            try
            {
                HideGroupToolbar();
                ModelDoc2 doc = (ModelDoc2)iSwApp.ActiveDoc;
                if (doc != null)
                {
                    AttachDocEvents(doc);
                    Log("Attached doc events on FileOpenPost: " + doc.GetTitle());
                    RunSyncSafe(doc, fileName, false);
                }
            }
            catch { }
            return 0;
        }

        private int OnFileNew2(object newDoc, int docType, string templateName)
        {
            try
            {
                HideGroupToolbar();
                if (newDoc != null && newDoc is ModelDoc2)
                {
                    ModelDoc2 doc = (ModelDoc2)newDoc;
                    AttachDocEvents(doc);
                    Log("Attached doc events on FileNew2: " + doc.GetTitle());
                    RunSyncSafe(doc);
                }
            }
            catch { }
            return 0;
        }

        private int OnActiveDocChange()
        {
            try
            {
                HideGroupToolbar();
                ModelDoc2 doc = (ModelDoc2)iSwApp.ActiveDoc;
                if (doc != null)
                {
                    AttachDocEvents(doc);
                    // D-1: при смене активного документа — ТОЛЬКО лёгкая синхронизация самого чертежа.
                    // Без перестроения и без записи в ссылочную 3D-модель (SyncMode.OnActivate).
                    RunSyncSafe(doc, null, false, SyncMode.OnActivate);
                }
            }
            catch { }
            return 0;
        }

        private int OnCommandClose(int command, int reason)
        {
            try
            {
                if (command == SwCmdEditMaterial)
                {
                    ModelDoc2 doc = (ModelDoc2)iSwApp.ActiveDoc;
                    if (doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocPART)
                    {
                        Log("swCommands_EditMaterial closed. Triggering sync for " + doc.GetTitle());
                        RunSyncSafe(doc);
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// D-3: стабильный ключ хука документа — полный путь, при его отсутствии — заголовок.
        /// </summary>
        private static string GetDocHookKey(ModelDoc2 doc)
        {
            if (doc == null) return "";
            try
            {
                string path = doc.GetPathName();
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch { }
            try
            {
                string title = doc.GetTitle();
                if (!string.IsNullOrEmpty(title)) return title;
            }
            catch { }
            return "";
        }

        private void AttachDocEvents(ModelDoc2 doc)
        {
            if (doc == null) return;
            try
            {
                string key = GetDocHookKey(doc);
                if (string.IsNullOrEmpty(key)) return;

                // Уже подписаны под этим ключом?
                if (_docHooks.ContainsKey(key)) return;

                // D-3: если этот же COM-объект документа уже подписан под устаревшим ключом
                // (например, после SaveAs/переименования) — перепривязываем запись к новому ключу,
                // не создавая дублирующих подписок.
                string staleKey = null;
                foreach (KeyValuePair<string, DocEventHooks> kv in _docHooks)
                {
                    if (kv.Value != null && object.ReferenceEquals(kv.Value.Doc, doc))
                    {
                        staleKey = kv.Key;
                        break;
                    }
                }
                if (staleKey != null)
                {
                    DocEventHooks staleHooks = _docHooks[staleKey];
                    staleHooks.Key = key;
                    _docHooks.Remove(staleKey);
                    _docHooks[key] = staleHooks;
                    return;
                }

                int docType = doc.GetType();
                DocEventHooks hooks = new DocEventHooks();
                hooks.Key = key;
                hooks.Doc = doc;
                hooks.DocType = docType;
                ModelDoc2 docRef = doc;

                if (docType == (int)swDocumentTypes_e.swDocPART)
                {
                    PartDoc part = (PartDoc)doc;
                    // Полный путь (включая запись в ссылочную модель чертежа и ForceRebuild3)
                    // зарезервирован для уведомлений сохранения — SyncMode.OnSave по умолчанию.
                    hooks.PartFileSave = delegate(string fn) { RunSyncSafe(docRef, fn, true); return 0; };
                    hooks.PartFileSaveAs = delegate(string fn) { RunSyncSafe(docRef, fn, true); return 0; };
                    hooks.PartDestroy = delegate { DetachDocEvents(hooks); return 0; };
                    part.FileSaveNotify += hooks.PartFileSave;
                    part.FileSaveAsNotify2 += hooks.PartFileSaveAs;
                    part.DestroyNotify += hooks.PartDestroy;
                }
                else if (docType == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    AssemblyDoc asm = (AssemblyDoc)doc;
                    hooks.AsmFileSave = delegate(string fn) { RunSyncSafe(docRef, fn, true); return 0; };
                    hooks.AsmFileSaveAs = delegate(string fn) { RunSyncSafe(docRef, fn, true); return 0; };
                    hooks.AsmDestroy = delegate { DetachDocEvents(hooks); return 0; };
                    asm.FileSaveNotify += hooks.AsmFileSave;
                    asm.FileSaveAsNotify2 += hooks.AsmFileSaveAs;
                    asm.DestroyNotify += hooks.AsmDestroy;
                }
                else if (docType == (int)swDocumentTypes_e.swDocDRAWING)
                {
                    DrawingDoc drw = (DrawingDoc)doc;
                    hooks.DrwFileSave = delegate(string fn) { RunSyncSafe(docRef, fn, true); return 0; };
                    hooks.DrwFileSaveAs = delegate(string fn) { RunSyncSafe(docRef, fn, true); return 0; };
                    hooks.DrwDestroy = delegate { DetachDocEvents(hooks); return 0; };
                    drw.FileSaveNotify += hooks.DrwFileSave;
                    drw.FileSaveAsNotify2 += hooks.DrwFileSaveAs;
                    drw.DestroyNotify += hooks.DrwDestroy;
                    // drw.RegenNotify intentionally removed: regen should not mutate document properties or trigger rebuilds
                }

                _docHooks[key] = hooks;
            }
            catch (Exception ex)
            {
                Log("AttachDocEvents exception: " + ex.Message);
            }
        }

        /// <summary>
        /// D-3: снятие подписок конкретного документа (вызывается из DestroyNotify)
        /// с явным -= по сохранённым делегатам.
        /// </summary>
        private void DetachDocEvents(DocEventHooks hooks)
        {
            if (hooks == null) return;
            try
            {
                ModelDoc2 doc = hooks.Doc;
                if (doc != null)
                {
                    if (hooks.DocType == (int)swDocumentTypes_e.swDocPART)
                    {
                        try
                        {
                            PartDoc part = (PartDoc)doc;
                            if (hooks.PartFileSave != null) part.FileSaveNotify -= hooks.PartFileSave;
                            if (hooks.PartFileSaveAs != null) part.FileSaveAsNotify2 -= hooks.PartFileSaveAs;
                            if (hooks.PartDestroy != null) part.DestroyNotify -= hooks.PartDestroy;
                        }
                        catch { }
                    }
                    else if (hooks.DocType == (int)swDocumentTypes_e.swDocASSEMBLY)
                    {
                        try
                        {
                            AssemblyDoc asm = (AssemblyDoc)doc;
                            if (hooks.AsmFileSave != null) asm.FileSaveNotify -= hooks.AsmFileSave;
                            if (hooks.AsmFileSaveAs != null) asm.FileSaveAsNotify2 -= hooks.AsmFileSaveAs;
                            if (hooks.AsmDestroy != null) asm.DestroyNotify -= hooks.AsmDestroy;
                        }
                        catch { }
                    }
                    else if (hooks.DocType == (int)swDocumentTypes_e.swDocDRAWING)
                    {
                        try
                        {
                            DrawingDoc drw = (DrawingDoc)doc;
                            if (hooks.DrwFileSave != null) drw.FileSaveNotify -= hooks.DrwFileSave;
                            if (hooks.DrwFileSaveAs != null) drw.FileSaveAsNotify2 -= hooks.DrwFileSaveAs;
                            if (hooks.DrwDestroy != null) drw.DestroyNotify -= hooks.DrwDestroy;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            hooks.Doc = null;
            hooks.PartFileSave = null;
            hooks.PartFileSaveAs = null;
            hooks.PartDestroy = null;
            hooks.AsmFileSave = null;
            hooks.AsmFileSaveAs = null;
            hooks.AsmDestroy = null;
            hooks.DrwFileSave = null;
            hooks.DrwFileSaveAs = null;
            hooks.DrwDestroy = null;

            try
            {
                if (!string.IsNullOrEmpty(hooks.Key) && _docHooks.ContainsKey(hooks.Key))
                {
                    _docHooks.Remove(hooks.Key);
                }
            }
            catch { }
        }

        /// <summary>
        /// D-3: полное снятие всех документных подписок (вызывается из DisconnectFromSW).
        /// </summary>
        private void DetachAllDocEvents()
        {
            try
            {
                List<DocEventHooks> allHooks = new List<DocEventHooks>(_docHooks.Values);
                foreach (DocEventHooks h in allHooks)
                {
                    DetachDocEvents(h);
                }
            }
            catch { }
            try { _docHooks.Clear(); } catch { }
        }

        private void RunSyncSafe(ModelDoc2 doc, string targetFileName = null, bool triggerRebuild = true, SyncMode mode = SyncMode.OnSave)
        {
            if (_isSyncing || doc == null) return;
            if (!MaterialSyncEngine.IsServiceEnabled()) return;
            _isSyncing = true;
            try
            {
                MaterialSyncEngine.SyncModelProperties(doc, iSwApp, false, triggerRebuild, targetFileName, mode);
            }
            catch (Exception ex)
            {
                Log("RunSyncSafe error: " + ex.Message);
            }
            finally
            {
                _isSyncing = false;
            }
        }

        #endregion
    }
}
