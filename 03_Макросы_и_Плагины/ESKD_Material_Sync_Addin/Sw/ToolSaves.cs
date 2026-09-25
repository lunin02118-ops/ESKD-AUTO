using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Сохранения, которые делает сама кнопка: ЛЗК — модели с записанными «Операциями» и «Габаритом», выгрузка — деталь
    /// после возврата исполнения и временной СК. Кнопка сохраняет только то, что изменила сама: документ с несохранёнными
    /// правками конструктора она не сохраняет (решение владельца 23.09.2026, журнал З-25) — это проверяют вызывающие.
    /// На время сохранения молчит синхронизация по событию: кнопка уже записала своё, а изменения, о которых конструктора
    /// не спрашивали, при чужом сохранении не пишутся. Суммы файла до и после — чтобы отчёт проверки знал, что файл
    /// изменила кнопка, а не конструктор (<see cref="ProductFreshness.Restamp"/>).
    /// </summary>
    public sealed class ToolSaves
    {
        private static int _depth;

        /// <summary>Идёт сохранение кнопкой: синхронизация по событию сохранения пропускается.</summary>
        public static bool Busy { get { return _depth > 0; } }

        public readonly List<StampChange> Changes = new List<StampChange>();

        /// <summary>Сохранить документ молча. false — не сохранён; errors — код SolidWorks.</summary>
        public bool Save(ModelDoc2 model, out int errors)
        {
            int warnings;
            return Save(model, out errors, out warnings);
        }

        /// <summary>
        /// Сохранить документ молча. Открытый только для чтения (деталь держит коллега, файл защищён) не сохраняется вовсе:
        /// Save3 не зовётся, errors — код SolidWorks «только для чтения», и отчёт скажет «закройте без сохранения», а не
        /// «ответьте «Да»» (сверка SW API 23.09.2026, №17). Ошибки перестроения в модели — в журнал, сохранение засчитано.
        /// </summary>
        public bool Save(ModelDoc2 model, out int errors, out int warnings)
        {
            errors = 0;
            warnings = 0;
            string path = DocInfo.PathOf(model);
            if (OpenedReadOnly(model))
            {
                errors = SwCodes.SaveReadOnly;
                Log.Warn("Сохранение кнопкой: документ открыт только для чтения — " + path);
                return false;
            }
            string before = path.Length > 0 && File.Exists(path) ? ProductFreshness.Checksum(path) : "";
            bool saved;
            _depth++;
            try
            {
                saved = model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
            }
            catch (COMException ex)
            {
                Log.Error("Сохранение кнопкой: " + path, ex);
                saved = false;
            }
            finally
            {
                _depth--;
            }
            if (saved && (warnings & SwCodes.SaveWarningRebuildError) != 0)
                Log.Warn("Сохранение кнопкой: в модели ошибки перестроения — " + path);
            if (saved && before.Length > 0)
                Changes.Add(new StampChange
                {
                    Path = path,
                    Name = System.IO.Path.GetFileName(path),
                    Before = before,
                    After = ProductFreshness.Checksum(path)
                });
            return saved;
        }

        /// <summary>
        /// Документы, которые кнопка застала чистыми, а SolidWorks пометил изменёнными, пока она их читала, сохраняются снова
        /// (З-55, 25.09.2026): первое открытие устаревшего чертежа с видом развёртки перестраивает развёртку детали,
        /// подсборка с двумя исполнениями в изделии отмечается при перестроении и чтении главной сборки. В них ничего не
        /// менялось, но следующая кнопка называла их «несохранённые правки». models — только те, что кнопке можно
        /// сохранять, в порядке состава сверху вниз (главная сборка первой). Сначала все перестроения — без них SolidWorks
        /// сохраняет с вопросом «перестроить?» (X05), а перестроение главной сборки снова отметило бы сохранённую до него
        /// подсборку, — потом детали и сборки снизу вверх; второй круг — если сохранение одного документа снова отметило
        /// другой. Возвращает имена документов, оставшихся изменёнными.
        /// </summary>
        public List<string> ResaveUnchanged(IList<ModelDoc2> models, string step)
        {
            List<ModelDoc2> parts = new List<ModelDoc2>(), assemblies = new List<ModelDoc2>();
            foreach (ModelDoc2 model in models)
            {
                if (model == null) continue;
                if (TypeOf(model) == (int)swDocumentTypes_e.swDocASSEMBLY) assemblies.Add(model);
                else parts.Add(model);
            }
            for (int round = 0; round < 2 && Dirty(parts, assemblies).Count > 0; round++)
            {
                foreach (ModelDoc2 model in parts) Rebuild(model, step);
                foreach (ModelDoc2 model in assemblies) Rebuild(model, step);
                foreach (ModelDoc2 model in parts) Resave(model, step);
                for (int i = assemblies.Count - 1; i >= 0; i--) Resave(assemblies[i], step);
            }
            List<string> left = Dirty(parts, assemblies);
            if (left.Count > 0) Log.Warn(step + ": после своих сохранений остались изменёнными — " + string.Join(", ", left.ToArray()));
            return left;
        }

        private static List<string> Dirty(List<ModelDoc2> parts, List<ModelDoc2> assemblies)
        {
            List<string> dirty = new List<string>();
            foreach (ModelDoc2 model in parts)
                if (DocumentGuard.HasUserEdits(model)) dirty.Add(System.IO.Path.GetFileName(DocInfo.PathOf(model)));
            foreach (ModelDoc2 model in assemblies)
                if (DocumentGuard.HasUserEdits(model)) dirty.Add(System.IO.Path.GetFileName(DocInfo.PathOf(model)));
            return dirty;
        }

        private static int TypeOf(ModelDoc2 model)
        {
            try
            {
                return model.GetType();
            }
            catch (COMException)
            {
                return 0;
            }
        }

        private static void Rebuild(ModelDoc2 model, string step)
        {
            try
            {
                model.EditRebuild3();
            }
            catch (COMException ex)
            {
                Log.Error(step + ": перестроение " + DocInfo.PathOf(model), ex);
            }
        }

        private void Resave(ModelDoc2 model, string step)
        {
            if (!DocumentGuard.HasUserEdits(model)) return;
            string name = System.IO.Path.GetFileName(DocInfo.PathOf(model));
            int errors;
            if (Save(model, out errors))
                Log.Info(step + ": " + name + " сохранён снова — изменённым его пометил SolidWorks, пока кнопка его читала");
            else
                Log.Warn(step + ": " + name + " после чтения не сохранён (" + SwCodes.SaveProblem(errors) + ")");
        }

        private static bool OpenedReadOnly(ModelDoc2 model)
        {
            try
            {
                return model.IsOpenedReadOnly();
            }
            catch (COMException ex)
            {
                Log.Error("Сохранение кнопкой: только для чтения ли документ", ex);
                return true;
            }
        }
    }
}
