# -*- coding: utf-8 -*-
"""T1 — юнит-тесты ядра надстройки на настоящей сборке (ESKD.Tests.exe, вывод TAP)."""
import re
import shutil
import subprocess
import unittest

from eskd_e2e import paths

UNIT_DIR = paths.TESTS / "unit"
BIN = UNIT_DIR / "bin"
EXE = BIN / "ESKD.Tests.exe"
REFS = ("ESKD_Material_Sync_v5.dll", "SolidWorks.Interop.sldworks.dll", "SolidWorks.Interop.swconst.dll",
        "SolidWorks.Interop.swpublished.dll")


def build():
    BIN.mkdir(parents=True, exist_ok=True)
    for name in REFS:
        shutil.copy2(paths.ADDIN_DIR / name, BIN / name)
    sources = sorted(str(p) for p in UNIT_DIR.glob("*.cs"))
    cmd = [str(paths.CSC), "/nologo", "/target:exe", "/platform:anycpu", "/codepage:65001", f"/out:{EXE}"]
    cmd += [f"/r:{BIN / name}" for name in REFS] + ["/r:System.dll", "/r:System.Xml.dll", "/r:System.Core.dll"] + sources
    proc = subprocess.run(cmd, capture_output=True)
    if proc.returncode != 0:
        raise RuntimeError("Сборка ESKD.Tests.exe не удалась:\n" +
                           proc.stdout.decode("cp866", errors="replace") + proc.stderr.decode("cp866", errors="replace"))


class UnitTests(unittest.TestCase):
    def test_T1_core_unit_tests(self):
        """T1: юнит-тесты чистых модулей ядра (разбор имён, происхождение, разметка, БЧ, словарь, материалы)."""
        build()
        proc = subprocess.run([str(EXE)], capture_output=True, cwd=str(BIN), timeout=120)
        out = proc.stdout.decode("utf-8", errors="replace")
        lines = [ln for ln in out.splitlines() if re.match(r"^(ok|not ok) \d+", ln)]
        failed = [ln for ln in lines if ln.startswith("not ok")]
        plan = re.search(r"^1\.\.(\d+)", out, re.MULTILINE)
        self.assertTrue(plan and int(plan.group(1)) == len(lines), f"раннер не выполнил все тесты:\n{out}")
        self.assertGreater(len(lines), 0, "нет ни одного юнит-теста")
        self.assertEqual([], failed, "\n".join(failed))


if __name__ == "__main__":
    unittest.main()
