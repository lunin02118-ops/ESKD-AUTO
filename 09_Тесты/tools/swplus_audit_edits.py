# -*- coding: utf-8 -*-
"""Правки SpecEditor и MProp по глубокому аудиту 19.09.2026 (М-В2…М-В6). Последний шаг цепочки swplus_apply_all.py.

* М-В2 SwpExtraLines: временная графа «Запись_БЧ» удаляется и при ошибке посреди чтения — в спецификации не остаётся
  лишней графы; ошибка передаётся дальше.
* М-В3 SwpFitWidth: пробная строка для подбора ширины шрифта пишется во временную строку таблицы, не связанную
  с моделью; ячейка «Наименования» детали пробного текста не получает (в свойство модели он не уходит).
* М-В4 SwpBomAnchor: точку привязки меняет только у листа без точки или с точкой прежних форматок SWPlus (205; 68) —
  точка, поставленная иначе, остаётся; правка основной надписи в обработчике ошибок (лист всегда выходит из режима
  редактирования основной надписи); повторная попытка берёт уже созданную точку, а не ставит вторую; неудача —
  предупреждение конструктору, а не молчание.
* М-В5 SwpLayoutRecords: графы «Обозначение», «Наименование», «Кол.», «Примечание», «Поз.» — по заголовку таблицы;
  номера граф шаблона SpecEditor_sp — только если заголовка нет (групповая спецификация: графы по исполнениям).
* М-В6 «БЧ» без учёта регистра и пробелов; «Запись_БЧ» — одна константа, коды документов — тот же список, что
  NameParsing.DocCodes надстройки (обе сверяет тест T0).
"""

