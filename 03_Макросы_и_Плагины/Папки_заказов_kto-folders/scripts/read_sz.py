"""Печатает таблицы и текст служебной записки (.docx) — чтобы выписать позиции заказа.

python read_sz.py "<путь к СЗ.docx>"
"""
import re
import sys
import zipfile


def cell_text(xml):
    parts = re.findall(r"<w:p[ >].*?</w:p>", xml, re.S) or [xml]
    lines = [re.sub(r"<[^>]+>", "", p).strip() for p in parts]
    return " / ".join(l for l in lines if l)


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    xml = zipfile.ZipFile(sys.argv[1]).read("word/document.xml").decode("utf-8")
    for t, table in enumerate(re.findall(r"<w:tbl>.*?</w:tbl>", xml, re.S), 1):
        print(f"--- таблица {t}")
        for row in re.findall(r"<w:tr[ >].*?</w:tr>", table, re.S):
            cells = [cell_text(c) for c in re.findall(r"<w:tc>.*?</w:tc>", row, re.S)]
            print(" | ".join(cells))
    body = re.sub(r"<w:tbl>.*?</w:tbl>", "", xml, flags=re.S)
    text = [re.sub(r"<[^>]+>", "", p).strip() for p in re.findall(r"<w:p[ >].*?</w:p>", body, re.S)]
    print("--- текст")
    print("\n".join(l for l in text if l))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
