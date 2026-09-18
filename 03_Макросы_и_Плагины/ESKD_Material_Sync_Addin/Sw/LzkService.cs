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
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Кнопка К-4 «Ведомость ЛЗК» (ТЗ-02 Т-35…Т-37): окно «Операции» → запись «Операции» и «Габарит» в новые модели
    /// изделия → сохранение сборки → SWTools без окна по пресету «ЛЗК» → листы «Покраска», «Покупные», шапка и пометки
    /// → Ведомость_&lt;шифр&gt;.xlsx в папке изделия (прежняя — в _Аннулировано) и отчёт _Ведомость.txt.
    /// SWTools ждётся таймером в потоке SolidWorks: синхронное ожидание заблокировало бы надстройку SWTools,
    /// которая читает модель в этом же потоке.
    /// </summary>
    public sealed class LzkService
    {
        private const int TimeoutMinutes = 15;
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
        private string _tempWorkbook;
        private string _resultPath;
        private readonly List<LzkItem> _items = new List<LzkItem>();
        private readonly List<string> _notes = new List<string>();
        private Process _process;
        private Timer _timer;
        private DateTime _started;
        private LzkHeader _header;
        private SldWorks _events;
        private bool _finishing;
        private bool _exitedEarly;

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
            _silent = !interactive;
            if (_running != null)
            {
                Info("Ведомость ЛЗК уже формируется — дождитесь окончания.", MessageBoxIcon.Information);
                return;
            }
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
                Info("Откройте главную сборку изделия (папка «01_3D») и нажмите кнопку ещё раз.", MessageBoxIcon.Information);
                return false;
            }
            _assemblyPath = DocInfo.PathOf(_doc);
            if (_assemblyPath.Length == 0)
            {
                Info("Сборка ещё не сохранена в файл. Сохраните её в папку «01_3D» изделия.", MessageBoxIcon.Information);
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
            _workbookPath = LzkNaming.WorkbookPath(_productFolder, _cipher);
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(_assemblyPath)), LzkNaming.ModelsFolder, StringComparison.OrdinalIgnoreCase))
                _notes.Add("Сборка лежит не в папке «01_3D»: ведомость записана рядом со сборкой.");
            if (FileLocked(_workbookPath))
            {
                Info("Файл " + Path.GetFileName(_workbookPath) + " открыт в Excel. Закройте его и повторите.", MessageBoxIcon.Information);
                return false;
            }

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

            LzkItem top = Describe(_doc, _assemblyPath, "", true, traits);
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
                    _notes.Add("Модель не загружена, пропущена: " + path);
                    continue;
                }
                // Строка ведомости — модель в конкретной конфигурации: у исполнений одного файла свои обозначения
                // и количества («Укосина» 00 и 01 — NC3-7R.02.000 и NC3-7R.02.000-01).
                string cfg = comp.ReferencedConfiguration ?? "";
                string key = path + "|" + cfg;
                LzkItem item;
                if (!byKey.TryGetValue(key, out item))
                {
                    item = Describe(model, path, cfg, model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY, traits);
                    item.AreaM2 = Area(model, comp);
                    byKey[key] = item;
                    models[path] = model;
                }
                item.Quantity++;
                instances.Add(new Instance { Component = comp, Item = item });
            }
            _items.AddRange(byKey.Values);

            List<LzkItem> editable = _items.Where(i => i.InProduct && !i.IsPurchased).ToList();
            if (interactive && editable.Count > 0)
            {
                Dictionary<string, string> chosen;
                using (LzkOperationsForm form = new LzkOperationsForm(editable, traits, path => ShellThumbnail.Get(path, 256)))
                {
                    if (form.ShowDialog(Owner()) != DialogResult.OK) return false;
                    chosen = form.Result;
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

            if (!WriteProperties(editable, models)) return false;
            MarkPaintedUnits(instances, top);

            Status("ЕСКД: ведомость ЛЗК — сохранение сборки…");
            int errors = 0, warnings = 0;
            if (!_doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
            {
                Info("Сборка не сохранена (код " + errors + "). Ведомость не сформирована.", MessageBoxIcon.Warning);
                return false;
            }

            _header = new LzkHeader
            {
                Product = _cipher + (top.Name.Length > 0 ? " " + top.Name : ""),
                Author = _settings.Author ?? "",
                Model = Path.GetFileName(_assemblyPath),
                Date = DateTime.Now.ToString("dd.MM.yyyy HH:mm"),
                Checksum = Checksum(_assemblyPath)
            };
            return true;
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
                if (c.IsSuppressed() || c.ExcludeFromBOM || c.IsEnvelope()) return false;
            }
            return true;
        }

        private LzkItem Describe(ModelDoc2 model, string path, string cfg, bool assembly, Dictionary<string, ModelTraits> traits)
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
                IsPurchased = SafeProtected(w, model),
                Designation = Prop(w, active, "Обозначение"),
                Name = Prop(w, active, "Наименование"),
                Operations = Prop(w, active, LzkOperations.PropertyName),
                Code = Prop(w, active, "Код_Продукции")
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
            ModelTraits t = Traits(model, assembly);
            t.Material = item.Material;
            t.IsPurchased = item.IsPurchased;
            if (!assembly) t.DensityKgM3 = Density(model);
            traits[path] = t;
            item.IsProfile = t.IsStructuralMember;
            Size(model, assembly, t, item);
            return item;
        }

        private static bool SafeProtected(PropertyWriter w, ModelDoc2 model)
        {
            try
            {
                return SyncService.IsProtected(w, model);
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: признак покупного", ex);
                return false;
            }
        }

        private static string Prop(PropertyWriter w, string cfg, string name)
        {
            string value = w.Resolved(cfg, name);
            if (string.IsNullOrWhiteSpace(value)) value = w.Resolved("", name);
            return (value ?? "").Trim();
        }

        private static ModelTraits Traits(ModelDoc2 model, bool assembly)
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
                    else if (type == "WeldMemberFeat") t.IsStructuralMember = true;
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

        /// <summary>Габарит: прокат — длина заготовки (LENGTH списка вырезов, иначе RD1@Примечания, иначе габарит с «*»); прочее — Д×Ш×В.</summary>
        private static void Size(ModelDoc2 model, bool assembly, ModelTraits t, LzkItem item)
        {
            try
            {
                if (t.IsStructuralMember)
                {
                    double length = CutListLength(model);
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
                if (box != null && box.Length >= 6)
                    item.Size = LzkOperations.FormatSize((box[3] - box[0]) * 1000, (box[4] - box[1]) * 1000, (box[5] - box[2]) * 1000);
            }
            catch (Exception ex)
            {
                Log.Error("Ведомость ЛЗК: габарит " + item.Path, ex);
            }
        }

        private static double CutListLength(ModelDoc2 model)
        {
            double found = double.NaN;
            for (Feature f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
            {
                if (f.GetTypeName2() != "SolidBodyFolder") continue;
                for (Feature sub = f.GetFirstSubFeature() as Feature; sub != null; sub = sub.GetNextSubFeature() as Feature)
                {
                    if (sub.GetTypeName2() != "CutListFolder") continue;
                    CustomPropertyManager m = sub.CustomPropertyManager;
                    if (m == null) continue;
                    string raw, resolved;
                    m.Get4("LENGTH", false, out raw, out resolved);
                    double v = LzkOperations.ParseNumber(resolved);
                    if (double.IsNaN(v)) v = LzkOperations.ParseNumber(raw);
                    if (!double.IsNaN(v) && (double.IsNaN(found) || v > found)) found = v;
                }
            }
            return found;
        }

        /// <summary>Площадь поверхности экземпляра, м² (для узла — сумма тел всех деталей).</summary>
        /// <summary>
        /// Площадь поверхности под покраску, м². Считается по документу самой модели: масса-свойство сборки,
        /// которому скармливают тела компонента, возвращает площадь всего документа (боевая проверка 17.09.2026 —
        /// у трёх разных деталей выходило одно и то же число).
        /// </summary>
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

        /// <summary>Узлы с «Покраска» красятся целиком: их детали на лист «Покраска» не выводятся (Т-14б).</summary>
        private void MarkPaintedUnits(List<Instance> instances, LzkItem top)
        {
            bool topPainted = LzkOperations.Contains(top.Operations, LzkOperations.Painting);
            Dictionary<LzkItem, bool> allInside = new Dictionary<LzkItem, bool>();
            foreach (Instance inst in instances)
            {
                bool inside = topPainted;
                for (Component2 p = inst.Component.GetParent() as Component2; p != null && !inside; p = p.GetParent() as Component2)
                {
                    LzkItem parent = _items.FirstOrDefault(i => string.Equals(i.Path, p.GetPathName(), StringComparison.OrdinalIgnoreCase));
                    if (parent != null && LzkOperations.Contains(parent.Operations, LzkOperations.Painting)) inside = true;
                }
                bool prev;
                allInside[inst.Item] = allInside.TryGetValue(inst.Item, out prev) ? prev && inside : inside;
            }
            foreach (KeyValuePair<LzkItem, bool> kv in allInside) kv.Key.InsidePaintedUnit = kv.Value;
        }

        /// <summary>«Операции» и «Габарит» — в общие свойства новых моделей изделия; изменённые модели сохраняются молча (Т-3).</summary>
        private bool WriteProperties(List<LzkItem> editable, Dictionary<string, ModelDoc2> models)
        {
            List<string> readOnly = new List<string>();
            List<KeyValuePair<LzkItem, ModelDoc2>> changed = new List<KeyValuePair<LzkItem, ModelDoc2>>();
            foreach (LzkItem item in editable)
            {
                ModelDoc2 model = models[item.Path];
                PropertyWriter w = new PropertyWriter(model, true);
                bool differs = !string.Equals(Prop(w, "", LzkOperations.PropertyName), item.Operations ?? "", StringComparison.Ordinal)
                    || (!item.SizeIsEstimate && item.Size.Length > 0 &&
                        !string.Equals(Prop(w, "", LzkOperations.SizePropertyName), item.Size, StringComparison.Ordinal));
                if (!differs) continue;
                if (model.IsOpenedReadOnly()) readOnly.Add(Path.GetFileName(item.Path));
                else changed.Add(new KeyValuePair<LzkItem, ModelDoc2>(item, model));
            }
            if (readOnly.Count > 0)
            {
                Info("Эти модели открыты только для чтения (заняты другим пользователем или защищены), свойства в них не записать:\n\n" +
                     string.Join("\n", readOnly.ToArray()) + "\n\nВедомость не сформирована.", MessageBoxIcon.Warning);
                return false;
            }
            int n = 0;
            foreach (KeyValuePair<LzkItem, ModelDoc2> kv in changed)
            {
                Status(string.Format("ЕСКД: ведомость ЛЗК — запись свойств {0}/{1}…", ++n, changed.Count));
                PropertyWriter w = new PropertyWriter(kv.Value, false);
                if (!string.IsNullOrEmpty(kv.Key.Operations)) w.Set("", LzkOperations.PropertyName, kv.Key.Operations);
                else w.Delete("", LzkOperations.PropertyName);
                if (!kv.Key.SizeIsEstimate && kv.Key.Size.Length > 0) w.Set("", LzkOperations.SizePropertyName, kv.Key.Size);
                if (w.Failures > 0)
                {
                    Info("Не удалось записать свойства в " + Path.GetFileName(kv.Key.Path) + ". Подробности — в журнале ЕСКД.", MessageBoxIcon.Warning);
                    return false;
                }
                if (object.ReferenceEquals(kv.Value, _doc)) continue;
                int errors = 0, warnings = 0;
                if (!kv.Value.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                {
                    Info("Модель " + Path.GetFileName(kv.Key.Path) + " не сохранена (код " + errors + "). Ведомость не сформирована.", MessageBoxIcon.Warning);
                    return false;
                }
                Log.Info("Ведомость ЛЗК: свойства записаны — " + kv.Key.Path);
            }
            return true;
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
                Info("Установленная версия SWTools не умеет выгружать ведомость без окна. Обновите SWTools до 1.1.109 или новее " +
                     "(запустите настройку рабочего места).", MessageBoxIcon.Warning);
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
            // Завершение SWTools проверяется в простое SolidWorks (как отложенные задачи EventHub) и по таймеру.
            _events = _app as SldWorks;
            if (_events != null) _events.OnIdleNotify += OnIdle;
            _timer = new Timer { Interval = 500 };
            _timer.Tick += OnTick;
            _timer.Start();
            return true;
        }

        private int OnIdle()
        {
            Poll();
            return 0;
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
                    try { _process.Kill(); }
                    catch (InvalidOperationException ex) { Log.Error("Ведомость ЛЗК: остановка SWTools", ex); }
                    Finish(null, "SWTools не ответил за " + TimeoutMinutes + " мин и остановлен.");
                    return;
                }
                _finishing = true;
                _timer.Stop();
                // SWTools запущен надстройкой SWTools: код завершения этому процессу недоступен, итог — в отчёте.
                SwToolsExport.Outcome outcome = SwToolsExport.ReadResult(_resultPath);
                string problem = outcome.Ok ? "" : SwToolsExport.Explain(outcome, -1);
                Finish(outcome, problem);
            }
            catch (Exception ex)
            {
                _finishing = true;
                if (_timer != null) _timer.Stop();
                Log.Error("Ведомость ЛЗК: завершение", ex);
                Finish(null, ex.Message);
            }
        }

        private void Finish(SwToolsExport.Outcome outcome, string problem)
        {
            LzkResult result = null;
            try
            {
                if (problem.Length == 0 && File.Exists(_tempWorkbook))
                {
                    Status("ЕСКД: ведомость ЛЗК — покраска, покупные, проверка…");
                    result = LzkWorkbook.Complete(_tempWorkbook, _header, _items);
                    if (result.Errors.Count > 0) problem = string.Join("\n", result.Errors.ToArray());
                }
                else if (problem.Length == 0)
                {
                    problem = "SWTools сообщил об успехе, но файл ведомости не создан.";
                }

                if (problem.Length == 0)
                {
                    if (File.Exists(_workbookPath))
                    {
                        string archive = LzkNaming.ArchivePath(_productFolder, _cipher, File.GetLastWriteTime(_workbookPath));
                        Directory.CreateDirectory(Path.GetDirectoryName(archive));
                        File.Move(_workbookPath, archive);
                        _notes.Add("Прежняя ведомость перенесена: " + archive);
                    }
                    File.Copy(_tempWorkbook, _workbookPath, false);
                }
                if (outcome != null && outcome.Version.Length > 0) _notes.Add("SWTools " + outcome.Version);
                string report = LzkWorkbook.Report(_header, problem.Length == 0 ? _workbookPath : "(не создан)",
                    result, problem.Length == 0 ? _notes : new[] { "ОШИБКА: " + problem }.Concat(_notes));
                File.WriteAllText(LzkNaming.ReportPath(_productFolder), report, new UTF8Encoding(true));
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

            if (problem.Length > 0)
            {
                Status("ЕСКД: ведомость ЛЗК не сформирована");
                Info("Ведомость ЛЗК не сформирована.\n\n" + problem, MessageBoxIcon.Warning);
                return;
            }
            int issues = result.Issues.Count;
            _lastOutcome = "ok|" + _workbookPath + "|" + result.Rows + "|" + issues;
            Log.Info("Ведомость ЛЗК: " + _lastOutcome);
            if (_silent)
            {
                Status("ЕСКД: ведомость ЛЗК сохранена");
                return;
            }
            Status("ЕСКД: ведомость ЛЗК сохранена — " + Path.GetFileName(_workbookPath) + (issues > 0 ? ", замечаний " + issues : ""));
            string text = string.Format("Ведомость сохранена:\n{0}\n\nСтрок: {1}; покраска: {2}; покупные: {3}.\n{4}\n\nОткрыть ведомость?",
                _workbookPath, result.Rows, result.PaintRows, result.PurchasedRows,
                issues == 0 ? "Замечаний нет." : "Замечаний: " + issues + " — пометки «?» в ведомости, список в " + LzkNaming.ReportName + ".");
            if (MessageBox.Show(text, "ЕСКД: Ведомость ЛЗК", MessageBoxButtons.YesNo,
                    issues == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                try { Process.Start(_workbookPath); }
                catch (Exception ex) { Log.Error("Открытие ведомости", ex); }
            }
        }

        private void Cleanup()
        {
            if (_events != null)
            {
                try { _events.OnIdleNotify -= OnIdle; }
                catch (COMException ex) { Log.Error("Ведомость ЛЗК: отписка от OnIdleNotify", ex); }
                _events = null;
            }
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
            }
            if (_process != null) _process.Dispose();
            _process = null;
            if (object.ReferenceEquals(_running, this)) _running = null;
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