SPEC_RUN = [
    # М-В6: имя свойства строк записи БЧ — одна константа модуля.
    # Вместо закомментированной строки SWPlus — номера строк модуля, на которые ссылаются документы, не сдвигаются.
    ("Option Explicit\n'Public swApp As Object\n",
     "Option Explicit\n"
     "Private Const prpRecordBch As String = \"Запись_БЧ\" ' ЕСКД З-9: = BchRecord.LinesProperty надстройки (сверяет тест T0)\n"),
    # М-В5: графы по заголовку.
    ("""    Dim ok As Boolean
    vExtra = SwpExtraLines(swTable): For i""",
     """    Dim ok As Boolean
    nColDesignation = SwpColumn(swTable, "ОБОЗНАЧ", nColDesignation) ' ЕСКД М-В5: графы — по заголовку таблицы
    nColName = SwpColumn(swTable, "НАИМЕН", nColName)
    nColQty = SwpColumn(swTable, "КОЛ", nColQty)
    nColRemark = SwpColumn(swTable, "ПРИМ", nColRemark)
    nColPos = SwpColumn(swTable, "ПОЗ", nColPos)
    vExtra = SwpExtraLines(swTable): For i"""),
    # М-В3: подбор ширины — во временной строке таблицы.
    ("""' пишется простым текстом в однострочную ячейку; пока строка переносится (высота больше 8 мм) — ширина меньше на 0,05,
' но не меньше 0,8. В ячейку возвращается сама строка; связанная ячейка, где она уже есть, не переписывается (З-9).""",
     """' пишется простым текстом во временную строку таблицы под строкой r; пока строка переносится (высота больше 8 мм) — ширина
' меньше на 0,05, но не меньше 0,8. Временная строка не связана с моделью: ячейка «Наименования» детали пробного текста не
' получает (аудит 19.09, М-В3) и удаляется и при ошибке. В ячейку r пишется сама строка, если её там ещё нет (З-9)."""),
    ("""    Dim ok As Boolean
    p = InStr(1, sLine, "<STACK", vbTextCompare)""",
     """    Dim ok As Boolean, bRow As Boolean
    Dim t As Long, nErr As Long, sErr As String
    p = InStr(1, sLine, "<STACK", vbTextCompare)"""),
    ("""    If swTable.Text(r, c) <> sProbe Then swTable.Text(r, c) = sProbe
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
    If swTable.Text(r, c) <> sLine Then swTable.Text(r, c) = sLine
    swFormat.WidthFactor = SwpFitWidth
    ok = swTable.SetCellTextFormat(r, c, False, swFormat)
End Function""",
     """    Set swFormat = swTable.GetCellTextFormat(r, c)
    dBase = swFormat.WidthFactor
    SwpFitWidth = 0.8
    On Error GoTo Failed
    bRow = swTable.InsertRow(swTableItemInsertPosition_After, r)
    If Not bRow Then Err.Raise vbObjectError + 514, "SwpFitWidth", "не удалось вставить временную строку таблицы"
    t = r + 1
    ok = swTable.SetRowVerticalGap(t, 0)
    swTable.Text(t, c) = sProbe
    For dWidth = dBase To 0.7999 Step -0.05
        swFormat.WidthFactor = dWidth
        ok = swTable.SetCellTextFormat(t, c, False, swFormat)
        If swTable.SetRowHeight(t, 0.008, swTableRowColChange_TableSizeCanChange) <= 0.0081 Then
            SwpFitWidth = dWidth
            Exit For
        End If
    Next dWidth
Cleanup:
    On Error GoTo 0
    If bRow Then ok = swTable.DeleteRow(t)
    If nErr <> 0 Then Err.Raise nErr, "SwpFitWidth", sErr
    If swTable.Text(r, c) <> sLine Then swTable.Text(r, c) = sLine
    swFormat.WidthFactor = SwpFitWidth
    ok = swTable.SetCellTextFormat(r, c, False, swFormat)
    Exit Function
Failed:
    nErr = Err.Number: sErr = Err.Description
    Resume Cleanup
End Function"""),
    # М-В2: временная графа удаляется и при ошибке.
    ("""' а остальные строки записи читает временная графа, связанная с «Запись_БЧ», — как графы «Раздел» и «Группа» при чтении
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
End Function""",
     """' а остальные строки записи читает временная графа, связанная с «Запись_БЧ», — как графы «Раздел» и «Группа» при чтении
' таблицы. Графа удаляется и при ошибке посреди чтения (аудит 19.09, М-В2), ошибка передаётся дальше.
Private Function SwpExtraLines(ByVal swTable As Object) As Variant
    Dim v() As String
    Dim i As Long, n As Long, nErr As Long
    Dim ok As Boolean, bAdded As Boolean
    Dim sErr As String
    n = swTable.ColumnCount
    ReDim v(swTable.RowCount)
    On Error GoTo Failed
    bAdded = swTable.InsertColumn(swTableItemInsertPosition_Last, 0, prpRecordBch)
    If Not bAdded Then Err.Raise vbObjectError + 515, "SwpExtraLines", "не удалось вставить временную графу «" & prpRecordBch & "»"
    ok = swTable.SetColumnType(n, swWeldTableColumnType_CustomProperty)
    ok = swTable.SetColumnCustomProperty(n, prpRecordBch)
    For i = 1 To swTable.RowCount - 1
        v(i) = Replace(swTable.Text(i, n), Chr$(13), "")
    Next i
Cleanup:
    On Error GoTo 0
    If bAdded Then ok = swTable.DeleteColumn(n)
    If nErr <> 0 Then Err.Raise nErr, "SwpExtraLines", sErr
    SwpExtraLines = v
    Exit Function
Failed:
    nErr = Err.Number: sErr = Err.Description
    Resume Cleanup
End Function"""),
    # М-В4: точка привязки спецификации.
    ("""' надписью; у чертежа на прежней форматке точка привязки BOM его листа переставляется здесь один раз. Узкий лист (A4,
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
End Sub""",
     """' надписью; у чертежа на прежней форматке точка привязки BOM его листа переставляется здесь один раз. Узкий лист (A4,
' A3 книжный) не меняется: там спецификация над основной надписью. Аудит 19.09, М-В4: точку меняет только у листа без
' точки или с точкой прежних форматок — другую точку поставили намеренно, она остаётся; лист всегда выходит из режима
' редактирования основной надписи; повторная попытка берёт уже созданную точку; неудача — ошибка с объяснением.
Public Sub SwpBomAnchor(ByVal swDraw As Object, ByVal swSheet As Object)
    Dim vProps As Variant, vPos As Variant
    Dim swAnchor As Object, swPoint As Object
    Dim bTemplate As Boolean, bSelected As Boolean
    Dim nErr As Long, sErr As String
    vProps = swSheet.GetProperties
    If vProps(5) < 0.395 Then Exit Sub
    Set swAnchor = swSheet.TableAnchor(swTableAnnotation_BillOfMaterials)
    If Not swAnchor Is Nothing Then
        vPos = swAnchor.Position
        If Not SwpAt(vPos, 0.205, 0.068) Then Exit Sub
    End If
    On Error GoTo Failed
    swDraw.EditTemplate
    bTemplate = True
    swDraw.ClearSelection2 True
    bSelected = swDraw.Extension.SelectByID2("", "SKETCHPOINT", 0.205, 0.005, 0, False, 0, Nothing, 0)
    If Not bSelected Then
        swDraw.SketchManager.AddToDB = True
        Set swPoint = swDraw.SketchManager.CreatePoint(0.205, 0.005, 0)
        swDraw.SketchManager.AddToDB = False
        swDraw.ClearSelection2 True
        If Not swPoint Is Nothing Then bSelected = swPoint.Select4(False, Nothing)
    End If
    If bSelected Then swSheet.SetAsTableAnchor swTableAnnotation_BillOfMaterials
Cleanup:
    On Error GoTo 0
    If bTemplate Then
        swDraw.SketchManager.AddToDB = False
        swDraw.ClearSelection2 True
        swDraw.EditSheet
    End If
    If nErr <> 0 Then Err.Raise nErr, "SwpBomAnchor", sErr
    Set swAnchor = swSheet.TableAnchor(swTableAnnotation_BillOfMaterials)
    If Not swAnchor Is Nothing Then vPos = swAnchor.Position
    If swAnchor Is Nothing Then
        Err.Raise vbObjectError + 513, "SwpBomAnchor", "не удалось поставить точку привязки спецификации (205; 5) мм"
    ElseIf Not SwpAt(vPos, 0.205, 0.005) Then
        Err.Raise vbObjectError + 513, "SwpBomAnchor", "не удалось поставить точку привязки спецификации (205; 5) мм"
    End If
    Exit Sub
Failed:
    nErr = Err.Number: sErr = Err.Description
    Resume Cleanup
End Sub"""),
    # Вспомогательные процедуры аудита — в конец модуля.
    (None, """
' ===== Правки ЕСКД: глубокий аудит 19.09.2026 (М-В4, М-В5) =====

' Точка vPos (м) совпадает с (x; y) с допуском 0,5 мм.
Private Function SwpAt(ByVal vPos As Variant, ByVal x As Double, ByVal y As Double) As Boolean
    SwpAt = Abs(vPos(0) - x) < 0.0005 And Abs(vPos(1) - y) < 0.0005
End Function

' Номер графы таблицы, заголовок которой начинается с sTitle (заглавными): без учёта регистра, пробелов, переносов и
' дефисов («Приме-чание»). Нет такой графы — nDefault, номер по шаблону SpecEditor_sp.
Private Function SwpColumn(ByVal swTable As Object, ByVal sTitle As String, ByVal nDefault As Integer) As Integer
    Dim c As Long
    Dim s As String
    SwpColumn = nDefault
    For c = 0 To swTable.ColumnCount - 1
        s = UCase$(swTable.GetColumnTitle(c))
        s = Replace(Replace(Replace(Replace(s, Chr$(13), ""), Chr$(10), ""), "-", ""), " ", "")
        If Left$(s, Len(sTitle)) = sTitle Then
            SwpColumn = c
            Exit Function
        End If
    Next c
End Function

' Точка привязки спецификации из формы SpecEditor: неудача — предупреждение конструктору, спецификация вставляется дальше.
Public Sub SwpBomAnchorWarn(ByVal swDraw As Object, ByVal swSheet As Object)
    Dim lAnswer As Long
    On Error GoTo Failed
    SwpBomAnchor swDraw, swSheet
    Exit Sub
Failed:
    lAnswer = Application.SldWorks.SendMsgToUser2("Спецификация: " & Err.Description & ". Проверьте положение таблицы на листе.", _
        swMbWarning, swMbOk)
End Sub
"""),
]

