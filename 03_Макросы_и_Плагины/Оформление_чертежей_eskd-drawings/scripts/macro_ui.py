# -*- coding: utf-8 -*-
"""Окна VBA-форм SW+ (ThunderDFrame): найти, сфотографировать (PrintWindow), нажать по координатам клиента (PostMessage).
python macro_ui.py list | shot <png> | click <x> <y> | key <vk>"""
import ctypes, sys, time
import win32gui, win32con, win32ui, win32process
from PIL import Image
sys.stdout.reconfigure(encoding="utf-8")


def forms():
    out = []
    def cb(h, _):
        if win32gui.IsWindowVisible(h) and (win32gui.GetClassName(h) == "ThunderDFrame" or (win32gui.GetClassName(h) == "#32770" and "Отчет об ошибках" not in win32gui.GetWindowText(h))):
            out.append((h, win32gui.GetClassName(h), win32gui.GetWindowText(h), win32gui.GetWindowRect(h)))
    win32gui.EnumWindows(cb, None)
    return out


def inner(h):
    """Дочернее окно, куда приходят щелчки MSForms (обычно класс 'F3 Server ...')."""
    kids = []
    win32gui.EnumChildWindows(h, lambda c, _: kids.append((c, win32gui.GetClassName(c))), None)
    for c, cls in kids:
        if cls.startswith("F3 Server"):
            return c
    return kids[0][0] if kids else h


def shot(h, path):
    l, t, r, b = win32gui.GetWindowRect(h)
    w_, h_ = r - l, b - t
    hdc = win32gui.GetWindowDC(h)
    src = win32ui.CreateDCFromHandle(hdc)
    mem = src.CreateCompatibleDC()
    bmp = win32ui.CreateBitmap()
    bmp.CreateCompatibleBitmap(src, w_, h_)
    mem.SelectObject(bmp)
    ctypes.windll.user32.PrintWindow(h, mem.GetSafeHdc(), 2)
    info = bmp.GetInfo()
    img = Image.frombuffer("RGB", (info["bmWidth"], info["bmHeight"]), bmp.GetBitmapBits(True), "raw", "BGRX", 0, 1)
    img.save(path)
    win32gui.DeleteObject(bmp.GetHandle()); mem.DeleteDC(); src.DeleteDC(); win32gui.ReleaseDC(h, hdc)
    return (l, t)


def click(h, sx, sy):
    """sx, sy — координаты на снимке окна (от левого верхнего угла окна)."""
    l, t, _, _ = win32gui.GetWindowRect(h)
    X, Y = l + sx, t + sy
    kids = []
    win32gui.EnumChildWindows(h, lambda k, _: kids.append(k), None)
    hit = [k for k in kids if win32gui.IsWindowVisible(k) and (lambda r: r[0] <= X < r[2] and r[1] <= Y < r[3])(win32gui.GetWindowRect(k))]
    c = min(hit, key=lambda k: (lambda r: (r[2] - r[0]) * (r[3] - r[1]))(win32gui.GetWindowRect(k))) if hit else inner(h)
    cx, cy = win32gui.ScreenToClient(c, (X, Y))
    lp = (cy << 16) | (cx & 0xFFFF)
    win32gui.PostMessage(c, win32con.WM_MOUSEMOVE, 0, lp)
    win32gui.PostMessage(c, win32con.WM_LBUTTONDOWN, win32con.MK_LBUTTON, lp)
    time.sleep(0.08)
    win32gui.PostMessage(c, win32con.WM_LBUTTONUP, 0, lp)


if __name__ == "__main__":
    cmd = sys.argv[1]
    fs = forms()
    if cmd == "list":
        for f in fs:
            print(f)
    elif cmd == "shot":
        for i, f in enumerate(fs):
            p = sys.argv[2].replace(".png", "_%d.png" % i)
            shot(f[0], p)
            print(p, f)
    elif cmd == "click":
        idx = int(sys.argv[4]) if len(sys.argv) > 4 else 0
        click(fs[idx][0], int(sys.argv[2]), int(sys.argv[3]))
        print("щёлк", fs[idx][2])
