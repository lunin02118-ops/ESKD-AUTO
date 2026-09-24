using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>Состояние экземпляра компонента в сборке.</summary>
    internal static class ComponentState
    {
        /// <summary>
        /// Погашен конструктором — в изделие не входит. Скрытый компонент сборки, открытой без скрытых компонентов
        /// («Не загружать скрытые компоненты»), SolidWorks 2025 тоже отдаёт погашенным (GetSuppression2 = 0, IsSuppressed),
        /// но не загруженным (IsLoaded = false; у погашенного — true, проба 23.09.2026). Такой не погашен, а без модели в
        /// памяти: выгрузка, проверка и ЛЗК называют его «модель не загружена», а раньше молча отбрасывали (сверка SW API
        /// 23.09.2026, №20; e2e X17).
        /// </summary>
        internal static bool Suppressed(Component2 comp)
        {
            return comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed && comp.IsLoaded();
        }
    }
}
