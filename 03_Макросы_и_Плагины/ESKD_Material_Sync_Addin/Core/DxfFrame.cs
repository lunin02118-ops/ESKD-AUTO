using System;
using System.Globalization;
using System.IO;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Рамка развёртки по готовому DXF (ТЗ-02 Т-28). Свойства «Bounding Box» в списке вырезов есть не у всех
    /// деталей (боевой заказ NC3-7R: их нет вовсе), а сам DXF всегда содержит координаты — по ним и меряем.
    /// Разбор простой: пары «код / значение», где 10, 11, 12, 13 — X, а 20, 21, 22, 23 — Y.
    /// </summary>
    public static class DxfFrame
    {
        public static bool Measure(string path, out double widthMm, out double lengthMm)
        {
            widthMm = 0;
            lengthMm = 0;
            try
            {
                using (StreamReader reader = new StreamReader(path))
                    return Measure(reader, out widthMm, out lengthMm);
            }
            catch (IOException ex)
            {
                Log.Error("Рамка развёртки: " + path, ex);
                return false;
            }
        }

        public static bool Measure(TextReader reader, out double widthMm, out double lengthMm)
        {
            widthMm = 0;
            lengthMm = 0;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            string line;
            bool entities = false;
            while ((line = reader.ReadLine()) != null)
            {
                string code = line.Trim();
                string value = reader.ReadLine();
                if (value == null) break;
                value = value.Trim();
                if (code == "2")
                {
                    // Меряем только тело чертежа: в HEADER лежат служебные точки вроде $EXTMIN.
                    if (value == "ENTITIES") entities = true;
                    else if (value == "OBJECTS" || value == "BLOCKS") entities = false;
                    continue;
                }
                if (!entities) continue;
                int number;
                if (!int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) continue;
                double coordinate;
                if ((number < 10 || number > 23) ||
                    !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate)) continue;
                if (number <= 13)
                {
                    if (coordinate < minX) minX = coordinate;
                    if (coordinate > maxX) maxX = coordinate;
                }
                else if (number >= 20 && number <= 23)
                {
                    if (coordinate < minY) minY = coordinate;
                    if (coordinate > maxY) maxY = coordinate;
                }
            }
            if (minX > maxX || minY > maxY) return false;
            double dx = maxX - minX;
            double dy = maxY - minY;
            // Ширина — меньшая сторона, длина — большая: так технолог и подбирает лист.
            widthMm = Math.Min(dx, dy);
            lengthMm = Math.Max(dx, dy);
            return widthMm > 0 && lengthMm > 0;
        }
    }
}
