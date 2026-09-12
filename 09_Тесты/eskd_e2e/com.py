# -*- coding: utf-8 -*-
"""Вспомогательные функции COM для SolidWorks через win32com.

SolidWorks отдаёт методы без аргументов через IDispatch и как свойства: при позднем
связывании к ним обращаются без скобок (doc.GetTitle). Методы с побочным эффектом,
которые нужно вызывать именно как методы, помечаются через _FlagAsMethod.
"""
import pythoncom
import win32com.client
import win32com.client.dynamic

SW_DOC_PART = 1
SW_DOC_ASSEMBLY = 2
SW_DOC_DRAWING = 3

OPEN_SILENT = 1
OPEN_READONLY = 2

SAVE_SILENT = 1
SAVE_COPY = 2
SAVE_COPY_AND_OPEN = 512

CUSTOM_INFO_TEXT = 30
PROP_ADD_REPLACE = 2
PROP_DELETE_AND_ADD = 1

APP_METHODS = (
    "ExitApp", "GetDocumentCount", "LoadAddIn", "UnloadAddIn", "GetAddInObject", "CloseDoc",
    "NewDocument", "OpenDoc6", "ActivateDoc3", "RunCommand", "GetOpenDocumentByName",
    "GetMaterialDatabases", "RevisionNumber", "GetFirstDocument", "CloseAllDocuments",
)


RPC_E_CALL_REJECTED = -2147418111
RPC_E_SERVERCALL_RETRYLATER = -2147417846
RETRY_SECONDS = 30.0
NAME_RETRY_SECONDS = 5.0


def _retry(fn):
    """Повторяет вызов, пока SolidWorks отклоняет его как занятый.

    Отклонённый вызов сервером не выполнялся, поэтому повтор безопасен. Ошибка поиска имени
    (AttributeError) у позднего связывания тоже может быть отказом занятого сервера — её
    повторяем недолго.
    """
    import time as _t
    start = _t.time()
    while True:
        try:
            return fn()
        except pythoncom.com_error as exc:
            if exc.hresult in (RPC_E_CALL_REJECTED, RPC_E_SERVERCALL_RETRYLATER) and _t.time() - start < RETRY_SECONDS:
                _t.sleep(0.2)
                continue
            raise
        except AttributeError:
            if _t.time() - start < NAME_RETRY_SECONDS:
                _t.sleep(0.25)
                continue
            raise


def _unwrap(value):
    return object.__getattribute__(value, "_obj") if isinstance(value, Proxy) else value


def _wrap(value):
    if isinstance(value, win32com.client.dynamic.CDispatch):
        return Proxy(value)
    if isinstance(value, tuple):
        return tuple(_wrap(v) for v in value)
    if callable(value) and not isinstance(value, (Proxy, type)):
        def call(*args):
            return _wrap(_retry(lambda: value(*[_unwrap(a) for a in args])))
        return call
    return value


class Proxy:
    """Прозрачная обёртка над CDispatch с повтором отклонённых вызовов."""

    def __init__(self, obj):
        object.__setattr__(self, "_obj", obj)

    def __getattr__(self, name):
        obj = object.__getattribute__(self, "_obj")
        if name == "_oleobj_":
            return obj._oleobj_
        if name.startswith("__"):
            raise AttributeError(name)
        return _wrap(_retry(lambda: getattr(obj, name)))

    def __setattr__(self, name, value):
        obj = object.__getattribute__(self, "_obj")
        _retry(lambda: setattr(obj, name, _unwrap(value)))

    def __call__(self, *args):
        obj = object.__getattribute__(self, "_obj")
        return _wrap(_retry(lambda: obj(*[_unwrap(a) for a in args])))

    def __bool__(self):
        return True

    def __repr__(self):
        return f"<Proxy {object.__getattribute__(self, '_obj')!r}>"


def dyn(obj):
    """Позднее связывание для объекта COM (или None) с повтором отклонённых вызовов."""
    if obj is None:
        return None
    if isinstance(obj, Proxy):
        return obj
    return Proxy(win32com.client.dynamic.Dispatch(obj._oleobj_))


def call(obj, name, *args):
    """Вызов метода как метода (для методов без аргументов позднее связывание иначе
    выполняет их как чтение свойства)."""
    try:
        obj._FlagAsMethod(name)
    except Exception:
        pass
    return getattr(obj, name)(*args)


def flag_methods(obj, names):
    for name in names:
        try:
            obj._FlagAsMethod(name)
        except Exception:
            pass
    return obj


def ref_int(value=0):
    return win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, value)


def ref_str(value=""):
    return win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_BSTR, value)


def ref_bool(value=False):
    return win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_BOOL, value)


def str_array(values):
    """Массив строк BSTR для параметров вида string[] (списки Python уходят как VARIANT[])."""
    return win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_BSTR, [str(v) for v in values])


def null_dispatch():
    return win32com.client.VARIANT(pythoncom.VT_DISPATCH, None)


def doc_type_for(path):
    ext = str(path).lower().rsplit(".", 1)[-1]
    return {"sldprt": SW_DOC_PART, "sldasm": SW_DOC_ASSEMBLY, "slddrw": SW_DOC_DRAWING,
            "prtdot": SW_DOC_PART, "asmdot": SW_DOC_ASSEMBLY, "drwdot": SW_DOC_DRAWING}.get(ext, 0)


def as_list(value):
    """Массивы COM приходят кортежами или None."""
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return list(value)
    return [value]


def prop_get(cpm, name):
    """Сырое и разрешённое значение свойства: (raw, resolved) или (None, None), если свойства нет."""
    raw = ref_str("")
    resolved = ref_str("")
    try:
        cpm.Get4(name, False, raw, resolved)
    except Exception:
        return None, None
    names = as_list(cpm.GetNames)
    if name not in names:
        return None, None
    return str(raw.value or ""), str(resolved.value or "")


def prop_names(cpm):
    return [str(n) for n in as_list(cpm.GetNames)]


def prop_set(cpm, name, value):
    """Запись текстового свойства с заменой значения. Возвращает код Add3."""
    return int(cpm.Add3(name, CUSTOM_INFO_TEXT, value, PROP_DELETE_AND_ADD))
