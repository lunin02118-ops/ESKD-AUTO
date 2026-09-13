# -*- coding: utf-8 -*-
"""Правка исходного текста модулей VBA в .swp без редактора VBA (шаг 4 плана согласования, порядок правки макросов В-5).

Как устроено (MS-OVBA): поток модуля = кэш P-code (MODULEOFFSET байт) + сжатый исходник. Инструмент заменяет
исходник, убирает кэш P-code у всех модулей (MODULEOFFSET = 0) и помечает _VBA_PROJECT как версию, которую нужно
перекомпилировать (Version другой сборки VBA, спайк S-9). SolidWorks при загрузке макроса компилирует проект из исходника — исполняется
ровно тот текст, который лежит в .swp и в текстовой выгрузке (снимает расхождение исходника и P-code, Н-17).
Формы (дизайнеры) и остальные потоки не меняются.

    python 09_Тесты/tools/vba_patch.py <in.swp> <out.swp> <правки.json>

правки.json: {"модуль": [["было", "стало"], ...]} — каждое «было» должно встретиться в модуле ровно один раз
(null — «стало» дописывается в конец модуля);
переводы строк в правках — LF (в .swp пишутся CR LF). Входной файл только читается.
"""
import json
import re
import shutil
import struct
import sys
from pathlib import Path

import olefile
import pythoncom
from oletools.olevba import decompress_stream

STGM_READWRITE = 0x2
STGM_SHARE_EXCLUSIVE = 0x10
# Заголовок _VBA_PROJECT без кэша (MS-OVBA 2.3.4.1): Reserved1 0x61CC, Version, Reserved2 0x00, Reserved3 0x0000.
# Version 0xFFFF VBA SolidWorks 2025 отвергает (спайк S-9: макрос не запускается); версия другой сборки VBA
# (0x00B2 — VBA7 Office 2016 x64; у SolidWorks 2025 — 0x009A) заставляет перекомпилировать проект из исходника.
RECOMPILE_VERSIONS = (0x00B2, 0x0097)


# ------------------------------------------------------------------ сжатие MS-OVBA 2.4.1
def _copy_token_help(difference):
    bit_count = 4
    while (1 << bit_count) < difference:
        bit_count += 1
    length_mask = 0xFFFF >> bit_count
    return bit_count, length_mask + 3


def _compress_chunk(data):
    out = bytearray()
    pos, end = 0, len(data)
    index = {}
    while pos < end:
        flag_at = len(out)
        out.append(0)
        flags = 0
        for bit in range(8):
            if pos >= end:
                break
            bit_count, max_length = _copy_token_help(pos)
            best_len, best_off = 0, 0
            if pos >= 1 and end - pos >= 3:
                for cand in reversed(index.get(bytes(data[pos:pos + 3]), ())):
                    length = 0
                    limit = min(max_length, end - pos)
                    while length < limit and data[cand + length] == data[pos + length]:
                        length += 1
                    if length > best_len:
                        best_len, best_off = length, pos - cand
                        if length == limit:
                            break
            step = best_len if best_len >= 3 else 1
            if best_len >= 3:
                token = ((best_off - 1) << (16 - bit_count)) | (best_len - 3)
                out += struct.pack("<H", token)
                flags |= 1 << bit
            else:
                out.append(data[pos])
            for p in range(pos, pos + step):
                if p + 3 <= end:
                    index.setdefault(bytes(data[p:p + 3]), []).append(p)
            pos += step
        out[flag_at] = flags
    return bytes(out)


def compress(data):
    out = bytearray(b"\x01")
    for start in range(0, len(data), 4096):
        chunk = data[start:start + 4096]
        body = _compress_chunk(chunk)
        if len(body) + 2 > 4098:
            if len(chunk) != 4096:
                raise ValueError("несжимаемый короткий блок")
            out += struct.pack("<H", 0x3000 | 0x0FFF) + chunk
        else:
            out += struct.pack("<H", 0xB000 | (len(body) + 2 - 3)) + body
    return bytes(out)


def decompress(data):
    return bytes(decompress_stream(bytearray(data)))


# ------------------------------------------------------------------ поток dir
def parse_dir(raw):
    """Модули: [{"name", "stream", "offset", "offset_pos"}] и кодовая страница проекта."""
    modules, codepage, current = [], 1252, None
    pos = 0
    while pos + 6 <= len(raw):
        rid, size = struct.unpack_from("<HI", raw, pos)
        data_pos = pos + 6
        if rid == 0x0009:  # PROJECTVERSION: размер 4, данных 6
            size = 6
        data = raw[data_pos:data_pos + size]
        if rid == 0x0003:
            codepage = struct.unpack_from("<H", data)[0]
        elif rid == 0x0019:
            current = {"name": data.decode("cp1252", "replace")}
            modules.append(current)
        elif rid == 0x001A and current is not None:
            current["stream"] = data
        elif rid == 0x0032 and current is not None:
            current["stream_unicode"] = data.decode("utf-16-le")
        elif rid == 0x0031 and current is not None:
            current["offset"] = struct.unpack_from("<I", data)[0]
            current["offset_pos"] = data_pos
        pos = data_pos + size
    for m in modules:
        m["stream_name"] = m.get("stream_unicode") or m["stream"].decode("cp%d" % codepage, "replace")
    return modules, codepage


