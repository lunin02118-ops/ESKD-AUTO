# -*- coding: utf-8 -*-
"""Правка DProp по дефекту Д-64 (решение владельца 19.09.2026 «сделать так, как правильно»).

    Накладывается вместе с остальными правками: python 09_Тесты/tools/swplus_apply_all.py [--apply]

При открытии формы DProp (UserForm_Activate) вызывал SheetsControl: переименовывал листы в DRW1…n, выставлял масштаб,
вставлял или предлагал удалить ЛРИ и писал «Лист»/«Листов» — чертёж менялся от одного взгляда на форму.
Правка: открытие формы только читает и проверяет чертёж. Нумерацию листов и «Листов N» делает явное действие:
«Исправить оформление чертежа» (CmdStandard), а также, как и раньше, «Добавить лист», «Удалить лист», «Масштаб».
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import swplus_step4_edits as base  # noqa: E402

EDITS = {
    "DProp/DProp.swp": {
        "FrmDProp": [
            ("""Tests
SheetsControl

' Возвращение активного листа
ok = swDraw.ActivateSheet(strActiveSheetName)

If prpLeftTopCorner = 1 Then""",
             """Tests
' ЕСКД Д-64: открытие формы чертёж не меняет — листы и «Листов» правит «Исправить оформление чертежа»

' Возвращение активного листа
ok = swDraw.ActivateSheet(strActiveSheetName)

If prpLeftTopCorner = 1 Then"""),
            ("""    AnnDef
    ' Узнаем имя активного листа""",
             """    AnnDef: SheetsControl ' ЕСКД Д-64: нумерация листов DRW1…n и «Лист»/«Листов» — по явной команде
    ' Узнаем имя активного листа"""),
        ],
    },
}


def main():
    return base.run(EDITS, base.ARCHIVE, sys.argv)


if __name__ == "__main__":
    sys.exit(main())
