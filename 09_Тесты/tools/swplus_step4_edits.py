# -*- coding: utf-8 -*-
"""Правки макросов SWPlus шага 4 плана согласования (WP-3.1…3.6), применяются vba_patch.py.

    Накладывается вместе с остальными правками: python 09_Тесты/tools/swplus_apply_all.py [--apply]

Правки пишутся против текстовой выгрузки (_VBA_выгрузка): «было» должно встретиться ровно один раз.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import vba_patch  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
SWPLUS = ROOT / "03_Макросы_и_Плагины" / "Макросы_SW_ZTool" / "SWPlusMacro_v_2018_SP0.0"
ARCHIVE = ROOT / "99_Архив" / "SWPlus_до_шага4"

VIEW_MESSAGE = 'swApp.SendMsgToUser ("Не удалось определить вид из свойств листа. Используется первый вид.")'
VIEW_SILENT = "' ЕСКД WP-3.5 (S-8): вид пуст или не найден — первый вид листа, без окна"

HELPERS = """
' ===== Правки ЕСКД: план согласования надстройки ЕСКД с SWPlus, шаг 4 =====

' Разбор имени файла «Обозначение [код документа] Наименование», как DesignationParser надстройки (WP-3.3, WP-3.6):
' код документа отбрасывается; если в первом слове нет цифр, обозначения нет и всё имя — наименование.
Private Sub SwpSplitName(ByVal sTitle As String, ByRef sNum As String, ByRef sName As String)
    Dim p As Long
    Dim sWord As String
    sNum = ""
    sName = ""
    p = InStr(sTitle, prpNameSep)
    If p = 0 Then
        If SwpHasDigit(sTitle) Then sNum = sTitle Else sName = sTitle
        Exit Sub
    End If
    If Not SwpHasDigit(Left$(sTitle, p - 1)) Then
        sName = sTitle
        Exit Sub
    End If
    sNum = Trim$(Left$(sTitle, p - 1))
    sName = Trim$(Mid$(sTitle, p + Len(prpNameSep)))
    p = InStr(sName, " ")
    If p > 0 Then sWord = Left$(sName, p - 1) Else sWord = sName
    If SwpIsDocCode(sWord) Then sName = Trim$(Mid$(sName, Len(sWord) + 1))
    If Len(sNum) > 2 Then
        If SwpIsDocCode(Right$(sNum, 2)) And Mid$(sNum, Len(sNum) - 2, 1) Like "#" Then sNum = Left$(sNum, Len(sNum) - 2)
    End If
End Sub

Private Function SwpHasDigit(ByVal s As String) As Boolean
    SwpHasDigit = s Like "*#*"
End Function

Private Function SwpIsDocCode(ByVal s As String) As Boolean
    Select Case UCase$(s)
        Case "СБ", "ГЧ", "МЧ", "ВО", "ТУ", "ТБ", "ПЭ", "СХ", "СЭ", "ВП", "СП"
            SwpIsDocCode = True
        Case Else
            SwpIsDocCode = UCase$(s) Like "Э#"
    End Select
End Function

Private Function SwpNum(ByVal sTitle As String) As String
    Dim sNum As String
    Dim sName As String
    SwpSplitName sTitle, sNum, sName
    SwpNum = sNum
End Function

Private Function SwpName(ByVal sTitle As String) As String
    Dim sNum As String
    Dim sName As String
    SwpSplitName sTitle, sNum, sName
    SwpName = sName
End Function

' Запись БЧ документа, прочитанная при загрузке формы (WP-3.2); с аргументом — запомнить.
Private Function SwpBchState(Optional ByVal sSet As Variant) As String
    Static sRecord As String
    If Not IsMissing(sSet) Then sRecord = sSet
    SwpBchState = sRecord
End Function

' «Наименование» для сравнения с именем файла: у детали БЧ — первая строка записи (WP-3.2).
Private Function SwpDescriptionForName() As String
    If SwpBchState() <> "" Then
        SwpDescriptionForName = SwpFirstLine(SwpBchState())
    Else
        SwpDescriptionForName = swModel.CustomInfo(prpDescription)
    End If
