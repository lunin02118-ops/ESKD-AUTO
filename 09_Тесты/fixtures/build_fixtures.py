# -*- coding: utf-8 -*-
"""Сборка корпуса А — эталонных файлов SolidWorks для автотестов.

Файлы строятся через API из корпоративных шаблонов и библиотеки материалов в сессии БЕЗ
надстройки ЕСКД: так выглядит файл, сохранённый конструктором без автоматизации.
Эталонные значения (масса, обозначения) рассчитываются независимо от SolidWorks и
записываются в manifest.json рядом с хешами файлов.

Запуск (SolidWorks должен быть закрыт):  python 09_Тесты/fixtures/build_fixtures.py
"""
import hashlib
import json
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eskd_e2e import build, com, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

SHEET4 = "Лист 4,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
SHEET6 = "Лист 6,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
SHEET3 = "Лист 3,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89"
TUBE80 = "Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86"
TUBE40 = "Труба 40х40х2,0 ГОСТ 8639-82 / Ст3сп ГОСТ 13663-86"

A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A02 = "ПРТИ.468211.102 Стойка.sldprt"
A03 = "ПРТИ.468211.103 Планка.sldprt"
A04 = "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt"
A05 = "Электродвигатель АИР71А4.sldprt"
A06 = "ПРТИ.468211.104 Кронштейн направляющий удлинённый.sldprt"
A07 = "ПРТИ.468211.105 Рама сварная.sldprt"
A08 = "ПРТИ.468211.110 СБ Узел опоры.sldasm"
A09 = "ПРТИ.468211.100 СБ Кондуктор сварочный.sldasm"
A10 = "ПРТИ.468211.101 Пластина опорная.slddrw"
A11 = "ПРТИ.468211.100 СБ Кондуктор сварочный.slddrw"
A13 = "ПРТИ.468211.106 Крышка.sldprt"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def save_new(session, doc, name):
    target = session.ws(name)
    ok, err, warn = session.save_as(doc, target)
    if not ok:
        raise RuntimeError(f"Сохранение {name} не удалось: err={err} warn={warn}")
    return target


