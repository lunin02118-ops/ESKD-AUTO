# -*- coding: utf-8 -*-
"""Пользовательские свойства файла SolidWorks без SolidWorks — для статических тестов (шаблоны документов).

В .sldprt/.sldasm/.prtdot/.asmdot лежат потоки raw-deflate; среди них XML «Properties» (общие свойства) и
«ConfigProperties» (по одному на конфигурацию; имени конфигурации в XML нет). Свойства в XML идут в порядке, в каком
их показывает SolidWorks: новое и перенесённое в конец получает следующий номер pid.
"""
import html
import re
import zlib
from pathlib import Path

_PROPERTY = re.compile(r'<property name="([^"]*)"[^>]*>(.*?)</property>', re.S)
_VALUE = re.compile(r'^<vt:(\w+)>(.*?)</vt:\1>', re.S)


def _streams(data):
    i, n = 0, len(data)
    while i < n - 8:
        if data[i] & 0x06 == 0x06:  # тип блока deflate 3 недопустим — здесь потока нет
            i += 1
            continue
        try:
            d = zlib.decompressobj(-15)
            chunk = data[i:i + 2_000_000]
            head = d.decompress(chunk, 64)
            if not head.startswith(b"<?xml"):
                i += 1
                continue
            out = head + d.decompress(d.unconsumed_tail) + d.flush()
            yield out.decode("utf-8", "replace")
            i += max(1, len(chunk) - len(d.unused_data))
        except zlib.error:
            i += 1


def _user(xml):
    """[(имя, значение)] раздела UserDefinedProperties в порядке файла; выражение вида «"SW-Mass"» — как записано."""
    out = []
    for section in re.finditer(r'<propertySection[^>]*name="UserDefinedProperties"[^>]*>(.*?)</propertySection>', xml, re.S):
        for m in _PROPERTY.finditer(section.group(1)):
            name = html.unescape(m.group(1))
            if not name or name.startswith("SW-"):
                continue
            v = _VALUE.search(m.group(2).strip())
            out.append((name, html.unescape(v.group(2)) if v else ""))
    return out


def read(path):
    """{'general': [(имя, значение)…], 'configs': [[(имя, значение)…], …]} в порядке файла."""
    general, configs = [], []
    for xml in _streams(Path(path).read_bytes()):
        head = xml[:400]
        if "<ConfigProperties" in head:
            configs.append(_user(xml))
        elif "<Properties" in head and "/custom-properties" in head:
            # Остальные потоки «Properties» — служебные SolidWorks (единицы документа, «Assembly type»…).
            general += _user(xml)
    return {"general": general, "configs": configs}
