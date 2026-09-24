using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Граничная рамка развёртки по готовому DXF (ТЗ-02 Т-28): наименьший прямоугольник, в который входит весь контур
    /// детали, — так её считает и SolidWorks («Длина/Ширина граничной рамки» списка вырезов). Свойства граничной рамки
    /// есть не у всех деталей (боевой заказ NC3-7R: их нет вовсе) и устаревают, если список вырезов не обновлён, а DXF
    /// снят с модели только что — по нему и меряем.
    ///
    /// Сущности разбираются по типу (справка формата DXF): у дуги и окружности коды 10/20 — центр, рамку дают точки самой
    /// дуги; у эллипса 11/21 — вектор большой оси от центра; у сплайна 10/20 — управляющие точки, а 12/22 и 13/23 —
    /// касательные; у полилинии 42 — выпуклость дуги между вершинами. Дуги, окружности и полилинии заданы в своей системе
    /// координат (код 230 = −1 — зеркально по X). Развёртку SolidWorks кладёт в DXF так, как деталь лежит в модели, —
    /// наискось, если деталь построена под углом, поэтому рамка берётся не по осям DXF, а наименьшая по площади.
    ///
    /// Прежний замер брал все коды 10–23 как точки и рамку по осям: у круглой детали — «развёртка пустая» и DXF не
    /// делался, у планки с полукруглыми торцами длина без радиусов (80х200 вместо 80х280), пластина 200х100 под 30° —
    /// 187х223 (проверено на SolidWorks 2025; сверка с практиками API 23.09.2026, находка 13; замечание владельца
    /// 23.09.2026: брать по внешней границе, с радиусами).
    /// </summary>
    public static class DxfFrame
    {
        /// <summary>Наибольшее отклонение ломаной, которой заменяется кривая, от самой кривой, мм.</summary>
        private const double Chord = 0.001;
        private const int MaxPointsPerCurve = 20000;
        private const int SplineSamplesPerSpan = 64;
        private const int MaxInsertDepth = 8;
        private const int MaxInsertCopies = 10000;

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

        /// <summary>Ширина — меньшая сторона рамки, длина — большая: так технолог и подбирает лист. false — контура нет.</summary>
        public static bool Measure(TextReader reader, out double widthMm, out double lengthMm)
        {
            widthMm = 0;
            lengthMm = 0;
            Drawing drawing = Read(reader);
            List<Pt> points = new List<Pt>();
            Emit(drawing, drawing.Entities, Affine.Identity, points, 0);
            points.RemoveAll(p => double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y));
            if (points.Count < 2) return false;
            double a, b;
            if (!SmallestRectangle(points, out a, out b)) return false;
            widthMm = Math.Min(a, b) * drawing.Scale;
            lengthMm = Math.Max(a, b) * drawing.Scale;
            return widthMm > 0 && lengthMm > 0;
        }

        // ------------------------------------------------------------------ чтение файла

        private struct Pt
        {
            public readonly double X, Y;

            public Pt(double x, double y)
            {
                X = x;
                Y = y;
            }
        }

        private sealed class Entity
        {
            public readonly string Type;
            public readonly List<int> Codes = new List<int>();
            public readonly List<string> Values = new List<string>();

            public Entity(string type)
            {
                Type = type;
            }

            public double Number(int code, double fallback)
            {
                for (int i = 0; i < Codes.Count; i++)
                    if (Codes[i] == code) return Parse(Values[i], fallback);
                return fallback;
            }

            public string Text(int code)
            {
                for (int i = 0; i < Codes.Count; i++)
                    if (Codes[i] == code) return Values[i];
                return "";
            }

            public List<double> All(int code)
            {
                List<double> result = new List<double>();
                for (int i = 0; i < Codes.Count; i++)
                    if (Codes[i] == code) result.Add(Parse(Values[i], 0));
                return result;
            }

            /// <summary>Точки, заданные парами кодов (x, x + 10), в порядке записи: вершины полилинии, точки сплайна.</summary>
            public List<Pt> Pairs(int xCode)
            {
                List<Pt> result = new List<Pt>();
                double x = 0;
                bool open = false;
                for (int i = 0; i < Codes.Count; i++)
                {
                    if (Codes[i] == xCode)
                    {
                        if (open) result.Add(new Pt(x, 0));
                        x = Parse(Values[i], 0);
                        open = true;
                    }
                    else if (Codes[i] == xCode + 10 && open)
                    {
                        result.Add(new Pt(x, Parse(Values[i], 0)));
                        open = false;
                    }
                }
                if (open) result.Add(new Pt(x, 0));
                return result;
            }
        }

        private sealed class Block
        {
            public string Name = "";
            public double BaseX, BaseY;
            public readonly List<Entity> Entities = new List<Entity>();
        }

        private sealed class Drawing
        {
            public readonly List<Entity> Entities = new List<Entity>();
            public readonly Dictionary<string, Block> Blocks = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Миллиметров в единице чертежа ($INSUNITS); SolidWorks для детали в мм пишет 4 — мм.</summary>
            public double Scale = 1;
        }

        private static Drawing Read(TextReader reader)
        {
            Drawing drawing = new Drawing();
            string section = "";
            bool sectionName = false;
            string variable = "";
            Block block = null;
            Entity current = null;
            string codeLine;
            while ((codeLine = reader.ReadLine()) != null)
            {
                string value = reader.ReadLine();
                if (value == null) break;
                int code;
                if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) continue;
                value = value.Trim();
                if (code == 0)
                {
                    current = null;
                    sectionName = false;
                    if (value == "SECTION")
                    {
                        sectionName = true;
                        continue;
                    }
                    if (value == "ENDSEC")
                    {
                        section = "";
                        block = null;
                        continue;
                    }
                    if (value == "EOF") break;
                    if (section == "BLOCKS")
                    {
                        if (value == "BLOCK") block = new Block();
                        else if (value == "ENDBLK")
                        {
                            if (block != null && block.Name.Length > 0) drawing.Blocks[block.Name] = block;
                            block = null;
                        }
                        else if (block != null)
                        {
                            current = new Entity(value);
                            block.Entities.Add(current);
                        }
                        continue;
                    }
                    // Меряем только тело чертежа: в HEADER лежат служебные точки вроде $EXTMIN.
                    if (section == "ENTITIES")
                    {
                        current = new Entity(value);
                        drawing.Entities.Add(current);
                    }
                    continue;
                }
                if (sectionName)
                {
                    if (code == 2) section = value;
                    sectionName = false;
                    continue;
                }
                if (section == "HEADER")
                {
                    if (code == 9) variable = value;
                    else if (code == 70 && variable == "$INSUNITS") drawing.Scale = UnitScale(value);
                    continue;
                }
                if (current != null)
                {
                    current.Codes.Add(code);
                    current.Values.Add(value);
                }
                else if (block != null)
                {
                    // Заголовок блока: имя и базовая точка.
                    if (code == 2) block.Name = value;
                    else if (code == 10) block.BaseX = Parse(value, 0);
                    else if (code == 20) block.BaseY = Parse(value, 0);
                }
            }
            return drawing;
        }

        private static double UnitScale(string value)
        {
            int units;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out units)) return 1;
            switch (units)
            {
                case 1: return 25.4;     // дюймы
                case 2: return 304.8;    // футы
                case 5: return 10;       // сантиметры
                case 6: return 1000;     // метры
                case 13: return 0.001;   // микроны
                case 14: return 100;     // дециметры
                default: return 1;       // 4 — миллиметры, 0 — без единиц: мм, как в ТЗ
            }
        }

        private static double Parse(string value, double fallback)
        {
            double result;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        // ------------------------------------------------------------------ геометрия

        /// <summary>Плоское аффинное преобразование: x' = A·x + B·y + E, y' = C·x + D·y + F.</summary>
        private struct Affine
        {
            public double A, B, C, D, E, F;

            public static readonly Affine Identity = new Affine { A = 1, D = 1 };

            public Pt Apply(double x, double y)
            {
                return new Pt(A * x + B * y + E, C * x + D * y + F);
            }

            public Pt Linear(double x, double y)
            {
                return new Pt(A * x + B * y, C * x + D * y);
            }

            /// <summary>Сначала это преобразование, потом <paramref name="outer"/>.</summary>
            public Affine Then(Affine outer)
            {
                return new Affine
                {
                    A = outer.A * A + outer.B * C,
                    B = outer.A * B + outer.B * D,
                    C = outer.C * A + outer.D * C,
                    D = outer.C * B + outer.D * D,
                    E = outer.A * E + outer.B * F + outer.E,
                    F = outer.C * E + outer.D * F + outer.F
                };
            }
        }

        /// <summary>
        /// Система координат объекта (OCS) по нормали 210/220/230 — «произвольная ось» формата DXF, спроецированная на XY.
        /// Нормаль (0, 0, −1), которую SolidWorks пишет у части дуг, зеркалит X.
        /// </summary>
        private static Affine Ocs(Entity e, double elevation)
        {
            double nx = e.Number(210, 0), ny = e.Number(220, 0), nz = e.Number(230, 1);
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-12) return Affine.Identity;
            nx /= length;
            ny /= length;
            nz /= length;
            double ax, ay, az;
            if (Math.Abs(nx) < 1.0 / 64 && Math.Abs(ny) < 1.0 / 64)
            {
                ax = nz;    // Wy × N
                ay = 0;
                az = -nx;
            }
            else
            {
                ax = -ny;   // Wz × N
                ay = nx;
                az = 0;
            }
            double la = Math.Sqrt(ax * ax + ay * ay + az * az);
            ax /= la;
            ay /= la;
            az /= la;
            double bx = ny * az - nz * ay, by = nz * ax - nx * az, bz = nx * ay - ny * ax;   // N × Ax
            double lb = Math.Sqrt(bx * bx + by * by + bz * bz);
            bx /= lb;
            by /= lb;
            return new Affine { A = ax, B = bx, C = ay, D = by, E = elevation * nx, F = elevation * ny };
        }

        private static void Emit(Drawing drawing, List<Entity> entities, Affine t, List<Pt> points, int depth)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                Entity e = entities[i];
                switch (e.Type)
                {
                    case "LINE":
                        points.Add(t.Apply(e.Number(10, 0), e.Number(20, 0)));
                        points.Add(t.Apply(e.Number(11, 0), e.Number(21, 0)));
                        break;
                    case "ARC":
                    case "CIRCLE":
                    {
                        double r = Math.Abs(e.Number(40, 0));
                        bool arc = e.Type == "ARC";
                        double start = arc ? Radians(e.Number(50, 0)) : 0;
                        double sweep = arc ? Sweep(start, Radians(e.Number(51, 360))) : 2 * Math.PI;
                        Conic(Ocs(e, e.Number(30, 0)).Then(t), e.Number(10, 0), e.Number(20, 0), r, 0, 0, r, start, sweep, points);
                        break;
                    }
                    case "ELLIPSE":
                    {
                        // Центр и ось — в мировых координатах; малая полуось = отношение · (нормаль × большая полуось).
                        double mx = e.Number(11, 0), my = e.Number(21, 0), mz = e.Number(31, 0);
                        double nx = e.Number(210, 0), ny = e.Number(220, 0), nz = e.Number(230, 1);
                        double ratio = e.Number(40, 1);
                        double kx = ratio * (ny * mz - nz * my), ky = ratio * (nz * mx - nx * mz);
                        double start = e.Number(41, 0);
                        Conic(t, e.Number(10, 0), e.Number(20, 0), mx, my, kx, ky, start, Sweep(start, e.Number(42, 2 * Math.PI)), points);
                        break;
                    }
                    case "LWPOLYLINE":
                        Polyline(LightVertices(e), ((int)e.Number(70, 0) & 1) != 0, Ocs(e, e.Number(38, 0)).Then(t), points);
                        break;
                    case "POLYLINE":
                    {
                        List<Vertex> vertices = new List<Vertex>();
                        int j = i + 1;
                        for (; j < entities.Count && entities[j].Type == "VERTEX"; j++)
                            vertices.Add(new Vertex(entities[j].Number(10, 0), entities[j].Number(20, 0), entities[j].Number(42, 0)));
                        if (j < entities.Count && entities[j].Type == "SEQEND") j++;
                        i = j - 1;
                        int flags = (int)e.Number(70, 0);
                        // 8 — пространственная полилиния, 16 и 64 — сети: вершины в мировых координатах, без дуг.
                        if ((flags & (8 | 16 | 64)) != 0)
                            foreach (Vertex v in vertices) points.Add(t.Apply(v.X, v.Y));
                        else
                            Polyline(vertices, (flags & 1) != 0, Ocs(e, e.Number(30, 0)).Then(t), points);
                        break;
                    }
                    case "SPLINE":
                        Spline(e, t, points);
                        break;
                    case "INSERT":
                        Insert(drawing, e, t, points, depth);
                        break;
                    // Текст, размеры, точки, штриховки — не контур детали.
                }
            }
        }

        private static double Radians(double degrees)
        {
            return degrees * Math.PI / 180;
        }

        /// <summary>
        /// Угол дуги от начала до конца против часовой, (0; 2π]; совпавшие начало и конец — полная окружность. Полный оборот
        /// с округлением записи (42 = 6.2831853072, 51 = 360.0000000001) — полный, а не дуга в 1e-11 (ревью 23.09.2026).
        /// </summary>
        private static double Sweep(double start, double end)
        {
            double sweep = (end - start) % (2 * Math.PI);
            if (sweep < 0) sweep += 2 * Math.PI;
            return sweep < 1e-8 || sweep > 2 * Math.PI - 1e-8 ? 2 * Math.PI : sweep;
        }

        /// <summary>
        /// Дуга окружности или эллипса c + a·cos θ + b·sin θ, θ от start на sweep, после преобразования t — точками не дальше
        /// <see cref="Chord"/> от кривой. Аффинный образ дуги — снова такая дуга, поэтому зеркало и поворот считаются точно.
        /// </summary>
        private static void Conic(Affine t, double cx, double cy, double ax, double ay, double bx, double by,
            double start, double sweep, List<Pt> points)
        {
            Pt c = t.Apply(cx, cy), a = t.Linear(ax, ay), b = t.Linear(bx, by);
            double radius = Math.Sqrt(a.X * a.X + a.Y * a.Y + b.X * b.X + b.Y * b.Y);
            int steps = 1;
            if (radius > Chord)
            {
                double step = 2 * Math.Acos(1 - Chord / radius);
                steps = (int)Math.Min(MaxPointsPerCurve, Math.Max(1, Math.Ceiling(sweep / step)));
            }
            for (int k = 0; k <= steps; k++)
            {
                double angle = start + sweep * k / steps;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                points.Add(new Pt(c.X + a.X * cos + b.X * sin, c.Y + a.Y * cos + b.Y * sin));
            }
        }

        private struct Vertex
        {
            public readonly double X, Y, Bulge;

            public Vertex(double x, double y, double bulge)
            {
                X = x;
                Y = y;
                Bulge = bulge;
            }
        }

        /// <summary>Вершины LWPOLYLINE: 10/20 — точка, 42 после неё — выпуклость дуги до следующей вершины.</summary>
        private static List<Vertex> LightVertices(Entity e)
        {
            List<Vertex> result = new List<Vertex>();
            double x = 0, y = 0, bulge = 0;
            bool open = false;
            for (int i = 0; i < e.Codes.Count; i++)
            {
                int code = e.Codes[i];
                if (code == 10)
                {
                    if (open) result.Add(new Vertex(x, y, bulge));
                    x = Parse(e.Values[i], 0);
                    y = 0;
                    bulge = 0;
                    open = true;
                }
                else if (code == 20 && open) y = Parse(e.Values[i], 0);
                else if (code == 42 && open) bulge = Parse(e.Values[i], 0);
            }
            if (open) result.Add(new Vertex(x, y, bulge));
            return result;
        }

        private static void Polyline(List<Vertex> vertices, bool closed, Affine t, List<Pt> points)
        {
            int count = vertices.Count;
            for (int k = 0; k < count; k++)
            {
                Vertex p = vertices[k];
                points.Add(t.Apply(p.X, p.Y));
                if (k == count - 1 && !closed) break;
                Vertex q = vertices[(k + 1) % count];
                if (Math.Abs(p.Bulge) > 1e-12) BulgeArc(p, q, t, points);
            }
        }

        /// <summary>
        /// Дуга полилинии от p к q: выпуклость = tg(θ/4), знак — направление (плюс — против часовой). Центр — на
        /// перпендикуляре к середине хорды на расстоянии хорда/2 · (1 − b²)/(2b).
        /// </summary>
        private static void BulgeArc(Vertex p, Vertex q, Affine t, List<Pt> points)
        {
            double dx = q.X - p.X, dy = q.Y - p.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-12) return;
            double b = p.Bulge;
            double h = chord / 2 * (1 - b * b) / (2 * b);
            double cx = (p.X + q.X) / 2 - dy / chord * h;
            double cy = (p.Y + q.Y) / 2 + dx / chord * h;
            double radius = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
            double sweep = 4 * Math.Atan(Math.Abs(b));
            // По часовой от p к q — то же, что против часовой от q к p.
            double start = b > 0 ? Math.Atan2(p.Y - cy, p.X - cx) : Math.Atan2(q.Y - cy, q.X - cx);
            Conic(t, cx, cy, radius, 0, 0, radius, start, sweep, points);
        }

        /// <summary>
        /// Сплайн (NURBS) — точками самой кривой по алгоритму де Бура. Управляющие точки лежат вне кривой, и рамка по ним
        /// была бы больше детали; они берутся, только если узлы записаны не по формату.
        /// </summary>
        private static void Spline(Entity e, Affine t, List<Pt> points)
        {
            int degree = (int)e.Number(71, 3);
            List<double> knots = e.All(40);
            List<double> weights = e.All(41);
            List<Pt> control = e.Pairs(10);
            if (degree < 1 || control.Count < degree + 1 || knots.Count != control.Count + degree + 1)
            {
                foreach (Pt p in control) points.Add(t.Apply(p.X, p.Y));
                foreach (Pt p in e.Pairs(11)) points.Add(t.Apply(p.X, p.Y));
                return;
            }
            bool rational = weights.Count == control.Count;
            double[] x = new double[degree + 1], y = new double[degree + 1], w = new double[degree + 1];
            for (int span = degree; span < control.Count; span++)
            {
                double u0 = knots[span], u1 = knots[span + 1];
                if (u1 - u0 <= 1e-12) continue;
                for (int s = 0; s <= SplineSamplesPerSpan; s++)
                {
                    double u = u0 + (u1 - u0) * s / SplineSamplesPerSpan;
                    for (int j = 0; j <= degree; j++)
                    {
                        Pt p = control[span - degree + j];
                        double weight = rational && weights[span - degree + j] > 0 ? weights[span - degree + j] : 1;
                        x[j] = p.X * weight;
                        y[j] = p.Y * weight;
                        w[j] = weight;
                    }
                    for (int r = 1; r <= degree; r++)
                    {
                        for (int j = degree; j >= r; j--)
                        {
                            double left = knots[j + span - degree], right = knots[j + 1 + span - r];
                            double alpha = right - left > 1e-15 ? (u - left) / (right - left) : 0;
                            x[j] = (1 - alpha) * x[j - 1] + alpha * x[j];
                            y[j] = (1 - alpha) * y[j - 1] + alpha * y[j];
                            w[j] = (1 - alpha) * w[j - 1] + alpha * w[j];
                        }
                    }
                    if (Math.Abs(w[degree]) > 1e-15) points.Add(t.Apply(x[degree] / w[degree], y[degree] / w[degree]));
                }
            }
        }

        /// <summary>Вставка блока: точка вставки 41/42 — масштаб, 50 — поворот, 70/71 и 44/45 — массив копий.</summary>
        private static void Insert(Drawing drawing, Entity e, Affine t, List<Pt> points, int depth)
        {
            Block block;
            if (depth >= MaxInsertDepth || !drawing.Blocks.TryGetValue(e.Text(2), out block)) return;
            double sx = e.Number(41, 1), sy = e.Number(42, 1);
            double angle = Radians(e.Number(50, 0));
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            int columns = Math.Max(1, (int)e.Number(70, 1)), rows = Math.Max(1, (int)e.Number(71, 1));
            double columnStep = e.Number(44, 0), rowStep = e.Number(45, 0);
            Affine ocs = Ocs(e, e.Number(30, 0)).Then(t);
            int copies = 0;
            for (int c = 0; c < columns; c++)
            {
                for (int r = 0; r < rows; r++)
                {
                    if (++copies > MaxInsertCopies) return;
                    // Точка блока p → вставка + поворот(масштаб · (p − база) + шаг массива).
                    double ox = -sx * block.BaseX + c * columnStep, oy = -sy * block.BaseY + r * rowStep;
                    Affine local = new Affine
                    {
                        A = cos * sx,
                        B = -sin * sy,
                        C = sin * sx,
                        D = cos * sy,
                        E = e.Number(10, 0) + cos * ox - sin * oy,
                        F = e.Number(20, 0) + sin * ox + cos * oy
                    };
                    Emit(drawing, block.Entities, local.Then(ocs), points, depth + 1);
                }
            }
        }

        // ------------------------------------------------------------------ наименьшая рамка

        /// <summary>
        /// Наименьшая рамка вокруг точек плоского контура xy = {x0, y0, x1, y1, …}: ширина — меньшая сторона. Та же, что у
        /// DXF, — для развёртки, измеренной по телу детали (ведомость ЛЗК без свойств граничной рамки).
        /// </summary>
        public static bool Smallest(IList<double> xy, out double width, out double length)
        {
            width = 0;
            length = 0;
            List<Pt> points = new List<Pt>();
            for (int i = 0; i + 1 < xy.Count; i += 2)
                if (!double.IsNaN(xy[i]) && !double.IsNaN(xy[i + 1]) && !double.IsInfinity(xy[i]) && !double.IsInfinity(xy[i + 1]))
                    points.Add(new Pt(xy[i], xy[i + 1]));
            double a, b;
            if (points.Count < 3 || !SmallestRectangle(points, out a, out b)) return false;
            width = Math.Min(a, b);
            length = Math.Max(a, b);
            return width > 0 && length > 0;
        }

        /// <summary>
        /// Наименьший по площади прямоугольник вокруг точек: одна его сторона лежит на стороне выпуклой оболочки
        /// (вращающиеся калиперы), поэтому достаточно перебрать стороны оболочки. Равные по площади — у прямоугольного
        /// треугольника рамка по катетам и по гипотенузе — решает близость к осям DXF: как деталь нарисована, так и
        /// меряется. false — точки на одной прямой.
        /// </summary>
        private static bool SmallestRectangle(List<Pt> points, out double side1, out double side2)
        {
            side1 = 0;
            side2 = 0;
            List<Pt> hull = Hull(points);
            if (hull.Count < 3) return false;
            double best = double.MaxValue, bestTilt = double.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                Pt p = hull[i], q = hull[(i + 1) % hull.Count];
                double ux = q.X - p.X, uy = q.Y - p.Y;
                double length = Math.Sqrt(ux * ux + uy * uy);
                if (length < 1e-12) continue;
                ux /= length;
                uy /= length;
                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (Pt h in hull)
                {
                    double u = h.X * ux + h.Y * uy, v = -h.X * uy + h.Y * ux;
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }
                double area = (maxU - minU) * (maxV - minV);
                // Наклон стороны к ближайшей оси: 0 — по оси, 1 — под 45°.
                double tilt = Math.Abs(2 * ux * uy);
                bool tie = Math.Abs(area - best) <= 1e-9 * Math.Max(area, best);
                if (tie ? tilt >= bestTilt : area > best) continue;
                best = area;
                bestTilt = tilt;
                side1 = maxU - minU;
                side2 = maxV - minV;
            }
            return best < double.MaxValue && side1 > 0 && side2 > 0;
        }

        /// <summary>Выпуклая оболочка (монотонная цепочка Эндрю), против часовой, без точек на сторонах.</summary>
        private static List<Pt> Hull(List<Pt> points)
        {
            List<Pt> sorted = new List<Pt>(points);
            sorted.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            List<Pt> hull = new List<Pt>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int k = 0; k < sorted.Count; k++)
                {
                    Pt p = pass == 0 ? sorted[k] : sorted[sorted.Count - 1 - k];
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            return hull;
        }

        private static double Cross(Pt o, Pt a, Pt b)
        {
            return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        }
    }
}
