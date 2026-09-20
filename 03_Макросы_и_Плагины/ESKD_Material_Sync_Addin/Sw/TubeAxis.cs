using System;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Т-29: труборезу нужен IGS, где ось профиля — ось X. Своей СК «Труба» конструкторы не строят (С-5: 0 из 89 деталей),
    /// поэтому на время выгрузки строится СК с началом в начале отрезка первого элемента конструкции (WeldMemberFeat)
    /// и осью X по нему, и назначается системой координат вывода документа. После выгрузки СК удаляется, прежняя
    /// настройка возвращается; модель, которая до выгрузки была сохранена, сохраняется снова — деталь остаётся как была.
    /// Нет элемента конструкции или отрезка — <see cref="Applied"/> = false, выгрузка идёт в глобальной СК.
    /// </summary>
    public sealed class TubeAxis : IDisposable
    {
        private const string Name = "_ЕСКД_ось_трубы";
        private const int OutputCoordinateSystem = (int)swUserPreferenceStringValue_e.swFileSaveAsCoordinateSystem;
        private const int DocumentLevel = (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified;

        private readonly ModelDoc2 model;
        private readonly bool wasDirty;
        private string previousOutput = "";
        private Feature feature;

        public bool Applied { get { return feature != null; } }
        /// <summary>Почему СК по оси не построена — для замечания в отчёте.</summary>
        public string Reason { get; private set; }

        private TubeAxis(ModelDoc2 model)
        {
            this.model = model;
            wasDirty = model.GetSaveFlag();
            Reason = "";
        }

        public static TubeAxis Create(ISldWorks app, ModelDoc2 model, string path)
        {
            TubeAxis axis = new TubeAxis(model);
            try
            {
                SketchLine line = FirstMemberLine(model);
                if (line == null)
                {
                    axis.Reason = "в детали нет элемента конструкции с прямым отрезком, ось трубы не найдена";
                    return axis;
                }
                // Выделение работает с активным документом: компонент сборки делается активным на время выгрузки.
                // Занятый SolidWorks отвечает отказом — тогда ось строилась бы по чужому документу, и IGS уезжал
                // в глобальной системе координат с отметкой «ok» (аудит 20.09.2026).
                int errors = 0;
                app.ActivateDoc3(path, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                ModelDoc2 active = app.ActiveDoc as ModelDoc2;
                string now = active != null ? active.GetPathName() ?? "" : "";
                if (!string.Equals(now, path, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("Выгрузка: деталь не стала активной (код " + errors + "), активен «" + now + "» вместо «" + path + "»");
                    axis.Reason = "SolidWorks не сделал деталь активной — ось трубы не построена";
                    return axis;
                }
                axis.Build(line);
            }
            catch (Exception ex)
            {
                Log.Error("Выгрузка: СК по оси трубы " + path, ex);
                axis.Reason = "СК по оси трубы не построена: " + ex.Message;
                axis.Remove();
            }
            return axis;
        }

        private static SketchLine FirstMemberLine(ModelDoc2 model)
        {
            for (Feature f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
            {
                if (f.GetTypeName2() != "WeldMemberFeat") continue;
                StructuralMemberFeatureData data = f.GetDefinition() as StructuralMemberFeatureData;
                object[] groups = data == null ? null : data.Groups as object[];
                if (groups == null || groups.Length == 0) continue;
                StructuralMemberGroup group = groups[0] as StructuralMemberGroup;
                object[] segments = group == null ? null : group.Segments as object[];
                if (segments == null || segments.Length == 0) continue;
                return segments[0] as SketchLine;
            }
            return null;
        }

        private void Build(SketchLine line)
        {
            // Начало — точка начала отрезка (отметка 1), ось X — сам отрезок (отметка 2): координаты считать не нужно,
            // SolidWorks сам переводит эскиз в пространство детали.
            model.ClearSelection2(true);
            SelectionMgr selection = (SelectionMgr)model.SelectionManager;
            SelectData origin = (SelectData)selection.CreateSelectData();
            origin.Mark = 1;
            SelectData xAxis = (SelectData)selection.CreateSelectData();
            xAxis.Mark = 2;
            SketchPoint start = (SketchPoint)line.GetStartPoint2();
            if (!start.Select4(true, origin) || !((SketchSegment)line).Select4(true, xAxis))
            {
                Reason = "отрезок элемента конструкции не выделяется";
                model.ClearSelection2(true);
                return;
            }
            feature = model.FeatureManager.InsertCoordinateSystem(false, false, false) as Feature;
            model.ClearSelection2(true);
            if (feature == null)
            {
                Reason = "SolidWorks не построил СК по отрезку элемента конструкции";
                return;
            }
            feature.Name = Name;
            previousOutput = model.Extension.GetUserPreferenceString(OutputCoordinateSystem, DocumentLevel) ?? "";
            if (!model.Extension.SetUserPreferenceString(OutputCoordinateSystem, DocumentLevel, feature.Name))
            {
                Reason = "СК вывода не назначена";
                Remove();
            }
        }

        private void Remove()
        {
            if (feature == null) return;
            try
            {
                model.Extension.SetUserPreferenceString(OutputCoordinateSystem, DocumentLevel, previousOutput);
                model.ClearSelection2(true);
                if (feature.Select2(false, 0)) model.EditDelete();
                model.ClearSelection2(true);
            }
            catch (COMException ex)
            {
                Log.Error("Выгрузка: удаление временной СК", ex);
            }
            finally
            {
                Marshal.ReleaseComObject(feature);
                feature = null;
            }
            if (wasDirty) return;
            int errors = 0, warnings = 0;
            if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                Log.Warn("Выгрузка: деталь после временной СК не сохранена (код " + errors + ")");
        }

        public void Dispose()
        {
            Remove();
        }
    }
}
