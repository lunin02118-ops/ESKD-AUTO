using System;
using System.Collections.Generic;
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
    /// общие — при одной конфигурации. Деталь БЧ не трогается. Листы спецификации и ведомости (SP…, VP…) не считаются.
    /// </summary>
    public static class DrawingFormatService
    {
        /// <summary>Форматы листов чертежа по порядку; лист не формата ГОСТ — пустая строка.</summary>
        public static List<string> SheetFormats(DrawingDoc drw)
        {
            List<string> formats = new List<string>();
            object[] names = drw.GetSheetNames() as object[];
            if (names == null) return formats;
            foreach (object o in names)
            {
                string name = o as string ?? "";
                if (name.StartsWith("SP", StringComparison.OrdinalIgnoreCase) || name.StartsWith("VP", StringComparison.OrdinalIgnoreCase)) continue;
                Sheet sheet = drw.get_Sheet(name) as Sheet;
                if (sheet == null) continue;
                double w = 0, h = 0;
                sheet.GetSize(ref w, ref h);
                formats.Add(DrawingFormat.FromSize(w * 1000, h * 1000));
            }
            return formats;
        }

        /// <summary>Записать формат чертежа в его модель; модель без других несохранённых правок сохраняется молча.</summary>
        public static void Apply(ISldWorks app, DrawingDoc drw)
        {
            ModelDoc2 model = SyncService.ReferencedModel(drw);
            if (model == null) return;
            string title = DocInfo.TitleOf(model);
            List<string> sheets = SheetFormats(drw);
            if (sheets.Contains(""))
            {
                Log.Warn("Формат чертежа не определён (лист не формата ГОСТ 2.301): «Формат» модели " + title + " не меняется");
                return;
            }
            string remark;
            string column = DrawingFormat.Column(sheets, out remark);
            if (column.Length == 0) return;
            if (model.IsOpenedReadOnly())
            {
                Log.Warn("Модель " + title + " открыта только для чтения: «Формат» = " + column + " не записан");
                return;
            }

            Settings settings = Settings.Read();
            PropertyDictionary dict = SyncService.Dictionary(settings);
            PropertyWriter w = new PropertyWriter(model, settings.DryRun);
            if (BchService.IsBch(w, dict)) return;
            bool wasDirty = model.GetSaveFlag();

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
                    else Log.Warn(string.Format("{0} [{1}]: «Примечание» занято («{2}») — перечень форматов «{3}» впишите сами",
                        title, level.Length == 0 ? "общие" : level, oldRemark, remark));
                }
                else if (oldRemark != null && oldRemark.Trim().StartsWith(DrawingFormat.Several, StringComparison.Ordinal))
                {
                    w.Set(level, dict[Role.Remark], "");
                }
            }
            if (w.Operations.Count == 0) return;
            Log.Info(string.Format("Формат чертежа → модель {0}: {1}; операций {2}\r\n    {3}", title, column, w.Operations.Count,
                string.Join("\r\n    ", w.Operations.ToArray())));
            if (w.Changes == 0 || wasDirty)
            {
                if (wasDirty) Log.Info("Модель " + title + " не сохранена: в ней есть и другие несохранённые правки");
                return;
            }
            int errors = 0, warnings = 0;
            if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                Log.Error(string.Format("Модель {0} не сохранена после записи формата (errors={1}, warnings={2})", title, errors, warnings));
        }
    }
}
