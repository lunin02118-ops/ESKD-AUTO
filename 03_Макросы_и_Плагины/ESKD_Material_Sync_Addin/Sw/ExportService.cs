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
    /// в папки изделия, отчёт `_Экспорт.txt` с контрольными суммами. Модели не меняются (Т-6).
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
            public bool IsAssembly;
            public bool IsPurchased;
            public string Designation = "";
            public string Name = "";
            public int Revision;
        }

        public static bool Run(ISldWorks app, bool interactive)
        {
            LastOutcome = "";
            ModelDoc2 doc = null;
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
                string productFolder = location.ProductFolder.Length > 0
                    ? location.ProductFolder : LzkNaming.ProductFolder(path);

                Status(app, "ЕСКД: выгрузка для производства — состав…");
                List<Item> items = Collect(app, doc, path, productFolder);
                HashSet<string> issued = ExportNaming.Issued(productFolder);
                ExportLog log = new ExportLog
                {
                    Product = LzkNaming.Cipher(productFolder, path),
                    User = Settings.AuthorOrUser(),
                    Time = DateTime.Now
                };
                foreach (Item item in items)
                {
                    Status(app, "ЕСКД: выгрузка — " + Path.GetFileName(item.Path));
                    // Чертёж открывается один раз — и для ревизии, и для PDF (аудит 19.09, Л-В7): по сети каждое
                    // открытие чертежа — секунды, а у изделия их десятки.
                    string drawingPath = Path.ChangeExtension(item.Path, ".slddrw");
                    bool opened = false;
                    ModelDoc2 drawing = null;
                    try
                    {
                        if (File.Exists(drawingPath)) drawing = OpenDrawing(app, drawingPath, out opened);
                        // Ревизия принадлежит чертежу (Р0-8): её поднимает К-7 в чертеже, а модель об этом не знает.
                        // Поэтому суффикс «_ИзмN» и право переписать выданное берутся из чертежа, если он есть.
                        item.Revision = Math.Max(item.Revision, DrawingRevision(drawing, drawingPath));
                        // Выданный документ перезаписывать нельзя: цех работает по тому, что у него на руках (Т-30).
                        if (item.Revision == 0 && issued.Contains(Path.GetFileName(item.Path)))
                        {
                            log.Skip(Path.GetFileName(item.Path), "документ выдан в производство, оформите новую ревизию");
                            continue;
                        }
                        Pdf(app, item, productFolder, log, drawingPath, drawing);
                    }
                    finally
                    {
                        if (opened && drawing != null) app.CloseDoc(drawing.GetPathName());
                    }
                    if (!item.IsAssembly)
                    {
                        Dxf(app, item, productFolder, log);
                        Igs(app, item, productFolder, log);
                    }
                }
                // Экспорт переключал окна: конструктор должен увидеть ту же сборку, с которой начал.
                Activate(app, path);
                string reportPath = Write(productFolder, log, type == (int)swDocumentTypes_e.swDocPART, items);
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
        }

        // ------------------------------------------------------------------ состав
        /// <summary>Что выгружать: сама модель и — для сборки — её новые детали и подсборки изделия (Т-26).</summary>
        private static List<Item> Collect(ISldWorks app, ModelDoc2 doc, string path, string productFolder)
        {
            List<Item> items = new List<Item>();
            AddItem(items, doc, path, doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY);
            if (doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return items;

            AssemblyDoc asm = (AssemblyDoc)doc;
            try
            {
                asm.ResolveAllLightWeightComponents(false);
            }
            catch (COMException ex)
            {
                Log.Error("Выгрузка: разрешение облегчённых компонентов", ex);
            }
            object[] comps = asm.GetComponents(false) as object[] ?? new object[0];
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            foreach (object o in comps)
            {
                Component2 comp = o as Component2;
                if (comp == null) continue;
                string componentPath = comp.GetPathName() ?? "";
                if (componentPath.Length == 0 || !File.Exists(componentPath) || !seen.Add(componentPath)) continue;
                if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                // Выгружается только своё: эталоны базы и покупные приходят готовыми (Т-26).
                if (!LzkNaming.IsInside(componentPath, productFolder)) continue;
                ModelDoc2 model = comp.GetModelDoc2() as ModelDoc2;
                if (model == null) continue;
                AddItem(items, model, componentPath, model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY);
            }
            return items;
        }

        private static void AddItem(List<Item> items, ModelDoc2 model, string path, bool assembly)
        {
            Item item = new Item { Path = path, Model = model, IsAssembly = assembly };
            try
            {
                PropertyWriter w = new PropertyWriter(model, true);
                string cfg = w.ActiveConfigurationName();
                item.Designation = Value(w, cfg, "Обозначение");
                item.Name = Value(w, cfg, "Наименование");
                item.Revision = ExportNaming.Revision(Value(w, cfg, "Revision"));
                item.IsPurchased = ComponentKind.IsPurchased(w, model, path, "Выгрузка");
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: реквизиты " + path, ex);
            }
            if (!item.IsPurchased) items.Add(item);
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

        /// <summary>Чертёж, уже открытый в SolidWorks, или открытый здесь (opened — закрыть после работы); null — не открылся.</summary>
        private static ModelDoc2 OpenDrawing(ISldWorks app, string drawingPath, out bool opened)
        {
            opened = false;
            try
            {
                ModelDoc2 drawing = app.GetOpenDocumentByName(drawingPath) as ModelDoc2;
                if (drawing != null) return drawing;
                int errors = 0, warnings = 0;
                drawing = app.OpenDoc6(drawingPath, (int)swDocumentTypes_e.swDocDRAWING,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as ModelDoc2;
                opened = drawing != null;
                if (drawing == null) Log.Warn("Выгрузка: чертёж не открылся (код " + errors + "): " + drawingPath);
                return drawing;
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: открытие чертежа " + drawingPath, ex);
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
        private static void Pdf(ISldWorks app, Item item, string productFolder, ExportLog log, string drawingPath, ModelDoc2 drawing)
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
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");

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
            bool[] previous = new bool[toggles.Length];
            if (drawing == null)
            {
                log.Skip(Path.GetFileName(drawingPath), "чертёж не открылся");
                return;
            }
            try
            {
                for (int i = 0; i < toggles.Length; i++)
                {
                    previous[i] = app.GetUserPreferenceToggle(toggles[i]);
                    app.SetUserPreferenceToggle(toggles[i], wanted[i]);
                }
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
                for (int i = 0; i < toggles.Length; i++)
                {
                    try
                    {
                        app.SetUserPreferenceToggle(toggles[i], previous[i]);
                    }
                    catch (COMException ex)
                    {
                        Log.Error("Выгрузка: восстановление настройки PDF", ex);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ DXF развёрток (Т-28)
        private static void Dxf(ISldWorks app, Item item, string productFolder, ExportLog log)
        {
            PartDoc part = item.Model as PartDoc;
            if (part == null) return;
            double thickness = SheetThicknessMm(item.Model);
            if (double.IsNaN(thickness)) return;  // не листовая деталь — развёртки и не должно быть

            string folder = ExportNaming.LaserDirectory(productFolder);
            Directory.CreateDirectory(folder);
            // Таблица соответствия слоёв превратила бы тихий экспорт в диалог: она здесь не нужна. Настройки
            // пользователя возвращаются после экспорта (аудит 19.09, Л-В8) — его ручной DXF не должен меняться.
            int[] toggles = { (int)swUserPreferenceToggle_e.swDxfMapping, (int)swUserPreferenceToggle_e.swDXFDontShowMap };
            bool[] wanted = { false, true };
            bool[] previous = new bool[toggles.Length];
            bool[] changed = new bool[toggles.Length];
            for (int i = 0; i < toggles.Length; i++)
            {
                try
                {
                    previous[i] = app.GetUserPreferenceToggle(toggles[i]);
                    if (previous[i] == wanted[i]) continue;
                    app.SetUserPreferenceToggle(toggles[i], wanted[i]);
                    changed[i] = true;
                }
                catch (COMException ex)
                {
                    Log.Error("Выгрузка: настройки DXF", ex);
                }
            }
            // Размеры рамки видны только в готовой развёртке, поэтому экспорт идёт во временный файл,
            // а окончательное имя «…_S<толщина>мм_<ширина>х<длина>.dxf» получается после замера.
            string temporary = Path.Combine(folder, "_замер_" + Guid.NewGuid().ToString("N") + ".dxf");
            try
            {
                // Развёртку SolidWorks отдаёт только активному документу: компонент сборки, открытый в фоне,
                // получает отказ без объяснения (боевой заказ NC3-7R). Активная сборка возвращается в конце.
                if (!Activate(app, item.Path))
                {
                    log.Skip(Path.GetFileName(item.Path), "SolidWorks не сделал деталь активной: DXF не сделан");
                    return;
                }
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
                    log.Skip(Path.GetFileName(item.Path), "нет развёртки: DXF не сделан");
                    return;
                }
                double width, length;
                if (!DxfFrame.Measure(temporary, out width, out length))
                {
                    log.Skip(Path.GetFileName(item.Path), "развёртка пустая: DXF не сделан");
                    return;
                }
                string target = ExportNaming.DxfPath(productFolder, item.Designation, item.Name, item.Path,
                    thickness, width, length, item.Revision);
                if (ExportNaming.TooLong(target).Length > 0)
                {
                    log.Skip(Path.GetFileName(item.Path), "DXF не сделан: " + ExportNaming.TooLong(target));
                    return;
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(temporary, target);
                log.Add(target);
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: DXF " + item.Path, ex);
                log.Skip(Path.GetFileName(item.Path), "DXF не сделан: " + ex.Message);
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
                for (int i = 0; i < toggles.Length; i++)
                {
                    if (!changed[i]) continue;
                    try
                    {
                        app.SetUserPreferenceToggle(toggles[i], previous[i]);
                    }
                    catch (COMException ex)
                    {
                        Log.Error("Выгрузка: восстановление настройки DXF", ex);
                    }
                }
            }
        }

        /// <summary>Толщина листовой детали, мм; NaN — деталь не листовая.</summary>
        private static double SheetThicknessMm(ModelDoc2 model)
        {
            try
            {
                for (Feature f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                {
                    if (f.GetTypeName2() != "SheetMetal") continue;
                    SheetMetalFeatureData data = f.GetDefinition() as SheetMetalFeatureData;
                    if (data != null) return data.Thickness * 1000.0;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: толщина листа", ex);
            }
            return double.NaN;
        }

        /// <summary>
        /// Сделать документ активным — экспорт развёртки и IGS работает только с активным окном. false — не стал
        /// активным: занятый SolidWorks отвечает отказом, и выгрузка пошла бы по чужому документу, но с отметкой «ok»
        /// (аудит 20.09.2026). Проверяется не код возврата, а сам активный документ: он и есть признак успеха.
        /// </summary>
        private static bool Activate(ISldWorks app, string path)
        {
            try
            {
                int errors = 0;
                app.ActivateDoc3(path, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                ModelDoc2 active = app.ActiveDoc as ModelDoc2;
                string now = active != null ? active.GetPathName() ?? "" : "";
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

        private static double Number(CustomPropertyManager m, string name)
        {
            string raw, resolved;
            m.Get4(name, false, out raw, out resolved);
            double value = LzkOperations.ParseNumber(resolved);
            if (double.IsNaN(value)) value = LzkOperations.ParseNumber(raw);
            return double.IsNaN(value) ? 0 : value;
        }

        // ------------------------------------------------------------------ IGS профиля (Т-29)
        private static void Igs(ISldWorks app, Item item, string productFolder, ExportLog log)
        {
            if (!IsStructuralMember(item.Model)) return;
            string target = ExportNaming.IgsPath(productFolder, item.Designation, item.Name, item.Path, item.Revision);
            if (ExportNaming.TooLong(target).Length > 0)
            {
                log.Skip(Path.GetFileName(item.Path), "IGS не сделан: " + ExportNaming.TooLong(target));
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
            // Труборезу нужны поверхности вместе с кривыми в стандартном наборе IGES (Т-29); прежние
            // настройки конструктора возвращаются на место — кнопка ничего за собой не оставляет.
            int[] prefs =
            {
                (int)swUserPreferenceIntegerValue_e.swIGESRepresentation,
                (int)swUserPreferenceIntegerValue_e.swIGESSystem
            };
            int[] wanted = { (int)swIGESRepresentation_e.swIGES_TRMSRFANDCURVES, (int)swIGESPreferredSystem_e.swIGES_STANDARD };
            int[] previous = new int[prefs.Length];
            try
            {
                for (int i = 0; i < prefs.Length; i++)
                {
                    previous[i] = app.GetUserPreferenceIntegerValue(prefs[i]);
                    app.SetUserPreferenceIntegerValue(prefs[i], wanted[i]);
                }
                int errors = 0, warnings = 0;
                bool ok;
                using (TubeAxis axis = TubeAxis.Create(app, item.Model, item.Path))
                {
                    ok = item.Model.Extension.SaveAs(target, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref errors, ref warnings);
                    if (ok && !axis.Applied)
                        log.Warn(Path.GetFileName(target), "IGS в глобальной системе координат: " + axis.Reason);
                }
                if (ok) log.Add(target);
                else log.Skip(Path.GetFileName(item.Path), "IGS не сохранён (код " + errors + ")");
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: IGS " + item.Path, ex);
                log.Skip(Path.GetFileName(item.Path), "IGS не сделан: " + ex.Message);
            }
            finally
            {
                for (int i = 0; i < prefs.Length; i++)
                {
                    try
                    {
                        app.SetUserPreferenceIntegerValue(prefs[i], previous[i]);
                    }
                    catch (COMException ex)
                    {
                        Log.Error("Выгрузка: восстановление настройки IGES", ex);
                    }
                }
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
                for (Feature f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                {
                    string type = f.GetTypeName2() ?? "";
                    if (type == "WeldMemberFeat") return true;
                    if (type != "SolidBodyFolder") continue;
                    for (Feature sub = f.GetFirstSubFeature() as Feature; sub != null; sub = sub.GetNextSubFeature() as Feature)
                    {
                        if (sub.GetTypeName2() != "CutListFolder") continue;
                        CustomPropertyManager m = sub.CustomPropertyManager;
                        if (m == null) continue;
                        // Имя свойства зависит от языка SolidWorks («ДЛИНА»), поэтому ищется по английской ссылке.
                        string length = CutListProperties.Find(LzkService.Written(m), "LENGTH", CutListProperties.LengthSpellings);
                        if (LzkService.Value(m, length) > 0) return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: признак профиля", ex);
            }
            return false;
        }

        // ------------------------------------------------------------------ отчёт
        private static string Write(string productFolder, ExportLog log, bool partial, IEnumerable<Item> items)
        {
            foreach (string file in log.Files) log.Checksums[file] = Checksum(file);
            string path = ExportNaming.ReportPath(productFolder);
            // Выгрузка одной детали (и «Новая ревизия») дописывает отчёт изделия, а не заменяет его: иначе проверка
            // изделия сочла бы все остальные детали невыгруженными. Прежние файлы остаются, если лежат на месте
            // и не переписаны сейчас; прежние пропуски — если документ сейчас не выгружался.
            if (partial && File.Exists(path)) Merge(productFolder, log, ExportLog.Parse(File.ReadAllText(path, Encoding.UTF8)), items);
            Directory.CreateDirectory(productFolder);
            File.WriteAllText(path, log.Text(), new UTF8Encoding(true));
            return path;
        }

        private static void Merge(string productFolder, ExportLog log, ExportLog previous, IEnumerable<Item> items)
        {
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
                if (!documents.Contains(Path.GetFileNameWithoutExtension(ExportLog.SplitSkip(line).Key))) log.Skipped.Add(line);
            foreach (string line in previous.Warnings)
                if (!documents.Contains(Path.GetFileNameWithoutExtension(ExportLog.SplitSkip(line).Key))) log.Warnings.Add(line);
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
