using System;
using System.IO;
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
    [ClassInterface(ClassInterfaceType.AutoDual)]
    [ProgId("ESKD.MaterialSync.SwAddin_v5")]
    public class SwAddin : ISwAddin
    {
        private const int SwCmdEditMaterial = 175; // swCommands_EditMaterial
        private const int CommandGroupId = 9995;

        private ISldWorks iSwApp;
        private ICommandManager iCmdMgr;
        private int iSwCookie;
        private bool _isSyncing = false;
        private readonly HashSet<int> _hookedDocIds = new HashSet<int>();

        public static void Log(string msg)
        {
            try
            {
                string logFile = Path.Combine(Path.GetTempPath(), "eskd_material_sync.log");
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
                _hookedDocIds.Clear();
                iSwApp = null;
                Log("DisconnectFromSW finished successfully.");
            }
            catch (Exception ex)
            {
                Log("DisconnectFromSW exception: " + ex.Message);
            }
            return true;
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
                // Force ignorePreviousVersion = true so toolbar and tab are recreated cleanly in SW
                Log("AddCommandManager: Calling CreateCommandGroup2 with ID " + CommandGroupId);
                ICommandGroup cmdGroup = iCmdMgr.CreateCommandGroup2(
                    CommandGroupId,
                    "ЕСКД",
                    "Инструменты ЕСКД: Настройки реквизитов, синхронизация материалов и массы",
                    "",
                    -1,
                    true,
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

                    int cmdIndexSettings = cmdGroup.AddCommandItem2(
                        "Настройки ЕСКД",
                        -1,
                        "Открыть настройки реквизитов ЕСКД (фамилии, контора, масса)",
                        "Настройки ЕСКД",
                        0,
                        "ShowSettings",
                        "EnableCommand",
                        101,
                        (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

                    int cmdIndexSync = cmdGroup.AddCommandItem2(
                        "Синхронизировать",
                        -1,
                        "Синхронизировать материал, массу и штамп в активном документе",
                        "Синхронизировать",
                        1,
                        "SyncCurrentDoc",
                        "EnableCommand",
                        102,
                        (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

                    cmdGroup.HasToolbar = false; // No standalone toolbar window
                    cmdGroup.HasMenu = true;
                    try
                    {
                        cmdGroup.ShowInDocumentType = (int)(swDocTemplateTypes_e.swDocTemplateTypePART |
                                                            swDocTemplateTypes_e.swDocTemplateTypeASSEMBLY |
                                                            swDocTemplateTypes_e.swDocTemplateTypeDRAWING);
                    }
                    catch { }

                    Log("AddCommandManager: Activating cmdGroup");
                    cmdGroup.Activate();

                    int cmdIDSettings = cmdGroup.get_CommandID(cmdIndexSettings);
                    int cmdIDSync = cmdGroup.get_CommandID(cmdIndexSync);

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
                            ICommandTab tab = iCmdMgr.GetCommandTab(dt, "ЕСКД");
                            if (tab != null)
                            {
                                try { iCmdMgr.RemoveCommandTab((CommandTab)tab); } catch { }
                            }
                            tab = iCmdMgr.AddCommandTab(dt, "ЕСКД");
                            if (tab != null)
                            {
                                tab.Visible = true;
                                ICommandTabBox box = tab.AddCommandTabBox();
                                if (box != null)
                                {
                                    int[] cmdIDs = new int[] { cmdIDSettings, cmdIDSync };
                                    int[] textTypes = new int[] {
                                        (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow,
                                        (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow
                                    };
                                    box.AddCommands(cmdIDs, textTypes);
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

        public void SyncCurrentDoc()
        {
            try
            {
                ModelDoc2 doc = (ModelDoc2)iSwApp.ActiveDoc;
                if (doc != null)
                {
                    MaterialSyncEngine.SyncModelProperties(doc, iSwApp, true);
                    doc.ForceRebuild3(true);
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
            catch { }
        }

        private int OnFileOpenPost(string fileName)
        {
            try
            {
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
                ModelDoc2 doc = (ModelDoc2)iSwApp.ActiveDoc;
                if (doc != null)
                {
                    AttachDocEvents(doc);
                    RunSyncSafe(doc);
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

        private void AttachDocEvents(ModelDoc2 doc)
        {
            if (doc == null) return;
            try
            {
                int id = doc.GetHashCode();
                if (_hookedDocIds.Contains(id)) return;
                _hookedDocIds.Add(id);

                int docType = doc.GetType();
                if (docType == (int)swDocumentTypes_e.swDocPART)
                {
                    PartDoc part = (PartDoc)doc;
                    part.FileSaveNotify += (string fn) => { RunSyncSafe(doc, fn, true); return 0; };
                    part.FileSaveAsNotify2 += (string fn) => { RunSyncSafe(doc, fn, true); return 0; };
                }
                else if (docType == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    AssemblyDoc asm = (AssemblyDoc)doc;
                    asm.FileSaveNotify += (string fn) => { RunSyncSafe(doc, fn, true); return 0; };
                    asm.FileSaveAsNotify2 += (string fn) => { RunSyncSafe(doc, fn, true); return 0; };
                }
                else if (docType == (int)swDocumentTypes_e.swDocDRAWING)
                {
                    DrawingDoc drw = (DrawingDoc)doc;
                    drw.FileSaveNotify += (string fn) => { RunSyncSafe(doc, fn, true); return 0; };
                    drw.FileSaveAsNotify2 += (string fn) => { RunSyncSafe(doc, fn, true); return 0; };
                    drw.RegenNotify += () => { RunSyncSafe(doc, null, false); return 0; };
                }
            }
            catch (Exception ex)
            {
                Log("AttachDocEvents exception: " + ex.Message);
            }
        }

        private void RunSyncSafe(ModelDoc2 doc, string targetFileName = null, bool triggerRebuild = true)
        {
            if (_isSyncing || doc == null) return;
            _isSyncing = true;
            try
            {
                MaterialSyncEngine.SyncModelProperties(doc, iSwApp, false, triggerRebuild, targetFileName);
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
