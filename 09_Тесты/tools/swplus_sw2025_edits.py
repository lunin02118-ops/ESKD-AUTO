# -*- coding: utf-8 -*-
"""Правка SpecEditor под SolidWorks 2025 (коммит 2652461, 08.09.2026; тогда вносилась в редакторе VBA, здесь — записана).

SolidWorks 2025 называет вид свойств листа не «По умолчанию», а по имени формата; прежняя проверка не находила вид
и спецификация не читала свойства листа. Условие «вид задан» — любой, кроме «ОТСУТСТВУЕТ».

Применяется первой в цепочке swplus_apply_all.py.
"""

OLD = 'swSheet.CustomPropertyView = "По умолчанию" Or swSheet.CustomPropertyView = "Default" Then'
NEW = 'swSheet.CustomPropertyView <> "ОТСУТСТВУЕТ" Or swSheet.CustomPropertyView = "Default" Then'

EDITS = {
    "SpecEditor/SpecEditor.swp": {
        "FrmSpecEditor": [
            ("\n    If " + OLD, "\n    If " + NEW),
            ("\n            If " + OLD, "\n            If " + NEW),
        ],
        "SpecEditor_run": [("\nIf " + OLD, "\nIf " + NEW)],
    },
}