def build_all(session, manifest):
    items = manifest["fixtures"]

    # A-01 — пластина из листа 4 мм
    doc, _ = build.plate(session, 200, 100, 4, SHEET4)
    items["A-01"] = {"file": A01, "kind": "part", "material": SHEET4, "designation": "ПРТИ.468211.101",
                     "title": "Пластина опорная", "configs": ["00"],
                     "mass_kg": round(build.analytic_mass_plate(200, 100, 4, SHEET4), 4),
                     "sw_mass_kg": round(build.mass_kg(doc), 4)}
    save_new(session, doc, A01)
    session.close(doc)

    # A-02 — стойка из квадратной трубы 80х80х4, L = 300
    doc, _ = build.square_tube(session, 80, 4, 300, TUBE80)
    items["A-02"] = {"file": A02, "kind": "part", "material": TUBE80, "designation": "ПРТИ.468211.102",
                     "title": "Стойка", "configs": ["00"], "length_mm": 300,
                     "mass_kg": round(build.analytic_mass_square_tube(80, 4, 300, TUBE80), 4),
                     "sw_mass_kg": round(build.mass_kg(doc), 4)}
    save_new(session, doc, A02)
    session.close(doc)

    # A-03 — планка с исполнениями: конфигурации 00/01/02 длиной 100/150/200 мм.
    # Длина набирается тремя вытягиваниями; лишние гасятся в конфигурации.
    doc, _ = build.plate(session, 100, 40, 4, SHEET4)
    base_cfg = str(doc.GetActiveConfiguration.Name)
    build.sketch_rectangles(doc, [(0.05, -0.02, 0.10, 0.02)])
    ext150 = build.extrude(doc, 0.004)
    build.sketch_rectangles(doc, [(0.10, -0.02, 0.15, 0.02)])
    ext200 = build.extrude(doc, 0.004)
    build.add_configuration(doc, "01")
    build.add_configuration(doc, "02")
    # SetSuppression2(погасить=0, в указанных конфигурациях=3, [имена])
    r1 = ext150.SetSuppression2(0, 3, com.str_array([base_cfg]))
    r2 = ext200.SetSuppression2(0, 3, com.str_array([base_cfg, "01"]))
    if not (r1 and r2):
        raise RuntimeError(f"Гашение по конфигурациям не выполнено: {r1}, {r2}")
    lengths = {base_cfg: 100, "01": 150, "02": 200}
    masses = {}
    for cfg in lengths:
        build.show_configuration(doc, cfg)
        doc.ForceRebuild3(False)
        masses[cfg] = round(build.mass_kg(doc), 4)
    build.show_configuration(doc, base_cfg)
    items["A-03"] = {"file": A03, "kind": "part", "material": SHEET4, "designation": "ПРТИ.468211.103",
                     "title": "Планка", "configs": list(lengths),
                     "execution_designations": {base_cfg: "ПРТИ.468211.103", "01": "ПРТИ.468211.103-01",
                                                "02": "ПРТИ.468211.103-02"},
                     "mass_kg": {c: round(build.analytic_mass_plate(l, 40, 4, SHEET4), 4) for c, l in lengths.items()},
                     "sw_mass_kg": masses}
    save_new(session, doc, A03)
    session.close(doc)

    # A-04 — стандартное изделие, оформленное SProp
    doc, _ = build.plate(session, 20, 10, 6, SHEET6)
    build.props(doc, {"Раздел": "Стандартные изделия", "IsFastener": "1",
                      "Наименование": "Болт М6-6gх20.58 ГОСТ 7798-70", "Обозначение": ""})
    items["A-04"] = {"file": A04, "kind": "standard", "protected": True}
    save_new(session, doc, A04)
    session.close(doc)

    # A-05 — покупное изделие с полями ведомости покупных
    doc, _ = build.plate(session, 200, 120, 10, SHEET6)
    build.props(doc, {"Раздел": "Прочие изделия", "Наименование_ВП": "Электродвигатель АИР71А4",
                      "Поставщик": "ООО «Электромаш»", "Код_Продукции": "33 1111"})
    items["A-05"] = {"file": A05, "kind": "purchased", "protected": True}
    save_new(session, doc, A05)
    session.close(doc)

    # A-06 — длинное наименование
    doc, _ = build.plate(session, 150, 60, 6, SHEET6)
    items["A-06"] = {"file": A06, "kind": "part", "material": SHEET6, "designation": "ПРТИ.468211.104",
                     "title": "Кронштейн направляющий удлинённый", "configs": ["00"],
                     "mass_kg": round(build.analytic_mass_plate(150, 60, 6, SHEET6), 4),
                     "sw_mass_kg": round(build.mass_kg(doc), 4)}
    save_new(session, doc, A06)
    session.close(doc)

    # A-07 — сварная деталь из трёх труб 40х40х2 (три тела, список вырезов)
    doc = session.new_doc(paths.PART_TEMPLATE)
    for dx in (-0.1, 0.0, 0.1):
        build.sketch_rectangles(doc, [(dx - 0.02, -0.02, dx + 0.02, 0.02), (dx - 0.018, -0.018, dx + 0.018, 0.018)])
        build.extrude(doc, 0.5, merge=False)
    doc.ClearSelection2(True)
    com.call(doc.FeatureManager, "InsertWeldmentFeature")
    build.set_material(doc, TUBE40)
    doc.ForceRebuild3(False)
    bodies = com.as_list(doc.GetBodies2(0, True))
    items["A-07"] = {"file": A07, "kind": "weldment", "material": TUBE40, "designation": "ПРТИ.468211.105",
                     "title": "Рама сварная", "bodies": len(bodies),
                     "mass_kg": round(3 * build.analytic_mass_square_tube(40, 2, 500, TUBE40), 4),
                     "sw_mass_kg": round(build.mass_kg(doc), 4)}
    save_new(session, doc, A07)
    session.close(doc)

    # A-08 — подсборка: A-01 ×2 + A-04 ×4
    p01, p04 = session.run_dir / A01, session.run_dir / A04
    asm, opened = build.assembly(session, [(p01, 0, 0, 0), (p01, 0, 0.2, 0), (p04, 0.3, 0, 0), (p04, 0.35, 0, 0),
                                           (p04, 0.4, 0, 0), (p04, 0.45, 0, 0)])
    items["A-08"] = {"file": A08, "kind": "assembly", "designation": "ПРТИ.468211.110", "code": "СБ",
                     "title": "Узел опоры", "components": 6}
    save_new(session, asm, A08)
    session.close(asm)
    for d in opened:
        session.close(d)

    # A-09 — верхняя сборка
    comps = [(session.run_dir / A08, 0, 0, 0), (session.run_dir / A02, 0.6, 0, 0), (session.run_dir / A02, 0.8, 0, 0),
             (session.run_dir / A03, 1.0, 0, 0), (session.run_dir / A05, 1.3, 0, 0), (session.run_dir / A06, 1.6, 0, 0),
             (session.run_dir / A07, 2.0, 0, 0)]
    asm, opened = build.assembly(session, comps)
    items["A-09"] = {"file": A09, "kind": "assembly", "designation": "ПРТИ.468211.100", "code": "СБ",
                     "title": "Кондуктор сварочный", "components": len(comps)}
    save_new(session, asm, A09)
    session.close(asm)
    for d in opened:
        session.close(d)

    # A-10 — чертёж пластины: лист 1 A3-A-1, лист 2 A4-P-2
    model = session.open(session.run_dir / A01)
    drw = session.new_doc(paths.DRAWING_TEMPLATE)
    build.set_sheet_format(drw, build.sheet_format("A3-A-1"), 420, 297)
    build.model_view(drw, session.run_dir / A01, 150, 180)
    build.add_sheet(drw, "Лист2", build.sheet_format("A4-P-2"), 210, 297)
    build.model_view(drw, session.run_dir / A01, 100, 160, "*Изометрия")
    drw.ActivateSheet(str(com.as_list(drw.GetSheetNames)[0]))
    build.wait(1.5)
    items["A-10"] = {"file": A10, "kind": "drawing", "model": A01, "sheets": [["A3-A-1", 420, 297], ["A4-P-2", 210, 297]]}
    save_new(session, drw, A10)
    session.close(drw)
    session.close(model)

    # A-11 — сборочный чертёж A2-A-1
    model = session.open(session.run_dir / A09)
    drw = session.new_doc(paths.DRAWING_TEMPLATE)
    build.set_sheet_format(drw, build.sheet_format("A2-A-1"), 594, 420)
    build.model_view(drw, session.run_dir / A09, 250, 260)
    build.wait(1.5)
    items["A-11"] = {"file": A11, "kind": "drawing", "model": A09, "sheets": [["A2-A-1", 594, 420]]}
    save_new(session, drw, A11)
    session.close(drw)
    session.close(model)

    # A-13 — «чужая» деталь: подписи и ручное обозначение по правилам MProp
    doc, _ = build.plate(session, 120, 80, 3, SHEET3)
    cfg = str(doc.GetActiveConfiguration.Name)
    build.props(doc, {"Обозначение": "ПРТИ.468211.199", "Наименование": "Крышка", "Конструктор": "Петров П.П.",
                      "RenameSWP": "1"})
    build.props(doc, {"Обозначение": "ПРТИ.468211.199", "Контора": "ООО «Вектор»", "Проверил": "Сидоров С.С."},
                config=cfg)
    items["A-13"] = {"file": A13, "kind": "foreign", "designation": "ПРТИ.468211.199", "title": "Крышка",
                     "signatures": {"Конструктор": "Петров П.П.", "Контора": "ООО «Вектор»", "Проверил": "Сидоров С.С."}}
    save_new(session, doc, A13)
    session.close(doc)


