# -*- coding: utf-8 -*-
"""
Master Test Runner: Единый запуск всех уровней тестового покрытия системы ЕСКД / SolidWorks 2025.
Координирует выполнение Tier 1, Tier 2, Tier 3 и Tier 4, генерируя сводный отчет TEST_REPORT.md.
"""

import os
import sys
import time
import subprocess
from datetime import datetime

# Configure console output for UTF-8
try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
REPORT_MD = os.path.join(SCRIPT_DIR, "TEST_REPORT.md")


def run_command(cmd, desc, timeout=180):
    print(f"\n>>> Запуск: {desc}...")
    t0 = time.time()
    try:
        proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace', timeout=timeout)
        dt = time.time() - t0
        output = proc.stdout
        success = (proc.returncode == 0)
        print(output)
        return success, output, dt
    except subprocess.TimeoutExpired:
        dt = time.time() - t0
        msg = f"ОШИБКА: Превышен таймаут выполнения ({timeout} сек) для {desc}!"
        print(msg)
        return False, msg, dt
    except Exception as e:
        dt = time.time() - t0
        msg = f"ОШИБКА запуска {desc}: {e}"
        print(msg)
        return False, msg, dt


def main():
    print("=" * 80)
    print("  КОМПЛЕКСНЫЙ ЗАПУСК ВСЕХ ТЕСТОВ ЭКОСИСТЕМЫ SOLIDWORKS 2025 (ЕСКД / SWPLUS)")
    print("=" * 80)

    start_time = datetime.now()
    results = {}

    # Tier 1: Static Sanity
    tier1_py = os.path.join(SCRIPT_DIR, "test_tier1_static_sanity.py")
    s1, out1, t1 = run_command([sys.executable, tier1_py], "Tier 1: Статический аудит целостности и конфигураций")
    results["Tier 1: Static Sanity"] = {"success": s1, "output": out1, "time": t1}

    # Tier 2: Unit Tests
    tier2_py = os.path.join(SCRIPT_DIR, "test_tier2_engine_units.py")
    s2, out2, t2 = run_command([sys.executable, tier2_py], "Tier 2: Модульные тесты алгоритмов ЕСКД")
    results["Tier 2: Unit Tests"] = {"success": s2, "output": out2, "time": t2}

    # Tier 4: Provisioning Tests (PowerShell)
    tier4_ps = os.path.join(SCRIPT_DIR, "test_tier4_provisioning.ps1")
    s4, out4, t4 = run_command(["powershell.exe", "-ExecutionPolicy", "Bypass", "-File", tier4_ps], "Tier 4: Верификация развертывания рабочей станции")
    results["Tier 4: Provisioning"] = {"success": s4, "output": out4, "time": t4}

    # Tier 3: SW Integration Tests
    tier3_py = os.path.join(SCRIPT_DIR, "test_tier3_sw_integration.py")
    # CR#9: увеличенный таймаут для Tier 3 — при незапущенном SolidWorks тест сам поднимает
    # новый экземпляр SW через COM (старт 60-150 сек) поверх прогона самих сценариев.
    s3, out3, t3 = run_command([sys.executable, tier3_py], "Tier 3: Сквозные тесты в SolidWorks 2025", timeout=600)
    results["Tier 3: SW Integration"] = {"success": s3, "output": out3, "time": t3}

    end_time = datetime.now()
    total_sec = (end_time - start_time).total_seconds()

    # Generate Markdown Report
    all_passed = all(r["success"] for r in results.values())

    md = []
    md.append("# 📊 Сводный отчет о комплексном тестировании экосистемы SolidWorks 2025\n")
    md.append(f"**Дата и время проведения:** {start_time.strftime('%Y-%m-%d %H:%M:%S')}  ")
    md.append(f"**Общее время выполнения:** {total_sec:.2f} сек  ")
    md.append(f"**Итоговый статус:** {'✅ ВСЕ ТЕСТЫ ПРОЙДЕНЫ УСПЕШНО' if all_passed else '❌ ОБНАРУЖЕНЫ ОШИБКИ'}\n")

    md.append("## Результаты по уровням пирамиды тестирования\n")
    md.append("| Уровень тестирования | Компонент / Назначение | Время (с) | Статус |")
    md.append("|---|---|---|---|")
    for name, data in results.items():
        st = "✅ PASS" if data["success"] else "❌ FAIL"
        md.append(f"| **{name}** | {name.split(':')[1].strip()} | {data['time']:.2f} | {st} |")

    md.append("\n---\n")
    md.append("## Детальные протоколы выполнения\n")

    for name, data in results.items():
        md.append(f"### {name}\n")
        md.append("```text")
        md.append(data["output"].strip())
        md.append("```\n")

    report_content = "\n".join(md)

    # Ротация отчёта: существующий TEST_REPORT.md переименовывается с временной меткой
    if os.path.exists(REPORT_MD):
        try:
            stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            rotated_path = os.path.join(SCRIPT_DIR, f"TEST_REPORT_{stamp}.md")
            os.replace(REPORT_MD, rotated_path)
            print(f"  Предыдущий отчет сохранен как: {rotated_path}")
        except Exception as e:
            print(f"  [WARN] Не удалось переименовать предыдущий отчет: {e}")

    with open(REPORT_MD, "w", encoding="utf-8") as f:
        f.write(report_content)

    print("\n" + "=" * 80)
    print(f"  ИТОГОВЫЙ СТАТУС: {'УСПЕХ (100% PASS)' if all_passed else 'НЕКОТОРЫЕ ТЕСТЫ ПРОВАЛЕНЫ'}")
    print(f"  Сводный отчет сохранен: {REPORT_MD}")
    print("=" * 80)

    return all_passed


if __name__ == "__main__":
    ok = main()
    sys.exit(0 if ok else 1)