EDITS = {
    "SpecEditor/SpecEditor.swp": {
        "SpecEditor_run": SPEC_RUN,
        "FrmSpecEditor": [
            ("vSheetProps = swSheet.GetProperties: SwpBomAnchor swDraw, swSheet ' ЕСКД: спецификация в левом нижнем углу листа",
             "vSheetProps = swSheet.GetProperties: SwpBomAnchorWarn swDraw, swSheet ' ЕСКД: спецификация в левом нижнем углу листа"),
        ],
    },
    "MProp/MProp.swp": {
        "FrmMProp": [
            ('If Формат.Value = "БЧ" Then', 'If UCase$(Trim$(Формат.Value)) = "БЧ" Then ' + "' ЕСКД М-В6: без учёта регистра и пробелов"),
            ('    If Trim$(sFormat) = "БЧ" And InStr(sDesc, Chr$(10)) > 0 Then SwpBchRecord = sDesc',
             '    If UCase$(Trim$(sFormat)) = "БЧ" And InStr(sDesc, Chr$(10)) > 0 Then SwpBchRecord = sDesc ' + "' ЕСКД М-В6"),
            ("Private Function SwpIsDocCode(ByVal s As String) As Boolean\n",
             "Private Function SwpIsDocCode(ByVal s As String) As Boolean ' ЕСКД М-В6: список = NameParsing.DocCodes надстройки (тест T0)\n"),
        ],
    },
}
