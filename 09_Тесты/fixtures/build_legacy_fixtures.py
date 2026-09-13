# -*- coding: utf-8 -*-
"""Фикстура A-14 — файлы, сохранённые надстройкой v5 (миграция WP-3.3, сценарии R03 и R04).

SolidWorks загружает надстройку только по зарегистрированному пути, поэтому на время сборки фикстуры собранная v6
в каталоге надстройки подменяется DLL v5 из истории git (последний коммит, где сборка лежала в репозитории).
После выхода SolidWorks v6 возвращается на место и сверяется с build_manifest.json. Что загружена именно v5,
проверяется по отсутствию метода GetVersion.

    python fixtures/build_legacy_fixtures.py
"""
import hashlib
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eskd_e2e import com, paths  # noqa: E402
from eskd_e2e.session import SwSession  # noqa: E402

V5_COMMIT = "855bdd7"
DLL_IN_GIT = "03_Макросы_и_Плагины/ESKD_Material_Sync_Addin/ESKD_Material_Sync_v5.dll"
A01 = "ПРТИ.468211.101 Пластина опорная.sldprt"
A04 = "Болт М6-6gх20.58 ГОСТ 7798-70.sldprt"
A08 = "ПРТИ.468211.110 СБ Узел опоры.sldasm"
A14_PART = "ПРТИ.468211.107 Кожух.sldprt"
A14_ASSEMBLY = "ПРТИ.468211.108 СБ Узел.sldasm"


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    run = paths.RUNS / ("legacy_fixtures_" + time.strftime("%Y%m%d_%H%M%S"))
    run.mkdir(parents=True)
    manifest_path = paths.ADDIN_DLL.parent / "build_manifest.json"
    v6_sha = json.loads(manifest_path.read_text(encoding="utf-8-sig"))["outputs"]["ESKD_Material_Sync_v5.dll"]
    if sha256(paths.ADDIN_DLL) != v6_sha:
        raise SystemExit("Собранная надстройка не совпадает с build_manifest.json — сначала запустите build.ps1")
    v6_backup = run / "v6_ESKD_Material_Sync_v5.dll"
    shutil.copy2(paths.ADDIN_DLL, v6_backup)
    v5 = subprocess.run(["git", "-C", str(paths.ROOT), "show", f"{V5_COMMIT}:{DLL_IN_GIT}"], capture_output=True, check=True).stdout

    work = run / "work"
    try:
        paths.ADDIN_DLL.write_bytes(v5)
        with SwSession(work, load_eskd=True, use_probe=False) as s:
            try:
                com.call(s.eskd(), "GetVersion")
                raise SystemExit("Загрузилась v6 вместо v5 — фикстура не собрана")
            except SystemExit:
                raise
            except Exception:
                pass  # у v5 нет GetVersion

            part_path = s.workspace_copy(paths.FIXTURES_A / A01, name=A14_PART, subdir="part")
            doc = s.open(part_path)
            s.activate(doc)
            state = int(com.call(s.eskd(), "ToggleDrawinglessSilent"))
            ok, err, warn = s.save(doc)
            s.close(doc)
            print("деталь v5: БЧ =", state, "сохранение =", ok, err, warn)

            for component in (A01, A04):
                s.workspace_copy(paths.FIXTURES_A / component, subdir="assembly")
            assembly_path = s.workspace_copy(paths.FIXTURES_A / A08, name=A14_ASSEMBLY, subdir="assembly")
            doc = s.open(assembly_path)
            ok, err, warn = s.save(doc)
            s.close(doc)
            print("сборка v5: сохранение =", ok, err, warn)
    finally:
        shutil.copy2(v6_backup, paths.ADDIN_DLL)
        restored = sha256(paths.ADDIN_DLL) == v6_sha
        print("надстройка v6 восстановлена:", restored)
        if not restored:
            raise SystemExit("ВНИМАНИЕ: DLL надстройки не совпадает с build_manifest.json — запустите build.ps1")

    shutil.copy2(work / "part" / A14_PART, paths.FIXTURES_A / A14_PART)
    shutil.copy2(work / "assembly" / A14_ASSEMBLY, paths.FIXTURES_A / A14_ASSEMBLY)
    manifest = json.loads(paths.FIXTURE_MANIFEST.read_text(encoding="utf-8"))
    manifest["fixtures"]["A-14"] = {
        "file": A14_PART, "assembly": A14_ASSEMBLY, "components": [A01, A04], "kind": "legacy_v5",
        "source": f"A-01 и A-08, открыты и сохранены надстройкой v5 ({V5_COMMIT}); у детали включена «Деталь БЧ»",
        "sha256": {name: sha256(paths.FIXTURES_A / name) for name in (A14_PART, A14_ASSEMBLY)},
    }
    paths.FIXTURE_MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")
    print("A-14 записана в", paths.FIXTURES_A)


if __name__ == "__main__":
    main()
