# -*- coding: utf-8 -*-
"""Правки SpecEditor и Master пакета WP-4.3 плана согласования (Д-16, Р-15, спайк S-2f), применяются vba_patch.py.

    Накладывается вместе с остальными правками: python 09_Тесты/tools/swplus_apply_all.py [--apply]

* SpecEditor: формат сборочной единицы «А4» кириллицей (FrmSpecEditor:1971–1972); запись в несколько строк (деталь БЧ)
  оформляется строго по ГОСТ Р 2.105-2019 п. 7.4–7.5 и рис. 15 проекта ГОСТ Р 2.109 — процедура SwpLayoutRecords в
  конце SpecEditor_run (её же вызывает автотест), вызов — после оформления строк (FrmSpecEditor:3083).
* Master: в надписи «Формат …» буква кириллицей (FrmMaster:250, 336); списки и имена файлов шаблонов остаются латиницей.
Перекомпиляция проекта из исходника снимает расхождение исходника и P-code SpecEditor (Н-17).
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import swplus_step4_edits as base  # noqa: E402

LAYOUT = """
' ===== Правки ЕСКД: план согласования надстройки ЕСКД с SWPlus, WP-4.3 (Р-15, спайк S-2f) =====

' Запись в несколько строк (деталь БЧ: наименование, дробь материала, размер) — по ГОСТ Р 2.105-2019 п. 7.4–7.5 и
' рис. 15 проекта ГОСТ Р 2.109: каждая строка записи — строка таблицы 8 мм; дробь — объединённая ячейка «Наименование»
' на две строки с интервалом 2,9 мм (черта дроби на границе строк); текст по середине строки; при выходе за графу ширина
' шрифта сжимается до 0,8; «Кол.» и «Примечание» — в последней строке записи; позиция и обозначение — в первой.
Public Sub SwpLayoutRecords(ByVal swTable As Object, ByVal nColDesignation As Integer, ByVal nColName As Integer, _
    ByVal nColQty As Integer, ByVal nColRemark As Integer, ByVal nColPos As Integer)
    Dim i As Long, r As Long, k As Long, c As Long, j As Long, n As Long, nTotal As Long
    Dim sText As String, sQty As String, sRemark As String
    Dim vLines As Variant
    Dim swFormat As Object
    Dim dWidth As Double
    Dim ok As Boolean
    For i = swTable.RowCount - 1 To 1 Step -1
        sText = Replace(swTable.Text(i, nColName), Chr$(13), "")
        If InStr(sText, Chr$(10)) > 0 Then
            vLines = Split(sText, Chr$(10))
            nTotal = 0
            For k = LBound(vLines) To UBound(vLines)
                nTotal = nTotal + SwpTableRows(vLines(k))
            Next k
            sQty = swTable.Text(i, nColQty)
            sRemark = swTable.Text(i, nColRemark)
            For k = 1 To nTotal - 1
                ok = swTable.InsertRow(swTableItemInsertPosition_After, i)
            Next k
            r = i
            For k = LBound(vLines) To UBound(vLines)
                n = SwpTableRows(vLines(k))
                For c = 0 To n - 1
                    ok = swTable.SetRowVerticalGap(r + c, 0)
                    For j = 0 To swTable.ColumnCount - 1
                        Set swFormat = swTable.GetCellTextFormat(i, j)
                        swFormat.LineSpacing = 0.0044
                        ok = swTable.SetCellTextFormat(r + c, j, False, swFormat)
                        If j = nColDesignation Or j = nColName Or j = nColRemark Then
                            swTable.CellTextHorizontalJustification(r + c, j) = swTextJustificationLeft
                        Else
                            swTable.CellTextHorizontalJustification(r + c, j) = swTextJustificationCenter
                        End If
                        swTable.CellTextVerticalJustification(r + c, j) = swTextAlignmentMiddle
                    Next j
                    If r + c <> i Then swTable.Text(r + c, nColPos) = " "
                Next c
                dWidth = SwpFitWidth(swTable, r, nColName, CStr(vLines(k)))
                If n = 2 Then
                    ok = swTable.MergeCells(r, nColName, r + 1, nColName)
                    Set swFormat = swTable.GetCellTextFormat(r, nColName)
                    swFormat.LineSpacing = 0.0029
                    swFormat.WidthFactor = dWidth
                    ok = swTable.SetCellTextFormat(r, nColName, False, swFormat)
                    swTable.CellTextHorizontalJustification(r, nColName) = swTextJustificationLeft
                    swTable.CellTextVerticalJustification(r, nColName) = swTextAlignmentMiddle
                End If
                For c = 0 To n - 1
                    swTable.SetRowHeight r + c, 0.008, swTableRowColChange_TableSizeCanChange
                Next c
                r = r + n
            Next k
            If nTotal > 1 Then
                swTable.Text(i + nTotal - 1, nColQty) = sQty
                swTable.Text(i + nTotal - 1, nColRemark) = sRemark
                swTable.Text(i, nColQty) = " "
                swTable.Text(i, nColRemark) = " "
            End If
        End If
    Next i
End Sub

