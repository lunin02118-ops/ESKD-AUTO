# -*- coding: utf-8 -*-
"""Правка SpecEditor по замечанию владельца 19.09.2026: спецификация на листе сборочного чертежа — в левом нижнем углу.

    python 09_Тесты/tools/swplus_spec_anchor_edits.py            — пробная проверка
    python 09_Тесты/tools/swplus_spec_anchor_edits.py --apply    — архив прежнего .swp в 99_Архив/SWPlus_до_шага4, правка

SpecEditor вставляет спецификацию на лист сборки с привязкой к точке привязки BOM форматки (UseAnchor = True), правым
нижним углом. Все форматки SWPlus несли точку листа A4 — (205; 68) мм, над основной надписью: на A3 и больших листах
таблица вставала посреди листа и её двигали руками. Форматки исправлены (set_bom_anchor.py); у чертежа, сделанного на
прежней форматке, процедура SwpBomAnchor один раз переставляет точку привязки его листа тем же штатным способом —
точка эскиза форматки назначается точкой привязки BOM (ISheet.SetAsTableAnchor).
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import swplus_step4_edits as base  # noqa: E402

HELPERS = """
' ===== Правки ЕСКД: спецификация на листе сборочного чертежа — в левом нижнем углу (замечание владельца 19.09.2026) =====

' На листе шире 395 мм спецификация (185 мм) стоит слева от основной надписи: правым нижним углом в (205; 5) мм, то есть
' левым нижним углом в углу рамки (20; 5), и растёт вверх. Форматки SWPlus несли точку листа A4 (205; 68) — над основной
' надписью; у чертежа на прежней форматке точка привязки BOM его листа переставляется здесь один раз. Узкий лист (A4,
' A3 книжный) не меняется: там спецификация над основной надписью.
Public Sub SwpBomAnchor(ByVal swDraw As Object, ByVal swSheet As Object)
    Dim vProps As Variant, vPos As Variant
    Dim swAnchor As Object, swPoint As Object
    vProps = swSheet.GetProperties
    If vProps(5) < 0.395 Then Exit Sub
    Set swAnchor = swSheet.TableAnchor(swTableAnnotation_BillOfMaterials)
    If Not swAnchor Is Nothing Then
        vPos = swAnchor.Position
        If Abs(vPos(0) - 0.205) < 0.0005 And Abs(vPos(1) - 0.005) < 0.0005 Then Exit Sub
    End If
    swDraw.EditTemplate
    swDraw.SketchManager.AddToDB = True
    Set swPoint = swDraw.SketchManager.CreatePoint(0.205, 0.005, 0)
    swDraw.SketchManager.AddToDB = False
    swDraw.ClearSelection2 True
    If Not swPoint Is Nothing Then
        If swPoint.Select4(False, Nothing) Then swSheet.SetAsTableAnchor swTableAnnotation_BillOfMaterials
    End If
    swDraw.ClearSelection2 True
    swDraw.EditSheet
End Sub

' Автотест: точка привязки BOM текущего листа активного чертежа. Ошибка — в свойство чертежа «SwpAnchorError».
Public Sub swp_bom_anchor_active_sheet()
    Dim swDoc As Object
    On Error GoTo Failed
    Set swDoc = Application.SldWorks.ActiveDoc
    SwpBomAnchor swDoc, swDoc.GetCurrentSheet
    Exit Sub
Failed:
    swDoc.AddCustomInfo3 "", "SwpAnchorError", 30, ""
    swDoc.CustomInfo2("", "SwpAnchorError") = Err.Number & ": " & Err.Description
End Sub
"""

EDITS = {
    "SpecEditor/SpecEditor.swp": {
        "FrmSpecEditor": [
            ("""            vSheetProps = swSheet.GetProperties
            Set swBomTable = swView.InsertBomTable4(True, vSheetProps(5) - 0.005, 0.068,""",
             """            vSheetProps = swSheet.GetProperties: SwpBomAnchor swDraw, swSheet ' ЕСКД: спецификация в левом нижнем углу листа
            Set swBomTable = swView.InsertBomTable4(True, vSheetProps(5) - 0.005, 0.068,"""),
        ],
        "SpecEditor_run": [(None, HELPERS)],
    },
}


def main():
    return base.run(EDITS, base.ARCHIVE, sys.argv)


if __name__ == "__main__":
    sys.exit(main())