def vba_storage_path(ole):
    for entry in ole.listdir(streams=True, storages=False):
        if len(entry) >= 2 and entry[-1] == "dir" and entry[-2] == "VBA":
            return entry[:-1]
    raise ValueError("в файле нет проекта VBA")


# ------------------------------------------------------------------ правка
def read_modules(swp):
    """{имя модуля: исходник str (LF)} и служебные данные для записи."""
    ole = olefile.OleFileIO(str(swp))
    try:
        base = vba_storage_path(ole)
        dir_raw = decompress(ole.openstream(base + ["dir"]).read())
        modules, codepage = parse_dir(dir_raw)
        texts = {}
        for m in modules:
            stream = ole.openstream(base + [m["stream_name"]]).read()
            m["pcode"] = len(stream[:m["offset"]])
            texts[m["name"]] = decompress(stream[m["offset"]:]).decode("cp%d" % codepage).replace("\r\n", "\n")
        return base, dir_raw, modules, codepage, texts
    finally:
        ole.close()


def _open_path(root, names):
    storages = []
    stg = root
    for name in names:
        stg = stg.OpenStorage(name, None, STGM_READWRITE | STGM_SHARE_EXCLUSIVE, None, 0)
        storages.append(stg)
    return storages


def _write_stream(stg, name, data):
    stm = stg.OpenStream(name, None, STGM_READWRITE | STGM_SHARE_EXCLUSIVE, 0)
    stm.SetSize(len(data))
    stm.Seek(0, 0)
    stm.Write(data)
    stm.Commit(0)


def recompile_header(swp, base):
    """7 байт заголовка _VBA_PROJECT с версией, отличной от той, которой собран кэш P-code."""
    ole = olefile.OleFileIO(str(swp))
    try:
        current = struct.unpack_from("<H", ole.openstream(base + ["_VBA_PROJECT"]).read(), 2)[0]
    finally:
        ole.close()
    version = next(v for v in RECOMPILE_VERSIONS if v != current)
    return struct.pack("<HHBH", 0x61CC, version, 0, 0)


def patch(src, dst, edits):
    """Копия src → dst с правками {модуль: [(было, стало)]}; все модули без P-code. Возвращает отчёт."""
    base, dir_raw, modules, codepage, texts = read_modules(src)
    report = {"codepage": codepage, "modules": {}}
    unknown = set(edits) - set(texts)
    if unknown:
        raise ValueError(f"нет модулей: {sorted(unknown)}")
    for module, pairs in edits.items():
        text = texts[module]
        for old, new in pairs:
            if old is None:  # дописать в конец модуля
                text = text.rstrip("\n") + "\n" + new
                continue
            count = text.count(old)
            if count != 1:
                raise ValueError(f"{module}: «{old[:80]}» встречается {count} раз")
            text = text.replace(old, new)
        texts[module] = text
    shutil.copyfile(src, dst)
    new_dir = bytearray(dir_raw)
    for m in modules:
        struct.pack_into("<I", new_dir, m["offset_pos"], 0)
    root = pythoncom.StgOpenStorage(str(dst), None, STGM_READWRITE | STGM_SHARE_EXCLUSIVE, None, 0)
    storages = _open_path(root, base)
    vba = storages[-1]
    for m in modules:
        source = texts[m["name"]].replace("\n", "\r\n").encode("cp%d" % codepage)
        _write_stream(vba, m["stream_name"], compress(source))
        report["modules"][m["name"]] = {"pcode_removed": m["pcode"], "edits": len(edits.get(m["name"], []))}
    _write_stream(vba, "dir", compress(bytes(new_dir)))
    _write_stream(vba, "_VBA_PROJECT", recompile_header(src, base))
    for stg in reversed(storages):
        stg.Commit(0)
    root.Commit(0)
    del vba, storages, root
    # проверка: текст читается обратно и совпадает
    _, _, after_modules, _, after = read_modules(dst)
    for name, text in texts.items():
        if after[name] != text:
            raise AssertionError(f"{name}: текст после записи не совпал")
    if any(m["offset"] for m in after_modules):
        raise AssertionError("кэш P-code остался")
    return report


def main(argv):
    if len(argv) != 4:
        print(__doc__)
        return 2
    edits = json.loads(Path(argv[3]).read_text(encoding="utf-8"))
    report = patch(Path(argv[1]), Path(argv[2]), {k: [tuple(p) for p in v] for k, v in edits.items()})
    print(json.dumps(report, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
