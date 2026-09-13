# -*- coding: utf-8 -*-
"""Внешний зонд событий ESKD_ProbeHost: сборка, запуск, команды, журнал."""
import json
import os
import shutil
import subprocess
import time
from pathlib import Path

from . import paths

INTEROPS = ("SolidWorks.Interop.sldworks.dll", "SolidWorks.Interop.swconst.dll")


def build_probe(force=False):
    """Компилирует ESKD_ProbeHost.exe системным csc, если исходник новее сборки."""
    exe = paths.PROBE_EXE
    src = paths.PROBE_SOURCE
    if exe.exists() and not force and exe.stat().st_mtime >= src.stat().st_mtime:
        return exe
    paths.PROBE_BIN.mkdir(parents=True, exist_ok=True)
    refs = []
    for name in INTEROPS:
        target = paths.PROBE_BIN / name
        shutil.copy2(paths.SW_INTEROP_DIR / name, target)
        refs.append(f"/r:{target}")
    cmd = [str(paths.CSC), "/nologo", "/target:winexe", "/platform:x64", "/optimize+", "/codepage:65001",
           f"/out:{exe}", "/r:System.dll", "/r:System.Windows.Forms.dll"] + refs + [str(src)]
    proc = subprocess.run(cmd, capture_output=True)
    if proc.returncode != 0:
        out = proc.stdout.decode("cp866", errors="replace") + proc.stderr.decode("cp866", errors="replace")
        raise RuntimeError("Сборка зонда не удалась:\n" + out)
    return exe


class ProbeClient:
    """Запускает зонд рядом с тестовой сессией и передаёт ему команды через файлы."""

    def __init__(self, run_dir, flags=None):
        self.flags = dict(flags or {})
        self.run_dir = Path(run_dir)
        self.control = self.run_dir / "_probe"
        self.journal_path = self.run_dir / "events.jsonl"
        self.proc = None
        self._n = 0

    def start(self, attach_timeout=120):
        build_probe()
        self.control.mkdir(parents=True, exist_ok=True)
        for f in self.control.glob("*.json"):
            f.unlink()
        args = [str(paths.PROBE_EXE), "--journal", str(self.journal_path), "--workspace", str(self.run_dir),
                "--control", str(self.control), "--parent-pid", str(os.getpid()),
                "--attach-timeout", str(attach_timeout)]
        for k, v in self.flags.items():
            args += [f"--{k}", str(v)]
        self.proc = subprocess.Popen(args, creationflags=subprocess.CREATE_NO_WINDOW)
        journal = Journal(self.journal_path)
        deadline = time.time() + attach_timeout + 10
        while time.time() < deadline:
            evs = [e["event"] for e in journal.events()]
            if "ProbeConnected" in evs:
                return self
            if "ProbeAttachFailed" in evs or self.proc.poll() is not None:
                raise RuntimeError("Зонд не подключился к SolidWorks")
            time.sleep(0.3)
        raise RuntimeError("Зонд не подключился к SolidWorks за отведённое время")

    def call(self, op, arg="", timeout=30):
        self._n += 1
        cid = f"{self._n:06d}"
        tmp = self.control / f"tmp_{cid}.json"
        tmp.write_text(json.dumps({"op": op, "arg": str(arg)}, ensure_ascii=False), encoding="utf-8")
        for attempt in range(50):
            try:
                tmp.replace(self.control / f"cmd_{cid}.json")
                break
            except PermissionError:
                if attempt == 49:
                    raise
                time.sleep(0.02)
        ack = self.control / f"ack_{cid}.json"
        deadline = time.time() + timeout
        while time.time() < deadline:
            if ack.exists():
                try:
                    data = json.loads(ack.read_text(encoding="utf-8"))
                except (ValueError, PermissionError):
                    # Ответ только что переименован зондом и ещё занят (переименование, проверка антивирусом):
                    # PermissionError в setUp однажды уронил тест P09 при полном прогоне.
                    time.sleep(0.02)
                    continue
                self._remove(ack)
                return data.get("result")
            if self.proc is not None and self.proc.poll() is not None:
                raise RuntimeError(f"Зонд завершился, команда {op} не выполнена")
            time.sleep(0.02)
        raise TimeoutError(f"Зонд не ответил на команду {op}")

    @staticmethod
    def _remove(path, attempts=50):
        """Удаление прочитанного ответа; занятый файл не мешает следующим командам — у них другие номера."""
        for _ in range(attempts):
            try:
                path.unlink()
                return
            except FileNotFoundError:
                return
            except PermissionError:
                time.sleep(0.02)

    def stop(self):
        if self.proc is None:
            return
        if self.proc.poll() is None:
            try:
                self.call("stop", timeout=10)
            except Exception:
                pass
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
        self.proc = None

    # удобные обёртки
    def mark(self, label):
        self.call("mark", label)
        return label

    def queue_save_as(self, path):
        self.call("saveas", path)

    def pending_save_as(self):
        return int(self.call("pending_saveas") or 0)

    def clear_save_as(self):
        self.call("clear_saveas")

    def violations(self):
        return int(self.call("violations") or 0)


class Journal:
    """Чтение JSONL-журнала зонда."""

    PROPERTY_EVENTS = ("AddCustomPropertyNotify", "ChangeCustomPropertyNotify", "DeleteCustomPropertyNotify")

    def __init__(self, path):
        self.path = Path(path)

    def events(self, mark=None, since_seq=0):
        if not self.path.exists():
            return []
        out = []
        with open(self.path, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except ValueError:
                    continue
                if rec.get("seq", 0) <= since_seq:
                    continue
                if mark is not None and rec.get("mark") != mark:
                    continue
                out.append(rec)
        return out

    def property_writes(self, mark=None, title=None):
        return [e for e in self.events(mark) if e["event"] in self.PROPERTY_EVENTS
                and (title is None or e.get("title", "").lower() == title.lower())]

    def saves(self, mark=None):
        return [e for e in self.events(mark) if e["event"] == "FileSavePostNotify"]

    def of(self, event, mark=None):
        return [e for e in self.events(mark) if e["event"] == event]
