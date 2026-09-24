using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using Microsoft.Win32;
using Environment = System.Environment;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Кнопка К-4 «Ведомость ЛЗК» (ТЗ-02 Т-35…Т-37, ТЗ-04): сверка с проверкой изделия (изменено после неё — вопрос
    /// «Проверить сейчас / Продолжить как есть», З-27) → окно «Операции» с тиражом, сроком и цветом → запись «Операции»
    /// и «Габарит» в новые модели изделия → сохранение изменённого кнопкой (документы с несохранёнными правками
    /// конструктора не сохраняются, З-25) → SWTools без окна по пресету «ЛЗК» → живая книга:
    /// паспорт, участки, расход, нормы → ЛЗК_&lt;шифр&gt;.xlsx: в структуре заказа — в «04_Сопроводительная документация»
    /// изделия (прежняя — в _Аннулировано), вне её — рядом со сборкой (прежняя — в резервную копию); введённое переносится.
    /// SWTools ждётся в потоке SolidWorks — таймером и в простое (опрос из постоянной подписки EventHub): синхронное
    /// ожидание заблокировало бы надстройку SWTools, которая читает модель в этом же потоке.
    /// </summary>
    public sealed class LzkService
    {
        private const int TimeoutMinutes = 15;
        /// <summary>Всего попыток выгрузки SWTools: одна рабочая и одна на потерянный по дороге пакет данных.</summary>
        private const int Attempts = 2;
        private static LzkService _running;
        private static bool _silent;
        private static string _lastOutcome = "";

        private readonly ISldWorks _app;
        private readonly Settings _settings;
        private ModelDoc2 _doc;
        private string _assemblyPath;
        private string _productFolder;
        private string _cipher;
        private string _workbookPath;
        private string _legacyPath;
        /// <summary>Сборка в «01_3D» изделия: книга в «04_…», прежняя — в «_Аннулировано». Иначе — рядом со сборкой, без папок.</summary>
        private bool _inOrder;
        private LzkInputs _inputs;
        private readonly LzkBook.Options _options = new LzkBook.Options();
        private string _tempWorkbook;
        private string _resultPath;
        private readonly List<LzkItem> _items = new List<LzkItem>();
        private readonly List<Notice> _notes = new List<Notice>();
        private Process _process;
        private Timer _timer;
        private DateTime _started;
        private LzkHeader _header;
        /// <summary>Сверка с проверкой изделия: версия — в паспорт книги.</summary>
        private ProductFreshness _freshness;
        /// <summary>Сохранения самой кнопки: их суммы — в отчёт проверки, версия изделия от них не меняется.</summary>
        private readonly ToolSaves _saves = new ToolSaves();
        /// <summary>
        /// Модели, в которых до кнопки были несохранённые правки конструктора: кнопка пишет в них «Операции» и «Габарит», но
        /// не сохраняет — сохранять правки конструктора без его команды нельзя (решение владельца 23.09.2026, З-25).
        /// </summary>
        private readonly HashSet<string> _editedBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Активное исполнение модели до кнопки: замер его переключает, и сохранять можно, только если оно вернулось.</summary>
        private readonly Dictionary<string, string> _activeBefore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _finishing;
        private bool _finished;
        private bool _exitedEarly;
        /// <summary>Сделано попыток выгрузки: пакет из SolidWorks теряется при занятом окне SWTools, повтор помогает.</summary>
        private int _attempt = 1;

        private LzkService(ISldWorks app)
        {
            _app = app;
            _settings = Settings.Read();
        }

        public static bool Running { get { return _running != null; } }

        /// <summary>Итог последнего запуска: «running», «ok|путь|строк|замечаний» или «error|текст».</summary>
        public static string LastOutcome { get { return _running != null ? "running" : _lastOutcome; } }

        /// <summary>
        /// Запуск по кнопке. Возвращает сразу; итог — окном по завершении SWTools.
        /// interactive=false (COM, проверки): без окна «Операции» и без сообщений — итог в LastOutcome и журнале.
        /// </summary>
        public static void Start(ISldWorks app, bool interactive)
        {
            if (_running != null)
            {
                // Идущий запуск не трогаем: его режим (окно итога или тихий) остаётся прежним.
                if (interactive) MessageBox.Show("Ведомость ЛЗК уже формируется — дождитесь окончания.", "ЕСКД: Ведомость ЛЗК",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                else Log.Warn("Ведомость ЛЗК: уже формируется");
                return;
            }
            _silent = !interactive;
            LzkService service = new LzkService(app);
            try
            {
                if (!service.Prepare(interactive)) return;
                _running = service;
                if (!service.Launch()) service.Cleanup();
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК", ex);
                service.Cleanup();
                Info("Ведомость ЛЗК не сформирована: " + ex.Message, MessageBoxIcon.Error);
            }
            finally
            {
                // Отмена или ошибка после своих сохранений — их суммы всё равно в отчёт проверки (повторная запись пустая).
                service.RecordSaves();
            }
        }

        /// <summary>
        /// Свои сохранения кнопки — в отчёт проверки: изделие для следующей кнопки остаётся проверенным. Повторный вызов
        /// ничего не пишет: суммы в отчёте уже новые.
        /// </summary>
        private void RecordSaves()
        {
            if (_freshness != null) ProductFreshness.Restamp(_freshness.ProductFolder, _saves.Changes, "ведомость ЛЗК");
        }

        private static void Info(string text, MessageBoxIcon icon)
        {
            if (icon != MessageBoxIcon.Information || _running == null) _lastOutcome = "error|" + text;
            if (_silent)
            {
                Log.Warn("Ведомость ЛЗК: " + text);
                return;
            }
            MessageBox.Show(text, "ЕСКД: Ведомость ЛЗК", MessageBoxButtons.OK, icon);
        }

        private void Status(string text)
        {
            try
            {
                Frame frame = _app.Frame() as Frame;
                if (frame != null) frame.SetStatusBarText(text);
            }
            catch (COMException ex)
            {
                Log.Error("Ведомость ЛЗК: строка состояния", ex);
            }
        }

        // ------------------------------------------------------------------ подготовка
        private bool Prepare(bool interactive)
        {
            _lastOutcome = "";
            _doc = _app.ActiveDoc as ModelDoc2;
            if (_doc == null || _doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                Info("Откройте главную сборку изделия (из любой папки) и нажмите кнопку ещё раз.", MessageBoxIcon.Information);
                return false;
            }
            _assemblyPath = DocInfo.PathOf(_doc);
            if (_assemblyPath.Length == 0)
            {
                Info("Сборка ещё не сохранена в файл. Сохраните её (в любую папку) и нажмите кнопку ещё раз.", MessageBoxIcon.Information);
                return false;
            }
            string exe = SwToolsExe();
            if (exe == null)
            {
                Info("SWTools не установлен. Установите SWTools (он ставится вместе с инструментами конструктора) и повторите.", MessageBoxIcon.Warning);
                return false;
            }
            if (Process.GetProcessesByName("SWTools").Length > 0)
            {
                Info("Открыто окно SWTools. Закройте его и нажмите «Ведомость ЛЗК» ещё раз.", MessageBoxIcon.Information);
                return false;
            }
            _productFolder = LzkNaming.ProductFolder(_assemblyPath);
            _cipher = LzkNaming.Cipher(_productFolder, _assemblyPath);
            _inOrder = LzkNaming.InModelsFolder(_assemblyPath);
            _workbookPath = _inOrder ? LzkNaming.WorkbookPath(_productFolder, _cipher) : LzkNaming.LooseWorkbookPath(_assemblyPath, _cipher);
            _legacyPath = LzkNaming.LegacyWorkbookPath(_productFolder, _cipher);
            foreach (string busy in new[] { _workbookPath, _legacyPath })
                if (FileLocked(busy))
                {
                    Info("Файл " + Path.GetFileName(busy) + " открыт в Excel. Закройте его и повторите.", MessageBoxIcon.Information);
                    return false;
                }
            // Подменённый одноимённый компонент другого заказа — книга собралась бы по чужой детали (№16).
            List<string> swapped = ProductNamesakes.Swapped(_doc, _productFolder);
            if (swapped.Count > 0)
            {
                Info(OrderArchive.SwappedText(swapped, 20), MessageBoxIcon.Warning);
                return false;
            }
            // Изделие проверено и с тех пор не менялось — без вопросов; иначе «Проверить сейчас / Продолжить как есть» (З-27).
            bool cancelled;
            _freshness = ProductFreshness.Ensure(_app, _doc, "Ведомость ЛЗК",
                "книга соберётся по файлам как есть, в паспорте будет «" + ProductStamp.Unchecked + "»: «Готово к производству» " +
                "её не примет, пока изделие не проверят и книгу не соберут заново.", interactive, out cancelled);
            if (cancelled)
            {
                Log.Info("Ведомость ЛЗК: отменена в вопросе о проверке изделия");
                return false;
            }
            if (!_freshness.Fresh)
                _notes.Add(Notices.Of(NoticeLevel.Warning, Path.GetFileName(_assemblyPath),
                    "книга собрана по непроверенному изделию — " + string.Join("; ", _freshness.Reasons.ToArray()),
                    "Нажмите «Проверить изделие», затем «Ведомость ЛЗК»: «Готово к производству» примет только такую книгу"));
            // Правки конструктора — до любых действий кнопки: такие документы кнопка не сохраняет (З-25).
            if (DocumentGuard.HasUserEdits(_doc)) _editedBefore.Add(_assemblyPath);

            // Введённое в прежней книге (тираж, срок, цвет, нормы, указания) переносится в новую.
            _inputs = LzkInputs.Read(File.Exists(_workbookPath) ? _workbookPath : "");
            if (_inputs.ReadError.Length > 0)
            {
                Info("Прежняя книга " + Path.GetFileName(_workbookPath) + " не читается (" + _inputs.ReadError + "): введённое в ней — " +
                     "тираж, срок, цвет, нормы — не перенести.\n\nОткройте её в Excel и сохраните заново (или переименуйте, чтобы " +
                     "собрать книгу с нуля) и нажмите кнопку ещё раз.", MessageBoxIcon.Warning);
                return false;
            }
            _options.Inputs = _inputs;
            if (!ReadReferences()) return false;

            Status("ЕСКД: ведомость ЛЗК — чтение состава изделия…");
            AssemblyDoc asm = (AssemblyDoc)_doc;
            try
            {
                asm.ResolveAllLightWeightComponents(false);
            }
            catch (COMException ex)
            {
                Log.Error("ResolveAllLightWeightComponents", ex);
            }
            Dictionary<string, LzkItem> byKey = new Dictionary<string, LzkItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ModelTraits> traits = new Dictionary<string, ModelTraits>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ModelDoc2> models = new Dictionary<string, ModelDoc2>(StringComparer.OrdinalIgnoreCase);
            List<Instance> instances = new List<Instance>();

            LzkItem top = Describe(_doc, _assemblyPath, "", true, traits, null);
            top.Quantity = 1;
            top.IsTop = true;
            byKey[_assemblyPath] = top;
            models[_assemblyPath] = _doc;

            object[] comps = asm.GetComponents(false) as object[] ?? new object[0];
            foreach (object o in comps)
            {
                Component2 comp = o as Component2;
                if (comp == null || !Counts(comp)) continue;
                string path = comp.GetPathName() ?? "";
                if (path.Length == 0) continue;
                ModelDoc2 model = comp.GetModelDoc2() as ModelDoc2;
                if (model == null)
                {
                    _notes.Add(Notices.Of(NoticeLevel.Warning, Path.GetFileName(path), "модель не загружена — в книгу не попала",
                        "Откройте сборку полностью (не облегчённой) и пересоберите книгу"));
                    continue;
                }
                // Строка ведомости — модель в конкретной конфигурации: у исполнений одного файла свои обозначения
                // и количества («Укосина» 00 и 01 — NC3-7R.02.000 и NC3-7R.02.000-01).
                string cfg = comp.ReferencedConfiguration ?? "";
                string key = path + "|" + cfg;
                LzkItem item;
                if (!byKey.TryGetValue(key, out item))
                {
                    // Первая встреча файла — до замера, который может переключить исполнение.
                    if (!models.ContainsKey(path))
                    {
                        if (DocumentGuard.HasUserEdits(model)) _editedBefore.Add(path);
                        _activeBefore[path] = ActiveName(model);
                    }
                    item = Describe(model, path, cfg, model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY, traits, comp);
                    byKey[key] = item;
                    models[path] = model;
                }
                item.Quantity++;
                instances.Add(new Instance { Component = comp, Item = item });
            }
            ExcludePurchasedContents(instances, byKey);
            _items.AddRange(byKey.Values);

            // Операции отмечаются и пишутся в свои модели изделия, где бы они ни лежали: детали сборки бывают и в другой
            // папке (замечание владельца 19.09.2026 — иначе у них «?» и участки пустые). Не правятся только покупные и
            // модели базы эталонов и библиотеки, общие для всех заказов.
            // Модели другого заказа в окне есть (их операции нужны книге), но в их файлы ничего не пишется — ModelsToWrite.
            List<LzkItem> editable = _items.Where(i => !i.IsPurchased && (!i.InBase || (_inOrder && i.InProduct))).ToList();
            HashSet<string> bookOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (interactive && editable.Count > 0)
            {
                Dictionary<string, string> chosen;
                using (LzkOperationsForm form = new LzkOperationsForm(editable, traits, path => ShellThumbnail.Get(path, 256), _inputs))
                {
                    if (form.ShowDialog(Owner()) != DialogResult.OK)
                    {
                        SaveMeasured(models);
                        return false;
                    }
                    chosen = form.Result;
                    bookOnly.UnionWith(form.BookOnly);
                }
                foreach (LzkItem i in editable)
                {
                    string value;
                    if (chosen.TryGetValue(i.Path, out value)) i.Operations = value;
                }
            }
            else
            {
                // Без окна — то же, что «Сформировать» без правок: пустые «Операции» получают предложенные по модели.
                foreach (LzkItem i in editable)
                    if (string.IsNullOrWhiteSpace(i.Operations)) i.Operations = LzkOperations.Join(LzkOperations.Suggest(traits[i.Path]));
            }

            // Модель базы без «Операций» — операции по признакам модели, только в книге: файл базы не трогаем.
            foreach (LzkItem i in _items.Where(i => !i.IsPurchased && !editable.Contains(i) && string.IsNullOrWhiteSpace(i.Operations)))
            {
                i.Operations = LzkOperations.Join(LzkOperations.Suggest(traits[i.Path]));
                _notes.Add(Notices.Of(NoticeLevel.Info, Path.GetFileName(i.Path),
                    "модель базы: операции предложены по модели и записаны только в книгу, файл базы не изменён"));
            }

            WriteProperties(ModelsToWrite(editable, bookOnly), models);
            ExportOutdated();
            MarkPaintedUnits(instances, top);
            PaintedAssemblyAreas(instances, top, byKey, traits);

            Status("ЕСКД: ведомость ЛЗК — сохранение сборки…");
            // Сборку, которую не сохранить (только для чтения, чужая, защищённая папка), SWTools читает с диска как есть:
            // книга собирается всё равно (замечание владельца 19.09.2026 — ЛЗК из любой папки). Сборка сохраняется, только
            // если её изменила сама кнопка (записала «Операции»); с правками конструктора — нет (З-25).
            int errors;
            if (_doc.IsOpenedReadOnly())
                _notes.Add(Notices.Of(NoticeLevel.Info, Path.GetFileName(_assemblyPath),
                    "сборка открыта только для чтения: состав взят из сохранённого файла"));
            else if (_editedBefore.Contains(_assemblyPath))
                _notes.Add(Notices.Of(NoticeLevel.Warning, Path.GetFileName(_assemblyPath),
                    (DocumentGuard.OlderVersion(_app, _assemblyPath) ? SwFileVersion.OlderNote : "в сборке ваши несохранённые правки") +
                    " — кнопка её не сохраняет: состав взят из сохранённого файла",
                    "Сохраните сборку и пересоберите книгу"));
            else if (DocumentGuard.HasUserEdits(_doc) && !_saves.Save(_doc, out errors))
                _notes.Add(Notices.Of(NoticeLevel.Warning, Path.GetFileName(_assemblyPath),
                    "сборка не сохранена (код " + errors + "): состав взят из сохранённого файла", "Сохраните сборку и пересоберите книгу"));
            // Свои сохранения кнопки — в отчёт проверки: изделие для следующей кнопки остаётся проверенным.
            RecordSaves();

            _header = new LzkHeader
            {
                Product = _cipher + (top.Name.Length > 0 ? " " + top.Name : ""),
                Cipher = _cipher,
                Name = top.Name,
                Order = OrderName(),
                Author = _settings.Author ?? "",
                Model = Path.GetFileName(_assemblyPath),
                Date = DateTime.Now.ToString("dd.MM.yyyy HH:mm"),
                Checksum = Checksum(_assemblyPath),
                Version = _freshness.Version
            };
            return true;
        }

        /// <summary>
        /// Нормативы и бланки — вверх по папкам от изделия, иначе из справочников инструментария (Т-13, ТЗ-04). Встроенных
        /// нормативов нет: без справочника книга собирается, только если все нормы уже введены в прежней книге.
        /// </summary>
        private bool ReadReferences()
        {
            string references = Settings.ReferenceFolder();
            string normsPath = Norms.Find(_productFolder, references);
            string problem = "";
            Norms norms = normsPath.Length > 0 ? Norms.Read(normsPath, out problem) : null;
            if (norms == null)
            {
                if (LzkBook.NormRows.Any(n => !_inputs.Norms.ContainsKey(n.Key)))
                {
                    Info((problem.Length > 0 ? problem : "Справочник «" + Norms.FileName + "» не найден ни у изделия, ни в " +
                        (references.Length > 0 ? references : "папке инструментария") + ".") +
                        "\n\nВстроенных нормативов нет: положите справочник на место (или переустановите рабочее место) и повторите.",
                        MessageBoxIcon.Warning);
                    return false;
                }
                norms = Norms.Empty();
                _notes.Add(Notices.Of(NoticeLevel.Warning, Norms.FileName, (problem.Length > 0 ? problem : "справочник не найден") +
                    " — нормы взяты из прежней книги ЛЗК", "Поправьте справочник нормативов"));
            }
            _options.Norms = norms;
            _options.Blanks = LzkBlanks.Read(LzkBlanks.Find(_productFolder, references), out problem);
            if (problem.Length > 0) _notes.Add(Notices.Of(NoticeLevel.Warning, LzkBlanks.FileName, problem, "Поправьте справочник бланков"));
            return true;
        }

        /// <summary>Заказ — имя папки заказа изделия; вне структуры заказов — пусто.</summary>
        private string OrderName()
        {
            try
            {
                string order = ProductLocator.Locate(_assemblyPath).OrderFolder;
                return string.IsNullOrEmpty(order) ? "" : Path.GetFileName(order.TrimEnd(Path.DirectorySeparatorChar, '/'));
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: папка заказа", ex);
                return "";
            }
        }

        private IWin32Window Owner()
        {
            try
            {
                Frame frame = _app.Frame() as Frame;
                if (frame != null) return new WindowWrapper(new IntPtr(frame.GetHWnd()));
            }
            catch (COMException ex)
            {
                Log.Error("Ведомость ЛЗК: окно SolidWorks", ex);
            }
            return null;
        }

        private sealed class Instance
        {
            public Component2 Component;
            public LzkItem Item;
        }

        /// <summary>Экземпляр входит в изделие: не погашен, не исключён из спецификации, не оболочка — и так же все его родители.</summary>
        private static bool Counts(Component2 comp)
        {
            for (Component2 c = comp; c != null; c = c.GetParent() as Component2)
            {
                if (ComponentState.Suppressed(c) || c.ExcludeFromBOM || c.IsEnvelope()) return false;
            }
            return true;
        }

        private LzkItem Describe(ModelDoc2 model, string path, string cfg, bool assembly, Dictionary<string, ModelTraits> traits,
            Component2 comp)
        {
            PropertyWriter w = new PropertyWriter(model, true);
            // Свойства — той конфигурации, что стоит в изделии, а не активной в модели: у исполнения своё обозначение.
            string active = cfg.Length > 0 && w.ConfigurationNames().Contains(cfg) ? cfg : w.ActiveConfigurationName();
            LzkItem item = new LzkItem
            {
                Path = path,
                Configuration = cfg,
                IsAssembly = assembly,
                InProduct = LzkNaming.IsInside(path, _productFolder),
                InBase = ProductLocator.Locate(path).InBase,
                Place = LzkNaming.Place(path, _productFolder, _assemblyPath),
                IsPurchased = ComponentKind.IsPurchased(w, model, path, "Ведомость ЛЗК", _cipher),
                Designation = Prop(w, active, "Обозначение"),
                Name = Prop(w, active, "Наименование"),
                Operations = Prop(w, active, LzkOperations.PropertyName),
                Code = Prop(w, active, "Код_Продукции"),
                Section = Prop(w, active, "Раздел")
            };
            if (item.Code.Length == 0) item.Code = Prop(w, active, "Справочный_номер");
            item.Unit = Prop(w, active, "ЕдИзм");
            item.Material = assembly ? "" : Prop(w, active, "Материал_Строка");
            // Обозначение и наименование ещё не записаны (пусто или формула SW-File Name, равная имени файла) —
            // по имени файла, как это сделает синхронизация ЕСКД при сохранении.
            ParsedName parsed = DesignationParser.Parse(path, " ");
            string baseName = Path.GetFileNameWithoutExtension(path) ?? "";
            if (item.Designation.Length == 0 || string.Equals(item.Designation, baseName, StringComparison.OrdinalIgnoreCase))
                item.Designation = parsed.HasDesignation ? parsed.Designation : "";
            if (item.Name.Length == 0 || string.Equals(item.Name, baseName, StringComparison.OrdinalIgnoreCase))
                item.Name = parsed.Title.Length > 0 ? parsed.Title : parsed.BaseName;
            ModelTraits t = Traits(model, assembly, active);
            t.Material = item.Material;
            // «Материал_Строка» ещё не записан (заказ не оформлен по ЕСКД) — резка предлагается по материалу SolidWorks,
            // как у выгрузки: иначе труба, построенная телом, получала одну «Покраску», и после записи «Операций»
            // выгрузка не делала ей IGS (замечание владельца 24.09.2026).
            if (!assembly && t.Material.Length == 0) t.Material = SwMaterial(model, active);
            t.IsPurchased = item.IsPurchased;
            traits[path] = t;
            item.IsProfile = t.IsStructuralMember;
            if (item.IsPurchased) return item;
            // Размер, развёртка, плотность и площадь — той конфигурации, что стоит в изделии: у «Укосины» 00 и 01 разные
            // длины, а SolidWorks меряет активную (аудит 21.09.2026, Л-1). Модель базы и библиотеки не переключаем —
            // её файл не правится и не сохраняется; её исполнение меряется по активной конфигурации с пометкой «оценка».
            // Модель другого заказа — так же (решение владельца 23.09.2026): ни исполнение, ни развёртка в ней не
            // переключаются, иначе SolidWorks пометил бы её изменённой, и её сохранили бы вместе со сборкой.
            // Модель только для чтения — так же: пометку «изменена» с неё не снять сохранением.
            bool readOnly = ReadOnly(model);
            bool mayMeasure = (!item.InBase || (_inOrder && item.InProduct)) && item.Place != LzkPlace.OtherOrder && !readOnly;
            bool otherConfig = cfg.Length > 0 && !string.Equals(cfg, w.ActiveConfigurationName(), StringComparison.OrdinalIgnoreCase) &&
                w.ConfigurationNames().Contains(cfg);
            bool wasDirty = DocumentGuard.HasUserEdits(model);
            bool switchFailed;
            using (ConfigScope scope = new ConfigScope(model, otherConfig && mayMeasure ? cfg : ""))
            {
                switchFailed = scope.Failed;
                if (!assembly) t.DensityKgM3 = Density(model);
                Size(model, assembly, t, item, mayMeasure);
                // Деталь — наружная поверхность (у трубы без внутренней стенки); сборка — ниже, по её деталям (З-7).
                item.AreaM2 = assembly || comp == null ? double.NaN : PaintArea.Outer(Area(model, comp), item.Material);
                scope.Restore();
            }
            // Исполнение не открылось — замер шёл по активной конфигурации, и «Габарит» исполнения «00» ушёл бы в «01»
            // (сверка SW API 23.09.2026, №18): размер — оценка, в модель не пишется.
            if (switchFailed)
            {
                item.SizeIsEstimate = true;
                _notes.Add(Notices.Of(NoticeLevel.Info, Path.GetFileName(path),
                    "исполнение «" + cfg + "» не открылось — измерено по активной конфигурации, уточните размер заготовки"));
            }
            // Замер переключал исполнение или включал развёртку — SolidWorks пометил модель изменённой, хотя в ней ничего не
            // поменялось. Такие модели кнопка сохраняет сама (или говорит о них): иначе их сочли бы правками конструктора (З-27).
            if (!wasDirty && DocumentGuard.HasUserEdits(model)) _dirtiedBySwitch.Add(path);
            if (otherConfig && !mayMeasure)
            {
                item.SizeIsEstimate = true;
                _notes.Add(Notices.Of(NoticeLevel.Info, Path.GetFileName(path),
                    "исполнение «" + cfg + "» " + (item.Place == LzkPlace.OtherOrder ? "модели другого заказа"
                        : readOnly ? "модели, открытой только для чтения," : "модели базы") +
                    " измерено по активной конфигурации — уточните размер заготовки"));
            }
            return item;
        }

        /// <summary>
        /// Модели, в которые пишутся «Операции» и «Габарит» (решение владельца 23.09.2026): модели изделия — всегда;
        /// другой папки заказа и вне заказов — если в окне операций не снята галочка «В модель»; другого заказа — никогда.
        /// Книга получает операции всех; файл другого заказа не меняется — иначе книга одного заказа переписала бы другой
        /// (его выданные файлы, его операции). Про каждую модель, оставшуюся без записи, — строка в замечаниях.
        /// </summary>
        private List<LzkItem> ModelsToWrite(List<LzkItem> editable, HashSet<string> bookOnly)
        {
            List<LzkItem> write = new List<LzkItem>();
            HashSet<string> told = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LzkItem item in editable)
            {
                bool other = item.Place == LzkPlace.OtherOrder;
                if (!other && !bookOnly.Contains(item.Path))
                {
                    write.Add(item);
                    continue;
                }
                if (!told.Add(item.Path)) continue;
                string reason = other
                    ? "модель другого заказа («" + Path.GetFileName(ProductLocator.OrderOf(item.Path)) + "»): операции и габарит — только в книге, файл не изменён"
                    : "запись в модель снята в окне операций: операции и габарит — только в книге";
                if (_dirtiedBySwitch.Contains(item.Path))
                    reason += "; SolidWorks считает её изменённой (для замера переключалось исполнение), но в ней ничего не " +
                        "поменялось — закройте её без сохранения";
                _notes.Add(Notices.Of(NoticeLevel.Info, Path.GetFileName(item.Path), reason));
            }
            return write;
        }

        /// <summary>
        /// Модели, у которых замер поставил признак «изменён»: переключение конфигурации (SolidWorks хранит активную
        /// конфигурацию в файле) или временно включённая развёртка. Они сохраняются вместе с записью свойств (и при «Отмене»
        /// окна операций), чтобы при закрытии не спрашивали «Сохранить?», а проверка не сочла это правками конструктора.
        /// </summary>
        private readonly HashSet<string> _dirtiedBySwitch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// «Отмена» в окне операций: модели, которые пометил изменёнными только замер кнопки, сохраняются — в них ничего не
        /// поменялось, а несохранённые они считались бы правками конструктора (З-27). Модели с правками конструктора — нет.
        /// </summary>
        private void SaveMeasured(Dictionary<string, ModelDoc2> models)
        {
            foreach (string path in _dirtiedBySwitch)
            {
                ModelDoc2 model;
                if (!models.TryGetValue(path, out model)) continue;
                string refusal = SaveRefusal(_editedBefore.Contains(path), Before(path), ActiveName(model));
                if (refusal.Length > 0)
                {
                    if (!_editedBefore.Contains(path)) NotSaved(path, refusal, "");
                    continue;
                }
                int errors;
                if (!_saves.Save(model, out errors))
                    Log.Warn("Ведомость ЛЗК: после замера модель не сохранена (" + SwCodes.SaveProblem(errors) + ") — " + path);
            }
        }

        private static bool ReadOnly(ModelDoc2 model)
        {
            try
            {
                return model.IsOpenedReadOnly();
            }
            catch (COMException ex)
            {
                Log.Error("Ведомость ЛЗК: только для чтения?", ex);
                return true;
            }
        }

        /// <summary>
        /// Файлы с изменённой «Лазерной резкой трубы»: выгрузка решает по этой галочке, делать ли IGS. Версия изделия от
        /// записи операций не меняется, поэтому прежняя выгрузка изделия отмечается «не проверено» — её нужно повторить.
        /// </summary>
        private readonly List<string> _tubeChanged = new List<string>();

        private void ExportOutdated()
        {
            if (_tubeChanged.Count == 0 || _freshness == null) return;
            string path = Path.Combine(_freshness.ProductFolder, CheckRules.ExportReportName);
            try
            {
                if (!File.Exists(path)) return;
                bool changed;
                string text = ExportLog.MarkUnchecked(File.ReadAllText(path, Encoding.UTF8), out changed);
                if (!changed) return;
                File.WriteAllText(path, text, new UTF8Encoding(true));
                _notes.Add(Notices.Of(NoticeLevel.Warning, CheckRules.ExportReportName,
                    "изменена «Лазерная резка трубы» (" + string.Join(", ", _tubeChanged.Take(5).ToArray()) +
                    (_tubeChanged.Count > 5 ? " и ещё " + (_tubeChanged.Count - 5) : "") +
                    "): прежняя выгрузка изделия устарела, в её отчёте теперь «" + ProductStamp.Unchecked + "»",
                    "Выгрузите изделие заново"));
            }
            catch (IOException ex)
            {
                Log.Error("Ведомость ЛЗК: отметка устаревшей выгрузки", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Ведомость ЛЗК: отметка устаревшей выгрузки", ex);
            }
        }

        /// <summary>
        /// Состав покупной сборки (мотор-редуктор, готовый узел поставщика) — не наше: его детали не комплектуются
        /// отдельно, не идут на участки и не получают «Операции» (аудит 21.09.2026, Л-4). Экземпляры внутри покупной
        /// сборки убираются, количества пересчитываются по оставшимся.
        /// </summary>
        private static void ExcludePurchasedContents(List<Instance> instances, Dictionary<string, LzkItem> byKey)
        {
            List<Instance> kept = new List<Instance>();
            foreach (Instance inst in instances)
            {
                bool insidePurchased = false;
                for (Component2 p = inst.Component.GetParent() as Component2; p != null && !insidePurchased; p = p.GetParent() as Component2)
                {
                    LzkItem parent;
                    if (byKey.TryGetValue((p.GetPathName() ?? "") + "|" + (p.ReferencedConfiguration ?? ""), out parent) && parent.IsPurchased)
                        insidePurchased = true;
                }
                if (!insidePurchased) kept.Add(inst);
            }
            if (kept.Count == instances.Count) return;
            foreach (LzkItem item in byKey.Values) if (!item.IsTop) item.Quantity = 0;
            foreach (Instance inst in kept) inst.Item.Quantity++;
            foreach (string key in byKey.Where(kv => !kv.Value.IsTop && kv.Value.Quantity == 0).Select(kv => kv.Key).ToList())
                byKey.Remove(key);
            instances.Clear();
            instances.AddRange(kept);
        }

        /// <summary>
        /// Временно делает активной конфигурацию исполнения; <see cref="Restore"/> (и <see cref="Dispose"/>) возвращает
        /// прежнюю. Пустое имя — ничего не переключает.
        /// </summary>
        private sealed class ConfigScope : IDisposable
        {
            private ModelDoc2 _model;
            private readonly string _previous;

            /// <summary>
            /// Исполнение не стало активным — замер пойдёт по активной конфигурации (сверка SW API 23.09.2026, №18): раньше
            /// об этом никто не знал, и «Габарит» чужого исполнения записывался в модель.
            /// </summary>
            public bool Failed;

            public ConfigScope(ModelDoc2 model, string cfg)
            {
                if (string.IsNullOrEmpty(cfg)) return;
                try
                {
                    string previous = ActiveName(model);
                    if (previous.Length == 0)
                    {
                        Failed = true;
                        return;
                    }
                    bool shown = model.ShowConfiguration2(cfg);
                    string now = ActiveName(model);
                    // Возвращать нужно всякий раз, когда активное уже не прежнее, — и после неудачного переключения.
                    if (!string.Equals(now, previous, StringComparison.OrdinalIgnoreCase))
                    {
                        _model = model;
                        _previous = previous;
                    }
                    Failed = !shown || !string.Equals(now, cfg, StringComparison.OrdinalIgnoreCase);
                }
                catch (COMException ex)
                {
                    Log.Error("Ведомость ЛЗК: переключение конфигурации " + cfg, ex);
                    Failed = true;
                }
            }

            public void Restore()
            {
                if (_model == null) return;
                try
                {
                    // Не вернулось — модель не сохранится (SaveRefusal сверит перед сохранением), здесь — след в журнале.
                    if (!_model.ShowConfiguration2(_previous) ||
                        !string.Equals(ActiveName(_model), _previous, StringComparison.OrdinalIgnoreCase))
                        Log.Warn("Ведомость ЛЗК: прежнее исполнение «" + _previous + "» не вернулось активным — " +
                            DocInfo.TitleOf(_model));
                }
                catch (COMException ex)
                {
                    Log.Error("Ведомость ЛЗК: возврат конфигурации " + _previous, ex);
                }
                _model = null;
            }

            public void Dispose()
            {
                Restore();
            }
        }


        private static string Prop(PropertyWriter w, string cfg, string name)
        {
            string value = w.Resolved(cfg, name);
            if (string.IsNullOrWhiteSpace(value)) value = w.Resolved("", name);
            return (value ?? "").Trim();
        }

        /// <param name="cfg">Исполнение из изделия: погашенный в нём элемент конструкции — чужого исполнения.</param>
        private static ModelTraits Traits(ModelDoc2 model, bool assembly, string cfg)
        {
            ModelTraits t = new ModelTraits { IsAssembly = assembly };
            try
            {
                for (Feature f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                {
                    string type = f.GetTypeName2() ?? "";
                    if (type == "SheetMetal" || type == "SMBaseFlange") t.IsSheetMetal = true;
                    else if (type == "EdgeFlange" || type == "Hem" || type == "Jog" || type == "MiterFlange" || type == "OneBend" ||
                             type == "SketchBend" || type == "LoftedBend" || type == "SM3dBend") t.HasBends = true;
                    // Элемент, погашенный в исполнении, — чужого исполнения (критик сверки SW API 23.09.2026).
                    else if (type == "WeldMemberFeat" && !SuppressedIn(f, cfg)) t.IsStructuralMember = true;
                    else if (type == "WeldmentFeature") t.IsWeldment = true;
                    else if (type.IndexOf("WeldBead", StringComparison.OrdinalIgnoreCase) >= 0 || type == "Weld") t.HasWeldBeads = true;
                }
            }
            catch (COMException ex)
            {
                Log.Error("Ведомость ЛЗК: дерево " + DocInfo.TitleOf(model), ex);
            }
            return t;
        }

        /// <summary>
        /// Габарит: прокат — длина заготовки (LENGTH списка вырезов, иначе RD1@Примечания, иначе габарит с «*»);
        /// листовая деталь — развёртка Д×Ш×S (замечание владельца 21.09.2026: у гнутых деталей в ведомость уходил
        /// габарит готовой детали, а заготовка — это развёртка); прочее — Д×Ш×В.
        /// </summary>
        /// <param name="mayChange">Можно временно включить развёртку (модель своего заказа, не база); нельзя — развёртка
        /// только из свойств списка вырезов, иначе габарит согнутой детали с пометкой «оценка».</param>
        private static void Size(ModelDoc2 model, bool assembly, ModelTraits t, LzkItem item, bool mayChange)
        {
            try
            {
                if (t.IsSheetMetal && !assembly)
                {
                    bool estimate;
                    double[] flat = FlatPatternSides((PartDoc)model, model, mayChange, out estimate);
                    if (flat != null)
                    {
                        item.Size = LzkOperations.FormatSize(flat[0], flat[1], flat[2]);
                        item.SizeIsEstimate = estimate;
                        return;
                    }
                    // Развёртку не измерить — габарит согнутой детали с пометкой «оценка».
                    item.SizeIsEstimate = true;
                }
                if (t.IsStructuralMember)
                {
                    bool several;
                    double length = CutListLength(model, out several);
                    if (several)
                    {
                        // Рама из нескольких труб в одном файле: одна длина на всю деталь занизила бы «Расход» —
                        // берём наибольшую с пометкой «оценка», конструктор уточнит (аудит 19.09, Л-В9).
                        item.SizeIsEstimate = true;
                        if (!double.IsNaN(length) && length > 0)
                        {
                            item.Size = LzkOperations.FormatLength(length);
                            return;
                        }
                    }
                    if (double.IsNaN(length))
                    {
                        Dimension rd1 = model.Parameter("RD1@Примечания") as Dimension;
                        if (rd1 != null) length = rd1.SystemValue * 1000.0;
                    }
                    if (!double.IsNaN(length) && length > 0)
                    {
                        item.Size = LzkOperations.FormatLength(length);
                        return;
                    }
                    item.SizeIsEstimate = true;
                }
                double[] box = assembly
                    ? ((AssemblyDoc)model).GetBox(0) as double[]
                    : ((PartDoc)model).GetPartBox(true) as double[];
                double[] sides = box != null && box.Length >= 6
                    ? new[] { (box[3] - box[0]) * 1000, (box[4] - box[1]) * 1000, (box[5] - box[2]) * 1000 }
                    : null;
                if (t.IsStructuralMember && !assembly)
                {
                    // SolidWorks длину не дал (тело дорабатывали после элемента конструкции) — меряем тело вдоль
                    // его собственной оси; габарит по осям для наклонной детали занижает (решение владельца 21.09.2026).
                    double measured = MeasuredLength((PartDoc)model);
                    double boxMax = sides == null ? 0 : Math.Max(sides[0], Math.Max(sides[1], sides[2]));
                    double chosen = BodyLength.Choose(measured, boxMax);
                    if (chosen > 0)
                    {
                        item.Size = LzkOperations.FormatLength(chosen);
                        return;
                    }
                }
                if (sides != null)
                    item.Size = LzkOperations.FormatSize(sides[0], sides[1], sides[2]);
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: габарит " + item.Path, ex);
            }
        }

        /// <summary>
        /// Развёртка листовой детали {длина, ширина, толщина} в мм: из свойств «граничной рамки» списка вырезов; их нет
        /// (список не обновляли — так в боевом заказе NC3-7R) — включается элемент «Развёртка» (FlatPattern), деталь
        /// меряется по габаритному ящику, элемент гасится обратно (SetBendState в SolidWorks 2025 ничего не делает,
        /// проверено 21.09.2026). mayToggle = false (база, другой заказ) — развёртка не включается: это пометило бы модель
        /// изменённой. null — измерить не удалось. estimate — размер с пометкой «оценка» (<see cref="FlatSize.Choose"/>).
        /// </summary>
        private static double[] FlatPatternSides(PartDoc part, ModelDoc2 model, bool mayToggle, out bool estimate)
        {
            double thickness = StockService.SheetThicknessMm(model);
            double length = double.NaN, width = double.NaN;
            // Только папки с телами этого исполнения (CutListFolders): папка погашенного тела — чужая рамка.
            foreach (Feature sub in CutListFolders.Active(model))
            {
                CustomPropertyManager m = sub.CustomPropertyManager;
                if (m == null) continue;
                List<KeyValuePair<string, string>> written = Written(m);
                double l = Value(m, CutListProperties.Find(written, "Bounding Box Length", CutListProperties.BoundingBoxLengthSpellings));
                double w = Value(m, CutListProperties.Find(written, "Bounding Box Width", CutListProperties.BoundingBoxWidthSpellings));
                if (double.IsNaN(thickness))
                    thickness = Value(m, CutListProperties.Find(written, "Sheet Metal Thickness", CutListProperties.SheetThicknessSpellings));
                if (double.IsNaN(l) || double.IsNaN(w) || l <= 0 || w <= 0) continue;
                if (double.IsNaN(length) || l * w > length * width) { length = l; width = w; }
            }
            // Своя модель меряется по телу всегда: свойства списка вырезов SolidWorks пересчитывает только при его обновлении,
            // и после правки детали в них оставалась прежняя рамка, а имя DXF — уже по новой (ревью 23.09.2026). Замер —
            // та же наименьшая рамка, что в имени DXF. Не измерить — свойства с пометкой «оценка» (FlatSize.Choose).
            double[] fromProperties = double.IsNaN(length) || double.IsNaN(width) || length <= 0 || width <= 0
                ? null : new[] { length, width, thickness };
            double[] measured = mayToggle ? MeasureUnfolded(part, model, thickness) : null;
            double[] size = FlatSize.Choose(fromProperties, measured, mayToggle, out estimate);
            if (size == null) return null;
            double sheet = !double.IsNaN(thickness) ? thickness : size[2];
            if (double.IsNaN(sheet) || sheet <= 0) sheet = 0;
            return new[] { size[0], size[1], sheet };
        }

        /// <summary>
        /// Развёртка по телу {длина, ширина, толщина}, мм — когда в списке вырезов нет граничной рамки: элемент «Развёртка»
        /// (FlatPattern) включается, деталь меряется (<see cref="FlatFrame"/>), элемент гасится обратно (SetBendState в
        /// SolidWorks 2025 ничего не делает, проверено 21.09.2026). Гасится, только если был погашен: развёрнутую
        /// конструктором деталь раньше складывало. Модель помечается изменённой. null — не измерить.
        /// </summary>
        /// <param name="sheet">Толщина листа, мм (NaN — не известна): тело толще — не развёрнуто, не мерится.</param>
        public static double[] MeasureUnfolded(PartDoc part, ModelDoc2 model, double sheet)
        {
            Feature flat = null;
            for (Feature f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                if (f.GetTypeName2() == "FlatPattern") { flat = f; break; }
            if (flat == null) return null;
            bool folded = flat.IsSuppressed();
            if (folded && !flat.SetSuppression2((int)swFeatureSuppressionAction_e.swUnSuppressFeature,
                    (int)swInConfigurationOpts_e.swThisConfiguration, null)) return null;
            try
            {
                if (folded) model.ForceRebuild3(false);
                // Развёртка не перестроилась (элемент поперёк сгиба, битое тело) — SolidWorks пропускает её, и тело
                // остаётся согнутым: его рамка — не заготовка (ревью 23.09.2026). Тогда «Габарит» — оценка.
                bool warning;
                int error = flat.GetErrorCode2(out warning);
                if ((error != 0 && !warning) || flat.IsSuppressed()) return null;
                return FlatFrame(part, sheet);
            }
            finally
            {
                if (folded)
                {
                    flat.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature,
                        (int)swInConfigurationOpts_e.swThisConfiguration, null);
                    model.ForceRebuild3(false);
                }
            }
        }

        /// <summary>
        /// Граничная рамка развёрнутой детали {длина, ширина, толщина}, мм: наименьший прямоугольник вокруг всех рёбер тела в
        /// проекции на плоскость наибольшей плоской грани — это и есть контур заготовки, — как у SolidWorks и в имени DXF.
        /// Все рёбра, а не рёбра одной грани: без «Объединить грани» развёртка разбита по зонам сгиба, и наибольшая грань —
        /// одна полка; у выштамповки наибольшей бывает площадка (ревью 23.09.2026). Прямые рёбра берутся по концам, кривые —
        /// точками через 0,5 мм (отклонение хорды от дуги — сотые доли миллиметра), сверенными с концами ребра: у
        /// развёрнутого тела кривая ребра бывает сдвинута, и уголок 100+60 выходил 262 мм (L15, EdgePoints). Толщина —
        /// размах тела по нормали к грани; тело толще листа sheet больше чем на 0,5 мм — согнутое, не мерится.
        /// Габаритный ящик детали (GetPartBox) шёл по осям модели и с запасом: пластина 200×100, построенная под 30°,
        /// давала 223×187 (проверено на SolidWorks 2025; сверка 23.09.2026, замечание владельца — размер заготовки по
        /// внешней рамке). Тел несколько — наибольшая рамка. null — плоской грани нет.
        /// </summary>
        private static double[] FlatFrame(PartDoc part, double sheet)
        {
            double[] best = null;
            try
            {
                object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                if (bodies == null) return null;
                foreach (object o in bodies)
                {
                    Body2 body = o as Body2;
                    Face2 flat = body == null ? null : LargestPlane(body);
                    if (flat == null) continue;
                    double[] plane = ((Surface)flat.GetSurface()).PlaneParams as double[];
                    double[] u, v;
                    if (plane == null || plane.Length < 3 || !PlaneAxes(plane, out u, out v)) continue;
                    object[] edges = body.GetEdges() as object[];
                    if (edges == null) continue;
                    List<double> xy = new List<double>();
                    int rough = 0;
                    foreach (object e in edges)
                    {
                        Edge edge = e as Edge;
                        double[] p = edge == null ? null : edge.GetCurveParams2() as double[];
                        if (p == null || p.Length < 8) continue;
                        double[] start = { p[0], p[1], p[2] }, end = { p[3], p[4], p[5] };
                        Curve curve = edge.GetCurve() as Curve;
                        bool line = curve == null || curve.IsLine();
                        List<double[]> points = line
                            ? EdgePoints.Choose(start, end)
                            : EdgePoints.Choose(start, end, () => EdgeSamples(edge, curve, p, true),
                                () => EdgeSamples(edge, curve, p, false), () => TessSamples(curve, start, end));
                        if (!line && points.Count == 2) rough++;
                        foreach (double[] q in points)
                        {
                            xy.Add((q[0] * u[0] + q[1] * u[1] + q[2] * u[2]) * 1000);
                            xy.Add((q[0] * v[0] + q[1] * v[1] + q[2] * v[2]) * 1000);
                        }
                    }
                    if (rough > 0)
                        Log.Info("Ведомость ЛЗК: граничная рамка развёртки — у " + rough +
                                 " кривых рёбер точки не сошлись с концами, взяты только концы");
                    double width, length;
                    if (!DxfFrame.Smallest(xy, out width, out length)) continue;
                    if (best != null && length * width <= best[0] * best[1]) continue;
                    double fx, fy, fz, bx, by, bz, thickness = double.NaN;
                    double[] n = PlaneNormal(plane);
                    if (n != null && body.GetExtremePoint(n[0], n[1], n[2], out fx, out fy, out fz) &&
                        body.GetExtremePoint(-n[0], -n[1], -n[2], out bx, out by, out bz))
                        thickness = ((fx - bx) * n[0] + (fy - by) * n[1] + (fz - bz) * n[2]) * 1000;
                    if (!double.IsNaN(sheet) && sheet > 0 && !double.IsNaN(thickness) && thickness > sheet + 0.5) continue;
                    best = new[] { length, width, thickness };
                }
            }
            catch (COMException ex)
            {
                Log.Error("Ведомость ЛЗК: граничная рамка развёртки по телу", ex);
            }
            return best;
        }

        /// <summary>
        /// Точки кривого ребра через 0,5 мм по его параметрам p[6]…p[7]: по самому ребру (Edge.Evaluate2) или по его
        /// кривой (Curve.Evaluate2) — у развёрнутого тела кривая бывает сдвинута, это отсеет EdgePoints.Choose.
        /// null — SolidWorks не вычислил.
        /// </summary>
        private static List<double[]> EdgeSamples(Edge edge, Curve curve, double[] p, bool byEdge)
        {
            try
            {
                double t0 = p[6], t1 = p[7];
                int steps = EdgePoints.Steps(curve.GetLength3(Math.Min(t0, t1), Math.Max(t0, t1)) * 1000);
                List<double[]> points = new List<double[]>(steps + 1);
                for (int k = 0; k <= steps; k++)
                {
                    double t = t0 + (t1 - t0) * k / steps;
                    double[] q = (byEdge ? edge.Evaluate2(t, 0) : curve.Evaluate2(t, 0)) as double[];
                    if (q == null || q.Length < 3) return null;
                    points.Add(new[] { q[0], q[1], q[2] });
                }
                return points;
            }
            catch (COMException)
            {
                return null;
            }
        }

        /// <summary>
        /// Точки кривого ребра по его концам (Curve.GetTessPts): хорда отходит от дуги не больше чем на 0,01 мм.
        /// null — SolidWorks не вычислил.
        /// </summary>
        private static List<double[]> TessSamples(Curve curve, double[] start, double[] end)
        {
            try
            {
                double[] q = curve.GetTessPts(EdgePoints.Tolerance, 0.0005, start, end) as double[];
                if (q == null || q.Length < 6) return null;
                List<double[]> points = new List<double[]>(q.Length / 3);
                for (int i = 0; i + 2 < q.Length; i += 3) points.Add(new[] { q[i], q[i + 1], q[i + 2] });
                return points;
            }
            catch (COMException)
            {
                return null;
            }
        }

        private static Face2 LargestPlane(Body2 body)
        {
            Face2 best = null;
            double area = 0;
            object[] faces = body.GetFaces() as object[];
            if (faces == null) return null;
            foreach (object o in faces)
            {
                Face2 face = o as Face2;
                Surface surface = face == null ? null : face.GetSurface() as Surface;
                if (surface == null || !surface.IsPlane()) continue;
                double a = face.GetArea();
                if (a <= area) continue;
                area = a;
                best = face;
            }
            return best;
        }

        private static double[] PlaneNormal(double[] plane)
        {
            double n = Math.Sqrt(plane[0] * plane[0] + plane[1] * plane[1] + plane[2] * plane[2]);
            return n < 1e-12 ? null : new[] { plane[0] / n, plane[1] / n, plane[2] / n };
        }

        /// <summary>Две единичные оси в плоскости с нормалью plane[0..2].</summary>
        private static bool PlaneAxes(double[] plane, out double[] u, out double[] v)
        {
            u = null;
            v = null;
            double[] normal = PlaneNormal(plane);
            if (normal == null) return false;
            double nx = normal[0], ny = normal[1], nz = normal[2];
            // Ось, наименее параллельная нормали, × нормаль — лежит в плоскости.
            double ax = Math.Abs(nx) < 0.9 ? 1 : 0, ay = 1 - ax;
            u = new[] { ay * nz, -ax * nz, ax * ny - ay * nx };
            double lu = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
            u[0] /= lu;
            u[1] /= lu;
            u[2] /= lu;
            v = new[] { ny * u[2] - nz * u[1], nz * u[0] - nx * u[2], nx * u[1] - ny * u[0] };
            return true;
        }

        /// <summary>Элемент погашен в конфигурации cfg (пусто — в активной), без её переключения; не прочитать — не погашен, как раньше.</summary>
        private static bool SuppressedIn(Feature f, string cfg)
        {
            try
            {
                if (string.IsNullOrEmpty(cfg)) return f.IsSuppressed();
                bool[] states = f.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { cfg }) as bool[];
                return states != null && states.Length > 0 && states[0];
            }
            catch (COMException ex)
            {
                Log.Error("Ведомость ЛЗК: погашен ли элемент", ex);
                return false;
            }
        }

        /// <summary>Наибольшая длина LENGTH по папкам списка вырезов; several — заготовок (тел в папках) больше одной.</summary>
        private static double CutListLength(ModelDoc2 model, out bool several)
        {
            double found = double.NaN;
            int pieces = 0;
            // Только папки с телами активного исполнения, вместе с подсварками (CutListFolders): скрытая
            // папка другого исполнения давала вторую «заготовку» (пометка «оценка») и свою длину — QUANTITY «0» у неё
            // считался за 1 (сверка SW API 23.09.2026, №34). Заготовок в папке — сколько в ней тел: это и есть QUANTITY.
            foreach (Feature sub in CutListFolders.Active(model))
            {
                CustomPropertyManager m = sub.CustomPropertyManager;
                if (m == null) continue;
                // Имена свойств зависят от языка SolidWorks («ДЛИНА», а не LENGTH), поэтому нужное ищется
                // по ссылке внутри значения — она английская всегда (замечание владельца 21.09.2026).
                List<KeyValuePair<string, string>> written = Written(m);
                double v = Value(m, CutListProperties.Find(written, "LENGTH", CutListProperties.LengthSpellings));
                if (double.IsNaN(v)) continue;
                if (double.IsNaN(found) || v > found) found = v;
                pieces += CutListFolders.BodyCount(sub);
            }
            several = pieces > 1;
            return found;
        }

        /// <summary>
        /// Длина заготовки по телу, мм: протяжённость вдоль самого длинного прямого ребра. У детали из нескольких
        /// тел — наибольшая. NaN — мерить не по чему. Рёбра сначала сравниваются по хордам, и только победитель
        /// спрашивается, прямой ли он: вопрос о форме — отдельный вызов SolidWorks на каждое ребро.
        /// </summary>
        public static double MeasuredLength(PartDoc part)
        {
            double best = double.NaN;
            if (part == null) return best;
            try
            {
                object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                if (bodies == null) return best;
                foreach (object o in bodies)
                {
                    Body2 body = o as Body2;
                    if (body == null) continue;
                    double v = MeasuredLength(body);
                    if (!double.IsNaN(v) && (double.IsNaN(best) || v > best)) best = v;
                }
            }
            catch (COMException ex)
            {
                Log.Error("Ведомость ЛЗК: длина по телу", ex);
            }
            return best;
        }

        private static double MeasuredLength(Body2 body)
        {
            object[] edges = body.GetEdges() as object[];
            if (edges == null || edges.Length == 0) return double.NaN;
            List<double[]> curves = new List<double[]>(edges.Length);
            List<double> chords = new List<double>(edges.Length);
            foreach (object o in edges)
            {
                Edge edge = o as Edge;
                double[] p = edge != null ? edge.GetCurveParams2() as double[] : null;
                curves.Add(p);
                chords.Add(BodyLength.Chord(p));
            }
            foreach (int i in BodyLength.ByChordDescending(chords))
            {
                if (chords[i] <= 0) break;
                Curve curve = ((Edge)edges[i]).GetCurve() as Curve;
                if (curve == null || !curve.IsLine()) continue;   // дуга длиннее прямых — спросим следующее
                double[] dir = BodyLength.Direction(curves[i]);
                if (dir == null) continue;
                double fx, fy, fz, bx, by, bz;
                bool forward = body.GetExtremePoint(dir[0], dir[1], dir[2], out fx, out fy, out fz);
                bool backward = body.GetExtremePoint(-dir[0], -dir[1], -dir[2], out bx, out by, out bz);
                double metres = forward && backward
                    ? BodyLength.Along(new[] { fx, fy, fz }, new[] { bx, by, bz }, dir)
                    : chords[i];
                return Math.Max(metres, chords[i]) * 1000.0;
            }
            return double.NaN;
        }

        /// <summary>Пары «имя свойства → записанное значение» папки списка вырезов: по ним ищется нужная величина.</summary>
        public static List<KeyValuePair<string, string>> Written(CustomPropertyManager m)
        {
            List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
            if (m == null) return pairs;
            object[] names = m.GetNames() as object[];
            if (names == null) return pairs;
            foreach (object o in names)
            {
                string name = o as string;
                if (string.IsNullOrEmpty(name)) continue;
                string raw, resolved;
                m.Get4(name, false, out raw, out resolved);
                pairs.Add(new KeyValuePair<string, string>(name, raw ?? ""));
            }
            return pairs;
        }

        /// <summary>Число из свойства с таким именем: сначала вычисленное значение, потом записанное.</summary>
        public static double Value(CustomPropertyManager m, string name)
        {
            if (m == null || string.IsNullOrEmpty(name)) return double.NaN;
            string raw, resolved;
            m.Get4(name, false, out raw, out resolved);
            return CutListProperties.Number(raw, resolved);
        }

        /// <summary>Материал SolidWorks исполнения cfg («Труба 80х80х4,0 …»); «» — не задан или не узнать.</summary>
        private static string SwMaterial(ModelDoc2 model, string cfg)
        {
            PartDoc part = model as PartDoc;
            if (part == null) return "";
            try
            {
                string db;
                return (SyncService.ActualMaterial(part, model, cfg, out db, null) ?? "").Trim();
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: материал детали", ex);
                return "";
            }
        }

        /// <summary>Плотность материала детали, кг/м³; 0 — не определена.</summary>
        private static double Density(ModelDoc2 model)
        {
            try
            {
                MassProperty mp = model.Extension.CreateMassProperty() as MassProperty;
                return mp != null && mp.Density > 0 ? mp.Density : 0;
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: плотность материала", ex);
                return 0;
            }
        }

        /// <summary>
        /// Площадь поверхности под покраску, м². Считается по документу самой модели: масса-свойство сборки,
        /// которому скармливают тела компонента, возвращает площадь всего документа (боевая проверка 17.09.2026 —
        /// у трёх разных деталей выходило одно и то же число).
        /// </summary>
        private double Area(ModelDoc2 model, Component2 comp)
        {
            try
            {
                MassProperty mp = model.Extension.CreateMassProperty() as MassProperty;
                if (mp != null && mp.SurfaceArea > 0) return mp.SurfaceArea;
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: площадь модели " + (model != null ? model.GetPathName() : ""), ex);
            }
            // Запасной путь — тела компонента в контексте сборки.
            try
            {
                List<object> bodies = new List<object>();
                CollectBodies(comp, bodies);
                if (bodies.Count == 0) return double.NaN;
                MassProperty mp = _doc.Extension.CreateMassProperty() as MassProperty;
                if (mp == null || !mp.AddBodies(bodies.ToArray())) return double.NaN;
                return mp.SurfaceArea;
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: площадь " + comp.Name2, ex);
                return double.NaN;
            }
        }

        private static void CollectBodies(Component2 comp, List<object> bodies)
        {
            if (!Counts(comp)) return;
            object info;
            object[] own = comp.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[];
            if (own != null) bodies.AddRange(own);
            object[] children = comp.GetChildren() as object[];
            if (children == null) return;
            foreach (object child in children)
            {
                Component2 c = child as Component2;
                if (c != null) CollectBodies(c, bodies);
            }
        }

        /// <summary>
        /// Площадь окрашиваемой сборки (З-7) — сумма наружных площадей её металлических деталей на одну сборку, со всеми
        /// вложенными узлами. Пластик, древесные плиты и покупные не красятся и не входят; места сварки и касания
        /// не вычитаются (решение владельца: пренебречь). Раньше бралась полная площадь документа сборки —
        /// с внутренней поверхностью труб, почти вдвое больше.
        /// </summary>
        private void PaintedAssemblyAreas(List<Instance> instances, LzkItem top, Dictionary<string, LzkItem> byKey,
            Dictionary<string, ModelTraits> traits)
        {
            foreach (LzkItem assembly in byKey.Values.Where(i => i.IsAssembly && Blanks.HasPainting(i.Operations)))
            {
                if (assembly.IsTop)
                {
                    assembly.AreaM2 = Sum(instances.Where(i => !i.Item.IsAssembly).Select(i => i.Item), traits);
                    continue;
                }
                Instance first = instances.FirstOrDefault(i => object.ReferenceEquals(i.Item, assembly));
                if (first == null) continue;
                List<LzkItem> parts = new List<LzkItem>();
                CollectParts(first.Component, byKey, parts);
                assembly.AreaM2 = Sum(parts, traits);
            }
        }

        private static void CollectParts(Component2 parent, Dictionary<string, LzkItem> byKey, List<LzkItem> parts)
        {
            object[] children = parent.GetChildren() as object[];
            if (children == null) return;
            foreach (object o in children)
            {
                Component2 child = o as Component2;
                if (child == null || !Counts(child)) continue;
                LzkItem item;
                if (!byKey.TryGetValue((child.GetPathName() ?? "") + "|" + (child.ReferencedConfiguration ?? ""), out item)) continue;
                if (item.IsAssembly) CollectParts(child, byKey, parts);
                else parts.Add(item);
            }
        }

        /// <summary>Сумма площадей окрашиваемых деталей; NaN — если ни у одной площадь не определена.</summary>
        private static double Sum(IEnumerable<LzkItem> parts, Dictionary<string, ModelTraits> traits)
        {
            double sum = 0;
            bool any = false;
            foreach (LzkItem part in parts)
            {
                if (part.IsPurchased || double.IsNaN(part.AreaM2)) continue;
                ModelTraits t;
                double density = traits.TryGetValue(part.Path, out t) ? t.DensityKgM3 : 0;
                if (LzkMaterials.Kind(part.Material, density) == MaterialKind.NonMetal) continue;
                sum += part.AreaM2;
                any = true;
            }
            return any ? sum : double.NaN;
        }

        /// <summary>
        /// Узлы с покраской красятся целиком: их детали на лист «Покрасочный» не выводятся (Т-14б). Считается по экземплярам:
        /// кронштейн, вваренный в окрашиваемую раму 4 раза и стоящий отдельно 2 раза, красится отдельно 2 шт
        /// (аудит 21.09.2026, Л-3; раньше один экземпляр вне узла отправлял на покраску все 6). Покраска — по справочнику
        /// участков, со всеми её названиями (Л-5).
        /// </summary>
        private void MarkPaintedUnits(List<Instance> instances, LzkItem top)
        {
            bool topPainted = Blanks.HasPainting(top.Operations);
            Dictionary<LzkItem, int> outside = new Dictionary<LzkItem, int>();
            // Родитель — по пути и конфигурации, одним COM-вызовом на уровень и поиском по словарю.
            Dictionary<string, LzkItem> byKey = new Dictionary<string, LzkItem>(StringComparer.OrdinalIgnoreCase);
            foreach (LzkItem item in _items)
                if (!string.IsNullOrEmpty(item.Path)) byKey[item.Path + "|" + item.Configuration] = item;
            foreach (Instance inst in instances)
            {
                bool inside = topPainted;
                for (Component2 p = inst.Component.GetParent() as Component2; p != null && !inside; p = p.GetParent() as Component2)
                {
                    LzkItem parent;
                    if (byKey.TryGetValue((p.GetPathName() ?? "") + "|" + (p.ReferencedConfiguration ?? ""), out parent) &&
                        Blanks.HasPainting(parent.Operations)) inside = true;
                }
                int n;
                outside.TryGetValue(inst.Item, out n);
                outside[inst.Item] = n + (inside ? 0 : 1);
            }
            foreach (KeyValuePair<LzkItem, int> kv in outside)
            {
                kv.Key.PaintQuantity = kv.Value;
                kv.Key.InsidePaintedUnit = kv.Value == 0;
            }
        }

        private LzkBlanks Blanks
        {
            get { return _options.Blanks ?? LzkBlanks.Defaults(); }
        }


        /// <summary>
        /// «Операции» и «Габарит» — в модели (изменённые сохраняются молча, Т-3). Модель, которую не записать (только для
        /// чтения, не сохраняется), книге не мешает: её операции идут в книгу, а в замечаниях — какие модели остались без записи.
        /// Модель с несохранёнными правками конструктора получает свойства в открытый документ, но не сохраняется (З-25).
        /// </summary>
        private void WriteProperties(List<LzkItem> editable, Dictionary<string, ModelDoc2> models)
        {
            // Одна модель — одна запись и одно сохранение, даже если в изделии стоят два её исполнения. «Габарит» — на
            // уровень конфигурации исполнения, когда их в файле несколько: у «Укосины» 00 и 01 разные длины, и общее
            // свойство перезаписывалось при каждой сборке книги (аудит 21.09.2026, Л-7). «Операции» — общие: окно
            // операций ведёт их по файлу.
            List<string> order = new List<string>();
            Dictionary<string, List<LzkItem>> changed = new Dictionary<string, List<LzkItem>>(StringComparer.OrdinalIgnoreCase);
            List<string> readOnly = new List<string>();
            foreach (LzkItem item in editable)
            {
                ModelDoc2 model = models[item.Path];
                PropertyWriter w = new PropertyWriter(model, true);
                string level = SizeLevel(w, item);
                bool differs = !string.Equals(Prop(w, "", LzkOperations.PropertyName), item.Operations ?? "", StringComparison.Ordinal)
                    || (!item.SizeIsEstimate && item.Size.Length > 0 &&
                        !string.Equals(Prop(w, level, LzkOperations.SizePropertyName), item.Size, StringComparison.Ordinal));
                bool dirtied = _dirtiedBySwitch.Contains(item.Path);
                if (!differs && !dirtied) continue;
                if (model.IsOpenedReadOnly())
                {
                    if (differs && !readOnly.Contains(Path.GetFileName(item.Path))) readOnly.Add(Path.GetFileName(item.Path));
                    continue;
                }
                List<LzkItem> list;
                if (!changed.TryGetValue(item.Path, out list))
                {
                    changed[item.Path] = list = new List<LzkItem>();
                    order.Add(item.Path);
                }
                if (differs) list.Add(item);
            }
            foreach (string name in readOnly)
                NotWritten(name, "модель открыта только для чтения (занята другим пользователем или защищена)");
            int n = 0;
            foreach (string path in order)
            {
                Status(string.Format("ЕСКД: ведомость ЛЗК — запись свойств {0}/{1}…", ++n, order.Count));
                ModelDoc2 model = models[path];
                PropertyWriter w = new PropertyWriter(model, false);
                foreach (LzkItem item in changed[path])
                {
                    if (!item.IsAssembly && LzkOperations.TubeDecisionMayDiffer(Prop(w, "", LzkOperations.PropertyName), item.Operations ?? "") &&
                        !_tubeChanged.Contains(Path.GetFileName(path)))
                        _tubeChanged.Add(Path.GetFileName(path));
                    if (!string.IsNullOrEmpty(item.Operations)) w.Set("", LzkOperations.PropertyName, item.Operations);
                    else w.Delete("", LzkOperations.PropertyName);
                    if (!item.SizeIsEstimate && item.Size.Length > 0) w.Set(SizeLevel(w, item), LzkOperations.SizePropertyName, item.Size);
                }
                if (w.Failures > 0)
                {
                    NotWritten(Path.GetFileName(path), "свойства не записались (подробности — в журнале ЕСКД)");
                    continue;
                }
                if (object.ReferenceEquals(model, _doc)) continue;
                // Сверяется фактическое состояние перед сохранением: любой путь, которым исполнение не вернулось, ловится здесь.
                string refusal = SaveRefusal(_editedBefore.Contains(path), Before(path), ActiveName(model));
                if (refusal.Length > 0)
                {
                    NotSaved(path, refusal, " — операции и габарит записаны в открытую модель, но кнопка её не сохранила");
                    continue;
                }
                CleanPreview(model);
                int errors;
                if (!_saves.Save(model, out errors))
                {
                    NotWritten(Path.GetFileName(path), "модель не сохранена (" + SwCodes.SaveProblem(errors) + ")");
                    continue;
                }
                Log.Info("Ведомость ЛЗК: свойства записаны — " + path);
            }
        }

        public const string EditedRefusal = "в модели ваши несохранённые правки";

        /// <summary>
        /// Почему модель нельзя сохранить кнопкой; пусто — можно (сверка SW API 23.09.2026, №18). Замер переключал
        /// исполнение, а прежнее не вернулось активным — нельзя: в файл ушла бы чужая активная конфигурация, и конструктор
        /// открыл бы деталь в другом исполнении (выгрузка так уже делает — ExportService.RestoreConfiguration). Правки
        /// конструктора — нельзя (З-25). Отделено от SolidWorks ради юнит-тестов.
        /// </summary>
        public static string SaveRefusal(bool editedBefore, string activeBefore, string activeNow)
        {
            if (!string.IsNullOrEmpty(activeBefore) &&
                !string.Equals(activeBefore, activeNow ?? "", StringComparison.OrdinalIgnoreCase))
                return "после замера прежнее исполнение «" + activeBefore + "» не вернулось активным";
            return editedBefore ? EditedRefusal : "";
        }

        /// <summary>Модель не сохранена кнопкой: строка в журнал и замечание — что сделать конструктору.</summary>
        private void NotSaved(string path, string refusal, string tail)
        {
            Log.Info("Ведомость ЛЗК: модель не сохранена (" + refusal + ") — " + path);
            // Файл прежней версии SolidWorks сам «изменён» после открытия — называется своим текстом (№26).
            string text = refusal == EditedRefusal && DocumentGuard.OlderVersion(_app, path) ? SwFileVersion.OlderNote : refusal;
            _notes.Add(Notices.Of(NoticeLevel.Warning, Path.GetFileName(path), text + tail,
                refusal == EditedRefusal ? "Сохраните модель сами"
                    : "Сделайте исполнение «" + Before(path) + "» активным и сохраните модель сами"));
        }

        private string Before(string path)
        {
            string before;
            return _activeBefore.TryGetValue(path, out before) ? before : "";
        }

        private static string ActiveName(ModelDoc2 model)
        {
            try
            {
                Configuration c = model.GetActiveConfiguration() as Configuration;
                return c != null ? c.Name ?? "" : "";
            }
            catch (COMException ex)
            {
                Log.Error("Ведомость ЛЗК: активное исполнение", ex);
                return "";
            }
        }

        /// <summary>Уровень «Габарита»: конфигурация исполнения, если их в файле несколько; иначе общие свойства.</summary>
        private static string SizeLevel(PropertyWriter w, LzkItem item)
        {
            string cfg = item.Configuration ?? "";
            string[] configs = w.ConfigurationNames();
            return cfg.Length > 0 && configs.Length > 1 && configs.Contains(cfg) ? cfg : "";
        }


        private void NotWritten(string model, string reason)
        {
            Log.Warn("Ведомость ЛЗК: " + model + " — " + reason);
            _notes.Add(Notices.Of(NoticeLevel.Warning, model, reason + ": операции записаны только в книгу",
                "Когда модель станет доступна для записи, пересоберите книгу — операции запишутся и в модель"));
        }

        // ------------------------------------------------------------------ SWTools
        internal static string SwToolsExe()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    // Установщик SWTools пишет папку в значение по умолчанию раздела SOFTWARE\SWTools\InstallRoot.
                    using (RegistryKey key = hklm.OpenSubKey(@"SOFTWARE\SWTools\InstallRoot"))
                    {
                        string root = key != null ? key.GetValue("") as string : null;
                        if (string.IsNullOrEmpty(root)) continue;
                        string exe = Path.Combine(root, "SWTools.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("SWTools InstallRoot", ex);
                }
            }
            return null;
        }

        /// <summary>
        /// Надстройка SWTools в этом SolidWorks. Не загружена — загружается из её регистрации; null — не удалось.
        /// Выгрузку запускает она сама: без её канала-приёмника SWTools.exe модель не читает.
        /// </summary>
        private object SwToolsAddin()
        {
            object addin = _app.GetAddInObject(SwToolsExport.AddinClsid);
            if (addin != null) return addin;
            string dll = SwToolsAddinDll();
            if (dll == null) return null;
            int rc = _app.LoadAddIn(dll);
            Log.Info("Ведомость ЛЗК: загрузка надстройки SWTools " + dll + " — код " + rc);
            return _app.GetAddInObject(SwToolsExport.AddinClsid);
        }

        private static string SwToolsAddinDll()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = hklm.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + SwToolsExport.AddinClsid + @"\InprocServer32"))
                    {
                        string codeBase = key != null ? key.GetValue("CodeBase") as string : null;
                        if (string.IsNullOrEmpty(codeBase)) continue;
                        string path = new Uri(codeBase).LocalPath;
                        if (File.Exists(path)) return path;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Ведомость ЛЗК: регистрация надстройки SWTools", ex);
                }
            }
            return null;
        }

        /// <summary>Запуск выгрузки. false — отказ уже показан пользователю.</summary>
        private bool Launch()
        {
            object addin = SwToolsAddin();
            if (addin == null)
            {
                Info("Надстройка SWTools не загружена в SolidWorks. Включите её: Инструменты → Надстройки → SWTools, и повторите.",
                    MessageBoxIcon.Warning);
                return false;
            }
            string work = Path.Combine(Path.GetTempPath(), "ESKD", "LZK");
            Directory.CreateDirectory(work);
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            _tempWorkbook = Path.Combine(work, "Ведомость_" + id + ".xlsx");
            _resultPath = Path.Combine(work, "result_" + id + ".txt");
            int pid;
            try
            {
                pid = Convert.ToInt32(addin.GetType().InvokeMember("StartBomExport", BindingFlags.InvokeMethod, null, addin,
                    new object[] { SwToolsExport.Preset, _tempWorkbook, _resultPath, _assemblyPath }));
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: StartBomExport", ex);
                Info("Установленная версия SWTools не умеет выгружать ведомость без окна. " + SwToolsExport.UpdateAdvice + ".",
                    MessageBoxIcon.Warning);
                return false;
            }
            if (pid == 0)
            {
                string reason = "";
                try
                {
                    reason = Convert.ToString(addin.GetType().InvokeMember("LastBomExportError", BindingFlags.GetProperty, null, addin, null));
                }
                catch (Exception ex)
                {
                    Log.Error("Ведомость ЛЗК: LastBomExportError", ex);
                }
                Info("SWTools не запустил выгрузку: " + reason, MessageBoxIcon.Warning);
                return false;
            }
            Log.Info("Ведомость ЛЗК: SWTools.exe PID " + pid + ", книга " + _tempWorkbook + ", модель " + _assemblyPath);
            try
            {
                _process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                // SWTools успел завершиться: итог — в файле отчёта.
                _process = null;
                _exitedEarly = true;
            }
            _started = DateTime.Now;
            Status("ЕСКД: ведомость ЛЗК — SWTools выгружает спецификацию…");
            // Завершение SWTools проверяется по таймеру и в простое SolidWorks — из постоянной подписки EventHub (PollIdle):
            // таймер WinForms в SolidWorks, запущенном через COM, не срабатывает, а подписываться и отписываться от
            // OnIdleNotify из его же обработчика нельзя (класс ошибки M07).
            _timer = new Timer { Interval = 500 };
            _timer.Tick += OnTick;
            _timer.Start();
            return true;
        }

        /// <summary>
        /// Надстройку выгружают (сняли галочку в «Надстройках» или закрывают SolidWorks), а ведомость ещё формируется:
        /// таймер остановить, SWTools завершить, временные файлы убрать. Раньше таймер выгруженной надстройки дописывал
        /// книгу и показывал окно, а в SolidWorks, запущенном из другой программы, ведомость зависала до следующей
        /// загрузки (сверка SW API 23.09.2026, №3). Прежняя книга не тронута: выгруженная надстройка книгу не пишет и окон
        /// не показывает.
        /// </summary>
        public static void Abort(string reason)
        {
            LzkService running = _running;
            if (running == null) return;
            running._finishing = true;
            running._finished = true;
            try
            {
                // Kill не ждёт конца процесса: без ожидания SWTools ещё держит временную книгу, и уборка её не удалит
                // (ревью 23.09.2026).
                if (running._process != null && !running._process.HasExited)
                {
                    running._process.Kill();
                    running._process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: остановка SWTools при выгрузке", ex);
            }
            try
            {
                running.CleanupAttempt();
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: уборка при выгрузке", ex);
            }
            finally
            {
                // Сбой уборки не оставляет службу текущей: иначе после повторного включения — «уже формируется».
                if (object.ReferenceEquals(_running, running)) _running = null;
            }
            _lastOutcome = "error|Ведомость ЛЗК прервана: " + reason + ". Книга не записана, прежняя на месте — запустите ведомость заново.";
            Log.Warn("Ведомость ЛЗК прервана (" + reason + "): книга не записана, прежняя на месте");
        }

        /// <summary>Опрос из простоя SolidWorks (EventHub.OnIdle).</summary>
        public static void PollIdle()
        {
            LzkService running = _running;
            if (running != null) running.Poll();
        }

        private void OnTick(object sender, EventArgs e)
        {
            Poll();
        }

        private void Poll()
        {
            if (_finishing || (_process == null && !_exitedEarly)) return;
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    if ((DateTime.Now - _started).TotalMinutes < TimeoutMinutes) return;
                    _finishing = true;
                    _timer.Stop();
                    // Kill не ждёт конца процесса — без ожидания временная книга ещё занята и не удалится (ревью 23.09.2026).
                    try
                    {
                        _process.Kill();
                        _process.WaitForExit(5000);
                    }
                    catch (InvalidOperationException ex) { Log.Error("Ведомость ЛЗК: остановка SWTools", ex); }
                    catch (System.ComponentModel.Win32Exception ex) { Log.Error("Ведомость ЛЗК: остановка SWTools", ex); }
                    Finish(null, "SWTools не ответил за " + TimeoutMinutes + " мин и остановлен.");
                    return;
                }
                _finishing = true;
                _timer.Stop();
                // SWTools запущен надстройкой SWTools: код завершения этому процессу недоступен, итог — в отчёте.
                SwToolsExport.Outcome outcome = SwToolsExport.ReadResult(_resultPath);
                // Пакет с данными потерялся по дороге из SolidWorks — изделие ни при чём, выгрузка повторяется сама.
                if (SwToolsExport.DeliveryLost(outcome) && _attempt < Attempts && Retry(outcome)) return;
                string problem = outcome.Ok ? "" : SwToolsExport.Explain(outcome, -1);
                Finish(outcome, problem);
            }
            catch (Exception ex)
            {
                _finishing = true;
                if (_timer != null) _timer.Stop();
                Log.Error("Ведомость ЛЗК: завершение", ex);
                if (!_finished) Finish(null, ex.Message);
            }
        }

        /// <summary>
        /// Повторный запуск выгрузки после потерянного пакета. Прежние временные файлы убираются, таймер заводится
        /// заново. false — повторить не удалось (отказ уже показан), итог подводится по прежней попытке.
        /// </summary>
        private bool Retry(SwToolsExport.Outcome outcome)
        {
            _attempt++;
            Log.Warn("Ведомость ЛЗК: SWTools не получил данные из SolidWorks («" + (outcome.Error ?? "").Trim() +
                     "») — повтор " + _attempt + " из " + Attempts);
            CleanupAttempt();
            _exitedEarly = false;
            _finishing = false;
            Status("ЕСКД: ведомость ЛЗК — SolidWorks не передал состав, повтор…");
            if (Launch()) return true;
            _finishing = true;
            return false;
        }

        private void Finish(SwToolsExport.Outcome outcome, string problem)
        {
            _finished = true;
            LzkResult result = null;
            try
            {
                if (problem.Length == 0 && File.Exists(_tempWorkbook))
                {
                    Status("ЕСКД: ведомость ЛЗК — паспорт, участки, расход, проверка…");
                    result = LzkWorkbook.Complete(_tempWorkbook, _header, _items, _options);
                    if (result.Errors.Count > 0) problem = string.Join("\n", result.Errors.ToArray());
                }
                else if (problem.Length == 0)
                {
                    problem = "SWTools сообщил об успехе, но файл ведомости не создан.";
                }

                if (problem.Length == 0)
                {
                    try
                    {
                        Deliver();
                        if (!_inOrder)
                            _notes.Add(Notices.Of(NoticeLevel.Info, Path.GetFileName(_assemblyPath),
                                "сборка лежит не в папке «01_3D»: книга ЛЗК записана рядом со сборкой, новых папок нет"));
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        // Папка без права записи (чужой ресурс, архив, защищённая папка): книга не пропадает. Прочие
                        // ошибки (книгу открыли в Excel, сеть) — отказ: прежняя книга возвращена на место.
                        Log.Error("Ведомость ЛЗК: запись рядом со сборкой", ex);
                        string denied = Path.GetDirectoryName(_workbookPath);
                        _inOrder = false;
                        _workbookPath = LzkNaming.FallbackWorkbookPath(_cipher);
                        Deliver();
                        _notes.Add(Notices.Of(NoticeLevel.Warning, Path.GetFileName(_workbookPath),
                            "в папку «" + denied + "» записать нельзя (" + ex.Message.Trim() + "): книга сохранена в «" +
                            Path.GetDirectoryName(_workbookPath) + "»", "Перенесите книгу к изделию, когда папка станет доступна"));
                    }
                }
                if (outcome != null && outcome.Version.Length > 0) Log.Info("Ведомость ЛЗК: SWTools " + outcome.Version);
                if (problem.Length == 0 && outcome != null)
                {
                    string old = SwToolsExport.VersionWarning(outcome.Version);
                    if (old.Length > 0)
                        _notes.Add(Notices.Of(NoticeLevel.Warning, Path.GetFileName(_workbookPath), old, SwToolsExport.UpdateAdvice));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: запись", ex);
                if (problem.Length == 0) problem = ex.Message;
            }
            finally
            {
                Cleanup();
            }
            try
            {
                Report(result, problem);
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: итог", ex);
            }
        }

        /// <summary>
        /// Готовая книга из временной папки — на место. Прежняя книга: в структуре заказа — в «_Аннулировано», вне её — в
        /// резервную копию (LzkNaming.BackupPath); не встала новая — прежняя возвращается на место, «.new» не остаётся.
        /// </summary>
        private void Deliver()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_workbookPath));
            string fresh = _workbookPath + ".new";
            try
            {
                // Новая книга сначала ложится рядом («.new»): упадёт копирование на NAS — прежняя книга на месте.
                if (File.Exists(fresh)) File.Delete(fresh);
                File.Copy(_tempWorkbook, fresh, false);
                if (File.Exists(_workbookPath)) ReplacePrevious(fresh);
                else File.Move(fresh, _workbookPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(fresh)) File.Delete(fresh);
                }
                catch (IOException ex)
                {
                    Log.Error("Ведомость ЛЗК: " + fresh, ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Error("Ведомость ЛЗК: " + fresh, ex);
                }
            }
            // Ведомость старого образца (в корне изделия) заменяется книгой ЛЗК — два документа об одном не нужны.
            if (_inOrder && File.Exists(_legacyPath))
            {
                string archive = LzkNaming.ArchivePath(_productFolder, _cipher, File.GetLastWriteTime(_legacyPath), true);
                Directory.CreateDirectory(Path.GetDirectoryName(archive));
                File.Move(_legacyPath, archive);
                _notes.Add(Notices.Of(NoticeLevel.Info, Path.GetFileName(_legacyPath), "ведомость старого образца перенесена в «" + LzkNaming.ArchiveFolder + "»"));
            }
        }

        private void ReplacePrevious(string fresh)
        {
            DateTime stamp = File.GetLastWriteTime(_workbookPath);
            string previous = _inOrder ? LzkNaming.ArchivePath(_productFolder, _cipher, stamp) : LzkNaming.BackupPath(_cipher, stamp);
            Directory.CreateDirectory(Path.GetDirectoryName(previous));
            File.Copy(_workbookPath, previous, false);
            try
            {
                File.Delete(_workbookPath);
                File.Move(fresh, _workbookPath);
            }
            catch
            {
                if (!File.Exists(_workbookPath)) File.Copy(previous, _workbookPath, false);
                if (!_inOrder) File.Delete(previous);
                throw;
            }
            if (_inOrder)
            {
                _notes.Add(Notices.Of(NoticeLevel.Info, Path.GetFileName(_workbookPath),
                    "прежняя книга перенесена в «" + LzkNaming.ArchiveFolder + "»: " + Path.GetFileName(previous)));
            }
            else
            {
                _notes.Add(Notices.Of(NoticeLevel.Info, Path.GetFileName(_workbookPath),
                    "прежняя книга заменена, её копия — " + previous));
            }
        }

        private void Report(LzkResult result, string problem)
        {
            if (problem.Length > 0)
            {
                Status("ЕСКД: ведомость ЛЗК не сформирована");
                List<Notice> failed = new List<Notice> { Notices.Of(NoticeLevel.Critical, "", problem) };
                failed.AddRange(_notes);
                Notices.Remember(failed);
                _lastOutcome = "error|Ведомость ЛЗК не сформирована.\n\n" + problem;
                if (_silent)
                {
                    Log.Warn("Ведомость ЛЗК не сформирована: " + problem);
                    return;
                }
                NoticeForm.Present(_app, "ЕСКД: Ведомость ЛЗК", "Книга ЛЗК не сформирована",
                    "Прежняя книга, если была, осталась на месте.", failed, NoticeLevel.Critical);
                return;
            }
            int issues = result.Issues.Count;
            List<Notice> notices = Notices.FromLzk(result);
            notices.AddRange(_notes);
            Notices.Remember(notices);
            _lastOutcome = "ok|" + _workbookPath + "|" + result.Rows + "|" + issues;
            Log.Info("Ведомость ЛЗК: " + _lastOutcome);
            if (_silent)
            {
                Status("ЕСКД: ведомость ЛЗК сохранена");
                return;
            }
            Status("ЕСКД: ведомость ЛЗК сохранена — " + Path.GetFileName(_workbookPath) + (issues > 0 ? ", пометок «?» " + issues : ""));
            string workbook = _workbookPath;
            string details = workbook + Environment.NewLine +
                string.Format("Строк: {0}; изделий в заказе: {1}; участки: {2}.", result.Rows, _inputs != null ? _inputs.Quantity : 1,
                    result.SectionRows.Count == 0 ? "—" : string.Join(", ", result.SectionRows.Select(kv => kv.Key + " " + kv.Value).ToArray()));
            NoticeForm.Present(_app, "ЕСКД: Ведомость ЛЗК",
                issues == 0 ? "Книга ЛЗК сохранена" : "Книга ЛЗК сохранена, но в ней есть пометки «?»: " + issues,
                details, notices, issues > 0 ? NoticeLevel.Warning : (NoticeLevel?)null,
                new NoticeButton("Открыть книгу ЛЗК", () => Process.Start(workbook)),
                new NoticeButton("Открыть папку", () => Process.Start("explorer.exe", "/select,\"" + workbook + "\"")));
        }

        private void Cleanup()
        {
            CleanupAttempt();
            if (object.ReferenceEquals(_running, this)) _running = null;
        }

        /// <summary>Хвосты одной попытки: таймер, временные файлы SWTools и его процесс. Служба остаётся текущей.</summary>
        private void CleanupAttempt()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
            foreach (string f in new[] { _tempWorkbook, _resultPath })
            {
                try
                {
                    if (!string.IsNullOrEmpty(f) && File.Exists(f)) File.Delete(f);
                }
                catch (IOException ex)
                {
                    Log.Error("Ведомость ЛЗК: временный файл " + f, ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Error("Ведомость ЛЗК: временный файл " + f, ex);
                }
            }
            if (_process != null) _process.Dispose();
            _process = null;
        }

        // swDisplayOrigins не включён: у модели без окна SolidWorks 2025 отвечает на него RPC_E_SERVERFAULT и может упасть.
        private static readonly swUserPreferenceToggle_e[] HiddenInPreview =
        {
            swUserPreferenceToggle_e.swDisplaySketches, swUserPreferenceToggle_e.swDisplayPlanes, swUserPreferenceToggle_e.swDisplayAxes,
            swUserPreferenceToggle_e.swDisplayTemporaryAxes,
            swUserPreferenceToggle_e.swDisplayCoordSystems, swUserPreferenceToggle_e.swDisplayCurves,
            swUserPreferenceToggle_e.swDisplayReferencePoints, swUserPreferenceToggle_e.swDisplayReferencePoints2,
            swUserPreferenceToggle_e.swDisplayAnnotations, swUserPreferenceToggle_e.swDisplayAllAnnotations,
            swUserPreferenceToggle_e.swDisplayReferenceTriad, swUserPreferenceToggle_e.swDisplayArcCenterPoints,
            swUserPreferenceToggle_e.swDisplayEntityPoints, swUserPreferenceToggle_e.swDisplayVirtualSharps,
            swUserPreferenceToggle_e.swDisplayLights, swUserPreferenceToggle_e.swDisplayCameras
        };

        /// <summary>
        /// Чистый эскиз в файле модели (ТЗ-04, «Эскизы»): SolidWorks сохраняет в файл картинку вида, и эскизы, плоскости,
        /// оси и начала координат попадают в эскизы ведомости. У модели без открытого окна они скрываются, вид —
        /// изометрия во весь экран. Модели, открытые пользователем в окне, не трогаются: у него на глазах вид не меняется.
        /// </summary>
        private static void CleanPreview(ModelDoc2 model)
        {
            try
            {
                if (model.Visible) return;
                foreach (swUserPreferenceToggle_e t in HiddenInPreview)
                {
                    try
                    {
                        model.Extension.SetUserPreferenceToggle((int)t, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, false);
                    }
                    catch (COMException ex)
                    {
                        // Настройка не документа, а системы: у модели её не переключить — эскиз без неё всё равно чище.
                        Log.WarnOnce("Ведомость ЛЗК: чистый эскиз — настройка " + t + " у модели недоступна: " + ex.Message);
                    }
                }
                model.ShowNamedView2("", (int)swStandardViews_e.swIsometricView);
                model.ViewZoomtofit2();
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: чистый эскиз " + DocInfo.TitleOf(model), ex);
            }
        }

        private static bool FileLocked(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Папка только для чтения — книга не занята; запись уйдёт в запасное место.
                return false;
            }
        }

        private static string Checksum(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] hash = sha.ComputeHash(fs);
                return "SHA-256 " + BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
