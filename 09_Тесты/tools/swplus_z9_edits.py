# -*- coding: utf-8 -*-
"""Правка SpecEditor по замечанию З-9, применяется vba_patch.py.

    python 09_Тесты/tools/swplus_z9_edits.py            — пробная проверка
    python 09_Тесты/tools/swplus_z9_edits.py --apply    — архив прежнего .swp в 99_Архив/SWPlus_до_шага4, правка

З-9: графа «Наименование» спецификации связана с «Наименованием» детали — всё, что SpecEditor пишет в её ячейку,
SolidWorks пишет в модель. Раскладка записи БЧ по строкам (SwpLayoutRecords, WP-4.3) писала в эту ячейку первую строку
записи, и у детали пропадала запись; надстройка при сохранении её восстанавливала, и таблица двоилась.
Теперь у детали БЧ «Наименование» — одно название, остальные строки записи — свойство модели «Запись_БЧ» (надстройка,
BchRecord.LinesProperty). SpecEditor читает их временной графой, связанной с «Запись_БЧ», как графы «Раздел» и «Группа»
при чтении таблицы, а в связанную ячейку «Наименования» пишет только то, чего в ней ещё нет, — то есть ничего.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import swplus_step4_edits as base  # noqa: E402

HELPERS = """
' ===== Правки ЕСКД: З-9 — SpecEditor не переписывает свойства модели =====

' Строки записи БЧ после наименования по строкам таблицы — свойство модели «Запись_БЧ». Графа «Наименование» связана с
' «Наименованием» детали, и всё, что пишется в её ячейку, SolidWorks пишет в модель; поэтому у детали БЧ там одно название,
' а остальные строки записи читает временная графа, связанная с «Запись_БЧ», — как графы «Раздел» и «Группа» при чтении
' таблицы.
Private Function SwpExtraLines(ByVal swTable As Object) As Variant
    Dim v() As String
    Dim i As Long, n As Long
    Dim ok As Boolean
    n = swTable.ColumnCount
    ReDim v(swTable.RowCount)
    ok = swTable.InsertColumn(swTableItemInsertPosition_Last, 0, "Запись_БЧ")
    ok = swTable.SetColumnType(n, swWeldTableColumnType_CustomProperty)
    ok = swTable.SetColumnCustomProperty(n, "Запись_БЧ")
    For i = 1 To swTable.RowCount - 1
        v(i) = Replace(swTable.Text(i, n), Chr$(13), "")
    Next i
    ok = swTable.DeleteColumn(n)
    SwpExtraLines = v
End Function

' Текст записи строки таблицы: наименование и строки «Запись_БЧ»; запись прежних версий — вся в наименовании.
Private Function SwpRecordText(ByVal sName As String, ByVal sExtra As String) As String
    SwpRecordText = Replace(sName, Chr$(13), "")
    If Len(sExtra) > 0 And InStr(SwpRecordText, Chr$(10)) = 0 Then SwpRecordText = SwpRecordText & Chr$(10) & sExtra
End Function
"""

EDITS = {
    "SpecEditor/SpecEditor.swp": {
        "SpecEditor_run": [
            ("    Dim vLines As Variant\n    Dim swFormat As Object\n",
             "    Dim vLines As Variant, vExtra As Variant\n    Dim swFormat As Object\n"),
            ("""    For i = swTable.RowCount - 1 To 1 Step -1
        sText = Replace(swTable.Text(i, nColName), Chr$(13), "")""",
             """    vExtra = SwpExtraLines(swTable): For i = swTable.RowCount - 1 To 1 Step -1 ' ЕСКД З-9: строки записи БЧ — из модели
        sText = SwpRecordText(swTable.Text(i, nColName), vExtra(i))"""),
            ("' но не меньше 0,8. В ячейку возвращается сама строка.\n",
             "' но не меньше 0,8. В ячейку возвращается сама строка; связанная ячейка, где она уже есть, не переписывается (З-9).\n"),
            ("    swTable.Text(r, c) = sProbe\n",
             "    If swTable.Text(r, c) <> sProbe Then swTable.Text(r, c) = sProbe\n"),
            ("    swTable.Text(r, c) = sLine\n",
             "    If swTable.Text(r, c) <> sLine Then swTable.Text(r, c) = sLine\n"),
            (None, HELPERS),
        ],
    },
}


def main():
    return base.run(EDITS, base.ARCHIVE, sys.argv)


if __name__ == "__main__":
    sys.exit(main())
