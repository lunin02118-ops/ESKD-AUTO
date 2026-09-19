# -*- coding: utf-8 -*-
"""Правка MProp по замечанию владельца З-3 (18.09.2026): повторное заполнение после «Удалить все свойства».

    Накладывается вместе с остальными правками: python 09_Тесты/tools/swplus_apply_all.py [--apply]

«Удалить все свойства» (CmdDelete_Click) стирает и «Сводку → Автор». После перезапуска формы:
* пустая «Контора» — MProp выбирает первую организацию списка (CboFirm.ListIndex = 0); при пустом MProp_Firm.txt это
  ошибка 380 «Could not set the ListIndex property», форма обрывается и ничего не заполняет;
* «Разработал» пуст — MProp берёт его только из «Автора».
Правка: пустой список организаций не обрывает форму; при пустом «Авторе» «Разработал» — первая фамилия списка
MProp_Fam.txt, где установщик и окно «Настройки ЕСКД» ставят фамилию этого рабочего места первой (Core/SwPlusLists).
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import swplus_step4_edits as base  # noqa: E402

EDITS = {
    "MProp/MProp.swp": {
        "FrmMProp": [
            ("""    If CboFirm.Value = "" Then
        CboFirm.ListIndex = 0""",
             """    If CboFirm.Value = "" Then
        If CboFirm.ListCount > 0 Then CboFirm.ListIndex = 0 ' ЕСКД З-3: пустой список организаций — без ошибки 380"""),
            ("""Разработал.Value = swModel.SummaryInfo(2)
""",
             """Разработал.Value = swModel.SummaryInfo(2): If Разработал.Value = "" And Разработал.ListCount > 0 Then Разработал.ListIndex = 0 ' ЕСКД З-3: после «Удалить все свойства» — первая фамилия списка
"""),
        ],
    },
}


def main():
    return base.run(EDITS, base.ARCHIVE, sys.argv)


if __name__ == "__main__":
    sys.exit(main())
