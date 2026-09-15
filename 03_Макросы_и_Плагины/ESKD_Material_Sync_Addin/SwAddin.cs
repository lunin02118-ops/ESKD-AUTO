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
            int bch = group.AddCommandItem2("Деталь БЧ", -1, "Установить или снять признак безчертёжной детали (ГОСТ Р 2.109-2023)",
                "Деталь БЧ", 2, "ToggleDrawingless", "EnableBchCommand", CommandUserIds[3], buttons);
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
            _commandIds = ids;
            foreach (int docType in new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING })
            {
                try
                {
                    bool part = docType == (int)swDocumentTypes_e.swDocPART;
                    int[] wanted = part ? ids : new[] { ids[0], ids[1] };
                    CommandTab tab = _commands.GetCommandTab(docType, TabTitle);

                    // Вкладка отсутствует, ссылается на чужие команды (у сборки вместо «Синхронизировать» — «Определенный
                    // пользователем маршрут») или запрошен сброс: удаляются все накопившиеся дубликаты с этим заголовком.
                    // Попыток не больше десяти — если SolidWorks не удаляет вкладку, цикл не должен повесить его запуск.
                    bool needRecreate = (tab == null) || ignorePrevious || !SameIds(TabCommands(tab), wanted);
                    if (needRecreate)
                    {
                        int removed = 0;
                        for (int attempt = 0; tab != null && attempt < 10; attempt++)
                        {
                            if (!_commands.RemoveCommandTab(tab))
                            {
                                Core.Log.Warn("Вкладка ЕСКД для типа " + docType + " не удалена SolidWorks — дубликаты могут остаться");
                                break;
                            }
                            removed++;
                            tab = _commands.GetCommandTab(docType, TabTitle);
                        }
                        if (removed > 0 && !ignorePrevious)
                            Core.Log.Info("Вкладка ЕСКД для типа " + docType + " пересоздана (удалено вкладок: " + removed + ")");
                        tab = _commands.AddCommandTab(docType, TabTitle);
                        CommandTabBox box = tab.AddCommandTabBox();
                        int below = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;
                        box.AddCommands(wanted, part ? new[] { below, below, below } : new[] { below, below });
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

        /// <summary>Идентификаторы команд всех блоков вкладки по порядку.</summary>
        private static int[] TabCommands(CommandTab tab)
        {
            System.Collections.Generic.List<int> result = new System.Collections.Generic.List<int>();
            object[] boxes = tab.CommandTabBoxes() as object[];
            if (boxes == null) return result.ToArray();
            foreach (object b in boxes)
            {
                CommandTabBox box = b as CommandTabBox;
                if (box == null) continue;
                object commands, texts;
                box.GetCommands(out commands, out texts);
                int[] ids = commands as int[];
                if (ids != null) result.AddRange(ids);
            }
            return result.ToArray();
        }

        /// <summary>Кнопки вкладки ЕСКД для типа документа (1 — деталь, 2 — сборка, 3 — чертёж): «имя команды» через «|».</summary>
        public string TabButtons(int docType)
        {
            try
            {
                CommandTab tab = _commands != null ? _commands.GetCommandTab(docType, TabTitle) : null;
                if (tab == null) return "";
                System.Collections.Generic.List<string> names = new System.Collections.Generic.List<string>();
                foreach (int id in TabCommands(tab))
                {
                    int i = _commandIds != null ? Array.IndexOf(_commandIds, id) : -1;
                    names.Add(i >= 0 ? CommandNames[i] : "чужая команда " + id);
                }
                return string.Join("|", names.ToArray());
            }
            catch (COMException ex)
            {
                Core.Log.Error("TabButtons", ex);
                return "ошибка: " + ex.Message;
            }
        }

        private static readonly string[] CommandNames = { "Настройки ЕСКД", "Синхронизировать", "Деталь БЧ" };
        private int[] _commandIds;

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

        /// <summary>Кнопка «Синхронизировать»: итог в строке состояния, предупреждения — окном (только по нажатию, Д-38).</summary>
        public void SyncCurrentDoc()
        {
            SyncReport report = RunExplicitSync();
            if (report == null || report.Skipped) return;
            try
            {
                StatusText(report.Changes > 0 ? "ЕСКД: обновлено свойств — " + report.Changes : "ЕСКД: реквизиты актуальны");
                if (report.Warnings.Count > 0)
                    MessageBox.Show(string.Join("\n\n", report.Warnings.ToArray()), "ЕСКД: предупреждения синхронизации",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Core.Log.Error("SyncCurrentDoc: итог", ex);
            }
        }

        /// <summary>Синхронизация активного документа без интерфейса: число изменённых свойств или -1.</summary>
        public int SyncActiveDocumentSilent()
        {
            SyncReport report = RunExplicitSync();
            return report == null || report.Skipped ? -1 : report.Changes;
        }

        private SyncReport RunExplicitSync()
        {
            try
            {
                ModelDoc2 doc = _app.ActiveDoc as ModelDoc2;
                return doc != null ? SyncService.SyncExplicit(_app, doc) : null;
            }
            catch (Exception ex)
            {
                Core.Log.Error("SyncActiveDocumentSilent", ex);
                return null;
            }
        }

        /// <summary>Предупреждения последней автоматической синхронизации — те, что показаны в строке состояния.</summary>
        public string LastSyncWarnings()
        {
            return _hub != null ? _hub.LastWarnings : "";
        }

        /// <summary>Кнопка «Деталь БЧ» нажата (вдавлена) у безчертёжной детали — тип детали виден без открытия свойств.</summary>
        public int EnableBchCommand()
        {
            try
            {
                ModelDoc2 doc = _app.ActiveDoc as ModelDoc2;
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART) return 0;
                if (_formatProperty == null) _formatProperty = SyncService.Dictionary(Settings.Read())[Role.Format];
                string cfg = doc.ConfigurationManager.ActiveConfiguration != null ? doc.ConfigurationManager.ActiveConfiguration.Name : "";
                string value = FormatValue(doc, cfg) ?? FormatValue(doc, "");
                return (value ?? "").Trim() == BchRecord.FormatValue ? 3 : 1;
            }
            catch (COMException)
            {
                // SolidWorks опрашивает состояние кнопки постоянно; занятый COM — просто «доступна, не нажата».
                return 1;
            }
        }

        private string _formatProperty;
        private DateTime _bchDialogClosed = DateTime.MinValue;

        private string FormatValue(ModelDoc2 doc, string cfg)
        {
            CustomPropertyManager m = doc.Extension.get_CustomPropertyManager(cfg);
            if (m == null) return null;
            object names = m.GetNames();
            if (!(names is string[]) || Array.IndexOf((string[])names, _formatProperty) < 0) return null;
            string raw, resolved;
            bool wasResolved;
            m.Get5(_formatProperty, false, out raw, out resolved, out wasResolved);
            return raw;
        }

        /// <summary>
        /// Переключение чертёжная ⇄ безчертёжная: окно называет текущий тип и спрашивает, сменить ли его. Щелчок, пришедший
        /// сразу после закрытия окна (второй щелчок двойного нажатия), не открывает окно снова.
        /// </summary>
        public void ToggleDrawingless()
        {
            if ((DateTime.Now - _bchDialogClosed).TotalMilliseconds < 800) return;
            ModelDoc2 doc = null;
            string format;
            bool isBch;
            try
            {
                doc = _app.ActiveDoc as ModelDoc2;
                isBch = BchService.State(doc, out format);
            }
            catch (Exception ex)
            {
                Core.Log.Error("Деталь БЧ: состояние", ex);
                return;
            }
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                MessageBox.Show("Признак БЧ применяется только к деталям.", "ЕСКД: Деталь БЧ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string title = DocInfo.TitleOf(doc);
            string question = isBch
                ? string.Format("Деталь «{0}» сейчас БЕЗЧЕРТЁЖНАЯ (Формат = БЧ).\n\nСделать её ЧЕРТЁЖНОЙ?\n\n" +
                    "Вернутся прежние «Формат» и «Примечание», наименование — из имени файла.", title)
                : string.Format("Деталь «{0}» сейчас ЧЕРТЁЖНАЯ{1}.\n\nСделать её БЕЗЧЕРТЁЖНОЙ (БЧ)?\n\n" +
                    "«Формат» = БЧ, масса в «Примечании», в «Наименовании» — запись для спецификации. Прежние значения " +
                    "сохраняются и вернутся при обратном переключении.", title, format.Length > 0 ? " (Формат = " + format + ")" : "");
            DialogResult answer;
            try
            {
                answer = MessageBox.Show(question, "ЕСКД: чертёжная или безчертёжная деталь", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            }
            finally
            {
                _bchDialogClosed = DateTime.Now;
            }
            if (answer != DialogResult.Yes) return;
            int result;
            try
            {
                result = BchService.Set(_app, doc, !isBch);
            }
            catch (Exception ex)
            {
                Core.Log.Error("ToggleDrawingless", ex);
                return;
            }
            try
            {
                StatusText(result == BchService.Enabled
                    ? "ЕСКД: деталь безчертёжная (Формат = БЧ, масса в «Примечании») — сохраните деталь"
                    : "ЕСКД: деталь чертёжная, прежние «Формат» и «Примечание» восстановлены — сохраните деталь");
            }
            catch (Exception ex)
            {
                Core.Log.Error("SetStatusBarText", ex);
            }
        }

        /// <summary>Установить тип детали без окна: wanted 1 — безчертёжная, 0 — чертёжная. Итог: 0 — не деталь; 1 — БЧ; 2 — чертёжная.</summary>
        public int SetDrawinglessSilent(int wanted)
        {
            try
            {
                return BchService.Set(_app, _app.ActiveDoc as ModelDoc2, wanted != 0);
            }
            catch (Exception ex)
            {
                Core.Log.Error("SetDrawinglessSilent", ex);
                return BchService.NotPart;
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

        // Пакетная очистка файлов v5 — отдельной утилитой ESKD_Sync.exe /clean (Sw.MigrationService), а не методом надстройки:
        // пакет открывает и закрывает десятки документов, этому не место внутри COM-вызова, пришедшего из другого процесса.

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
