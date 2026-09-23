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
            errors = 0;
            int warnings = 0;
            string path = DocInfo.PathOf(model);
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
            if (saved && before.Length > 0)
                Changes.Add(new StampChange { Name = Path.GetFileName(path), Before = before, After = ProductFreshness.Checksum(path) });
            return saved;
        }
    }
}
