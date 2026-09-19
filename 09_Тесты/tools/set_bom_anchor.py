# -*- coding: utf-8 -*-
"""Точка привязки спецификации (BOM) в форматках первого листа (замечание владельца 19.09.2026).

Все форматки SWPlus несли точку привязки BOM листа A4 — (205; 68) мм, над основной надписью. SpecEditor вставляет
спецификацию на лист сборки правым нижним углом в эту точку, и на A3 и больших листах таблица вставала посреди листа.
На листе шире 395 мм спецификация (185 мм) помещается слева от основной надписи: точка (205; 5) мм — левый нижний
угол таблицы в углу рамки (20; 5), таблица растёт вверх. Узкие листы (A4, A3 книжный) и форматки последующих листов
(«-2») не меняются.

Точка ставится штатно: в режиме правки форматки создаётся точка эскиза и назначается точкой привязки BOM
(ISheet.SetAsTableAnchor). SolidWorks работает с копиями в каталоге прогона, без надстройки; в репозиторий копии
попадают только с ключом --apply и только если у всех после повторного открытия точка на месте.

    python tools/set_bom_anchor.py            # правка и проверка на копиях, репозиторий не меняется
    python tools/set_bom_anchor.py --apply    # то же и замена файлов в репозитории
"""
import argparse
import json
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eskd_e2e import com, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

BOM = 2  # swTableAnnotation_BillOfMaterials
WANTED = (0.205, 0.005)
MIN_WIDTH = 0.395


def bom_anchor(doc):
    a = com.dyn(doc.GetCurrentSheet).TableAnchor(BOM)
    return tuple(round(float(x), 4) for x in com.dyn(a).Position) if a is not None else None


def sheet_width(doc):
    return float(list(com.dyn(doc.GetCurrentSheet).GetProperties2)[5])


def targets():
    """Форматки первого листа и шаблоны чертежей: шаблон хранит свою копию форматки, и SolidWorks не перечитывает
    форматку с тем же именем — без правки шаблона новый чертёж A3 получает прежнюю точку."""
    for source in sorted(paths.SHEET_FORMATS.glob("*-1.slddrt")):
        # .slddrt SolidWorks открывает только как чертёж — переименование на время правки (как normalize_formats.py)
        yield source, source.stem + ".SLDDRW"
    for source in sorted(paths.TEMPLATES.glob("*.drwdot")):
        yield source, None


def sheets(doc):
    for name in list(doc.GetSheetNames or []):
        doc.ActivateSheet(name)
        yield str(name)


def set_anchor(doc):
    doc.EditTemplate()
    sketch = com.dyn(doc.SketchManager)
    sketch.AddToDB = True
    point = sketch.CreatePoint(WANTED[0], WANTED[1], 0)
    sketch.AddToDB = False
    doc.ClearSelection2(True)
    if point is None or not com.dyn(point).Select4(False, com.null_dispatch()):
        raise RuntimeError("точка эскиза не создана или не выделена")
    com.dyn(doc.GetCurrentSheet).SetAsTableAnchor(BOM)
    doc.ClearSelection2(True)
    doc.EditSheet()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--apply", action="store_true", help="заменить файлы в репозитории проверенными копиями")
    args = ap.parse_args()
    run = paths.RUNS / ("formats_bom_anchor_" + time.strftime("%Y%m%d_%H%M%S"))
    report = {}
    with SwSession(run, load_eskd=False, use_probe=False) as session:
        for source, work_name in targets():
            work = session.workspace_copy(source, subdir="work/" + source.parent.name, name=work_name)
            item = report[source.name] = {"source": str(source), "work": str(work), "sheets": {}}
            try:
                doc = session.open(work)
                try:
                    for sheet in sheets(doc):
                        state = item["sheets"][sheet] = {"width_mm": round(sheet_width(doc) * 1000), "before": bom_anchor(doc)}
                        if sheet_width(doc) >= MIN_WIDTH and state["before"] != WANTED:
                            set_anchor(doc)
                            state["set"] = True
                    item["skip"] = not any(s.get("set") for s in item["sheets"].values())
                    if not item["skip"]:
                        ok, err, warn = session.save(doc)
                        if not ok:
                            raise RuntimeError(f"Save3 errors={err} warnings={warn}")
                finally:
                    session.close(doc)
                if not item["skip"]:
                    doc = session.open(work)
                    for sheet in sheets(doc):
                        if item["sheets"][sheet].get("set"):
                            item["sheets"][sheet]["after"] = bom_anchor(doc)
                    session.close(doc)
            except Exception as exc:
                item["error"] = repr(exc)
    (run / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    changed = {k: v for k, v in report.items() if not v.get("skip")}
    bad = {k: v.get("error") or v["sheets"] for k, v in changed.items()
           if v.get("error") or any(s.get("set") and s.get("after") != WANTED for s in v["sheets"].values())}
    for name, item in report.items():
        for sheet, s in item["sheets"].items():
            print(f"{name} [{sheet}]: {s['width_mm']} мм, {s['before']} → {s.get('after', 'без изменений')}")
    if bad:
        print("НЕ ИСПРАВЛЕНЫ:", json.dumps(bad, ensure_ascii=False))
        print("Файлы в репозитории не изменены.")
        return 1
    if args.apply:
        for name, item in changed.items():
            shutil.copy2(item["work"], item["source"])  # целевое имя с исходным расширением
        print(f"Заменено файлов: {len(changed)}. Каталог: {run}")
    else:
        print(f"Проверка пройдена; для замены запустите с --apply. Каталог: {run}")
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
