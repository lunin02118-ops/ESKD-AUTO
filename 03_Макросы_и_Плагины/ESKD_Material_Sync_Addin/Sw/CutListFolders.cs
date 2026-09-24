using System.Collections.Generic;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Обход списка вырезов — одно правило для материала по геометрии, книги ЛЗК и выгрузки (сверка SW API 23.09.2026,
    /// №32, №34, №41). Только папки с телами в активном исполнении: папка без тел — папка элемента, погашенного в этом
    /// исполнении (у «Укосины» — труба другого исполнения), её свойства — чужого профиля и чужой длины. Раньше такая папка
    /// давала материалу позицию «вся деталь» с чужим профилем, книге — вторую заготовку («оценка»). Папки подсварок
    /// (SubWeldFolder) обходятся вглубь — их элементы раньше пропускались.
    /// </summary>
    internal static class CutListFolders
    {
        /// <summary>Папки списка вырезов (CutListFolder) с телами в активном исполнении, вместе с вложенными в подсварки.</summary>
        public static List<Feature> Active(ModelDoc2 model)
        {
            if (model == null) return new List<Feature>();
            try
            {
                // Фоновая перестройка дерева после открытия окна — обход повторяется целиком (FeatureWalk).
                return FeatureWalk.Retry(() =>
                {
                    List<Feature> folders = new List<Feature>();
                    for (Feature f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                        if (f.GetTypeName2() == "SolidBodyFolder") Walk(f, folders, 0);
                    return folders;
                }, "список вырезов");
            }
            catch (COMException ex)
            {
                Log.Error("Список вырезов: обход " + DocInfo.TitleOf(model), ex);
            }
            return new List<Feature>();
        }

        private static void Walk(Feature parent, List<Feature> folders, int depth)
        {
            if (depth > 8) return;
            for (Feature sub = parent.GetFirstSubFeature() as Feature; sub != null; sub = sub.GetNextSubFeature() as Feature)
            {
                string type = sub.GetTypeName2();
                if (type == "SubWeldFolder") Walk(sub, folders, depth + 1);
                else if (type == "CutListFolder" && BodyCount(sub) > 0) folders.Add(sub);
            }
        }

        /// <summary>Тел в папке в активном исполнении; не прочитать — 0 (папка не в счёт).</summary>
        public static int BodyCount(Feature folder)
        {
            try
            {
                BodyFolder bodies = folder.GetSpecificFeature2() as BodyFolder;
                return bodies != null ? bodies.GetBodyCount() : 0;
            }
            catch (COMException ex)
            {
                Log.Error("Список вырезов: тела папки", ex);
                return 0;
            }
        }

        /// <summary>Тела папки в активном исполнении.</summary>
        public static List<Body2> Bodies(Feature folder)
        {
            List<Body2> found = new List<Body2>();
            try
            {
                BodyFolder bodies = folder.GetSpecificFeature2() as BodyFolder;
                object[] list = bodies != null ? bodies.GetBodies() as object[] : null;
                if (list != null)
                    foreach (object o in list)
                    {
                        Body2 body = o as Body2;
                        if (body != null) found.Add(body);
                    }
            }
            catch (COMException ex)
            {
                Log.Error("Список вырезов: тела папки", ex);
            }
            return found;
        }
    }
}