End Function

' Значение поля «Наименование»: у детали БЧ — запись целиком, многострочный ввод (WP-3.2).
Private Function SwpFormValue(ByVal sValue As String) As String
    If SwpBchState() <> "" Then
        ChkEnter.Value = True
        SwpFormValue = SwpBchState()
    Else
        SwpFormValue = sValue
    End If
End Function

' Текст графы 1: у записи БЧ — первая строка; одна строка шрифтом 5 мм переносится по словам (WP-3.1, WP-3.2).
Private Function SwpStampText() As String
    Dim t As String
    t = Наименование.Value
    If SwpBchState() <> "" And ChkEnter.Value = True Then t = SwpFirstLine(t)
    If InStr(t, Chr$(10)) = 0 And ChkFont.Value = False Then t = SwpWrapTitle(t)
    SwpStampText = t
End Function

' Запись детали БЧ в «Наименовании» уровня sConf (WP-3.2): «Формат» = «БЧ» и запись в несколько строк; иначе пусто.
Private Function SwpBchRecord(ByVal sConf As String) As String
    Dim sFormat As String
    Dim sDesc As String
    sFormat = swModel.CustomInfo2(sConf, prpFormat)
    If sFormat = "" Then sFormat = swModel.CustomInfo2("", prpFormat)
    If sFormat = "" Then sFormat = swModel.CustomInfo2(sConfigName, prpFormat)
    sDesc = swModel.CustomInfo2(sConf, prpDescription)
    If Trim$(sFormat) = "БЧ" And InStr(sDesc, Chr$(10)) > 0 Then SwpBchRecord = sDesc
End Function

Private Function SwpFirstLine(ByVal s As String) As String
    Dim p As Long
    p = InStr(s, Chr$(10))
    If p > 0 Then s = Left$(s, p - 1)
    SwpFirstLine = Trim$(Replace(s, Chr$(13), ""))
End Function

' Перенос наименования графы 1 по словам, как SwPlusFormat.WrapTitle надстройки (WP-3.1, спайк S-4): до 22 знаков — одна
' строка 5 мм; до двух строк по 22 знака — две строки; иначе три строки по 31 знаку.
Private Function SwpWrapTitle(ByVal sText As String) As String
    Dim t As String
    Dim n As Integer
    Dim sLines As String
    t = Trim$(Replace(Replace(sText, Chr$(13), " "), Chr$(10), " "))
    If Len(t) <= 22 Then
        SwpWrapTitle = t
        Exit Function
    End If
    sLines = SwpWrapLines(t, 22, n)
    If n <= 2 Then
        SwpWrapTitle = sLines
    Else
        SwpWrapTitle = SwpWrapLines(t, 31, n)
    End If
End Function

Private Function SwpWrapLines(ByVal t As String, ByVal iLimit As Integer, ByRef n As Integer) As String
    Dim vWords As Variant
    Dim k As Long
    Dim sLine As String
    Dim sOut As String
    vWords = Split(Replace(Replace(t, Chr$(9), " "), ChrW$(160), " "), " ")
    n = 0
    For k = LBound(vWords) To UBound(vWords)
        If vWords(k) <> "" Then
            If sLine = "" Then
                sLine = vWords(k)
            ElseIf Len(sLine) + 1 + Len(vWords(k)) <= iLimit Then
                sLine = sLine & " " & vWords(k)
            Else
                If n > 0 Then sOut = sOut & Chr$(10)
                sOut = sOut & sLine
                n = n + 1
                sLine = vWords(k)
            End If
        End If
    Next k
    If sLine <> "" Then
        If n > 0 Then sOut = sOut & Chr$(10)
        sOut = sOut & sLine
        n = n + 1
    End If
    SwpWrapLines = sOut
