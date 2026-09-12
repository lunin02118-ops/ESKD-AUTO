# -*- coding: utf-8 -*-
"""
Tier 3: Сквозные функциональные тесты через SolidWorks API (E2E Integration).

Реальные сценарии конструктора (не пустые шаблоны):
  1. Деталь из ПРОФИЛЬНОЙ ТРУБЫ 80х80х4 (ГОСТ-материал из библиотеки, STACK-дробь в штампе).
  2. Деталь из ЛИСТА 4 мм (Лист ГОСТ 19903-2015 / Ст3сп).
  3. СБОРКА ИЗ ТРЁХ ДЕТАЛЕЙ (шифр «СБ», «Сборочный чертёж», чистое Обозначение).
  4. Чертежи: resolved-заметки штампа (графы 1/2/3/5/26), двухстрочное наименование,
     центрирование массы (пиксельная проверка PNG), отсутствие служебной заметки «Файл:».
  5. Защита стандартных/покупных изделий (SProp).
  6. Zero-Drift: нулевое смещение заметок при сохранении чертежа.
  7. Экспорт в PDF.

Технические паттерны (выявлены живыми A/B-тестами против SW2025):
  - Свойства моделей читаются ИЗ ФАЙЛА после CloseDoc: надстройка мутирует их в
    FileSaveAsNotify2, файл сериализуется уже с ними, но in-memory Get() отдаёт стейл-кэш.
  - Виды в чертёж вставляются при ОТКРЫТОЙ модели (P1): лёгкая подгрузка закрытого файла
    кэширует листовые $PRPSHEET-резолвы и мусорит штамп.
  - AddComponent5 требует открытой модели-источника.
"""

import os
import sys
import time
import win32com.client
import win32com.client.dynamic
import pythoncom

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
TEMPLATES_DIR = os.path.join(ROOT_DIR, "02_Шаблоны_и_Форматки")
MATERIALS_DIR = os.path.join(ROOT_DIR, "04_Библиотеки_Материалов_и_Профилей", "Библиотека материалов")
OUTPUT_DIR = os.path.join(ROOT_DIR, "08_Результаты_Тестирования", "Auto_E2E_Test_Output")

MAT_DB = os.path.join(MATERIALS_DIR, "Библиотека_Материалов_ГОСТ.sldmat")
MAT_TUBE = "Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86"
MAT_SHEET = "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"

MARK = None


class TestResult:
    def __init__(self):
        self.passed = 0
        self.failed = 0
        self.warnings = 0
        self.errors = []
        self.warn_msgs = []

    def assert_true(self, condition, test_name, error_msg=""):
        if condition:
            self.passed += 1
            print(f"  [PASS] {test_name}")
        else:
            self.failed += 1
            msg = f"  [FAIL] {test_name}: {error_msg}"
            print(msg)
            self.errors.append(msg)

    def warn(self, test_name, msg):
        self.warnings += 1
        w_msg = f"  [WARN] {test_name}: {msg}"
        print(w_msg)
        self.warn_msgs.append(w_msg)


def get_sw():
    raw = win32com.client.GetActiveObject("SldWorks.Application")
    return win32com.client.dynamic.Dispatch(raw._oleobj_)


def mkref():
    return win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)


def select_plane(doc, *names):
    for nm in names:
        try:
            if doc.Extension.SelectByID2(nm, "PLANE", False, 0, 0, 0, False, 0, MARK, 0):
                return nm
        except Exception:
            pass
    return None


def rect(sk, x0, y0, x1, y1):
    sk.CreateLine(x0, y0, 0, x1, y0, 0)
    sk.CreateLine(x1, y0, 0, x1, y1, 0)
    sk.CreateLine(x1, y1, 0, x0, y1, 0)
    sk.CreateLine(x0, y1, 0, x0, y0, 0)


