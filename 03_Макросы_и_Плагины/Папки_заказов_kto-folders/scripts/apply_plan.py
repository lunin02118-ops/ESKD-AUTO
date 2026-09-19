"""Выполняет plan.csv из sort_plan.py. Ничего не удаляет.

python apply_plan.py --plan <папка плана>\\plan.csv            # только показать (по умолчанию)
python apply_plan.py --plan ... --apply                        # копировать
python apply_plan.py --plan ... --apply --move --quarantine <папка>   # копировать, сверить, исходник -> карантин

- создаёт все папки из folders.txt (полное дерево заказа, даже пустые);
- копирует с сохранением дат во временный файл, сверяет SHA-256, затем переименовывает;
- если в месте назначения уже есть файл: тот же SHA-256 — пропуск; другой — новый файл кладётся
  в <папка назначения>\\_Конфликт_<дата>\\ и попадает в журнал (ничего не перезаписывается);
- --move: исходник переносится в карантин с сохранением относительного пути (не удаляется);
- журнал: apply_log.csv рядом с планом.
"""
import argparse
import csv
import datetime
import hashlib
import json
import os
import shutil
import sys


def long_path(p):
    p = os.path.abspath(p)
    if os.name == "nt" and not p.startswith("\\\\?\\"):
        return "\\\\?\\UNC\\" + p[2:] if p.startswith("\\\\") else "\\\\?\\" + p
    return p


def sha256(path):
    h = hashlib.sha256()
    with open(long_path(path), "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--plan", required=True)
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--move", action="store_true")
    ap.add_argument("--quarantine")
    a = ap.parse_args()
    if a.move and not a.quarantine:
        ap.error("--move требует --quarantine")

    plan_dir = os.path.dirname(os.path.abspath(a.plan))
    meta = json.load(open(os.path.join(plan_dir, "meta.json"), encoding="utf-8"))
    with open(a.plan, encoding="utf-8-sig") as f:
        rows = list(csv.DictReader(f, delimiter=";"))
    folders_file = os.path.join(plan_dir, "folders.txt")
    folders = [l.strip() for l in open(folders_file, encoding="utf-8") if l.strip()] if os.path.exists(folders_file) else []

    todo = [r for r in rows if r["action"] == "copy"]
    print(f"Заказ: {meta['order']}\nФайлов в плане: {len(rows)}, к переносу: {len(todo)}, "
          f"пропуск: {len(rows) - len(todo)}, папок создать: {len(folders)}")
    print("Режим:", "ПЕРЕНОС (копия → сверка → исходник в карантин)" if a.move else "КОПИРОВАНИЕ (исходник не меняется)")
    if not a.apply:
        print("Это пробный запуск. Для выполнения добавьте --apply.")
        return

    for d in folders:
        os.makedirs(long_path(d), exist_ok=True)

    stamp = datetime.datetime.now().strftime("%Y-%m-%d_%H%M")
    log = []
    ok = err = same = conflict = 0
    for r in todo:
        src, dst, want = r["src"], r["dst"], r["sha256"]
        status, final = "", dst
        try:
            if not os.path.exists(long_path(src)):
                raise FileNotFoundError("исходник пропал")
            if sha256(src) != want:
                raise RuntimeError("исходник изменился после построения плана — перестройте план")
            if os.path.exists(long_path(dst)):
                if sha256(dst) == want:
                    status = "уже есть"
                    same += 1
                else:
                    final = os.path.join(os.path.dirname(dst), f"_Конфликт_{stamp}", os.path.basename(dst))
                    conflict += 1
            if not status:
                os.makedirs(long_path(os.path.dirname(final)), exist_ok=True)
                tmp = final + ".part"
                shutil.copy2(long_path(src), long_path(tmp))
                if sha256(tmp) != want:
                    raise RuntimeError("контрольная сумма копии не совпала (временный файл оставлен: .part)")
                os.replace(long_path(tmp), long_path(final))
                status = "скопирован" if final == dst else "конфликт имени"
                ok += 1
            if a.move:
                q = os.path.join(a.quarantine, os.path.relpath(src, meta["src"]))
                os.makedirs(long_path(os.path.dirname(q)), exist_ok=True)
                if os.path.exists(long_path(q)):
                    raise RuntimeError(f"в карантине уже есть {q}")
                shutil.move(long_path(src), long_path(q))
                status += " + исходник в карантин"
        except Exception as e:  # noqa: BLE001 — журналируем и идём дальше
            status = f"ОШИБКА: {e}"
            err += 1
        log.append({"src": src, "dst": final, "status": status, "sha256": want})

    log_path = os.path.join(plan_dir, "apply_log.csv")
    with open(log_path, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, ["src", "dst", "status", "sha256"], delimiter=";")
        w.writeheader()
        w.writerows(log)
    print(f"Готово: скопировано {ok}, уже было {same}, конфликтов имён {conflict}, ошибок {err}. Журнал: {log_path}")
    sys.exit(1 if err else 0)


if __name__ == "__main__":
    main()
