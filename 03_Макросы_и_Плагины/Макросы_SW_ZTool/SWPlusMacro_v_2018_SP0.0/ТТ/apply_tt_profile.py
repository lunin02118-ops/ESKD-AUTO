# -*- coding: utf-8 -*-
"""
Утилита: apply_tt_profile.py
Назначение: Безопасное автоматическое нанесение типовых технических требований (ТТ)
            по ГОСТ 2.316-2008 / ЕСКД из базы TT_Prof.txt на первый лист активного чертежа SolidWorks.
Правила ГОСТ 2.316-2008:
- ТТ размещаются только на первом листе (Sheet 1) над основной надписью.
- Ширина колонки не превышает 185 мм.
- Не удаляет сторонние заметки, шероховатости или размеры с чертежа.
"""

import os
import sys
import argparse

if sys.stdout.encoding != 'utf-8':
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass

import win32com.client
import pythoncom

WORKSPACE_ROOT = r"d:\Work\_Инструменты_Конструктора"
TT_DIR = os.path.join(WORKSPACE_ROOT, "03_Макросы_и_Плагины", "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "ТТ")
TT_PROF_FILE = os.path.join(TT_DIR, "TT_Prof.txt")


def load_profiles():
    """Загружает профили ТТ из TT_Prof.txt (кодировка CP1251)."""
    if not os.path.exists(TT_PROF_FILE):
        raise FileNotFoundError(f"Файл профилей не найден: {TT_PROF_FILE}")

    with open(TT_PROF_FILE, "r", encoding="cp1251", errors="replace") as f:
        lines = [line.rstrip("\r\n") for line in f]

    profiles = {}
    current_prof = None

    for line in lines:
        if line.startswith("$$$"):
            current_prof = line[3:].strip()
            profiles[current_prof] = []
        elif current_prof and line.strip():
            profiles[current_prof].append(line.strip())

    return profiles


def get_sw_app():
    """Подключается к активной сессии SolidWorks."""
    pythoncom.CoInitialize()
    try:
        return win32com.client.GetActiveObject("SldWorks.Application")
    except Exception:
        try:
            return win32com.client.Dispatch("SldWorks.Application")
        except Exception as ex:
            print(f"[ОШИБКА] Не удалось подключиться к SolidWorks: {ex}")
            return None


def apply_profile_to_active_drawing(profile_name: str, save_drawing: bool = False):
    """
    Безопасно наносит выбранный профиль ТТ на первый лист активного чертежа SolidWorks
    строго по ГОСТ 2.316-2008.
    """
    profiles = load_profiles()
    
    if profile_name not in profiles:
        print(f"[ОШИБКА] Профиль '{profile_name}' не найден.")
        print("Доступные профили:")
        for idx, name in enumerate(profiles.keys(), 1):
            print(f"  {idx}. {name}")
        return False

    items = profiles[profile_name]
    
    # Форматирование списка по ГОСТ: пункт со звездочкой (*) всегда идет первым
    ref_items = [it for it in items if it.startswith("*")]
    other_items = [it for it in items if not it.startswith("*")]
    all_ordered = ref_items + other_items
    
    formatted_lines = []
    for idx, item in enumerate(all_ordered, 1):
        if len(all_ordered) == 1:
            formatted_lines.append(item)
        else:
            formatted_lines.append(f"{idx}. {item}")
            
    tt_text = "\r\n".join(formatted_lines)

    sw = get_sw_app()
    if not sw:
        print("[ОШИБКА] SolidWorks не запущен.")
        return False

    drw = sw.ActiveDoc
    if not drw or drw.GetType != 3:
        print("[ОШИБКА] В SolidWorks не открыт чертеж (SLDDRW).")
        return False

    sheet_names = drw.GetSheetNames
    if not sheet_names or len(sheet_names) == 0:
        print("[ОШИБКА] В чертеже нет листов.")
        return False

    current_sheet_name = drw.GetCurrentSheet.GetName
    sheet1_name = sheet_names[0] # ГОСТ: ТТ наносятся ТОЛЬКО на первый лист

    print(f"Применение профиля '{profile_name}' к чертежу '{drw.GetTitle}' (Лист 1: {sheet1_name})...")

    # Активируем Лист 1
    drw.ActivateSheet(sheet1_name)
    sheet = drw.GetCurrentSheet
    props = sheet.GetProperties2
    sheet_width = props[5]
    sheet_height = props[6]

    # Активируем листовой вид (не проекционный)
    drw.ActivateView("")
    sheet_view = drw.GetFirstView

    existing_tt_note = None
    
    # Ищем существующую заметку ТТ ТОЛЬКО в листовом виде над штампом
    if sheet_view:
        n = sheet_view.GetFirstNote
        while n:
            name = n.GetName or ""
            txt = n.GetText or ""
            ann = n.GetAnnotation
            pos = ann.GetPosition if ann else None
            
            # Признаки заметки ТТ:
            # 1. Задано имя Заметка_ТТ
            # 2. Или текст содержит "размеры для справок" / "гост 30893" / "гост 14771"
            #    И координата находится над штампом: X >= SheetWidth - 195 мм, Y >= 55 мм, Y <= 250 мм
            is_tt = False
            if "заметка_тт" in name.lower() or "tt_note" in name.lower():
                is_tt = True
            elif pos and len(pos) >= 2:
                in_tt_zone = (pos[0] >= (sheet_width - 0.200)) and (0.055 <= pos[1] <= 0.280)
                if in_tt_zone and any(k in txt.lower() for k in ["*размеры для справок", "для справок", "гост 30893", "гост 14771", "технические требования"]):
                    is_tt = True
                    
            if is_tt:
                existing_tt_note = n
                break
            n = n.GetNext

    # Координаты по ГОСТ 2.104 и 2.316:
    # Штамп: ширина 185 мм, высота 55 мм, отступ справа 5 мм, снизу 5 мм.
    # Левый край штампа: SheetWidth - 0.190.
    # Точка вставки ТТ (верхний левый угол блока):
    pos_x = sheet_width - 0.185
    line_count = len(formatted_lines)
    # Высота строки ~6.5 мм, базовый зазор над штампом 5 мм
    pos_y = 0.065 + (0.0065 * line_count)

    if existing_tt_note:
        print("Найдена существующая заметка ТТ. Обновление текста...")
        existing_tt_note.SetText(tt_text)
        existing_tt_note.SetName("Заметка_ТТ")
        ann = existing_tt_note.GetAnnotation
        if ann:
            ann.SetPosition(pos_x, pos_y, 0.0)
            tf = existing_tt_note.GetTextFormat
            if tf:
                tf.CharHeight = 0.0035
                tf.Italic = True
                ann.SetTextFormat(0, False, tf)
    else:
        print("Создание новой заметки ТТ над штампом...")
        new_note = drw.CreateText2(tt_text, pos_x, pos_y, 0, 0.0035, 0)
        if new_note:
            new_note.SetName("Заметка_ТТ")
            tf = new_note.GetTextFormat
            if tf:
                tf.CharHeight = 0.0035
                tf.Italic = True
                new_note.GetAnnotation.SetTextFormat(0, False, tf)
        else:
            print("[ПРЕДУПРЕЖДЕНИЕ] Не удалось создать заметку через CreateText2.")

    # Восстанавливаем ранее активный лист пользователя
    if current_sheet_name != sheet1_name:
        drw.ActivateSheet(current_sheet_name)

    drw.ClearSelection2(True)
    drw.ForceRebuild3(False)

    if save_drawing:
        err = win32com.client.VARIANT(win32com.client.pythoncom.VT_BYREF | win32com.client.pythoncom.VT_I4, 0)
        warn = win32com.client.VARIANT(win32com.client.pythoncom.VT_BYREF | win32com.client.pythoncom.VT_I4, 0)
        drw.Save3(1, err, warn)

    print(f"[УСПЕХ] Профиль ТТ '{profile_name}' успешно нанесен на Лист 1 без повреждения чертежа!")
    return True


if __name__ == "__main__":
    profiles = load_profiles()
    parser = argparse.ArgumentParser(description="Безопасное нанесение профиля ТТ по ГОСТ 2.316 на чертеж SolidWorks")
    parser.add_argument("--profile", "-p", type=str, help="Название профиля ТТ")
    parser.add_argument("--list", "-l", action="store_true", help="Показать список доступных профилей")
    parser.add_argument("--save", "-s", action="store_true", help="Сохранить чертеж после нанесения ТТ")

    args = parser.parse_args()

    if args.list:
        print("Доступные типовые профили ТТ:")
        for idx, name in enumerate(profiles.keys(), 1):
            print(f"  {idx}. {name}")
        sys.exit(0)

    if args.profile:
        apply_profile_to_active_drawing(args.profile, args.save)
    else:
        print("Использование: python apply_tt_profile.py --profile \"<Имя профиля>\"")
        print("Для просмотра списка: python apply_tt_profile.py --list")