' Строк таблицы на строку записи: дробь — две, иначе одна.
Private Function SwpTableRows(ByVal sLine As String) As Long
    If InStr(1, sLine, "<STACK", vbTextCompare) > 0 Then SwpTableRows = 2 Else SwpTableRows = 1
End Function

' Ширина шрифта строки записи: самая длинная часть (у дроби — вид сортамента и длинный из числителя и знаменателя)
' пишется простым текстом в однострочную ячейку; пока строка переносится (высота больше 8 мм) — ширина меньше на 0,05,
' но не меньше 0,8. В ячейку возвращается сама строка.
Private Function SwpFitWidth(ByVal swTable As Object, ByVal r As Long, ByVal c As Long, ByVal sLine As String) As Double
    Dim sProbe As String, sPart As String, sPrefix As String
    Dim vParts As Variant
    Dim k As Long, p As Long
    Dim swFormat As Object
    Dim dWidth As Double, dBase As Double
    Dim ok As Boolean
    p = InStr(1, sLine, "<STACK", vbTextCompare)
    If p > 0 Then
        sPrefix = Left$(sLine, p - 1)
        vParts = Split(Replace(Replace(Mid$(sLine, InStr(p, sLine, ">") + 1), "</STACK>", ""), "<OVER>", Chr$(1)), Chr$(1))
        For k = LBound(vParts) To UBound(vParts)
            If Len(Trim$(vParts(k))) > Len(sPart) Then sPart = Trim$(vParts(k))
        Next k
        sProbe = sPrefix & sPart
    Else
        sProbe = sLine
    End If
    swTable.Text(r, c) = sProbe
    Set swFormat = swTable.GetCellTextFormat(r, c)
    dBase = swFormat.WidthFactor
    SwpFitWidth = 0.8
    For dWidth = dBase To 0.7999 Step -0.05
        swFormat.WidthFactor = dWidth
        ok = swTable.SetCellTextFormat(r, c, False, swFormat)
        If swTable.SetRowHeight(r, 0.008, swTableRowColChange_TableSizeCanChange) <= 0.0081 Then
            SwpFitWidth = dWidth
            Exit For
        End If
    Next dWidth
    swTable.Text(r, c) = sLine
    swFormat.WidthFactor = SwpFitWidth
    ok = swTable.SetCellTextFormat(r, c, False, swFormat)
End Function

' Автотест WP-4.3: оформить первую таблицу активного чертежа (столбцы шаблона SpecEditor_sp).
' Ошибка не останавливает макрос, а записывается в свойство чертежа «SwpLayoutError» — его читает тест.
Public Sub swp_layout_active_table()
    Dim swDoc As Object, swDrwView As Object, vTables As Variant
    On Error GoTo Failed
    Set swDoc = Application.SldWorks.ActiveDoc
    Set swDrwView = swDoc.GetFirstView
    Do While Not swDrwView Is Nothing
        If swDrwView.GetTableAnnotationCount > 0 Then
            vTables = swDrwView.GetTableAnnotations
            SwpLayoutRecords vTables(0), 3, 4, 5, vTables(0).ColumnCount - 1, 2
            Exit Sub
        End If
        Set swDrwView = swDrwView.GetNextView
    Loop
    Err.Raise 1001, , "таблица не найдена"
Failed:
    swDoc.AddCustomInfo3 "", "SwpLayoutError", 30, ""
    swDoc.CustomInfo2("", "SwpLayoutError") = Err.Number & ": " & Err.Description
End Sub
"""

EDITS = {
    "SpecEditor/SpecEditor.swp": {
        "FrmSpecEditor": [
            ("""                        sSpecData(k, 0) = "A4"
                        swTable.Text(k1 + 1, 0) = "A4\"""",
             """                        sSpecData(k, 0) = "А4" ' ЕСКД Д-16: кириллицей
                        swTable.Text(k1 + 1, 0) = "А4\""""),
            ("""    End If
Next i

' Перенос раздела Электромонтаж на новый лист для первого листа""",
             """    End If
Next i
If CboType.ListIndex = 0 Or CboType.ListIndex = 1 Then SwpLayoutRecords swTable, 3, 4, 5, nNumColumn - 1, 2 ' ЕСКД WP-4.3: запись БЧ по ГОСТ (Р-15, S-2f)
' Перенос раздела Электромонтаж на новый лист для первого листа"""),
        ],
        "SpecEditor_run": [(None, LAYOUT)],
    },
    "Master/Master.swp": {
        "FrmMaster": [
            # строки 250 и 336 одинаковы — различаются отступом следующей пустой строки
            ('''swNote.SetText "Формат " & FormatText
        
''', '''swNote.SetText "Формат " & Replace(FormatText, "A", "А") ' ЕСКД Д-16: буква формата кириллицей
        
'''),
            ('''swNote.SetText "Формат " & FormatText
    
''', '''swNote.SetText "Формат " & Replace(FormatText, "A", "А") ' ЕСКД Д-16: буква формата кириллицей
    
'''),
        ],
    },
}


def main():
    return base.run(EDITS, base.ARCHIVE, sys.argv)


if __name__ == "__main__":
    sys.exit(main())
