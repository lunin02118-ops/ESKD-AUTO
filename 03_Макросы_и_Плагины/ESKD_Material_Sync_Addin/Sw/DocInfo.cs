using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Путь и заголовок документа без исключений. Ожидаемые отказы — только COM:
    /// документ уже выгружен (DestroyNotify) или SolidWorks занят; остальные ошибки не глотаются.
    /// </summary>
    internal static class DocInfo
    {
        public static string PathOf(ModelDoc2 doc)
        {
            if (doc == null) return "";
            try { return doc.GetPathName() ?? ""; }
            catch (COMException) { return ""; }
            catch (InvalidComObjectException) { return ""; }
        }

        public static string TitleOf(ModelDoc2 doc)
        {
            if (doc == null) return "";
            try { return doc.GetTitle() ?? ""; }
            catch (COMException) { return ""; }
            catch (InvalidComObjectException) { return ""; }
        }
    }
}
