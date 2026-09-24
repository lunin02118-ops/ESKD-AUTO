namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Размер развёртки листовой детали для ведомости ЛЗК: замер по развёрнутому телу или свойства граничной рамки
    /// списка вырезов. Здесь — выбор без SolidWorks, проверяемый юнит-тестами; замер и чтение свойств — в Sw/LzkService.cs.
    /// </summary>
    public static class FlatSize
    {
        /// <summary>
        /// {длина, ширина, толщина}, мм, или null. measured — замер по телу (null — не измерить), measureTried — модель
        /// своего заказа, её пробовали мерить. estimate — размер с пометкой «оценка».
        /// </summary>
        public static double[] Choose(double[] fromProperties, double[] measured, bool measureTried, out bool estimate)
        {
            estimate = false;
            if (measured != null) return measured;
            // Своя модель не измерилась (развёртка не перестроилась): свойства могли остаться от прежней геометрии.
            estimate = measureTried && fromProperties != null;
            return fromProperties;
        }
    }
}