End Function
"""

STAMP_OLD = """If prpFontSize = 1 Then ' Проверка количества строк
    varTemp = InStr(Наименование.Value, Chr$(10))
    If varTemp > 0 Then
        If InStr(varTemp + 1, Наименование.Value, Chr$(10)) > 0 Then ' Три строки
            strTemp = "<FONT size=3.5>" & Наименование.Value
        Else ' Две строки
            If ChkFont.Value = True Then
                strTemp = "<FONT size=2> " & Chr$(13) & Chr$(10) & "<FONT size=3.5>" & Наименование.Value
            Else
                strTemp = "<FONT size=2> " & Chr$(13) & Chr$(10) & "<FONT size=5>" & Наименование.Value
            End If
        End If
    Else ' Одна строка
        If ChkFont.Value = True Then
            strTemp = "<FONT size=4> " & Chr$(10) & "<FONT size=3.5>" & Наименование.Value
        Else
            strTemp = "<FONT size=4> " & Chr$(10) & "<FONT size=5>" & Наименование.Value
        End If
    End If
Else
    strTemp = Наименование.Value
End If"""

STAMP_NEW = STAMP_OLD.replace("Наименование.Value", "SwpStampText()")

FORM_OLD = """    If strTemp = "" Then ' Если строчка пустая
        strTemp = swModel.CustomInfo2("", prpDescription)
        If strTemp = "" Then ' Если строчка пустая
            Наименование.Value = swModel.SummaryInfo(0)
        Else
            Наименование.Value = strTemp
        End If
    Else
        Наименование.Value = strTemp
    End If
Else
    ChkFont.Value = False
    ChkFont_Click
    ChkEnter.Enabled = False
    ChkEnter.Value = False
End If"""
FORM_NEW = """    If strTemp = "" Then ' Если строчка пустая
        strTemp = swModel.CustomInfo2("", prpDescription)
        If strTemp = "" Then ' Если строчка пустая
            Наименование.Value = SwpFormValue(swModel.SummaryInfo(0)) ' ЕСКД WP-3.2
        Else
            Наименование.Value = SwpFormValue(strTemp) ' ЕСКД WP-3.2
        End If
    Else
        Наименование.Value = SwpFormValue(strTemp) ' ЕСКД WP-3.2: у детали БЧ в поле — запись, а не текст графы 1
    End If
Else
    ChkFont.Value = False
    ChkFont_Click
    ChkEnter.Enabled = False
    ChkEnter.Value = False: Наименование.Value = SwpFormValue(Наименование.Value) ' ЕСКД WP-3.2
