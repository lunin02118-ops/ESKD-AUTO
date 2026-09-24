using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using Environment = System.Environment;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Кнопка К-2 «Экспорт для производства» (ТЗ-02 Т-25…Т-31): PDF чертежей, DXF развёрток и IGS профиля
    /// в папки изделия, отчёт `_Экспорт.txt` с контрольными суммами и версией изделия. Изделие сначала сверяется с
    /// проверкой (изменено после неё — вопрос «Проверить сейчас / Продолжить как есть», З-27). Модели не меняются (Т-6):
    /// переключённое исполнение и временная СК возвращаются, и сохранённая до выгрузки деталь сохраняется снова — только
    /// если возврат удался; иначе замечание в отчёте, и деталь остаётся несохранённой.
    /// </summary>
    public static class ExportService
    {
        /// <summary>«ok|файлов|пропущено|путь отчёта» или «error|текст» — для автотестов (Т-9).</summary>
        public static string LastOutcome = "";

        /// <summary>
        /// Биты параметра SheetMetalOptions у PartDoc.ExportToDWG2 (справка SolidWorks API):
        /// 1 — геометрия развёртки, 2 — скрытые рёбра, 4 — линии сгиба, 8 — эскизы, 16 — объединять грани,
        /// 32 — библиотечные элементы, 64 — формообразующие. По Т-28 нужны геометрия и линии сгиба.
        /// </summary>
        private const int FlatPatternGeometry = 1;
        private const int BendLines = 4;

        private sealed class Item
        {
            public string Path = "";
            public ModelDoc2 Model;
            /// <summary>Своё окно было до выгрузки. Нет — окно открыла сама выгрузка (ActivateDoc3), в конце оно закрывается.</summary>
            public bool HadWindow = true;
            public bool IsAssembly;
            public bool IsPurchased;
            public string Designation = "";
            public string Name = "";
            public int Revision;
            /// <summary>Свойство «Операции» (галочки ведомости ЛЗК); пусто — ведомость ещё не строилась.</summary>
            public string Operations = "";
            /// <summary>Основы имён файлов выдачи документа — у каждого исполнения своя (обозначение с «-01»).</summary>
            public readonly List<string> Stems = new List<string>();
            /// <summary>Документ уже у цеха, и выгрузка его пропустила (Т-30).</summary>
            public bool Protected;

            public void AddStem(string stem)
            {
                if (!string.IsNullOrEmpty(stem) && !Stems.Contains(stem, StringComparer.OrdinalIgnoreCase)) Stems.Add(stem);
            }

            /// <summary>
            /// Исполнения детали в изделии и сколько штук каждого (конфигурация → количество), в порядке появления.
            /// Пусто — деталь выгружается сама по себе: одна активная конфигурация, количество не известно.
            /// </summary>
            public readonly List<KeyValuePair<string, int>> Executions = new List<KeyValuePair<string, int>>();

            public void Count(string configuration)
            {
                for (int i = 0; i < Executions.Count; i++)
                {
                    if (!string.Equals(Executions[i].Key, configuration, StringComparison.Ordinal)) continue;
                    Executions[i] = new KeyValuePair<string, int>(configuration, Executions[i].Value + 1);
                    return;
                }
                Executions.Add(new KeyValuePair<string, int>(configuration, 1));
            }
        }

        public static bool Run(ISldWorks app, bool interactive)
        {
            LastOutcome = "";
            ModelDoc2 doc = null;
            ToolSaves saves = null;
            string productFolder = "";
            try
            {
                doc = app.ActiveDoc as ModelDoc2;
                int type = doc == null ? 0 : doc.GetType();
                if (doc == null || (type != (int)swDocumentTypes_e.swDocASSEMBLY && type != (int)swDocumentTypes_e.swDocPART))
                {
                    Fail(app, interactive, "Выгрузка запускается на сборке изделия или на его детали.");
                    return false;
                }
                string path = doc.GetPathName() ?? "";
                if (path.Length == 0)
                {
                    Fail(app, interactive, "Документ ещё не сохранён: сохраните его в папке изделия и повторите.");
                    return false;
                }
                ProductLocation location = ProductLocator.Locate(path);
                productFolder = location.ProductFolder.Length > 0
                    ? location.ProductFolder : LzkNaming.ProductFolder(path);
                bool assembly = type == (int)swDocumentTypes_e.swDocASSEMBLY;
                // Подменённый одноимённый компонент другого заказа — до любых файлов: в цех ушла бы чужая деталь (№16).
                List<string> swapped = assembly ? ProductNamesakes.Swapped(doc, productFolder) : new List<string>();
                if (swapped.Count > 0)
                {
                    Fail(app, interactive, OrderArchive.SwappedText(swapped, 20));
                    return false;
                }
                // Деталь с несохранёнными правками выгружается из открытого, а не из файла: такую выгрузку версия изделия
                // не покрывает. Флаг — до переключений исполнений.
                bool partEdited = !assembly && DocumentGuard.HasUserEdits(doc);
                // Изделие выгружается по проверенному и с тех пор не менявшемуся состоянию (З-27); деталь — сама по себе,
                // версия изделия в отчёте остаётся прежней.
                ProductFreshness freshness = null;
                if (assembly)
                {
                    bool cancelled;
                    freshness = ProductFreshness.Ensure(app, doc, "Выгрузка для производства",
                        "файлы выгрузятся по моделям как есть, в отчёте выгрузки будет «" + ProductStamp.Unchecked + "»: «Готово к " +
                        "производству» такую выгрузку не примет, пока изделие не проверят и не выгрузят заново.", interactive, out cancelled);
                    if (cancelled)
                    {
                        LastOutcome = "error|отменено";
                        Status(app, "");
                        return false;
                    }
                }
                saves = new ToolSaves();

                Status(app, "ЕСКД: выгрузка для производства — состав…");
                // Отчёт заводится до состава: нечитаемый и незагруженный компонент состав называет в «Пропущено» (№20, №29).
                ExportLog log = new ExportLog
                {
                    Product = LzkNaming.Cipher(productFolder, path),
                    User = Settings.AuthorOrUser(),
                    Time = DateTime.Now,
                    Version = freshness != null ? freshness.Version : null
                };
                if (freshness != null && !freshness.Fresh)
                    log.Warn(Path.GetFileName(path), "выгрузка по непроверенному изделию — " + string.Join("; ", freshness.Reasons.ToArray()) +
                        ": нажмите «Проверить изделие» и выгрузите изделие заново");
                List<Item> items = Collect(app, doc, path, productFolder, log);
                foreach (Item item in items) CollectStems(item);
                HashSet<string> issued = ExportNaming.Issued(productFolder);
                // Сборки, чистые до выгрузки: после неё они сохраняются снова, если выгрузка их изменила (ResaveAssemblies).
                HashSet<Item> clean = new HashSet<Item>(items.Where(i => i.IsAssembly && !DocumentGuard.HasUserEdits(i.Model)));
                try
                {
                    foreach (Item item in items)
                    {
                        Status(app, "ЕСКД: выгрузка — " + Path.GetFileName(item.Path));
                        // Один сбойный документ не обрывает выгрузку (сверка SW API 23.09.2026, №29): исключение уходило во
                        // внешний catch — «Выгрузка не выполнена», отчёт не писался, а уже сделанные PDF и DXF лежали без него.
                        try
                        {
                            ExportItem(app, item, productFolder, log, issued, saves);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Выгрузка: " + item.Path, ex);
                            log.Skip(Path.GetFileName(item.Path), "не выгружен: " + ex.Message.Trim());
                        }
                    }
                }
                finally
                {
                    // Экспорт переключал окна: конструктор должен увидеть ту же сборку, с которой начал, — и после сбоя тоже.
                    // Сначала закрываются свои окна, потом активируется изделие: закрытое окно SolidWorks сменяет следующим
                    // по порядку — окном конструктора, а не изделием (e2e X19, прогон r34 24.09.2026).
                    CloseOwnWindows(app, items);
                    Activate(app, path);
                }
                ResaveAssemblies(items, clean, log, saves);
                // Свои сохранения кнопки — в отчёт проверки: изделие для следующей кнопки остаётся проверенным.
                ProductFreshness.Restamp(productFolder, saves.Changes, "выгрузка для производства");
                bool keepVersion = !partEdited && (assembly || SameAsChecked(path));
                if (!assembly && !keepVersion)
                    log.Warn(Path.GetFileName(path), (partEdited ? (DocumentGuard.OlderVersion(app, path)
                            ? "деталь выгружена, а " + SwFileVersion.OlderNote : "деталь выгружена с несохранёнными правками")
                        : "деталь изменена после проверки изделия") + " — выгрузка изделия теперь «" + ProductStamp.Unchecked +
                        "»: сохраните деталь, проверьте изделие и выгрузите его заново");
                // Полная выгрузка изделия наводит порядок в папках выдачи (З-48): только с главной сборки изделия заказа —
                // выгрузка подсборки или сборки вне структуры заказа не знает всех документов, чьи файлы там лежат.
                if (assembly && IsProductAssembly(location, path)) Sweep(productFolder, log, items, issued);
                string reportPath = Write(productFolder, log, !assembly, items, keepVersion);
                LastOutcome = string.Join("|", new[]
                {
                    "ok", log.Files.Count.ToString(CultureInfo.InvariantCulture),
                    log.Skipped.Count.ToString(CultureInfo.InvariantCulture), reportPath
                });
                Notices.Remember(Notices.FromExport(log.Skipped, log.Warnings));
                if (interactive) Show(app, log, productFolder);
                Status(app, "");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка для производства", ex);
                Fail(app, interactive, "Выгрузка не выполнена: " + ex.Message);
                return false;
            }
            finally
            {
                // Сорвалась выгрузка после своих сохранений — их суммы всё равно в отчёт проверки (повторная запись пустая).
                if (saves != null) ProductFreshness.Restamp(productFolder, saves.Changes, "выгрузка для производства");
            }
        }

        /// <summary>Один документ выгрузки: PDF чертежа, у детали — ещё DXF развёрток и IGS профиля.</summary>
        private static void ExportItem(ISldWorks app, Item item, string productFolder, ExportLog log, HashSet<string> issued,
            ToolSaves saves)
        {
            // Чертёж открывается один раз — и для ревизии, и для PDF (аудит 19.09, Л-В7): по сети каждое
            // открытие чертежа — секунды, а у изделия их десятки.
            string drawingPath = Path.ChangeExtension(item.Path, ".slddrw");
            bool opened = false;
            ModelDoc2 drawing = null;
            string problem = "";
            try
            {
                if (File.Exists(drawingPath)) drawing = OpenDrawing(app, drawingPath, out opened, out problem);
                // Ревизия принадлежит чертежу (Р0-8): её поднимает К-7 в чертеже, а модель об этом не знает.
                // Поэтому суффикс «_ИзмN» и право переписать выданное берутся из чертежа, если он есть.
                item.Revision = Math.Max(item.Revision, DrawingRevision(drawing, drawingPath));
                // Выданный документ перезаписывать нельзя: цех работает по тому, что у него на руках (Т-30). Выдана модель на
                // ревизии 0 — или файл документа с его нынешней ревизией: после «Новой ревизии» и повторной выдачи правка без
                // следующей ревизии молча переписала бы у цеха «_Изм1» (З-48).
                if ((item.Revision == 0 && issued.Contains(Path.GetFileName(item.Path))) ||
                    ExportNaming.IssuedAtRevision(issued, item.Stems, item.Revision))
                {
                    item.Protected = true;
                    log.Skip(Path.GetFileName(item.Path), "документ выдан в производство, оформите новую ревизию");
                    return;
                }
                Pdf(app, item, productFolder, log, drawingPath, drawing, problem);
            }
            finally
            {
                if (opened && drawing != null)
                {
                    // Сбой закрытия — в журнал: он не должен подменить собой исключение выгрузки (№29).
                    try
                    {
                        app.CloseDoc(drawing.GetPathName());
                    }
                    catch (COMException ex)
                    {
                        Log.Error("Выгрузка: закрытие чертежа " + drawingPath, ex);
                    }
                }
            }
            if (!item.IsAssembly) PartFiles(app, item, productFolder, log, saves);
        }

        /// <summary>
        /// Сборки изделия, чистые до выгрузки, после неё перестраиваются и сохраняются снова (Т-6 — как детали после
        /// развёртки). Когда выгрузка открывает сборочный чертёж ради PDF, SolidWorks перестраивает сборку, детали которой
        /// перед этим пересохранила ЛЗК, и отмечает её изменённой; подсборка становилась изменённой при перестроении в
        /// проверке. Проверка изделия называла их «несохранённые правки», и «Готово к производству» не проходило (e2e G05,
        /// 24.09.2026). Сохранение — своё, кнопки (ToolSaves): версия изделия остаётся проверенной. Сборка с правками
        /// конструктора не сохраняется (З-25).
        /// </summary>
        private static void ResaveAssemblies(List<Item> items, HashSet<Item> clean, ExportLog log, ToolSaves saves)
        {
            // Сначала подсборки, потом главная сборка: её сохранение изменённые подсборки не пишет.
            for (int i = items.Count - 1; i >= 0; i--)
            {
                Item item = items[i];
                if (!clean.Contains(item) || item.Model == null) continue;
                string file = Path.GetFileName(item.Path);
                try
                {
                    item.Model.EditRebuild3();
                    if (!DocumentGuard.HasUserEdits(item.Model)) continue;
                    int errors;
                    if (!saves.Save(item.Model, out errors))
                        log.Warn(file, "сборка после выгрузки не сохранена (" + SwCodes.SaveProblem(errors) + "): правок в ней нет — " +
                            "сохраните её и проверьте изделие снова");
                }
                catch (COMException ex)
                {
                    Log.Error("Выгрузка: сохранение сборки " + item.Path, ex);
                    log.Warn(file, "сборка после выгрузки не сохранена (" + ex.Message.Trim() + "): сохраните её и проверьте изделие снова");
                }
            }
        }

        /// <summary>Файл детали — ровно тот, что в отчёте последней проверки изделия (с поправкой на сохранения кнопок).</summary>
        private static bool SameAsChecked(string path)
        {
            ProductStamp stamp = ProductStamp.Read(CheckRules.ReportPath(ProductReviewService.ProductFolderOf(path)));
            if (stamp == null || stamp.Version.Length == 0) return false;
            string sum = ProductFreshness.Checksum(path);
            string name = Path.GetFileName(path);
            return sum.Length > 0 && stamp.Checksums.Any(c =>
                string.Equals(c.Key, name, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Value, sum, StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------ состав
        /// <summary>Что выгружать: сама модель и — для сборки — её новые детали и подсборки изделия (Т-26).</summary>
        private static List<Item> Collect(ISldWorks app, ModelDoc2 doc, string path, string productFolder, ExportLog log)
        {
            List<Item> items = new List<Item>();
            string cipher = LzkNaming.Cipher(productFolder, path);
            AddItem(items, doc, path, doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY, cipher);
            if (doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return items;

            AssemblyDoc asm = (AssemblyDoc)doc;
            try
            {
                int resolved = asm.ResolveAllLightWeightComponents(false);
                if (resolved != (int)swComponentResolveStatus_e.swResolveOk)
                    Log.Warn("Выгрузка: облегчённые компоненты разрешены не все (код " + resolved + ")");
            }
            catch (COMException ex)
            {
                Log.Error("Выгрузка: разрешение облегчённых компонентов", ex);
            }
            object[] comps = asm.GetComponents(false) as object[] ?? new object[0];
            Dictionary<string, Item> byPath = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            // Экземпляры без модели в памяти: путь — исполнение (null — исключён из спецификации).
            List<KeyValuePair<string, string>> unloaded = new List<KeyValuePair<string, string>>();
            foreach (object o in comps)
            {
                Component2 comp = o as Component2;
                if (comp == null) continue;
                string componentPath = "";
                // Один нечитаемый компонент не обрывает выгрузку (сверка SW API 23.09.2026, №29): он назван в «Пропущено».
                try
                {
                    componentPath = comp.GetPathName() ?? "";
                    if (componentPath.Length == 0 || rejected.Contains(componentPath)) continue;
                    if (ComponentState.Suppressed(comp)) continue;
                    // Каждый экземпляр считается: развёртке нужно количество на изделие по каждому исполнению (заказ 778).
                    Item known;
                    if (byPath.TryGetValue(componentPath, out known))
                    {
                        if (!comp.ExcludeFromBOM) known.Count(comp.ReferencedConfiguration ?? "");
                        continue;
                    }
                    // Выгружается только своё: эталоны базы и покупные приходят готовыми (Т-26).
                    if (!File.Exists(componentPath) || !LzkNaming.IsInside(componentPath, productFolder))
                    {
                        rejected.Add(componentPath);
                        continue;
                    }
                    ModelDoc2 model = comp.GetModelDoc2() as ModelDoc2;
                    if (model == null)
                    {
                        unloaded.Add(new KeyValuePair<string, string>(componentPath,
                            comp.ExcludeFromBOM ? null : comp.ReferencedConfiguration ?? ""));
                        continue;
                    }
                    Item item = AddItem(items, model, componentPath, model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY, cipher);
                    if (item == null)
                    {
                        rejected.Add(componentPath);
                        continue;
                    }
                    byPath[componentPath] = item;
                    if (!comp.ExcludeFromBOM) item.Count(comp.ReferencedConfiguration ?? "");
                }
                catch (COMException ex)
                {
                    string label = componentPath.Length > 0 ? Path.GetFileName(componentPath) : NameOf(comp);
                    Log.Error("Выгрузка: компонент " + label, ex);
                    log.Skip(label, "компонент не прочитан (" + ex.Message.Trim() + ") — не выгружен");
                }
            }
            // Модели нет в памяти (облегчённая не разрешилась, SpeedPak, скрытая незагруженная): деталь молча выпадала из
            // выгрузки — ни в «Выгружено», ни в «Пропущено», а её экземпляры не входили в количество на развёртке (сверка
            // SW API 23.09.2026, №20). Проверка и ЛЗК в том же случае пишут замечание.
            HashSet<string> named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> lost in unloaded)
            {
                Item known;
                if (byPath.TryGetValue(lost.Key, out known))
                {
                    if (lost.Value != null) known.Count(lost.Value);
                    continue;
                }
                if (rejected.Contains(lost.Key) || !named.Add(lost.Key)) continue;
                log.Skip(Path.GetFileName(lost.Key), "модель не загружена (облегчённая, SpeedPak или скрытая) — PDF, DXF и IGS " +
                    "не сделаны: откройте сборку полностью и выгрузите заново");
            }
            return items;
        }

        private static string NameOf(Component2 comp)
        {
            try
            {
                return comp.Name2 ?? "компонент";
            }
            catch (COMException)
            {
                return "компонент";
            }
        }

        /// <summary>Добавить документ в выгрузку; null — покупное, не выгружается.</summary>
        private static bool HasWindow(ModelDoc2 model)
        {
            try
            {
                return model == null || model.Visible;
            }
            catch (COMException ex)
            {
                Log.Error("Выгрузка: окно документа", ex);
                return true; // не знаем — не закрываем
            }
        }

        /// <summary>
        /// Окна деталей, которые открыла сама выгрузка (развёртку и ось трубы SolidWorks делает только в активном окне),
        /// закрываются: модель остаётся загруженной в сборке, закрывается только окно. Раньше после изделия с десятками
        /// деталей у конструктора оставались десятки окон (сверка SW API 23.09.2026, №15). Окно с несохранёнными
        /// изменениями остаётся — о нём замечание отчёта. CloseDoc не сохраняет и снимает с модели, оставшейся в сборке,
        /// отметку «изменена»: правка молча пропала бы при закрытии сборки (проба 23.09.2026).
        /// </summary>
        private static void CloseOwnWindows(ISldWorks app, IEnumerable<Item> items)
        {
            foreach (Item item in items)
            {
                if (item.HadWindow || item.Model == null) continue;
                try
                {
                    if (!item.Model.Visible || DocumentGuard.HasUserEdits(item.Model)) continue;
                    app.CloseDoc(item.Path);
                }
                catch (COMException ex)
                {
                    Log.Error("Выгрузка: закрытие окна " + item.Path, ex);
                }
            }
        }

        private static Item AddItem(List<Item> items, ModelDoc2 model, string path, bool assembly, string cipher)
        {
            Item item = new Item { Path = path, Model = model, IsAssembly = assembly, HadWindow = HasWindow(model) };
            try
            {
                PropertyWriter w = new PropertyWriter(model, true);
                string cfg = w.ActiveConfigurationName();
                item.Designation = Value(w, cfg, "Обозначение");
                item.Name = Value(w, cfg, "Наименование");
                item.Operations = Value(w, cfg, LzkOperations.PropertyName);
                // Ревизия чертежа — «Revision» чертежа (ниже), детали БЧ — её «Ревизия» (словарь SWPlus): К-7 у детали без
                // чертежа пишет туда. Читалась только «Revision» — после новой ревизии БЧ-деталь пропускалась как «выданная»,
                // а её прежние файлы уже были в «_Аннулировано» (аудит 23.09.2026, NAME-1). «Ревизия» — только у детали БЧ:
                // у детали с чертежом ревизия принадлежит чертежу, и устаревшая или вписанная вручную «Ревизия» модели
                // обходила бы защиту выданного (Т-30; ревью 23.09.2026).
                item.Revision = ExportNaming.Revision(Value(w, cfg, "Revision"));
                string format;
                if (!assembly && BchService.State(model, out format))
                    item.Revision = Math.Max(item.Revision, ExportNaming.Revision(Value(w, cfg, PropertyDictionary.RevisionName)));
                item.IsPurchased = ComponentKind.IsPurchased(w, model, path, "Выгрузка", cipher);
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: реквизиты " + path, ex);
            }
            if (item.IsPurchased) return null;
            items.Add(item);
            return item;
        }

        /// <summary>
        /// Основы имён файлов выдачи документа: активное исполнение и каждое, что стоит в изделии. По ним выгрузка узнаёт
        /// выданную ревизию документа и его прежние файлы в папках выдачи (З-48).
        /// </summary>
        private static void CollectStems(Item item)
        {
            item.AddStem(ExportNaming.Stem(item.Designation, item.Name, item.Path));
            if (item.Model == null || item.Executions.Count == 0) return;
            try
            {
                PropertyWriter w = new PropertyWriter(item.Model, true);
                foreach (KeyValuePair<string, int> execution in item.Executions)
                {
                    if (execution.Key.Length == 0) continue;
                    string designation = Value(w, execution.Key, "Обозначение");
                    string name = Value(w, execution.Key, "Наименование");
                    item.AddStem(ExportNaming.Stem(designation.Length > 0 ? designation : item.Designation,
                        name.Length > 0 ? name : item.Name, item.Path));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: основы имён " + item.Path, ex);
            }
        }

        private static string Value(PropertyWriter w, string cfg, string name)
        {
            try
            {
                string resolved = w.Resolved(cfg, name);
                if ((resolved ?? "").Trim().Length > 0) return resolved.Trim();
                return (w.Resolved("", name) ?? "").Trim();
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: свойство " + name, ex);
                return "";
            }
        }

        /// <summary>
        /// Чертёж для ревизии и PDF: уже открытый — как есть, иначе — только для чтения. Выгрузка чертёж не меняет (PDF —
        /// копия), а открытый на запись файл на время выгрузки заперт — коллега получил бы его «только для чтения».
        /// problem — почему не открылся, словами: одноимённый чертёж другого заказа, открытый в SolidWorks, давал «чертёж
        /// не открылся» без объяснения (сверка SW API 23.09.2026, №28). Открывается без окна, как в «Формате» чертежа:
        /// окно каждого чертежа изделия мелькало и становилось активным (там же, шаг 2; e2e X20).
        /// </summary>
        private static ModelDoc2 OpenDrawing(ISldWorks app, string drawingPath, out bool opened, out string problem)
        {
            opened = false;
            problem = "";
            try
            {
                ModelDoc2 drawing = app.GetOpenDocumentByName(drawingPath) as ModelDoc2;
                if (drawing != null) return drawing;
                int errors = 0, warnings = 0;
                bool visible = app.GetDocumentVisible((int)swDocumentTypes_e.swDocDRAWING);
                app.DocumentVisible(false, (int)swDocumentTypes_e.swDocDRAWING);
                try
                {
                    drawing = app.OpenDoc6(drawingPath, (int)swDocumentTypes_e.swDocDRAWING,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "",
                        ref errors, ref warnings) as ModelDoc2;
                }
                finally
                {
                    app.DocumentVisible(visible, (int)swDocumentTypes_e.swDocDRAWING);
                }
                opened = drawing != null;
                if (drawing == null)
                {
                    problem = SwCodes.OpenProblem(errors);
                    Log.Warn("Выгрузка: чертёж не открылся (код " + errors + ", предупреждения " + warnings + "): " + drawingPath);
                }
                return drawing;
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: открытие чертежа " + drawingPath, ex);
                problem = ex.Message.Trim();
                return null;
            }
        }

        /// <summary>Ревизия чертежа рядом с моделью: 0 — чертежа нет или он ещё черновик.</summary>
        private static int DrawingRevision(ModelDoc2 drawing, string drawingPath)
        {
            if (drawing == null) return 0;
            try
            {
                return ExportNaming.Revision(new PropertyWriter(drawing, true).Resolved("", "Revision"));
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: ревизия чертежа " + drawingPath, ex);
                return 0;
            }
        }

        // ------------------------------------------------------------------ PDF чертежей (Т-27)
        private static void Pdf(ISldWorks app, Item item, string productFolder, ExportLog log, string drawingPath, ModelDoc2 drawing,
            string problem)
        {
            if (!File.Exists(drawingPath))
            {
                log.Skip(Path.GetFileName(item.Path), "нет чертежа: PDF не сделан");
                return;
            }
            string target = ExportNaming.PdfPath(productFolder, item.Designation, item.Name, item.Path, item.Revision);
            if (ExportNaming.TooLong(target).Length > 0)
            {
                log.Skip(Path.GetFileName(drawingPath), "PDF не сделан: " + ExportNaming.TooLong(target));
                return;
            }
            if (drawing == null)
            {
                log.Skip(Path.GetFileName(drawingPath), "чертёж не открылся" + (problem.Length > 0 ? ": " + problem : ""));
                return;
            }

            int[] toggles =
            {
                (int)swUserPreferenceToggle_e.swPDFExportInColor, (int)swUserPreferenceToggle_e.swPDFExportEmbedFonts,
                (int)swUserPreferenceToggle_e.swPDFExportHighQuality, (int)swUserPreferenceToggle_e.swPDFExportPrintHeaderFooter,
                (int)swUserPreferenceToggle_e.swPDFExportUseCurrentPrintLineWeights,
                (int)swUserPreferenceToggle_e.swPDFViewOnSave
            };
            // Цвет, шрифты, качество — как у SaveAsPDF SWPlus (С-4); колонтитулы не печатаются.
            // Просмотрщик после сохранения выключается: выгрузка делает PDF десятками, и SolidWorks открыл бы
            // по окну на каждый. В прогоне 21.09.2026 так набралось 152 окна PDF-XChange на 27 ГБ памяти,
            // после чего SolidWorks сам предупредил о нехватке памяти и упал. Прежнее значение возвращается.
            bool[] wanted = { true, true, true, false, true, false };
            PreferenceSwap<bool> preferences = null;
            // Чертёж без окна SolidWorks 2025 в PDF не пишет — SaveAs отвечает кодом 1 при любых настройках (проба
            // 24.09.2026, X20). Окно показывается только на время записи PDF и скрывается снова; активным после этого
            // SolidWorks сам делает прежний документ — изделие (там же).
            bool shown = false;
            try
            {
                // Папка — внутри try: на месте «02_PDF» может лежать файл или не хватить прав — это пропуск одного PDF,
                // а не обрыв всей выгрузки (сверка SW API 23.09.2026, №29).
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
                preferences = Toggles(app, toggles, wanted, "Выгрузка: восстановление настройки PDF");
                ExportPdfData data = app.GetExportFileData((int)swExportDataFileType_e.swExportPdfData) as ExportPdfData;
                // Листы перечисляются поимённо, как в SaveAsPDF SWPlus: режим «все листы» без списка
                // SolidWorks не принимает — SetSheets возвращает false и PDF не создаётся.
                string[] sheets = (drawing as DrawingDoc).GetSheetNames() as string[];
                if (data == null || sheets == null || sheets.Length == 0 ||
                    !data.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportSpecifiedSheets, sheets))
                {
                    log.Skip(Path.GetFileName(drawingPath), "листы чертежа не переданы в PDF");
                    return;
                }
                int saveErrors = 0, saveWarnings = 0;
                if (!drawing.Visible)
                {
                    drawing.Visible = true;
                    shown = true;
                }
                bool ok = drawing.Extension.SaveAs(target, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, data, ref saveErrors, ref saveWarnings);
                if (ok) log.Add(target);
                else log.Skip(Path.GetFileName(drawingPath), "PDF не сохранён (код " + saveErrors + ")");
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: PDF " + drawingPath, ex);
                log.Skip(Path.GetFileName(drawingPath), "PDF не сделан: " + ex.Message);
            }
            finally
            {
                if (shown)
                {
                    try
                    {
                        drawing.Visible = false;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Выгрузка: скрыть чертёж после PDF " + drawingPath, ex);
                    }
                }
                if (preferences != null) preferences.Restore();
            }
        }

        /// <summary>
        /// Настройки-переключатели SolidWorks на время выгрузки: меняется только отличающееся, возвращается только
        /// изменённое (<see cref="PreferenceSwap{T}"/>; сверка SW API 23.09.2026, №25).
        /// </summary>
        private static PreferenceSwap<bool> Toggles(ISldWorks app, int[] ids, bool[] wanted, string label)
        {
            return new PreferenceSwap<bool>(id => app.GetUserPreferenceToggle(id),
                (id, value) => { app.SetUserPreferenceToggle(id, value); return true; }, ids, wanted, label);
        }

        /// <summary>Числовые настройки SolidWorks на время выгрузки — так же, как <see cref="Toggles"/>.</summary>
        private static PreferenceSwap<int> Numbers(ISldWorks app, int[] ids, int[] wanted, string label)
        {
            return new PreferenceSwap<int>(id => app.GetUserPreferenceIntegerValue(id),
                (id, value) => app.SetUserPreferenceIntegerValue(id, value), ids, wanted, label);
        }

        // ------------------------------------------------------------------ файлы детали: DXF развёрток (Т-28) и IGS (Т-29)
        /// <summary>
        /// Развёртки и файлы трубореза всех исполнений детали, стоящих в изделии (заказ 778, 22.09.2026: у кронштейна 00,
        /// 01, 02, 03 — выгружалась бы только активная конфигурация). И развёртку, и IGS SolidWorks отдаёт по активной
        /// конфигурации, поэтому исполнения по очереди делаются активными — одно переключение на оба файла; прежняя
        /// конфигурация возвращается. IGS выгружался только для активного исполнения — у трубы «-01» файла не было
        /// (сверка с практиками API 23.09.2026, находка 12).
        /// </summary>
        private static void PartFiles(ISldWorks app, Item item, string productFolder, ExportLog log, ToolSaves saves)
        {
            if (!(item.Model is PartDoc)) return;
            if (item.Executions.Count == 0)
            {
                string file = Path.GetFileName(item.Path);
                bool dirtyBefore = DocumentGuard.HasUserEdits(item.Model);
                DxfOne(app, item, item.Designation, item.Name, 0, productFolder, log, file);
                bool clean = IgsOne(app, item, item.Designation, item.Name, item.Operations, productFolder, log, saves, file);
                // Экспорт развёртки SolidWorks отмечает листовую деталь изменённой, хотя в ней ничего не поменялось: сохранённая
                // до выгрузки деталь сохраняется снова, как после переключения исполнений. Иначе новая ревизия оставляла окно
                // «изменённой» модели открытым, а закрытие чертежа спрашивало «Сохранить?» (V09, 24.09.2026).
                string active = ActiveConfiguration(item.Model);
                if (clean && !dirtyBefore && active.Length > 0 && DocumentGuard.HasUserEdits(item.Model))
                    RestoreConfiguration(item, active, false, log, saves);
                return;
            }
            string original = ActiveConfiguration(item.Model);
            // SolidWorks не ответил — «правки есть»: такую деталь выгрузка не сохраняет (сверка SW API 23.09.2026, №29).
            bool wasDirty = DocumentGuard.HasUserEdits(item.Model);
            bool switched = false, leftover = false;
            try
            {
                foreach (KeyValuePair<string, int> execution in item.Executions)
                {
                    string cfg = execution.Key.Length > 0 ? execution.Key : original;
                    if (!string.Equals(cfg, ActiveConfiguration(item.Model), StringComparison.Ordinal))
                    {
                        // Флаг — до переключения: сорвавшееся посередине переключение тоже возвращается (№29).
                        switched = true;
                        bool shown;
                        try
                        {
                            shown = Activate(app, item.Path) && item.Model.ShowConfiguration2(cfg);
                        }
                        catch (COMException ex)
                        {
                            Log.Error("Выгрузка: исполнение " + cfg + " " + item.Path, ex);
                            shown = false;
                        }
                        if (!shown)
                        {
                            log.Skip(Path.GetFileName(item.Path) + " [" + cfg + "]", "исполнение не стало активным: DXF и IGS не сделаны");
                            continue;
                        }
                    }
                    // Обозначение, наименование и операции — той конфигурации, что теперь активна: у исполнения своё «…-02».
                    PropertyWriter w = new PropertyWriter(item.Model, true);
                    string designation = Value(w, cfg, "Обозначение");
                    string name = Value(w, cfg, "Наименование");
                    designation = designation.Length > 0 ? designation : item.Designation;
                    name = name.Length > 0 ? name : item.Name;
                    // Пропуск и замечание исполнения подписываются им самим: «деталь.sldprt [01]» (проверка изделия
                    // сопоставляет их с файлом по имени без расширения).
                    string label = Path.GetFileName(item.Path) + (item.Executions.Count > 1 ? " [" + cfg + "]" : "");
                    item.AddStem(ExportNaming.Stem(designation, name, item.Path));
                    DxfOne(app, item, designation, name, execution.Value, productFolder, log, label);
                    // Временную СК оси трубы IGS убирает, но деталь не сохраняет: её сохраняет возврат прежнего исполнения —
                    // иначе в файл ушла бы чужая активная конфигурация. СК не убралась — деталь не сохраняется совсем.
                    if (!IgsOne(app, item, designation, name, Value(w, cfg, LzkOperations.PropertyName), productFolder, log, null, label))
                        leftover = true;
                }
            }
            finally
            {
                if (switched || (!wasDirty && DocumentGuard.HasUserEdits(item.Model)))
                    RestoreConfiguration(item, original, wasDirty || leftover, log, saves);
            }
        }

        /// <summary>
        /// Вернуть конфигурацию, с которой деталь пришла. Переключение исполнений — не правка: сохранённая до выгрузки деталь
        /// сохраняется снова, но только если исполнение действительно вернулось — иначе в файл ушла бы чужая активная
        /// конфигурация. Не вернулось или не сохранилось — замечание в отчёте выгрузки; деталь с правками конструктора не
        /// сохраняется никогда (З-25).
        /// </summary>
        private static void RestoreConfiguration(Item item, string original, bool wasDirty, ExportLog log, ToolSaves saves)
        {
            string file = Path.GetFileName(item.Path);
            try
            {
                if (original.Length > 0 && !string.Equals(original, ActiveConfiguration(item.Model), StringComparison.Ordinal))
                    item.Model.ShowConfiguration2(original);
                if (original.Length == 0 || !string.Equals(original, ActiveConfiguration(item.Model), StringComparison.Ordinal))
                {
                    log.Warn(file, "прежнее исполнение" + (original.Length > 0 ? " «" + original + "»" : "") +
                        " не вернулось активным — деталь не сохранена: верните его и сохраните деталь сами");
                    return;
                }
                if (wasDirty || !item.Model.GetSaveFlag()) return;
                int errors;
                // Исполнение возвращено — содержимое детали совпадает с файлом: совет «закройте без сохранения» верен при
                // любой причине отказа; «ответьте «Да»» на детали только для чтения был неверен (сверка SW API 23.09.2026, №17).
                if (!saves.Save(item.Model, out errors))
                    log.Warn(file, "деталь не сохранена (" + SwCodes.SaveProblem(errors) + ") — исполнение возвращено, в детали " +
                        "ничего не изменилось: закройте её без сохранения (на вопрос SolidWorks ответьте «Нет»)");
            }
            catch (COMException ex)
            {
                Log.Error("Выгрузка: возврат конфигурации " + item.Path, ex);
                log.Warn(file, "прежнее исполнение не возвращено (" + ex.Message.Trim() + ") — деталь не сохранена: проверьте её");
            }
        }

        private static string ActiveConfiguration(ModelDoc2 model)
        {
            try
            {
                Configuration c = model.GetActiveConfiguration() as Configuration;
                return c != null ? c.Name ?? "" : "";
            }
            catch (COMException ex)
            {
                Log.Error("Выгрузка: активная конфигурация", ex);
                return "";
            }
        }

        private static void DxfOne(ISldWorks app, Item item, string designation, string name, int quantity,
            string productFolder, ExportLog log, string label)
        {
            PartDoc part = (PartDoc)item.Model;
            double thickness = StockService.SheetThicknessMm(item.Model);
            // Многотельная листовая деталь — DXF на каждое листовое тело (сверка SW API 23.09.2026, №42; решение владельца
            // 24.09.2026). Раньше ExportToDWG2 без выделения развёртки отдавал одну развёртку или все тела одним файлом, а
            // толщина в имени была первого тела. Справка ExportToDWG2: развёртку тела многотельной детали выделяют до вызова.
            List<SheetBody> sheets = double.IsNaN(thickness) ? new List<SheetBody>() : SheetBodies(part);
            bool several = sheets.Count > 1;
            if (double.IsNaN(thickness))
            {
                // Не листовая деталь — развёртки и не должно быть. Но если материал — лист, конструктор ждёт DXF:
                // говорим, почему его нет, а не молчим (22.09.2026, заказ 778).
                if (LzkMaterials.Cutting(MaterialName(item.Model)) == LzkOperations.SheetCutting)
                    log.Skip(label, "материал — лист, но деталь построена не листовым металлом: DXF развёртки не сделан");
                return;
            }

            string folder = ExportNaming.LaserDirectory(productFolder);
            // Таблица соответствия слоёв превратила бы тихий экспорт в диалог: она здесь не нужна. Настройки
            // пользователя возвращаются после экспорта (аудит 19.09, Л-В8) — его ручной DXF не должен меняться.
            int[] toggles = { (int)swUserPreferenceToggle_e.swDxfMapping, (int)swUserPreferenceToggle_e.swDXFDontShowMap };
            PreferenceSwap<bool> map = Toggles(app, toggles, new[] { false, true }, "Выгрузка: восстановление настройки DXF");
            // Версия R2000 и масштаб 1:1 — по Т-28, а не как настроено у конструктора: у каждого своя версия, а с чужим
            // масштабом вывода и деталь в файле, и рамка в имени не той величины (сверка 23.09.2026, находка 22).
            int[] numbers = { (int)swUserPreferenceIntegerValue_e.swDxfVersion, (int)swUserPreferenceIntegerValue_e.swDxfOutputNoScale };
            PreferenceSwap<int> format = Numbers(app, numbers, new[] { (int)swDxfFormat_e.swDxfFormat_R2000, 1 },
                "Выгрузка: восстановление настройки DXF");
            // Размеры рамки видны только в готовой развёртке, поэтому экспорт идёт во временный файл,
            // а окончательное имя «…_S<толщина>мм_<ширина>х<длина>.dxf» получается после замера.
            string temporary = Path.Combine(folder, "_замер_" + Guid.NewGuid().ToString("N") + ".dxf");
            try
            {
                // Папка — внутри try: на месте «Лазер_Лист» может лежать файл или не хватить прав — это пропуск одной
                // развёртки, а не обрыв всей выгрузки (сверка SW API 23.09.2026, №29).
                Directory.CreateDirectory(folder);
                // Развёртку SolidWorks отдаёт только активному документу: компонент сборки, открытый в фоне,
                // получает отказ без объяснения (боевой заказ NC3-7R). Активная сборка возвращается в конце.
                if (!Activate(app, item.Path))
                {
                    log.Skip(label, "SolidWorks не сделал деталь активной: DXF не сделан");
                    return;
                }
                if (several)
                    log.Warn(label, "многотельная листовая деталь: листовых тел " + sheets.Count + " — DXF на каждое тело " +
                        "(«" + ExportNaming.BodyMark + "1», «" + ExportNaming.BodyMark + "2»…)");
                for (int i = 0; i < (several ? sheets.Count : 1); i++)
                {
                    string bodyLabel = several ? label + " [тело " + (i + 1) + "]" : label;
                    double bodyThickness = several && !double.IsNaN(sheets[i].ThicknessMm) ? sheets[i].ThicknessMm : thickness;
                    item.Model.ClearSelection2(true);
                    if (several && (sheets[i].Flat == null || !sheets[i].Flat.Select2(false, -1)))
                    {
                        log.Skip(bodyLabel, "развёртка тела не выделилась: DXF не сделан");
                        continue;
                    }
                    DxfBody(part, item, designation, name, quantity, productFolder, log, bodyLabel, folder,
                        several ? i + 1 : 0, bodyThickness, i == 0 ? temporary : Path.Combine(folder,
                            "_замер_" + Guid.NewGuid().ToString("N") + ".dxf"));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: DXF " + item.Path, ex);
                log.Skip(label, "DXF не сделан: " + ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (IOException ex)
                {
                    Log.Error("Выгрузка: временный DXF " + temporary, ex);
                }
                if (several)
                {
                    try
                    {
                        item.Model.ClearSelection2(true);
                    }
                    catch (COMException ex)
                    {
                        Log.Error("Выгрузка: снять выделение развёртки " + item.Path, ex);
                    }
                }
                format.Restore();
                map.Restore();
            }
        }

        /// <summary>Листовое тело детали: его «Развёртка» и толщина из его «Листового металла» (№42).</summary>
        private sealed class SheetBody
        {
            public Feature Flat;
            public double ThicknessMm = double.NaN;
        }

        /// <summary>Листовые тела детали — у многотельной у каждого своя «Развёртка» и свой «Листовой металл».</summary>
        private static List<SheetBody> SheetBodies(PartDoc part)
        {
            try
            {
                // Фоновая перестройка дерева после открытия окна — обход повторяется целиком (FeatureWalk).
                return FeatureWalk.Retry(() =>
                {
                    List<SheetBody> list = new List<SheetBody>();
                    object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                    if (bodies == null) return list;
                    foreach (object o in bodies)
                    {
                        Body2 body = o as Body2;
                        if (body == null || !body.IsSheetMetal()) continue;
                        SheetBody sheet = new SheetBody { ThicknessMm = StockService.BodySheetThicknessMm(body, (ModelDoc2)part) };
                        object[] features = body.GetFeatures() as object[];
                        if (features != null)
                            foreach (object fo in features)
                            {
                                Feature f = fo as Feature;
                                if (f != null && f.GetTypeName2() == "FlatPattern" && sheet.Flat == null) sheet.Flat = f;
                            }
                        list.Add(sheet);
                    }
                    return list;
                }, "листовые тела");
            }
            catch (COMException ex)
            {
                Log.Error("Выгрузка: листовые тела", ex);
            }
            return new List<SheetBody>();
        }

        /// <summary>Тела детали в показанном исполнении: все, из них поверхностей и скрытых.</summary>
        private sealed class BodyCount
        {
            public int All;
            public int Surfaces;
            public int Hidden;
        }

        /// <summary>
        /// Тела детали в показанном исполнении (З-51) — твёрдые и поверхности, скрытые тоже: SaveAs IGS пишет модель
        /// целиком, и лишнее тело попало бы труборезу. GetBodies2 отдаёт тела активной конфигурации — PartFiles уже
        /// показал нужное исполнение. Null — тела не прочитаны: такую деталь нельзя ни выгрузить, ни назвать многотельной.
        /// </summary>
        private static BodyCount Bodies(PartDoc part)
        {
            if (part == null) return null;
            try
            {
                // Сразу после смены окна или исполнения SolidWorks перестраивает дерево в фоне (FeatureWalk).
                return FeatureWalk.Retry(() =>
                {
                    int solids = BodiesOf(part, swBodyType_e.swSolidBody, false);
                    int surfaces = BodiesOf(part, swBodyType_e.swSheetBody, false);
                    int shown = BodiesOf(part, swBodyType_e.swSolidBody, true) + BodiesOf(part, swBodyType_e.swSheetBody, true);
                    return new BodyCount { All = solids + surfaces, Surfaces = surfaces, Hidden = solids + surfaces - shown };
                }, "тела детали");
            }
            catch (COMException ex)
            {
                Log.Error("Выгрузка: тела детали", ex);
                return null;
            }
        }

        private static int BodiesOf(PartDoc part, swBodyType_e type, bool visibleOnly)
        {
            object[] bodies = part.GetBodies2((int)type, visibleOnly) as object[];
            return bodies == null ? 0 : bodies.Count(b => b is Body2);
        }

        /// <summary>
        /// Развёртка одного листового тела (body ≥ 1) или всей однотельной детали (body = 0): экспорт во временный файл,
        /// замер рамки, имя «…[_телоN]_S&lt;толщина&gt;мм[_Nшт]_&lt;ширина&gt;х&lt;длина&gt;.dxf», прежние развёртки — в архив.
        /// </summary>
        private static void DxfBody(PartDoc part, Item item, string designation, string name, int quantity, string productFolder,
            ExportLog log, string label, string folder, int body, double thickness, string temporary)
        {
            try
            {
                // Выравнивание — 12 чисел, список видов — массив строк: null в этих параметрах
                // SolidWorks молча отвергает, и развёртка не выгружается.
                double[] alignment = new double[12];
                string[] views = { "" };
                bool ok = part.ExportToDWG2(temporary, item.Path, (int)swExportToDWG_e.swExportToDWG_ExportSheetMetal,
                    true, alignment, false, false, FlatPatternGeometry | BendLines, views);
                if (!ok || !File.Exists(temporary))
                {
                    Log.Warn("Выгрузка: ExportToDWG2 отказал для " + item.Path + " (ok=" + ok +
                        ", файл=" + File.Exists(temporary) + ", цель=" + temporary + ")");
                    log.Skip(label, "нет развёртки: DXF не сделан");
                    return;
                }
                double width, length;
                if (!DxfFrame.Measure(temporary, out width, out length))
                {
                    log.Skip(label, "развёртка пустая: DXF не сделан");
                    return;
                }
                string target = ExportNaming.DxfPath(productFolder, designation, name, item.Path, body,
                    thickness, quantity, width, length, item.Revision);
                if (ExportNaming.TooLong(target).Length > 0)
                {
                    log.Skip(label, "DXF не сделан: " + ExportNaming.TooLong(target));
                    return;
                }
                // Два исполнения без своих обозначений дали бы одно имя — второе молча затёрло бы первое.
                if (log.Files.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    log.Skip(label, "у исполнения нет своего обозначения — его развёртка совпала бы по имени с " +
                        Path.GetFileName(target) + ": DXF не сделан");
                    return;
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(temporary, target);
                log.Add(target);
                RetireStaleDxf(folder, ExportNaming.Stem(designation, name, item.Path), item.Revision, target, log, label);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (IOException ex)
                {
                    Log.Error("Выгрузка: временный DXF " + temporary, ex);
                }
            }
        }

        /// <summary>
        /// Прежние развёртки этого документа той же ревизии под другим именем — в «_Аннулировано», с замечанием в отчёте:
        /// в имени изменились рамка, количество или толщина, и цех получил бы две развёртки одной детали (сверка SW API
        /// 23.09.2026: рамка теперь округляется вверх, и имена прежних выгрузок меняются). Выгруженное в этот раз не
        /// трогается; выданное в производство сюда не попадает — его выгрузка пропускается раньше.
        /// </summary>
        private static void RetireStaleDxf(string folder, string stem, int revision, string target, ExportLog log, string label)
        {
            string file = "";
            try
            {
                foreach (string path in Directory.GetFiles(folder, "*.dxf"))
                {
                    file = path;
                    if (!ExportNaming.IsStaleDxf(Path.GetFileName(path), stem, revision, Path.GetFileName(target)) ||
                        log.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                    string archive = ExportNaming.ArchivePath(path, DateTime.Now);
                    Directory.CreateDirectory(Path.GetDirectoryName(archive));
                    File.Move(path, archive);
                    log.Warn(label, "прежняя развёртка «" + Path.GetFileName(path) + "» убрана в " + ExportNaming.ArchiveFolder +
                        ": в имени другая рамка, количество или толщина");
                }
            }
            catch (IOException ex)
            {
                Log.Error("Выгрузка: прежняя развёртка " + file, ex);
                log.Warn(label, "прежняя развёртка «" + Path.GetFileName(file) + "» не убрана: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Выгрузка: прежняя развёртка " + file, ex);
                log.Warn(label, "прежняя развёртка «" + Path.GetFileName(file) + "» не убрана: " + ex.Message);
            }
        }


        /// <summary>
        /// Сделать документ активным — развёртку (ExportToDWG2), IGS, построение СК оси трубы и переключение исполнения
        /// SolidWorks делает только с активным документом. IGS из фоновой модели SolidWorks пишет по активному документу:
        /// в «&lt;деталь&gt;.igs» уходила вся сборка или соседняя деталь, а заголовок называл деталь (замечание владельца
        /// 24.09.2026; находку 21 сверки SW API 23.09.2026 X06 не опроверг — он смотрел только размер файла). false — не
        /// стал активным: занятый SolidWorks отвечает отказом, и выгрузка пошла бы по чужому документу, но с отметкой
        /// «ok» (аудит 20.09.2026). Проверяется не код возврата, а сам активный документ: он и есть признак успеха.
        /// </summary>
        private static bool Activate(ISldWorks app, string path)
        {
            try
            {
                int errors = 0;
                app.ActivateDoc3(path, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                string now = ActivePath(app);
                if (string.Equals(now, path, StringComparison.OrdinalIgnoreCase)) return true;
                Log.Warn("Выгрузка: документ не стал активным (код " + errors + "), активен «" + now + "» вместо «" + path + "»");
                return false;
            }
            catch (COMException ex)
            {
                Log.Error("Выгрузка: активация " + path, ex);
                return false;
            }
        }

        /// <summary>Путь активного документа SolidWorks; пусто — активного нет или он не сохранён.</summary>
        private static string ActivePath(ISldWorks app)
        {
            ModelDoc2 active = app.ActiveDoc as ModelDoc2;
            return active != null ? active.GetPathName() ?? "" : "";
        }

        // ------------------------------------------------------------------ IGS профиля (Т-29)
        /// <summary>
        /// IGS активного исполнения детали. <paramref name="saves"/> = null — временную СК оси трубы убрать, но деталь не
        /// сохранять: её сохранит возврат прежнего исполнения.
        /// </summary>
        private static bool IgsOne(ISldWorks app, Item item, string designation, string name, string operations,
            string productFolder, ExportLog log, ToolSaves saves, string label)
        {
            // Решает галочка «Лазерная резка трубы» в операциях; без операций — признак профиля в модели. Материал —
            // активного исполнения: у исполнений он может быть разный (решение владельца 23.09.2026).
            bool tube = IsStructuralMember(item.Model) || IsTubeByMaterial(item.Model);
            bool cutting = LzkOperations.WantsTubeFile(operations, tube);
            if (!cutting && !tube) return true;
            string target = ExportNaming.IgsPath(productFolder, designation, name, item.Path, item.Revision);
            // Многотельная деталь в IGS не идёт (решение владельца 24.09.2026, З-51): в файл ушли бы все тела — сварная
            // рама целиком или труба с лишним телом. Тела — показанного исполнения: у исполнений их может быть разное число.
            // Прежний IGS такой детали цеху отдавать нельзя — он уходит в «_Аннулировано»; выгруженный в этот раз (тем же
            // именем у исполнения без своего обозначения) не трогается.
            BodyCount bodies = Bodies(item.Model as PartDoc);
            if (bodies != null && bodies.All > 1)
            {
                log.Warn(label, ExportLog.MultibodyReason(bodies.All, bodies.Surfaces, bodies.Hidden, cutting));
                if (File.Exists(target) && !log.Files.Contains(target, StringComparer.OrdinalIgnoreCase))
                    Retire(target, DateTime.Now, "IGS многотельной детали — на труборез она не идёт", log);
                return true;
            }
            if (!cutting)
            {
                // Галочку снял конструктор — или «Операции» записала ЛЗК до 24.09.2026, не узнав трубу по материалу
                // SolidWorks. Молча пропускать нельзя: в «Труборез» не попала бы труба, и никто бы не заметил.
                log.Warn(label, "деталь из трубы, а в «Операциях» нет «" + LzkOperations.TubeCutting +
                    "»: IGS не сделан — если он нужен, поставьте эту операцию в ведомости ЛЗК и выгрузите заново");
                return true;
            }
            if (bodies == null || bodies.All == 0)
            {
                // Непрочитанные тела могли оказаться многотельной деталью — наугад IGS не делается.
                log.Skip(label, bodies == null ? "тела детали не прочитаны: IGS не сделан — выгрузите изделие заново"
                    : "в исполнении нет тел: IGS не сделан");
                return true;
            }
            if (ExportNaming.TooLong(target).Length > 0)
            {
                log.Skip(label, "IGS не сделан: " + ExportNaming.TooLong(target));
                return true;
            }
            // Два исполнения без своих обозначений дали бы одно имя — второе молча затёрло бы первое.
            if (log.Files.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                log.Skip(label, "у исполнения нет своего обозначения — его IGS совпал бы по имени с " +
                    Path.GetFileName(target) + ": IGS не сделан");
                return true;
            }
            // Труборезу нужны поверхности вместе с кривыми в стандартном наборе IGES (Т-29); прежние
            // настройки конструктора возвращаются на место — кнопка ничего за собой не оставляет.
            int[] prefs =
            {
                (int)swUserPreferenceIntegerValue_e.swIGESRepresentation,
                (int)swUserPreferenceIntegerValue_e.swIGESSystem
            };
            int[] wanted = { (int)swIGESRepresentation_e.swIGES_TRMSRFANDCURVES, (int)swIGESPreferredSystem_e.swIGES_STANDARD };
            bool clean = true;
            PreferenceSwap<int> iges = null;
            try
            {
                // Папка — внутри try, как у DXF (№29).
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
                // SolidWorks пишет в IGS активный документ, а не модель, у которой вызван SaveAs: труба без элемента
                // конструкции получала IGS всей сборки или соседней пластины (замечание владельца 24.09.2026).
                if (!Activate(app, item.Path))
                {
                    log.Skip(label, "SolidWorks не сделал деталь активной: IGS не сделан");
                    return true;
                }
                iges = Numbers(app, prefs, wanted, "Выгрузка: восстановление настройки IGES");
                int errors = 0, warnings = 0;
                bool ok = false;
                string other = "";
                TubeAxis axis = TubeAxis.Create(app, item.Model, item.Path, saves);
                try
                {
                    other = ActivePath(app);
                    if (string.Equals(other, item.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        other = "";
                        ok = item.Model.Extension.SaveAs(target, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                            (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref errors, ref warnings);
                    }
                    if (ok && !axis.Applied)
                        log.Warn(Path.GetFileName(target), "IGS в глобальной системе координат: " + axis.Reason);
                }
                finally
                {
                    axis.Dispose();
                    clean = axis.Leftover.Length == 0;
                }
                if (!clean) log.Warn(label, axis.Leftover);
                // Записанный файл проверяется сам: заголовок IGS называет деталь, даже когда внутри сборка.
                string foreign = ok ? IgesContent.Foreign(File.ReadLines(target, IgesContent.Cyrillic)) : "";
                if (other.Length > 0)
                    log.Skip(label, "активным стал другой документ («" + Path.GetFileName(other) + "»): IGS не сделан");
                else if (!ok)
                    log.Skip(label, "IGS не сохранён (код " + errors + ")");
                else if (foreign.Length > 0)
                {
                    File.Delete(target);
                    Log.Warn("Выгрузка: " + target + " — " + foreign + "; файл удалён");
                    log.Skip(label, "в IGS попала не эта деталь (" + foreign + "): файл удалён, IGS не сделан");
                }
                else
                {
                    log.Add(target);
                    Log.Info("Выгрузка: IGS «" + Path.GetFileName(target) + "» — из «" + Path.GetFileName(item.Path) + "»");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: IGS " + item.Path, ex);
                log.Skip(label, "IGS не сделан: " + ex.Message);
            }
            finally
            {
                if (iges != null) iges.Restore();
            }
            return clean;
        }

        /// <summary>
        /// Деталь из трубы или профиля по материалу — то же правило, по которому ЛЗК ставит ей «Лазерная резка трубы».
        /// Нужно для тел, выделенных из многотельной детали командой «Разделить»: у них нет ни элемента сварной
        /// конструкции, ни списка вырезов, и выгрузка молча пропускала IGS (22.09.2026, заказ 778).
        /// </summary>
        private static bool IsTubeByMaterial(ModelDoc2 model)
        {
            return LzkMaterials.Cutting(MaterialName(model)) == LzkOperations.TubeCutting;
        }

        /// <summary>Имя материала детали в активной конфигурации: «Труба ПО 40х20х1,5 ГОСТ 8644-68 / 08пс …».</summary>
        private static string MaterialName(ModelDoc2 model)
        {
            try
            {
                string id = model.MaterialIdName ?? "";
                string[] parts = id.Split('|');
                return parts.Length >= 2 ? parts[1] : "";
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: материал детали", ex);
                return "";
            }
        }

        /// <summary>
        /// Деталь из профиля (Т-29): элемент сварной конструкции в дереве или длина заготовки `LENGTH`
        /// в списке вырезов — вторым признаком опознаются рамы, нарезанные из трубы без WeldMemberFeat.
        /// </summary>
        private static bool IsStructuralMember(ModelDoc2 model)
        {
            try
            {
                // Погашенный в активном исполнении элемент — чужого исполнения: выгрузка идёт по исполнениям, и
                // исполнение-пластина получала бы IGS (критик сверки SW API 23.09.2026). Так же — папка списка вырезов без
                // тел: её тела погашены в этом исполнении (CutListFolders). Обход переживает фоновую перестройку дерева
                // после открытия окна (FeatureWalk): сбой в нём был «не труба».
                bool member = FeatureWalk.Retry(() =>
                {
                    for (Feature f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                        if ((f.GetTypeName2() ?? "") == "WeldMemberFeat" && !f.IsSuppressed()) return true;
                    return false;
                }, "признак профиля");
                if (member) return true;
                foreach (Feature sub in CutListFolders.Active(model))
                {
                    CustomPropertyManager m = sub.CustomPropertyManager;
                    if (m == null) continue;
                    // Имя свойства зависит от языка SolidWorks («ДЛИНА»), поэтому ищется по английской ссылке.
                    string length = CutListProperties.Find(LzkService.Written(m), "LENGTH", CutListProperties.LengthSpellings);
                    if (LzkService.Value(m, length) > 0) return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: признак профиля", ex);
            }
            return false;
        }

        // ------------------------------------------------------------------ отчёт
        private static string Write(string productFolder, ExportLog log, bool partial, IEnumerable<Item> items, bool keepVersion)
        {
            // Сумма уже есть у файла, который полная выгрузка оставила из прежнего отчёта (Sweep): она — та, что знает цех.
            foreach (string file in log.Files)
                if (!log.Checksums.ContainsKey(file)) log.Checksums[file] = Checksum(file);
            string path = ExportNaming.ReportPath(productFolder);
            // Выгрузка одной детали (и «Новая ревизия») дописывает отчёт изделия, а не заменяет его: иначе проверка
            // изделия сочла бы все остальные детали невыгруженными. Прежние файлы остаются, если лежат на месте
            // и не переписаны сейчас; прежние пропуски — если документ сейчас не выгружался.
            if (partial && File.Exists(path)) Merge(productFolder, log, ExportLog.Parse(File.ReadAllText(path, Encoding.UTF8)), items, keepVersion);
            else if (partial) log.Version = "";
            Directory.CreateDirectory(productFolder);
            File.WriteAllText(path, log.Text(), new UTF8Encoding(true));
            return path;
        }

        private static void Merge(string productFolder, ExportLog log, ExportLog previous, IEnumerable<Item> items, bool keepVersion)
        {
            // Деталь выгружается сама по себе: версия изделия — та, по которой выгружено изделие, если деталь ровно как при
            // проверке. Выгружена с несохранёнными правками или изменена после проверки — «не проверено»: правки могут и
            // не сохранить, и тогда файлы цеха не совпали бы ни с одной проверенной версией.
            log.Version = keepVersion ? previous.Version : "";
            HashSet<string> now = new HashSet<string>(log.Files.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            HashSet<string> documents = new HashSet<string>(
                items.Select(i => Path.GetFileNameWithoutExtension(i.Path)), StringComparer.OrdinalIgnoreCase);
            string[] folders =
            {
                ExportNaming.PdfDirectory(productFolder), ExportNaming.LaserDirectory(productFolder),
                ExportNaming.TubeDirectory(productFolder)
            };
            foreach (string name in previous.Files)
            {
                if (now.Contains(name)) continue;
                string found = folders.Select(f => Path.Combine(f, name)).FirstOrDefault(File.Exists);
                if (found == null) continue;
                log.Files.Add(found);
                string sum;
                if (previous.Checksums.TryGetValue(name, out sum)) log.Checksums[found] = sum;
            }
            foreach (string line in previous.Skipped)
                if (!documents.Contains(ExportLog.DocumentName(ExportLog.SplitSkip(line).Key))) log.Skipped.Add(line);
            // Замечание «убран в _Аннулировано» — о прошлой выгрузке, а не о документе: дальше оно не переходит.
            foreach (string line in previous.Warnings)
            {
                KeyValuePair<string, string> note = ExportLog.SplitSkip(line);
                if (!documents.Contains(ExportLog.DocumentName(note.Key)) && !ExportLog.IsArchiveNote(note.Value)) log.Warnings.Add(line);
            }
        }

        /// <summary>
        /// Главная сборка изделия заказа (<see cref="ProductLocator.MainAssembly"/>): только её выгрузка знает все документы,
        /// чьи файлы лежат в папках выдачи. Сборка вне «01_3D» или подсборка — нет.
        /// </summary>
        private static bool IsProductAssembly(ProductLocation location, string path)
        {
            try
            {
                if (location == null || location.ModelsFolder.Length == 0 || !Directory.Exists(location.ModelsFolder)) return false;
                string main = ProductLocator.MainAssembly(location.ProductFolder,
                    Directory.GetFiles(location.ModelsFolder, "*.sldasm", SearchOption.AllDirectories));
                return main != null && string.Equals(Path.GetFullPath(main), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: главная сборка изделия " + path, ex);
                return false;
            }
        }

        /// <summary>
        /// Полная выгрузка изделия (З-48): файлы папок выдачи, которых в этот раз нет в выгрузке, уходят в «_Аннулировано»
        /// или остаются и переходят в новый отчёт — выданные цеху и файлы документов, чья выгрузка сорвалась. Решение —
        /// <see cref="ExportLeftovers"/>; каждый убранный файл — замечанием в отчёте выгрузки. Раньше в папках оставались
        /// файлы убранных и переименованных деталей, и «Готово к производству» отдавало их цеху вместе с изделием.
        /// </summary>
        private static void Sweep(string productFolder, ExportLog log, List<Item> items, HashSet<string> issued)
        {
            ExportLeftovers leftovers = new ExportLeftovers();
            ExportLog previous = null;
            string report = ExportNaming.ReportPath(productFolder);
            try
            {
                if (File.Exists(report)) previous = ExportLog.Parse(File.ReadAllText(report, Encoding.UTF8));
            }
            catch (IOException ex)
            {
                Log.Error("Выгрузка: прежний отчёт " + report, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Выгрузка: прежний отчёт " + report, ex);
            }
            if (previous != null) leftovers.Previous.UnionWith(previous.Files);
            leftovers.Issued.UnionWith(issued);
            HashSet<string> failed = new HashSet<string>(log.Skipped.Select(ExportLog.SplitSkip)
                .Where(s => !ExportLog.IsBenignSkip(s.Value)).Select(s => ExportLog.DocumentName(s.Key)), StringComparer.OrdinalIgnoreCase);
            leftovers.Failed.AddRange(failed);
            foreach (Item item in items)
            {
                if (item.Protected) leftovers.Protected.AddRange(item.Stems);
                else if (failed.Contains(Path.GetFileNameWithoutExtension(item.Path))) leftovers.Failed.AddRange(item.Stems);
                else leftovers.Exported.AddRange(item.Stems);
            }
            leftovers.TopExported = items.Count > 0 && !items[0].Protected &&
                !failed.Contains(Path.GetFileNameWithoutExtension(items[0].Path));

            HashSet<string> now = new HashSet<string>(log.Files.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            DateTime stamp = DateTime.Now;
            string[] folders =
            {
                ExportNaming.PdfDirectory(productFolder), ExportNaming.LaserDirectory(productFolder),
                ExportNaming.TubeDirectory(productFolder)
            };
            foreach (string folder in folders)
            {
                string[] files = Files(folder, log);
                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    if (now.Contains(name) || !ExportNaming.IsOutputFile(file)) continue;
                    string reason;
                    LeftoverAction action = leftovers.Decide(name, out reason);
                    if (action == LeftoverAction.Carry)
                    {
                        string sum;
                        log.Add(file);
                        log.Checksums[file] = previous != null && previous.Checksums.TryGetValue(name, out sum) ? sum : Checksum(file);
                        if (reason.Length > 0) log.Warn(name, reason);
                    }
                    else if (action == LeftoverAction.Archive) Retire(file, stamp, reason, log);
                }
            }
        }

        /// <summary>Файлы папки выдачи; папку не прочитать (сеть, права) — замечание в отчёт и пустой список.</summary>
        private static string[] Files(string folder, ExportLog log)
        {
            string problem;
            try
            {
                return Directory.Exists(folder) ? Directory.GetFiles(folder) : new string[0];
            }
            catch (IOException ex)
            {
                Log.Error("Выгрузка: папка выдачи " + folder, ex);
                problem = ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Выгрузка: папка выдачи " + folder, ex);
                problem = ex.Message;
            }
            log.Warn(Path.GetFileName(folder), "папка выдачи не прочитана (" + problem.Trim() +
                "): прежние файлы в ней не убраны — проверьте её сами");
            return new string[0];
        }

        /// <summary>Файл папки выдачи — в «_Аннулировано» рядом с ним; не вышло (файл открыт у цеха) — замечание, файл на месте.</summary>
        private static void Retire(string file, DateTime stamp, string reason, ExportLog log)
        {
            string name = Path.GetFileName(file);
            string problem;
            try
            {
                string archive = ExportNaming.ArchivePath(file, stamp);
                Directory.CreateDirectory(Path.GetDirectoryName(archive) ?? "");
                File.Move(file, archive);
                log.Warn(name, ExportLog.ArchiveNote + ": " + reason);
                return;
            }
            catch (IOException ex)
            {
                Log.Error("Выгрузка: перенос в " + ExportNaming.ArchiveFolder + " " + file, ex);
                problem = ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Выгрузка: перенос в " + ExportNaming.ArchiveFolder + " " + file, ex);
                problem = ex.Message;
            }
            log.Warn(name, "не убран в «" + ExportNaming.ArchiveFolder + "» (" + problem.Trim() + "): " + reason +
                " — уберите его сами, иначе проверка изделия его не пропустит");
        }

        private static void Show(ISldWorks app, ExportLog log, string productFolder)
        {
            List<Notice> notices = Notices.FromExport(log.Skipped, log.Warnings);
            NoticeLevel worst = Notices.Max(notices);
            string headline = worst == NoticeLevel.Critical ? "Выгружено не всё" : "Выгрузка готова";
            string details = "Выгружено файлов: " + log.Files.Count + ", пропущено: " + log.Skipped.Count + Environment.NewLine +
                "PDF — в «02_PDF», DXF — в «03_ЧПУ\\Лазер_Лист», IGS — в «03_ЧПУ\\Труборез».";
            string folder = productFolder;
            NoticeForm.Present(app, "ЕСКД: выгрузка для производства", headline, details, notices,
                worst == NoticeLevel.Info ? (NoticeLevel?)null : worst,
                new NoticeButton("Открыть папку изделия", () => System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\"")));
        }

        private static void Fail(ISldWorks app, bool interactive, string text)
        {
            LastOutcome = "error|" + text;
            Status(app, "");
            if (interactive) MessageBox.Show(text, "ЕСКД: выгрузка для производства", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                Log.Error("Выгрузка: строка состояния", ex);
            }
        }

        private static string Checksum(string path)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch (IOException ex)
            {
                Log.Error("Выгрузка: контрольная сумма " + path, ex);
                return "";
            }
        }
    }
}
