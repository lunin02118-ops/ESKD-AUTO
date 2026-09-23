using System;
using System.Collections.Generic;
using System.IO;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// З-1: графа «Формат» спецификации берётся из свойства «Формат» модели (SpecEditor связывает столбец со свойством,
    /// FrmSpecEditor:1803), а в SWPlus его вписывают руками в MProp. Надстройка при сохранении чертежа пишет в модель
    /// формат его листов — модель знает формат своего чертежа, спецификация читает его без правки ячеек.
    /// Уровни — как у MProp (FrmMProp:3042): каждая конфигурация модели (у файла один чертёж на все исполнения),
    /// общие — при одной конфигурации. Деталь БЧ не трогается. Служебные листы — спецификация, ведомость и развёртка
    /// Drew (SP…, VP…, DXF…) — не считаются.
    ///
    /// Замечание владельца 21.09.2026: «формат в спецификацию не попадает». Чертёж обычно сохраняют и сразу закрывают,
    /// а запись формата ждала простоя вместе с документом чертежа — закрытие её отменяло («Документ закрыт до выполнения
    /// отложенной задачи „drawingformat“»). Теперь форматы листов читаются в момент сохранения, пока чертёж открыт, а
    /// модель находится в простое по пути. Для чертежей, сохранённых до исправления, формат дозаполняет
    /// «Синхронизировать» на сборке (Backfill).
    ///
    /// Сохранение модели — только по настройке FormatSavesModel (решение владельца 23.09.2026: деталь без команды
    /// конструктора не сохраняется). По умолчанию открытая модель получает свойство и остаётся несохранённой, закрытая
    /// не открывается.
    /// </summary>
    public static class DrawingFormatService
    {
        /// <summary>
        /// Надстройка сама открывает, сохраняет и закрывает модель ради формата. Её сохранение не должно поднимать обычную
        /// синхронизацию и задачи простоя: документ закроется сразу после Save3 (урок R01).
        /// </summary>
        public static bool Busy { get; private set; }

        /// <summary>
        /// Форматы листов закрытых чертежей, прочитанные «Синхронизировать» за сеанс: путь → (время файла, форматы).
        /// Чертёж, сохранённый позже модели, так и остаётся новее (модель при совпадении не меняется и не сохраняется),
        /// и без запоминания каждое нажатие заново открывало бы все такие чертежи. Изменился файл — читается снова.
        /// </summary>
        private static readonly Dictionary<string, KeyValuePair<DateTime, List<string>>> SheetsRead =
            new Dictionary<string, KeyValuePair<DateTime, List<string>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Форматы листов чертежа по порядку; лист не формата ГОСТ — пустая строка.</summary>
        public static List<string> SheetFormats(DrawingDoc drw)
        {
            List<string> formats = new List<string>();
            object[] names = drw.GetSheetNames() as object[];
            if (names == null) return formats;
            foreach (object o in names)
            {
                string name = o as string ?? "";
                if (IsServiceSheet(name)) continue;
                Sheet sheet = drw.get_Sheet(name) as Sheet;
                if (sheet == null) continue;
                double w = 0, h = 0;
                sheet.GetSize(ref w, ref h);
                formats.Add(DrawingFormat.FromSize(w * 1000, h * 1000));
            }
            return formats;
        }

        /// <summary>
        /// Служебный лист — не лист чертежа: спецификация и ведомость (SP…, VP…) и развёртка Drew для лазера (DXF…,
        /// произвольного размера; в PDF её тоже не выводят). Раньше лист DXF давал «формат не ГОСТ», и «Формат» листовых
        /// деталей не писался совсем (живая проверка NC3-7R 22.09.2026).
        /// </summary>
        public static bool IsServiceSheet(string name)
        {
            foreach (string prefix in new[] { "SP", "VP", "DXF" })
                if ((name ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Снять форматы листов и путь модели, пока чертёж открыт (событие сохранения). Только чтение.
        /// false — модели нет, лист не формата ГОСТ 2.301 или «Формат» модели уже такой: задача простоя не нужна.
        /// </summary>
        public static bool Capture(DrawingDoc drw, out string modelPath, out List<string> sheets)
        {
            modelPath = "";
            sheets = new List<string>();
            ModelDoc2 model = SyncService.ReferencedModel(drw);
            if (model == null) return false;
            modelPath = model.GetPathName() ?? "";
            if (modelPath.Length == 0) return false;
            sheets = SheetFormats(drw);
            if (sheets.Count == 0) return false;
            if (sheets.Contains(""))
            {
                Log.Warn("Формат чертежа не определён (лист не формата ГОСТ 2.301): «Формат» модели " +
                    Path.GetFileName(modelPath) + " не меняется");
                return false;
            }
            // Модель, пока чертёж открыт, загружена: сверка без записи. Совпадает — ни задачи, ни сообщения.
            return Write(model, sheets, true) > 0;
        }

        /// <summary>
        /// Записать формат в модель по пути (в простое). Возвращает текст для строки состояния; "" — сказать нечего.
        /// FormatSavesModel выключена (по умолчанию): открытая модель получает свойство без сохранения, закрытая не
        /// трогается. Включена: открытая без других правок сохраняется, закрытая открывается скрыто, записывается,
        /// сохраняется и закрывается.
        /// </summary>
        public static string ApplyToModel(ISldWorks app, string modelPath, List<string> sheets)
        {
            if (string.IsNullOrEmpty(modelPath) || sheets == null || sheets.Count == 0) return "";
            bool save = Settings.Read().FormatSavesModel;
            ModelDoc2 model = app.GetOpenDocumentByName(modelPath) as ModelDoc2;
            bool opened = false;
            if (model == null && !save)
            {
                string closed = "ЕСКД: формат чертежа не записан в модель " + Path.GetFileName(modelPath) +
                    " — она закрыта. Нажмите «Синхронизировать» на сборке или откройте модель и сохраните чертёж ещё раз";
                Log.Warn(closed);
                return closed;
            }
            Busy = true;
            try
            {
                if (model == null)
                {
                    model = OpenHidden(app, modelPath);
                    opened = model != null;
                    if (opened) Log.Info("Формат чертежа: модель " + Path.GetFileName(modelPath) + " выгружена вместе с чертежом — открыта скрыто");
                    if (model == null)
                    {
                        Log.Warn("Формат чертежа: модель " + modelPath + " не открылась — «Формат» не записан");
                        return "";
                    }
                }
                string title = DocInfo.TitleOf(model);
                // Общее правило надстройки: покупное и стандартное (по свойствам) и открытое только для чтения не пишется.
                string why = DocumentGuard.DocVerdict(model);
                if (why.Length > 0)
                {
                    Log.Warn("Модель " + title + ": " + why + " — «Формат» не записан");
                    return "";
                }
                bool wasDirty = !opened && model.GetSaveFlag();
                int changes = Write(model, sheets);
                if (changes == 0) return "";
                if (!save)
                {
                    string told = "ЕСКД: формат чертежа записан в модель " + title + " — сохраните её";
                    Log.Info(told);
                    return told;
                }
                if (wasDirty)
                {
                    Log.Info("Модель " + title + " не сохранена: в ней есть и другие несохранённые правки — формат запишется с ними");
                    return "";
                }
                int errors = 0, warnings = 0;
                if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                    Log.Error(string.Format("Модель {0} не сохранена после записи формата (errors={1}, warnings={2})", title, errors, warnings));
                return "";
            }
            finally
            {
                if (opened) Close(app, model);
                Busy = false;
            }
        }

        /// <summary>
        /// Привести «Формат» модели к её чертежу рядом (тот же путь, .slddrw). Сверяется, когда «Формат» пуст (чертежи,
        /// сохранённые до исправления 21.09.2026), когда чертёж открыт и когда файл чертежа новее файла модели: с
        /// 23.09.2026 сохранение чертежа не открывает закрытую модель, и её прежний формат остаётся до этой команды.
        /// Чертёж открывается скрыто только для чтения и закрывается. Модель не сохраняется — это делает вызывающий.
        /// Возвращает число изменений.
        /// </summary>
        public static int Backfill(ISldWorks app, ModelDoc2 model)
        {
            string modelPath = model.GetPathName() ?? "";
            if (modelPath.Length == 0) return 0;
            string drawingPath = Path.ChangeExtension(modelPath, ".slddrw");
            if (!File.Exists(drawingPath)) return 0;
            Settings settings = Settings.Read();
            PropertyDictionary dict = SyncService.Dictionary(settings);
            PropertyWriter probe = new PropertyWriter(model, true);
            if (BchService.IsBch(probe, dict)) return 0;
            ModelDoc2 drawing = app.GetOpenDocumentByName(drawingPath) as ModelDoc2;
            if (drawing == null && !FormatMissing(probe, dict) && !DrawingNewer(drawingPath, modelPath)) return 0;

            DateTime stamp = DateTime.MinValue;
            try
            {
                stamp = File.GetLastWriteTimeUtc(drawingPath);
            }
            catch (Exception ex)
            {
                Log.Warn("Формат: время файла " + Path.GetFileName(drawingPath) + " не прочитано (" + ex.Message + ")");
            }
            KeyValuePair<DateTime, List<string>> known;
            if (drawing == null && stamp != DateTime.MinValue && SheetsRead.TryGetValue(drawingPath, out known) && known.Key == stamp)
                return known.Value.Count == 0 || known.Value.Contains("") ? 0 : Write(model, known.Value);

            List<string> sheets;
            bool opened = false;
            Busy = true;
            try
            {
                if (drawing == null)
                {
                    int errors = 0, warnings = 0;
                    app.DocumentVisible(false, (int)swDocumentTypes_e.swDocDRAWING);
                    try
                    {
                        drawing = app.OpenDoc6(drawingPath, (int)swDocumentTypes_e.swDocDRAWING,
                            (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                            "", ref errors, ref warnings) as ModelDoc2;
                    }
                    finally
                    {
                        app.DocumentVisible(true, (int)swDocumentTypes_e.swDocDRAWING);
                    }
                    opened = drawing != null;
                }
                if (drawing == null)
                {
                    Log.Warn("Формат: чертёж " + Path.GetFileName(drawingPath) + " не открылся — «Формат» не дозаполнен");
                    return 0;
                }
                sheets = SheetFormats((DrawingDoc)drawing);
                // Запоминаются только листы файла: открытый конструктором чертёж может быть изменён и не сохранён.
                if (opened && stamp != DateTime.MinValue) SheetsRead[drawingPath] = new KeyValuePair<DateTime, List<string>>(stamp, sheets);
            }
            finally
            {
                if (opened) Close(app, drawing);
                Busy = false;
            }
            if (sheets.Count == 0 || sheets.Contains("")) return 0;
            return Write(model, sheets);
        }

        /// <summary>Чертёж сохранён позже модели: её «Формат» мог отстать от листов.</summary>
        private static bool DrawingNewer(string drawingPath, string modelPath)
        {
            try
            {
                return File.GetLastWriteTimeUtc(drawingPath) > File.GetLastWriteTimeUtc(modelPath);
            }
            catch (Exception ex)
            {
                Log.Warn("Формат: время файлов " + Path.GetFileName(modelPath) + " не прочитано (" + ex.Message + ") — сверяю по чертежу");
                return true;
            }
        }

        /// <summary>«Формат» пуст (или шаблонный) и в общих свойствах, и во всех конфигурациях.</summary>
        private static bool FormatMissing(PropertyWriter w, PropertyDictionary dict)
        {
            string name = dict[Role.Format];
            List<string> levels = new List<string>(w.ConfigurationNames()) { "" };
            foreach (string level in levels)
            {
                if (!PropertyWriter.IsEmptyOrTemplate((w.Raw(level, name) ?? "").Trim())) return false;
            }
            return true;
        }

        /// <summary>Записать «Формат» (и перечень форматов в «Примечание») на уровни модели. Возвращает число изменений.</summary>
        private static int Write(ModelDoc2 model, List<string> sheets)
        {
            return Write(model, sheets, false);
        }

        /// <summary>dryRun — только сверка: число свойств, которые запись изменила бы; модель не меняется.</summary>
        private static int Write(ModelDoc2 model, List<string> sheets, bool dryRun)
        {
            string title = DocInfo.TitleOf(model);
            string remark;
            string column = DrawingFormat.Column(sheets, out remark);
            if (column.Length == 0) return 0;

            Settings settings = Settings.Read();
            PropertyDictionary dict = SyncService.Dictionary(settings);
            PropertyWriter w = new PropertyWriter(model, dryRun || settings.DryRun);
            if (BchService.IsBch(w, dict)) return 0;

            string[] configs = w.ConfigurationNames();
            List<string> levels = new List<string>(configs);
            if (configs.Length <= 1) levels.Add("");
            foreach (string level in levels)
            {
                w.Set(level, dict[Role.Format], column);
                string oldRemark = w.Raw(level, dict[Role.Remark]);
                if (remark.Length > 0)
                {
                    if (DrawingFormat.RemarkIsFormatList(oldRemark)) w.Set(level, dict[Role.Remark], remark);
                    else if (!dryRun) Log.Warn(string.Format("{0} [{1}]: «Примечание» занято («{2}») — перечень форматов «{3}» впишите сами",
                        title, level.Length == 0 ? "общие" : level, oldRemark, remark));
                }
                else if (oldRemark != null && oldRemark.Trim().StartsWith(DrawingFormat.Several, StringComparison.Ordinal))
                {
                    w.Set(level, dict[Role.Remark], "");
                }
            }
            if (dryRun) return w.Operations.Count;
            if (w.Operations.Count > 0)
                Log.Info(string.Format("Формат чертежа → модель {0}: {1}; операций {2}\r\n    {3}", title, column, w.Operations.Count,
                    string.Join("\r\n    ", w.Operations.ToArray())));
            return w.Changes;
        }

        private static ModelDoc2 OpenHidden(ISldWorks app, string path)
        {
            int type = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                ? (int)swDocumentTypes_e.swDocASSEMBLY
                : (int)swDocumentTypes_e.swDocPART;
            int errors = 0, warnings = 0;
            app.DocumentVisible(false, type);
            try
            {
                return app.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as ModelDoc2;
            }
            catch (Exception ex)
            {
                Log.Error("Формат чертежа: открытие " + path, ex);
                return null;
            }
            finally
            {
                app.DocumentVisible(true, type);
            }
        }

        private static void Close(ISldWorks app, ModelDoc2 doc)
        {
            try
            {
                if (doc != null) app.CloseDoc(doc.GetTitle());
            }
            catch (Exception ex)
            {
                Log.Error("Формат чертежа: закрытие документа", ex);
            }
        }
    }
}
