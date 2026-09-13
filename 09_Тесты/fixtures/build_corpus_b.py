# -*- coding: utf-8 -*-
"""Корпус Б с сортаментом из корпоративной библиотеки (решение владельца 13.09.2026).

Реальные файлы архива (paths.CORPUS_B) остаются как есть — на них R01 сравнивает поведение с v5. Этот скрипт делает их
копии и штатно назначает в них материал так, как это сделал бы конструктор: сортамент из библиотеки по геометрии тела.
Надстройка ЕСКД не загружается — свойства копии остаются прежними (в том числе старая дробь MProp в графе 3), и рабочие
сценарии проверяют, что делает с такой деталью сохранение.

    python 09_Тесты/fixtures/build_corpus_b.py        (SolidWorks должен быть закрыт)

B-01 «ПРТИ.468211.010 Стойка»: тело есть только у конфигурации «Труба 80х80х4» (профиль 80×80, стенка 4, L = 500),
ей назначается «Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86»; остальные 78 конфигураций — строки таблицы
профилей без тел, материал им не нужен. Чертёж ПРТИ.468211.010.SLDDRW копируется рядом и ссылается на копию детали.
B-02 (деталь без геометрии — проверка имён исполнений) и B-03 (сварная деталь; трубы 120×80×4 и уголка 40×40×4 в
библиотеке нет) рабочими сценариями не используются и в этот корпус не входят.
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

TUBE80 = "Труба 80х80х4,0 ГОСТ 8639-82 / В 10 ГОСТ 13663-86"
PLAN = {
    "B-01": {"part": paths.CORPUS_B["B-01"][0], "drawing": paths.CORPUS_B["B-01"][1],
             "materials": {"Труба 80х80х4": TUBE80},
             "note": "тело есть только у конфигурации «Труба 80х80х4»; остальные конфигурации — таблица профилей без тел"},
}


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def build_copy(session, fid, plan):
    part = session.workspace_copy(plan["part"], subdir=fid)
    drawing = session.workspace_copy(plan["drawing"], subdir=fid) if plan.get("drawing") else None
    doc = session.open(part)
    try:
        configurations = [str(c) for c in com.as_list(doc.GetConfigurationNames)]
        before = {cfg: build.material_of(doc, cfg) for cfg in configurations}
        for cfg, material in plan["materials"].items():
            build.set_material(doc, material, cfg)
        after = {cfg: build.material_of(doc, cfg) for cfg in configurations}
        for cfg in configurations:
            expected = plan["materials"].get(cfg)
            if expected is not None and after[cfg][0] != expected:
                raise RuntimeError(f"{fid} «{cfg}»: назначен «{after[cfg][0]}» вместо «{expected}»")
            if expected is None and after[cfg] != before[cfg]:
                raise RuntimeError(f"{fid} «{cfg}»: материал изменился без плана: {before[cfg]} → {after[cfg]}")
        active = str(doc.GetActiveConfiguration.Name)
        masses = {}
        for cfg in plan["materials"]:
            doc.ShowConfiguration2(cfg)
            masses[cfg] = round(float(com.dyn(doc.Extension.CreateMassProperty).Mass), 4)
        doc.ShowConfiguration2(active)
        ok, err, warn = session.save(doc)
        if not ok:
            raise RuntimeError(f"{fid}: сохранение не удалось: err={err} warn={warn}")
    finally:
        session.close(doc)
    return part, drawing, masses, {cfg: list(v) for cfg, v in before.items() if cfg in plan["materials"]}


def main():
    run_dir = paths.RUNS / ("corpus_b_" + time.strftime("%Y%m%d_%H%M%S"))
    manifest = json.loads(paths.FIXTURE_MANIFEST.read_text(encoding="utf-8"))
    corpus = {}
    with SwSession(run_dir, load_eskd=False) as session:
        built = {fid: build_copy(session, fid, plan) for fid, plan in PLAN.items()}
        unexpected = session.watchdog.pop_unexpected()
        if unexpected:
            raise RuntimeError(f"Неожиданные диалоги: {unexpected}")
    paths.FIXTURES_B.mkdir(parents=True, exist_ok=True)
    for fid, (part, drawing, masses, before) in built.items():
        plan = PLAN[fid]
        entry = {"source": str(plan["part"].relative_to(paths.ROOT)).replace("\\", "/"), "source_sha256": sha256(plan["part"]),
                 "file": part.name, "materials": plan["materials"], "materials_before": before, "mass_kg": masses,
                 "note": plan["note"]}
        shutil.copy2(part, paths.FIXTURES_B / part.name)
        entry["sha256"] = sha256(paths.FIXTURES_B / part.name)
        if drawing is not None:
            shutil.copy2(drawing, paths.FIXTURES_B / drawing.name)
            entry["drawing"] = drawing.name
            entry["drawing_sha256"] = sha256(paths.FIXTURES_B / drawing.name)
        corpus[fid] = entry
    manifest["corpus_b"] = corpus
    paths.FIXTURE_MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(corpus, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