def read_notes(drw):
    """Resolved-тексты заметок штампа листа (форматки)."""
    out = {}
    v = drw.GetFirstView
    n = v.GetFirstNote
    while n is not None:
        nm = str(n.GetName or "")
        t = str(n.GetText or "")
        if nm in ("MYPRP0", "MYPRP2", "MYPRP3", "MYPRP4", "MYPRP15", "MYPRP16"):
            out[nm] = t
        if t.startswith("Файл"):
            out.setdefault("ФАЙЛ_МУСОР", t)
        n = n.GetNext
    return out


def file_props(sw, path, doctype):
    """Свойства из ФАЙЛА: документ обязан быть закрыт (in-memory кэш после SaveAs3 стейл,
    CloseDoc выгружает асинхронно — пауза обязательна)."""
    time.sleep(1.5)
    doc = sw.OpenDoc6(path, doctype, 1, "", mkref(), mkref())
    if not doc:
        return {}
    cpm = doc.Extension.CustomPropertyManager("")
    out = {}
    for nm in (cpm.GetNames or []):
        out[str(nm)] = str(cpm.Get(str(nm)) or "")
    sw.CloseDoc(doc.GetTitle)
    return out


def close_doc(sw, doc):
    try:
        sw.CloseDoc(doc.GetTitle)
    except Exception:
        pass


