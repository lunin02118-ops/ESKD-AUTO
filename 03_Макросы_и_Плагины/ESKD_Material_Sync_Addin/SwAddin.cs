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
        private static readonly int[] CommandUserIds = { 9900, 9901, 9902, 9903, 9904, 9905, 9906, 9907, 9908, 9909, 9910 };

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
            int lzk = group.AddCommandItem2("Ведомость ЛЗК", -1,
                "Операции, габариты, выгрузка SWTools, листы «Покраска» и «Покупные» — Ведомость_<шифр>.xlsx в папке изделия",
                "Ведомость ЛЗК", 3, "BuildLzk", "EnableLzkCommand", CommandUserIds[4], buttons);
            int check = group.AddCommandItem2("Проверить изделие", -1,
                "Состав, перестроение, реквизиты, чертежи и ведомость — отчёт _Проверка.txt с итогом ГОТОВО, ЗАМЕЧАНИЯ или БРАК",
                "Проверить изделие", 4, "CheckProduct", "EnableCheckCommand", CommandUserIds[5], buttons);
            // Пункт «Отчёт проверки» живёт в меню «Инструменты → ЕСКД» и на панели инструментов: на вкладке
            // ему места нет, а открыть прежний отчёт, ничего не проверяя, бывает нужно (ТЗ-02 Т-34).
            int report = group.AddCommandItem2("Отчёт проверки", -1, "Открыть последний отчёт _Проверка.txt, не проверяя заново",
                "Отчёт проверки", 5, "ShowCheckReport", "EnableCheckCommand", CommandUserIds[6], buttons);
            int export = group.AddCommandItem2("Выгрузить в производство", -1,
                "PDF чертежей, DXF развёрток и IGS профиля в папки изделия — отчёт _Экспорт.txt",
                "Выгрузить в производство", 6, "ExportProduct", "EnableExportCommand", CommandUserIds[7], buttons);
            int independent = group.AddCommandItem2("Сделать независимым", -1,
                "Выделенный эталон или чужая деталь становится своей копией в 01_3D — с чертежом и новым номером",
                "Сделать независимым", 7, "MakeIndependent", "EnableIndependentCommand", CommandUserIds[8], buttons);
            int revision = group.AddCommandItem2("Новая ревизия", -1,
                "Поднять ревизию выданного чертежа: штамп, строка в Изменения.xlsx и выгрузка с суффиксом _ИзмN",
                "Новая ревизия", 8, "NewRevision", "EnableRevisionCommand", CommandUserIds[9], buttons);
            // Снимок делает куратор базы и делает редко: место ему в меню «Инструменты → ЕСКД», а не на вкладке.
            int snapshot = group.AddCommandItem2("Снимок эталона", -1,
                "Сложить нынешнее состояние эталона в _Версии и записать строку в Изменения.xlsx",
                "Снимок эталона", 9, "EtalonSnapshot", "EnableEtalonCommand", CommandUserIds[10], buttons);
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

            int[] ids =
            {
                group.get_CommandID(settings), group.get_CommandID(sync), group.get_CommandID(bch),
                group.get_CommandID(lzk), group.get_CommandID(check), group.get_CommandID(report),
                group.get_CommandID(export), group.get_CommandID(independent), group.get_CommandID(revision),
                group.get_CommandID(snapshot)
            };
            _commandIds = ids;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] <= 0) Core.Log.Error("Команда «" + CommandNames[i] + "» не создана: идентификатор " + ids[i]);
            foreach (int docType in new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING })
            {
                try
                {
                    bool part = docType == (int)swDocumentTypes_e.swDocPART;
                    bool assembly = docType == (int)swDocumentTypes_e.swDocASSEMBLY;
                    // Порядок идентификаторов тот же, что в CommandNames; «Отчёт проверки» (ids[5]) живёт
                    // только в меню, на вкладку идут «Выгрузить в производство» (ids[6]) и у сборки
                    // «Сделать независимым» (ids[7]) — в том порядке, в каком идёт работа над изделием.
                    // «Новая ревизия» (ids[8]) живёт там, где живёт ревизия: на чертеже и на БЧ-детали (Т-48).
                    int[] wanted = part ? new[] { ids[0], ids[1], ids[2], ids[6], ids[8] }
                        : assembly ? new[] { ids[0], ids[1], ids[7], ids[3], ids[4], ids[6] }
                        : new[] { ids[0], ids[1], ids[8] };
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
                        int[] texts = new int[wanted.Length];
                        for (int t = 0; t < texts.Length; t++) texts[t] = below;
                        box.AddCommands(wanted, texts);
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

        private static readonly string[] CommandNames =
        {
            "Настройки ЕСКД", "Синхронизировать", "Деталь БЧ", "Ведомость ЛЗК", "Проверить изделие", "Отчёт проверки",
            "Выгрузить в производство", "Сделать независимым", "Новая ревизия", "Снимок эталона"
        };
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
                if (ActiveDocType() != (int)swDocumentTypes_e.swDocPART) return 0;
                ModelDoc2 doc = _app.ActiveDoc as ModelDoc2;
                if (doc == null) return 0;
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

        private int _activeType = -1;
        private DateTime _activeTypeRead = DateTime.MinValue;

        /// <summary>
        /// Тип активного документа для доступности кнопок. SolidWorks опрашивает каждую кнопку отдельно, и при
        /// семи командах это семь обращений к ActiveDoc на каждое обновление панели — переключение окна из-за
        /// этого заметно дорожало (P14). Ответ живёт четверть секунды: за это время окно не сменится.
        /// </summary>
        private int ActiveDocType()
        {
            if ((DateTime.UtcNow - _activeTypeRead).TotalMilliseconds < 250) return _activeType;
            ModelDoc2 doc = _app != null ? _app.ActiveDoc as ModelDoc2 : null;
            _activeType = doc == null ? 0 : doc.GetType();
            _activeTypeRead = DateTime.UtcNow;
            return _activeType;
        }

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

        /// <summary>Кнопка «Ведомость ЛЗК»: доступна у сборки и неактивна, пока предыдущая ведомость формируется.</summary>
        public int EnableLzkCommand()
        {
            try
            {
                if (ActiveDocType() != (int)swDocumentTypes_e.swDocASSEMBLY) return 0;
                return LzkService.Running ? 0 : 1;
            }
            catch (COMException)
            {
                return 1;
            }
        }

        public void BuildLzk()
        {
            LzkService.Start(_app, true);
        }

        /// <summary>Ведомость ЛЗК без окон (проверки, пакетный запуск). Итог — LzkStatus().</summary>
        public void BuildLzkSilent()
        {
            LzkService.Start(_app, false);
        }

        /// <summary>«running», «ok|путь|строк|замечаний» или «error|текст».</summary>
        public string LzkStatus()
        {
            return LzkService.LastOutcome;
        }

        /// <summary>Кнопка «Проверить изделие» доступна на сборке (ТЗ-02 Т-2, Т-25).</summary>
        public int EnableCheckCommand()
        {
            try
            {
                return ActiveDocType() == (int)swDocumentTypes_e.swDocASSEMBLY ? 1 : 0;
            }
            catch (COMException)
            {
                return 1;
            }
        }

        public void CheckProduct()
        {
            CheckService.Run(_app, true);
        }

        /// <summary>Проверка изделия без окон. Итог — CheckStatus().</summary>
        public void CheckProductSilent()
        {
            CheckService.Run(_app, false);
        }

        /// <summary>Открыть последний отчёт проверки, не проверяя заново (Т-34).</summary>
        public void ShowCheckReport()
        {
            CheckService.ShowLast(_app, true);
        }

        /// <summary>Последний отчёт без окон: итог — CheckStatus().</summary>
        public void ShowCheckReportSilent()
        {
            CheckService.ShowLast(_app, false);
        }

        /// <summary>«ok|итог|брак|замечаний|отчёт» или «error|текст».</summary>
        public string CheckStatus()
        {
            return CheckService.LastOutcome;
        }

        /// <summary>Кнопка «Выгрузить в производство» доступна на сборке изделия и на детали (ТЗ-02 Т-25).</summary>
        public int EnableExportCommand()
        {
            try
            {
                int type = ActiveDocType();
                return type == (int)swDocumentTypes_e.swDocASSEMBLY || type == (int)swDocumentTypes_e.swDocPART ? 1 : 0;
            }
            catch (COMException)
            {
                return 1;
            }
        }

        public void ExportProduct()
        {
            ExportService.Run(_app, true);
        }

        /// <summary>Выгрузка без окон. Итог — ExportStatus().</summary>
        public void ExportProductSilent()
        {
            ExportService.Run(_app, false);
        }

        /// <summary>«ok|файлов|пропущено|отчёт» или «error|текст».</summary>
        public string ExportStatus()
        {
            return ExportService.LastOutcome;
        }

        /// <summary>
        /// Кнопка «Сделать независимым» доступна в сборке (ТЗ-02 Т-19). Выделение здесь не проверяется:
        /// SolidWorks опрашивает доступность постоянно, а разбор дерева на каждый опрос сделал бы
        /// переключение окон заметно медленнее — что выделено, кнопка объясняет при нажатии.
        /// </summary>
        public int EnableIndependentCommand()
        {
            try
            {
                return ActiveDocType() == (int)swDocumentTypes_e.swDocASSEMBLY ? 1 : 0;
            }
            catch (COMException)
            {
                return 1;
            }
        }

        public void MakeIndependent()
        {
            IndependentService.Run(_app, true);
        }

        /// <summary>
        /// Сделать выделенное независимым без окон (Т-9). Обозначение и наименование пустые — берутся
        /// предложенные; withDrawing: 1 — с чертежом, 0 — без, -1 — как есть рядом с исходной моделью.
        /// </summary>
        public void MakeIndependentSilent(string designation, string name, int withDrawing)
        {
            IndependentService.Run(_app, false, designation, name,
                withDrawing < 0 ? (bool?)null : withDrawing != 0);
        }

        /// <summary>«ok|создано|пропущено|оборванных размеров|отчёт» или «error|текст».</summary>
        public string IndependentStatus()
        {
            return IndependentService.LastOutcome;
        }

        /// <summary>
        /// Кнопка «Новая ревизия» доступна на чертеже и БЧ-детали выданного изделия (ТЗ-02 Т-48).
        /// Причина недоступности видна в подсказке: конструктору важно знать, что документ ещё черновик.
        /// </summary>
        public int EnableRevisionCommand()
        {
            try
            {
                int type = ActiveDocType();
                if (type != (int)swDocumentTypes_e.swDocDRAWING && type != (int)swDocumentTypes_e.swDocPART) return 0;
                return RevisionService.Unavailable(_app).Length == 0 ? 1 : 0;
            }
            catch (COMException)
            {
                return 0;
            }
        }

        /// <summary>Почему «Новая ревизия» недоступна — текст для подсказки и автотестов.</summary>
        public string RevisionUnavailable()
        {
            return RevisionService.Unavailable(_app);
        }

        public void NewRevision()
        {
            RevisionService.Run(_app, true);
        }

        /// <summary>Новая ревизия без окон (Т-9): что изменено, код причины, задел.</summary>
        public void NewRevisionSilent(string what, string code, string backlog)
        {
            RevisionService.Run(_app, false, what, code, backlog);
        }

        /// <summary>«ok|ревизия|строка журнала|выгрузка» или «error|текст».</summary>
        public string RevisionStatus()
        {
            return RevisionService.LastOutcome;
        }

        /// <summary>Кнопка «Снимок эталона» доступна в сборке базы эталонов (ТЗ-02 Т-53).</summary>
        public int EnableEtalonCommand()
        {
            try
            {
                if (ActiveDocType() != (int)swDocumentTypes_e.swDocASSEMBLY) return 0;
                ModelDoc2 doc = _app.ActiveDoc as ModelDoc2;
                string path = doc == null ? "" : (doc.GetPathName() ?? "");
                return path.Length > 0 && ProductLocator.Locate(path).InBase ? 1 : 0;
            }
            catch (COMException)
            {
                return 0;
            }
        }

        public void EtalonSnapshot()
        {
            EtalonService.Run(_app, true);
        }

        /// <summary>Снимок эталона без окон (Т-9): что изменено и код причины.</summary>
        public void EtalonSnapshotSilent(string what, string code)
        {
            EtalonService.Run(_app, false, what, code);
        }

        /// <summary>«ok|папка снимка|файлов|применяемость» или «error|текст».</summary>
        public string EtalonStatus()
        {
            return EtalonService.LastOutcome;
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