def check_masses(manifest):
    """Масса, посчитанная SolidWorks, обязана совпадать с аналитической (0,5 %)."""
    problems = []
    for fid, item in manifest["fixtures"].items():
        expected, measured = item.get("mass_kg"), item.get("sw_mass_kg")
        if expected is None or measured is None:
            continue
        pairs = expected.items() if isinstance(expected, dict) else [("", expected)]
        for cfg, exp in pairs:
            got = measured[cfg] if isinstance(measured, dict) else measured
            if abs(got - exp) > max(0.005 * exp, 0.0005):
                problems.append(f"{fid} {cfg}: SolidWorks {got} кг, расчёт {exp} кг")
    if problems:
        raise RuntimeError("Геометрия фикстур не совпала с расчётом: " + "; ".join(problems))


def main():
    run_dir = paths.RUNS / ("fixtures_" + time.strftime("%Y%m%d_%H%M%S"))
    manifest = {"sw_revision": "", "built": time.strftime("%Y-%m-%d %H:%M:%S"), "templates": {}, "fixtures": {}}
    for t in (paths.PART_TEMPLATE, paths.ASSEMBLY_TEMPLATE, paths.DRAWING_TEMPLATE):
        manifest["templates"][t.name] = sha256(t)
    with SwSession(run_dir, load_eskd=False) as session:
        manifest["sw_revision"] = str(session.sw.RevisionNumber())
        build_all(session, manifest)
        check_masses(manifest)
        unexpected = session.watchdog.pop_unexpected()
        if unexpected:
            raise RuntimeError(f"Неожиданные диалоги при сборке фикстур: {unexpected}")
        if session.violations():
            raise RuntimeError("Зонд зафиксировал сохранение вне каталога прогона")
    paths.FIXTURES_A.mkdir(parents=True, exist_ok=True)
    for item in manifest["fixtures"].values():
        src = run_dir / item["file"]
        dst = paths.FIXTURES_A / item["file"]
        shutil.copy2(src, dst)
        item["sha256"] = sha256(dst)
    paths.FIXTURE_MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(manifest, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