def run_tier3_tests():
    global MARK
    MARK = win32com.client.VARIANT(pythoncom.VT_DISPATCH, None)

    print("=" * 70)
    print("  Tier 3: СКВОЗНЫЕ ТЕСТЫ SOLIDWORKS (E2E: ТРУБА/ЛИСТ/СБОРКА-3/ШТАМП/ZERO-DRIFT)")
    print("=" * 70)

    res = TestResult()
    os.makedirs(OUTPUT_DIR, exist_ok=True)

    print("\n--- 1. Подключение к SolidWorks 2025 ---")
    try:
        sw = get_sw()
        rev = str(sw.RevisionNumber)
        res.assert_true(sw is not None, f"Подключение к SolidWorks (Ревизия: {rev})")
    except Exception as e:
        res.assert_true(False, "Подключение к SolidWorks", str(e))
        return False

    part_template = os.path.join(TEMPLATES_DIR, "Шаблоны документов", "Деталь.prtdot")
    asm_template = os.path.join(TEMPLATES_DIR, "Шаблоны документов", "Сборка.asmdot")
    drw_template = os.path.join(TEMPLATES_DIR, "Шаблоны документов", "Чертеж.drwdot")
    for t in (part_template, asm_template, drw_template):
        res.assert_true(os.path.isfile(t), f"Наличие шаблона {os.path.basename(t)}")

    tube_path = os.path.join(OUTPUT_DIR, "ПРТИ.468211.020 Стойка направляющая из профильной трубы.sldprt")
    sheet_path = os.path.join(OUTPUT_DIR, "ПРТИ.468211.021 Пластина опорная нижняя.sldprt")
    asm_path = os.path.join(OUTPUT_DIR, "ПРТИ.468211.030 СБ Рама кондуктора сварная.sldasm")
    for p in (tube_path, sheet_path, asm_path):
        if os.path.isfile(p):
            try:
                os.remove(p)
            except Exception:
                pass

    # =====================================================================
    # СЦЕНАРИЙ 1: Деталь из профильной трубы 80х80х4 (реальная геометрия+материал)
    # =====================================================================
    print("\n--- 2. Сценарий: деталь из профтрубы 80х80х4, материал ГОСТ ---")
    tube_doc = sw.NewDocument(part_template, 0, 0, 0)
    res.assert_true(tube_doc is not None, "Создание детали по корпоративному шаблону")
    sk = tube_doc.SketchManager
    select_plane(tube_doc, "Спереди", "Front")
    sk.InsertSketch(True)
    rect(sk, -0.040, -0.040, 0.040, 0.040)   # 80x80
    rect(sk, -0.036, -0.036, 0.036, 0.036)   # стенка 4
    tube_doc.FeatureManager.FeatureExtrusion3(
        True, False, False, 0, 0, 0.300, 0.0,
        False, False, False, False, 0, 0,
        False, False, False, False, True, True, True, 0, 0, False)
    cfgn = str(tube_doc.GetActiveConfiguration.Name)
    try:
        tube_doc.SetMaterialPropertyName2(cfgn, MAT_DB, MAT_TUBE)
        res.assert_true(True, f"Назначен материал ГОСТ: {MAT_TUBE}")
    except Exception as e:
        res.assert_true(False, "Назначение материала из ГОСТ-библиотеки", str(e))
    tube_doc.SaveAs3(tube_path, 0, 1)   # свойства пишет надстройка ЕСКД
    res.assert_true(os.path.isfile(tube_path), "Сохранение: ПРТИ.468211.020 Стойка направляющая из профильной трубы.sldprt")
    close_doc(sw, tube_doc)

    props = file_props(sw, tube_path, 1)
    desig = props.get("Обозначение", "")
    title = props.get("Наименование", "")
    title_fb = props.get("Наименование_ФБ", "")
    mass_fb = props.get("Масса_ФБ", "")
    mat_fb = props.get("Материал_ФБ", "")
    res.assert_true(desig == "ПРТИ.468211.020", f"Обозначение чистое из имени файла ('{desig}')", "ожидалось ПРТИ.468211.020")
    res.assert_true(title == "Стойка направляющая из профильной трубы", f"Наименование из имени файла ('{title}')")
    nl = title_fb.replace("\r\n", "\n")
    res.assert_true("\n" in nl and nl.split("\n")[0] == "Стойка направляющая из",
                    f"Двухстрочный перенос наименования_ФБ ('{nl[:50]}...')")
    res.assert_true(mass_fb.startswith("<FONT size=3.5>") and "," in mass_fb and "\n" not in mass_fb,
                    f"Масса_ФБ однострочная с запятой ('{mass_fb}')")
    res.assert_true("<STACK" in mat_fb and "Труба 80х80х4,0 ГОСТ 8639-82" in mat_fb and "В 10 ГОСТ 13663-86" in mat_fb,
                    f"Материал_ФБ = STACK-дробь сортамент/марка ('{mat_fb[:70]}...')")

    # =====================================================================
    # СЦЕНАРИЙ 2: Деталь из листа 4 мм
    # =====================================================================
    print("\n--- 3. Сценарий: деталь из листа 4 мм (Лист ГОСТ 19903-2015) ---")
    sheet_doc = sw.NewDocument(part_template, 0, 0, 0)
    sk = sheet_doc.SketchManager
    select_plane(sheet_doc, "Спереди", "Front")
    sk.InsertSketch(True)
    rect(sk, -0.100, -0.075, 0.100, 0.075)   # 200x150
    sheet_doc.FeatureManager.FeatureExtrusion3(
        True, False, False, 0, 0, 0.004, 0.0,
        False, False, False, False, 0, 0,
        False, False, False, False, True, True, True, 0, 0, False)
    cfgn = str(sheet_doc.GetActiveConfiguration.Name)
    try:
        sheet_doc.SetMaterialPropertyName2(cfgn, MAT_DB, MAT_SHEET)
        res.assert_true(True, f"Назначен материал ГОСТ: {MAT_SHEET}")
    except Exception as e:
        res.assert_true(False, "Назначение материала (лист)", str(e))
    sheet_doc.SaveAs3(sheet_path, 0, 1)
    res.assert_true(os.path.isfile(sheet_path), "Сохранение: ПРТИ.468211.021 Пластина опорная нижняя.sldprt")
    close_doc(sw, sheet_doc)

    sprops = file_props(sw, sheet_path, 1)
    mat_fb = sprops.get("Материал_ФБ", "")
    res.assert_true("<STACK" in mat_fb and "ГОСТ 19903-2015" in mat_fb and "Ст3сп" in mat_fb,
                    f"Материал_ФБ листа = STACK-дробь ('{mat_fb[:70]}...')")
    res.assert_true(sprops.get("Обозначение", "") == "ПРТИ.468211.021", "Обозначение пластины")

    # =====================================================================
    # СЦЕНАРИЙ 3: Сборка из ТРЁХ деталей
    # =====================================================================
    print("\n--- 4. Сценарий: сборка из трёх деталей (шифр СБ) ---")
    t_open = sw.OpenDoc6(tube_path, 1, 1, "", mkref(), mkref())   # AddComponent5 требует открытую модель
    s_open = sw.OpenDoc6(sheet_path, 1, 1, "", mkref(), mkref())
    asm_doc = sw.NewDocument(asm_template, 0, 0, 0)
    res.assert_true(asm_doc is not None, "Создание сборки по корпоративному шаблону")
    added = 0
    for path, x in ((tube_path, 0.0), (tube_path, 0.120), (sheet_path, 0.060)):
        try:
            c = asm_doc.AddComponent5(path, 0, "", False, "", x, 0.0, 0.0)
            added += (c is not None)
        except Exception:
            pass
    res.assert_true(added == 3, f"Вставлено 3 компонента (фактически {added})")
    asm_doc.SaveAs3(asm_path, 0, 1)
    res.assert_true(os.path.isfile(asm_path), "Сохранение: ПРТИ.468211.030 СБ Рама кондуктора сварная.sldasm")
    alt_on = asm_doc.GetActiveConfiguration.UseAlternateNameInBOM
    close_doc(sw, asm_doc)
    for d in (t_open, s_open):
        if d:
            close_doc(sw, d)

    aprops = file_props(sw, asm_path, 2)
    res.assert_true(aprops.get("Обозначение", "") == "ПРТИ.468211.030",
                    f"Обозначение сборки ЧИСТОЕ, без шифра ('{aprops.get('Обозначение', '')}')")
    res.assert_true(aprops.get("Сборка1_ФБ", "") == " СБ",
                    f"Шифр СБ в свойстве Сборка1_ФБ с ведущим пробелом ('{aprops.get('Сборка1_ФБ', '')}')")
    res.assert_true(aprops.get("Сборка2_ФБ", "") == "Сборочный чертёж",
                    f"Вторая строка графы 2 по ГОСТ 2.109 ('{aprops.get('Сборка2_ФБ', '')}')")
    mfb = aprops.get("Масса_ФБ", "")
    res.assert_true(mfb.startswith("<FONT size=3.5>") and "," in mfb,
                    f"Масса сборки посчитана ('{mfb}')")
    res.assert_true(not alt_on, "UseAlternateNameInBOM выключен (не рвёт $PRPSHEET-обозначение)")

    # =====================================================================
    # СЦЕНАРИЙ 4: Чертежи и штампы (P1: модель открыта при вставке вида)
    # =====================================================================
    print("\n--- 5. Сценарий: чертежи, штампы, графа 26, центрирование массы ---")
    from PIL import Image
    import numpy as np

    def stamp_scenario(model_path, doctype, png_name, checks):
        mdoc = sw.OpenDoc6(model_path, doctype, 1, "", mkref(), mkref())
        if not mdoc:
            res.assert_true(False, f"Открытие модели {os.path.basename(model_path)}")
            return
        drw = sw.NewDocument(drw_template, 0, 0, 0)
        view = None
        for vn in ("*Спереди", "*Front", "*Изометрия", "*Isometric"):
            try:
                view = drw.CreateDrawViewFromModelView3(model_path, vn, 0.15, 0.18, 0.0)
                if view is not None:
                    break
            except Exception:
                pass
        res.assert_true(view is not None, f"[{png_name}] Вставлен вид модели")
        time.sleep(2)
        notes = read_notes(drw)
        for key, want in checks.items():
            if want == "__NONEMPTY__":
                ok = bool(notes.get(key, "").strip())
                res.assert_true(ok, f"[{png_name}] {key} заполнена ('{notes.get(key, '')[:50]}')")
            else:
                real = notes.get(key, "").replace("\r\n", "\n")
                ok = real == want.replace("\r\n", "\n")
                res.assert_true(ok, f"[{png_name}] {key}", f"'{real[:60]}' != '{want[:60]}'")
        res.assert_true("ФАЙЛ_МУСОР" not in notes, f"[{png_name}] Служебная заметка «Файл:» отсутствует")
        png = os.path.join(OUTPUT_DIR, png_name)
        if os.path.isfile(png):
            try:
                os.remove(png)
            except Exception:
                pass
        drw.SaveAs3(png, 0, 1)
        ok_png = os.path.isfile(png) and os.path.getsize(png) > 5000
        res.assert_true(ok_png, f"[{png_name}] PNG-рендер сохранён")
        try:
            im = Image.open(png).convert("L")
            a = np.array(im)
            h, w = a.shape
            pxx, pxy = w / 420.0, h / 297.0
            ink = a < 128
            reg = ink[int((297 - 38.5) * pxy):int((297 - 26.5) * pxy), int(384.5 * pxx):int(396.5 * pxx)]
            if reg.any():
                rows = np.where(reg.any(axis=1))[0]
                cy = (rows[0] + rows[-1]) / 2 / reg.shape[0]
                res.assert_true(0.25 <= cy <= 0.75,
                                f"[{png_name}] Масса в ячейке, вертикальный центр {cy:.2f} (0.5=центр)")
            else:
                res.assert_true(False, f"[{png_name}] Масса в ячейке массы", "ячейка пуста на рендере")
        except Exception as e:
            res.warn(png_name, f"пиксельная проверка массы: {e}")
        close_doc(sw, drw)
        close_doc(sw, mdoc)

    stamp_scenario(tube_path, 1, "T3_stamp_tube.png", {
        "MYPRP0": "ПРТИ.468211.020",            # графа 1
        "MYPRP2": "ПРТИ.468211.020",            # графа 26 (повёрнутое обозначение)
        "MYPRP4": "Стойка направляющая из\nпрофильной трубы",  # двухстрочное
        "MYPRP15": "__NONEMPTY__",              # масса
    })
    stamp_scenario(sheet_path, 1, "T3_stamp_sheet.png", {
        "MYPRP0": "ПРТИ.468211.021",
        "MYPRP2": "ПРТИ.468211.021",
        "MYPRP4": "Пластина опорная нижняя",
        "MYPRP15": "__NONEMPTY__",
    })
    stamp_scenario(asm_path, 2, "T3_stamp_asm.png", {
        "MYPRP0": "ПРТИ.468211.030 СБ",         # склейка Обозначение + Сборка1_ФБ
        "MYPRP2": "ПРТИ.468211.030 СБ",         # графа 26 сборочного чертежа
        "MYPRP4": "Рама кондуктора сварная",
        "MYPRP3": "Сборочный чертёж",           # вторая строка графы 2
        "MYPRP15": "__NONEMPTY__",
    })

    # =====================================================================
    # СЦЕНАРИЙ 5: Защита стандартных и покупных изделий (SProp)
    # =====================================================================
    print("\n--- 6. Сценарий: защита стандартных изделий (SProp) ---")
    sprop_part_path = os.path.join(OUTPUT_DIR, "Test_Bolt_M6.sldprt")
    if os.path.isfile(sprop_part_path):
        try:
            os.remove(sprop_part_path)
        except Exception:
            pass
    sprop_doc = sw.NewDocument(part_template, 0, 0, 0)
    if sprop_doc:
        cpm_sp = sprop_doc.Extension.CustomPropertyManager("")
        cpm_sp.Add3("Раздел", 30, "Стандартные изделия", 1)
        cpm_sp.Add3("Наименование", 30, "Винт М6х20 ГОСТ 11738-84", 1)
        cprop_ok = cpm_sp.Add3("Обозначение", 30, "Винт М6х20", 1)
        cpm_sp.Add3("Материал", 30, "Сталь 45", 1)
        sprop_doc.SaveAs3(sprop_part_path, 0, 1)
        close_doc(sw, sprop_doc)
        spr = file_props(sw, sprop_part_path, 1)
        res.assert_true(spr.get("Наименование", "") == "Винт М6х20 ГОСТ 11738-84",
                        "Сохранение исходного наименования SProp")
        res.assert_true(spr.get("Обозначение", "") == "Винт М6х20",
                        "Сохранение исходного обозначения SProp")

    # =====================================================================
    # СЦЕНАРИЙ 6: Zero-Drift — стабильность координат заметок при сохранении
    # =====================================================================
    print("\n--- 7. Сценарий: Zero-Drift (сохранение чертежа не смещает штамп) ---")
    mdoc = sw.OpenDoc6(tube_path, 1, 1, "", mkref(), mkref())
    drw_doc = None
    try:
        drw_doc = sw.NewDocument(drw_template, 0, 0, 0)
        if drw_doc and mdoc:
            for vn in ("*Спереди", "*Front"):
                try:
                    v = drw_doc.CreateDrawViewFromModelView3(tube_path, vn, 0.10, 0.150, 0.0)
                    if v is not None:
                        break
                except Exception:
                    pass

            def collect_notes(drw_obj):
                note_positions = {}
                v = drw_obj.GetFirstView
                while v is not None:
                    n = v.GetFirstNote
                    while n is not None:
                        n_name = str(n.GetName)
                        ann = n.GetAnnotation
                        if ann:
                            pos = ann.GetPosition
                            if pos:
                                note_positions[n_name] = (pos[0], pos[1], pos[2])
                        n = n.GetNext
                    v = v.GetNextView
                return note_positions

            positions_before = collect_notes(drw_doc)
            res.assert_true(len(positions_before) > 0,
                            f"Зафиксировано заметок в чертеже: {len(positions_before)}")
            drw_path = os.path.join(OUTPUT_DIR, "T3_ZeroDrift.slddrw")
            if os.path.isfile(drw_path):
                try:
                    os.remove(drw_path)
                except Exception:
                    pass
            errors_drw = win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
            warnings_drw = win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
            save_ok = drw_doc.SaveAs4(drw_path, 0, 1, errors_drw, warnings_drw)
            res.assert_true(save_ok and os.path.isfile(drw_path), "Сохранение чертежа (SaveAs4)")
            positions_after = collect_notes(drw_doc)
            max_drift = 0.0
            drifted = []
            for name, (x0, y0, z0) in positions_before.items():
                if name in positions_after:
                    x1, y1, z1 = positions_after[name]
                    drift = max(abs(x1 - x0) * 1000.0, abs(y1 - y0) * 1000.0)
                    if drift > max_drift:
                        max_drift = drift
                    if drift > 0.001:
                        drifted.append(name)
            res.assert_true(max_drift < 0.001,
                            f"Нулевое смещение надписей штампа при сохранении (макс. дрейф {max_drift:.4f} мм)",
                            f"сместились: {drifted}")

            pdf_path = os.path.join(OUTPUT_DIR, "T3_ZeroDrift.pdf")
            err_pdf = win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
            warn_pdf = win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
            drw_doc.SaveAs4(pdf_path, 0, 1, err_pdf, warn_pdf)
            pdf_exists = os.path.isfile(pdf_path)
            pdf_size = os.path.getsize(pdf_path) if pdf_exists else 0
            res.assert_true(pdf_exists and pdf_size > 5000,
                            f"Экспорт векторного PDF ({os.path.basename(pdf_path)}, {pdf_size} байт)")
    finally:
        if drw_doc:
            close_doc(sw, drw_doc)
        if mdoc:
            close_doc(sw, mdoc)

    print("\n" + "=" * 70)
    print(f"  ИТОГ TIER 3: Успешно: {res.passed}, Провалено: {res.failed}, Предупреждений: {res.warnings}")
    print("=" * 70)

    return res.failed == 0


if __name__ == "__main__":
    success = run_tier3_tests()
    sys.exit(0 if success else 1)
