# Патч SaveAsPDF.swp — выгрузка PDF по структуре заказа КТО

Основание: ТЗ-03 ред. 8 §4.3, регламент КТО v3.1 §7.2.
Исходный текст модуля — `_VBA_выгрузка/SaveAsPDF/SaveAsPDF.frm.txt` (номера строк ниже — по нему).
`.swp` — двоичный файл, править можно только в редакторе VBA SolidWorks.

## Что меняется

| Было | Стало (если чертёж лежит в папке `01_3D`) |
|---|---|
| PDF в подпапку `PDF\` рядом с чертежом (`01_3D\PDF\`) | PDF в `<изделие>\02_PDF\` |
| имя PDF = имя чертежа; прежний PDF → `PDF\Revisions\<имя>_Rev.NN.pdf` | имя PDF = `<Обозначение> <Наименование>` при ревизии 0 и `<Обозначение> <Наименование>_Изм<N>.pdf` при ревизии N ≥ 1; все прежние PDF этого чертежа (`<имя>.pdf`, `<имя>_Изм*.pdf`) → `<изделие>\_Аннулировано\` без переименования |
| лист развёртки Drew `DXF…` — вопрос «сохранять?» | лист `DXF…` молча не попадает в PDF (он уходит в DXF) |

Если чертёж лежит **не** в `01_3D` (старые папки, личные эскизы) — поведение прежнее.
`SaveAsPDF.ini` не меняется (строка 4 должна оставаться `1`).

## Как применить (один раз, в исходниках выпуска)

1. Закрыть все документы. SolidWorks → **Инструменты → Макрос → Редактировать…** → `03_Макросы_и_Плагины\Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\SaveAsPDF\SaveAsPDF.swp` (из рабочей копии репозитория, **не** из `%LOCALAPPDATA%`).
2. В проекте открыть форму `SaveAsPDF` → **View Code**. Внести три правки ниже (Ctrl+F по тексту «Было»).
3. **Debug → Compile VBAProject** — без ошибок. Сохранить (Ctrl+S), закрыть редактор.
4. Проверка: чертёж `…\02_Металл\И01_…\01_3D\<Обозн> <Наим>.SLDDRW` → кнопка SaveAsPDF → PDF в `…\И01_…\02_PDF\`; выставить ревизию 1 → снова SaveAsPDF → `…_Изм1.pdf` в `02_PDF`, прежний PDF — в `…\И01_…\_Аннулировано\`.
5. `python 09_Тесты/tools/export_vba.py` → выгрузка и `manifest.json` обновятся → коммит вместе с `.swp` → публикация выпуска → переустановка рабочих мест (кнопки SWPlus работают из локальной копии `%LOCALAPPDATA%\ESKD\Toolkit`, её обновляет установщик).

---

## Правка 1 — папка PDF (стр. 310–323)

**Было:**
```vba
' Добавляем путь к папке
If sIni4 = 1 Then
    varTemp = InStrRev(sPathName, "\")
    If sIni3 = 0 Then ' PDF
        sPathName = Left$(sPathName, varTemp - 1) & "\PDF" & Right$(sPathName, Len(sPathName) - varTemp + 1)
        sDirName = sDirName & "PDF\"
    Else ' TIF
```

**Стало:**
```vba
' Добавляем путь к папке
' Структура КТО (ТЗ-03): чертёж в <изделие>\01_3D -> PDF в <изделие>\02_PDF, старые PDF -> <изделие>\_Аннулировано
Dim sProdDir As String
sProdDir = ""
If UCase$(Right$(sDirName, 6)) = "01_3D\" Then sProdDir = Left$(sDirName, Len(sDirName) - 6)
If sIni4 = 1 Then
    varTemp = InStrRev(sPathName, "\")
    If sIni3 = 0 And sProdDir <> "" Then ' PDF по структуре КТО
        sPathName = sProdDir & "02_PDF" & Right$(sPathName, Len(sPathName) - varTemp + 1)
        sDirName = sProdDir & "02_PDF\"
    ElseIf sIni3 = 0 Then ' PDF
        sPathName = Left$(sPathName, varTemp - 1) & "\PDF" & Right$(sPathName, Len(sPathName) - varTemp + 1)
        sDirName = sDirName & "PDF\"
    Else ' TIF
```

## Правка 2 — ревизии (стр. 602–628, внутри ветки «Изменения были»)

**Было:** блок от `If sIni5 = 1 Then  ' Копируем старые версии` до его `End If` (перед `End If` / `End If` / `End If` / `If prpTestName = 1`).

**Стало:**
```vba
                If sIni5 = 1 And sProdDir <> "" And sIni3 = 0 Then ' Структура КТО: _ИзмN у нового, старые -> _Аннулировано
                    Dim sBase As String, sBaseName As String, sAnnul As String
                    Dim aOld() As String, nOld As Long, kOld As Long
                    strRev = Trim$(swDraw.CustomInfo2("", "Revision"))
                    sBase = Left$(sPathName, Len(sPathName) - 4)
                    sBaseName = Mid$(sBase, InStrRev(sBase, "\") + 1)
                    If IsNumeric(strRev) Then
                        If CInt(strRev) >= 1 Then sPathName = sBase & "_Изм" & CStr(CInt(strRev)) & ".pdf"
                    End If
                    sAnnul = sProdDir & "_Аннулировано\"
                    If fs.FolderExists(sAnnul) = False Then MkDir sAnnul
                    ' Сначала собираем список (Dir нельзя совмещать с переносом файлов)
                    nOld = 0
                    strTemp = Dir$(sDirName & sBaseName & "*.pdf")
                    Do While strTemp <> ""
                        If StrComp(strTemp, sBaseName & ".pdf", vbTextCompare) = 0 _
                           Or InStr(1, strTemp, sBaseName & "_Изм", vbTextCompare) = 1 Then
                            ReDim Preserve aOld(nOld)
                            aOld(nOld) = strTemp
                            nOld = nOld + 1
                        End If
                        strTemp = Dir$()
                    Loop
                    For kOld = 0 To nOld - 1
                        If StrComp(sDirName & aOld(kOld), sPathName, vbTextCompare) <> 0 Then
                            If fs.FileExists(sAnnul & aOld(kOld)) = False Then
                                fs.MoveFile sDirName & aOld(kOld), sAnnul & aOld(kOld)
                            End If
                        End If
                    Next kOld
                ElseIf sIni5 = 1 Then  ' Копируем старые версии (прежний режим, вне структуры КТО)
                    ' ... здесь без изменений остаётся весь прежний блок: от "Определяем номер изменения" до его End If ...
                End If
```
Прежнее тело блока (`' Определяем номер изменения` … `End If`) переносится внутрь ветки `ElseIf sIni5 = 1 Then` без изменений.

## Правка 3 — лист развёртки Drew (стр. 638–650)

**Было:**
```vba
            If Left$(vSheetNames(i), 3) = "DRW" Or Left$(vSheetNames(i), 4) = "Лист" Or ...
```

**Стало:**
```vba
            If UCase$(Left$(vSheetNames(i), 3)) = "DXF" Then
                ' лист развёртки Drew — в PDF не выводится (его выгружает пресет DXF)
            ElseIf Left$(vSheetNames(i), 3) = "DRW" Or Left$(vSheetNames(i), 4) = "Лист" Or ...
```
(остаток условия и ветки `Else` — без изменений).

## Проверка после правки
- `python 09_Тесты/run_tests.py static` — `test_T0_vba_export_matches_swp` зелёный после `export_vba.py`.
- Ручная проверка — п. 4 выше, плюс чертёж вне `01_3D` — PDF как раньше в `PDF\`.