End If"""

EDITS = {
    "MProp/MProp.swp": {
        "MProp_run": [(VIEW_MESSAGE, VIEW_SILENT)],
        "FrmMProp": [
            (VIEW_MESSAGE, VIEW_SILENT),
            ("        m = 0 ' Метка заданного Наименования (=1)\n",
             "        m = 0: SwpBchState SwpBchRecord(\"\") ' Метка заданного Наименования (=1); ЕСКД WP-3.2: запись БЧ\n"),
            ("""                strTemp1 = swModel.CustomInfo(prpNumber) & prpNameSep & swModel.CustomInfo(prpDescription)
                If sNumberTitle = strTemp1 Or swModel.CustomInfo(prpNumber) = "" Or swModel.CustomInfo(prpNumber) = strTemp Then
                    ChkManual.Value = False
                    TxtNumber.Value = Left$(sNumberTitle, varTemp - 1)
                    Наименование.Value = Right$(sNumberTitle, Len(sNumberTitle) - varTemp - Len(prpNameSep) + 1)
                    m = 1""",
             """                strTemp1 = SwpDescriptionForName() ' ЕСКД WP-3.2, 3.3, 3.6: имя файла разбирается как в надстройке
                If (swModel.CustomInfo(prpNumber) = SwpNum(sNumberTitle) And strTemp1 = SwpName(sNumberTitle)) Or swModel.CustomInfo(prpNumber) = "" Or swModel.CustomInfo(prpNumber) = strTemp Then
                    ChkManual.Value = False
                    TxtNumber.Value = SwpNum(sNumberTitle)
                    Наименование.Value = SwpName(sNumberTitle)
                    m = 1"""),
            ("""                            ChkManual.Value = False
                            TxtNumber.Value = Left$(sNumberTitle, varTemp - 1)
                            Наименование.Value = Right$(sNumberTitle, Len(sNumberTitle) - varTemp - Len(prpNameSep) + 1)
                            m = 1""",
             """                            ChkManual.Value = False
                            TxtNumber.Value = SwpNum(sNumberTitle) ' ЕСКД WP-3.3, 3.6
                            Наименование.Value = SwpName(sNumberTitle)
                            m = 1"""),
            (FORM_OLD, FORM_NEW),
            ("""            Наименование.Enabled = False
            TxtNumber.Value = Left$(sNumberTitle, varTemp - 1)
            Наименование.Value = Right$(sNumberTitle, Len(sNumberTitle) - varTemp - Len(prpNameSep) + 1)
            ChkFont.Value = False
            ChkFont_Click
            ChkEnter.Enabled = False
            ChkEnter.Value = False""",
             """            Наименование.Enabled = False
            TxtNumber.Value = SwpNum(sNumberTitle) ' ЕСКД WP-3.3, 3.6
            Наименование.Value = SwpName(sNumberTitle)
            ChkFont.Value = False
            ChkFont_Click
            ChkEnter.Enabled = False
            ChkEnter.Value = False: Наименование.Value = SwpFormValue(Наименование.Value) ' ЕСКД WP-3.2"""),
            (STAMP_OLD, STAMP_NEW),
            ("""Else
    ok = swModel.DeleteCustomInfo2(sConfigName, prpDescription)
    ok = swModel.DeleteCustomInfo2(sConfigName, prpDescriptionMulti)
End If""",
             """Else
    If SwpBchRecord(sConfigName) = "" Then ok = swModel.DeleteCustomInfo2(sConfigName, prpDescription) ' ЕСКД WP-3.2
    ok = swModel.DeleteCustomInfo2(sConfigName, prpDescriptionMulti)
End If"""),
            ("""        ok = swModel.DeleteCustomInfo2(vConfNameArr(i), prpDescription)
        ok = swModel.DeleteCustomInfo2(vConfNameArr(i), prpDescriptionMulti)""",
             """        If SwpBchRecord(vConfNameArr(i)) = "" Then ok = swModel.DeleteCustomInfo2(vConfNameArr(i), prpDescription) ' ЕСКД WP-3.2
        ok = swModel.DeleteCustomInfo2(vConfNameArr(i), prpDescriptionMulti)"""),
        ],
    },
    "DProp/DProp.swp": {
        "FrmDProp": [(VIEW_MESSAGE, VIEW_SILENT)],
    },
}


def with_helpers(edits):
    """Вспомогательные процедуры дописываются в конец модуля FrmMProp — номера строк выгрузки, на которые ссылаются план и
    правила записи, не сдвигаются."""
    if "FrmMProp" not in edits:
        return edits
    return {**edits, "FrmMProp": list(edits["FrmMProp"]) + [(None, HELPERS)]}


def plan(swp, edits):
    _, _, _, _, texts = vba_patch.read_modules(swp)
    problems = []
    for module, pairs in edits.items():
        for old, new in pairs:
            if old is None:
                continue
            count = texts[module].count(old)
            if count != 1:
                problems.append(f"{swp.name}/{module}: «{old[:70]}…» — {count} раз")
            if old.count("\n") != new.count("\n"):
                problems.append(f"{swp.name}/{module}: правка «{old[:40]}…» меняет число строк")
    return problems


def run(edits, archive, argv):
    """Правки накладываются только все вместе и из исходного SWPlus — swplus_apply_all.py (аудит 19.09, М-К2):
    повторное наложение одной правки на уже правленый макрос не находит своего места."""
    import swplus_apply_all
    return swplus_apply_all.main(argv)


def main():
    return run({rel: with_helpers(e) for rel, e in EDITS.items()}, ARCHIVE, sys.argv)


if __name__ == "__main__":
    sys.exit(main())
